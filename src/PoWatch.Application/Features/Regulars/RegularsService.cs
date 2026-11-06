using PoWatch.Application.Contracts;
using PoWatch.Domain.Models;
using PoWatch.Domain.Services;

namespace PoWatch.Application.Features.Regulars;

public sealed record Observation(Regular Regular, bool IsNew);

/// <summary>Recognises, names, merges and keeps score for a user's regulars.</summary>
public sealed class RegularsService(IRegularStore store, TimeProvider time)
{
    public const int MaxNameLength = 40;

    public Task<IReadOnlyList<Regular>> ListAsync(string userId, CancellationToken cancellationToken) =>
        store.ListAsync(userId, cancellationToken);

    /// <summary>
    /// Matches a sighting to a known regular of the same class, or creates a new unnamed one
    /// ("Person 4"). A match nudges the regular's look toward this sighting.
    /// </summary>
    public async Task<Observation> ObserveAsync(string userId, string className, IReadOnlyList<float> signature, CancellationToken cancellationToken)
    {
        var known = await store.ListAsync(userId, cancellationToken);
        var now = time.GetUtcNow();

        if (RegularMatcher.BestMatch(known, className, signature) is { } match)
        {
            var updated = match with
            {
                Signature = RegularMatcher.Blend(match.Signature, match.Sightings, signature),
                Sightings = match.Sightings + 1,
                LastSeenUtc = now > match.LastSeenUtc ? now : match.LastSeenUtc
            };
            await store.UpsertAsync(updated, cancellationToken);
            return new Observation(updated, IsNew: false);
        }

        var number = known.Where(r => string.Equals(r.Class, className, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Number).DefaultIfEmpty(0).Max() + 1;
        var created = new Regular
        {
            Id = $"r{Guid.NewGuid():N}"[..13],
            UserId = userId,
            Class = className.ToLowerInvariant(),
            Number = number,
            Signature = [.. signature],
            Sightings = 1,
            FirstSeenUtc = now,
            LastSeenUtc = now
        };
        await store.UpsertAsync(created, cancellationToken);
        return new Observation(created, IsNew: true);
    }

    /// <summary>Names a regular; a blank name makes it anonymous again. Null when not found.</summary>
    public async Task<Regular?> RenameAsync(string userId, string regularId, string? name, CancellationToken cancellationToken)
    {
        if (await store.GetAsync(userId, regularId, cancellationToken) is not { } regular) return null;
        var trimmed = name?.Trim();
        var renamed = regular with
        {
            Name = string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength] : trimmed
        };
        await store.UpsertAsync(renamed, cancellationToken);
        return renamed;
    }

    /// <summary>Folds a duplicate into the primary and deletes the duplicate. Null when either is missing.</summary>
    public async Task<Regular?> MergeAsync(string userId, string primaryId, string duplicateId, CancellationToken cancellationToken)
    {
        if (primaryId == duplicateId) return null;
        var primary = await store.GetAsync(userId, primaryId, cancellationToken);
        var duplicate = await store.GetAsync(userId, duplicateId, cancellationToken);
        if (primary is null || duplicate is null) return null;

        var merged = RegularMatcher.Merge(primary, duplicate);
        await store.UpsertAsync(merged, cancellationToken);
        await store.DeleteAsync(userId, duplicateId, cancellationToken);
        return merged;
    }

    /// <summary>Counts a finished visit (a track that left the frame) toward the regular's totals.</summary>
    public async Task RecordVisitAsync(string userId, string regularId, double dwellSeconds, DateTimeOffset atUtc, CancellationToken cancellationToken)
    {
        if (await store.GetAsync(userId, regularId, cancellationToken) is not { } regular) return;
        await store.UpsertAsync(regular with
        {
            Visits = regular.Visits + 1,
            DwellSeconds = regular.DwellSeconds + Math.Max(0, dwellSeconds),
            LastSeenUtc = atUtc > regular.LastSeenUtc ? atUtc : regular.LastSeenUtc
        }, cancellationToken);
    }
}
