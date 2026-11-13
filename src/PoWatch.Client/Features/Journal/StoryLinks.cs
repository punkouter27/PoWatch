using System.Globalization;
using PoWatch.Client.Services;
using PoWatch.Shared.Models;

namespace PoWatch.Client.Features.Journal;

/// <summary>The Journal's addresses, so every link to a session, a day or a period agrees.</summary>
public static class StoryLinks
{
    /// <summary>A session in the reader: its story, numbers, replay and timeline.</summary>
    public static string Session(Guid sessionId) => $"journal/s/{sessionId}";

    /// <summary>A day in the reader: its recap, numbers, replay and timeline.</summary>
    public static string Day(DateOnly day) => $"journal/{Key(day)}";

    /// <summary>The day a session started on, in this browser's zone.</summary>
    public static string DayOf(SessionDto session) => Day(DateOnly.FromDateTime(session.StartedUtc.ToLocalTime().DateTime));

    /// <summary>The story of the week or month ("week" or "month") holding <paramref name="day"/>, or the current one.</summary>
    public static string Period(string kind, DateOnly? day = null) => $"journal?view={kind}{(day is { } d ? $"&date={Key(d)}" : null)}";

    private static string Key(DateOnly day) => DisplayText.DayKey(day);
}
