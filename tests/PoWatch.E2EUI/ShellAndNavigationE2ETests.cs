using Microsoft.Playwright;

namespace PoWatch.E2EUI;

/// <summary>
/// The app shell: chrome that must be present on every page, and navigation between them.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
public sealed class ShellAndNavigationE2ETests(PlaywrightFixture fixture)
{
    [Fact]
    public async Task The_terminal_shell_renders_header_and_keys()
    {
        if (PlaywrightFixture.BaseUrl is null) return;
        await using var page = await PoWatchPage.SignedInAsync(fixture.Browser);

        await Assertions.Expect(page.GetByTestId("term-header")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("app-navbar")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("session-status")).ToContainTextAsync("STANDBY");
        await Assertions.Expect(page.GetByTestId("settings-menu")).ToBeVisibleAsync();
        await page.AssertNoBlazorErrorAsync();
    }

    [Fact]
    public async Task Every_route_renders_with_its_section_title()
    {
        foreach (var (route, heading) in new (string route, string heading)[]
        {
            ("/", "LIVE"),
            ("/stories", "STORIES"),
            ("/days/2026-01-01", "DAYS"),
            // History's old addresses land on their new homes.
            ("/history", "STORIES"),
            ("/history/2026-01-01", "DAYS"),
        })
        {
            if (PlaywrightFixture.BaseUrl is null) return;
            await using var page = await PoWatchPage.SignedInAsync(fixture.Browser);

            await page.GoToAsync(route, heading);
            await page.AssertNoBlazorErrorAsync();
        }
    }

    [Fact]
    public async Task The_app_starts_simple_and_Advanced_in_Settings_adds_the_extras()
    {
        if (PlaywrightFixture.BaseUrl is null) return;
        await using var page = await PoWatchPage.SignedInAsync(fixture.Browser);

        await Assertions.Expect(page.GetByTestId("stats-toolbar")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("stats-ask")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("stats-json")).ToHaveCountAsync(0);

        await page.OpenSettingsAsync("alerts");
        await Assertions.Expect(page.GetByTestId("watch-rules")).ToHaveCountAsync(0);
        await page.GetByTestId("settings-tab-look").ClickAsync();
        await page.GetByTestId("advanced-toggle").ClickAsync();
        await Assertions.Expect(page.GetByTestId("advanced-toggle")).ToHaveAttributeAsync("aria-checked", "true");
        await page.GetByTestId("settings-tab-alerts").ClickAsync();
        await Assertions.Expect(page.GetByTestId("watch-rules")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("stats-ask")).ToBeAttachedAsync();
        await Assertions.Expect(page.GetByTestId("stats-json")).ToBeAttachedAsync();

        // Reset puts every browser choice back, power features included.
        await page.GetByTestId("settings-tab-look").ClickAsync();
        await page.GetByTestId("settings-reset").ClickAsync();
        await Assertions.Expect(page.GetByTestId("advanced-toggle")).ToHaveAttributeAsync("aria-checked", "false");
        await Assertions.Expect(page.GetByTestId("stats-ask")).ToHaveCountAsync(0);
        await page.AssertNoBlazorErrorAsync();
    }

    [Fact]
    public async Task The_key_bar_navigates()
    {
        if (PlaywrightFixture.BaseUrl is null) return;
        await using var page = await PoWatchPage.SignedInAsync(fixture.Browser);

        await page.GetByTestId("nav-stories").ClickAsync();
        await page.ExpectSectionAsync("STORIES");

        await page.GetByTestId("nav-days").ClickAsync();
        await page.ExpectSectionAsync("DAYS");

        await page.GetByTestId("nav-live").ClickAsync();
        await page.ExpectSectionAsync("LIVE");
    }

    [Fact]
    public async Task An_unknown_route_offers_a_way_back_instead_of_dead_ending()
    {
        if (PlaywrightFixture.BaseUrl is null) return;
        await using var page = await PoWatchPage.SignedInAsync(fixture.Browser);

        await page.GotoAsync($"{PlaywrightFixture.BaseUrl}/no-such-page");

        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Page not found" }))
            .ToBeVisibleAsync(new() { Timeout = 30000 });
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Back to Live" }))
            .ToBeVisibleAsync();
    }

