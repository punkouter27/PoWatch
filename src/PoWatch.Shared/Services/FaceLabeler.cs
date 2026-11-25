namespace PoWatch.Shared.Services;

/// <summary>Which temporary label a face got, and how far it sat from that label's face (cosine distance).</summary>
public readonly record struct FaceMatch(string Label, double Distance);

/// <summary>
/// Labels the faces seen during one session — "Person 1", "Person 2" — by matching the descriptor a
/// face model produced against the ones already labelled. The descriptors live in this object and
/// nowhere else: they are never written to a tick, a rollup or storage, and a new session starts with
/// nobody known. This is not <see cref="Regular"/> matching, which is colour and does persist; it is
/// only "which of the people in front of me right now is which".
/// </summary>
/// <remarks>One instance per session, driven from the sensing loop: not thread-safe.</remarks>
/// <param name="threshold">
/// The cosine distance this labeler matches under: <see cref="MatchDistance"/> unless the descriptor's
/// model needs a tighter one.
/// </param>
public sealed class FaceLabeler(double threshold = FaceLabeler.MatchDistance)
{
    /// <summary>
    /// The default cosine distance under which two descriptors count as the same person. Tight enough
    /// that a different face is a new label; loose enough to survive a turned head or new light.
    /// </summary>
    public const double MatchDistance = 0.35;

    /// <summary>The cosine distance this labeler matches under.</summary>
    public double Threshold { get; } = threshold;

    /// <summary>How many people one session labels before the rest go unlabelled.</summary>
    public const int MaxLabels = 20;

    private readonly List<Known> _known = [];
    private int _issued;

    /// <summary>How many people this session has labelled.</summary>
    public int Count => _known.Count;

    /// <summary>
    /// The label for this face, labelling it as new when it matches nobody seen so far, or null when
    /// the descriptor cannot describe a face (empty, not finite, all zeros, a different width than
    /// the ones already seen, or a session that has met <see cref="MaxLabels"/> people).
    /// </summary>
    /// <param name="descriptor">The face model's descriptor.</param>
    /// <param name="exclude">
    /// Labels this face cannot have: the people already in view, so two faces side by side never share one.
    /// </param>
    public FaceMatch? Observe(IReadOnlyList<float> descriptor, IReadOnlyCollection<string>? exclude = null)
    {
        if (Unit(descriptor) is not { } unit) return null;

        Known? nearest = null;
        var nearestDistance = double.MaxValue;
        foreach (var known in _known)
        {
            // One model produces one width; a mixed feed means two descriptors of different kinds
            // arrived, and matching across them would be nonsense.
            if (known.Centroid.Length != unit.Length) return null;
            if (exclude?.Contains(known.Label) == true) continue;
            var distance = 1 - Dot(known.Centroid, unit);
            if (distance < nearestDistance)
            {
                (nearest, nearestDistance) = (known, distance);
            }
        }

        if (nearest is not null && nearestDistance <= Threshold)
        {
            nearest.Learn(unit);
            return new FaceMatch(nearest.Label, Math.Round(nearestDistance, 3));
        }

        if (_known.Count >= MaxLabels) return null;

        var label = $"Person {++_issued}";
        _known.Add(new Known(label, unit));
        return new FaceMatch(label, 0);
    }

    /// <summary>
    /// Folds another sighting into <paramref name="label"/>, for a face already known by other means
    /// (it has been followed from frame to frame), so the label is easier to recognise when they come
    /// back. False when the label is unknown or the descriptor cannot describe a face.
    /// </summary>
    public bool Learn(string label, IReadOnlyList<float> descriptor)
    {
        if (Unit(descriptor) is not { } unit) return false;
        var known = _known.Find(k => k.Label == label);
        if (known is null || known.Centroid.Length != unit.Length) return false;
        known.Learn(unit);
        return true;
    }

    /// <summary>
    /// How far <paramref name="descriptor"/> sits from <paramref name="label"/>'s face (cosine distance),
    /// or null when the label is unknown or the descriptor cannot describe a face.
    /// </summary>
    public double? DistanceTo(string label, IReadOnlyList<float> descriptor)
    {
        if (Unit(descriptor) is not { } unit) return null;
        var known = _known.Find(k => k.Label == label);
        if (known is null || known.Centroid.Length != unit.Length) return null;
        return 1 - Dot(known.Centroid, unit);
    }

    /// <summary>The descriptor scaled to length 1, or null when it is empty, not finite or all zeros.</summary>
    private static double[]? Unit(IReadOnlyList<float> descriptor)
    {
        if (descriptor is null || descriptor.Count == 0) return null;

        var unit = new double[descriptor.Count];
        var squares = 0.0;
        for (var i = 0; i < descriptor.Count; i++)
        {
            var value = descriptor[i];
            if (!float.IsFinite(value)) return null;
            unit[i] = value;
            squares += value * value;
        }
        var length = Math.Sqrt(squares);
        if (length <= 1e-9) return null;
        for (var i = 0; i < unit.Length; i++) unit[i] /= length;
        return unit;
    }

    private static double Dot(double[] a, double[] b)
    {
        var sum = 0.0;
        for (var i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    /// <summary>One person this session has labelled, with the running mean of their descriptors.</summary>
    private sealed class Known(string label, double[] first)
    {
        public string Label { get; } = label;

        public double[] Centroid { get; private set; } = first.ToArray();

        public int Seen { get; private set; } = 1;

        /// <summary>Folds a new observation in, so a label follows a face as its angle and light change.</summary>
        public void Learn(double[] unit)
        {
            for (var i = 0; i < Centroid.Length; i++)
                Centroid[i] = ((Centroid[i] * Seen) + unit[i]) / (Seen + 1);
            Seen++;

            var squares = 0.0;
            foreach (var value in Centroid) squares += value * value;
            var length = Math.Sqrt(squares);
            if (length <= 1e-9) return;
            for (var i = 0; i < Centroid.Length; i++) Centroid[i] /= length;
        }
    }
}
