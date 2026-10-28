using CsCheck;
using PoWatch.Domain.Models;

namespace PoWatch.Unit;

public sealed class RollupTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 21, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo UtcMinus5 =
        TimeZoneInfo.CreateCustomTimeZone("Test/UTC-5", TimeSpan.FromHours(-5), "UTC-5", "UTC-5");

    private static readonly Gen<Tick> GenTick =
        from motion in Gen.Double[0, 1]
        from luminance in Gen.Double[0, 1]
        from grid in Gen.Float[0f, 1f].Array[SpatialGrid.Cells]
        from people in Gen.Int[0, 3]
        from cats in Gen.Int[0, 2]
        from color in Gen.Int[0, 0xFFFFFF]
        from offset in Gen.Int[0, 100_000]
        select new Tick
        {
            SessionId = Guid.NewGuid(),
            StartUtc = T0.AddSeconds(offset * 10),
            PixelSamples = 40,
            DetectorSamples = 10,
            MotionMean = motion,
            MotionMax = motion,
            LuminanceMean = luminance,
            Palette = [color],
            MotionGrid = grid,
            PresenceGrid = grid,
            Classes = new Dictionary<string, ClassCount>
            {
                ["person"] = new(people, people),
                ["cat"] = new(cats, cats / 2.0)
            }
        };

    private static readonly Gen<SceneEvent> GenEvent =
        from kind in Gen.OneOf<SceneEventKind>(Gen.Const(SceneEventKind.TrackEnter), Gen.Const(SceneEventKind.TrackExit), Gen.Const(SceneEventKind.Caption))
        from edge in Gen.Enum<FrameEdge>()
        from dwell in Gen.Double[0, 5_000]
        from offset in Gen.Int[0, 100_000]
        select new SceneEvent
        {
            SessionId = Guid.NewGuid(),
            AtUtc = T0.AddSeconds(offset * 10),
            Kind = kind,
            TrackId = "T1",
            Class = "person",
            RegularId = offset % 2 == 0 ? "r1" : "r2",
            Edge = edge,
            Text = "caption",
            DwellSeconds = dwell
        };

    private static readonly Gen<Rollup> GenRollup =
        Gen.Select(GenTick.Array[1, 4], GenEvent.Array[0, 3]).Select(t =>
            t.Item1.Select(Rollup.FromTick).Concat(t.Item2.Select(Rollup.FromEvent)).Aggregate(Rollup.Merge));

    [Fact]
    public void Merging_is_associative_and_commutative_with_empty_as_identity()
    {
        Gen.Select(GenRollup, GenRollup, GenRollup).Sample(t =>
        {
            var (a, b, c) = t;
            return Equivalent(Rollup.Merge(Rollup.Merge(a, b), c), Rollup.Merge(a, Rollup.Merge(b, c)))
                && Equivalent(Rollup.Merge(a, b), Rollup.Merge(b, a))
                && Equivalent(Rollup.Merge(a, Rollup.Empty), a)
                && Equivalent(Rollup.Merge(Rollup.Empty, a), a);
        }, iter: 10_000);
    }

    [Fact]
    public void A_tick_folds_into_a_rollup_and_buckets_by_local_time()
    {
        var grid = new float[SpatialGrid.Cells];
        grid[17] = 0.5f;
        var tick = new Tick
        {
            SessionId = Guid.NewGuid(),
            StartUtc = T0,
            DurationSeconds = 10,
            PixelSamples = 48,
            DetectorSamples = 9,
            MotionMean = 0.1,
            MotionMax = 0.4,
            LuminanceMean = 0.6,
            Palette = [0xFF8000, 0x0F0F0F],
            MotionGrid = grid,
            Classes = new Dictionary<string, ClassCount>
            {
                ["person"] = new(2, 1.5),
                ["cup"] = new(3, 3)
            }
        };

        var rollup = Rollup.FromTick(tick);

        Assert.Equal(1, rollup.Ticks);
        Assert.Equal(10, rollup.Seconds);
        Assert.Equal(1, rollup.OccupiedTicks);
        Assert.Equal(2, rollup.PeakConcurrency);
        Assert.Equal(0.4, rollup.MotionPeak.Max);
        Assert.Equal(0.6, rollup.Luminance.Mean);
        Assert.Equal(0.5f, rollup.Grid.Span[17]);
        Assert.Equal(1, rollup.Classes["cup"].TicksPresent);
        Assert.Equal(1, rollup.Palette[0xF80]);

        // A tick with only objects in it is not "occupied".
        var objectsOnly = Rollup.FromTick(tick with { Classes = new Dictionary<string, ClassCount> { ["cup"] = new(1, 1) } });
        Assert.Equal(0, objectsOnly.OccupiedTicks);

        var merged = Rollup.Merge(rollup, objectsOnly);
        Assert.Equal(2, merged.Ticks);
        Assert.Equal(1, merged.OccupiedTicks);
        Assert.Equal(0.1, merged.Motion.Mean, 12);

        // A caption is counted (for Wordsmith) and nothing else.
        var caption = Rollup.FromEvent(new SceneEvent { SessionId = tick.SessionId, AtUtc = T0, Kind = SceneEventKind.Caption, Text = "A cat naps." });
        Assert.Equal(1, Rollup.Merge(merged, caption).Captions);
        Assert.Equal(0, caption.Visits);

        // 21:00Z is 16:00 at UTC-5; the day bucket starts at local midnight.
        var at = new DateTimeOffset(2026, 9, 25, 3, 28, 42, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 3, 28, 0, TimeSpan.Zero), RollupBuckets.StartUtc(RollupGrain.Minute, at, UtcMinus5));
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 3, 0, 0, TimeSpan.Zero), RollupBuckets.StartUtc(RollupGrain.Hour, at, UtcMinus5));
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 5, 0, 0, TimeSpan.Zero), RollupBuckets.StartUtc(RollupGrain.Day, at, UtcMinus5));
    }

    private static bool Equivalent(Rollup x, Rollup y)
    {
        static bool Close(double a, double b) =>
            (double.IsInfinity(a) && a.Equals(b)) || Math.Abs(a - b) <= 1e-9 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));
        static bool Stat(RunningStat a, RunningStat b) =>
            a.Count == b.Count && Close(a.Sum, b.Sum) && Close(a.SumSquares, b.SumSquares) && Close(a.Min, b.Min) && Close(a.Max, b.Max);

        return x.BucketStartUtc == y.BucketStartUtc
            && x.Ticks == y.Ticks && Close(x.Seconds, y.Seconds)
            && x.PixelSamples == y.PixelSamples && x.DetectorSamples == y.DetectorSamples
            && x.OccupiedTicks == y.OccupiedTicks && x.PeakConcurrency == y.PeakConcurrency
            && Stat(x.Motion, y.Motion) && Stat(x.MotionPeak, y.MotionPeak) && Stat(x.Luminance, y.Luminance)
            && x.Grid.Length == y.Grid.Length
            && x.Grid.Span.ToArray().Zip(y.Grid.Span.ToArray()).All(p => Math.Abs(p.First - p.Second) <= 1e-3f)
            && x.Classes.Count == y.Classes.Count
            && x.Classes.All(kv => y.Classes.TryGetValue(kv.Key, out var o)
                && o.TicksPresent == kv.Value.TicksPresent && o.PeakCount == kv.Value.PeakCount && Close(o.MeanSum, kv.Value.MeanSum))
            && SameCounts(x.Palette, y.Palette)
            && x.Visits == y.Visits && x.Captions == y.Captions && Close(x.DwellMaxSeconds, y.DwellMaxSeconds)
            && SameCounts(x.DwellHistogram, y.DwellHistogram)
            && SameCounts(x.Entries, y.Entries) && SameCounts(x.Exits, y.Exits)
            && x.PresenceGrid.Length == y.PresenceGrid.Length
            && x.Regulars.Count == y.Regulars.Count
            && x.Regulars.All(kv => y.Regulars.TryGetValue(kv.Key, out var o) && o.Visits == kv.Value.Visits && Close(o.DwellSeconds, kv.Value.DwellSeconds));
    }

    private static bool SameCounts<TKey>(IReadOnlyDictionary<TKey, long> x, IReadOnlyDictionary<TKey, long> y)
        where TKey : notnull
    {
        return x.Count == y.Count && x.All(kv => y.TryGetValue(kv.Key, out var o) && o == kv.Value);
    }
}
