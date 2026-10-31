using System.Globalization;
using PoWatch.Domain.Models;

namespace PoWatch.Application.Services;

/// <summary>
/// Tells what happened in front of the camera from the scene events: who came in and from where, what
/// the caption model saw them doing, who left, when the lights changed. Times are the only numbers, so
/// a recap reads as a story rather than a table; the numbers themselves stay in the recap's grid.
/// </summary>
public static class SceneNarrator
{
    /// <summary>Events further apart than this start a new beat of the story.</summary>
    private static readonly TimeSpan BeatGap = TimeSpan.FromMinutes(3);

    /// <summary>A long day keeps its busiest beats, still told in order.</summary>
    private const int MaxBeats = 6;

    // Things that come and go on their own; anything else (a cup, a chair) is simply in view.
    private static readonly HashSet<string> Movers = new(StringComparer.OrdinalIgnoreCase)
    {
        "person", "dog", "cat", "bird", "horse", "sheep", "cow", "elephant", "bear", "zebra", "giraffe"
    };

    /// <summary>The story of <paramref name="events"/>, or null when nothing happened worth telling.</summary>
    /// <param name="names">Regular id → the name it goes by, for tracks that were recognised.</param>
    public static string? Tell(IEnumerable<SceneEvent> events, TimeZoneInfo zone, IReadOnlyDictionary<string, string> names)
    {
        ArgumentNullException.ThrowIfNull(events);
        var ordered = events.Where(Tellable).OrderBy(e => e.AtUtc).ToList();
        if (ordered.Count == 0) return null;
        // A track is only recognised as a regular by the time it leaves; its exit names its entrance too.
        var regularOf = ordered.Where(e => e is { Kind: SceneEventKind.TrackExit, RegularId: not null, TrackId: not null })
            .GroupBy(e => e.TrackId!).ToDictionary(g => g.Key, g => g.Last().RegularId!);
        ordered = [.. ordered.Select(e => e.RegularId is null && e.TrackId is { } track && regularOf.TryGetValue(track, out var regular) ? e with { RegularId = regular } : e)];

        var beats = new List<List<SceneEvent>> { new() { ordered[0] } };
        foreach (var e in ordered.Skip(1))
        {
            if (e.AtUtc - beats[^1][^1].AtUtc > BeatGap) beats.Add([]);
            beats[^1].Add(e);
        }
        var told = beats.Count <= MaxBeats
            ? beats
            : [.. beats.OrderByDescending(b => b.Count).Take(MaxBeats).OrderBy(b => b[0].AtUtc)];

        var seenCaptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sentences = new List<string>();
        for (var i = 0; i < told.Count; i++)
        {
            var beat = told[i];
            var when = $"{(i == 0 ? "At" : Opener(i))} {Clock(beat[0].AtUtc, zone)}";
            var arrived = Who(beat.Where(e => e.Kind == SceneEventKind.TrackEnter && IsMover(e)), names);
            var objects = Things(beat.Where(e => e.Kind == SceneEventKind.TrackEnter && !IsMover(e)));
            var left = Who(beat.Where(e => e.Kind == SceneEventKind.TrackExit && IsMover(e)), names);
            var caption = beat.Where(e => e.Kind == SceneEventKind.Caption && seenCaptions.Add(e.Text!.Trim())).Select(e => e.Text!.Trim()).FirstOrDefault();
            var lights = beat.LastOrDefault(e => e.Kind is SceneEventKind.LightsOn or SceneEventKind.LightsOff);
            var line = beat.FirstOrDefault(e => e.Kind == SceneEventKind.Speech);
            var said = line?.Text!.Trim();
            // Who said it, when their voice was known; otherwise "someone".
            var speaker = line?.RegularId is { } speakerId && names.TryGetValue(speakerId, out var speakerName) ? speakerName : "someone";
            var heard = Sounds(beat);

            if (arrived.Count > 0)
            {
                var edge = beat.First(e => e.Kind == SceneEventKind.TrackEnter && IsMover(e)).Edge;
                sentences.Add($"{when}, {Join(arrived)} came in{From(edge)}.");
            }
            else if (caption is not null)
            {
                sentences.Add($"{when}, the camera saw this: “{Sentence(caption)}”");
                caption = null;
            }
            else if (said is not null)
            {
                sentences.Add($"{when}, {speaker} said: “{Sentence(said)}”");
                said = null;
            }
            else if (heard.Count > 0)
            {
                sentences.Add($"{when}, the microphone picked up {Join(heard)}.");
                heard = [];
            }
            else if (lights is not null)
            {
                sentences.Add($"{when}, the lights went {(lights.Kind == SceneEventKind.LightsOn ? "on" : "off")}.");
                lights = null;
            }
            else if (objects.Count > 0)
            {
                sentences.Add($"{when}, {Join(objects)} {(objects.Count == 1 ? "was" : "were")} in view.");
                objects = [];
            }
            else if (left.Count > 0)
            {
                sentences.Add($"{when}, {Join(left)} left.");
                left = [];
            }
            else continue;

            if (caption is not null) sentences.Add($"The camera saw this: “{Sentence(caption)}”");
            if (said is not null) sentences.Add($"{Capital(speaker)} said: “{Sentence(said)}”");
            if (heard.Count > 0) sentences.Add($"The microphone picked up {Join(heard)}.");
            if (objects.Count > 0) sentences.Add($"{Capital(Join(objects))} {(objects.Count == 1 ? "was" : "were")} in view.");
            if (left.Count > 0) sentences.Add($"{Capital(Join(left))} left again{To(beat.Last(e => e.Kind == SceneEventKind.TrackExit && IsMover(e)).Edge)}.");
            if (lights is not null) sentences.Add($"The lights went {(lights.Kind == SceneEventKind.LightsOn ? "on" : "off")}.");
        }

        if (sentences.Count == 0) return null;
        // Ending on everyone gone says so; a story that ends on the lights or a caption ends there.
        if (ordered[^1] is { Kind: SceneEventKind.TrackExit } last && IsMover(last) && Inside(ordered) == 0)
            sentences.Add($"By {Clock(last.AtUtc, zone)} the room was quiet again.");
        return string.Join(' ', sentences);
    }

