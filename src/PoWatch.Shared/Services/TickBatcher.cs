using PoWatch.Shared.Models;

namespace PoWatch.Shared.Services;

/// <summary>One pixel-layer measurement from the browser.</summary>
public sealed record PixelSample(DateTimeOffset AtUtc, double Motion, double Luminance, IReadOnlyList<float> Grid, IReadOnlyList<int> Palette);

/// <summary>
/// Folds the browser's samples into ten-second ticks aligned to wall-clock boundaries and packs
/// them, with the scene events raised along the way, into ingest batches. Each batch is handed out
/// once; keeping it until the server answers is the outbox's job (outbox.js), not this class's.
/// </summary>
public sealed class TickBatcher(TimeProvider time)
{
    public const int WindowSeconds = 10;

    private const int GridCells = 144;

    private readonly List<TickDto> _ready = [];
    private readonly List<SceneEventDto> _events = [];
    private Window? _window;
    private DateTimeOffset? _runStartUtc;

    public void AddPixel(PixelSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        WindowFor(sample.AtUtc).AddPixel(sample);
    }

    /// <param name="regularOf">The regular a track was recognised as, if any; exits carry it so visits count per regular.</param>
    public void AddDetections(DateTimeOffset atUtc, TrackerFrame frame, Func<string, string?>? regularOf = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        WindowFor(atUtc).AddDetections(frame);

        _events.AddRange(frame.Entered.Select(e => new SceneEventDto
        {
            AtUtc = e.AtUtc,
            Kind = "TrackEnter",
            TrackId = e.TrackId,
            Class = e.Label,
            Edge = e.Edge
        }));
        _events.AddRange(frame.Exited.Select(e => new SceneEventDto
        {
            AtUtc = e.AtUtc,
            Kind = "TrackExit",
            TrackId = e.TrackId,
            Class = e.Label,
            Edge = e.Edge,
            DwellSeconds = e.DwellSeconds,
            RegularId = regularOf?.Invoke(e.TrackId)
        }));
    }

    /// <summary>One loudness reading from the microphone (audio-layer.js), in [0, 1].</summary>
    public void AddSound(DateTimeOffset atUtc, double mean, double max) => WindowFor(atUtc).AddSound(mean, max);

    /// <summary>Words the speech model heard.</summary>
    /// <param name="regularId">Who said it, when their voice (or being alone in frame) told.</param>
    public void AddSpeech(DateTimeOffset atUtc, string text, string? regularId = null) =>
        _events.Add(new SceneEventDto { AtUtc = atUtc, Kind = "Speech", Text = text, RegularId = regularId });

    /// <summary>A sound the classifier recognised, by its label.</summary>
    public void AddSoundEvent(DateTimeOffset atUtc, string label, double score) =>
        _events.Add(new SceneEventDto { AtUtc = atUtc, Kind = "Sound", Text = label, Score = Math.Clamp(score, 0, 1) });

    public void AddCaption(DateTimeOffset atUtc, string text, double? notableScore = null) =>
        _events.Add(new SceneEventDto { AtUtc = atUtc, Kind = "Caption", Text = text, Score = notableScore });

    public void AddEvent(SceneEventDto sceneEvent)
    {
        ArgumentNullException.ThrowIfNull(sceneEvent);
        _events.Add(sceneEvent);
    }

    /// <summary>
    /// Closes the current window if it has ended (or, when <paramref name="final"/>, right now) and
    /// returns a new batch with every closed tick and pending event, or null when there is nothing to send.
    /// </summary>
    public IngestBatchDto? Flush(bool final = false)
    {
        var now = time.GetUtcNow();
        if (_window is not null && (final || now >= _window.EndUtc))
        {
            _ready.Add(_window.ToTick(final ? Min(now, _window.EndUtc) : _window.EndUtc));
            _window = null;
        }

        // Events ride along with their window's tick; they only go alone when nothing is sampling.
        if (_ready.Count == 0 && (_events.Count == 0 || _window is not null)) return null;

        var batch = new IngestBatchDto
        {
            BatchKey = Guid.NewGuid(),
            Ticks = [.. _ready],
            Events = [.. _events.OrderBy(e => e.AtUtc)]
        };
        _ready.Clear();
        _events.Clear();
        return batch;
    }

