using PoWatch.Shared.Models;
using PoWatch.Shared.Services;

namespace PoWatch.Client.Services;

/// <summary>What the Live page shows while a session runs: counters, rates, rolling histories.</summary>
public sealed class LiveSensingState
{
    public const int HistoryLength = 240;
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(10);

    private readonly Queue<DateTimeOffset> _pixelTimes = new();
    private readonly List<double> _motion = [];
    private readonly List<double> _light = [];
    private readonly float[] _heat = new float[144];

    public SessionDto? Session { get; set; }
    public bool Demo { get; init; }
    public DateTimeOffset? StartedUtc { get; init; }

    public long PixelSamples { get; private set; }
    public double PixelHz { get; private set; }
    public double DetectorLatencyMs { get; set; }
    public string? DetectorModel { get; set; }
    public string? DetectorDevice { get; set; }

    public int PeopleNow { get; private set; }
    public int AnimalsNow { get; private set; }
    public IReadOnlyDictionary<string, int> ClassesNow { get; private set; } = new Dictionary<string, int>();
    public IReadOnlyList<TrackState> Tracks { get; private set; } = [];
    public long Visits { get; private set; }

    /// <summary>How long the current sitting stretch has lasted (<see cref="SittingStreak"/>); zero when nobody is in one.</summary>
    public TimeSpan SittingFor { get; set; }
    public int PeakConcurrency { get; private set; }
    public IReadOnlyDictionary<string, int> ClassesSeen => _classesSeen;
    private readonly Dictionary<string, int> _classesSeen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many of each presence class were in frame on each recent detector pass.</summary>
    public const int ClassHistoryLength = 120;
    private readonly Dictionary<string, List<int>> _classHistory = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The counts for one class over the last <see cref="ClassHistoryLength"/> passes (empty if unseen).</summary>
    public IReadOnlyList<int> ClassHistory(string name) => _classHistory.TryGetValue(name, out var counts) ? counts : [];

    /// <summary>Live zero-shot scene activity tag derived from detections, posture, motion, and sound.</summary>
    public string SceneActivity =>
        PeopleNow == 0 ? "Empty room"
        : Motion > 0.14 ? "Moving around"
        : SoundLevel > 0.35 ? "Speaking / call"
        : SittingFor >= TimeSpan.FromSeconds(15) ? "Working at desk"
        : "Present & calm";

    public double Motion { get; private set; }
    public IReadOnlyList<double> MotionHistory => _motion;

    /// <summary>1-minute average series for time-varying numeric metrics.</summary>
    public MinuteSeries MotionMinutes { get; } = new();
    public MinuteSeries LightMinutes { get; } = new();
    public MinuteSeries SoundMinutes { get; } = new();
    public MinuteSeries PeopleMinutes { get; } = new();

    /// <summary>The room's mean brightness in [0, 1] from the latest pixel sample (null before the first).</summary>
    public double? Light { get; private set; }
    public IReadOnlyList<double> LightHistory => _light;

    /// <summary>Exponentially smoothed motion per grid cell, for the overlay.</summary>
    public IReadOnlyList<float> Heat => _heat;

    /// <summary>The microphone: "listening", why it could not, or null when it was not asked to.</summary>
    public string? AudioStatus { get; set; }

    /// <summary>The latest sound the classifier named, and when.</summary>
    public string? LastSound { get; private set; }
    public DateTimeOffset? LastSoundUtc { get; private set; }

    /// <summary>Latest microphone loudness level in [0, 1].</summary>
    public double SoundLevel { get; private set; }

    /// <summary>When the latest highlight snapshot was captured, for HUD feedback.</summary>
    public DateTimeOffset? LastSnapshotUtc { get; private set; }
    public string? LastSnapshotReason { get; private set; }

    /// <summary>Everything this session saw, said, heard, and who came and went, newest last: Live's narration.</summary>
    public IReadOnlyList<TimelineEntry> Narration => _narration;
    private readonly List<TimelineEntry> _narration = [];
    public const int NarrationLength = 200;

