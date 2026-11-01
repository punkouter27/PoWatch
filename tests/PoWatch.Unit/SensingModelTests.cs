using PoWatch.Domain.Models;
using PoWatch.Shared.Services;

namespace PoWatch.Unit;

public sealed class SensingModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 21, 0, 0, TimeSpan.Zero);
    private static readonly Guid SessionId = Guid.Parse("7d4c3f0e-4b8a-4a57-9d0e-0f4e5b1c2a33");

    private static Tick ValidTick() => new()
    {
        SessionId = SessionId,
        StartUtc = Now.AddSeconds(-10),
        DurationSeconds = 10,
        PixelSamples = 48,
        DetectorSamples = 10,
        MotionMean = 0.04,
        MotionMax = 0.21,
        LuminanceMean = 0.55,
        Palette = [0x1E2A38, 0xC8B89A],
        MotionGrid = new float[SpatialGrid.Cells],
        Classes = new Dictionary<string, ClassCount> { ["person"] = new(Max: 2, Mean: 1.4) },
        ActiveTrackIds = ["T12", "T13"]
    };

    [Fact]
    public void A_tick_is_valid_only_inside_its_invariants()
    {
        Assert.Empty(ValidTick().Validate(Now));

        var bad = ValidTick() with
        {
            StartUtc = Now.AddMinutes(6),
            DurationSeconds = 11,
            PixelSamples = -1,
            MotionMean = 1.2,
            LuminanceMean = -0.1,
            MotionGrid = new float[10],
            Classes = new Dictionary<string, ClassCount> { ["cat"] = new(Max: -1, Mean: 0) }
        };

        var errors = bad.Validate(Now);

        Assert.Contains(errors, e => e.Contains("future", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains(nameof(Tick.DurationSeconds), StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains(nameof(Tick.PixelSamples), StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains(nameof(Tick.MotionMean), StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains(nameof(Tick.LuminanceMean), StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains(nameof(Tick.MotionGrid), StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("cat", StringComparison.Ordinal));
        Assert.Equal(Now.AddSeconds(-10), ValidTick().StartUtc);
    }

    [Fact]
    public void Scene_events_need_the_fields_their_kind_implies()
    {
        var enter = new SceneEvent { SessionId = SessionId, AtUtc = Now, Kind = SceneEventKind.TrackEnter, TrackId = "T12", Class = "person", Edge = FrameEdge.Left };
        var caption = new SceneEvent { SessionId = SessionId, AtUtc = Now, Kind = SceneEventKind.Caption, Text = "A cat naps on the sofa." };

        Assert.Empty(enter.Validate(Now));
        Assert.Empty(caption.Validate(Now));

        Assert.NotEmpty((enter with { TrackId = null }).Validate(Now));
        Assert.NotEmpty((caption with { Text = " " }).Validate(Now));
        Assert.NotEmpty((caption with { Text = new string('x', SceneEvent.MaxTextLength + 1) }).Validate(Now));
        Assert.NotEmpty((caption with { AtUtc = Now.AddMinutes(6) }).Validate(Now));

        // Replays of the same event map to the same id, so storage can dedupe them.
        Assert.Equal(enter.StableId(), (enter with { }).StableId());
        Assert.NotEqual(enter.StableId(), (enter with { TrackId = "T13" }).StableId());
    }

    [Fact]
    public void A_session_runs_forward_in_time_and_stops_once()
    {
        var session = Session.Start(SessionId, "user-1", Now, "America/New_York");

        Assert.True(session.IsRunning);
        Assert.Throws<ArgumentException>(() => Session.Start(SessionId, "user-1", Now, "Not/AZone"));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Stop(Now.AddSeconds(-1)));

        var stopped = session.Stop(Now.AddHours(2));

        Assert.False(stopped.IsRunning);
        Assert.Equal(TimeSpan.FromHours(2), stopped.Duration(Now.AddHours(5)));
        Assert.Equal(TimeSpan.FromHours(1), session.Duration(Now.AddHours(1)));
        Assert.Throws<InvalidOperationException>(() => stopped.Stop(Now.AddHours(3)));
    }

    [Fact]
    public void Synthetic_scene_repeats_a_walker_and_varies_which_animals_are_present()
    {
        var scene = new SyntheticScene();

        var start = scene.Next(Now);
        var later = scene.Next(Now.AddSeconds(20));
        var afterCatLeaves = scene.Next(Now.AddSeconds(35));

        Assert.Contains(start.Detections, detection => detection.Label == "person");
        Assert.Contains(start.Detections, detection => detection.Label == "dog");
        Assert.Contains(start.Detections, detection => detection.Label == "cat");
        Assert.Contains(later.Detections, detection => detection.Label == "person");
        Assert.DoesNotContain(later.Detections, detection => detection.Label == "dog");
        Assert.Contains(later.Detections, detection => detection.Label == "cat");
        Assert.DoesNotContain(afterCatLeaves.Detections, detection => detection.Label == "cat");
        Assert.Contains(afterCatLeaves.Detections, detection => detection.Label == "cup");
    }

    [Fact]
    public void A_voice_is_learned_matched_kept_through_a_merge_and_never_confused_with_another()
    {
        static float[] Voice(params (int Index, float Value)[] parts)
        {
            var v = new float[8];
            foreach (var (i, x) in parts) v[i] = x;
            return v;
        }
        var book = new VoiceBook();
        Assert.Null(book.Match(Voice((0, 1))));

        book.Learn("sam", Voice((0, 1), (1, 0.1f)));
        book.Learn("sam", Voice((0, 1), (2, 0.1f)));
        book.Learn("ana", Voice((5, 1)));
        Assert.Equal("sam", book.Match(Voice((0, 0.9f), (1, 0.05f))));
        Assert.Equal("ana", book.Match(Voice((5, 1), (6, 0.2f))));
        // Halfway between two voices is nobody's.
        Assert.Null(book.Match(Voice((0, 1), (5, 1))));

        book.Merge("sam", "ana");
        Assert.False(book.Knows("ana"));
        Assert.True(book.Knows("sam"));
        book.Merge("bob", "sam");
        Assert.True(book.Knows("bob"));
    }
}
