using System.Net;
using System.Net.Http.Json;
using PoWatch.Shared.Models;

namespace PoWatch.E2EAPI;

public sealed class RegularsE2ETests(ApiE2EFactory factory) : IClassFixture<ApiE2EFactory>
{
    private HttpClient ClientFor(string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fake-User", user);
        return client;
    }

    private static List<float> Look(params int[] bins)
    {
        var histogram = new float[64];
        foreach (var bin in bins) histogram[bin] = 1f / bins.Length;
        return [.. histogram];
    }

    private static async Task<ObserveRegularResultDto> ObserveAsync(HttpClient client, string cls, List<float> look) =>
        (await (await client.PostAsJsonAsync("/api/regulars/observe", new ObserveRegularRequestDto { Class = cls, Signature = look }))
            .Content.ReadFromJsonAsync<ObserveRegularResultDto>())!;

    [DockerFact]
    public async Task A_new_person_can_be_named_recognised_counted_and_merged()
    {
        using var client = ClientFor($"regulars-{Guid.NewGuid():N}");
        using var stranger = ClientFor($"other-{Guid.NewGuid():N}");

        // First sighting: a new, unnamed "Person 1" — the moment the UI offers to name them.
        var first = await ObserveAsync(client, "person", Look(48, 52, 56));
        Assert.True(first.IsNew);
        Assert.Equal("Person 1", first.Regular.DisplayName);
        Assert.False(first.Regular.IsNamed);

        // The same look again is recognised, not re-offered.
        var again = await ObserveAsync(client, "person", Look(48, 52, 56));
        Assert.False(again.IsNew);
        Assert.Equal(first.Regular.Id, again.Regular.Id);

        // Naming is optional and reversible.
        var named = await (await client.PatchAsJsonAsync($"/api/regulars/{first.Regular.Id}", new RenameRegularRequestDto { Name = "  Bob  " }))
            .Content.ReadFromJsonAsync<RegularDto>();
        Assert.Equal("Bob", named!.DisplayName);

        // A finished visit with the regular's id counts once, even when the batch is replayed.
        var session = (await (await client.PostAsJsonAsync("/api/sessions", new StartSessionRequestDto { TimeZoneId = "UTC" }))
            .Content.ReadFromJsonAsync<SessionDto>())!;
        var batch = new IngestBatchDto
        {
            BatchKey = Guid.NewGuid(),
            Ticks = [new() { StartUtc = session.StartedUtc.AddSeconds(1), PixelSamples = 4, MotionMean = 0.1, MotionMax = 0.1, LuminanceMean = 0.5,
                             Classes = new() { ["person"] = new() { Max = 1, Mean = 1 } } }],
            Events = [new() { AtUtc = session.StartedUtc.AddSeconds(5), Kind = "TrackExit", TrackId = "T1", Class = "person", Edge = "Left", DwellSeconds = 42, RegularId = first.Regular.Id }]
        };
        for (var i = 0; i < 2; i++)
            (await client.PostAsJsonAsync($"/api/sessions/{session.Id}/batches", batch)).EnsureSuccessStatusCode();

        var bob = (await client.GetFromJsonAsync<List<RegularDto>>("/api/regulars"))!.Single(r => r.Id == first.Regular.Id);
        Assert.Equal(1, bob.Visits);
        Assert.Equal(42, bob.DwellSeconds);

        var objects = await client.GetFromJsonAsync<ObjectStatsDto>($"/api/stats/objects?range=session&sessionId={session.Id}");
        var entry = Assert.Single(objects!.Regulars);
        Assert.Equal("Bob", entry.DisplayName);
        Assert.Equal(1, entry.Visits);

        // A duplicate (the same person in a different jacket) folds into Bob.
        var duplicate = await ObserveAsync(client, "person", Look(3, 7, 11));
        Assert.True(duplicate.IsNew);
        Assert.Equal("Person 2", duplicate.Regular.DisplayName);
        var merged = await (await client.PostAsJsonAsync("/api/regulars/merge", new MergeRegularsRequestDto { PrimaryId = first.Regular.Id, DuplicateId = duplicate.Regular.Id }))
            .Content.ReadFromJsonAsync<RegularDto>();
        Assert.Equal("Bob", merged!.DisplayName);
        Assert.Single(await client.GetFromJsonAsync<List<RegularDto>>("/api/regulars") ?? []);

        // Private and validated.
        Assert.Empty((await stranger.GetFromJsonAsync<List<RegularDto>>("/api/regulars"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PatchAsJsonAsync($"/api/regulars/{first.Regular.Id}", new RenameRegularRequestDto { Name = "Mallory" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/regulars/observe", new ObserveRegularRequestDto { Class = "person", Signature = [1, 2] })).StatusCode);
    }
}
