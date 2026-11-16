namespace PoWatch.Client.Services;

/// <summary>
/// The caption models Settings offers, keyed as in wwwroot/js/caption-models.js. The choice is kept
/// per browser: it depends on this device's GPU, not on the account.
/// </summary>
public static class CaptionModels
{
    public const string Key = "powatch:caption-model";

    /// <summary>
    /// Key and the label Settings shows, default first. The Ollama tags run on this PC's own Ollama server:
    /// MiniCPM-V was the best value when the same frames were captioned on an RTX 5070 Ti laptop
    /// (2026-10-17), nearly Qwen3-VL's accuracy at a third of the time. Ollama is optional; without it an
    /// Ollama pick captions with in-browser Florence-2. The browser keys run in this browser on WebGPU
    /// (else the CPU): Florence-2 came closest to human captions on scripts/caption-eval, and SmolVLM
    /// 500M is the one that reads the detector's hint.
    /// </summary>
    public static readonly IReadOnlyList<(string Key, string Label)> All =
    [
        ("minicpm-v", "MINICPM-V · OLLAMA"),
        ("qwen3-vl:8b-instruct", "QWEN3-VL 8B · OLLAMA · MOST DETAIL"),
        ("qwen2.5vl:7b", "QWEN2.5-VL 7B · OLLAMA"),
        ("florence-2", "FLORENCE-2 · WEBGPU"),
        ("smolvlm-500m", "SMOLVLM 500M · WEBGPU · USES DETECTOR"),
        ("smolvlm-256m", "SMOLVLM 256M · WEBGPU"),
    ];

    public static string Default => All[0].Key;

    public static string Label(string key) => All.FirstOrDefault(m => m.Key == key).Label ?? All[0].Label;

    public static string Normalize(string? key) => All.Any(m => m.Key == key) ? key! : Default;

    public static string Next(string key)
    {
        var i = All.Select(m => m.Key).ToList().IndexOf(key);
        return All[(i + 1) % All.Count].Key;
    }
}
