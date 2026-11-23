using PoWatch.Application.Contracts;
using PoWatch.Domain.Models;
using PoWatch.Domain.Services;

namespace PoWatch.Application.Features.Regulars;

public sealed record Observation(Regular Regular, bool IsNew);

/// <summary>Recognises a user's regulars by appearance and keeps score; they are never named.</summary>
public sealed class RegularsService(IRegularStore store, TimeProvider time)
{
    public Task<IReadOnlyList<Regular>> ListAsync(string userId, CancellationToken cancellationToken) =>
        store.ListAsync(userId, cancellationToken);

    /// <summary>
    /// Matches a sighting to a known regular of the same class, or creates a new one
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
