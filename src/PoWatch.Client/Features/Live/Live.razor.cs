using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PoWatch.Client.Services;
using PoWatch.Shared.Models;

namespace PoWatch.Client.Features.Live;

/// <summary>The Live page's state and behaviour; the markup is in Live.razor.</summary>
public sealed partial class Live
{
    private static readonly string[] Edges = ["Left", "Top", "Right", "Bottom"];

    /// <summary>The phone strip's keys, in the order the panels appear.</summary>
    private static readonly string[] Pages = ["Grid", "Energy", "Space", "Curve", "Week", "Room"];

    private Guid _sessionId;
    private StatsQuery _query = StatsQuery.For("session");
    private string? _question;
    private string? _answer;
    private bool _asking;

    private ElementReference _slot;
    private ElementReference _crt;
    private ElementReference _heat;
    private ElementReference _more;
    private bool _shaderHeat = true;
    private IReadOnlyList<double> _occupancyTrend = [];
    private IReadOnlyList<double> _visitsTrend = [];
    private DotNetObjectReference<Live>? _self;
    private bool _starting;
    private bool _firstRun;
    private bool _wasRunning;
    private string? _error;
    private PresenceStatsDto? _presence;
    private SpaceStatsDto? _space;
    private PatternStatsDto? _patterns;
    private ObjectStatsDto? _objects;
    private CancellationTokenSource? _refresh;
    private DateTimeOffset _lastRender;
    private DateTimeOffset _lastPush;

    [SupplyParameterFromQuery(Name = "start")] public string? StartMode { get; set; }

    /// <summary>A session to open straight away (the Journal links each of its sessions here).</summary>
    /// <remarks>Text, not a Guid: a mistyped link must not take the page down.</remarks>
    [SupplyParameterFromQuery(Name = "session")] public string? SessionParameter { get; set; }

    /// <summary>Set when ?session= named no session of yours; the page shows the latest instead.</summary>
    private bool _sessionMissing;
    private string? _checkedParameter;

    private string WindowLabel => _sessionId == Guid.Empty
        ? "No session yet: press Start"
        : $"{(Sensing.IsRunning && Sensing.Live.Session?.Id == _sessionId ? "Recording" : "Last")} session {SessionDto.Tag(_sessionId)}, minute by minute · {TimeZoneInfo.Local.Id}";

    private LiveSensingState L => Sensing.Live;

    protected override void OnInitialized()
    {
        _wasRunning = Sensing.IsRunning;
        Sensing.Changed += OnSensingChanged;
        Feed.Changed += OnStatsChanged;
        Prefs.Changed += OnPrefsChanged;
        _ = Feed.EnsureConnectedAsync();
        _refresh = new CancellationTokenSource();
        _ = RefreshLoopAsync(_refresh.Token);
    }

    /// <summary>A Journal link (?session=) opens that session; otherwise the page follows the recording one.</summary>
    protected override async Task OnParametersSetAsync()
    {
        if (SessionParameter == _checkedParameter) return;
        _checkedParameter = SessionParameter;
        _sessionMissing = false;
        if (SessionParameter is null) return;

        // Only a session the server knows as yours: anything else says so and keeps the latest.
        if (!Guid.TryParse(SessionParameter, out var id) || await Api.GetSessionAsync(id) is not { } session)
        {
            _sessionMissing = true;
            return;
        }
        if (session.Id == _sessionId) return;
        _sessionId = session.Id;
        Apply();
        // Key metrics and the census follow the session too (the stat panels reload themselves).
        _ = RefreshStatsAsync();
    }

    private void Apply() => _query = StatsQuery.For("session", _sessionId == Guid.Empty ? null : _sessionId);

    private async Task AskAsync()
    {
        if (_asking || string.IsNullOrWhiteSpace(_question)) return;
        _asking = true;
        _answer = "Thinking…";
        _answer = await Api.AskAsync(_question.Trim())
            ?? "No answer: this server has no AI model configured, or it did not reply.";
        _asking = false;
    }

    private Task ExplainAsync()
    {
        _question = "Which of today's numbers are unusual against my usual day, and what stands out about today?";
        return AskAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _self = DotNetObjectReference.Create(this);
            await JS.TryInvokeVoidAsync("powatchFeed.watchReturn", _self, 300);
            await DockFeedAsync();
        }

        if (_shaderHeat && Prefs.Advanced && !await JS.TryInvokeAsync<bool>("powatchHeat.draw", _heat, L.Heat, 16, 9))
        {
            _shaderHeat = false;
            StateHasChanged();
        }

