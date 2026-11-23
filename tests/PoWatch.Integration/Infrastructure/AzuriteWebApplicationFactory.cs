using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PoWatch.Api.Features.Diagnostics;
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
                ["FeatureFlags:EnableKeyVault"] = "false",
                // /api/diag probes Azure OpenAI with a key here, never with the machine's Azure sign-in.
                ["AzureOpenAi:ApiKey"] = "integration-test-key",
                ["ApplicationInsights:ConnectionString"] = ""
            });
        });
        // /api/diag's live probes answer from a stub, so the suite never depends on the internet.
        builder.ConfigureTestServices(services =>
            services.AddHttpClient(ServiceProbes.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new ProbeStub(WeatherStatus)));
    }

    /// <summary>What the stubbed Open-Meteo answers; a test sets 500 to see a service row degrade.</summary>
    public HttpStatusCode WeatherStatus { get; init; } = HttpStatusCode.OK;
}

/// <summary>
/// Stands in for every service /api/diag probes: Open-Meteo finds a place (or fails with the given status),
/// Azure OpenAI lists its deployments, nothing listens on localhost (a local Ollama that is not running), and
/// anything else is a 404.
/// </summary>
internal sealed class ProbeStub(HttpStatusCode weather) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        if (uri.IsLoopback)
            throw new HttpRequestException("Connection refused (stub).");
        if (uri.Host.Contains("open-meteo", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(weather == HttpStatusCode.OK ? Json("""{"results":[{"name":"London"}]}""") : new HttpResponseMessage(weather));
        if (uri.AbsolutePath.EndsWith("/openai/deployments", StringComparison.Ordinal))
            return Task.FromResult(Json("""{"data":[{"id":"gpt-5.4-nano"}]}"""));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
