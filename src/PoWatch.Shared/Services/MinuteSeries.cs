namespace PoWatch.Shared.Services;

/// <summary>
/// Aggregates a time-varying numeric metric into 1-minute average buckets (up to 60 minutes)
/// alongside its running overall average, for live per-minute trend graphs.
/// </summary>
public sealed class MinuteSeries
{
    public const double BucketSeconds = 60;
    public const int MaxBuckets = 60;

    private readonly List<double> _closed = [];
    private double? _bucketStart;
    private double _bucketSum;
    private int _bucketCount;
    private double _allSum;
    private long _allCount;

    /// <summary>Overall mean across every recorded sample; null before the first sample.</summary>
    public double? Average => _allCount == 0 ? null : _allSum / _allCount;

    /// <summary>
    /// Closed 1-minute averages plus the open current-minute mean. Returns at least two points
    /// once any sample has arrived so a sparkline draws immediately and steps each minute.
    /// </summary>
    public IReadOnlyList<double> Points
    {
        get
        {
            if (_closed.Count == 0 && _bucketCount == 0) return [];
            var result = new List<double>(_closed.Count + 1);
            result.AddRange(_closed);
            if (_bucketCount > 0) result.Add(Math.Round(_bucketSum / _bucketCount, 3));
            if (result.Count == 1) result.Add(result[0]);
            return result;
        }
    }

    public void Add(double value, DateTimeOffset atUtc) => Add(value, atUtc.ToUnixTimeMilliseconds() / 1000.0);

    public void Add(double value, double seconds)
    {
        if (!double.IsFinite(value) || !double.IsFinite(seconds)) return;
        _bucketStart ??= seconds;
        if (seconds - _bucketStart.Value >= BucketSeconds)
        {
            if (_bucketCount > 0)
            {
                _closed.Add(Math.Round(_bucketSum / _bucketCount, 3));
                if (_closed.Count > MaxBuckets) _closed.RemoveAt(0);
            }
            _bucketSum = 0;
            _bucketCount = 0;
            _bucketStart = seconds;
        }

        _bucketSum += value;
        _bucketCount++;
        _allSum += value;
        _allCount++;
    }
}
