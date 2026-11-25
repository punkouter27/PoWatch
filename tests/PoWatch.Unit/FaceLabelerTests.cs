using PoWatch.Shared.Services;

namespace PoWatch.Unit;

public sealed class FaceLabelerTests
{
    /// <summary>A 2-D unit descriptor at an angle: two angles differ by the angle between them.</summary>
    private static float[] At(double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return [(float)Math.Cos(radians), (float)Math.Sin(radians)];
    }

    [Fact]
    public void The_same_face_keeps_its_label()
    {
        var labeler = new FaceLabeler();

        var first = labeler.Observe(At(0));
        var again = labeler.Observe(At(0));

        Assert.Equal("Person 1", first!.Value.Label);
        Assert.Equal("Person 1", again!.Value.Label);
        Assert.Equal(0, again.Value.Distance, 3);
        Assert.Equal(1, labeler.Count);
    }

    [Fact]
    public void A_different_face_gets_the_next_label()
    {
        var labeler = new FaceLabeler();

        Assert.Equal("Person 1", labeler.Observe(At(0))!.Value.Label);
        Assert.Equal("Person 2", labeler.Observe(At(90))!.Value.Label);
        Assert.Equal(2, labeler.Count);
    }

    [Fact]
    public void A_face_close_to_a_known_one_keeps_its_label()
    {
        var labeler = new FaceLabeler();
        labeler.Observe(At(0));

        var turned = labeler.Observe(At(26));

        Assert.Equal("Person 1", turned!.Value.Label);
        Assert.True(turned.Value.Distance <= FaceLabeler.MatchDistance);
    }

    [Fact]
    public void A_face_past_the_threshold_is_a_new_person()
    {
        var labeler = new FaceLabeler();
        labeler.Observe(At(0));

        var other = labeler.Observe(At(60));

        Assert.Equal("Person 2", other!.Value.Label);
        Assert.Equal(0, other.Value.Distance, 3);
        Assert.Equal(2, labeler.Count);
    }

    [Fact]
    public void Learning_follows_a_face_as_it_turns()
    {
        var labeler = new FaceLabeler();
        Assert.Equal("Person 1", labeler.Observe(At(0))!.Value.Label);
        Assert.Equal("Person 1", labeler.Observe(At(31))!.Value.Label);

        // 62° is too far from the first sighting to match it, but not from the face as it has been
        // learned since, which is the point of folding each observation in.
        var later = labeler.Observe(At(62));

        Assert.Equal("Person 1", later!.Value.Label);
        Assert.True(later.Value.Distance <= FaceLabeler.MatchDistance);
        Assert.Equal(1, labeler.Count);
    }

    [Fact]
    public void A_zero_descriptor_is_not_a_face()
    {
        var labeler = new FaceLabeler();

        Assert.Null(labeler.Observe([0, 0]));
        Assert.Equal(0, labeler.Count);
    }

    [Fact]
    public void A_non_finite_descriptor_is_not_a_face()
    {
        var labeler = new FaceLabeler();

        Assert.Null(labeler.Observe([float.NaN, 1]));
        Assert.Null(labeler.Observe([float.PositiveInfinity, 1]));
        Assert.Equal(0, labeler.Count);
    }

    [Fact]
    public void A_descriptor_of_a_different_width_is_refused()
    {
        var labeler = new FaceLabeler();
        labeler.Observe(At(0));

        Assert.Null(labeler.Observe([1, 0, 0]));
        Assert.Equal(1, labeler.Count);
    }

    [Fact]
    public void Only_MaxLabels_people_are_labelled()
    {
        var labeler = new FaceLabeler();
        for (var i = 0; i < FaceLabeler.MaxLabels; i++)
        {
            var one = new float[FaceLabeler.MaxLabels];
            one[i] = 1;
            Assert.NotNull(labeler.Observe(one));
        }

        // A face unlike all of them, arriving when the session is already full: unlabelled, not a 21st.
        var stranger = Enumerable.Repeat(1f, FaceLabeler.MaxLabels).ToArray();

        Assert.Equal(FaceLabeler.MaxLabels, labeler.Count);
        Assert.Null(labeler.Observe(stranger));
    }
}
