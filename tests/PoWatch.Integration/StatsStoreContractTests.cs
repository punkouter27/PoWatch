using Microsoft.Extensions.Options;
using PoWatch.Application.Contracts;
using PoWatch.Application.Options;
using PoWatch.Domain.Models;
using PoWatch.Domain.Services;
using PoWatch.Infrastructure.Persistence;

namespace PoWatch.Integration;

/// <summary>
/// Behavioural contract for the Azure stats stores (against Azurite): keys, ordering and replays.
/// </summary>
public sealed class StatsStoreContractTests(AzuriteWebApplicationFactory factory) : IClassFixture<AzuriteWebApplicationFactory>
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Day = new(2026, 9, 24);

    private IEnumerable<(string Name, Func<ISessionRepository> Sessions, Func<IIngestLedger> Ledger)> SessionStores()
    {
        yield return ("azure", () => new AzureSessionRepository(AzureClients(), Options), () => new AzureIngestLedger(AzureClients(), Options));
    }

    private IEnumerable<(string Name, Func<ISensingLog> Log)> SensingLogs()
    {
        yield return ("azure", () => new AzureSensingLog(AzureClients(), Options));
    }

    private IOptions<AzureStorageOptions> Options =>
        Microsoft.Extensions.Options.Options.Create(new AzureStorageOptions { ConnectionString = factory.StorageConnectionString });

    /// <summary>Booting the host runs the storage initializer, which creates every table.</summary>
    private AzureStorageClients AzureClients()
    {
        _ = factory.Services;
        return new AzureStorageClients(Options);
    }

    private IEnumerable<(string Name, Func<IRollupStore> Rollups)> StatsStores()
    {
        yield return ("azure", () => new AzureRollupStore(AzureClients(), Options));
    }

    private static string NewUser() => $"user-{Guid.NewGuid():N}";

    [DockerFact]
    public async Task Sessions_list_newest_first_per_user_and_batches_claim_once()
    {
        foreach (var (name, newSessions, newLedger) in SessionStores())
        {
            var sessions = newSessions();
            var user = NewUser();
            var older = Session.Start(Guid.NewGuid(), user, T0, "UTC");
            var newer = Session.Start(Guid.NewGuid(), user, T0.AddHours(1), "UTC");

            await sessions.UpsertAsync(older, CancellationToken.None);
            await sessions.UpsertAsync(newer, CancellationToken.None);
            await sessions.UpsertAsync(Session.Start(Guid.NewGuid(), NewUser(), T0, "UTC"), CancellationToken.None);
            await sessions.UpsertAsync(older.Stop(T0.AddMinutes(30)), CancellationToken.None);

            var listed = await sessions.ListAsync(user, 10, CancellationToken.None);
            Assert.True(listed.Select(s => s.Id).SequenceEqual([newer.Id, older.Id]), name);
            Assert.Equal(T0.AddMinutes(30), (await sessions.GetAsync(user, older.Id, CancellationToken.None))!.EndedUtc);
            Assert.Null(await sessions.GetAsync(user, Guid.NewGuid(), CancellationToken.None));

            var ledger = newLedger();
            var batch = Guid.NewGuid();
            Assert.True(await ledger.TryClaimAsync(user, batch, CancellationToken.None), name);
            Assert.False(await ledger.TryClaimAsync(user, batch, CancellationToken.None), name);
            Assert.True(await ledger.TryClaimAsync(NewUser(), batch, CancellationToken.None), name);
        }
    }

    [DockerFact]
    public async Task The_sensing_log_keeps_one_row_per_tick_and_event_across_replays()
    {
        foreach (var (name, newLog) in SensingLogs())
        {
            var log = newLog();
            var user = NewUser();
            var session = Guid.NewGuid();
            var grid = Enumerable.Repeat(0.25f, SpatialGrid.Cells).ToArray();
            Tick[] ticks =
            [
                new() { SessionId = session, StartUtc = T0.AddSeconds(10), MotionMean = 0.2, MotionGrid = grid, Palette = [0xFF8000],
                        Classes = new Dictionary<string, ClassCount> { ["person"] = new(2, 1.5) }, ActiveTrackIds = ["T1"] },
                new() { SessionId = session, StartUtc = T0, MotionMean = 0.1 },
            ];
            SceneEvent[] events =
            [
                new() { SessionId = session, AtUtc = T0, Kind = SceneEventKind.TrackEnter, TrackId = "T1", Class = "person", Edge = FrameEdge.Left },
                new() { SessionId = session, AtUtc = T0.AddSeconds(5), Kind = SceneEventKind.Caption, Text = "A cat naps.", Score = 0.4 },
            ];

            await log.AppendAsync(user, Day, ticks, events, CancellationToken.None);
            await log.AppendAsync(user, Day, ticks, events, CancellationToken.None);

            var storedTicks = await log.GetTicksAsync(user, Day, CancellationToken.None);
            var storedEvents = await log.GetEventsAsync(user, Day, CancellationToken.None);

            Assert.Equal(2, storedTicks.Count);
            Assert.Equal(T0, storedTicks[0].StartUtc);
            Assert.Equal(0.2, storedTicks[1].MotionMean);
            Assert.Equal(grid, storedTicks[1].MotionGrid);
            Assert.Equal([0xFF8000], storedTicks[1].Palette);
            Assert.Equal(new ClassCount(2, 1.5), storedTicks[1].Classes["person"]);
            Assert.Equal(["T1"], storedTicks[1].ActiveTrackIds);
            Assert.Equal(2, storedEvents.Count);
            Assert.Equal(FrameEdge.Left, storedEvents[0].Edge);
            Assert.Equal("A cat naps.", storedEvents[1].Text);
            Assert.Empty(await log.GetTicksAsync(user, Day.AddDays(1), CancellationToken.None));
            Assert.True(storedTicks.Count == 2, name);
        }
    }

    [DockerFact]
    public async Task Rollups_merge_per_bucket_and_concurrent_merges_all_count()
    {
        foreach (var (name, newRollups) in StatsStores())
        {
            var rollups = newRollups();
            var user = NewUser();
            var grid = new float[SpatialGrid.Cells];
            grid[5] = 1;
            var tick = Rollup.FromTick(new Tick
            {
                SessionId = Guid.NewGuid(),
                StartUtc = T0.AddSeconds(20),
                MotionMean = 0.3,
                MotionGrid = grid,
                Palette = [0x102030],
                Classes = new Dictionary<string, ClassCount> { ["cat"] = new(1, 1) }
            });
            var visit = Rollup.FromEvent(new SceneEvent
            {
                SessionId = Guid.NewGuid(),
                AtUtc = T0,
                Kind = SceneEventKind.TrackExit,
                TrackId = "T1",
                Class = "person",
                Edge = FrameEdge.Top,
                DwellSeconds = 42
            });

            await rollups.MergeAsync(user, RollupGrain.Minute, T0, tick, CancellationToken.None);
            await rollups.MergeAsync(user, RollupGrain.Minute, T0, tick, CancellationToken.None);
            await rollups.MergeAsync(user, RollupGrain.Minute, T0, visit, CancellationToken.None);
            await rollups.MergeAsync(user, RollupGrain.Minute, T0.AddMinutes(1), tick, CancellationToken.None);
            await rollups.MergeAsync(user, RollupGrain.AllTime, DateTimeOffset.UnixEpoch, tick, CancellationToken.None);

            var range = await rollups.GetRangeAsync(user, RollupGrain.Minute, T0, T0.AddMinutes(1), CancellationToken.None);
            var bucket = Assert.Single(range);
            Assert.Equal(T0, bucket.BucketStartUtc);
            Assert.Equal(2, bucket.Ticks);
            Assert.Equal(0.3, bucket.Motion.Mean, 12);
            Assert.Equal(2f, bucket.Grid.Span[5]);
            Assert.Equal(2, bucket.Classes["cat"].TicksPresent);
            Assert.Equal(2, bucket.Palette[Rollup.QuantizeColor(0x102030)]);
            Assert.Equal(1, bucket.Exits[FrameEdge.Top]);
            Assert.Equal(42, bucket.DwellMaxSeconds);
            Assert.Equal(1, (await rollups.GetAllTimeAsync(user, CancellationToken.None)).Ticks);
            Assert.Equal(0, (await rollups.GetAllTimeAsync(NewUser(), CancellationToken.None)).Ticks);
            Assert.True(range.Count == 1, name);

            // Concurrent merges into one bucket all count: the ETag check re-merges on conflict.
            var hot = T0.AddHours(1);
            await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => rollups.MergeAsync(user, RollupGrain.Hour, hot, tick, CancellationToken.None)));
            var hotBucket = Assert.Single(await rollups.GetRangeAsync(user, RollupGrain.Hour, hot, hot.AddHours(1), CancellationToken.None));
            Assert.True(hotBucket.Ticks == 20, $"{name}: {hotBucket.Ticks} of 20 concurrent merges counted");
        }
    }
}
