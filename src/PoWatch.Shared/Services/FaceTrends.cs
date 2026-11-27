namespace PoWatch.Shared.Services;

/// <summary>
/// The faces in view over one session as moving averages: every <see cref="BinSeconds"/> the mean of each
/// face metric (beauty, age, mood, smile, symmetry, proportions, blink rate, face length/width, heart
/// rate) over the faces seen in that stretch, the most faces at once, and how the scored faces split
/// male/female as a percentage. Session-wide means, the lowest and highest stretch, the session's emotion
/// mix and its most common face shape go with them. Aggregates only, in memory for the session (ADR 0001).
/// One instance per session: not thread-safe.
/// </summary>
/// <remarks>
/// Each metric gets a point only for stretches where a face had a reading for it, so the lines skip the
/// empty room rather than dropping to zero; the face count gets a point for every stretch with a frame.
/// The sex split is a percentage of the faces scored in the stretch, not a count.
/// </remarks>
public sealed class FaceTrends
{
    /// <summary>How many seconds go into one point on the lines.</summary>
    public const double BinSeconds = 5;

    /// <summary>Points kept per line: ten minutes at 5 s.</summary>
    public const int HistoryLength = 120;

    private readonly List<double> _faces = [];
    private readonly List<double> _male = [];
    private readonly List<double> _female = [];
    private readonly double[] _emotionSum = new double[FaceReadings.Emotions.Length];
    private readonly Dictionary<string, long> _shapes = new(StringComparer.Ordinal);
    private long _emotionReadings;
    private double? _binStart;
    private int _binFrames;
    private int _binMostFaces;
    private int _binMale;
    private int _binFemale;
    private long _allMale;
    private long _allFemale;

    /// <summary>Classical beauty canon score, 0–100.</summary>
    public MetricTrend Beauty { get; } = new();

    /// <summary>Expected age in years.</summary>
    public MetricTrend Age { get; } = new();

    /// <summary>Mood from the emotion mix, 0–100 (50 neutral).</summary>
    public MetricTrend Mood { get; } = new();

    /// <summary>Smile over the last few seconds, 0–100.</summary>
    public MetricTrend Smile { get; } = new();

    /// <summary>Left/right landmark symmetry, 0–100.</summary>
    public MetricTrend Symmetry { get; } = new();

    /// <summary>Mean of the φ, thirds and fifths proportion scores, 0–100.</summary>
    public MetricTrend Proportions { get; } = new();

    /// <summary>Blinks per minute over the last minute.</summary>
    public MetricTrend BlinkRate { get; } = new();

    /// <summary>Face length over width, the measure behind the face shape.</summary>
    public MetricTrend FaceRatio { get; } = new(3);

    /// <summary>Experimental camera heart rate in beats per minute.</summary>
    public MetricTrend HeartRate { get; } = new();

    /// <summary>Mean beauty per stretch with a scored face, oldest first.</summary>
    public IReadOnlyList<double> BeautyHistory => Beauty.History;

    /// <summary>1-minute average beauty and age series for per-minute live graphs.</summary>
    public MinuteSeries BeautyMinutes => Beauty.Minutes;
    public MinuteSeries AgeMinutes => Age.Minutes;

    /// <summary>Mean expected age in years per stretch with an aged face, oldest first.</summary>
    public IReadOnlyList<double> AgeHistory => Age.History;

    /// <summary>The most faces at once per stretch, oldest first.</summary>
    public IReadOnlyList<double> FacesHistory => _faces;

    /// <summary>Male share of the scored faces per stretch, 0-100; male + female = 100.</summary>
    public IReadOnlyList<double> MaleHistory => _male;

    /// <summary>Female share of the scored faces per stretch, 0-100.</summary>
    public IReadOnlyList<double> FemaleHistory => _female;

    /// <summary>Male and female share of every scored face this session, 0-100; null before one was scored.</summary>
    public (double Male, double Female)? SessionSexSplit =>
        _allMale + _allFemale == 0 ? null : (Math.Round(100.0 * _allMale / (_allMale + _allFemale), 1),
            Math.Round(100.0 * _allFemale / (_allMale + _allFemale), 1));

    /// <summary>Mean beauty over every frame of every face this session; null before one was scored.</summary>
    public double? SessionBeauty => Beauty.Mean;

    /// <summary>Mean expected age over every frame of every face this session; null before one was aged.</summary>
    public double? SessionAge => Age.Mean;

    /// <summary>The lowest and highest stretch on the beauty line; null before the first point.</summary>
    public (double Low, double High)? BeautyRange => Beauty.Range;

    /// <summary>The youngest and oldest stretch on the age line; null before the first point.</summary>
    public (double Low, double High)? AgeRange => Age.Range;

    /// <summary>
    /// Each emotion's share of every face-frame's mix this session, 0–100, largest first; empty before
    /// the emotion model read anyone.
    /// </summary>
    public IReadOnlyList<(string Emotion, double Share)> EmotionShares =>
        _emotionReadings == 0
            ? []
            : [.. FaceReadings.Emotions
                .Select((name, i) => (name, Math.Round(100.0 * _emotionSum[i] / _emotionReadings, 1)))
                .OrderByDescending(e => e.Item2)];

    /// <summary>The face shape read most often this session and its share of face-frames, 0–100.</summary>
    public (string Shape, double Share)? CommonShape
    {
        get
        {
            if (_shapes.Count == 0) return null;
            var total = _shapes.Values.Sum();
            var top = _shapes.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).First();
            return (top.Key, Math.Round(100.0 * top.Value / total, 1));
        }
    }

    private IEnumerable<MetricTrend> All => [Beauty, Age, Mood, Smile, Symmetry, Proportions, BlinkRate, FaceRatio, HeartRate];

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
            Beauty.Add(face.Beauty, seconds);
            Age.Add(face.AgeYears, seconds);
            Mood.Add(face.Mood, seconds);
            Smile.Add(face.SmileScore, seconds);
            Symmetry.Add(face.Symmetry * 100, seconds);
            Proportions.Add(face.Proportions * 100, seconds);
            BlinkRate.Add(face.BlinkRatePerMin, seconds);
            FaceRatio.Add(face.FaceRatio, seconds);
            HeartRate.Add(face.HeartRateBpm, seconds);
            if (face.EmotionMix is { Count: var n } mix && n == _emotionSum.Length)
            {
                for (var i = 0; i < n; i++) _emotionSum[i] += mix[i];
                _emotionReadings++;
            }
            if (!string.IsNullOrEmpty(face.FaceShape))
                _shapes[face.FaceShape] = _shapes.GetValueOrDefault(face.FaceShape) + 1;
            if (face.Male is { } male)
            {
                if (male) { _binMale++; _allMale++; }
                else { _binFemale++; _allFemale++; }
            }
        }
        _binFrames++;
        _binMostFaces = Math.Max(_binMostFaces, inView.Count);
    }

    /// <summary>Ends the open stretch: each line gets its point, and the stretch starts empty.</summary>
    private void Close()
    {
        if (_binFrames > 0) Push(_faces, _binMostFaces);
        foreach (var metric in All) metric.Close();
        if (_binMale + _binFemale > 0)
        {
            Push(_male, 100.0 * _binMale / (_binMale + _binFemale));
            Push(_female, 100.0 * _binFemale / (_binMale + _binFemale));
        }
        _binFrames = 0;
        _binMostFaces = 0;
        _binMale = 0;
        _binFemale = 0;
    }

    private static void Push(List<double> line, double value)
    {
        line.Add(Math.Round(value, 1));
        if (line.Count > HistoryLength) line.RemoveAt(0);
    }
}
