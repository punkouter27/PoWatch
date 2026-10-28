using Azure.Data.Tables;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using PoWatch.Application.Contracts;
using PoWatch.Application.Options;

namespace PoWatch.Infrastructure.Persistence;

/// <summary>Deletes every row and snapshot stored for one user, across all tables.</summary>
public sealed class AzureUserDataStore(AzureStorageClients clients, IOptions<AzureStorageOptions> options) : IUserDataStore
{
    public async Task PurgeAsync(string userId, CancellationToken cancellationToken)
    {
        var filter = TableDeletes.UserPartitions(userId);
        foreach (var name in options.Value.AllTables())
            await TableDeletes.DeleteWhereAsync(clients.TableService.GetTableClient(name), filter, cancellationToken);

        // Shares are keyed by their token, not by user, so they are found by owner instead.
        var shares = clients.TableService.GetTableClient(options.Value.SharesTable);
        await TableDeletes.DeleteWhereAsync(shares, TableClient.CreateQueryFilter($"UserId eq {userId}"), cancellationToken);

        var snapshots = clients.BlobService.GetBlobContainerClient(options.Value.SnapshotsContainer);
        await foreach (var blob in snapshots.GetBlobsAsync(BlobTraits.None, BlobStates.None, AzureSnapshotStore.Prefix(userId), cancellationToken))
            await snapshots.DeleteBlobIfExistsAsync(blob.Name, cancellationToken: cancellationToken);
    }
}
