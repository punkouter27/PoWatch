using System.Text.Json;
using PoWatch.Shared.Models;
using PoWatch.Shared.Services;

namespace PoWatch.Mobile.Services.Sensing;

/// <summary>What the Live page shows while observing.</summary>
public sealed class LiveState
{
    public double Motion { get; set; }
    public double Luminance { get; set; }
    public double Sound { get; set; }
    public double? Lux { get; set; }
    public double? PressureHpa { get; set; }
    public bool DeviceMoving { get; set; }
    public IReadOnlyList<string> InFrame { get; set; } = [];
    public int Detections { get; set; }
    public double DetectorMs { get; set; }
    public string ModelStatus { get; set; } = "not loaded";
    public string? Throttle { get; set; }
    public string? LastHighlight { get; set; }
    public string? LastError { get; set; }
    public byte[]? Thumbnail { get; set; }
    public int Ticks { get; set; }
    public DateTimeOffset? StartedUtc { get; set; }
}

/// <summary>
/// The phone as a PoWatch sensor: native camera frames through the web's pixel layer and the same
/// RF-DETR detector, microphone loudness, and the phone's own sensors (light, barometer, accelerometer),
/// folded by the shared <see cref="TickBatcher"/> and <see cref="CentroidTracker"/> into the exact
/// 10-second ticks a browser sends — then into the durable <see cref="Outbox"/>.
/// </summary>
public sealed class SensingEngine(ApiClient api, Outbox outbox, ICameraSource camera, IMicrophone microphone, ILightSensor light, IImageEncoder images, IDeviceHost host) : IDisposable
{
    private const string RulesKey = "powatch_watch_rules";
    private const string PendingStopsKey = "powatch_pending_stops";
    private static readonly TimeSpan PixelEvery = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ThumbnailEvery = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RecogniseAfter = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AlertCooldown = TimeSpan.FromMinutes(5);
    private static readonly HashSet<string> Nameable = new(StringComparer.OrdinalIgnoreCase) { "person", "cat", "dog" };
    private static readonly HttpClient Upload = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly object _gate = new();
    private readonly PixelLayer _pixels = new();
    private readonly Detector _detector = new();
    private readonly Dictionary<string, string> _regularOfTrack = new(StringComparer.Ordinal);
    private readonly HashSet<string> _observing = new(StringComparer.Ordinal);
    private readonly Dictionary<WatchRule, DateTimeOffset> _lastAlert = [];
    private TickBatcher _batcher = new(TimeProvider.System);
    private CentroidTracker _tracker = CentroidTracker.ForLive();
    private HighlightRules _highlights = new(TimeProvider.System);
    private CancellationTokenSource? _run;
    private DateTimeOffset _lastPixel, _lastDetect, _lastThumb, _lastAccel = DateTimeOffset.UtcNow;
    private double _movedSince;
    private bool _detecting, _capturing;
    private RgbaFrame? _latest;

    public LiveState Live { get; } = new();

    public bool IsRunning => SessionId is not null;

    public Guid? SessionId { get; private set; }

    public event Action? Changed;

    public static List<WatchRule> Rules
    {
        get => JsonSerializer.Deserialize(Preferences.Default.Get(RulesKey, "[]"), MobileJsonContext.Default.ListWatchRule) ?? [];
        set => Preferences.Default.Set(RulesKey, JsonSerializer.Serialize(value, MobileJsonContext.Default.ListWatchRule));
    }

    /// <summary>Base detector cadence about once a second, as the browser; slower on low battery or when hot (item 2).</summary>
    private TimeSpan DetectEvery
    {
        get
        {
            int heat = host.ThermalLevel;
            bool onBattery = Battery.Default.PowerSource == BatteryPowerSource.Battery;
            (TimeSpan every, string? why) = heat >= 4 ? (TimeSpan.MaxValue, "phone too hot — detector paused")
                : heat >= 3 ? (TimeSpan.FromSeconds(5), "phone hot — detector slowed")
                : onBattery && Battery.Default.ChargeLevel is >= 0 and < 0.3 ? (TimeSpan.FromSeconds(3), "battery low — detector slowed")
                : (TimeSpan.FromSeconds(1), null);
            Live.Throttle = why;
            return every;
        }
    }

