namespace PoWatch.Shared.Services;

/// <summary>
/// Maps a frame-diff score to a poll interval. The score is the fraction of bytes that changed
/// beyond a small threshold between consecutive frames in the inference bridge. A near-zero
/// score means "the room is still" — slow the cadence; a high score means "something moved"
/// — ramp it back up so the next model call has fresh material.
///
/// The mapping is piecewise linear between four named regimes: Idle (slowest), Calm, Active,
/// Hot (fastest). Bounds are in seconds and are deliberately coarse: a 30s idle floor means the
/// model still runs at least twice a minute, so the alarm path is never starved of data even
/// when the scene is quiet.
/// </summary>
public static class AdaptiveCadence
{
    public const int IdleFloorSeconds = 30;
    public const int ActiveCeilingSeconds = 5;

    /// <summary>Map a frame-diff score in [0.0, 1.0] to a poll interval in seconds.</summary>
    public static int ScoreToIntervalSeconds(double score, int baseIntervalSeconds)
    {
        // Clamp the score so a noisy frame never produces a zero interval.
        var clamped = Math.Clamp(score, 0.0, 1.0);
        // Lower base = faster default. The scale factor remaps the [0, 1] score onto a
        // [ActiveCeiling, IdleFloor] range, then biases toward the base interval when the
        // score is in the middle so most polls land near what the operator configured.
        var intervalFromScore = IdleFloorSeconds
            - (IdleFloorSeconds - ActiveCeilingSeconds) * clamped;
        // Pull the score-derived interval toward the base interval by 50% — half the model's
        // direction, half the operator's setting. Avoids a wild swing on a single noisy frame.
        var blended = (intervalFromScore + baseIntervalSeconds) / 2.0;
        return Math.Clamp((int)Math.Round(blended), ActiveCeilingSeconds, IdleFloorSeconds);
    }
}
