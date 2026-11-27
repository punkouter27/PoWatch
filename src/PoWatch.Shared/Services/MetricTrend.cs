namespace PoWatch.Shared.Services;

/// <summary>
/// One face metric over a session, read the same way as Beauty and Age: a mean per
/// <see cref="FaceTrends.BinSeconds"/> stretch (the line), a session-wide mean, the lowest and highest
/// stretch, and 1-minute averages for the live sparkline. Stretches with no reading are skipped, so the
/// line follows the faces rather than dropping to zero when the room is empty. Not thread-safe.
/// </summary>
/// <param name="digits">Decimal places kept on the line.</param>
public sealed class MetricTrend(int digits = 1)
{
    private readonly List<double> _history = [];
    private double _binSum;
    private long _binCount;
    private double _allSum;
    private long _allCount;

    /// <summary>Mean per stretch with a reading, oldest first, at most <see cref="FaceTrends.HistoryLength"/>.</summary>
    public IReadOnlyList<double> History => _history;

    /// <summary>1-minute averages and the running overall average, for the live sparkline.</summary>
    public MinuteSeries Minutes { get; } = new();

    /// <summary>Mean over every reading this session; null before the first.</summary>
    public double? Mean => _allCount == 0 ? null : _allSum / _allCount;

    /// <summary>The lowest and highest stretch on the line; null before the first point.</summary>
    public (double Low, double High)? Range => _history.Count == 0 ? null : (_history.Min(), _history.Max());

    /// <summary>One reading at <paramref name="seconds"/> since the session started; non-finite values are ignored.</summary>
    public void Add(double? value, double seconds)
    {
        if (value is not { } v || !double.IsFinite(v)) return;
        _binSum += v;
        _binCount++;
        _allSum += v;
        _allCount++;
        Minutes.Add(v, seconds);
    }

    /// <summary>Ends the open stretch: the line gets its mean if anything was read, and the stretch starts empty.</summary>
    public void Close()
    {
        if (_binCount > 0)
        {
            _history.Add(Math.Round(_binSum / _binCount, digits));
            if (_history.Count > FaceTrends.HistoryLength) _history.RemoveAt(0);
        }
        _binSum = 0;
        _binCount = 0;
    }
}
