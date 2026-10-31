using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace PoWatch.E2EUI;

/// <summary>
/// Shared setup for UI tests: a signed-in page on a booted Blazor app.
/// <para>
/// The wait matters. A WASM cold start downloads and boots the .NET runtime, which takes far longer
/// than Playwright's 5 s default expect timeout — the original single smoke test asserted straight
/// after <c>NetworkIdle</c> and failed on any cold instance. Every test here waits for the navbar,
/// which only exists once the app has actually rendered.
/// </para>
/// </summary>
internal static class PoWatchPage
{
    /// <summary>Generous enough for a cold WASM boot on a slow host.</summary>
    private const int BootTimeoutMs = 60_000;

    /// <remarks>The caller must dispose the returned page to close its isolated browser context.</remarks>
    /// <param name="user">A distinct guest identity, for tests that need a user with no history.</param>
    /// <param name="advanced">Start with Settings' Advanced switch on (it is off for a new browser).</param>
    public static async Task<IPage> SignedInAsync(IBrowser browser, string route = "/", string? user = null, bool advanced = false)
    {
        var page = await browser.NewPageAsync(new()
        {
            IgnoreHTTPSErrors = true,
            ViewportSize = new() { Width = 1600, Height = 1000 }
        });
        var errors = new ConcurrentQueue<string>();
        page.PageError += (_, error) => errors.Enqueue($"Page error: {error}");
        page.Console += (_, message) =>
        {
            if (message.Type == "error") errors.Enqueue($"Console: {message.Text}");
        };
        page.RequestFailed += (_, request) => errors.Enqueue($"Request failed: {request.Url}: {request.Failure}");
        page.Response += (_, response) =>
        {
            if (response.Status >= 400) errors.Enqueue($"HTTP {response.Status}: {response.Url}");
        };

        try
        {
            // The first-run tour sits over a corner of the page; tests start as a returning browser.
            await page.AddInitScriptAsync("localStorage.setItem('powatch:tour', 'done')");
            if (advanced) await page.AddInitScriptAsync("localStorage.setItem('powatch:advanced', 'on')");

            // BFF auth: sign in first to set the session cookie before returning to the app.
            await page.GotoAsync(
                $"{PlaywrightFixture.BaseUrl}/auth/login/fake?returnUrl={Uri.EscapeDataString(route)}"
                + (user is null ? string.Empty : $"&user={Uri.EscapeDataString(user)}"));

            await Assertions.Expect(page.GetByTestId("app-navbar"))
                .ToBeVisibleAsync(new() { Timeout = BootTimeoutMs });

            return page;
        }
        catch (PlaywrightException ex)
        {
            var diagnostics = string.Join(Environment.NewLine, errors.TakeLast(30));
            var url = page.Url;
            await page.CloseAsync();
            throw new InvalidOperationException($"Blazor startup failed at {url}.{Environment.NewLine}{diagnostics}", ex);
        }
    }

    /// <summary>
    /// Headers that make a <c>page.APIRequest</c> call act as <paramref name="user"/>. APIRequest does not
    /// send the page's sign-in cookie, so without them the Dev/Test fake auth treats the call as "guest".
    /// </summary>
    public static Dictionary<string, string> As(string user) => new() { ["X-Fake-User"] = user };

    /// <summary>Navigate within the booted app and wait for its section key to light up.</summary>
    public static async Task GoToAsync(this IPage page, string route, string expectedSection)
    {
        await page.GotoAsync($"{PlaywrightFixture.BaseUrl}{route}");
        await page.ExpectSectionAsync(expectedSection, BootTimeoutMs);
    }

    /// <summary>System has no section key (it is reached from Settings): load it and wait for its first panel.</summary>
    public static async Task GoToSystemAsync(this IPage page)
    {
        await page.GotoAsync($"{PlaywrightFixture.BaseUrl}/system");
        await Assertions.Expect(page.GetByTestId("health-overall")).ToBeVisibleAsync(new() { Timeout = BootTimeoutMs });
    }

    /// <summary>System is three tabs (Connections, Engine, Pipeline); this opens one by its label.</summary>
    public static Task OpenSystemTabAsync(this IPage page, string tab) =>
        page.Locator("[data-test=system-tabs] [role=tab]").Filter(new() { HasText = tab }).ClickAsync();

    /// <summary>The active key in the header names the section (there is no separate page title).</summary>
    public static Task ExpectSectionAsync(this IPage page, string section, int timeoutMs = 5_000) =>
        Assertions.Expect(page.GetByTestId($"nav-{section.ToLowerInvariant()}"))
            .ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bactive\b"), new() { Timeout = timeoutMs });

    /// <summary>Theme, captions and sign-out live on the Settings page, behind the header's gear.</summary>
    public static Task OpenSettingsAsync(this IPage page) => page.GetByTestId("settings-menu").ClickAsync();
}
