using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PoWatch.Client.Services;
using PoWatch.Shared.Models;

namespace PoWatch.Client.Features.Live;

/// <summary>The Live page's state and behaviour; the markup is in Live.razor.</summary>
public sealed partial class Live
{
    private const string RangeKey = "powatch:range";
    private static readonly string[] Ranges = ["session", "today", "7d", "30d", "all"];
    private static readonly string[] Edges = ["Left", "Top", "Right", "Bottom"];

    /// <summary>The phone strip's keys, in the order the panels appear.</summary>
    private static readonly string[] Pages = ["Grid", "Energy", "Space", "Curve", "Week", "Room"];

    private const string DayFormat = "yyyy-MM-dd";
    private string _range = "today";
    private DateOnly? _date;
    private Guid _sessionId;
    private StatsQuery _query = StatsQuery.For("today");
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

    [SupplyParameterFromQuery(Name = "range")] public string? RangeParameter { get; set; }

    /// <summary>The day for ?range=day, yyyy-MM-dd.</summary>
    [SupplyParameterFromQuery(Name = "date")] public string? DateParameter { get; set; }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>A session to open straight away (History links each of its sessions here).</summary>
    [SupplyParameterFromQuery(Name = "session")] public Guid? SessionParameter { get; set; }

    private string WindowLabel => _range switch
    {
        "session" => $"Session {SessionDto.Tag(_sessionId)}, minute by minute",
        "day" => $"{_date?.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture)}, minute by minute",
        "7d" => "Last 7 days, hour by hour",
        "30d" => "Last 30 days, hour by hour",
        "all" => "Everything, day by day",
        _ => "Today so far, minute by minute"
    } + $" · {TimeZoneInfo.Local.Id}";

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

    /// <summary>The URL is the state: the range control navigates, and this reads the result back.</summary>
    protected override void OnParametersSet()
    {
        DateOnly? date = RangeParameter == "day"
            && DateOnly.TryParseExact(DateParameter, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            && d <= Today ? d : null;
        var range = SessionParameter is not null ? "session" : date is not null ? "day" : RangeParameter is { } r && Ranges.Contains(r) ? r : null;
        var session = SessionParameter ?? _sessionId;
        if (range is null || (range == _range && session == _sessionId && date == _date)) return;

        (_range, _sessionId, _date) = (range, session, date);
        Apply();
        // Key metrics and the census follow the range too (the stat panels reload themselves).
        _ = RefreshStatsAsync();
    }

    private void OnRangeChanged(string range)
    {
        if (range != "session") _ = JS.TryInvokeVoidAsync("localStorage.setItem", RangeKey, range);
        Navigation.NavigateTo($"?range={range}" + (range == "session" && _sessionId != Guid.Empty ? $"&session={_sessionId}" : string.Empty));
    }

    private void PickDay(DateOnly day) =>
        Navigation.NavigateTo($"?range=day&date={day.ToString(DayFormat, CultureInfo.InvariantCulture)}");

    private void Apply() =>
        _query = _range == "day" && _date is { } day
            ? StatsQuery.ForDay(day)
            : StatsQuery.For(_range, _range == "session" && _sessionId != Guid.Empty ? _sessionId : null);

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
            // SESSION means the one running, else the latest.
            if (SessionParameter is null && Sensing.Live.Session is { } running)
            {
                _sessionId = running.Id;
                if (_range == "session") Apply();
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
        if (_sessionId == Guid.Empty) _sessionId = Sensing.Live.Session?.Id ?? (latest.Count > 0 ? latest[0].Id : Guid.Empty);

        // A bare address opens on the range you last looked at.
        if (RangeParameter is null && SessionParameter is null
            && await JS.TryInvokeAsync<string>("localStorage.getItem", RangeKey) is { } remembered
            && Ranges.Contains(remembered) && remembered != "session" && remembered != _range)
        {
            _range = remembered;
            Apply();
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
