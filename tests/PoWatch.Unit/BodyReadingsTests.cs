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
    public void One_frame_reads_as_upright_slumped_leaning_or_closed()
    {
        var upright = BodyGeometry.Read(Body());
        Assert.True(upright.UpperBody);
        Assert.True(upright.FullBody);
        Assert.False(upright.Slumped);
        Assert.Equal(BodyCues.None, upright.Cues);
        Assert.Equal(0, upright.ShoulderTilt!.Value, 3);

        var noHips = Body();
        noHips[(BodyGeometry.LeftHip * BodyGeometry.ValuesPerPoint) + 2] = 0;
        Assert.False(BodyGeometry.Read(noHips).UpperBody);
        Assert.False(BodyGeometry.Read([1, 2, 3]).UpperBody);

        Assert.True(BodyGeometry.Read(Body(HeadForward)).Slumped);

        var leaning = BodyGeometry.Read(Body(p =>
        {
            foreach (var point in new[] { BodyGeometry.LeftShoulder, BodyGeometry.RightShoulder, BodyGeometry.LeftEar, BodyGeometry.RightEar })
                p[point] = p[point] with { Z = -0.12 };
        }));
        Assert.Equal(BodyCues.LeaningIn, leaning.Cues);
        Assert.False(leaning.Slumped);

        Assert.True(BodyGeometry.Read(Body(p =>
        {
            p[BodyGeometry.LeftWrist] = (0.45, 0.45, 0);
            p[BodyGeometry.RightWrist] = (0.55, 0.45, 0);
        })).Cues.HasFlag(BodyCues.ArmsCrossed));
        Assert.Equal(BodyCues.HandToFace, BodyGeometry.Read(Body(p => p[BodyGeometry.RightWrist] = (0.48, 0.24, 0))).Cues);
        Assert.True(BodyGeometry.Read(Body(p => p[BodyGeometry.LeftShoulder] = (0.58, 0.32, 0))).ShoulderTilt > 5);
    }

    [Fact]
    public void Speeds_read_as_an_activity()
    {
        static BodyActivity Read(double limbs, double hips, double wrists, double ankles, bool anklesSeen = false, bool handUp = false, bool bothUp = false) =>
            BodyReadings.Classify(new BodyReadings.Speeds(limbs, hips, wrists, ankles), anklesSeen, handUp, bothUp);

        Assert.Equal(BodyActivity.Walking, Read(0, 0.6, 0, 0, anklesSeen: true));
        Assert.Equal(BodyActivity.Active, Read(1.2, 0, 1, 1, anklesSeen: true));
        Assert.Equal(BodyActivity.Waving, Read(0.3, 0, 1.0, 0, handUp: true));
        Assert.Equal(BodyActivity.Stretching, Read(0.1, 0, 0.2, 0, handUp: true, bothUp: true));
        Assert.Equal(BodyActivity.Gesturing, Read(0.2, 0, 0.5, 0));
        Assert.Equal(BodyActivity.Still, Read(0.01, 0.01, 0.01, 0.01, anklesSeen: true));
    }

    [Fact]
    public void A_session_adds_up_time_slumps_waves_walks_and_leavers()
    {
        var still = new BodyReadings();
        for (var i = 0; i <= 12; i++) still.OnLandmarks([1], Body(), i * 0.5);
        var body = Assert.Single(still.InView);
        Assert.Equal(1, body.Slot);
        Assert.Equal(BodyActivity.Still, body.Activity);
        Assert.Equal(6, still.ActivitySeconds[BodyActivity.Still], 6);
        Assert.Equal(100, still.PostureScore);
        Assert.Equal(6, still.OpenSeconds, 6);
        Assert.Equal(0, Assert.Single(still.EnergyHistory));
        Assert.True(still.FullBodySeen);

        var waving = new BodyReadings();
        for (var i = 0; i <= 6; i++)
            waving.OnLandmarks([1], Body(p => p[BodyGeometry.LeftWrist] = (i % 2 == 0 ? 0.6 : 0.8, 0.2, 0)), i * 0.5);
        Assert.Equal(BodyActivity.Waving, Assert.Single(waving.InView).Activity);
        Assert.True(waving.ArmBalance > 0.9);

        var walking = new BodyReadings();
        for (var i = 0; i <= 4; i++) walking.OnLandmarks([1], Body(shift: i * 0.1), i * 0.5);
        Assert.Equal(BodyActivity.Walking, Assert.Single(walking.InView).Activity);

        var slumping = new BodyReadings();
        for (var i = 0; i <= 4; i++) slumping.OnLandmarks([1], Body(HeadForward), i * 0.5);
        Assert.Equal(2, slumping.SlumpedNowSeconds, 6);
        slumping.OnLandmarks([1], Body(), 2.5);
        Assert.Equal(0, slumping.SlumpedNowSeconds);
        Assert.Equal(2, slumping.LongestSlumpSeconds, 6);
        Assert.Equal(0.5, slumping.UprightSeconds, 6);

        // A gap (a hidden tab) is not time, and a body not handed over has left.
        var gap = new BodyReadings();
        gap.OnLandmarks([1], Body(), 0);
        gap.OnLandmarks([1], Body(), 60);
        Assert.Equal(0, gap.ObservedSeconds);
        gap.OnLandmarks([], [], 60.5);
        Assert.Empty(gap.InView);
    }

    /// <summary>Ears pushed 20 cm towards the camera: a forward head.</summary>
    private static void HeadForward(Dictionary<int, (double X, double Y, double Z)> p)
    {
        p[BodyGeometry.LeftEar] = p[BodyGeometry.LeftEar] with { Z = -0.2 };
        p[BodyGeometry.RightEar] = p[BodyGeometry.RightEar] with { Z = -0.2 };
    }
}
