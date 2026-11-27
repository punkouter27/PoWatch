namespace PoWatch.Shared.Models;

public sealed class StartSessionRequestDto
{
    /// <summary>Optional client-chosen id, so a retried start returns the same session.</summary>
    public Guid? SessionId { get; init; }

    /// <summary>The browser's IANA time zone; every tick in the session is bucketed into this zone's days.</summary>
    public string TimeZoneId { get; init; } = string.Empty;
}

public sealed class SessionDto
{
    /// <summary>The short session tag shown everywhere: "#AB12".</summary>
    public static string Tag(Guid id) => $"#{id.ToString("N")[..4].ToUpperInvariant()}";

    public Guid Id { get; init; }
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset? EndedUtc { get; init; }
    public string TimeZoneId { get; init; } = string.Empty;
    public bool IsRunning { get; init; }
    public double DurationSeconds { get; init; }

    /// <summary>Session-level aggregates captured on the device (faces, bodies, posture, energy) for Journal reports.</summary>
    public SessionSummaryDto? Summary { get; init; }
}

/// <summary>
/// Aggregate-only summary of on-device face and body readings saved with a session so Journal reports
/// can analyse everything captured alongside the session's snapshots. Never holds embeddings or per-face rows.
/// </summary>
public sealed class SessionSummaryDto
{
    public FaceSummaryDto? Faces { get; init; }
    public BodySummaryDto? Bodies { get; init; }
}

public sealed class FaceSummaryDto
{
    public double? MeanBeauty { get; init; }
    public double? BeautyLow { get; init; }
    public double? BeautyHigh { get; init; }
    public double? MeanAge { get; init; }
    public double? AgeLow { get; init; }
    public double? AgeHigh { get; init; }
    public int MaxFacesAtOnce { get; init; }
    public int FacesLabelled { get; init; }
    public double? MaleShare { get; init; }
    public double? FemaleShare { get; init; }
    public List<double> BeautyHistory { get; init; } = [];
    public List<double> AgeHistory { get; init; } = [];
}

public sealed class BodySummaryDto
{
    public double ObservedSeconds { get; init; }
    public double? PostureScore { get; init; }
    public double UprightSeconds { get; init; }
    public double SlumpedSeconds { get; init; }
    public double LongestSlumpSeconds { get; init; }
    public double? MeanEnergy { get; init; }
    public bool FullBodySeen { get; init; }
    public double? ArmBalance { get; init; }
    public double? LegBalance { get; init; }
    public double? ShoulderTilt { get; init; }
    public double OpenSeconds { get; init; }
    public Dictionary<string, double> ActivitySeconds { get; init; } = [];
    public Dictionary<string, double> CueSeconds { get; init; } = [];
    public List<double> EnergyHistory { get; init; } = [];
}

/// <summary>One ten-minute chapter of a session's story.</summary>
public sealed class StoryChapterDto
{
    public int Index { get; init; }
    public DateTimeOffset FromUtc { get; init; }
    public DateTimeOffset ToUtc { get; init; }
    public string Text { get; init; } = string.Empty;

    /// <summary>template, or the server model's provider name.</summary>
    public string Source { get; init; } = "template";
}

/// <summary>A session's chapters so far and, once it has ended, the one story they were joined into.</summary>
public sealed class SessionStoryDto
{
    public Guid SessionId { get; init; }
    public List<StoryChapterDto> Chapters { get; init; } = [];

    /// <summary>Null while the session is still running.</summary>
    public string? Story { get; init; }

    public string? Source { get; init; }
    public DateTimeOffset? WrittenUtc { get; init; }
}

/// <summary>One day's part of a week's or month's story.</summary>
public sealed class DayStoryDto
{
    public DateOnly Date { get; init; }
    public string Text { get; init; } = string.Empty;
}

/// <summary>The story of a whole week (Monday to Sunday) or calendar month, and the days it was told from, newest first.</summary>
public sealed class PeriodStoryDto
{
    /// <summary>week or month.</summary>
    public string Kind { get; init; } = "week";

    public DateOnly From { get; init; }
    public DateOnly To { get; init; }
    public string Story { get; init; } = string.Empty;

    /// <summary>template, or the server model's provider name.</summary>
    public string Source { get; init; } = "template";

    public List<DayStoryDto> Days { get; init; } = [];
}

/// <summary>A line of a session's transcript: words the on-device speech model heard.</summary>
public sealed class HeardLineDto
{
    public DateTimeOffset AtUtc { get; init; }
    public string Text { get; init; } = string.Empty;

    /// <summary>The regular who said it, by label ("Person 3"), when their voice was known; null for "someone".</summary>
    public string? Speaker { get; init; }
}
