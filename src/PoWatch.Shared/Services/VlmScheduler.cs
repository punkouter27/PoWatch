
namespace PoWatch.Shared.Services;

/// <summary>
/// Decides when the vision model runs next. The pixel layer's motion is smoothed and mapped through
/// <see cref="AdaptiveCadence"/>: a still room slows captions to save power, a busy one speeds them
/// up so the model sees fresh material.
/// </summary>
public sealed class VlmScheduler(TimeProvider time, int baseIntervalSeconds = 15)
{
    /// <summary>Pixel motion at or above this counts as "as busy as it gets" for the cadence.</summary>
    public const double BusyMotion = 0.15;

    private const double Smoothing = 0.2;

    private DateTimeOffset? _lastRunUtc;
    private double _motion;

    public TimeSpan Interval =>
        TimeSpan.FromSeconds(AdaptiveCadence.ScoreToIntervalSeconds(Math.Min(1, _motion / BusyMotion), baseIntervalSeconds));

    /// <summary>A scene the detector reports unchanged since the last caption is re-captioned only this often.</summary>
    public static readonly TimeSpan SameSceneEvery = TimeSpan.FromMinutes(2);

    private string? _lastScene;

    public bool IsDue => _lastRunUtc is not { } last || time.GetUtcNow() - last >= Interval;

    /// <summary>
    /// Due, and worth a caption: <paramref name="scene"/> (what the detector sees, or null when no
    /// detector runs) changed since the last one, or that caption is getting old. Captioning the same
    /// two people on the same couch every 15 s only buys near-duplicate sentences.
    /// </summary>
    public bool IsDueFor(string? scene) =>
        IsDue && (scene is null || scene != _lastScene || time.GetUtcNow() - _lastRunUtc >= SameSceneEvery);

    public void ObserveMotion(double motion) => _motion = (Smoothing * motion) + ((1 - Smoothing) * _motion);

    public void MarkRun(string? scene = null)
    {
        _lastRunUtc = time.GetUtcNow();
        _lastScene = scene;
    }
}
