using PoWatch.Application.Contracts;
using PoWatch.Domain.Models;
using PoWatch.Domain.Services;
using PoWatch.Shared.Models;
using PoWatch.Application.Features.Sessions;

namespace PoWatch.Application.Features.Stats;

/// <summary>
/// Reads rollups for a window and runs the domain stat calculators over them. Everything here is
/// composition; the maths lives in <c>PoWatch.Domain.Services</c> and is unit-tested there.
/// </summary>
public sealed partial class StatsQueryService(IRollupStore rollups, ISensingLog sensingLog, SessionService sessions, IRegularStore regulars, TimeProvider time)
{
    /// <summary>The local days a window touches, up to today.</summary>
    private IEnumerable<DateOnly> DaysOf(StatsWindow window)
    {
        var lastDay = LocalDay.Of(Min(window.ToUtc, time.GetUtcNow()), window.Zone);
        for (var day = LocalDay.Of(window.FromUtc, window.Zone); day <= lastDay; day = day.AddDays(1))
            yield return day;
    }

    private async Task<List<SceneEvent>> EventsAsync(string userId, StatsWindow window, CancellationToken cancellationToken)
    {
        var events = new List<SceneEvent>();
        foreach (var day in DaysOf(window))
            events.AddRange((await sensingLog.GetEventsAsync(userId, day, cancellationToken)).Where(e => window.Contains(e.AtUtc)));
        return events;
    }

    /// <summary>Days that make up "your usual".</summary>
    public const int BaselineDays = 28;

    private const int CalendarDays = 365;

    private static readonly Dictionary<string, StatsRange> RangeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["session"] = StatsRange.Session,
        ["today"] = StatsRange.Today,
        ["7d"] = StatsRange.Week,
        ["30d"] = StatsRange.Month,
        ["all"] = StatsRange.All,
        ["day"] = StatsRange.Day
    };

    public static bool TryParseRange(string? value, out StatsRange range) =>
        RangeNames.TryGetValue(value ?? "today", out range);

    public static string RangeName(StatsRange range) => RangeNames.First(kv => kv.Value == range).Key;

    /// <summary>
    /// Null when the range is <see cref="StatsRange.Session"/> and the session is not the user's, or
    /// <see cref="StatsRange.Day"/> without a date.
    /// </summary>
    public async Task<StatsWindow?> ResolveAsync(string userId, StatsRange range, string? timeZoneId, Guid? sessionId, CancellationToken cancellationToken, DateOnly? date = null)
    {
        var session = sessionId is { } id ? await sessions.GetAsync(userId, id, cancellationToken) : null;
        if (range == StatsRange.Session && session is null) return null;
        if (range == StatsRange.Day && date is null) return null;

        var zone = LocalDay.FindZone(timeZoneId) ?? session?.TimeZone ?? TimeZoneInfo.Utc;
        var now = time.GetUtcNow();
        var today = LocalDay.Today(time, zone);
        DateTimeOffset DayStart(int daysAgo) => LocalDay.Window(today.AddDays(-daysAgo), zone).StartUtc;

        return range switch
        {
            StatsRange.Session => new StatsWindow(range,
                RollupBuckets.StartUtc(RollupGrain.Minute, session!.StartedUtc, zone),
                (session.EndedUtc ?? now).AddMinutes(1), RollupGrain.Minute, zone),
            StatsRange.Week => new StatsWindow(range, DayStart(6), now.AddHours(1), RollupGrain.Hour, zone),
            StatsRange.Month => new StatsWindow(range, DayStart(29), now.AddHours(1), RollupGrain.Hour, zone),
            StatsRange.All => new StatsWindow(range, DateTimeOffset.UnixEpoch, now.AddDays(1), RollupGrain.Day, zone),
            StatsRange.Day => LocalDay.Window(date!.Value, zone) is var (start, end)
                ? new StatsWindow(range, start, end, RollupGrain.Minute, zone)
                : null,
            _ => new StatsWindow(range, DayStart(0), now.AddMinutes(1), RollupGrain.Minute, zone)
        };
    }

    private Task<IReadOnlyList<Rollup>> SeriesAsync(string userId, StatsWindow window, CancellationToken cancellationToken) =>
        rollups.GetRangeAsync(userId, window.Grain, window.FromUtc, window.ToUtc, cancellationToken);

    private static Rollup Total(IEnumerable<Rollup> series) => series.Aggregate(Rollup.Empty, Rollup.Merge);

    private static AnomalyDto Anomaly(Anomaly a) => new()
    {
        Metric = a.Metric,
        Today = a.Today,
        Mean = a.Mean,
        StdDev = a.StdDev,
        Z = a.Z,
        Flagged = a.Flagged
    };

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
