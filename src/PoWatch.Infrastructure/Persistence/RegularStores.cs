using System.Collections.Concurrent;
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
        if (regular.Name is not null) entity["Name"] = regular.Name;
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
        Name = e.GetString("Name"),
        Signature = StatsEntityMapper.Unpack(e.GetBinary("Signature")),
        Sightings = e.GetInt32("Sightings") ?? 0,
        FirstSeenUtc = e.GetDateTimeOffset("FirstSeenUtc")!.Value,
        LastSeenUtc = e.GetDateTimeOffset("LastSeenUtc")!.Value,
        Visits = e.GetInt64("Visits") ?? 0,
        DwellSeconds = e.GetDouble("DwellSeconds") ?? 0
    };
}
