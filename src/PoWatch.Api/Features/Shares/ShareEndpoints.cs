using Microsoft.Extensions.Caching.Hybrid;
using PoWatch.Api.Features.Recaps;
using PoWatch.Api.Security;
using PoWatch.Application.Features.Shares;
using PoWatch.Shared.Models;

namespace PoWatch.Api.Features.Shares;

/// <summary>
/// Shared recap links: the owner makes, lists and revokes them under /api/shares; anyone holding a
/// link reads /s/{token} (a page) and /s/{token}/card.png (its preview image) without signing in.
/// </summary>
internal static class ShareEndpoints
{
    private static readonly HybridCacheEntryOptions CardEntry = new() { Expiration = TimeSpan.FromHours(1), LocalCacheExpiration = TimeSpan.FromHours(1) };

    private static string CardKey(string token) => $"share-card:{token}";

    internal static IEndpointRouteBuilder MapSharesFeature(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/shares").WithTags("Shares").RequireAuthorization();

        group.MapPost("/", async (CreateShareDto request, UserId userId, ShareService shares, TimeProvider time, CancellationToken ct) =>
            {
                if (!ShareDto.LifetimeChoices.Contains(request.Days))
                    return Results.BadRequest(new { message = $"days must be one of {string.Join(", ", ShareDto.LifetimeChoices)}." });
                if (request.Day > DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime.AddDays(1)))
                    return Results.BadRequest(new { message = "That day has not happened yet." });

                var share = await shares.CreateAsync(userId, request.Day, request.TimeZoneId, request.Days, ct);
                return share is null
                    ? Results.NotFound(new { message = "Nothing was observed that day, so there is no recap to share." })
                    : Results.Created($"/s/{share.Token}", ShareService.ToDto(share));
            })
            .WithName("CreateShare")
            .WithSummary("Freeze a day's recap behind a link that works without signing in, for 1, 7 or 30 days.");

        group.MapGet("/", async (UserId userId, ShareService shares, CancellationToken ct) =>
            {
                return Results.Ok(await shares.ListAsync(userId, ct));
            })
            .WithName("ListShares");

        group.MapDelete("/{token}", async (string token, UserId userId, ShareService shares, HybridCache cache, CancellationToken ct) =>
            {
                if (!await shares.RevokeAsync(userId, token, ct)) return Results.NotFound();
                await cache.RemoveAsync(CardKey(token), CancellationToken.None);
                return Results.NoContent();
            })
            .WithName("RevokeShare");

        // Public: these never read the caller's identity, only the frozen copy behind the token.
        app.MapGet("/s/{token}", async (string token, HttpContext http, ShareService shares, CancellationToken ct) =>
            {
                Harden(http);
                if (await shares.ReadAsync(token, ct) is not { } shared)
                    return Results.Content(SharePage.NotFound(), "text/html; charset=utf-8", statusCode: StatusCodes.Status404NotFound);

                var card = new Uri($"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}/s/{token}/card.png");
                return Results.Content(SharePage.Render(shared.Recap, card, shared.ExpiresUtc), "text/html; charset=utf-8");
            })
            .AllowAnonymous()
            .ExcludeFromDescription();

        app.MapGet("/s/{token}/card.png", async (string token, HttpContext http, ShareService shares, HybridCache cache, ILogger<Program> logger, CancellationToken ct) =>
            {
                Harden(http);
                if (await shares.ReadAsync(token, ct) is not { } shared) return Results.NotFound();
                try
                {
                    var png = await cache.GetOrCreateAsync(CardKey(token), _ => ValueTask.FromResult(RecapReportRenderer.RenderCard(shared.Recap)), CardEntry, cancellationToken: ct);
                    return Results.File(png, "image/png");
                }
                catch (RecapReportRenderer.RendererUnavailableException ex)
                {
                    logger.LogError(ex, "Share card renderer unavailable.");
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
            })
            .AllowAnonymous()
            .ExcludeFromDescription();

        return app;
    }

    /// <summary>A revoked link must stop working at once, so nothing caches these; and nothing on them runs or leaks.</summary>
    private static void Harden(HttpContext http)
    {
        var headers = http.Response.Headers;
        headers.CacheControl = "no-store";
        headers.ContentSecurityPolicy = SharePage.ContentSecurityPolicy;
        headers["Referrer-Policy"] = "no-referrer";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Robots-Tag"] = "noindex, nofollow";
    }
}
