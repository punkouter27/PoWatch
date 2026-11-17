using System.Reflection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using PoWatch.Application.Contracts;
using PoWatch.Application.Options;
using PoWatch.Shared.Models;

namespace PoWatch.Api.Features.Diagnostics;

/// <summary>
/// <c>GET /api/diag</c>: the anonymous data behind the friendly /diag page. It reads the same registered
/// health checks as <c>/health</c> but rewrites every entry in plain words of its own, so a check's
/// description (which may name an endpoint or quote an exception) never leaves the server.
/// </summary>
internal static class DiagEndpoints
{
    private static readonly string BuildDate = typeof(DiagEndpoints).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "BuildDate")?.Value ?? "unknown";

    internal static IEndpointRouteBuilder MapDiagFeature(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/diag", async (
                HealthCheckService health,
                IAiModelRegistry models,
                IOptions<FeatureFlagsOptions> flags,
                IConfiguration config,
                IWebHostEnvironment env,
                TimeProvider time,
                CancellationToken ct) =>
            {
                var report = await health.CheckHealthAsync(ct);
                var now = time.GetUtcNow();

                List<DiagEntryDto> stores = [];
                List<DiagEntryDto> services = [];
                foreach (var (name, entry) in report.Entries)
                {
                    var problem = entry.Exception?.GetType().Name
                        ?? (entry.Status == HealthStatus.Healthy ? null : "Check failed");
                    switch (name)
                    {
                        case "azure-storage":
                            stores.Add(new("Storage", entry.Status.ToString(), "Keeps your stats, sessions, regulars and snapshots.",
                                entry.Duration.TotalMilliseconds, now, problem));
                            break;
                        case "azure-key-vault":
                            services.Add(new("Key Vault", entry.Status.ToString(), "Holds the server's secrets and keys.",
                                entry.Duration.TotalMilliseconds, now, problem));
                            break;
                        default:
                            services.Add(new(name, entry.Status.ToString(), "A dependency the server checks.",
                                entry.Duration.TotalMilliseconds, now, problem));
                            break;
                    }
                }

                // Sign-in: Microsoft Entra when it is configured; the guest sign-in only outside Production.
                var entra = !string.IsNullOrWhiteSpace(config["AzureAd:ClientId"]);
                var guest = flags.Value.DeveloperBypassAuth && !env.IsProduction();
                services.Add(new("Sign-in", entra || guest ? "Healthy" : "Unhealthy",
                    entra ? "Microsoft Entra sign-in for your own account." : "Guest sign-in for local use (no Microsoft tenant configured).",
                    null, now, entra || guest ? null : "No sign-in method configured"));

                // AI: the server only rewords recaps and answers Ask; without a model the numbers still come through.
                var configured = models.Models.Where(m => m.Provider is not ("Template" or "Auto")).Select(m => m.Provider).Distinct().ToList();
                services.Add(new("AI models", configured.Count > 0 ? "Healthy" : "Degraded",
                    configured.Count > 0
                        ? $"Rewords recaps and answers Ask ({string.Join(", ", configured)})."
                        : "Rewords recaps and answers Ask; none configured, so recaps use plain templates.",
                    null, now, null));

                var all = stores.Concat(services).Select(e => e.Status).ToList();
                var overall = all.Contains("Unhealthy") ? "Unhealthy" : all.Contains("Degraded") ? "Degraded" : "Healthy";
                return Results.Ok(new DiagReportDto(overall, env.EnvironmentName, BuildDate, now, stores, services));
            })
            .WithTags("Diagnostics")
            .WithName("Diag")
            .WithSummary("Plain-language status of each store and service (anonymous, no secrets).")
            .Produces<DiagReportDto>(StatusCodes.Status200OK)
            .AllowAnonymous();

        return app;
    }
}
