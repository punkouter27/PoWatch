using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
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

    /// <summary>The account's saved settings, once <see cref="LoadProfileAsync"/> has run; null while loading or when the server did not answer.</summary>
    protected ProfileDto? Profile { get; private set; }

    /// <summary>A profile save is on its way: the controls wait.</summary>
    protected bool Saving { get; private set; }

    /// <summary>What the last save did, in a sentence.</summary>
    protected string? ProfileMessage { get; private set; }

    /// <summary>What to show in place of the controls while <see cref="Profile"/> is null.</summary>
    protected string ProfileMissing => _profileLoaded ? "Could not load your settings — the server did not answer." : "Loading…";

    private bool _profileLoaded;

    protected async Task LoadProfileAsync() => (Profile, _profileLoaded) = (await Api.GetProfileAsync(), true);

    /// <summary>
    /// The profile is replaced as a whole: <paramref name="change"/> builds the next one from the current one.
    /// The tab then shows what the server saved, or keeps the old values and says nothing changed.
    /// </summary>
    protected async Task SaveProfileAsync(Func<ProfileDto, UpdateProfileDto> change, string done)
    {
        if (Profile is null) return;
        Saving = true;
        var update = change(Profile);
        var saved = await Api.UpdateProfileAsync(new UpdateProfileDto
        {
            RetentionDays = update.RetentionDays,
            Place = update.Place,
            DigestEmail = update.DigestEmail,
            TimeZoneId = TimeZoneInfo.Local.Id
        });
        if (saved is not null) Profile = saved;
        ProfileMessage = saved is null ? "Could not save — nothing was changed." : done;
        Saving = false;
    }
}
