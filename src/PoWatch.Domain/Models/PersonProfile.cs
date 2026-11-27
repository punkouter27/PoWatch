namespace PoWatch.Domain.Models;

/// <summary>Running face and body appearance totals for one persistent anonymous person profile.</summary>
public sealed record PersonProfileStats
{
    public int FaceSamples { get; init; }
    public double BeautySum { get; init; }
    public double? BeautyLow { get; init; }
    public double? BeautyHigh { get; init; }
    public double SymmetrySum { get; init; }
    public int SymmetryCount { get; init; }
    public double GoldenSum { get; init; }
    public int GoldenCount { get; init; }
    public double ThirdsSum { get; init; }
    public int ThirdsCount { get; init; }
    public double FifthsSum { get; init; }
    public int FifthsCount { get; init; }
    public double AgeSum { get; init; }
    public int AgeCount { get; init; }
    public string? AgeBand { get; init; }
    public string? Presentation { get; init; }
    public string? FaceShape { get; init; }
    public double AttentionSum { get; init; }
    public int AttentionCount { get; init; }
    public double SmileSum { get; init; }
    public int SmileCount { get; init; }
    public double ConcentrationSum { get; init; }
    public int ConcentrationCount { get; init; }
    public double BlinkRateSum { get; init; }
    public int BlinkRateCount { get; init; }
    public double FatigueSum { get; init; }
    public int FatigueCount { get; init; }
    public double TalkingSum { get; init; }
    public int TalkingCount { get; init; }
    public string? DistanceBand { get; init; }

    public int BodySamples { get; init; }
    public double UprightSum { get; init; }
    public int UprightCount { get; init; }
    public double EnergySum { get; init; }
    public int EnergyCount { get; init; }
    public double OpenPostureSum { get; init; }
    public int OpenPostureCount { get; init; }
    public double ArmBalanceSum { get; init; }
    public int ArmBalanceCount { get; init; }
    public double LegBalanceSum { get; init; }
    public int LegBalanceCount { get; init; }
    public double ShoulderTiltSum { get; init; }
    public int ShoulderTiltCount { get; init; }
}

/// <summary>
/// One anonymous face-matched person ("Person 1", "Person 2", …) persisted in Azure Table Storage so
/// subsequent sessions recognise them even in different clothes and carry their face/body appearance stats.
/// </summary>
public sealed record PersonProfile
{
    public const double MatchDistance = 0.25;
    public const int MaxProfiles = 100;

    public required string Id { get; init; }
    public required string UserId { get; init; }
    public required int Number { get; init; }
    public string? CustomName { get; init; }
    public string? ThumbnailDataUrl { get; init; }
    public required float[] Descriptor { get; init; }
    public int DescriptorCount { get; init; }
    public int SessionsSeen { get; init; }
    public Guid LastSessionId { get; init; }
    public DateTimeOffset FirstSeenUtc { get; init; }
    public DateTimeOffset LastSeenUtc { get; init; }
    public PersonProfileStats Stats { get; init; } = new();

    public string DisplayName => !string.IsNullOrWhiteSpace(CustomName) ? CustomName.Trim() : $"Person #{Number}";

    /// <summary>Cosine distance in [0, 2]; returns 2 if lengths differ or either vector is zero.</summary>
    public static double CosineDistance(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        if (a.Count == 0 || a.Count != b.Count) return 2;
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Count; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        var denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom <= 1e-9 ? 2 : Math.Clamp(1.0 - (dot / denom), 0, 2);
    }

    public static float[] Normalize(IReadOnlyList<float> v)
    {
        double norm = 0;
        for (var i = 0; i < v.Count; i++) norm += v[i] * v[i];
        var len = Math.Sqrt(norm);
        var result = new float[v.Count];
        if (len <= 1e-9) return result;
        for (var i = 0; i < v.Count; i++) result[i] = (float)(v[i] / len);
        return result;
    }

    public static float[] Blend(float[] current, int currentCount, IReadOnlyList<float> incoming)
    {
        if (current.Length != incoming.Count) return Normalize(incoming);
        var weight = Math.Min(currentCount, 20);
        var blended = new float[current.Length];
        for (var i = 0; i < current.Length; i++)
            blended[i] = ((current[i] * weight) + incoming[i]) / (weight + 1);
        return Normalize(blended);
    }
}
