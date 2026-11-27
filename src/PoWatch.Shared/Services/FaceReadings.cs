namespace PoWatch.Shared.Services;

/// <summary>
/// What the camera reads from one face in view right now: pose-gated roll-aligned Beauty, quality-weighted
/// Age &amp; Presentation, plus blink rate, eye fatigue, iris gaze attention, smile, concentration, talking,
/// screen distance, and geometric face shape.
/// </summary>
public sealed record FaceReadout(
    int Slot,
    string? Label,
    double? Beauty,
    string? AgeBand,
    double AgeConfidence,
    string? Presentation,
    double PresentationConfidence,
    int Readings,
    double? AgeYears = null,
    double? Symmetry = null,
    double? Golden = null,
    double? Thirds = null,
    double? Fifths = null,
    string? QualityLabel = null,
    double? AttentionShare = null,
    double? SmileScore = null,
    double? ConcentrationScore = null,
    double? BlinkRatePerMin = null,
    double? FatigueShare = null,
    bool IsTalking = false,
    double? TalkingShare = null,
    string? DistanceBand = null,
    string? FaceShape = null,
    double? HeadTiltDeg = null)
{
    public bool? Male => Presentation switch
    {
        "male" => true,
        "female" => false,
        _ => null,
    };
}

public sealed class FaceReadings
{
    public static readonly string[] AgeBands = ["0–2", "3–9", "10–19", "20–29", "30–39", "40–49", "50–59", "60–69", "70+"];
    public static readonly double[] AgeBandMiddles = [1, 6, 14.5, 24.5, 34.5, 44.5, 54.5, 64.5, 75];
    public static readonly string[] Presentations = ["female", "male"];

    public const double MinPresentationConfidence = 0.8;
    public const double DescriptorMatchDistance = 0.2;
    public const double SwapDistance = 0.45;
    public const double BeautySmoothing = 0.2;
    public const int MeshValues = MediaPipeFace.MeshPoints * 2;

    private readonly FaceLabeler _labeler = new(DescriptorMatchDistance);
    private readonly SortedDictionary<int, Face> _faces = [];

    public int Labelled => _labeler.Count;
    public bool HasRead { get; private set; }

    public IReadOnlyList<FaceReadout> InView =>
        [.. _faces.Select(pair => pair.Value.Readout(pair.Key))];

