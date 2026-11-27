using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using PoWatch.Api.Features.Stats;
using PoWatch.Api.Hosting;
using PoWatch.Api.Security;
using PoWatch.Application.Features.Sessions;
using PoWatch.Domain.Models;
using PoWatch.Shared.Models;

namespace PoWatch.Api.Features.Sessions;

internal static class SessionEndpoints
{
    private const int MaxSummaryBytes = 32_768;

    internal static SessionDto ToDto(this Session session, DateTimeOffset nowUtc) => new()
    {
        Id = session.Id,
        StartedUtc = session.StartedUtc,
        EndedUtc = session.EndedUtc,
        TimeZoneId = session.TimeZoneId,
        IsRunning = session.IsRunning,
        DurationSeconds = session.Duration(nowUtc).TotalSeconds,
        Summary = ParseSummary(session.SummaryJson)
    };

    private static SessionSummaryDto? ParseSummary(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize(json, ApiJson.Default.SessionSummaryDto);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static IEndpointRouteBuilder MapSessionsFeature(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sessions").WithTags("Sessions").RequireAuthorization();

        group.MapPost("/", async (StartSessionRequestDto request, UserId userId, SessionService service, CancellationToken ct) =>
        {
            try
            {
                var session = await service.StartAsync(userId, request.TimeZoneId, request.SessionId, ct);
                return Results.Created($"/api/sessions/{session.Id}", session.ToDto(service.Now));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        })
        .WithName("StartSession")
        .WithSummary("Start a session, or return it if a session with the same id already exists.");

        group.MapPost("/{id:guid}/stop", async (Guid id, UserId userId, SessionService service, CancellationToken ct) =>
        {
            var session = await service.StopAsync(userId, id, ct);
            return session is null ? Results.NotFound() : Results.Ok(session.ToDto(service.Now));
        })
        .WithName("StopSession")
        .WithSummary("Stop a session; stopping twice keeps the first end time.");

        group.MapPut("/{id:guid}/summary", async (Guid id, SessionSummaryDto summary, UserId userId, SessionService service, CancellationToken ct) =>
        {
            var json = JsonSerializer.Serialize(summary, ApiJson.Default.SessionSummaryDto);
            if (json.Length > MaxSummaryBytes) return Results.BadRequest(new { message = "Summary payload is too large." });
            var session = await service.SaveSummaryAsync(userId, id, json, ct);
            return session is null ? Results.NotFound() : Results.Ok(session.ToDto(service.Now));
        })
        .WithName("SaveSessionSummary")
        .WithSummary("Store session-level on-device aggregate summaries (faces, bodies) for Journal analysis.");

        group.MapGet("/", async (int? take, DateTimeOffset? from, DateTimeOffset? to, UserId userId, SessionService service, CancellationToken ct) =>
        {
            var sessions = from is { } start && to is { } end
                ? await service.ListBetweenAsync(userId, start, end, ct)
                : await service.ListAsync(userId, take ?? 20, ct);
            return Results.Ok(sessions.Select(s => s.ToDto(service.Now)).ToList());
        })
        .WithName("ListSessions")
        .WithSummary("The caller's most recent sessions, newest first; with from and to, every session that overlaps that range.");

        group.MapGet("/{id:guid}", async (Guid id, UserId userId, SessionService service, CancellationToken ct) =>
        {
            var session = await service.GetAsync(userId, id, ct);
            return session is null ? Results.NotFound() : Results.Ok(session.ToDto(service.Now));
        })
        .WithName("GetSession");

        return app;
    }
}
