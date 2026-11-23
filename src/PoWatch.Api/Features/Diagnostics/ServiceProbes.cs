using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Communication.Email;
using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using PoWatch.Api.Hosting;
using PoWatch.Application.Options;
using PoWatch.Infrastructure.Runtime;
using PoWatch.Shared.Models;

namespace PoWatch.Api.Features.Diagnostics;

/// <summary>
/// The service rows of <c>/api/diag</c>: every external service the server really calls is reached
/// over the network here, never just looked up in configuration. Each probe costs nothing (a model
/// listing, a metadata read, a geocoding lookup, a status read of an id that does not exist), and the
/// rows are cached for <see cref="CacheFor"/> so an anonymous visitor pressing Refresh cannot hammer a
/// dependency.
/// <para>
/// A service switched off on purpose (no key, no connection string, a local run without Key Vault or
/// Microsoft sign-in, a local-only model server that is not running here) is <see cref="Off"/>: neutral,
/// and left out of the overall status. Only a service that is meant to be on and does not answer is
/// Degraded or Unhealthy. Detail and Problem are fixed text: never a host, key or exception message.
/// </para>
/// </summary>
public sealed class ServiceProbes(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    IHostEnvironment environment,
    IOptions<FeatureFlagsOptions> flags,
    TimeProvider timeProvider,
    ILogger<ServiceProbes> logger) : IDisposable
{
    /// <summary>Status of a service that is switched off on purpose. Never counts toward the banner.</summary>
    public const string Off = "Off";

    /// <summary>The named HttpClient every probe uses (short timeout, no retries).</summary>
    public const string HttpClientName = "diag-probes";

    internal static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    // One credential for the process: building a DefaultAzureCredential chain per probe is slow.
    private static readonly Lazy<DefaultAzureCredential> s_credential = new(() => new DefaultAzureCredential());

    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DateTimeOffset At, DiagCheckDto[] Rows)? _cache;

    public void Dispose() => _gate.Dispose();

    public async Task<DiagCheckDto[]> RunAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cache is { } cached && timeProvider.GetUtcNow() - cached.At < CacheFor)
                return cached.Rows;

            var rows = await Task.WhenAll(
                Run("sign-in", "Microsoft sign-in", "Lets you sign in to see your own stats.", MicrosoftSignInAsync, cancellationToken),
                Run("key-vault", "Key Vault", "Holds the server's secrets.", KeyVaultAsync, cancellationToken),
                Run("azure-openai", "Azure OpenAI", "Rewords recaps and stories.", AzureOpenAiAsync, cancellationToken),
                Run("gemini", "Gemini", "A second cloud model for recaps and stories.", GeminiAsync, cancellationToken),
                Run("ollama", "Ollama (local model)", "A free local model for recaps and stories.", OllamaAsync, cancellationToken),
                Run("weather", "Weather (Open-Meteo)", "Adds the day's weather to the journal.", WeatherAsync, cancellationToken),
                Run("digest-email", "Digest email", "Sends the weekly digest email.", DigestEmailAsync, cancellationToken),
                Run("telemetry", "Telemetry", "Sends logs and traces to Application Insights.", TelemetryAsync, cancellationToken));

            _cache = (timeProvider.GetUtcNow(), rows);
            return rows;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A probe's verdict.</summary>
    internal readonly record struct Outcome(string Status, string Detail, string? Problem = null)
    {
        public static Outcome Healthy(string detail = "Checked with a live call") => new(nameof(HealthStatus.Healthy), detail);
        public static Outcome Degraded(string problem) => new(nameof(HealthStatus.Degraded), "Answered with a problem", problem);
        public static Outcome Unhealthy(string problem) => new(nameof(HealthStatus.Unhealthy), "Not working", problem);
        public static Outcome SwitchedOff(string detail) => new(Off, detail);
    }

    private delegate Task<Outcome> Probe(HttpClient http, CancellationToken cancellationToken);

    private async Task<DiagCheckDto> Run(string name, string label, string purpose, Probe probe, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        Outcome outcome;
        try
        {
            using var http = httpClientFactory.CreateClient(HttpClientName);
            outcome = await probe(http, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "/api/diag probe {Probe} failed", name);
            outcome = Outcome.Degraded($"Not reachable ({ex.GetType().Name}).");
        }

        // An Off row made no call, so it has no latency to report.
        var latency = outcome.Status == Off ? 0 : Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1);
        return new DiagCheckDto(name, DiagGroups.Service, label, outcome.Status, purpose, outcome.Detail, latency, outcome.Problem);
    }

    private string? Setting(string key) => configuration[key] is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    // ── Microsoft sign-in: reads the sign-in metadata, then asks for a token with the app's client
    //    secret. Entra checks the secret first, so only its "bad/expired secret" codes
    //    (AADSTS7000215/7000222) fail the row; a later refusal (tenant, grant) still proves the secret.
    private async Task<Outcome> MicrosoftSignInAsync(HttpClient http, CancellationToken ct)
    {
        var clientId = Setting("AzureAd:ClientId");
        if (clientId is null)
        {
            return environment.IsProduction()
                ? Outcome.Unhealthy("Microsoft sign-in is not configured, so nobody can sign in.")
                : Outcome.SwitchedOff(flags.Value.DeveloperBypassAuth ? "Off (guest sign-in, local use)" : "Off (not configured here)");
        }

        var instance = (Setting("AzureAd:Instance") ?? "https://login.microsoftonline.com/").TrimEnd('/');
        var tenant = Setting("AzureAd:TenantId") ?? "common";
        using (var metadata = await http.GetAsync($"{instance}/{tenant}/v2.0/.well-known/openid-configuration", ct))
        {
            if (!metadata.IsSuccessStatusCode)
                return Outcome.Unhealthy($"The Microsoft sign-in service answered {(int)metadata.StatusCode}, so nobody can sign in.");
        }

        if (Setting("AzureAd:ClientSecret") is not { } secret)
            return Outcome.Unhealthy("The app's sign-in secret is missing, so sign-in cannot finish.");

        using var token = await http.PostAsync($"{instance}/{tenant}/oauth2/v2.0/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["grant_type"] = "client_credentials",
            ["scope"] = "https://graph.microsoft.com/.default",
        }), ct);
        if (token.IsSuccessStatusCode)
            return Outcome.Healthy("Secret accepted (live call)");

        // The AADSTS number is a public error code, safe to show.
        var codes = await ReadEntraErrorCodesAsync(token, ct);
        if (codes.FirstOrDefault(code => code is 7000215 or 7000222) is var bad and > 0)
            return Outcome.Unhealthy($"Microsoft rejected the app's sign-in secret (AADSTS{bad}), so nobody can sign in.");
        return Outcome.Healthy(codes.Count > 0 ? $"Secret accepted (live call, then AADSTS{codes[0]})" : "Secret accepted (live call)");
    }

    internal static async Task<List<int>> ReadEntraErrorCodesAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return json.RootElement.TryGetProperty("error_codes", out var codes) && codes.ValueKind == JsonValueKind.Array
                ? [.. codes.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.Number).Select(c => c.GetInt32())]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // ── Key Vault: lists one secret NAME with the app's own credential (no value is read).
    private async Task<Outcome> KeyVaultAsync(HttpClient http, CancellationToken ct)
    {
        if (!flags.Value.EnableKeyVault)
            return Outcome.SwitchedOff("Off (FeatureFlags:EnableKeyVault is false; secrets come from app settings)");

        if (!Uri.TryCreate(Setting("KeyVault:Uri"), UriKind.Absolute, out var vaultUri))
            return Outcome.Degraded("Key Vault is switched on but KeyVault:Uri is not set, so no secret is read from it.");

        var client = new SecretClient(vaultUri, s_credential.Value);
        await foreach (var page in client.GetPropertiesOfSecretsAsync(ct).AsPages(pageSizeHint: 1).WithCancellation(ct))
        {
            _ = page;
            break;
        }

        return Outcome.Healthy("Listed secret names (live call)");
    }

    // ── Azure OpenAI: lists the resource's deployments (free) with the same credential the app uses,
    //    its API key or, without one, the app's Entra identity. Not /openai/v1/models: the app's
    //    least-privilege role (Cognitive Services OpenAI User) may chat but not list models (403 there).
    private async Task<Outcome> AzureOpenAiAsync(HttpClient http, CancellationToken ct)
    {
        if (!Uri.TryCreate(Setting("AzureOpenAi:Endpoint"), UriKind.Absolute, out var endpoint))
            return Outcome.SwitchedOff("Off (no endpoint configured)");

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, "openai/deployments?api-version=2023-03-15-preview"));
        if (Setting("AzureOpenAi:ApiKey") is { } key)
        {
            request.Headers.Add("api-key", key);
        }
        else
        {
            var token = await s_credential.Value.GetTokenAsync(new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]), ct);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
        }

        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.OK && Setting("AzureOpenAi:DeploymentName") is { } deployment)
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var found = json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                && data.EnumerateArray().Any(d => d.TryGetProperty("id", out var id) && string.Equals(id.GetString(), deployment, StringComparison.OrdinalIgnoreCase));
            return found
                ? Outcome.Healthy($"Found deployment {deployment} (live call)")
                : Outcome.Degraded($"Azure OpenAI has no deployment named {deployment}, so recaps fall back to plain templates.");
        }

        return response.StatusCode switch
        {
            HttpStatusCode.OK => Outcome.Healthy("Listed deployments (live call)"),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Outcome.Degraded("Azure OpenAI refused the app's credential, so recaps fall back to plain templates."),
            _ => Outcome.Degraded($"Azure OpenAI answered {(int)response.StatusCode}, so recaps may fall back to plain templates."),
        };
    }

    // ── Gemini: lists one model (free). Off unless a key is configured.
    private async Task<Outcome> GeminiAsync(HttpClient http, CancellationToken ct)
    {
        if (Setting("Gemini:ApiKey") is not { } key)
            return Outcome.SwitchedOff("Off (no key configured)");

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://generativelanguage.googleapis.com/v1beta/models?pageSize=1");
        request.Headers.Add("x-goog-api-key", key);
        using var response = await http.SendAsync(request, ct);
        return response.IsSuccessStatusCode
            ? Outcome.Healthy("Listed models (live call)")
            : Outcome.Degraded($"Gemini answered {(int)response.StatusCode}, so it is skipped.");
    }

    // ── Ollama: lists the local server's models. It is a developer's own machine model, so a server on
    //    this host (localhost) that is not running is Off, not a fault; a remote one that fails is Degraded.
    private async Task<Outcome> OllamaAsync(HttpClient http, CancellationToken ct)
    {
        if (!Uri.TryCreate(Setting("Ollama:Endpoint") ?? new OllamaOptions().Endpoint, UriKind.Absolute, out var endpoint))
            return Outcome.SwitchedOff("Off (no endpoint configured)");

        try
        {
            using var response = await http.GetAsync(new Uri(endpoint, "models"), ct);
            if (response.IsSuccessStatusCode)
                return Outcome.Healthy("Listed models (live call)");
            if (!endpoint.IsLoopback)
                return Outcome.Degraded($"The model server answered {(int)response.StatusCode}, so it is skipped.");
        }
        catch (HttpRequestException) when (endpoint.IsLoopback)
        {
            // Nothing listens on this machine: the usual state on the server.
        }

        return Outcome.SwitchedOff("Off (no local model server running on this host)");
    }

    // ── Open-Meteo: one geocoding lookup (free, no key), the same service the journal's weather uses.
    private static async Task<Outcome> WeatherAsync(HttpClient http, CancellationToken ct)
    {
        using var response = await http.GetAsync($"{OpenMeteoWeatherSource.SearchUrl}?name=London&count=1&format=json", ct);
        if (!response.IsSuccessStatusCode)
            return Outcome.Degraded($"Open-Meteo answered {(int)response.StatusCode}, so the journal shows no weather.");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.TryGetProperty("results", out var results) && results.GetArrayLength() > 0
            ? Outcome.Healthy("Looked up a place (live call)")
            : Outcome.Degraded("Open-Meteo answered without results, so the journal may show no weather.");
    }

    // ── Azure Communication Services email: reads the status of a send id that does not exist (free).
    //    ACS checks the key first, so 404 proves the key; 401/403 means it was refused.
    private async Task<Outcome> DigestEmailAsync(HttpClient http, CancellationToken ct)
    {
        if (Setting("Digest:Email:ConnectionString") is not { } connection || Setting("Digest:Email:Sender") is null)
            return Outcome.SwitchedOff("Off (no email service configured)");

        var operation = new EmailSendOperation(Guid.Empty.ToString(), new EmailClient(connection));
        try
        {
            await operation.UpdateStatusAsync(ct);
            return Outcome.Healthy("Key accepted (live call)");
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return Outcome.Healthy("Key accepted (live call)");
        }
        catch (RequestFailedException ex) when (ex.Status is 401 or 403)
        {
            return Outcome.Degraded("The email service refused the app's key, so no digest is sent.");
        }
    }

    // ── Application Insights: an empty batch to the ingestion endpoint (nothing is recorded).
    private async Task<Outcome> TelemetryAsync(HttpClient http, CancellationToken ct)
    {
        if (PoPlatform.ResolveAppInsightsConnectionString(configuration) is not { } connectionString)
            return Outcome.SwitchedOff("Off (no connection string)");

        var ingestion = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2))
            .Where(kv => kv.Length == 2 && kv[0].Equals("IngestionEndpoint", StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv[1])
            .FirstOrDefault() ?? "https://dc.services.visualstudio.com/";

        using var response = await http.PostAsync($"{ingestion.TrimEnd('/')}/v2.1/track", new StringContent("[]", Encoding.UTF8, "application/json"), ct);
        return (int)response.StatusCode < 500
            ? Outcome.Healthy("Ingestion answered (live call)")
            : Outcome.Degraded($"Application Insights answered {(int)response.StatusCode}, so telemetry may be lost.");
    }
}
