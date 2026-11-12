using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PoWatch.Client.Services;

namespace PoWatch.Client.Layout;

public static class AccountMenu
{
    public static async Task SwitchFakeUserAsync(IJSRuntime js, NavigationManager nav, string? user, string? roles = null)
    {
        if (string.IsNullOrWhiteSpace(user))
        {
            await js.TryInvokeVoidAsync("localStorage.removeItem", DevFakeUserHandler.FakeUserStorageKey);
            await js.TryInvokeVoidAsync("localStorage.removeItem", DevFakeUserHandler.FakeRolesStorageKey);
        }
        else
        {
            await js.TryInvokeVoidAsync("localStorage.setItem", DevFakeUserHandler.FakeUserStorageKey, user);
            if (!string.IsNullOrWhiteSpace(roles))
                await js.TryInvokeVoidAsync("localStorage.setItem", DevFakeUserHandler.FakeRolesStorageKey, roles);
            else
                await js.TryInvokeVoidAsync("localStorage.removeItem", DevFakeUserHandler.FakeRolesStorageKey);
        }

        nav.NavigateTo(nav.Uri, forceLoad: true);
    }
}
