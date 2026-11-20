using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace PoWatch.E2EUI;

/// <summary>
/// [Fact] for the E2E UI tier, which drives a real browser against the app: skips with one clear
/// reason, instead of failing every test, when Playwright's Chromium is not installed, when
/// <c>E2E_BASE_URL</c> is set but nothing answers there, or (no <c>E2E_BASE_URL</c>, so the fixture
/// hosts the app itself against an Azurite Testcontainer) when Docker is not running. Not part of CI.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (E2EAvailability.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>[Theory] counterpart of <see cref="E2EFactAttribute"/>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class E2ETheoryAttribute : TheoryAttribute
{
    public E2ETheoryAttribute()
    {
        if (E2EAvailability.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>Checks once per test run that an app answers and a browser is installed.</summary>
public static class E2EAvailability
{
    /// <summary><c>E2E_BASE_URL</c>, or null when the fixture hosts the app itself.</summary>
    private static readonly string? ExternalUrl =
        Environment.GetEnvironmentVariable("E2E_BASE_URL") is { Length: > 0 } url ? url.TrimEnd('/') : null;

    private static readonly Lazy<string?> Reason = new(Probe);

    /// <summary>Null when the tier can run; otherwise why it is skipped.</summary>
    public static string? SkipReason => Reason.Value;

    private static string? Probe()
    {
        if (!ChromiumInstalled())
        {
            return "Playwright Chromium is not installed, so the E2E UI tier is skipped. " +
                   "Build, then run: pwsh tests/PoWatch.E2EUI/bin/Release/net10.0/playwright.ps1 install chromium";
        }

        if (ExternalUrl is null)
        {
            return DockerRuns()
                ? null
                : "Docker is not running, so the E2E UI tier is skipped (without E2E_BASE_URL it hosts the app " +
                  "against an Azurite Testcontainer). Start Docker Desktop, or set E2E_BASE_URL to a running copy.";
        }

        try
        {
            using var handler = new HttpClientHandler
            {
                // Local dev certificates are self-signed.
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            using var client = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(10) };
            using var response = client.GetAsync(new Uri(ExternalUrl + "/health/live")).GetAwaiter().GetResult();
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return "No app answers at " + ExternalUrl + ", so the E2E UI tier is skipped. " +
                   "Start the app there, or unset E2E_BASE_URL to let the tier host it (needs Docker).";
        }
    }

    private static bool DockerRuns()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", "info --format {{.ServerVersion}}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null || !process.WaitForExit(TimeSpan.FromSeconds(20)))
            {
                process?.Kill(entireProcessTree: true);
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool ChromiumInstalled()
    {
        var root = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        if (string.IsNullOrEmpty(root) || root == "0")
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            root = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ms-playwright")
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? Path.Combine(home, "Library", "Caches", "ms-playwright")
                    : Path.Combine(home, ".cache", "ms-playwright");
        }

        return Directory.Exists(root)
            && Directory.EnumerateDirectories(root, "chromium*").Any();
    }
}
