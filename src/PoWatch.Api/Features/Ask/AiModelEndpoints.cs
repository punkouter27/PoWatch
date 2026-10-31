using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.AI;
using PoWatch.Application.Contracts;
using PoWatch.Shared.Models;

namespace PoWatch.Api.Features.Ask;

internal static class AiModelEndpoints
{
    private const string CookieName = "powatch.ai-model";

    internal static IServiceCollection AddBrowserAiSelection(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        // Each request gets its own selection (not a global switch): the chosen model, then the next
        // configured one should it fail. The list is lazy, so the fallback is resolved only when needed.
        services.AddScoped<IEnumerable<IChatClient>>(sp =>
        {
            var models = sp.GetRequiredService<IAiModelRegistry>();
            var http = sp.GetRequiredService<IHttpContextAccessor>().HttpContext;
            return models.Chain(SelectedId(http, models));
        });
        return services;
    }

    internal static IEndpointRouteBuilder MapAiModelsFeature(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/ai/models", (HttpContext http, IAiModelRegistry models) =>
                TypedResults.Ok(new AiModelsDto(SelectedId(http, models), models.Describe())))
            .RequireAuthorization()
            .WithTags("AI")
            .WithName("GetAiModels")
            .WithSummary("List configured server models and this browser's current selection.");

        app.MapPut("/api/ai/models", Results<Ok<AiModelsDto>, ProblemHttpResult> (
                SelectAiModelDto request, HttpContext http, IAiModelRegistry models) =>
            {
                if (!models.Models.Any(m => m.Id == request.ModelId))
                    return TypedResults.Problem("Choose one of the models offered by this server.", statusCode: StatusCodes.Status400BadRequest);

                http.Response.Cookies.Append(CookieName, request.ModelId, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = http.Request.IsHttps,
                    SameSite = SameSiteMode.Strict,
                    Path = "/",
                    MaxAge = TimeSpan.FromDays(365),
                    IsEssential = true
                });
                return TypedResults.Ok(new AiModelsDto(request.ModelId, models.Describe()));
            })
            .RequireAuthorization()
            .WithTags("AI")
            .WithName("SelectAiModel")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithSummary("Select a configured model for this browser's next Ask, recap or assist request.");
        return app;
    }

    private static string SelectedId(HttpContext? http, IAiModelRegistry models) =>
        http?.Request.Cookies[CookieName] is { } id && models.Models.Any(m => m.Id == id) ? id : models.DefaultId;
}
