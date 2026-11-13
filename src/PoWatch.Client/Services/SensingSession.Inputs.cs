using System.Globalization;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using PoWatch.Shared.Models;
using PoWatch.Shared.Services;

namespace PoWatch.Client.Services;

/// <summary>
/// What the browser reports (pixels, detector boxes, captions, sound and speech) and what the session does with it:
/// tracks, highlights and snapshots, watch-rule alerts, naming prompts, and the demo scene.
/// </summary>
public sealed partial class SensingSession
{
    /// <summary>A second of loudness from the microphone (audio-layer.js), in [0, 1].</summary>
    [JSInvokable]
    public void OnSoundLevel(double mean, double max)
    {
        if (IsRunning) _batcher.AddSound(time.GetUtcNow(), mean, max);
    }

    /// <summary>Words the speech model heard, starting at <paramref name="atIso"/>, with the speaker's voice embedding (JSON), if one was taken.</summary>
    [JSInvokable]
    public void OnHeard(string atIso, string text, string? voiceJson)
    {
        if (!IsRunning || string.IsNullOrWhiteSpace(text)) return;
        float[]? voice = null;
        try
        {
            voice = string.IsNullOrEmpty(voiceJson) ? null : JsonSerializer.Deserialize(voiceJson, Json.SingleArray);
        }
        catch (JsonException)
        {
            // The words still count without the voice.
        }
        AddHeard(At(atIso), text.Trim(), voice);
    }

    /// <summary>A sound the classifier named (an AudioSet label).</summary>
    [JSInvokable]
    public void OnSound(string atIso, string label, double score)
    {
        if (!IsRunning || string.IsNullOrWhiteSpace(label)) return;
        AddSound(At(atIso), label.Trim(), score);
    }

    private DateTimeOffset At(string iso) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : time.GetUtcNow();

    private void AddHeard(DateTimeOffset atUtc, string text, float[]? voice = null)
    {
        var line = text.Length > SceneEventDto.MaxTextLength ? text[..SceneEventDto.MaxTextLength] : text;
        var speaker = Speaker(voice);
        _batcher.AddSpeech(atUtc, line, speaker);
        Live.RecordHeard(atUtc, line, speaker is null ? null : Live.RegularNamed(speaker));
        Notify();
    }

    private void AddSound(DateTimeOffset atUtc, string label, double score)
    {
        _batcher.AddSoundEvent(atUtc, label, score);
        Live.RecordSound(atUtc, label);
        Notify();
    }

    [JSInvokable]
    public void OnPixelSample(string json)
    {
        if (!IsRunning || JsonSerializer.Deserialize(json, Json.PixelSamplePayload) is not { } p) return;
        AddPixel(new PixelSample(p.AtUtc, p.Motion, p.Luminance, p.Grid, p.Palette));
    }

    /// <summary>
    /// The detector's boxes as typed arrays (detector-bridge.js), not JSON: one label per box, and in
    /// <paramref name="values"/> (a Float64Array) per box its score, x0, y0, x1, y1, the length of its
    /// colour signature, the signature itself, the length of its body keypoints (0 when its pose was not
    /// read) and the keypoints (<see cref="PostureRules.Classify"/>).
    /// </summary>
    [JSExport]
    [SupportedOSPlatform("browser")]
    internal static void OnDetectorBoxes(
        [JSMarshalAs<JSType.Date>] DateTimeOffset atUtc,
        double latencyMs,
        [JSMarshalAs<JSType.Array<JSType.String>>] string[] labels,
        [JSMarshalAs<JSType.Array<JSType.Number>>] double[] values) =>
        s_detecting?.OnDetections(atUtc, latencyMs, labels, values);

    private void OnDetections(DateTimeOffset atUtc, double latencyMs, string[] labels, double[] values)
    {
        if (!IsRunning) return;
        var detections = new List<Detection>(labels.Length);
        var at = 0;
        foreach (var label in labels)
        {
            if (at + 6 > values.Length) break;
            var length = (int)values[at + 5];
            if (length < 0 || at + 6 + length > values.Length) break;
            float[]? signature = length == 0 ? null : new float[length];
            for (var i = 0; i < length; i++) signature![i] = (float)values[at + 6 + i];
            var next = at + 6 + length;
            if (next >= values.Length) break;
            var joints = (int)values[next];
            if (joints < 0 || next + 1 + joints > values.Length) break;
            Posture? posture = joints == 0 ? null : PostureRules.Classify(new ArraySegment<double>(values, next + 1, joints));
            detections.Add(new Detection(label, values[at], values[at + 1], values[at + 2], values[at + 3], values[at + 4], signature, posture));
            at = next + 1 + joints;
        }

        Live.DetectorLatencyMs = latencyMs;
        AddDetections(atUtc, detections);
    }

