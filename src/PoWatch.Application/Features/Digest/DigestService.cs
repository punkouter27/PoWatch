using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using PoWatch.Application.Contracts;
using PoWatch.Domain.Models;
using PoWatch.Domain.Services;
using PoWatch.Shared.Models;
using PoWatch.Application.Features.Ask;
using PoWatch.Application.Features.Recaps;

namespace PoWatch.Application.Features.Digest;

/// <summary>
/// The week in review: a Monday-to-Sunday week against the one before, read from the day rollups,
/// with the regulars first seen in it. History shows it in-app; a background job can mail it. With a
/// chat client, a model adds a one-line headline (checked against the summary's numbers).
/// </summary>
public sealed class DigestService(IRollupStore rollups, IRegularStore regulars, AssistService? assist = null)
{
    /// <summary>The week that holds <paramref name="anyDay"/>, against the week before it.</summary>
    public async Task<WeekDigestDto> WeekAsync(string userId, DateOnly anyDay, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var start = LocalDay.WeekStart(anyDay);
        var thisFrom = LocalDay.Window(start, zone).StartUtc;
        var thisTo = LocalDay.Window(start.AddDays(6), zone).EndUtc;
        var lastFrom = LocalDay.Window(start.AddDays(-7), zone).StartUtc;

        var daysRead = rollups.GetRangeAsync(userId, RollupGrain.Day, lastFrom, thisTo, cancellationToken);
        var regularsRead = regulars.ListAsync(userId, cancellationToken);
        await Task.WhenAll(daysRead, regularsRead);

        var days = (await daysRead).Where(d => d.Ticks > 0).ToList();
        var thisWeek = Numbers(start, days.Where(d => d.BucketStartUtc >= thisFrom), zone);
        var lastWeek = Numbers(start.AddDays(-7), days.Where(d => d.BucketStartUtc < thisFrom), zone);
        var newcomers = (await regularsRead)
            .Where(r => r.FirstSeenUtc >= thisFrom && r.FirstSeenUtc < thisTo)
            .OrderBy(r => r.FirstSeenUtc)
            .Select(r => r.DisplayName)
            .ToList();

        var summary = Summary(thisWeek, lastWeek, newcomers);
        // The chat client's cache answers a week already titled, so reopening History costs nothing.
        var headline = assist is not null && thisWeek.SecondsObserved > 0 ? await assist.HeadlineAsync(summary, cancellationToken) : null;
        return new WeekDigestDto { ThisWeek = thisWeek, LastWeek = lastWeek, NewRegulars = newcomers, Summary = summary, Headline = headline };
    }

    private static WeekNumbersDto Numbers(DateOnly weekStart, IEnumerable<Rollup> days, TimeZoneInfo zone)
    {
        var list = days.ToList();
        var total = list.Aggregate(Rollup.Empty, Rollup.Merge);
        var busiest = list.OrderByDescending(d => d.OccupancyFraction).FirstOrDefault();
        return new WeekNumbersDto
        {
            WeekStart = weekStart,
            DaysObserved = list.Count,
            SecondsObserved = total.Seconds,
            Occupancy = total.OccupancyFraction,
            Visits = total.Visits,
            PeakConcurrency = total.PeakConcurrency,
            BusiestDay = busiest is null ? null : LocalDay.Of(busiest.BucketStartUtc, zone),
            BusiestOccupancy = busiest?.OccupancyFraction ?? 0
        };
    }

    public static string Summary(WeekNumbersDto week, WeekNumbersDto before, IReadOnlyList<string> newcomers)
    {
        ArgumentNullException.ThrowIfNull(week);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(newcomers);
        static string Pct(double v) => v.ToString("0%", CultureInfo.InvariantCulture);
        if (week.SecondsObserved <= 0)
            return before.SecondsObserved > 0
                ? $"Nothing was observed this week; the week before had {TemplateRecap.Duration(TimeSpan.FromSeconds(before.SecondsObserved))}."
                : "Nothing was observed this week.";

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"PoWatch watched for {TemplateRecap.Duration(TimeSpan.FromSeconds(week.SecondsObserved))} over {week.DaysObserved} {(week.DaysObserved == 1 ? "day" : "days")}; ");
        text.Append(CultureInfo.InvariantCulture, $"the room was occupied {Pct(week.Occupancy)} of the time");
        if (before.SecondsObserved > 0)
        {
            var points = Math.Round((week.Occupancy - before.Occupancy) * 100);
            var unit = Math.Abs(points) == 1 ? "point" : "points";
            text.Append(points switch
            {
                0 => ", the same as the week before",
                > 0 => $", up {points:0} {unit} on the week before",
                _ => $", down {-points:0} {unit} on the week before"
            });
        }
        text.Append(CultureInfo.InvariantCulture, $", with {week.Visits} {(week.Visits == 1 ? "visit" : "visits")}.");
        if (week.BusiestDay is { } busiest && week.DaysObserved > 1)
            text.Append(CultureInfo.InvariantCulture, $" {busiest.DayOfWeek} was the busiest day ({Pct(week.BusiestOccupancy)}).");
        if (newcomers.Count > 0)
            text.Append(CultureInfo.InvariantCulture, $" New this week: {string.Join(", ", newcomers.Take(5))}{(newcomers.Count > 5 ? $" and {newcomers.Count - 5} more" : string.Empty)}.");
        return text.ToString();
    }

    /// <summary>The digest as an email: subject, an HTML body and its plain-text twin. Every value is encoded.</summary>
    public static (string Subject, string Html, string Text) Email(WeekDigestDto digest, Uri? appUrl)
    {
        ArgumentNullException.ThrowIfNull(digest);
        var week = digest.ThisWeek;
        var heading = $"Your week of {week.WeekStart.ToString("d MMMM", CultureInfo.InvariantCulture)}";
        var subject = $"PoWatch · {digest.Headline ?? heading}";
        var html = HtmlEncoder.Default;
        var link = appUrl is null ? null : new Uri(appUrl, "stories?view=week");

        var body = new StringBuilder();
        body.Append("<div style=\"font-family:system-ui,sans-serif;max-width:560px;color:#1f2329\">");
        body.Append("<p style=\"font-size:12px;letter-spacing:.08em;color:#b26b00;margin:0\">POWATCH · THE WEEK IN REVIEW</p>");
        body.Append(CultureInfo.InvariantCulture, $"<h1 style=\"font-size:20px;margin:6px 0 12px\">{html.Encode(heading)}</h1>");
        body.Append(CultureInfo.InvariantCulture, $"<p style=\"font-size:15px;line-height:1.5\">{html.Encode(digest.Summary)}</p>");
        if (link is not null)
            body.Append(CultureInfo.InvariantCulture, $"<p><a href=\"{html.Encode(link.ToString())}\" style=\"color:#b26b00\">Open History →</a></p>");
        body.Append("<p style=\"font-size:12px;color:#6b7280\">You get this because the weekly email is on in PoWatch Settings. Turn it off there.</p></div>");

        var text = $"{subject}\n\n{digest.Summary}\n\n{(link is null ? string.Empty : $"Open History: {link}\n\n")}Turn this email off in PoWatch Settings.";
        return (subject, body.ToString(), text);
    }
}
