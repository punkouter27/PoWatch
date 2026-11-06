using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace PoWatch.Api.Hosting;

/// <summary>The <c>/health</c> body: overall status and each registered check.</summary>
internal sealed record HealthReportBody(string Status, double DurationMs, IReadOnlyList<HealthCheckBody> Checks);

internal sealed record HealthCheckBody(string Name, string Status, string? Description, double DurationMs);

/// <summary>The <c>/diag/boot</c> body: per-dependency readiness, no secrets.</summary>
internal sealed record DiagBootBody(string Environment, bool Ready, DiagBootStorage Storage, DiagBootKeyVault KeyVault);

internal sealed record DiagBootStorage(bool Configured, bool Ready, string? Detail);

internal sealed record DiagBootKeyVault(bool Enabled, bool UriConfigured);

/// <summary>
/// Source-generated JSON for the bodies the hosting code writes by hand (health, boot, the 500
/// ProblemDetails), so they serialize without reflection. Endpoint results go through the Request
/// Delegate Generator instead.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(HealthReportBody))]
[JsonSerializable(typeof(DiagBootBody))]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(string))]
internal sealed partial class ApiJson : JsonSerializerContext;
