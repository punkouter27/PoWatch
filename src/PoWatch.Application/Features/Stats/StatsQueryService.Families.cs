using PoWatch.Application.Contracts;
using PoWatch.Domain.Models;
using PoWatch.Domain.Services;
using PoWatch.Shared.Models;
using PoWatch.Application.Features.Sessions;

namespace PoWatch.Application.Features.Stats;

/// <summary>The stat families over a window: presence, space, objects, patterns, environment, pipeline.</summary>
public sealed partial class StatsQueryService
{
    public async Task<PresenceStatsDto> PresenceAsync(string userId, StatsWindow window, CancellationToken cancellationToken)
    {
        var series = await SeriesAsync(userId, window, cancellationToken);
        var summary = PresenceStats.Summarize(series, window.GrainLength);
        var total = Total(series);

        return new PresenceStatsDto
        {
            Window = window.ToDto(),
            SecondsObserved = total.Seconds,
            Occupancy = summary.Occupancy,
            Visits = total.Visits,
            VisitsPerHour = summary.VisitsPerHour,
            PeakConcurrency = summary.PeakConcurrency,
            DwellP50Seconds = summary.DwellP50Seconds,
            DwellP90Seconds = summary.DwellP90Seconds,
            DwellMaxSeconds = summary.DwellMaxSeconds,
            LongestEmptySeconds = summary.LongestEmptyStreak.TotalSeconds,
            LongestStillSeconds = summary.LongestStillStreak.TotalSeconds,
            BusiestStartUtc = summary.BusiestStartUtc,
            BusiestMotion = summary.BusiestMotion,
            SittingSeconds = total.SittingSeconds,
            StandingSeconds = total.StandingSeconds,
            LyingSeconds = total.LyingSeconds,
            LongestSittingSeconds = summary.LongestSittingStreak.TotalSeconds,
            Series = series.Select(r => new SeriesPointDto
            {
                AtUtc = r.BucketStartUtc,
                Motion = r.Motion.Mean,
                MotionPeak = r.MotionPeak.Count == 0 ? 0 : r.MotionPeak.Max,
                Occupancy = r.OccupancyFraction,
                Luminance = r.Luminance.Mean,
                Visits = r.Visits,
                PeakConcurrency = r.PeakConcurrency
            }).ToList()
        };
    }

    public async Task<SpaceStatsDto> SpaceAsync(string userId, StatsWindow window, CancellationToken cancellationToken)
    {
        var space = SpaceStats.Summarize(Total(await SeriesAsync(userId, window, cancellationToken)));
        return new SpaceStatsDto
        {
            Window = window.ToDto(),
            MotionHeat = space.MotionHeat.ToList(),
            PresenceHeat = space.PresenceHeat.ToList(),
            HottestCell = space.HottestCell,
            FavouriteSpotCell = space.FavouriteSpotCell,
            Entries = space.Entries.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            Exits = space.Exits.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            BusiestEdge = space.BusiestEdge.ToString()
        };
    }

    public async Task<ObjectStatsDto> ObjectsAsync(string userId, StatsWindow window, CancellationToken cancellationToken)
    {
        var total = Total(await SeriesAsync(userId, window, cancellationToken));
        var classes = total.Classes
            .Select(kv => new ClassStatDto
            {
                Name = kv.Key,
                TicksPresent = kv.Value.TicksPresent,
                Peak = kv.Value.PeakCount,
                MeanCount = kv.Value.TicksPresent == 0 ? 0 : kv.Value.MeanSum / kv.Value.TicksPresent,
                Share = total.Ticks == 0 ? 0 : (double)kv.Value.TicksPresent / total.Ticks
            })
            .OrderByDescending(c => c.TicksPresent)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        var known = total.Regulars.Count == 0
            ? new Dictionary<string, Regular>()
            : (await regulars.ListAsync(userId, cancellationToken)).ToDictionary(r => r.Id, StringComparer.Ordinal);

        return new ObjectStatsDto
        {
            Window = window.ToDto(),
            Classes = classes,
            Rarest = classes.Count > 1 ? classes[^1].Name : null,
            Regulars = total.Regulars
                .Where(kv => known.ContainsKey(kv.Key))
                .Select(kv => new RegularStatDto
                {
                    Id = kv.Key,
                    DisplayName = known[kv.Key].DisplayName,
                    Class = known[kv.Key].Class,
                    Visits = kv.Value.Visits,
                    DwellSeconds = kv.Value.DwellSeconds
                })
                .OrderByDescending(r => r.Visits)
                .ThenByDescending(r => r.DwellSeconds)
                .ToList()
        };
    }

