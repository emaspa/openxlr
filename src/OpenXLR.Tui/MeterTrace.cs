namespace OpenXLR.Tui;

/// <summary>Stereo RMS levels and their held maxima, on the daemon's -60 to 0 dBFS scale.</summary>
internal readonly record struct MeterReading(double Left, double Right, double HoldLeft, double HoldRight)
{
    public double Level => Math.Max(Left, Right);
    public double Hold => Math.Max(HoldLeft, HoldRight);
}

/// <summary>
/// A bounded fifteen-second history and two hold markers. Time is monotonic
/// seconds, supplied by the connection so drawing never advances the data.
/// </summary>
internal sealed class MeterTrace
{
    public const int Samples = 150;
    public const double Interval = 0.1;
    private readonly double[] _history = new double[Samples];
    private readonly long[] _buckets = Enumerable.Repeat(long.MinValue, Samples).ToArray();
    private readonly Hold _left = new();
    private readonly Hold _right = new();
    private double _received = double.NegativeInfinity;

    public void Push(double left, double right, double now)
    {
        left = Clean(left);
        right = Clean(right);
        _left.Push(left, now);
        _right.Push(right, now);
        _received = now;
        long bucket = (long)Math.Floor(now / Interval);
        int at = (int)(bucket % Samples);
        double level = Math.Max(left, right);
        _history[at] = _buckets[at] == bucket ? Math.Max(_history[at], level) : level;
        _buckets[at] = bucket;
    }

    public MeterReading Read(double now)
    {
        bool fresh = now - _received < 1;
        return new(fresh ? _left.Level : 0, fresh ? _right.Level : 0,
            Math.Max(fresh ? _left.Level : 0, _left.Read(now)),
            Math.Max(fresh ? _right.Level : 0, _right.Read(now)));
    }

    /// <summary>Fixed time buckets, oldest first. Missing packets leave a gap.</summary>
    public double[] History(double now)
    {
        double[] result = new double[Samples];
        long last = (long)Math.Floor(now / Interval);
        for (int i = 0; i < Samples; i++)
        {
            long bucket = last - Samples + 1 + i;
            if (bucket < 0) continue;
            int at = (int)(bucket % Samples);
            if (_buckets[at] == bucket) result[i] = _history[at];
        }
        return result;
    }

    private static double Clean(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    private sealed class Hold
    {
        private double _peak;
        private double _until;
        public double Level { get; private set; }

        public void Push(double value, double now)
        {
            if (value >= Read(now))
            {
                _peak = value;
                _until = now + 1;
            }
            Level = value;
        }

        // One second at the maximum, then eighteen dB per second to zero.
        public double Read(double now) => Math.Max(0, _peak - Math.Max(0, now - _until) * 0.3);
    }
}
