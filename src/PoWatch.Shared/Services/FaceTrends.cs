namespace PoWatch.Shared.Services;

/// <summary>
/// The faces in view over one session as moving averages: every <see cref="BinSeconds"/> the mean beauty
/// and mean expected age of the faces seen in that stretch, and the most faces at once. Session-wide
/// means and the lowest and highest stretch go with them. Aggregates only, in memory for the session,
/// never stored or sent (ADR 0001). One instance per session: not thread-safe.
/// </summary>
/// <remarks>
/// Beauty and age get a point only for stretches where a face was scored, so the lines skip the empty
/// room rather than dropping to zero; the face count gets a point for every stretch with a frame.
/// </remarks>
public sealed class FaceTrends
{
    /// <summary>How many seconds go into one point on the lines.</summary>
    public const double BinSeconds = 5;

    /// <summary>Points kept per line: ten minutes at 5 s.</summary>
    public const int HistoryLength = 120;

    private readonly List<double> _beauty = [];
    private readonly List<double> _age = [];
    private readonly List<double> _faces = [];
    private readonly Mean _binBeauty = new();
    private readonly Mean _binAge = new();
    private readonly Mean _allBeauty = new();
    private readonly Mean _allAge = new();
    private double? _binStart;
    private int _binFrames;
    private int _binMostFaces;

    /// <summary>Mean beauty per stretch with a scored face, oldest first.</summary>
    public IReadOnlyList<double> BeautyHistory => _beauty;

    /// <summary>Mean expected age in years per stretch with an aged face, oldest first.</summary>
    public IReadOnlyList<double> AgeHistory => _age;

    /// <summary>The most faces at once per stretch, oldest first.</summary>
    public IReadOnlyList<double> FacesHistory => _faces;

    /// <summary>Mean beauty over every frame of every face this session; null before one was scored.</summary>
    public double? SessionBeauty => _allBeauty.Value;

    /// <summary>Mean expected age over every frame of every face this session; null before one was aged.</summary>
    public double? SessionAge => _allAge.Value;

    /// <summary>The lowest and highest stretch on the beauty line; null before the first point.</summary>
    public (double Low, double High)? BeautyRange => _beauty.Count == 0 ? null : (_beauty.Min(), _beauty.Max());

    /// <summary>The youngest and oldest stretch on the age line; null before the first point.</summary>
    public (double Low, double High)? AgeRange => _age.Count == 0 ? null : (_age.Min(), _age.Max());

    /// <summary>One frame's faces at <paramref name="seconds"/> since the session started (any steady clock).</summary>
    public void Observe(IReadOnlyList<FaceReadout> inView, double seconds)
    {
        ArgumentNullException.ThrowIfNull(inView);
        if (!double.IsFinite(seconds)) return;
        _binStart ??= seconds;
        if (seconds - _binStart >= BinSeconds)
        {
            Close();
            _binStart = seconds;
        }

        foreach (var face in inView)
        {
            if (face.Beauty is { } beauty && double.IsFinite(beauty))
            {
                _binBeauty.Add(beauty);
                _allBeauty.Add(beauty);
            }
            if (face.AgeYears is { } years && double.IsFinite(years))
            {
                _binAge.Add(years);
                _allAge.Add(years);
            }
        }
        _binFrames++;
        _binMostFaces = Math.Max(_binMostFaces, inView.Count);
    }

    /// <summary>Ends the open stretch: each line gets its point, and the stretch starts empty.</summary>
    private void Close()
    {
        if (_binFrames > 0) Push(_faces, _binMostFaces);
        if (_binBeauty.Value is { } beauty) Push(_beauty, beauty);
        if (_binAge.Value is { } age) Push(_age, age);
        _binBeauty.Clear();
        _binAge.Clear();
        _binFrames = 0;
        _binMostFaces = 0;
    }

    private static void Push(List<double> line, double value)
    {
        line.Add(Math.Round(value, 1));
        if (line.Count > HistoryLength) line.RemoveAt(0);
    }

    private sealed class Mean
    {
        private double _sum;
        private long _count;

        public double? Value => _count == 0 ? null : _sum / _count;

        public void Add(double value)
        {
            _sum += value;
            _count++;
        }

        public void Clear() => (_sum, _count) = (0, 0);
    }
}
