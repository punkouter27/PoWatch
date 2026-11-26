namespace PoWatch.Shared.Services;

/// <summary>
/// What the camera reads from one face in view right now. <see cref="Label"/> is null until the face
/// model has read it once; <see cref="AgeBand"/> and <see cref="Presentation"/> are the most likely
/// class over every reading of this face so far, with that class's mean probability. Presentation is
/// null below <see cref="FaceReadings.MinPresentationConfidence"/>: the model is unsure, and saying so
/// is better than a guess.
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
    double? AgeYears = null)
{
    /// <summary>
    /// Whether the presentation model's estimate is male; false for female, null while unclear. Follows
    /// <see cref="Presentation"/>, so a low-confidence guess counts for neither.
    /// </summary>
    public bool? Male => Presentation switch
    {
        "male" => true,
        "female" => false,
        _ => null,
    };
}

/// <summary>
/// The faces in front of the camera during one session, and what has been read from them: traditional
/// beauty from the landmarks (<see cref="FaceGeometry"/>, smoothed over frames), an age band and a
/// presentation estimate from two image classifiers, and a temporary label from <see cref="FaceLabeler"/>.
/// It lives in memory for the session and is never stored or sent (ADR 0001 and 0002).
/// </summary>
/// <remarks>
/// The page follows each face from frame to frame and gives it a slot number; a slot keeps its label
/// for as long as it is followed. The descriptor is only needed when a slot is new, to tell a face
/// that has come back from one never seen. One instance per session: not thread-safe.
/// </remarks>
public sealed class FaceReadings
{
    /// <summary>The age model's classes (jdp8/vit-age-classifier), in its output order.</summary>
    public static readonly string[] AgeBands = ["0–2", "3–9", "10–19", "20–29", "30–39", "40–49", "50–59", "60–69", "70+"];

    /// <summary>Each age band's middle in years (70+ taken as 75), for an expected age that moves smoothly.</summary>
    public static readonly double[] AgeBandMiddles = [1, 6, 14.5, 24.5, 34.5, 44.5, 54.5, 64.5, 75];

    /// <summary>The presentation model's classes (onnx-community/gender-classification-ONNX), in its output order.</summary>
    public static readonly string[] Presentations = ["female", "male"];

    /// <summary>
    /// Below this mean probability the presentation is shown as unclear. The model is surest of men:
    /// on 40 LFW photos every one of its 5 misses was a woman, most of them above 0.9.
    /// </summary>
    public const double MinPresentationConfidence = 0.8;

    /// <summary>
    /// Cosine distance under which two DINOv2-small face crops count as one person. DINOv2 is a general
    /// image model, not a face recogniser: on LFW, 5% of different-person pairs were within 0.3 and
    /// half of same-person pairs were past 0.39. So matching is tight, and only decides whether a new
    /// slot is someone who came back; a miss gives them a new number rather than someone else's.
    /// </summary>
    public const double DescriptorMatchDistance = 0.2;

    /// <summary>
    /// A followed face whose descriptor is this far from its own label is someone else who took its
    /// place (one person leaning into the spot another just left), so the slot starts over rather than
    /// averaging two people together. Far past the drift one face shows within a session.
    /// </summary>
    public const double SwapDistance = 0.45;

    /// <summary>How much of each new frame's beauty score goes into the shown one, so a blink does not jump it.</summary>
    public const double BeautySmoothing = 0.2;

    /// <summary>x and y per mesh point, per face, in what <see cref="OnLandmarks"/> is handed.</summary>
    public const int MeshValues = MediaPipeFace.MeshPoints * 2;

    private readonly FaceLabeler _labeler = new(DescriptorMatchDistance);
    private readonly SortedDictionary<int, Face> _faces = [];

    /// <summary>How many people this session has labelled.</summary>
    public int Labelled => _labeler.Count;

    /// <summary>Whether the face models have read anyone this session (so they have loaded).</summary>
    public bool HasRead { get; private set; }

