using Microsoft.Extensions.DependencyInjection;
using PoWatch.Application.Contracts;
using PoWatch.Infrastructure.Persistence;
using PoWatch.Infrastructure.Runtime;

namespace PoWatch.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPoWatchInfrastructure(this IServiceCollection services)
    {
        // Boot-time readiness snapshot: lets the app start and report unhealthy on a dependency failure
        // instead of aborting host construction with an opaque 500.30 (see AzureStorageInitializer).
        services.AddSingleton<StartupReadiness>();

        services.AddSingleton<AzureStorageClients>();

        services.AddSingleton<ISessionRepository, AzureSessionRepository>();
        services.AddSingleton<ISensingLog, AzureSensingLog>();
        services.AddSingleton<IIngestLedger, AzureIngestLedger>();
        services.AddSingleton<IRollupStore, AzureRollupStore>();
        services.AddSingleton<ISnapshotStore, AzureSnapshotStore>();
        services.AddSingleton<IRegularStore, AzureRegularStore>();
        services.AddSingleton<IUserDataStore, AzureUserDataStore>();
        services.AddSingleton<IProfileStore, AzureProfileStore>();
        services.AddSingleton<IShareStore, AzureShareStore>();
        services.AddSingleton<IStoryStore, AzureStoryStore>();

        // One long-lived client for Open-Meteo; pooled connections are recycled so DNS changes still land.
        services.AddSingleton<IWeatherSource>(sp => new OpenMeteoWeatherSource(
            new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(15) }) { Timeout = TimeSpan.FromSeconds(15) },
            sp.GetRequiredService<Microsoft.Extensions.Caching.Hybrid.HybridCache>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<OpenMeteoWeatherSource>>()));

        services.AddSingleton<IDiagnosticsProvider, LocalDiagnosticsProvider>();

        // Runs once before the app accepts requests: creates the tables and containers.
        services.AddHostedService<AzureStorageInitializer>();

        return services;
    }
}
