using PoWatch.Api.Security;
using PoWatch.Application.Services;
using PoWatch.Domain.Services;

namespace PoWatch.Api.Features.Stories;

internal static class StoryEndpoints
{
    internal static IEndpointRouteBuilder MapStoriesFeature(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/sessions/{id:guid}/story", async (Guid id, HttpContext http, StoryService stories, CancellationToken ct) =>
            {
                if (CurrentUser.Id(http.User) is not { } userId) return Results.Unauthorized();
                var story = await stories.UpdateAsync(userId, id, ct);
                return story is null ? Results.NotFound() : Results.Ok(story);
            })
            .RequireAuthorization()
            .WithTags("Stories")
            .WithName("SessionStory")
            .WithSummary("Writes the session's ten-minute chapters that have closed and, once it has ended, the story they make; returns all of it.");

        app.MapGet("/api/sessions/{id:guid}/transcript", async (Guid id, HttpContext http, StoryService stories, CancellationToken ct) =>
            {
                if (CurrentUser.Id(http.User) is not { } userId) return Results.Unauthorized();
                var lines = await stories.TranscriptAsync(userId, id, ct);
                return lines is null ? Results.NotFound() : Results.Ok(lines);
            })
            .RequireAuthorization()
            .WithTags("Stories")
            .WithName("SessionTranscript")
            .WithSummary("Every line the on-device speech model heard during the session, in order.");

        app.MapGet("/api/stories/{kind}", async (string kind, string? date, string? tz, HttpContext http, PeriodStoryService periods, TimeProvider time, CancellationToken ct) =>
            {
                if (CurrentUser.Id(http.User) is not { } userId) return Results.Unauthorized();
                if (kind is not ("week" or "month")) return Results.BadRequest(new { message = "kind must be week or month." });
                var zone = ApiParsing.Zone(tz);
                DateOnly day;
                if (date is null) day = LocalDay.Today(time, zone);
                else if (!ApiParsing.TryDay(date, out day)) return Results.BadRequest(new { message = "date must be yyyy-MM-dd." });
                return Results.Ok(await periods.TellAsync(userId, kind == "month", day, zone, ct));
            })
            .RequireAuthorization()
            .WithTags("Stories")
            .WithName("PeriodStory")
            .WithSummary("The story of the Monday-to-Sunday week or the calendar month holding a day, told from its days.");

        return app;
    }
}
