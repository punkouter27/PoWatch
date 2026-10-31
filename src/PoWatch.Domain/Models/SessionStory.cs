namespace PoWatch.Domain.Models;

/// <summary>One ten-minute stretch of a session, summarised once it has closed.</summary>
public sealed record StoryChapter(Guid SessionId, int Index, DateTimeOffset FromUtc, DateTimeOffset ToUtc, string Text, string Source);

/// <summary>A finished session's chapters told as one story; written once, when the session has ended.</summary>
public sealed record SessionStory(Guid SessionId, string Text, string Source, DateTimeOffset WrittenUtc);
