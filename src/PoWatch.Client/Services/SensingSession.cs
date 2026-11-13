using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PoWatch.Shared.Models;
using PoWatch.Shared.Services;

namespace PoWatch.Client.Services;

/// <summary>
/// Runs one observation session in the browser: the pixel layer, the detector and the vision model
/// (or, in demo mode, a scripted <see cref="SyntheticScene"/>) feed the tracker and the tick batcher;
/// every closed batch goes into the durable outbox (outbox.js) and is posted from there, oldest first.
/// The Live page renders <see cref="Live"/> and listens to <see cref="Changed"/>.
/// </summary>
public sealed partial class SensingSession(PoWatchApiClient api, IJSRuntime js, TimeProvider time) : IAsyncDisposable
{
    private static readonly PoWatchJsonContext Json = PoWatchJsonContext.Default;
    private static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DemoStep = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How often the server is asked to write the session's next story chapter (StoryService), offset
    /// by <see cref="ChapterSettle"/> so each ten minutes has closed and its last batches have landed.
    /// </summary>
    private static readonly TimeSpan ChapterEvery = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ChapterSettle = TimeSpan.FromSeconds(30);

    private readonly CentroidTracker _tracker = new();
    private TickBatcher _batcher = new(time);
    private VlmScheduler _vlm = new(time);
    private HighlightRules _highlights = new(time);
    private bool _capturing;
    private readonly HashSet<string> _observing = new(StringComparer.Ordinal);

    /// <summary>A track must be this old before we ask who it is, so the look has settled.</summary>
    private static readonly TimeSpan RecogniseAfter = TimeSpan.FromSeconds(3);

    /// <summary>Only these get a naming prompt; everything else is still tracked as a regular.</summary>
    private static readonly HashSet<string> Nameable = new(StringComparer.OrdinalIgnoreCase) { "person", "cat", "dog" };
    private SyntheticScene? _scene;
    private DotNetObjectReference<SensingSession>? _self;

    /// <summary>The session the detector's boxes go to (<see cref="OnDetectorBoxes"/> is static, as [JSExport] needs).</summary>
    private static SensingSession? s_detecting;
    private ElementReference? _video;
    private CancellationTokenSource? _cts;
    private bool _vlmBusy;

    /// <summary>Labels the detector tracks right now; null until it reports, so captions then go ungrounded.</summary>
    private List<string>? _inFrame;
    private Task _sending = Task.CompletedTask;
    private int _demoStep;

    public LiveSensingState Live { get; private set; } = new();

    /// <summary>The layout's always-mounted camera element (see MainLayout and feed-dock.js).</summary>
    public ElementReference? Feed { get; set; }

    public bool IsRunning => _cts is not null;

