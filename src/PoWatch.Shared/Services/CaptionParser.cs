using System.Text.Json;

namespace PoWatch.Shared.Services;

/// <summary>A vision-model caption reduced to the parts the stats use.</summary>
public sealed record ParsedCaption(string Text, IReadOnlyList<string> Activities, string? Weather, bool Notable);

/// <summary>
/// Reads whatever the vision model said. Small models reliably produce one plain sentence but not
/// strict JSON, so the prompt asks for a sentence and this parser takes JSON only when a model
/// volunteers it; otherwise activities and weather come from a keyword taxonomy over the text.
/// </summary>
public static class CaptionParser
{
    public const int MaxLength = 200;

    /// <summary>The instruction line of every caption prompt.</summary>
    public const string Instruction = "Describe what is happening in this scene in one short sentence.";

    /// <summary>Decode budget: one short sentence fits, and the parser keeps only the first anyway.</summary>
    public const int MaxNewTokens = 32;

    /// <summary>
    /// The prompt the sensing loop sends with each frame. What the detector already sees goes on a
    /// second line: small models make up far less when told what is there. The worker's prompt-echo
    /// check reads only the first line, so the caption may reuse these words.
    /// </summary>
    public static string Prompt(IEnumerable<string> visible)
    {
        var counts = visible.GroupBy(l => l, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Count() == 1 ? g.Key : $"{g.Key} x{g.Count()}")
            .ToList();
        return counts.Count == 0 ? Instruction : $"{Instruction}\nVisible: {string.Join(", ", counts)}.";
    }

    private static readonly (string Activity, string[] Keywords)[] ActivityKeywords =
    [
        ("cleaning", ["clean", "vacuum", "sweep", "mop", "wipe"]),
        ("cooking", ["cook", "stove", "frying", "baking", "chopping"]),
        ("drinking", ["drink", "coffee", "tea ", "sipping", "mug"]),
        ("eating", ["eat", "meal", "snack", "dinner", "lunch", "breakfast"]),
        ("exercising", ["exercis", "workout", "yoga", "stretching", "push-up", "running on"]),
        ("playing", ["play", "game", "toy", "wrestl", "chasing"]),
        ("reading", ["read", "book", "newspaper", "magazine"]),
        ("sleeping", ["sleep", "asleep", "nap", "dozing"]),
        ("talking", ["talk", "chat", "conversation", "phone call", "speaking"]),
        ("typing", ["typing", "keyboard", "laptop", "computer"]),
        ("walking", ["walk", "stroll", "pacing", "passing by"]),
        ("watching", ["watch", "television", " tv", "screen"]),
    ];

    private static readonly (string Weather, string[] Keywords)[] WeatherKeywords =
    [
        ("snowy", ["snow"]),
        ("rainy", ["rain", "drizzle", "wet street"]),
        ("foggy", ["fog", "mist", "haze"]),
        ("sunny", ["sunny", "sunlight", "sun is shining", "bright sun"]),
        ("cloudy", ["cloud", "overcast", "grey sky", "gray sky"]),
        ("dark", ["night", "dark outside", "darkness"]),
    ];

    public static ParsedCaption? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var cleaned = raw.Replace("<S>", " ", StringComparison.Ordinal).Replace("<E>", " ", StringComparison.Ordinal).Trim();
        cleaned = StripFence(cleaned);

        if (cleaned.StartsWith('{') && TryParseJson(cleaned) is { } fromJson) return fromJson;

        var text = FirstSentence(cleaned);
        if (!text.Any(char.IsLetter)) return null;

        var lower = $" {cleaned.ToLowerInvariant()} ";
        return new ParsedCaption(text, ActivitiesIn(lower), WeatherIn(lower), Notable: false);
    }

    private static ParsedCaption? TryParseJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var text = (Str(root, "caption") ?? Str(root, "scene") ?? Str(root, "description"))?.Trim();
            if (string.IsNullOrWhiteSpace(text) || !text.Any(char.IsLetter)) return null;

            var lower = $" {text.ToLowerInvariant()} ";
            var activities = root.TryGetProperty("activities", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!.Trim().ToLowerInvariant())
                    .Where(a => a.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()
                : ActivitiesIn(lower);
            var notable = root.TryGetProperty("notable", out var flag) && flag.ValueKind == JsonValueKind.True;

            return new ParsedCaption(Truncate(text), activities, Str(root, "weather") ?? WeatherIn(lower), notable);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string StripFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;
        var firstNewline = text.IndexOf('\n', StringComparison.Ordinal);
        var body = firstNewline < 0 ? text[3..] : text[(firstNewline + 1)..];
        var closing = body.LastIndexOf("```", StringComparison.Ordinal);
        return (closing < 0 ? body : body[..closing]).Trim();
    }

    private static string FirstSentence(string text)
    {
        var end = text.IndexOfAny(['.', '!', '?']);
        var sentence = end >= 0 ? text[..(end + 1)] : text;
        return Truncate(sentence.Trim());
    }

    private static string Truncate(string text) => text.Length <= MaxLength ? text : text[..MaxLength].TrimEnd();

    private static List<string> ActivitiesIn(string lower) =>
        ActivityKeywords.Where(a => a.Keywords.Any(k => lower.Contains(k, StringComparison.Ordinal))).Select(a => a.Activity).ToList();

    private static string? WeatherIn(string lower) =>
        WeatherKeywords.FirstOrDefault(w => w.Keywords.Any(k => lower.Contains(k, StringComparison.Ordinal))).Weather;
}