    /// <summary>
    /// The feel of the window from its telemetry, in words: the light and its colour, the liveliest
    /// moment and the longest still stretch, where in the frame things moved, how many were in view at
    /// once and who stayed longest. Null when there are no ticks.
    /// </summary>
    public static string? Atmosphere(IReadOnlyList<Tick> ticks, IEnumerable<SceneEvent> events, TimeZoneInfo zone, IReadOnlyDictionary<string, string> names)
    {
        ArgumentNullException.ThrowIfNull(ticks);
        ArgumentNullException.ThrowIfNull(events);
        if (ticks.Count == 0) return null;
        var ordered = ticks.OrderBy(t => t.StartUtc).ToList();
        var sentences = new List<string>();

        // Light: how it started and ended, and its colour.
        var opening = Light(ordered.Take(6).Average(t => t.LuminanceMean));
        var closing = Light(ordered.TakeLast(6).Average(t => t.LuminanceMean));
        var colour = Colour(ordered.SelectMany(t => t.Palette.Take(1)).ToList());
        sentences.Add(opening == closing
            ? $"The room stayed {opening}{colour} throughout."
            : $"The room went from {opening} to {closing}{colour}.");

        // Motion: the liveliest moment, and the longest stretch where nothing moved.
        var liveliest = ordered.MaxBy(t => t.MotionMax)!;
        if (liveliest.MotionMax >= 0.05) sentences.Add($"The liveliest moment came at {Clock(liveliest.StartUtc, zone)}.");
        if (LongestStill(ordered) is { } still && still.To - still.From >= TimeSpan.FromMinutes(5))
            sentences.Add($"Between {Clock(still.From, zone)} and {Clock(still.To, zone)} nothing moved at all.");

        // Where: the busiest third of the frame, across and from front to back.
        if (Where(ordered) is { } where) sentences.Add($"Most of the movement was {where}.");

        // Sound: how loud the room was, and its loudest moment (only when the microphone was listening).
        var listened = ordered.Where(t => t.SoundMax > 0).ToList();
        if (listened.Count > 0)
        {
            sentences.Add(listened.Average(t => t.SoundMean) switch
            {
                < 0.15 => "It was quiet.",
                < 0.35 => "There was a soft background murmur.",
                < 0.55 => "It was fairly noisy.",
                _ => "It was loud."
            });
            var loudest = listened.MaxBy(t => t.SoundMax)!;
            if (loudest.SoundMax >= 0.6) sentences.Add($"The loudest moment was at {Clock(loudest.StartUtc, zone)}.");
        }

        // Company: the most in view at once, and the longest stay.
        var together = ordered.Max(t => t.Classes.Where(c => Movers.Contains(c.Key)).Sum(c => c.Value.Max));
        if (together >= 2) sentences.Add($"At the busiest, {Count(together)} were in view together.");
        var stay = events.Where(e => e.Kind == SceneEventKind.TrackExit && IsMover(e) && e.DwellSeconds is > 60).MaxBy(e => e.DwellSeconds);
        if (stay is not null)
        {
            var who = stay.RegularId is { } id && names.TryGetValue(id, out var name) ? name : Article(stay.Class!);
            sentences.Add($"{Capital(who)} stayed longest, {Stay(stay.DwellSeconds!.Value)}.");
        }

        return string.Join(' ', sentences);
    }

