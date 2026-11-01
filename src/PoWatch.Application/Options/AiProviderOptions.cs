namespace PoWatch.Application.Options;

/// <summary>Who writes the recap paragraph.</summary>
public enum AiProviderType
{
    /// <summary>Deterministic template-based synthesis without external LLM calls.</summary>
    Template,

    /// <summary>Azure OpenAI deployment, see <see cref="AzureOpenAiOptions"/>.</summary>
    AzureOpenAi,

    /// <summary>Google Gemini through its OpenAI-compatible endpoint, see <see cref="GeminiOptions"/>.</summary>
    Gemini,

    /// <summary>A local Ollama server (free, offline), see <see cref="OllamaOptions"/>.</summary>
    Ollama,

    /// <summary>Ollama when it answers, else Gemini, else Azure OpenAI, else Template.</summary>
    Auto
}

/// <summary>Picks the recap writer: Template (default), Azure OpenAI, Gemini, Ollama or Auto.</summary>
public sealed class AiProviderOptions
{
    /// <summary>Selected AI provider. Defaults to Template; every model falls back to Template on failure.</summary>
    public AiProviderType Provider { get; set; } = AiProviderType.Template;
}