    public void OnLandmarks(IReadOnlyList<double> slots, IReadOnlyList<double> meshes)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(meshes);
        var seen = new HashSet<int>();
        for (var i = 0; i < slots.Count; i++)
        {
            var offset = i * MeshValues;
            if (offset + MeshValues > meshes.Count || !double.IsFinite(slots[i])) break;
            var slot = (int)slots[i];
            seen.Add(slot);
            if (!_faces.TryGetValue(slot, out var face)) _faces[slot] = face = new Face();

            var metrics = FaceMetrics.Analyze(meshes, offset);
            if (metrics is { } m)
            {
                face.ObserveMetrics(m);
                // Pose- and neutral-mouth-gated, roll-aligned Beauty scoring (falls back on first frame so a score appears promptly).
                var allowBeauty = face.Beauty is null || (m.IsFrontal && m.IsNeutralMouth);
                if (allowBeauty && m.AlignedIbug68 is { } aligned && FaceGeometry.Read(aligned) is { } score)
                {
                    face.Beauty = face.Beauty is { } shown
                        ? shown + (BeautySmoothing * (score.Beauty - shown))
                        : score.Beauty;
                    face.Symmetry = score.Symmetry;
                    face.Golden = score.Golden;
                    face.Thirds = score.Thirds;
                    face.Fifths = score.Fifths;
                }
            }
            else if (MediaPipeFace.ToIbug68(meshes, offset) is { } points && FaceGeometry.Read(points) is { } score)
            {
                face.Beauty = face.Beauty is { } shown
                    ? shown + (BeautySmoothing * (score.Beauty - shown))
                    : score.Beauty;
                face.Symmetry = score.Symmetry;
                face.Golden = score.Golden;
                face.Thirds = score.Thirds;
                face.Fifths = score.Fifths;
            }
        }
        foreach (var gone in _faces.Keys.Where(slot => !seen.Contains(slot)).ToList()) _faces.Remove(gone);
    }

    public void OnBlendshapes(IReadOnlyList<double> slots, IReadOnlyList<double> values)
    {
        for (var i = 0; i < slots.Count && (i * 5) + 4 < values.Count; i++)
        {
            var slot = (int)slots[i];
            if (_faces.TryGetValue(slot, out var face))
                face.ObserveBlendshapes(values[i * 5], values[(i * 5) + 1], values[(i * 5) + 2], values[(i * 5) + 3], values[(i * 5) + 4]);
        }
    }

    public bool OnReading(int slot, IReadOnlyList<double> age, IReadOnlyList<double> presentation, IReadOnlyList<float> descriptor)
    {
        ArgumentNullException.ThrowIfNull(age);
        ArgumentNullException.ThrowIfNull(presentation);
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!_faces.TryGetValue(slot, out var face)) return false;

        if (face.Label is { } held && _labeler.DistanceTo(held, descriptor) > SwapDistance)
            _faces[slot] = face = new Face();

        var weight = Math.Clamp(face.LastQuality, 0.15, 1.0);
        face.Age.Add(age, weight);
        face.Presentation.Add(presentation, weight);
        face.RecordExpectedAge(age);

        if (face.Label is null)
        {
            var inView = _faces.Values.Where(f => f != face && f.Label is not null).Select(f => f.Label!).ToList();
            face.Label = _labeler.Observe(descriptor, inView)?.Label;
        }
        else
        {
            _labeler.Learn(face.Label, descriptor);
        }
        face.Readings++;
        HasRead = true;
        return true;
    }

    private sealed class Face
    {
        private readonly Queue<double> _mouthWindow = new();
        private readonly List<double> _ageEstimates = [];
        private int _frames;
        private int _lookingFrames;
        private int _closedFrames;
        private int _talkingFrames;
        private int _blinks;
        private bool _wasClosed;
        private double _smileSum;
        private double _concSum;

        public string? Label { get; set; }
        public double? Beauty { get; set; }
        public double? Symmetry { get; set; }
        public double? Golden { get; set; }
        public double? Thirds { get; set; }
        public double? Fifths { get; set; }
        public double LastQuality { get; private set; } = 1.0;
        public string? QualityLabel { get; private set; }
        public string? DistanceBand { get; private set; }
        public string? FaceShape { get; private set; }
        public double? HeadTiltDeg { get; private set; }
        public bool IsTalking { get; private set; }
        public int Readings { get; set; }

        public WeightedProbabilities Age { get; } = new(AgeBands.Length);
        public WeightedProbabilities Presentation { get; } = new(Presentations.Length);

        public void ObserveMetrics(FaceFrameMetrics m)
        {
            _frames++;
            LastQuality = m.Quality;
            QualityLabel = m.QualityLabel;
            DistanceBand = m.DistanceBand;
            FaceShape = m.FaceShape;
            HeadTiltDeg = m.RollDeg;
            if (m.LookingAtScreen) _lookingFrames++;
            if (m.EyesClosed) _closedFrames++;
            if (m.EyesClosed && !_wasClosed) _blinks++;
            _wasClosed = m.EyesClosed;
            _smileSum += m.SmileScore;
            _concSum += m.ConcentrationScore;
            _mouthWindow.Enqueue(m.MouthOpen);
            if (_mouthWindow.Count > 6) _mouthWindow.Dequeue();
            if (_mouthWindow.Count >= 3)
            {
                var mean = _mouthWindow.Average();
                var spread = _mouthWindow.Max() - _mouthWindow.Min();
                IsTalking = mean > 0.035 && spread > 0.025;
                if (IsTalking) _talkingFrames++;
            }
        }

        public void ObserveBlendshapes(double smile, double browDown, double blink, double jawOpen, double eyeWide)
        {
            if (_frames == 0) return;
            // Blend MediaPipe's exact 52-weight ARKit blendshapes into the running smile & concentration averages.
            _smileSum = (_smileSum * 0.75) + (Math.Clamp(smile * 100.0, 0, 100) * 0.25 * _frames);
            _concSum = (_concSum * 0.75) + (Math.Clamp(browDown * 100.0, 0, 100) * 0.25 * _frames);
            if (jawOpen > 0.55) QualityLabel = "Yawning";
            else if (eyeWide > 0.55) QualityLabel = "Surprised";
            _ = blink;
        }

        public void RecordExpectedAge(IReadOnlyList<double> probabilities)
        {
            if (probabilities.Count != AgeBandMiddles.Length) return;
            var total = 0.0;
            foreach (var p in probabilities)
            {
                if (!double.IsFinite(p) || p < 0) return;
                total += p;
            }
            if (total <= 0) return;
            var expected = 0.0;
            for (var i = 0; i < AgeBandMiddles.Length; i++) expected += (probabilities[i] / total) * AgeBandMiddles[i];
            _ageEstimates.Add(expected);
            if (_ageEstimates.Count > 25) _ageEstimates.RemoveAt(0);
        }

        private double? RobustAgeYears()
        {
            if (_ageEstimates.Count >= 3)
            {
                var sorted = _ageEstimates.OrderBy(v => v).ToList();
                return Math.Round(sorted[sorted.Count / 2], 1);
            }
            return Age.Expected(AgeBandMiddles) is { } years ? Math.Round(years, 1) : null;
        }

        public FaceReadout Readout(int slot)
        {
            var (age, ageP) = Age.Top();
            var (presentation, presentationP) = Presentation.Top();
            // OnLandmarks fires at ~2 Hz (every 500 ms), so 120 frames ≈ 1 minute.
            var blinkRate = _frames >= 6 ? Math.Round(_blinks / Math.Max(0.1, _frames / 120.0), 1) : (double?)null;
            return new FaceReadout(
                slot,
                Label,
                Beauty is { } b ? Math.Round(b, 1) : null,
                age < 0 ? null : AgeBands[age],
                Math.Round(ageP, 2),
                presentation < 0 || presentationP < MinPresentationConfidence ? null : Presentations[presentation],
                Math.Round(presentationP, 2),
                Readings,
                RobustAgeYears(),
                Symmetry,
                Golden,
                Thirds,
                Fifths,
                QualityLabel,
                _frames > 0 ? Math.Round((double)_lookingFrames / _frames, 2) : null,
                _frames > 0 ? Math.Round(_smileSum / _frames, 0) : null,
                _frames > 0 ? Math.Round(_concSum / _frames, 0) : null,
                blinkRate,
                _frames > 0 ? Math.Round((double)_closedFrames / _frames, 3) : null,
                IsTalking,
                _frames > 0 ? Math.Round((double)_talkingFrames / _frames, 2) : null,
                DistanceBand,
                FaceShape,
                HeadTiltDeg);
        }
    }

    private sealed class WeightedProbabilities(int classes)
    {
        private readonly double[] _sum = new double[classes];
        private double _weightSum;

        public void Add(IReadOnlyList<double> probabilities, double weight)
        {
            if (probabilities.Count != _sum.Length || weight <= 0) return;
            var total = 0.0;
            foreach (var p in probabilities)
            {
                if (!double.IsFinite(p) || p < 0) return;
                total += p;
            }
            if (total <= 0) return;
            for (var i = 0; i < _sum.Length; i++) _sum[i] += (probabilities[i] / total) * weight;
            _weightSum += weight;
        }

        public double? Expected(IReadOnlyList<double> values)
        {
            if (_weightSum <= 0) return null;
            var sum = 0.0;
            for (var i = 0; i < _sum.Length; i++) sum += _sum[i] * values[i];
            return sum / _weightSum;
        }

        public (int Index, double Probability) Top()
        {
            if (_weightSum <= 0) return (-1, 0);
            var best = 0;
            for (var i = 1; i < _sum.Length; i++)
                if (_sum[i] > _sum[best]) best = i;
            return (best, _sum[best] / _weightSum);
        }
    }
}
