using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoWatch.Application.Contracts;
using PoWatch.Shared.Models;

namespace PoWatch.Api.Features.Diagnostics;

/// <summary>
/// The one diagnostics surface, <c>/api/diag</c> (the /diag contract in AGENTS.md):
/// <list type="bullet">
/// <item><c>GET /api/diag</c> — anonymous <see cref="DiagReportDto"/> behind the /diag page. It reads the
/// same cached report as <c>/health</c> (<see cref="HealthSnapshot"/>, 60 s) but rewrites every entry in plain words of its own, so
/// a check's description (which may name an endpoint or quote an exception) never leaves the server.</item>
/// <item><c>GET /api/diag/runtime</c> — signed in only: the masked server runtime snapshot that /diag
/// adds below the three sections for a signed-in operator.</item>
/// <item><c>POST /api/diag/client-error</c> — signed in only: a browser's unhandled error, logged.</item>
/// </list>
/// </summary>
internal static class DiagEndpoints
{
    /// <summary>The <c>YYMMDD</c> BuildVersion Directory.Build.props stamps on every assembly.</summary>
    private static readonly string Version = typeof(DiagEndpoints).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "BuildVersion")?.Value ?? "unknown";

    internal static IEndpointRouteBuilder MapDiagFeature(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/diag", async (
                HttpContext http,
                HealthSnapshot snapshot,
                IWebHostEnvironment env,
                CancellationToken ct) =>
            {
                // The report itself is cached on the server for a minute (HealthSnapshot); the browser must
                // not keep an older copy on top of that.
                http.Response.Headers.CacheControl = "no-store";
                var taken = await snapshot.GetAsync(ct);
                var report = taken.Value.Health;

                List<DiagCheckDto> checks =
                [
                    // Answering this request is the check; the page measures the round trip itself.
                    new("api", DiagGroups.Api, "PoWatch server", nameof(HealthStatus.Healthy),
                        "Serves this app, your stats and sign-in.", "Answering", 0, null),
                ];

                // Storage (and any other registered health check) comes from the health checks; Key Vault is
                // probed with the services below, so its health-check entry is not shown twice.
                foreach (var (name, entry) in report.Entries.Where(e => e.Key != "azure-key-vault"))
                {
                    var (group, label, purpose) = name switch
                    {
                        "azure-storage" => (DiagGroups.Data, "Storage", "Keeps your stats, sessions, regulars and snapshots."),
                        _ => (DiagGroups.Service, name, "A dependency the server checks."),
                    };
                    var problem = entry.Status == HealthStatus.Healthy
                        ? null
                        : (entry.Status == HealthStatus.Degraded ? "Answering, but slowly or only in part." : "Not reachable, so features that need it will fail.")
                          + (entry.Exception is null ? string.Empty : $" ({entry.Exception.GetType().Name})");
                    checks.Add(new(name, group, label, entry.Status.ToString(), purpose,
                        entry.Status switch { HealthStatus.Healthy => "Working", HealthStatus.Degraded => "Partly working", _ => "Not working" },
                        Math.Round(entry.Duration.TotalMilliseconds, 1), problem));
                }

                // Every external service, each reached with a live call (cached with the checks above). "Off"
                // rows are switched off on purpose and never count toward the overall status.
                checks.AddRange(taken.Value.Services);

                var counted = checks.Where(c => c.Status != ServiceProbes.Off).ToList();
                var overall = counted.Any(c => c.Status == "Unhealthy") ? "Unhealthy" : counted.Any(c => c.Status == "Degraded") ? "Degraded" : "Healthy";
                return Results.Ok(new DiagReportDto(overall, env.EnvironmentName, Version, taken.At,
                    Math.Round(taken.Took.TotalMilliseconds, 1), [.. checks]));
            })
            .WithTags("Diagnostics")
            .WithName("Diag")
            .WithSummary("Plain-language status of the API, each store and each service (anonymous, no secrets).")
            .Produces<DiagReportDto>(StatusCodes.Status200OK)
            .AllowAnonymous();

        var signedIn = app.MapGroup("/api/diag").WithTags("Diagnostics").RequireAuthorization();

        signedIn.MapGet("/runtime", (IDiagnosticsProvider provider, ILogger<Program> logger) =>
        {
            logger.LogDebug(
                "Diagnostics runtime request received. TraceId={TraceId}",
                Activity.Current?.TraceId.ToString());
            return Results.Ok(provider.CaptureSnapshot());
        })
        .WithName("DiagRuntime")
        .WithSummary("Get the masked server runtime snapshot (signed in).")
        .Produces<DiagnosticsSnapshotDto>(StatusCodes.Status200OK);

        // A browser that hit an unhandled error says so (js/error-report.js), so a crash on someone's
        // screen lands in the server log with its stack instead of vanishing with the tab.
        signedIn.MapPost("/client-error", (ClientErrorReport report, ILogger<Program> logger) =>
        {
            logger.LogWarning("Client error at {Url} ({UserAgent}): {Message}{NewLine}{Stack}",
                Clip(report.Url, 300), Clip(report.UserAgent, 200), Clip(report.Message, 1000), Environment.NewLine, Clip(report.Stack, 6000));
            return Results.NoContent();
        })
        .WithName("ClientError")
        .WithSummary("Log an unhandled error a signed-in browser hit.")
        .Produces(StatusCodes.Status204NoContent);

        return app;
    }

    private static string Clip(string? text, int max) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Length <= max ? text : text[..max] + "…";
}

/// <summary>What a browser reports about an unhandled error; every field is clipped before it is logged.</summary>
internal sealed record ClientErrorReport(string? Message, string? Stack, string? Url, string? UserAgent);
