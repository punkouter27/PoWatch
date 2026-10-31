using System.Globalization;
using Azure.Data.Tables;
using Microsoft.Extensions.Options;
using PoWatch.Application.Contracts;
using PoWatch.Application.Options;
using PoWatch.Domain.Models;

namespace PoWatch.Infrastructure.Persistence;

/// <summary>
/// Partition = user key; row = "{session}|c0003" for a chapter, "{session}|story" for the story, so a
/// session's chapters are one range read and sort in order.
/// </summary>
public sealed class AzureStoryStore(AzureStorageClients clients, IOptions<AzureStorageOptions> options) : IStoryStore
{
    // A table string property holds 32K characters; a story of a very long session is cut there.
    private const int MaxText = 30_000;
    private readonly TableClient _table = clients.TableService.GetTableClient(options.Value.StoriesTable);

    public async Task<IReadOnlyList<StoryChapter>> ListChaptersAsync(string userId, Guid sessionId, CancellationToken cancellationToken)
    {
        var prefix = $"{sessionId:N}|c";
        var filter = TableClient.CreateQueryFilter(
            $"PartitionKey eq {StatsEntityMapper.UserKey(userId)} and RowKey ge {prefix} and RowKey lt {$"{sessionId:N}|d"}");
        var chapters = new List<StoryChapter>();
        await foreach (var e in _table.QueryAsync<TableEntity>(filter, cancellationToken: cancellationToken))
        {
            chapters.Add(new StoryChapter(sessionId,
                int.Parse(e.RowKey[prefix.Length..], CultureInfo.InvariantCulture),
                e.GetDateTimeOffset("FromUtc") ?? DateTimeOffset.MinValue,
                e.GetDateTimeOffset("ToUtc") ?? DateTimeOffset.MinValue,
                e.GetString("Text") ?? string.Empty,
                e.GetString("Source") ?? "template"));
        }
        return [.. chapters.OrderBy(c => c.Index)];
    }

    public async Task UpsertChapterAsync(string userId, StoryChapter chapter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        await _table.UpsertEntityAsync(new TableEntity(StatsEntityMapper.UserKey(userId), $"{chapter.SessionId:N}|c{chapter.Index:D4}")
        {
            ["FromUtc"] = chapter.FromUtc,
            ["ToUtc"] = chapter.ToUtc,
            ["Text"] = Cut(chapter.Text),
            ["Source"] = chapter.Source
        }, TableUpdateMode.Replace, cancellationToken);
    }

    public async Task<SessionStory?> GetStoryAsync(string userId, Guid sessionId, CancellationToken cancellationToken)
    {
        var response = await _table.GetEntityIfExistsAsync<TableEntity>(StatsEntityMapper.UserKey(userId), StoryRow(sessionId), cancellationToken: cancellationToken);
        if (!response.HasValue) return null;
        var e = response.Value!;
        return new SessionStory(sessionId, e.GetString("Text") ?? string.Empty, e.GetString("Source") ?? "template",
            e.GetDateTimeOffset("WrittenUtc") ?? DateTimeOffset.MinValue);
    }

    public async Task UpsertStoryAsync(string userId, SessionStory story, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(story);
        await _table.UpsertEntityAsync(new TableEntity(StatsEntityMapper.UserKey(userId), StoryRow(story.SessionId))
        {
            ["Text"] = Cut(story.Text),
            ["Source"] = story.Source,
            ["WrittenUtc"] = story.WrittenUtc
        }, TableUpdateMode.Replace, cancellationToken);
    }

    private static string StoryRow(Guid sessionId) => $"{sessionId:N}|story";

    private static string Cut(string text) => text.Length <= MaxText ? text : text[..MaxText];
}
