namespace PoWatch.Shared.Services;

/// <summary>A regular's voice: the mean of the speaker embeddings heard from them, unit length.</summary>
public sealed class VoicePrint
{
    public float[] Vector { get; set; } = [];

    /// <summary>How many lines it was learned from (capped, so it keeps adapting).</summary>
    public int Lines { get; set; }
}

/// <summary>
/// Who said it, by voice. A voiceprint is learned for a regular when they are the only person in frame
/// while someone speaks; later speech that sounds like them is theirs even off camera. Voiceprints are
/// biometric, so the book lives in this browser only (localStorage) and is never sent to the server;
/// only the regular's id travels with each line heard.
/// </summary>
public sealed class VoiceBook(Dictionary<string, VoicePrint>? prints = null)
{
    /// <summary>The cosine at which two WavLM x-vectors are taken for the same speaker (the model's own suggestion is 0.86).</summary>
    public const double SameSpeaker = 0.86;

    /// <summary>The learned mean stops weighting old lines more than this many, so a voice can drift.</summary>
    private const int MaxLines = 50;

    private readonly Dictionary<string, VoicePrint> _prints = prints ?? new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, VoicePrint> Prints => _prints;

    public bool Knows(string regularId) => _prints.ContainsKey(regularId);

    /// <summary>The regular whose voice this sounds most like, if any sounds enough like it.</summary>
    public string? Match(IReadOnlyList<float> voice)
    {
        ArgumentNullException.ThrowIfNull(voice);
        string? best = null;
        var bestCosine = SameSpeaker;
        foreach (var (id, print) in _prints)
        {
            if (print.Vector.Length != voice.Count) continue;
            var cosine = Cosine(print.Vector, voice);
            if (cosine >= bestCosine) (best, bestCosine) = (id, cosine);
        }
        return best;
    }

    /// <summary>Folds one line's embedding into the regular's voiceprint.</summary>
    public void Learn(string regularId, IReadOnlyList<float> voice)
    {
        ArgumentNullException.ThrowIfNull(voice);
        if (voice.Count == 0) return;
        if (!_prints.TryGetValue(regularId, out var print) || print.Vector.Length != voice.Count)
        {
            _prints[regularId] = new VoicePrint { Vector = Normalized([.. voice]), Lines = 1 };
            return;
        }
        var n = Math.Min(print.Lines, MaxLines);
        var mean = new float[voice.Count];
        for (var i = 0; i < mean.Length; i++) mean[i] = ((print.Vector[i] * n) + voice[i]) / (n + 1);
        print.Vector = Normalized(mean);
        print.Lines = Math.Min(print.Lines + 1, MaxLines);
    }

    /// <summary>Two regulars merged into one: the voice of the one that goes is kept only if the other had none.</summary>
    public void Merge(string keptId, string goneId)
    {
        if (_prints.Remove(goneId, out var gone) && !_prints.ContainsKey(keptId)) _prints[keptId] = gone;
    }

    public void Clear() => _prints.Clear();

    private static double Cosine(float[] a, IReadOnlyList<float> b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }

    private static float[] Normalized(float[] v)
    {
        var norm = Math.Sqrt(v.Sum(x => (double)x * x));
        if (norm == 0) return v;
        for (var i = 0; i < v.Length; i++) v[i] = (float)(v[i] / norm);
        return v;
    }
}
