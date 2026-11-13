using Microsoft.Extensions.Caching.Hybrid;
using PoWatch.Api.Security;
using PoWatch.Application.Features.Stats;

namespace PoWatch.Api.Features.Stats;

internal static class StatsEndpoints
{
    private static readonly HybridCacheEntryOptions Entry = new()
    {
        Expiration = TimeSpan.FromMinutes(5),
        LocalCacheExpiration = TimeSpan.FromMinutes(5)
    };

    /// <summary>Every cached stats entry for a user carries this tag; ingest and <see cref="InvalidatesStats"/> evict it.</summary>
    internal static string CacheTag(string userId) => $"stats:{userId}";

    /// <summary>For writes that change what the stat families show: evicts the caller's cached stats once the endpoint succeeds.</summary>
    internal static RouteHandlerBuilder InvalidatesStats(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var result = await next(context);
            if (result is IStatusCodeHttpResult { StatusCode: >= 200 and < 300 }
                && CurrentUser.Id(context.HttpContext.User) is { } userId)
            {
                // The write has already landed, so a client that disconnects now must not cancel the eviction.
                var cache = context.HttpContext.RequestServices.GetRequiredService<HybridCache>();
                await cache.RemoveByTagAsync(CacheTag(userId), CancellationToken.None);
            }
            return result;
        });

    internal static IEndpointRouteBuilder MapStatsFeature(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/stats").WithTags("Stats").RequireAuthorization();

        Map(group, "presence", (s, u, w, ct) => s.PresenceAsync(u, w, ct));
        Map(group, "space", (s, u, w, ct) => s.SpaceAsync(u, w, ct));
        Map(group, "objects", (s, u, w, ct) => s.ObjectsAsync(u, w, ct));
        Map(group, "patterns", (s, u, w, ct) => s.PatternsAsync(u, w, ct));
        Map(group, "environment", (s, u, w, ct) => s.EnvironmentAsync(u, w, ct));
        Map(group, "pipeline", (s, u, w, ct) => s.PipelineAsync(u, w, ct));

        // Everything the families know about a range in one file; the Live grid shows only the headlines.
        group.MapGet("/all", async (string? range, string? tz, Guid? sessionId, string? date, UserId u, StatsQueryService s, CancellationToken ct) =>
            {
                var (error, w, _) = await WindowAsync(u, range, tz, sessionId, date, s, ct);
                if (w is null) return error!;

                var raw = await s.RawAsync(u, w, ct);
                return Results.Ok(new
                {
                    presence = await s.PresenceAsync(u, w, ct),
                    space = await s.SpaceAsync(u, w, ct),
                    objects = await s.ObjectsAsync(u, w, ct),
                    patterns = await s.PatternsAsync(u, w, ct),
                    environment = await s.EnvironmentAsync(u, w, ct),
                    pipeline = await s.PipelineAsync(u, w, ct),
                    // Session, today or one day: the stored record itself. Longer ranges have only the rollups above.
                    ticks = raw?.Ticks,
                    events = raw?.Events.Select(e => new { e.SessionId, e.AtUtc, Kind = e.Kind.ToString(), e.TrackId, e.Class, Edge = e.Edge.ToString(), e.Text, e.Score, e.DwellSeconds, e.RegularId, e.ImagePath }),
                });
            })
            .WithName("StatsAll")
            .WithSummary("Every stat family for a range in one JSON document.");

        // One day minute by minute, for History's replay. Not cached: a day is read once per visit.
        group.MapGet("/replay", async (string? date, string? tz, UserId u, StatsQueryService s, CancellationToken ct) =>
            {
                if (!ApiParsing.TryDay(date, out var day)) return Results.BadRequest(new { message = "date must be yyyy-MM-dd." });
                var w = await s.ResolveAsync(u, StatsRange.Day, tz, null, ct, day);
                return w is null ? Results.NotFound() : Results.Ok(await s.ReplayAsync(u, w, ct));
            })
            .WithName("StatsReplay")
            .WithSummary("A day's observed minutes with their motion grids, plus its captions, moments, entrances and exits.");

        return app;
    }

    /// <summary>The window a request asks for, or the response that says why there is none.</summary>
    private static async Task<(IResult? Error, StatsWindow? Window, StatsRange Range)> WindowAsync(
        string userId, string? range, string? tz, Guid? sessionId, string? date, StatsQueryService service, CancellationToken ct)
    {
        if (!StatsQueryService.TryParseRange(range, out var parsed))
            return (Results.BadRequest(new { message = "range must be one of session, today, 7d, 30d, all, day." }), null, parsed);
        DateOnly? day = null;
        if (parsed == StatsRange.Day)
        {
            if (!ApiParsing.TryDay(date, out var parsedDay))
                return (Results.BadRequest(new { message = "range=day needs date=yyyy-MM-dd." }), null, parsed);
            day = parsedDay;
        }

        var window = await service.ResolveAsync(userId, parsed, tz, sessionId, ct, day);
        return (window is null ? Results.NotFound() : null, window, parsed);
    }

    private static void Map<T>(
        RouteGroupBuilder group,
        string family,
        Func<StatsQueryService, string, StatsWindow, CancellationToken, Task<T>> query)
    {
        group.MapGet($"/{family}", async (
                string? range,
                string? tz,
                Guid? sessionId,
                string? date,
                UserId userId,
                StatsQueryService service,
                HybridCache cache,
                CancellationToken ct) =>
            {
                var (error, window, parsed) = await WindowAsync(userId, range, tz, sessionId, date, service, ct);
                if (window is null) return error!;

                // Windows ending "now" shift every call; key on the resolved grain bucket so repeat
                // polls within one bucket share an entry, and ingest clears the user's tag anyway.
                var key = $"stats:{userId}:{family}:{StatsQueryService.RangeName(parsed)}:{window.Zone.Id}:{sessionId}:{window.FromUtc.UtcTicks}";
                var result = await cache.GetOrCreateAsync(
                    key,
                    (service, userId, window, query),
                    static async (state, token) => await state.query(state.service, state.userId, state.window, token),
                    Entry,
                    [CacheTag(userId)],
                    ct);

                return Results.Ok(result);
            })
            .WithName($"Stats{char.ToUpperInvariant(family[0])}{family[1..]}")
            .WithSummary($"Stat family '{family}' for a range: session, today, 7d, 30d, all, or day (with date).");
    }
}
