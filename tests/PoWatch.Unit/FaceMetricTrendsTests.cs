using PoWatch.Shared.Services;

namespace PoWatch.Unit;

/// <summary>Mood, smile, symmetry, proportions, blinks, face shape and heart rate trend the same way Beauty and Age do.</summary>
public sealed class FaceMetricTrendsTests
{
    private static readonly double[] Thirties = [0, 0, 0, 0.1, 0.7, 0.2, 0, 0, 0];
    private static readonly double[] SureFemale = [0.95, 0.05];

    private static FaceReadout Face(int slot = 1) => new(slot, "Person 1", null, null, 0, null, 0, 1);

    [Fact]
    public void Every_new_metric_gets_a_line_a_session_mean_and_a_range_and_unread_ones_stay_empty()
    {
        var trends = new FaceTrends();
        var happy = Face() with
        {
            Mood = 80,
            SmileScore = 60,
            Symmetry = 0.9,
            Golden = 0.8,
            Thirds = 0.7,
            Fifths = 0.6,
            BlinkRatePerMin = 12,
            FaceRatio = 1.3,
            FaceShape = "Oval",
            HeartRateBpm = 70,
            EmotionMix = [0.8, 0.2, 0, 0, 0, 0, 0],
        };
        var calm = happy with { Mood = 40, SmileScore = 20, HeartRateBpm = 80, FaceShape = "Round", EmotionMix = [0.2, 0.8, 0, 0, 0, 0, 0] };

        trends.Observe([happy], 0);
        trends.Observe([happy], FaceTrends.BinSeconds);
        trends.Observe([calm], FaceTrends.BinSeconds * 2);
        trends.Observe([], FaceTrends.BinSeconds * 3);

        Assert.Equal([80.0, 80.0, 40.0], trends.Mood.History);
        Assert.Equal(200.0 / 3, trends.Mood.Mean!.Value, 6);
        Assert.Equal((40.0, 80.0), trends.Mood.Range);
        Assert.Equal((20.0, 60.0), trends.Smile.Range);
        Assert.Equal(90, trends.Symmetry.Mean!.Value, 6);
        Assert.Equal(70, trends.Proportions.Mean!.Value, 6);
        Assert.Equal(12, trends.BlinkRate.Mean);
        Assert.Equal([1.3, 1.3, 1.3], trends.FaceRatio.History);
        Assert.Equal((70.0, 80.0), trends.HeartRate.Range);
        Assert.Equal(("Oval", 66.7), trends.CommonShape);
        Assert.Equal(("happy", 60.0), trends.EmotionShares[0]);
        Assert.NotEmpty(trends.Mood.Minutes.Points);

        // A metric no face had a reading for keeps no line, mean, shape or emotion mix.
        var sparse = new FaceTrends();
        sparse.Observe([Face() with { SmileScore = 50 }], 0);
        sparse.Observe([], FaceTrends.BinSeconds);

        Assert.Equal([50.0], sparse.Smile.History);
        Assert.Empty(sparse.HeartRate.History);
        Assert.Null(sparse.HeartRate.Mean);
        Assert.Null(sparse.CommonShape);
        Assert.Empty(sparse.EmotionShares);
    }

    [Fact]
    public void Emotion_readings_become_a_smoothed_mix_a_top_emotion_and_a_mood()
    {
        var faces = new FaceReadings();
        faces.OnLandmarks([1], new double[FaceReadings.MeshValues]);

        faces.OnReading(1, Thirties, SureFemale, [1, 0], [0.9, 0.1, 0, 0, 0, 0, 0]);
        var happy = Assert.Single(faces.InView);
        Assert.Equal("happy", happy.Emotion);
        Assert.Equal(0.9, happy.EmotionConfidence);
        Assert.Equal(95, happy.Mood);

        // One sad reading moves the mix only part of the way (EmotionSmoothing), so happy still leads.
        faces.OnReading(1, Thirties, SureFemale, [1, 0], [0, 0, 0, 1, 0, 0, 0]);
        var mixed = faces.InView[0];
        Assert.Equal("happy", mixed.Emotion);
        Assert.InRange(mixed.Mood!.Value, 50, 94);
        Assert.Equal(1.0, mixed.EmotionMix!.Sum(), 2);

        // A wrong-width emotion vector is ignored.
        var bad = new FaceReadings();
        bad.OnLandmarks([1], new double[FaceReadings.MeshValues]);
        bad.OnReading(1, Thirties, SureFemale, [1, 0], [1, 0]);
        Assert.Null(bad.InView[0].Mood);
    }

    [Fact]
    public void Pulse_finds_a_steady_heartbeat_in_skin_colour_but_not_in_noise_or_a_short_window()
    {
        var pulse = new PulseEstimator();
        var random = new Random(7);
        // 15 samples a second for 12 s; the heartbeat (72 bpm) tints green most, red a little, over noise and drift.
        for (var i = 0; i < 180; i++)
        {
            var t = i / 15.0;
            var beat = Math.Sin(2 * Math.PI * 1.2 * t);
            var drift = 2 * t / 12;
            pulse.Add(t, 150 + drift + (0.15 * beat) + Noise(random), 110 + drift + (0.5 * beat) + Noise(random), 90 + drift + Noise(random));
            if (i % 8 == 0) pulse.Estimate();
        }
        pulse.Estimate();

        Assert.NotNull(pulse.Bpm);
        Assert.InRange(pulse.Bpm!.Value, 70, 74);
        Assert.True(pulse.PeakShare >= PulseEstimator.MinPeakShare);

        var shortWindow = new PulseEstimator();
        for (var i = 0; i < 60; i++) shortWindow.Add(i / 15.0, 150, 110 + Math.Sin(2 * Math.PI * 1.2 * i / 15.0), 90);
        shortWindow.Estimate();
        Assert.Null(shortWindow.Bpm);

        var noise = new PulseEstimator();
        var noisy = new Random(3);
        for (var i = 0; i < 180; i++)
        {
            noise.Add(i / 15.0, 150 + (4 * Noise(noisy)), 110 + (4 * Noise(noisy)), 90 + (4 * Noise(noisy)));
            noise.Estimate();
        }
        Assert.Null(noise.Bpm);
    }

    private static double Noise(Random random) => (random.NextDouble() - 0.5) * 0.4;
}
