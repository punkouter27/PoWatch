using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.Azurite;

namespace PoWatch.E2EAPI;

/// <summary>
/// Boots the full API host in the Test environment against a real Azurite storage container
/// so E2E API scenarios exercise real client-server behaviour end to end.
/// </summary>
public sealed class ApiE2EFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly AzuriteContainer _azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest")
        .WithCommand("--skipApiVersionCheck")
        .Build();

    async Task IAsyncLifetime.InitializeAsync()
    {
        if (DockerAvailability.SkipReason is not null)
        {
            return; // Every test in this tier is skipped; there is no Docker to start Azurite in.
        }

        await _azurite.StartAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (DockerAvailability.SkipReason is null)
        {
            await _azurite.StopAsync();
        }

        await base.DisposeAsync();
    }

    /// <summary>The host's environment; a test that checks Production behaviour builds its own factory with "Production".</summary>
    public string EnvironmentName { get; init; } = "Test";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureStorage:ConnectionString"] = _azurite.GetConnectionString(),
                ["FeatureFlags:EnableKeyVault"] = "false"
            }));
    }
}
