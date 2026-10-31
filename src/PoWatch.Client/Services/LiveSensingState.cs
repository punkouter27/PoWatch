using PoWatch.Shared.Models;
using PoWatch.Shared.Services.Sensing;
using PoWatch.Shared.Services.Tracking;

namespace PoWatch.Client.Services;

/// <summary>What the Live page shows while a session runs: counters, rates, rolling histories.</summary>
public sealed class LiveSensingState
{
    public const int HistoryLength = 240;
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(10);

    private readonly Queue<DateTimeOffset> _pixelTimes = new();
    private readonly List<double> _motion = [];
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
    public int PeakConcurrency { get; private set; }
    public IReadOnlyDictionary<string, int> ClassesSeen => _classesSeen;
    private readonly Dictionary<string, int> _classesSeen = new(StringComparer.OrdinalIgnoreCase);

    public double Motion { get; private set; }
    public IReadOnlyList<double> MotionHistory => _motion;

    /// <summary>The room's mean brightness in [0, 1] from the latest pixel sample (null before the first).</summary>
    public double? Light { get; private set; }

    /// <summary>Exponentially smoothed motion per grid cell, for the overlay.</summary>
    public IReadOnlyList<float> Heat => _heat;

    public string? LastCaption { get; private set; }
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
        AnimalsNow = frame.CountsByLabel.Where(kv => kv.Key != "person" && CentroidTracker.IsPresence(kv.Key)).Sum(kv => kv.Value);
        PeakConcurrency = Math.Max(PeakConcurrency, PeopleNow + AnimalsNow);

        foreach (var entered in frame.Entered)
        {
            _classesSeen[entered.Label] = _classesSeen.GetValueOrDefault(entered.Label) + 1;
            if (CentroidTracker.IsPresence(entered.Label)) Visits++;
        }
    }

    private readonly Dictionary<string, RegularDto> _regularByTrack = new(StringComparer.Ordinal);
    private readonly List<NamingPrompt> _prompts = [];

    /// <summary>New people and pets waiting (passively) for a name; ignored ones expire.</summary>
    public IReadOnlyList<NamingPrompt> Prompts => _prompts;

    public RegularDto? RegularFor(string trackId) => _regularByTrack.GetValueOrDefault(trackId);

    public void AssignRegular(string trackId, RegularDto regular) => _regularByTrack[trackId] = regular;

    public void ForgetTrack(string trackId) => _regularByTrack.Remove(trackId);

    public void AddPrompt(NamingPrompt prompt)
    {
        _prompts.RemoveAll(p => p.Regular.Id == prompt.Regular.Id);
        _prompts.Add(prompt);
    }

    public void DismissPrompt(string regularId) => _prompts.RemoveAll(p => p.Regular.Id == regularId);

    /// <summary>Drops prompts nobody answered; they stay unnamed ("Person N").</summary>
    public bool ExpirePrompts(DateTimeOffset nowUtc) => _prompts.RemoveAll(p => nowUtc - p.SeenUtc > NamingPrompt.Lifetime) > 0;

    /// <summary>A name was given: every live track of that regular shows it straight away.</summary>
    public void Rename(RegularDto regular)
    {
        foreach (var trackId in _regularByTrack.Where(kv => kv.Value.Id == regular.Id).Select(kv => kv.Key).ToList())
            _regularByTrack[trackId] = regular;
        _prompts.RemoveAll(p => p.Regular.Id == regular.Id);
    }

    public void RecordCaption(ParsedCaption caption, DateTimeOffset atUtc)
    {
        LastCaption = caption.Text;
        LastCaptionUtc = atUtc;
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
/// "New person spotted — name them?" A passive offer: it waits for <see cref="Lifetime"/> and then
/// quietly goes away, leaving the regular unnamed. The snapshot is a local data URL shown only here.
/// </summary>
public sealed record NamingPrompt(RegularDto Regular, string? SnapshotDataUrl, DateTimeOffset SeenUtc)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
}

/// <summary>
/// What the recap card shows: the session, how long the tab was away (null when the session just
/// stopped), and the long-exposure PNG data URL (null when nobody moved).
/// </summary>
public sealed record SessionRecap(SessionDto Session, TimeSpan? AwayFor, string? Exposure);
