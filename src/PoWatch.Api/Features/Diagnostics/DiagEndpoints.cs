using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using PoWatch.Application.Contracts;
using PoWatch.Application.Options;
using PoWatch.Shared.Models;

namespace PoWatch.Api.Features.Diagnostics;

/// <summary>
/// The one diagnostics surface, <c>/api/diag</c> (the /diag contract in AGENTS.md):
/// <list type="bullet">
/// <item><c>GET /api/diag</c> — anonymous <see cref="DiagReportDto"/> behind the /diag page. It reads the
/// same registered health checks as <c>/health</c> but rewrites every entry in plain words of its own, so
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
                HealthCheckService health,
                IAiModelRegistry models,
                IOptions<FeatureFlagsOptions> flags,
                IConfiguration config,
                IWebHostEnvironment env,
                TimeProvider time,
                CancellationToken ct) =>
            {
                // A cached report would show a stale status on Refresh.
                http.Response.Headers.CacheControl = "no-store";
                var started = time.GetTimestamp();
                var report = await health.CheckHealthAsync(ct);

                List<DiagCheckDto> checks =
                [
                    // Answering this request is the check; the page measures the round trip itself.
                    new("api", DiagGroups.Api, "PoWatch server", nameof(HealthStatus.Healthy),
                        "Serves this app, your stats and sign-in.", "Answering", 0, null),
                ];

                foreach (var (name, entry) in report.Entries)
                {
                    var (group, label, purpose) = name switch
                    {
                        "azure-storage" => (DiagGroups.Data, "Storage", "Keeps your stats, sessions, regulars and snapshots."),
                        "azure-key-vault" => (DiagGroups.Service, "Key Vault", "Holds the server's secrets and keys."),
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

                // Sign-in: Microsoft Entra when it is configured; the guest sign-in only outside Production.
                var entra = !string.IsNullOrWhiteSpace(config["AzureAd:ClientId"]);
                var guest = flags.Value.DeveloperBypassAuth && !env.IsProduction();
                checks.Add(new("sign-in", DiagGroups.Service, "Sign-in", entra || guest ? "Healthy" : "Unhealthy",
                    "Lets you sign in to see your own stats.",
                    entra ? "Microsoft Entra" : guest ? "Guest sign-in (local use, no Microsoft tenant configured)" : "Not configured",
                    0, entra || guest ? null : "No sign-in method is configured, so nobody can sign in."));

                // AI: the server only rewords recaps and answers Ask; without a model the numbers still come through.
                var configured = models.Models.Where(m => m.Provider is not ("Template" or "Auto")).Select(m => m.Provider).Distinct().ToList();
                checks.Add(new("ai-models", DiagGroups.Service, "AI models", configured.Count > 0 ? "Healthy" : "Degraded",
                    "Rewords recaps and answers Ask.",
                    configured.Count > 0 ? $"Configured ({string.Join(", ", configured)})" : "None configured",
                    0, configured.Count > 0 ? null : "No AI model is configured, so recaps use plain templates."));

                var overall = checks.Any(c => c.Status == "Unhealthy") ? "Unhealthy" : checks.Any(c => c.Status == "Degraded") ? "Degraded" : "Healthy";
                return Results.Ok(new DiagReportDto(overall, env.EnvironmentName, Version, time.GetUtcNow(),
                    Math.Round(time.GetElapsedTime(started).TotalMilliseconds, 1), [.. checks]));
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
