namespace PoWatch.Shared.Models;

/// <summary>
/// The anonymous <c>GET /api/diag</c> document (the /diag contract in AGENTS.md) behind the /diag page
/// and the mobile app's status page: plain-language status of the API, each store and each external
/// service. Never carries secrets, URIs, ids, paths or exception messages (an exception's type name at most).
/// </summary>
/// <param name="Status">Worst status across every check: Healthy, Degraded or Unhealthy.</param>
/// <param name="Environment">Hosting environment name (Development, Production).</param>
/// <param name="Version">The build version, <c>YYMMDD</c> (the header badge shows it as <c>vYYMMDD</c>).</param>
/// <param name="CheckedAt">When the server ran the checks.</param>
/// <param name="CheckedMs">Server-side time spent running the checks.</param>
/// <param name="Checks">One row per dependency, grouped api / data / service.</param>
public sealed record DiagReportDto(
    string Status,
    string Environment,
    string Version,
    DateTimeOffset CheckedAt,
    double CheckedMs,
    DiagCheckDto[] Checks);

/// <summary>One dependency on <c>/api/diag</c>.</summary>
/// <param name="Name">Stable machine name, e.g. <c>azure-storage</c>.</param>
/// <param name="Group"><see cref="DiagGroups"/>: api, data or service.</param>
/// <param name="Label">Display name, e.g. "Storage".</param>
/// <param name="Status">Healthy, Degraded or Unhealthy.</param>
/// <param name="Purpose">One line on what the app uses it for.</param>
/// <param name="Detail">A short plain-English state ("Working", "Configured", "Guest sign-in").</param>
/// <param name="LatencyMs">How long the probe took; 0 when nothing was probed (a configuration check).</param>
/// <param name="Problem">What is wrong, in plain words, when the check is not Healthy; otherwise null.</param>
public sealed record DiagCheckDto(
    string Name,
    string Group,
    string Label,
    string Status,
    string Purpose,
    string Detail,
    double LatencyMs,
    string? Problem);

/// <summary>Values of <see cref="DiagCheckDto.Group"/>, in the order /diag shows them.</summary>
public static class DiagGroups
{
    public const string Api = "api";
    public const string Data = "data";
    public const string Service = "service";
}
