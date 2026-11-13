using System.Globalization;

namespace PoWatch.Client.Services;

/// <summary>This browser's sensing choices: one place that knows their localStorage keys, reads them and writes them.</summary>
public sealed partial class SensingSession
{
    public const string CaptionsKey = "powatch:captions";
    public const string ListeningKey = "powatch:listening";
    public const string PostureKey = "powatch:posture";
    public const string SitNudgeKey = "powatch:sit-nudge";

    /// <summary>Applies what this browser remembered; anything never chosen keeps its default.</summary>
    public async Task LoadPreferencesAsync()
    {
        if (await Stored(CaptionsKey) == "off") Captions = false;
        if (await Stored(ListeningKey) == "off") Listening = false;
        if (await Stored(PostureKey) == "off") ReadPosture = false;
        if (int.TryParse(await Stored(SitNudgeKey), CultureInfo.InvariantCulture, out var nudge)) SitNudgeMinutes = nudge;
        CaptionModel = CaptionModels.Normalize(await Stored(CaptionModels.Key));
    }

    /// <summary>Turns captions on or off for this browser.</summary>
    public Task SetCaptionsAsync(bool on)
    {
        Captions = on;
        return Store(CaptionsKey, on ? "on" : "off");
    }

    /// <summary>Turns listening on or off for this browser.</summary>
    public Task SetListeningAsync(bool on)
    {
        Listening = on;
        return Store(ListeningKey, on ? "on" : "off");
    }

    /// <summary>Turns posture reading (and the sitting nudge) on or off for this browser.</summary>
    public Task SetPostureAsync(bool on)
    {
        ReadPosture = on;
        return Store(PostureKey, on ? "on" : "off");
    }

    /// <summary>Sets how long a sit lasts before the nudge (minutes, 0 for never) for this browser.</summary>
    public Task SetSitNudgeAsync(int minutes)
    {
        SitNudgeMinutes = minutes;
        return Store(SitNudgeKey, SitNudgeMinutes.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Chooses the caption model for this browser.</summary>
    public Task SetCaptionModelAsync(string model)
    {
        CaptionModel = model;
        return Store(CaptionModels.Key, CaptionModel);
    }

    private Task<string?> Stored(string key) => js.TryInvokeAsync<string>("localStorage.getItem", key);

    private Task Store(string key, string value) => js.TryInvokeVoidAsync("localStorage.setItem", key, value);
}
