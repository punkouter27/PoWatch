using PoWatch.Shared.Models;

namespace PoWatch.Client.Services;

/// <summary>
/// One line of what happened, however it was sensed: a caption (saw), words (said), a sound (heard), a
/// coming or going, or a notable moment. Live's narration, a session's or a day's timeline and the day's
/// moments all show these through <c>MomentLine</c>, so a thing reads the same wherever it appears.
/// </summary>
/// <param name="Kind">caption, speech, sound, enter, exit or moment.</param>
/// <param name="Text">The caption, the words, the sound's name, or the moment; for enter/exit the class.</param>
/// <param name="Who">The regular who spoke, came in or left, by name; null for "someone".</param>
public sealed record TimelineEntry(DateTimeOffset AtUtc, string Kind, string Text, string? Who = null, string? ImageUrl = null)
{
    /// <summary>The short tag in front of the line.</summary>
    public string Tag => Kind switch
    {
        "caption" => "SAW",
        "speech" => "SAID",
        "sound" => "HEARD",
        "enter" => "IN",
        "exit" => "OUT",
        _ => "★",
    };

    /// <summary>The line as a sentence: "Sam came in", "a dog left", "Sam: “hello”", "Dog".</summary>
    public string Sentence => Kind switch
    {
        "speech" => $"{(Who is { } who ? $"{who}: " : null)}“{Text}”",
        "enter" => $"{Subject} came in",
        "exit" => $"{Subject} left",
        _ => Text,
    };

    private string Subject => Who ?? (Text == "person" ? "Someone" : $"A {Text}");

    public static TimelineEntry? From(ReplayEventDto e) => e.Kind switch
    {
        "enter" or "exit" when e.Class is { } cls => new(e.AtUtc, e.Kind, cls, e.Who),
        "caption" or "speech" or "sound" when !string.IsNullOrWhiteSpace(e.Text) => new(e.AtUtc, e.Kind, e.Text.Trim(), e.Who),
        "moment" => new(e.AtUtc, e.Kind, string.IsNullOrWhiteSpace(e.Text) ? "Something notable happened" : e.Text.Trim()),
        _ => null,
    };

    public static TimelineEntry From(MomentDto moment) => new(moment.AtUtc, "moment", moment.Text, ImageUrl: moment.ImageUrl);

    /// <summary>The kinds a timeline can be filtered to, with the label of each filter.</summary>
    public static readonly (string Key, string Label, string[] Kinds)[] Filters =
    [
        ("all", "All", []),
        ("saw", "Saw", ["caption", "moment"]),
        ("said", "Said", ["speech"]),
        ("heard", "Sounds", ["sound"]),
        ("came", "Comings and goings", ["enter", "exit"]),
    ];

    public static bool Passes(TimelineEntry entry, string filter) =>
        Filters.FirstOrDefault(f => f.Key == filter) is not { Kinds.Length: > 0 } f || f.Kinds.Contains(entry.Kind);
}
