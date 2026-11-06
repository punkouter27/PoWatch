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

    [Fact]
    public async Task Read_endpoints_answer_successfully_and_the_api_ones_with_json()
    {
        foreach (var route in new[] { "/api/sessions", "/api/regulars", "/api/stats/presence?range=today", "/api/diagnostics/status", "/auth/me", "/auth/config", "/health", "/diag/boot" })
        {
            var response = await _client.GetAsync(route);

            Assert.True(response.IsSuccessStatusCode, $"GET {route} returned {(int)response.StatusCode} {response.StatusCode}");
            if (route.StartsWith("/api/", StringComparison.Ordinal))
                Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        }
    }

    [Fact]
    public async Task Unknown_and_retired_api_routes_are_404s_not_the_spa_fallback()
    {
        foreach (var route in new[] { "/api/definitely-not-a-route", "/api/observer/state", "/api/archives/2026-01-01", "/api/identity/subjects", "/api/blobs/read?blobPath=x.jpg" })
            Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(route)).StatusCode);
    }

    [Fact]
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

    [Fact]
    public async Task A_browser_can_report_an_unhandled_error_to_the_server_log()
    {
        var response = await _client.PostAsJsonAsync("/api/diagnostics/client-error",
            new { message = "Unhandled exception rendering component", stack = new string('x', 20_000), url = "http://localhost/", userAgent = "test" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Diagnostics_report_storage_and_mask_every_secret()
    {
        var snapshot = await _client.GetFromJsonAsync<DiagnosticsSnapshotDto>("/api/diagnostics/status");

        Assert.NotNull(snapshot);
        Assert.Equal("Azurite-OK", snapshot!.StorageConnectionStatus);
        Assert.Contains("...", snapshot.MaskedEndpoint);
        Assert.Contains("...", snapshot.MaskedApiKey);
        Assert.DoesNotContain("DEV-LOCAL-KEY-12345", snapshot.MaskedApiKey, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AccountKey=", snapshot.MaskedEndpoint ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AccountKey=", snapshot.MaskedApiKey ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_malformed_json_body_is_rejected_rather_than_crashing_the_host()
    {
        var response = await _client.PostAsync(
            "/api/sessions",
            new StringContent("{ this is not json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
