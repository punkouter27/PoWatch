using FluentValidation;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Hybrid;
using PoWatch.Api.Features.Stats;
using PoWatch.Api.Security;
using PoWatch.Application.Features.Ingest;
using PoWatch.Domain.Models;
using PoWatch.Shared.Models;

namespace PoWatch.Api.Features.Ingest;

internal static class IngestEndpoints
{
    internal static IEndpointRouteBuilder MapIngestFeature(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/sessions/{id:guid}/batches", async (
                Guid id,
                IngestBatchDto batch,
                UserId userId,
                IValidator<IngestBatchDto> validator,
                IngestService service,
                HybridCache cache,
                IHubContext<StatsHub> hub,
                CancellationToken ct) =>
            {

                var shape = await validator.ValidateAsync(batch, ct);
                if (!shape.IsValid)
                    return Results.BadRequest(Rejected(shape.Errors.Select(e => e.ErrorMessage)));

                var outcome = await service.IngestAsync(
                    userId,
                    id,
                    batch.BatchKey,
                    batch.Ticks.Select(t => t.ToDomain(id)).ToList(),
                    batch.Events.Select(e => e.ToDomain(id)).ToList(),
                    ct);

                if (!outcome.SessionFound) return Results.NotFound();
                if (!outcome.Accepted) return Results.BadRequest(Rejected(outcome.Errors));
                if (!outcome.Replayed)
                {
                    await cache.RemoveByTagAsync(StatsEndpoints.CacheTag(userId), ct);
                    await hub.NotifyStatsChangedAsync(userId, new StatsChangedDto
                    {
                        AtUtc = DateTimeOffset.UtcNow,
                        SessionId = id,
                        Ticks = batch.Ticks.Count,
                        Events = batch.Events.Count
                    }, ct);
                }

                return Results.Ok(new IngestBatchResultDto
                {
                    Accepted = true,
                    Replayed = outcome.Replayed,
                    Ticks = batch.Ticks.Count,
                    Events = batch.Events.Count
                });
            })
            .WithTags("Ingest")
            .RequireAuthorization()
            .WithName("IngestBatch")
            .WithSummary("Store a batch of ticks and scene events; a replayed batch key is stored but not re-counted.");

        return app;
    }

    private static IngestBatchResultDto Rejected(IEnumerable<string> errors) => new() { Accepted = false, Errors = errors.ToList() };

    internal static Tick ToDomain(this TickDto dto, Guid sessionId) => new()
    {
        SessionId = sessionId,
        StartUtc = dto.StartUtc,
        DurationSeconds = dto.DurationSeconds,
        PixelSamples = dto.PixelSamples,
        DetectorSamples = dto.DetectorSamples,
        MotionMean = dto.MotionMean,
        MotionMax = dto.MotionMax,
        LuminanceMean = dto.LuminanceMean,
        SoundMean = dto.SoundMean,
        SoundMax = dto.SoundMax,
        SittingSeconds = dto.SittingSeconds,
        StandingSeconds = dto.StandingSeconds,
        LyingSeconds = dto.LyingSeconds,
        Palette = dto.Palette,
        MotionGrid = dto.MotionGrid,
        PresenceGrid = dto.PresenceGrid,
        Classes = dto.Classes.ToDictionary(kv => kv.Key, kv => new ClassCount(kv.Value.Max, kv.Value.Mean), StringComparer.Ordinal),
        ActiveTrackIds = dto.ActiveTrackIds
    };

    internal static SceneEvent ToDomain(this SceneEventDto dto, Guid sessionId) => new()
    {
        SessionId = sessionId,
        AtUtc = dto.AtUtc,
        Kind = Enum.Parse<SceneEventKind>(dto.Kind, ignoreCase: true),
        TrackId = dto.TrackId,
        Class = dto.Class,
        Edge = string.IsNullOrWhiteSpace(dto.Edge) ? FrameEdge.None : Enum.Parse<FrameEdge>(dto.Edge, ignoreCase: true),
        Text = dto.Text,
        Score = dto.Score,
        DwellSeconds = dto.DwellSeconds,
        ImagePath = dto.ImagePath,
        RegularId = dto.RegularId
    };
}

/// <summary>Shape checks that must pass before the batch can be mapped; domain invariants are checked after.</summary>
internal sealed class IngestBatchValidator : AbstractValidator<IngestBatchDto>
{
    public IngestBatchValidator()
    {
        RuleFor(b => b.BatchKey).NotEmpty();
        RuleFor(b => b.Ticks).Must(t => t.Count <= IngestBatchDto.MaxTicks)
            .WithMessage($"A batch holds at most {IngestBatchDto.MaxTicks} ticks.");
        RuleFor(b => b.Events).Must(e => e.Count <= IngestBatchDto.MaxEvents)
            .WithMessage($"A batch holds at most {IngestBatchDto.MaxEvents} events.");
        RuleForEach(b => b.Events).ChildRules(e =>
        {
            e.RuleFor(x => x.Kind)
                .Must(k => Enum.TryParse<SceneEventKind>(k, ignoreCase: true, out var kind) && Enum.IsDefined(kind))
                .WithMessage(x => $"Unknown event kind '{x.Kind}'.");
            // Caption and moment text later reaches the recap prompt; nothing sensing produces is longer.
            e.RuleFor(x => x.Text).MaximumLength(SceneEventDto.MaxTextLength);
            e.RuleFor(x => x.Class).MaximumLength(SceneEventDto.MaxNameLength);
            e.RuleFor(x => x.TrackId).MaximumLength(SceneEventDto.MaxNameLength);
            e.RuleFor(x => x.RegularId).MaximumLength(SceneEventDto.MaxNameLength);
            e.RuleFor(x => x.ImagePath).MaximumLength(SceneEventDto.MaxTextLength);
            e.RuleFor(x => x.Edge)
                .Must(edge => string.IsNullOrWhiteSpace(edge) || (Enum.TryParse<FrameEdge>(edge, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)))
                .WithMessage(x => $"Unknown frame edge '{x.Edge}'.");
        });
    }
}
