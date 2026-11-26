using Microsoft.Playwright;

namespace PoWatch.E2EUI;

/// <summary>
/// Locks in the viewport-fit rules that the design system says every screen must obey:
/// <list type="bullet">
///   <item><description>Pages with internal scrolling (Live, History) don't extend the body past the viewport.</description></item>
///   <item><description>On narrow viewports every section key stays visible without widening the page.</description></item>
///   <item><description>A camera session keeps sampling when you leave the Live page.</description></item>
/// </list>
/// These rules used to live only as a docstring in the design tokens. The audit pass moved them
/// into CI: a regression that re-introduces a layout that exceeds the viewport on a360×640 phone
/// now fails a test rather than going unnoticed.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
public sealed class ViewportFitE2ETests
{
    private readonly PlaywrightFixture _fixture;

    public ViewportFitE2ETests(PlaywrightFixture fixture) => _fixture = fixture;

    public static IEnumerable<object[]> PhoneAndDesktopViewports()
    {
        // 360×640 = the lowest common denominator Android/iOS portrait.
        // 768×1024 = iPad portrait — middle tier where the layout must still work.
        // 1440×900 = typical desktop.
        yield return new object[] { 360, 640 };
        yield return new object[] { 768, 1024 };
        yield return new object[] { 1440, 900 };
    }

    [E2EFact]
    public async Task Live_Journal_and_System_bodies_fit_within_every_viewport()
    {
        foreach (var (name, open) in new (string, Func<IPage, Task>)[]
        {
            // Live opens on sign-in; its panels need a beat longer to settle than the other pages.
            ("Live", page => page.WaitForTimeoutAsync(1000)),
            ("Journal", async page => { await page.GoToAsync("/journal", "JOURNAL"); await page.WaitForTimeoutAsync(500); }),
            ("System", async page => { await page.GoToSystemAsync(); await page.WaitForTimeoutAsync(500); }),
        })
        {
            foreach (var scenario in PhoneAndDesktopViewports())
            {
                await using var page = await PoWatchPage.SignedInAsync(_fixture.Browser);
                await page.SetViewportSizeAsync((int)scenario[0], (int)scenario[1]);
                await open(page);

                await AssertBodyFitsViewportAsync(page, $"{name} at {scenario[0]}×{scenario[1]}");
                await page.AssertNoBlazorErrorAsync();
            }
        }
    }

