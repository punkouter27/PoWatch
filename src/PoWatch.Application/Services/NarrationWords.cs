using PoWatch.Domain.Models;

namespace PoWatch.Application.Services;

/// <summary>
/// The words <see cref="SceneNarrator"/> tells a story in: sounds as a person would name them, light and
/// colour, counts and stays, edges of the frame, and the small grammar of articles, plurals and lists.
/// </summary>
internal static class NarrationWords
{
    internal static string Light(double luminance) => luminance switch
    {
        < 0.15 => "dark",
        < 0.35 => "dim",
        < 0.65 => "softly lit",
        _ => "bright"
    };

    /// <summary>", in warm orange light": the hue the dominant colours share most, or nothing when they are grey.</summary>
    internal static string Colour(IReadOnlyList<int> dominant)
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

    internal static string Count(int n) => n switch
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

    internal static string Stay(double seconds) => seconds switch
    {
        < 150 => "a couple of minutes",
        < 3600 => $"about {Math.Round(seconds / 60)} minutes",
        < 7200 => "over an hour",
        _ => $"about {Math.Round(seconds / 3600)} hours"
    };

    // Generic tones say nothing, and body sounds read as medical, which a story never is (audio-layer.js
    // drops these too; this keeps them out of stories told from older events).
    internal static bool Untold(string label) =>
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

    internal static string SoundPhrase(string label) => SoundPhrases.TryGetValue(label.Trim(), out var phrase) ? phrase : label.Trim().ToLowerInvariant();

    internal static string Opener(int beat) => (beat % 3) switch { 1 => "Later, at", 2 => "Around", _ => "Then at" };

    internal static string From(FrameEdge edge) => edge switch
    {
        FrameEdge.Left => " from the left",
        FrameEdge.Right => " from the right",
        FrameEdge.Top => " from the far side",
        FrameEdge.Bottom => " from the near side",
        _ => string.Empty
    };

    internal static string To(FrameEdge edge) => edge switch
    {
        FrameEdge.Left => " to the left",
        FrameEdge.Right => " to the right",
        FrameEdge.Top => " by the far side",
        FrameEdge.Bottom => " by the near side",
        _ => string.Empty
    };

    internal static string Article(string noun) => $"{("aeiou".Contains(char.ToLowerInvariant(noun[0]), StringComparison.Ordinal) ? "an" : "a")} {noun}";

    internal static string Plural(string noun) => noun.ToLowerInvariant() switch
    {
        "person" => "people",
        "sheep" => "sheep",
        _ when noun.EndsWith('s') => noun,
        _ => $"{noun}s"
    };

    internal static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}"
    };

    internal static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    internal static string Sentence(string caption)
    {
        var text = Capital(caption.Trim());
        return text.EndsWith('.') || text.EndsWith('!') || text.EndsWith('?') ? text : text + ".";
    }
}
