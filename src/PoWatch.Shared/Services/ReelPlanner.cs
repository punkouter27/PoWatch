using System.Globalization;
using PoWatch.Shared.Models;

namespace PoWatch.Shared.Services;

/// <summary>One time-lapse frame in the reel.</summary>
/// <param name="FrameAt">The frame's time (Unix ms), as the browser's time-lapse store keys it.</param>
/// <param name="Hold">How many video frames it stays up.</param>
/// <param name="Text">What to write under it: the notable moment it shows, else the caption of that time; null for nothing.</param>
/// <param name="Star">Whether it shows a notable moment.</param>
/// <param name="Level">How busy its minute was against the busiest, in [0, 1], for the reel's activity strip.</param>
public sealed record ReelShot(long FrameAt, int Hold, string? Text, bool Star, double Level);

/// <summary>A highlight reel, ready to draw: timelapse.js paints it and records the WebM.</summary>
public sealed class ReelPlan
{
    public int Fps { get; init; } = ReelPlanner.Fps;
    public string Title { get; init; } = string.Empty;

    /// <summary>The closing card: the day's numbers, one per line.</summary>
    public List<string> Facts { get; init; } = [];
    public List<ReelShot> Shots { get; init; } = [];
}

/// <summary>
/// Cuts a day's (or a session's) time-lapse down to about half a minute. The browser keeps one frame a
/// minute; the reel keeps the frames of the liveliest minutes, in order, lingers on the ones that show
/// a notable moment, and writes the caption of the time under each. A short recording is not cut at
/// all: its frames are held longer instead.
/// </summary>
public static class ReelPlanner
{
    public const int Fps = 12;

    /// <summary>Video frames given to the time-lapse itself: 25 seconds, between a title and a closing card.</summary>
    public const int Budget = 25 * Fps;

    /// <summary>The longest an ordinary frame is held (half a second), however few there are.</summary>
    public const int MaxHold = 6;

    /// <summary>How long a frame showing a notable moment is held (a second and a half).</summary>
    public const int StarHold = 18;

    /// <summary>At most this many frames get the long hold, so a day full of moments still fits.</summary>
    public const int MaxStars = 8;

    /// <summary>A frame stands for a moment, or carries a caption, this close to it.</summary>
    private static readonly TimeSpan Near = TimeSpan.FromSeconds(90);

    /// <summary>A caption still describes the scene this long after it was written (as in the replay).</summary>
    private static readonly TimeSpan CaptionHold = TimeSpan.FromMinutes(15);

    /// <param name="frameTimes">When each kept time-lapse frame was taken (Unix ms), in any order.</param>
    /// <param name="replay">The same stretch's observed minutes and events (the Journal's replay).</param>
    /// <param name="title">The opening card: the day or the session.</param>
    /// <param name="timeZone">Clock times on the closing card are in this zone.</param>
    public static ReelPlan Plan(IEnumerable<long> frameTimes, ReplayDayDto replay, string title, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(frameTimes);
        ArgumentNullException.ThrowIfNull(replay);
        ArgumentNullException.ThrowIfNull(timeZone);

        var minutes = replay.Minutes.ToDictionary(m => m.AtUtc.ToUnixTimeMilliseconds() / 60_000, m => m);
        var maxMotion = Math.Max(1e-6, replay.Minutes.Select(m => m.Motion).DefaultIfEmpty(0).Max());
        var moments = replay.Events.Where(e => e.Kind == "moment").OrderBy(e => e.AtUtc).ToList();
        var captions = replay.Events.Where(e => e.Kind == "caption" && !string.IsNullOrWhiteSpace(e.Text)).OrderBy(e => e.AtUtc).ToList();
        var lively = replay.Events.Where(e => e.Kind is "enter" or "exit" or "speech" or "sound").Select(e => e.AtUtc.ToUnixTimeMilliseconds()).ToList();

        var frames = frameTimes.Distinct().Order().ToList();

        // Each moment stars the one frame nearest it; when there are too many, an even spread of them.
        var starred = new Dictionary<long, string?>();
        foreach (var moment in Spread(moments, MaxStars))
        {
            var at = moment.AtUtc.ToUnixTimeMilliseconds();
            if (frames.Count == 0) break;
            var nearest = frames.MinBy(f => Math.Abs(f - at));
            if (Math.Abs(nearest - at) <= Near.TotalMilliseconds) starred.TryAdd(nearest, moment.Text);
        }

        double Level(long frame) => minutes.TryGetValue(frame / 60_000, out var m) ? Math.Clamp(m.Motion / maxMotion, 0, 1) : 0;
        double Score(long frame)
        {
            var score = Level(frame) + (minutes.TryGetValue(frame / 60_000, out var m) ? Math.Min(1, m.Occupancy) : 0);
            if (lively.Any(at => Math.Abs(at - frame) <= Near.TotalMilliseconds)) score += 0.5;
            return starred.ContainsKey(frame) ? score + 10 : score;
        }

        // Room for the frames once the long holds are paid for; a short recording keeps every frame.
        var room = Math.Max(1, Budget - (starred.Count * StarHold));
        var kept = frames.Count <= room
            ? frames
            : frames.OrderByDescending(Score).ThenBy(f => f).Take(room + starred.Count).Order().ToList();
        var plain = Math.Max(1, kept.Count - starred.Count);
        var hold = Math.Clamp(room / plain, 1, MaxHold);

        return new ReelPlan
        {
            Title = title,
            Facts = Facts(replay, timeZone),
            Shots = kept.Select(frame => starred.TryGetValue(frame, out var moment)
                ? new ReelShot(frame, StarHold, moment, true, Level(frame))
                : new ReelShot(frame, hold, CaptionAt(captions, frame), false, Level(frame))).ToList(),
        };
    }

    /// <summary>The latest caption written before a frame, while it still describes the scene.</summary>
    private static string? CaptionAt(List<ReplayEventDto> captions, long frame)
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds(frame) + Near;
        var last = captions.LastOrDefault(c => c.AtUtc <= at);
        return last is not null && at - last.AtUtc <= CaptionHold + Near ? last.Text : null;
    }

    /// <summary>At most <paramref name="count"/> of <paramref name="items"/>, evenly spaced through them.</summary>
    private static IEnumerable<T> Spread<T>(List<T> items, int count) =>
        items.Count <= count ? items : Enumerable.Range(0, count).Select(i => items[i * items.Count / count]);

    private static List<string> Facts(ReplayDayDto replay, TimeZoneInfo zone)
    {
        var facts = new List<string>();
        if (replay.Minutes.Count == 0) return facts;

        facts.Add($"{Count(replay.Minutes.Count, "minute")} watched");
        var visits = replay.Events.Count(e => e.Kind == "enter");
        if (visits > 0) facts.Add($"{Count(visits, "visit")} · {replay.Minutes.Max(m => m.Peak)} at once at most");
        var busiest = replay.Minutes.MaxBy(m => m.Motion)!;
        if (busiest.Motion > 0)
            facts.Add($"Liveliest at {TimeZoneInfo.ConvertTime(busiest.AtUtc, zone).ToString("HH:mm", CultureInfo.InvariantCulture)}");
        var said = replay.Events.Count(e => e.Kind == "speech");
        if (said > 0) facts.Add($"{Count(said, "line")} said");
        var notable = replay.Events.Count(e => e.Kind == "moment");
        if (notable > 0) facts.Add($"{Count(notable, "notable moment")}");
        return facts;
    }

    private static string Count(int n, string noun) => string.Create(CultureInfo.InvariantCulture, $"{n} {noun}{(n == 1 ? null : "s")}");
}
