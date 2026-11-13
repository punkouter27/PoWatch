using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Options;
using PoWatch.Application.Contracts;
using PoWatch.Application.Options;
using PoWatch.Domain.Models;
using PoWatch.Domain.Services;

namespace PoWatch.Infrastructure.Persistence;

/// <summary>
/// Rollups partitioned by user and grain, one row per bucket. Merges are read-merge-write with an
/// ETag check, retried on conflict, so two ingests landing in the same minute both count.
/// </summary>
public sealed class AzureRollupStore(AzureStorageClients clients, IOptions<AzureStorageOptions> options) : IRollupStore
{
    private const int MaxAttempts = 10;

    private readonly TableClient _table = clients.TableService.GetTableClient(options.Value.RollupsTable);

    public async Task MergeAsync(string userId, RollupGrain grain, DateTimeOffset bucketStartUtc, Rollup delta, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delta);
        var partition = Partition(userId, grain);
        var rowKey = StatsEntityMapper.Ticks(bucketStartUtc);

        for (var attempt = 1; ; attempt++)
        {
            var existing = await _table.GetEntityIfExistsAsync<TableEntity>(partition, rowKey, cancellationToken: cancellationToken);
            var current = existing.HasValue ? RollupMapper.ToRollup(existing.Value!) : Rollup.Empty;
            var merged = RollupMapper.ToEntity(partition, rowKey, Rollup.Merge(current, delta) with { BucketStartUtc = bucketStartUtc });

            try
            {
                if (existing.HasValue)
                    await _table.UpdateEntityAsync(merged, existing.Value!.ETag, TableUpdateMode.Replace, cancellationToken);
                else
                    await _table.AddEntityAsync(merged, cancellationToken);
                return;
            }
            catch (RequestFailedException ex) when (ex.Status is 409 or 412 && attempt < MaxAttempts)
            {
                // Someone else merged into this bucket first; re-read and merge on top of theirs.
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(5, 25 * attempt)), cancellationToken);
            }
        }
    }

    public async Task<IReadOnlyList<Rollup>> GetRangeAsync(string userId, RollupGrain grain, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        var partition = Partition(userId, grain);
        var from = StatsEntityMapper.Ticks(fromUtc);
        var to = StatsEntityMapper.Ticks(toUtc);
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {partition} and RowKey ge {from} and RowKey lt {to}");

        var rollups = new List<Rollup>();
        await foreach (var entity in _table.QueryAsync<TableEntity>(filter, cancellationToken: cancellationToken))
            rollups.Add(RollupMapper.ToRollup(entity));
        return rollups;
    }

    public async Task<Rollup> GetAllTimeAsync(string userId, CancellationToken cancellationToken)
    {
        var response = await _table.GetEntityIfExistsAsync<TableEntity>(
            Partition(userId, RollupGrain.AllTime), StatsEntityMapper.Ticks(DateTimeOffset.UnixEpoch), cancellationToken: cancellationToken);
        return response.HasValue ? RollupMapper.ToRollup(response.Value!) : Rollup.Empty;
    }

    private static string Partition(string userId, RollupGrain grain) => $"{StatsEntityMapper.UserKey(userId)}|{grain}";
}

