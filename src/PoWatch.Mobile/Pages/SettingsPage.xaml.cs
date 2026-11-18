using PoWatch.Mobile.Services;

namespace PoWatch.Mobile.Pages;

public partial class SettingsPage : ContentPage
{
    private readonly MobileConfig _config;
    private readonly SecureSessionStore _sessionStore;
    private readonly PoWatchMobileClient _client;

    public SettingsPage(MobileConfig config, SecureSessionStore sessionStore, PoWatchMobileClient client)
    {
        _config = config;
        _sessionStore = sessionStore;
        _client = client;
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        BaseUrlEntry.Text = _config.BaseUrl;
        FakeUserEntry.Text = await _sessionStore.GetFakeUserAsync();
        TokenEntry.Text = await _sessionStore.GetBearerTokenAsync();
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        _config.BaseUrl = BaseUrlEntry.Text;
        await _sessionStore.SetFakeUserAsync(FakeUserEntry.Text);
        await _sessionStore.SetBearerTokenAsync(TokenEntry.Text);
        ResultLabel.Text = $"Saved settings for {_config.BaseUrl}";
    }

    private async void OnTestConnectionClicked(object? sender, EventArgs e)
    {
        try
        {
            var me = await _client.GetAuthMeAsync();
            ResultLabel.Text = me is { IsAuthenticated: true }
                ? $"Authenticated as {me.Name ?? me.Email ?? "user"}"
                : "Connected, but unauthenticated.";
        }
        catch (Exception ex)
        {
            ResultLabel.Text = $"Connection error: {ex.Message}";
        }
    }
}
