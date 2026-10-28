using System.Globalization;
using PoWatch.Api.Features;
using PoWatch.Api.Features.Ingest;
using PoWatch.Api.Features.Stats;
using PoWatch.Api.HealthChecks;
using PoWatch.Api.Hosting;
using PoWatch.Api.Middleware;
using PoWatch.Api.Observability;
using PoWatch.Api.Platform;
using PoWatch.Api.Features.Ask;
using PoWatch.Api.Security;
using PoWatch.Application;
using PoWatch.Application.Options;
using PoWatch.Infrastructure;
using PoWatch.Infrastructure.Runtime;
using Scalar.AspNetCore;
using Serilog;

// Initialise Serilog early so startup errors are captured before host construction.
// Use a regular logger here so repeated WebApplicationFactory host creation in integration tests
// does not re-freeze a shared ReloadableLogger instance.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.WithMachineName()
    .Enrich.WithThreadId()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateLogger();

// Create the builder FIRST so we can install the Kestrel hooks on it.
var builder = WebApplication.CreateBuilder(args);

// Development only: step to the next free port when the configured one is held by another app.
builder.WebHost.ConfigureKestrel((ctx, opts) =>
    PortNegotiation.Configure(opts, ctx.Configuration, ctx.HostingEnvironment, Log.Logger.ForContext(typeof(PortNegotiation))));

// Two-stage Serilog initialisation — reads config from appsettings after host is built
builder.Host.UseSerilog(TelemetrySetup.ConfigureSerilog);

// Feature flags are not secrets, so they are read before Key Vault joins the configuration.
var featureFlags = builder.Configuration.GetSection("FeatureFlags").Get<FeatureFlagsOptions>() ?? new FeatureFlagsOptions();
if (featureFlags.EnableKeyVault && Uri.TryCreate(builder.Configuration["KeyVault:Uri"], UriKind.Absolute, out var kvUri))
    KeyVaultConfiguration.AddPoWatchKeyVault(builder.Configuration, kvUri, Log.Logger);

builder.Services.Configure<FeatureFlagsOptions>(builder.Configuration.GetSection("FeatureFlags"));

// Fail-fast options — the startup-critical settings are validated and ValidateOnStart()
// forces evaluation during host build, so a bad table name, polling interval, or Azure OpenAI range
// aborts boot with an actionable message instead of throwing lazily on first use.
builder.Services.AddOptions<AzureStorageOptions>()
    .Bind(builder.Configuration.GetSection("AzureStorage"))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<PoWatch.Application.Options.AzureOpenAiOptions>()
    .Bind(builder.Configuration.GetSection("AzureOpenAi"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Durable, shared Data Protection keyring so BFF auth cookies survive recycle/scale-out.
builder.AddPoWatchDataProtection();

// OpenAPI document at /openapi/v1.json, emitted at OpenAPI 3.1.
builder.Services.AddOpenApi(options =>
    options.OpenApiVersion = Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_1);

// HybridCache for stats queries, tagged per user and evicted when a batch lands (entry lifetime: StatsEndpoints).
builder.Services.AddHybridCache();

// OpenTelemetry tracing (passes config so Azure Monitor exporter can be gated on connection string)
builder.Services.AddPoWatchTelemetry(builder.Configuration);

// Health checks: Azure Storage, plus Key Vault when it is enabled (served at /health).
builder.Services.AddPoWatchHealthChecks(featureFlags);
builder.Services.AddPoWatchRateLimiting(builder.Environment);

builder.Services.AddPoWatchApplication();
builder.Services.AddPoWatchInfrastructure();
// ponytail: in-memory recap response cache (200 MB cap), lost on restart; swap in Redis/SQL if that matters.
builder.Services.AddDistributedMemoryCache();
builder.Services.AddPoWatchRecapAi(builder.Configuration);
builder.Services.AddBrowserAiSelection();
builder.Services.AddPoWatchDigestEmail(builder.Configuration);
// Retention and the weekly digest run on a timer; the Test host leaves them off so suites stay deterministic.
if (!builder.Environment.IsEnvironment("Test"))
{
    builder.Services.AddHostedService<RetentionJob>();
    builder.Services.AddHostedService<DigestJob>();
}
builder.Services.AddSingleton<FluentValidation.IValidator<PoWatch.Shared.Models.IngestBatchDto>, IngestBatchValidator>();
builder.Services.AddSignalR();
builder.Services.AddSingleton<Microsoft.AspNetCore.SignalR.IUserIdProvider, CurrentUserIdProvider>();

// Global ProblemDetails middleware
builder.Services.AddProblemDetails();

// Auth: BFF cookie session + Microsoft Entra OIDC (when configured) + dev/test guest bypass.
builder.AddPoWatchAuthentication(featureFlags);

var app = builder.Build();

// X-Commit lets the deploy confirm which build is answering (`curl -sI /health/live`).
var commit = PoPlatform.Version.Split('+') is [_, var sha] ? sha : "unknown";
app.Use((context, next) =>
{
    context.Response.Headers["X-Commit"] = commit;
    return next(context);
});

// The API reference is a development aid: /openapi/v1.json and the Scalar UI at /scalar/v1.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi("/openapi/v1.json");
    app.MapScalarApiReference("/scalar/v1");
}

app.UsePoWatchExceptionHandler();

// Only enforce HTTPS redirect in non-development environments.
// In dev the API binds only to HTTP; the middleware cannot resolve the HTTPS port and
// emits a WRN on every request otherwise.
if (!app.Environment.IsDevelopment())
    app.UseHttpsRedirection();

// Before authentication, so responses the auth handlers end themselves (the OIDC callback) still carry one.
app.UseMiddleware<CorrelationIdMiddleware>();

// Auth middleware — always active (BFF cookie session + OIDC/guest schemes)
app.UseAuthentication();

app.UsePoWatchUserLogScope();
app.UseAuthorization();
// After authentication: the limiter partitions by the signed-in user.
app.UseRateLimiter();

app.MapPoWatchHealth();

app.UsePoWatchResponseHeaders();
// Static assets (WASM framework files, JS, CSS) must stay anonymous so the client can load and reach /login.
app.MapStaticAssets().AllowAnonymous();

app.MapPoWatchFeatures();
// Uniform cross-app liveness probe (see PoPlatform). Same shape in every Po app, which
// is what lets the portfolio dashboard poll them all and render one uptime grid.
app.MapPoLiveness();

// Fall back to the Blazor WASM entry point for all unmatched requests. Anonymous: the SPA host page
// must load for unauthenticated users so the client can render /login (the fallback authz policy would
// otherwise 401 the host page itself and make the app unreachable).
app.MapFallbackToFile("index.html").AllowAnonymous();

// An unknown API route is a 404, not the SPA host page with a 200 (the more specific pattern wins).
app.MapFallback("/api/{**rest}", () => Results.NotFound()).AllowAnonymous();

await app.RunAsync();

public partial class Program { }
