using System.Globalization;
using PoWatch.Shared.Models;

namespace PoWatch.Client.Features.History;

/// <summary>The addresses of Stories and Days, so every link to a session or a day agrees.</summary>
public static class StoryLinks
{
    /// <summary>A session's own page: its story, chapters, transcript and numbers.</summary>
    public static string Session(Guid sessionId) => $"stories/s/{sessionId}";

    /// <summary>A day's numbers, recap and replay.</summary>
    public static string Day(DateOnly day) => $"days/{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

    /// <summary>The day a session started on, in this browser's zone.</summary>
    public static string DayOf(SessionDto session) => Day(DateOnly.FromDateTime(session.StartedUtc.ToLocalTime().DateTime));
}
