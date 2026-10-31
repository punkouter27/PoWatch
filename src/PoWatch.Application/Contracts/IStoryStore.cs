using PoWatch.Domain.Models;

namespace PoWatch.Application.Contracts;

/// <summary>A session's ten-minute chapters and, once it has ended, its story.</summary>
public interface IStoryStore
{
    /// <summary>The session's chapters, first first; empty when none has been written.</summary>
    Task<IReadOnlyList<StoryChapter>> ListChaptersAsync(string userId, Guid sessionId, CancellationToken cancellationToken);

    Task UpsertChapterAsync(string userId, StoryChapter chapter, CancellationToken cancellationToken);

    Task<SessionStory?> GetStoryAsync(string userId, Guid sessionId, CancellationToken cancellationToken);

    Task UpsertStoryAsync(string userId, SessionStory story, CancellationToken cancellationToken);
}
