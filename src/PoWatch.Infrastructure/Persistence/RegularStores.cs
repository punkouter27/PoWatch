using Azure.Data.Tables;
using Microsoft.Extensions.Options;
using PoWatch.Application.Contracts;
using PoWatch.Application.Options;
using PoWatch.Domain.Models;

namespace PoWatch.Infrastructure.Persistence;

/// <summary>Regulars partitioned by user, one row per regular; the signature is packed as float bytes.</summary>
public sealed class AzureRegularStore(AzureStorageClients clients, IOptions<AzureStorageOptions> options) : IRegularStore
{
    private readonly TableClient _table = clients.TableService.GetTableClient(options.Value.RegularsTable);

    public async Task<IReadOnlyList<Regular>> ListAsync(string userId, CancellationToken cancellationToken)
    {
        var partition = StatsEntityMapper.UserKey(userId);
        var regulars = new List<Regular>();
        await foreach (var entity in _table.QueryAsync<TableEntity>(e => e.PartitionKey == partition, cancellationToken: cancellationToken))
            regulars.Add(ToRegular(entity));
        return regulars;
    }

    public async Task<Regular?> GetAsync(string userId, string regularId, CancellationToken cancellationToken)
    {
        var response = await _table.GetEntityIfExistsAsync<TableEntity>(StatsEntityMapper.UserKey(userId), regularId, cancellationToken: cancellationToken);
        return response.HasValue ? ToRegular(response.Value!) : null;
    }

    public async Task UpsertAsync(Regular regular, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(regular);
        var entity = new TableEntity(StatsEntityMapper.UserKey(regular.UserId), regular.Id)
        {
            ["UserId"] = regular.UserId,
            ["Class"] = regular.Class,
            ["Number"] = regular.Number,
            ["Signature"] = StatsEntityMapper.Pack(regular.Signature.ToArray()),
            ["Sightings"] = regular.Sightings,
            ["FirstSeenUtc"] = regular.FirstSeenUtc,
            ["LastSeenUtc"] = regular.LastSeenUtc,
            ["Visits"] = regular.Visits,
            ["DwellSeconds"] = regular.DwellSeconds
        };
        await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken);
    }

    public async Task DeleteAsync(string userId, string regularId, CancellationToken cancellationToken) =>
        await _table.DeleteEntityAsync(StatsEntityMapper.UserKey(userId), regularId, cancellationToken: cancellationToken);

    private static Regular ToRegular(TableEntity e) => new()
    {
        Id = e.RowKey,
        UserId = e.GetString("UserId"),
        Class = e.GetString("Class"),
        Number = e.GetInt32("Number") ?? 0,
        Signature = StatsEntityMapper.Unpack(e.GetBinary("Signature")),
        Sightings = e.GetInt32("Sightings") ?? 0,
        FirstSeenUtc = e.GetDateTimeOffset("FirstSeenUtc")!.Value,
        LastSeenUtc = e.GetDateTimeOffset("LastSeenUtc")!.Value,
        Visits = e.GetInt64("Visits") ?? 0,
        DwellSeconds = e.GetDouble("DwellSeconds") ?? 0
    };
}

/// <summary>
/// Face-matched anonymous person profiles stored in RegularsTable under partition "{UserKey}|face"
/// so Delete My Data automatically purges them with zero extra Azure tables.
/// </summary>
public sealed class AzurePersonProfileStore(AzureStorageClients clients, IOptions<AzureStorageOptions> options) : IPersonProfileStore
{
    private readonly TableClient _table = clients.TableService.GetTableClient(options.Value.RegularsTable);

    private static string Partition(string userId) => $"{StatsEntityMapper.UserKey(userId)}|face";

    public async Task<IReadOnlyList<PersonProfile>> ListAsync(string userId, CancellationToken cancellationToken)
    {
        var partition = Partition(userId);
        var list = new List<PersonProfile>();
        await foreach (var entity in _table.QueryAsync<TableEntity>(e => e.PartitionKey == partition, cancellationToken: cancellationToken))
            list.Add(ToProfile(entity));
        return list;
    }

    public async Task<PersonProfile?> GetAsync(string userId, string personId, CancellationToken cancellationToken)
    {
        var response = await _table.GetEntityIfExistsAsync<TableEntity>(Partition(userId), personId, cancellationToken: cancellationToken);
        return response.HasValue ? ToProfile(response.Value!) : null;
    }

    public async Task UpsertAsync(PersonProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var entity = new TableEntity(Partition(profile.UserId), profile.Id)
        {
            ["UserId"] = profile.UserId,
            ["Number"] = profile.Number,
            ["Descriptor"] = StatsEntityMapper.Pack(profile.Descriptor),
            ["DescriptorCount"] = profile.DescriptorCount,
            ["SessionsSeen"] = profile.SessionsSeen,
            ["LastSessionId"] = profile.LastSessionId,
            ["FirstSeenUtc"] = profile.FirstSeenUtc,
            ["LastSeenUtc"] = profile.LastSeenUtc,
            ["StatsJson"] = StatsEntityMapper.ToJson(profile.Stats)
        };
        if (!string.IsNullOrWhiteSpace(profile.CustomName)) entity["CustomName"] = profile.CustomName.Trim();
        if (!string.IsNullOrWhiteSpace(profile.ThumbnailDataUrl)) entity["ThumbnailDataUrl"] = profile.ThumbnailDataUrl;
        await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken);
    }

    public async Task DeleteAsync(string userId, string personId, CancellationToken cancellationToken) =>
        await _table.DeleteEntityAsync(Partition(userId), personId, cancellationToken: cancellationToken);

    private static PersonProfile ToProfile(TableEntity e) => new()
    {
        Id = e.RowKey,
        UserId = e.GetString("UserId"),
        Number = e.GetInt32("Number") ?? 1,
        CustomName = e.GetString("CustomName"),
        ThumbnailDataUrl = e.GetString("ThumbnailDataUrl"),
        Descriptor = StatsEntityMapper.Unpack(e.GetBinary("Descriptor")),
        DescriptorCount = e.GetInt32("DescriptorCount") ?? 1,
        SessionsSeen = e.GetInt32("SessionsSeen") ?? 1,
        LastSessionId = e.GetGuid("LastSessionId") ?? Guid.Empty,
        FirstSeenUtc = e.GetDateTimeOffset("FirstSeenUtc") ?? DateTimeOffset.UtcNow,
        LastSeenUtc = e.GetDateTimeOffset("LastSeenUtc") ?? DateTimeOffset.UtcNow,
        Stats = StatsEntityMapper.Json<PersonProfileStats>(e, "StatsJson") ?? new PersonProfileStats()
    };
}
