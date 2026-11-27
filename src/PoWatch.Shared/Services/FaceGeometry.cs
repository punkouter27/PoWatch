namespace PoWatch.Shared.Services;

/// <summary>
/// How one face scores against the classical canons of proportion. <see cref="Beauty"/> is the
/// composite, 0–100; <see cref="Symmetry"/> and <see cref="Canons"/> are its two halves, both 0–1.
/// </summary>
public readonly record struct FaceScore(double Beauty, double Symmetry, double Canons, double Golden = 0, double Thirds = 0, double Fifths = 0);

/// <summary>
/// Traditional beauty, read as geometry. This is the classical canon — symmetry, facial thirds, the
/// rule of fifths and the golden-ratio proportions — applied to the 68 landmarks of the iBUG/300-W
/// layout that face models emit, never a learned opinion about a photograph. It says how close a face
/// sits to the textbook proportions, nothing about the person; and it cannot be flattering, because
/// the canons are only approximately true of any real face.
/// </summary>
public static class FaceGeometry
{
    /// <summary>Landmarks per face: the iBUG 68-point layout.</summary>
    public const int Points = 68;

    /// <summary>x and y per landmark, both in units of the frame's width so that lengths compare.</summary>
    public const int ValuesPerPoint = 2;

    public const double GoldenRatio = 1.6180339887498949;

    /// <summary>A face narrower than this share of the frame is too small to measure; the score is null.</summary>
    public const double MinFaceWidth = 0.02;

    /// <summary>Widths, as a share of face width, that no 68-point face reaches: anything past it is a broken read.</summary>
    public const double MaxFaceWidth = 1.5;

    // iBUG 68: 0–16 jaw (0 and 16 are the ears, 8 the chin), 17–26 brows, 27–35 nose,
    // 36–47 eyes, 48–59 outer mouth, 60–67 inner mouth.
    private const int JawLeft = 0, Chin = 8, JawRight = 16;
    private const int BrowLeft = 19, BrowRight = 24;
    private const int NoseLeft = 31, NoseBase = 33, NoseRight = 35;
    private const int EyeLeftOuter = 36, EyeLeftInner = 39, EyeRightInner = 42, EyeRightOuter = 45;
    private const int MouthLeft = 48, MouthRight = 54;

    /// <summary>Landmark pairs that mirror each other across the face's midline.</summary>
    private static readonly (int Left, int Right)[] Mirrored =
    [
        (0, 16), (1, 15), (2, 14), (3, 13), (4, 12), (5, 11), (6, 10), (7, 9),
        (17, 26), (18, 25), (19, 24), (20, 23), (21, 22),
        (31, 35), (32, 34),
        (36, 45), (37, 44), (38, 43), (39, 42), (40, 47), (41, 46),
        (48, 54), (49, 53), (50, 52), (60, 64), (61, 63), (67, 65),
    ];

    /// <summary>How far mirrored pairs may drift, on average, as a share of face width, before symmetry reaches zero.</summary>
    public const double MaxAsymmetry = 0.05;

    /// <summary>Face width divided by one eye's width: the rule of fifths puts five eyes across a face.</summary>
    public const double FaceWidthInEyes = 5;

    /// <summary>
    /// Scores one face, or null when the landmarks cannot describe one (too few, not finite, or a
    /// width outside <see cref="MinFaceWidth"/>–<see cref="MaxFaceWidth"/>).
    /// </summary>
    /// <param name="landmarks">68 × (x, y), in units of the frame's width.</param>
    public static FaceScore? Read(IReadOnlyList<double> landmarks)
    {
        ArgumentNullException.ThrowIfNull(landmarks);
        if (landmarks.Count < Points * ValuesPerPoint) return null;
        for (var i = 0; i < Points * ValuesPerPoint; i++)
            if (!double.IsFinite(landmarks[i])) return null;

        var faceWidth = X(landmarks, JawRight) - X(landmarks, JawLeft);
        if (faceWidth < MinFaceWidth || faceWidth > MaxFaceWidth) return null;

        var faceHeight = Y(landmarks, Chin) - BrowY(landmarks);
        if (faceHeight <= 0) return null;

        var symmetry = Symmetry(landmarks, faceWidth);
        var golden = GoldenProportions(landmarks, faceWidth);
        var thirds = LowerThirds(landmarks);
        var fifths = RuleOfFifths(landmarks, faceWidth);
        var canons = (0.45 * golden)
                     + (0.30 * thirds)
                     + (0.25 * fifths);

        var beauty = Math.Clamp((0.35 * symmetry) + (0.65 * canons), 0, 1) * 100;
        return new FaceScore(Math.Round(beauty, 1), Math.Round(symmetry, 3), Math.Round(canons, 3),
            Math.Round(golden, 3), Math.Round(thirds, 3), Math.Round(fifths, 3));
    }

