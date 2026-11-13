namespace PoWatch.Domain.Models;

/// <summary>The 16×9 grid laid over the camera frame for motion and position stats.</summary>
public static class SpatialGrid
{
    public const int Columns = 16;
    public const int Rows = 9;
    public const int Cells = Columns * Rows;

    /// <summary>
    /// Writes a grid as bytes relative to its busiest cell (255), so a quiet minute and a busy one
    /// both show where in the frame things happened. An empty or all-zero grid writes zeros.
    /// </summary>
    public static void Quantize(ReadOnlySpan<float> values, Span<byte> into)
    {
        into.Clear();
        var max = 0f;
        foreach (var v in values) if (v > max) max = v;
        if (max <= 0) return;
        for (var i = 0; i < Math.Min(values.Length, into.Length); i++)
            into[i] = (byte)Math.Round(Math.Clamp(values[i] / max, 0f, 1f) * 255f);
    }
}

/// <summary>Per-class detector counts folded over one tick: the peak and the average seen.</summary>
public sealed record ClassCount(int Max, double Mean);

/// <summary>
/// Ten seconds of folded sensing: the unit of ingest. The browser samples the pixel layer several
/// times a second and the detector about once a second; a tick carries the summary of those samples
/// plus how many there were, so rates stay honest when a background tab gets throttled.
/// </summary>
public sealed record Tick
{
    public const int MaxDurationSeconds = 10;
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    public required Guid SessionId { get; init; }
    public required DateTimeOffset StartUtc { get; init; }
    public double DurationSeconds { get; init; } = MaxDurationSeconds;

    public int PixelSamples { get; init; }
    public int DetectorSamples { get; init; }

    /// <summary>Fraction of the frame that changed, averaged and peaked over the tick, in [0, 1].</summary>
    public double MotionMean { get; init; }
    public double MotionMax { get; init; }

    /// <summary>Mean brightness in [0, 1].</summary>
    public double LuminanceMean { get; init; }

    /// <summary>Loudness heard by the microphone in [0, 1] (−60 dBFS to 0), averaged and peaked; 0 when not listening.</summary>
    public double SoundMean { get; init; }

    public double SoundMax { get; init; }

    /// <summary>Person-seconds spent sitting, standing and lying down; zero when posture was not read.</summary>
    public double SittingSeconds { get; init; }

    public double StandingSeconds { get; init; }

    public double LyingSeconds { get; init; }

    /// <summary>Up to five dominant colours as 0xRRGGBB.</summary>
    public IReadOnlyList<int> Palette { get; init; } = [];

    /// <summary>Motion per cell of the <see cref="SpatialGrid"/>, in [0, 1]; empty when not sampled.</summary>
    public IReadOnlyList<float> MotionGrid { get; init; } = [];

    /// <summary>How often a person or animal was centred in each <see cref="SpatialGrid"/> cell; empty when not sampled.</summary>
    public IReadOnlyList<float> PresenceGrid { get; init; } = [];

    public IReadOnlyDictionary<string, ClassCount> Classes { get; init; } = new Dictionary<string, ClassCount>();

    public IReadOnlyList<string> ActiveTrackIds { get; init; } = [];

    /// <summary>Returns every broken invariant; empty means the tick is valid.</summary>
    public IReadOnlyList<string> Validate(DateTimeOffset nowUtc)
    {
        var errors = new List<string>();

        if (SessionId == Guid.Empty)
            errors.Add($"{nameof(SessionId)} is required.");
        if (StartUtc > nowUtc + MaxClockSkew)
            errors.Add($"{nameof(StartUtc)} is more than {MaxClockSkew.TotalMinutes:0} minutes in the future.");
        if (DurationSeconds is <= 0 or > MaxDurationSeconds)
            errors.Add($"{nameof(DurationSeconds)} must be in (0, {MaxDurationSeconds}].");
        if (PixelSamples < 0)
            errors.Add($"{nameof(PixelSamples)} cannot be negative.");
        if (DetectorSamples < 0)
            errors.Add($"{nameof(DetectorSamples)} cannot be negative.");

        RequireFraction(errors, nameof(MotionMean), MotionMean);
        RequireFraction(errors, nameof(MotionMax), MotionMax);
        RequireFraction(errors, nameof(LuminanceMean), LuminanceMean);
        RequireFraction(errors, nameof(SoundMean), SoundMean);
        RequireFraction(errors, nameof(SoundMax), SoundMax);
        RequirePersonSeconds(errors, nameof(SittingSeconds), SittingSeconds);
        RequirePersonSeconds(errors, nameof(StandingSeconds), StandingSeconds);
        RequirePersonSeconds(errors, nameof(LyingSeconds), LyingSeconds);

        if (Palette.Count > 5 || Palette.Any(c => c is < 0 or > 0xFFFFFF))
            errors.Add($"{nameof(Palette)} holds at most five 0xRRGGBB colours.");
        if (MotionGrid.Count is not (0 or SpatialGrid.Cells) || MotionGrid.Any(v => v is < 0 or > 1 || float.IsNaN(v)))
            errors.Add($"{nameof(MotionGrid)} must be empty or {SpatialGrid.Cells} values in [0, 1].");
        if (PresenceGrid.Count is not (0 or SpatialGrid.Cells) || PresenceGrid.Any(v => v < 0 || float.IsNaN(v)))
            errors.Add($"{nameof(PresenceGrid)} must be empty or {SpatialGrid.Cells} non-negative values.");

        foreach (var (name, count) in Classes)
        {
            if (string.IsNullOrWhiteSpace(name) || count.Max < 0 || count.Mean < 0 || count.Mean > count.Max)
                errors.Add($"Class '{name}' has an invalid count.");
        }

        return errors;
    }

    /// <summary>The most people whose posture one tick may count: more than a webcam frame ever holds.</summary>
    public const int MaxPosturePeople = 50;

    private static void RequirePersonSeconds(List<string> errors, string name, double value)
    {
        if (value is < 0 or > MaxDurationSeconds * MaxPosturePeople || double.IsNaN(value))
            errors.Add($"{name} must be in [0, {MaxDurationSeconds * MaxPosturePeople}].");
    }

    private static void RequireFraction(List<string> errors, string name, double value)
    {
        if (value is < 0 or > 1 || double.IsNaN(value))
            errors.Add($"{name} must be in [0, 1].");
    }
}
