using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PoWatch.Api.Security;
using PoWatch.Application.Options;
using Serilog.Context;

namespace PoWatch.Api.Hosting;

/// <summary>The middleware Program.cs runs on every request, each piece in the order it is used there.</summary>
internal static class RequestPipeline
{
    /// <summary>Unhandled exceptions become a ProblemDetails 500; the detail only when ExposeDebugDetailsInUi is on.</summary>
    public static WebApplication UsePoWatchExceptionHandler(this WebApplication app)
    {
        app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
        {
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("GlobalException");
            var flags = context.RequestServices.GetRequiredService<IOptions<FeatureFlagsOptions>>();
            var exception = context.Features.Get<IExceptionHandlerPathFeature>()?.Error;

            logger.LogError(exception, "Unhandled exception. Path={Path} TraceId={TraceId}", context.Request.Path, context.TraceIdentifier);

            var details = new ProblemDetails
            {
                Title = "An error occurred while processing the request.",
                Status = StatusCodes.Status500InternalServerError,
                Detail = flags.Value.ExposeDebugDetailsInUi
                    ? exception?.ToString()
                    : "An internal error occurred. See server logs for details.",
                Instance = context.Request.Path
            };
            details.Extensions["traceId"] = context.TraceIdentifier;

            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(details, ApiJson.Default.ProblemDetails);
        }));
        return app;
    }

    /// <summary>After authentication: every later log line, Serilog or ILogger scope, carries the caller's partition id.</summary>
    public static WebApplication UsePoWatchUserLogScope(this WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            var userId = CurrentUser.Id(ctx.User) ?? "anonymous";
            using (LogContext.PushProperty("UserId", userId))
            using (app.Logger.BeginScope(new Dictionary<string, object> { ["UserId"] = userId }))
            {
                await next(ctx);
            }
        });
        return app;
    }

    /// <summary>
    /// Cross-origin isolation unlocks SharedArrayBuffer, so the vision model's WASM backend runs
    /// multi-threaded on machines without WebGPU. "credentialless" (not require-corp) keeps the Hugging
    /// Face weights and SAS snapshot images loading without CORP headers on their side. App wwwroot/js
    /// and css files are not fingerprinted, so they revalidate on every request after a deployment.
    /// </summary>
    public static WebApplication UsePoWatchResponseHeaders(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            context.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
            context.Response.Headers["Cross-Origin-Embedder-Policy"] = "credentialless";

            if (context.Request.Path.StartsWithSegments("/js") || context.Request.Path.StartsWithSegments("/css"))
                context.Response.Headers["Cache-Control"] = "no-cache, must-revalidate";
            await next();
        });
        return app;
    }
}
