using PoWatch.Shared.Services;

namespace PoWatch.Unit;

public sealed class FaceReadingsTests
{
    /// <summary>A MediaPipe mesh holding <see cref="FaceGeometryTests.Face"/>'s 68 points at their mesh vertices.</summary>
    private static double[] Mesh(double[]? ibug = null)
    {
        ibug ??= FaceGeometryTests.Face();
        var mesh = new double[FaceReadings.MeshValues];
        for (var i = 0; i < MediaPipeFace.Ibug68.Length; i++)
        {
            mesh[MediaPipeFace.Ibug68[i] * 2] = ibug[i * 2];
            mesh[(MediaPipeFace.Ibug68[i] * 2) + 1] = ibug[(i * 2) + 1];
        }
        return mesh;
    }

    private static double[] Meshes(int count) => [.. Enumerable.Repeat(0, count).SelectMany(_ => Mesh())];

    private static readonly double[] Thirties = [0, 0, 0, 0.1, 0.7, 0.2, 0, 0, 0];
    private static readonly double[] SureFemale = [0.95, 0.05];
    private static readonly double[] Unsure = [0.6, 0.4];

    [Fact]
    public void MediaPipe_iBUG68_mapping_and_scoring_match()
    {
        Assert.Equal(FaceGeometry.Points, MediaPipeFace.Ibug68.Length);
        Assert.Equal(MediaPipeFace.Ibug68.Length, MediaPipeFace.Ibug68.Distinct().Count());
        // 468–477 are the irises, which no iBUG point is.
        Assert.All(MediaPipeFace.Ibug68, v => Assert.InRange(v, 0, 467));

        int[] midline = [152, 168, 197, 5, 4, 2, 0, 17, 13, 14];
        foreach (var ibug in new[] { 8, 27, 28, 29, 30, 33, 51, 57, 62, 66 })
            Assert.Contains(MediaPipeFace.Ibug68[ibug], midline);

        Assert.Equal(33, MediaPipeFace.Ibug68[36]);
        Assert.Equal(133, MediaPipeFace.Ibug68[39]);
        Assert.Equal(362, MediaPipeFace.Ibug68[42]);
        Assert.Equal(263, MediaPipeFace.Ibug68[45]);
        Assert.Equal(61, MediaPipeFace.Ibug68[48]);
        Assert.Equal(291, MediaPipeFace.Ibug68[54]);

        var direct = FaceGeometry.Read(FaceGeometryTests.Face())!.Value;
        var viaMesh = FaceGeometry.Read(MediaPipeFace.ToIbug68(Mesh())!)!.Value;
        Assert.Equal(direct, viaMesh);
        Assert.Null(MediaPipeFace.ToIbug68(new double[FaceReadings.MeshValues - 1]));
    }

    [Fact]
    public void A_face_in_view_gets_a_beauty_score_before_any_reading()
    {
        var faces = new FaceReadings();

        faces.OnLandmarks([1], Mesh());

        var face = Assert.Single(faces.InView);
        Assert.Equal(1, face.Slot);
        Assert.Equal(FaceGeometry.Read(FaceGeometryTests.Face())!.Value.Beauty, face.Beauty);
        Assert.Null(face.Label);
        Assert.Null(face.AgeBand);
        Assert.False(faces.HasRead);
    }

    [Fact]
    public void Beauty_moves_toward_a_new_frame_rather_than_jumping()
    {
        var faces = new FaceReadings();
        faces.OnLandmarks([1], Mesh());
        var first = faces.InView[0].Beauty!.Value;
        var wide = FaceGeometry.Read(FaceGeometryTests.Face(mouthWidth: 0.0972))!.Value.Beauty;

        faces.OnLandmarks([1], Mesh(FaceGeometryTests.Face(mouthWidth: 0.0972)));

        var shown = faces.InView[0].Beauty!.Value;
        Assert.InRange(shown, wide, first);
        Assert.Equal(first + (FaceReadings.BeautySmoothing * (wide - first)), shown, 1);
    }

    [Fact]
    public void A_face_that_leaves_view_is_dropped()
    {
        var faces = new FaceReadings();
        faces.OnLandmarks([1, 2], Meshes(2));

        faces.OnLandmarks([2], Mesh());

        Assert.Equal(2, Assert.Single(faces.InView).Slot);
        Assert.False(faces.OnReading(1, Thirties, SureFemale, [1, 0]));
    }

