using Microsoft.Extensions.Time.Testing;
using PoWatch.Infrastructure.Runtime;

namespace PoWatch.Unit;

/// <summary>
/// The one health report behind /health and /api/diag is cached for a minute and every check in it has a
/// deadline (SnapshotCache, used by HealthSnapshot and ServiceProbes in the API).
/// </summary>
public sealed class SnapshotCacheTests
{
    [Fact]
    public async Task Loads_inside_the_lifetime_share_one_build_and_one_check_time()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero));
        using var cache = new SnapshotCache<int>(time, TimeSpan.FromSeconds(60));
        var builds = 0;
        Task<int> Build(CancellationToken _) => Task.FromResult(++builds);

        var first = await cache.GetAsync(Build);
        time.Advance(TimeSpan.FromSeconds(59));
        var second = await cache.GetAsync(Build);

        Assert.Equal(1, builds);
        Assert.Same(first, second);
        Assert.Equal(first.At, second.At);

        time.Advance(TimeSpan.FromSeconds(1));
        var third = await cache.GetAsync(Build);

        Assert.Equal(2, builds);
        Assert.Equal(2, third.Value);
        Assert.Equal(time.GetUtcNow(), third.At);
    }

    [Fact]
    public async Task Callers_that_arrive_during_a_build_wait_for_it_instead_of_starting_their_own()
    {
        using var cache = new SnapshotCache<string>(TimeProvider.System, TimeSpan.FromSeconds(60));
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builds = 0;
        Task<string> Build(CancellationToken _) { Interlocked.Increment(ref builds); return release.Task; }

        var waiting = Enumerable.Range(0, 5).Select(_ => cache.GetAsync(Build)).ToArray();
        release.SetResult("report");
        var results = await Task.WhenAll(waiting);

        Assert.Equal(1, builds);
        Assert.All(results, r => Assert.Same(results[0], r));
    }

    [Fact]
    public async Task A_caller_that_gives_up_does_not_cancel_the_build_for_everyone_else()
    {
        using var cache = new SnapshotCache<int>(TimeProvider.System, TimeSpan.FromSeconds(60));
        CancellationToken seen = default;
        using var gaveUp = new CancellationTokenSource();
        await gaveUp.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetAsync(ct => { seen = ct; return Task.FromResult(1); }, gaveUp.Token));
        var report = await cache.GetAsync(ct => { seen = ct; return Task.FromResult(2); });

        Assert.Equal(2, report.Value);
        Assert.False(seen.CanBeCanceled);
    }

    [Fact]
    public async Task A_check_that_misses_its_deadline_gives_its_timeout_answer_and_is_cancelled()
    {
        var time = new FakeTimeProvider();
        CancellationToken checkToken = default;
        var stalled = SnapshotCache.WithTimeoutAsync(
            async ct => { checkToken = ct; await Task.Delay(Timeout.Infinite, ct); return "answered"; },
            TimeSpan.FromSeconds(10), () => "timed out", time);

        time.Advance(TimeSpan.FromSeconds(9));
        Assert.False(stalled.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal("timed out", await stalled);
        Assert.True(checkToken.IsCancellationRequested);
    }

    [Fact]
    public async Task A_check_that_answers_in_time_keeps_its_own_answer()
    {
        var answer = await SnapshotCache.WithTimeoutAsync(_ => Task.FromResult("Healthy"), TimeSpan.FromSeconds(10), () => "timed out", new FakeTimeProvider());

        Assert.Equal("Healthy", answer);
    }
}
