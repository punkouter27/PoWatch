using System.Text.Json;

namespace PoWatch.Shared.Services;

/// <summary>A vision-model caption reduced to the parts the stats use.</summary>
public sealed record ParsedCaption(string Text, bool Notable);

/// <summary>
/// Reads whatever the vision model said. Small models reliably produce one plain sentence but not
/// strict JSON, so the prompt asks for a sentence and this parser takes JSON only when a model
/// volunteers it.
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

    public static ParsedCaption? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var cleaned = raw.Replace("<S>", " ", StringComparison.Ordinal).Replace("<E>", " ", StringComparison.Ordinal).Trim();
        cleaned = StripFence(cleaned);

        if (cleaned.StartsWith('{') && TryParseJson(cleaned) is { } fromJson) return fromJson;

        var text = FirstSentence(cleaned);
        if (!text.Any(char.IsLetter)) return null;

        return new ParsedCaption(text, Notable: false);
    }

    private static ParsedCaption? TryParseJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var text = (Str(root, "caption") ?? Str(root, "scene") ?? Str(root, "description"))?.Trim();
            if (string.IsNullOrWhiteSpace(text) || !text.Any(char.IsLetter)) return null;

            var notable = root.TryGetProperty("notable", out var flag) && flag.ValueKind == JsonValueKind.True;

            return new ParsedCaption(Truncate(text), notable);
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
}
