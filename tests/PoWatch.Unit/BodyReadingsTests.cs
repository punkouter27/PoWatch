using PoWatch.Shared.Services;

namespace PoWatch.Unit;

public sealed class BodyReadingsTests
{
    /// <summary>
    /// Someone standing square to the camera, arms hanging: image x, y in frame-width units (trunk 0.25
    /// long), world points derived from them in metres around the hips, every named joint seen.
    /// </summary>
    private static double[] Body(Action<Dictionary<int, (double X, double Y, double Z)>>? change = null, double shift = 0)
    {
        var image = new Dictionary<int, (double X, double Y, double Z)>
        {
            [BodyGeometry.Nose] = (0.5, 0.2, 0),
            [BodyGeometry.LeftEar] = (0.53, 0.2, 0),
            [BodyGeometry.RightEar] = (0.47, 0.2, 0),
            [BodyGeometry.LeftShoulder] = (0.58, 0.3, 0),
            [BodyGeometry.RightShoulder] = (0.42, 0.3, 0),
            [BodyGeometry.LeftElbow] = (0.6, 0.45, 0),
            [BodyGeometry.RightElbow] = (0.4, 0.45, 0),
            [BodyGeometry.LeftWrist] = (0.61, 0.62, 0),
            [BodyGeometry.RightWrist] = (0.39, 0.62, 0),
            [BodyGeometry.LeftHip] = (0.55, 0.55, 0),
            [BodyGeometry.RightHip] = (0.45, 0.55, 0),
            [BodyGeometry.LeftKnee] = (0.55, 0.75, 0),
            [BodyGeometry.RightKnee] = (0.45, 0.75, 0),
            [BodyGeometry.LeftAnkle] = (0.55, 0.95, 0),
            [BodyGeometry.RightAnkle] = (0.45, 0.95, 0),
        };
        change?.Invoke(image);
        var values = new double[BodyGeometry.ValuesPerBody];
        foreach (var (point, (x, y, z)) in image)
        {
            var at = point * BodyGeometry.ValuesPerPoint;
            values[at] = x + shift;
            values[at + 1] = y;
            values[at + 2] = 1;
            values[at + 3] = (x - 0.5) * 2;
            values[at + 4] = (y - 0.55) * 2;
            values[at + 5] = z;
        }
        return values;
    }

    [Fact]
    public void An_upright_body_facing_the_camera_is_whole_upright_and_open()
    {
        var pose = BodyGeometry.Read(Body());

        Assert.True(pose.UpperBody);
        Assert.True(pose.FullBody);
        Assert.False(pose.Slumped);
        Assert.Equal(BodyCues.None, pose.Cues);
        Assert.Equal(0, pose.ShoulderTilt!.Value, 3);
    }

    [Fact]
    public void A_body_without_hips_is_not_read()
    {
        var values = Body();
        values[(BodyGeometry.LeftHip * BodyGeometry.ValuesPerPoint) + 2] = 0;

        Assert.False(BodyGeometry.Read(values).UpperBody);
        Assert.False(BodyGeometry.Read([1, 2, 3]).UpperBody);
    }

    [Fact]
    public void A_head_pushed_towards_the_camera_is_slumped()
    {
        var pose = BodyGeometry.Read(Body(p =>
        {
            p[BodyGeometry.LeftEar] = p[BodyGeometry.LeftEar] with { Z = -0.15 };
            p[BodyGeometry.RightEar] = p[BodyGeometry.RightEar] with { Z = -0.15 };
        }));

        Assert.True(pose.Slumped);
    }

    [Fact]
    public void Shoulders_nearer_the_camera_than_the_hips_lean_in_without_slumping()
    {
        var pose = BodyGeometry.Read(Body(p =>
        {
            foreach (var point in new[] { BodyGeometry.LeftShoulder, BodyGeometry.RightShoulder, BodyGeometry.LeftEar, BodyGeometry.RightEar })
                p[point] = p[point] with { Z = -0.12 };
        }));

        Assert.Equal(BodyCues.LeaningIn, pose.Cues);
        Assert.False(pose.Slumped);
    }

    [Fact]
    public void Wrists_across_the_body_between_shoulders_and_hips_are_arms_crossed()
    {
        var pose = BodyGeometry.Read(Body(p =>
        {
            p[BodyGeometry.LeftWrist] = (0.45, 0.45, 0);
            p[BodyGeometry.RightWrist] = (0.55, 0.45, 0);
        }));

        Assert.True(pose.Cues.HasFlag(BodyCues.ArmsCrossed));
    }

