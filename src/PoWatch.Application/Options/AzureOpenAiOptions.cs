using System.ComponentModel.DataAnnotations;

namespace PoWatch.Application.Options;

/// <summary>The Azure OpenAI deployment used for recaps when AiProvider is AzureOpenAi.</summary>
public sealed class AzureOpenAiOptions
{
    /// <summary>Azure OpenAI resource endpoint, e.g. https://my-resource.openai.azure.com/</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Azure OpenAI API key. Leave empty to sign in with the app's Entra identity instead (needs the Cognitive Services OpenAI User role).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Name of the deployed chat model, e.g. gpt-5.4-nano.</summary>
    [Required]
    public string DeploymentName { get; set; } = "gpt-5.4-nano";
}
