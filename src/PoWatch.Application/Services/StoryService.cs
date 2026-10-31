using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PoWatch.Application.Contracts;
using PoWatch.Domain.Models;
using PoWatch.Domain.Services;
using PoWatch.Shared.Models;
using PoWatch.Shared.Services.Recaps;

namespace PoWatch.Application.Services;

/// <summary>
/// A session's story: a recap of every ten minutes (a chapter) kept as the session runs, and once it
/// has ended, the chapters joined into one story. Nothing runs on a timer here; each call writes
/// whatever has fallen due since the last, so a missed call is caught up by the next.
/// </summary>
public sealed class StoryService(
    SessionService sessions,
    RecapService recaps,
    IStoryStore store,
    IEnumerable<IChatClient> chatClients,
    TimeProvider time,
    ILogger<StoryService> logger)
{
    private static readonly TimeSpan AiTimeout = TimeSpan.FromSeconds(30);
    private const string Empty = TemplateRecap.NothingObserved;

    /// <summary>The session's story so far, after writing any chapter (and, once it has ended, the story) that is due. Null when the session is not the user's.</summary>
    public async Task<SessionStoryDto?> UpdateAsync(string userId, Guid sessionId, CancellationToken cancellationToken)
    {
        if (await sessions.GetAsync(userId, sessionId, cancellationToken) is not { } session) return null;

        var chapters = (await store.ListChaptersAsync(userId, sessionId, cancellationToken)).ToDictionary(c => c.Index);
        var due = StoryChapters.Closed(session.StartedUtc, session.EndedUtc, time.GetUtcNow()).Where(c => !chapters.ContainsKey(c.Index)).ToList();
        foreach (var (index, from, to) in due)
        {
            // Only the newest chapter is worth a model call; a backlog (a tab asleep, a session closed
            // as stale) is written from the template, so one call never waits on dozens of replies.
            var recap = await recaps.ForStretchAsync(userId, session, from, to, $"Chapter {index + 1}", useAi: index == due[^1].Index, cancellationToken);
            var chapter = new StoryChapter(sessionId, index, from, to, recap.Summary, recap.Source);
            await store.UpsertChapterAsync(userId, chapter, cancellationToken);
            chapters[index] = chapter;
        }

        var ordered = chapters.Values.OrderBy(c => c.Index).ToList();
        var story = await store.GetStoryAsync(userId, sessionId, cancellationToken);
        if (story is null && !session.IsRunning)
        {
            story = await CompileAsync(session, ordered, cancellationToken);
            await store.UpsertStoryAsync(userId, story, cancellationToken);
        }

        return new SessionStoryDto
        {
            SessionId = sessionId,
            Chapters = [.. ordered.Select(c => new StoryChapterDto { Index = c.Index, FromUtc = c.FromUtc, ToUtc = c.ToUtc, Text = c.Text, Source = c.Source })],
            Story = story?.Text,
            Source = story?.Source,
            WrittenUtc = story?.WrittenUtc
        };
    }

    private async Task<SessionStory> CompileAsync(Session session, IReadOnlyList<StoryChapter> chapters, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        // Quiet chapters add nothing to a story; a session that saw nothing at all says so once.
        var told = chapters.Where(c => c.Text != Empty).Select(c => (When: Span(c, session.TimeZone), c.Text)).ToList();
        if (told.Count == 0) return new SessionStory(session.Id, Empty, "template", now);

        // Each chapter already tells its own times; joined, they read in order.
        var joined = string.Join("\n\n", told.Select(c => c.Text));
        if (told.Count == 1 || !chatClients.Any()) return new SessionStory(session.Id, joined, "template", now);

        var facts = RecapPrompt.StoryUser(told);
        try
        {
            var (chat, text) = await chatClients.FirstAnswerAsync("story", AiTimeout, async (model, ct) =>
            {
                var response = await model.GetResponseAsync(
                    [new ChatMessage(ChatRole.System, RecapPrompt.StorySystem), new ChatMessage(ChatRole.User, facts)],
                    new ChatOptions { Temperature = 0.5f, MaxOutputTokens = 700 },
                    ct);
                return (model, response.Text?.Trim());
            }, logger, cancellationToken);

            if (string.IsNullOrWhiteSpace(text)) return new SessionStory(session.Id, joined, "template", now);
            if (!RecapPrompt.KeepsToFacts(text, facts))
            {
                logger.LogWarning("AI story used a number that is not in the chapters; joining the chapters instead.");
                return new SessionStory(session.Id, joined, "template", now);
            }
            return new SessionStory(session.Id, text, chat.GetService<ChatClientMetadata>()?.ProviderName ?? "ai", now);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "AI story failed on every model; joining the chapters instead.");
            return new SessionStory(session.Id, joined, "template", now);
        }
    }

    private static string Span(StoryChapter chapter, TimeZoneInfo zone) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{TimeZoneInfo.ConvertTime(chapter.FromUtc, zone):HH:mm}–{TimeZoneInfo.ConvertTime(chapter.ToUtc, zone):HH:mm}");
}
