namespace PoWatch.Domain.Models;

/// <summary>
/// A recurring entity — a person, pet, car or object — recognised by how it looks, not by any
/// biometric. Regulars are never named: they read "Person 3", "Cat 1".
/// </summary>
public sealed record Regular
{
    public required string Id { get; init; }
    public required string UserId { get; init; }
    public required string Class { get; init; }

    /// <summary>Per-class counter behind the automatic name ("Person 3").</summary>
    public int Number { get; init; }

    /// <summary>Appearance signature: a normalised colour histogram (see RegularMatcher).</summary>
    public IReadOnlyList<float> Signature { get; init; } = [];

    /// <summary>How many sightings the signature averages over.</summary>
    public int Sightings { get; init; }

    public DateTimeOffset FirstSeenUtc { get; init; }
    public DateTimeOffset LastSeenUtc { get; init; }
    public long Visits { get; init; }
    public double DwellSeconds { get; init; }

    public string DisplayName => $"{char.ToUpperInvariant(Class[0])}{Class[1..]} {Number}";
}
