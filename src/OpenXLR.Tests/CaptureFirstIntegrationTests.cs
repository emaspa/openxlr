using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// Elgato's full-speed units record silence when their playback stream
/// starts before capture, so the mixer links the interface's capture into
/// the XLR strip before it routes any mix into the interface's own sink.
/// A pw-link wrapper records the order in which the links are made.
/// </summary>
public sealed partial class MonitorVolumeIntegrationTests
{
    private const string Wave3Card = "usb-Elgato_Systems_Elgato_Wave_3_ORDERTEST-00";
    private const string Wave3Sink = "alsa_output." + Wave3Card + ".pro-output-0";
    private const string Wave3Source = "alsa_input." + Wave3Card + ".pro-input-0";
    private static readonly string[] Wave3Hints = ["Wave_3_ORDERTEST", "Elgato_Wave_3_"];

    [MonitorPipeWireFact]
    public void ADaemonMonitorOutputOnTheInterfaceIsLinkedAfterItsCapture()
    {
        using var links = new LinkLog();
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph(); using var mixer = new Mixer(pw);
        pw.CreateNullSink(Wave3Sink, "Wave 3 test output");
        WaitForInputPorts(pw, Wave3Sink, 2);
        WaveSource(pw, "test_wave3_parent", Wave3Source);
        mixer.SetInputDeviceHint(Wave3Hints, hardwareOutputRouting: false, modelFallback: false);

        // monitorOutput in daemon.json or OPENXLR_MONITOR_OUTPUT names the unit.
        links.Start();
        mixer.Build(HardwareInputConfig, monitorOutputSink: Wave3Sink);

        links.AssertCaptureFirst();
        Assert.Equal(2, IncomingRouteLinks(Xlr1).Length);
        Assert.Equal(2, GraphLinksInto(Wave3Sink).Length);
    }

    [MonitorPipeWireFact]
    public void TheRouteIntoTheInterfaceWaitsForItsCaptureToAppear()
    {
        using var links = new LinkLog();
        // A clock that stands still: the wait never runs out in this test.
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph(); using var mixer = new Mixer(pw) { Clock = () => 0 };
        // The card's sink is published before its capture source, as after a
        // replug or a profile switch.
        pw.CreateNullSink(Wave3Sink, "Wave 3 test output");
        WaitForInputPorts(pw, Wave3Sink, 2);
        mixer.SetInputDeviceHint(Wave3Hints, hardwareOutputRouting: false, modelFallback: false);
        links.Start();
        mixer.Build(HardwareInputConfig, monitorOutputSink: Wave3Sink);
        mixer.EnsureInputFeeds();
        mixer.EnsureMonitorRoutes();
        Assert.Empty(GraphLinksInto(Wave3Sink));

        WaveSource(pw, "test_wave3_late_parent", Wave3Source);
        // One sweep's order: the strips' feeds, then the monitor routes.
        WaitForRecovery(() =>
        {
            mixer.EnsureInputFeeds();
            mixer.EnsureMonitorRoutes();
            return IncomingRouteLinks(Xlr1).Length == 2 && GraphLinksInto(Wave3Sink).Length == 2;
        });
        links.AssertCaptureFirst();
    }

    [MonitorPipeWireFact]
    public void ACaptureThatNeverComesStopsHoldingTheOutputAfterTheGrace()
    {
        using var links = new LinkLog();
        long now = 0;
        var notes = new List<string>();
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw) { Clock = () => Volatile.Read(ref now), InputNote = notes.Add };
        pw.CreateNullSink(Wave3Sink, "Wave 3 test output");
        WaitForInputPorts(pw, Wave3Sink, 2);
        mixer.SetInputDeviceHint(Wave3Hints, hardwareOutputRouting: false, modelFallback: false);
        mixer.Build(HardwareInputConfig);
        // The user picks the interface as the monitor output.
        mixer.SetMonitorOutputs([Wave3Sink]);
        mixer.EnsureMonitorRoutes();
        Assert.Empty(GraphLinksInto(Wave3Sink));
        Assert.Empty(notes);

