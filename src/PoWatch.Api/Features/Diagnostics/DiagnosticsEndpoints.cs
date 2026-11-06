using System.Diagnostics;
using PoWatch.Application.Contracts;
using PoWatch.Shared.Models;

namespace PoWatch.Api.Features.Diagnostics;

internal static class DiagnosticsEndpoints
{
    internal static IEndpointRouteBuilder MapDiagnosticsFeature(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/diagnostics").WithTags("Diagnostics").RequireAuthorization();

        group.MapGet("/status", (IDiagnosticsProvider provider, ILogger<Program> logger) =>
        {
            logger.LogDebug(
                "Diagnostics API request received. TraceId={TraceId}",
                Activity.Current?.TraceId.ToString());
            return Results.Ok(provider.CaptureSnapshot());
        })
        .WithName("DiagnosticsStatus")
        .WithSummary("Get the masked system health snapshot for the current environment.")
        .Produces<DiagnosticsSnapshotDto>(StatusCodes.Status200OK);

        // A browser that hit an unhandled error says so (js/error-report.js), so a crash on someone's
        // screen lands in the server log with its stack instead of vanishing with the tab.
        group.MapPost("/client-error", (ClientErrorReport report, ILogger<Program> logger) =>
        {
            logger.LogWarning("Client error at {Url} ({UserAgent}): {Message}{NewLine}{Stack}",
                Clip(report.Url, 300), Clip(report.UserAgent, 200), Clip(report.Message, 1000), Environment.NewLine, Clip(report.Stack, 6000));
            return Results.NoContent();
        })
        .WithName("ClientError")
        .WithSummary("Log an unhandled error a signed-in browser hit.")
        .Produces(StatusCodes.Status204NoContent);

        return app;
    }

    private static string Clip(string? text, int max) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Length <= max ? text : text[..max] + "…";
}

/// <summary>What a browser reports about an unhandled error; every field is clipped before it is logged.</summary>
internal sealed record ClientErrorReport(string? Message, string? Stack, string? Url, string? UserAgent);
