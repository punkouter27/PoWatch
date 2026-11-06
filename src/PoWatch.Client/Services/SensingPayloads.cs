namespace PoWatch.Client.Services;

// Shapes the sensing JS bridges send to .NET. They arrive as JSON text and are parsed with the
// source-generated PoWatchJsonContext, so nothing here depends on reflection surviving the trimmer.

public sealed class PixelSamplePayload
{
    public DateTimeOffset AtUtc { get; init; }
    public double Motion { get; init; }
    public double Luminance { get; init; }
    public List<float> Grid { get; init; } = [];
    public List<int> Palette { get; init; } = [];
}

public sealed class DetectorInfoPayload
{
    public string ModelId { get; init; } = string.Empty;
    public string Device { get; init; } = string.Empty;
    /// <summary>The precision that passed calibration: fp16 or fp32 on WebGPU, q8 or fp32 on WASM.</summary>
    public string? Dtype { get; init; }
}

public sealed class VlmResultPayload
{
    public bool IsAvailable { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Activity { get; init; } = string.Empty;
    /// <summary>The model's reply, wrapped in &lt;S&gt;…&lt;E&gt;; the caption parser strips the markers.</summary>
    public string Caption { get; init; } = string.Empty;
    /// <summary>The caption's sentence embedding (MiniLM, unit length), or null when the embedder is unavailable.</summary>
    public float[]? Embedding { get; init; }
}

/// <summary>The caption worker's actual model and backend, separate from the pending selection.</summary>
public sealed record InferenceStatusPayload(string? ModelKey, string LoadState, string? Device, string? LoadError, string? ModelId = null, string? OllamaMissing = null);

/// <summary>One batch waiting in the outbox (outbox.js), as its JSON text.</summary>
public sealed class OutboxEntryPayload
{
    public string Key { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string Json { get; init; } = string.Empty;
}