    /// <summary>Whether a camera session also runs the vision model for captions.</summary>
    public bool Captions
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            _vlm = new(time);
            Notify();
        }
    } = true;

    /// <summary>Whether a camera session also listens: loudness, words (Whisper) and sounds, all heard on this machine.</summary>
    public bool Listening
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            Notify();
        }
    } = true;

    /// <summary>
    /// Whether a camera session also reads people's posture (sitting, standing, lying) from body
    /// keypoints, on this machine, and nudges after <see cref="SitNudgeMinutes"/> of sitting. Applies at once.
    /// </summary>
    public bool ReadPosture
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            if (IsRunning && _scene is null) _ = js.TryInvokeVoidAsync("powatchDetector.setPose", value);
            Notify();
        }
    } = true;

    /// <summary>The nudge times offered in Settings, in minutes; 0 is never.</summary>
    public static readonly int[] SitNudgeChoices = [0, 30, 45, 60, 90];
    public const int DefaultSitNudgeMinutes = 45;

    /// <summary>
    /// How many minutes someone sits without a break before the "time to stand up" nudge, and between
    /// nudges after that (one of <see cref="SitNudgeChoices"/>; 0 never nudges). Applies at once, mid-sit too.
    /// </summary>
    public int SitNudgeMinutes
    {
        get;
        set
        {
            var minutes = SitNudgeChoices.Contains(value) ? value : DefaultSitNudgeMinutes;
            if (field == minutes) return;
            field = minutes;
            _sitting.NudgeAfter = TimeSpan.FromMinutes(minutes);
            Notify();
        }
    } = DefaultSitNudgeMinutes;

    private SittingStreak _sitting = new(TimeSpan.FromMinutes(DefaultSitNudgeMinutes));

    /// <summary>Which caption model runs (a <see cref="CaptionModels"/> key); read on every caption, so a change applies at once.</summary>
    public string CaptionModel
    {
        get;
        set
        {
            var model = CaptionModels.Normalize(value);
            if (field == model) return;
            field = model;
            _vlm = new(time);
            Notify();
        }
    } = CaptionModels.Default;

    /// <summary>Watch rules checked against everything that enters the frame (see <see cref="WatchRules"/>).</summary>
    public List<WatchRule> Rules { get; set; } = [];

    /// <summary>A rule may fire again only after this long, so someone pacing in and out is one alert.</summary>
    private static readonly TimeSpan AlertCooldown = TimeSpan.FromMinutes(1);
    private readonly Dictionary<WatchRule, DateTimeOffset> _lastAlert = [];

    /// <summary>The recap card to show (AwayCard): set when a session stops or the tab comes back; null once dismissed.</summary>
    public SessionRecap? Recap
    {
        get;
        set { field = value; Notify(); }
    }

    /// <summary>Raised whenever <see cref="Live"/> changes; handlers should marshal to the renderer.</summary>
    public event Action? Changed;

    /// <summary>Starts a server session and the sensing loops. Returns an error message, or null on success.</summary>
    public async Task<string?> StartAsync(ElementReference? video, bool demo, bool enableVlm, string timeZoneId)
    {
        if (IsRunning) return null;
        await SendOutboxAsync();

        if (!demo)
        {
            if (video is null) return "No video element to watch.";
            var preview = await js.TryInvokeAsync<string>("powatchInference.startPreview", video);
            if (preview != "OK") return preview ?? "The camera could not be started.";
        }

        SessionDto? session = null;
        try
        {
            session = await api.StartSessionAsync(new StartSessionRequestDto { SessionId = Guid.NewGuid(), TimeZoneId = timeZoneId });
        }
        finally
        {
            if (session is null && !demo) await js.TryInvokeVoidAsync("powatchInference.stopMonitor");
        }

        if (session is null)
        {
            return "The server could not be reached, so no session was started.";
        }

        _video = video;
        Captions = enableVlm;
        _batcher = new TickBatcher(time);
        _vlm = new VlmScheduler(time);
        _highlights = new HighlightRules(time);
        _sitting = new SittingStreak(TimeSpan.FromMinutes(SitNudgeMinutes));
        _observing.Clear();
        _inFrame = null;
        _scene = demo ? new SyntheticScene() : null;
        _demoStep = 0;
        _cts = new CancellationTokenSource();
        Live = new LiveSensingState { Session = session, Demo = demo, StartedUtc = session.StartedUtc };
        // A new recording makes the last one's recap card stale.
        Recap = null;
        await js.TryInvokeVoidAsync("powatchOutbox.guard", session.Id.ToString());
        Rules = await WatchRules.LoadAsync(js);
        _lastAlert.Clear();
        Cue("start");

        if (!demo)
        {
            _self = DotNetObjectReference.Create(this);
            await js.TryInvokeVoidAsync("powatchPixels.start", video, _self, 250);
            // Asked once per session: the browser shows its own microphone prompt the first time.
            if (Listening)
            {
                var heard = await js.TryInvokeAsync<string>("powatchAudio.start", _self);
                Live.AudioStatus = heard == "OK" ? "listening" : heard ?? "The microphone could not be started.";
            }
            _ = LoadDetectorAsync(video!.Value, _cts.Token);
        }

        _ = RunLoopAsync(FlushEvery, FlushAsync, _cts.Token);
        _ = ChapterLoopAsync(session.Id, _cts.Token);
        _ = RunLoopAsync(demo ? DemoStep : TimeSpan.FromSeconds(1), demo ? DemoTickAsync : VlmTickAsync, _cts.Token);
        Notify();
        return null;
    }

    /// <summary>Starts a camera (or demo) session with the <see cref="Captions"/> setting in the local time zone.</summary>
    public Task<string?> StartAsync(bool demo) => StartAsync(demo ? null : Feed, demo, Captions, TimeZoneInfo.Local.Id);

    /// <summary>Shows the camera over <paramref name="slot"/> while a camera session runs; hides it otherwise.</summary>
    public async Task DockFeedAsync(ElementReference slot)
    {
        if (Feed is { } feed && IsRunning && !Live.Demo)
            await js.TryInvokeVoidAsync("powatchFeed.dock", feed, slot);
        else
            await js.TryInvokeVoidAsync("powatchFeed.undock");
    }

    /// <summary>Stops the session and leaves its recap in <see cref="Recap"/>, whichever control stopped it.</summary>
    public async Task StopAsync()
    {
        if (_cts is null) return;
        await _cts.CancelAsync();
        _cts.Dispose();
        _cts = null;

        await js.TryInvokeVoidAsync("powatchPixels.stop");
        await js.TryInvokeVoidAsync("powatchAudio.stop");
        await js.TryInvokeVoidAsync("powatchDetector.stop");
        if (s_detecting == this) s_detecting = null;
        if (_scene is null) await js.TryInvokeVoidAsync("powatchInference.stopMonitor");

        await KeepAsync(_batcher.Flush(final: true));
        // A send cancelled with the session may still be finishing; then one last pass with the final batch.
        await _sending;
        await SendOutboxAsync();
        if (Live.Session is { } session)
        {
            // Offline or refused: keep the page-exit guard so leaving still ends the session server-side.
            if (await api.StopSessionAsync(session.Id) is { } stopped)
            {
                Live.Session = stopped;
                await js.TryInvokeVoidAsync("powatchOutbox.unguard");
            }
            Recap = new SessionRecap(Live.Session, null);
        }

        Cue("stop");

        _self?.Dispose();
        _self = null;
        Notify();
    }

    /// <summary>Asks for a chapter every ten minutes; a missed one is written by the next call, or when the session ends.</summary>
    private async Task ChapterLoopAsync(Guid sessionId, CancellationToken ct)
    {
        try
        {
            await Task.Delay(ChapterSettle, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await RunLoopAsync(ChapterEvery, c => api.UpdateStoryAsync(sessionId, c), ct);
    }

    private static async Task RunLoopAsync(TimeSpan period, Func<CancellationToken, Task> step, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(period);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await step(ct);
        }
        catch (OperationCanceledException)
        {
            // Session stopped.
        }
    }

    private void Notify() => Changed?.Invoke();

    public async ValueTask DisposeAsync()
    {
        if (IsRunning) await StopAsync();
        _self?.Dispose();
    }
}
