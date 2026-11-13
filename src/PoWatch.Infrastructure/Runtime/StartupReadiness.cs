namespace PoWatch.Infrastructure;

/// <summary>
/// Process-wide readiness snapshot for infrastructure dependencies that are initialised at boot.
///
/// Why: converting a dependency failure (e.g. Storage RBAC gap) into a host-construction throw produces an
/// opaque <strong>HTTP 500.30</strong> that hides the cause and takes the whole app — including /health,
/// /diag/boot, and log endpoints — offline. Instead, in non-Development environments we let the app boot and
/// record the failure here, so orchestrators see a live process that reports <em>unhealthy/not-ready</em>
/// with an actionable detail string, rather than a black-box crash loop.
/// </summary>
public sealed class StartupReadiness
{
    private volatile bool _storageReady = true;
    private volatile string _storageDetail = "Storage initialisation has not run yet.";

    /// <summary>True when Storage is either configured and reachable.</summary>
    public bool StorageReady => _storageReady;

    /// <summary>Human-readable detail describing the current storage readiness state.</summary>
    public string StorageDetail => _storageDetail;

    public void MarkStorageReady(string detail)
    {
        _storageDetail = detail;
        _storageReady = true;
    }

    public void MarkStorageFailed(string detail)
    {
        _storageDetail = detail;
        _storageReady = false;
    }
}
