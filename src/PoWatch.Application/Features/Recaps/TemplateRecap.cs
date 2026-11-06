using System.Globalization;
using Humanizer;
using PoWatch.Domain.Models;
using PoWatch.Shared.Models;
using PoWatch.Application.Features.Stories;

namespace PoWatch.Application.Features.Recaps;

/// <summary>Everything a recap is written from, gathered once.</summary>
public sealed record RecapFacts(
    string Title,
    string Subtitle,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    TimeZoneInfo Zone,
    PresenceStatsDto Presence,
    ObjectStatsDto Objects,
    EnvironmentStatsDto Environment,
    PatternStatsDto? Patterns,
    IReadOnlyList<MomentDto> Moments,
    IReadOnlyList<SceneEvent>? Events = null,
    IReadOnlyList<Tick>? Ticks = null);

/// <summary>
/// The deterministic recap: always available, needs no AI, and is what an AI rewrite falls back to.
/// Numbers are written the way a person would say them ("3 hours, 12 minutes", "1.2K frames").
/// </summary>
public static class TemplateRecap
{
    /// <summary>The whole summary of a window in which nothing was observed.</summary>
    public const string NothingObserved = "Nothing was observed in this window.";

    public static RecapDto Build(RecapFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var p = facts.Presence;
        var observed = TimeSpan.FromSeconds(p.SecondsObserved);

        return new RecapDto
        {
            Title = facts.Title,
            Subtitle = facts.Subtitle,
            FromUtc = facts.FromUtc,
            ToUtc = facts.ToUtc,
            Summary = Summary(facts),
            Highlights = Highlights(facts),
            Numbers =
            [
                new() { Label = "Observed", Value = Duration(observed) },
                new() { Label = "Occupancy", Value = p.Occupancy.ToString("0%", CultureInfo.InvariantCulture) },
                new() { Label = "Visits", Value = p.Visits.ToString(CultureInfo.InvariantCulture) },
                new() { Label = "Peak at once", Value = p.PeakConcurrency.ToString(CultureInfo.InvariantCulture) },
                new() { Label = "Busiest", Value = Local(p.BusiestStartUtc, facts.Zone) },
                new() { Label = "Longest empty", Value = Duration(TimeSpan.FromSeconds(p.LongestEmptySeconds)) },
                new() { Label = "Things seen", Value = facts.Objects.Classes.Count.ToString(CultureInfo.InvariantCulture) },
                new() { Label = "Captions", Value = facts.Environment.CaptionCount.ToString(CultureInfo.InvariantCulture) },
            ],
            Moments = [.. facts.Moments.Take(6)],
            Source = "template"
        };
    }

    private static string Summary(RecapFacts facts)
    {
        var p = facts.Presence;
        if (p.SecondsObserved <= 0) return NothingObserved;

        // What happened, told from the scene events, then how it felt from the telemetry; the numbers
        // below are only for a window with neither (seeded history).
        var names = facts.Objects.Regulars.DistinctBy(r => r.Id).ToDictionary(r => r.Id, r => r.DisplayName);
        var events = facts.Events ?? [];
        var told = string.Join(' ', new[]
        {
            SceneNarrator.Tell(events, facts.Zone, names),
            SceneNarrator.Atmosphere(facts.Ticks ?? [], events, facts.Zone, names)
        }.Where(s => s is not null));
        if (told.Length > 0) return told;

        var parts = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"PoWatch watched for {Duration(TimeSpan.FromSeconds(p.SecondsObserved))} and someone was in frame {p.Occupancy:0%} of that time.")
        };

        parts.Add(p.Visits switch
        {
            0 => "Nobody came or went.",
            1 => "There was one visit.",
            _ => string.Create(CultureInfo.InvariantCulture, $"There were {p.Visits} visits, about {p.VisitsPerHour:0.#} an hour, with up to {p.PeakConcurrency} at once.")
        });

        if (facts.Objects.Regulars.FirstOrDefault() is { } top)
            parts.Add($"{top.DisplayName} dropped by most ({"visit".ToQuantity(top.Visits)}, {Duration(TimeSpan.FromSeconds(top.DwellSeconds))} in frame).");

        if (p.BusiestStartUtc is { } busiest)
            parts.Add($"The busiest minute was {Local(busiest, facts.Zone)}.");

        if (facts.Patterns?.Anomalies.FirstOrDefault(a => a.Flagged) is { } unusual)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{char.ToUpperInvariant(unusual.Metric[0])}{unusual.Metric[1..]} was unusual — {Math.Abs(unusual.Z ?? 0):0.0}σ {(unusual.Z > 0 ? "above" : "below")} your usual."));

        return string.Join(' ', parts);
    }

    private static List<string> Highlights(RecapFacts facts)
    {
        var highlights = new List<string>();
        var classes = facts.Objects.Classes;
        if (classes.Count > 0)
            highlights.Add($"Seen: {string.Join(", ", classes.Take(5).Select(c => c.Name))}{(classes.Count > 5 ? $" and {classes.Count - 5} more" : string.Empty)}.");
        if (facts.Objects.Rarest is { } rarest)
            highlights.Add($"Rarest sighting: {rarest}.");
        if (facts.Environment.Switches.Count > 0)
            highlights.Add($"The lights were switched {"time".ToQuantity(facts.Environment.Switches.Count)}.");
        if (facts.Environment.Words.Count > 0)
            highlights.Add($"The model kept mentioning: {string.Join(", ", facts.Environment.Words.Take(4).Select(w => w.Word))}.");
        if (facts.Environment.WeirdestCaption is { } weird)
            highlights.Add($"Weirdest caption: “{weird}”");
        if (facts.Presence.LongestStillSeconds >= 600)
            highlights.Add($"Stillest stretch: {Duration(TimeSpan.FromSeconds(facts.Presence.LongestStillSeconds))} without anything moving.");
        return highlights;
    }

    internal static string Duration(TimeSpan span) =>
        span.TotalSeconds < 1 ? "no time" : span.Humanize(2, CultureInfo.InvariantCulture, minUnit: TimeUnit.Second);

    private static string Local(DateTimeOffset? instant, TimeZoneInfo zone) =>
        instant is { } at ? TimeZoneInfo.ConvertTime(at, zone).ToString("HH:mm", CultureInfo.InvariantCulture) : "—";
}
