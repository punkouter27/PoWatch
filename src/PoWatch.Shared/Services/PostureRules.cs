namespace PoWatch.Shared.Services;

/// <summary>How a person is holding themselves, read from body keypoints; Unknown when too little of them shows.</summary>
public enum Posture
{
    Unknown,
    Standing,
    Sitting,
    Lying,
}

/// <summary>
/// Reads a posture from the 17 COCO body keypoints the pose model returns for a person box
/// (detector-worker.js). Only the shoulders, hips and knees are used: joints, never a face. A camera
/// that shows someone from the chest up cannot tell sitting from standing, and says Unknown.
/// </summary>
public static class PostureRules
{
    public const int Keypoints = 17;

    /// <summary>x, y and confidence per keypoint.</summary>
    public const int ValuesPerKeypoint = 3;

    /// <summary>A joint the model is less sure of than this is treated as not seen.</summary>
    public const double MinScore = 0.35;

    /// <summary>A trunk leaning further than this from upright (degrees) is lying down.</summary>
    public const double LyingAngle = 55;

    /// <summary>
    /// Hip to knee, measured down the frame, as a share of the trunk's length. Standing, a thigh hangs
    /// about as long as the trunk; seated it is level (seen from the side) or points at the camera
    /// (seen from the front), and either way it covers little height.
    /// </summary>
    public const double SittingThighDrop = 0.55;

    private const int LeftShoulder = 5, RightShoulder = 6, LeftHip = 11, RightHip = 12, LeftKnee = 13, RightKnee = 14;

    /// <param name="keypoints">
    /// 17 × (x, y, score) in COCO order, x and y both in units of the frame's width, so that angles are true.
    /// </param>
    public static Posture Classify(IReadOnlyList<double> keypoints)
    {
        ArgumentNullException.ThrowIfNull(keypoints);
        if (keypoints.Count < Keypoints * ValuesPerKeypoint) return Posture.Unknown;

        if (Mid(keypoints, LeftShoulder, RightShoulder) is not { } shoulders || Mid(keypoints, LeftHip, RightHip) is not { } hips)
            return Posture.Unknown;

        var (dx, dy) = (hips.X - shoulders.X, hips.Y - shoulders.Y);
        var trunk = Math.Sqrt((dx * dx) + (dy * dy));
        if (trunk < 1e-6) return Posture.Unknown;

        var lean = Math.Atan2(Math.Abs(dx), Math.Abs(dy)) * 180 / Math.PI;
        if (lean > LyingAngle) return Posture.Lying;

        // Upright: the legs decide, and without knees there is nothing to go on.
        if (Mid(keypoints, LeftKnee, RightKnee) is not { } knees) return Posture.Unknown;
        return (knees.Y - hips.Y) / trunk < SittingThighDrop ? Posture.Sitting : Posture.Standing;
    }

    /// <summary>The middle of a left and right joint, or whichever one is seen; null when neither is.</summary>
    private static (double X, double Y)? Mid(IReadOnlyList<double> k, int left, int right)
    {
        var (l, r) = (left * ValuesPerKeypoint, right * ValuesPerKeypoint);
        var (hasLeft, hasRight) = (k[l + 2] >= MinScore, k[r + 2] >= MinScore);
        if (hasLeft && hasRight) return ((k[l] + k[r]) / 2, (k[l + 1] + k[r + 1]) / 2);
        if (hasLeft) return (k[l], k[l + 1]);
        if (hasRight) return (k[r], k[r + 1]);
        return null;
    }

    /// <summary>
    /// The posture a track has settled into: the commonest known one among its last few readings, when
    /// at least two agree, so one odd frame neither sits anyone down nor stands them up.
    /// </summary>
    public static Posture Settled(IEnumerable<Posture> recent)
    {
        ArgumentNullException.ThrowIfNull(recent);
        var best = recent.Where(p => p != Posture.Unknown)
            .GroupBy(p => p)
            .Select(g => (Posture: g.Key, Count: g.Count()))
            .OrderByDescending(g => g.Count)
            .FirstOrDefault();
        return best.Count >= 2 ? best.Posture : Posture.Unknown;
    }
}

/// <summary>
/// How long someone has been sitting without a break, for the "time to stand up" nudge. The room is
/// watched, not one track, so a person who is lost and found again keeps their streak. Standing for
/// <see cref="StandBreak"/>, or leaving the frame for <see cref="AwayBreak"/>, is a break.
/// </summary>
public sealed class SittingStreak(TimeSpan nudgeAfter)
{
    /// <summary>How long a sit lasts before the nudge, and between nudges; zero or less never nudges. May change mid-sit.</summary>
    public TimeSpan NudgeAfter { get; set; } = nudgeAfter;

    public static readonly TimeSpan StandBreak = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan AwayBreak = TimeSpan.FromMinutes(2);

    private DateTimeOffset? _sittingSince;
    private DateTimeOffset? _lastSitting;
    private DateTimeOffset? _standingSince;
    private int _nudges;

    /// <summary>How long the current sitting stretch has lasted; zero when nobody is in one.</summary>
    public TimeSpan Current => _sittingSince is { } since && _lastSitting is { } last ? last - since : TimeSpan.Zero;

    /// <summary>
    /// Takes one detector pass. Returns the stretch's length when it has just reached the next multiple
    /// of the nudge time (so a nudge is said once, then again each time that much more has gone by).
    /// </summary>
    public TimeSpan? Observe(DateTimeOffset atUtc, IReadOnlyCollection<Posture> people)
    {
        ArgumentNullException.ThrowIfNull(people);
        if (people.Contains(Posture.Sitting))
        {
            _sittingSince ??= atUtc;
            _lastSitting = atUtc;
            _standingSince = null;
        }
        else if (_sittingSince is not null)
        {
            // Lying down, or a posture nobody could read, neither counts as sitting nor ends it,
            // and the standing has to be unbroken to be a break.
            if (people.Contains(Posture.Standing)) _standingSince ??= atUtc;
            else _standingSince = null;
            var stoodUp = _standingSince is { } standing && atUtc - standing >= StandBreak;
            var wentAway = _lastSitting is { } last && atUtc - last >= AwayBreak;
            if (stoodUp || wentAway) Reset();
        }

        if (NudgeAfter <= TimeSpan.Zero || Current < NudgeAfter * (_nudges + 1)) return null;
        _nudges++;
        return Current;
    }

    private void Reset() => (_sittingSince, _lastSitting, _standingSince, _nudges) = (null, null, null, 0);
}
