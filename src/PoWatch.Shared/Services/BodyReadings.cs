namespace PoWatch.Shared.Services;

/// <summary>What a body is doing, read from how its limbs moved over the last couple of seconds.</summary>
public enum BodyActivity
{
    Still,
    Gesturing,
    Waving,
    Stretching,
    Walking,
    Active,
}

/// <summary>One body in view right now.</summary>
/// <param name="Energy">How much the limbs are moving, 0 (still) to 1 (as much as the scale shows).</param>
/// <param name="Slumped">Null when posture could not be judged this frame.</param>
public sealed record BodyReadout(int Slot, BodyActivity Activity, double Energy, bool FullBody, bool? Slumped, BodyCues Cues);

/// <summary>
/// The bodies in front of the camera during one session (MediaPipe Pose, body-overlay.js) and what they
/// add up to: activity, posture score, limb energy, left/right balance and body language. Person-time is
/// summed over everyone in view. It lives in memory for the session and is never stored or sent
/// (docs/adr/0003-body-readings-on-device.md). One instance per session: not thread-safe.
/// </summary>
public sealed class BodyReadings
{
    /// <summary>A gap longer than this between two handovers (a hidden tab) is not counted as time.</summary>
    public const double MaxStepSeconds = 2;

    /// <summary>Limb speed, in trunk lengths per second, that reads as full energy.</summary>
    public const double FullEnergy = 1.5;

    /// <summary>Seconds of energy averaged into one point of <see cref="EnergyHistory"/>.</summary>
    public const double EnergyBinSeconds = 5;

    public const int EnergyHistoryLength = 120;

    /// <summary>Samples (half a second apart) a body's speeds are smoothed over.</summary>
    private const int SpeedWindow = 4;

    private static readonly int[] ArmsLeft = [BodyGeometry.LeftWrist, BodyGeometry.LeftElbow];
    private static readonly int[] ArmsRight = [BodyGeometry.RightWrist, BodyGeometry.RightElbow];
    private static readonly int[] LegsLeft = [BodyGeometry.LeftAnkle, BodyGeometry.LeftKnee];
    private static readonly int[] LegsRight = [BodyGeometry.RightAnkle, BodyGeometry.RightKnee];
    private static readonly int[] Limbs = [.. ArmsLeft, .. ArmsRight, .. LegsLeft, .. LegsRight];

    private readonly SortedDictionary<int, Tracked> _bodies = [];
    private readonly Dictionary<BodyActivity, double> _activity = [];
    private readonly Dictionary<BodyCues, double> _cues = [];
    private readonly List<double> _energyHistory = [];
    private double _binEnergy, _binSeconds;
    private double _armsLeft, _armsRight, _legsLeft, _legsRight;
    private double _tiltSum;
    private int _tiltCount;

    /// <summary>The bodies in view, in slot order.</summary>
    public IReadOnlyList<BodyReadout> InView => [.. _bodies.Where(p => p.Value.Readout is not null).Select(p => p.Value.Readout!)];

    /// <summary>Person-seconds per activity this session.</summary>
    public IReadOnlyDictionary<BodyActivity, double> ActivitySeconds => _activity;

    /// <summary>Person-seconds per body-language cue this session.</summary>
    public IReadOnlyDictionary<BodyCues, double> CueSeconds => _cues;

    /// <summary>Person-seconds a body was in view with shoulders and hips seen.</summary>
    public double ObservedSeconds { get; private set; }

    /// <summary>Person-seconds of those with no closed cue (arms crossed, hand to face).</summary>
    public double OpenSeconds { get; private set; }

    public double UprightSeconds { get; private set; }
    public double SlumpedSeconds { get; private set; }
    public double LongestSlumpSeconds { get; private set; }

    /// <summary>How long the body slumped longest right now has been; zero when nobody is.</summary>
    public double SlumpedNowSeconds => _bodies.Values.Select(b => b.SlumpRun).DefaultIfEmpty().Max();

    /// <summary>0–100: the share of judged posture time spent upright; null before any was judged.</summary>
    public double? PostureScore => UprightSeconds + SlumpedSeconds < 1 ? null : 100 * UprightSeconds / (UprightSeconds + SlumpedSeconds);

    /// <summary>Mean limb energy (0–1) per <see cref="EnergyBinSeconds"/> while someone was in view, oldest first.</summary>
    public IReadOnlyList<double> EnergyHistory => _energyHistory;

    /// <summary>1-minute average limb energy and uprightness series for per-minute live graphs.</summary>
    public MinuteSeries EnergyMinutes { get; } = new();
    public MinuteSeries UprightMinutes { get; } = new();

