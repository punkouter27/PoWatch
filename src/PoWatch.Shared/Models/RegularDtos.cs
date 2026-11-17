namespace PoWatch.Shared.Models;

public sealed class RegularDto
{
    public string Id { get; init; } = string.Empty;
    public string Class { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool IsNamed { get; init; }
    public DateTimeOffset FirstSeenUtc { get; init; }
    public DateTimeOffset LastSeenUtc { get; init; }
    public long Visits { get; init; }
    public double DwellSeconds { get; init; }
}

/// <summary>A tracked person, pet or object the browser wants recognised.</summary>
public sealed class ObserveRegularRequestDto
{
    public string Class { get; init; } = string.Empty;

    /// <summary>64-bin colour histogram of the track's box (not a face; nothing biometric).</summary>
    public List<float> Signature { get; init; } = [];
}

public sealed class ObserveRegularResultDto
{
    public RegularDto Regular { get; init; } = new();

    /// <summary>True when nobody like this had been seen before — the moment to offer naming them.</summary>
    public bool IsNew { get; init; }
}

public sealed class RenameRegularRequestDto
{
    public const int MaxLength = 40;

    /// <summary>The new name; blank makes the regular anonymous again ("Person 3").</summary>
    public string? Name { get; set; }
}

public sealed class MergeRegularsRequestDto
{
    public string PrimaryId { get; init; } = string.Empty;
    public string DuplicateId { get; init; } = string.Empty;
}