    public async Task<string?> StartAsync(IProgress<double> modelProgress)
    {
        if (IsRunning)
        {
            return null;
        }

        if (await Permissions.RequestAsync<Permissions.Camera>() != PermissionStatus.Granted)
        {
            return "PoWatch needs the camera to observe.";
        }

        bool listen = await Permissions.RequestAsync<Permissions.Microphone>() == PermissionStatus.Granted;
        await host.RequestNotificationsAsync();

        try
        {
            Live.ModelStatus = ModelStore.IsDownloaded ? "loading" : "downloading model";
            Changed?.Invoke();
            string path = await ModelStore.EnsureAsync(modelProgress, CancellationToken.None);
            await Task.Run(() => _detector.Load(path));
            Live.ModelStatus = $"RF-DETR nano · {_detector.Accelerator}";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException)
        {
            Live.ModelStatus = "no detector";
            Live.LastError = $"Detector unavailable ({ex.GetType().Name}) — observing motion, light and sound only.";
        }

        Guid id = Guid.NewGuid();
        if (await api.StartSessionAsync(id) is null)
        {
            return ApiClient.Online ? "The server didn't start a session. Are you signed in?" : "Starting needs a connection; after that, observing works offline.";
        }

        lock (_gate)
        {
            _batcher = new TickBatcher(TimeProvider.System);
            _tracker = CentroidTracker.ForLive();
            _highlights = new HighlightRules(TimeProvider.System);
            _regularOfTrack.Clear();
            _observing.Clear();
            _pixels.Reset();
        }

        SessionId = id;
        Live.StartedUtc = DateTimeOffset.UtcNow;
        Live.Ticks = 0;
        _run = new CancellationTokenSource();
        host.SetKeepAlive(true, "Observing");

        if (!await camera.StartAsync(OnFrame))
        {
            await StopAsync();
            return "The camera couldn't be opened.";
        }

        if (listen)
        {
            await microphone.StartAsync(OnSound);
        }

        StartSensors();
        _ = FlushLoopAsync(_run.Token);
        Changed?.Invoke();
        return null;
    }

    public async Task StopAsync()
    {
        if (SessionId is not { } id)
        {
            return;
        }

        camera.Stop();
        microphone.Stop();
        StopSensors();
        _run?.Cancel();

        IngestBatchDto? last;
        lock (_gate)
        {
            last = _batcher.Flush(final: true);
        }

        if (last is not null)
        {
            await outbox.PutAsync(id, last);
        }

        SessionId = null;
        host.SetKeepAlive(false, "Stopped");
        host.SetDim(false);
        await outbox.SendAsync();
        if (await api.StopSessionAsync(id) is null)
        {
            // Offline: close it on the next pass so the journal doesn't show it running forever.
            Preferences.Default.Set(PendingStopsKey, Preferences.Default.Get(PendingStopsKey, string.Empty) + id + ";");
        }

        Changed?.Invoke();
    }

    /// <summary>Retries stops that happened offline (called with each outbox pass).</summary>
    public async Task RetryPendingStopsAsync()
    {
        string pending = Preferences.Default.Get(PendingStopsKey, string.Empty);
        if (pending.Length == 0 || !ApiClient.Online || outbox.Pending > 0)
        {
            return;
        }

        List<string> left = [];
        foreach (string raw in pending.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Guid.TryParse(raw, out Guid id) && await api.StopSessionAsync(id) is null)
            {
                left.Add(raw);
            }
        }

        Preferences.Default.Set(PendingStopsKey, string.Join(';', left.Select(l => l + ";")));
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        int pass = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                IngestBatchDto? batch;
                lock (_gate)
                {
                    batch = _batcher.Flush();
                }

                if (batch is not null && SessionId is { } id)
                {
                    Live.Ticks += batch.Ticks.Count;
                    await outbox.PutAsync(id, batch);
                    host.SetKeepAlive(true, $"{Live.Ticks} ticks · {outbox.Pending} waiting to send");
                }

                // Send every 10 s (or as soon as a batch closes) — the outbox stops at the first outage.
                if (batch is not null || ++pass % 10 == 0)
                {
                    _ = outbox.SendAsync(ct);
                }

                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ── Inputs ───────────────────────────────────────────────────────────────