    [JSInvokable]
    public void OnDetectorError(string message)
    {
        Live.LastError = $"Detector: {message}";
        Cue("error");
        Notify();
    }

    private void AddPixel(PixelSample sample)
    {
        _batcher.AddPixel(sample);
        _vlm.ObserveMotion(sample.Motion);
        Live.RecordPixel(sample, time.GetUtcNow());
        Consider(_highlights.OnPixel(sample), sample.AtUtc);
        Notify();
    }

    private void AddDetections(DateTimeOffset atUtc, IReadOnlyList<Detection> detections)
    {
        var frame = _tracker.Update(atUtc, detections);
        _inFrame = [.. frame.Active.Select(t => t.Label)];
        _batcher.AddDetections(atUtc, frame, trackId => Live.RegularFor(trackId)?.Id);
        Live.RecordDetections(frame, time.GetUtcNow());
        foreach (var exited in frame.Exited) Live.ForgetTrack(exited.TrackId);
        foreach (var entered in frame.Entered) Announce(entered);
        Consider(_highlights.OnDetections(frame), atUtc);
        NudgeIfSittingLong(atUtc, frame);
        RecogniseStableTracks(frame);
        Live.ExpirePrompts(time.GetUtcNow());
        Notify();
    }

    private void AddCaption(DateTimeOffset atUtc, string raw, float[]? embedding = null)
    {
        if (CaptionParser.Parse(raw) is not { } caption) return;
        _batcher.AddCaption(atUtc, caption.Text, caption.Notable ? 1 : null);
        Live.RecordCaption(caption, atUtc);
        Consider(_highlights.OnCaption(caption, embedding), atUtc);
        Notify();
    }

    /// <summary>A soft blip from the side of the frame it entered on, and an alert for any watch rule it matches.</summary>
    private void Announce(TrackEntered track)
    {
        if (!CentroidTracker.IsPresence(track.Label)) return;
        var pan = track.Edge switch { "Left" => -0.8, "Right" => 0.8, _ => 0 };
        // The room's motion is the impact: an entrance in a busy scene lands harder than one in a quiet one.
        Cue("enter", pan, energy: Math.Min(1, Live.Motion * 4));
        // A regular is worth seeing, not just hearing: one small burst where they came in.
        if (Live.RegularFor(track.TrackId) is not null) _ = js.TryInvokeVoidAsync("powatchFx.burst", pan);

        var now = time.GetLocalNow();
        foreach (var rule in Rules.Where(r => r.Matches(track.Label, now)))
        {
            if (_lastAlert.TryGetValue(rule, out var last) && now - last < AlertCooldown) continue;
            _lastAlert[rule] = now;
            var text = $"{char.ToUpperInvariant(track.Label[0])}{track.Label[1..]} seen at {now:HH:mm}";
            Cue("alert");
            _ = js.TryInvokeVoidAsync("powatchCues.notify", text);
            // The hub relays it to every open tab of this user, this one included (the tray lists it).
            _ = api.RaiseAlertAsync(text);
        }
    }

    /// <summary>Says once, and again after each further stretch, that someone has sat for <see cref="SitNudgeMinutes"/> minutes without a break.</summary>
    private void NudgeIfSittingLong(DateTimeOffset atUtc, TrackerFrame frame)
    {
        var people = frame.Active.Where(t => string.Equals(t.Label, "person", StringComparison.OrdinalIgnoreCase)).Select(t => t.Posture).ToList();
        var due = _sitting.Observe(atUtc, people);
        Live.SittingFor = _sitting.Current;
        if (due is not { } sat) return;

        var text = $"Sitting for {(int)Math.Round(sat.TotalMinutes)} minutes without a break: time to stand up";
        Cue("alert");
        _ = js.TryInvokeVoidAsync("powatchCues.notify", text);
        _ = api.RaiseAlertAsync(text);
    }

    private void Cue(string name, double pan = 0, double depth = 0, double? energy = null) =>
        _ = js.TryInvokeVoidAsync("powatchCues.play", name, pan, depth, energy ?? 0.5);

    /// <summary>
    /// Records a highlight as a Notable moment. With a camera, a snapshot of the frame is uploaded
    /// first (the only image that ever leaves the browser); in demo mode, or without image storage,
    /// the moment is kept without a picture.
    /// </summary>
    private void Consider(Highlight? highlight, DateTimeOffset atUtc)
    {
        if (highlight is null) return;
        if (_scene is not null || _capturing || _video is null)
        {
            AddMoment(highlight, atUtc, imagePath: null);
            return;
        }

        _capturing = true;
        _ = CaptureAsync(highlight, atUtc);
    }