    [Fact]
    public void A_wrist_at_the_nose_is_a_hand_to_the_face()
    {
        var pose = BodyGeometry.Read(Body(p => p[BodyGeometry.RightWrist] = (0.48, 0.24, 0)));

        Assert.Equal(BodyCues.HandToFace, pose.Cues);
    }

    [Fact]
    public void A_lower_left_shoulder_tilts_positive()
    {
        var pose = BodyGeometry.Read(Body(p => p[BodyGeometry.LeftShoulder] = (0.58, 0.32, 0)));

        Assert.True(pose.ShoulderTilt > 5);
    }

    [Theory]
    [InlineData(0, 0.6, 0, 0, true, false, false, BodyActivity.Walking)]
    [InlineData(1.2, 0, 1, 1, true, false, false, BodyActivity.Active)]
    [InlineData(0.3, 0, 1.0, 0, false, true, false, BodyActivity.Waving)]
    [InlineData(0.1, 0, 0.2, 0, false, true, true, BodyActivity.Stretching)]
    [InlineData(0.2, 0, 0.5, 0, false, false, false, BodyActivity.Gesturing)]
    [InlineData(0.01, 0.01, 0.01, 0.01, true, false, false, BodyActivity.Still)]
    public void Speeds_read_as_an_activity(double limbs, double hips, double wrists, double ankles, bool anklesSeen, bool handUp, bool bothUp, BodyActivity expected) =>
        Assert.Equal(expected, BodyReadings.Classify(new BodyReadings.Speeds(limbs, hips, wrists, ankles), anklesSeen, handUp, bothUp));

    [Fact]
    public void A_still_body_adds_upright_still_open_time_and_an_energy_point()
    {
        var readings = new BodyReadings();
        for (var i = 0; i <= 12; i++) readings.OnLandmarks([1], Body(), i * 0.5);

        var body = Assert.Single(readings.InView);
        Assert.Equal(1, body.Slot);
        Assert.Equal(BodyActivity.Still, body.Activity);
        Assert.Equal(6, readings.ActivitySeconds[BodyActivity.Still], 6);
        Assert.Equal(6, readings.UprightSeconds, 6);
        Assert.Equal(100, readings.PostureScore);
        Assert.Equal(6, readings.OpenSeconds, 6);
        Assert.Equal(0, Assert.Single(readings.EnergyHistory));
        Assert.True(readings.FullBodySeen);
    }

    [Fact]
    public void A_fast_hand_above_the_shoulder_waves_and_counts_as_left_arm_movement()
    {
        var readings = new BodyReadings();
        for (var i = 0; i <= 6; i++)
            readings.OnLandmarks([1], Body(p => p[BodyGeometry.LeftWrist] = (i % 2 == 0 ? 0.6 : 0.8, 0.2, 0)), i * 0.5);

        Assert.Equal(BodyActivity.Waving, Assert.Single(readings.InView).Activity);
        Assert.True(readings.ArmBalance > 0.9);
    }

    [Fact]
    public void Moving_across_the_frame_is_walking()
    {
        var readings = new BodyReadings();
        for (var i = 0; i <= 4; i++) readings.OnLandmarks([1], Body(shift: i * 0.1), i * 0.5);

        Assert.Equal(BodyActivity.Walking, Assert.Single(readings.InView).Activity);
    }

    [Fact]
    public void A_slump_is_timed_and_its_run_ends_when_the_back_straightens()
    {
        var slumped = Body(p =>
        {
            p[BodyGeometry.LeftEar] = p[BodyGeometry.LeftEar] with { Z = -0.2 };
            p[BodyGeometry.RightEar] = p[BodyGeometry.RightEar] with { Z = -0.2 };
        });
        var readings = new BodyReadings();
        for (var i = 0; i <= 4; i++) readings.OnLandmarks([1], slumped, i * 0.5);
        Assert.Equal(2, readings.SlumpedNowSeconds, 6);

        readings.OnLandmarks([1], Body(), 2.5);

        Assert.Equal(0, readings.SlumpedNowSeconds);
        Assert.Equal(2, readings.LongestSlumpSeconds, 6);
        Assert.Equal(2, readings.SlumpedSeconds, 6);
        Assert.Equal(0.5, readings.UprightSeconds, 6);
    }

    [Fact]
    public void A_gap_is_not_time_and_a_body_not_handed_over_has_left()
    {
        var readings = new BodyReadings();
        readings.OnLandmarks([1], Body(), 0);
        readings.OnLandmarks([1], Body(), 60);
        Assert.Equal(0, readings.ObservedSeconds);

        readings.OnLandmarks([], [], 60.5);

        Assert.Empty(readings.InView);
    }
}
