namespace PoWatch.Domain.Services;

/// <summary>Where a session's story chapters fall: every ten minutes from its start.</summary>
public static class StoryChapters
{
    public static readonly TimeSpan Length = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The chapters that have closed by <paramref name="nowUtc"/>: every full ten minutes of a running
    /// session, and once it has ended, the shorter last one too.
    /// </summary>
    public static IReadOnlyList<(int Index, DateTimeOffset FromUtc, DateTimeOffset ToUtc)> Closed(
        DateTimeOffset startedUtc, DateTimeOffset? endedUtc, DateTimeOffset nowUtc)
    {
        var end = endedUtc ?? nowUtc;
        var chapters = new List<(int, DateTimeOffset, DateTimeOffset)>();
        for (var index = 0; startedUtc + (index * Length) < end; index++)
        {
            var from = startedUtc + (index * Length);
            var to = from + Length;
            if (to > end)
            {
                if (endedUtc is null) break;
                to = end;
            }
            chapters.Add((index, from, to));
        }
        return chapters;
    }
}
