namespace PoWatch.Shared.Models;

/// <summary>Pushed over the stats hub when an ingest batch lands.</summary>
public sealed class StatsChangedDto
{
    public DateTimeOffset AtUtc { get; init; }
    public Guid SessionId { get; init; }
    public int Ticks { get; init; }
    public int Events { get; init; }
}

/// <summary>The window a stats response covers and the rollup grain it was read from.</summary>
public sealed class StatsWindowDto
{
    /// <summary>session, today, 7d, 30d or all.</summary>
    public string Range { get; init; } = string.Empty;
    public DateTimeOffset FromUtc { get; init; }
    public DateTimeOffset ToUtc { get; init; }

    /// <summary>Minute, Hour or Day.</summary>
    public string Grain { get; init; } = string.Empty;
    public string TimeZoneId { get; init; } = string.Empty;
}

public sealed class SeriesPointDto
{
    public DateTimeOffset AtUtc { get; init; }
    public double Motion { get; init; }
    public double MotionPeak { get; init; }
    public double Occupancy { get; init; }
    public double Luminance { get; init; }
    public long Visits { get; init; }
    public int PeakConcurrency { get; init; }
}

/// <summary>Stat family A — presence and motion.</summary>
public sealed class PresenceStatsDto
{
    public StatsWindowDto Window { get; init; } = new();
    public double SecondsObserved { get; init; }
    public double Occupancy { get; init; }
    public long Visits { get; init; }
    public double VisitsPerHour { get; init; }
    public int PeakConcurrency { get; init; }
    public double? DwellP50Seconds { get; init; }
    public double? DwellP90Seconds { get; init; }
    public double DwellMaxSeconds { get; init; }
    public double LongestEmptySeconds { get; init; }
    public double LongestStillSeconds { get; init; }
    public DateTimeOffset? BusiestStartUtc { get; init; }
    public double BusiestMotion { get; init; }

    /// <summary>Person-seconds spent sitting, standing and lying down in the window; all zero when posture was not read.</summary>
    public double SittingSeconds { get; init; }

    public double StandingSeconds { get; init; }

    public double LyingSeconds { get; init; }

    /// <summary>The longest unbroken run of buckets someone mostly sat through.</summary>
    public double LongestSittingSeconds { get; init; }
    public List<SeriesPointDto> Series { get; init; } = [];
}

/// <summary>Stat family A — where in the frame.</summary>
public sealed class SpaceStatsDto
{
    public StatsWindowDto Window { get; init; } = new();
    public List<double> MotionHeat { get; init; } = [];
    public List<double> PresenceHeat { get; init; } = [];
    public int HottestCell { get; init; } = -1;
    public int FavouriteSpotCell { get; init; } = -1;
    public Dictionary<string, long> Entries { get; init; } = [];
    public Dictionary<string, long> Exits { get; init; } = [];
    public string BusiestEdge { get; init; } = "None";
}

public sealed class ClassStatDto
{
    public string Name { get; init; } = string.Empty;
    public long TicksPresent { get; init; }
    public int Peak { get; init; }
    public double MeanCount { get; init; }

    /// <summary>Fraction of observed ticks in which the class appeared.</summary>
    public double Share { get; init; }
}

public sealed class RegularStatDto
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Class { get; init; } = string.Empty;
    public long Visits { get; init; }
    public double DwellSeconds { get; init; }
}

/// <summary>Stat family B — the object census and the regulars leaderboard.</summary>
public sealed class ObjectStatsDto
{
    public StatsWindowDto Window { get; init; } = new();
    public List<ClassStatDto> Classes { get; init; } = [];
    public string? Rarest { get; init; }
    public List<RegularStatDto> Regulars { get; init; } = [];
}

public sealed class AnomalyDto
{
    public string Metric { get; init; } = string.Empty;
    public double Today { get; init; }
    public double Mean { get; init; }
    public double StdDev { get; init; }
    public double? Z { get; init; }
    public bool Flagged { get; init; }
}

public sealed class DayValueDto
{
    public DateOnly Date { get; init; }
    public double Occupancy { get; init; }
    public long Visits { get; init; }
    public double Motion { get; init; }
}

/// <summary>Stat family C — patterns and today vs. usual.</summary>
public sealed class PatternStatsDto
{
    public StatsWindowDto Window { get; init; } = new();

    /// <summary>Mean occupancy per [weekday (Sunday = 0)][local hour] over the last 28 days.</summary>
    public List<List<double>> HourByWeekday { get; init; } = [];
    public List<double> TodayProfile { get; init; } = [];
    public List<double> UsualProfile { get; init; } = [];
    public double? RhythmScore { get; init; }
    public List<AnomalyDto> Anomalies { get; init; } = [];
    public double OccupancyTrendPerDay { get; init; }
    public int? NextBusyHour { get; init; }
    public List<DayValueDto> Calendar { get; init; } = [];
}

public sealed class LightPointDto
{
    public DateTimeOffset AtUtc { get; init; }
    public double Luminance { get; init; }
}

public sealed class LightSwitchDto
{
    public DateTimeOffset AtUtc { get; init; }
    public bool On { get; init; }
}

public sealed class PaletteColorDto
{
    public int Rgb { get; init; }
    public double Share { get; init; }
}

public sealed class WordCountDto
{
    public string Word { get; init; } = string.Empty;
    public int Count { get; init; }
}

/// <summary>Stat family D — light, colour and captions.</summary>
public sealed class EnvironmentStatsDto
{
    public StatsWindowDto Window { get; init; } = new();
    public List<LightPointDto> LightCurve { get; init; } = [];
    public List<LightSwitchDto> Switches { get; init; } = [];
    public DateTimeOffset? SunriseUtc { get; init; }
    public DateTimeOffset? SunsetUtc { get; init; }
    public List<PaletteColorDto> Palette { get; init; } = [];
    public int CaptionCount { get; init; }
    public List<WordCountDto> Words { get; init; } = [];
    public string? WeirdestCaption { get; init; }
    public DateTimeOffset? WeirdestAtUtc { get; init; }
}

/// <summary>Stat family F — the pipeline's own numbers.</summary>
public sealed class PipelineStatsDto
{
    public StatsWindowDto Window { get; init; } = new();
    public long Ticks { get; init; }
    public double SecondsObserved { get; init; }
    public long PixelSamples { get; init; }
    public long DetectorSamples { get; init; }
    public double PixelHz { get; init; }
    public double DetectorHz { get; init; }
    public long AllTimeTicks { get; init; }
    public long AllTimeFrames { get; init; }
    public double AllTimeHours { get; init; }
    public int Sessions { get; init; }
    public double? RunningSessionSeconds { get; init; }
}
