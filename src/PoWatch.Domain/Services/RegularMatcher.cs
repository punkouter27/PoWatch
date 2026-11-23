using PoWatch.Domain.Models;

namespace PoWatch.Domain.Services;

/// <summary>
/// Recognises regulars by appearance: a 64-bin colour histogram (4 levels per RGB channel) of the
/// centre of the detector box, compared by cosine similarity within the same class. Deliberately
/// not face recognition — nothing biometric is computed or stored.
/// </summary>
public static class RegularMatcher
{
    public const int SignatureBins = 64;

    /// <summary>Similarity at or above this is "the same regular".</summary>
    public const double Threshold = 0.85;

    /// <summary>The most similar regular of the same class above <see cref="Threshold"/>, or null.</summary>
    public static Regular? BestMatch(IEnumerable<Regular> regulars, string className, IReadOnlyList<float> signature)
    {
        ArgumentNullException.ThrowIfNull(regulars);
        return regulars
            .Where(r => string.Equals(r.Class, className, StringComparison.OrdinalIgnoreCase))
            .Select(r => (Regular: r, Score: Similarity(r.Signature, signature)))
            .Where(m => m.Score >= Threshold)
            .OrderByDescending(m => m.Score)
            .Select(m => m.Regular)
            .FirstOrDefault();
    }

    public static double Similarity(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Count != b.Count || a.Count == 0) return 0;

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Count; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na <= 0 || nb <= 0 ? 0 : dot / Math.Sqrt(na * nb);
    }

    /// <summary>
    /// Folds a new sighting into a running average, so a regular's look drifts slowly (clothes
    /// under different light) without one odd frame overwriting it. Capped so it can still adapt.
    /// </summary>
    public static float[] Blend(IReadOnlyList<float> current, int sightings, IReadOnlyList<float> incoming)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(incoming);
        if (current.Count != incoming.Count || sightings <= 0) return [.. incoming];

        var weight = Math.Min(sightings, 20);
        return current.Select((v, i) => ((v * weight) + incoming[i]) / (weight + 1)).ToArray();
    }
}
