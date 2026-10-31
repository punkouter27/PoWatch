namespace PoWatch.Client.Services;

/// <summary>Which window of stats to ask for: session, today, 7d, 30d or all, in the browser's time zone.</summary>
/// <param name="Stamp">Bumped when the server says the stats changed, so an otherwise equal query reloads.</param>
public sealed record StatsQuery(string Range, string TimeZoneId, Guid? SessionId = null, long Stamp = 0, DateOnly? Date = null)
{
    /// <summary>In Blazor WebAssembly the local zone is the browser's IANA zone.</summary>
    public static StatsQuery For(string range, Guid? sessionId = null) => new(range, TimeZoneInfo.Local.Id, sessionId);

    /// <summary>One local calendar day.</summary>
    public static StatsQuery ForDay(DateOnly date) => new("day", TimeZoneInfo.Local.Id, Date: date);

    /// <summary>A session range with no session yet (Live before its first Start): nothing to ask the server.</summary>
    public bool IsEmpty => Range == "session" && SessionId is null;
}
