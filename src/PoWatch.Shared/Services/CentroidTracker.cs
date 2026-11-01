namespace PoWatch.Shared.Services;

/// <summary>One detector box, with coordinates normalised to [0, 1] of the frame, and optionally its colour signature.</summary>
public sealed record Detection(string Label, double Score, double X0, double Y0, double X1, double Y1, IReadOnlyList<float>? Signature = null)
{
    public double CentreX => (X0 + X1) / 2;
    public double CentreY => (Y0 + Y1) / 2;
}

public sealed record TrackState(string TrackId, string Label, Detection Box, DateTimeOffset FirstSeenUtc, DateTimeOffset LastSeenUtc)
{
    /// <summary>The track's look, averaged over its sightings (empty until the detector sends one).</summary>
    public IReadOnlyList<float> Signature { get; init; } = [];

    public TimeSpan Age => LastSeenUtc - FirstSeenUtc;
}

public sealed record TrackEntered(string TrackId, string Label, string Edge, DateTimeOffset AtUtc);

public sealed record TrackExited(string TrackId, string Label, string Edge, DateTimeOffset AtUtc, double DwellSeconds);

/// <summary>What changed on one detector frame.</summary>
public sealed record TrackerFrame(
    IReadOnlyList<TrackState> Active,
    IReadOnlyList<TrackEntered> Entered,
    IReadOnlyList<TrackExited> Exited,
    IReadOnlyList<int> PresenceCells,
    IReadOnlyDictionary<string, int> CountsByLabel);

/// <summary>
/// Turns per-frame detections into tracks that persist across frames. Matching is per class:
/// best IoU first, then nearest centre within reach. A track that goes unseen for longer than the
/// gap is closed with its dwell time and the frame edge it was nearest when last seen, so brief
/// occlusions do not split one visitor into two.
/// </summary>
public sealed class CentroidTracker(TimeSpan? gap = null)
{
    public const double MinScore = 0.5;
    public const double MinIou = 0.3;
    public const double MaxCentreJump = 0.15;

    /// <summary>Within this distance of a side, a track is said to have used that edge.</summary>
    public const double EdgeBand = 0.2;

    private const int Columns = 16;
    private const int Rows = 9;

    private static readonly HashSet<string> PresenceLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "person", "cat", "dog", "bird", "horse", "sheep", "cow", "elephant", "bear", "zebra", "giraffe"
    };

    private readonly TimeSpan _gap = gap ?? TimeSpan.FromSeconds(3);
    private readonly Dictionary<string, TrackState> _tracks = [];
    private int _nextId = 1;

    public IReadOnlyCollection<TrackState> Tracks => _tracks.Values;

    public TrackerFrame Update(DateTimeOffset atUtc, IReadOnlyList<Detection> detections)
    {
        ArgumentNullException.ThrowIfNull(detections);
        var confident = detections.Where(d => d.Score >= MinScore).ToList();
        var entered = new List<TrackEntered>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var byLabel in confident.GroupBy(d => d.Label, StringComparer.OrdinalIgnoreCase))
        {
            var candidates = _tracks.Values
                .Where(t => string.Equals(t.Label, byLabel.Key, StringComparison.OrdinalIgnoreCase) && !seen.Contains(t.TrackId))
                .ToList();

            // Greedy: strongest overlaps claim their tracks first.
            var pairs = (from d in byLabel
                         from t in candidates
                         let iou = Iou(d, t.Box)
                         let jump = Distance(d, t.Box)
                         where iou >= MinIou || jump <= MaxCentreJump
                         orderby iou descending, jump
                         select (Detection: d, Track: t)).ToList();

            var claimed = new HashSet<Detection>(ReferenceEqualityComparer.Instance);
            foreach (var (detection, track) in pairs)
            {
                if (claimed.Contains(detection) || seen.Contains(track.TrackId)) continue;
                claimed.Add(detection);
                seen.Add(track.TrackId);
                _tracks[track.TrackId] = track with { Box = detection, LastSeenUtc = atUtc, Signature = Average(track.Signature, detection.Signature) };
            }

            foreach (var detection in byLabel.Where(d => !claimed.Contains(d)))
            {
                var id = $"T{_nextId++}";
                _tracks[id] = new TrackState(id, detection.Label, detection, atUtc, atUtc) { Signature = detection.Signature ?? [] };
                seen.Add(id);
                entered.Add(new TrackEntered(id, detection.Label, EdgeOf(detection), atUtc));
            }
        }

        var exited = _tracks.Values
            .Where(t => !seen.Contains(t.TrackId) && atUtc - t.LastSeenUtc > _gap)
            .Select(t => new TrackExited(t.TrackId, t.Label, EdgeOf(t.Box), atUtc, (t.LastSeenUtc - t.FirstSeenUtc).TotalSeconds))
            .ToList();
        foreach (var exit in exited) _tracks.Remove(exit.TrackId);

        var active = _tracks.Values.Where(t => seen.Contains(t.TrackId)).OrderBy(t => t.TrackId, StringComparer.Ordinal).ToList();

        return new TrackerFrame(
            active,
            entered,
            exited,
            active.Where(t => PresenceLabels.Contains(t.Label)).Select(t => Cell(t.Box)).ToList(),
            active.GroupBy(t => t.Label, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase));
    }

    public static bool IsPresence(string label) => PresenceLabels.Contains(label);

    /// <summary>The side of the frame a box is nearest, or "None" when it is well inside.</summary>
    public static string EdgeOf(Detection box)
    {
        ArgumentNullException.ThrowIfNull(box);
        var distances = new (string Edge, double Distance)[]
        {
            ("Left", box.X0), ("Right", 1 - box.X1), ("Top", box.Y0), ("Bottom", 1 - box.Y1)
        };
        var nearest = distances.MinBy(d => d.Distance);
        return nearest.Distance <= EdgeBand ? nearest.Edge : "None";
    }

    /// <summary>Exponential average (30% new) so one odd frame cannot redefine a track's look.</summary>
    private static IReadOnlyList<float> Average(IReadOnlyList<float> current, IReadOnlyList<float>? incoming)
    {
        if (incoming is null || incoming.Count == 0) return current;
        if (current.Count != incoming.Count) return incoming;
        return current.Select((v, i) => (0.7f * v) + (0.3f * incoming[i])).ToArray();
    }

    private static int Cell(Detection box)
    {
        var column = Math.Clamp((int)(box.CentreX * Columns), 0, Columns - 1);
        var row = Math.Clamp((int)(box.CentreY * Rows), 0, Rows - 1);
        return row * Columns + column;
    }

    private static double Iou(Detection a, Detection b)
    {
        var ix = Math.Max(0, Math.Min(a.X1, b.X1) - Math.Max(a.X0, b.X0));
        var iy = Math.Max(0, Math.Min(a.Y1, b.Y1) - Math.Max(a.Y0, b.Y0));
        var intersection = ix * iy;
        var union = ((a.X1 - a.X0) * (a.Y1 - a.Y0)) + ((b.X1 - b.X0) * (b.Y1 - b.Y0)) - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    private static double Distance(Detection a, Detection b) =>
        Math.Sqrt(Math.Pow(a.CentreX - b.CentreX, 2) + Math.Pow(a.CentreY - b.CentreY, 2));
}