    private void Narrate(TimelineEntry entry)
    {
        _narration.Add(entry);
        if (_narration.Count > NarrationLength) _narration.RemoveAt(0);
    }

    public void RecordHeard(DateTimeOffset atUtc, string text, string? speaker = null)
    {
        Narrate(new TimelineEntry(atUtc, "speech", text, speaker));
    }

    public void RecordSound(DateTimeOffset atUtc, string label)
    {
        LastSound = label;
        LastSoundUtc = atUtc;
        Narrate(new TimelineEntry(atUtc, "sound", label));
    }

    public void RecordSoundLevel(double mean)
    {
        SoundLevel = Math.Clamp(mean, 0, 1);
        SoundMinutes.Add(SoundLevel, DateTimeOffset.UtcNow);
    }

    public void RecordMoment(DateTimeOffset atUtc, string reason, string? imageUrl = null)
    {
        LastSnapshotUtc = atUtc;
        LastSnapshotReason = reason;
        Narrate(new TimelineEntry(atUtc, "moment", reason, ImageUrl: imageUrl));
    }

    public DateTimeOffset? LastCaptionUtc { get; private set; }

    public long BatchesSent { get; set; }
    public long RejectedBatches { get; set; }
    public int PendingBatches { get; set; }
    public long LostTicks { get; set; }
    public string? LastError { get; set; }

    public void RecordPixel(PixelSample sample, DateTimeOffset nowUtc)
    {
        PixelSamples++;
        PixelHz = Rate(_pixelTimes, nowUtc);
        Motion = sample.Motion;
        Light = sample.Luminance;
        Push(_motion, sample.Motion);
        Push(_light, sample.Luminance);
        MotionMinutes.Add(sample.Motion, nowUtc);
        LightMinutes.Add(sample.Luminance, nowUtc);
        if (sample.Grid.Count == _heat.Length)
        {
            for (var i = 0; i < _heat.Length; i++) _heat[i] = (0.85f * _heat[i]) + (0.15f * sample.Grid[i]);
        }
    }

    public void RecordDetections(TrackerFrame frame, DateTimeOffset nowUtc)
    {
        ClassesNow = frame.CountsByLabel;
        Tracks = frame.Active;
        PeopleNow = frame.CountsByLabel.GetValueOrDefault("person");
        PeopleMinutes.Add(PeopleNow, nowUtc);
        AnimalsNow = frame.CountsByLabel.Where(kv => kv.Key != "person" && CentroidTracker.IsPresence(kv.Key)).Sum(kv => kv.Value);
        PeakConcurrency = Math.Max(PeakConcurrency, PeopleNow + AnimalsNow);

        // One point per pass for every presence class ever seen, so a class that leaves goes to zero
        // instead of the line stopping at its last sighting.
        foreach (var name in _classesSeen.Keys.Concat(ClassesNow.Keys)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Where(CentroidTracker.IsPresence))
        {
            if (!_classHistory.TryGetValue(name, out var counts)) _classHistory[name] = counts = [];
            counts.Add(ClassesNow.GetValueOrDefault(name));
            if (counts.Count > ClassHistoryLength) counts.RemoveAt(0);
        }

        foreach (var entered in frame.Entered)
        {
            _classesSeen[entered.Label] = _classesSeen.GetValueOrDefault(entered.Label) + 1;
            if (!CentroidTracker.IsPresence(entered.Label)) continue;
            Visits++;
            Narrate(new TimelineEntry(nowUtc, "enter", entered.Label, RegularFor(entered.TrackId)?.DisplayName, TrackId: entered.TrackId));
        }

        // Called before the tracker forgets them, so a recognised regular leaves by name.
        foreach (var exited in frame.Exited.Where(t => CentroidTracker.IsPresence(t.Label)))
            Narrate(new TimelineEntry(nowUtc, "exit", exited.Label, RegularFor(exited.TrackId)?.DisplayName, TrackId: exited.TrackId));
    }

