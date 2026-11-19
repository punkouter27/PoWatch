using System.Text.Json;
using PoWatch.Shared.Models;

namespace PoWatch.Mobile.Services;

/// <summary>
/// Durable tick buffer (item 3): every closed 10-second batch is written to the phone first, one file
/// per batch, and posted from there oldest first — so no signal, a server outage, a crash or a reboot
/// loses nothing. The server counts each <c>BatchKey</c> once, so a replay after a timeout is safe.
/// A full outbox drops the oldest batch and counts it, rather than filling the phone.
/// </summary>
public sealed class Outbox(ApiClient api) : IDisposable
{
    /// <summary>Two days of continuous observing at one batch per 10 s.</summary>
    public const int MaxBatches = 17_280;

    private readonly string _dir = Directory.CreateDirectory(Path.Combine(FileSystem.AppDataDirectory, "outbox")).FullName;
    private readonly SemaphoreSlim _sending = new(1, 1);

    public int Pending => Directory.EnumerateFiles(_dir, "*.json").Count();

    public int Dropped { get; private set; }

    public int Sent { get; private set; }

    public int Refused { get; private set; }

    public string? LastProblem { get; private set; }

    public event Action? Changed;

    public void Dispose() => _sending.Dispose();

    // File name: <utc ticks>_<session>_<batch key> — ordering by name is ordering by time.
    public async Task PutAsync(Guid sessionId, IngestBatchDto batch)
    {
        string file = Path.Combine(_dir, $"{DateTime.UtcNow.Ticks:D19}_{sessionId:N}_{batch.BatchKey:N}.json");
        string temp = file + ".tmp";
        await File.WriteAllBytesAsync(temp, JsonSerializer.SerializeToUtf8Bytes(batch, MobileJsonContext.Default.IngestBatchDto));
        File.Move(temp, file, overwrite: true);

        string[] all = [.. Directory.EnumerateFiles(_dir, "*.json").Order(StringComparer.Ordinal)];
        foreach (string old in all.Take(Math.Max(0, all.Length - MaxBatches)))
        {
            File.Delete(old);
            Dropped++;
        }

        Changed?.Invoke();
    }

    /// <summary>Posts everything waiting, oldest first; stops at the first outage. Concurrent calls join the running pass.</summary>
    public async Task SendAsync(CancellationToken ct = default)
    {
        if (!ApiClient.Online || !await _sending.WaitAsync(0, ct))
        {
            return;
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(_dir, "*.json").Order(StringComparer.Ordinal).ToList())
            {
                string[] parts = Path.GetFileNameWithoutExtension(file).Split('_');
                IngestBatchDto? batch = parts.Length == 3 && Guid.TryParseExact(parts[1], "N", out _) ? Read(file) : null;
                if (batch is null)
                {
                    File.Delete(file); // unreadable: it can never be sent, so it must not block the queue
                    continue;
                }

                try
                {
                    IngestBatchResultDto? result = await api.PostBatchAsync(Guid.ParseExact(parts[1], "N"), batch, ct);
                    if (result is null)
                    {
                        Refused++;
                    }
                    else
                    {
                        Sent++;
                    }

                    File.Delete(file);
                    LastProblem = null;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    LastProblem = "Offline — batches are kept on the phone and will be resent.";
                    break;
                }
            }
        }
        finally
        {
            _sending.Release();
            Changed?.Invoke();
        }
    }

    private static IngestBatchDto? Read(string file)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllBytes(file), MobileJsonContext.Default.IngestBatchDto);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }
}
