using PoWatch.Shared.Services;

namespace PoWatch.Unit;

public sealed class CentroidTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 14, 0, 0, TimeSpan.Zero);

    private static Detection Box(string label, double x0, double y0, double x1, double y1, double score = 0.9) =>
        new(label, score, x0, y0, x1, y1);

    [Fact]
    public void A_walker_keeps_one_id_through_a_short_gap_and_leaves_with_a_dwell()
    {
        var tracker = new CentroidTracker();

        // Walks in from the left edge…
        var first = tracker.Update(T0, [Box("person", 0.00, 0.3, 0.10, 0.8)]);
        var entered = Assert.Single(first.Entered);
        Assert.Equal("Left", entered.Edge);
        Assert.Equal("person", entered.Label);
        var id = entered.TrackId;

        // …moves right, drops out for two seconds (occluded), reappears nearby: same track.
        Assert.Empty(tracker.Update(T0.AddSeconds(1), [Box("person", 0.08, 0.3, 0.20, 0.8)]).Entered);
        Assert.Empty(tracker.Update(T0.AddSeconds(3), []).Exited);
        var back = tracker.Update(T0.AddSeconds(3), [Box("person", 0.20, 0.3, 0.32, 0.8)]);
        Assert.Empty(back.Entered);
        Assert.Equal([id], back.Active.Select(t => t.TrackId));

        // Heads out through the right edge and stays gone past the gap: one exit with its dwell.
        for (var second = 4; second <= 10; second++)
        {
            var x0 = Math.Min(0.88, 0.10 * (second - 1));
            tracker.Update(T0.AddSeconds(second), [Box("person", x0, 0.3, x0 + 0.12, 0.8)]);
        }
        Assert.Empty(tracker.Update(T0.AddSeconds(12), []).Exited);
        var gone = tracker.Update(T0.AddSeconds(14), []);
        var exit = Assert.Single(gone.Exited);
        Assert.Equal(id, exit.TrackId);
        Assert.Equal("Right", exit.Edge);
        Assert.Equal(10, exit.DwellSeconds, 9);
        Assert.Empty(gone.Active);
    }

    [Fact]
    public void Classes_and_distant_boxes_get_their_own_tracks_and_presence_cells()
    {
        var tracker = new CentroidTracker();

        var frame = tracker.Update(T0,
        [
            Box("person", 0.40, 0.40, 0.60, 0.90),
            Box("cat", 0.42, 0.70, 0.55, 0.90),       // overlaps the person but is another class
            Box("person", 0.05, 0.40, 0.15, 0.90),    // same class, far away
            Box("cup", 0.70, 0.50, 0.75, 0.55),
            Box("person", 0.41, 0.41, 0.61, 0.91, score: 0.2), // below the confidence floor
        ]);

        Assert.Equal(4, frame.Active.Count);
        Assert.Equal(4, frame.Active.Select(t => t.TrackId).Distinct().Count());
        Assert.Equal(2, frame.Active.Count(t => t.Label == "person"));
        // Middle of the frame is nobody's edge.
        Assert.Contains(frame.Entered, e => e.Label == "cup" && e.Edge == "None");

        // Presence cells: people and the cat, not the cup. Centre (0.5, 0.65) → column 8, row 5.
        Assert.Equal(3, frame.PresenceCells.Count);
        Assert.Contains(5 * 16 + 8, frame.PresenceCells);
        Assert.Equal(new Dictionary<string, int> { ["person"] = 2, ["cat"] = 1, ["cup"] = 1 }, frame.CountsByLabel);
    }
}
