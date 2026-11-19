using PoWatch.Shared.Models;

namespace PoWatch.Mobile.Services;

/// <summary>Who is signed in (/auth/me), refreshed on demand; offline, a stored token still counts.</summary>
public sealed class Session(ApiClient api, SecureSessionStore tokens)
{
    public AuthStateDto? Me { get; private set; }

    public bool SignedIn => Me?.IsAuthenticated == true;

    public async Task<bool> RefreshAsync()
    {
        if (!ApiClient.Online && Me is not null)
        {
            return SignedIn;
        }

        AuthStateDto? me = await api.GetMeAsync();
        if (me is not null)
        {
            Me = me;
            return SignedIn;
        }

        // Unreachable: a saved Microsoft token keeps the app usable (sensing queues offline).
        return Me?.IsAuthenticated == true || !string.IsNullOrEmpty(await tokens.GetBearerTokenAsync());
    }

    public void Clear() => Me = null;
}
