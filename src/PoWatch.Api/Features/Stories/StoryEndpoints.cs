using PoWatch.Api.Security;
using PoWatch.Application.Services;

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

        return app;
    }
}
