using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PoWatch.Application.Contracts;
using PoWatch.Infrastructure.Runtime;

namespace PoWatch.Unit;

public sealed class AiModelRegistryTests
{
    [Fact]
    public void Configured_models_have_distinct_clients_safe_labels_and_an_auto_choice_with_one_fallback()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AiProvider:Provider"] = "AzureOpenAi",
            ["AzureOpenAi:Endpoint"] = "https://example.openai.azure.com/",
            ["AzureOpenAi:ApiKey"] = "not-a-real-key",
            ["AzureOpenAi:DeploymentName"] = "test-deployment",
            ["Gemini:ApiKey"] = "not-a-real-google-key",
            ["Gemini:Model"] = "test-gemini",
            ["Ollama:Model"] = "test-local",
            // Nothing listens on the discard port, so Auto finds Ollama down without leaving this machine.
            ["Ollama:Endpoint"] = "http://127.0.0.1:9/v1/"
        }).Build();
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        services.AddPoWatchRecapAi(configuration);
        using var provider = services.BuildServiceProvider();
        var models = provider.GetRequiredService<IAiModelRegistry>();
        // A chosen model always leads its own chain, so the first link is that model's client.
        IChatClient Client(string id) => models.Chain(id).First();

        Assert.Equal("AzureOpenAi", models.DefaultId);
        Assert.Equal(["Template", "Auto", "AzureOpenAi", "Gemini", "Ollama"], models.Models.Select(m => m.Id));
        Assert.Equal("test-deployment", models.Models.Single(m => m.Id == "AzureOpenAi").Model);
        Assert.Same(Client("Ollama"), Client("Ollama"));
        Assert.NotSame(Client("AzureOpenAi"), Client("Ollama"));
        Assert.DoesNotContain(models.Models, m => m.ToString().Contains("not-a-real", StringComparison.Ordinal));
        Assert.Empty(models.Chain("unknown"));

        // Ollama is down, so Auto picks Gemini with Azure OpenAI behind it; a chosen model gets the next
        // available one as its only fallback, and Template gets none.
        Assert.Equal([Client("Gemini"), Client("AzureOpenAi")], models.Chain("Auto"));
        Assert.Equal([Client("Ollama"), Client("Gemini")], models.Chain("Ollama"));
        Assert.Equal([Client("Gemini"), Client("AzureOpenAi")], models.Chain("Gemini"));
        Assert.Empty(models.Chain("Template"));
        Assert.Equal("Gemini test-gemini", models.Describe().Single(m => m.Id == "Auto").Model);
    }
}
