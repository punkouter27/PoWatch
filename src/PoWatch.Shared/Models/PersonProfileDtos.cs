namespace PoWatch.Shared.Models;

/// <summary>
/// Carried-over appearance statistics for one anonymous face-matched person across sessions
/// (stored in Azure Table Storage; never named).
/// </summary>
public sealed class PersonAppearanceStatsDto
{
    public double? BeautyMean { get; init; }
    public double? BeautyLow { get; init; }
    public double? BeautyHigh { get; init; }
    public double? SymmetryMean { get; init; }
    public double? GoldenMean { get; init; }
    public double? ThirdsMean { get; init; }
    public double? FifthsMean { get; init; }
    public double? AgeMean { get; init; }
    public string? AgeBand { get; init; }
    public string? Presentation { get; init; }
    public int FaceSamples { get; init; }

    public double? UprightScore { get; init; }
    public double? MeanEnergy { get; init; }
    public double? OpenPostureShare { get; init; }
    public double? ArmBalance { get; init; }
    public double? LegBalance { get; init; }
    public double? ShoulderTilt { get; init; }
    public int BodySamples { get; init; }
}

public sealed class PersonProfileDto
{
    public string Id { get; init; } = string.Empty;
    public int Number { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public int SessionsSeen { get; init; }
    public DateTimeOffset FirstSeenUtc { get; init; }
    public DateTimeOffset LastSeenUtc { get; init; }
    public PersonAppearanceStatsDto Stats { get; init; } = new();
}

/// <summary>Face descriptor + current face/body appearance metrics sent to match or update a returning person.</summary>
public sealed class ObservePersonRequestDto
{
    public Guid SessionId { get; init; }
    public List<float> Descriptor { get; init; } = [];

    public double? Beauty { get; init; }
    public double? Symmetry { get; init; }
    public double? Golden { get; init; }
    public double? Thirds { get; init; }
    public double? Fifths { get; init; }
    public double? AgeYears { get; init; }
    public string? AgeBand { get; init; }
    public string? Presentation { get; init; }

    public double? UprightScore { get; init; }
    public double? MeanEnergy { get; init; }
    public double? OpenPostureShare { get; init; }
    public double? ArmBalance { get; init; }
    public double? LegBalance { get; init; }
    public double? ShoulderTilt { get; init; }
}

public sealed class ObservePersonResultDto
{
    public PersonProfileDto Person { get; init; } = new();
    public bool IsNew { get; init; }
    public bool ReturningFromPreviousSession { get; init; }
}
