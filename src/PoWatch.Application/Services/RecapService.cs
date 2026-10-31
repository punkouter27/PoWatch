using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PoWatch.Application.Contracts;
using PoWatch.Domain.Models;
using PoWatch.Domain.Services;
using PoWatch.Shared.Models;
using PoWatch.Shared.Services.Recaps;

namespace PoWatch.Application.Services;

/// <summary>
/// Writes recaps for a session or a day. The template recap is always built; when an AI chat client
/// is configured it rewrites only the summary paragraph from the same facts, and any failure or
/// timeout falls back to the template paragraph.
/// </summary>
public sealed class RecapService(
    StatsQueryService stats,
    ISessionRepository sessions,
    ISensingLog sensingLog,
    ISnapshotStore snapshots,
    IEnumerable<IChatClient> chatClients,
    ILogger<RecapService> logger)
{
    private static readonly TimeSpan AiTimeout = TimeSpan.FromSeconds(20);

    public async Task<RecapDto?> ForSessionAsync(string userId, Guid sessionId, CancellationToken cancellationToken)
    {
        if (await sessions.GetAsync(userId, sessionId, cancellationToken) is not { } session) return null;
        var window = await stats.ResolveAsync(userId, StatsRange.Session, session.TimeZoneId, sessionId, cancellationToken);
        if (window is null) return null;

        var start = TimeZoneInfo.ConvertTime(session.StartedUtc, window.Zone);
        var facts = await GatherAsync(userId, window,
            $"Session {SessionDto.Tag(sessionId)}",
            $"{start:ddd d MMM, HH:mm} · {TemplateRecap.Duration(session.Duration(DateTimeOffset.UtcNow))}",
            await MomentsAsync(userId, session, cancellationToken),
            cancellationToken);
        return await WriteAsync(facts, cancellationToken);
    }

    /// <summary>A recap of part of a session, [<paramref name="fromUtc"/>, <paramref name="toUtc"/>): one story chapter.</summary>
    /// <param name="useAi">False returns the template recap without asking the model.</param>
    public async Task<RecapDto> ForStretchAsync(string userId, Session session, DateTimeOffset fromUtc, DateTimeOffset toUtc, string title, bool useAi, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var zone = session.TimeZone;
        // Whole minute buckets, so neighbouring chapters never count the same minute; the last one
        // runs to the end of the session's final minute, as the session range does.
        var windowTo = toUtc == session.EndedUtc ? toUtc.AddMinutes(1) : toUtc;
        var window = new StatsWindow(StatsRange.Session,
            RollupBuckets.StartUtc(RollupGrain.Minute, fromUtc, zone),
            RollupBuckets.StartUtc(RollupGrain.Minute, windowTo, zone),
            RollupGrain.Minute, zone);

        var from = TimeZoneInfo.ConvertTime(fromUtc, zone);
        var to = TimeZoneInfo.ConvertTime(toUtc, zone);
        var moments = (await MomentsAsync(userId, session, cancellationToken)).Where(m => m.AtUtc >= fromUtc && m.AtUtc < toUtc).ToList();
        var facts = await GatherAsync(userId, window, title, $"{from:HH:mm}–{to:HH:mm}", moments, cancellationToken);
        return useAi ? await WriteAsync(facts, cancellationToken) : TemplateRecap.Build(facts);
    }

    /// <param name="useAi">False returns the template recap without asking the model.</param>
    public async Task<RecapDto?> ForDayAsync(string userId, DateOnly day, string? timeZoneId, CancellationToken cancellationToken, bool useAi = true)
    {
        var window = await stats.ResolveAsync(userId, StatsRange.Day, timeZoneId, null, cancellationToken, day);
        if (window is null) return null;

        // Every session, not the latest hundred: a day long ago still has its moments.
        var daySessions = (await sessions.ListAsync(userId, int.MaxValue, cancellationToken))
            .Where(s => s.StartedUtc < window.ToUtc && (s.EndedUtc ?? DateTimeOffset.UtcNow) >= window.FromUtc);
        var moments = new List<MomentDto>();
        foreach (var session in daySessions)
            moments.AddRange((await MomentsAsync(userId, session, cancellationToken)).Where(m => m.AtUtc >= window.FromUtc && m.AtUtc < window.ToUtc));

        var facts = await GatherAsync(userId, window,
            day.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture),
            $"Daily recap · {window.Zone.Id}",
            [.. moments.OrderByDescending(m => m.Score)],
            cancellationToken);
        return useAi ? await WriteAsync(facts, cancellationToken) : TemplateRecap.Build(facts);
    }

    /// <summary>A session's notable moments, best first, with short-lived snapshot links.</summary>
    public async Task<IReadOnlyList<MomentDto>> MomentsAsync(string userId, Session session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var zone = session.TimeZone;
        var end = session.EndedUtc ?? DateTimeOffset.UtcNow;
        var notable = new List<SceneEvent>();
        for (var day = LocalDay.Of(session.StartedUtc, zone); day <= LocalDay.Of(end, zone); day = day.AddDays(1))
            notable.AddRange((await sensingLog.GetEventsAsync(userId, day, cancellationToken)).Where(e => e.SessionId == session.Id && e.Kind == SceneEventKind.Notable));

        var moments = new List<MomentDto>();
        foreach (var moment in notable.OrderByDescending(e => e.Score ?? 0).ThenBy(e => e.AtUtc).Take(12))
        {
            var url = moment.ImagePath is { } path ? await snapshots.CreateReadUrlAsync(userId, path, cancellationToken) : null;
            moments.Add(new MomentDto { AtUtc = moment.AtUtc, Text = moment.Text ?? string.Empty, Score = moment.Score ?? 0, ImageUrl = url?.ToString() });
        }
        return moments;
    }

    private async Task<RecapFacts> GatherAsync(string userId, StatsWindow window, string title, string subtitle, IReadOnlyList<MomentDto> moments, CancellationToken ct)
    {
        var presence = await stats.PresenceAsync(userId, window, ct);
        var objects = await stats.ObjectsAsync(userId, window, ct);
        var environment = await stats.EnvironmentAsync(userId, window, ct);
        var patterns = window.Range == StatsRange.Day ? await stats.PatternsAsync(userId, window, ct) : null;
        // Every tick and scene event of a short window: the story is told from them (SceneNarrator).
        var raw = await stats.RawAsync(userId, window, ct);
        return new RecapFacts(title, subtitle, window.FromUtc, window.ToUtc, window.Zone, presence, objects, environment, patterns, moments, raw?.Events, raw?.Ticks);
    }

    private async Task<RecapDto> WriteAsync(RecapFacts facts, CancellationToken cancellationToken)
    {
        var recap = TemplateRecap.Build(facts);
        if (!chatClients.Any() || facts.Presence.SecondsObserved <= 0) return recap;

        var prompt = RecapPrompt.User(recap);
        try
        {
            // The chosen model, then the next configured one if it fails; the number check below
            // applies to whichever answered.
            var (chat, rewrite) = await chatClients.FirstAnswerAsync("recap", AiTimeout, async (model, ct) =>
            {
                // Identical facts give an identical prompt, which the chat client's response cache answers
                // (RecapAi), so re-opening a past day or its PDF does not pay for the model again.
                var response = await model.GetResponseAsync(
                    [new ChatMessage(ChatRole.System, RecapPrompt.System), new ChatMessage(ChatRole.User, prompt)],
                    // A JSON schema, so there is no reply format to parse or guess at.
                    new ChatOptions { Temperature = 0.4f, MaxOutputTokens = 400, ResponseFormat = ChatResponseFormat.ForJsonSchema<RecapRewrite>() },
                    ct);
                return (model, string.IsNullOrWhiteSpace(response.Text) ? null : JsonSerializer.Deserialize<RecapRewrite>(response.Text, JsonSerializerOptions.Web));
            }, logger, cancellationToken);

            if (rewrite is null || string.IsNullOrWhiteSpace(rewrite.Summary)) return recap;
            if (!RecapPrompt.KeepsToFacts(rewrite.Summary + ' ' + string.Join(' ', rewrite.Highlights), prompt))
            {
                logger.LogWarning("AI recap used a number that is not in the facts; using the template recap.");
                return recap;
            }

            return RecapPrompt.Rewritten(recap, rewrite, chat.GetService<ChatClientMetadata>()?.ProviderName ?? "ai");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Every model in the chain failed (each failure is already logged): the template paragraph.
            logger.LogWarning(ex, "AI recap failed on every model; using the template recap.");
            return recap;
        }
    }
}
