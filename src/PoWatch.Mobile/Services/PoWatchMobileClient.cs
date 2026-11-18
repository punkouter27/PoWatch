using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using PoWatch.Shared.Models;

namespace PoWatch.Mobile.Services;

public sealed class PoWatchMobileClient(
    HttpClient http,
    MobileConfig config,
    SecureSessionStore sessionStore,
    OfflineOutboxService outbox) : IAsyncDisposable
{
    private HubConnection? _hub;

    public event Action<StatsChangedDto>? StatsChanged;

    private Uri BuildUri(string relativePath) => new(new Uri(config.BaseUrl + "/"), relativePath.TrimStart('/'));

    public async Task<AuthStateDto?> GetAuthMeAsync(CancellationToken ct = default)
        => await http.GetFromJsonAsync<AuthStateDto>(BuildUri("/auth/me"), ct).ConfigureAwait(false);

    public async Task<SessionDto?> StartSessionAsync(Guid? sessionId = null, CancellationToken ct = default)
    {
        var req = new StartSessionRequestDto
        {
            SessionId = sessionId ?? Guid.NewGuid(),
            TimeZoneId = TimeZoneInfo.Local.Id,
        };
        using var resp = await http.PostAsJsonAsync(BuildUri("/api/sessions"), req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<SessionDto>(ct).ConfigureAwait(false);
    }

    public async Task<SessionDto?> StopSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        using var resp = await http.PostAsync(BuildUri($"/api/sessions/{sessionId}/stop"), content: null, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<SessionDto>(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SessionDto>> GetRecentSessionsAsync(int take = 15, CancellationToken ct = default)
        => await http.GetFromJsonAsync<List<SessionDto>>(BuildUri($"/api/sessions?take={take}"), ct).ConfigureAwait(false) ?? [];

    public async Task<bool> SendOrQueueBatchAsync(Guid sessionId, IngestBatchDto batch, CancellationToken ct = default)
    {
        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
        {
            await outbox.EnqueueAsync(sessionId, batch, ct).ConfigureAwait(false);
            return false;
        }

        try
        {
            using var resp = await http.PostAsJsonAsync(BuildUri($"/api/sessions/{sessionId}/batches"), batch, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                return true;
            }
        }
        catch (HttpRequestException)
        {
        }
        catch (TaskCanceledException)
        {
        }

        await outbox.EnqueueAsync(sessionId, batch, ct).ConfigureAwait(false);
        return false;
    }

    public async Task<int> FlushOutboxAsync(CancellationToken ct = default)
    {
        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
        {
            return 0;
        }

        var sent = 0;
        var pending = await outbox.ListAsync(ct).ConfigureAwait(false);
        foreach (var item in pending)
        {
            using var resp = await http.PostAsJsonAsync(BuildUri($"/api/sessions/{item.SessionId}/batches"), item.Batch, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                break;
            }

            await outbox.RemoveAsync(item.BatchKey, ct).ConfigureAwait(false);
            sent++;
        }

        return sent;
    }

    public async Task ConnectHubAsync(CancellationToken ct = default)
    {
        if (_hub is { State: HubConnectionState.Connected or HubConnectionState.Connecting })
        {
            return;
        }

        var fakeUser = await sessionStore.GetFakeUserAsync().ConfigureAwait(false);
        _hub = new HubConnectionBuilder()
            .WithUrl(BuildUri("/hubs/stats"), options =>
            {
                options.AccessTokenProvider = () => sessionStore.GetBearerTokenAsync();
                if (!string.IsNullOrWhiteSpace(fakeUser))
                {
                    options.Headers["X-Fake-User"] = fakeUser;
                }
            })
            .WithAutomaticReconnect()
            .Build();

        _hub.On<StatsChangedDto>("statsChanged", dto => StatsChanged?.Invoke(dto));
        await _hub.StartAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null)
        {
            await _hub.DisposeAsync().ConfigureAwait(false);
        }
    }
}
