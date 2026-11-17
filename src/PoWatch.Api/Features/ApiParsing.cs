using System.Globalization;
using FluentValidation;
using PoWatch.Domain.Services;

namespace PoWatch.Api.Features;

/// <summary>How every endpoint reads a local day and a time zone from the query string.</summary>
internal static class ApiParsing
{
    public const string DayFormat = "yyyy-MM-dd";

    public static bool TryDay(string? value, out DateOnly day) =>
        DateOnly.TryParseExact(value, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    /// <summary>The named zone, or UTC when it is missing or unknown.</summary>
    public static TimeZoneInfo Zone(string? id) => LocalDay.FindZone(id) ?? TimeZoneInfo.Utc;

    /// <summary>
    /// Null when <paramref name="value"/> passes the form's shared rules (PoWatch.Shared/Validation), else the
    /// 400 ValidationProblem listing each field's message, the same words the browser shows inline.
    /// </summary>
    public static IResult? Invalid<T>(IValidator<T> validator, T value)
    {
        var result = validator.Validate(value);
        return result.IsValid
            ? null
            : Results.ValidationProblem(result.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray()));
    }
}