    private async Task CaptureAsync(Highlight highlight, DateTimeOffset atUtc)
    {
        // Offline or out of storage: the moment is kept without its picture.
        string? path = null;
        try
        {
            var image = await js.TryInvokeAsync<string>("powatchPixels.snapshot", _video);
            var upload = image is null ? null : await api.CreateSnapshotUploadAsync(DateOnly.FromDateTime(atUtc.ToLocalTime().DateTime));
            if (upload is not null && await js.TryInvokeVoidAsync("powatchBlobUpload.uploadFrame", upload.UploadUrl, image))
                path = upload.Path;
        }
        finally
        {
            _capturing = false;
        }

        AddMoment(highlight, atUtc, path);
    }

    private void AddMoment(Highlight highlight, DateTimeOffset atUtc, string? imagePath)
    {
        _batcher.AddEvent(new SceneEventDto { AtUtc = atUtc, Kind = "Notable", Text = highlight.Reason, Score = highlight.Score, ImagePath = imagePath });
        Notify();
    }

    /// <summary>Asks the server who each settled person or pet is, once per track.</summary>
    private void RecogniseStableTracks(TrackerFrame frame)
    {
        foreach (var track in frame.Active)
        {
            if (!CentroidTracker.IsPresence(track.Label) || track.Signature.Count == 0 || track.Age < RecogniseAfter) continue;
            if (Live.RegularFor(track.TrackId) is not null || !_observing.Add(track.TrackId)) continue;
            _ = RecogniseAsync(track);
        }
    }

    private async Task RecogniseAsync(TrackState track)
    {
        // Unanswered (offline or refused): this track stays unrecognised; it is asked again when it next enters.
        var result = await api.ObserveRegularAsync(new ObserveRegularRequestDto { Class = track.Label, Signature = [.. track.Signature] });
        if (result is null || !IsRunning) return;
        Live.AssignRegular(track.TrackId, result.Regular);

        if (result.IsNew && Nameable.Contains(track.Label))
        {
            var snapshot = _scene is null && _video is not null ? await js.TryInvokeAsync<string>("powatchPixels.snapshot", _video) : null;
            Live.AddPrompt(new NamingPrompt(result.Regular, snapshot, time.GetUtcNow()));
            Cue("regular");
        }

        Notify();
    }

    /// <summary>Saves a name from a prompt (or dismisses it when blank) and relabels live tracks.</summary>
    public async Task NameAsync(string regularId, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            Live.DismissPrompt(regularId);
            Notify();
            return;
        }

        if (await api.RenameRegularAsync(regularId, name) is { } renamed) Live.Rename(renamed);
        Notify();
    }

    private Task DemoTickAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var withCaption = _vlm.IsDue;
        var frame = _scene!.Next(now, withCaption);
        AddPixel(frame.Pixels);
        // The demo room is heard too, so the transcript and the story's sounds work without a microphone.
        var sound = _scene.Hear(now);
        _batcher.AddSound(now, sound.Level, sound.Level);
        if (sound.Speech is { } said) AddHeard(now, said);
        if (sound.Sound is { } label) AddSound(now, label, 0.8);
        if (_demoStep++ % 4 == 0) AddDetections(now, frame.Detections);
        if (frame.Caption is { } caption)
        {
            _vlm.MarkRun();
            AddCaption(now, caption);
        }

        Live.DetectorModel ??= "synthetic scene";
        return Task.CompletedTask;
    }

    private async Task VlmTickAsync(CancellationToken ct)
    {
        // The grounded prompt doubles as the scene key: same objects, same counts, same prompt.
        var prompt = CaptionParser.Prompt(_inFrame ?? []);
        var scene = _inFrame is null ? null : prompt;
        if (!Captions || _vlmBusy || !_vlm.IsDueFor(scene) || _video is null) return;
        _vlmBusy = true;
        try
        {
            _vlm.MarkRun(scene);
            var result = await js.TryInvokeAsync<VlmResultPayload>("powatchInference.captureAndInfer", prompt, _video, CaptionParser.MaxNewTokens, CaptionModel);
            if (result is { IsAvailable: true })
                AddCaption(time.GetUtcNow(), string.IsNullOrWhiteSpace(result.Caption) ? result.Activity : result.Caption, result.Embedding);
        }
        finally
        {
            _vlmBusy = false;
        }
    }

    private async Task LoadDetectorAsync(ElementReference video, CancellationToken ct)
    {
        var info = await js.TryInvokeAsync<DetectorInfoPayload>("powatchDetector.load");
        if (ct.IsCancellationRequested) return;
        if (info is null)
        {
            Live.LastError = "The object detector could not be loaded; pixel stats continue.";
            Notify();
            return;
        }

        Live.DetectorModel = info.ModelId;
        Live.DetectorDevice = info.Dtype is null ? info.Device : $"{info.Device} · {info.Dtype}";
        s_detecting = this;
        await js.TryInvokeVoidAsync("powatchDetector.start", video, _self, 1000, 0.5, ReadPosture);
        Notify();
    }
}