    [Fact]
    public void Reading_aggregates_age_presentation_and_ignores_invalid_widths()
    {
        // First reading sets label, age band, presentation.
        var faces = new FaceReadings();
        faces.OnLandmarks([1], Mesh());
        Assert.True(faces.OnReading(1, Thirties, SureFemale, [1, 0]));
        var face = Assert.Single(faces.InView);
        Assert.Equal("Person 1", face.Label);
        Assert.Equal("30–39", face.AgeBand);
        Assert.Equal(0.7, face.AgeConfidence);
        Assert.Equal("female", face.Presentation);
        Assert.Equal(1, face.Readings);
        Assert.True(faces.HasRead);

        // Averaged over readings: 20–29 averages 0.5 against 30–39's 0.4.
        faces.OnReading(1, [0, 0, 0, 0.9, 0.1, 0, 0, 0, 0], SureFemale, [1, 0]);
        Assert.Equal("20–29", faces.InView[0].AgeBand);
        Assert.Equal(0.5, faces.InView[0].AgeConfidence);

        // Unsure presentation is left unnamed.
        var unsure = new FaceReadings();
        unsure.OnLandmarks([1], Mesh());
        unsure.OnReading(1, Thirties, Unsure, [1, 0]);
        Assert.Null(unsure.InView[0].Presentation);
        Assert.Equal(0.6, unsure.InView[0].PresentationConfidence);

        // Wrong-width probability vectors are ignored.
        var bad = new FaceReadings();
        bad.OnLandmarks([1], Mesh());
        bad.OnReading(1, [1, 0], [0.5, 0.25, 0.25], [1, 0]);
        Assert.Null(bad.InView[0].AgeBand);
        Assert.Equal(0, bad.InView[0].PresentationConfidence);
    }

    [Fact]
    public void A_followed_face_keeps_its_label_as_it_turns()
    {
        var faces = new FaceReadings();
        faces.OnLandmarks([1], Mesh());
        faces.OnReading(1, Thirties, SureFemale, [1, 0]);

        // 0.4 away: too far to be recognised on its own, but the slot says who this is.
        faces.OnReading(1, Thirties, SureFemale, [0.6f, 0.8f]);

        Assert.Equal("Person 1", faces.InView[0].Label);
        Assert.Equal(2, faces.InView[0].Readings);
        Assert.Equal(1, faces.Labelled);
    }

    [Fact]
    public void Someone_else_in_the_same_spot_starts_the_slot_over()
    {
        var faces = new FaceReadings();
        faces.OnLandmarks([1], Mesh());
        faces.OnReading(1, Thirties, SureFemale, [1, 0]);

        // Past SwapDistance from Person 1: a new person, and none of Person 1's readings carry over.
        faces.OnReading(1, [0, 0, 0, 0, 0, 0, 0.8, 0.2, 0], [0.1, 0.9], [0, 1]);

        var face = faces.InView[0];
        Assert.Equal("Person 2", face.Label);
        Assert.Equal("50–59", face.AgeBand);
        Assert.Equal("male", face.Presentation);
        Assert.Equal(1, face.Readings);
    }

    [Fact]
    public void Two_faces_in_view_never_share_a_label()
    {
        var faces = new FaceReadings();
        faces.OnLandmarks([1, 2], Meshes(2));

        faces.OnReading(1, Thirties, SureFemale, [1, 0]);
        faces.OnReading(2, Thirties, SureFemale, [1, 0]);

        Assert.Equal(["Person 1", "Person 2"], faces.InView.Select(f => f.Label));
    }

    [Fact]
    public void Someone_who_comes_back_gets_their_label_back()
    {
        var faces = new FaceReadings();
        faces.OnLandmarks([1], Mesh());
        faces.OnReading(1, Thirties, SureFemale, [1, 0]);
        faces.OnLandmarks([], []);

        faces.OnLandmarks([7], Mesh());
        faces.OnReading(7, Thirties, SureFemale, [1, 0.05f]);

        Assert.Equal("Person 1", faces.InView[0].Label);
        Assert.Equal(1, faces.Labelled);
    }

    [Fact]
    public void Labeler_threshold_and_learn_rules()
    {
        var tight = new FaceLabeler(0.1);
        tight.Observe([1, 0]);
        Assert.Equal("Person 2", tight.Observe([0.71f, 0.71f])!.Value.Label);
        Assert.Equal(0.1, tight.Threshold);

        var labeler = new FaceLabeler();
        labeler.Observe([1, 0]);
        Assert.True(labeler.Learn("Person 1", [0, 1]));
        Assert.False(labeler.Learn("Person 9", [0, 1]));
        Assert.False(labeler.Learn("Person 1", [0, 0]));
    }
}
