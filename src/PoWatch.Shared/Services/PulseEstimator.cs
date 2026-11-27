namespace PoWatch.Shared.Services;

/// <summary>
/// Experimental camera heart rate (remote photoplethysmography) for one face. face-overlay.js samples
/// the mean colour of the forehead and both cheeks about 15 times a second; each heartbeat tints the
/// skin very slightly, so the colour wobbles at the pulse rate. This keeps the last
/// <see cref="WindowSeconds"/> of samples, combines the channels with the CHROM method (which cancels
/// most lighting and motion changes), and finds the strongest frequency between
/// <see cref="MinBpm"/> and <see cref="MaxBpm"/>. A peak that does not clearly stand out is not
/// reported, so poor light or a moving head gives no number rather than a wrong one.
/// </summary>
public sealed class PulseEstimator
{
    public const double WindowSeconds = 12;
    public const double MinSeconds = 8;
    public const double MinSampleHz = 6;
    public const double MinBpm = 45;
    public const double MaxBpm = 180;

    /// <summary>Share of the band's power that must sit within ±<see cref="PeakHalfWidthBpm"/> of the peak.</summary>
    public const double MinPeakShare = 0.22;

    public const double PeakHalfWidthBpm = 6;
    private const int Estimates = 5;
    private const int MissesBeforeLost = 6;
    private const double DetrendSeconds = 1.5;

    private readonly List<Sample> _samples = [];
    private readonly List<double> _estimates = [];
    private int _misses;

    /// <summary>Median of the recent confident estimates, in beats per minute; null while the signal is poor.</summary>
    public double? Bpm { get; private set; }

    /// <summary>How clearly the last peak stood out: its share of the band's power, 0–1.</summary>
    public double PeakShare { get; private set; }

    /// <summary>One colour sample (channel means, 0–255) at <paramref name="seconds"/> on any steady clock.</summary>
    public void Add(double seconds, double r, double g, double b)
    {
        if (!double.IsFinite(seconds) || !double.IsFinite(r) || !double.IsFinite(g) || !double.IsFinite(b)) return;
        if (r <= 1 || g <= 1 || b <= 1) return;
        if (_samples.Count > 0 && seconds <= _samples[^1].T) return;
        _samples.Add(new Sample(seconds, r, g, b));
        var cutoff = seconds - WindowSeconds;
        var stale = _samples.FindIndex(s => s.T >= cutoff);
        if (stale > 0) _samples.RemoveRange(0, stale);
    }

    /// <summary>Re-reads the window; call after each batch of samples.</summary>
    public void Estimate()
    {
        if (Read() is { } bpm)
        {
            _misses = 0;
            _estimates.Add(bpm);
            if (_estimates.Count > Estimates) _estimates.RemoveAt(0);
            var sorted = _estimates.OrderBy(v => v).ToList();
            Bpm = Math.Round(sorted[sorted.Count / 2], 0);
        }
        else if (++_misses >= MissesBeforeLost)
        {
            _estimates.Clear();
            Bpm = null;
        }
    }

    private double? Read()
    {
        var n = _samples.Count;
        if (n < 2) return null;
        var span = _samples[^1].T - _samples[0].T;
        if (span < MinSeconds || n < span * MinSampleHz) return null;

        // Each channel relative to its own mean, so brightness and skin tone fall away.
        double mr = 0, mg = 0, mb = 0;
        foreach (var s in _samples) { mr += s.R; mg += s.G; mb += s.B; }
        mr /= n; mg /= n; mb /= n;

        // CHROM chrominance signals, then slow drift (lighting, auto-exposure) removed with a moving mean.
        var x = new double[n];
        var y = new double[n];
        for (var i = 0; i < n; i++)
        {
            var (rn, gn, bn) = (_samples[i].R / mr, _samples[i].G / mg, _samples[i].B / mb);
            x[i] = (3 * rn) - (2 * gn);
            y[i] = (1.5 * rn) + gn - (1.5 * bn);
        }
        Detrend(x);
        Detrend(y);
        var sy = StdDev(y);
        var alpha = sy > 1e-12 ? StdDev(x) / sy : 0;
        var signal = new double[n];
        for (var i = 0; i < n; i++) signal[i] = x[i] - (alpha * y[i]);
        if (StdDev(signal) < 1e-9) return null;

        // Power at each whole bpm, straight from the timestamps (the samples are not evenly spaced).
        var bins = (int)(MaxBpm - MinBpm) + 1;
        var power = new double[bins];
        for (var k = 0; k < bins; k++)
        {
            var w = 2 * Math.PI * (MinBpm + k) / 60.0;
            double re = 0, im = 0;
            for (var i = 0; i < n; i++)
            {
                var phase = w * _samples[i].T;
                re += signal[i] * Math.Cos(phase);
                im += signal[i] * Math.Sin(phase);
            }
            power[k] = (re * re) + (im * im);
        }

        var peak = 0;
        for (var k = 1; k < bins; k++) if (power[k] > power[peak]) peak = k;
        var total = power.Sum();
        if (total <= 0) return null;
        var half = (int)PeakHalfWidthBpm;
        var near = 0.0;
        for (var k = Math.Max(0, peak - half); k <= Math.Min(bins - 1, peak + half); k++) near += power[k];
        PeakShare = Math.Round(near / total, 3);
        return PeakShare >= MinPeakShare ? MinBpm + peak : null;
    }

    private void Detrend(double[] values)
    {
        var copy = (double[])values.Clone();
        var from = 0;
        var to = 0;
        var sum = 0.0;
        for (var i = 0; i < copy.Length; i++)
        {
            var t = _samples[i].T;
            while (to < copy.Length && _samples[to].T <= t + (DetrendSeconds / 2)) sum += copy[to++];
            while (_samples[from].T < t - (DetrendSeconds / 2)) sum -= copy[from++];
            values[i] = copy[i] - (sum / (to - from));
        }
    }

    private static double StdDev(double[] values)
    {
        var mean = values.Average();
        var sum = 0.0;
        foreach (var v in values) sum += (v - mean) * (v - mean);
        return Math.Sqrt(sum / values.Length);
    }

    private readonly record struct Sample(double T, double R, double G, double B);
}
