using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PoWatch.Application.Features.Ask;
using PoWatch.Application.Features.Data;
using PoWatch.Application.Features.Digest;
using PoWatch.Application.Features.Ingest;
using PoWatch.Application.Features.Recaps;
using PoWatch.Application.Features.Regulars;
using PoWatch.Application.Features.Sessions;
using PoWatch.Application.Features.Shares;
using PoWatch.Application.Features.Stats;
using PoWatch.Application.Features.Stories;
using PoWatch.Application.Features.Weather;

namespace PoWatch.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddPoWatchApplication(this IServiceCollection services)
    {
        // Sessions
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<SessionService>();
        services.AddScoped<IngestService>();
        services.AddScoped<StatsQueryService>();
        services.AddScoped<RegularsService>();
        services.AddScoped<RecapService>();
        services.AddScoped<StoryService>();
        services.AddScoped<PeriodStoryService>();
        services.AddScoped<AskService>();
        services.AddScoped<AssistService>();
        services.AddScoped<RetentionService>();
        services.AddScoped<WeatherService>();
        services.AddScoped<DigestService>();
        services.AddScoped<ShareService>();
        services.AddScoped<DigestMailer>();

        return services;
    }
}
