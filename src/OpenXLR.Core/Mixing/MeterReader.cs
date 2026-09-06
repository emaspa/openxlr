using System.Diagnostics;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// Live stereo level meters. One capture process per metered sink reads its
/// monitor as interleaved float samples; RMS is accumulated per channel between
/// polls and mapped to a dB scale, which tracks perceived loudness far better
/// than raw sample peaks (a single transient no longer pins the bar).
///
/// Display mapping: 0 = -60 dBFS and below, 1 = 0 dBFS. A short release keeps
/// bars falling smoothly instead of flickering.
/// </summary>
public sealed class MeterReader : IDisposable
{
    private const int SampleRate = 8000;
    private const double FloorDb = -60.0;
    private const double Release = 0.75;

    private readonly Dictionary<string, Meter> _meters = [];
    private readonly object _gate = new();
    private bool _disposed;

    private sealed class Meter
    {
        public required Process Process { get; init; }
        public double SumL, SumR;    // sums of squares since the last poll
        public long Count;
        public double DispL, DispR;  // smoothed display values
        public int EmptyPolls;       // polls since data last arrived
    }

    /// <summary>Begin metering a sink by monitoring its output.</summary>
    public void Add(string id, string sinkName)
    {
        lock (_gate)
        {
            if (_disposed || _meters.ContainsKey(id)) return;

            var psi = new ProcessStartInfo("parec")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-d"); psi.ArgumentList.Add($"{sinkName}.monitor");
            psi.ArgumentList.Add("--format=float32le");
            psi.ArgumentList.Add($"--rate={SampleRate}");
            psi.ArgumentList.Add("--channels=2");
            // Without an explicit latency parec batches its output in ~1 s
            // chunks, which turns a steady signal into a once-a-second spike
            // followed by decay. 50 ms delivers fragments faster than the
            // 15 Hz poll, so bars track the signal.
            psi.ArgumentList.Add("--latency-msec=50");

            Process? p;
            try { p = Process.Start(psi); }
            catch (Exception) { return; }
            if (p is null) return;

            var meter = new Meter { Process = p };
            _meters[id] = meter;
            new Thread(() => Pump(meter)) { IsBackground = true, Name = $"meter-{id}" }.Start();
            // stderr must be drained: parec blocks once the pipe fills with
            // warnings, which silently freezes its audio output mid-stream.
            new Thread(() => Drain(p)) { IsBackground = true, Name = $"meter-err-{id}" }.Start();
        }
    }

    /// <summary>
    /// Sum the squares of every complete stereo float frame in
    /// <paramref name="buf"/>[0..<paramref name="length"/>), move any trailing
    /// partial frame to the front of the buffer, and return its length so
    /// the next read appends after it.
    /// </summary>
    public static (double SumL, double SumR, int Frames, int Carry) AccumulateFrames(byte[] buf, int length)
    {
        double sumL = 0, sumR = 0;
        int frames = 0;
        int i = 0;
        for (; i + 8 <= length; i += 8)
        {
            float l = BitConverter.ToSingle(buf, i);
            float r = BitConverter.ToSingle(buf, i + 4);
            sumL += l * l;
            sumR += r * r;
            frames++;
        }
        int rest = length - i;
        if (rest > 0) Buffer.BlockCopy(buf, i, buf, 0, rest);
        return (sumL, sumR, frames, rest);
    }

    private static void Drain(Process p)
    {
        var buf = new byte[4096];
        try { while (p.StandardError.BaseStream.Read(buf, 0, buf.Length) > 0) { } }
        catch (Exception) { /* process ended */ }
    }

    private void Pump(Meter meter)
    {
        // A pipe read can end anywhere inside an 8-byte stereo frame; the
        // remainder is carried into the next read so the stream never goes
        // out of alignment (which would turn the meters into noise).
        var buf = new byte[8192];
        int carry = 0;
        Stream stdout = meter.Process.StandardOutput.BaseStream;
        while (!_disposed)
        {
            int read;
            try { read = stdout.Read(buf, carry, buf.Length - carry); }
            catch (Exception) { return; }
            if (read <= 0) return;

            (double sumL, double sumR, int frames, carry) = AccumulateFrames(buf, carry + read);

            lock (_gate)
            {
                meter.SumL += sumL;
                meter.SumR += sumR;
                meter.Count += frames;
            }
        }
    }

    /// <summary>
    /// Smoothed stereo levels per meter as [left, right] in 0..1 (dB scale).
    /// Call at a steady rate; each call consumes the window since the last one.
    /// </summary>
    public IReadOnlyDictionary<string, double[]> Read()
    {
        var result = new Dictionary<string, double[]>();
        lock (_gate)
        {
            foreach ((string id, Meter m) in _meters)
            {
                if (m.Count > 0)
                {
                    double newL = ToDisplay(Math.Sqrt(m.SumL / m.Count));
                    double newR = ToDisplay(Math.Sqrt(m.SumR / m.Count));
                    m.SumL = m.SumR = 0;
                    m.Count = 0;
                    m.EmptyPolls = 0;
                    // instant attack, smoothed release
                    m.DispL = Math.Max(newL, m.DispL * Release);
                    m.DispR = Math.Max(newR, m.DispR * Release);
                }
                else if (++m.EmptyPolls > 5)
                {
                    // Audio delivery is batched (~300 ms), so a few empty polls
                    // between batches are normal: hold the bar. Only release
                    // once the gap is clearly silence.
                    m.DispL *= Release;
                    m.DispR *= Release;
                }
                if (m.DispL < 0.004) m.DispL = 0;
                if (m.DispR < 0.004) m.DispR = 0;
                result[id] = [Math.Round(m.DispL, 3), Math.Round(m.DispR, 3)];
            }
        }
        return result;
    }

    private static double ToDisplay(double rms)
    {
        if (rms <= 0) return 0;
        double db = 20.0 * Math.Log10(rms);
        return Math.Clamp((db - FloorDb) / -FloorDb, 0, 1);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (Meter m in _meters.Values)
            {
                try { if (!m.Process.HasExited) m.Process.Kill(entireProcessTree: true); }
                catch (Exception) { /* already gone */ }
                m.Process.Dispose();
            }
            _meters.Clear();
        }
    }
}
