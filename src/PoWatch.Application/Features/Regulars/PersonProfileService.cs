using PoWatch.Application.Contracts;
using PoWatch.Domain.Models;
using PoWatch.Shared.Models;

namespace PoWatch.Application.Features.Regulars;

/// <summary>
/// Matches faces across sessions by DINOv2 descriptor and accumulates per-person face &amp; body
/// appearance statistics in Azure Table Storage.
/// </summary>
public sealed class PersonProfileService(IPersonProfileStore store, TimeProvider time)
{
    public async Task<IReadOnlyList<PersonProfileDto>> ListAsync(string userId, CancellationToken cancellationToken)
    {
        var profiles = await store.ListAsync(userId, cancellationToken);
        return [.. profiles.OrderBy(p => p.Number).Select(ToDto)];
    }

    public async Task<ObservePersonResultDto> ObserveAsync(string userId, ObservePersonRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = time.GetUtcNow();
        var unit = PersonProfile.Normalize(request.Descriptor);
        var existing = await store.ListAsync(userId, cancellationToken);

        PersonProfile? best = null;
        var bestDist = PersonProfile.MatchDistance;
        foreach (var candidate in existing)
        {
            var d = PersonProfile.CosineDistance(candidate.Descriptor, unit);
            if (d <= bestDist)
            {
                bestDist = d;
                best = candidate;
            }
        }

        var isNew = best is null;
        var returning = best is not null && best.LastSessionId != Guid.Empty && best.LastSessionId != request.SessionId;

        var thumb = ValidThumbnail(request.ThumbnailDataUrl);
        PersonProfile updated;
        if (best is null)
        {
            var nextNum = existing.Count == 0 ? 1 : existing.Max(p => p.Number) + 1;
            updated = new PersonProfile
            {
                Id = $"p-{Guid.NewGuid():N}",
                UserId = userId,
                Number = nextNum,
                ThumbnailDataUrl = thumb,
                Descriptor = unit,
                DescriptorCount = 1,
                SessionsSeen = 1,
                LastSessionId = request.SessionId,
                FirstSeenUtc = now,
                LastSeenUtc = now,
                Stats = Fold(new PersonProfileStats(), request)
            };
        }
        else
        {
            updated = best with
            {
                ThumbnailDataUrl = best.ThumbnailDataUrl ?? thumb,
                Descriptor = PersonProfile.Blend(best.Descriptor, best.DescriptorCount, unit),
                DescriptorCount = best.DescriptorCount + 1,
                SessionsSeen = returning ? best.SessionsSeen + 1 : Math.Max(1, best.SessionsSeen),
                LastSessionId = request.SessionId != Guid.Empty ? request.SessionId : best.LastSessionId,
                LastSeenUtc = now,
                Stats = Fold(best.Stats, request)
            };
        }

        if (existing.Count < PersonProfile.MaxProfiles || !isNew)
            await store.UpsertAsync(updated, cancellationToken);

        return new ObservePersonResultDto
        {
            Person = ToDto(updated),
            IsNew = isNew,
            ReturningFromPreviousSession = returning
        };
    }

    public async Task<PersonProfileDto?> RenameAsync(string userId, string personId, string? name, CancellationToken cancellationToken)
    {
        if (await store.GetAsync(userId, personId, cancellationToken) is not { } profile) return null;
        var cleaned = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (cleaned is { Length: > 40 }) cleaned = cleaned[..40];
        var updated = profile with { CustomName = cleaned };
        await store.UpsertAsync(updated, cancellationToken);
        return ToDto(updated);
    }

    public async Task<PersonProfileDto?> MergeAsync(string userId, string sourceId, string targetId, CancellationToken cancellationToken)
    {
        if (sourceId == targetId) return null;
        var source = await store.GetAsync(userId, sourceId, cancellationToken);
        var target = await store.GetAsync(userId, targetId, cancellationToken);
        if (source is null || target is null) return null;

        var merged = target with
        {
            CustomName = target.CustomName ?? source.CustomName,
            ThumbnailDataUrl = target.ThumbnailDataUrl ?? source.ThumbnailDataUrl,
            Descriptor = PersonProfile.Blend(target.Descriptor, target.DescriptorCount, source.Descriptor),
            DescriptorCount = target.DescriptorCount + source.DescriptorCount,
            SessionsSeen = target.SessionsSeen + source.SessionsSeen,
            FirstSeenUtc = target.FirstSeenUtc < source.FirstSeenUtc ? target.FirstSeenUtc : source.FirstSeenUtc,
            LastSeenUtc = target.LastSeenUtc > source.LastSeenUtc ? target.LastSeenUtc : source.LastSeenUtc,
            Stats = Combine(target.Stats, source.Stats)
        };
        await store.UpsertAsync(merged, cancellationToken);
        await store.DeleteAsync(userId, sourceId, cancellationToken);
        return ToDto(merged);
    }