    /// <summary>The faces in view, in slot order.</summary>
    public IReadOnlyList<FaceReadout> InView =>
        [.. _faces.Select(pair => pair.Value.Readout(pair.Key))];

    /// <summary>
    /// One frame's faces: a slot number each, and per slot <see cref="MeshValues"/> values (the 478
    /// mesh points as x, y in units of the frame's width). Slots not in this frame have left view.
    /// </summary>
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
            if (MediaPipeFace.ToIbug68(meshes, offset) is { } points && FaceGeometry.Read(points) is { } score)
            {
                face.Beauty = face.Beauty is { } shown
                    ? shown + (BeautySmoothing * (score.Beauty - shown))
                    : score.Beauty;
            }
        }
        foreach (var gone in _faces.Keys.Where(slot => !seen.Contains(slot)).ToList()) _faces.Remove(gone);
    }

    /// <summary>
    /// The face models' reading of one slot's face: age and presentation probabilities in the models'
    /// class order, and a descriptor for its label. False when the slot has left view since.
    /// </summary>
    public bool OnReading(int slot, IReadOnlyList<double> age, IReadOnlyList<double> presentation, IReadOnlyList<float> descriptor)
    {
        ArgumentNullException.ThrowIfNull(age);
        ArgumentNullException.ThrowIfNull(presentation);
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!_faces.TryGetValue(slot, out var face)) return false;

        if (face.Label is { } held && _labeler.DistanceTo(held, descriptor) > SwapDistance)
        {
            // Someone else in the same spot: nothing read so far is about them.
            _faces[slot] = face = new Face();
        }

        face.Age.Add(age);
        face.Presentation.Add(presentation);
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
        public string? Label { get; set; }

        public double? Beauty { get; set; }

        public int Readings { get; set; }

        public MeanProbabilities Age { get; } = new(AgeBands.Length);

        public MeanProbabilities Presentation { get; } = new(Presentations.Length);

        public FaceReadout Readout(int slot)
        {
            var (age, ageP) = Age.Top();
            var (presentation, presentationP) = Presentation.Top();
            return new FaceReadout(
                slot,
                Label,
                Beauty is { } b ? Math.Round(b, 1) : null,
                age < 0 ? null : AgeBands[age],
                Math.Round(ageP, 2),
                presentation < 0 || presentationP < MinPresentationConfidence ? null : Presentations[presentation],
                Math.Round(presentationP, 2),
                Readings,
                Age.Expected(AgeBandMiddles) is { } years ? Math.Round(years, 1) : null);
        }
    }

    /// <summary>The running mean of a classifier's probabilities; readings of the wrong width or not finite are dropped.</summary>
    private sealed class MeanProbabilities(int classes)
    {
        private readonly double[] _sum = new double[classes];
        private int _count;

        public void Add(IReadOnlyList<double> probabilities)
        {
            if (probabilities.Count != _sum.Length) return;
            var total = 0.0;
            foreach (var p in probabilities)
            {
                if (!double.IsFinite(p) || p < 0) return;
                total += p;
            }
            if (total <= 0) return;
            for (var i = 0; i < _sum.Length; i++) _sum[i] += probabilities[i] / total;
            _count++;
        }

        /// <summary>The probability-weighted mean of one value per class; null before any reading.</summary>
        public double? Expected(IReadOnlyList<double> values)
        {
            if (_count == 0) return null;
            var sum = 0.0;
            for (var i = 0; i < _sum.Length; i++) sum += _sum[i] * values[i];
            return sum / _count;
        }

        /// <summary>The most likely class and its mean probability, or (-1, 0) before any reading.</summary>
        public (int Index, double Probability) Top()
        {
            if (_count == 0) return (-1, 0);
            var best = 0;
            for (var i = 1; i < _sum.Length; i++)
                if (_sum[i] > _sum[best]) best = i;
            return (best, _sum[best] / _count);
        }
    }
}
