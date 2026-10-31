using PoWatch.Api.Features.Ask;
using PoWatch.Api.Features.Auth;
using PoWatch.Api.Features.Data;
using PoWatch.Api.Features.Dev;
using PoWatch.Api.Features.Diagnostics;
using PoWatch.Api.Features.Digest;
using PoWatch.Api.Features.Ingest;
using PoWatch.Api.Features.Profile;
using PoWatch.Api.Features.Recaps;
using PoWatch.Api.Features.Regulars;
using PoWatch.Api.Features.Sessions;
using PoWatch.Api.Features.Shares;
using PoWatch.Api.Features.Snapshots;
using PoWatch.Api.Features.Stats;
using PoWatch.Api.Features.Stories;
using PoWatch.Api.Features.Weather;

namespace PoWatch.Api.Features;

/// <summary>Every feature slice's routes, one line each; a new slice adds its Map*Feature() here.</summary>
internal static class FeatureRoutes
{
    public static WebApplication MapPoWatchFeatures(this WebApplication app)
    {
        app.MapAuthEndpoints();
        app.MapDiagnosticsFeature();
        app.MapSessionsFeature();
        app.MapIngestFeature();
        app.MapSnapshotsFeature();
        app.MapRegularsFeature();
        app.MapRecapsFeature();
        app.MapStoriesFeature();
        app.MapStatsFeature();
        app.MapAskFeature();
        app.MapAiModelsFeature();
        app.MapDataFeature();
        app.MapProfileFeature();
        app.MapWeatherFeature();
        app.MapDigestFeature();
        app.MapSharesFeature();
        app.MapHub<StatsHub>(StatsHub.Path).RequireAuthorization();
        app.MapDevSeedFeature(app.Environment);
        return app;
    }
}
