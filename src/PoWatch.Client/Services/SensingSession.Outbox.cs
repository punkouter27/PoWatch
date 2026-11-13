using System.Text.Json;
using PoWatch.Shared.Models;

namespace PoWatch.Client.Services;

/// <summary>Closed batches go into the durable outbox (outbox.js) first and are posted from there, oldest first.</summary>
public sealed partial class SensingSession
{
    private async Task FlushAsync(CancellationToken ct)
    {
        await KeepAsync(_batcher.Flush());
        // Sending runs beside this loop: a request hanging on a dead network must not stop new
        // batches from reaching the durable outbox.
        _ = SendOutboxAsync(ct);
    }

    /// <summary>Puts a new batch in the durable outbox, so a reload or crash cannot lose it; counts what an overflowing outbox dropped.</summary>
    private async Task KeepAsync(IngestBatchDto? batch)
    {
        if (batch is null || Live.Session is not { } session) return;
        Live.LostTicks += await js.TryInvokeAsync<int>("powatchOutbox.put", batch.BatchKey.ToString(), session.Id.ToString(), JsonSerializer.Serialize(batch, Json.IngestBatchDto));
    }

    /// <summary>
    /// Posts everything waiting in the outbox, oldest first, whichever session it belongs to: this one,
    /// or one an earlier page load left behind (reloaded, crashed, or stopped while offline). Replays are
    /// safe: the server counts a batch key once. Stops at the first transport failure; the rest wait for
    /// the next pass. While a pass runs, a second call joins it instead of starting another.
    /// </summary>
    public Task SendOutboxAsync(CancellationToken ct = default) =>
        _sending.IsCompleted ? _sending = SendWaitingAsync(ct) : _sending;

    private async Task SendWaitingAsync(CancellationToken ct)
    {
        List<OutboxEntryPayload> waiting;
        try
        {
            waiting = await js.TryInvokeAsync<string>("powatchOutbox.waiting") is { } json ? JsonSerializer.Deserialize(json, Json.ListOutboxEntryPayload) ?? [] : [];
        }
        catch (JsonException)
        {
            waiting = [];
        }
        var left = waiting.Count;
        foreach (var entry in waiting)
        {
            if (!Guid.TryParse(entry.SessionId, out var sessionId) || Read(entry.Json) is not { } batch)
            {
                // Unreadable: it can never be sent, so it must not block the queue.
                await js.TryInvokeVoidAsync("powatchOutbox.remove", entry.Key);
                left--;
                continue;
            }

            try
            {
                var result = await api.PostBatchAsync(sessionId, batch, ct);
                // Null means the server refused it outright; retrying would never help.
                if (result is null) Live.RejectedBatches++;
                else Live.BatchesSent++;
                // Back online: the offline notice has done its job.
                if (Live.LastError?.StartsWith("Offline", StringComparison.Ordinal) == true) Live.LastError = null;
                await js.TryInvokeVoidAsync("powatchOutbox.remove", entry.Key);
                left--;
            }
            catch (JsonException)
            {
                // The server answered with something unreadable. The batch stays queued for the next pass.
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (IsRunning && !ct.IsCancellationRequested)
                {
                    if (Live.LastError is null) Cue("error");
                    Live.LastError = "Offline — batches are queued and will be resent.";
                }
                break;
            }
        }

        Live.PendingBatches = left;
        Notify();
    }

    /// <summary>The batch an outbox entry holds, or null when the entry is corrupt.</summary>
    private static IngestBatchDto? Read(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, Json.IngestBatchDto);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
