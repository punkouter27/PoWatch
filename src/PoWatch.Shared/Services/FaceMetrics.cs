namespace PoWatch.Shared.Services;

/// <summary>
/// Per-frame geometry and expression measurements derived directly from MediaPipe's 478-point face mesh
/// (including iris landmarks 468–477): head pose, quality gating, roll-aligned iBUG68 points, blink/EAR,
/// iris gaze attention, smile, concentration, mouth openness, screen distance, and face shape.
/// </summary>
public readonly record struct FaceFrameMetrics(
    double YawDeg,
    double PitchDeg,
    double RollDeg,
    double Quality,
    string QualityLabel,
    bool IsFrontal,
    bool IsNeutralMouth,
    double EyeAspectRatio,
    bool EyesClosed,
    bool LookingAtScreen,
    double SmileScore,
    double ConcentrationScore,
    double MouthOpen,
    string DistanceBand,
    string FaceShape,
    double[]? AlignedIbug68);

public static class FaceMetrics
{
    private const int Forehead = 10, Chin = 152, NoseTip = 1;
    private const int CheekLeft = 234, CheekRight = 454;
    private const int ForeheadLeft = 54, ForeheadRight = 284;
    private const int JawLeft = 172, JawRight = 397;
    private const int EyeLeftOuter = 33, EyeLeftInner = 133, EyeLeftTop = 159, EyeLeftBottom = 145;
    private const int EyeRightInner = 362, EyeRightOuter = 263, EyeRightTop = 386, EyeRightBottom = 374;
    private const int IrisLeft = 468, IrisRight = 473;
    private const int MouthLeft = 61, MouthRight = 291, LipTop = 13, LipBottom = 14;
    private const int BrowLeftInner = 107, BrowRightInner = 336;

