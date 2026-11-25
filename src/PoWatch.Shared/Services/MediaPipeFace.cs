namespace PoWatch.Shared.Services;

/// <summary>
/// Reads MediaPipe's Face Landmarker mesh (478 points) in the 68-point iBUG/300-W layout that
/// <see cref="FaceGeometry"/> scores. The mesh numbers its points differently, so each iBUG point is
/// the mesh vertex at the same place on the face; without this table the score would be read from
/// the wrong points and still look like a number.
/// </summary>
public static class MediaPipeFace
{
    /// <summary>Points in the mesh: 468 for the face and 10 for the irises.</summary>
    public const int MeshPoints = 478;

    /// <summary>
    /// The mesh vertex for each iBUG point, in iBUG order. Both layouts start on the subject's right,
    /// which is the left of an unmirrored camera frame.
    /// </summary>
    public static readonly int[] Ibug68 =
    [
        // 0–16 jaw, ear to ear through the chin (8)
        162, 234, 93, 58, 172, 136, 149, 148, 152, 377, 378, 365, 397, 288, 323, 454, 389,
        // 17–26 brows
        71, 63, 105, 66, 107, 336, 296, 334, 293, 301,
        // 27–30 nose bridge, 31–35 nostrils to nose base (33)
        168, 197, 5, 4, 75, 97, 2, 326, 305,
        // 36–41 right eye from the outer corner, 42–47 left eye from the inner corner
        33, 160, 158, 133, 153, 144, 362, 385, 387, 263, 373, 380,
        // 48–59 outer lips from the right corner, 60–67 inner lips
        61, 39, 37, 0, 267, 269, 291, 405, 314, 17, 84, 181, 78, 82, 13, 312, 308, 317, 14, 87,
    ];

    /// <summary>
    /// The 68 iBUG points, x then y, out of a mesh given as x then y per point in units of the
    /// frame's width; null when the mesh is shorter than <see cref="MeshPoints"/> points.
    /// </summary>
    public static double[]? ToIbug68(IReadOnlyList<double> mesh, int offset = 0)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (offset < 0 || mesh.Count - offset < MeshPoints * 2) return null;
        var points = new double[FaceGeometry.Points * FaceGeometry.ValuesPerPoint];
        for (var i = 0; i < Ibug68.Length; i++)
        {
            points[i * 2] = mesh[offset + (Ibug68[i] * 2)];
            points[(i * 2) + 1] = mesh[offset + (Ibug68[i] * 2) + 1];
        }
        return points;
    }
}