/// <summary>Rollup ↔ entity. Empty stats omit min/max so no infinities reach the table.</summary>
internal static class RollupMapper
{
    public static TableEntity ToEntity(string partitionKey, string rowKey, Rollup r)
    {
        var entity = new TableEntity(partitionKey, rowKey)
        {
            ["BucketStartUtc"] = r.BucketStartUtc,
            ["Ticks"] = r.Ticks,
            ["Seconds"] = r.Seconds,
            ["PixelSamples"] = r.PixelSamples,
            ["DetectorSamples"] = r.DetectorSamples,
            ["OccupiedTicks"] = r.OccupiedTicks,
            ["PeakConcurrency"] = r.PeakConcurrency,
            ["Grid"] = StatsEntityMapper.Pack(r.Grid.Span),
            ["PresenceGrid"] = StatsEntityMapper.Pack(r.PresenceGrid.Span),
            ["Classes"] = StatsEntityMapper.ToJson(r.Classes),
            ["Palette"] = StatsEntityMapper.ToJson(r.Palette),
            ["SittingSeconds"] = r.SittingSeconds,
            ["StandingSeconds"] = r.StandingSeconds,
            ["LyingSeconds"] = r.LyingSeconds,
            ["Visits"] = r.Visits,
            ["Captions"] = r.Captions,
            ["DwellHistogram"] = StatsEntityMapper.ToJson(r.DwellHistogram),
            ["DwellMaxSeconds"] = r.DwellMaxSeconds,
            ["Entries"] = StatsEntityMapper.ToJson(r.Entries),
            ["Exits"] = StatsEntityMapper.ToJson(r.Exits),
            ["Regulars"] = StatsEntityMapper.ToJson(r.Regulars)
        };
        WriteStat(entity, "Motion", r.Motion);
        WriteStat(entity, "MotionPeak", r.MotionPeak);
        WriteStat(entity, "Luminance", r.Luminance);
        return entity;
    }

    public static Rollup ToRollup(TableEntity e) => new()
    {
        BucketStartUtc = e.GetDateTimeOffset("BucketStartUtc")!.Value,
        Ticks = e.GetInt64("Ticks") ?? 0,
        Seconds = e.GetDouble("Seconds") ?? 0,
        PixelSamples = e.GetInt64("PixelSamples") ?? 0,
        DetectorSamples = e.GetInt64("DetectorSamples") ?? 0,
        OccupiedTicks = e.GetInt64("OccupiedTicks") ?? 0,
        PeakConcurrency = e.GetInt32("PeakConcurrency") ?? 0,
        Motion = ReadStat(e, "Motion"),
        MotionPeak = ReadStat(e, "MotionPeak"),
        Luminance = ReadStat(e, "Luminance"),
        Grid = StatsEntityMapper.Unpack(e.GetBinary("Grid")),
        PresenceGrid = StatsEntityMapper.Unpack(e.GetBinary("PresenceGrid")),
        Classes = StatsEntityMapper.Json<Dictionary<string, ClassTotals>>(e, "Classes") ?? [],
        Palette = StatsEntityMapper.Json<Dictionary<int, long>>(e, "Palette") ?? [],
        SittingSeconds = e.GetDouble("SittingSeconds") ?? 0,
        StandingSeconds = e.GetDouble("StandingSeconds") ?? 0,
        LyingSeconds = e.GetDouble("LyingSeconds") ?? 0,
        Visits = e.GetInt64("Visits") ?? 0,
        Captions = e.GetInt64("Captions") ?? 0,
        DwellHistogram = StatsEntityMapper.Json<Dictionary<int, long>>(e, "DwellHistogram") ?? [],
        DwellMaxSeconds = e.GetDouble("DwellMaxSeconds") ?? 0,
        Entries = StatsEntityMapper.Json<Dictionary<FrameEdge, long>>(e, "Entries") ?? [],
        Exits = StatsEntityMapper.Json<Dictionary<FrameEdge, long>>(e, "Exits") ?? [],
        Regulars = StatsEntityMapper.Json<Dictionary<string, RegularTotals>>(e, "Regulars") ?? []
    };

    private static void WriteStat(TableEntity entity, string name, RunningStat stat)
    {
        entity[$"{name}Count"] = stat.Count;
        entity[$"{name}Sum"] = stat.Sum;
        entity[$"{name}SumSquares"] = stat.SumSquares;
        if (stat.Count == 0) return;
        entity[$"{name}Min"] = stat.Min;
        entity[$"{name}Max"] = stat.Max;
    }

    private static RunningStat ReadStat(TableEntity e, string name)
    {
        var count = e.GetInt64($"{name}Count") ?? 0;
        return count == 0
            ? RunningStat.Empty
            : new RunningStat(count, e.GetDouble($"{name}Sum") ?? 0, e.GetDouble($"{name}SumSquares") ?? 0,
                e.GetDouble($"{name}Min") ?? 0, e.GetDouble($"{name}Max") ?? 0);
    }
}
