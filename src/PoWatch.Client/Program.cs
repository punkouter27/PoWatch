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
// One stable session id per app load, threaded onto every BFF call by CorrelationHandler so the
// server can correlate an entire monitoring session instead of seeing N unrelated requests.
var sessionId = Guid.NewGuid().ToString("N");
// Reads that fail on the way are retried (RetryHandler); every attempt keeps the request's correlation id.
builder.Services.AddScoped(sp => new HttpClient(new CorrelationHandler(sessionId) { InnerHandler = new RetryHandler { InnerHandler = new HttpClientHandler() } })
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
