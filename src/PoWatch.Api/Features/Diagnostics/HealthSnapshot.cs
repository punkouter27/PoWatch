using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoWatch.Infrastructure.Runtime;
using PoWatch.Shared.Models;

namespace PoWatch.Api.Features.Diagnostics;

/// <summary>
/// The one health report behind <c>/health</c> and <c>/api/diag</c>: the registered health checks
/// (storage, Key Vault) and the live service probes, run together and cached for <see cref="CacheFor"/>.
/// Every check has its own deadline (<see cref="PerCheckTimeout"/>: the health-check registrations and
/// <see cref="ServiceProbes"/>), so one stalled dependency cannot hold the report. Two loads of /diag inside
/// a minute show the same <c>checkedAt</c>, and no visitor can make the server call a dependency more than
/// once a minute.
/// </summary>
public sealed class HealthSnapshot(HealthCheckService health, ServiceProbes probes, TimeProvider time) : IDisposable
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    /// <summary>Longer than the probe client's 8 s timeout, which covers the connect retries (RetryingConnectHandler).</summary>
    public static readonly TimeSpan PerCheckTimeout = TimeSpan.FromSeconds(10);

    private readonly SnapshotCache<Report> _cache = new(time, CacheFor);

    /// <summary>The health checks' report and the service rows, taken at the same moment.</summary>
    public sealed record Report(HealthReport Health, DiagCheckDto[] Services);

    public Task<Snapshot<Report>> GetAsync(CancellationToken cancellationToken) =>
        _cache.GetAsync(async build =>
        {
            var checks = health.CheckHealthAsync(build);
            var services = probes.RunAsync(build);
            return new Report(await checks, await services);
        }, cancellationToken);

    public void Dispose() => _cache.Dispose();
}