    private static string Light(double luminance) => luminance switch
    {
        < 0.15 => "dark",
        < 0.35 => "dim",
        < 0.65 => "softly lit",
        _ => "bright"
    };

    /// <summary>", in warm orange light": the hue the dominant colours share most, or nothing when they are grey.</summary>
    private static string Colour(IReadOnlyList<int> dominant)
    {
        var hues = dominant.Select(Hue).Where(h => h is not null).GroupBy(h => h).OrderByDescending(g => g.Count()).FirstOrDefault();
        return hues is null || hues.Count() < dominant.Count / 3 ? string.Empty : $", in {hues.Key} tones";
    }

    private static string? Hue(int rgb)
    {
        double r = (rgb >> 16 & 0xFF) / 255.0, g = (rgb >> 8 & 0xFF) / 255.0, b = (rgb & 0xFF) / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        if (max - min < 0.12 || max < 0.12) return null;
        var hue = max == r ? 60 * (((g - b) / (max - min)) % 6) : max == g ? 60 * (((b - r) / (max - min)) + 2) : 60 * (((r - g) / (max - min)) + 4);
        if (hue < 0) hue += 360;
        return hue switch
        {
            < 20 or >= 330 => "warm red",
            < 45 => "warm orange",
            < 70 => "yellow",
            < 160 => "green",
            < 200 => "teal",
            < 260 => "blue",
            _ => "purple"
        };
    }

    private static (DateTimeOffset From, DateTimeOffset To)? LongestStill(IReadOnlyList<Tick> ordered)
    {
        (DateTimeOffset From, DateTimeOffset To)? best = null;
        DateTimeOffset? start = null;
        foreach (var t in ordered.Append(null))
        {
            if (t is not null && t.MotionMean < 0.005)
            {
                start ??= t.StartUtc;
                continue;
            }
            if (start is { } from)
            {
                var to = t?.StartUtc ?? ordered[^1].StartUtc.AddSeconds(ordered[^1].DurationSeconds);
                if (best is null || to - from > best.Value.To - best.Value.From) best = (from, to);
            }
            start = null;
        }
        return best;
    }