        Volatile.Write(ref now, (long)Mixer.CaptureFirstGrace.TotalMilliseconds);
        WaitForRecovery(() => { mixer.EnsureMonitorRoutes(); return GraphLinksInto(Wave3Sink).Length == 2; });
        Assert.Contains(notes, note => note.Contains(Wave3Sink, StringComparison.Ordinal));
        int noted = notes.Count;
        mixer.EnsureMonitorRoutes();
        Assert.Equal(noted, notes.Count);   // said once, not on every sweep
        Assert.Empty(IncomingRouteLinks(Xlr1));
    }

    /// <summary>
    /// A strip bound to another Elgato unit's node takes that unit's capture,
    /// so a route into that unit's own sink waits for the strip's capture as
    /// it does for the driven interface: the Wave:3 an additional interface
    /// or not, while the primary is another unit.
    /// </summary>
    [MonitorPipeWireFact]
    public void TheRouteIntoAUnitAStripIsBoundToWaitsForThatStripsCapture()
    {
        const string card = "usb-Elgato_Systems_Elgato_Wave_3_BOUNDTEST-00";
        const string sink = "alsa_output." + card + ".analog-stereo", source = "alsa_input." + card + ".mono-fallback";
        using var links = new LinkLog();
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph(); using var mixer = new Mixer(pw) { Clock = () => 0 };
        pw.CreateNullSink(sink, "Bound Wave 3 output");
        WaitForInputPorts(pw, sink, 2);
        // The primary is another unit, absent here; XLR 1 is bound to the Wave:3.
        mixer.SetInputDeviceHint(["Wave_XLR_PRIMARYTEST-"], hardwareOutputRouting: false, modelFallback: false);
        mixer.SetInputBindings(new Dictionary<string, SourceBinding> { ["xlr1"] = new(source, 0, 0) { Unit = "0fd9:0070@00000000000000b0" } });
        links.Start();
        mixer.Build(HardwareInputConfig, monitorOutputSink: sink);
        mixer.EnsureInputFeeds();
        mixer.EnsureMonitorRoutes();
        Assert.Empty(GraphLinksInto(sink));

        WaveSource(pw, "test_bound_wave3_parent", source);
        WaitForRecovery(() =>
        {
            mixer.EnsureInputFeeds();
            mixer.EnsureMonitorRoutes();
            return IncomingRouteLinks(Xlr1).Length == 2 && GraphLinksInto(sink).Length == 2;
        });
        links.AssertCaptureFirst(source, sink);
    }

    /// <summary>
    /// A capture that is linked is not yet running: the Wave:3 MK.2 kept its
    /// PipeWire source running while the kernel left its capture stopped. The
    /// route into the unit waits until the kernel says the capture runs.
    /// </summary>
    [MonitorPipeWireFact]
    public void ALinkedCaptureTheKernelHasNotStartedKeepsTheRouteWaiting()
    {
        using var links = new LinkLog();
        bool kernel = false;
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw)
        {
            Clock = () => 0,
            CaptureProbe = name => name == Wave3Source ? new CaptureRun(1, Alsa: true, NodeRunning: true, Volatile.Read(ref kernel)) : pw.CaptureRunOf(name),
        };
        pw.CreateNullSink(Wave3Sink, "Wave 3 test output");
        WaitForInputPorts(pw, Wave3Sink, 2);
        WaveSource(pw, "test_wave3_stopped_parent", Wave3Source);
        mixer.SetInputDeviceHint(Wave3Hints, hardwareOutputRouting: false, modelFallback: false);
        links.Start();
        mixer.Build(HardwareInputConfig, monitorOutputSink: Wave3Sink);
        mixer.EnsureInputFeeds();
        mixer.EnsureMonitorRoutes();
        Assert.Equal(2, IncomingRouteLinks(Xlr1).Length);
        Assert.Empty(GraphLinksInto(Wave3Sink));
        Assert.Null(mixer.DrivenUnitPlayback());

        Volatile.Write(ref kernel, true);
        WaitForRecovery(() => { mixer.EnsureMonitorRoutes(); return GraphLinksInto(Wave3Sink).Length == 2; });
        links.AssertCaptureFirst();
        Assert.Equal(new UnitPlayback(Wave3Source, Wave3Sink), mixer.DrivenUnitPlayback());
    }

    /// <summary>
    /// A unit that re-enumerates brings its sink back under the port names
    /// the route remembers, often before its capture. The route is not
    /// relinked until the capture runs again.
    /// </summary>
    [MonitorPipeWireFact]
    public void ARouteThatLostItsLinksWaitsForTheCaptureAgain()
    {
        bool kernel = true;
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw)
        {
            Clock = () => 0,
            CaptureProbe = name => name == Wave3Source ? new CaptureRun(1, Alsa: true, NodeRunning: true, Volatile.Read(ref kernel)) : pw.CaptureRunOf(name),
        };
        pw.CreateNullSink(Wave3Sink, "Wave 3 test output");
        WaitForInputPorts(pw, Wave3Sink, 2);
        WaveSource(pw, "test_wave3_relink_parent", Wave3Source);
        mixer.SetInputDeviceHint(Wave3Hints, hardwareOutputRouting: false, modelFallback: false);
        mixer.Build(HardwareInputConfig, monitorOutputSink: Wave3Sink);
        WaitForRecovery(() => { mixer.EnsureInputFeeds(); mixer.EnsureMonitorRoutes(); return GraphLinksInto(Wave3Sink).Length == 2; });

        // The unit drops off and comes back: its links are gone and its
        // capture has not started yet.
        Volatile.Write(ref kernel, false);
        pw.Unlink(new PortLink([.. GraphLinksInto(Wave3Sink)]));
        WaitForRecovery(() => GraphLinksInto(Wave3Sink).Length == 0);
        mixer.EnsureMonitorRoutes();
        mixer.EnsureMonitorRoutes();
        Assert.Empty(GraphLinksInto(Wave3Sink));

        Volatile.Write(ref kernel, true);
        WaitForRecovery(() => { mixer.EnsureMonitorRoutes(); return GraphLinksInto(Wave3Sink).Length == 2; });
    }

    /// <summary>
    /// The way back from a stalled capture: every mix comes off the unit's
    /// sink, the daemon resets the unit, and the mixes return only once the
    /// re-enumerated card's capture runs, under a new node.
    /// </summary>
    [MonitorPipeWireFact]
    public void ACaptureRecoveryKeepsThePlaybackOffUntilTheResetCardsCaptureRuns()
    {
        long now = 0;
        int node = 1;
        var notes = new List<string>();
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw)
        {
            Clock = () => Volatile.Read(ref now),
            InputNote = note => { lock (notes) notes.Add(note); },
            CaptureProbe = name => name == Wave3Source ? new CaptureRun(Volatile.Read(ref node), true, true, true) : pw.CaptureRunOf(name),
        };
        pw.CreateNullSink(Wave3Sink, "Wave 3 test output");
        WaitForInputPorts(pw, Wave3Sink, 2);
        WaveSource(pw, "test_wave3_recovery_parent", Wave3Source);
        mixer.SetInputDeviceHint(Wave3Hints, hardwareOutputRouting: false, modelFallback: false);
        mixer.Build(HardwareInputConfig, monitorOutputSink: Wave3Sink);
        WaitForRecovery(() => { mixer.EnsureInputFeeds(); mixer.EnsureMonitorRoutes(); return GraphLinksInto(Wave3Sink).Length == 2; });

        Assert.Equal(1, mixer.BeginCaptureRecovery());
        Assert.True(mixer.CaptureRecoveryActive);
        WaitForRecovery(() => GraphLinksInto(Wave3Sink).Length == 0);
        Assert.Null(mixer.DrivenUnitPlayback());
        // The old card's capture running is not the way back: the reset has
        // not happened yet, or has not reached the graph.
        mixer.EnsureInputFeeds();
        mixer.EnsureMonitorRoutes();
        Assert.Empty(GraphLinksInto(Wave3Sink));
        Assert.Equal(2, IncomingRouteLinks(Xlr1).Length);

        Volatile.Write(ref node, 2);
        WaitForRecovery(() => { mixer.EnsureInputFeeds(); mixer.EnsureMonitorRoutes(); return GraphLinksInto(Wave3Sink).Length == 2; });
        Assert.False(mixer.CaptureRecoveryActive);
        lock (notes) Assert.Contains(notes, note => note.Contains("runs again", StringComparison.Ordinal));

        // A capture that never comes back stops holding the mixes after the limit.
        Assert.Equal(1, mixer.BeginCaptureRecovery());
        WaitForRecovery(() => GraphLinksInto(Wave3Sink).Length == 0);
        mixer.EnsureMonitorRoutes();
        Assert.Empty(GraphLinksInto(Wave3Sink));
        Volatile.Write(ref now, (long)Mixer.CaptureRecoveryLimit.TotalMilliseconds);
        WaitForRecovery(() => { mixer.EnsureMonitorRoutes(); return GraphLinksInto(Wave3Sink).Length == 2; });
        Assert.False(mixer.CaptureRecoveryActive);
        lock (notes) Assert.Contains(notes, note => note.Contains("did not run again", StringComparison.Ordinal));
    }

    /// <summary>
    /// The first step of a recovery asked for by a client: the links out of
    /// the unit's capture are dropped and made again, and the mix playing
    /// into the unit's own sink is left where it is.
    /// </summary>
    [MonitorPipeWireFact]
    public void ARelinkMakesTheCaptureLinksAgainAndLeavesThePlayback()
    {
        using var links = new LinkLog();
        var pw = new PipeWireAdapter(); using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw) { Clock = () => 0 };
        pw.CreateNullSink(Wave3Sink, "Wave 3 test output");
        WaitForInputPorts(pw, Wave3Sink, 2);
        WaveSource(pw, "test_wave3_relink_parent", Wave3Source);
        mixer.SetInputDeviceHint(Wave3Hints, hardwareOutputRouting: false, modelFallback: false);
        mixer.Build(HardwareInputConfig, monitorOutputSink: Wave3Sink);
        WaitForRecovery(() => { mixer.EnsureInputFeeds(); mixer.EnsureMonitorRoutes(); return GraphLinksInto(Wave3Sink).Length == 2; });

        links.Start();
        UnitRelink? relink = mixer.RelinkDrivenUnitCapture();
        Assert.Equal(new UnitRelink(Wave3Source, 2), relink);
        string[] made = links.Made();
        Assert.Equal(2, made.Length);
        Assert.All(made, l => Assert.StartsWith(Wave3Source + ":", l, StringComparison.Ordinal));
        Assert.Equal(2, IncomingRouteLinks(Xlr1).Length);
        Assert.Equal(2, GraphLinksInto(Wave3Sink).Length);
        Assert.False(mixer.CaptureRecoveryActive);
    }

    /// <summary>
    /// Puts a pw-link in front of the real one that appends every link it
    /// made, "output input", to a file, and takes it away again on dispose.
    /// </summary>
    private sealed class LinkLog : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-link-order-").FullName;
        private readonly string? _path = Environment.GetEnvironmentVariable("PATH");
        private string Log => Path.Combine(_directory, "links");

        public LinkLog()
        {
            string real = (_path ?? "").Split(Path.PathSeparator)
                .Select(p => Path.GetFullPath(Path.Combine(p, "pw-link"))).First(File.Exists);
            ExecutableScript.Write(Path.Combine(_directory, "pw-link"), $$"""
                '{{real}}' "$@"
                status=$?
                if [ "$status" -eq 0 ] && [ "$#" -eq 2 ] && [ -e '{{Log}}.on' ]; then
                  case "$1" in -*) ;; *) echo "$1 $2" >> '{{Log}}' ;; esac
                fi
                exit "$status"
                """);
            Environment.SetEnvironmentVariable("PATH", _directory + Path.PathSeparator + _path);
        }

        /// <summary>Record from now on; links made by the fixture setup are left out.</summary>
        public void Start() => File.WriteAllText(Log + ".on", "");

        /// <summary>Every link made since <see cref="Start"/>, "output input".</summary>
        public string[] Made() => File.Exists(Log) ? File.ReadAllLines(Log) : [];

        public void AssertCaptureFirst(string source = Wave3Source, string sink = Wave3Sink)
        {
            string[] made = Made();
            int capture = Array.FindIndex(made, l => l.StartsWith(source + ":", StringComparison.Ordinal)
                && l.Contains(" " + Xlr1 + ":", StringComparison.Ordinal));
            int playback = Array.FindIndex(made, l => l.Contains(" " + sink + ":", StringComparison.Ordinal));
            Assert.True(capture >= 0, "the capture was never linked:\n" + string.Join('\n', made));
            Assert.True(playback >= 0, "nothing was routed into the interface:\n" + string.Join('\n', made));
            Assert.True(capture < playback, "playback into the interface started before its capture:\n" + string.Join('\n', made));
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("PATH", _path);
            Directory.Delete(_directory, recursive: true);
        }
    }
}