    /// <summary>Mean limb energy over the session; null before anyone was in view.</summary>
    public double? MeanEnergy { get; private set; }

    /// <summary>The left arm's share of all arm movement (0.5 is even); null until there has been enough.</summary>
    public double? ArmBalance => Share(_armsLeft, _armsRight);

    /// <summary>The left leg's share of all leg movement; null until there has been enough.</summary>
    public double? LegBalance => Share(_legsLeft, _legsRight);

    /// <summary>Mean shoulder tilt in degrees, positive when the left shoulder sits lower; null until seen facing the camera.</summary>
    public double? ShoulderTilt => _tiltCount < 10 ? null : _tiltSum / _tiltCount;

    /// <summary>Whether anyone has been seen with knees and ankles too.</summary>
    public bool FullBodySeen { get; private set; }

    private double _energySeconds, _energySum;

    /// <summary>
    /// One handover: a slot number per body and <see cref="BodyGeometry.ValuesPerBody"/> values each, at
    /// <paramref name="seconds"/> on any steadily increasing clock. Slots not handed over have left view.
    /// </summary>
    public void OnLandmarks(IReadOnlyList<double> slots, IReadOnlyList<double> values, double seconds)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(values);
        var seen = new HashSet<int>();
        var energies = new List<double>();
        double step = 0;
        for (var i = 0; i < slots.Count && (i + 1) * BodyGeometry.ValuesPerBody <= values.Count; i++)
        {
            var slot = (int)slots[i];
            seen.Add(slot);
            var offset = i * BodyGeometry.ValuesPerBody;
            if (!_bodies.TryGetValue(slot, out var body)) _bodies[slot] = body = new Tracked();
            var dt = body.LastSeconds is { } last ? seconds - last : 0;
            if (dt <= 0 || dt > MaxStepSeconds) dt = 0;
            step = Math.Max(step, dt);
            if (Update(slot, body, values, offset, seconds, dt) is { } energy) energies.Add(energy);
        }
        foreach (var gone in _bodies.Keys.Where(k => !seen.Contains(k)).ToList()) _bodies.Remove(gone);

