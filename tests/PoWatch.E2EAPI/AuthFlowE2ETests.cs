using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace PoWatch.E2EAPI;

/// <summary>
/// BFF auth flow (Test environment → guest bypass enabled, Microsoft disabled).
/// Uses an HTTPS base address so the Secure session cookie is honoured.
/// </summary>
public sealed class AuthFlowE2ETests(ApiE2EFactory factory) : IClassFixture<ApiE2EFactory>
{
    private HttpClient HttpsClient(bool followRedirects = true) => factory.CreateClient(
        new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = followRedirects,
            HandleCookies = true
        });

    [DockerFact]
    public async Task Config_reports_guest_only_in_test_environment()
    {
        var client = HttpsClient();
        var config = await client.GetFromJsonAsync<ConfigResponse>("/auth/config");

        Assert.NotNull(config);
        Assert.True(config!.GuestEnabled);
        Assert.False(config.MicrosoftEnabled);
        Assert.Equal("Test", config.Environment);
    }

    [DockerFact]
    public async Task Guest_login_issues_a_session_cookie_that_authenticates_me()
    {
        var client = HttpsClient(followRedirects: false); // capture the 302 + its Set-Cookie

        var before = await client.GetFromJsonAsync<MeResponse>("/auth/me");
        Assert.False(before!.IsAuthenticated);

        var login = await client.GetAsync("/auth/login/fake?returnUrl=/");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode); // 302 → returnUrl, session cookie set

        var after = await client.GetFromJsonAsync<MeResponse>("/auth/me");
        Assert.True(after!.IsAuthenticated);
        Assert.Equal("Guest", after.Name);
    }

    private sealed record ConfigResponse(bool MicrosoftEnabled, bool GuestEnabled, string Environment);
    private sealed record MeResponse(bool IsAuthenticated, string? Name, string[]? Roles);
}
