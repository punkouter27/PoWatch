namespace PoWatch.Shared.Services;

/// <summary>
/// What the camera reads from one face in view right now: pose-gated roll-aligned Beauty, quality-weighted
/// Age &amp; Presentation, plus blink rate (last minute), eye fatigue, iris gaze attention, smile (last few
/// seconds), concentration, talking, screen distance, geometric face shape, emotion mix and mood, and an
/// experimental camera heart rate.
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
    double? HeadTiltDeg = null,
    double? FaceRatio = null,
    string? Emotion = null,
    double? EmotionConfidence = null,
    IReadOnlyList<double>? EmotionMix = null,
    double? Mood = null,
    double? HeartRateBpm = null)
{
    /// <summary>Mean of the φ, thirds and fifths proportion scores (0–1); null before any was read.</summary>
    public double? Proportions
    {
        get
        {
            var parts = new[] { Golden, Thirds, Fifths }.OfType<double>().ToList();
            return parts.Count == 0 ? null : Math.Round(parts.Average(), 3);
        }
    }

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

    /// <summary>The emotion classifier's classes, in the order face-reader-worker.js sends them.</summary>
    public static readonly string[] Emotions = ["happy", "neutral", "surprise", "sad", "angry", "fear", "disgust"];

    /// <summary>How pleasant each emotion reads, -1 to 1, for the 0–100 mood score (50 is neutral).</summary>
    private static readonly double[] EmotionValence = [1.0, 0.0, 0.2, -0.8, -1.0, -0.7, -0.9];

    public const double EmotionSmoothing = 0.4;

    /// <summary>Landmark frames (~2 per second) the blink rate is counted over: about a minute.</summary>
    public const int BlinkWindowFrames = 120;

    /// <summary>Frames before a blink rate is shown: about ten seconds.</summary>
    public const int MinBlinkFrames = 20;

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

    /// <summary>
    /// Per slot [smile, browDown, blink, jawOpen, eyeWide] and optionally a sixth value: the blinks
    /// face-overlay.js has counted on this face so far, from every landmarker frame.
    /// </summary>
    public void OnBlendshapes(IReadOnlyList<double> slots, IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(values);
        if (slots.Count == 0) return;
        var stride = values.Count / slots.Count;
        if (stride < 5) return;
        for (var i = 0; i < slots.Count; i++)
        {
            var at = i * stride;
            if (_faces.TryGetValue((int)slots[i], out var face))
            {
                face.ObserveBlendshapes(values[at], values[at + 1], values[at + 2], values[at + 3], values[at + 4],
                    stride >= 6 ? values[at + 5] : null);
            }
        }
    }

    /// <summary>
    /// Skin colour samples for the heart rate: per slot a count, then that many [seconds, r, g, b]
    /// samples, all slots' samples one after another.
    /// </summary>
    public void OnPulse(IReadOnlyList<double> slots, IReadOnlyList<double> counts, IReadOnlyList<double> samples)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(counts);
        ArgumentNullException.ThrowIfNull(samples);
        var at = 0;
        for (var i = 0; i < slots.Count && i < counts.Count; i++)
        {
            var count = (int)counts[i];
            if (count < 0 || at + (count * 4) > samples.Count) return;
            if (_faces.TryGetValue((int)slots[i], out var face))
            {
                for (var k = 0; k < count; k++)
                {
                    var o = at + (k * 4);
                    face.Pulse.Add(samples[o], samples[o + 1], samples[o + 2], samples[o + 3]);
                }
                if (count > 0) face.Pulse.Estimate();
            }
            at += count * 4;
        }
    }

    public bool OnReading(int slot, IReadOnlyList<double> age, IReadOnlyList<double> presentation, IReadOnlyList<float> descriptor,
        IReadOnlyList<double>? emotion = null)
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
        if (emotion is not null) face.ObserveEmotion(emotion);

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
        private readonly Queue<int> _blinkWindow = new();
        private double[]? _emotion;
        private int? _countedBlinks;
        private double? _smileNow;
        private double? _ratio;
        private int _frames;
        private int _lookingFrames;
        private int _closedFrames;
        private int _talkingFrames;
        private int _blinks;
        private bool _wasClosed;
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
        public PulseEstimator Pulse { get; } = new();

        /// <summary>Blinks so far: face-overlay.js's count from every landmarker frame when it sends one, else this side's.</summary>
        private int BlinkCount => _countedBlinks ?? _blinks;

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
            _blinkWindow.Enqueue(BlinkCount);
            if (_blinkWindow.Count > BlinkWindowFrames + 1) _blinkWindow.Dequeue();
            _smileNow = _smileNow is { } smile ? smile + (0.3 * (m.SmileScore - smile)) : m.SmileScore;
            if (m.LengthToWidth > 0) _ratio = m.LengthToWidth;
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

        public void ObserveBlendshapes(double smile, double browDown, double blink, double jawOpen, double eyeWide, double? blinks = null)
        {
            if (_frames == 0) return;
            if (blinks is { } counted && double.IsFinite(counted) && counted >= 0) _countedBlinks = (int)counted;
            // MediaPipe's ARKit smile weight is the steadier read, so the current smile leans on it.
            var weighted = Math.Clamp(smile * 100.0, 0, 100);
            _smileNow = _smileNow is { } now ? now + (0.5 * (weighted - now)) : weighted;
            _concSum = (_concSum * 0.75) + (Math.Clamp(browDown * 100.0, 0, 100) * 0.25 * _frames);
            if (jawOpen > 0.55) QualityLabel = "Yawning";
            else if (eyeWide > 0.55) QualityLabel = "Surprised";
            _ = blink;
        }

        /// <summary>One reading of the emotion classifier, in <see cref="Emotions"/> order, smoothed into the face's mix.</summary>
        public void ObserveEmotion(IReadOnlyList<double> probabilities)
        {
            if (probabilities.Count != Emotions.Length) return;
            var total = 0.0;
            foreach (var p in probabilities)
            {
                if (!double.IsFinite(p) || p < 0) return;
                total += p;
            }
            if (total <= 0) return;
            if (_emotion is null)
            {
                _emotion = [.. probabilities.Select(p => p / total)];
                return;
            }
            for (var i = 0; i < _emotion.Length; i++)
                _emotion[i] += EmotionSmoothing * ((probabilities[i] / total) - _emotion[i]);
        }

        private (string? Top, double? Confidence, double? Mood) ReadEmotion()
        {
            if (_emotion is null) return (null, null, null);
            var best = 0;
            var valence = 0.0;
            for (var i = 0; i < _emotion.Length; i++)
            {
                if (_emotion[i] > _emotion[best]) best = i;
                valence += _emotion[i] * EmotionValence[i];
            }
            return (Emotions[best], Math.Round(_emotion[best], 2), Math.Round(Math.Clamp(50 + (50 * valence), 0, 100), 0));
        }

        private double? BlinkRate()
        {
            var frames = _blinkWindow.Count - 1;
            if (frames < MinBlinkFrames) return null;
            // OnLandmarks fires at ~2 Hz (every 500 ms), so 120 frames ≈ 1 minute.
            return Math.Round(Math.Max(0, _blinkWindow.Last() - _blinkWindow.Peek()) / (frames / 120.0), 1);
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
            var (emotion, emotionP, mood) = ReadEmotion();
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
                _smileNow is { } smile ? Math.Round(smile, 0) : null,
                _frames > 0 ? Math.Round(_concSum / _frames, 0) : null,
                BlinkRate(),
                _frames > 0 ? Math.Round((double)_closedFrames / _frames, 3) : null,
                IsTalking,
                _frames > 0 ? Math.Round((double)_talkingFrames / _frames, 2) : null,
                DistanceBand,
                FaceShape,
                HeadTiltDeg,
                _ratio,
                emotion,
                emotionP,
                _emotion is null ? null : [.. _emotion.Select(p => Math.Round(p, 3))],
                mood,
                Pulse.Bpm);
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
