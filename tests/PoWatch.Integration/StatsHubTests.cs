using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using PoWatch.Shared.Models;

namespace PoWatch.Integration;

public sealed class StatsHubTests(AzuriteWebApplicationFactory factory) : IClassFixture<AzuriteWebApplicationFactory>
{
    [DockerFact]
    public async Task An_ingest_pushes_stats_changed_to_that_user_only()
    {
        var owner = $"hub-{Guid.NewGuid():N}";
        await using var ownerHub = await ConnectAsync(owner);
        await using var strangerHub = await ConnectAsync($"hub-other-{Guid.NewGuid():N}");

        var received = new TaskCompletionSource<StatsChangedDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var strangerHeard = false;
        ownerHub.On<StatsChangedDto>("statsChanged", change => received.TrySetResult(change));
        strangerHub.On<StatsChangedDto>("statsChanged", _ => strangerHeard = true);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fake-User", owner);
        var session = (await (await client.PostAsJsonAsync("/api/sessions", new StartSessionRequestDto { TimeZoneId = "UTC" }))
            .Content.ReadFromJsonAsync<SessionDto>())!;
        var batch = new IngestBatchDto
        {
            BatchKey = Guid.NewGuid(),
            Ticks = [new() { StartUtc = session.StartedUtc.AddSeconds(1), PixelSamples = 4, MotionMean = 0.1, MotionMax = 0.1, LuminanceMean = 0.5 }]
        };
        (await client.PostAsJsonAsync($"/api/sessions/{session.Id}/batches", batch)).EnsureSuccessStatusCode();

        var change = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(session.Id, change.SessionId);
        Assert.Equal(1, change.Ticks);

        // A replay is not news.
        var replayHeard = false;
        ownerHub.On<StatsChangedDto>("statsChanged", _ => replayHeard = true);
        (await client.PostAsJsonAsync($"/api/sessions/{session.Id}/batches", batch)).EnsureSuccessStatusCode();
        await Task.Delay(500);
        Assert.False(replayHeard);
        Assert.False(strangerHeard);
    }

    private async Task<HubConnection> ConnectAsync(string user)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "hubs/stats"), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                options.Headers["X-Fake-User"] = user;
            })
            .Build();
        await connection.StartAsync();
        return connection;
    }
}
