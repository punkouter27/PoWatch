using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.Azurite;

namespace PoWatch.Integration;

/// <summary>
/// WebApplicationFactory that starts a real Azurite container via Docker before tests run,
/// then injects its connection string into the API configuration so integration tests
/// exercise actual Azure Table/Blob Storage behaviour without touching cloud resources.
/// </summary>
public sealed class AzuriteWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly AzuriteContainer _azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest")
        .WithCommand("--skipApiVersionCheck")
        .Build();

    /// <summary>The running Azurite container's connection string, for tests that build stores directly.</summary>
    public string StorageConnectionString => _azurite.GetConnectionString();

    async Task IAsyncLifetime.InitializeAsync() => await _azurite.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _azurite.StopAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Integration suite is the Test environment: guest/dev bypass on, Key Vault off.
        builder.UseEnvironment("Test");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureStorage:ConnectionString"] = _azurite.GetConnectionString(),
                ["FeatureFlags:DeveloperBypassAuth"] = "true",
                ["FeatureFlags:EnableKeyVault"] = "false"
            });
        });
    }
}
