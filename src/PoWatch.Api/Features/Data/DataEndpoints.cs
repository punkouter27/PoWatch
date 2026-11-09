using System.Globalization;
using System.Text;
using PoWatch.Api.Features.Stats;
using PoWatch.Api.Security;
using PoWatch.Application.Contracts;
using PoWatch.Application.Features.Stats;

namespace PoWatch.Api.Features.Data;

/// <summary>The caller's own data: take a copy, or delete all of it.</summary>
internal static class DataEndpoints
{
    internal static IEndpointRouteBuilder MapDataFeature(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/export.csv", async (string? tz, DateOnly? from, DateOnly? to, HttpContext http, StatsQueryService stats, CancellationToken ct) =>
            {
                if (CurrentUser.Id(http.User) is not { } userId) return Results.Unauthorized();
                var csv = new StringBuilder("date,occupancy,visits,motion\n");
                if (await stats.ResolveAsync(userId, StatsRange.Today, tz, null, ct) is { } window)
                {
                    foreach (var day in (await stats.PatternsAsync(userId, window, ct)).Calendar)
                    {
                        // from/to narrow the same year of rows, so a month leaves in one request.
                        if (from is { } start && day.Date < start) continue;
                        if (to is { } end && day.Date > end) continue;
                        csv.Append(CultureInfo.InvariantCulture, $"{day.Date:yyyy-MM-dd},{day.Occupancy:0.####},{day.Visits},{day.Motion:0.####}\n");
                    }
                }

                return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", "PoWatch-days.csv");
            })
            .WithTags("Data")
            .RequireAuthorization()
            .WithName("ExportDays")
            .WithSummary("One CSV row per observed day of the last year: occupancy, visits, motion. Narrow it with from/to (yyyy-MM-dd).");

        app.MapDelete("/api/data", async (HttpContext http, IUserDataStore data, ILogger<Program> logger, CancellationToken ct) =>
            {
                if (CurrentUser.Id(http.User) is not { } userId) return Results.Unauthorized();
                await data.PurgeAsync(userId, ct);
                logger.LogWarning("User data purged on request.");
                return Results.NoContent();
            })
            .WithTags("Data")
            .RequireAuthorization()
            .WithName("DeleteMyData")
            .WithSummary("Delete everything stored for the caller: sessions, ticks, events, rollups, regulars and snapshots.")
            .InvalidatesStats();

        return app;
    }
}
