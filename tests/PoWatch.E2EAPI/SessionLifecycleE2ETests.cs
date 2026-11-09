using System.Net;
using System.Net.Http.Json;
using PoWatch.Shared.Models;

namespace PoWatch.E2EAPI;

/// <summary>Turn it on, walk away, come back: the session lifecycle as the client drives it.</summary>
public sealed class SessionLifecycleE2ETests(ApiE2EFactory factory) : IClassFixture<ApiE2EFactory>
{
    private HttpClient ClientFor(string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fake-User", user);
        return client;
    }

    [Fact]
    public async Task Deleting_your_data_removes_all_of_it_and_nobody_elses()
    {
        // "ab" and "ab-c" share a prefix on purpose: the purge is a partition-range delete.
        var prefix = $"purge-{Guid.NewGuid():N}";
        using var mine = ClientFor(prefix);
        using var theirs = ClientFor($"{prefix}-neighbour");
        foreach (var client in new[] { mine, theirs })
        {
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/sessions", new StartSessionRequestDto { SessionId = Guid.NewGuid(), TimeZoneId = "UTC" })).StatusCode);
            Assert.True((await client.PostAsync("/api/dev/seed?days=2&tz=UTC", null)).IsSuccessStatusCode);
        }

        Assert.StartsWith("date,occupancy,visits,motion\n2", await mine.GetStringAsync("/api/export.csv?tz=UTC"), StringComparison.Ordinal);
        // from/to narrow the same year of rows, so one month can leave in a single request.
        Assert.Equal("date,occupancy,visits,motion\n", await mine.GetStringAsync("/api/export.csv?tz=UTC&from=2999-01-01"));

        Assert.Equal(HttpStatusCode.NoContent, (await mine.DeleteAsync("/api/data")).StatusCode);

        Assert.Empty((await mine.GetFromJsonAsync<List<SessionDto>>("/api/sessions"))!);
        Assert.Equal("date,occupancy,visits,motion\n", await mine.GetStringAsync("/api/export.csv?tz=UTC"));
        Assert.Single((await theirs.GetFromJsonAsync<List<SessionDto>>("/api/sessions"))!);
        Assert.StartsWith("date,occupancy,visits,motion\n2", await theirs.GetStringAsync("/api/export.csv?tz=UTC"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sessions_can_be_listed_by_the_time_range_they_overlap()
    {
        using var client = ClientFor($"range-{Guid.NewGuid():N}");
        var id = Guid.NewGuid();
        var started = await (await client.PostAsJsonAsync("/api/sessions", new StartSessionRequestDto { SessionId = id, TimeZoneId = "UTC" })).Content.ReadFromJsonAsync<SessionDto>();

        string Range(DateTimeOffset from, DateTimeOffset to) => $"/api/sessions?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";
        var around = await client.GetFromJsonAsync<List<SessionDto>>(Range(started!.StartedUtc.AddHours(-1), started.StartedUtc.AddHours(1)));
        var before = await client.GetFromJsonAsync<List<SessionDto>>(Range(started.StartedUtc.AddDays(-2), started.StartedUtc.AddDays(-1)));

        Assert.Equal([id], around!.Select(s => s.Id));
        Assert.Empty(before!);
    }

    [Fact]
    public async Task A_session_starts_lists_and_stops_once()
    {
        using var client = ClientFor($"sessions-{Guid.NewGuid():N}");
        var id = Guid.NewGuid();

        var start = await client.PostAsJsonAsync("/api/sessions", new StartSessionRequestDto { SessionId = id, TimeZoneId = "America/New_York" });
        Assert.Equal(HttpStatusCode.Created, start.StatusCode);
        var started = await start.Content.ReadFromJsonAsync<SessionDto>();
        Assert.Equal(id, started!.Id);
        Assert.True(started.IsRunning);
        Assert.Equal("America/New_York", started.TimeZoneId);

        // Starting the same session again (a retried request) returns it rather than a second one.
        var retry = await client.PostAsJsonAsync("/api/sessions", new StartSessionRequestDto { SessionId = id, TimeZoneId = "America/New_York" });
        Assert.Equal(started.StartedUtc, (await retry.Content.ReadFromJsonAsync<SessionDto>())!.StartedUtc);

        var listed = await client.GetFromJsonAsync<List<SessionDto>>("/api/sessions");
        Assert.Equal([id], listed!.Select(s => s.Id));

        var stop = await client.PostAsync($"/api/sessions/{id}/stop", null);
        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
        var stopped = await stop.Content.ReadFromJsonAsync<SessionDto>();
        Assert.False(stopped!.IsRunning);
        Assert.NotNull(stopped.EndedUtc);

        // Stopping twice keeps the first end time.
        var again = await (await client.PostAsync($"/api/sessions/{id}/stop", null)).Content.ReadFromJsonAsync<SessionDto>();
        Assert.Equal(stopped.EndedUtc, again!.EndedUtc);

        var fetched = await client.GetFromJsonAsync<SessionDto>($"/api/sessions/{id}");
        Assert.Equal(stopped.EndedUtc, fetched!.EndedUtc);
    }

    [Fact]
    public async Task Sessions_are_private_and_bad_input_is_rejected()
    {
        using var owner = ClientFor($"owner-{Guid.NewGuid():N}");
        using var stranger = ClientFor($"stranger-{Guid.NewGuid():N}");

        var started = await (await owner.PostAsJsonAsync("/api/sessions", new StartSessionRequestDto { TimeZoneId = "UTC" }))
            .Content.ReadFromJsonAsync<SessionDto>();

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/sessions/{started!.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync($"/api/sessions/{started.Id}/stop", null)).StatusCode);
        Assert.Empty((await stranger.GetFromJsonAsync<List<SessionDto>>("/api/sessions"))!);

        var badZone = await owner.PostAsJsonAsync("/api/sessions", new StartSessionRequestDto { TimeZoneId = "Mars/Olympus_Mons" });
        Assert.Equal(HttpStatusCode.BadRequest, badZone.StatusCode);
    }
}
