using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using PoWatch.Application.Options;
using PoWatch.Application.Contracts;
using PoWatch.Shared.Models;

namespace PoWatch.Infrastructure.Runtime;

/// <summary>
/// Registers the configured <see cref="IChatClient"/> pipelines, defaulting to
/// <c>AiProvider:Provider</c>. Template (the default) uses no client, so recaps stay fully
/// deterministic and offline. Every provider is reached through an OpenAI-compatible endpoint, so one
/// SDK serves them all: Azure OpenAI's v1 endpoint, with an API key or, when none is set, the app's
/// Entra identity (managed identity in App Service, the az CLI login locally); Gemini's with an AI
/// Studio key; a local Ollama server with none. Responses are cached by prompt, so the same facts
/// never cost twice. The same client answers "Ask PoWatch", which is why it can call functions.
/// </summary>
public static class RecapAi
{
    public static IServiceCollection AddPoWatchRecapAi(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton<IAiModelRegistry>(sp => new ModelRegistry(configuration, sp));
        return services;
    }

    /// <summary>How many model turns one request may spend calling tools.</summary>
    public const int MaxToolRounds = 4;

    /// <summary>How long one answer about whether Ollama is up is trusted.</summary>
    public static readonly TimeSpan OllamaProbeTtl = TimeSpan.FromSeconds(30);

    private const string TemplateId = "Template";
    private const string AutoId = "Auto";

    private sealed class ModelRegistry : IAiModelRegistry, IDisposable
    {
        // The selector lists configured models in this order, after Template and Auto.
        private static readonly AiProviderType[] Offered = [AiProviderType.AzureOpenAi, AiProviderType.Gemini, AiProviderType.Ollama];

        // Auto's preference and the fallback order: the free local model when it answers, then the cloud.
        private static readonly string[] Preference = [nameof(AiProviderType.Ollama), nameof(AiProviderType.Gemini), nameof(AiProviderType.AzureOpenAi)];

        private readonly Dictionary<string, IChatClient> _clients = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AiModelDto> _labels = new(StringComparer.Ordinal);
        private readonly HttpClient _probe = new() { Timeout = TimeSpan.FromSeconds(1) };
        private readonly TimeProvider _time;
        private readonly Lock _probeLock = new();
        private readonly Uri? _ollamaModels;
        private readonly string _ollamaModel;
        private DateTimeOffset _probedUntil = DateTimeOffset.MinValue;
        private bool _ollamaUp;

        public ModelRegistry(IConfiguration configuration, IServiceProvider services)
        {
            _time = services.GetService<TimeProvider>() ?? TimeProvider.System;
            var ollama = configuration.GetSection("Ollama").Get<OllamaOptions>() ?? new OllamaOptions();
            _ollamaModel = ollama.Model;
            _ollamaModels = Uri.TryCreate(ollama.Endpoint, UriKind.Absolute, out var local) ? new Uri(local, "models") : null;

            var models = new List<AiModelDto> { new(TemplateId, TemplateId, "No server AI") };
            foreach (var provider in Offered)
            {
                if (Client(provider, configuration) is not { } client) continue;
                var id = provider.ToString();
                var model = provider switch
                {
                    AiProviderType.AzureOpenAi => configuration["AzureOpenAi:DeploymentName"] ?? new AzureOpenAiOptions().DeploymentName,
                    AiProviderType.Gemini => configuration["Gemini:Model"] ?? new GeminiOptions().Model,
                    _ => configuration["Ollama:Model"] ?? new OllamaOptions().Model
                };
                _clients.Add(id, new ChatClientBuilder(client)
                    .UseFunctionInvocation(configure: f => f.MaximumIterationsPerRequest = MaxToolRounds)
                    .UseDistributedCache(configure: cache => cache.CacheKeyAdditionalValues = [id, model])
                    .Build(services));
                _labels.Add(id, new AiModelDto(id, provider == AiProviderType.AzureOpenAi ? "Azure OpenAI" : id, model));
            }
            if (_clients.Count > 0) models.Add(new AiModelDto(AutoId, AutoId, "Ollama, else cloud"));
            models.AddRange(Offered
                .Select(p => p.ToString()).Where(_labels.ContainsKey).Select(id => _labels[id]));
            Models = models.AsReadOnly();
            var preferred = configuration.GetSection("AiProvider").Get<AiProviderOptions>()?.Provider.ToString() ?? TemplateId;
            DefaultId = Models.Any(m => m.Id == preferred) ? preferred : TemplateId;
        }

        public IReadOnlyList<AiModelDto> Models { get; }
        public string DefaultId { get; }

