using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PoWatch.Shared.Models;
using PoWatch.Shared.Services.Sensing;
using PoWatch.Shared.Services.Tracking;

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
        _observing.Clear();
        _inFrame = null;
        _scene = demo ? new SyntheticScene() : null;
        _demoStep = 0;
        _cts = new CancellationTokenSource();
        Live = new LiveSensingState { Session = session, Demo = demo, StartedUtc = session.StartedUtc };
        await js.TryInvokeVoidAsync("powatchOutbox.guard", session.Id.ToString());
        Rules = await WatchRules.LoadAsync(js);
        _lastAlert.Clear();
        Cue("start");

        if (!demo)
        {
            _self = DotNetObjectReference.Create(this);
            await js.TryInvokeVoidAsync("powatchPixels.start", video, _self, 250);
            _ = LoadDetectorAsync(video!.Value, _cts.Token);
        }

        _ = RunLoopAsync(FlushEvery, FlushAsync, _cts.Token);
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

    /// <summary>The session's long-exposure print so far (fx.js), or null when nobody has moved.</summary>
    public Task<string?> ExposureAsync() => js.TryInvokeAsync<string>("powatchFx.exposure", Live.Demo ? null : Feed);

    /// <summary>Stops the session and leaves its recap in <see cref="Recap"/>, whichever control stopped it.</summary>
    public async Task StopAsync()
    {
        if (_cts is null) return;
        // Taken before stopping, while the camera still has a frame to print the paths over.
        var exposure = await ExposureAsync();
        await _cts.CancelAsync();
        _cts.Dispose();
        _cts = null;

        await js.TryInvokeVoidAsync("powatchPixels.stop");
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
            Recap = new SessionRecap(Live.Session, null, exposure);
        }

        Cue("stop");

        _self?.Dispose();
        _self = null;
        Notify();
    }

    [JSInvokable]
    public void OnPixelSample(string json)
    {
        if (!IsRunning || JsonSerializer.Deserialize(json, Json.PixelSamplePayload) is not { } p) return;
        AddPixel(new PixelSample(p.AtUtc, p.Motion, p.Luminance, p.Grid, p.Palette));
    }

    /// <summary>
    /// The detector's boxes as typed arrays (detector-bridge.js), not JSON: one label per box, and in
    /// <paramref name="values"/> (a Float64Array) per box its score, x0, y0, x1, y1, the length of its
    /// colour signature, then the signature itself.
    /// </summary>
    [JSExport]
    [SupportedOSPlatform("browser")]
    internal static void OnDetectorBoxes(
        [JSMarshalAs<JSType.Date>] DateTimeOffset atUtc,
        double latencyMs,
        [JSMarshalAs<JSType.Array<JSType.String>>] string[] labels,
        [JSMarshalAs<JSType.Array<JSType.Number>>] double[] values) =>
        s_detecting?.OnDetections(atUtc, latencyMs, labels, values);

    private void OnDetections(DateTimeOffset atUtc, double latencyMs, string[] labels, double[] values)
    {
        if (!IsRunning) return;
        var detections = new List<Detection>(labels.Length);
        var at = 0;
        foreach (var label in labels)
        {
            if (at + 6 > values.Length) break;
            var length = (int)values[at + 5];
            if (length < 0 || at + 6 + length > values.Length) break;
            float[]? signature = length == 0 ? null : new float[length];
            for (var i = 0; i < length; i++) signature![i] = (float)values[at + 6 + i];
            detections.Add(new Detection(label, values[at], values[at + 1], values[at + 2], values[at + 3], values[at + 4], signature));
            at += 6 + length;
        }

        Live.DetectorLatencyMs = latencyMs;
        AddDetections(atUtc, detections);
    }

    [JSInvokable]
    public void OnDetectorError(string message)
    {
        Live.LastError = $"Detector: {message}";
        Cue("error");
        Notify();
    }

    private void AddPixel(PixelSample sample)
    {
        _batcher.AddPixel(sample);
        _vlm.ObserveMotion(sample.Motion);
        Live.RecordPixel(sample, time.GetUtcNow());
        Consider(_highlights.OnPixel(sample), sample.AtUtc);
        Notify();
    }

    private void AddDetections(DateTimeOffset atUtc, IReadOnlyList<Detection> detections)
    {
        var frame = _tracker.Update(atUtc, detections);
        _inFrame = [.. frame.Active.Select(t => t.Label)];
        _batcher.AddDetections(atUtc, frame, trackId => Live.RegularFor(trackId)?.Id);
        Live.RecordDetections(frame, time.GetUtcNow());
        foreach (var exited in frame.Exited) Live.ForgetTrack(exited.TrackId);
        foreach (var entered in frame.Entered) Announce(entered);
        Consider(_highlights.OnDetections(frame), atUtc);
        RecogniseStableTracks(frame);
        Live.ExpirePrompts(time.GetUtcNow());
        Notify();
    }

    private void AddCaption(DateTimeOffset atUtc, string raw, float[]? embedding = null)
    {
        if (CaptionParser.Parse(raw) is not { } caption) return;
        _batcher.AddCaption(atUtc, caption.Text, caption.Notable ? 1 : null);
        Live.RecordCaption(caption, atUtc);
        Consider(_highlights.OnCaption(caption, embedding), atUtc);
        Notify();
    }

    /// <summary>A soft blip from the side of the frame it entered on, and an alert for any watch rule it matches.</summary>
    private void Announce(TrackEntered track)
    {
        if (!CentroidTracker.IsPresence(track.Label)) return;
        Cue("enter", pan: track.Edge switch { "Left" => -0.8, "Right" => 0.8, _ => 0 });

        var now = time.GetLocalNow();
        foreach (var rule in Rules.Where(r => r.Matches(track.Label, now)))
        {
            if (_lastAlert.TryGetValue(rule, out var last) && now - last < AlertCooldown) continue;
            _lastAlert[rule] = now;
            var text = $"{char.ToUpperInvariant(track.Label[0])}{track.Label[1..]} seen at {now:HH:mm}";
            Cue("alert");
            _ = js.TryInvokeVoidAsync("powatchCues.notify", text);
            // The hub relays it to every open tab of this user, this one included (the tray lists it).
            _ = api.RaiseAlertAsync(text);
        }
    }

    private void Cue(string name, double pan = 0) => _ = js.TryInvokeVoidAsync("powatchCues.play", name, pan);

    /// <summary>
    /// Records a highlight as a Notable moment. With a camera, a snapshot of the frame is uploaded
    /// first (the only image that ever leaves the browser); in demo mode, or without image storage,
    /// the moment is kept without a picture.
    /// </summary>
    private void Consider(Highlight? highlight, DateTimeOffset atUtc)
    {
        if (highlight is null) return;
        if (_scene is not null || _capturing || _video is null)
        {
            AddMoment(highlight, atUtc, imagePath: null);
            return;
        }

        _capturing = true;
        _ = CaptureAsync(highlight, atUtc);
    }

    private async Task CaptureAsync(Highlight highlight, DateTimeOffset atUtc)
    {
        // Offline or out of storage: the moment is kept without its picture.
        string? path = null;
        try
        {
            var image = await js.TryInvokeAsync<string>("powatchPixels.snapshot", _video);
            var upload = image is null ? null : await api.CreateSnapshotUploadAsync(DateOnly.FromDateTime(atUtc.ToLocalTime().DateTime));
            if (upload is not null && await js.TryInvokeVoidAsync("powatchBlobUpload.uploadFrame", upload.UploadUrl, image))
                path = upload.Path;
        }
        finally
        {
            _capturing = false;
        }

        AddMoment(highlight, atUtc, path);
    }

    private void AddMoment(Highlight highlight, DateTimeOffset atUtc, string? imagePath)
    {
        _batcher.AddEvent(new SceneEventDto { AtUtc = atUtc, Kind = "Notable", Text = highlight.Reason, Score = highlight.Score, ImagePath = imagePath });
        Notify();
    }

    /// <summary>Asks the server who each settled person or pet is, once per track.</summary>
    private void RecogniseStableTracks(TrackerFrame frame)
    {
        foreach (var track in frame.Active)
        {
            if (!CentroidTracker.IsPresence(track.Label) || track.Signature.Count == 0 || track.Age < RecogniseAfter) continue;
            if (Live.RegularFor(track.TrackId) is not null || !_observing.Add(track.TrackId)) continue;
            _ = RecogniseAsync(track);
        }
    }

    private async Task RecogniseAsync(TrackState track)
    {
        // Unanswered (offline or refused): this track stays unrecognised; it is asked again when it next enters.
        var result = await api.ObserveRegularAsync(new ObserveRegularRequestDto { Class = track.Label, Signature = [.. track.Signature] });
        if (result is null || !IsRunning) return;
        Live.AssignRegular(track.TrackId, result.Regular);

        if (result.IsNew && Nameable.Contains(track.Label))
        {
            var snapshot = _scene is null && _video is not null ? await js.TryInvokeAsync<string>("powatchPixels.snapshot", _video) : null;
            Live.AddPrompt(new NamingPrompt(result.Regular, snapshot, time.GetUtcNow()));
            Cue("regular");
        }

        Notify();
    }

    /// <summary>Saves a name from a prompt (or dismisses it when blank) and relabels live tracks.</summary>
    public async Task NameAsync(string regularId, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            Live.DismissPrompt(regularId);
            Notify();
            return;
        }

        if (await api.RenameRegularAsync(regularId, name) is { } renamed) Live.Rename(renamed);
        Notify();
    }

    private Task DemoTickAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var withCaption = _vlm.IsDue;
        var frame = _scene!.Next(now, withCaption);
        AddPixel(frame.Pixels);
        if (_demoStep++ % 4 == 0) AddDetections(now, frame.Detections);
        if (frame.Caption is { } caption)
        {
            _vlm.MarkRun();
            AddCaption(now, caption);
        }

        Live.DetectorModel ??= "synthetic scene";
        return Task.CompletedTask;
    }

    private async Task VlmTickAsync(CancellationToken ct)
    {
        // The grounded prompt doubles as the scene key: same objects, same counts, same prompt.
        var prompt = CaptionParser.Prompt(_inFrame ?? []);
        var scene = _inFrame is null ? null : prompt;
        if (!Captions || _vlmBusy || !_vlm.IsDueFor(scene) || _video is null) return;
        _vlmBusy = true;
        try
        {
            _vlm.MarkRun(scene);
            var result = await js.TryInvokeAsync<VlmResultPayload>("powatchInference.captureAndInfer", prompt, _video, CaptionParser.MaxNewTokens, CaptionModel);
            if (result is { IsAvailable: true })
                AddCaption(time.GetUtcNow(), string.IsNullOrWhiteSpace(result.Caption) ? result.Activity : result.Caption, result.Embedding);
        }
        finally
        {
            _vlmBusy = false;
        }
    }

    private async Task LoadDetectorAsync(ElementReference video, CancellationToken ct)
    {
        var info = await js.TryInvokeAsync<DetectorInfoPayload>("powatchDetector.load");
        if (ct.IsCancellationRequested) return;
        if (info is null)
        {
            Live.LastError = "The object detector could not be loaded; pixel stats continue.";
            Notify();
            return;
        }

        Live.DetectorModel = info.ModelId;
        Live.DetectorDevice = info.Dtype is null ? info.Device : $"{info.Device} · {info.Dtype}";
        s_detecting = this;
        await js.TryInvokeVoidAsync("powatchDetector.start", video, _self, 1000, 0.5);
        Notify();
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        await KeepAsync(_batcher.Flush());
        // Sending runs beside this loop: a request hanging on a dead network must not stop new
        // batches from reaching the durable outbox.
        _ = SendOutboxAsync(ct);
    }

    /// <summary>Puts a new batch in the durable outbox, so a reload or crash cannot lose it; counts what an overflowing outbox dropped.</summary>
    private async Task KeepAsync(IngestBatchDto? batch)
    {
        if (batch is null || Live.Session is not { } session) return;
        Live.LostTicks += await js.TryInvokeAsync<int>("powatchOutbox.put", batch.BatchKey.ToString(), session.Id.ToString(), JsonSerializer.Serialize(batch, Json.IngestBatchDto));
    }

    /// <summary>
    /// Posts everything waiting in the outbox, oldest first, whichever session it belongs to: this one,
    /// or one an earlier page load left behind (reloaded, crashed, or stopped while offline). Replays are
    /// safe: the server counts a batch key once. Stops at the first transport failure; the rest wait for
    /// the next pass. While a pass runs, a second call joins it instead of starting another.
    /// </summary>
    public Task SendOutboxAsync(CancellationToken ct = default) =>
        _sending.IsCompleted ? _sending = SendWaitingAsync(ct) : _sending;

    private async Task SendWaitingAsync(CancellationToken ct)
    {
        List<OutboxEntryPayload> waiting;
        try
        {
            waiting = await js.TryInvokeAsync<string>("powatchOutbox.waiting") is { } json ? JsonSerializer.Deserialize(json, Json.ListOutboxEntryPayload) ?? [] : [];
        }
        catch (JsonException)
        {
            waiting = [];
        }
        var left = waiting.Count;
        foreach (var entry in waiting)
        {
            if (!Guid.TryParse(entry.SessionId, out var sessionId) || JsonSerializer.Deserialize(entry.Json, Json.IngestBatchDto) is not { } batch)
            {
                // Unreadable: it can never be sent, so it must not block the queue.
                await js.TryInvokeVoidAsync("powatchOutbox.remove", entry.Key);
                left--;
                continue;
            }

            try
            {
                var result = await api.PostBatchAsync(sessionId, batch, ct);
                // Null means the server refused it outright; retrying would never help.
                if (result is null) Live.RejectedBatches++;
                else Live.BatchesSent++;
                // Back online: the offline notice has done its job.
                if (Live.LastError?.StartsWith("Offline", StringComparison.Ordinal) == true) Live.LastError = null;
                await js.TryInvokeVoidAsync("powatchOutbox.remove", entry.Key);
                left--;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (IsRunning && !ct.IsCancellationRequested)
                {
                    if (Live.LastError is null) Cue("error");
                    Live.LastError = "Offline — batches are queued and will be resent.";
                }
                break;
            }
        }

        Live.PendingBatches = left;
        Notify();
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
