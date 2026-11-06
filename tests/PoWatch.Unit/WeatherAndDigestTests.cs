using PoWatch.Application.Features.Digest;
using PoWatch.Application.Features.Weather;
using PoWatch.Domain.Models;
using PoWatch.Domain.Services;
using PoWatch.Infrastructure.Runtime;
using PoWatch.Shared.Models;

namespace PoWatch.Unit;

public sealed class WeatherAndDigestTests
{
    [Fact]
    public void Weather_codes_read_as_words()
    {
        Assert.Equal("Clear", WeatherStats.Describe(0));
        Assert.Equal("Overcast", WeatherStats.Describe(3));
        Assert.Equal("Light rain", WeatherStats.Describe(61));
        Assert.Equal("Thunderstorm with hail", WeatherStats.Describe(99));
        Assert.Equal("Unknown", WeatherStats.Describe(42));
    }

    [Fact]
    public void Wet_and_dry_days_compare_only_once_each_side_has_enough_days()
    {
        var start = new DateOnly(2026, 10, 1);
        DailyWeather Day(int i, double rain) => new(start.AddDays(i), rain > 0 ? 61 : 0, 15, 5, rain, null, null);
        var weather = Enumerable.Range(0, 8).Select(i => Day(i, i < 4 ? 5 : 0)).ToDictionary(d => d.Date);
        var observed = Enumerable.Range(0, 8).Select(i => (start.AddDays(i), i < 4 ? 0.6 : 0.3)).ToList();

        var comparison = WeatherStats.CompareWetDry(observed, weather);
        Assert.NotNull(comparison);
        Assert.Equal((4, 4), (comparison.WetDays, comparison.DryDays));
        Assert.Equal(0.6, comparison.WetOccupancy, 9);
        Assert.Equal(0.3, comparison.DryOccupancy, 9);
        Assert.StartsWith("Wet days are busier: 60%", WeatherService.Sentence(comparison), StringComparison.Ordinal);

        // Two wet days are not a pattern; days without weather are left out.
        Assert.Null(WeatherStats.CompareWetDry(observed.Skip(2).Append((start.AddDays(30), 0.9)), weather));
    }

    [Fact]
    public void Open_Meteo_days_parse_with_the_sun_in_the_places_zone_and_skip_days_not_reported_yet()
    {
        const string json = """
            {"timezone":"America/New_York","daily":{
              "time":["2026-10-15","2026-10-16"],
              "weather_code":[1,null],
              "temperature_2m_max":[17.6,null],
              "temperature_2m_min":[7.6,null],
              "precipitation_sum":[0.0,null],
              "sunrise":["2026-10-15T06:57","2026-10-16T06:58"],
              "sunset":["2026-10-15T18:12","2026-10-16T18:10"]}}
            """;

        var day = Assert.Single(OpenMeteoWeatherSource.Parse(json));
        Assert.Equal(new DateOnly(2026, 10, 15), day.Date);
        Assert.Equal(1, day.Code);
        Assert.Equal(17.6, day.TempMaxC);
        // 06:57 Eastern Daylight Time is 10:57 UTC.
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 10, 57, 0, TimeSpan.Zero), day.SunriseUtc);
    }

    [Fact]
    public void A_week_reads_against_the_week_before_and_names_its_newcomers()
    {
        var before = new WeekNumbersDto { SecondsObserved = 36000, Occupancy = 0.40, DaysObserved = 3, Visits = 20 };
        var week = new WeekNumbersDto
        {
            SecondsObserved = 7200,
            Occupancy = 0.46,
            DaysObserved = 2,
            Visits = 1,
            BusiestDay = new DateOnly(2026, 10, 14),
            BusiestOccupancy = 0.5
        };

        var summary = DigestService.Summary(week, before, ["Person 4", "Cat 1"]);
        Assert.Equal(
            "PoWatch watched for 2 hours over 2 days; the room was occupied 46% of the time, up 6 points on the week before, with 1 visit. "
            + "Wednesday was the busiest day (50%). New this week: Person 4, Cat 1.", summary);

        Assert.Equal("Nothing was observed this week.", DigestService.Summary(new WeekNumbersDto(), new WeekNumbersDto(), []));
        Assert.Equal(new DateOnly(2026, 10, 12), LocalDay.WeekStart(new DateOnly(2026, 10, 18))); // Sunday → its Monday
        Assert.Equal(new DateOnly(2026, 10, 12), LocalDay.WeekStart(new DateOnly(2026, 10, 12)));
    }
}
