using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PoWatch.Client.Layout;
using PoWatch.Client.Services;
using PoWatch.Shared.Models;

namespace PoWatch.Client.Features.Settings;

/// <summary>One tab of <see cref="SettingsPanel"/>: redraws when the browser's choices or the session change.</summary>
public abstract class SettingsTab : ComponentBase, IDisposable
{
    [Inject] protected UiPrefs Prefs { get; set; } = default!;
    [Inject] protected SensingSession Sensing { get; set; } = default!;
    [Inject] protected PoWatchApiClient Api { get; set; } = default!;
    [Inject] protected IJSRuntime JS { get; set; } = default!;

    protected override void OnInitialized()
    {
        Prefs.Changed += OnChanged;
        Sensing.Changed += OnChanged;
    }

    private void OnChanged() => InvokeAsync(StateHasChanged);

    public void Dispose()
    {
        Prefs.Changed -= OnChanged;
        Sensing.Changed -= OnChanged;
        GC.SuppressFinalize(this);
    }

    /// <summary>Turns listening on or off for this browser.</summary>
    protected async Task SetListeningAsync(bool on)
    {
        Sensing.Listening = on;
        await JS.TryInvokeVoidAsync("localStorage.setItem", MainLayout.ListeningKey, on ? "on" : "off");
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
