using PoWatch.Shared.Services;

namespace PoWatch.Unit;

public sealed class FaceGeometryTests
{
    /// <summary>
    /// A synthetic, exactly symmetric 68-point face in the iBUG layout, in units of the frame's width.
    /// The defaults are the textbook proportions: face length over width at φ, five eye widths across,
    /// the lower two thirds equal, and the mouth splitting nose-base-to-chin at φ. The topology is
    /// nominal — this is a fixture, not a rendering — but every pair <see cref="FaceGeometry"/> mirrors
    /// is mirrored here, so the base face is exactly symmetric.
    /// </summary>
    private static double[] Face(
        double faceWidth = 0.28,
        double eyeWidth = 0.056,
        double noseWidth = 0.04,
        double mouthWidth = 0.0648,
        double browY = 0.35,
        double noseY = 0.575,
        double mouthY = 0.6609,
        double chinY = 0.80)
    {
        const double axis = 0.5;
        var k = new double[FaceGeometry.Points * FaceGeometry.ValuesPerPoint];

        void Set(int point, double x, double y)
        {
            k[point * 2] = x;
            k[(point * 2) + 1] = y;
        }

        void Mirror(int left, int right, double x, double y)
        {
            Set(left, x, y);
            Set(right, axis + (axis - x), y);
        }

        // Jaw 0–16: an arc from ear to ear through the chin, so 0 and 16 are the widest points.
        for (var i = 0; i <= 16; i++)
        {
            var t = Math.PI * i / 16;
            Set(i, axis - (faceWidth / 2 * Math.Cos(t)), browY + ((chinY - browY) * Math.Sin(t)));
        }

        // Brows 17–26; 19 and 24 are the two FaceGeometry reads.
        Mirror(19, 24, axis - (eyeWidth * 1.6), browY);
        Mirror(17, 26, axis - (eyeWidth * 2.4), browY);
        Mirror(18, 25, axis - (eyeWidth * 2.0), browY - 0.004);
        Mirror(20, 23, axis - (eyeWidth * 1.2), browY - 0.004);
        Mirror(21, 22, axis - (eyeWidth * 0.8), browY);

        // Nose 27–35: a bridge on the axis, and a base noseWidth wide at noseY.
        for (var i = 27; i <= 30; i++) Set(i, axis, browY + ((noseY - browY) * (i - 26) / 5));
        Set(31, axis - (noseWidth / 2), noseY);
        Set(32, axis - (noseWidth / 6), noseY);
        Set(33, axis, noseY);
        Set(34, axis + (noseWidth / 6), noseY);
        Set(35, axis + (noseWidth / 2), noseY);

        // Eyes 36–47: eyeWidth wide, on the coordinates FaceGeometry pairs.
        var eyeY = browY + ((noseY - browY) / 2);
        Mirror(36, 45, axis - (eyeWidth * 2), eyeY);
        Mirror(37, 44, axis - (eyeWidth * 5 / 3), eyeY - 0.008);
        Mirror(38, 43, axis - (eyeWidth * 4 / 3), eyeY - 0.008);
        Mirror(39, 42, axis - eyeWidth, eyeY);
        Mirror(40, 47, axis - (eyeWidth * 4 / 3), eyeY + 0.008);
        Mirror(41, 46, axis - (eyeWidth * 5 / 3), eyeY + 0.008);

        // Mouth 48–59 outside, 60–67 inside, mirrored where FaceGeometry pairs them.
        var half = mouthWidth / 2;
        Mirror(48, 54, axis - half, mouthY);
        Mirror(49, 53, axis - (half * 2 / 3), mouthY - 0.004);
        Mirror(50, 52, axis - (half / 3), mouthY - 0.006);
        Set(51, axis, mouthY - 0.006);
        Mirror(55, 59, axis - (half * 2 / 3), mouthY + 0.010);
        Mirror(56, 58, axis - (half / 3), mouthY + 0.010);
        Set(57, axis, mouthY + 0.012);

        var inner = half * 0.6;
        Mirror(60, 64, axis - inner, mouthY);
        Mirror(61, 63, axis - (inner * 0.6), mouthY - 0.004);
        Mirror(67, 65, axis - (inner * 0.3), mouthY + 0.007);
        Set(62, axis, mouthY - 0.005);
        Set(66, axis, mouthY + 0.008);

        return k;
    }

    [Fact]
    public void A_canonical_face_scores_near_the_top_and_is_perfectly_symmetric()
    {
        var score = FaceGeometry.Read(Face());

        Assert.NotNull(score);
        Assert.Equal(1, score!.Value.Symmetry, 3);
        Assert.True(score.Value.Canons > 0.95, $"canons were {score.Value.Canons}");
        Assert.True(score.Value.Beauty >= 95, $"beauty was {score.Value.Beauty}");
    }

    [Fact]
    public void A_mouth_too_wide_for_the_nose_loses_the_golden_proportion()
    {
        var canonical = FaceGeometry.Read(Face())!.Value;
        var wide = FaceGeometry.Read(Face(mouthWidth: 0.0972))!.Value;

        Assert.True(wide.Canons < canonical.Canons);
        Assert.True(wide.Beauty < canonical.Beauty - 5, $"beauty went from {canonical.Beauty} to {wide.Beauty}");
    }

    [Fact]
    public void A_face_out_of_true_loses_symmetry_and_the_score_follows()
    {
        var lopsided = Face();
        foreach (var point in new[] { 11, 12, 13, 14, 15, 16 }) lopsided[point * 2] += 0.03;

        var canonical = FaceGeometry.Read(Face())!.Value;
        var scored = FaceGeometry.Read(lopsided)!.Value;

        Assert.True(scored.Symmetry < 0.9, $"symmetry was {scored.Symmetry}");
        Assert.True(scored.Beauty < canonical.Beauty);
    }

    [Fact]
    public void Too_few_landmarks_scores_nothing()
    {
        var shortOfALandmark = Face().Take((FaceGeometry.Points * FaceGeometry.ValuesPerPoint) - 2).ToArray();

        Assert.Null(FaceGeometry.Read(shortOfALandmark));
    }

    [Fact]
    public void A_broken_landmark_scores_nothing()
    {
        var broken = Face();
        broken[33 * 2] = double.NaN;

        Assert.Null(FaceGeometry.Read(broken));
    }

    [Fact]
    public void A_face_too_small_in_frame_scores_nothing()
    {
        Assert.Null(FaceGeometry.Read(Face(faceWidth: FaceGeometry.MinFaceWidth / 2)));
    }

    [Fact]
    public void A_face_wider_than_a_face_scores_nothing()
    {
        Assert.Null(FaceGeometry.Read(Face(faceWidth: FaceGeometry.MaxFaceWidth + 0.01)));
    }

    [Fact]
    public void An_absurd_face_stays_within_zero_and_a_hundred()
    {
        var absurd = Face(mouthWidth: 0.25, noseWidth: 0.01, browY: 0.10, mouthY: 0.79);
        var score = FaceGeometry.Read(absurd)!.Value;

        Assert.InRange(score.Beauty, 0, 100);
        Assert.InRange(score.Symmetry, 0, 1);
        Assert.InRange(score.Canons, 0, 1);
    }
}
