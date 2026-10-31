using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace PoWatch.Client.Services;

/// <summary>
/// Derives auth state from the BFF's <c>/auth/me</c> endpoint. The WASM client never handles
/// tokens — the session lives in the server's encrypted HttpOnly cookie.
/// </summary>
internal sealed class BffAuthenticationStateProvider(HttpClient http, IJSRuntime js) : AuthenticationStateProvider
{
    private const string LastUserKey = "powatch:user";
    private static readonly AuthenticationState Anonymous = new(new ClaimsPrincipal(new ClaimsIdentity()));

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            var me = await http.GetFromJsonAsync("auth/me", PoWatchJsonContext.Default.AuthStateDto);
            if (me is null || !me.IsAuthenticated)
            {
                await js.TryInvokeVoidAsync("localStorage.removeItem", LastUserKey);
                return Anonymous;
            }

            await js.TryInvokeVoidAsync("localStorage.setItem", LastUserKey, me.Name ?? "user");
            var claims = new List<Claim> { new(ClaimTypes.Name, me.Name ?? "user") };
            if (!string.IsNullOrWhiteSpace(me.Email))
                claims.Add(new Claim(ClaimTypes.Email, me.Email));
            claims.AddRange((me.Roles ?? []).Select(r => new Claim(ClaimTypes.Role, r)));
            var identity = new ClaimsIdentity(claims, authenticationType: "bff");
            return new AuthenticationState(new ClaimsPrincipal(identity));
        }
        catch (HttpRequestException)
        {
            // Offline. The server still decides what the cookie may read once it is reachable; this only
            // keeps the shell (and a session's outbox) open for whoever was last signed in here.
            return await js.TryInvokeAsync<string>("localStorage.getItem", LastUserKey) is { Length: > 0 } name
                ? new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], authenticationType: "bff-offline")))
                : Anonymous;
        }
        catch
        {
            return Anonymous;
        }
    }
}
