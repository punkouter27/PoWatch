namespace PoWatch.Application.Options;

/// <summary>Google Gemini, reached through its OpenAI-compatible endpoint, when AiProvider is Gemini.</summary>
public sealed class GeminiOptions
{
    public string Endpoint { get; set; } = "https://generativelanguage.googleapis.com/v1beta/openai/";

    /// <summary>A Google AI Studio key. A secret: keep it in Key Vault (Gemini--ApiKey), never in appsettings.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>The model; the "latest" alias follows Google's newest Flash-Lite, the cheapest tier.</summary>
    public string Model { get; set; } = "gemini-flash-lite-latest";
}
