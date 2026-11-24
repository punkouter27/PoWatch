using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoWatch.Api.Features.Diagnostics;
using Microsoft.Extensions.Options;
using PoWatch.Application.Options;
using PoWatch.Infrastructure;

namespace PoWatch.Api.Hosting;

/// <summary>
/// The two public operations probes: <c>/health</c> (each registered check, from the cached <see cref="HealthSnapshot"/>) and
/// <c>/api/diag/boot</c> (per-dependency readiness without secrets; under /api since 2026-11-17 so a browser
/// at /diag gets the diagnostics page). The friendly status is <c>/api/diag</c> (DiagEndpoints) and the
/// signed-in runtime snapshot is <c>/api/diag/runtime</c>.
/// </summary>
internal static class HealthEndpoints
{
    /// <summary>Azure Storage always; Key Vault when it is enabled.</summary>
    public static IServiceCollection AddPoWatchHealthChecks(this IServiceCollection services, FeatureFlagsOptions flags)
    {
        // Each check has its own deadline; the report is run and cached by HealthSnapshot (60 s), never per request.
        var checks = services.AddHealthChecks()
            .AddCheck<AzureStorageHealthCheck>("azure-storage", timeout: HealthSnapshot.PerCheckTimeout);
        if (flags.EnableKeyVault)
            checks.AddCheck<KeyVaultHealthCheck>("azure-key-vault", timeout: HealthSnapshot.PerCheckTimeout);
        services.AddSingleton<HealthSnapshot>();
        return services;
    }

    public static IEndpointRouteBuilder MapPoWatchHealth(this IEndpointRouteBuilder app)
    {
        // Readiness: the same cached report /api/diag shows (HealthSnapshot), so a load balancer, the deploy and
        // /diag agree, and polling /health cannot multiply the calls to storage. 503 only when Unhealthy.
        app.MapGet("/health", async (HttpContext context, HealthSnapshot snapshot, CancellationToken ct) =>
        {
            var report = (await snapshot.GetAsync(ct)).Value.Health;
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(new HealthReportBody(
                    report.Status.ToString(),
                    report.TotalDuration.TotalMilliseconds,
                    [.. report.Entries.Select(e => new HealthCheckBody(
                        e.Key, e.Value.Status.ToString(), e.Value.Description, e.Value.Duration.TotalMilliseconds))]),
                ApiJson.Default.HealthReportBody,
                statusCode: report.Status == HealthStatus.Unhealthy ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK);
        }).AllowAnonymous();

        // The fastest answer to "why did the app not come ready?". Anonymous and dependency-light, so it
        // stays reachable when a downstream dependency is degraded; 503 until storage is ready.
        app.MapGet("/api/diag/boot", (StartupReadiness readiness, IOptions<FeatureFlagsOptions> flags, IConfiguration config, IWebHostEnvironment env) =>
            {
                var payload = new DiagBootBody(
                    env.EnvironmentName,
                    readiness.StorageReady,
                    new DiagBootStorage(
                        !string.IsNullOrWhiteSpace(config["AzureStorage:ConnectionString"])
                        || !string.IsNullOrWhiteSpace(config["AzureStorage:ServiceUri"]),
                        readiness.StorageReady,
                        readiness.StorageDetail),
                    new DiagBootKeyVault(flags.Value.EnableKeyVault, !string.IsNullOrWhiteSpace(config["KeyVault:Uri"])));
                return Results.Json(payload, ApiJson.Default.DiagBootBody,
                    statusCode: readiness.StorageReady ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
            })
            .WithName("DiagBoot")
            .WithSummary("Per-dependency readiness (no secrets).")
            .AllowAnonymous();

        return app;
    }
}
