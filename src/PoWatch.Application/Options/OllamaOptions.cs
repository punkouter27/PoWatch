namespace PoWatch.Application.Options;

/// <summary>A local Ollama server, reached through its OpenAI-compatible endpoint, when AiProvider is Ollama.</summary>
public sealed class OllamaOptions
{
    public string Endpoint { get; set; } = "http://localhost:11434/v1/";

    /// <summary>A pulled model that can call tools and answer in a JSON schema. Not a thinking model (qwen3): over the
    /// OpenAI-compatible endpoint its reasoning spends the whole token budget and the reply comes back empty.</summary>
    public string Model { get; set; } = "llama3.2";
}
