namespace PoWatch.Shared.Services;

/// <summary>Body-language cues one frame shows; several can hold at once.</summary>
[Flags]
public enum BodyCues
{
    None = 0,
    ArmsCrossed = 1,
    HandsOnHips = 2,
    HandToFace = 4,
    HandsBehindHead = 8,
    LeaningIn = 16,
    LeaningBack = 32,
}

/// <summary>
/// What one frame of one body says on its own, before anything is compared with the frame before.
/// <see cref="Slumped"/> is null when the shoulders, hips or ears are not seen well enough to judge, or
/// the person is lying down.
/// </summary>
public sealed record BodyPose(bool UpperBody, bool FullBody, double TrunkLength, bool? Slumped, double? ShoulderTilt, BodyCues Cues);

/// <summary>
/// Reads one frame of MediaPipe Pose's 33 points (body-overlay.js). Per point: x and y in units of the
/// frame's width, visibility, then the world point in metres around the hips (x, y down, z towards the
/// camera negative). "Left" is the person's own left. Thresholds are rules of thumb, not medical
/// measures: the point is a trend over a session, not a diagnosis.
/// </summary>
public static class BodyGeometry
{
    public const int Points = 33;

    /// <summary>Values per point in what body-overlay.js hands over.</summary>
    public const int ValuesPerPoint = 6;

    public const int ValuesPerBody = Points * ValuesPerPoint;

    /// <summary>A point the model is less sure of than this is treated as not seen.</summary>
    public const double MinVisibility = 0.5;

    /// <summary>
    /// The neck (shoulders to ears) bent further than this from the trunk's line, in degrees, is a
    /// forward head; upright, the ears sit roughly over the shoulders.
    /// </summary>
    public const double SlumpNeckAngle = 30;

    /// <summary>A trunk tipped further than this from vertical (degrees) while upright is slumped or hunched.</summary>
    public const double SlumpTrunkAngle = 25;

    /// <summary>Past this the person is lying down, and posture is not scored.</summary>
    public const double LyingAngle = 55;

    /// <summary>Shoulders this far (metres) nearer the camera than the hips is leaning in; this far behind, leaning back.</summary>
    public const double LeanMetres = 0.08;

    public const int Nose = 0, LeftEar = 7, RightEar = 8, LeftShoulder = 11, RightShoulder = 12, LeftElbow = 13, RightElbow = 14,
        LeftWrist = 15, RightWrist = 16, LeftIndex = 19, RightIndex = 20, LeftHip = 23, RightHip = 24, LeftKnee = 25, RightKnee = 26,
        LeftAnkle = 27, RightAnkle = 28;