    private Window WindowFor(DateTimeOffset atUtc)
    {
        _runStartUtc ??= atUtc;
        if (_window is not null && atUtc >= _window.EndUtc)
        {
            _ready.Add(_window.ToTick(_window.EndUtc));
            _window = null;
        }

        if (_window is null)
        {
            var start = new DateTimeOffset(atUtc.UtcTicks - (atUtc.UtcTicks % TimeSpan.FromSeconds(WindowSeconds).Ticks), TimeSpan.Zero);
            // The first window of a run only counts from when observing began.
            _window = new Window(start, start.AddSeconds(WindowSeconds), Max(start, _runStartUtc.Value));
        }

        return _window;
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private sealed class Window(DateTimeOffset startUtc, DateTimeOffset endUtc, DateTimeOffset observedFromUtc)
    {
        private readonly float[] _gridSum = new float[GridCells];
        private readonly float[] _presence = new float[GridCells];
        private readonly Dictionary<int, int> _colors = [];
        private readonly Dictionary<string, (int Max, int Sum)> _classes = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _tracks = new(StringComparer.Ordinal);
        private readonly int[] _postures = new int[Enum.GetValues<Posture>().Length];
        private int _pixelSamples, _gridSamples, _detectorSamples, _soundSamples;
        private double _motionSum, _motionMax, _lumaSum, _soundSum, _soundMax;

        public DateTimeOffset EndUtc => endUtc;

        public void AddPixel(PixelSample s)
        {
            _pixelSamples++;
            _motionSum += s.Motion;
            _motionMax = Math.Max(_motionMax, s.Motion);
            _lumaSum += s.Luminance;
            foreach (var color in s.Palette) _colors[color] = _colors.GetValueOrDefault(color) + 1;

            if (s.Grid.Count != GridCells) return;
            _gridSamples++;
            for (var i = 0; i < GridCells; i++) _gridSum[i] += s.Grid[i];
        }

        public void AddSound(double mean, double max)
        {
            _soundSamples++;
            _soundSum += Math.Clamp(mean, 0, 1);
            _soundMax = Math.Max(_soundMax, Math.Clamp(max, 0, 1));
        }

        public void AddDetections(TrackerFrame frame)
        {
            _detectorSamples++;
            foreach (var (label, count) in frame.CountsByLabel)
            {
                var current = _classes.GetValueOrDefault(label);
                _classes[label] = (Math.Max(current.Max, count), current.Sum + count);
            }
            foreach (var cell in frame.PresenceCells) _presence[cell]++;
            foreach (var track in frame.Active)
            {
                _tracks.Add(track.TrackId);
                _postures[(int)track.Posture]++;
            }
        }

        /// <summary>Person-seconds in a posture: how many held it on an average detector pass, over the tick's length.</summary>
        private double PostureSeconds(Posture posture, double duration) =>
            _detectorSamples == 0 ? 0 : duration * _postures[(int)posture] / _detectorSamples;

        public TickDto ToTick(DateTimeOffset closedAtUtc) =>
            ToTick(Math.Clamp((closedAtUtc - observedFromUtc).TotalSeconds, 0.001, WindowSeconds));

        private TickDto ToTick(double duration) => new()
        {
            StartUtc = startUtc,
            DurationSeconds = duration,
            SittingSeconds = PostureSeconds(Posture.Sitting, duration),
            StandingSeconds = PostureSeconds(Posture.Standing, duration),
            LyingSeconds = PostureSeconds(Posture.Lying, duration),
            PixelSamples = _pixelSamples,
            DetectorSamples = _detectorSamples,
            MotionMean = _pixelSamples == 0 ? 0 : _motionSum / _pixelSamples,
            MotionMax = _motionMax,
            LuminanceMean = _pixelSamples == 0 ? 0 : _lumaSum / _pixelSamples,
            SoundMean = _soundSamples == 0 ? 0 : _soundSum / _soundSamples,
            SoundMax = _soundMax,
            Palette = _colors.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(5).Select(kv => kv.Key).ToList(),
            MotionGrid = _gridSamples == 0 ? [] : _gridSum.Select(v => v / _gridSamples).ToList(),
            PresenceGrid = _presence.Any(v => v > 0) ? [.. _presence] : [],
            Classes = _classes.ToDictionary(
                kv => kv.Key,
                kv => new ClassCountDto { Max = kv.Value.Max, Mean = _detectorSamples == 0 ? 0 : (double)kv.Value.Sum / _detectorSamples },
                StringComparer.OrdinalIgnoreCase),
            ActiveTrackIds = _tracks.Order(StringComparer.Ordinal).ToList()
        };
    }
}
