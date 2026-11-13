using Microsoft.AspNetCore.SignalR;
using PoWatch.Api.Features.Stats;
using PoWatch.Api.Security;
using PoWatch.Application.Features.Ask;
using PoWatch.Shared.Models;

namespace PoWatch.Api.Features.Ask;

internal static class AskEndpoints
{
    public const string AlertMethod = "alert";

    internal static IEndpointRouteBuilder MapAskFeature(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/ask", async (AskRequestDto request, UserId userId, AskService ask, ILogger<AskService> logger, CancellationToken ct) =>
            {
                var question = request.Question?.Trim();
                if (string.IsNullOrEmpty(question) || question.Length > AskService.MaxQuestionLength)
                    return Results.BadRequest(new { message = $"question is required, at most {AskService.MaxQuestionLength} characters." });

                try
                {
                    return await ask.AskAsync(userId, question, request.TimeZoneId, ct) is { } answer
                        ? Results.Ok(new AskAnswerDto { Answer = answer })
                        : Results.Problem("No AI provider is configured on this server.", statusCode: StatusCodes.Status503ServiceUnavailable);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // Timeouts and provider errors: the question simply goes unanswered.
                    logger.LogWarning(ex, "Ask failed.");
                    return Results.Problem("The model did not answer.", statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            })
            .WithTags("Ask")
            .RequireAuthorization()
            .WithName("Ask")
            .WithSummary("Answer a plain-language question from the caller's own statistics.");

        app.MapPost("/api/ask/rule", async (RuleDraftRequestDto request, UserId _, AssistService assist, CancellationToken ct) =>
            {
                var text = request.Text?.Trim();
                if (string.IsNullOrEmpty(text) || text.Length > RuleDraftRequestDto.MaxLength)
                    return Results.BadRequest(new { message = $"text is required, at most {RuleDraftRequestDto.MaxLength} characters." });
                if (!assist.IsAvailable)
                    return Results.Problem("No AI provider is configured on this server.", statusCode: StatusCodes.Status503ServiceUnavailable);

                return await assist.DraftRuleAsync(text, ct) is { } draft
                    ? Results.Ok(draft)
                    : Results.UnprocessableEntity(new { message = "That did not read as a watch rule. Try: \"a person between 22 and 6\"." });
            })
            .WithTags("Ask")
            .RequireAuthorization()
            .WithName("DraftWatchRule")
            .WithSummary("Draft a watch rule from a sentence; the caller confirms it before keeping it.");

        app.MapPost("/api/alerts", async (AlertDto alert, UserId userId, IHubContext<StatsHub> hub, TimeProvider time, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(alert.Text) || alert.Text.Length > AlertDto.MaxLength)
                    return Results.BadRequest(new { message = $"text is required, at most {AlertDto.MaxLength} characters." });

                await hub.Clients.User(userId).SendAsync(AlertMethod, new AlertDto { AtUtc = time.GetUtcNow(), Text = alert.Text.Trim() }, ct);
                return Results.Accepted();
            })
            .WithTags("Alerts")
            .RequireAuthorization()
            .WithName("RaiseAlert")
            .WithSummary("Relay a watch-rule alert from the sensing tab to every open tab of the same user.");

        return app;
    }
}
