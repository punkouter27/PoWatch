using System.Text.Json;
using PoWatch.Shared.Models;

namespace PoWatch.Mobile.Services;

public sealed record QueuedWatchBatch(Guid BatchKey, Guid SessionId, IngestBatchDto Batch, DateTimeOffset QueuedUtc);

public sealed class OfflineOutboxService : IDisposable
{
    private const int MaxBatches = 360;
    private readonly string _outboxDir = Path.Combine(FileSystem.AppDataDirectory, "powatch-outbox");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Dispose() => _gate.Dispose();

    public OfflineOutboxService()
    {
        Directory.CreateDirectory(_outboxDir);
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return Directory.EnumerateFiles(_outboxDir, "*.json").Count();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<QueuedWatchBatch>> ListAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var items = new List<QueuedWatchBatch>();
            foreach (var file in Directory.EnumerateFiles(_outboxDir, "*.json"))
            {
                var json = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                var item = JsonSerializer.Deserialize<QueuedWatchBatch>(json);
                if (item is not null)
                {
                    items.Add(item);
                }
            }

            return [.. items.OrderBy(i => i.QueuedUtc)];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EnqueueAsync(Guid sessionId, IngestBatchDto batch, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var record = new QueuedWatchBatch(batch.BatchKey, sessionId, batch, DateTimeOffset.UtcNow);
            var target = Path.Combine(_outboxDir, $"{record.QueuedUtc.UtcTicks:D20}_{batch.BatchKey:N}.json");
            var temp = target + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(record), ct).ConfigureAwait(false);
            File.Move(temp, target, overwrite: true);

            var files = Directory.EnumerateFiles(_outboxDir, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToList();
            while (files.Count > MaxBatches)
            {
                File.Delete(files[0]);
                files.RemoveAt(0);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(Guid batchKey, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var suffix = $"_{batchKey:N}.json";
            foreach (var file in Directory.EnumerateFiles(_outboxDir, "*.json"))
            {
                if (file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
