using Microsoft.Extensions.Logging;
using PoWatch.Application.Contracts;
using PoWatch.Domain.Models;
using PoWatch.Domain.Services;

namespace PoWatch.Application.Features.Data;

/// <summary>
/// Applies each user's retention window: raw ticks, scene events, snapshots and ingest claims older
/// than it are deleted. Rollups are never touched, so every chart, the calendar and the all-time
/// numbers still cover the whole history; only replays, captions and moments of old days go.
/// </summary>
public sealed class RetentionService(
    IProfileStore profiles,
    ISensingLog sensingLog,
    ISnapshotStore snapshots,
    IIngestLedger ledger,
    TimeProvider time,
    ILogger<RetentionService> logger)
{
    /// <summary>Prunes one user's raw record; returns the rows and blobs removed (0 when they keep everything).</summary>
    public async Task<int> PruneAsync(UserProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.RetentionDays is not { } days) return 0;

        var zone = LocalDay.FindZone(profile.TimeZoneId) ?? TimeZoneInfo.Utc;
        var firstKept = LocalDay.Today(time, zone).AddDays(-days);
        return await sensingLog.PruneBeforeAsync(profile.UserId, firstKept, cancellationToken)
            + await snapshots.PruneBeforeAsync(profile.UserId, firstKept, cancellationToken)
            + await ledger.PruneBeforeAsync(profile.UserId, time.GetUtcNow().AddDays(-days), cancellationToken);
    }

    /// <summary>Prunes every user who chose a window. One user's failure is logged and does not stop the rest.</summary>
    public async Task PruneAllAsync(CancellationToken cancellationToken)
    {
        await foreach (var profile in profiles.ListAsync(cancellationToken))
        {
            if (profile.RetentionDays is null) continue;
            try
            {
                var removed = await PruneAsync(profile, cancellationToken);
                if (removed > 0) logger.LogInformation("Retention removed {Removed} old rows and snapshots.", removed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Retention pruning failed for one user; the next pass tries again.");
            }
        }
    }
}
