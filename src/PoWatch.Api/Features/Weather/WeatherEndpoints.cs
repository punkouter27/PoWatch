using PoWatch.Api.Security;
using PoWatch.Application.Features.Weather;

namespace PoWatch.Api.Features.Weather;

/// <summary>Weather context from Open-Meteo: place search, and a year of days at the caller's place.</summary>
internal static class WeatherEndpoints
{
    internal static IEndpointRouteBuilder MapWeatherFeature(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("Weather").RequireAuthorization();

        group.MapGet("/places", async (string? q, WeatherService weather, CancellationToken ct) =>
                Results.Ok(await weather.SearchAsync(q ?? string.Empty, ct)))
            .WithName("SearchPlaces")
            .WithSummary("Places matching a name, for picking where the weather comes from.");

        group.MapGet("/weather/year", async (string? tz, HttpContext http, WeatherService weather, CancellationToken ct) =>
            {
                if (CurrentUser.Id(http.User) is not { } userId) return Results.Unauthorized();
                return Results.Ok(await weather.YearAsync(userId, tz, ct));
            })
            .WithName("WeatherYear")
            .WithSummary("Daily weather at the caller's place for the last year, and how wet days compare with dry ones.");

        return app;
    }
}
