namespace PoWatch.Domain.Models;

/// <summary>Where each grain's bucket starts for an instant, in the user's local wall-clock time.</summary>
public static class RollupBuckets
{
    public static DateTimeOffset StartUtc(RollupGrain grain, DateTimeOffset instant, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = TimeZoneInfo.ConvertTime(instant, zone);

        // Truncating the local DateTimeOffset keeps its own offset, so a repeated fall-back hour still
        // maps to the right instant.
        return grain switch
        {
            RollupGrain.Minute => new DateTimeOffset(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, local.Offset).ToUniversalTime(),
            RollupGrain.Hour => new DateTimeOffset(local.Year, local.Month, local.Day, local.Hour, 0, 0, local.Offset).ToUniversalTime(),
            RollupGrain.Day => Services.LocalDay.Window(DateOnly.FromDateTime(local.DateTime), zone).StartUtc,
            _ => DateTimeOffset.UnixEpoch
        };
    }
}