    private void OnFrame(RgbaFrame frame)
    {
        if (!IsRunning)
        {
            return;
        }

        _latest = frame;
        if (frame.AtUtc - _lastPixel >= PixelEvery)
        {
            _lastPixel = frame.AtUtc;
            PixelSample sample = _pixels.Sample(frame);
            Highlight? spike;
            lock (_gate)
            {
                _batcher.AddPixel(sample);
                spike = _highlights.OnPixel(sample);
            }

            Live.Motion = sample.Motion;
            Live.Luminance = sample.Luminance;
            Consider(spike, frame);
        }

        if (frame.AtUtc - _lastThumb >= ThumbnailEvery)
        {
            _lastThumb = frame.AtUtc;
            Live.Thumbnail = images.Jpeg(frame, 480, 70);
        }

        TimeSpan every = DetectEvery;
        if (_detector.IsReady && !_detecting && every != TimeSpan.MaxValue && frame.AtUtc - _lastDetect >= every)
        {
            _lastDetect = frame.AtUtc;
            _detecting = true;
            _ = Task.Run(() => DetectAsync(frame));
        }
    }

    private async Task DetectAsync(RgbaFrame frame)
    {
        try
        {
            (List<Detection> found, double ms) = _detector.Detect(frame);
            TrackerFrame tracked;
            Highlight? notable;
            lock (_gate)
            {
                tracked = _tracker.Update(frame.AtUtc, found);
                _batcher.AddDetections(frame.AtUtc, tracked, track => _regularOfTrack.GetValueOrDefault(track));
                notable = _highlights.OnDetections(tracked);
                foreach (TrackExited gone in tracked.Exited)
                {
                    _regularOfTrack.Remove(gone.TrackId);
                    _observing.Remove(gone.TrackId);
                }
            }

            Live.DetectorMs = ms;
            Live.Detections = found.Count;
            Live.InFrame = [.. tracked.Active.Select(t => t.Label)];
            foreach (TrackEntered entered in tracked.Entered)
            {
                Announce(entered);
            }

            Consider(notable, frame);
            await RecogniseAsync(tracked);
        }
        catch (Exception ex) when (ex is Microsoft.ML.OnnxRuntime.OnnxRuntimeException or InvalidOperationException)
        {
            Live.LastError = $"Detector: {ex.Message}";
        }
        finally
        {
            _detecting = false;
        }
    }

    private void OnSound(double mean, double max)
    {
        if (!IsRunning)
        {
            return;
        }

        lock (_gate)
        {
            _batcher.AddSound(DateTimeOffset.UtcNow, mean, max);
        }

        Live.Sound = mean;
    }

    // ── Phone sensors (item 5) ───────────────────────────────────────────────

    private void StartSensors()
    {
        if (light.IsSupported)
        {
            light.Start(lux => Live.Lux = lux);
        }

        try
        {
            if (Barometer.Default.IsSupported)
            {
                Barometer.Default.ReadingChanged += OnPressure;
                Barometer.Default.Start(SensorSpeed.UI);
            }

            if (Accelerometer.Default.IsSupported)
            {
                Accelerometer.Default.ReadingChanged += OnAcceleration;
                Accelerometer.Default.Start(SensorSpeed.UI);
            }
        }
        catch (FeatureNotSupportedException)
        {
        }

        _ = EnvironmentLoopAsync(_run!.Token);
    }

    private void StopSensors()
    {
        light.Stop();
        try
        {
            if (Barometer.Default.IsMonitoring)
            {
                Barometer.Default.Stop();
            }

            if (Accelerometer.Default.IsMonitoring)
            {
                Accelerometer.Default.Stop();
            }
        }
        catch (FeatureNotSupportedException)
        {
        }

        Barometer.Default.ReadingChanged -= OnPressure;
        Accelerometer.Default.ReadingChanged -= OnAcceleration;
    }

    private void OnPressure(object? sender, BarometerChangedEventArgs e) => Live.PressureHpa = e.Reading.PressureInHectopascals;

