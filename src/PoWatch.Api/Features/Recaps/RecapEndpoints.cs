using PoWatch.Api.Hosting;
using PoWatch.Api.Security;
using PoWatch.Application.Contracts;
using PoWatch.Application.Services;
using PoWatch.Shared.Models;

namespace PoWatch.Api.Features.Recaps;

internal static class RecapEndpoints
{
    internal static IEndpointRouteBuilder MapRecapsFeature(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/recaps").WithTags("Recaps").RequireAuthorization();

        group.MapGet("/session/{id:guid}.pdf", async (Guid id, HttpContext http, RecapService recaps, ISessionRepository sessions, ILogger<Program> logger, CancellationToken ct) =>
            {
                if (CurrentUser.Id(http.User) is not { } userId) return Results.Unauthorized();
                if (await sessions.GetAsync(userId, id, ct) is not { } session) return Results.NotFound();
                var recap = await recaps.ForSessionAsync(userId, id, ct);
                return recap is null ? Results.NotFound() : Pdf(recap, session.TimeZone, $"PoWatch-session-{SessionDto.Tag(id)[1..]}.pdf", logger);
            })
            .WithName("SessionRecapPdf")
            .Produces(StatusCodes.Status200OK, contentType: "application/pdf")
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/day/{date}", async (string date, string? tz, bool? ai, HttpContext http, RecapService recaps, CancellationToken ct) =>
            {
                if (CurrentUser.Id(http.User) is not { } userId) return Results.Unauthorized();
                if (!ApiParsing.TryDay(date, out var day)) return Results.BadRequest(new { message = "date must be yyyy-MM-dd." });
                // ai=false answers straight from the template, so a page can paint before the model replies.
                var recap = await recaps.ForDayAsync(userId, day, tz, ct, useAi: ai ?? true);
                return recap is null ? Results.NotFound() : Results.Ok(recap);
            })
            .WithName("DayRecap")
            .WithSummary("A readable recap of one local day; ai=false skips the model rewrite.");

        group.MapGet("/day/{date}.pdf", async (string date, string? tz, HttpContext http, RecapService recaps, ILogger<Program> logger, CancellationToken ct) =>
            {
                if (CurrentUser.Id(http.User) is not { } userId) return Results.Unauthorized();
                if (!ApiParsing.TryDay(date, out var day)) return Results.BadRequest(new { message = "date must be yyyy-MM-dd." });
                var recap = await recaps.ForDayAsync(userId, day, tz, ct);
                return recap is null ? Results.NotFound() : Pdf(recap, ApiParsing.Zone(tz), $"PoWatch-{day:yyyy-MM-dd}.pdf", logger);
            })
            .WithName("DayRecapPdf")
            .Produces(StatusCodes.Status200OK, contentType: "application/pdf")
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    private static IResult Pdf(RecapDto recap, TimeZoneInfo zone, string fileName, ILogger logger)
    {
        try
        {
            return Results.File(RecapReportRenderer.Render(recap, zone), "application/pdf", fileName);
        }
        catch (RecapReportRenderer.RendererUnavailableException ex)
        {
            logger.LogError(ex, "Recap PDF renderer unavailable.");
            return Results.Problem(title: "The PDF could not be generated on this server.", detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
