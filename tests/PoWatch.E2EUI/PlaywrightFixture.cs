using Microsoft.Playwright;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.Azurite;

namespace PoWatch.E2EUI;

/// <summary>
/// Shared xUnit collection fixture: spins up a single Chromium instance for all UI tests
/// so each test doesn't pay the ~250 ms browser-launch cost. Tests get their own isolated
/// <see cref="IBrowserContext"/> + <see cref="IPage"/> so cookies/storage don't bleed.
/// </summary>
public sealed class PlaywrightFixture : IAsyncLifetime, IAsyncDisposable
{
    /// <summary>
    /// The app under test. <c>E2E_BASE_URL</c> points the suite at an already-running instance; otherwise
    /// (the default since 2026-11-17) the fixture hosts the app itself in-process against an Azurite
    /// Testcontainer. When that cannot work (no Docker, no app at E2E_BASE_URL, no Chromium) every test
    /// is reported as skipped with the reason (<see cref="E2EFactAttribute"/>), never as a pass.
    /// </summary>
    public static string BaseUrl => Environment.GetEnvironmentVariable("E2E_BASE_URL") is { Length: > 0 } url
        ? url.TrimEnd('/')
        : _localBaseUrl ?? throw new InvalidOperationException("The E2EUI fixture has not started the app.");
    private static string? _localBaseUrl;
    private AzuriteContainer? _azurite;
    private LocalUiApplicationFactory? _localFactory;

    // Local dev cert is untrusted by Chromium — ignore for E2E runs.
    // The fake media flags give every page a synthetic moving camera with permission pre-granted,
    // so camera sessions run headless.
    private static readonly string[] LaunchArgs = ["--ignore-certificate-errors", "--use-fake-device-for-media-stream", "--use-fake-ui-for-media-stream"];

    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public IBrowser Browser => _browser
        ?? throw new InvalidOperationException("Browser not initialized.");

    public async Task InitializeAsync()
    {
        if (E2EAvailability.SkipReason is not null)
        {
            return; // Every test in this tier is skipped; start nothing.
        }

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("E2E_BASE_URL")))
        {
            try
            {
                _azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest").WithCommand("--skipApiVersionCheck").Build();
                await _azurite.StartAsync();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "E2EUI hosts the app against an Azurite Testcontainer, so Docker must be running " +
                    "(or set E2E_BASE_URL to test a running instance).", ex);
            }
            _localFactory = new LocalUiApplicationFactory(_azurite.GetConnectionString());
            _localFactory.UseKestrel(0);
            using var client = _localFactory.CreateClient();
            _localBaseUrl = client.BaseAddress!.ToString().TrimEnd('/');
        }
        _playwright = await Playwright.CreateAsync();
        // The app's stable selectors use data-test (see MainLayout/NavMenu), not Playwright's
        // default data-testid — align GetByTestId with the markup.
        _playwright.Selectors.SetTestIdAttribute("data-test");
        try
        {
            _browser = await _playwright.Chromium.LaunchAsync(new()
            {
                // E2E_HEADED=1 opens a visible browser window (demo/debug); default stays headless for CI.
                Headless = Environment.GetEnvironmentVariable("E2E_HEADED") != "1",
                Args = LaunchArgs
            });
        }
        catch (PlaywrightException ex)
        {
            throw new InvalidOperationException(
                "Playwright's Chromium is missing: run tests/PoWatch.E2EUI/bin/<config>/net10.0/playwright.ps1 install chromium.", ex);
        }
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
        if (_localFactory is not null) await _localFactory.DisposeAsync();
        if (_azurite is not null) await _azurite.DisposeAsync();
        _localBaseUrl = null;
    }

    async ValueTask IAsyncDisposable.DisposeAsync() => await DisposeAsync();
}

internal sealed class LocalUiApplicationFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");
        builder.UseStaticWebAssets();
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureStorage:ConnectionString"] = connectionString,
            ["AzureStorage:ServiceUri"] = "",
            ["FeatureFlags:DeveloperBypassAuth"] = "true",
            ["FeatureFlags:EnableKeyVault"] = "false",
            ["AiProvider:Provider"] = "Template",
            ["ApplicationInsights:ConnectionString"] = ""
        }));
    }
}

[CollectionDefinition(nameof(PlaywrightCollection))]
public sealed class PlaywrightCollection : ICollectionFixture<PlaywrightFixture> { }

/// <summary>
/// Helpers layered on top of the playwright-blazor skill's standard patterns.
/// </summary>
public static class PlaywrightPageExtensions
{
    /// <summary>
    /// Asserts Blazor's <c>#blazor-error-ui</c> overlay is hidden. Blazor populates this
    /// when an unhandled exception escapes a component — a silent failure mode that
    /// would otherwise let tests pass on broken pages.
    /// </summary>
    public static async Task AssertNoBlazorErrorAsync(this IPage page)
    {
        await Assertions.Expect(page.Locator(".po-error-panel")).Not.ToBeVisibleAsync();
        // A single panel's retry box (TerminalPanel's boundary) is an error too, even with the page up.
        await Assertions.Expect(page.Locator("[data-test=panel-error]")).ToHaveCountAsync(0);
        var errorUi = page.Locator("#blazor-error-ui");
        if (await errorUi.IsVisibleAsync())
        {
            var text = await errorUi.InnerTextAsync();
            Assert.Fail($"Blazor error UI was visible: {text}");
        }
        page.ThrowIfErrors();
    }
}
