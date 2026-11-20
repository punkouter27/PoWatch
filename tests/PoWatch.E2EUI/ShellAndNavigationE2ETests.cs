using Microsoft.Playwright;

namespace PoWatch.E2EUI;

/// <summary>
/// The app shell: chrome that must be present on every page, and navigation between them.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
public sealed class ShellAndNavigationE2ETests(PlaywrightFixture fixture)
{
    [E2EFact]
    public async Task The_terminal_shell_renders_header_and_keys_and_the_keys_navigate()
    {
        await using var page = await PoWatchPage.SignedInAsync(fixture.Browser);

        await Assertions.Expect(page.GetByTestId("term-header")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("app-navbar")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("session-status")).ToContainTextAsync("STANDBY");
        // The Po shell: the name upper-left, the signed-in email and the gear (a link to /settings) upper-right.
        await Assertions.Expect(page.GetByTestId("header-title")).ToHaveTextAsync("PoWatch");
        await Assertions.Expect(page.GetByTestId("header-email")).ToContainTextAsync("@");
        await Assertions.Expect(page.GetByTestId("settings-menu")).ToHaveAttributeAsync("href", "/settings");
        // The build follows you to every page as v{YYMMDD}, so a bug report can name the build it came from.
        await Assertions.Expect(page.Locator(".po-version")).ToHaveCountAsync(1);
        await Assertions.Expect(page.GetByTestId("build-stamp-corner")).ToHaveTextAsync(new System.Text.RegularExpressions.Regex(@"^v\d{6}$"));

        await page.GetByTestId("nav-journal").ClickAsync();
        await page.ExpectSectionAsync("JOURNAL");
        await page.GetByTestId("nav-live").ClickAsync();
        await page.ExpectSectionAsync("LIVE");
        await page.AssertNoBlazorErrorAsync();
    }

    [E2EFact]
    public async Task Going_back_and_forth_between_pages_does_not_grow_the_js_heap()
    {
        await using var page = await PoWatchPage.SignedInAsync(fixture.Browser);
        var cdp = await page.Context.NewCDPSessionAsync(page);

        async Task RoundTripsAsync(int count)
        {
            for (var i = 0; i < count; i++)
            {
                await page.GetByTestId("nav-journal").ClickAsync();
                await page.ExpectSectionAsync("JOURNAL");
                await page.GetByTestId("nav-live").ClickAsync();
                await page.ExpectSectionAsync("LIVE");
            }
            await page.WaitForTimeoutAsync(500);
        }

        async Task<double> UsedHeapAsync()
        {
            await cdp.SendAsync("HeapProfiler.collectGarbage");
            var usage = await cdp.SendAsync("Runtime.getHeapUsage");
            return usage!.Value.GetProperty("usedSize").GetDouble();
        }

        // Warm up first: the first visits load scripts and caches that stay on purpose.
        await RoundTripsAsync(3);
        var before = await UsedHeapAsync();
        await RoundTripsAsync(10);
        var after = await UsedHeapAsync();

        // Pages that leave listeners, canvases or WebGL contexts behind grow by megabytes over ten
        // round trips; a little noise from the runtime is fine.
        Assert.True(after < before * 1.25 + 2_000_000, $"JS heap grew from {before:N0} to {after:N0} bytes over ten round trips.");
        await page.AssertNoBlazorErrorAsync();
    }

    [E2EFact]
    public async Task Every_route_renders_with_its_section_title()
    {
        foreach (var (route, heading) in new (string route, string heading)[]
        {
            ("/", "LIVE"),
            ("/journal", "JOURNAL"),
            ("/journal/2026-01-01", "JOURNAL"),
            ("/journal?view=week", "JOURNAL"),
        })
        {
            await using var page = await PoWatchPage.SignedInAsync(fixture.Browser);

            await page.GoToAsync(route, heading);
            await page.AssertNoBlazorErrorAsync();
        }
    }

