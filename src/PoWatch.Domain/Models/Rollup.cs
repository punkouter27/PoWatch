namespace PoWatch.Domain.Models;

/// <summary>
/// A mergeable summary of any number of ticks. Merge is associative and commutative with
/// <see cref="Empty"/> as identity, so minute, hour, day and all-time buckets can be built in any
/// order and replayed batches can be folded in without re-reading raw ticks.
/// </summary>
public sealed partial record Rollup
{
    public static readonly Rollup Empty = new();

    /// <summary>Earliest tick start folded in; <see cref="DateTimeOffset.MaxValue"/> when empty.</summary>
    public DateTimeOffset BucketStartUtc { get; init; } = DateTimeOffset.MaxValue;
    public long Ticks { get; init; }
    public double Seconds { get; init; }
    public long PixelSamples { get; init; }
    public long DetectorSamples { get; init; }

    /// <summary>Ticks in which at least one person or animal was present.</summary>
    public long OccupiedTicks { get; init; }

    /// <summary>Most people and animals seen at once in any single tick.</summary>
    public int PeakConcurrency { get; init; }

    /// <summary>Per-tick mean motion.</summary>
    public RunningStat Motion { get; init; } = RunningStat.Empty;

    /// <summary>Per-tick peak motion.</summary>
    public RunningStat MotionPeak { get; init; } = RunningStat.Empty;

    public RunningStat Luminance { get; init; } = RunningStat.Empty;

    /// <summary>Summed motion per <see cref="SpatialGrid"/> cell; empty until a tick with a grid arrives.</summary>
    public ReadOnlyMemory<float> Grid { get; init; } = ReadOnlyMemory<float>.Empty;

    /// <summary>Summed presence sightings per <see cref="SpatialGrid"/> cell — where people and animals hang out.</summary>
    public ReadOnlyMemory<float> PresenceGrid { get; init; } = ReadOnlyMemory<float>.Empty;

    /// <summary>Person-seconds spent sitting, standing and lying down, where posture was read.</summary>
    public double SittingSeconds { get; init; }

    public double StandingSeconds { get; init; }

    public double LyingSeconds { get; init; }

    public IReadOnlyDictionary<string, ClassTotals> Classes { get; init; } = new Dictionary<string, ClassTotals>();

    /// <summary>People and animals that entered the frame.</summary>
    public long Visits { get; init; }

    /// <summary>VLM captions folded in.</summary>
    public long Captions { get; init; }

    /// <summary>Dwell times keyed by <see cref="DwellBin"/> — a quarter-octave log scale, so percentiles merge.</summary>
    public IReadOnlyDictionary<int, long> DwellHistogram { get; init; } = new Dictionary<int, long>();

    public double DwellMaxSeconds { get; init; }

    public IReadOnlyDictionary<FrameEdge, long> Entries { get; init; } = new Dictionary<FrameEdge, long>();

    public IReadOnlyDictionary<FrameEdge, long> Exits { get; init; } = new Dictionary<FrameEdge, long>();

    /// <summary>Finished visits per regular id, for leaderboards over any range.</summary>
    public IReadOnlyDictionary<string, RegularTotals> Regulars { get; init; } = new Dictionary<string, RegularTotals>();

    /// <summary>Colour histogram keyed by 12-bit 0xRGB bins.</summary>
    public IReadOnlyDictionary<int, long> Palette { get; init; } = new Dictionary<int, long>();

    public double OccupancyFraction => Ticks == 0 ? 0 : (double)OccupiedTicks / Ticks;
}
