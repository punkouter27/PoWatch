using Azure;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoWatch.Infrastructure.Runtime;

namespace PoWatch.Api.Hosting;

/// <summary>
/// Health check that verifies Azure Key Vault connectivity using Managed Identity.
/// Skipped gracefully when KeyVaultUri is not configured (local dev without KV).
/// </summary>
public sealed class KeyVaultHealthCheck : IHealthCheck
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<KeyVaultHealthCheck> _logger;

    public KeyVaultHealthCheck(IConfiguration configuration, ILogger<KeyVaultHealthCheck> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    // /health is anonymous and polled by the deploy gate: one client (and its token cache) for the
    // process, not a new credential chain per probe.
    private static readonly Lock s_clientLock = new();
    private static SecretClient? s_client;

    private static SecretClient ClientFor(Uri vaultUri)
    {
        lock (s_clientLock)
        {
            if (s_client?.VaultUri != vaultUri)
                s_client = new SecretClient(vaultUri, new Azure.Identity.DefaultAzureCredential());
            return s_client;
        }
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var rawUri = _configuration["KeyVault:Uri"];
        if (string.IsNullOrWhiteSpace(rawUri))
            return HealthCheckResult.Healthy("Key Vault not configured — check skipped");

        if (!Uri.TryCreate(rawUri, UriKind.Absolute, out var vaultUri))
            return HealthCheckResult.Degraded("KeyVault:Uri is not a valid absolute URI.");

        // /health is AllowAnonymous and its JSON is also rendered on the Health page, so the check
        // descriptions are public. The vault's hostname names a real tenant resource, so it is masked
        // out there and kept only in the server-side log.
        var safeVault = MaskingUtility.MaskMiddle(vaultUri.Host);

        _logger.LogDebug("Running Key Vault health check. Uri={VaultUri}", vaultUri);

        try
        {
            var client = ClientFor(vaultUri);

            // List first page of secret names — lightweight connectivity probe
            await foreach (var _ in client.GetPropertiesOfSecretsAsync(cancellationToken)
                               .AsPages(pageSizeHint: 1)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                break;
            }

            _logger.LogDebug("Key Vault health check passed. Uri={VaultUri}", vaultUri);
            return HealthCheckResult.Healthy($"Azure Key Vault is reachable ({safeVault}).");
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Key Vault health check failed. Uri={VaultUri} Status={StatusCode}", vaultUri, ex.Status);
            return HealthCheckResult.Degraded($"Azure Key Vault is unreachable ({safeVault}, HTTP {ex.Status}).", ex);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Key Vault health check timed out or was cancelled");
            return HealthCheckResult.Degraded("Key Vault health check cancelled", ex);
        }
        catch (AggregateException ex)
        {
            _logger.LogWarning(ex, "Key Vault health check failed with wrapped network error. Uri={VaultUri}", vaultUri);
            return HealthCheckResult.Degraded("Key Vault health check failed due to network error", ex);
        }
    }
}