    /// <summary>
    /// Symmetry, 0–1: how closely each landmark pair mirrors the other across the midline (the face's
    /// own axis, taken through the chin and the brow), measured as drift from face width.
    /// </summary>
    public static double Symmetry(IReadOnlyList<double> landmarks, double faceWidth)
    {
        var axis = (X(landmarks, Chin) + BrowX(landmarks)) / 2;
        var drift = 0.0;
        foreach (var (left, right) in Mirrored)
        {
            var across = Math.Abs((X(landmarks, left) - axis) + (X(landmarks, right) - axis)) / faceWidth;
            var down = Math.Abs(Y(landmarks, left) - Y(landmarks, right)) / faceWidth;
            drift += (across + down) / 2;
        }
        return Math.Clamp(1 - ((drift / Mirrored.Length) / MaxAsymmetry), 0, 1);
    }

    /// <summary>
    /// The golden-ratio proportions, 0–1: face length over width, mouth over nose, and the mouth's
    /// place between the nose base and the chin, where it splits that span at φ.
    /// </summary>
    private static double GoldenProportions(IReadOnlyList<double> landmarks, double faceWidth)
    {
        var faceHeight = Y(landmarks, Chin) - BrowY(landmarks);
        var noseWidth = X(landmarks, NoseRight) - X(landmarks, NoseLeft);
        var mouthWidth = X(landmarks, MouthRight) - X(landmarks, MouthLeft);
        var noseBaseToMouth = MouthY(landmarks) - Y(landmarks, NoseBase);
        var mouthToChin = Y(landmarks, Chin) - MouthY(landmarks);
        if (noseWidth <= 0 || noseBaseToMouth <= 0 || mouthToChin <= 0) return 0;

        // Tolerances are the drift a real face regularly shows before it stops reading as canonical.
        return new[]
        {
            Closeness(faceHeight / faceWidth, GoldenRatio, 0.6),
            Closeness(mouthWidth / noseWidth, GoldenRatio, 0.8),
            Closeness(mouthToChin / noseBaseToMouth, GoldenRatio, 1.0),
        }.Average();
    }

    /// <summary>
    /// The lower two facial thirds, 0–1: brow to nose base and nose base to chin should match, each
    /// being a third of the face once the hairline counts. Only their ratio is knowable here.
    /// </summary>
    private static double LowerThirds(IReadOnlyList<double> landmarks)
    {
        var browToNose = Y(landmarks, NoseBase) - BrowY(landmarks);
        var noseToChin = Y(landmarks, Chin) - Y(landmarks, NoseBase);
        if (browToNose <= 0) return 0;
        return Closeness(noseToChin / browToNose, 1, 0.5);
    }

    /// <summary>The rule of fifths, 0–1: five eye widths across the face.</summary>
    private static double RuleOfFifths(IReadOnlyList<double> landmarks, double faceWidth)
    {
        var eyeWidth = ((X(landmarks, EyeLeftInner) - X(landmarks, EyeLeftOuter))
                        + (X(landmarks, EyeRightOuter) - X(landmarks, EyeRightInner))) / 2;
        if (eyeWidth <= 0) return 0;
        return Closeness(faceWidth / eyeWidth, FaceWidthInEyes, 1.5);
    }

    /// <summary>1 at <paramref name="target"/>, 0 once the value is <paramref name="tolerance"/> away from it.</summary>
    private static double Closeness(double value, double target, double tolerance)
        => Math.Clamp(1 - (Math.Abs(value - target) / tolerance), 0, 1);

    private static double BrowX(IReadOnlyList<double> k) => (X(k, BrowLeft) + X(k, BrowRight)) / 2;

    private static double BrowY(IReadOnlyList<double> k) => (Y(k, BrowLeft) + Y(k, BrowRight)) / 2;

    private static double MouthY(IReadOnlyList<double> k) => (Y(k, MouthLeft) + Y(k, MouthRight)) / 2;

    private static double X(IReadOnlyList<double> k, int point) => k[point * ValuesPerPoint];

    private static double Y(IReadOnlyList<double> k, int point) => k[(point * ValuesPerPoint) + 1];
}
