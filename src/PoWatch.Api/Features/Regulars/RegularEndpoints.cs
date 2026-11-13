using PoWatch.Api.Features.Stats;
using PoWatch.Api.Security;
using PoWatch.Application.Features.Regulars;
using PoWatch.Domain.Models;
using PoWatch.Domain.Services;
using PoWatch.Shared.Models;

namespace PoWatch.Api.Features.Regulars;

internal static class RegularEndpoints
{
    internal static RegularDto ToDto(this Regular regular) => new()
    {
        Id = regular.Id,
        Class = regular.Class,
        DisplayName = regular.DisplayName,
        IsNamed = regular.IsNamed,
        FirstSeenUtc = regular.FirstSeenUtc,
        LastSeenUtc = regular.LastSeenUtc,
        Visits = regular.Visits,
        DwellSeconds = regular.DwellSeconds
    };

    internal static IEndpointRouteBuilder MapRegularsFeature(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/regulars").WithTags("Regulars").RequireAuthorization();

        group.MapGet("/", async (UserId userId, RegularsService service, CancellationToken ct) =>
            {
                var regulars = await service.ListAsync(userId, ct);
                return Results.Ok(regulars.OrderByDescending(r => r.Visits).ThenByDescending(r => r.LastSeenUtc).Select(r => r.ToDto()).ToList());
            })
            .WithName("ListRegulars");

        group.MapPost("/observe", async (ObserveRegularRequestDto request, UserId userId, RegularsService service, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(request.Class) || request.Class.Length > 40
                    || request.Signature.Count != RegularMatcher.SignatureBins
                    || request.Signature.Any(v => v < 0 || !float.IsFinite(v)))
                    return Results.BadRequest(new { message = $"class and a {RegularMatcher.SignatureBins}-value non-negative signature are required." });

                var observation = await service.ObserveAsync(userId, request.Class.Trim(), request.Signature, ct);
                return Results.Ok(new ObserveRegularResultDto { Regular = observation.Regular.ToDto(), IsNew = observation.IsNew });
            })
            .WithName("ObserveRegular")
            .WithSummary("Recognise a tracked person, pet or object by its look, or start a new unnamed regular.");

        group.MapPatch("/{id}", async (string id, RenameRegularRequestDto request, UserId userId, RegularsService service, CancellationToken ct) =>
            {
                var renamed = await service.RenameAsync(userId, id, request.Name, ct);
                return renamed is null ? Results.NotFound() : Results.Ok(renamed.ToDto());
            })
            .WithName("RenameRegular")
            .InvalidatesStats();

        group.MapPost("/merge", async (MergeRegularsRequestDto request, UserId userId, RegularsService service, CancellationToken ct) =>
            {
                var merged = await service.MergeAsync(userId, request.PrimaryId, request.DuplicateId, ct);
                return merged is null ? Results.NotFound() : Results.Ok(merged.ToDto());
            })
            .WithName("MergeRegulars")
            .WithSummary("Fold a duplicate regular into another; their visits and time add up.")
            .InvalidatesStats();

        return app;
    }
}