        // ?start=camera|demo (header Start, or a typed URL) works whether or not Live was already open.
        if (StartMode is not { } mode || Sensing.IsRunning || _starting) return;
        Navigation.NavigateTo("", replace: true);
        await StartAsync(demo: mode == "demo");
    }

    private async Task StartAsync(bool demo)
    {
        _starting = true;
        _error = null;
        try
        {
            _error = await Sensing.StartAsync(demo);
        }
        finally
        {
            _starting = false;
        }
    }

    private void OnSensingChanged()
    {
        // Started or stopped, from here or the header: dock or undock the camera and recount.
        if (Sensing.IsRunning != _wasRunning)
        {
            _wasRunning = Sensing.IsRunning;
            // The page is the recording session; once it stops it stays on screen as the latest.
            if (Sensing.Live.Session is { } running && running.Id != _sessionId)
            {
                _sessionId = running.Id;
                Apply();
            }

            InvokeAsync(async () =>
            {
                // On: the tube is black before the picture docks under it. Off: the picture stays until it is dark.
                if (Prefs.Advanced && Sensing.IsRunning) _ = JS.TryInvokeVoidAsync("powatchCrt.power", _crt, true);
                else if (Prefs.Advanced) await JS.TryInvokeVoidAsync("powatchCrt.power", _crt, false);
                await DockFeedAsync();
                await RefreshStatsAsync();
            });
            return;
        }

        // Sensing fires several times a second; four renders a second is plenty for the eye.
        var now = DateTimeOffset.UtcNow;
        if (now - _lastRender < TimeSpan.FromMilliseconds(250)) return;
        _lastRender = now;
        InvokeAsync(StateHasChanged);
    }

    private void OnPrefsChanged() => InvokeAsync(StateHasChanged);

    private void OnStatsChanged(StatsChangedDto change)
    {
        if (DateTimeOffset.UtcNow - _lastPush < TimeSpan.FromSeconds(5)) return;
        _lastPush = DateTimeOffset.UtcNow;
        _query = _query with { Stamp = _query.Stamp + 1 };
        _ = RefreshStatsAsync();
    }

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        var latest = await Api.ListSessionsAsync(1, ct);
        _firstRun = latest.Count == 0;
        if (_sessionId == Guid.Empty)
        {
            _sessionId = Sensing.Live.Session?.Id ?? (latest.Count > 0 ? latest[0].Id : Guid.Empty);
            Apply();
        }

        // Leaving or reloading the page ends a recording (the outbox's page-exit guard); coming back
        // shows its recap, as Stop would have, unless that card was already dismissed here.
        if (!Sensing.IsRunning && Sensing.Recap is null && latest.Count > 0 && latest[0] is { EndedUtc: { } ended } last
            && DateTimeOffset.UtcNow - ended < TimeSpan.FromMinutes(2)
            && await JS.TryInvokeAsync<string>("localStorage.getItem", PoWatch.Client.Components.AwayCard.DismissedKey) != last.Id.ToString())
        {
            Sensing.Recap = new SessionRecap(last, null, null);
        }

        await RefreshStatsAsync();
        // Push (StatsFeed) refreshes as data lands; this slow timer only covers a dropped hub.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await RefreshStatsAsync();
        }
        catch (OperationCanceledException)
        {
            // Page closed.
        }
    }

    /// <summary>Presence and classes follow the range; the edges and "vs usual" are always about today.</summary>
    private async Task RefreshStatsAsync()
    {
        var query = _query;
        if (query.IsEmpty) return;
        var today = StatsQuery.For("today");
        var presence = Api.GetPresenceAsync(query);
        var space = Api.GetSpaceAsync(today);
        var patterns = Api.GetPatternsAsync(today);
        var objects = Api.GetObjectsAsync(query);
        await Task.WhenAll(presence, space, patterns, objects);
        // A newer range owns the panels now.
        if (query.Range != _query.Range || query.SessionId != _query.SessionId || query.Date != _query.Date) return;
        (_presence, _space, _patterns, _objects) = (presence.Result, space.Result, patterns.Result, objects.Result);
        // Derived once per refresh, not once per render.
        _occupancyTrend = _presence?.Series.Select(p => p.Occupancy).TakeLast(120).ToList() ?? [];
        _visitsTrend = _presence?.Series.Select(p => (double)p.Visits).TakeLast(120).ToList() ?? [];
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>The tab was hidden for five minutes or more: show what happened meanwhile.</summary>
    [JSInvokable]
    public async Task OnReturned(int secondsAway)
    {
        if (!Sensing.IsRunning || Sensing.Live.Session is not { } session) return;
        var refreshed = await Api.ListSessionsAsync(5);
        Sensing.Recap = new SessionRecap(refreshed.FirstOrDefault(s => s.Id == session.Id) ?? session, TimeSpan.FromSeconds(secondsAway), await Sensing.ExposureAsync());
    }

    private Task DockFeedAsync() => Sensing.DockFeedAsync(_slot);

    private Task ShowPanelAsync(int index) => JS.TryInvokeVoidAsync("powatchMenus.reveal", _more, index);

    private static string Pct(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        Sensing.Changed -= OnSensingChanged;
        Feed.Changed -= OnStatsChanged;
        Prefs.Changed -= OnPrefsChanged;
        await JS.TryInvokeVoidAsync("powatchFeed.undock");
        await JS.TryInvokeVoidAsync("powatchFeed.unwatchReturn");
        _self?.Dispose();
        if (_refresh is not null)
        {
            await _refresh.CancelAsync();
            _refresh.Dispose();
        }
    }
}
