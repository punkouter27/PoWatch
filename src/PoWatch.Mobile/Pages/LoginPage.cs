using PoWatch.Mobile.Services;

namespace PoWatch.Mobile.Pages;

/// <summary>Sign in: Microsoft (Entra PKCE in the system browser → bearer token) or, in Debug builds, the API's guest.</summary>
public sealed class LoginPage : ContentPage
{
    private readonly ApiClient _api;
    private readonly Session _session;
    private readonly EntraAuthService _entra;
    private readonly MobileConfig _config;
    private readonly CookieJar _cookies;
    private readonly Label _status = Ui.Muted(null);

    public LoginPage(ApiClient api, Session session, EntraAuthService entra, MobileConfig config, CookieJar cookies)
    {
        _api = api;
        _session = session;
        _entra = entra;
        _config = config;
        _cookies = cookies;
        Title = "Sign in";
        Shell.SetTabBarIsVisible(this, false);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        VerticalStackLayout body = Ui.Stack(14,
            Ui.H1("PoWatch"),
            Ui.Text("Point a camera at anything, press Start, walk away — and come back to a nerdy statistical picture of what happened."),
            Ui.Primary("Continue with Microsoft", MicrosoftAsync));

        if (MobileConfig.GuestAllowed && await _api.GetAuthConfigAsync() is { GuestEnabled: true })
        {
            body.Add(Ui.Secondary("Continue as guest (dev)", GuestAsync));
        }
        else if (!ApiClient.Online)
        {
            _status.Text = "You're offline.";
        }

        body.Add(_status);
        Content = new ScrollView { Content = body, Padding = 20 };
    }

    private async Task MicrosoftAsync()
    {
        if (string.IsNullOrWhiteSpace(_config.EntraClientId))
        {
            _status.Text = "Microsoft sign-in needs the app's Entra client id — set it under Settings → Server.";
            return;
        }

        try
        {
            _cookies.Clear();
            await _entra.SignInWithPkceAsync();
            await FinishAsync();
        }
        catch (TaskCanceledException)
        {
            _status.Text = "Sign-in was cancelled.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Sign-in failed: {ex.Message}";
        }
    }

    private async Task GuestAsync()
    {
        if (!await _api.GuestLoginAsync())
        {
            _status.Text = $"Guest sign-in failed — is the API at {_config.BaseUrl} running in Development?";
            return;
        }

        await _cookies.SaveAsync();
        await FinishAsync();
    }

    private async Task FinishAsync()
    {
        if (await _session.RefreshAsync())
        {
            await Shell.Current.GoToAsync("..");
        }
        else
        {
            _status.Text = "Signed in, but the server didn't accept the session.";
        }
    }
}
