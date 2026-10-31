using System.Security.Cryptography;
using System.Text;

namespace PoWatch.Domain.Models;

public enum SceneEventKind
{
    TrackEnter,
    TrackExit,
    LightsOn,
    LightsOff,
    Caption,
    Notable,

    /// <summary>Words heard by the on-device speech model; the text is what was said.</summary>
    Speech,

    /// <summary>A sound the on-device classifier recognised; the text is its label ("Dog", "Music").</summary>
    Sound
}

/// <summary>Which side of the frame a track crossed when it entered or left.</summary>
public enum FrameEdge
{
    None,
    Top,
    Right,
    Bottom,
    Left
}

/// <summary>A discrete moment posted alongside the ticks: a track coming or going, lights, a caption.</summary>
public sealed record SceneEvent
{
    public const int MaxTextLength = 500;

    public required Guid SessionId { get; init; }
    public required DateTimeOffset AtUtc { get; init; }
    public required SceneEventKind Kind { get; init; }
    public string? TrackId { get; init; }
    public string? Class { get; init; }
    public FrameEdge Edge { get; init; }
    public string? Text { get; init; }

    /// <summary>Optional strength in [0, 1] — detector confidence or the notable-moment score.</summary>
    public double? Score { get; init; }

    /// <summary>On <see cref="SceneEventKind.TrackExit"/>, how long the track was in frame.</summary>
    public double? DwellSeconds { get; init; }

    /// <summary>On track events, the regular the track was recognised as (once it was).</summary>
    public string? RegularId { get; init; }

    /// <summary>On <see cref="SceneEventKind.Notable"/>, the highlight snapshot's blob path, if one was kept.</summary>
    public string? ImagePath { get; init; }

    public IReadOnlyList<string> Validate(DateTimeOffset nowUtc)
    {
        var errors = new List<string>();

        if (SessionId == Guid.Empty)
            errors.Add($"{nameof(SessionId)} is required.");
        if (AtUtc > nowUtc + Tick.MaxClockSkew)
            errors.Add($"{nameof(AtUtc)} is more than {Tick.MaxClockSkew.TotalMinutes:0} minutes in the future.");
        if (Score is < 0 or > 1)
            errors.Add($"{nameof(Score)} must be in [0, 1].");
        if (ImagePath is { } path && (path.Length > 200 || path.Contains("..", StringComparison.Ordinal) || path.StartsWith('/')))
            errors.Add($"{nameof(ImagePath)} is not a snapshot path.");
        if (DwellSeconds is < 0)
            errors.Add($"{nameof(DwellSeconds)} cannot be negative.");
        if (Text is { Length: > MaxTextLength })
            errors.Add($"{nameof(Text)} is longer than {MaxTextLength} characters.");

        switch (Kind)
        {
            case SceneEventKind.TrackEnter or SceneEventKind.TrackExit:
                if (string.IsNullOrWhiteSpace(TrackId) || string.IsNullOrWhiteSpace(Class))
                    errors.Add($"{Kind} needs {nameof(TrackId)} and {nameof(Class)}.");
                break;
            case SceneEventKind.Caption or SceneEventKind.Notable or SceneEventKind.Speech or SceneEventKind.Sound:
                if (string.IsNullOrWhiteSpace(Text))
                    errors.Add($"{Kind} needs {nameof(Text)}.");
                break;
        }

        return errors;
    }

    /// <summary>
    /// A deterministic id from the event's identity, so a replayed batch writes the same row twice
    /// instead of two rows.
    /// </summary>
    public Guid StableId()
    {
        var key = $"{SessionId:N}|{AtUtc.UtcTicks}|{Kind}|{TrackId}|{Text}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return new Guid(hash.AsSpan(0, 16));
    }
}
