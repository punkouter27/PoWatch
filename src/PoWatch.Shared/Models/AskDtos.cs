namespace PoWatch.Shared.Models;

/// <summary>A watch rule fired ("person seen at 23:14"), relayed over the stats hub to every open tab.</summary>
public sealed class AlertDto
{
    public const int MaxLength = 120;

    public DateTimeOffset AtUtc { get; init; }
    public string Text { get; init; } = string.Empty;
}

/// <summary>"Tell me if a person shows up after 10pm": a watch rule in the user's own words.</summary>
public sealed class RuleDraftRequestDto
{
    public const int MaxLength = 200;

    public string Text { get; set; } = string.Empty;
}

/// <summary>The Add-rule form: what to watch for and between which local hours (kept in the browser, not posted).</summary>
public sealed class WatchRuleInputDto
{
    /// <summary>"Anything": a rule that fires on every detector label.</summary>
    public const string Any = "*";

    /// <summary>A detector label (<see cref="Services.DetectorLabels.Coco"/>) or <see cref="Any"/>.</summary>
    public string Class { get; set; } = "person";
    public int FromHour { get; set; } = 22;
    public int ToHour { get; set; } = 6;
}

/// <summary>A watch rule a model drafted from a sentence; the user still confirms it before it is kept.</summary>
public sealed class RuleDraftDto
{
    /// <summary>A detector label, or "*" for anything.</summary>
    public string Class { get; init; } = string.Empty;
    public int FromHour { get; init; }
    public int ToHour { get; init; }
}
