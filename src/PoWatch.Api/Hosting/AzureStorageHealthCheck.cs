using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoWatch.Infrastructure.Persistence;

namespace PoWatch.Api.Hosting;

// Lists one table through the app's own storage client, so the probe checks the same endpoint and
// credential every store uses, and reuses that credential's cached token instead of fetching a new one.
public sealed class AzureStorageHealthCheck(AzureStorageClients clients) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // GetPropertiesAsync requires management-plane permissions (Shared Key / Storage Account Contributor).
            // QueryAsync (list tables) is a data-plane operation supported by Storage Table Data Contributor.
            await foreach (var _ in clients.TableService.QueryAsync(maxPerPage: 1, cancellationToken: cancellationToken))
            {
                break; // Only need to confirm the call succeeds; no tables is fine.
            }
            return HealthCheckResult.Healthy("Azure Table Storage is reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"Azure Table Storage is unreachable: {ex.GetType().Name} — {ex.Message}", ex);
        }
    }
}
