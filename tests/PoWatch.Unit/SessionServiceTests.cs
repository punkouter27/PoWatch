using Microsoft.Extensions.Time.Testing;
using PoWatch.Application.Contracts;
using PoWatch.Application.Features.Sessions;
using PoWatch.Domain.Models;
using PoWatch.Domain.Services;

namespace PoWatch.Unit;

public sealed class SessionServiceTests
{
    private static readonly DateTimeOffset StartUtc = new(2026, 10, 17, 17, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Story_chapters_close_every_ten_minutes_and_the_last_one_when_the_session_ends()
    {
        // Running 25 minutes: two full chapters; the third is still open.
        var running = StoryChapters.Closed(StartUtc, null, StartUtc.AddMinutes(25));
        Assert.Equal([0, 1], running.Select(c => c.Index));
        Assert.Equal(StartUtc.AddMinutes(10), running[0].ToUtc);
        Assert.Equal(StartUtc.AddMinutes(20), running[1].ToUtc);

        // Ended at 25 minutes: the short last chapter closes with it.
        var ended = StoryChapters.Closed(StartUtc, StartUtc.AddMinutes(25), StartUtc.AddHours(1));
        Assert.Equal((2, StartUtc.AddMinutes(20), StartUtc.AddMinutes(25)), ended[^1]);
        Assert.Equal(3, ended.Count);

        Assert.Empty(StoryChapters.Closed(StartUtc, null, StartUtc.AddMinutes(9)));
        Assert.Empty(StoryChapters.Closed(StartUtc, StartUtc, StartUtc));
    }

    [Fact]
    public async Task Stale_sessions_are_closed_at_their_last_activity()
    {
        var time = new FakeTimeProvider(StartUtc);
        var repository = new InMemorySessions();
        var sessions = new SessionService(repository, time);
        var session = await sessions.StartAsync("user", "America/New_York", Guid.NewGuid(), CancellationToken.None);

        time.Advance(SessionService.StaleAfter + TimeSpan.FromSeconds(1));
        var listed = await sessions.ListAsync("user", 10, CancellationToken.None);

        var closed = Assert.Single(listed);
        Assert.False(closed.IsRunning);
        Assert.Equal(session.LastActivityUtc, closed.EndedUtc);
    }

    [Fact]
    public async Task Recent_batch_activity_keeps_a_session_open()
    {
        var time = new FakeTimeProvider(StartUtc);
        var repository = new InMemorySessions();
        var sessions = new SessionService(repository, time);
        var session = await sessions.StartAsync("user", "America/New_York", Guid.NewGuid(), CancellationToken.None);

        time.Advance(SessionService.StaleAfter);
        await repository.TouchAsync("user", session.Id, time.GetUtcNow(), CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));

        var listed = await sessions.ListAsync("user", 10, CancellationToken.None);

        Assert.True(Assert.Single(listed).IsRunning);
    }

    private sealed class InMemorySessions : ISessionRepository
    {
        private readonly Dictionary<Guid, Session> _sessions = [];

        public Task UpsertAsync(Session session, CancellationToken cancellationToken)
        {
            _sessions[session.Id] = session;
            return Task.CompletedTask;
        }

        public Task<Session?> GetAsync(string userId, Guid sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(_sessions.GetValueOrDefault(sessionId));

        public Task<IReadOnlyList<Session>> ListAsync(string userId, int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Session>>(_sessions.Values.OrderByDescending(s => s.StartedUtc).Take(take).ToList());

        public Task TouchAsync(string userId, Guid sessionId, DateTimeOffset atUtc, CancellationToken cancellationToken)
        {
            if (_sessions.GetValueOrDefault(sessionId) is { IsRunning: true } session)
                _sessions[sessionId] = session with { LastActivityUtc = atUtc };
            return Task.CompletedTask;
        }

        public Task<bool> StopIfStaleAsync(string userId, Guid sessionId, DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
        {
            if (_sessions.GetValueOrDefault(sessionId) is { IsRunning: true } session && session.LastActivityUtc < cutoffUtc)
            {
                _sessions[sessionId] = session.Stop(session.LastActivityUtc);
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }
    }
}
