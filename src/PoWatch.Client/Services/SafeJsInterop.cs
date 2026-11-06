using System.Diagnostics.CodeAnalysis;
using Microsoft.JSInterop;

namespace PoWatch.Client.Services;

/// <summary>
/// Safe wrappers around <see cref="IJSRuntime"/> that swallow the JSInterop exceptions
/// thrown when an optional JS bridge (audio, blob upload, webcam-shell overlay, …) failed
/// to load or evaluate. every uncaught JSException shows up to the user as the
/// global "An unhandled error has occurred" banner, which is hostile on a kiosk. Bridges
/// are enhancements, not requirements — the app must keep rendering when one is missing.
/// </summary>
public static class SafeJsInterop
{
    // What JSInterop's own InvokeAsync<T> keeps on T so its result can be deserialized; restating it
    // here lets the trim analyzer follow T through the wrapper.
    private const DynamicallyAccessedMemberTypes JsonSerialized =
        DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties;

    /// <summary>Fire-and-forget; never throws. Returns true when the JS call actually ran.</summary>
    public static async Task<bool> TryInvokeVoidAsync(this IJSRuntime js, string identifier, params object?[] args)
    {
        try
        {
            await js.InvokeVoidAsync(identifier, args);
            return true;
        }
        catch (Exception ex) when (IsBridgeFailure(ex))
        {
            return false;
        }
    }

    /// <summary>Returns <c>default</c> instead of throwing when the bridge is missing.</summary>
    public static async Task<T?> TryInvokeAsync<[DynamicallyAccessedMembers(JsonSerialized)] T>(this IJSRuntime js, string identifier, params object?[] args)
    {
        try
        {
            return await js.InvokeAsync<T?>(identifier, args);
        }
        catch (Exception ex) when (IsBridgeFailure(ex))
        {
            return default;
        }
    }

    /// <summary>
    /// The failures an optional bridge may cause: the browser navigated away or the circuit dropped
    /// (<see cref="JSDisconnectedException"/>); the bridge isn't there or threw (<see cref="JSException"/>);
    /// JSRuntime isn't ready, during prerender or the dispose window (<see cref="InvalidOperationException"/>);
    /// or the call was cancelled.
    /// </summary>
    private static bool IsBridgeFailure(Exception ex) =>
        ex is JSDisconnectedException or JSException or InvalidOperationException or TaskCanceledException;
}
