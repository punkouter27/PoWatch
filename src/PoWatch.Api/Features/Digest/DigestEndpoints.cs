using PoWatch.Api.Security;
using PoWatch.Application.Features.Digest;
using PoWatch.Domain.Services;

namespace PoWatch.Api.Features.Digest;

internal static class DigestEndpoints
{
    internal static IEndpointRouteBuilder MapDigestFeature(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/digest/week", async (string? date, string? tz, UserId userId, DigestService digests, TimeProvider time, CancellationToken ct) =>
            {
                var zone = ApiParsing.Zone(tz);
                DateOnly day;
                if (date is null) day = LocalDay.Today(time, zone);
                else if (!ApiParsing.TryDay(date, out day)) return Results.BadRequest(new { message = "date must be yyyy-MM-dd." });
                return Results.Ok(await digests.WeekAsync(userId, day, zone, ct));
            })
            .WithTags("Digest")
            .RequireAuthorization()
            .WithName("WeekDigest")
            .WithSummary("The Monday-to-Sunday week holding a day (default today) against the week before.");

        return app;
    }
}
