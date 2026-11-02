namespace PoWatch.Shared.Models;

/// <summary>One observed minute of a day, for the replay track.</summary>
public sealed class ReplayMinuteDto
{
    public DateTimeOffset AtUtc { get; init; }
    public double Occupancy { get; init; }
    public int Peak { get; init; }
    public double Motion { get; init; }
    public double Luminance { get; init; }
}

/// <summary>Something that happened at an instant: a caption, words or a sound heard, a notable moment, an entrance or an exit.</summary>
public sealed class ReplayEventDto
{
    public DateTimeOffset AtUtc { get; init; }

    /// <summary>caption, speech, sound, moment, enter or exit.</summary>
    public string Kind { get; init; } = string.Empty;
    public string? Text { get; init; }
    public string? Class { get; init; }

    /// <summary>The regular who spoke, came in or left, by name, once they were recognised; null for "someone".</summary>
    public string? Who { get; init; }
}

/// <summary>A day, minute by minute, for scrubbing through: only minutes something was observed in.</summary>
public sealed class ReplayDayDto
{
    public StatsWindowDto Window { get; init; } = new();
    public List<ReplayMinuteDto> Minutes { get; init; } = [];

    /// <summary>
    /// Each minute's 16×9 motion grid as 144 bytes (0–255, relative to that minute's busiest cell),
    /// one after another in the order of <see cref="Minutes"/>, base64-encoded.
    /// </summary>
    public string Grids { get; init; } = string.Empty;
    public List<ReplayEventDto> Events { get; init; } = [];
}
