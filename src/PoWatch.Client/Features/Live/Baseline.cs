using PoWatch.Shared.Models;

namespace PoWatch.Client.Features.Live;

/// <summary>
/// Whether there is enough history for "vs usual" to mean anything. Before a few earlier days have been
/// watched the usual is 0.0% and every comparison against it is noise, so Live says how far along the
/// baseline is instead of comparing.
/// </summary>
internal static class Baseline
{
    public const int DaysNeeded = 3;

    /// <summary>Earlier days (not today) with anything watched, from the patterns' 28-day calendar.</summary>
    public static int Days(PatternStatsDto? patterns)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        return patterns?.Calendar.Count(d => d.Date < today) ?? 0;
    }

    public static bool Ready(PatternStatsDto? patterns) => Days(patterns) >= DaysNeeded;

    /// <summary>"Baseline 1/3 days" for a hint or title bar while it builds.</summary>
    public static string Progress(PatternStatsDto? patterns) => $"Baseline {Math.Min(Days(patterns), DaysNeeded)}/{DaysNeeded} days";
}
