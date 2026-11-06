using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using PoWatch.Application.Options;
using PoWatch.Infrastructure;

namespace PoWatch.Api.Hosting;

/// <summary>
/// The two public operations probes: <c>/health</c> (each registered check, the deploy gate) and
/// <c>/diag/boot</c> (per-dependency readiness without secrets). The signed-in runtime snapshot is
/// <c>/api/diagnostics/status</c>.
/// </summary>
internal static class HealthEndpoints
{
    /// <summary>Azure Storage always; Key Vault when it is enabled.</summary>
    public static IServiceCollection AddPoWatchHealthChecks(this IServiceCollection services, FeatureFlagsOptions flags)
    {
        var checks = services.AddHealthChecks().AddCheck<AzureStorageHealthCheck>("azure-storage");
        if (flags.EnableKeyVault)
            checks.AddCheck<KeyVaultHealthCheck>("azure-key-vault");
        return services;
    }

    public static IEndpointRouteBuilder MapPoWatchHealth(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = async (context, report) =>
            {
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new HealthReportBody(
                    report.Status.ToString(),
                    report.TotalDuration.TotalMilliseconds,
                    [.. report.Entries.Select(e => new HealthCheckBody(
                        e.Key, e.Value.Status.ToString(), e.Value.Description, e.Value.Duration.TotalMilliseconds))]),
                    ApiJson.Default.HealthReportBody);
            }
        }).AllowAnonymous();

        // The fastest answer to "why did the app not come ready?". Anonymous and dependency-light, so it
        // stays reachable when a downstream dependency is degraded; 503 until storage is ready.
        app.MapGet("/diag/boot", (StartupReadiness readiness, IOptions<FeatureFlagsOptions> flags, IConfiguration config, IWebHostEnvironment env) =>
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