    [Fact]
    public async Task The_theme_toggle_switches_and_persists_across_a_reload()
    {
        if (PlaywrightFixture.BaseUrl is null) return;
        await using var page = await PoWatchPage.SignedInAsync(fixture.Browser);

        var before = await page.GetAttributeAsync("html", "data-theme");
        await page.OpenSettingsAsync();
        await page.GetByTestId("theme-toggle").ClickAsync();
        await page.WaitForTimeoutAsync(400);
        var after = await page.GetAttributeAsync("html", "data-theme");

        Assert.NotEqual(before, after);

        await page.ReloadAsync();
        await Assertions.Expect(page.GetByTestId("app-navbar")).ToBeVisibleAsync(new() { Timeout = 60000 });
        Assert.Equal(after, await page.GetAttributeAsync("html", "data-theme"));
    }

    [Fact]
    public async Task The_skip_link_is_the_first_thing_a_keyboard_user_reaches()
    {
        if (PlaywrightFixture.BaseUrl is null) return;
        await using var page = await PoWatchPage.SignedInAsync(fixture.Browser);

        // Blazor's FocusOnNavigate moves focus into <main> once the page renders; on a slow host that lands
        // after the navbar shows, and the Tab then starts from inside main. Let it settle, then start where a
        // keyboard user arriving on the page does: the top of the document.
        await page.WaitForFunctionAsync("() => document.activeElement?.tagName === 'MAIN'", null, new() { Timeout = 10_000 });
        await page.EvaluateAsync("() => { document.body.tabIndex = -1; document.body.focus(); document.body.removeAttribute('tabindex'); }");
        await page.Keyboard.PressAsync("Tab");
        var focused = await page.EvaluateAsync<string>("() => document.activeElement?.textContent ?? ''");

        Assert.Contains("Skip to main content", focused, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every in-app link opens a working page, and every harmless button on every page can be pressed
    /// without an error or a sideways scroll; the pages settle without layout jumps (CLS).
    /// </summary>
    [Fact]
    public async Task Every_link_and_button_works_without_errors_or_layout_jumps()
    {
        if (PlaywrightFixture.BaseUrl is null) return;
        await using var page = await PoWatchPage.SignedInAsync(fixture.Browser);
        await page.AddInitScriptAsync("""
            window.__cls = 0;
            new PerformanceObserver(list => {
                for (const e of list.getEntries()) if (!e.hadRecentInput) window.__cls += e.value;
            }).observe({ type: 'layout-shift', buffered: true });
            """);
        // Buttons that start, stop, delete, merge, share, reset or sign out change state other tests rely on.
        var unsafeLabel = new System.Text.RegularExpressions.Regex(
            "start|stop|delete|merge|share|reset|sign out|keep|demo|clear|send|ask|save|remove|take down|download|explain|pdf",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (var route in new[] { "/", "/stories", "/days", "/system" })
        {
            await page.GotoAsync($"{PlaywrightFixture.BaseUrl}{route}");
            await Assertions.Expect(page.GetByTestId("app-navbar")).ToBeVisibleAsync(new() { Timeout = 60_000 });
            await page.WaitForTimeoutAsync(3_000);
            var cls = await page.EvaluateAsync<double>("() => window.__cls");
            Assert.True(cls < 0.25, $"{route} shifted its layout by {cls:0.000} while loading (CLS)");

            var buttons = page.Locator("main button:visible");
            var count = await buttons.CountAsync();
            for (var i = 0; i < count; i++)
            {
                var button = buttons.Nth(i);
                if (!await button.IsVisibleAsync() || !await button.IsEnabledAsync()) continue;
                var label = $"{await button.InnerTextAsync()} {await button.GetAttributeAsync("aria-label")} {await button.GetAttributeAsync("title")}";
                if (unsafeLabel.IsMatch(label)) continue;

                await button.ClickAsync(new() { Timeout = 5_000 });
                await page.Keyboard.PressAsync("Escape");
                await page.AssertNoBlazorErrorAsync();
                var sideways = await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth + 1");
                Assert.False(sideways, $"{route}: pressing \"{label.Trim()}\" made the page scroll sideways");
            }
        }

        var links = await page.EvaluateAsync<string[]>("""
            () => [...new Set([...document.querySelectorAll('header a[href], nav a[href]')]
                .map(a => a.getAttribute('href'))
                .filter(h => h !== null && !h.startsWith('http') && !h.startsWith('#') && !h.includes('auth/') && !h.includes('/api/')))]
            """);
        Assert.NotEmpty(links);
        foreach (var href in links)
        {
            await page.GotoAsync($"{PlaywrightFixture.BaseUrl}/{href.TrimStart('/')}");
            await Assertions.Expect(page.GetByTestId("app-navbar")).ToBeVisibleAsync(new() { Timeout = 60_000 });
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Page not found" })).ToHaveCountAsync(0);
            await page.AssertNoBlazorErrorAsync();
        }
    }
}
