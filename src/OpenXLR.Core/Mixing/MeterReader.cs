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
    // Mean square of a -80 dBFS signal. A read below it is silence for LastSound.
    private const double SoundFloor = 1e-8;

    private readonly Dictionary<string, Meter> _meters = [];
    private readonly object _gate = new();
    private readonly Lazy<(int? Quantum, int? Rate)> _graph = new(ReadGraphSettings);
    private int? _forceQuantum;
    private CancellationTokenSource? _release;
    private bool _disposed;

    /// <summary>
    /// How long the meters hold PipeWire's default quantum after a forced one
    /// is cleared, before they stop forcing at all.
    /// </summary>
    internal TimeSpan ReleaseHold { get; init; } = TimeSpan.FromSeconds(2);

    private sealed class Meter
    {
        public required Process Process { get; init; }
        public string? SinkName { get; init; }  // set for a sink meter, which a new force restarts
        public double SumL, SumR;    // sums of squares since the last poll
        public long Count;
        public double DispL, DispR;  // smoothed display values
        public int EmptyPolls;       // polls since data last arrived
        public long LastSound;       // Environment.TickCount64 of the last read above SoundFloor
    }

    /// <summary>Begin metering a sink by monitoring its output.</summary>
    public void Add(string id, string sinkName)
    {
        lock (_gate) AddLocked(id, SinkMeter(sinkName), sinkName);
    }

    /// <summary>The capture process that meters <paramref name="sinkName"/>.</summary>
    internal ProcessStartInfo SinkMeter(string sinkName)
    {
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
        // followed by decay. The latency is also a quantum request to the
        // graph, so it matches the graph's own default (see LatencyMs).
        psi.ArgumentList.Add($"--latency-msec={LatencyMs(_graph.Value.Quantum, _graph.Value.Rate)}");
        // A meter reads one sink and must stay on it. The session manager
        // treats a stream that it linked to the default output as following
        // the default (target.node -1 in the default metadata) and moves it
        // when the default changes, but it never marks a stream in a link
        // group. Its own group, so the meter never shares a driver with
        // anything else. node.dont-reconnect would also stop the marking, but
        // WirePlumber 0.4 destroys such a stream when its target is not ready
        // yet, and a meter starts right after its sink is created.
        psi.ArgumentList.Add($"--property=node.link-group=openxlr-meter-{sinkName}");
        // A block size the user chose. Every meter shares the driver of the
        // sink it reads, so the force reaches the whole graph OpenXLR runs in.
        if (_forceQuantum is int quantum)
            psi.ArgumentList.Add($"--property=node.force-quantum={quantum}");
        return psi;
    }

    /// <summary>The quantum the sink meters force on their graph, or null.</summary>
    internal int? ForceQuantum { get { lock (_gate) return _forceQuantum; } }

    /// <summary>
    /// Force the graph the meters join to run at <paramref name="quantum"/>
    /// frames, or leave the choice to PipeWire again with null. A sink meter
    /// carries the force as node.force-quantum, so every one restarts with
    /// the new value; PipeWire reopens the driver at the new size, which is
    /// heard as a short gap. A device in IRQ mode (the pro-audio profile)
    /// keeps the period it was opened with once nothing forces it any more,
    /// so clearing a force first forces PipeWire's default quantum for
    /// <see cref="ReleaseHold"/> and only then stops forcing.
    /// </summary>
    public void SetForceQuantum(int? quantum)
    {
        lock (_gate)
        {
            if (_disposed) return;
            bool releasing = _release is not null;
            _release?.Cancel();
            _release = null;
            if (quantum is null && (_forceQuantum is not null || releasing))
            {
                int fallback = _graph.Value.Quantum ?? 1024;
                var release = new CancellationTokenSource();
                _release = release;
                RestartSinkMetersLocked(fallback);
                _ = Task.Delay(ReleaseHold, release.Token).ContinueWith(t =>
                {
                    lock (_gate)
                    {
                        if (t.IsCanceled || _release != release || _disposed) return;
                        _release = null;
                        RestartSinkMetersLocked(null);
                    }
                }, TaskScheduler.Default);
                return;
            }
            RestartSinkMetersLocked(quantum);
        }
    }

    private void RestartSinkMetersLocked(int? quantum)
    {
        if (_forceQuantum == quantum) return;
        _forceQuantum = quantum;
        foreach ((string id, Meter meter) in _meters.Where(m => m.Value.SinkName is not null).ToArray())
        {
            RemoveLocked(id);
            AddLocked(id, SinkMeter(meter.SinkName!), meter.SinkName);
        }
    }

    /// <summary>
    /// The latency a meter asks for, in milliseconds. pipewire-pulse turns it
    /// into the stream's node.latency, and PipeWire runs a graph at the
    /// smallest node.latency in it, rounded down to a power of two. Asking
    /// for the default quantum <paramref name="quantum"/> at
    /// <paramref name="rate"/>, rounded up to whole milliseconds, rounds back
    /// down to that quantum, so the meters never move the block size the
    /// rest of the graph runs at: a fixed 50 ms pushed a graph with no other
    /// request from 1024 to 2048 frames. At most 50 ms, so fragments still
    /// arrive faster than the 15 Hz poll. Without settings to read, 1024
    /// frames at 48 kHz, PipeWire's own default.
    /// </summary>
    internal static int LatencyMs(int? quantum, int? rate)
    {
        long q = quantum ?? 1024, r = rate ?? 48000;
        return (int)Math.Clamp((q * 1000 + r - 1) / r, 1, 50);
    }

    private static (int? Quantum, int? Rate) ReadGraphSettings()
    {
        try
        {
            string settings = ProcessRunner.Run("pw-metadata", ["-n", "settings"], TimeSpan.FromSeconds(2)).StdoutText;
            int? rate = null;
            try { rate = PipeWireAdapter.ParseGraphSampleRate(settings); }
            catch (InvalidOperationException) { /* keep the default */ }
            return (PipeWireAdapter.ParseGraphQuantum(settings), rate);
        }
        catch (Exception) { return (null, null); }
    }

    /// <summary>Begin metering with a capture process of the caller's choosing.</summary>
    internal void Add(string id, ProcessStartInfo psi)
    {
        lock (_gate) AddLocked(id, psi, null);
    }

    private void AddLocked(string id, ProcessStartInfo psi, string? sinkName)
    {
        if (_disposed || _meters.ContainsKey(id)) return;

        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception) { return; }
        if (p is null) return;

        var meter = new Meter { Process = p, SinkName = sinkName, LastSound = Environment.TickCount64 };
        _meters[id] = meter;
        // Take both streams here, while the gate still holds the process:
        // Remove and Dispose can retire the meter before either thread is
        // scheduled, and Process.StandardOutput on a disposed process
        // throws, on a raw thread with no handler above it. Reading a
        // stream that was closed underneath fails inside the loops, where
        // it is caught.
        Stream output = p.StandardOutput.BaseStream;
        Stream errors = p.StandardError.BaseStream;
        new Thread(() => Pump(meter, output)) { IsBackground = true, Name = $"meter-{id}" }.Start();
        // stderr must be drained: parec blocks once the pipe fills with
        // warnings, which silently freezes its audio output mid-stream.
        new Thread(() => Drain(errors)) { IsBackground = true, Name = $"meter-err-{id}" }.Start();
    }

    /// <summary>Stop metering one sink; its process goes with it.</summary>
    public void Remove(string id)
    {
        lock (_gate) RemoveLocked(id);
    }

    private void RemoveLocked(string id)
    {
        if (!_meters.Remove(id, out Meter? meter)) return;
        try { if (!meter.Process.HasExited) meter.Process.Kill(entireProcessTree: true); }
        catch (Exception) { /* already gone */ }
        meter.Process.Dispose();
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
            double l = BitConverter.ToSingle(buf, i);
            double r = BitConverter.ToSingle(buf, i + 4);
            // One non-finite sample from upstream must not poison the smoothing state
            // or the JSON meter message. Square finite samples as doubles so
            // even a finite float outside the normal audio range stays finite.
            if (double.IsFinite(l)) sumL += l * l;
            if (double.IsFinite(r)) sumR += r * r;
            frames++;
        }
        int rest = length - i;
        if (rest > 0) Buffer.BlockCopy(buf, i, buf, 0, rest);
        return (sumL, sumR, frames, rest);
    }

    private static void Drain(Stream errors)
    {
        var buf = new byte[4096];
        try { while (errors.Read(buf, 0, buf.Length) > 0) { } }
        catch (Exception) { /* process ended */ }
    }

    private void Pump(Meter meter, Stream stdout)
    {
        // A pipe read can end anywhere inside an 8-byte stereo frame; the
        // remainder is carried into the next read so the stream never goes
        // out of alignment (which would turn the meters into noise).
        var buf = new byte[8192];
        int carry = 0;
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
                if (IsSound(sumL, sumR, frames)) meter.LastSound = Environment.TickCount64;
            }
        }
    }

    /// <summary>
    /// Whether a read with these sums of squares over <paramref name="frames"/>
    /// stereo frames is sound, above -80 dBFS. A running mix delivers frames
    /// of zeros too, so frames alone are not sound.
    /// </summary>
    internal static bool IsSound(double sumL, double sumR, int frames) => sumL + sumR > SoundFloor * 2 * frames;

    /// <summary>
    /// When a meter last read sound above -80 dBFS, in
    /// <see cref="Environment.TickCount64"/> milliseconds, or the time it
    /// started until it first does. Null without that meter. Read() does not
    /// consume it, so it holds whether or not a client takes the levels.
    /// </summary>
    public long? LastSound(string id)
    {
        lock (_gate) return _meters.TryGetValue(id, out Meter? meter) ? meter.LastSound : null;
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

    /// <summary>
    /// A level in dBFS on the meters' scale, rounded as <see cref="Read"/>
    /// rounds: 0 at -60 dBFS and below, 1 at 0 dBFS, and 0 for null
    /// (silence), for a level a device measured itself.
    /// </summary>
    public static double DisplayOfDb(double? db)
    {
        if (db is not { } value || !double.IsFinite(value)) return 0;
        double display = Math.Clamp((value - FloorDb) / -FloorDb, 0, 1);
        return display < 0.004 ? 0 : Math.Round(display, 3);
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
