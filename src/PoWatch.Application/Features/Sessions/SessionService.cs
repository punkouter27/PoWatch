using PoWatch.Application.Contracts;
using PoWatch.Domain.Models;

namespace PoWatch.Application.Features.Sessions;

/// <summary>Starts, stops and lists a user's observation sessions. Start and stop are both safe to retry.</summary>
public sealed class SessionService(ISessionRepository sessions, TimeProvider time)
{
    public const int MaxListed = 100;
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    /// <exception cref="ArgumentException">The time zone is not a known IANA or Windows zone.</exception>
    public async Task<Session> StartAsync(string userId, string timeZoneId, Guid? sessionId, CancellationToken cancellationToken)
    {
        await ExpireStaleAsync(userId, cancellationToken);
        if (sessionId is { } id && await sessions.GetAsync(userId, id, cancellationToken) is { } existing)
            return existing;

        var session = Session.Start(sessionId ?? Guid.NewGuid(), userId, time.GetUtcNow(), timeZoneId);
        await sessions.UpsertAsync(session, cancellationToken);
        return session;
    }

    /// <summary>Stops the session; a session that already stopped keeps its first end time. Null when not found.</summary>
    public async Task<Session?> StopAsync(string userId, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(userId, sessionId, cancellationToken);
        if (session is null || !session.IsRunning)
            return session;

        var stopped = session.Stop(Max(time.GetUtcNow(), session.StartedUtc));
        await sessions.UpsertAsync(stopped, cancellationToken);
        return stopped;
    }

    public async Task<Session?> GetAsync(string userId, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(userId, sessionId, cancellationToken);
        if (session is not { IsRunning: true }) return session;
        var cutoff = time.GetUtcNow() - StaleAfter;
        if (session.LastActivityUtc >= cutoff) return session;
        await sessions.StopIfStaleAsync(userId, sessionId, cutoff, cancellationToken);
        return await sessions.GetAsync(userId, sessionId, cancellationToken);
    }

    public async Task<IReadOnlyList<Session>> ListAsync(string userId, int take, CancellationToken cancellationToken)
    {
        await ExpireStaleAsync(userId, cancellationToken);
        return await sessions.ListAsync(userId, Math.Clamp(take, 1, MaxListed), cancellationToken);
    }

    /// <summary>Every session that overlaps [<paramref name="fromUtc"/>, <paramref name="toUtc"/>), oldest first, however long ago.</summary>
    public async Task<IReadOnlyList<Session>> ListBetweenAsync(string userId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        await ExpireStaleAsync(userId, cancellationToken);
        var now = time.GetUtcNow();
        return (await sessions.ListAsync(userId, int.MaxValue, cancellationToken))
            .Where(s => s.StartedUtc < toUtc && (s.EndedUtc ?? now) >= fromUtc)
            .OrderBy(s => s.StartedUtc)
            .ToList();
    }

    public DateTimeOffset Now => time.GetUtcNow();

    private async Task ExpireStaleAsync(string userId, CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow() - StaleAfter;
        var sessionsForUser = await sessions.ListAsync(userId, int.MaxValue, cancellationToken);
        foreach (var session in sessionsForUser.Where(s => s.IsRunning && s.LastActivityUtc < cutoff))
            await sessions.StopIfStaleAsync(userId, session.Id, cutoff, cancellationToken);
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
