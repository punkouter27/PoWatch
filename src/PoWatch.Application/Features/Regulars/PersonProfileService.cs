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

        PersonProfile updated;
        if (best is null)
        {
            var nextNum = existing.Count == 0 ? 1 : existing.Max(p => p.Number) + 1;
            updated = new PersonProfile
            {
                Id = $"p-{Guid.NewGuid():N}",
                UserId = userId,
                Number = nextNum,
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
            DisplayName = p.DisplayName,
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
