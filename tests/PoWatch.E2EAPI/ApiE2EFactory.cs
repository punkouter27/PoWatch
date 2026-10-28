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

    async Task IAsyncLifetime.InitializeAsync() => await _azurite.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _azurite.StopAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureStorage:ConnectionString"] = _azurite.GetConnectionString(),
                ["FeatureFlags:EnableKeyVault"] = "false"
            }));
    }
}