        if (energies.Count > 0 && step > 0)
        {
            var avgEnergy = energies.Average();
            AddEnergy(avgEnergy, step);
            EnergyMinutes.Add(avgEnergy, seconds);
        }
        if (PostureScore is { } score) UprightMinutes.Add(score, seconds);
    }

    /// <returns>The body's energy when it could be measured.</returns>
    private double? Update(int slot, Tracked body, IReadOnlyList<double> values, int offset, double seconds, double dt)
    {
        var pose = BodyGeometry.Read(values, offset);
        var previous = body.Values;
        body.Values = [.. values.Skip(offset).Take(BodyGeometry.ValuesPerBody)];
        body.LastSeconds = seconds;
        if (!pose.UpperBody)
        {
            body.Readout = null;
            body.SlumpRun = 0;
            return null;
        }
        FullBodySeen |= pose.FullBody;

        var now = new BodyGeometry.Body(body.Values, 0);
        double? energy = null;
        if (previous is not null && dt > 0)
        {
            var before = new BodyGeometry.Body(previous, 0);
            double Travel(int point) => now.Seen(point) && before.Seen(point)
                ? Math.Sqrt(Math.Pow(now.X(point) - before.X(point), 2) + Math.Pow(now.Y(point) - before.Y(point), 2)) / pose.TrunkLength
                : double.NaN;

            var limbs = Limbs.Select(Travel).Where(v => !double.IsNaN(v)).ToList();
            var hips = (Travel(BodyGeometry.LeftHip) + Travel(BodyGeometry.RightHip)) / 2;
            if (double.IsNaN(hips)) hips = 0;
            var wrists = new[] { Travel(BodyGeometry.LeftWrist), Travel(BodyGeometry.RightWrist) }.Where(v => !double.IsNaN(v)).DefaultIfEmpty().Max();
            var ankles = new[] { Travel(BodyGeometry.LeftAnkle), Travel(BodyGeometry.RightAnkle) }.Where(v => !double.IsNaN(v)).DefaultIfEmpty().Average();
            body.Push(new Speeds(limbs.Count == 0 ? 0 : limbs.Average() / dt, hips / dt, wrists / dt, ankles / dt));

            Balance(ArmsLeft, ArmsRight, ref _armsLeft, ref _armsRight, Travel);
            Balance(LegsLeft, LegsRight, ref _legsLeft, ref _legsRight, Travel);
            energy = Math.Clamp(body.Smoothed.Limbs / FullEnergy, 0, 1);
        }

        var activity = Classify(now, body.Smoothed, pose);
        body.Readout = new BodyReadout(slot, activity, energy ?? body.Readout?.Energy ?? 0, pose.FullBody, pose.Slumped, pose.Cues);
        if (pose.ShoulderTilt is { } tilt)
        {
            _tiltSum += tilt;
            _tiltCount++;
        }
        if (dt <= 0) return energy;

        ObservedSeconds += dt;
        _activity[activity] = _activity.GetValueOrDefault(activity) + dt;
        foreach (var cue in Enum.GetValues<BodyCues>())
            if (cue != BodyCues.None && pose.Cues.HasFlag(cue)) _cues[cue] = _cues.GetValueOrDefault(cue) + dt;
        if ((pose.Cues & (BodyCues.ArmsCrossed | BodyCues.HandToFace)) == 0) OpenSeconds += dt;

        if (pose.Slumped == true)
        {
            SlumpedSeconds += dt;
            body.SlumpRun += dt;
            LongestSlumpSeconds = Math.Max(LongestSlumpSeconds, body.SlumpRun);
        }
        else
        {
            if (pose.Slumped == false) UprightSeconds += dt;
            body.SlumpRun = 0;
        }
        return energy;
    }

    /// <summary>
    /// Rules of thumb on the smoothed speeds (trunk lengths per second): walking moves the hips and feet,
    /// a wave is a fast hand above the shoulder, a stretch is both arms up and slow.
    /// </summary>
    public static BodyActivity Classify(Speeds s, bool anklesSeen, bool handAboveShoulder, bool bothHandsUp)
    {
        if (s.Hips > 0.5 || (anklesSeen && s.Hips > 0.2 && s.Ankles > 0.4)) return BodyActivity.Walking;
        if (s.Limbs > 1.0) return BodyActivity.Active;
        if (handAboveShoulder && s.Wrists > 0.8) return BodyActivity.Waving;
        if (bothHandsUp) return BodyActivity.Stretching;
        if (s.Wrists > 0.4) return BodyActivity.Gesturing;
        return BodyActivity.Still;
    }

    private static BodyActivity Classify(BodyGeometry.Body b, Speeds s, BodyPose pose)
    {
        bool Up(int wrist, int shoulder) => b.Seen(wrist) && b.Y(wrist) < b.Y(shoulder);
        var left = Up(BodyGeometry.LeftWrist, BodyGeometry.LeftShoulder);
        var right = Up(BodyGeometry.RightWrist, BodyGeometry.RightShoulder);
        return Classify(s, pose.FullBody, left || right, left && right);
    }

    private static void Balance(int[] left, int[] right, ref double leftSum, ref double rightSum, Func<int, double> travel)
    {
        var l = left.Select(travel).ToList();
        var r = right.Select(travel).ToList();
        // Only when both sides are seen, so a hidden arm is not read as a still one.
        if (l.Any(double.IsNaN) || r.Any(double.IsNaN)) return;
        leftSum += l.Sum();
        rightSum += r.Sum();
    }

    private static double? Share(double left, double right) => left + right < 2 ? null : left / (left + right);

    private void AddEnergy(double energy, double dt)
    {
        _energySum += energy * dt;
        _energySeconds += dt;
        MeanEnergy = _energySum / _energySeconds;
        _binEnergy += energy * dt;
        _binSeconds += dt;
        if (_binSeconds < EnergyBinSeconds) return;
        _energyHistory.Add(_binEnergy / _binSeconds);
        if (_energyHistory.Count > EnergyHistoryLength) _energyHistory.RemoveAt(0);
        _binEnergy = _binSeconds = 0;
    }

    /// <summary>Speeds in trunk lengths per second: all limbs' mean, the hips, the faster wrist, the ankles' mean.</summary>
    public readonly record struct Speeds(double Limbs, double Hips, double Wrists, double Ankles);

    private sealed class Tracked
    {
        private readonly Queue<Speeds> _speeds = new();

        public double[]? Values { get; set; }
        public double? LastSeconds { get; set; }
        public BodyReadout? Readout { get; set; }
        public double SlumpRun { get; set; }
        public Speeds Smoothed { get; private set; }

        public void Push(Speeds s)
        {
            _speeds.Enqueue(s);
            if (_speeds.Count > SpeedWindow) _speeds.Dequeue();
            Smoothed = new Speeds(_speeds.Average(x => x.Limbs), _speeds.Average(x => x.Hips), _speeds.Average(x => x.Wrists), _speeds.Average(x => x.Ankles));
        }
    }
}
