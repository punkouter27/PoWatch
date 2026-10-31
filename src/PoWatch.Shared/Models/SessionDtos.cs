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
