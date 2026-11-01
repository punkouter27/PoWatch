using System.Diagnostics;
using Serilog.Context;

namespace PoWatch.Api.Hosting;

/// <summary>
/// Middleware that ensures every request has a Correlation ID.
/// If the client sends X-Correlation-ID it is reused; otherwise a new GUID is generated.
/// The ID is added to the response header and pushed into Serilog's log context.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    private const string HeaderName = "X-Correlation-ID";

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var existing)
            ? existing.ToString()
            : Guid.NewGuid().ToString();

        context.Response.Headers[HeaderName] = correlationId;

        var sessionId = context.Request.Headers.TryGetValue("X-Session-ID", out var sessionHeader)
            ? sessionHeader.ToString()
            : context.Request.Cookies.TryGetValue("session", out var sessionCookie) ? sessionCookie : "n/a";

        using (LogContext.PushProperty("CorrelationId", correlationId))
        using (LogContext.PushProperty("SessionId", sessionId))
        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["SessionId"] = sessionId
        }))
        {
            _logger.LogDebug(
                "Request started. Method={Method} Path={Path} CorrelationId={CorrelationId}",
                context.Request.Method, context.Request.Path, correlationId);

            var sw = Stopwatch.StartNew();
            await _next(context);
            sw.Stop();

            _logger.LogDebug(
                "Request finished. StatusCode={StatusCode} DurationMs={DurationMs} CorrelationId={CorrelationId}",
                context.Response.StatusCode, sw.ElapsedMilliseconds, correlationId);
        }
    }
}
