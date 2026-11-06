using PoWatch.Application.Features.Data;
using PoWatch.Application.Features.Digest;

namespace PoWatch.Api.Hosting;

/// <summary>
/// The server's two chores, each a periodic pass over the saved profiles in a fresh scope:
/// retention every six hours, the weekly digest every hour (it only mails what is due).
/// A failed pass is logged and the next one tries again.
/// </summary>
internal abstract class PeriodicJob(IServiceScopeFactory scopes, TimeProvider time, ILogger logger) : BackgroundService
{
    protected abstract TimeSpan FirstRunAfter { get; }

    protected abstract TimeSpan Every { get; }

    protected abstract Task RunAsync(IServiceProvider services, CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Out of the way of startup: storage is still being created and the first requests are landing.
            await Task.Delay(FirstRunAfter, time, stoppingToken);
            using var timer = new PeriodicTimer(Every, time);
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await RunAsync(scope.ServiceProvider, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "{Job} pass failed; the next one tries again.", GetType().Name);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}

/// <summary>Deletes raw detail older than each user's retention window (Settings → Kept on the server).</summary>
internal sealed class RetentionJob(IServiceScopeFactory scopes, TimeProvider time, ILogger<RetentionJob> logger) : PeriodicJob(scopes, time, logger)
{
    protected override TimeSpan FirstRunAfter => TimeSpan.FromMinutes(2);

    protected override TimeSpan Every => TimeSpan.FromHours(6);

    protected override Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<RetentionService>().PruneAllAsync(cancellationToken);
}

/// <summary>Mails last week's digest from Monday 08:00 local to everyone who turned the weekly email on.</summary>
internal sealed class DigestJob(IServiceScopeFactory scopes, IConfiguration configuration, TimeProvider time, ILogger<DigestJob> logger) : PeriodicJob(scopes, time, logger)
{
    protected override TimeSpan FirstRunAfter => TimeSpan.FromMinutes(3);

    protected override TimeSpan Every => TimeSpan.FromHours(1);

    protected override Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        // The email links back to History when the public address is known (Digest:AppUrl).
        var appUrl = Uri.TryCreate(configuration["Digest:AppUrl"], UriKind.Absolute, out var url) ? url : null;
        return services.GetRequiredService<DigestMailer>().SendDueAsync(appUrl, cancellationToken);
    }
}
