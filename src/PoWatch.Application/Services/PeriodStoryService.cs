using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PoWatch.Application.Contracts;
using PoWatch.Domain.Models;
using PoWatch.Domain.Services;
using PoWatch.Shared.Models;
using PoWatch.Shared.Services;

namespace PoWatch.Application.Services;

/// <summary>
/// The story of a whole week (Monday to Sunday) or calendar month: each day told first — by its
/// sessions' stories where they were written, else from the day's scene events — then the days told
/// together as one story. Nothing is stored; the chat client's response cache answers an unchanged
/// period again for free.
/// </summary>
public sealed class PeriodStoryService(
    SessionService sessions,
    IStoryStore stories,
    ISensingLog sensingLog,
    IRegularStore regulars,
    IEnumerable<IChatClient> chatClients,
    ILogger<PeriodStoryService> logger)
{
    private static readonly TimeSpan AiTimeout = TimeSpan.FromSeconds(45);

    /// <summary>A day's part of the prompt is cut here, so a month stays a reasonable prompt.</summary>
    private const int MaxDayText = 700;

    public async Task<PeriodStoryDto> TellAsync(string userId, bool month, DateOnly anyDay, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var from = month ? new DateOnly(anyDay.Year, anyDay.Month, 1) : LocalDay.WeekStart(anyDay);
        var to = month ? from.AddMonths(1).AddDays(-1) : from.AddDays(6);

        var names = (await regulars.ListAsync(userId, cancellationToken)).ToDictionary(r => r.Id, r => r.DisplayName);
        var inPeriod = await sessions.ListBetweenAsync(userId, LocalDay.Window(from, zone).StartUtc, LocalDay.Window(to, zone).EndUtc, cancellationToken);
        var told = new Dictionary<DateOnly, List<string>>();
        foreach (var session in inPeriod.Where(s => !s.IsRunning))
        {
            if (await stories.GetStoryAsync(userId, session.Id, cancellationToken) is not { } story || story.Text == TemplateRecap.NothingObserved) continue;
            var day = LocalDay.Of(session.StartedUtc, zone);
            if (day < from || day > to) continue;
            (told.TryGetValue(day, out var list) ? list : told[day] = []).Add(story.Text);
        }

        var days = new List<DayStoryDto>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var text = told.TryGetValue(day, out var list)
                ? string.Join(' ', list)
                : SceneNarrator.Tell(await sensingLog.GetEventsAsync(userId, day, cancellationToken), zone, names);
            if (!string.IsNullOrWhiteSpace(text)) days.Add(new DayStoryDto { Date = day, Text = text.Replace("\n\n", " ", StringComparison.Ordinal) });
        }

        var (storyText, source) = days.Count == 0
            ? ($"Nothing was observed this {(month ? "month" : "week")}.", "template")
            : await CompileAsync(month, days, cancellationToken);
        return new PeriodStoryDto
        {
            Kind = month ? "month" : "week",
            From = from,
            To = to,
            Story = storyText,
            Source = source,
            Days = [.. days.OrderByDescending(d => d.Date)]
        };
    }

    private async Task<(string Text, string Source)> CompileAsync(bool month, List<DayStoryDto> days, CancellationToken cancellationToken)
    {
        // Without a model: each day's opening, in order.
        var template = string.Join("\n\n", days.Select(d => $"{Label(d.Date)}: {Opening(d.Text)}"));
        // One day is its own story, whole; several without a model are each day's opening.
        if (days.Count == 1) return (days[0].Text, "template");
        if (!chatClients.Any()) return (template, "template");

        var facts = RecapPrompt.StoryUser(days.Select(d => (Label(d.Date), Cut(d.Text))));
        try
        {
            var (chat, text) = await chatClients.FirstAnswerAsync(month ? "month story" : "week story", AiTimeout, async (model, ct) =>
            {
                var response = await model.GetResponseAsync(
                    [new ChatMessage(ChatRole.System, RecapPrompt.PeriodSystem(month ? "month" : "week")), new ChatMessage(ChatRole.User, facts)],
                    new ChatOptions { Temperature = 0.5f, MaxOutputTokens = 900 },
                    ct);
                return (model, response.Text?.Trim());
            }, logger, cancellationToken);

            if (string.IsNullOrWhiteSpace(text)) return (template, "template");
            if (!RecapPrompt.KeepsToFacts(text, facts))
            {
                logger.LogWarning("AI period story used a number that is not in the days; telling each day's opening instead.");
                return (template, "template");
            }
            return (text, chat.GetService<ChatClientMetadata>()?.ProviderName ?? "ai");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "AI period story failed on every model; telling each day's opening instead.");
            return (template, "template");
        }
    }

    private static string Label(DateOnly day) => day.ToString("ddd d MMM", CultureInfo.InvariantCulture);

    /// <summary>The first two sentences: enough to say how a day began.</summary>
    private static string Opening(string text)
    {
        var end = -1;
        for (var i = 0; i < 2; i++)
        {
            var next = text.IndexOf(". ", end + 1, StringComparison.Ordinal);
            if (next < 0) return text;
            end = next + 1;
        }
        return text[..end];
    }

    private static string Cut(string text) => text.Length <= MaxDayText ? text : text[..MaxDayText] + "…";
}
