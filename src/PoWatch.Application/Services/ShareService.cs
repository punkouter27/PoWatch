using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using PoWatch.Application.Contracts;
using PoWatch.Domain.Models;
using PoWatch.Shared.Models;

namespace PoWatch.Application.Services;

/// <summary>
/// Shared recap links. Sharing freezes the day's recap as it reads now, without its moments (their
/// snapshot links are private); anyone with the token can read that copy until it expires or is revoked.
/// </summary>
public sealed class ShareService(IShareStore shares, RecapService recaps, StatsQueryService stats, TimeProvider time)
{
    private const int TokenBytes = 16;

    /// <summary>A day's recap, frozen and shared for <paramref name="days"/> days; null when nothing was observed that day.</summary>
    public async Task<RecapShare?> CreateAsync(string userId, DateOnly day, string? timeZoneId, int days, CancellationToken cancellationToken)
    {
        if (!ShareDto.LifetimeChoices.Contains(days)) throw new ArgumentOutOfRangeException(nameof(days), days, "Not an offered lifetime.");

        var window = await stats.ResolveAsync(userId, StatsRange.Day, timeZoneId, null, cancellationToken, day);
        if (window is null || (await stats.PresenceAsync(userId, window, cancellationToken)).SecondsObserved <= 0) return null;
        if (await recaps.ForDayAsync(userId, day, timeZoneId, cancellationToken) is not { } recap) return null;

        var now = time.GetUtcNow();
        var share = new RecapShare
        {
            Token = NewToken(),
            UserId = userId,
            Day = day,
            CreatedUtc = now,
            ExpiresUtc = now.AddDays(days),
            Content = JsonSerializer.Serialize(recap with { Moments = [] }, ShareJson.Default.RecapDto)
        };
        await shares.AddAsync(share, cancellationToken);
        return share;
    }

    /// <summary>The shared recap behind a token and when it stops working; null when the token is malformed, unknown, expired or revoked.</summary>
    public async Task<(RecapDto Recap, DateTimeOffset ExpiresUtc)?> ReadAsync(string token, CancellationToken cancellationToken)
    {
        if (!IsToken(token) || await shares.GetAsync(token, cancellationToken) is not { } share || !share.IsLive(time.GetUtcNow())) return null;
        return JsonSerializer.Deserialize(share.Content, ShareJson.Default.RecapDto) is { } recap ? (recap, share.ExpiresUtc) : null;
    }

    /// <summary>The caller's links that still work, newest first.</summary>
    public async Task<IReadOnlyList<ShareDto>> ListAsync(string userId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        return (await shares.ListAsync(userId, cancellationToken))
            .Where(s => s.IsLive(now))
            .OrderByDescending(s => s.CreatedUtc)
            .Select(ToDto)
            .ToList();
    }

    public Task<bool> RevokeAsync(string userId, string token, CancellationToken cancellationToken) =>
        IsToken(token) ? shares.DeleteAsync(userId, token, cancellationToken) : Task.FromResult(false);

    public static ShareDto ToDto(RecapShare share)
    {
        ArgumentNullException.ThrowIfNull(share);
        return new() { Token = share.Token, Path = $"s/{share.Token}", Day = share.Day, CreatedUtc = share.CreatedUtc, ExpiresUtc = share.ExpiresUtc };
    }

    /// <summary>128 random bits, base64url: 22 characters, unguessable, safe in a URL and a table key.</summary>
    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));

    /// <summary>Exactly the shape <see cref="NewToken"/> makes; anything else never reaches storage.</summary>
    public static bool IsToken(string? token) =>
        token is { Length: 22 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
