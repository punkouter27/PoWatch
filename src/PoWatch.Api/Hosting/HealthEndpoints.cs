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
                await context.Response.WriteAsJsonAsync(new
                {
                    status = report.Status.ToString(),
                    durationMs = report.TotalDuration.TotalMilliseconds,
                    checks = report.Entries.Select(e => new
                    {
                        name = e.Key,
                        status = e.Value.Status.ToString(),
                        description = e.Value.Description,
                        durationMs = e.Value.Duration.TotalMilliseconds
                    })
                });
            }
        }).AllowAnonymous();

        // The fastest answer to "why did the app not come ready?". Anonymous and dependency-light, so it
        // stays reachable when a downstream dependency is degraded; 503 until storage is ready.
        app.MapGet("/diag/boot", (StartupReadiness readiness, IOptions<FeatureFlagsOptions> flags, IConfiguration config, IWebHostEnvironment env) =>
            {
                var payload = new
                {
                    environment = env.EnvironmentName,
                    ready = readiness.StorageReady,
                    storage = new
                    {
                        configured = !string.IsNullOrWhiteSpace(config["AzureStorage:ConnectionString"])
                                     || !string.IsNullOrWhiteSpace(config["AzureStorage:ServiceUri"]),
                        ready = readiness.StorageReady,
                        detail = readiness.StorageDetail
                    },
                    keyVault = new
                    {
                        enabled = flags.Value.EnableKeyVault,
                        uriConfigured = !string.IsNullOrWhiteSpace(config["KeyVault:Uri"])
                    }
                };
                return readiness.StorageReady ? Results.Ok(payload) : Results.Json(payload, statusCode: StatusCodes.Status503ServiceUnavailable);
            })
            .WithName("DiagBoot")
            .WithSummary("Per-dependency readiness (no secrets).")
            .AllowAnonymous();

        return app;
    }
}
