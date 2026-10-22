using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public void DiskRestartRestoresThePausedSelectionAndItsNativeOutputBaseline()
    {
        foreach (bool originallyMuted in new[] { false, true })
            foreach (bool explicitUnmute in new[] { false, true })
                CheckDiskRestart(originallyMuted, explicitUnmute);
    }

    private static void CheckDiskRestart(bool originallyMuted, bool explicitUnmute)
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-native-mute-restart-").FullName;
        string path = Path.Combine(directory, "mixer.json");
        string output = $"restart_mute_{originallyMuted}_{explicitUnmute}".ToLowerInvariant();
        var outputs = new PipeWireAdapter();
        try
        {
            outputs.CreateNullSink(output, "Native restart output");
            outputs.SetSinkMuted(output, originallyMuted);
            using (var original = new Mixer())
            {
                original.Build(MonitorConfig());
                original.SetMonitorOutputs([output]);
                original.SetMixMuted("monitor2", true);
                original.SetMonitorMuted(true);
                Assert.Null(original.ExportSettings().Save(path));
                Assert.True(outputs.GetSinkMuted(output));
            }
            MixerSettings saved = Assert.IsType<MixerSettings>(MixerSettings.Load(path, out string? warning));
            Assert.Null(warning);
            using var restored = new Mixer();
            restored.SetMonitorMuted(saved.MonitorMuted);
            restored.Build(MixerConfig.FromSettings(saved), output);
            restored.ApplySettings(saved.WithMonitorOverride(output));
            Assert.True(restored.MonitorMuted);
            Assert.Equal(["monitor2"], restored.ExportSettings().MixMuted);
            Assert.True(outputs.GetSinkMuted(output));
            Assert.Equal(originallyMuted, restored.ExportSettings().MonitorOutputMutes[output]);

            if (explicitUnmute) restored.SetMixMuted("monitor", false);
            else restored.SetMonitorMuted(false);
            Assert.False(restored.MonitorMuted);
            Assert.Equal(["monitor2"], restored.ExportSettings().MixMuted);
            Assert.Equal(originallyMuted && !explicitUnmute, outputs.GetSinkMuted(output));
            for (int sweep = 0; sweep < 3; sweep++)
            {
                restored.SyncOwnSinkLevels(out _);
                restored.SyncDeviceVolumes();
            }
            Assert.False(restored.MonitorMuted);
            Assert.Null(restored.ExportSettings().Save(path));
            Assert.False(MixerSettings.Load(path)!.MonitorMuted);
            Assert.Empty(MixerSettings.Load(path)!.MonitorOutputMutes);
        }
        finally { outputs.TearDown(); Directory.Delete(directory, true); }
    }

    [MonitorPipeWireFact]
    public void DiskRestartReappliesClosedMonitorFeedsAfterANativeOnlyUnmute()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-native-release-restart-").FullName;
        string path = Path.Combine(directory, "mixer.json");
        const string output = "native_release_restart_output";
        var outputs = new PipeWireAdapter();
        try
        {
            outputs.CreateNullSink(output, "Native release restart output");
            var originalAdapter = new PipeWireAdapter();
            using (var registry = originalAdapter.WatchGraph())
            using (var original = new Mixer(originalAdapter))
            {
                original.Build(MonitorConfig());
                original.SetMonitorOutputs([output]);
                original.SetMixMuted("monitor", true);
                original.SyncDeviceVolumes();
                Assert.True(outputs.GetSinkMuted(output));

                outputs.SetSinkMuted(output, false);
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    if (!originalAdapter.ListDevices().Any(d => d.Name == output && d.Muted is false)) return false;
                    original.SyncOwnSinkLevels(out _);
                    original.SyncDeviceVolumes();
                    return !original.MonitorMuted && !outputs.GetSinkMuted(output)
                        && original.ExportSettings().MixMuted.Contains("monitor");
                }, TimeSpan.FromSeconds(3)));
                for (int sweep = 0; sweep < 3; sweep++)
                {
                    original.SyncOwnSinkLevels(out _);
                    original.SyncDeviceVolumes();
                }
                Assert.False(outputs.GetSinkMuted(output));
                Assert.Null(original.ExportSettings().Save(path));
            }

            MixerSettings saved = Assert.IsType<MixerSettings>(MixerSettings.Load(path, out string? warning));
            Assert.Null(warning);
            Assert.False(saved.MonitorMuted);
            Assert.Equal(["monitor"], saved.MixMuted);
            Assert.False(saved.MonitorOutputMutes[output]);
            Assert.False(outputs.GetSinkMuted(output));

            var restoredAdapter = new PipeWireAdapter();
            using var restoredRegistry = restoredAdapter.WatchGraph();
            using var restored = new Mixer(restoredAdapter);
            restored.SetMonitorMuted(saved.MonitorMuted);
            restored.Build(MixerConfig.FromSettings(saved));
            restored.ApplySettings(saved);
            Assert.False(restored.MonitorMuted);
            Assert.Equal([output], restored.MonitorOutputs);
            Assert.Equal(["monitor"], restored.ExportSettings().MixMuted);
            Assert.True(outputs.GetSinkMuted(output));
            Assert.False(restored.ExportSettings().MonitorOutputMutes[output]);
            for (int sweep = 0; sweep < 3; sweep++)
            {
                restored.SyncOwnSinkLevels(out _);
                restored.SyncDeviceVolumes();
            }
            Assert.False(restored.MonitorMuted);
            Assert.True(outputs.GetSinkMuted(output));

            restored.SetMixMuted("monitor", false);
            Assert.False(outputs.GetSinkMuted(output));
            Assert.DoesNotContain("monitor", restored.ExportSettings().MixMuted);
        }
        finally { outputs.TearDown(); Directory.Delete(directory, true); }
    }

    [MonitorPipeWireFact]
    public void DesktopMuteOnAnApplicationSinkPausesOnlyMonitoringAndTheSecondPressRestoresIt()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.Build(MonitorConfig());
        pw.CreateNullSink("mute_output", "Monitor mute test");
        mixer.SetMonitorOutputs(["mute_output"]);
        mixer.SetMixMuted("monitor2", true);
        mixer.SetLevel("test", "monitor", 1);
        mixer.SetLevel("test", "chat", 1);
        pw.SetDefaultSink("OpenXLR_ch_test");
        mixer.SyncOwnSinkLevels(out _);
        mixer.SyncDeviceVolumes();
        MixerSettings before = mixer.ExportSettings();

        Press();
        CaptureLevel(0, "OpenXLR_ch_test", "mute_output");
        CaptureLevel(0.1, "OpenXLR_ch_test", "OpenXLR_post_chat");
        Press();
        CaptureLevel(0.1, "OpenXLR_ch_test", "mute_output");
        Assert.Equal(before.MixMuted.Order(), mixer.ExportSettings().MixMuted.Order());
        Assert.Equal(before.ChannelMuted.Order(), mixer.ExportSettings().ChannelMuted.Order());
        Assert.Equal(before.Levels, mixer.ExportSettings().Levels);

        void Press()
        {
            ProcessResult result = ProcessRunner.Run("pactl", ["set-sink-mute", "@DEFAULT_SINK@", "toggle"]);
            Assert.True(result.Ok, result.Stderr);
            Assert.True(SpinWait.SpinUntil(() => pw.GetSinkMuted("OpenXLR_ch_test"), TimeSpan.FromSeconds(3)));
            Assert.True(mixer.SyncOwnSinkLevels(out _));
            Assert.False(pw.GetSinkMuted("OpenXLR_ch_test"));
            mixer.SyncDeviceVolumes();
            for (int sweep = 0; sweep < 3; sweep++)
            {
                Assert.False(mixer.SyncOwnSinkLevels(out _));
                mixer.EnsureMonitorRoutes();
            }
        }
    }

    [MonitorPipeWireFact]
    public void ASelectedOutputMuteKeyKeepsAnAlreadyMutedFeedClosed()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.Build(MonitorConfig());
        pw.CreateNullSink("mute_sum", "Monitor sum mute test");
        mixer.SetMonitorOutputs(["mute_sum"]);
        Assert.Null(mixer.SetMonitorFeed("mute_sum", "monitor+monitor2"));
        mixer.SetMixMuted("monitor2", true);

        mixer.ToggleOutputMute("mute_sum");
        mixer.ToggleOutputMute("mute_sum");

        Assert.True(mixer.Snapshot().Mixes.Single(m => m.Id == "monitor2").Muted);
        Assert.False(mixer.Snapshot().Mixes.Single(m => m.Id == "monitor").Muted);
    }

    [MonitorPipeWireFact]
    public void NativePhysicalMuteTogglesTheWholeMonitorWithoutChangingRecordingOrPriorMutes()
        => CheckNativePhysicalMute(firstWasMuted: false);

    [MonitorPipeWireFact]
    public void UnmutingAnOutputMutedBeforeUpgradeRestoresAudioAndTheNextMutePausesMonitoring()
        => CheckNativePhysicalMute(firstWasMuted: true);

    private static void CheckNativePhysicalMute(bool firstWasMuted)
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.Build(MonitorConfig());
        // Each case needs its own device state, including WirePlumber's saved properties.
        string first = firstWasMuted ? "mute_first_closed" : "mute_first_open";
        string closed = first + "_other";
        pw.CreateNullSink(first, first);
        pw.CreateNullSink(closed, closed);
        mixer.SetMixMuted("monitor2", true);
        mixer.SetLevel("test", "monitor", 1);
        mixer.SetLevel("test", "chat", 1);
        SettleSends(mixer);
        WaitForChatPost(mixer);
        pw.SetDefaultSink(first);
        pw.SetSinkMuted(first, firstWasMuted);
        pw.SetSinkMuted(closed, true);
        mixer.SetMonitorOutputs([first, closed]);
        mixer.SyncDeviceVolumes();
        Assert.Equal(firstWasMuted, pw.GetSinkMuted(first));
        Assert.True(pw.GetSinkMuted(closed));
        Assert.False(mixer.MonitorMuted);

        if (firstWasMuted)
        {
            pw.SetSinkMuted(first, false);
            Assert.True(SpinWait.SpinUntil(() =>
                pw.ObservedSinkMutes(new HashSet<string> { first }).TryGetValue(first, out var node) && !node.Muted,
                TimeSpan.FromSeconds(3)));
            mixer.SyncDeviceVolumes();
            Assert.False(pw.GetSinkMuted(first));
            Assert.True(pw.GetSinkMuted(closed));
            Assert.False(mixer.MonitorMuted);
            CaptureLevel(0.1, "OpenXLR_ch_test", first);
        }

        foreach (bool paused in new[] { true, false, true, false })
        {
            Assert.True(ProcessRunner.Run("pactl", ["set-sink-mute", "@DEFAULT_SINK@", "toggle"]).Ok);
            Assert.True(SpinWait.SpinUntil(() =>
            {
                mixer.SyncOwnSinkLevels(out _);
                mixer.SyncDeviceVolumes();
                return mixer.MonitorMuted == paused;
            }, TimeSpan.FromSeconds(3)));
            Assert.Equal(paused, pw.GetSinkMuted(first));
            Assert.True(pw.GetSinkMuted(closed));
            Assert.True(mixer.Snapshot().Mixes.Single(m => m.Id == "monitor2").Muted);
            Assert.False(mixer.SyncOwnSinkLevels(out _));
            mixer.SyncDeviceVolumes(); // Native readback may still complete a saved baseline change.
            Assert.Equal(paused, mixer.MonitorMuted);
            Assert.Equal(paused, pw.GetSinkMuted(first));
            Assert.True(pw.GetSinkMuted(closed));
            mixer.EnsureMonitorRoutes();
        }
        mixer.SetMonitorMuted(true);
        CaptureLevel(0, "OpenXLR_ch_test", first);
        CaptureLevel(0, first, first); // The physical mute also stops playback sent straight to the device.
        SettleSends(mixer);
        CaptureLevel(0.1, "OpenXLR_ch_test", "OpenXLR_post_chat");
        mixer.SetMonitorMuted(false);
        CaptureLevel(0.1, "OpenXLR_ch_test", first);
        CaptureLevel(0.1, first, first);
    }

    [MonitorPipeWireFact]
    public void InitiallyRefusedRecordingMixLinksRecoverWhileMonitoringRemainsPaused()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-paused-post-retry-").FullName;
        string? path = Environment.GetEnvironmentVariable("PATH");
        try
        {
            string realLink = (path ?? "").Split(Path.PathSeparator).Select(p => Path.Combine(p, "pw-link")).First(File.Exists);
            string marker = Path.Combine(directory, "reject");
            File.WriteAllText(marker, "1");
            File.WriteAllText(Path.Combine(directory, "real"), realLink);
            ExecutableScript.Write(Path.Combine(directory, "pw-link"), """
                #!/bin/sh
                folder=$(dirname "$0")
                if [ "$#" -eq 2 ] && [ -f "$folder/reject" ]; then
                    case "$1:$2" in
                      OpenXLR_mix_chat:monitor_FL:OpenXLR_post_chat:playback_FL|OpenXLR_mix_chat:monitor_FR:OpenXLR_post_chat:playback_FR)
                        echo 'transient recording route refusal' >&2
                        exit 1 ;;
                    esac
                fi
                exec "$(cat "$folder/real")" "$@"
                """);
            Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + path);
            var pw = new PipeWireAdapter();
            using var registry = pw.WatchGraph();
            using var mixer = new Mixer(pw);
            mixer.Build(MonitorConfig());
            pw.CreateNullSink("mute_paused_post_repair", "Paused post-link recovery output");
            mixer.SetMonitorOutputs(["mute_paused_post_repair"]);
            mixer.SetLevel("test", "monitor", 1);
            mixer.SetLevel("test", "chat", 1);
            SettleSends(mixer);
            foreach (string side in new[] { "FL", "FR" })
                Assert.DoesNotContain(("OpenXLR_mix_chat:monitor_" + side, "OpenXLR_post_chat:playback_" + side), MixLinks());
            mixer.SetMonitorMuted(true);
            File.Delete(marker);
            WaitForChatPost(mixer);
            Assert.True(mixer.MonitorMuted);
            CaptureLevel(0, "OpenXLR_ch_test", "mute_paused_post_repair");
            CaptureLevel(0.1, "OpenXLR_ch_test", "OpenXLR_post_chat");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
            Directory.Delete(directory, true);
        }
    }

    [MonitorPipeWireFact]
    public void ExplicitlyOpeningOnePausedMonitorReplacesTheSelectionAndUnmutesOnlyItsHardwareFeeds()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.Build(MonitorConfig());
        foreach (string name in new[] { "mute_override_a", "mute_override_b", "mute_override_sum" })
        {
            pw.CreateNullSink(name, name);
            pw.SetSinkMuted(name, false);
        }
        mixer.SetMonitorOutputs(["mute_override_a", "mute_override_b", "mute_override_sum"]);
        Assert.Null(mixer.SetMonitorFeed("mute_override_b", "monitor2"));
        Assert.Null(mixer.SetMonitorFeed("mute_override_sum", "monitor+monitor2"));
        mixer.SetLevel("test", "monitor", 1);
        mixer.SetLevel("test", "monitor2", 1);
        mixer.SetLevel("test", "chat", 1);
        mixer.SetMixMuted("monitor2", true);
        SettleSends(mixer);
        WaitForChatPost(mixer);
        mixer.SetMonitorMuted(true);
        Assert.True(pw.GetSinkMuted("mute_override_a"));
        Assert.True(pw.GetSinkMuted("mute_override_b"));
        Assert.True(pw.GetSinkMuted("mute_override_sum"));

        mixer.SetMixMuted("monitor2", false);
        SettleSends(mixer);
        Assert.False(mixer.MonitorMuted);
        Assert.Equal(["monitor"], mixer.ExportSettings().MixMuted);
        Assert.True(pw.GetSinkMuted("mute_override_a"));
        Assert.False(pw.GetSinkMuted("mute_override_b"));
        Assert.False(pw.GetSinkMuted("mute_override_sum"));
        CaptureLevel(0, "OpenXLR_ch_test", "mute_override_a");
        CaptureLevel(0.1, "OpenXLR_ch_test", "mute_override_b");
        CaptureLevel(0.1, "OpenXLR_ch_test", "mute_override_sum");
        CaptureLevel(0.1, "OpenXLR_ch_test", "OpenXLR_post_chat");
        mixer.ToggleMonitorMuted();
        Assert.True(pw.GetSinkMuted("mute_override_b"));
        mixer.ToggleMonitorMuted();
        SettleSends(mixer);
        Assert.True(pw.GetSinkMuted("mute_override_a"));
        Assert.False(pw.GetSinkMuted("mute_override_b"));
        CaptureLevel(0, "OpenXLR_ch_test", "mute_override_a");
        CaptureLevel(0.1, "OpenXLR_ch_test", "mute_override_b");
    }

    private static void WaitForChatPost(Mixer mixer)
    {
        Assert.True(SpinWait.SpinUntil(() =>
        {
            mixer.EnsureFilterRoutes();
            var links = MixLinks();
            return new[] { "FL", "FR" }.All(side =>
                links.Contains(("OpenXLR_mix_chat:monitor_" + side, "OpenXLR_post_chat:playback_" + side)));
        }, TimeSpan.FromSeconds(4)), "The recording mix did not grow both post links.");
    }

    [MonitorPipeWireFact]
    public void TheFirstPhysicalMuteKeyImmediatelyAfterSelectionPausesMonitoring()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.Build(MonitorConfig());
        pw.CreateNullSink("mute_immediate", "Immediate mute output");
        mixer.SetLevel("test", "monitor", 1);
        mixer.SetLevel("test", "chat", 1);
        SettleSends(mixer);
        pw.SetSinkMuted("mute_immediate", false);
        mixer.SetMonitorOutputs(["mute_immediate"]);

        pw.ToggleSinkMuted("mute_immediate");
        Assert.True(SpinWait.SpinUntil(() =>
        {
            mixer.SyncDeviceVolumes();
            return mixer.MonitorMuted;
        }, TimeSpan.FromSeconds(3)));
        Assert.True(pw.GetSinkMuted("mute_immediate"));
        CaptureLevel(0, "OpenXLR_ch_test", "mute_immediate");
        CaptureLevel(0.1, "OpenXLR_ch_test", "OpenXLR_post_chat");
    }

    [MonitorPipeWireFact]
    public void ABlanketDesktopMuteIsOnePulseAndPreservesMutedMixesAndOutputs()
        => CheckBlanketDesktopMute(applicationDefault: true);

    [MonitorPipeWireFact]
    public void ABlanketDesktopMuteWithAPhysicalDefaultPreservesMutedMixesAndOutputs()
        => CheckBlanketDesktopMute(applicationDefault: false);

    private static void CheckBlanketDesktopMute(bool applicationDefault)
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.Build(MonitorConfig());
        string first = applicationDefault ? "mute_blanket_app_first" : "mute_blanket_physical_first";
        string closed = first + "_closed";
        pw.CreateNullSink(first, "Monitor blanket first");
        pw.CreateNullSink(closed, "Monitor blanket closed");
        pw.SetSinkMuted(closed, true);
        pw.SetDefaultSink(applicationDefault ? "OpenXLR_ch_test" : first);
        mixer.SetMonitorOutputs([first, closed]);
        mixer.SetMixMuted("monitor2", true);
        mixer.SyncOwnSinkLevels(out _);
        mixer.SyncDeviceVolumes();

        foreach (bool paused in new[] { true, false })
        {
            foreach (string sink in pw.OwnSinkLevels().Select(s => s.Name)) pw.SetSinkMuted(sink, true);
            pw.ToggleSinkMuted(first);
            pw.SetSinkMuted(closed, true);
            Assert.True(mixer.SyncOwnSinkLevels(out _));
            mixer.SyncDeviceVolumes();
            Assert.Equal(paused, mixer.MonitorMuted);
            Assert.Equal(["monitor2"], mixer.ExportSettings().MixMuted);
            Assert.True(pw.GetSinkMuted("OpenXLR_mix_monitor2"));
            Assert.False(pw.GetSinkMuted("OpenXLR_mix_monitor"));
            Assert.Equal(paused, pw.GetSinkMuted(first));
            Assert.True(pw.GetSinkMuted(closed));
            Assert.False(mixer.SyncOwnSinkLevels(out _));
            mixer.SyncDeviceVolumes(); // Native readback may still complete a saved baseline change.
            Assert.Equal(paused, mixer.MonitorMuted);
            Assert.Equal(paused, pw.GetSinkMuted(first));
            Assert.True(pw.GetSinkMuted(closed));
        }
    }

    [MonitorPipeWireFact]
    public void ARefusedDefaultApplicationConsumeKeepsRecordingOpenAndThePhysicalBlanketPulseCountsOnce()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.Build(MonitorConfig());
        pw.CreateNullSink("mute_refused_application", "Refused application mute output");
        mixer.SetMonitorOutputs(["mute_refused_application"]);
        mixer.SetMixMuted("monitor2", true);
        mixer.SetLevel("test", "monitor", 1);
        mixer.SetLevel("test", "chat", 1);
        SettleSends(mixer);
        pw.SetDefaultSink("OpenXLR_ch_test");
        mixer.SyncOwnSinkLevels(out _);
        mixer.SyncDeviceVolumes();
        string directory = Directory.CreateTempSubdirectory("openxlr-mute-refusal-").FullName;
        string? path = Environment.GetEnvironmentVariable("PATH");
        string realPactl = (path ?? "").Split(Path.PathSeparator)
            .Select(p => Path.GetFullPath(Path.Combine(p, "pactl"))).First(File.Exists);
        try
        {
            ExecutableScript.Write(Path.Combine(directory, "pactl"), $$"""
                #!/bin/sh
                exec python3 - "$@" <<'PY'
                import os, sys
                args = sys.argv[1:]
                if args == ['set-sink-mute', 'OpenXLR_ch_test', 'toggle']:
                    sys.exit(1)
                os.execv({{JsonSerializer.Serialize(realPactl)}}, ['pactl', *args])
                PY
                """);
            foreach (string sink in pw.OwnSinkLevels().Select(s => s.Name).Concat(["mute_refused_application"]))
                pw.SetSinkMuted(sink, true);
            Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + path);
            Assert.True(mixer.SyncOwnSinkLevels(out var repaired));
            Assert.Contains("OpenXLR_ch_test", repaired);
            Assert.False(pw.GetSinkMuted("OpenXLR_ch_test"));
            Assert.True(mixer.MonitorMuted);
            mixer.SyncDeviceVolumes();
            Assert.True(mixer.MonitorMuted);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
            Directory.Delete(directory, true);
        }
        mixer.SyncOwnSinkLevels(out _);
        mixer.SyncDeviceVolumes();
        mixer.EnsureMonitorRoutes();
        Assert.True(mixer.MonitorMuted);
        Assert.Equal(["monitor2"], mixer.ExportSettings().MixMuted);
        Assert.False(pw.GetSinkMuted("OpenXLR_mix_monitor"));
        Assert.True(pw.GetSinkMuted("OpenXLR_mix_monitor2"));
        CaptureLevel(0, "OpenXLR_ch_test", "mute_refused_application");
        SettleSends(mixer);
        CaptureLevel(0.1, "OpenXLR_ch_test", "OpenXLR_post_chat");
        mixer.SetMonitorMuted(false);
        SettleSends(mixer);
        CaptureLevel(0.1, "OpenXLR_ch_test", "mute_refused_application");
        Assert.Equal(["monitor2"], mixer.ExportSettings().MixMuted);
    }

    [MonitorPipeWireFact]
    public void ARefusedMonitorMixRestorationResumesAudioWithoutUnmutingTheOtherMix()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.Build(MonitorConfig());
        pw.CreateNullSink("mute_restore_a", "Monitor A restoration");
        pw.CreateNullSink("mute_restore_b", "Monitor B restoration");
        mixer.SetMonitorOutputs(["mute_restore_a", "mute_restore_b"]);
        Assert.Null(mixer.SetMonitorFeed("mute_restore_b", "monitor2"));
        mixer.SetMixMuted("monitor2", true);
        mixer.SetLevel("test", "monitor", 1);
        mixer.SetLevel("test", "monitor2", 1);
        SettleSends(mixer);
        pw.SetDefaultSink("OpenXLR_ch_test");
        mixer.SyncOwnSinkLevels(out _);
        mixer.SyncDeviceVolumes();
        string directory = Directory.CreateTempSubdirectory("openxlr-mix-mute-refusal-").FullName;
        string? path = Environment.GetEnvironmentVariable("PATH");
        string realPactl = (path ?? "").Split(Path.PathSeparator)
            .Select(p => Path.GetFullPath(Path.Combine(p, "pactl"))).First(File.Exists);
        try
        {
            ExecutableScript.Write(Path.Combine(directory, "pactl"), $$"""
                #!/bin/sh
                exec python3 - "$@" <<'PY'
                import os, sys
                args = sys.argv[1:]
                if args == ['set-sink-mute', 'OpenXLR_mix_monitor', '0']:
                    sys.exit(1)
                os.execv({{JsonSerializer.Serialize(realPactl)}}, ['pactl', *args])
                PY
                """);
            foreach (string sink in pw.OwnSinkLevels().Select(s => s.Name).Concat(["mute_restore_a", "mute_restore_b"]))
                pw.SetSinkMuted(sink, true);
            Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + path);
            Assert.True(mixer.SyncOwnSinkLevels(out _));
            mixer.SyncDeviceVolumes();
            Assert.True(mixer.MonitorMuted);
            Assert.False(pw.GetSinkMuted("OpenXLR_ch_test"));
            Assert.True(pw.GetSinkMuted("OpenXLR_mix_monitor"));
            Assert.Equal(["monitor2"], mixer.ExportSettings().MixMuted);
            mixer.SyncOwnSinkLevels(out _);
            mixer.SyncDeviceVolumes();
            Assert.Equal(["monitor2"], mixer.ExportSettings().MixMuted);
            Assert.True(pw.GetSinkMuted("OpenXLR_mix_monitor"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
            Directory.Delete(directory, true);
        }
        Assert.True(SpinWait.SpinUntil(() =>
        {
            mixer.SyncOwnSinkLevels(out _);
            mixer.SyncDeviceVolumes();
            return !pw.GetSinkMuted("OpenXLR_mix_monitor");
        }, TimeSpan.FromSeconds(3)));
        mixer.SyncOwnSinkLevels(out _);
        mixer.SetMonitorMuted(false);
        SettleSends(mixer);
        CaptureLevel(0.1, "OpenXLR_ch_test", "mute_restore_a");
        CaptureLevel(0, "OpenXLR_ch_test", "mute_restore_b");
        Assert.False(mixer.MonitorMuted);
        Assert.Equal(["monitor2"], mixer.ExportSettings().MixMuted);
        Assert.True(pw.GetSinkMuted("OpenXLR_mix_monitor2"));
        Assert.True(pw.GetSinkMuted("mute_restore_b"));
    }

    [MonitorPipeWireFact]
    public void PausedMonitoringSurvivesFeedSelectionAndProfileChangesAndSettingsRecall()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.SetMonitorMuted(true); // Startup restores the pause before building any routes.
        mixer.Build(MonitorConfig());
        Assert.True(mixer.MonitorMuted);
        pw.CreateNullSink("mute_old_output", "Monitor old");
        pw.CreateNullSink("mute_new_output", "Monitor new");
        mixer.SetMonitorOutputs(["mute_old_output"]);
        mixer.SetLevel("test", "chat", 1);
        mixer.SetMonitorMuted(true);
        Assert.Null(mixer.SetMonitorFeed("mute_old_output", "chat"));
        mixer.SetMonitorOutputs(["mute_new_output"]);
        Assert.Null(mixer.SetMonitorFeed("mute_new_output", "chat"));
        mixer.SetOutputVolume(1.5);
        mixer.ApplyScene(mixer.ExportScene());
        Assert.True(mixer.MonitorMuted);
        Assert.False(mixer.EnsureMonitorRoutes());
        CaptureLevel(0, "OpenXLR_ch_test", "mute_new_output");
        CaptureLevel(0.1, "OpenXLR_ch_test", "OpenXLR_post_chat");
        MixerSettings saved = mixer.ExportSettings();
        Assert.True(saved.MonitorMuted);
        mixer.SetMonitorMuted(false);
        mixer.ApplySettings(saved);
        Assert.True(mixer.MonitorMuted);
        Assert.False(mixer.EnsureMonitorRoutes());
        mixer.ApplySettings(saved with { MonitorMuted = false });
        Assert.False(mixer.MonitorMuted);
        CaptureLevel(0.1 * Math.Pow(1.5, 3), "OpenXLR_ch_test", "mute_new_output");
    }

    [MonitorPipeWireFact]
    public void AnOutputReturningDuringMonitorMuteStaysSilentUntilTheMasterResumes()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.Build(MonitorConfig());
        uint output = pw.CreateNullSink("mute_returning", "Returning output");
        mixer.SetMonitorOutputs(["mute_returning"]);
        mixer.SetLevel("test", "monitor", 1);
        mixer.SyncDeviceVolumes();
        mixer.SetMonitorMuted(true);
        pw.UnloadModule(output);
        Assert.True(SpinWait.SpinUntil(() => pw.GetSinkVolume("mute_returning") is null, TimeSpan.FromSeconds(3)));
        mixer.SyncDeviceVolumes();
        pw.CreateNullSink("mute_returning", "Returning output");
        Assert.True(SpinWait.SpinUntil(() => pw.GetSinkVolume("mute_returning") is not null, TimeSpan.FromSeconds(3)));
        mixer.SyncDeviceVolumes();
        Assert.True(mixer.MonitorMuted);
        Assert.False(mixer.EnsureMonitorRoutes());
        CaptureLevel(0, "OpenXLR_ch_test", "mute_returning");
        mixer.SetMonitorMuted(false);
        CaptureLevel(0.1, "OpenXLR_ch_test", "mute_returning");
    }

    [MonitorPipeWireFact]
    public void RapidNativeKeyBurstsKeepTheirParityOnApplicationAndPhysicalOutputs()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.Build(MonitorConfig());
        pw.CreateNullSink("mute_burst_output", "Burst output");
        mixer.SetMonitorOutputs(["mute_burst_output"]);
        mixer.SetMixMuted("monitor2", true);
        mixer.SetLevel("test", "monitor", 1);
        foreach (string sink in new[] { "OpenXLR_ch_test", "mute_burst_output" })
        {
            pw.SetDefaultSink(sink);
            mixer.SyncOwnSinkLevels(out _);
            mixer.SyncDeviceVolumes();
            foreach (int count in new[] { 2, 3, 2, 3 })
            {
                bool expected = mixer.MonitorMuted ^ (count % 2 != 0);
                for (int press = 0; press < count; press++) pw.ToggleSinkMuted(sink);
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    mixer.SyncOwnSinkLevels(out _);
                    mixer.SyncDeviceVolumes();
                    return mixer.MonitorMuted == expected && pw.GetSinkMuted(sink) == (sink == "OpenXLR_ch_test" ? false : expected);
                }, TimeSpan.FromSeconds(3)), $"Lost a {count}-press burst on {sink}.");
                Assert.True(mixer.Snapshot().Mixes.Single(m => m.Id == "monitor2").Muted);
            }
            CaptureLevel(0.1, "OpenXLR_ch_test", "mute_burst_output");
        }
    }

    [MonitorPipeWireFact]
    public void MonitoringPauseLeavesBoundMicrophoneCaptureAndApplicationRecordingIndependent()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        pw.CreateNullSink("mute_mic_signal", "Bound microphone signal");
        pw.CreateNullSink("mute_capture_signal", "Capture signal");
        pw.CreateVirtualMic("mute_mic_source", "mute_mic_signal.monitor", "Bound microphone");
        pw.CreateVirtualMic("mute_capture_source", "mute_capture_signal.monitor", "Capture microphone");
        pw.CreateNullSink("mute_multi_output", "Multiple input output");
        mixer.SetInputBinding("xlr1", new("mute_mic_source", 0, 0));
        var levels = new Dictionary<string, double> { ["monitor"] = 1, ["chat"] = 1 };
        mixer.Build(MonitorConfig() with
        {
            Channels = [new("xlr1", "Bound mic") { InputPair = 0, Levels = levels },
                new("capture", "Capture") { CaptureSource = "mute_capture_source", CaptureMonoChannel = 0, Levels = levels },
                new("test", "Application") { Levels = levels }],
        });
        mixer.SetMonitorOutputs(["mute_multi_output"]);
        Assert.True(SpinWait.SpinUntil(() =>
        {
            mixer.EnsureInputFeeds();
            mixer.SyncStreams();
            mixer.EnsureCellLevels();
            return mixer.Snapshot().Channels.Where(c => c.Id is "xlr1" or "capture").All(c => c.BindingConnected);
        }, TimeSpan.FromSeconds(5)));
        mixer.SetMonitorMuted(true);
        foreach (string source in new[] { "mute_mic_signal", "mute_capture_signal", "OpenXLR_ch_test" })
        {
            CaptureLevel(0, source, "mute_multi_output");
            CaptureLevel(0.1, source, "OpenXLR_post_chat");
        }
        Assert.True(ProcessRunner.Run("pactl", ["set-source-mute", "mute_capture_source", "1"]).Ok);
        mixer.SyncOwnSinkLevels(out _);
        mixer.SyncDeviceVolumes();
        Assert.True(mixer.MonitorMuted);
        CaptureLevel(0, "mute_capture_signal", "OpenXLR_post_chat");
        CaptureLevel(0.1, "OpenXLR_ch_test", "OpenXLR_post_chat");
        Assert.True(ProcessRunner.Run("pactl", ["set-source-mute", "mute_capture_source", "0"]).Ok);
        // A native device mute also covers applications bypassing our feeds.
        CaptureLevel(0, "mute_multi_output", "mute_multi_output");
        mixer.ToggleMonitorMuted();
        mixer.ToggleMonitorMuted();
        Assert.True(mixer.MonitorMuted);
        mixer.ToggleMonitorMuted();
        CaptureLevel(0.1, "mute_mic_signal", "mute_multi_output");
        CaptureLevel(0.1, "mute_capture_signal", "mute_multi_output");
    }
}