    [E2EFact]
    public async Task On_a_phone_the_bottom_bar_reaches_every_section_and_diag_works_signed_out()
    {
        await using var page = await PoWatchPage.SignedInAsync(_fixture.Browser, "/");
        await page.SetViewportSizeAsync(360, 640);
        await page.WaitForTimeoutAsync(500);

        // Every primary section stays reachable from the bottom tab bar (the header's key bar hides at this
        // width), and the page itself never scrolls sideways.
        await Assertions.Expect(page.GetByTestId("nav-journal")).ToBeHiddenAsync();
        foreach (var section in new[] { "live", "journal", "system" })
            await Assertions.Expect(page.GetByTestId($"bottom-nav-{section}")).ToBeInViewportAsync();
        var pageOverflow = await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth + 1");
        Assert.False(pageOverflow, "The page scrolls horizontally on a 360 px phone.");

        await page.GetByTestId("bottom-nav-journal").ClickAsync();
        await page.ExpectSectionAsync("JOURNAL");
        await Assertions.Expect(page.GetByTestId("bottom-nav-journal")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bactive\b"));
        await page.GetByTestId("bottom-nav-system").ClickAsync();
        await Assertions.Expect(page.GetByTestId("health-overall")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await page.AssertNoBlazorErrorAsync();

        // /diag answers signed out too, on a phone and a laptop, with no console errors.
        await using var anonymous = await _fixture.Browser.NewPageAsync(new() { IgnoreHTTPSErrors = true });
        var errors = new List<string>();
        anonymous.Console += (_, m) => { if (m.Type == "error") errors.Add(m.Text); };
        anonymous.PageError += (_, e) => errors.Add(e);
        foreach (var (width, height) in new[] { (390, 844), (1366, 900) })
        {
            await anonymous.SetViewportSizeAsync(width, height);
            await anonymous.GotoAsync($"{PlaywrightFixture.BaseUrl}/diag");
            await Assertions.Expect(anonymous.GetByTestId("diag-banner")).ToBeVisibleAsync(new() { Timeout = 60_000 });
            await Assertions.Expect(anonymous.GetByTestId("health-check")).Not.ToHaveCountAsync(0);
            Assert.EndsWith("/diag", anonymous.Url, StringComparison.Ordinal);
            Assert.False(await anonymous.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth + 1"),
                $"/diag scrolls sideways at {width} px");
        }
        Assert.Empty(errors);
    }

    [E2EFact]
    public async Task A_camera_session_samples_the_fake_camera_and_survives_navigation()
    {
        await using var page = await PoWatchPage.SignedInAsync(_fixture.Browser);
        await page.SetViewportSizeAsync(1440, 900);

        // Chromium's fake camera (see PlaywrightFixture); captions off (Live's models strip) so no vision model downloads.
        await page.GetByTestId("nav-live").ClickAsync();
        await page.GetByTestId("ai-config-toggle").ClickAsync();
        await page.GetByTestId("ai-caption-model").PickAsync("off");
        await page.GetByTestId("start-camera").ClickAsync();
        await Assertions.Expect(page.GetByTestId("session-status")).ToContainTextAsync("OBSERVING", new() { Timeout = 15_000 });

        // Leaving Live must not stop sensing: the camera element lives in the layout, and System
        // (reached from Settings) shows the frame rate this browser is sampling at.
        await page.OpenSettingsAsync("advanced");
        await page.GetByTestId("nav-system").ClickAsync();
        await page.OpenSystemTabAsync("Pipeline");
        await Assertions.Expect(page.GetByTestId("stat-pixel-hz")).Not.ToContainTextAsync("0.0", new() { Timeout = 30_000 });
        await page.GetByTestId("nav-live").ClickAsync();
        await Assertions.Expect(page.GetByTestId("session-status")).ToContainTextAsync("OBSERVING");

        await page.GetByTestId("header-stop").ClickAsync();
        await Assertions.Expect(page.GetByTestId("session-status")).ToContainTextAsync("STANDBY");
        await page.AssertNoBlazorErrorAsync();
    }

    [E2EFact]
    public async Task Live_fills_the_window_so_no_panel_scrolls()
    {
        // Its own user: the nine panels are all shown, whatever another test left the guest's layout as.
        var user = $"fit-{Guid.NewGuid():N}";
        foreach (var (width, height) in new[] { (1440, 900), (1366, 768), (1280, 720), (1152, 700) })
        {
            await using var page = await PoWatchPage.SignedInAsync(_fixture.Browser, user: user);
            await page.SetViewportSizeAsync(width, height);
            await page.WaitForTimeoutAsync(1200);
            await Assertions.Expect(page.GetByTestId("live-grid")).ToBeVisibleAsync();

            // [main's own scroll, the worst panel's overflow, how many panels, how far the last one hangs past
            // the window]. The board is meant to be the window: all nine on screen, nothing scrolling.
            var fit = await page.EvaluateAsync<int[]>("""
                () => {
                    const main = document.querySelector('#main-content');
                    const bodies = [...document.querySelectorAll('.dash-grid .term-panel-body')];
                    const worst = bodies.reduce((m, b) => Math.max(m, b.scrollHeight - b.clientHeight), 0);
                    const slots = [...document.querySelectorAll('.live-slot')];
                    const lowest = Math.max(...slots.map(s => Math.round(s.getBoundingClientRect().bottom)));
                    return [main.scrollHeight - main.clientHeight, worst, slots.length, lowest - innerHeight];
                }
                """);
            Assert.True(fit[0] <= 1, $"Live at {width}×{height}: the page scrolls by {fit[0]} px.");
            Assert.True(fit[1] <= 1, $"Live at {width}×{height}: a panel scrolls by {fit[1]} px.");
            Assert.Equal(9, fit[2]);
            Assert.True(fit[3] <= 0, $"Live at {width}×{height}: the last panel runs {fit[3]} px past the window.");
            await page.AssertNoBlazorErrorAsync();
        }
    }

    private static async Task AssertBodyFitsViewportAsync(IPage page, string where)
    {
        var size = await page.EvaluateAsync<int[]>("() => [document.documentElement.clientWidth, document.documentElement.clientHeight, document.documentElement.scrollHeight]");
        // Some pages legitimately scroll a panel internally (Live panels, History day
        // grid). What we forbid is the *body* scrolling — that means a panel grew taller than
        // its container and pushed the chrome out of view. Allow a 2 px tolerance for sub-pixel
        // rounding between browsers.
        var overflow = size[2] - size[1];
        Assert.True(
            overflow <= 2,
            $"{where}: body exceeds viewport by {overflow} px (viewport={size[1]}, scrollHeight={size[2]}, width={size[0]}). " +
            "A page that scrolls the body pushes the chrome out of view on a kiosk — re-check the page's overflow contract.");
    }

}
