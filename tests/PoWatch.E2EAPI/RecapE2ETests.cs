using System.Net;
using System.Net.Http.Json;
using PoWatch.Shared.Models;

namespace PoWatch.E2EAPI;

public sealed class RecapE2ETests(ApiE2EFactory factory) : IClassFixture<ApiE2EFactory>
{
    [Fact]
    public async Task Days_get_a_readable_recap_and_sessions_a_pdf_without_any_ai_configured()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fake-User", $"recap-{Guid.NewGuid():N}");
        using var stranger = factory.CreateClient();
        stranger.DefaultRequestHeaders.Add("X-Fake-User", $"nosy-{Guid.NewGuid():N}");

        var session = (await (await client.PostAsJsonAsync("/api/sessions", new StartSessionRequestDto { TimeZoneId = "UTC" }))
            .Content.ReadFromJsonAsync<SessionDto>())!;
        var at = session.StartedUtc.AddSeconds(1);
        (await client.PostAsJsonAsync($"/api/sessions/{session.Id}/batches", new IngestBatchDto
        {
            BatchKey = Guid.NewGuid(),
            Ticks = [new() { StartUtc = at, PixelSamples = 40, MotionMean = 0.2, MotionMax = 0.4, LuminanceMean = 0.5,
                             Classes = new() { ["person"] = new() { Max = 1, Mean = 1 }, ["cat"] = new() { Max = 1, Mean = 1 } } }],
            Events =
            [
                new() { AtUtc = at, Kind = "TrackEnter", TrackId = "T1", Class = "person", Edge = "Left" },
                new() { AtUtc = at.AddSeconds(2), Kind = "Notable", Text = "First cat of the session", Score = 0.8 },
            ]
        })).EnsureSuccessStatusCode();

        var day = DateOnly.FromDateTime(at.UtcDateTime);
        var daily = await client.GetFromJsonAsync<RecapDto>($"/api/recaps/day/{day:yyyy-MM-dd}?tz=UTC");
        Assert.Equal("template", daily!.Source);
        Assert.Contains(daily.Numbers, n => n is { Label: "Visits", Value: "1" });
        Assert.Contains(daily.Highlights, h => h.Contains("cat", StringComparison.Ordinal));
        Assert.Equal("First cat of the session", Assert.Single(daily.Moments).Text);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/recaps/day/yesterday")).StatusCode);

        await AssertPdfAsync(await client.GetAsync($"/api/recaps/session/{session.Id}.pdf"));
        await AssertPdfAsync(await client.GetAsync($"/api/recaps/day/{day:yyyy-MM-dd}.pdf?tz=UTC"));
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/recaps/session/{session.Id}.pdf")).StatusCode);
    }

    private static async Task AssertPdfAsync(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            // QuestPDF ships no win-arm64 native binary; on such a host the failure must still be explained.
            Assert.Contains("PDF engine", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
            return;
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal("%PDF"u8.ToArray(), bytes.Take(4).ToArray());
    }
}
