using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using PoWatch.Shared.Models;

namespace PoWatch.E2EAPI;

public sealed class StatsE2ETests(ApiE2EFactory factory) : IClassFixture<ApiE2EFactory>
{
    private HttpClient ClientFor(string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fake-User", user);
        return client;
    }

    [DockerFact]
    public async Task A_year_of_history_answers_within_the_time_budget()
    {
        using var client = ClientFor($"year-{Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/dev/seed?days=365&tz=UTC", null)).StatusCode);

        var (today, todayMs) = await TimedAsync<PresenceStatsDto>(client, "/api/stats/presence?range=today&tz=UTC");
        var (all, allMs) = await TimedAsync<PresenceStatsDto>(client, "/api/stats/presence?range=all&tz=UTC");
        var (patterns, patternsMs) = await TimedAsync<PatternStatsDto>(client, "/api/stats/patterns?range=today&tz=UTC");

        // Today ≤ 1.5 s, all-time over a year ≤ 2 s (first, uncached call).
        Assert.True(todayMs <= 1_500, $"today took {todayMs} ms");
        Assert.True(allMs <= 2_000, $"all-time took {allMs} ms");
        Assert.True(patternsMs <= 2_000, $"patterns took {patternsMs} ms");

        Assert.Equal("Minute", today.Window.Grain);
        Assert.NotEmpty(today.Series);
        Assert.Equal("Day", all.Window.Grain);
        Assert.Equal(365, all.Series.Count);
        Assert.InRange(all.Occupancy, 0.01, 1);
        Assert.True(all.Visits > 0);
        Assert.Equal(365, patterns.Calendar.Count);
        Assert.Equal(7, patterns.HourByWeekday.Count);
        Assert.All(patterns.HourByWeekday, day => Assert.Equal(24, day.Count));
        Assert.Equal(24, patterns.UsualProfile.Count);

        var month = await client.GetFromJsonAsync<PresenceStatsDto>("/api/stats/presence?range=30d&tz=UTC");
        Assert.Equal("Hour", month!.Window.Grain);
        var pipeline = await client.GetFromJsonAsync<PipelineStatsDto>("/api/stats/pipeline?range=all&tz=UTC");
        Assert.True(pipeline!.AllTimeFrames > 0);
        Assert.InRange(pipeline.PixelHz, 3.9, 4.1);

        // The one-file export carries every family.
        var everything = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/stats/all?range=30d&tz=UTC");
        Assert.Equal(["presence", "space", "objects", "patterns", "environment", "pipeline", "ticks", "events"], everything.EnumerateObject().Select(p => p.Name));
        Assert.Equal(month.Visits, everything.GetProperty("presence").GetProperty("visits").GetInt64());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, everything.GetProperty("ticks").ValueKind);

        // A short window also carries the raw record.
        var day = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/stats/all?range=today&tz=UTC");
        Assert.Equal(System.Text.Json.JsonValueKind.Array, day.GetProperty("ticks").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, day.GetProperty("events").ValueKind);
    }

    [DockerFact]
    public async Task Fresh_batches_show_up_in_every_stat_family_straight_away()
    {
        using var client = ClientFor($"live-{Guid.NewGuid():N}");
        var session = (await (await client.PostAsJsonAsync("/api/sessions", new StartSessionRequestDto { TimeZoneId = "UTC" }))
            .Content.ReadFromJsonAsync<SessionDto>())!;

        var before = await client.GetFromJsonAsync<PresenceStatsDto>("/api/stats/presence?range=today&tz=UTC");
        Assert.Equal(0, before!.Visits);

        var at = session.StartedUtc.AddSeconds(1);
        var grid = Enumerable.Repeat(0f, 144).ToList();
        grid[40] = 0.9f;
        await PostBatchAsync(client, session.Id, new IngestBatchDto
        {
            BatchKey = Guid.NewGuid(),
            Ticks = [new() { StartUtc = at, PixelSamples = 40, DetectorSamples = 10, MotionMean = 0.3, MotionMax = 0.6, LuminanceMean = 0.5,
                             MotionGrid = grid, Palette = [0xFF8000], Classes = new() { ["person"] = new() { Max = 1, Mean = 1 }, ["dog"] = new() { Max = 1, Mean = 1 } } }],
            Events =
            [
                new() { AtUtc = at, Kind = "TrackEnter", TrackId = "T1", Class = "person", Edge = "Left" },
                new() { AtUtc = at.AddSeconds(1), Kind = "Caption", Text = "A dog trots past the sofa." },
                new() { AtUtc = at.AddSeconds(2), Kind = "Caption", Text = "A person sits on the sofa." },
                new() { AtUtc = at.AddSeconds(3), Kind = "Caption", Text = "A person sits on the sofa reading." },
            ]
        });

        // The earlier (cached) answer must not survive the ingest.
        var presence = await client.GetFromJsonAsync<PresenceStatsDto>("/api/stats/presence?range=today&tz=UTC");
        Assert.Equal(1, presence!.Visits);
        // A person and a dog: animals count toward presence.
        Assert.Equal(2, presence.PeakConcurrency);

        var space = await client.GetFromJsonAsync<SpaceStatsDto>("/api/stats/space?range=today&tz=UTC");
        Assert.Equal(40, space!.HottestCell);
        Assert.Equal(1, space.Entries["Left"]);

        var objects = await client.GetFromJsonAsync<ObjectStatsDto>($"/api/stats/objects?range=session&sessionId={session.Id}");
        Assert.Equal(["dog", "person"], objects!.Classes.Select(c => c.Name).Order());

        var environment = await client.GetFromJsonAsync<EnvironmentStatsDto>("/api/stats/environment?range=today&tz=UTC");
        Assert.Equal(3, environment!.CaptionCount);
        Assert.Contains("dog", environment.WeirdestCaption!, StringComparison.Ordinal);
        Assert.Contains(environment.Words, w => w is { Word: "sofa", Count: 3 });

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/stats/presence?range=forever")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/stats/presence?range=session&sessionId={Guid.NewGuid()}")).StatusCode);
    }

    private static async Task PostBatchAsync(HttpClient client, Guid sessionId, IngestBatchDto batch)
    {
        var response = await client.PostAsJsonAsync($"/api/sessions/{sessionId}/batches", batch);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<(T Body, long Milliseconds)> TimedAsync<T>(HttpClient client, string url)
    {
        var watch = Stopwatch.StartNew();
        var body = await client.GetFromJsonAsync<T>(url);
        return (body!, watch.ElapsedMilliseconds);
    }
}
