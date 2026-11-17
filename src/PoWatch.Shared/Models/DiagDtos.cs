namespace PoWatch.Shared.Models;

/// <summary>
/// The anonymous <c>GET /api/diag</c> document behind the /diag page: plain-language status of each store
/// and external service. Never carries secrets, URIs, ids, paths or exception messages (an exception's
/// type name at most).
/// </summary>
public sealed record DiagReportDto(
    string Status,
    string Environment,
    string Version,
    DateTimeOffset CheckedAtUtc,
    IReadOnlyList<DiagEntryDto> Stores,
    IReadOnlyList<DiagEntryDto> Services);

/// <summary>One store or service: Healthy, Degraded or Unhealthy, what it is for, and how long the check took (null when not probed).</summary>
public sealed record DiagEntryDto(
    string Name,
    string Status,
    string Purpose,
    double? LatencyMs,
    DateTimeOffset LastCheckedUtc,
    string? Problem);
