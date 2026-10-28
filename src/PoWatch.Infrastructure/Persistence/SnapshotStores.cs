using System.Globalization;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Options;
using PoWatch.Application.Contracts;
using PoWatch.Application.Options;

namespace PoWatch.Infrastructure.Persistence;

/// <summary>Snapshots in Blob Storage at {user}/{yyyyMMdd}/{id}.jpg, handed out as short-lived SAS links.</summary>
public sealed class AzureSnapshotStore(AzureStorageClients clients, IOptions<AzureStorageOptions> options) : ISnapshotStore
{
    private static readonly TimeSpan UploadWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ReadWindow = TimeSpan.FromMinutes(30);

    /// <summary>A user delegation key may live up to 7 days; one a day keeps the request rare.</summary>
    private static readonly TimeSpan DelegationKeyLife = TimeSpan.FromDays(1);

    /// <summary>Clocks drift: links start a little in the past so a freshly signed one is never "not yet valid".</summary>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    private readonly BlobServiceClient _service = clients.BlobService;
    private readonly BlobContainerClient _container = clients.BlobService.GetBlobContainerClient(options.Value.SnapshotsContainer);
    private volatile UserDelegationKey? _delegationKey;

    public async Task<SnapshotUpload?> CreateUploadAsync(string userId, DateOnly localDay, CancellationToken cancellationToken)
    {
        var prefix = $"{Prefix(userId)}{localDay.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}/";

        var count = 0;
        await foreach (var _ in _container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, cancellationToken))
        {
            if (++count >= ISnapshotStore.DailyCap) return null;
        }

        var path = $"{prefix}{Guid.NewGuid():N}.jpg";
        var url = await SignAsync(_container.GetBlobClient(path), BlobSasPermissions.Create | BlobSasPermissions.Write, UploadWindow, cancellationToken);
        return new SnapshotUpload(path, url);
    }

    public async Task<Uri?> CreateReadUrlAsync(string userId, string path, CancellationToken cancellationToken) =>
        IsOwnedBy(userId, path) ? await SignAsync(_container.GetBlobClient(path), BlobSasPermissions.Read, ReadWindow, cancellationToken) : null;

    /// <summary>
    /// A SAS link to one blob. With the account key (Azurite, a connection string) the client signs it
    /// itself. Under a managed identity there is no key, so the link is signed with a user delegation
    /// key instead, which any identity holding a Storage Blob Data role may request.
    /// </summary>
    private async Task<Uri> SignAsync(BlobClient blob, BlobSasPermissions permissions, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var expiresOn = DateTimeOffset.UtcNow.Add(lifetime);
        if (blob.CanGenerateSasUri) return blob.GenerateSasUri(permissions, expiresOn);

        var key = await DelegationKeyAsync(expiresOn, cancellationToken);
        var sas = new BlobSasBuilder(permissions, expiresOn)
        {
            BlobContainerName = blob.BlobContainerName,
            BlobName = blob.Name,
            Resource = "b",
            StartsOn = DateTimeOffset.UtcNow - ClockSkew
        };
        return new BlobUriBuilder(blob.Uri) { Sas = sas.ToSasQueryParameters(key, _service.AccountName) }.ToUri();
    }

    /// <summary>The cached delegation key while it outlasts the link being signed; otherwise a fresh one. Two callers racing just fetch twice.</summary>
    private async Task<UserDelegationKey> DelegationKeyAsync(DateTimeOffset neededUntil, CancellationToken cancellationToken)
    {
        if (_delegationKey is { } cached && cached.SignedExpiresOn > neededUntil + ClockSkew) return cached;
        var now = DateTimeOffset.UtcNow;
        var key = (await _service.GetUserDelegationKeyAsync(now - ClockSkew, now + DelegationKeyLife, cancellationToken)).Value;
        _delegationKey = key;
        return key;
    }

    public async Task<int> PruneBeforeAsync(string userId, DateOnly firstKeptDay, CancellationToken cancellationToken)
    {
        // Paths are {user}/{yyyyMMdd}/{id}.jpg, so the day folder sorts and compares as text.
        var prefix = Prefix(userId);
        var bound = firstKeptDay.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var removed = 0;
        await foreach (var blob in _container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, cancellationToken))
        {
            var day = blob.Name.AsSpan(prefix.Length);
            if (day.Length < 8 || day[..8].CompareTo(bound, StringComparison.Ordinal) >= 0) continue;
            await _container.DeleteBlobIfExistsAsync(blob.Name, cancellationToken: cancellationToken);
            removed++;
        }
        return removed;
    }

    internal static string Prefix(string userId) => $"{StatsEntityMapper.UserKey(userId)}/";

    internal static bool IsOwnedBy(string userId, string path) =>
        !string.IsNullOrWhiteSpace(path)
        && path.StartsWith(Prefix(userId), StringComparison.Ordinal)
        && !path.Contains("..", StringComparison.Ordinal)
        && path.EndsWith(".jpg", StringComparison.Ordinal);
}
