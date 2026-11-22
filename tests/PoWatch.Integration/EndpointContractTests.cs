using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using PoWatch.Shared.Models;

namespace PoWatch.Integration;

/// <summary>
/// Shape-and-status coverage for the endpoints the client depends on. These are the contracts that
/// break silently: a route that starts 404-ing, a payload that stops round-tripping, a validation
/// gap that lets a malformed body through.
/// </summary>
public sealed class EndpointContractTests(AzuriteWebApplicationFactory factory)
    : IClassFixture<AzuriteWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [DockerFact]
    public async Task Read_endpoints_answer_successfully_and_the_api_ones_with_json()
    {
        foreach (var route in new[] { "/api/sessions", "/api/regulars", "/api/stats/presence?range=today", "/api/diag/runtime", "/auth/me", "/auth/config", "/health", "/api/diag/boot", "/api/diag" })
        {
            var response = await _client.GetAsync(route);

            Assert.True(response.IsSuccessStatusCode, $"GET {route} returned {(int)response.StatusCode} {response.StatusCode}");
            if (route.StartsWith("/api/", StringComparison.Ordinal))
                Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        }
    }

    [DockerFact]
    public async Task Unknown_and_retired_api_routes_are_404s_not_the_spa_fallback()
    {
        foreach (var route in new[] { "/api/definitely-not-a-route", "/api/observer/state", "/api/archives/2026-01-01", "/api/identity/subjects", "/api/blobs/read?blobPath=x.jpg" })
            Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(route)).StatusCode);
    }

    [DockerFact]
    public async Task The_health_document_keeps_its_deploy_gating_shape_and_never_names_the_key_vault()
    {
        // The JSON contract gates every production deploy: status, checks[], and durations.
        var body = await _client.GetStringAsync("/health");
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.True(root.TryGetProperty("status", out _));
        Assert.True(root.TryGetProperty("checks", out var checks));
        Assert.Equal(JsonValueKind.Array, checks.ValueKind);
        Assert.DoesNotContain(".vault.azure.net", body, StringComparison.OrdinalIgnoreCase);
    }

    [DockerFact]
    public async Task A_browser_can_report_an_unhandled_error_to_the_server_log()
    {
        var response = await _client.PostAsJsonAsync("/api/diag/client-error",
            new { message = "Unhandled exception rendering component", stack = new string('x', 20_000), url = "http://localhost/", userAgent = "test" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [DockerFact]
    public async Task Diagnostics_report_storage_and_mask_every_secret()
    {
        var snapshot = await _client.GetFromJsonAsync<DiagnosticsSnapshotDto>("/api/diag/runtime");

        Assert.NotNull(snapshot);
        Assert.Equal("Azurite-OK", snapshot!.StorageConnectionStatus);
        Assert.Contains("...", snapshot.MaskedEndpoint);
        Assert.Contains("...", snapshot.MaskedApiKey);
        Assert.DoesNotContain("DEV-LOCAL-KEY-12345", snapshot.MaskedApiKey, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AccountKey=", snapshot.MaskedEndpoint ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AccountKey=", snapshot.MaskedApiKey ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [DockerFact]
    public async Task The_diag_document_names_each_store_and_service_without_secrets_or_internals()
    {
        var body = await _client.GetStringAsync("/api/diag");
        var report = JsonSerializer.Deserialize<DiagReportDto>(body, JsonSerializerOptions.Web);

        Assert.NotNull(report);
        Assert.Contains(report!.Status, (string[])["Healthy", "Degraded", "Unhealthy"]);
        Assert.Equal("Healthy", Assert.Single(report.Checks, c => c.Group == DiagGroups.Api).Status);
        Assert.Equal("Healthy", Assert.Single(report.Checks, c => c.Group == DiagGroups.Data && c.Label == "Storage").Status);
        // Every external service is a live probe (stubbed here); the ones switched off on purpose are "Off".
        Assert.Equal("Off", Assert.Single(report.Checks, c => c.Name == "sign-in").Status);
        Assert.Equal("Off", Assert.Single(report.Checks, c => c.Name == "key-vault").Status);
        Assert.Equal("Off", Assert.Single(report.Checks, c => c.Name == "ollama").Status);
        Assert.Equal("Off", Assert.Single(report.Checks, c => c.Name == "gemini").Status);
        Assert.Equal("Healthy", Assert.Single(report.Checks, c => c.Name == "azure-openai").Status);
        Assert.Equal("Healthy", Assert.Single(report.Checks, c => c.Name == "weather").Status);
        Assert.All(report.Checks.Where(c => c.Status == "Off"), c => Assert.Equal(0, c.LatencyMs));
        // Off rows never count toward the overall status.
        Assert.Equal("Healthy", report.Status);
        Assert.Matches(@"^(\d{6}|unknown)$", report.Version);
        Assert.False(string.IsNullOrWhiteSpace(report.Environment));
        // Connection strings, keys, URIs, file paths, emails and exception text must never reach an anonymous caller.
        foreach (var secret in new[] { "AccountKey", "DefaultEndpointsProtocol", "devstoreaccount1", "127.0.0.1", "localhost", "http://", "https://",
                     ".vault.azure.net", ".core.windows.net", ":\\", "/home/", "@", "Exception:", "   at ", "TenantId", "ClientSecret" })
            Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);
    }

    [DockerFact]
    public async Task A_service_that_fails_its_live_probe_degrades_the_diag_report()
    {
        await using var failing = new AzuriteWebApplicationFactory { WeatherStatus = HttpStatusCode.InternalServerError };
        await ((IAsyncLifetime)failing).InitializeAsync();
        using var client = failing.CreateClient();

        var report = await client.GetFromJsonAsync<DiagReportDto>("/api/diag", JsonSerializerOptions.Web);

        var weather = Assert.Single(report!.Checks, c => c.Name == "weather");
        Assert.Equal("Degraded", weather.Status);
        Assert.Contains("500", weather.Problem);
        Assert.Equal("Degraded", report.Status);
        await ((IAsyncLifetime)failing).DisposeAsync();
    }

    [DockerFact]
    public async Task Forms_that_break_the_shared_rules_get_a_validation_problem_per_field()
    {
        foreach (var (route, payload, field) in new (string, object, string)[]
        {
            ("/api/ask", new AskRequestDto { Question = "  " }, "Question"),
            ("/api/ask/rule", new RuleDraftRequestDto { Text = new string('a', RuleDraftRequestDto.MaxLength + 1) }, "Text"),
        })
        {
            var response = await _client.PostAsJsonAsync(route, payload);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains($"\"{field}\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        var rename = await _client.PatchAsJsonAsync("/api/regulars/anyone", new RenameRegularRequestDto { Name = new string('b', RenameRegularRequestDto.MaxLength + 1) });
        Assert.Equal(HttpStatusCode.BadRequest, rename.StatusCode);
        Assert.Contains("\"Name\"", await rename.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [DockerFact]
    public async Task A_malformed_json_body_is_rejected_rather_than_crashing_the_host()
    {
        var response = await _client.PostAsync(
            "/api/sessions",
            new StringContent("{ this is not json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
