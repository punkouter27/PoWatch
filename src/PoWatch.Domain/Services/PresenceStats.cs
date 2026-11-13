using PoWatch.Domain.Models;

namespace PoWatch.Domain.Services;

public sealed record PresenceSummary(
    double Occupancy,
    double VisitsPerHour,
    int PeakConcurrency,
    double? DwellP50Seconds,
    double? DwellP90Seconds,
    double DwellMaxSeconds,
    TimeSpan LongestEmptyStreak,
    TimeSpan LongestStillStreak,
    DateTimeOffset? BusiestStartUtc,
    double BusiestMotion,
    TimeSpan LongestSittingStreak);

/// <summary>Stat family A — presence and motion — computed from a single-grain rollup series.</summary>
public static class PresenceStats
{
    /// <summary>Peak motion below this counts as "still".</summary>
    public const double StillThreshold = 0.01;

    /// <param name="series">Rollups of one grain; several rollups for the same bucket are merged first.</param>
    /// <param name="grain">The bucket length, used to tell consecutive buckets from gaps.</param>
    public static PresenceSummary Summarize(IEnumerable<Rollup> series, TimeSpan grain)
    {
        ArgumentNullException.ThrowIfNull(series);

        var buckets = series
            .GroupBy(r => r.BucketStartUtc)
            .Select(g => g.Aggregate(Rollup.Merge))
            .OrderBy(r => r.BucketStartUtc)
            .ToList();
        var total = buckets.Aggregate(Rollup.Empty, Rollup.Merge);
        var busiest = buckets.Where(b => b.Ticks > 0).MaxBy(b => b.Motion.Mean);

        return new PresenceSummary(
            Occupancy: total.OccupancyFraction,
            VisitsPerHour: total.Seconds > 0 ? total.Visits / (total.Seconds / 3600) : 0,
            PeakConcurrency: total.PeakConcurrency,
            DwellP50Seconds: DwellPercentile(total, 0.5),
            DwellP90Seconds: DwellPercentile(total, 0.9),
            DwellMaxSeconds: total.DwellMaxSeconds,
            LongestEmptyStreak: LongestRun(buckets, grain, b => b.OccupiedTicks == 0),
            LongestStillStreak: LongestRun(buckets, grain, b => b.MotionPeak.Max < StillThreshold),
            BusiestStartUtc: busiest?.BucketStartUtc,
            BusiestMotion: busiest?.Motion.Mean ?? 0,
            LongestSittingStreak: LongestRun(buckets, grain, MostlySitting));
    }

    /// <summary>
    /// A bucket someone sat through: sitting for at least half of it, and for longer than anyone stood.
    /// Person-seconds, so two people sitting half the time count as one sitting throughout.
    /// </summary>
    public static bool MostlySitting(Rollup bucket)
    {
        ArgumentNullException.ThrowIfNull(bucket);
        return bucket.Seconds > 0 && bucket.SittingSeconds >= bucket.Seconds / 2 && bucket.SittingSeconds > bucket.StandingSeconds;
    }

    /// <summary>The dwell time at quantile <paramref name="p"/>, to the resolution of one histogram bin.</summary>
    public static double? DwellPercentile(Rollup rollup, double p)
    {
        ArgumentNullException.ThrowIfNull(rollup);
        var count = rollup.DwellHistogram.Values.Sum();
        if (count == 0) return null;

        var rank = p * count;
        long cumulative = 0;
        foreach (var (bin, binCount) in rollup.DwellHistogram.OrderBy(kv => kv.Key))
        {
            cumulative += binCount;
            if (cumulative >= rank) return Rollup.DwellBinCentre(bin);
        }

        return Rollup.DwellBinCentre(rollup.DwellHistogram.Keys.Max());
    }

    /// <summary>
    /// Longest run of consecutive recorded buckets matching <paramref name="predicate"/>. A missing
    /// bucket (no ticks) breaks the run: no data is not the same as an empty room.
    /// </summary>
    private static TimeSpan LongestRun(List<Rollup> buckets, TimeSpan grain, Func<Rollup, bool> predicate)
    {
        int longest = 0, current = 0;
        DateTimeOffset? previous = null;

        foreach (var bucket in buckets.Where(b => b.Ticks > 0))
        {
            var contiguous = previous is { } p && bucket.BucketStartUtc - p == grain;
            current = predicate(bucket) ? (contiguous ? current + 1 : 1) : 0;
            longest = Math.Max(longest, current);
            previous = bucket.BucketStartUtc;
        }

        return grain * longest;
    }
}
