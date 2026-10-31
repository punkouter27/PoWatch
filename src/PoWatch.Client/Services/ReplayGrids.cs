using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace PoWatch.Client.Services;

/// <summary>
/// Hands a replay's 16×9 motion grids to replay.js as raw bytes: a [JSImport] of
/// wwwroot/js/replay-grids.js with a MemoryView over .NET memory, instead of a base64 string inside the
/// JSON. The server still sends base64; the win is the C#→JS hop. Outside a browser, or when the module
/// does not load, it returns false and the caller sends the base64 as before.
/// </summary>
public static partial class ReplayGrids
{
    private const string Module = "powatch-replay-grids";
    private static Task? s_import;

    /// <summary>Stages <paramref name="base64"/>'s bytes for the next replay mount of <paramref name="day"/>.</summary>
    public static async Task<bool> TryStageAsync(string baseUri, string day, string base64)
    {
        if (!OperatingSystem.IsBrowser()) return false;
        try
        {
            await (s_import ??= JSHost.ImportAsync(Module, new Uri(new Uri(baseUri), "js/replay-grids.js").ToString()));
            Stage(day, Convert.FromBase64String(base64));
            return true;
        }
        catch (Exception ex) when (ex is JSException or FormatException or InvalidOperationException)
        {
            s_import = null;
            return false;
        }
    }

    [JSImport("stage", Module)]
    [SupportedOSPlatform("browser")]
    private static partial void Stage(string day, [JSMarshalAs<JSType.MemoryView>] Span<byte> grids);
}