    [E2EFact]
    public async Task Ask_downloads_and_watch_rules_are_there_from_the_start()
    {
        await using var page = await PoWatchPage.SignedInAsync(fixture.Browser);

        await Assertions.Expect(page.GetByTestId("stats-ask")).ToBeHiddenAsync();
        await page.GetByTestId("ask-toggle").ClickAsync();
        await Assertions.Expect(page.GetByTestId("stats-ask")).ToBeVisibleAsync();
        await page.GetByTestId("ask-toggle").ClickAsync();
        await Assertions.Expect(page.GetByTestId("stats-ask")).ToBeHiddenAsync();
        await Assertions.Expect(page.GetByTestId("session-health")).ToHaveAttributeAsync("href", "system");
        await Assertions.Expect(page.GetByTestId("live-metrics").Locator("table")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("live-pipeline")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("metric-motion")).ToContainTextAsync("Available while observing");

        // Weather sits with Recording, rules and shared links under Alerts, the rest under Advanced; there is no mode to switch.
        await page.OpenSettingsAsync("recording");
        await Assertions.Expect(page.GetByTestId("weather-place")).ToBeVisibleAsync();
        await page.GetByTestId("settings-tab-alerts").ClickAsync();
        await Assertions.Expect(page.GetByTestId("watch-rules")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("advanced-toggle")).ToHaveCountAsync(0);

        // Reset puts every browser choice back (light or dark is the device's, not a choice).
        await page.GetByTestId("settings-tab-advanced").ClickAsync();
        await page.GetByTestId("settings-reset").ClickAsync();
        await Assertions.Expect(page.Locator("html")).Not.ToHaveAttributeAsync("data-theme", new System.Text.RegularExpressions.Regex("."));
        await page.AssertNoBlazorErrorAsync();
    }

    [E2EFact]
    public async Task An_unknown_route_offers_a_way_back_instead_of_dead_ending()
    {
        await using var page = await PoWatchPage.SignedInAsync(fixture.Browser);

        await page.GotoAsync($"{PlaywrightFixture.BaseUrl}/no-such-page");

        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Page not found" }))
            .ToBeVisibleAsync(new() { Timeout = 30000 });
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Back to Live" }))
            .ToBeVisibleAsync();
    }

    [E2EFact]
    public async Task Light_and_dark_follow_the_device_with_no_switch()
    {
        await using var page = await PoWatchPage.SignedInAsync(fixture.Browser);
        const string Background = "() => getComputedStyle(document.body).backgroundColor";

        await Assertions.Expect(page.GetByTestId("theme-toggle")).ToHaveCountAsync(0);
        await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Light });
        var light = await page.EvaluateAsync<string>(Background);
        await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Dark });
        var dark = await page.EvaluateAsync<string>(Background);

        Assert.Equal("rgb(246, 247, 251)", light); // --po-bg, light
        Assert.Equal("rgb(15, 17, 23)", dark);     // --po-bg, dark
    }

    [E2EFact]
    public async Task The_skip_link_is_the_first_thing_a_keyboard_user_reaches()
    {
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
    [E2EFact]
    public async Task Every_link_and_button_works_without_errors_or_layout_jumps()
    {
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

        foreach (var route in new[] { "/", "/journal", "/system" })
        {
            await page.GotoAsync($"{PlaywrightFixture.BaseUrl}{route}");
            await Assertions.Expect(page.GetByTestId("app-navbar")).ToBeVisibleAsync(new() { Timeout = 60_000 });
            await page.WaitForTimeoutAsync(3_000);
            var cls = await page.EvaluateAsync<double>("() => window.__cls");
            Assert.True(cls < 0.25, $"{route} shifted its layout by {cls:0.000} while loading (CLS)");
            // Journal redirects to today's page; that is the page whose buttons are pressed.
            var home = page.Url;

            // Some buttons navigate (Journal's ◀ / Today / period keys move to another day's page). Pressing by
            // index on the page that comes next pointed past its shorter button list, and the click waited out
            // its 5 s for a button that would never appear. Each press therefore starts from this route's page,
            // and the list is counted afresh every time.
            for (var i = 0; ; i++)
            {
                if (page.Url != home)
                {
                    await page.GotoAsync(home);
                    await Assertions.Expect(page.GetByTestId("app-navbar")).ToBeVisibleAsync(new() { Timeout = 60_000 });
                    await page.WaitForTimeoutAsync(1_000);
                }
                var buttons = page.Locator("main button:visible");
                if (i >= await buttons.CountAsync()) break;
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