    public async Task<bool> DeleteAsync(string userId, string personId, CancellationToken cancellationToken)
    {
        if (await store.GetAsync(userId, personId, cancellationToken) is null) return false;
        await store.DeleteAsync(userId, personId, cancellationToken);
        return true;
    }

    private static string? ValidThumbnail(string? dataUrl) =>
        dataUrl is { Length: > 0 and <= 24_576 } && dataUrl.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal)
            ? dataUrl
            : null;

    private static PersonProfileStats Combine(PersonProfileStats a, PersonProfileStats b) => new()
    {
        FaceSamples = a.FaceSamples + b.FaceSamples,
        BeautySum = a.BeautySum + b.BeautySum,
        BeautyLow = a.BeautyLow is { } al && b.BeautyLow is { } bl ? Math.Min(al, bl) : a.BeautyLow ?? b.BeautyLow,
        BeautyHigh = a.BeautyHigh is { } ah && b.BeautyHigh is { } bh ? Math.Max(ah, bh) : a.BeautyHigh ?? b.BeautyHigh,
        SymmetrySum = a.SymmetrySum + b.SymmetrySum,
        SymmetryCount = a.SymmetryCount + b.SymmetryCount,
        GoldenSum = a.GoldenSum + b.GoldenSum,
        GoldenCount = a.GoldenCount + b.GoldenCount,
        ThirdsSum = a.ThirdsSum + b.ThirdsSum,
        ThirdsCount = a.ThirdsCount + b.ThirdsCount,
        FifthsSum = a.FifthsSum + b.FifthsSum,
        FifthsCount = a.FifthsCount + b.FifthsCount,
        AgeSum = a.AgeSum + b.AgeSum,
        AgeCount = a.AgeCount + b.AgeCount,
        AgeBand = a.AgeBand ?? b.AgeBand,
        Presentation = a.Presentation ?? b.Presentation,
        FaceShape = a.FaceShape ?? b.FaceShape,
        AttentionSum = a.AttentionSum + b.AttentionSum,
        AttentionCount = a.AttentionCount + b.AttentionCount,
        SmileSum = a.SmileSum + b.SmileSum,
        SmileCount = a.SmileCount + b.SmileCount,
        ConcentrationSum = a.ConcentrationSum + b.ConcentrationSum,
        ConcentrationCount = a.ConcentrationCount + b.ConcentrationCount,
        BlinkRateSum = a.BlinkRateSum + b.BlinkRateSum,
        BlinkRateCount = a.BlinkRateCount + b.BlinkRateCount,
        FatigueSum = a.FatigueSum + b.FatigueSum,
        FatigueCount = a.FatigueCount + b.FatigueCount,
        TalkingSum = a.TalkingSum + b.TalkingSum,
        TalkingCount = a.TalkingCount + b.TalkingCount,
        DistanceBand = a.DistanceBand ?? b.DistanceBand,
        BodySamples = a.BodySamples + b.BodySamples,
        UprightSum = a.UprightSum + b.UprightSum,
        UprightCount = a.UprightCount + b.UprightCount,
        EnergySum = a.EnergySum + b.EnergySum,
        EnergyCount = a.EnergyCount + b.EnergyCount,
        OpenPostureSum = a.OpenPostureSum + b.OpenPostureSum,
        OpenPostureCount = a.OpenPostureCount + b.OpenPostureCount,
        ArmBalanceSum = a.ArmBalanceSum + b.ArmBalanceSum,
        ArmBalanceCount = a.ArmBalanceCount + b.ArmBalanceCount,
        LegBalanceSum = a.LegBalanceSum + b.LegBalanceSum,
        LegBalanceCount = a.LegBalanceCount + b.LegBalanceCount,
        ShoulderTiltSum = a.ShoulderTiltSum + b.ShoulderTiltSum,
        ShoulderTiltCount = a.ShoulderTiltCount + b.ShoulderTiltCount
    };

    private static PersonProfileStats Fold(PersonProfileStats s, ObservePersonRequestDto r)
    {
        var hasFace = r.Beauty is not null || r.AgeYears is not null || r.Symmetry is not null;
        var hasBody = r.UprightScore is not null || r.MeanEnergy is not null || r.OpenPostureShare is not null
            || r.ArmBalance is not null || r.LegBalance is not null || r.ShoulderTilt is not null;

        return s with
        {
            FaceSamples = s.FaceSamples + (hasFace ? 1 : 0),
            BeautySum = s.BeautySum + (r.Beauty ?? 0),
            BeautyLow = r.Beauty is { } bl ? (s.BeautyLow is { } curLow ? Math.Min(curLow, bl) : bl) : s.BeautyLow,
            BeautyHigh = r.Beauty is { } bh ? (s.BeautyHigh is { } curHigh ? Math.Max(curHigh, bh) : bh) : s.BeautyHigh,
            SymmetrySum = s.SymmetrySum + (r.Symmetry ?? 0),
            SymmetryCount = s.SymmetryCount + (r.Symmetry is not null ? 1 : 0),
            GoldenSum = s.GoldenSum + (r.Golden ?? 0),
            GoldenCount = s.GoldenCount + (r.Golden is not null ? 1 : 0),
            ThirdsSum = s.ThirdsSum + (r.Thirds ?? 0),
            ThirdsCount = s.ThirdsCount + (r.Thirds is not null ? 1 : 0),
            FifthsSum = s.FifthsSum + (r.Fifths ?? 0),
            FifthsCount = s.FifthsCount + (r.Fifths is not null ? 1 : 0),
            AgeSum = s.AgeSum + (r.AgeYears ?? 0),
            AgeCount = s.AgeCount + (r.AgeYears is not null ? 1 : 0),
            AgeBand = !string.IsNullOrWhiteSpace(r.AgeBand) ? r.AgeBand : s.AgeBand,
            Presentation = !string.IsNullOrWhiteSpace(r.Presentation) ? r.Presentation : s.Presentation,
            FaceShape = !string.IsNullOrWhiteSpace(r.FaceShape) ? r.FaceShape : s.FaceShape,
            AttentionSum = s.AttentionSum + (r.AttentionShare ?? 0),
            AttentionCount = s.AttentionCount + (r.AttentionShare is not null ? 1 : 0),
            SmileSum = s.SmileSum + (r.SmileScore ?? 0),
            SmileCount = s.SmileCount + (r.SmileScore is not null ? 1 : 0),
            ConcentrationSum = s.ConcentrationSum + (r.ConcentrationScore ?? 0),
            ConcentrationCount = s.ConcentrationCount + (r.ConcentrationScore is not null ? 1 : 0),
            BlinkRateSum = s.BlinkRateSum + (r.BlinkRatePerMin ?? 0),
            BlinkRateCount = s.BlinkRateCount + (r.BlinkRatePerMin is not null ? 1 : 0),
            FatigueSum = s.FatigueSum + (r.FatigueShare ?? 0),
            FatigueCount = s.FatigueCount + (r.FatigueShare is not null ? 1 : 0),
            TalkingSum = s.TalkingSum + (r.TalkingShare ?? 0),
            TalkingCount = s.TalkingCount + (r.TalkingShare is not null ? 1 : 0),
            DistanceBand = !string.IsNullOrWhiteSpace(r.DistanceBand) ? r.DistanceBand : s.DistanceBand,
            BodySamples = s.BodySamples + (hasBody ? 1 : 0),
            UprightSum = s.UprightSum + (r.UprightScore ?? 0),
            UprightCount = s.UprightCount + (r.UprightScore is not null ? 1 : 0),
            EnergySum = s.EnergySum + (r.MeanEnergy ?? 0),
            EnergyCount = s.EnergyCount + (r.MeanEnergy is not null ? 1 : 0),
            OpenPostureSum = s.OpenPostureSum + (r.OpenPostureShare ?? 0),
            OpenPostureCount = s.OpenPostureCount + (r.OpenPostureShare is not null ? 1 : 0),
            ArmBalanceSum = s.ArmBalanceSum + (r.ArmBalance ?? 0),
            ArmBalanceCount = s.ArmBalanceCount + (r.ArmBalance is not null ? 1 : 0),
            LegBalanceSum = s.LegBalanceSum + (r.LegBalance ?? 0),
            LegBalanceCount = s.LegBalanceCount + (r.LegBalance is not null ? 1 : 0),
            ShoulderTiltSum = s.ShoulderTiltSum + (r.ShoulderTilt ?? 0),
            ShoulderTiltCount = s.ShoulderTiltCount + (r.ShoulderTilt is not null ? 1 : 0),
        };
    }

    public static PersonProfileDto ToDto(PersonProfile p)
    {
        var s = p.Stats;
        return new PersonProfileDto
        {
            Id = p.Id,
            Number = p.Number,
            CustomName = p.CustomName,
            DisplayName = p.DisplayName,
            ThumbnailDataUrl = p.ThumbnailDataUrl,
            SessionsSeen = p.SessionsSeen,
            FirstSeenUtc = p.FirstSeenUtc,
            LastSeenUtc = p.LastSeenUtc,
            Stats = new PersonAppearanceStatsDto
            {
                BeautyMean = s.FaceSamples > 0 && s.BeautySum > 0 ? Math.Round(s.BeautySum / s.FaceSamples, 1) : null,
                BeautyLow = s.BeautyLow is { } bl ? Math.Round(bl, 1) : null,
                BeautyHigh = s.BeautyHigh is { } bh ? Math.Round(bh, 1) : null,
                SymmetryMean = s.SymmetryCount > 0 ? Math.Round(s.SymmetrySum / s.SymmetryCount, 3) : null,
                GoldenMean = s.GoldenCount > 0 ? Math.Round(s.GoldenSum / s.GoldenCount, 3) : null,
                ThirdsMean = s.ThirdsCount > 0 ? Math.Round(s.ThirdsSum / s.ThirdsCount, 3) : null,
                FifthsMean = s.FifthsCount > 0 ? Math.Round(s.FifthsSum / s.FifthsCount, 3) : null,
                AgeMean = s.AgeCount > 0 ? Math.Round(s.AgeSum / s.AgeCount, 1) : null,
                AgeBand = s.AgeBand,
                Presentation = s.Presentation,
                FaceShape = s.FaceShape,
                AttentionShare = s.AttentionCount > 0 ? Math.Round(s.AttentionSum / s.AttentionCount, 2) : null,
                SmileMean = s.SmileCount > 0 ? Math.Round(s.SmileSum / s.SmileCount, 0) : null,
                ConcentrationMean = s.ConcentrationCount > 0 ? Math.Round(s.ConcentrationSum / s.ConcentrationCount, 0) : null,
                BlinkRateMean = s.BlinkRateCount > 0 ? Math.Round(s.BlinkRateSum / s.BlinkRateCount, 1) : null,
                FatigueShare = s.FatigueCount > 0 ? Math.Round(s.FatigueSum / s.FatigueCount, 3) : null,
                TalkingShare = s.TalkingCount > 0 ? Math.Round(s.TalkingSum / s.TalkingCount, 2) : null,
                DistanceBand = s.DistanceBand,
                FaceSamples = s.FaceSamples,
                UprightScore = s.UprightCount > 0 ? Math.Round(s.UprightSum / s.UprightCount, 1) : null,
                MeanEnergy = s.EnergyCount > 0 ? Math.Round(s.EnergySum / s.EnergyCount, 3) : null,
                OpenPostureShare = s.OpenPostureCount > 0 ? Math.Round(s.OpenPostureSum / s.OpenPostureCount, 3) : null,
                ArmBalance = s.ArmBalanceCount > 0 ? Math.Round(s.ArmBalanceSum / s.ArmBalanceCount, 3) : null,
                LegBalance = s.LegBalanceCount > 0 ? Math.Round(s.LegBalanceSum / s.LegBalanceCount, 3) : null,
                ShoulderTilt = s.ShoulderTiltCount > 0 ? Math.Round(s.ShoulderTiltSum / s.ShoulderTiltCount, 1) : null,
                BodySamples = s.BodySamples
            }
        };
    }
}
