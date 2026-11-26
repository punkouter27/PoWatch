using System.Diagnostics;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using PoWatch.Shared.Services;

namespace PoWatch.Client.Services;

/// <summary>
/// What the camera reads from the faces in front of it (face-overlay.js): landmarks scored for
/// traditional beauty, and the face models' age band, presentation and temporary label. All of it is
/// held in <see cref="FaceReadings"/> for this session only, and never batched, stored or sent.
/// </summary>
public sealed partial class SensingSession
{
    /// <summary>The session the face overlay's readings go to ([JSExport] methods are static).</summary>
    private static SensingSession? s_faces;

    private FaceReadings _faces = new();
    private FaceTrends _faceTrends = new();
    private long _facesStarted = Stopwatch.GetTimestamp();
    private string _faceLabelKey = string.Empty;

    /// <summary>The faces in view right now, in slot order.</summary>
    public IReadOnlyList<FaceReadout> Faces => _faces.InView;

    /// <summary>Mean beauty, age and face count over this session, in 5-second stretches.</summary>
    public FaceTrends FaceTrends => _faceTrends;

    /// <summary>Whether the face models have read anyone this session, so they have loaded.</summary>
    public bool FacesRead => _faces.HasRead;

    /// <summary>How many people this session has given a temporary label.</summary>
    public int FacesLabelled => _faces.Labelled;

    /// <summary>Starts this session's face readings from nobody known.</summary>
    private void StartFaces()
    {
        _faces = new FaceReadings();
        _faceTrends = new FaceTrends();
        _facesStarted = Stopwatch.GetTimestamp();
        _faceLabelKey = string.Empty;
        s_faces = this;
    }

    private void StopFaces()
    {
        if (s_faces == this) s_faces = null;
        _faces = new FaceReadings();
        _faceTrends = new FaceTrends();
        _faceLabelKey = string.Empty;
    }

    /// <summary>
    /// Hands face-overlay.js the label of every face in view, so each person in the picture wears their
    /// number and two of them are never confused. Pushed only when the labels change; the overlay drops a
    /// chip by itself when its face leaves.
    /// </summary>
    private void PushFaceLabels(IReadOnlyList<FaceReadout> faces)
    {
        var key = string.Join(',', faces.Select(f => $"{f.Slot}:{f.Label}"));
        if (key == _faceLabelKey) return;
        _faceLabelKey = key;
        _ = js.TryInvokeVoidAsync("powatchFaces.labels",
            faces.Select(f => (double)f.Slot).ToArray(),
            faces.Select(f => f.Label ?? string.Empty).ToArray());
    }

    /// <summary>
    /// One frame's faces: a slot number each, and per slot 478 points as x, y in units of the frame's
    /// width (<see cref="FaceReadings.OnLandmarks"/>).
    /// </summary>
    [JSExport]
    [SupportedOSPlatform("browser")]
    internal static void OnFaceLandmarks(
        [JSMarshalAs<JSType.Array<JSType.Number>>] double[] slots,
        [JSMarshalAs<JSType.Array<JSType.Number>>] double[] meshes)
    {
        if (s_faces is not { IsRunning: true } session) return;
        session._faces.OnLandmarks(slots, meshes);
        var faces = session._faces.InView;
        session._faceTrends.Observe(faces, Stopwatch.GetElapsedTime(session._facesStarted).TotalSeconds);
        session.PushFaceLabels(faces);
        session.Notify();
    }

    /// <summary>The face models' reading of one slot's face (<see cref="FaceReadings.OnReading"/>).</summary>
    [JSExport]
    [SupportedOSPlatform("browser")]
    internal static void OnFaceReading(
        double slot,
        [JSMarshalAs<JSType.Array<JSType.Number>>] double[] age,
        [JSMarshalAs<JSType.Array<JSType.Number>>] double[] presentation,
        [JSMarshalAs<JSType.Array<JSType.Number>>] double[] descriptor)
    {
        if (s_faces is not { IsRunning: true } session) return;
        if (!session._faces.OnReading((int)slot, age, presentation, Array.ConvertAll(descriptor, v => (float)v))) return;
        // A reading can name the face for the first time: who wears which number on the picture changes.
        session.PushFaceLabels(session._faces.InView);
        session.Notify();
    }
}
