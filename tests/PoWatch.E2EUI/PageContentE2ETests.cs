using Microsoft.Playwright;

namespace PoWatch.E2EUI;

/// <summary>
/// What each page actually shows: the controls people use, and the consistency rules that broke
/// silently before — one person under two names, a heading that contradicts the nav.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
public sealed class PageContentE2ETests(PlaywrightFixture fixture)
{
    [Fact]
    public async Task The_live_page_shows_the_camera_beside_its_narration_and_the_numbers_one_tab_away()
    {
        if (PlaywrightFixture.BaseUrl is null) return;
        await using var page = await PoWatchPage.SignedInAsync(fixture.Browser);

        foreach (var panel in new[] { "live-camera", "live-now", "live-narration" })
            await Assertions.Expect(page.GetByTestId(panel)).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("start-camera")).ToBeVisibleAsync();

        await page.GetByTestId("live-view-numbers").ClickAsync();
        foreach (var panel in new[] { "live-metrics", "live-tracks", "live-motion-grid" })
            await Assertions.Expect(page.GetByTestId(panel)).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("live-narration")).ToHaveCountAsync(0);
        await page.AssertNoBlazorErrorAsync();
    }

    [Fact]
    public async Task A_demo_session_fills_the_counters_and_stops_cleanly()
    {
        if (PlaywrightFixture.BaseUrl is null) return;
        await using var page = await PoWatchPage.SignedInAsync(fixture.Browser, user: $"demo-{Guid.NewGuid():N}");

        await page.GetByTestId("start-demo").ClickAsync();
        await Assertions.Expect(page.GetByTestId("session-status")).ToContainTextAsync("OBSERVING");

        // nonzero live counters within 15 s, with no camera, GPU or model, and the comings and goings written down.
        await Assertions.Expect(page.GetByTestId("metric-visits")).Not.ToHaveTextAsync("0", new() { Timeout = 15_000 });
        await Assertions.Expect(page.GetByTestId("narration-lines").Locator("[data-kind=enter]").First).ToBeVisibleAsync(new() { Timeout = 15_000 });

        await page.GetByTestId("header-stop").ClickAsync();
        await Assertions.Expect(page.GetByTestId("session-status")).ToContainTextAsync("STANDBY");

        // Stopping shows the recap: the session's own numbers and its moments.
        await Assertions.Expect(page.GetByTestId("away-card")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Assertions.Expect(page.GetByTestId("away-visits")).Not.ToContainTextAsync("0", new() { Timeout = 15_000 });
        await Assertions.Expect(page.GetByTestId("away-card")).ToContainTextAsync("of the session");
        await Assertions.Expect(page.GetByTestId("away-pdf")).ToHaveAttributeAsync("href", new System.Text.RegularExpressions.Regex(@"^api/recaps/session/.+\.pdf$"));
        await page.GetByTestId("away-close").ClickAsync();
        await Assertions.Expect(page.GetByTestId("away-card")).Not.ToBeVisibleAsync();
        await page.AssertNoBlazorErrorAsync();
    }

    [Fact]
    public async Task Live_renders_every_stat_family_with_real_numbers_for_the_recording_session()
    {
        if (PlaywrightFixture.BaseUrl is null) return;
        // Its own user: the seed runs once per user, so on the shared guest it would land on whatever earlier tests left.
        var user = $"month-{Guid.NewGuid():N}";
        await using var page = await PoWatchPage.SignedInAsync(fixture.Browser, user: user);
        // APIRequest does not carry the page's sign-in cookie; the header names the same user.
        var seed = await page.APIRequest.PostAsync($"{PlaywrightFixture.BaseUrl}/api/dev/seed?days=40&tz=UTC", new() { Headers = PoWatchPage.As(user) });
        Assert.True(seed.Ok, $"seed returned {seed.Status}");

        // Live is always the session being recorded (the seeded month is only the "usual" it is compared with).
        await page.GoToAsync("/?start=demo", "LIVE");
        await Assertions.Expect(page.GetByTestId("header-stop")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Assertions.Expect(page.GetByTestId("stats-window")).ToContainTextAsync("Recording session", new() { Timeout = 30_000 });
        await page.GetByTestId("live-view-numbers").ClickAsync();
        await Assertions.Expect(page.GetByTestId("stats-occupancy")).Not.ToContainTextAsync("0.0%", new() { Timeout = 45_000 });
        await Assertions.Expect(page.Locator("[data-test=stats-presence] .rz-chart svg").First).ToBeVisibleAsync();

        // Every family is under Live's Numbers (classes in "In frame"); pipeline counters are on System.
        foreach (var content in new[] { "stats-space", "live-census", "stats-patterns", "stats-environment" })
        {
            await Assertions.Expect(page.GetByTestId(content)).ToBeVisibleAsync(new() { Timeout = 30_000 });
        }

        await page.GetByTestId("header-stop").ClickAsync();
        await page.GoToSystemAsync();
        await page.OpenSystemTabAsync("Pipeline");
        await Assertions.Expect(page.GetByTestId("stats-all-frames")).Not.ToContainTextAsync("—", new() { Timeout = 30_000 });
        await page.AssertNoBlazorErrorAsync();
    }

    [Fact]
    public async Task New_people_can_be_named_from_a_passive_prompt_or_left_unnamed()
    {
        if (PlaywrightFixture.BaseUrl is null) return;
        // A fresh user: storage persists (Azurite), so a reused guest already knows the walker and the cat.
        await using var page = await PoWatchPage.SignedInAsync(fixture.Browser, user: $"names-{Guid.NewGuid():N}");

        await page.GetByTestId("start-demo").ClickAsync();

        // The demo walker and the cat each get an offer to be named, waiting behind the bell; nothing opens by itself.
        await page.GetByTestId("tray-toggle").ClickAsync();
        var person = page.GetByTestId("name-prompt").Filter(new() { HasText = "New person spotted" });
        var cat = page.GetByTestId("name-prompt").Filter(new() { HasText = "New cat spotted" });
        await Assertions.Expect(person).ToBeVisibleAsync(new() { Timeout = 20_000 });
        await Assertions.Expect(cat).ToBeVisibleAsync(new() { Timeout = 20_000 });

        await person.GetByTestId("name-input").FillAsync("Bob");
        await person.GetByTestId("name-save").ClickAsync();
        await Assertions.Expect(person).Not.ToBeVisibleAsync();
        await cat.GetByTestId("name-skip").ClickAsync();
        await Assertions.Expect(cat).Not.ToBeVisibleAsync();

        // Bob is recognised by name in frame, and listed in Settings' "Who PoWatch knows"; the cat stays unnamed.
        await page.GetByTestId("live-view-numbers").ClickAsync();
        await Assertions.Expect(page.GetByTestId("live-tracks")).ToContainTextAsync("Bob", new() { Timeout = 25_000 });
        await page.GetByTestId("header-stop").ClickAsync();
        await page.OpenSettingsAsync("people");
        await Assertions.Expect(page.GetByTestId("regulars-table")).ToBeVisibleAsync();
        ILocator NameBox(string who) => page.Locator(".regular-name").Filter(new() { HasText = $"Name for {who}" }).Locator("input");
        await Assertions.Expect(NameBox("Bob")).ToHaveValueAsync("Bob");
        await Assertions.Expect(NameBox("Cat 1")).ToHaveValueAsync("");
        await page.AssertNoBlazorErrorAsync();
    }

    [Fact]
    public async Task History_walks_back_day_by_day_through_real_sessions()
    {
        if (PlaywrightFixture.BaseUrl is null) return;
        var user = $"history-{Guid.NewGuid():N}";
        await using var page = await PoWatchPage.SignedInAsync(fixture.Browser, user: user, advanced: true);

        // Real ingest (not the seed), so today has the minute rollups a day view reads. Ingest refuses
        // ticks outside their session (no backdating), so only today has a recorded session.
        var zone = TimeZoneInfo.Local.Id;
        var start = await page.APIRequest.PostAsync($"{PlaywrightFixture.BaseUrl}/api/sessions", new() { DataObject = new { timeZoneId = zone }, Headers = PoWatchPage.As(user) });
        var sessionId = (await start.JsonAsync())!.Value.GetProperty("id").GetString();
        var justNow = DateTimeOffset.Now.AddSeconds(-30);
        object Tick(DateTimeOffset at) => new
        {
            startUtc = at.UtcDateTime,
            pixelSamples = 40,
            motionMean = 0.2,
            motionMax = 0.4,
            luminanceMean = 0.5,
            classes = new Dictionary<string, object> { ["person"] = new { max = 1, mean = 1 } }
        };
        var batch = await page.APIRequest.PostAsync($"{PlaywrightFixture.BaseUrl}/api/sessions/{sessionId}/batches", new()
        {
            Headers = PoWatchPage.As(user),
            DataObject = new { batchKey = Guid.NewGuid(), ticks = new[] { Tick(justNow), Tick(justNow.AddSeconds(10)) } }
        });
        Assert.True(batch.Ok, $"batch returned {batch.Status}");

        // The Journal's index lists every day with its sessions inside; a session opens in the reader with its
        // story, numbers, and the replay of just its minutes over its timeline.
        await page.GoToAsync("/journal", "JOURNAL");
        await Assertions.Expect(page.GetByTestId("story-open").First).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await page.GetByTestId("story-open").First.ClickAsync();
        await Assertions.Expect(page.GetByTestId("session-page")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Assertions.Expect(page.GetByTestId("reader-timeline").GetByTestId("replay-scrub")).ToBeVisibleAsync(new() { Timeout = 30_000 });

        // The day opens in the same reader; the year turns into a 3D skyline and back (the browser remembers which).
        await page.GetByTestId("journal-day").First.ClickAsync();
        await Assertions.Expect(page.GetByTestId("history-calendar")).ToBeVisibleAsync();
        await page.GetByTestId("year-3d").ClickAsync();
        await Assertions.Expect(page.GetByTestId("year-skyline")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("skyline-fly")).ToBeVisibleAsync();
        await page.GetByTestId("year-2d").ClickAsync();
        await Assertions.Expect(page.GetByTestId("year-skyline")).Not.ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("history-occupancy")).ToContainTextAsync("%", new() { Timeout = 30_000 });

        // The day's recap reads as a sentence and downloads as a real PDF.
        await Assertions.Expect(page.GetByTestId("history-recap")).ToContainTextAsync("WRITTEN BY TEMPLATE");
        var pdfHref = await page.GetByTestId("history-recap-pdf").GetAttributeAsync("href");
        var pdf = await page.APIRequest.GetAsync($"{PlaywrightFixture.BaseUrl}/{pdfHref}", new() { Headers = PoWatchPage.As(user) });
        Assert.True(pdf.Ok || pdf.Status == 503, $"recap PDF returned {pdf.Status}");
        if (pdf.Ok) Assert.Equal("%PDF"u8.ToArray(), (await pdf.BodyAsync()).Take(4).ToArray());

        // The week folds under the numbers; a share link works without signing in; the day replays minute by
        // minute over its timeline, with its moments and time-lapse under it.
        await page.GetByTestId("history-week").Locator("summary").ClickAsync();
        await Assertions.Expect(page.GetByTestId("week-summary")).ToContainTextAsync("PoWatch watched for", new() { Timeout = 30_000 });
        await page.GetByTestId("history-share").ClickAsync();
        var link = page.GetByTestId("history-share-link").Locator("input");
        await Assertions.Expect(link).ToHaveValueAsync(new System.Text.RegularExpressions.Regex(@"/s/[A-Za-z0-9_-]{22}$"), new() { Timeout = 30_000 });
        var shared = await page.APIRequest.GetAsync(await link.InputValueAsync());
        Assert.True(shared.Ok, $"shared page returned {shared.Status}");
        await Assertions.Expect(page.GetByTestId("replay-scrub")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Assertions.Expect(page.GetByTestId("replay-clock")).Not.ToHaveTextAsync("--:--");
        await Assertions.Expect(page.GetByTestId("history-moments")).ToBeAttachedAsync();
        await Assertions.Expect(page.GetByTestId("history-timelapse")).ToContainTextAsync("FRAMES · THIS DEVICE");

        // A day with nothing recorded says so instead of showing zeros; arrow keys page too.
        var today = await page.GetByTestId("history-day").TextContentAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Previous day" }).ClickAsync();
        await Assertions.Expect(page.GetByTestId("history-day")).Not.ToHaveTextAsync(today!);
        await Assertions.Expect(page.GetByTestId("history-empty")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        var yesterday = await page.GetByTestId("history-day").TextContentAsync();
        await page.GetByTestId("history-toolbar").FocusAsync();
        await page.Keyboard.PressAsync("ArrowLeft");
        await Assertions.Expect(page.GetByTestId("history-day")).Not.ToHaveTextAsync(yesterday!);
        await Assertions.Expect(page.GetByTestId("history-empty")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Go to today" }).ClickAsync();
        await Assertions.Expect(page.GetByTestId("history-day")).ToHaveTextAsync(today!);
        await page.AssertNoBlazorErrorAsync();
    }

    [Fact]
    public async Task The_system_page_reports_inference_state_and_every_connection_with_a_verdict()
    {
        if (PlaywrightFixture.BaseUrl is null) return;
        await using var page = await PoWatchPage.SignedInAsync(fixture.Browser);
        await page.GoToSystemAsync();

        await Assertions.Expect(page.GetByTestId("health-overall")).ToBeVisibleAsync(new() { Timeout = 30000 });
        // Wait for the list itself: "Checking…" also renders health-overall, so counting straight
        // after it is a race with the first fetch.
        await Assertions.Expect(page.GetByTestId("health-check-list")).ToBeVisibleAsync(new() { Timeout = 30000 });
        Assert.True(await page.GetByTestId("health-check").CountAsync() > 0);

        await page.OpenSystemTabAsync("Engine");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Inference engine" }))
            .ToBeVisibleAsync();
    }

    [Fact]
    public async Task Signing_out_returns_the_operator_to_the_login_page()
    {
        if (PlaywrightFixture.BaseUrl is null) return;
        await using var page = await PoWatchPage.SignedInAsync(fixture.Browser);

        await page.OpenSettingsAsync("account");
        await page.GetByTestId("sign-out").ClickAsync();

        await Assertions.Expect(page.GetByTestId("login-shell")).ToBeVisibleAsync(new() { Timeout = 60000 });
    }

}
