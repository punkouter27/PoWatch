using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using PoWatch.Shared.Services;

namespace PoWatch.Client.Services;

/// <summary>
/// What the camera reads from the bodies in front of it (body-overlay.js, MediaPipe Pose): activity,
/// posture score, limb energy, left/right balance and body language. All of it is held in
/// <see cref="BodyReadings"/> for this session only, and never batched, stored or sent (ADR 0003).
/// </summary>
public sealed partial class SensingSession
{
    /// <summary>The session the body overlay's readings go to ([JSExport] methods are static).</summary>
    private static SensingSession? s_bodies;

    /// <summary>This session's body readings; empty when no camera session runs.</summary>
    public BodyReadings Bodies { get; private set; } = new();

    private void StartBodies()
    {
        Bodies = new BodyReadings();
        s_bodies = this;
    }

    private void StopBodies()
    {
        if (s_bodies == this) s_bodies = null;
        Bodies = new BodyReadings();
    }

    /// <summary>
    /// One handover: a slot number per body and per slot 33 points × (x, y, visibility, world x, y, z),
    /// at <paramref name="seconds"/> on the page's clock (<see cref="BodyReadings.OnLandmarks"/>).
    /// </summary>
    [JSExport]
    [SupportedOSPlatform("browser")]
    internal static void OnBodyLandmarks(
        [JSMarshalAs<JSType.Array<JSType.Number>>] double[] slots,
        [JSMarshalAs<JSType.Array<JSType.Number>>] double[] values,
        double seconds)
    {
        if (s_bodies is not { IsRunning: true } session) return;
        session.Bodies.OnLandmarks(slots, values, seconds);
        session.Notify();
    }
}
