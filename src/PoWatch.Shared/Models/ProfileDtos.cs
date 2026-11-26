namespace PoWatch.Shared.Models;

/// <summary>A place to take the weather from, as the place search returns it.</summary>
public sealed class PlaceDto
{
    public string Name { get; init; } = string.Empty;

    /// <summary>Region and country, to tell two Springfields apart.</summary>
    public string? Detail { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public string? TimeZoneId { get; init; }
}

/// <summary>The choices the server acts on for the caller (retention, weather place, weekly email).</summary>
public sealed class ProfileDto
{
    /// <summary>Days of raw detail kept; null keeps everything.</summary>
    public int? RetentionDays { get; init; }
    public List<int> RetentionChoices { get; init; } = [];
    public PlaceDto? Place { get; init; }
    public bool DigestEmail { get; init; }
    public string? Email { get; init; }

    /// <summary>False when this server has no way to send mail; the digest is then in-app only.</summary>
    public bool DigestEmailAvailable { get; init; }
}

/// <summary>How the caller arranges Live's panels: the order they fill the grid in, and which are hidden.</summary>
public sealed class LiveLayoutDto
{
    public List<string> Order { get; init; } = [];
    public List<string> Hidden { get; init; } = [];
}

/// <summary>The whole set of choices, replaced at once.</summary>
public sealed class UpdateProfileDto
{
    public int? RetentionDays { get; init; }
    public PlaceDto? Place { get; init; }
    public bool DigestEmail { get; init; }
    public string? TimeZoneId { get; init; }
}
