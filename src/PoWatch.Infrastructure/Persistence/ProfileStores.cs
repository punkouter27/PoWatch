using System.Globalization;
using Azure.Data.Tables;
using Microsoft.Extensions.Options;
using PoWatch.Application.Contracts;
using PoWatch.Application.Options;
using PoWatch.Domain.Models;

namespace PoWatch.Infrastructure.Persistence;

/// <summary>One row per user (partition = user key, row = "profile"), and beside it the Live layout (row = "live-layout").</summary>
public sealed class AzureProfileStore(AzureStorageClients clients, IOptions<AzureStorageOptions> options) : IProfileStore
{
    private const string Row = "profile";
    private const string LayoutRow = "live-layout";
    private readonly TableClient _table = clients.TableService.GetTableClient(options.Value.ProfilesTable);

    public async Task<UserProfile?> GetAsync(string userId, CancellationToken cancellationToken)
    {
        var response = await _table.GetEntityIfExistsAsync<TableEntity>(StatsEntityMapper.UserKey(userId), Row, cancellationToken: cancellationToken);
        return response.HasValue ? ToProfile(response.Value!) : null;
    }

    public async Task UpsertAsync(UserProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var entity = new TableEntity(StatsEntityMapper.UserKey(profile.UserId), Row)
        {
            ["UserId"] = profile.UserId,
            ["TimeZoneId"] = profile.TimeZoneId,
            ["RetentionDays"] = profile.RetentionDays,
            ["DigestEmail"] = profile.DigestEmail,
            ["Email"] = profile.Email,
            ["LastDigestWeek"] = profile.LastDigestWeek?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        };
        if (profile.Place is { } place)
        {
            entity["PlaceName"] = place.Name;
            entity["PlaceLatitude"] = place.Latitude;
            entity["PlaceLongitude"] = place.Longitude;
            entity["PlaceTimeZoneId"] = place.TimeZoneId;
        }
        await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken);
    }

    public async IAsyncEnumerable<UserProfile> ListAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var entity in _table.QueryAsync<TableEntity>(e => e.RowKey == Row, cancellationToken: cancellationToken))
            yield return ToProfile(entity);
    }

    public async Task<LiveLayout?> GetLayoutAsync(string userId, CancellationToken cancellationToken)
    {
        var response = await _table.GetEntityIfExistsAsync<TableEntity>(StatsEntityMapper.UserKey(userId), LayoutRow, cancellationToken: cancellationToken);
        return response.HasValue
            ? new LiveLayout(Split(response.Value!.GetString("Order")), Split(response.Value!.GetString("Hidden")))
            : null;
    }

    public async Task SaveLayoutAsync(string userId, LiveLayout layout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        await _table.UpsertEntityAsync(new TableEntity(StatsEntityMapper.UserKey(userId), LayoutRow)
        {
            ["Order"] = string.Join(',', layout.Order),
            ["Hidden"] = string.Join(',', layout.Hidden)
        }, TableUpdateMode.Replace, cancellationToken);
    }

    private static string[] Split(string? ids) => (ids ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static UserProfile ToProfile(TableEntity e) => new()
    {
        UserId = e.GetString("UserId") ?? string.Empty,
        TimeZoneId = e.GetString("TimeZoneId"),
        RetentionDays = e.GetInt32("RetentionDays"),
        DigestEmail = e.GetBoolean("DigestEmail") ?? false,
        Email = e.GetString("Email"),
        LastDigestWeek = DateOnly.TryParseExact(e.GetString("LastDigestWeek"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var week) ? week : null,
        Place = e.GetString("PlaceName") is { } name && e.GetDouble("PlaceLatitude") is { } lat && e.GetDouble("PlaceLongitude") is { } lon
            ? new Place(name, lat, lon, e.GetString("PlaceTimeZoneId"))
            : null
    };
}

/// <summary>One row per shared link (partition = token), so a public read is a single point lookup.</summary>
public sealed class AzureShareStore(AzureStorageClients clients, IOptions<AzureStorageOptions> options) : IShareStore
{
    private const string Row = "share";
    private readonly TableClient _table = clients.TableService.GetTableClient(options.Value.SharesTable);

    public async Task AddAsync(RecapShare share, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(share);
        await _table.AddEntityAsync(new TableEntity(share.Token, Row)
        {
            ["UserId"] = share.UserId,
            ["Day"] = share.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["CreatedUtc"] = share.CreatedUtc,
            ["ExpiresUtc"] = share.ExpiresUtc,
            ["Content"] = share.Content
        }, cancellationToken);
    }

    public async Task<RecapShare?> GetAsync(string token, CancellationToken cancellationToken)
    {
        var response = await _table.GetEntityIfExistsAsync<TableEntity>(token, Row, cancellationToken: cancellationToken);
        return response.HasValue ? ToShare(response.Value!) : null;
    }

    public async Task<IReadOnlyList<RecapShare>> ListAsync(string userId, CancellationToken cancellationToken)
    {
        var shares = new List<RecapShare>();
        await foreach (var entity in _table.QueryAsync<TableEntity>(TableClient.CreateQueryFilter($"UserId eq {userId}"), cancellationToken: cancellationToken))
            shares.Add(ToShare(entity));
        return shares;
    }

    public async Task<bool> DeleteAsync(string userId, string token, CancellationToken cancellationToken)
    {
        if (await GetAsync(token, cancellationToken) is not { } share || share.UserId != userId) return false;
        await _table.DeleteEntityAsync(token, Row, cancellationToken: cancellationToken);
        return true;
    }

    private static RecapShare ToShare(TableEntity e) => new()
    {
        Token = e.PartitionKey,
        UserId = e.GetString("UserId") ?? string.Empty,
        Day = DateOnly.ParseExact(e.GetString("Day") ?? "0001-01-01", "yyyy-MM-dd", CultureInfo.InvariantCulture),
        CreatedUtc = e.GetDateTimeOffset("CreatedUtc") ?? DateTimeOffset.MinValue,
        ExpiresUtc = e.GetDateTimeOffset("ExpiresUtc") ?? DateTimeOffset.MinValue,
        Content = e.GetString("Content") ?? "{}"
    };
}
