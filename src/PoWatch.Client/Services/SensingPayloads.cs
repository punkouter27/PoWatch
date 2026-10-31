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
    public double LoadMs { get; init; }
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
public sealed record InferenceStatusPayload(string? ModelKey, string LoadState, string? Device, string? LoadError);

/// <summary>What the comet trails (wwwroot/js/fx.js) need from a live session, a few times a second.</summary>
public sealed class FxFramePayload
{
    public bool Running { get; init; }
    public List<FxTrackPayload> Tracks { get; init; } = [];
}

/// <summary>A person or animal in frame: its track and the centre of its box, normalised to [0, 1].</summary>
public sealed class FxTrackPayload
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public double X { get; init; }
    public double Y { get; init; }
}

/// <summary>One batch waiting in the outbox (outbox.js), as its JSON text.</summary>
public sealed class OutboxEntryPayload
{
    public string Key { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string Json { get; init; } = string.Empty;
}
