using Microsoft.Extensions.Logging;
using PoWatch.Application.Contracts;
using PoWatch.Domain.Services;

namespace PoWatch.Application.Features.Digest;

/// <summary>
/// Mails last week's digest to everyone who turned the weekly email on: once a week, from Monday
/// 08:00 in their own zone. A late pass (the server was asleep on Monday) still sends that week's
/// digest; the profile remembers the week, so nobody gets it twice.
/// </summary>
public sealed class DigestMailer(IProfileStore profiles, DigestService digests, IDigestSender sender, TimeProvider time, ILogger<DigestMailer> logger)
{
    public const int SendFromHour = 8;

    /// <summary>Sends every digest that is due; returns how many went out.</summary>
    public async Task<int> SendDueAsync(Uri? appUrl, CancellationToken cancellationToken)
    {
        if (!sender.IsEnabled) return 0;

        var sent = 0;
        await foreach (var profile in profiles.ListAsync(cancellationToken))
        {
            if (!profile.DigestEmail || string.IsNullOrWhiteSpace(profile.Email)) continue;

            var zone = LocalDay.FindZone(profile.TimeZoneId) ?? TimeZoneInfo.Utc;
            var local = TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone);
            var thisWeek = LocalDay.WeekStart(DateOnly.FromDateTime(local.DateTime));
            var lastWeek = thisWeek.AddDays(-7);
            var due = local.DateTime >= thisWeek.ToDateTime(new TimeOnly(SendFromHour, 0));
            if (!due || profile.LastDigestWeek >= lastWeek) continue;

            try
            {
                var digest = await digests.WeekAsync(profile.UserId, lastWeek, zone, cancellationToken);
                // A week with nothing in it, after a week with nothing in it, is not worth an email.
                if (digest.ThisWeek.SecondsObserved > 0 || digest.LastWeek.SecondsObserved > 0)
                {
                    var (subject, html, text) = DigestService.Email(digest, appUrl);
                    await sender.SendAsync(profile.Email, subject, html, text, cancellationToken);
                    sent++;
                }
                await profiles.UpsertAsync(profile with { LastDigestWeek = lastWeek }, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The weekly digest could not be sent to one user; the next pass tries again.");
            }
        }
        return sent;
    }
}
