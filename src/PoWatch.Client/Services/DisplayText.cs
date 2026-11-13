using System.Globalization;

namespace PoWatch.Client.Services;

/// <summary>Timestamps with day context: time-of-day alone is misleading when the data spans weeks.</summary>
public static class DisplayText
{
    /// <summary>"Today 14:48", "Yesterday 20:24", or "Jul 6, 20:24" — never a bare time-of-day.</summary>
    public static string RelativeTime(DateTimeOffset utc)
    {
        var local = utc.ToLocalTime();
        var today = DateTimeOffset.Now.Date;
        if (local.Date == today) return $"Today {local:HH:mm}";
        if (local.Date == today.AddDays(-1)) return $"Yesterday {local:HH:mm}";
        return local.ToString("MMM d, HH:mm", CultureInfo.CurrentCulture);
    }

    /// <summary>"1h 02m", "4m 05s" or "12s" — one way to print a length of time on every page.</summary>
    public static string Duration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes:00}m"
            : span.TotalMinutes >= 1 ? $"{span.Minutes}m {span.Seconds:00}s"
            : $"{span.Seconds}s";
    }

    /// <summary>"14:48" on this device's clock.</summary>
    public static string LocalTime(DateTimeOffset utc) => utc.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);

    /// <summary>"2026-11-13": a day as the API, the time-lapse store and file names spell it.</summary>
    public static string DayKey(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>A 0–1 share for a style or SVG attribute: at most three decimals, always a dot.</summary>
    public static string Fraction(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>"49.1%", or "—" before the number has loaded.</summary>
    public static string Percent(double? value) => value is { } v ? v.ToString("0.0%", CultureInfo.InvariantCulture) : "—";

    /// <summary>The value in <paramref name="format"/>, or "—" before it has loaded.</summary>
    public static string Number(double? value, string format) => value is { } v ? v.ToString(format, CultureInfo.InvariantCulture) : "—";

    /// <summary>"Rain · 7°–19°C · 7.2 mm · SUN 06:58–18:10": a day's weather on one line.</summary>
    public static string Weather(PoWatch.Shared.Models.WeatherDayDto day)
    {
        ArgumentNullException.ThrowIfNull(day);
        var line = string.Create(CultureInfo.CurrentCulture, $"{day.Summary} · {day.TempMinC:0}°–{day.TempMaxC:0}°C");
        if (day.PrecipitationMm >= 0.1) line += string.Create(CultureInfo.CurrentCulture, $" · {day.PrecipitationMm:0.#} mm");
        if (day.SunriseUtc is { } rise && day.SunsetUtc is { } set)
            line += $" · SUN {LocalTime(rise)}–{LocalTime(set)}";
        return line;
    }
}
