using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using PoWatch.Shared.Models;

namespace PoWatch.E2EAPI;

public sealed class SnapshotE2ETests(ApiE2EFactory factory) : IClassFixture<ApiE2EFactory>
{
    private HttpClient ClientFor(string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fake-User", user);
        return client;
    }

    [Fact]
    public async Task A_snapshot_uploads_under_its_owner_and_comes_back_as_a_private_moment()
    {
        var owner = $"snap-{Guid.NewGuid():N}";
        using var client = ClientFor(owner);
        using var stranger = ClientFor($"snoop-{Guid.NewGuid():N}");

        var session = (await (await client.PostAsJsonAsync("/api/sessions", new StartSessionRequestDto { TimeZoneId = "UTC" }))
            .Content.ReadFromJsonAsync<SessionDto>())!;
        var day = DateOnly.FromDateTime(session.StartedUtc.UtcDateTime);

        var upload = (await (await client.PostAsync($"/api/snapshots?day={day:yyyy-MM-dd}", null)).Content.ReadFromJsonAsync<SnapshotUploadDto>())!;
        Assert.StartsWith($"{owner}/{day:yyyyMMdd}/", upload.Path, StringComparison.Ordinal);

        // The browser PUTs the JPEG straight to storage with the write-only link.
        using var storage = new HttpClient();
        using var put = new HttpRequestMessage(HttpMethod.Put, upload.UploadUrl) { Content = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xD9]) };
        put.Headers.Add("x-ms-blob-type", "BlockBlob");
        put.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        Assert.Equal(HttpStatusCode.Created, (await storage.SendAsync(put)).StatusCode);

        var batch = new IngestBatchDto
        {
            BatchKey = Guid.NewGuid(),
            Events =
            [
                new() { AtUtc = session.StartedUtc.AddSeconds(2), Kind = "Notable", Text = "First cat of the session", Score = 0.8, ImagePath = upload.Path },
                new() { AtUtc = session.StartedUtc.AddSeconds(3), Kind = "Notable", Text = "Motion spike", Score = 0.5 },
            ]
        };
        (await client.PostAsJsonAsync($"/api/sessions/{session.Id}/batches", batch)).EnsureSuccessStatusCode();

        var moments = (await client.GetFromJsonAsync<List<MomentDto>>($"/api/sessions/{session.Id}/moments"))!;
        Assert.Equal(["First cat of the session", "Motion spike"], moments.Select(m => m.Text));
        Assert.NotNull(moments[0].ImageUrl);
        Assert.Null(moments[1].ImageUrl);
        Assert.Equal(HttpStatusCode.OK, (await storage.GetAsync(moments[0].ImageUrl!)).StatusCode);

        // Someone else's session is simply not found, and naming someone else's snapshot path in one's
        // own moment yields no image link.
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/sessions/{session.Id}/moments")).StatusCode);
        var theirs = (await (await stranger.PostAsJsonAsync("/api/sessions", new StartSessionRequestDto { TimeZoneId = "UTC" }))
            .Content.ReadFromJsonAsync<SessionDto>())!;
        (await stranger.PostAsJsonAsync($"/api/sessions/{theirs.Id}/batches", new IngestBatchDto
        {
            BatchKey = Guid.NewGuid(),
            Events = [new() { AtUtc = theirs.StartedUtc.AddSeconds(2), Kind = "Notable", Text = "Borrowed cat", Score = 0.9, ImagePath = upload.Path }]
        })).EnsureSuccessStatusCode();
        Assert.Null((await stranger.GetFromJsonAsync<List<MomentDto>>($"/api/sessions/{theirs.Id}/moments"))!.Single().ImageUrl);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/snapshots?day=someday", null)).StatusCode);
    }
}