    // "Moving" = the phone's acceleration departs from a steady 1 g by more than 0.08 g.
    private void OnAcceleration(object? sender, AccelerometerChangedEventArgs e)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        double gap = Math.Min(0.5, (now - _lastAccel).TotalSeconds);
        _lastAccel = now;
        bool moving = Math.Abs(e.Reading.Acceleration.Length() - 1) > 0.08;
        Live.DeviceMoving = moving;
        if (moving)
        {
            _movedSince += gap;
        }
    }

    private async Task EnvironmentLoopAsync(CancellationToken ct)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                double moved = Interlocked.Exchange(ref _movedSince, 0);
                lock (_gate)
                {
                    _batcher.AddEnvironment(DateTimeOffset.UtcNow, Live.Lux, Live.PressureHpa, moved);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ── Events ───────────────────────────────────────────────────────────────

    private void Announce(TrackEntered track)
    {
        if (!CentroidTracker.IsPresence(track.Label))
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        foreach (WatchRule rule in Rules.Where(r => r.Matches(track.Label, now)))
        {
            if (_lastAlert.TryGetValue(rule, out DateTimeOffset last) && now - last < AlertCooldown)
            {
                continue;
            }

            _lastAlert[rule] = now;
            string text = $"{char.ToUpperInvariant(track.Label[0])}{track.Label[1..]} seen at {now:HH:mm}";
            host.Notify("PoWatch", text);
            _ = api.RaiseAlertAsync(text);
        }
    }

    /// <summary>A highlight becomes a Notable moment with a snapshot (the only image that leaves the phone).</summary>
    private void Consider(Highlight? highlight, RgbaFrame frame)
    {
        if (highlight is null || SessionId is null)
        {
            return;
        }

        Live.LastHighlight = highlight.Reason;
        if (_capturing)
        {
            AddMoment(highlight, frame.AtUtc, null);
            return;
        }

        _capturing = true;
        _ = Task.Run(async () =>
        {
            string? path = null;
            try
            {
                byte[] jpeg = images.Jpeg(frame, 960, 80);
                if (ApiClient.Online && await api.CreateSnapshotUploadAsync(DateOnly.FromDateTime(frame.AtUtc.LocalDateTime)) is { } upload)
                {
                    using ByteArrayContent body = new(jpeg);
                    body.Headers.Add("x-ms-blob-type", "BlockBlob");
                    body.Headers.ContentType = new("image/jpeg");
                    using HttpResponseMessage put = await Upload.PutAsync(upload.UploadUrl, body);
                    path = put.IsSuccessStatusCode ? upload.Path : null;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Offline: the moment is kept without its picture.
            }
            finally
            {
                _capturing = false;
            }

            AddMoment(highlight, frame.AtUtc, path);
        });
    }

    private void AddMoment(Highlight highlight, DateTimeOffset atUtc, string? imagePath)
    {
        lock (_gate)
        {
            _batcher.AddEvent(new SceneEventDto { AtUtc = atUtc, Kind = "Notable", Text = highlight.Reason, Score = highlight.Score, ImagePath = imagePath });
        }
    }

    /// <summary>Asks the server who each settled person or pet is, once per track (as the web does).</summary>
    private async Task RecogniseAsync(TrackerFrame frame)
    {
        foreach (TrackState track in frame.Active)
        {
            if (!CentroidTracker.IsPresence(track.Label) || track.Signature.Count == 0 || track.Age < RecogniseAfter || !ApiClient.Online)
            {
                continue;
            }

            lock (_gate)
            {
                if (_regularOfTrack.ContainsKey(track.TrackId) || !_observing.Add(track.TrackId))
                {
                    continue;
                }
            }

            if (await api.ObserveRegularAsync(track.Label, track.Signature) is { } result)
            {
                lock (_gate)
                {
                    _regularOfTrack[track.TrackId] = result.Regular.Id;
                }

                if (result.IsNew && Nameable.Contains(track.Label))
                {
                    host.Notify("PoWatch", $"New {track.Label} seen — name them under Settings → Regulars.");
                }
            }
        }
    }

    /// <summary>The latest frame, for a manual snapshot.</summary>
    public byte[]? CurrentJpeg() => _latest is { } f ? images.Jpeg(f, 1280, 85) : null;

    public void Dispose()
    {
        _detector.Dispose();
        _run?.Dispose();
    }
}
