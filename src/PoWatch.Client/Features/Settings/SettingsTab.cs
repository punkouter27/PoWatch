using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PoWatch.Client.Layout;
using PoWatch.Client.Services;
using PoWatch.Shared.Models;

namespace PoWatch.Client.Features.Settings;

/// <summary>One tab of <see cref="SettingsPanel"/>: redraws when a setting or the recording state changes.</summary>
public abstract class SettingsTab : ComponentBase, IDisposable
{
    [Inject] protected SensingSession Sensing { get; set; } = default!;
    [Inject] protected PoWatchApiClient Api { get; set; } = default!;
    [Inject] protected IJSRuntime JS { get; set; } = default!;

    private object? _shown;

    protected override void OnInitialized()
    {
        _shown = Shown;
        Sensing.Changed += OnChanged;
    }

    /// <summary>What the tabs draw from the session. It changes several times a second while recording; these rarely do.</summary>
    private object Shown => (Sensing.IsRunning, Sensing.Captions, Sensing.Listening, Sensing.ReadPosture, Sensing.SitNudgeMinutes, Sensing.CaptionModel);

    private void OnChanged()
    {
        var now = Shown;
        if (now.Equals(_shown)) return;
        _shown = now;
        InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        Sensing.Changed -= OnChanged;
        GC.SuppressFinalize(this);
    }

    /// <summary>Turns listening on or off for this browser.</summary>
    protected async Task SetListeningAsync(bool on)
    {
        Sensing.Listening = on;
        await JS.TryInvokeVoidAsync("localStorage.setItem", MainLayout.ListeningKey, on ? "on" : "off");
    }

    /// <summary>Turns posture reading (and the sitting nudge) on or off for this browser.</summary>
    protected async Task SetPostureAsync(bool on)
    {
        Sensing.ReadPosture = on;
        await JS.TryInvokeVoidAsync("localStorage.setItem", MainLayout.PostureKey, on ? "on" : "off");
    }

    /// <summary>Sets how long a sit lasts before the nudge (minutes, 0 for never) for this browser.</summary>
    protected async Task SetSitNudgeAsync(int minutes)
    {
        Sensing.SitNudgeMinutes = minutes;
        await JS.TryInvokeVoidAsync("localStorage.setItem", MainLayout.SitNudgeKey, Sensing.SitNudgeMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Turns captions on or off for this browser.</summary>
    protected async Task SetCaptionsAsync(bool on)
    {
        Sensing.Captions = on;
        await JS.TryInvokeVoidAsync("localStorage.setItem", MainLayout.CaptionsKey, on ? "on" : "off");
    }

    /// <summary>
    /// The profile is replaced as a whole: <paramref name="change"/> builds the next one from <paramref name="current"/>.
    /// Returns what the server saved, or null when it saved nothing.
    /// </summary>
    protected Task<ProfileDto?> SaveProfileAsync(ProfileDto current, Func<ProfileDto, UpdateProfileDto> change)
    {
        var update = change(current);
        return Api.UpdateProfileAsync(new UpdateProfileDto
        {
            RetentionDays = update.RetentionDays,
            Place = update.Place,
            DigestEmail = update.DigestEmail,
            TimeZoneId = TimeZoneInfo.Local.Id
        });
    }
}
