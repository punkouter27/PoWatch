using System.Text.Json;
using PoWatch.Shared.Services;

namespace PoWatch.Client.Services;

/// <summary>The regulars' voiceprints: kept in this browser, and taught by each line heard.</summary>
public sealed partial class SensingSession
{
    /// <summary>Voiceprints of the regulars, kept in this browser only (see <see cref="VoiceBook"/>).</summary>
    public VoiceBook Voices { get; private set; } = new();

    public const string VoicesKey = "powatch:voices";

    /// <summary>Reads this browser's voiceprints; called once at start-up.</summary>
    public async Task LoadVoicesAsync()
    {
        var json = await js.TryInvokeAsync<string>("localStorage.getItem", VoicesKey);
        Dictionary<string, VoicePrint>? prints = null;
        try
        {
            prints = string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize(json, Json.DictionaryStringVoicePrint);
        }
        catch (JsonException)
        {
            // A damaged book is started afresh rather than failing every line heard.
        }
        Voices = new VoiceBook(prints is null ? null : new(prints, StringComparer.Ordinal));
        Notify();
    }

    /// <summary>Writes the voiceprints back to this browser (after learning, merging or forgetting).</summary>
    public Task SaveVoicesAsync() =>
        js.TryInvokeVoidAsync("localStorage.setItem", VoicesKey, JsonSerializer.Serialize(new Dictionary<string, VoicePrint>(Voices.Prints), Json.DictionaryStringVoicePrint));

    /// <summary>
    /// Who said it: the regular whose voice it matches; else, when exactly one recognised person is in
    /// frame, them, and their voiceprint learns from it. Null when neither tells.
    /// </summary>
    private string? Speaker(float[]? voice)
    {
        var people = Live.Tracks.Where(t => t.Label == "person").ToList();
        var alone = people.Count == 1 ? Live.RegularFor(people[0].TrackId)?.Id : null;
        var known = voice is null ? null : Voices.Match(voice);
        var speaker = known ?? alone;
        // A voice only learns from a line it was matched on, or from the one person in view; someone off
        // camera talking over them can still teach it wrong, which more lines average out.
        if (voice is not null && speaker is not null)
        {
            Voices.Learn(speaker, voice);
            _ = SaveVoicesAsync();
        }
        return speaker;
    }
}
