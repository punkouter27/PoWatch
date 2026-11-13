using PoWatch.Api.Security;
using PoWatch.Application.Contracts;
using PoWatch.Application.Features.Weather;
using PoWatch.Domain.Models;
using PoWatch.Domain.Services;
using PoWatch.Shared.Models;

namespace PoWatch.Api.Features.Profile;

/// <summary>The choices the server acts on for the caller: retention, the weather place, the weekly email.</summary>
internal static class ProfileEndpoints
{
    private const int MaxPlaceName = 120;

    internal static IEndpointRouteBuilder MapProfileFeature(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/profile").WithTags("Profile").RequireAuthorization();

        group.MapGet("/", async (UserId userId, IProfileStore profiles, IDigestSender sender, CancellationToken ct) =>
            {
                return Results.Ok(ToDto(await profiles.GetAsync(userId, ct), sender.IsEnabled));
            })
            .WithName("GetProfile");

        group.MapPut("/", async (UpdateProfileDto request, UserId userId, HttpContext http, IProfileStore profiles, IDigestSender sender, CancellationToken ct) =>
            {
                if (request.RetentionDays is { } days && !UserProfile.RetentionChoices.Contains(days))
                    return Results.BadRequest(new { message = $"retentionDays must be one of {string.Join(", ", UserProfile.RetentionChoices)}, or null to keep everything." });
                if (request.Place is { } p && (string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > MaxPlaceName
                    || p.Latitude is < -90 or > 90 || p.Longitude is < -180 or > 180 || !double.IsFinite(p.Latitude) || !double.IsFinite(p.Longitude)))
                    return Results.BadRequest(new { message = "place needs a name and a latitude and longitude on Earth." });

                var email = CurrentUser.Email(http.User);
                if (request.DigestEmail && (!sender.IsEnabled || string.IsNullOrWhiteSpace(email)))
                    return Results.BadRequest(new { message = "This server cannot send email, or the sign-in carries no address." });

                var existing = await profiles.GetAsync(userId, ct);
                var profile = new UserProfile
                {
                    UserId = userId,
                    TimeZoneId = LocalDay.FindZone(request.TimeZoneId) is not null ? request.TimeZoneId : existing?.TimeZoneId,
                    RetentionDays = request.RetentionDays,
                    Place = request.Place is { } place ? new Place(place.Name.Trim(), place.Latitude, place.Longitude, LocalDay.FindZone(place.TimeZoneId) is not null ? place.TimeZoneId : null) : null,
                    DigestEmail = request.DigestEmail,
                    Email = request.DigestEmail ? email : null,
                    LastDigestWeek = existing?.LastDigestWeek
                };
                await profiles.UpsertAsync(profile, ct);
                return Results.Ok(ToDto(profile, sender.IsEnabled));
            })
            .WithName("UpdateProfile")
            .WithSummary("Replace the caller's retention window, weather place and weekly-email choice.");

        return app;
    }

    private static ProfileDto ToDto(UserProfile? profile, bool emailAvailable) => new()
    {
        RetentionDays = profile?.RetentionDays,
        RetentionChoices = [.. UserProfile.RetentionChoices],
        Place = profile?.Place is { } place ? WeatherService.ToDto(place, detail: null) : null,
        DigestEmail = profile?.DigestEmail ?? false,
        Email = profile?.Email,
        DigestEmailAvailable = emailAvailable
    };
}
