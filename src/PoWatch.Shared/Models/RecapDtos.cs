namespace PoWatch.Shared.Models;

public sealed class RecapNumberDto
{
    public string Label { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
}

/// <summary>A readable recap of a session or a day: a paragraph, the headline numbers, the best moments.</summary>
public sealed record RecapDto
{
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public DateTimeOffset FromUtc { get; init; }
    public DateTimeOffset ToUtc { get; init; }
    public string Summary { get; init; } = string.Empty;
    public List<string> Highlights { get; init; } = [];
    public List<RecapNumberDto> Numbers { get; init; } = [];
    public List<MomentDto> Moments { get; init; } = [];

    /// <summary>template, or the server model's provider name — who wrote the paragraph.</summary>
    public string Source { get; init; } = "template";

    /// <summary>Optional session-level face and body aggregates for session recaps and PDFs.</summary>
    public SessionSummaryDto? SessionSummary { get; init; }
}
