using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
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
    private static readonly ConditionalWeakTable<IPage, ConcurrentQueue<string>> PageErrors = new();

    public static void ClearErrors(this IPage page)
    {
        if (PageErrors.TryGetValue(page, out var errors))
            errors.Clear();
    }

    public static void ThrowIfErrors(this IPage page)
    {
        if (PageErrors.TryGetValue(page, out var errors) && !errors.IsEmpty)
        {
            throw new InvalidOperationException(
                "E2EUI detected browser console/network errors:\n - " + string.Join("\n - ", errors));
        }
    }

    /// <remarks>The caller must dispose the returned page to close its isolated browser context.</remarks>
    /// <param name="user">A distinct guest identity, for tests that need a user with no history.</param>
    public static async Task<IPage> SignedInAsync(IBrowser browser, string route = "/", string? user = null)
    {
        var page = await browser.NewPageAsync(new()
        {
            IgnoreHTTPSErrors = true,
            ViewportSize = new() { Width = 1600, Height = 1000 }
        });
        var errors = new ConcurrentQueue<string>();
        PageErrors.AddOrUpdate(page, errors);
        page.PageError += (_, error) => errors.Enqueue($"Page error: {error}");
        page.Console += (_, message) =>
        {
            if (message.Type == "error") errors.Enqueue($"Console: {message.Text}");
        };
        page.RequestFailed += (_, request) =>
        {
            if (request.Failure?.Contains("ERR_ABORTED", StringComparison.Ordinal) != true)
                errors.Enqueue($"Request failed: {request.Url}: {request.Failure}");
        };
        page.Response += (_, response) =>
        {
            if (response.Status >= 400 && !response.Url.EndsWith(".map", StringComparison.OrdinalIgnoreCase))
                errors.Enqueue($"HTTP {response.Status}: {response.Url}");
        };

        try
        {
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

    /// <summary>System status (/diag) has no section key (it is reached from Settings): load it and wait for it.</summary>
    public static async Task GoToSystemAsync(this IPage page)
    {
        await page.GotoAsync($"{PlaywrightFixture.BaseUrl}/diag");
        await Assertions.Expect(page.GetByTestId("health-overall")).ToBeVisibleAsync(new() { Timeout = BootTimeoutMs });
    }

    /// <summary>/diag's signed-in operator details are three tabs (Server, Engine, Pipeline); this opens one by its label.</summary>
    public static Task OpenSystemTabAsync(this IPage page, string tab) =>
        page.Locator("[data-test=system-tabs] [role=tab]").Filter(new() { HasText = tab }).ClickAsync();

    /// <summary>The active key in the header names the section (there is no separate page title).</summary>
    public static Task ExpectSectionAsync(this IPage page, string section, int timeoutMs = 5_000) =>
        Assertions.Expect(page.GetByTestId($"nav-{section.ToLowerInvariant()}"))
            .ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bactive\b"), new() { Timeout = timeoutMs });

    /// <summary>Settings has no gear (owner, 2026-11-23): it opens the way Ctrl+K does, on its Recording tab.</summary>
    public static async Task OpenSettingsAsync(this IPage page)
    {
        await page.EvaluateAsync("Blazor.navigateTo('settings')");
        await page.WaitForURLAsync("**/settings");
    }

    /// <summary>The demo scene has no button (owner, 2026-11-23): it starts the way Ctrl+K does, ?start=demo.</summary>
    public static Task StartDemoAsync(this IPage page) => page.EvaluateAsync("Blazor.navigateTo('?start=demo')");

    /// <summary>Opens Settings at one of its tabs: recording, people or account (look, alerts, sharing and your data).</summary>
    public static async Task OpenSettingsAsync(this IPage page, string section)
    {
        await page.OpenSettingsAsync();
        await page.GetByTestId($"settings-tab-{section}").ClickAsync();
    }

    /// <summary>
    /// Picks an option of a RadzenDropDown by its key: opens the dropdown, then clicks the option tagged
    /// <c>data-value</c> (Layout/PickOption) in that dropdown's own popup.
    /// </summary>
    public static async Task PickAsync(this ILocator dropdown, string value)
    {
        // Radzen renders each dropdown's list as "popup-{dropdown id}"; aim at that one, not any open popup
        // (another dropdown's list may still be closing).
        var id = await dropdown.GetAttributeAsync("id");
        await dropdown.ClickAsync();
        await dropdown.Page.Locator($"[id=\"popup-{id}\"] [data-value=\"{value}\"]").ClickAsync();
    }

    /// <summary>The key a RadzenDropDown shows as chosen (its <c>data-value</c> attribute).</summary>
    public static Task ExpectPickedAsync(this ILocator dropdown, string value, int timeoutMs = 5_000) =>
        Assertions.Expect(dropdown).ToHaveAttributeAsync("data-value", value, new() { Timeout = timeoutMs });
}