    public async Task<PatternStatsDto> PatternsAsync(string userId, StatsWindow window, CancellationToken cancellationToken)
    {
        var zone = window.Zone;
        var now = time.GetUtcNow();
        var today = LocalDay.Today(time, zone);
        var todayStart = LocalDay.Window(today, zone).StartUtc;
        var baselineStart = LocalDay.Window(today.AddDays(-BaselineDays), zone).StartUtc;

        var baselineRead = rollups.GetRangeAsync(userId, RollupGrain.Hour, baselineStart, todayStart, cancellationToken);
        var todayRead = rollups.GetRangeAsync(userId, RollupGrain.Hour, todayStart, now.AddHours(1), cancellationToken);
        var calendarRead = rollups.GetRangeAsync(userId, RollupGrain.Day,
            LocalDay.Window(today.AddDays(-(CalendarDays - 1)), zone).StartUtc, now.AddDays(1), cancellationToken);
        await Task.WhenAll(baselineRead, todayRead, calendarRead);
        var (baselineHours, todayHours, calendar) = (await baselineRead, await todayRead, await calendarRead);

        static double Occupancy(Rollup r) => r.OccupancyFraction;
        static double VisitsPerHour(Rollup r) => r.Seconds > 0 ? r.Visits / (r.Seconds / 3600) : 0;

        var baselineDays = calendar.Where(r => r.Ticks > 0 && r.BucketStartUtc >= baselineStart && r.BucketStartUtc < todayStart).ToList();
        var todayTotal = Total(todayHours);
        var usualProfile = PatternStats.HourlyProfile(baselineHours, zone, Occupancy);
        var todayProfile = PatternStats.HourlyProfile(todayHours, zone, Occupancy);

        return new PatternStatsDto
        {
            Window = window.ToDto(),
            HourByWeekday = PatternStats.HourByWeekday(baselineHours, zone, Occupancy).Select(d => d.ToList()).ToList(),
            TodayProfile = todayProfile.ToList(),
            UsualProfile = usualProfile.ToList(),
            RhythmScore = todayTotal.Ticks > 0 ? AnomalyMath.RhythmScore(todayProfile, usualProfile) : null,
            Anomalies = todayTotal.Ticks == 0
                ? []
                :
                [
                    Anomaly(AnomalyMath.Compare("occupancy", Occupancy(todayTotal), baselineDays.Select(Occupancy).ToList())),
                    Anomaly(AnomalyMath.Compare("visits/hour", VisitsPerHour(todayTotal), baselineDays.Select(VisitsPerHour).ToList())),
                    Anomaly(AnomalyMath.Compare("motion", todayTotal.Motion.Mean, baselineDays.Select(r => r.Motion.Mean).ToList())),
                ],
            OccupancyTrendPerDay = AnomalyMath.TrendSlope(baselineDays.Select(Occupancy).ToList()),
            NextBusyHour = PatternStats.NextBusyHour(baselineHours, zone, now, Occupancy),
            Calendar = calendar.Where(r => r.Ticks > 0).Select(r => new DayValueDto
            {
                Date = LocalDay.Of(r.BucketStartUtc, zone),
                Occupancy = r.OccupancyFraction,
                Visits = r.Visits,
                Motion = r.Motion.Mean
            }).ToList()
        };
    }

    public async Task<EnvironmentStatsDto> EnvironmentAsync(string userId, StatsWindow window, CancellationToken cancellationToken)
    {
        var series = await SeriesAsync(userId, window, cancellationToken);
        var daylight = window.IsShort ? EnvironmentStats.EstimateDaylight(series) : null;

        // Captions are raw events, so only a short window reads them.
        List<(DateTimeOffset AtUtc, string Text)> captions = window.IsShort
            ? [.. (await EventsAsync(userId, window, cancellationToken))
                .Where(e => e.Kind == SceneEventKind.Caption && e.Text is not null && !CaptionStats.IsUninformative(e.Text))
                .Select(e => (e.AtUtc, e.Text!))]
            : [];

        var weirdest = CaptionStats.Weirdest(captions);
        return new EnvironmentStatsDto
        {
            Window = window.ToDto(),
            LightCurve = EnvironmentStats.LightCurve(series).Select(p => new LightPointDto { AtUtc = p.StartUtc, Luminance = p.Luminance }).ToList(),
            Switches = EnvironmentStats.LightSwitches(series).Select(s => new LightSwitchDto { AtUtc = s.AtUtc, On = s.On }).ToList(),
            SunriseUtc = daylight?.SunriseUtc,
            SunsetUtc = daylight?.SunsetUtc,
            Palette = EnvironmentStats.DominantColors(Total(series), 5).Select(c => new PaletteColorDto { Rgb = c.Rgb, Share = c.Share }).ToList(),
            CaptionCount = captions.Count,
            Words = CaptionStats.WordFrequencies(captions.Select(c => c.Text), 30).Select(w => new WordCountDto { Word = w.Word, Count = w.Count }).ToList(),
            WeirdestCaption = weirdest?.Text,
            WeirdestAtUtc = weirdest?.AtUtc
        };
    }

    public async Task<PipelineStatsDto> PipelineAsync(string userId, StatsWindow window, CancellationToken cancellationToken)
    {
        var total = Total(await SeriesAsync(userId, window, cancellationToken));
        var allTime = await rollups.GetAllTimeAsync(userId, cancellationToken);
        var recent = await sessions.ListAsync(userId, SessionService.MaxListed, cancellationToken);
        var running = recent.FirstOrDefault(s => s.IsRunning);

        return new PipelineStatsDto
        {
            Window = window.ToDto(),
            Ticks = total.Ticks,
            SecondsObserved = total.Seconds,
            PixelSamples = total.PixelSamples,
            DetectorSamples = total.DetectorSamples,
            PixelHz = total.Seconds > 0 ? total.PixelSamples / total.Seconds : 0,
            DetectorHz = total.Seconds > 0 ? total.DetectorSamples / total.Seconds : 0,
            AllTimeTicks = allTime.Ticks,
            AllTimeFrames = allTime.PixelSamples,
            AllTimeHours = allTime.Seconds / 3600,
            Sessions = recent.Count,
            RunningSessionSeconds = running?.Duration(time.GetUtcNow()).TotalSeconds
        };
    }
}
