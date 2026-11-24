namespace PoWatch.Infrastructure.Runtime;

/// <summary>
/// Holds one expensive result (the health report behind <c>/health</c> and <c>/api/diag</c>) for a fixed
/// lifetime. Callers inside the lifetime get the same value and the same <see cref="Snapshot{T}.At"/>;
/// callers that arrive while it is being rebuilt wait for that one build instead of starting their own,
/// so a visitor pressing Refresh, or several probes at once, cannot multiply the calls to a dependency.
/// The build never sees a caller's cancellation: one caller giving up must not fail the report for the
/// others waiting on it (each check inside the build has its own timeout instead, <see cref="SnapshotCache.WithTimeoutAsync"/>).
/// </summary>
public sealed class SnapshotCache<T>(TimeProvider time, TimeSpan lifetime) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Snapshot<T>? _current;

    public TimeSpan Lifetime => lifetime;

    public async Task<Snapshot<T>> GetAsync(Func<CancellationToken, Task<T>> build, CancellationToken cancellationToken = default)
    {
        if (Fresh(_current) is { } hit)
            return hit;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Fresh(_current) is { } built)
                return built;

            var started = time.GetTimestamp();
            var value = await build(CancellationToken.None);
            _current = new Snapshot<T>(value, time.GetUtcNow(), time.GetElapsedTime(started));
            return _current;
        }
        finally
        {
            _gate.Release();
        }
    }

    private Snapshot<T>? Fresh(Snapshot<T>? snapshot) =>
        snapshot is not null && time.GetUtcNow() - snapshot.At < lifetime ? snapshot : null;

    public void Dispose() => _gate.Dispose();
}

/// <summary>Per-check deadlines for the checks a <see cref="SnapshotCache{T}"/> build runs.</summary>
public static class SnapshotCache
{
    /// <summary>
    /// Runs one check with its own deadline. A check that has not answered by then gives
    /// <paramref name="onTimeout"/> instead, so one stalled dependency cannot hold the whole report.
    /// </summary>
    public static async Task<TResult> WithTimeoutAsync<TResult>(
        Func<CancellationToken, Task<TResult>> check, TimeSpan timeout, Func<TResult> onTimeout, TimeProvider time, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var stopTimer = new CancellationTokenSource();
        var work = check(deadline.Token);
        var timer = Task.Delay(timeout, time, stopTimer.Token);
        if (await Task.WhenAny(work, timer) == work)
        {
            await stopTimer.CancelAsync();
            return await work;
        }

        await deadline.CancelAsync();
        // The check is abandoned; observe its outcome so a late fault is not an unobserved exception.
        _ = work.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return onTimeout();
    }
}

/// <summary>A cached value, when it was taken, and how long taking it took.</summary>
public sealed record Snapshot<T>(T Value, DateTimeOffset At, TimeSpan Took);