    private static string? Where(IReadOnlyList<Tick> ticks)
    {
        var sum = new double[SpatialGrid.Cells];
        foreach (var t in ticks.Where(t => t.MotionGrid.Count == SpatialGrid.Cells))
            for (var i = 0; i < SpatialGrid.Cells; i++) sum[i] += t.MotionGrid[i];
        var total = sum.Sum();
        if (total <= 0) return null;

        var thirds = new double[3];
        var halves = new double[2];
        for (var i = 0; i < SpatialGrid.Cells; i++)
        {
            thirds[Math.Min(2, i % SpatialGrid.Columns * 3 / SpatialGrid.Columns)] += sum[i];
            halves[i / SpatialGrid.Columns < SpatialGrid.Rows / 2 ? 0 : 1] += sum[i];
        }
        var across = Array.IndexOf(thirds, thirds.Max());
        // Only worth saying when one part clearly leads; an even spread says nothing.
        var side = thirds[across] / total >= 0.45 ? across switch { 0 => "on the left", 2 => "on the right", _ => "in the middle" } : null;
        var depth = halves.Max() / total >= 0.65 ? (halves[0] > halves[1] ? "toward the back" : "near the camera") : null;
        return (side, depth) switch
        {
            (null, null) => null,
            ({ } s, null) => $"{s} of the frame",
            (null, { } d) => d,
            ({ } s, { } d) => $"{s} of the frame, {d}"
        };
    }

    private static string Count(int n) => n switch
    {
        2 => "two",
        3 => "three",
        4 => "four",
        5 => "five",
        6 => "six",
        7 => "seven",
        8 => "eight",
        9 => "nine",
        10 => "ten",
        _ => "more than ten"
    };

    private static string Stay(double seconds) => seconds switch
    {
        < 150 => "a couple of minutes",
        < 3600 => $"about {Math.Round(seconds / 60)} minutes",
        < 7200 => "over an hour",
        _ => $"about {Math.Round(seconds / 3600)} hours"
    };

    private static bool Tellable(SceneEvent e) => e.Kind switch
    {
        SceneEventKind.TrackEnter or SceneEventKind.TrackExit => !string.IsNullOrWhiteSpace(e.Class),
        SceneEventKind.Caption or SceneEventKind.Speech or SceneEventKind.Sound => !string.IsNullOrWhiteSpace(e.Text),
        SceneEventKind.LightsOn or SceneEventKind.LightsOff => true,
        _ => false
    };

    private static bool IsMover(SceneEvent e) => Movers.Contains(e.Class ?? string.Empty);

    /// <summary>The beat's recognised sounds, each once, as a phrase: "a dog barking", "music".</summary>
    private static List<string> Sounds(IEnumerable<SceneEvent> beat) =>
        [.. beat.Where(e => e.Kind == SceneEventKind.Sound && !Untold(e.Text!)).Select(e => SoundPhrase(e.Text!)).Distinct()];

    // Generic tones say nothing, and body sounds read as medical, which a story never is (audio-layer.js
    // drops these too; this keeps them out of stories told from older events).
    private static bool Untold(string label) =>
        label.Contains("heart", StringComparison.OrdinalIgnoreCase) || label.Contains("breath", StringComparison.OrdinalIgnoreCase)
        || label.Equals("Sound effect", StringComparison.OrdinalIgnoreCase) || label.Equals("Sine wave", StringComparison.OrdinalIgnoreCase);

