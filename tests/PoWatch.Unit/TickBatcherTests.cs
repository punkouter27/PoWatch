using Microsoft.Extensions.Time.Testing;
using PoWatch.Shared.Services;

namespace PoWatch.Unit;

public sealed class TickBatcherTests
{
    // 14:00:03 UTC — mid-window, so the first tick window is [14:00:00, 14:00:10).
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 14, 0, 3, TimeSpan.Zero);

    private static float[] Grid(int hotCell, float value)
    {
        var grid = new float[144];
        grid[hotCell] = value;
        return grid;
    }

    [Fact]
    public void Samples_fold_into_one_aligned_ten_second_tick_with_its_events()
    {
        var time = new FakeTimeProvider(T0);
        var batcher = new TickBatcher(time);
        var tracker = new CentroidTracker();

        batcher.AddPixel(new PixelSample(T0, Motion: 0.1, Luminance: 0.4, Grid(7, 0.2f), [0xFF8000, 0x101010]));
        batcher.AddPixel(new PixelSample(T0.AddSeconds(2), Motion: 0.3, Luminance: 0.6, Grid(7, 0.6f), [0xFF8000]));
        batcher.AddDetections(T0.AddSeconds(1), tracker.Update(T0.AddSeconds(1), [new("person", 0.9, 0.0, 0.3, 0.1, 0.8)]));
        batcher.AddDetections(T0.AddSeconds(2), tracker.Update(T0.AddSeconds(2),
            [new("person", 0.9, 0.02, 0.3, 0.12, 0.8), new("person", 0.9, 0.6, 0.3, 0.7, 0.8), new("cup", 0.9, 0.4, 0.5, 0.45, 0.55)]));
        batcher.AddCaption(T0.AddSeconds(4), "Two people chat by the window.");

        // Nothing is ready until the window has closed.
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Null(batcher.Flush());

        time.Advance(TimeSpan.FromSeconds(3)); // 14:00:11
        var batch = batcher.Flush();
        Assert.NotNull(batch);
        Assert.NotEqual(Guid.Empty, batch!.BatchKey);

        var tick = Assert.Single(batch.Ticks);
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 14, 0, 0, TimeSpan.Zero), tick.StartUtc);
        // The session began 3 s into the window, so the tick covers 7 s of observation.
        Assert.Equal(7, tick.DurationSeconds, 9);
        Assert.Equal(2, tick.PixelSamples);
        Assert.Equal(2, tick.DetectorSamples);
        Assert.Equal(0.2, tick.MotionMean, 12);
        Assert.Equal(0.3, tick.MotionMax, 12);
        Assert.Equal(0.5, tick.LuminanceMean, 12);
        Assert.Equal(0.4f, tick.MotionGrid[7], 5);
        Assert.Equal(0xFF8000, tick.Palette[0]);
        // Person: peak 2 at once, mean over the two detector frames (1 then 2) = 1.5. Cup: 1 then 0.
        Assert.Equal(2, tick.Classes["person"].Max);
        Assert.Equal(1.5, tick.Classes["person"].Mean, 12);
        Assert.Equal(0.5, tick.Classes["cup"].Mean, 12);
        Assert.Equal(3, tick.PresenceGrid.Sum(), 5);
        Assert.Equal(3, tick.ActiveTrackIds.Count);

        Assert.Equal(3, batch.Events.Count(e => e.Kind == "TrackEnter"));
        Assert.Contains(batch.Events, e => e is { Kind: "TrackEnter", Class: "person", Edge: "Left" });
        Assert.Contains(batch.Events, e => e is { Kind: "Caption", Text: "Two people chat by the window." });

        // Nothing new in the next window: no empty batch.
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Null(batcher.Flush());
    }

    [Fact]
    public void Loudness_folds_into_the_tick_and_words_and_sounds_become_events()
    {
        var time = new FakeTimeProvider(T0);
        var batcher = new TickBatcher(time);
        batcher.AddPixel(new PixelSample(T0, Motion: 0.1, Luminance: 0.4, Grid(7, 0.2f), []));
        batcher.AddSound(T0, mean: 0.2, max: 0.3);
        batcher.AddSound(T0.AddSeconds(1), mean: 0.4, max: 0.9);
        batcher.AddSpeech(T0.AddSeconds(1), "Hello there.");
        batcher.AddSoundEvent(T0.AddSeconds(2), "Dog", 1.4);

        time.Advance(TimeSpan.FromSeconds(10));
        var batch = batcher.Flush()!;
        var tick = Assert.Single(batch.Ticks);
        Assert.Equal(0.3, tick.SoundMean, 6);
        Assert.Equal(0.9, tick.SoundMax);
        Assert.Contains(batch.Events, e => e.Kind == "Speech" && e.Text == "Hello there.");
        Assert.Contains(batch.Events, e => e.Kind == "Sound" && e.Text == "Dog" && e.Score == 1);
    }

    [Fact]
    public void Each_closed_window_is_handed_out_once_under_its_own_key()
    {
        var time = new FakeTimeProvider(T0);
        var batcher = new TickBatcher(time);

        var keys = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            batcher.AddPixel(new PixelSample(time.GetUtcNow(), 0.1, 0.5, [], []));
            time.Advance(TimeSpan.FromSeconds(10));
            var batch = batcher.Flush()!;
            Assert.Single(batch.Ticks);
            keys.Add(batch.BatchKey);
            // The outbox keeps it from here: flushing again hands out nothing new.
            Assert.Null(batcher.Flush());
        }
        Assert.Equal(5, keys.Distinct().Count());

        // Stopping closes a partial window early: :50 to :57 is 7 s.
        batcher.AddPixel(new PixelSample(time.GetUtcNow(), 0.2, 0.5, [], []));
        time.Advance(TimeSpan.FromSeconds(4));
        var final = batcher.Flush(final: true);
        Assert.Equal(7, Assert.Single(final!.Ticks).DurationSeconds, 9);
    }

    [Fact]
    public void Phone_sensor_readings_average_into_the_tick_validate_and_reach_the_story()
    {
        var time = new FakeTimeProvider(T0);
        var batcher = new TickBatcher(time);
        batcher.AddEnvironment(T0, lux: 100, pressureHpa: 1012, movedSeconds: 0);
        batcher.AddEnvironment(T0.AddSeconds(2), lux: 300, pressureHpa: null, movedSeconds: 1.5);
        batcher.AddEnvironment(T0.AddSeconds(3), lux: double.NaN, pressureHpa: 1014, movedSeconds: 0.5);
        time.Advance(TimeSpan.FromSeconds(8));

        var tick = Assert.Single(batcher.Flush()!.Ticks);
        Assert.Equal(200, tick.AmbientLux!.Value, 9);        // the NaN reading is ignored
        Assert.Equal(1013, tick.PressureHpa!.Value, 9);      // a missing reading doesn't count as zero
        Assert.Equal(2, tick.DeviceMovedSeconds, 9);

        // A browser tick has no sensors: nulls, not zeros.
        var browser = new TickBatcher(new FakeTimeProvider(T0));
        browser.AddPixel(new PixelSample(T0, 0.1, 0.5, [], []));
        var none = Assert.Single(browser.Flush(final: true)!.Ticks);
        Assert.Null(none.AmbientLux);
        Assert.Null(none.PressureHpa);

        // The domain rejects impossible readings.
        var session = Guid.NewGuid();
        Domain.Models.Tick Tick(int minute, double? lux, double? hpa, double moved) => new()
        {
            SessionId = session,
            StartUtc = T0.AddMinutes(minute),
            DurationSeconds = 10,
            AmbientLux = lux,
            PressureHpa = hpa,
            DeviceMovedSeconds = moved
        };
        Assert.Contains(Tick(0, -1, null, 0).Validate(T0), e => e.Contains("AmbientLux", StringComparison.Ordinal));
        Assert.Contains(Tick(0, null, 50, 0).Validate(T0), e => e.Contains("PressureHpa", StringComparison.Ordinal));
        Assert.Contains(Tick(0, null, null, 11).Validate(T0), e => e.Contains("DeviceMovedSeconds", StringComparison.Ordinal));
        Assert.Empty(Tick(0, 250, 1013, 3).Validate(T0));

        // The story mentions the light range, a 4 hPa fall, and the camera moving.
        List<Domain.Models.Tick> day = [Tick(0, 50, 1015, 0), Tick(1, 800, 1014, 4), Tick(2, 400, 1011, 0)];
        var story = Application.Features.Stories.SceneNarrator.Atmosphere(day, [], TimeZoneInfo.Utc, new Dictionary<string, string>())!;
        Assert.Contains("ranged from 50 to 800 lux", story, StringComparison.Ordinal);
        Assert.Contains("Air pressure fell 4 hPa", story, StringComparison.Ordinal);
        Assert.Contains("The camera itself was moved at", story, StringComparison.Ordinal);
    }
}
