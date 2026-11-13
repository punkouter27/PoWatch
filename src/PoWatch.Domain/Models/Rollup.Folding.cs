using System.Numerics.Tensors;

namespace PoWatch.Domain.Models;

/// <summary>Building a rollup from a tick or an event, and merging two.</summary>
public sealed partial record Rollup
{
    public static Rollup FromTick(Tick tick)
    {
        ArgumentNullException.ThrowIfNull(tick);

        var presenceCount = tick.Classes
            .Where(kv => EntityClasses.IsPresence(kv.Key))
            .Sum(kv => kv.Value.Max);

        return new Rollup
        {
            BucketStartUtc = tick.StartUtc,
            Ticks = 1,
            Seconds = tick.DurationSeconds,
            PixelSamples = tick.PixelSamples,
            DetectorSamples = tick.DetectorSamples,
            OccupiedTicks = presenceCount > 0 ? 1 : 0,
            PeakConcurrency = presenceCount,
            Motion = RunningStat.Of(tick.MotionMean),
            MotionPeak = RunningStat.Of(tick.MotionMax),
            Luminance = RunningStat.Of(tick.LuminanceMean),
            Grid = tick.MotionGrid.Count == SpatialGrid.Cells ? tick.MotionGrid.ToArray() : ReadOnlyMemory<float>.Empty,
            PresenceGrid = tick.PresenceGrid.Count == SpatialGrid.Cells ? tick.PresenceGrid.ToArray() : ReadOnlyMemory<float>.Empty,
            SittingSeconds = tick.SittingSeconds,
            StandingSeconds = tick.StandingSeconds,
            LyingSeconds = tick.LyingSeconds,
            Classes = tick.Classes
                .Where(kv => kv.Value.Max > 0)
                .ToDictionary(kv => kv.Key, kv => new ClassTotals(1, kv.Value.Max, kv.Value.Mean), StringComparer.Ordinal),
            Palette = tick.Palette
                .GroupBy(QuantizeColor)
                .ToDictionary(g => g.Key, g => (long)g.Count())
        };
    }

    /// <summary>
    /// Folds a scene event into a rollup: people and animals entering count as visits, exits carry
    /// their dwell time, and both record which edge of the frame they used. Other events carry no
    /// counters and fold in as <see cref="Empty"/> apart from their timestamp.
    /// </summary>
    public static Rollup FromEvent(SceneEvent sceneEvent)
    {
        ArgumentNullException.ThrowIfNull(sceneEvent);
        var rollup = new Rollup { BucketStartUtc = sceneEvent.AtUtc };

        if (sceneEvent.Kind == SceneEventKind.Caption)
            return rollup with { Captions = 1 };

        if (sceneEvent.Class is null || !EntityClasses.IsPresence(sceneEvent.Class))
            return rollup;

        return sceneEvent.Kind switch
        {
            SceneEventKind.TrackEnter => rollup with
            {
                Visits = 1,
                Entries = new Dictionary<FrameEdge, long> { [sceneEvent.Edge] = 1 }
            },
            SceneEventKind.TrackExit => rollup with
            {
                Exits = new Dictionary<FrameEdge, long> { [sceneEvent.Edge] = 1 },
                DwellHistogram = sceneEvent.DwellSeconds is { } dwell
                    ? new Dictionary<int, long> { [DwellBin(dwell)] = 1 }
                    : new Dictionary<int, long>(),
                DwellMaxSeconds = sceneEvent.DwellSeconds ?? 0,
                Regulars = sceneEvent.RegularId is { } regular
                    ? new Dictionary<string, RegularTotals> { [regular] = new(1, sceneEvent.DwellSeconds ?? 0) }
                    : new Dictionary<string, RegularTotals>()
            },
            _ => rollup
        };
    }

    /// <summary>Quarter-octave bin for a dwell time: four bins per doubling, about 19% wide.</summary>
    public static int DwellBin(double seconds) => (int)Math.Floor(4 * Math.Log2(Math.Max(1, seconds)));

    /// <summary>The geometric centre of a <see cref="DwellBin"/>, in seconds.</summary>
    public static double DwellBinCentre(int bin) => Math.Pow(2, (bin + 0.5) / 4);

    public static Rollup Merge(Rollup a, Rollup b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        return new Rollup
        {
            BucketStartUtc = a.BucketStartUtc < b.BucketStartUtc ? a.BucketStartUtc : b.BucketStartUtc,
            Ticks = a.Ticks + b.Ticks,
            Seconds = a.Seconds + b.Seconds,
            PixelSamples = a.PixelSamples + b.PixelSamples,
            DetectorSamples = a.DetectorSamples + b.DetectorSamples,
            OccupiedTicks = a.OccupiedTicks + b.OccupiedTicks,
            PeakConcurrency = Math.Max(a.PeakConcurrency, b.PeakConcurrency),
            Motion = a.Motion.Merge(b.Motion),
            MotionPeak = a.MotionPeak.Merge(b.MotionPeak),
            Luminance = a.Luminance.Merge(b.Luminance),
            Grid = MergeGrids(a.Grid, b.Grid),
            PresenceGrid = MergeGrids(a.PresenceGrid, b.PresenceGrid),
            SittingSeconds = a.SittingSeconds + b.SittingSeconds,
            StandingSeconds = a.StandingSeconds + b.StandingSeconds,
            LyingSeconds = a.LyingSeconds + b.LyingSeconds,
            Classes = MergeMaps(a.Classes, b.Classes, (x, y) => x.Merge(y)),
            Palette = MergeMaps(a.Palette, b.Palette, (x, y) => x + y),
            Visits = a.Visits + b.Visits,
            Captions = a.Captions + b.Captions,
            DwellHistogram = MergeMaps(a.DwellHistogram, b.DwellHistogram, (x, y) => x + y),
            DwellMaxSeconds = Math.Max(a.DwellMaxSeconds, b.DwellMaxSeconds),
            Entries = MergeMaps(a.Entries, b.Entries, (x, y) => x + y),
            Exits = MergeMaps(a.Exits, b.Exits, (x, y) => x + y),
            Regulars = MergeMaps(a.Regulars, b.Regulars, (x, y) => x.Merge(y))
        };
    }

    /// <summary>Folds an 0xRRGGBB colour into a 12-bit 0xRGB bin (top four bits per channel).</summary>
    public static int QuantizeColor(int rgb) =>
        (((rgb >> 20) & 0xF) << 8) | (((rgb >> 12) & 0xF) << 4) | ((rgb >> 4) & 0xF);

    private static ReadOnlyMemory<float> MergeGrids(ReadOnlyMemory<float> a, ReadOnlyMemory<float> b)
    {
        if (a.IsEmpty) return b;
        if (b.IsEmpty) return a;

        var sum = new float[SpatialGrid.Cells];
        TensorPrimitives.Add(a.Span, b.Span, sum);
        return sum;
    }

    private static Dictionary<TKey, TValue> MergeMaps<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue> a,
        IReadOnlyDictionary<TKey, TValue> b,
        Func<TValue, TValue, TValue> merge)
        where TKey : notnull
    {
        var result = new Dictionary<TKey, TValue>(a);
        foreach (var (key, value) in b)
            result[key] = result.TryGetValue(key, out var existing) ? merge(existing, value) : value;
        return result;
    }
}
