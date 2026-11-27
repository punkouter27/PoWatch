using System.Diagnostics;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using PoWatch.Shared.Models;
using PoWatch.Shared.Services;

namespace PoWatch.Client.Services;

/// <summary>
/// What the camera reads from the faces in front of it (face-overlay.js): landmarks scored for
/// traditional beauty, age band, presentation, and cross-session matching against Azure Table Storage
/// (<c>POST /api/people/observe</c>) so returning people keep their anonymous "Person N" label and
/// bring their historical face &amp; body appearance stats into the new session.
/// </summary>
public sealed partial class SensingSession
{
    private static readonly TimeSpan PersonSyncEvery = TimeSpan.FromSeconds(15);

    /// <summary>The session the face overlay's readings go to ([JSExport] methods are static).</summary>
    private static SensingSession? s_faces;

    private FaceReadings _faces = new();
    private FaceTrends _faceTrends = new();
    private long _facesStarted = Stopwatch.GetTimestamp();
    private string _faceLabelKey = string.Empty;
    private readonly Dictionary<int, PersonProfileDto> _personBySlot = [];
    private readonly HashSet<int> _returningSlots = [];
    private readonly Dictionary<int, DateTimeOffset> _lastPersonSync = [];

    /// <summary>The faces in view right now, in slot order.</summary>
    public IReadOnlyList<FaceReadout> Faces => _faces.InView;

    /// <summary>Matched persistent anonymous person profile for a face slot, if matched in Azure Table Storage.</summary>
    public PersonProfileDto? PersonProfileFor(int slot) => _personBySlot.GetValueOrDefault(slot);

