using System.Net;
using Microsoft.Extensions.Logging;
using PoWatch.Mobile.Pages;
using PoWatch.Mobile.Services;
using PoWatch.Mobile.Services.Sensing;

namespace PoWatch.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if DEBUG
        builder.Logging.AddDebug();
#endif

        builder.Services.AddSingleton<MobileConfig>();
        builder.Services.AddSingleton<SecureSessionStore>();
        builder.Services.AddSingleton<EntraAuthService>();
        builder.Services.AddSingleton(new CookieContainer());
        builder.Services.AddSingleton<CookieJar>();
        builder.Services.AddTransient<AuthDelegatingHandler>();
        builder.Services.AddSingleton(sp =>
        {
            var handler = sp.GetRequiredService<AuthDelegatingHandler>();
            handler.InnerHandler = new HttpClientHandler { CookieContainer = sp.GetRequiredService<CookieContainer>(), UseCookies = true };
            var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            // UserAgentValidation on the API accepts "Mobile".
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PoWatch.Mobile/1.0");
            return http;
        });
        builder.Services.AddSingleton<ApiClient>();
        builder.Services.AddSingleton<Session>();
        builder.Services.AddSingleton<Outbox>();
        builder.Services.AddSingleton<SensingEngine>();
#if ANDROID
        builder.Services.AddSingleton<ICameraSource, AndroidCamera>();
        builder.Services.AddSingleton<IMicrophone, AndroidMicrophone>();
        builder.Services.AddSingleton<ILightSensor, AndroidLightSensor>();
        builder.Services.AddSingleton<IImageEncoder, AndroidImageEncoder>();
        builder.Services.AddSingleton<IDeviceHost, AndroidDeviceHost>();
#elif IOS
        builder.Services.AddSingleton<ICameraSource, IosCamera>();
        builder.Services.AddSingleton<IMicrophone, IosMicrophone>();
        builder.Services.AddSingleton<ILightSensor, IosLightSensor>();
        builder.Services.AddSingleton<IImageEncoder, IosImageEncoder>();
        builder.Services.AddSingleton<IDeviceHost, IosDeviceHost>();
#endif

        builder.Services.AddTransient<LoginPage>();
        builder.Services.AddTransient<LivePage>();
        builder.Services.AddTransient<JournalPage>();
        builder.Services.AddTransient<DayPage>();
        builder.Services.AddTransient<SessionPage>();
        builder.Services.AddTransient<SettingsPage>();
        builder.Services.AddTransient<DiagPage>();

        MauiApp app = builder.Build();
        app.Services.GetRequiredService<CookieJar>().RestoreAsync().GetAwaiter().GetResult();

        // Queued batches go as soon as the network is back, whatever page is open.
        Outbox outbox = app.Services.GetRequiredService<Outbox>();
        Connectivity.Current.ConnectivityChanged += (_, e) =>
        {
            if (e.NetworkAccess == NetworkAccess.Internet)
            {
                _ = outbox.SendAsync();
            }
        };
        return app;
    }
}
