using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Options;
using PoWatch.Client;
using PoWatch.Client.Services;
using Radzen;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var apiBaseUrl = builder.Configuration["ApiBaseUrl"];
var sessionId = Guid.NewGuid().ToString("N");
builder.Services.AddScoped(_ =>
    new HttpClient(new CorrelationHandler(sessionId) { InnerHandler = new RetryHandler { InnerHandler = new HttpClientHandler() } })
    {
        BaseAddress = new Uri(string.IsNullOrWhiteSpace(apiBaseUrl)
            ? builder.HostEnvironment.BaseAddress
            : apiBaseUrl)
    });
builder.Services.AddScoped<PoWatchApiClient>();
builder.Services.AddSingleton(TimeProvider.System);
// One sensing session per browser tab; every page reads its state.
builder.Services.AddScoped<SensingSession>();
builder.Services.AddScoped<StatsFeed>();
builder.Services.AddScoped<DrawerState>();
builder.Services.AddRadzenComponents();

// BFF auth: server cookie holds the session; client derives state from /auth/me.
builder.Services.AddAuthorizationCore();
builder.Services.AddScoped<BffAuthenticationStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<BffAuthenticationStateProvider>());

await builder.Build().RunAsync();