    /// <summary>True when this face slot matched a person seen in a previous session.</summary>
    public bool IsReturningPerson(int slot) => _returningSlots.Contains(slot);

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
        _personBySlot.Clear();
        _returningSlots.Clear();
        _lastPersonSync.Clear();
        s_faces = this;
    }

    private void StopFaces()
    {
        if (s_faces == this) s_faces = null;
        _faces = new FaceReadings();
        _faceTrends = new FaceTrends();
        _faceLabelKey = string.Empty;
        _personBySlot.Clear();
        _returningSlots.Clear();
        _lastPersonSync.Clear();
    }

    /// <summary>
    /// Hands face-overlay.js the label of every face in view, preferring the persistent cross-session
    /// "Person N" label when matched in Azure Table Storage.
    /// </summary>
    private void PushFaceLabels(IReadOnlyList<FaceReadout> faces)
    {
        var resolved = faces.Select(f => (f.Slot, Name: _personBySlot.GetValueOrDefault(f.Slot)?.DisplayName ?? f.Label ?? string.Empty)).ToList();
        var key = string.Join(',', resolved.Select(f => $"{f.Slot}:{f.Name}"));
        if (key == _faceLabelKey) return;
        _faceLabelKey = key;
        _ = js.TryInvokeVoidAsync("powatchFaces.labels",
            resolved.Select(f => (double)f.Slot).ToArray(),
            resolved.Select(f => f.Name).ToArray());
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
        if (s_faces is not { IsRunning: true, Paused: false } session) return;
        session._faces.OnLandmarks(slots, meshes);
        var faces = session._faces.InView;
        var activeSlots = faces.Select(f => f.Slot).ToHashSet();
        foreach (var gone in session._personBySlot.Keys.Where(s => !activeSlots.Contains(s)).ToList())
        {
            session._personBySlot.Remove(gone);
            session._returningSlots.Remove(gone);
            session._lastPersonSync.Remove(gone);
        }
        session._faceTrends.Observe(faces, Stopwatch.GetElapsedTime(session._facesStarted).TotalSeconds);
        session.PushFaceLabels(faces);
        session.Notify();
    }

    /// <summary>MediaPipe blendshape summary [smile, brow, blink, jawOpen, eyeWide, blinks counted so far] per face slot.</summary>
    [JSExport]
    [SupportedOSPlatform("browser")]
    internal static void OnFaceBlendshapes(
        [JSMarshalAs<JSType.Array<JSType.Number>>] double[] slots,
        [JSMarshalAs<JSType.Array<JSType.Number>>] double[] values)
    {
        if (s_faces is not { IsRunning: true, Paused: false } session) return;
        session._faces.OnBlendshapes(slots, values);
    }

    /// <summary>Forehead and cheek colour samples for the experimental heart rate (<see cref="FaceReadings.OnPulse"/>).</summary>
    [JSExport]
    [SupportedOSPlatform("browser")]
    internal static void OnFacePulse(
        [JSMarshalAs<JSType.Array<JSType.Number>>] double[] slots,
        [JSMarshalAs<JSType.Array<JSType.Number>>] double[] counts,
        [JSMarshalAs<JSType.Array<JSType.Number>>] double[] samples)
    {
        if (s_faces is not { IsRunning: true, Paused: false } session) return;
        session._faces.OnPulse(slots, counts, samples);
    }

    /// <summary>The face models' reading of one slot's face (<see cref="FaceReadings.OnReading"/>).</summary>
    [JSExport]
    [SupportedOSPlatform("browser")]
    internal static void OnFaceReading(
        double slot,
        [JSMarshalAs<JSType.Array<JSType.Number>>] double[] age,
        [JSMarshalAs<JSType.Array<JSType.Number>>] double[] presentation,
        [JSMarshalAs<JSType.Array<JSType.Number>>] double[] descriptor,
        [JSMarshalAs<JSType.Array<JSType.Number>>] double[] emotion,
        string? thumbnail)
    {
        if (s_faces is not { IsRunning: true, Paused: false } session) return;
        var slotId = (int)slot;
        var desc = Array.ConvertAll(descriptor, v => (float)v);
        if (!session._faces.OnReading(slotId, age, presentation, desc, emotion.Length > 0 ? emotion : null)) return;
        session.PushFaceLabels(session._faces.InView);
        session.Notify();

        var now = DateTimeOffset.UtcNow;
        if (!session._lastPersonSync.TryGetValue(slotId, out var last) || now - last >= PersonSyncEvery)
        {
            session._lastPersonSync[slotId] = now;
            _ = session.SyncPersonProfileAsync(slotId, desc, string.IsNullOrEmpty(thumbnail) ? null : thumbnail);
        }
    }

    private async Task SyncPersonProfileAsync(int slot, float[] descriptor, string? thumbnail = null)
    {
        var face = _faces.InView.FirstOrDefault(f => f.Slot == slot);
        if (face is null || Live.Session is not { } session) return;

        var singleBody = Bodies.InView.Count == 1;
        var request = new ObservePersonRequestDto
        {
            SessionId = session.Id,
            Descriptor = [.. descriptor],
            ThumbnailDataUrl = thumbnail,
            Beauty = face.Beauty,
            Symmetry = face.Symmetry,
            Golden = face.Golden,
            Thirds = face.Thirds,
            Fifths = face.Fifths,
            AgeYears = face.AgeYears,
            AgeBand = face.AgeBand,
            Presentation = face.Presentation,
            FaceShape = face.FaceShape,
            AttentionShare = face.AttentionShare,
            SmileScore = face.SmileScore,
            ConcentrationScore = face.ConcentrationScore,
            BlinkRatePerMin = face.BlinkRatePerMin,
            FatigueShare = face.FatigueShare,
            TalkingShare = face.TalkingShare,
            DistanceBand = face.DistanceBand,
            Mood = face.Mood,
            Emotion = face.Emotion,
            HeartRateBpm = face.HeartRateBpm,
            UprightScore = singleBody ? Bodies.PostureScore : null,
            MeanEnergy = singleBody ? Bodies.MeanEnergy : null,
            OpenPostureShare = singleBody && Bodies.ObservedSeconds >= 5 ? Bodies.OpenSeconds / Bodies.ObservedSeconds : null,
            ArmBalance = singleBody ? Bodies.ArmBalance : null,
            LegBalance = singleBody ? Bodies.LegBalance : null,
            ShoulderTilt = singleBody ? Bodies.ShoulderTilt : null
        };

        var result = await api.ObservePersonAsync(request);
        if (result is null || !IsRunning) return;

        _personBySlot[slot] = result.Person;
        if (result.ReturningFromPreviousSession || result.Person.SessionsSeen > 1)
            _returningSlots.Add(slot);
        PushFaceLabels(_faces.InView);
        Notify();
    }
}