        public IEnumerable<IChatClient> Chain(string id)
        {
            if (id == AutoId)
            {
                foreach (var pick in Available().Take(2)) yield return _clients[pick];
                yield break;
            }
            if (!_clients.TryGetValue(id, out var chosen)) yield break;
            yield return chosen;
            if (Available().FirstOrDefault(other => other != id) is { } next) yield return _clients[next];
        }

        public IReadOnlyList<AiModelDto> Describe() =>
        [
            .. Models.Select(m => m.Id != AutoId ? m
                : Available().FirstOrDefault() is { } pick ? m with { Model = $"{_labels[pick].Provider} {_labels[pick].Model}" }
                : m with { Model = TemplateId })
        ];

        private IEnumerable<string> Available() =>
            Preference.Where(id => _clients.ContainsKey(id) && (id != nameof(AiProviderType.Ollama) || OllamaAnswers()));

        /// <summary>Whether the local Ollama server lists the configured model; asked at most every <see cref="OllamaProbeTtl"/>.</summary>
        private bool OllamaAnswers()
        {
            lock (_probeLock)
            {
                if (_time.GetUtcNow() < _probedUntil) return _ollamaUp;
                _ollamaUp = ProbeOllama();
                _probedUntil = _time.GetUtcNow() + OllamaProbeTtl;
                return _ollamaUp;
            }
        }

        // Synchronous on purpose: the chain is enumerated lazily inside request code, and a local
        // server answers (or refuses the connection) in milliseconds; the client gives up after a second.
        private bool ProbeOllama()
        {
            if (_ollamaModels is null) return false;
            try
            {
                using var response = _probe.Send(new HttpRequestMessage(HttpMethod.Get, _ollamaModels));
                if (!response.IsSuccessStatusCode) return false;
                using var json = JsonDocument.Parse(response.Content.ReadAsStream());
                return json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                    && data.EnumerateArray().Any(m => m.TryGetProperty("id", out var name) && name.GetString() is { } tag
                        && (tag == _ollamaModel || tag.StartsWith(_ollamaModel + ":", StringComparison.Ordinal)));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            foreach (var client in _clients.Values) client.Dispose();
            _probe.Dispose();
        }
    }

    private static IChatClient? Client(AiProviderType provider, IConfiguration configuration)
    {
        switch (provider)
        {
            case AiProviderType.AzureOpenAi:
                var azure = configuration.GetSection("AzureOpenAi").Get<AzureOpenAiOptions>() ?? new AzureOpenAiOptions();
                return Uri.TryCreate(azure.Endpoint, UriKind.Absolute, out var endpoint)
                    ? OpenAi(new Uri(endpoint, "openai/v1/"), string.IsNullOrWhiteSpace(azure.ApiKey) ? null : new ApiKeyCredential(azure.ApiKey), azure.DeploymentName)
                    : null;
            case AiProviderType.Gemini:
                var gemini = configuration.GetSection("Gemini").Get<GeminiOptions>() ?? new GeminiOptions();
                return !string.IsNullOrWhiteSpace(gemini.ApiKey) && Uri.TryCreate(gemini.Endpoint, UriKind.Absolute, out var google)
                    ? OpenAi(google, new ApiKeyCredential(gemini.ApiKey), gemini.Model)
                    : null;
            case AiProviderType.Ollama:
                var ollama = configuration.GetSection("Ollama").Get<OllamaOptions>() ?? new OllamaOptions();
                // Ollama ignores the key, but the SDK will not send a request without one.
                return Uri.TryCreate(ollama.Endpoint, UriKind.Absolute, out var local)
                    ? OpenAi(local, new ApiKeyCredential("ollama"), ollama.Model)
                    : null;
            default:
                return null;
        }
    }

    /// <param name="key">Null signs in to Azure with the app's Entra identity instead.</param>
    private static IChatClient OpenAi(Uri endpoint, ApiKeyCredential? key, string model)
    {
        var options = new OpenAIClientOptions { Endpoint = endpoint };
        // The token-policy constructor is marked evaluation-only; it is the SDK's documented keyless
        // route to Azure's v1 endpoint, and the alternative is a second SDK (Azure.AI.OpenAI).
#pragma warning disable OPENAI001
        var client = key is null
            ? new OpenAIClient(new BearerTokenPolicy(new DefaultAzureCredential(), "https://cognitiveservices.azure.com/.default"), options)
            : new OpenAIClient(key, options);
#pragma warning restore OPENAI001
        return client.GetChatClient(model).AsIChatClient();
    }
}