    // AudioSet's labels as a person would say them; anything else is its own label in lower case.
    private static readonly Dictionary<string, string> SoundPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dog"] = "a dog barking",
        ["Bark"] = "a dog barking",
        ["Bow-wow"] = "a dog barking",
        ["Yip"] = "a dog barking",
        ["Growling"] = "growling",
        ["Cat"] = "a cat",
        ["Meow"] = "a cat meowing",
        ["Purr"] = "purring",
        ["Music"] = "music",
        ["Singing"] = "singing",
        ["Whistling"] = "whistling",
        ["Laughter"] = "laughter",
        ["Giggle"] = "giggling",
        ["Crying, sobbing"] = "crying",
        ["Baby cry, infant cry"] = "a baby crying",
        ["Door"] = "a door",
        ["Knock"] = "knocking",
        ["Doorbell"] = "the doorbell",
        ["Slam"] = "a door slamming",
        ["Footsteps"] = "footsteps",
        ["Walk, footsteps"] = "footsteps",
        ["Clapping"] = "clapping",
        ["Applause"] = "applause",
        ["Telephone"] = "a phone",
        ["Ringtone"] = "a phone ringing",
        ["Telephone bell ringing"] = "a phone ringing",
        ["Alarm"] = "an alarm",
        ["Smoke detector, smoke alarm"] = "a smoke alarm",
        ["Siren"] = "a siren",
        ["Television"] = "the TV",
        ["Radio"] = "the radio",
        ["Typing"] = "typing",
        ["Computer keyboard"] = "typing",
        ["Vacuum cleaner"] = "a vacuum cleaner",
        ["Water"] = "running water",
        ["Water tap, faucet"] = "a running tap",
        ["Dishes, pots, and pans"] = "dishes clattering",
        ["Cutlery, silverware"] = "cutlery",
        ["Microwave oven"] = "the microwave",
        ["Vehicle"] = "traffic",
        ["Car"] = "a car",
        ["Bird"] = "birds",
        ["Rain"] = "rain",
        ["Thunder"] = "thunder",
    };

    private static string SoundPhrase(string label) => SoundPhrases.TryGetValue(label.Trim(), out var phrase) ? phrase : label.Trim().ToLowerInvariant();

    /// <summary>Who in the beat, each once, in order: a regular by name, anyone else as "a dog".</summary>
    private static List<string> Who(IEnumerable<SceneEvent> tracks, IReadOnlyDictionary<string, string> names) =>
        [.. tracks
            .GroupBy(e => e.RegularId is { } id && names.TryGetValue(id, out var known) ? known : e.Class!.ToLowerInvariant())
            .Select(g => names.Values.Contains(g.Key)
                ? g.Key
                // The same regular coming back is one of them; only different ones (or unknown tracks) add up.
                : g.Select(e => e.RegularId ?? e.TrackId).Distinct().Count() switch
                {
                    1 => Article(g.Key),
                    2 => $"two {Plural(g.Key)}",
                    _ => $"several {Plural(g.Key)}"
                })];

    private static List<string> Things(IEnumerable<SceneEvent> tracks) =>
        [.. tracks.Select(e => Article(e.Class!)).Distinct()];

    /// <summary>Tracks in frame after the last event: entrances minus exits.</summary>
    private static int Inside(IEnumerable<SceneEvent> events) =>
        events.Where(IsMover).GroupBy(e => e.TrackId).Count(g => g.Last().Kind == SceneEventKind.TrackEnter);

    private static string Opener(int beat) => (beat % 3) switch { 1 => "Later, at", 2 => "Around", _ => "Then at" };

    private static string From(FrameEdge edge) => edge switch
    {
        FrameEdge.Left => " from the left",
        FrameEdge.Right => " from the right",
        FrameEdge.Top => " from the far side",
        FrameEdge.Bottom => " from the near side",
        _ => string.Empty
    };

    private static string To(FrameEdge edge) => edge switch
    {
        FrameEdge.Left => " to the left",
        FrameEdge.Right => " to the right",
        FrameEdge.Top => " by the far side",
        FrameEdge.Bottom => " by the near side",
        _ => string.Empty
    };

    private static string Article(string noun) => $"{("aeiou".Contains(char.ToLowerInvariant(noun[0]), StringComparison.Ordinal) ? "an" : "a")} {noun}";

    private static string Plural(string noun) => noun.ToLowerInvariant() switch
    {
        "person" => "people",
        "sheep" => "sheep",
        _ when noun.EndsWith('s') => noun,
        _ => $"{noun}s"
    };

    private static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}"
    };

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Sentence(string caption)
    {
        var text = Capital(caption.Trim());
        return text.EndsWith('.') || text.EndsWith('!') || text.EndsWith('?') ? text : text + ".";
    }

    private static string Clock(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("HH:mm", CultureInfo.InvariantCulture);
}