    public static FaceFrameMetrics? Analyze(IReadOnlyList<double> meshes, int offset)
    {
        if (offset < 0 || offset + FaceReadings.MeshValues > meshes.Count) return null;

        double X(int pt) => meshes[offset + (pt * 2)];
        double Y(int pt) => meshes[offset + (pt * 2) + 1];
        double Dist(int a, int b) => double.Hypot(X(a) - X(b), Y(a) - Y(b));

        // Require non-iBUG68 landmarks (forehead + iris) to be populated; synthetic 68-point meshes fall back to raw FaceGeometry.
        if ((X(Forehead) == 0 && Y(Forehead) == 0) || (X(IrisLeft) == 0 && Y(IrisLeft) == 0)) return null;

        var faceWidth = Dist(CheekLeft, CheekRight);
        var faceHeight = Dist(Forehead, Chin);
        if (!double.IsFinite(faceWidth) || !double.IsFinite(faceHeight) || faceWidth < FaceGeometry.MinFaceWidth || faceHeight <= 1e-4)
            return null;

        // 1. Head pose (roll, yaw, pitch) from eye line, nose offset, and vertical span.
        var rollRad = Math.Atan2(Y(EyeRightOuter) - Y(EyeLeftOuter), X(EyeRightOuter) - X(EyeLeftOuter));
        var rollDeg = rollRad * (180.0 / Math.PI);

        var midX = (X(CheekLeft) + X(CheekRight)) / 2.0;
        var yawNorm = Math.Clamp((X(NoseTip) - midX) / (faceWidth * 0.5), -1, 1);
        var yawDeg = yawNorm * 55.0;

        var eyeMidY = (Y(EyeLeftOuter) + Y(EyeRightOuter)) / 2.0;
        var noseRatio = (Y(NoseTip) - eyeMidY) / faceHeight;
        var pitchDeg = Math.Clamp((noseRatio - 0.24) * 140.0, -45, 45);

        var isFrontal = Math.Abs(yawDeg) <= 14 && Math.Abs(pitchDeg) <= 14;
        var poseScore = Math.Clamp(1.0 - (Math.Max(Math.Abs(yawDeg), Math.Abs(pitchDeg)) / 40.0), 0.1, 1.0);
        var sizeScore = Math.Clamp(faceWidth / 0.18, 0.2, 1.0);
        var quality = Math.Round(poseScore * sizeScore, 2);
        var qualityLabel = isFrontal && faceWidth >= 0.11 ? "Frontal · high" : Math.Abs(yawDeg) <= 22 && Math.Abs(pitchDeg) <= 22 ? "Good" : "Angled";

        // 2. Roll-aligned iBUG 68 points for accurate Beauty scoring.
        double[]? aligned = null;
        if (MediaPipeFace.ToIbug68(meshes, offset) is { } raw68)
        {
            aligned = new double[raw68.Length];
            var cx = midX;
            var cy = (Y(Forehead) + Y(Chin)) / 2.0;
            var cos = Math.Cos(-rollRad);
            var sin = Math.Sin(-rollRad);
            for (var i = 0; i < FaceGeometry.Points; i++)
            {
                var dx = raw68[i * 2] - cx;
                var dy = raw68[(i * 2) + 1] - cy;
                aligned[i * 2] = cx + (dx * cos) - (dy * sin);
                aligned[(i * 2) + 1] = cy + (dx * sin) + (dy * cos);
            }
        }

        // 3. Eye Aspect Ratio (blink & fatigue).
        var earLeft = Dist(EyeLeftOuter, EyeLeftInner) > 1e-4 ? Dist(EyeLeftTop, EyeLeftBottom) / Dist(EyeLeftOuter, EyeLeftInner) : 0.28;
        var earRight = Dist(EyeRightOuter, EyeRightInner) > 1e-4 ? Dist(EyeRightTop, EyeRightBottom) / Dist(EyeRightOuter, EyeRightInner) : 0.28;
        var ear = (earLeft + earRight) / 2.0;
        var eyesClosed = ear < 0.19;

        // 4. Iris gaze attention (are irises centred while facing the camera/screen?).
        var looking = Math.Abs(yawDeg) <= 22 && Math.Abs(pitchDeg) <= 20 && !eyesClosed;
        var leftSpan = X(EyeLeftInner) - X(EyeLeftOuter);
        var rightSpan = X(EyeRightOuter) - X(EyeRightInner);
        if (looking && Math.Abs(leftSpan) > 1e-4 && Math.Abs(rightSpan) > 1e-4)
        {
            var leftRatio = (X(IrisLeft) - X(EyeLeftOuter)) / leftSpan;
            var rightRatio = (X(IrisRight) - X(EyeRightInner)) / rightSpan;
            var irisPos = (leftRatio + rightRatio) / 2.0;
            looking = irisPos is >= 0.30 and <= 0.70;
        }

        // 5. Mouth openness (neutral gating, talking, yawn).
        var mouthOpen = Dist(LipTop, LipBottom) / faceHeight;
        var isNeutralMouth = mouthOpen < 0.075;

        // 6. Smile & Concentration (0–100).
        var mouthWidthRatio = Dist(MouthLeft, MouthRight) / faceWidth;
        var lipMidY = (Y(LipTop) + Y(LipBottom)) / 2.0;
        var cornerMidY = (Y(MouthLeft) + Y(MouthRight)) / 2.0;
        var cornerLift = (lipMidY - cornerMidY) / faceHeight;
        var smileScore = Math.Clamp(((mouthWidthRatio - 0.34) * 320.0) + (cornerLift * 900.0), 0, 100);

        var browGap = Dist(BrowLeftInner, BrowRightInner) / faceWidth;
        var browEyeGap = (Math.Abs(Y(EyeLeftTop) - Y(BrowLeftInner)) + Math.Abs(Y(EyeRightTop) - Y(BrowRightInner))) / (2.0 * faceHeight);
        var concentrationScore = Math.Clamp(((0.22 - browGap) * 420.0) + ((0.14 - browEyeGap) * 350.0), 0, 100);

        // 7. Screen distance from inter-pupillary distance.
        var ipd = Dist(IrisLeft, IrisRight);
        var distanceBand = ipd > 0.155 ? "Too close" : ipd < 0.065 ? "Far" : "Good";

        // 8. Geometric Face Shape classification.
        var foreheadW = Dist(ForeheadLeft, ForeheadRight);
        var jawW = Dist(JawLeft, JawRight);
        var aspect = faceHeight / faceWidth;
        var faceShape = ClassifyShape(aspect, foreheadW / faceWidth, jawW / faceWidth);

        return new FaceFrameMetrics(
            Math.Round(yawDeg, 1),
            Math.Round(pitchDeg, 1),
            Math.Round(rollDeg, 1),
            quality,
            qualityLabel,
            isFrontal,
            isNeutralMouth,
            Math.Round(ear, 3),
            eyesClosed,
            looking,
            Math.Round(smileScore, 0),
            Math.Round(concentrationScore, 0),
            Math.Round(mouthOpen, 3),
            distanceBand,
            faceShape,
            aligned);
    }

    private static string ClassifyShape(double lengthToWidth, double foreheadRatio, double jawRatio)
    {
        if (lengthToWidth >= 1.50) return "Oblong";
        if (foreheadRatio > jawRatio + 0.14) return "Heart";
        if (foreheadRatio < 0.80 && jawRatio < 0.78) return "Diamond";
        if (lengthToWidth <= 1.18) return jawRatio >= 0.86 ? "Square" : "Round";
        return jawRatio >= 0.88 ? "Square" : "Oval";
    }
}
