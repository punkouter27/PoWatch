namespace PoWatch.Domain.Models;

public enum RollupGrain
{
    Minute,
    Hour,
    Day,
    AllTime
}

/// <summary>Count, sum, sum of squares, min and max: enough to merge means and standard deviations.</summary>
public sealed record RunningStat(long Count, double Sum, double SumSquares, double Min, double Max)
{
    public static readonly RunningStat Empty = new(0, 0, 0, double.PositiveInfinity, double.NegativeInfinity);

    public static RunningStat Of(double value) => new(1, value, value * value, value, value);

    public double Mean => Count == 0 ? 0 : Sum / Count;

    public double StdDev => Count < 2 ? 0 : Math.Sqrt(Math.Max(0, (SumSquares - (Sum * Sum / Count)) / (Count - 1)));

    public RunningStat Merge(RunningStat other) => new(
        Count + other.Count,
        Sum + other.Sum,
        SumSquares + other.SumSquares,
        Math.Min(Min, other.Min),
        Math.Max(Max, other.Max));
}

/// <summary>Finished visits of one regular in a bucket and their total time in frame.</summary>
public sealed record RegularTotals(long Visits, double DwellSeconds)
{
    public RegularTotals Merge(RegularTotals other) => new(Visits + other.Visits, DwellSeconds + other.DwellSeconds);
}

/// <summary>How often a class appeared in a bucket, its peak simultaneous count, and the summed per-tick means.</summary>
public sealed record ClassTotals(long TicksPresent, int PeakCount, double MeanSum)
{
    public ClassTotals Merge(ClassTotals other) =>
        new(TicksPresent + other.TicksPresent, Math.Max(PeakCount, other.PeakCount), MeanSum + other.MeanSum);
}
