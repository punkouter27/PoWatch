using Microsoft.AspNetCore.SignalR;
using PoWatch.Api.Features.Stats;
using PoWatch.Api.Security;
using PoWatch.Application.Features.Sessions;
using PoWatch.Domain.Models;
using PoWatch.Shared.Models;

namespace PoWatch.Api.Features.Sessions;

internal static class SessionEndpoints
{
    internal static SessionDto ToDto(this Session session, DateTimeOffset nowUtc) => new()
    {
        Id = session.Id,
        StartedUtc = session.StartedUtc,
        EndedUtc = session.EndedUtc,
        TimeZoneId = session.TimeZoneId,
        IsRunning = session.IsRunning,
        DurationSeconds = session.Duration(nowUtc).TotalSeconds
    };

    internal static IEndpointRouteBuilder MapSessionsFeature(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sessions").WithTags("Sessions").RequireAuthorization();

        group.MapPost("/", async (StartSessionRequestDto request, HttpContext http, SessionService service, CancellationToken ct) =>
        {
            if (CurrentUser.Id(http.User) is not { } userId) return Results.Unauthorized();
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

        group.MapPost("/{id:guid}/stop", async (Guid id, HttpContext http, SessionService service, CancellationToken ct) =>
        {
            if (CurrentUser.Id(http.User) is not { } userId) return Results.Unauthorized();
            var session = await service.StopAsync(userId, id, ct);
            return session is null ? Results.NotFound() : Results.Ok(session.ToDto(service.Now));
        })
        .WithName("StopSession")
        .WithSummary("Stop a session; stopping twice keeps the first end time.");

        group.MapGet("/", async (int? take, DateTimeOffset? from, DateTimeOffset? to, HttpContext http, SessionService service, CancellationToken ct) =>
        {
            if (CurrentUser.Id(http.User) is not { } userId) return Results.Unauthorized();
            var sessions = from is { } start && to is { } end
                ? await service.ListBetweenAsync(userId, start, end, ct)
                : await service.ListAsync(userId, take ?? 20, ct);
            return Results.Ok(sessions.Select(s => s.ToDto(service.Now)).ToList());
        })
        .WithName("ListSessions")
        .WithSummary("The caller's most recent sessions, newest first; with from and to, every session that overlaps that range.");

        group.MapGet("/{id:guid}", async (Guid id, HttpContext http, SessionService service, CancellationToken ct) =>
        {
            if (CurrentUser.Id(http.User) is not { } userId) return Results.Unauthorized();
            var session = await service.GetAsync(userId, id, ct);
            return session is null ? Results.NotFound() : Results.Ok(session.ToDto(service.Now));
        })
        .WithName("GetSession");

        return app;
    }
}
