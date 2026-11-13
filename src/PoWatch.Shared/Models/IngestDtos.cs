namespace PoWatch.Shared.Models;

/// <summary>
/// About ten seconds of folded sensing, posted to <c>/api/sessions/{id}/batches</c>. The batch key
/// makes replays safe: a batch the server has already folded in is stored again but not re-counted.
/// </summary>
public sealed class IngestBatchDto
{
    public const int MaxTicks = 360;
    public const int MaxEvents = 2_000;

    public Guid BatchKey { get; init; }
    public List<TickDto> Ticks { get; init; } = [];
    public List<SceneEventDto> Events { get; init; } = [];
}

public sealed class TickDto
{
    public DateTimeOffset StartUtc { get; init; }
    public double DurationSeconds { get; init; } = 10;
    public int PixelSamples { get; init; }
    public int DetectorSamples { get; init; }
    public double MotionMean { get; init; }
    public double MotionMax { get; init; }
    public double LuminanceMean { get; init; }

    /// <summary>Microphone loudness in [0, 1] (−60 dBFS to 0), averaged and peaked over the tick.</summary>
    public double SoundMean { get; init; }

    public double SoundMax { get; init; }

    /// <summary>
    /// Person-seconds spent sitting, standing and lying down in the tick (two people sitting for ten
    /// seconds is twenty), read from body keypoints in the browser; all zero when posture is off or unreadable.
    /// </summary>
    public double SittingSeconds { get; init; }

    public double StandingSeconds { get; init; }

    public double LyingSeconds { get; init; }
    public List<int> Palette { get; init; } = [];
    public List<float> MotionGrid { get; init; } = [];
    public List<float> PresenceGrid { get; init; } = [];
    public Dictionary<string, ClassCountDto> Classes { get; init; } = [];
    public List<string> ActiveTrackIds { get; init; } = [];
}

public sealed class ClassCountDto
{
    public int Max { get; init; }
    public double Mean { get; init; }
}

public sealed class SceneEventDto
{
    public const int MaxTextLength = 300;
    public const int MaxNameLength = 80;

    public DateTimeOffset AtUtc { get; init; }

    /// <summary>TrackEnter, TrackExit, LightsOn, LightsOff, Caption, Notable, Speech (words heard) or Sound (a recognised sound's label).</summary>
    public string Kind { get; init; } = string.Empty;

    public string? TrackId { get; init; }
    public string? Class { get; init; }

    /// <summary>Top, Right, Bottom or Left for track enter/exit.</summary>
    public string? Edge { get; init; }

    public string? Text { get; init; }
    public double? Score { get; init; }
    public double? DwellSeconds { get; init; }

    /// <summary>The regular a track was recognised as, from <c>POST /api/regulars/observe</c>.</summary>
    public string? RegularId { get; init; }

    /// <summary>Snapshot blob path for a Notable event, from <c>POST /api/snapshots</c>.</summary>
    public string? ImagePath { get; init; }
}

/// <summary>Where to upload one highlight snapshot, and the path to cite in the Notable event.</summary>
public sealed class SnapshotUploadDto
{
    public string Path { get; init; } = string.Empty;
    public string UploadUrl { get; init; } = string.Empty;
}

/// <summary>A notable moment of a session, with a short-lived link to its snapshot when there is one.</summary>
public sealed class MomentDto
{
    public DateTimeOffset AtUtc { get; init; }
    public string Text { get; init; } = string.Empty;
    public double Score { get; init; }
    public string? ImageUrl { get; init; }
}

public sealed class IngestBatchResultDto
{
    public bool Accepted { get; init; }

    /// <summary>True when the batch had already been counted; it was stored again but not re-counted.</summary>
    public bool Replayed { get; init; }

    public int Ticks { get; init; }
    public int Events { get; init; }
    public List<string> Errors { get; init; } = [];
}
