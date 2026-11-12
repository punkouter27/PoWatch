using System.Globalization;
using Azure.Monitor.OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.ApplicationInsights.TelemetryConverters;

namespace PoWatch.Api.Hosting;

public static class TelemetrySetup
{
    /// <summary>
    /// Serilog two-stage initialisation delegate — reads config from appsettings after host is built.
    /// Always writes structured logs to Console (captured by App Service Log Stream) and to App Insights
    /// when the ApplicationInsights:ConnectionString setting is present. A rolling File sink is added ONLY
    /// in Development: on App Service the filesystem is ephemeral, the relative "logs/" path has no durable
    /// value, and a read-only CWD under ANCM could turn sink initialisation into a boot-time throw — one
    /// more 500.30 surface. Prod observability rides on stdout + App Insights instead.
    /// </summary>
    public static void ConfigureSerilog(
        HostBuilderContext ctx,
        IServiceProvider services,
        LoggerConfiguration cfg)
    {
        cfg.ReadFrom.Configuration(ctx.Configuration)
           .ReadFrom.Services(services)
           .Enrich.FromLogContext()
           .Enrich.WithProperty("Application", "PoWatch")
           .Enrich.WithProperty("Environment", ctx.HostingEnvironment.EnvironmentName)
           .WriteTo.Console(
               formatProvider: CultureInfo.InvariantCulture,
               outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{UserId}] {SourceContext}: {Message:lj}{NewLine}{Exception}");

        if (ctx.HostingEnvironment.IsDevelopment())
        {
            cfg.WriteTo.File(
                formatProvider: CultureInfo.InvariantCulture,
                path: "logs/powatch-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] [{UserId}] [{SessionId}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
        }

        // Structured logs — the UserSignedIn record among them — reach App Insights through THIS
        // sink and only this sink. The AddAzureMonitorTraceExporter() call in AddPoWatchTelemetry
        // exports OpenTelemetry activities (spans); it never sees an ILogger record, so before this
        // was wired the traces table stayed empty no matter what the app logged. Serilog also
        // replaces the default logging providers, which rules out the OTel logging pipeline here.
        var appInsightsConnectionString = PoPlatform.ResolveAppInsightsConnectionString(ctx.Configuration);
        cfg.WriteTo.Conditional(
            _ => !string.IsNullOrWhiteSpace(appInsightsConnectionString),
            sink => sink.ApplicationInsights(appInsightsConnectionString!, TelemetryConverter.Traces));
    }

    /// <summary>
    /// Registers OpenTelemetry tracing with ASP.NET Core instrumentation and an Azure Monitor (App
    /// Insights) exporter when the connection string is configured. There is no console exporter: it
    /// printed every span to stdout in every environment, burying the Serilog lines App Service's log
    /// stream shows, and the request log already says what a span would.
    /// </summary>
    public static IServiceCollection AddPoWatchTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        var aiConnectionString = PoPlatform.ResolveAppInsightsConnectionString(configuration);

        // Rule 6.2: map cloud_RoleName to the execution assembly via reflection (API host only),
        // so Azure Monitor never records "unknown_service:dotnet". In OpenTelemetry the
        // service.name resource attribute is projected onto cloud_RoleName by the Azure exporter.
        var roleName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "PoWatch";

        var isDev = string.Equals(configuration["ASPNETCORE_ENVIRONMENT"], "Development", StringComparison.OrdinalIgnoreCase);

        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(roleName))
            .WithTracing(tracing =>
            {
                tracing
                    .SetSampler(isDev ? new AlwaysOnSampler() : new TraceIdRatioBasedSampler(0.1))
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        options.RecordException = true;
                        options.Filter = ctx =>
                        {
                            var path = ctx.Request.Path.Value ?? string.Empty;
                            return !path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
                                && !path.StartsWith("/_framework/", StringComparison.OrdinalIgnoreCase)
                                && !path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase);
                        };
                    })
                    .AddSource("PoWatch.*");

                if (!string.IsNullOrWhiteSpace(aiConnectionString))
                {
                    tracing.AddAzureMonitorTraceExporter(o => o.ConnectionString = aiConnectionString);
                }
            });

        return services;
    }
}
