using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PoWatch.Shared.Models;

namespace PoWatch.E2EAPI;

/// <summary>The platform around the stats: default-deny auth, sign-in, health and diagnostics.</summary>
public sealed class PlatformE2ETests(ApiE2EFactory factory) : IClassFixture<ApiE2EFactory>
{
    // Every route a signed-out caller may reach. A new public route fails this test until it is
    // added here on purpose. Static files and the Blazor host are public by design and are
    // identified by their metadata, not listed.
    private static readonly string[] AnonymousAllowList =
    [
        "/api/{**rest}",
        "/auth/config",
        "/auth/login/fake",
        "/auth/login/microsoft",
        "/auth/logout",
        "/auth/me",
        "/diag/boot",
        "/health",
        "/health/live",
        "/s/{token}",
        "/s/{token}/card.png",
        "/{*path:nonfile}",
    ];

    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public void Anonymous_surface_is_exactly_the_allow_list()
    {
        var anonymous = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Where(e => !IsHostOrStaticAsset(e))
            .Select(e => "/" + e.RoutePattern.RawText!.TrimStart('/'))
            .Distinct()
            .Order(StringComparer.Ordinal);

        Assert.Equal(AnonymousAllowList, anonymous);
    }

    [Fact]
    public void Every_other_endpoint_requires_a_signed_in_user()
    {
        var policy = factory.Services.GetRequiredService<IOptions<AuthorizationOptions>>().Value.FallbackPolicy;

        Assert.NotNull(policy);
        Assert.Contains(policy.Requirements, r => r is DenyAnonymousAuthorizationRequirement);
    }

    [Fact]
    public async Task An_anonymous_caller_cannot_read_sessions()
    {
        // The API host is default-deny; only /health, /diag/boot and /auth opt out.
        using var anonymous = factory.CreateClient();
        anonymous.DefaultRequestHeaders.Remove("X-Fake-User");

        var response = await anonymous.GetAsync("/api/sessions");

        Assert.True(
            response.IsSuccessStatusCode || response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"Unexpected status {(int)response.StatusCode}");
    }

    [Fact]
    public async Task Operations_endpoints_report_health_boot_and_storage()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Accept.ParseAdd("application/json");
        var health = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        // The deploy reads X-Commit to confirm the new build is the one answering.
        Assert.True(health.Headers.Contains("X-Commit"));
        Assert.Contains("status", await health.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var boot = await _client.GetAsync("/diag/boot");
        Assert.Equal(HttpStatusCode.OK, boot.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(await boot.Content.ReadAsStringAsync()));

        var snapshot = await _client.GetFromJsonAsync<DiagnosticsSnapshotDto>("/api/diagnostics/status");
        Assert.NotNull(snapshot);
        Assert.False(string.IsNullOrWhiteSpace(snapshot!.StorageConnectionStatus));
    }

    [Fact]
    public async Task Sign_in_config_is_public_and_the_guest_bypass_establishes_a_session()
    {
        var config = await _client.GetFromJsonAsync<AuthConfigDto>("/auth/config");
        Assert.NotNull(config);
        Assert.False(string.IsNullOrWhiteSpace(config!.Environment));

        // HTTPS base address: the BFF session cookie is Secure, so it is dropped over plain http.
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        var login = await client.GetAsync("/auth/login/fake?returnUrl=/");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        var me = await client.GetFromJsonAsync<AuthStateDto>("/auth/me");
        Assert.NotNull(me);
        Assert.True(me!.IsAuthenticated);
    }

    private static bool IsHostOrStaticAsset(RouteEndpoint endpoint) =>
        endpoint.RoutePattern.RawText!.TrimStart('/').StartsWith("_framework", StringComparison.Ordinal)
        || endpoint.Metadata.Any(m => m.GetType().Name is "StaticAssetDescriptor" or "ComponentTypeMetadata" or "RootComponentMetadata");
}
