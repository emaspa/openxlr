using System.Globalization;
using System.Reflection;
using System.Text.Json;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class MonitorVolumeSyncTests
{
    private sealed class Sinks : IDisposable
    {
        private readonly string _dir = Directory.CreateTempSubdirectory("openxlr-sinks-").FullName;
        private readonly string? _path = Environment.GetEnvironmentVariable("PATH");
        public PipeWireAdapter Adapter { get; } = new();
        public Mixer Mixer { get; }
        public string[] Writes => File.ReadAllLines(Path.Combine(_dir, "writes"));
        public int Dumps => File.ReadAllLines(Path.Combine(_dir, "reads")).Length;
        public int NativeSnapshots => Writes.Count(line => line == "--format=json list sinks");
        public long Now { get; set; }

        public Sinks()
        {
            ExecutableScript.Write(Path.Combine(_dir, "pw-dump"), """
                dir=${0%/*}
                echo read >> "$dir/reads"
                if [ -f "$dir/graph-unavailable" ]; then exit 1; fi
                /bin/cat "$dir/dump.json"
                """);
            ExecutableScript.Write(Path.Combine(_dir, "pw-link"), "exit 0\n");
            ExecutableScript.Write(Path.Combine(_dir, "pactl"), """
                dir=${0%/*}
                echo "$*" >> "$dir/writes"
                if [ "$1" = --format=json ]; then
                    if [ -f "$dir/native-malformed" ]; then printf '{'; exit; fi
                    if [ -f "$dir/native-failed" ]; then exit 1; fi
                    if [ -f "$dir/native-missing" ]; then printf '[]'; exit; fi
                    serial=$(/bin/cat "$dir/native-serial")
                    current=$(/bin/cat "$dir/mute")
                    case "$current" in 'Mute: yes') muted=true ;; *) muted=false ;; esac
                    if [ -f "$dir/native-index-only" ]; then properties='{}'; else properties="{\"object.serial\":\"$serial\"}"; fi
                    printf '[{"index":%s,"name":"external","mute":%s,"properties":%s}]' "$serial" "$muted" "$properties"
                    exit
                fi
                if [ "$1" = get-default-sink ]; then
                    /bin/cat "$dir/default"
                    exit
                fi
                if [ "$1" = get-sink-mute ]; then
                    /bin/cat "$dir/mute"
                    exit
                fi
                if [ "$1" = get-sink-volume ]; then
                    /bin/cat "$dir/volume"
                    exit
                fi
                if [ "$1" = set-sink-volume ] && [ -f "$dir/fail-volume" ]; then exit 1; fi
                if [ "$1" = set-sink-mute ]; then
                    if [ -f "$dir/fail" ]; then
                        left=$(/bin/cat "$dir/fail")
                        if [ "$left" -gt 0 ]; then
                            printf '%s\n' "$((left - 1))" > "$dir/fail"
                            echo 'transient mute failure' >&2
                            exit 1
                        fi
                    fi
                    flip() {
                        current=$(/bin/cat "$dir/mute")
                        case "$current" in 'Mute: yes') printf 'Mute: no\n' > "$dir/mute" ;; *) printf 'Mute: yes\n' > "$dir/mute" ;; esac
                    }
                    if [ -f "$dir/press-before-write" ]; then
                        /bin/rm "$dir/press-before-write"
                        flip
                    fi
                    case "$3" in 0) printf 'Mute: no\n' > "$dir/mute" ;; 1) printf 'Mute: yes\n' > "$dir/mute" ;; toggle) flip ;; esac
                fi
                """);
            Environment.SetEnvironmentVariable("PATH", _dir);
            NativeSerial(1);
            DefaultSink("OpenXLR_ch_system");
            Adapter.SetSinkMuted("OpenXLR_mix_monitor", false);
            File.WriteAllText(Path.Combine(_dir, "writes"), "");
            Mixer = new(Adapter) { Clock = () => Now };
            typeof(Mixer).GetField("_built", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Mixer, true);
            typeof(Mixer).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Mixer,
                new MixerConfig { Channels = [], Mixes = [new("monitor", "Monitor", MixKind.Monitor)] });
        }

        public void ApplicationChannel() => typeof(Mixer).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(Mixer, new MixerConfig { Channels = [new("system", "System")], Mixes = [new("monitor", "Monitor", MixKind.Monitor)] });

        public void SelectExternalOutput() => ((List<string>)typeof(Mixer).GetField("_monitorOutputs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(Mixer)!).Add("external");

        public void TraceJackLinks() => ExecutableScript.Write(Path.Combine(_dir, "pw-link"), """
            dir=${0%/*}
            case "$1" in
                -o) printf 'OpenXLR_mix_monitor:monitor_FL\nOpenXLR_mix_monitor:monitor_FR\n' ;;
                -i) printf 'external:playback_FL\nexternal:playback_FR\nexternal:playback_AUX0\nexternal:playback_AUX1\n' ;;
                -*) ;;
                *) printf 'link %s\n' "$*" >> "$dir/writes" ;;
            esac
            """);

        public void OutputVolume(int left, int right) => File.WriteAllText(Path.Combine(_dir, "volume"),
            $"Volume: front-left: 0 / {left}% / 0 dB, front-right: 0 / {right}% / 0 dB");

        public void OutputVolumeUnreadable() => File.Delete(Path.Combine(_dir, "volume"));

        public void RefuseVolumeWrite(bool refuse)
        {
            string marker = Path.Combine(_dir, "fail-volume");
            if (refuse) File.WriteAllText(marker, "");
            else File.Delete(marker);
        }

        public void DirectMute(bool muted) => File.WriteAllText(Path.Combine(_dir, "mute"), muted ? "Mute: yes\n" : "Mute: no\n");
        public void DefaultSink(string name) => File.WriteAllText(Path.Combine(_dir, "default"), name + "\n");
        public void FailMuteWrites(int count) => File.WriteAllText(Path.Combine(_dir, "fail"), count.ToString(CultureInfo.InvariantCulture));
        public void PressDuringNextMuteWrite() => File.WriteAllText(Path.Combine(_dir, "press-before-write"), "");
        public void NativeSerial(int serial) => File.WriteAllText(Path.Combine(_dir, "native-serial"), serial.ToString(CultureInfo.InvariantCulture));
        public void NativeMissing(bool missing)
        {
            string path = Path.Combine(_dir, "native-missing");
            if (missing) File.WriteAllText(path, "");
            else File.Delete(path);
        }
        public void NativeIndexOnly() => File.WriteAllText(Path.Combine(_dir, "native-index-only"), "");
        public void FailNativeSnapshot(bool malformed, bool fail)
        {
            string path = Path.Combine(_dir, malformed ? "native-malformed" : "native-failed");
            if (fail) File.WriteAllText(path, "");
            else File.Delete(path);
        }
        public void GraphUnavailable(bool unavailable)
        {
            string path = Path.Combine(_dir, "graph-unavailable");
            if (unavailable) File.WriteAllText(path, "");
            else File.Delete(path);
            typeof(PipeWireAdapter).GetMethod("InvalidateDump", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Adapter, null);
        }

        public void Dump(double volume, bool muted = false, bool internalSink = false, bool? externalMuted = null, int externalSerial = 1, bool internalMuted = true)
        {
            if (externalMuted is not null) NativeSerial(externalSerial);
            string amplitude = Math.Pow(volume, 3).ToString("R", CultureInfo.InvariantCulture);
            string monitor = $$$$"""
                {"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"OpenXLR_mix_monitor","media.class":"Audio/Sink"},
                "params":{"Props":[{"channelVolumes":[{{{{amplitude}}}}],"mute":{{{{JsonSerializer.Serialize(muted)}}}}}]}}}
                """;
            string channel = $$$$"""
                {"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"OpenXLR_ch_system","media.class":"Audio/Sink"},
                "params":{"Props":[{"channelVolumes":[0.125],"mute":{{{{JsonSerializer.Serialize(internalMuted)}}}}}]}}}
                """;
            string external = externalMuted is bool value ? "," + JsonSerializer.Serialize(new
            {
                type = "PipeWire:Interface:Node", info = new
                {
                    props = new Dictionary<string, string> { ["node.name"] = "external", ["media.class"] = "Audio/Sink",
                        ["object.serial"] = externalSerial.ToString(CultureInfo.InvariantCulture) },
                    @params = new { Props = new[] { new { channelVolumes = new[] { 1.0 }, mute = value } } },
                },
            }) : "";
            File.WriteAllText(Path.Combine(_dir, "dump.json"), "[" + monitor + (internalSink ? "," + channel : "") + external + "]");
            typeof(PipeWireAdapter).GetMethod("InvalidateDump", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Adapter, null);
        }

        public void Dispose()
        {
            try { Mixer.Dispose(); }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", _path);
                Directory.Delete(_dir, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LeavingHardwareMixingRestoresTheLockBeforeRelinkingJacks(bool refused)
    {
        using var sinks = new Sinks();
        var mixer = sinks.Mixer;
        sinks.Dump(1, externalMuted: false);
        sinks.TraceJackLinks();
        sinks.OutputVolume(35, 70);
        mixer.SetMonitorOutputs(["external#hp1"]);
        mixer.SetMainOutput("external");
        mixer.SetOutputVolumeLock(true, _ => null);

        mixer.BeginHardwareMixing(new HardwareEntry { OutputVolume = .9 });
        mixer.EnterHardwareGraph(new Dictionary<string, int>(), new Dictionary<string, int>());
        Assert.True(mixer.Snapshot().OutputVolumeLockSuspended);
        sinks.OutputVolume(100, 100);
        sinks.RefuseVolumeWrite(refused);

        int before = sinks.Writes.Length;
        mixer.LeaveHardwareMixing();
        string[] writes = sinks.Writes[before..];
        const string restore = "set-sink-volume external 35% 70%";
        int restored = Array.IndexOf(writes, restore);
        int linked = Array.FindIndex(writes, write => write.StartsWith("link ", StringComparison.Ordinal));
        Assert.True(restored >= 0 && linked > restored, string.Join(Environment.NewLine, writes));
        Assert.Single(writes, write => write.StartsWith("set-sink-volume ", StringComparison.Ordinal));
        Assert.False(mixer.Snapshot().OutputVolumeLockSuspended);
        Assert.Equal([.35, .70], Assert.IsType<double[]>(mixer.Snapshot().OutputVolumeLock!.ChannelVolumes));

        if (!refused) return;
        sinks.RefuseVolumeWrite(false);
        mixer.SyncDeviceVolumes();
        Assert.Single(sinks.Writes[before..], write => write == restore);
        sinks.Now = 3000;
        mixer.SyncDeviceVolumes();
        Assert.Equal(2, sinks.Writes[before..].Count(write => write == restore));
    }

    [Fact]
    public void LeavingHardwareMixingWritesTheHeldLevelWhenTheSinkCannotBeRead()
    {
        using var sinks = new Sinks();
        var mixer = sinks.Mixer;
        sinks.Dump(1, externalMuted: false);
        sinks.TraceJackLinks();
        sinks.OutputVolume(35, 70);
        mixer.SetMonitorOutputs(["external#hp1"]);
        mixer.SetMainOutput("external");
        mixer.SetOutputVolumeLock(true, _ => null);
        mixer.BeginHardwareMixing(new HardwareEntry { OutputVolume = .9 });
        mixer.EnterHardwareGraph(new Dictionary<string, int>(), new Dictionary<string, int>());
        sinks.OutputVolumeUnreadable();

        int before = sinks.Writes.Length;
        mixer.LeaveHardwareMixing();
        string[] writes = sinks.Writes[before..];
        // The mode left the sink at 100%, so the jacks are not relinked on a level nobody could read.
        int restored = Array.IndexOf(writes, "set-sink-volume external 35% 70%");
        int linked = Array.FindIndex(writes, write => write.StartsWith("link ", StringComparison.Ordinal));
        Assert.True(restored >= 0 && linked > restored, string.Join(Environment.NewLine, writes));
        Assert.Single(writes, write => write.StartsWith("set-sink-volume ", StringComparison.Ordinal));
    }

    [Fact]
    public void LeavingHardwareMixingWithoutALockGivesTheSinkTheModesVolume()
    {
        using var sinks = new Sinks();
        var mixer = sinks.Mixer;
        sinks.Dump(1, externalMuted: false);
        sinks.TraceJackLinks();
        sinks.OutputVolume(100, 100);
        mixer.SetMonitorOutputs(["external#hp1"]);
        mixer.SetMainOutput("external");
        mixer.BeginHardwareMixing(new HardwareEntry { OutputVolume = .9 });
        mixer.EnterHardwareGraph(new Dictionary<string, int>(), new Dictionary<string, int>());

        int before = sinks.Writes.Length;
        mixer.LeaveHardwareMixing();
        string[] writes = sinks.Writes[before..];
        int restored = Array.IndexOf(writes, "set-sink-volume external 90%");
        int linked = Array.FindIndex(writes, write => write.StartsWith("link ", StringComparison.Ordinal));
        Assert.True(restored >= 0 && linked > restored, string.Join(Environment.NewLine, writes));
        Assert.Single(writes, write => write.StartsWith("set-sink-volume ", StringComparison.Ordinal));
        Assert.Null(mixer.Snapshot().OutputVolumeLock);
    }

    [Fact]
    public void TheLockWaitsWhileTheModeIsKeptAndTheGraphHasNotEntered()
    {
        using var sinks = new Sinks();
        var mixer = sinks.Mixer;
        sinks.Dump(1, externalMuted: false);
        sinks.TraceJackLinks();
        sinks.OutputVolume(35, 70);
        mixer.SetMonitorOutputs(["external#hp1"]);
        mixer.SetMainOutput("external");
        mixer.SetOutputVolumeLock(true, _ => null);

        // A restart in the mode: the entry is kept, the sink still holds the
        // 100% the mode pinned, and the graph enters a sweep or two later.
        mixer.BeginHardwareMixing(new HardwareEntry { OutputVolume = .9 });
        sinks.OutputVolume(100, 100);
        int before = sinks.Writes.Length;
        mixer.SyncDeviceVolumes();
        Assert.DoesNotContain(sinks.Writes[before..], write => write.StartsWith("set-sink-volume ", StringComparison.Ordinal));
        MixerState waiting = mixer.Snapshot();
        Assert.True(waiting.OutputVolumeLockSuspended);
        Assert.False(waiting.MonitorVolumeLocked);

        mixer.EnterHardwareGraph(new Dictionary<string, int>(), new Dictionary<string, int>());
        mixer.SyncDeviceVolumes();
        Assert.DoesNotContain(sinks.Writes[before..], write => write.StartsWith("set-sink-volume ", StringComparison.Ordinal));
        Assert.True(mixer.Snapshot().OutputVolumeLockSuspended);
    }

    [Fact]
    public void TheSharedOutputVolumeIsRefusedWhileItsFirstOutputIsLocked()
    {
        using var sinks = new Sinks();
        var mixer = sinks.Mixer;
        sinks.Dump(1, externalMuted: false);
        sinks.OutputVolume(60, 60);
        mixer.SetMonitorOutputs(["external"]);
        mixer.SetMainOutput("external");
        mixer.SetOutputVolumeLock(true, _ => null);
        Assert.True(mixer.Snapshot().MonitorVolumeLocked);

        int before = sinks.Writes.Length;
        string named = Assert.Throws<InvalidOperationException>(() => mixer.SetOutputDeviceVolume("external", .3)).Message;
        Assert.Equal(named, Assert.Throws<InvalidOperationException>(() => mixer.SetSharedOutputVolume(.3)).Message);
        // A profile recall carries an output volume too. It leaves the locked output alone and is not an error.
        mixer.SetOutputVolume(.3);
        Assert.DoesNotContain(sinks.Writes[before..], write => write.StartsWith("set-sink-volume ", StringComparison.Ordinal));
        Assert.Equal(.6, mixer.Snapshot().OutputVolume);

        mixer.SetOutputVolumeLock(false, _ => null);
        mixer.SetSharedOutputVolume(.3);
        Assert.Contains("set-sink-volume external 30%", sinks.Writes[before..]);
    }

    [Theory]
    [InlineData(0.665, 0.66)]
    [InlineData(0.675, 0.68)]
    [InlineData(1.205, 1.2)]
    public void RoundedReadbackDoesNotChangeTheStoredMaster(double stored, double readback)
    {
        using var sinks = new Sinks();
        sinks.Mixer.SetMixVolume("monitor", stored);
        sinks.Dump(readback);

        Assert.False(sinks.Mixer.SyncMonitorVolumes());

        Assert.Equal(stored, Assert.Single(sinks.Mixer.Snapshot().Mixes).Volume);
    }

    [Theory]
    [InlineData(0.65, 0.65)]
    [InlineData(1.2, 1.2)]
    [InlineData(1.6, 1.5)]
    public void DesktopChangesAreReadAndBoostIsClamped(double desktop, double expected)
    {
        using var sinks = new Sinks();
        sinks.Mixer.SetMixVolume("monitor", 0.665);
        sinks.Dump(desktop, muted: true);

        Assert.True(sinks.Mixer.SyncMonitorVolumes());

        var mix = Assert.Single(sinks.Mixer.Snapshot().Mixes);
        Assert.Equal(expected, mix.Volume);
        Assert.True(mix.Muted);
        if (desktop > 1.5) Assert.Contains("set-sink-volume OpenXLR_mix_monitor 150%", sinks.Writes);
    }

    [Fact]
    public void SinkRepairAndMonitorReadbackShareOneDump()
    {
        using var sinks = new Sinks();
        sinks.Dump(0.6, internalSink: true);

        Assert.True(sinks.Mixer.SyncOwnSinkLevels(out var restored));

        Assert.Equal(["OpenXLR_ch_system"], restored);
        Assert.Equal(0.6, Assert.Single(sinks.Mixer.Snapshot().Mixes).Volume);
        Assert.Equal(1, sinks.Dumps);
        Assert.Contains("set-sink-volume OpenXLR_ch_system 100%", sinks.Writes);
        Assert.Contains("set-sink-mute OpenXLR_ch_system 0", sinks.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AUiWriteInvalidatesTheDumpBeforeTheNextSweep(bool mute)
    {
        using var sinks = new Sinks();
        sinks.Dump(0.6);
        Assert.True(sinks.Mixer.SyncOwnSinkLevels(out _));
        if (mute) sinks.Mixer.SetMixMuted("monitor", true);
        else sinks.Mixer.SetMixVolume("monitor", 0.8);
        sinks.Dump(mute ? 0.6 : 0.8, muted: mute);

        Assert.False(sinks.Mixer.SyncOwnSinkLevels(out _));

        Assert.Equal(2, sinks.Dumps);
        var mix = Assert.Single(sinks.Mixer.Snapshot().Mixes);
        Assert.Equal(mute ? 0.6 : 0.8, mix.Volume);
        Assert.Equal(mute, mix.Muted);
    }

    [Fact]
    public void AStaleApplicationMuteEchoDoesNotBecomeAnotherKeyPress()
    {
        using var sinks = new Sinks();
        sinks.ApplicationChannel();
        sinks.DirectMute(true);
        sinks.Dump(0.6, internalSink: true);

        Assert.True(sinks.Mixer.SyncOwnSinkLevels(out _));
        Assert.True(sinks.Mixer.MonitorMuted);
        Assert.False(sinks.Mixer.SyncOwnSinkLevels(out _));
        Assert.True(sinks.Mixer.MonitorMuted);
    }

    [Theory]
    [InlineData("external")]
    [InlineData("OpenXLR_ch_music")]
    public void ANonDefaultApplicationMuteIsRepairedWithoutPausingMonitoring(string defaultSink)
    {
        using var sinks = new Sinks();
        sinks.ApplicationChannel();
        sinks.DefaultSink(defaultSink);
        sinks.DirectMute(true);
        sinks.Dump(1, internalSink: true);

        Assert.False(sinks.Mixer.SyncOwnSinkLevels(out var restored));

        Assert.False(sinks.Mixer.MonitorMuted);
        Assert.False(sinks.Adapter.GetSinkMuted("OpenXLR_ch_system"));
        Assert.Equal(["OpenXLR_ch_system"], restored);
        Assert.Contains("set-sink-mute OpenXLR_ch_system 0", sinks.Writes);
        Assert.DoesNotContain("set-sink-mute OpenXLR_ch_system toggle", sinks.Writes);
    }

    [Fact]
    public void AnUnmutedApplicationSinkDoesNotReadTheDefaultSinkEverySweep()
    {
        using var sinks = new Sinks();
        sinks.ApplicationChannel();
        sinks.Dump(1, internalSink: true, internalMuted: false);

        Assert.False(sinks.Mixer.SyncOwnSinkLevels(out _));

        Assert.False(sinks.Mixer.MonitorMuted);
        Assert.DoesNotContain(sinks.Writes, w => w.StartsWith("get-default-sink", StringComparison.Ordinal));
        Assert.DoesNotContain(sinks.Writes, w => w.StartsWith("set-sink-mute", StringComparison.Ordinal));
    }

    [Fact]
    public void ARefusedApplicationMuteConsumeStillAllowsRepairDuringBackoff()
    {
        using var sinks = new Sinks();
        sinks.ApplicationChannel();
        sinks.DirectMute(true);
        sinks.Dump(1, internalSink: true);
        sinks.FailMuteWrites(6);
        for (int sweep = 0; sweep < 3; sweep++)
            Assert.False(sinks.Mixer.SyncOwnSinkLevels(out _));
        Assert.True(sinks.Adapter.GetSinkMuted("OpenXLR_ch_system"));
        Assert.Equal(3, sinks.Writes.Count(w => w == "set-sink-mute OpenXLR_ch_system toggle"));

        sinks.FailMuteWrites(0);
        Assert.False(sinks.Mixer.SyncOwnSinkLevels(out var restored));

        Assert.Equal(["OpenXLR_ch_system"], restored);
        Assert.False(sinks.Adapter.GetSinkMuted("OpenXLR_ch_system"));
        Assert.False(sinks.Mixer.MonitorMuted);
        Assert.Equal(3, sinks.Writes.Count(w => w == "set-sink-mute OpenXLR_ch_system toggle"));
        Assert.False(sinks.Mixer.SyncOwnSinkLevels(out _));
        sinks.DirectMute(true);
        Assert.True(sinks.Mixer.SyncOwnSinkLevels(out _));
        Assert.True(sinks.Mixer.MonitorMuted);
    }

    [Fact]
    public void LinuxCanStillEditAnIndividualMixMuteWhileMonitoringIsPaused()
    {
        using var sinks = new Sinks();
        sinks.Mixer.SetMonitorMuted(true);
        sinks.Dump(0.6, muted: true);
        Assert.True(sinks.Mixer.SyncOwnSinkLevels(out _));
        Assert.True(Assert.Single(sinks.Mixer.Snapshot().Mixes).Muted);
        sinks.Dump(0.6, muted: false);
        Assert.True(sinks.Mixer.SyncOwnSinkLevels(out _));
        Assert.False(Assert.Single(sinks.Mixer.Snapshot().Mixes).Muted);
        Assert.True(sinks.Mixer.MonitorMuted);
    }

    [Fact]
    public void ARefusedNativePulseBacksOffAndRecoversWithoutForgettingTheNextKey()
    {
        using var sinks = new Sinks();
        sinks.SelectExternalOutput();
        sinks.Dump(0.6, externalMuted: false);
        sinks.DirectMute(false);
        sinks.Mixer.SyncDeviceVolumes();
        sinks.Dump(0.6, externalMuted: true);
        sinks.DirectMute(true);
        sinks.FailMuteWrites(4);

        Assert.False(sinks.Mixer.SyncDeviceVolumes());
        Assert.False(sinks.Mixer.MonitorMuted); // No pulse is counted before its consume is acknowledged.
        Assert.False(sinks.Mixer.SyncDeviceVolumes());
        Assert.False(sinks.Mixer.SyncDeviceVolumes());
        int beforeBackoff = sinks.Writes.Count(w => w.StartsWith("set-sink-mute external ", StringComparison.Ordinal));
        Assert.False(sinks.Mixer.SyncDeviceVolumes());
        Assert.Equal(beforeBackoff, sinks.Writes.Count(w => w.StartsWith("set-sink-mute external ", StringComparison.Ordinal)));
        sinks.Now = 1_000;
        Assert.False(sinks.Mixer.SyncDeviceVolumes());
        sinks.Now = 3_000;
        Assert.True(sinks.Mixer.SyncDeviceVolumes());
        Assert.Equal(sinks.Mixer.MonitorMuted, sinks.Adapter.GetSinkMuted("external"));
        Assert.True(sinks.Mixer.MonitorMuted);
        Assert.False(sinks.Mixer.SyncDeviceVolumes());

        sinks.DirectMute(false);
        sinks.Dump(0.6, externalMuted: false);
        Assert.True(sinks.Mixer.SyncDeviceVolumes());
        Assert.False(sinks.Mixer.MonitorMuted);
        Assert.Equal(sinks.Mixer.MonitorMuted, sinks.Adapter.GetSinkMuted("external"));
    }

    [Fact]
    public void AStaleOutputMuteEchoCannotCancelARefusedNativePulse()
    {
        using var sinks = new Sinks();
        sinks.SelectExternalOutput();
        sinks.Dump(0.6, externalMuted: false);
        sinks.DirectMute(false);
        sinks.Mixer.SyncDeviceVolumes();
        sinks.Dump(0.6, externalMuted: true);
        sinks.DirectMute(true);
        sinks.FailMuteWrites(4);
        Assert.False(sinks.Mixer.SyncDeviceVolumes());

        sinks.Dump(0.6, externalMuted: false);
        Assert.False(sinks.Mixer.SyncDeviceVolumes());
        sinks.Dump(0.6, externalMuted: true);
        Assert.False(sinks.Mixer.SyncDeviceVolumes());
        Assert.False(sinks.Mixer.MonitorMuted);
        Assert.True(sinks.Adapter.GetSinkMuted("external"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AReconnectedOutputRestoresItsPriorMuteWithoutBecomingAKeyPress(bool paused, bool sawRemoval)
    {
        using var sinks = new Sinks();
        sinks.SelectExternalOutput();
        sinks.Dump(0.6, externalMuted: true);
        sinks.DirectMute(true);
        sinks.Mixer.SyncDeviceVolumes();
        sinks.Mixer.SetMonitorMuted(paused);

        if (sawRemoval)
        {
            sinks.Dump(0.6); // The selected device has gone away.
            sinks.Mixer.SyncDeviceVolumes();
        }
        sinks.Dump(0.6, externalMuted: false, externalSerial: 2); // A new node starts unmuted.
        sinks.DirectMute(false);
        sinks.Mixer.SyncDeviceVolumes();

        Assert.Equal(paused, sinks.Mixer.MonitorMuted);
        Assert.True(sinks.Adapter.GetSinkMuted("external"));
        Assert.False(sinks.Mixer.SyncDeviceVolumes());
        Assert.Equal(paused, sinks.Mixer.MonitorMuted);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RegistryDiscoveryBeforeNativeDiscoveryKeepsAReconnectedOutputMuted(bool paused, bool nativeMissing)
    {
        using var sinks = new Sinks();
        sinks.DirectMute(true);
        sinks.Dump(1, externalMuted: true);
        sinks.Mixer.SetMonitorOutputs(["external"]);
        sinks.Mixer.SetMonitorMuted(paused);
        sinks.Mixer.SyncDeviceVolumes(); // Account for the initial saved native baseline.

        sinks.Dump(1, externalMuted: false, externalSerial: 2);
        sinks.NativeSerial(1);
        sinks.NativeMissing(nativeMissing);
        int snapshots = sinks.NativeSnapshots;
        for (int sweep = 0; sweep < 3; sweep++)
        {
            Assert.False(sinks.Mixer.SyncDeviceVolumes());
            Assert.Equal(paused, sinks.Mixer.MonitorMuted);
        }
        Assert.Equal(snapshots + 3, sinks.NativeSnapshots);

        sinks.NativeMissing(false);
        sinks.NativeSerial(2);
        sinks.DirectMute(false);
        Assert.False(sinks.Mixer.SyncDeviceVolumes());
        Assert.True(sinks.Adapter.GetSinkMuted("external"));
        Assert.Equal(paused, sinks.Mixer.MonitorMuted);
        Assert.False(sinks.Mixer.SyncDeviceVolumes());
        Assert.True(sinks.Adapter.GetSinkMuted("external"));
        Assert.DoesNotContain("set-sink-mute external toggle", sinks.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ANewOutputConfirmsItsBaselineBeforeTheNextNativeMuteKey(bool muted)
    {
        using var sinks = new Sinks();
        sinks.SelectExternalOutput();
        sinks.Dump(0.6, externalMuted: !muted); // Discovery can precede the native mute echo.
        sinks.DirectMute(muted);
        sinks.Mixer.SyncDeviceVolumes();

        sinks.Dump(0.6, externalMuted: !muted);
        sinks.DirectMute(!muted);
        Assert.Equal(!muted, sinks.Mixer.SyncDeviceVolumes());
        Assert.Equal(!muted, sinks.Mixer.MonitorMuted);
        Assert.Equal(sinks.Mixer.MonitorMuted, sinks.Adapter.GetSinkMuted("external"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASecondNativeKeyDuringConsumptionIsNotOverwritten(bool application)
    {
        using var sinks = new Sinks();
        if (application) sinks.ApplicationChannel();
        else
        {
            sinks.SelectExternalOutput();
            sinks.DirectMute(false);
            sinks.Dump(1, externalMuted: false);
            sinks.Mixer.SyncDeviceVolumes();
        }
        sinks.DirectMute(true);
        sinks.Dump(1, internalSink: application, externalMuted: application ? null : true);
        sinks.PressDuringNextMuteWrite();
        Sweep();
        Assert.True(sinks.Mixer.MonitorMuted);

        bool flag = sinks.Adapter.GetSinkMuted(application ? "OpenXLR_ch_system" : "external");
        sinks.Dump(1, internalSink: application && flag, externalMuted: application ? null : flag);
        Sweep();
        Assert.False(sinks.Mixer.MonitorMuted);
        Assert.False(sinks.Adapter.GetSinkMuted(application ? "OpenXLR_ch_system" : "external"));

        void Sweep()
        {
            if (application) sinks.Mixer.SyncOwnSinkLevels(out _);
            sinks.Mixer.SyncDeviceVolumes();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASecondNativeKeyCancelsAnUnacknowledgedPulseInsteadOfLeavingMonitoringPaused(bool application)
    {
        using var sinks = new Sinks();
        if (application) sinks.ApplicationChannel();
        else sinks.SelectExternalOutput();
        sinks.DirectMute(false);
        sinks.Dump(1, externalMuted: application ? null : false);
        sinks.Mixer.SyncDeviceVolumes();
        sinks.DirectMute(true);
        sinks.Dump(1, internalSink: application, externalMuted: application ? null : true);
        sinks.FailMuteWrites(4);
        Sweep();
        sinks.DirectMute(false); // Second key before any consume succeeded.
        sinks.Dump(1, externalMuted: application ? null : false);
        Sweep();
        Assert.False(sinks.Mixer.MonitorMuted);
        Assert.False(sinks.Adapter.GetSinkMuted(application ? "OpenXLR_ch_system" : "external"));

        void Sweep()
        {
            if (application) sinks.Mixer.SyncOwnSinkLevels(out _);
            sinks.Mixer.SyncDeviceVolumes();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheFirstNativeMuteChangeAfterSelectionOnlyPausesAnUnmutedOutput(bool originalMute)
    {
        using var sinks = new Sinks();
        sinks.DirectMute(originalMute);
        sinks.Dump(1, externalMuted: originalMute);
        sinks.Mixer.SetMonitorOutputs(["external"]);
        Assert.Equal(["external"], sinks.Mixer.MonitorOutputs);

        sinks.DirectMute(!originalMute);
        sinks.Dump(1, externalMuted: !originalMute);
        sinks.Mixer.SyncDeviceVolumes();

        Assert.Equal(!originalMute, sinks.Mixer.MonitorMuted);
        Assert.Equal(sinks.Mixer.MonitorMuted, sinks.Adapter.GetSinkMuted("external"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnmutingAnAlreadyMutedOutputKeepsThePauseAndTheNextMuteTogglesIt(bool paused)
    {
        using var sinks = new Sinks();
        sinks.DirectMute(true);
        sinks.Dump(1, externalMuted: true);
        sinks.Mixer.SetMonitorOutputs(["external"]);
        sinks.Mixer.SetMonitorMuted(paused);

        sinks.DirectMute(false);
        sinks.Dump(1, externalMuted: false);
        Assert.Equal(paused, sinks.Mixer.SyncDeviceVolumes());
        Assert.False(sinks.Mixer.MonitorMuted);
        Assert.False(sinks.Adapter.GetSinkMuted("external"));

        sinks.DirectMute(true);
        sinks.Dump(1, externalMuted: true);
        Assert.True(sinks.Mixer.SyncDeviceVolumes());
        Assert.True(sinks.Mixer.MonitorMuted);
        Assert.True(sinks.Adapter.GetSinkMuted("external"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SelectionRemembersAnAddressableSinkBeforeItsFirstRegistryObservation(bool originalMute, bool unavailableGraph)
    {
        using var sinks = new Sinks();
        sinks.DirectMute(originalMute);
        sinks.Dump(1); // Native commands can reach a sink before its Props arrive.
        if (unavailableGraph) sinks.GraphUnavailable(true);
        sinks.Mixer.SetMonitorOutputs(["external"]);
        if (unavailableGraph) sinks.GraphUnavailable(false);
        sinks.Mixer.SyncDeviceVolumes(); // Still no registry identity, so this is not a disconnect.

        sinks.DirectMute(!originalMute);
        sinks.Dump(1, externalMuted: !originalMute);
        sinks.Mixer.SyncDeviceVolumes();

        Assert.Equal(!originalMute, sinks.Mixer.MonitorMuted);
        Assert.Equal(sinks.Mixer.MonitorMuted, sinks.Adapter.GetSinkMuted("external"));
    }

    [Fact]
    public void ReselectingAnOutputEstablishesItsNewBaselineWithoutAnInterveningSweep()
    {
        using var sinks = new Sinks();
        sinks.DirectMute(false);
        sinks.Dump(1, externalMuted: false);
        sinks.Mixer.SetMonitorOutputs(["external"]);
        sinks.Mixer.SyncDeviceVolumes();
        sinks.Mixer.SetMonitorOutputs([]);
        sinks.DirectMute(true);
        sinks.Dump(1, externalMuted: true);
        sinks.Mixer.SetMonitorOutputs(["external"]);

        sinks.DirectMute(false);
        sinks.Dump(1, externalMuted: false);
        sinks.Mixer.SyncDeviceVolumes();

        Assert.False(sinks.Mixer.MonitorMuted);
        Assert.Equal(sinks.Mixer.MonitorMuted, sinks.Adapter.GetSinkMuted("external"));
    }

    [Fact]
    public void RelinkingTheSameSelectionKeepsAnUnconsumedNativeKey()
    {
        using var sinks = new Sinks();
        sinks.DirectMute(false);
        sinks.Dump(1, externalMuted: false);
        sinks.Mixer.SetMonitorOutputs(["external"]);

        sinks.DirectMute(true);
        sinks.Dump(1, externalMuted: true);
        sinks.Mixer.SetMonitorOutputs(["external"]);
        sinks.Mixer.SyncDeviceVolumes();

        Assert.True(sinks.Mixer.MonitorMuted);
        Assert.Equal(sinks.Mixer.MonitorMuted, sinks.Adapter.GetSinkMuted("external"));
    }

    [Fact]
    public void AnUnavailableGraphDoesNotRejectSelectionOrPreventLaterMuteDiscovery()
    {
        using var sinks = new Sinks();
        sinks.DirectMute(false);
        sinks.Dump(1, externalMuted: false);
        sinks.GraphUnavailable(true);
        sinks.Mixer.SetMonitorOutputs(["external"]);
        Assert.Equal(["external"], sinks.Mixer.MonitorOutputs);
        sinks.GraphUnavailable(false);
        sinks.Mixer.SyncDeviceVolumes();

        sinks.DirectMute(true);
        sinks.Dump(1, externalMuted: true);
        sinks.Mixer.SyncDeviceVolumes();

        Assert.True(sinks.Mixer.MonitorMuted);
        Assert.Equal(sinks.Mixer.MonitorMuted, sinks.Adapter.GetSinkMuted("external"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectingAnAlreadyReplacedNativeSinkDoesNotLoseItsFirstKey(bool staleRegistry)
    {
        using var sinks = new Sinks();
        sinks.DirectMute(false);
        sinks.Dump(1, externalMuted: false, externalSerial: staleRegistry ? 1 : 2);
        sinks.NativeSerial(2);
        sinks.Mixer.SetMonitorOutputs(["external"]);

        sinks.DirectMute(true);
        sinks.Dump(1, externalMuted: true, externalSerial: 2);
        sinks.Mixer.SyncDeviceVolumes();

        Assert.True(sinks.Mixer.MonitorMuted);
        Assert.Equal(sinks.Mixer.MonitorMuted, sinks.Adapter.GetSinkMuted("external"));
    }

    [Fact]
    public void AStaleRegistryIdentityAfterSelectionDoesNotReplaceTheNativeBaseline()
    {
        using var sinks = new Sinks();
        sinks.DirectMute(false);
        sinks.Dump(1, externalMuted: false, externalSerial: 2);
        sinks.Mixer.SetMonitorOutputs(["external"]);
        sinks.Dump(1, externalMuted: true, externalSerial: 1);
        sinks.NativeSerial(2);
        Assert.False(sinks.Mixer.SyncDeviceVolumes());
        Assert.False(sinks.Mixer.MonitorMuted);

        sinks.DirectMute(true);
        sinks.Dump(1, externalMuted: true, externalSerial: 2);
        Assert.True(sinks.Mixer.SyncDeviceVolumes());
        Assert.True(sinks.Mixer.MonitorMuted);
        Assert.Equal(sinks.Mixer.MonitorMuted, sinks.Adapter.GetSinkMuted("external"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnchangedNativeIdentitiesUseNoAdditionalSnapshotHelpers(bool indexOnly)
    {
        using var sinks = new Sinks();
        if (indexOnly) sinks.NativeIndexOnly();
        sinks.DirectMute(false);
        sinks.Dump(1, externalMuted: false);
        sinks.Mixer.SetMonitorOutputs(["external"]);
        Assert.Equal(1, sinks.NativeSnapshots);
        for (int sweep = 0; sweep < 5; sweep++) Assert.False(sinks.Mixer.SyncDeviceVolumes());
        sinks.Mixer.SetMonitorOutputs(["external"]);
        Assert.Equal(1, sinks.NativeSnapshots);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARefusedNativeSnapshotDoesNotLoseAKeyAfterSelection(bool malformed)
    {
        using var sinks = new Sinks();
        sinks.DirectMute(false);
        sinks.Dump(1, externalMuted: false);
        sinks.FailNativeSnapshot(malformed, true);
        sinks.Mixer.SetMonitorOutputs(["external"]);
        sinks.DirectMute(true);
        sinks.Dump(1, externalMuted: true);
        Assert.False(sinks.Mixer.SyncDeviceVolumes());
        Assert.True(sinks.Adapter.GetSinkMuted("external"));
        sinks.FailNativeSnapshot(malformed, false);

        Assert.True(sinks.Mixer.SyncDeviceVolumes());
        Assert.True(sinks.Mixer.MonitorMuted);
        Assert.Equal(sinks.Mixer.MonitorMuted, sinks.Adapter.GetSinkMuted("external"));
    }

    [Fact]
    public void ASelectionSnapshotThatMissesAnAddressableSinkKeepsItsFirstKey()
    {
        using var sinks = new Sinks();
        sinks.DirectMute(false);
        sinks.Dump(1, externalMuted: false);
        sinks.NativeMissing(true);
        sinks.Mixer.SetMonitorOutputs(["external"]);
        Assert.False(sinks.Mixer.SyncDeviceVolumes()); // Registry Props can precede native-list discovery too.
        sinks.NativeMissing(false);
        sinks.DirectMute(true);
        sinks.Dump(1, externalMuted: true);

        Assert.True(sinks.Mixer.SyncDeviceVolumes());
        Assert.True(sinks.Mixer.MonitorMuted);
        Assert.Equal(sinks.Mixer.MonitorMuted, sinks.Adapter.GetSinkMuted("external"));
    }

    [Fact]
    public void ADisconnectedOutputDoesNotStartAnIdentityHelperOnEverySweep()
    {
        using var sinks = new Sinks();
        sinks.DirectMute(true);
        sinks.Dump(1, externalMuted: true);
        sinks.Mixer.SetMonitorOutputs(["external"]);
        sinks.NativeMissing(true);
        sinks.Dump(1);
        Assert.False(sinks.Mixer.SyncDeviceVolumes());
        int snapshots = sinks.NativeSnapshots;
        for (int sweep = 0; sweep < 5; sweep++) Assert.False(sinks.Mixer.SyncDeviceVolumes());
        Assert.Equal(snapshots, sinks.NativeSnapshots);

        sinks.NativeMissing(false);
        sinks.DirectMute(false);
        sinks.Dump(1, externalMuted: false, externalSerial: 2);
        Assert.False(sinks.Mixer.SyncDeviceVolumes());
        Assert.True(sinks.Adapter.GetSinkMuted("external"));
        Assert.False(sinks.Mixer.MonitorMuted);
    }

    [Fact]
    public void FailedIdentitySnapshotsBackOffWithoutForgettingAnUnconsumedKey()
    {
        using var sinks = new Sinks();
        sinks.DirectMute(false);
        sinks.Dump(1, externalMuted: false);
        sinks.FailNativeSnapshot(malformed: false, fail: true);
        sinks.Mixer.SetMonitorOutputs(["external"]);
        sinks.DirectMute(true);
        sinks.Dump(1, externalMuted: true);
        Assert.False(sinks.Mixer.SyncDeviceVolumes());
        Assert.False(sinks.Mixer.SyncDeviceVolumes());
        int snapshots = sinks.NativeSnapshots;
        int muteReads = sinks.Writes.Count(w => w == "get-sink-mute external");
        for (int sweep = 0; sweep < 5; sweep++) Assert.False(sinks.Mixer.SyncDeviceVolumes());
        Assert.Equal(snapshots, sinks.NativeSnapshots);
        Assert.Equal(muteReads, sinks.Writes.Count(w => w == "get-sink-mute external"));
        Assert.True(sinks.Adapter.GetSinkMuted("external"));
        sinks.FailNativeSnapshot(malformed: false, fail: false);
        sinks.Now = 1_000;

        Assert.True(sinks.Mixer.SyncDeviceVolumes());
        Assert.True(sinks.Mixer.MonitorMuted);
        Assert.Equal(sinks.Mixer.MonitorMuted, sinks.Adapter.GetSinkMuted("external"));
    }
}
