using System.Globalization;
using PoWatch.Application.Contracts;
using PoWatch.Domain.Models;
using PoWatch.Domain.Services;
using PoWatch.Shared.Models;

namespace PoWatch.Application.Features.Weather;

/// <summary>
/// The weather at the caller's chosen place for the last year, and whether wet days are busier than
/// dry ones. Weather is context only: it never changes a stat.
/// </summary>
public sealed class WeatherService(IProfileStore profiles, IRollupStore rollups, IWeatherSource source, TimeProvider time)
{
    public const int YearDays = 365;

    /// <summary>Within this many occupancy points, rain is said to make no difference.</summary>
    private const double SamePoints = 0.02;

    public async Task<WeatherYearDto> YearAsync(string userId, string? timeZoneId, CancellationToken cancellationToken)
    {
        if ((await profiles.GetAsync(userId, cancellationToken))?.Place is not { } place) return new WeatherYearDto();

        // Weather days are the place's days; observed days are the caller's. Usually the same zone.
        var placeZone = LocalDay.FindZone(place.TimeZoneId) ?? LocalDay.FindZone(timeZoneId) ?? TimeZoneInfo.Utc;
        var userZone = LocalDay.FindZone(timeZoneId) ?? placeZone;
        var today = LocalDay.Today(time, placeZone);
        var from = today.AddDays(-(YearDays - 1));

        var weatherRead = source.GetDailyAsync(place, from, today, cancellationToken);
        var calendarRead = rollups.GetRangeAsync(userId, RollupGrain.Day, LocalDay.Window(from, userZone).StartUtc, time.GetUtcNow().AddDays(1), cancellationToken);
        await Task.WhenAll(weatherRead, calendarRead);

        var days = await weatherRead;
        var observed = (await calendarRead).Where(r => r.Ticks > 0).Select(r => (LocalDay.Of(r.BucketStartUtc, userZone), r.OccupancyFraction));
        var comparison = WeatherStats.CompareWetDry(observed, days.ToDictionary(d => d.Date));

        return new WeatherYearDto
        {
            Place = ToDto(place, detail: null),
            Days = days.Select(d => new WeatherDayDto
            {
                Date = d.Date,
                Code = d.Code,
                Summary = WeatherStats.Describe(d.Code),
                TempMaxC = d.TempMaxC,
                TempMinC = d.TempMinC,
                PrecipitationMm = d.PrecipitationMm,
                Wet = WeatherStats.IsWet(d),
                SunriseUtc = d.SunriseUtc,
                SunsetUtc = d.SunsetUtc
            }).ToList(),
            Insight = comparison is null ? null : new WeatherInsightDto
            {
                WetDays = comparison.WetDays,
                DryDays = comparison.DryDays,
                WetOccupancy = comparison.WetOccupancy,
                DryOccupancy = comparison.DryOccupancy,
                Sentence = Sentence(comparison)
            }
        };
    }

    public async Task<IReadOnlyList<PlaceDto>> SearchAsync(string name, CancellationToken cancellationToken) =>
        (await source.SearchAsync(name, cancellationToken)).Select(p => ToDto(p.Place, p.Detail)).ToList();

    public static PlaceDto ToDto(Place place, string? detail)
    {
        ArgumentNullException.ThrowIfNull(place);
        return new() { Name = place.Name, Detail = detail, Latitude = place.Latitude, Longitude = place.Longitude, TimeZoneId = place.TimeZoneId };
    }

    public static string Sentence(WetDryComparison c)
    {
        ArgumentNullException.ThrowIfNull(c);
        static string Pct(double v) => v.ToString("0%", CultureInfo.InvariantCulture);
        var counts = $"({c.WetDays} wet days, {c.DryDays} dry)";
        var gap = c.WetOccupancy - c.DryOccupancy;
        return Math.Abs(gap) < SamePoints
            ? $"Rain makes no real difference: {Pct(c.WetOccupancy)} occupied on wet days, {Pct(c.DryOccupancy)} on dry ones {counts}."
            : gap > 0
                ? $"Wet days are busier: {Pct(c.WetOccupancy)} occupied against {Pct(c.DryOccupancy)} on dry days {counts}."
                : $"Rain keeps it quiet: {Pct(c.WetOccupancy)} occupied on wet days against {Pct(c.DryOccupancy)} on dry ones {counts}.";
    }
}
