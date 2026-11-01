using PoWatch.Shared.Services;

namespace PoWatch.Unit;

/// <summary>Adaptive cadence mapping. The mapping is the load-bearing contract between the
/// JS-side frame diff and the managed-side poll loop: a noisy frame must ramp up the cadence,
/// a still frame must slow it down, and neither extreme may crash through the floor/ceiling.
/// </summary>
public sealed class AdaptiveCadenceTests
{
    [Fact]
    public void ScoreToIntervalSeconds_ClampsToFloor_WhenScoreIsZero_And_ScoreToIntervalSeconds_LandsNearActiveCeiling_WhenScoreIsOne()
    {
        // ScoreToIntervalSeconds_ClampsToFloor_WhenScoreIsZero
        {
            var interval = AdaptiveCadence.ScoreToIntervalSeconds(0.0, baseIntervalSeconds: 10);
            Assert.InRange(interval, AdaptiveCadence.ActiveCeilingSeconds + 1, AdaptiveCadence.IdleFloorSeconds + 1);

        }
        // ScoreToIntervalSeconds_LandsNearActiveCeiling_WhenScoreIsOne
        {
            var interval = AdaptiveCadence.ScoreToIntervalSeconds(1.0, baseIntervalSeconds: 10);
            Assert.InRange(interval, AdaptiveCadence.ActiveCeilingSeconds, AdaptiveCadence.ActiveCeilingSeconds + 6);

        }
    }

    [Fact]
    public void ScoreToIntervalSeconds_ClampsOutOfRangeScores_And_ScoreToIntervalSeconds_StaysWithinBounds_AcrossTheRange()
    {
        // ScoreToIntervalSeconds_ClampsOutOfRangeScores
        {
            // Scores outside [0, 1] are clamped, then mapped. A buggy JS layer passing 2.0 or -1.0
            // should land at the same intervals as 1.0 or 0.0 respectively, never below the floor
            // or above the ceiling.
            var positive = AdaptiveCadence.ScoreToIntervalSeconds(2.0, baseIntervalSeconds: 15);
            var clampedHigh = AdaptiveCadence.ScoreToIntervalSeconds(1.0, baseIntervalSeconds: 15);
            Assert.Equal(clampedHigh, positive);

            var negative = AdaptiveCadence.ScoreToIntervalSeconds(-1.0, baseIntervalSeconds: 15);
            var clampedLow = AdaptiveCadence.ScoreToIntervalSeconds(0.0, baseIntervalSeconds: 15);
            Assert.Equal(clampedLow, negative);

        }
        // ScoreToIntervalSeconds_StaysWithinBounds_AcrossTheRange
        {
            // Walk the full [0, 1] score range and confirm every output sits inside the floor/ceiling.
            for (var s = 0.0; s <= 1.0; s += 0.05)
            {
                var interval = AdaptiveCadence.ScoreToIntervalSeconds(s, baseIntervalSeconds: 12);
                Assert.InRange(interval, AdaptiveCadence.ActiveCeilingSeconds, AdaptiveCadence.IdleFloorSeconds);
            }

        }
    }

    [Fact]
    public void ScoreToIntervalSeconds_MonotonicInScore()
    {
        // Higher diff score → shorter interval. Pin the monotonicity property so a future
        // refactor of the blend formula cannot accidentally invert the relationship.
        var i0 = AdaptiveCadence.ScoreToIntervalSeconds(0.0, 10);
        var i1 = AdaptiveCadence.ScoreToIntervalSeconds(0.5, 10);
        var i2 = AdaptiveCadence.ScoreToIntervalSeconds(1.0, 10);

        Assert.True(i0 >= i1, $"Expected interval at score 0 ({i0}) to be >= interval at score 0.5 ({i1}).");
        Assert.True(i1 >= i2, $"Expected interval at score 0.5 ({i1}) to be >= interval at score 1.0 ({i2}).");
    }
}
