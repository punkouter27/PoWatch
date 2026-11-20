using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using PoWatch.Application.Contracts;
using PoWatch.Domain.Models;
using PoWatch.Shared.Models;

namespace PoWatch.Integration;

public sealed class IngestBatchTests(AzuriteWebApplicationFactory factory) : IClassFixture<AzuriteWebApplicationFactory>
{
    [DockerFact]
    public async Task A_batch_lands_once_no_matter_how_often_it_is_replayed()
    {
        var user = $"ingest-{Guid.NewGuid():N}";
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fake-User", user);

        var session = (await (await client.PostAsJsonAsync("/api/sessions", new StartSessionRequestDto { TimeZoneId = "UTC" }))
            .Content.ReadFromJsonAsync<SessionDto>())!;
        var start = session.StartedUtc.AddSeconds(5);
        var batch = new IngestBatchDto
        {
            BatchKey = Guid.NewGuid(),
            Ticks =
            [
                new() { StartUtc = start, PixelSamples = 40, DetectorSamples = 10, MotionMean = 0.2, MotionMax = 0.5, LuminanceMean = 0.6,
                        Classes = new() { ["person"] = new() { Max = 2, Mean = 1.5 } } },
                new() { StartUtc = start.AddSeconds(10), PixelSamples = 40, MotionMean = 0.1, MotionMax = 0.1, LuminanceMean = 0.6 },
            ],
            Events =
            [
                new() { AtUtc = start, Kind = "TrackEnter", TrackId = "T1", Class = "person", Edge = "Left" },
                new() { AtUtc = start.AddSeconds(3), Kind = "Caption", Text = "Someone waves at the camera." },
            ]
        };

        for (var i = 0; i < 3; i++)
        {
            var response = await client.PostAsJsonAsync($"/api/sessions/{session.Id}/batches", batch);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<IngestBatchResultDto>();
            Assert.True(result!.Accepted);
            Assert.Equal(i > 0, result.Replayed);
        }

        var rollups = factory.Services.GetRequiredService<IRollupStore>();
        var allTime = await rollups.GetAllTimeAsync(user, CancellationToken.None);
        Assert.Equal(2, allTime.Ticks);
        Assert.Equal(1, allTime.Visits);
        Assert.Equal(2, allTime.PeakConcurrency);
        var day = LocalDayOf(start);
        var dayBuckets = await rollups.GetRangeAsync(user, RollupGrain.Day, day, day.AddDays(1), CancellationToken.None);
        Assert.Equal(2, Assert.Single(dayBuckets).Ticks);

        var log = factory.Services.GetRequiredService<ISensingLog>();
        Assert.Equal(2, (await log.GetTicksAsync(user, DateOnly.FromDateTime(start.UtcDateTime), CancellationToken.None)).Count);
        Assert.Equal(2, (await log.GetEventsAsync(user, DateOnly.FromDateTime(start.UtcDateTime), CancellationToken.None)).Count);

        // A tick from the far future or from before the session, an unknown event kind and an unknown session are all refused.
        var future = new IngestBatchDto { BatchKey = Guid.NewGuid(), Ticks = [new() { StartUtc = DateTimeOffset.UtcNow.AddHours(1) }] };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/sessions/{session.Id}/batches", future)).StatusCode);
        var backdated = new IngestBatchDto { BatchKey = Guid.NewGuid(), Ticks = [new() { StartUtc = session.StartedUtc.AddDays(-3) }] };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/sessions/{session.Id}/batches", backdated)).StatusCode);
        var badKind = new IngestBatchDto { BatchKey = Guid.NewGuid(), Events = [new() { AtUtc = start, Kind = "Explosion", Text = "boom" }] };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/sessions/{session.Id}/batches", badKind)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/sessions/{Guid.NewGuid()}/batches", batch)).StatusCode);
        Assert.Equal(2, (await rollups.GetAllTimeAsync(user, CancellationToken.None)).Ticks);
    }

    private static DateTimeOffset LocalDayOf(DateTimeOffset instant) =>
        new(instant.UtcDateTime.Date, TimeSpan.Zero);
}