    /// <summary>Reads the body that starts at <paramref name="offset"/> in <paramref name="values"/>.</summary>
    public static BodyPose Read(IReadOnlyList<double> values, int offset = 0)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count < offset + ValuesPerBody) return new BodyPose(false, false, 0, null, null, BodyCues.None);
        var b = new Body(values, offset);

        var upper = b.Seen(LeftShoulder) && b.Seen(RightShoulder) && b.Seen(LeftHip) && b.Seen(RightHip);
        if (!upper) return new BodyPose(false, false, 0, null, null, BodyCues.None);
        var full = upper && b.Seen(LeftKnee) && b.Seen(RightKnee) && b.Seen(LeftAnkle) && b.Seen(RightAnkle);

        var (sx, sy) = b.Mid(LeftShoulder, RightShoulder);
        var (hx, hy) = b.Mid(LeftHip, RightHip);
        var trunk = Math.Sqrt(((sx - hx) * (sx - hx)) + ((sy - hy) * (sy - hy)));
        if (trunk < 1e-6) return new BodyPose(false, false, 0, null, null, BodyCues.None);

        var facing = Math.Abs(b.X(LeftShoulder) - b.X(RightShoulder)) > 0.3 * trunk;
        double? tilt = facing
            ? Math.Atan2(b.Y(LeftShoulder) - b.Y(RightShoulder), Math.Abs(b.X(LeftShoulder) - b.X(RightShoulder))) * 180 / Math.PI
            : null;

        return new BodyPose(true, full, trunk, Slumped(b), tilt, Cues(b, sx, sy, trunk, facing));
    }

    private static bool? Slumped(Body b)
    {
        var shoulders = b.WorldMid(LeftShoulder, RightShoulder);
        var hips = b.WorldMid(LeftHip, RightHip);
        var up = Sub(shoulders, hips);
        var length = Length(up);
        if (length < 1e-6) return null;
        var trunkAngle = Math.Acos(Math.Clamp(-up.Y / length, -1, 1)) * 180 / Math.PI;
        if (trunkAngle > LyingAngle) return null;
        if (trunkAngle > SlumpTrunkAngle) return true;

        if (!b.Seen(LeftEar) && !b.Seen(RightEar)) return null;
        var ears = b.Seen(LeftEar) && b.Seen(RightEar) ? b.WorldMid(LeftEar, RightEar) : b.World(b.Seen(LeftEar) ? LeftEar : RightEar);
        var neck = Sub(ears, shoulders);
        var neckLength = Length(neck);
        if (neckLength < 1e-6) return null;
        var bend = Math.Acos(Math.Clamp(Dot(neck, up) / (neckLength * length), -1, 1)) * 180 / Math.PI;
        return bend > SlumpNeckAngle;
    }

    private static BodyCues Cues(Body b, double midX, double shoulderY, double trunk, bool facing)
    {
        var cues = BodyCues.None;
        var hipY = b.Mid(LeftHip, RightHip).Y;
        var bothWrists = b.Seen(LeftWrist) && b.Seen(RightWrist);
        // Which way along x the person's left lies: a mirror camera and a back view flip it.
        var leftSide = Math.Sign(b.X(LeftShoulder) - b.X(RightShoulder));

        if (facing && bothWrists && leftSide != 0
            && (b.X(LeftWrist) - midX) * leftSide < 0 && (b.X(RightWrist) - midX) * leftSide > 0
            && Between(b.Y(LeftWrist), shoulderY, hipY) && Between(b.Y(RightWrist), shoulderY, hipY))
            cues |= BodyCues.ArmsCrossed;

        if (facing && bothWrists && b.Seen(LeftElbow) && b.Seen(RightElbow)
            && b.Distance(LeftWrist, LeftHip) < 0.3 * trunk && b.Distance(RightWrist, RightHip) < 0.3 * trunk
            && Math.Abs(b.X(LeftElbow) - midX) > Math.Abs(b.X(LeftShoulder) - midX)
            && Math.Abs(b.X(RightElbow) - midX) > Math.Abs(b.X(RightShoulder) - midX))
            cues |= BodyCues.HandsOnHips;

        if (b.Seen(Nose))
        {
            var noseY = b.Y(Nose);
            if (bothWrists && b.Y(LeftWrist) < noseY && b.Y(RightWrist) < noseY
                && b.Distance(LeftWrist, Nose) < 0.6 * trunk && b.Distance(RightWrist, Nose) < 0.6 * trunk)
                cues |= BodyCues.HandsBehindHead;
            else if (NearFace(b, LeftWrist, LeftIndex, trunk) || NearFace(b, RightWrist, RightIndex, trunk))
                cues |= BodyCues.HandToFace;
        }

        var lean = b.WorldMid(LeftShoulder, RightShoulder).Z - b.WorldMid(LeftHip, RightHip).Z;
        if (lean < -LeanMetres) cues |= BodyCues.LeaningIn;
        else if (lean > LeanMetres) cues |= BodyCues.LeaningBack;
        return cues;
    }

    private static bool NearFace(Body b, int wrist, int finger, double trunk) =>
        (b.Seen(wrist) && b.Distance(wrist, Nose) < 0.35 * trunk) || (b.Seen(finger) && b.Distance(finger, Nose) < 0.3 * trunk);

    private static bool Between(double y, double top, double bottom) => y > top && y < bottom;

    private static (double X, double Y, double Z) Sub((double X, double Y, double Z) a, (double X, double Y, double Z) b) => (a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    private static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

    private static double Length((double X, double Y, double Z) a) => Math.Sqrt(Dot(a, a));

    /// <summary>One body's slice of the values, read by point index.</summary>
    internal readonly struct Body(IReadOnlyList<double> values, int offset)
    {
        public double X(int point) => values[offset + (point * ValuesPerPoint)];

        public double Y(int point) => values[offset + (point * ValuesPerPoint) + 1];

        public bool Seen(int point) => values[offset + (point * ValuesPerPoint) + 2] >= MinVisibility;

        public (double X, double Y, double Z) World(int point)
        {
            var at = offset + (point * ValuesPerPoint) + 3;
            return (values[at], values[at + 1], values[at + 2]);
        }

        public (double X, double Y) Mid(int a, int b) => ((X(a) + X(b)) / 2, (Y(a) + Y(b)) / 2);

        public (double X, double Y, double Z) WorldMid(int a, int b)
        {
            var (p, q) = (World(a), World(b));
            return ((p.X + q.X) / 2, (p.Y + q.Y) / 2, (p.Z + q.Z) / 2);
        }

        public double Distance(int a, int b) => Math.Sqrt(((X(a) - X(b)) * (X(a) - X(b))) + ((Y(a) - Y(b)) * (Y(a) - Y(b))));
    }
}
