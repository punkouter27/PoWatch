using PoWatch.Application.Contracts;
using PoWatch.Domain.Models;
using PoWatch.Domain.Services;
using PoWatch.Application.Features.Regulars;
using PoWatch.Application.Features.Sessions;

namespace PoWatch.Application.Features.Ingest;

public sealed record IngestOutcome(bool SessionFound, bool Accepted, bool Replayed, IReadOnlyList<string> Errors);

/// <summary>
/// Folds a batch of ticks and scene events into storage: raw rows into the sensing log (keyed so
/// replays overwrite), then — only the first time the batch key is seen — into the minute, hour,
/// day and all-time rollups.
/// </summary>
public sealed class IngestService(
    ISessionRepository sessions,
    ISensingLog sensingLog,
    IIngestLedger ledger,
    IRollupStore rollups,
    RegularsService regulars,
    TimeProvider time)
{
    private static readonly RollupGrain[] BucketedGrains = [RollupGrain.Minute, RollupGrain.Hour, RollupGrain.Day];
    private const int MaxConcurrentMerges = 32;

    public async Task<IngestOutcome> IngestAsync(
        string userId,
        Guid sessionId,
        Guid batchKey,
        IReadOnlyList<Tick> ticks,
        IReadOnlyList<SceneEvent> events,
        CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(userId, sessionId, cancellationToken);
        if (session is null)
            return new IngestOutcome(false, false, false, []);

        var now = time.GetUtcNow();
        if (session.IsRunning && session.LastActivityUtc < now - SessionService.StaleAfter)
        {
            await sessions.StopIfStaleAsync(userId, sessionId, now - SessionService.StaleAfter, cancellationToken);
            session = await sessions.GetAsync(userId, sessionId, cancellationToken);
            if (session is null) return new IngestOutcome(false, false, false, []);
        }

        // Without this a client could backdate ticks into any past day and rewrite its stats.
        bool OutsideSession(DateTimeOffset at) => at < session.StartedUtc - Tick.MaxClockSkew
            || (session.EndedUtc is { } ended && at > ended + Tick.MaxClockSkew);
        var errors = ticks.SelectMany(t => t.Validate(now))
            .Concat(ticks.Where(t => OutsideSession(t.StartUtc)).Select(_ => "A tick falls outside its session."))
            .Concat(events.Where(e => OutsideSession(e.AtUtc)).Select(_ => "An event falls outside its session."))
            .Concat(events.SelectMany(e => e.Validate(now)))
            .Concat(ticks.Where(t => t.SessionId != sessionId).Select(_ => "A tick belongs to a different session."))
            .Concat(events.Where(e => e.SessionId != sessionId).Select(_ => "An event belongs to a different session."))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (errors.Count > 0)
            return new IngestOutcome(true, false, false, errors);

        await sessions.TouchAsync(userId, sessionId, now, cancellationToken);
        var zone = session.TimeZone;

        // Raw rows first: they are idempotent, and they are what a rollup rebuild would read.
        var days = ticks.Select(t => LocalDay.Of(t.StartUtc, zone))
            .Concat(events.Select(e => LocalDay.Of(e.AtUtc, zone)))
            .Distinct();
        foreach (var day in days)
        {
            await sensingLog.AppendAsync(
                userId,
                day,
                ticks.Where(t => LocalDay.Of(t.StartUtc, zone) == day).ToList(),
                events.Where(e => LocalDay.Of(e.AtUtc, zone) == day).ToList(),
                cancellationToken);
        }

        if (!await ledger.TryClaimAsync(userId, batchKey, cancellationToken))
            return new IngestOutcome(true, true, true, []);

        var deltas = ticks.Select(t => (At: t.StartUtc, Rollup: Rollup.FromTick(t)))
            .Concat(events.Select(e => (At: e.AtUtc, Rollup: Rollup.FromEvent(e))))
            .ToList();

        // One merge per touched bucket, not per tick. Every bucket is its own row, so the merges run
        // together, a bounded number at a time: a backlog batch can touch thousands of minute buckets.
        var merges = BucketedGrains.SelectMany(grain => deltas
            .GroupBy(d => RollupBuckets.StartUtc(grain, d.At, zone))
            .Select(bucket => (Grain: grain, StartUtc: bucket.Key, Delta: bucket.Select(d => d.Rollup).Aggregate(Rollup.Merge))))
            .ToList();
        if (deltas.Count > 0)
            merges.Add((RollupGrain.AllTime, DateTimeOffset.UnixEpoch, deltas.Select(d => d.Rollup).Aggregate(Rollup.Merge)));
        foreach (var chunk in merges.Chunk(MaxConcurrentMerges))
            await Task.WhenAll(chunk.Select(m => rollups.MergeAsync(userId, m.Grain, m.StartUtc, m.Delta, cancellationToken)));

        // Regulars' totals move with the rollups: only the first time a batch is claimed.
        foreach (var exit in events.Where(e => e.Kind == SceneEventKind.TrackExit && e.RegularId is not null))
            await regulars.RecordVisitAsync(userId, exit.RegularId!, exit.DwellSeconds ?? 0, exit.AtUtc, cancellationToken);

        return new IngestOutcome(true, true, false, []);
    }
}
