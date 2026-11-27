using PoWatch.Shared.Services;

namespace PoWatch.Unit;

public sealed class FaceTrendsTests
{
    private static FaceReadout Face(double? beauty, double? years) =>
        new(1, "Person 1", beauty, null, 0, null, 0, 1, years);

    private static FaceReadout FaceWith(int slot, string? presentation) =>
        Face(null, null) with { Slot = slot, Presentation = presentation, PresentationConfidence = 0.95 };

    [Fact]
    public void A_stretch_becomes_one_point_of_its_mean_over_every_face_once_the_next_starts()
    {
        var trends = new FaceTrends();
        trends.Observe([Face(50, 30)], 0);
        trends.Observe([Face(60, 40)], 2);
        Assert.Empty(trends.BeautyHistory);

        trends.Observe([Face(80, 20)], FaceTrends.BinSeconds);

        Assert.Equal([55.0], trends.BeautyHistory);
        Assert.Equal([35.0], trends.AgeHistory);
        Assert.Equal([1.0], trends.FacesHistory);

        // Two faces in one stretch average together and count as two.
        var two = new FaceTrends();
        two.Observe([Face(40, 20), Face(60, 50) with { Slot = 2 }], 0);
        two.Observe([], FaceTrends.BinSeconds);

        Assert.Equal([50.0], two.BeautyHistory);
        Assert.Equal([35.0], two.AgeHistory);
        Assert.Equal([2.0], two.FacesHistory);
    }

    [Fact]
    public void Empty_stretches_skip_beauty_and_age_and_the_session_mean_covers_the_rest()
    {
        var trends = new FaceTrends();
        trends.Observe([], 0);
        trends.Observe([], FaceTrends.BinSeconds);

        Assert.Empty(trends.BeautyHistory);
        Assert.Empty(trends.AgeHistory);
        Assert.Equal([0.0], trends.FacesHistory);
        Assert.Null(trends.SessionBeauty);

        // Session means and ranges cover every stretch that had a reading.
        trends.Observe([Face(40, null)], FaceTrends.BinSeconds * 2);
        trends.Observe([Face(70, null)], FaceTrends.BinSeconds * 3);
        trends.Observe([], FaceTrends.BinSeconds * 4);

        Assert.Equal(55, trends.SessionBeauty);
        Assert.Equal((40.0, 70.0), trends.BeautyRange);
        Assert.Null(trends.SessionAge);
        Assert.Null(trends.AgeRange);
    }

    [Fact]
    public void Lines_keep_only_the_latest_points()
    {
        var trends = new FaceTrends();
        for (var i = 0; i <= FaceTrends.HistoryLength + 5; i++) trends.Observe([Face(i, 30)], i * FaceTrends.BinSeconds);

        Assert.Equal(FaceTrends.HistoryLength, trends.BeautyHistory.Count);
        Assert.Equal(FaceTrends.HistoryLength + 4, trends.BeautyHistory[^1]);
    }

    [Fact]
    public void Expected_age_weights_each_band_middle_by_its_probability()
    {
        var readings = new FaceReadings();
        readings.OnLandmarks([1], new double[FaceReadings.MeshValues]);
        readings.OnReading(1, [0, 0, 0, 0, 0.5, 0.5, 0, 0, 0], [0.5, 0.5], [1f, 0f]);

        Assert.Equal(39.5, readings.InView[0].AgeYears);
    }

    [Fact]
    public void The_sex_split_is_the_share_of_the_faces_scored_in_a_stretch()
    {
        var trends = new FaceTrends();
        trends.Observe([FaceWith(1, "male"), FaceWith(2, "male"), FaceWith(3, "female")], 0);
        trends.Observe([FaceWith(1, null)], FaceTrends.BinSeconds);
        trends.Observe([], FaceTrends.BinSeconds * 2);

        Assert.Equal([66.7], trends.MaleHistory);
        Assert.Equal([33.3], trends.FemaleHistory);
        Assert.Equal((66.7, 33.3), trends.SessionSexSplit);
        Assert.True(FaceWith(1, "male").Male);
        Assert.False(FaceWith(1, "female").Male);
        Assert.Null(FaceWith(1, null).Male);
    }
}