    /// <summary>Recognition lands a moment after a track comes in: its lines so far take the name.</summary>
    private void NameLines(string trackId, string name)
    {
        for (var i = 0; i < _narration.Count; i++)
        {
            if (_narration[i].TrackId == trackId && _narration[i].Who != name) _narration[i] = _narration[i] with { Who = name };
        }
    }

    private readonly Dictionary<string, RegularDto> _regularByTrack = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RegularDto> _regularsSeen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _regularLastSeen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _regularSessionSeconds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _regularColors = new(StringComparer.Ordinal);
    private DateTimeOffset? _lastDetectionUtc;

    public RegularDto? RegularFor(string trackId) => _regularByTrack.GetValueOrDefault(trackId);

    /// <summary>The regulars recognised this session, one per person, in the order they were first recognised.</summary>
    public IReadOnlyList<RegularDto> Regulars => _regularsSeen.Values.ToList();

    /// <summary>When a regular was last seen in this session.</summary>
    public DateTimeOffset? RegularLastSeen(string regularId) => _regularLastSeen.TryGetValue(regularId, out var at) ? at : null;

    /// <summary>How many seconds a regular has been in view during this session.</summary>
    public double RegularSessionSeconds(string regularId) => _regularSessionSeconds.GetValueOrDefault(regularId);

    /// <summary>Anonymous dominant appearance colour swatch for a regular, derived from their histogram.</summary>
    public string? RegularColor(string regularId) => _regularColors.GetValueOrDefault(regularId);

    /// <summary>The name of a regular seen this session, by id; null when they have not been.</summary>
    public string? RegularNamed(string regularId) => _regularsSeen.TryGetValue(regularId, out var r) ? r.DisplayName : null;

    public void AssignRegular(string trackId, RegularDto regular, IReadOnlyList<float>? signature = null)
    {
        _regularByTrack[trackId] = regular;
        _regularsSeen[regular.Id] = regular;
        _regularLastSeen[regular.Id] = DateTimeOffset.UtcNow;
        if (ColorNamer.HexFromSignature(signature) is { } hex) _regularColors[regular.Id] = hex;
        NameLines(trackId, regular.DisplayName);
    }

    public void UpdateRegularPresence(DateTimeOffset nowUtc)
    {
        var dt = _lastDetectionUtc is { } last ? Math.Clamp((nowUtc - last).TotalSeconds, 0, 5) : 0;
        _lastDetectionUtc = nowUtc;
        var activeRegularIds = Tracks.Select(t => RegularFor(t.TrackId)?.Id).OfType<string>().Distinct(StringComparer.Ordinal);
        foreach (var id in activeRegularIds)
        {
            _regularLastSeen[id] = nowUtc;
            if (dt > 0) _regularSessionSeconds[id] = _regularSessionSeconds.GetValueOrDefault(id) + dt;
        }
    }

    public void ForgetTrack(string trackId) => _regularByTrack.Remove(trackId);

    public void RecordCaption(ParsedCaption caption, DateTimeOffset atUtc)
    {
        LastCaptionUtc = atUtc;
        Narrate(new TimelineEntry(atUtc, "caption", caption.Text));
    }

    private static void Push(List<double> history, double value)
    {
        history.Add(value);
        if (history.Count > HistoryLength) history.RemoveAt(0);
    }

    private static double Rate(Queue<DateTimeOffset> times, DateTimeOffset nowUtc)
    {
        times.Enqueue(nowUtc);
        while (times.Count > 0 && nowUtc - times.Peek() > RateWindow) times.Dequeue();
        return times.Count / RateWindow.TotalSeconds;
    }
}

/// <summary>
/// What the recap card shows: the session, how long the tab was away (null when the session just
/// stopped), and the long-exposure PNG data URL (null when nobody moved).
/// </summary>
public sealed record SessionRecap(SessionDto Session, TimeSpan? AwayFor);
