using OpenXLR.Core;
using OpenXLR.Core.Mixing;
using System.Text.Json;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public void RegistrySubscriptionRecoversAfterItsHelperIsKilled()
    {
        string dir = Path.Combine(Path.GetTempPath(), "openxlr-registry-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        string script = Path.Combine(dir, "watch");
        ExecutableScript.Write(script, """
            #!/bin/sh
            printf '%s\n' "$$" >> "$(dirname "$0")/launches"
            exec pw-dump "$@"
            """);
        var pw = new PipeWireAdapter();
        try
        {
            uint oldModule = pw.CreateNullSink("registry_before", "Registry before");
            using var graph = new PipeWireGraph(executable: script);
            Assert.True(graph.WaitFor(items => HasNode(items, "registry_before"), TimeSpan.FromSeconds(5)));
            JsonElement[] before = graph.Read();
            int pid = int.Parse(File.ReadAllLines(Path.Combine(dir, "launches"))[0]);
            using (var helper = System.Diagnostics.Process.GetProcessById(pid)) helper.Kill();
            pw.UnloadModule(oldModule);
            pw.CreateNullSink("registry_after", "Registry after");
            Assert.True(graph.WaitFor(items => HasNode(items, "registry_after") && !HasNode(items, "registry_before"), TimeSpan.FromSeconds(8)));
            Assert.True(File.ReadAllLines(Path.Combine(dir, "launches")).Length >= 2);
            Assert.True(HasNode(before, "registry_before"));
        }
        finally { pw.TearDown(); Directory.Delete(dir, true); }

        static bool HasNode(JsonElement[] items, string name) => items.Any(item =>
            item.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object &&
            info.TryGetProperty("props", out var props) && props.TryGetProperty("node.name", out var node) && node.GetString() == name);
    }

    [MonitorPipeWireFact]
    public void AnyMixFeedsSurviveRecallAndDeletionCannotLeaveAStaleRoute()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        uint output = pw.CreateNullSink("test_feeds_output", "Feed test");
        try
        {
            mixer.Build(MonitorConfig());
            mixer.SetMonitorOutputs(["test_feeds_output"]);
            Assert.Null(mixer.SetMonitorFeed("test_feeds_output", "chat+monitor"));
            Assert.Equal("monitor+chat", mixer.MonitorFeedOf("test_feeds_output"));
            var scene = mixer.ExportScene();
            Assert.Null(mixer.SetMonitorFeed("test_feeds_output", "monitor2"));
            mixer.ApplyScene(scene);
            Assert.Equal("monitor+chat", mixer.MonitorFeedOf("test_feeds_output"));

            Assert.Throws<IOException>(() => mixer.DeleteVirtualMix("chat", _ => "disk full"));
            Assert.True(mixer.HasMix("chat"));
            Assert.Equal("monitor+chat", mixer.MonitorFeedOf("test_feeds_output"));
            AssertIncoming("OpenXLR_mix_monitor", "OpenXLR_mix_chat");

            MixerSettings? saved = null;
            mixer.DeleteVirtualMix("chat", settings => { saved = settings; return null; });
            Assert.False(mixer.HasMix("chat"));
            Assert.DoesNotContain(saved!.MonitorFeeds.Values, feed => MonitorFeed.Includes(feed, "chat"));
            Assert.Equal("monitor", mixer.MonitorFeedOf("test_feeds_output"));
            AssertIncoming("OpenXLR_mix_monitor");
            mixer.CreateVirtualMix("Podcast", _ => null);
            Assert.Null(mixer.SetMonitorFeed("test_feeds_output", "podcast"));
            AssertIncoming("OpenXLR_mix_podcast");
            mixer.DeleteVirtualMix("podcast", _ => null);
            Assert.False(mixer.ExportSettings().MonitorFeeds.ContainsKey("test_feeds_output"));
            AssertIncoming("OpenXLR_mix_monitor");

            void AssertIncoming(params string[] expected)
            {
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    var result = ProcessRunner.Run("pw-dump", []);
                    if (!result.Ok) return false;
                    using var graph = System.Text.Json.JsonDocument.Parse(result.Stdout);
                    var nodes = graph.RootElement.EnumerateArray()
                        .Where(n => n.GetProperty("type").GetString() == "PipeWire:Interface:Node")
                        .ToDictionary(n => n.GetProperty("id").GetInt32(), n => n.GetProperty("info").GetProperty("props").GetProperty("node.name").GetString());
                    int target = nodes.Single(n => n.Value == "test_feeds_output").Key;
                    var sources = graph.RootElement.EnumerateArray()
                        .Where(n => n.GetProperty("type").GetString() == "PipeWire:Interface:Link")
                        .Select(n => n.GetProperty("info"))
                        .Where(n => n.GetProperty("input-node-id").GetInt32() == target)
                        .Select(n => nodes.GetValueOrDefault(n.GetProperty("output-node-id").GetInt32())).ToHashSet();
                    return sources.SetEquals(expected);
                }, TimeSpan.FromSeconds(3)), "The selected output did not follow the saved feed.");
            }
        }
        finally { pw.UnloadModule(output); }
    }

    [MonitorPipeWireFact]
    public void StreamAssignmentsCannotBypassTheAppLimitOrPartiallyMoveAudio()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.Build(new MixerConfig
        {
            Channels = [new("system", "System"), new("music", "Music")],
            Mixes = [new("monitor", "Monitor", MixKind.Monitor)],
        });
        using var stop = new CancellationTokenSource();
        Task<ProcessResult> playback = ProcessRunner.RunAsync("python3", ["-c", """
            import subprocess
            args = ['--format=f32', '--rate=48000', '--channels=2']
            if '--raw' in subprocess.check_output(['pw-cat', '--help'], text=True):
                args.append('--raw')
            # A real application playback stream, not a loopback: the mixer
            # deliberately excludes loopbacks from application routing.
            with open('/dev/zero', 'rb') as silence:
                subprocess.run(['pw-cat', '--playback', *args, '--target', 'OpenXLR_ch_system',
                    '--properties={ application.name = assignment-test application.process.binary = assignment-test }', '-'],
                    stdin=silence, check=True)
            """],
            TimeSpan.FromSeconds(30), cancel: stop.Token);
        try
        {
            Assert.True(SpinWait.SpinUntil(() =>
            {
                mixer.SyncStreams();
                return mixer.Streams.Any(s => s.Identity == "assignment-test");
            }, TimeSpan.FromSeconds(5)));
            StreamAssignment stream = mixer.Streams.Single(s => s.Identity == "assignment-test");
            for (int i = 0; i < Mixer.MaxAppOverrides; i++) mixer.Matcher.SetOverride($"app{i}", "system");
            foreach (string channel in new[] { "music", StreamMatcher.Ignore })
            {
                Assert.Throws<InvalidOperationException>(() => mixer.AssignStream(stream.Id, channel));
                Assert.Equal("OpenXLR_ch_system", pw.StreamSinkName(stream.Serial));
                Assert.False(mixer.Matcher.Overrides.ContainsKey(stream.Identity));
                Assert.Contains(mixer.Streams, s => s.Id == stream.Id);
            }
            mixer.ForgetApp("app0");
            mixer.AssignStream(stream.Id, "music");
            Assert.Equal(Mixer.MaxAppOverrides, mixer.OverrideCount);
            Assert.True(SpinWait.SpinUntil(() =>
            {
                mixer.SyncStreams();
                return mixer.Streams.Any(s => s.Serial == stream.Serial && s.ChannelId == "music")
                    && pw.StreamSinkName(stream.Serial) == "OpenXLR_ch_music";
            }, TimeSpan.FromSeconds(3)));
            mixer.AssignStream(stream.Id, StreamMatcher.Ignore);
            Assert.Equal(StreamMatcher.Ignore, mixer.Matcher.Overrides[stream.Identity]);
            Assert.Equal(Mixer.MaxAppOverrides, mixer.OverrideCount);
        }
        finally
        {
            stop.Cancel();
            playback.GetAwaiter().GetResult();
        }
    }

    [MonitorPipeWireFact]
    public void DesktopAndMixerShareTheSelectedOutputVolume()
    {
        // The runner creates a private PipeWire server. Never build or unload
        // a graph against the user's desktop during a regular test run.
        Assert.Equal("1", Environment.GetEnvironmentVariable("OPENXLR_TEST_MONITOR_VOLUME"));
        Assert.StartsWith("openxlr-monitor-test-", Path.GetFileName(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")!));
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        pw.CreateNullSink("test_headphones", "Test headphones");
        pw.CreateNullSink("test_speakers", "Test speakers");
        mixer.Build(new MixerConfig { Channels = [], Mixes = [] });
        pw.SetSinkVolume("test_headphones", 0.67);
        pw.SetSinkVolume("test_speakers", 0.45);
        mixer.SetMonitorOutputs(["test_headphones", "test_speakers"]);
        mixer.SetEnforcedDefaults(Mixer.FollowMonitorOutput, null);
        Assert.True(SpinWait.SpinUntil(() => pw.GetDefaultSink() == "test_headphones", TimeSpan.FromSeconds(3)));
        Assert.True(mixer.SyncDeviceVolumes());
        Assert.Equal(0.67, mixer.Snapshot().OutputVolume);

        // The same command desktop media-key handlers use, against the
        // default device rather than an OpenXLR API command.
        ProcessResult command = ProcessRunner.Run("pactl", ["set-sink-volume", "@DEFAULT_SINK@", "40%"]);
        Assert.True(command.Ok, command.Stderr);
        Assert.True(mixer.SyncDeviceVolumes());
        Assert.Equal(0.4, mixer.Snapshot().OutputVolume);
        Assert.Equal(0.4, pw.GetSinkVolume("test_speakers"));
        Assert.False(mixer.SyncDeviceVolumes());
        // The graph dump is cached for a fraction of a second, so ask for the
        // node until the snapshot the adapter holds is one that has it.
        int node = 0;
        Assert.True(SpinWait.SpinUntil(() => (node = pw.FindNodeId("test_headphones") ?? 0) != 0, TimeSpan.FromSeconds(3)));
        command = ProcessRunner.Run("wpctl", ["set-volume", node.ToString(), "0.3"]);
        Assert.True(command.Ok, command.Stderr);
        Assert.True(mixer.SyncDeviceVolumes());
        Assert.Equal(0.3, mixer.Snapshot().OutputVolume);
        Assert.Equal(0.3, pw.GetSinkVolume("test_speakers"));

        // Desktop applets and media keys go past unity. What is read there
        // has to be what the other selected outputs are set to, or they sit
        // quieter with the mixer believing both are at 120%.
        command = ProcessRunner.Run("pactl", ["set-sink-volume", "@DEFAULT_SINK@", "120%"]);
        Assert.True(command.Ok, command.Stderr);
        Assert.True(mixer.SyncDeviceVolumes());
        Assert.Equal(1.2, mixer.Snapshot().OutputVolume);
        Assert.Equal(1.2, pw.GetSinkVolume("test_speakers"));
        Assert.False(mixer.SyncDeviceVolumes());

        command = ProcessRunner.Run("pactl", ["set-sink-volume", "@DEFAULT_SINK@", "150%"]);
        Assert.True(command.Ok, command.Stderr);
        Assert.True(mixer.SyncDeviceVolumes());
        Assert.Equal(1.5, mixer.Snapshot().OutputVolume);
        Assert.Equal(1.5, pw.GetSinkVolume("test_speakers"));

        mixer.SetOutputVolume(0.58);
        Assert.Equal(0.58, pw.GetSinkVolume("test_headphones"));
        Assert.Equal(0.58, pw.GetSinkVolume("test_speakers"));
        Assert.False(mixer.SyncDeviceVolumes());

        // Relinking the same devices must not swallow the next desktop
        // adjustment before the daemon's next volume poll.
        mixer.SetMonitorOutputs(["test_headphones", "test_speakers"]);
        command = ProcessRunner.Run("pactl", ["set-sink-volume", "@DEFAULT_SINK@", "52%"]);
        Assert.True(command.Ok, command.Stderr);
        Assert.True(mixer.SyncDeviceVolumes());
        Assert.Equal(0.52, mixer.Snapshot().OutputVolume);
        Assert.Equal(0.52, pw.GetSinkVolume("test_speakers"));
        Assert.False(mixer.SyncDeviceVolumes());

        pw.SetSinkVolume("test_speakers", 0.31);
        mixer.SetMonitorOutputs(["test_speakers", "test_headphones"]);
        mixer.EnforceDefaults();
        Assert.True(SpinWait.SpinUntil(() => pw.GetDefaultSink() == "test_speakers", TimeSpan.FromSeconds(3)));
        mixer.SyncDeviceVolumes();
        Assert.Equal(0.31, mixer.Snapshot().OutputVolume);
        Assert.Equal(0.52, pw.GetSinkVolume("test_headphones"));

        mixer.SetMonitorOutputs([]);
        Assert.False(mixer.EnforceDefaults());
        Assert.Equal("test_speakers", pw.GetDefaultSink());
        mixer.SyncDeviceVolumes();
        Assert.Null(mixer.Snapshot().OutputVolume);
    }

    [MonitorPipeWireFact]
    public void AnOutputThatRefusedTheVolumeIsPutRightWhenItComesBack()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        pw.CreateNullSink("retry_first", "Retry first");
        uint second = pw.CreateNullSink("retry_second", "Retry second");
        mixer.Build(new MixerConfig { Channels = [], Mixes = [] });
        pw.SetSinkVolume("retry_first", 0.6);
        pw.SetSinkVolume("retry_second", 0.6);
        mixer.SetMonitorOutputs(["retry_first", "retry_second"]);
        mixer.SetEnforcedDefaults(Mixer.FollowMonitorOutput, null);
        Assert.True(mixer.SyncDeviceVolumes());
        Assert.Equal(0.6, mixer.Snapshot().OutputVolume);

        // The second device leaves (suspend, unplug, a profile change) and
        // the desktop turns the first one down while it is away.
        pw.UnloadModule(second);
        Assert.True(SpinWait.SpinUntil(() => pw.GetSinkVolume("retry_second") is null, TimeSpan.FromSeconds(3)));
        ProcessResult command = ProcessRunner.Run("pactl", ["set-sink-volume", "retry_first", "20%"]);
        Assert.True(command.Ok, command.Stderr);
        Assert.True(mixer.SyncDeviceVolumes());
        Assert.Equal(0.2, mixer.Snapshot().OutputVolume);

        // It comes back at the level it remembers, and nothing touches the
        // first output again: only what it was owed can put it right.
        pw.CreateNullSink("retry_second", "Retry second");
        pw.SetSinkVolume("retry_second", 0.6);
        Assert.False(mixer.SyncDeviceVolumes());
        Assert.Equal(0.2, pw.GetSinkVolume("retry_second"));
        Assert.Equal(0.2, mixer.Snapshot().OutputVolume);

        // And once delivered it is left alone, desktop volume and all.
        pw.SetSinkVolume("retry_second", 0.7);
        Assert.False(mixer.SyncDeviceVolumes());
        Assert.Equal(0.7, pw.GetSinkVolume("retry_second"));
    }

    [MonitorPipeWireFact]
    public void ANewSelectionDropsWhatTheOldOneStillOwed()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        pw.CreateNullSink("stale_first", "Stale first");
        uint second = pw.CreateNullSink("stale_second", "Stale second");
        pw.CreateNullSink("stale_third", "Stale third");
        mixer.Build(new MixerConfig { Channels = [], Mixes = [] });
        pw.SetSinkVolume("stale_first", 0.6);
        pw.SetSinkVolume("stale_second", 0.6);
        pw.SetSinkVolume("stale_third", 0.9);
        mixer.SetMonitorOutputs(["stale_first", "stale_second"]);
        mixer.SetEnforcedDefaults(Mixer.FollowMonitorOutput, null);
        Assert.True(mixer.SyncDeviceVolumes());

        pw.UnloadModule(second);
        Assert.True(SpinWait.SpinUntil(() => pw.GetSinkVolume("stale_second") is null, TimeSpan.FromSeconds(3)));
        ProcessResult command = ProcessRunner.Run("pactl", ["set-sink-volume", "stale_first", "20%"]);
        Assert.True(command.Ok, command.Stderr);
        Assert.True(mixer.SyncDeviceVolumes());
        pw.CreateNullSink("stale_second", "Stale second");
        pw.SetSinkVolume("stale_second", 0.6);

        // Another device leads the selection now, so its own level is the
        // baseline and the level owed to the old one is not written anywhere.
        mixer.SetMonitorOutputs(["stale_third", "stale_second"]);
        mixer.SyncDeviceVolumes();
        Assert.Equal(0.9, mixer.Snapshot().OutputVolume);
        Assert.Equal(0.6, pw.GetSinkVolume("stale_second"));
        Assert.False(mixer.SyncDeviceVolumes());
        Assert.Equal(0.6, pw.GetSinkVolume("stale_second"));
    }
    private static MixerConfig MonitorConfig() => new()
    {
        Mixes = [new("monitor", "Monitor A", MixKind.Monitor), new("monitor2", "Monitor B", MixKind.Monitor),
            new("chat", "Chat", MixKind.VirtualMic)],
        Channels = [new("test", "Test") { Levels = new Dictionary<string, double>
            { ["monitor"] = 0.8, ["monitor2"] = 0.6, ["chat"] = 0.7 } }],
    };

    [MonitorPipeWireFact]
    public void DesktopMonitorMastersAreIndependentAndSurviveSceneAndSettingsRecall()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.Build(MonitorConfig());
        mixer.SetMixVolume("monitor2", 0.6);
        pw.SetDefaultSink("OpenXLR_mix_monitor");
        Assert.True(SpinWait.SpinUntil(() => pw.GetDefaultSink() == "OpenXLR_mix_monitor", TimeSpan.FromSeconds(3)));
        foreach (double volume in new[] { 0, 0.5, 1, 1.2, 1.5, 0.25 })
        {
            var result = ProcessRunner.Run("pactl", ["set-sink-volume", "@DEFAULT_SINK@", $"{volume * 100:0}%"]);
            Assert.True(result.Ok, result.Stderr);
            Assert.True(SpinWait.SpinUntil(() => { mixer.SyncMonitorVolumes(); return mixer.Snapshot().Mixes.Single(m => m.Id == "monitor").Volume == volume; }, TimeSpan.FromSeconds(3)));
            Assert.Equal(0.6, mixer.Snapshot().Mixes.Single(m => m.Id == "monitor2").Volume);
            Assert.DoesNotContain("OpenXLR_mix_monitor", mixer.EnsureOwnSinkLevels());
            Assert.Equal(volume, pw.GetSinkVolume("OpenXLR_mix_monitor"));
            Assert.False(mixer.SyncMonitorVolumes());
        }
        pw.SetSinkVolume("OpenXLR_mix_monitor", 1.5);
        Assert.True(mixer.SyncMonitorVolumes());
        var excessive = ProcessRunner.Run("pactl", ["set-sink-volume", "OpenXLR_mix_monitor", "200%"]);
        Assert.True(excessive.Ok, excessive.Stderr);
        Assert.True(SpinWait.SpinUntil(() => { mixer.SyncMonitorVolumes(); return pw.GetSinkVolume("OpenXLR_mix_monitor") == 1.5; }, TimeSpan.FromSeconds(3)));
        mixer.SetMixVolume("monitor", -1);
        Assert.Equal(0, pw.GetSinkVolume("OpenXLR_mix_monitor"));
        mixer.SetMixVolume("monitor", 2);
        Assert.Equal(1.5, pw.GetSinkVolume("OpenXLR_mix_monitor"));
        mixer.SetMixMuted("monitor", true);
        // A cached graph from the preceding read cannot undo a UI write.
        Assert.False(mixer.SyncMonitorVolumes());
        Assert.Equal(1.5, pw.GetSinkVolume("OpenXLR_mix_monitor"));
        Assert.True(pw.OwnSinkLevels().Single(s => s.Name == "OpenXLR_mix_monitor").Muted);
        pw.SetSinkMuted("OpenXLR_mix_monitor", false);
        Assert.True(mixer.SyncMonitorVolumes());
        Assert.False(mixer.Snapshot().Mixes.Single(m => m.Id == "monitor").Muted);

        pw.SetDefaultSink("OpenXLR_mix_monitor2");
        Assert.True(SpinWait.SpinUntil(() => pw.GetDefaultSink() == "OpenXLR_mix_monitor2", TimeSpan.FromSeconds(3)));
        var command = ProcessRunner.Run("wpctl", ["set-volume", "@DEFAULT_AUDIO_SINK@", "1.2"]);
        Assert.True(command.Ok, command.Stderr);
        Assert.True(SpinWait.SpinUntil(() => { mixer.SyncMonitorVolumes(); return mixer.Snapshot().Mixes.Single(m => m.Id == "monitor2").Volume == 1.2; }, TimeSpan.FromSeconds(3)));
        Assert.Equal(1.5, mixer.Snapshot().Mixes.Single(m => m.Id == "monitor").Volume);
        pw.SetSinkMuted("OpenXLR_mix_monitor2", true);
        Assert.True(mixer.SyncMonitorVolumes());
        Assert.True(mixer.Snapshot().Mixes.Single(m => m.Id == "monitor2").Muted);
        Assert.False(mixer.Snapshot().Mixes.Single(m => m.Id == "monitor").Muted);

        var scene = mixer.ExportScene();
        var settings = mixer.ExportSettings();
        mixer.SetMixVolume("monitor", 0.1);
        mixer.SetMixMuted("monitor2", false);
        mixer.ApplyScene(scene);
        Assert.Equal(1.5, pw.GetSinkVolume("OpenXLR_mix_monitor"));
        Assert.True(pw.OwnSinkLevels().Single(s => s.Name == "OpenXLR_mix_monitor2").Muted);
        mixer.Build(MonitorConfig());
        mixer.ApplySettings(settings);
        Assert.Equal(1.5, pw.GetSinkVolume("OpenXLR_mix_monitor"));
        Assert.Equal(1.2, pw.GetSinkVolume("OpenXLR_mix_monitor2"));
        Assert.True(pw.OwnSinkLevels().Single(s => s.Name == "OpenXLR_mix_monitor2").Muted);
        Assert.False(mixer.SyncMonitorVolumes());

        // Internal channel and virtual-microphone sinks still recover from
        // desktop changes, and non-monitor masters keep their previous range.
        mixer.SetMixVolume("chat", 1.5);
        Assert.Equal(1, mixer.Snapshot().Mixes.Single(m => m.Id == "chat").Volume);
        pw.SetSinkVolume("OpenXLR_ch_test", 0.5);
        pw.SetSinkMuted("OpenXLR_mix_chat", true);
        Assert.Contains("OpenXLR_ch_test", mixer.EnsureOwnSinkLevels());
        Assert.Equal(1, pw.GetSinkVolume("OpenXLR_ch_test"));
        Assert.False(pw.OwnSinkLevels().Single(s => s.Name == "OpenXLR_mix_chat").Muted);
    }

    [MonitorPipeWireFact]
    public void MonitorMasterChangesTheRecordedAudioExactlyOnce()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        pw.CreateNullSink("test_master_output", "Test master output");
        var config = MonitorConfig();
        mixer.Build(config with
        {
            Mixes = [.. config.Mixes, new("stream", "Stream", MixKind.VirtualMic), new("auxout", "Aux", MixKind.AuxPort)],
            Channels = [config.Channels[0] with { Levels = new Dictionary<string, double>(config.Channels[0].Levels)
                { ["stream"] = 0.4, ["auxout"] = 0.3 } }],
        });
        mixer.SetMonitorOutputs(["test_master_output"]);
        // Wait for late combine legs and their initial channel sends.
        Assert.True(SpinWait.SpinUntil(() => { mixer.EnsureCellLevels(); return pw.FindNodeId("OpenXLR_ch_test") is not null; }, TimeSpan.FromSeconds(3)));
        foreach (var (feed, a, b, muteA, muteB, direct) in new[]
        {
            ("monitor", 1.0, 1.0, false, false, false),
            ("monitor", 0.5, 1.0, false, false, false),
            ("monitor", 1.5, 1.0, false, false, false),
            ("monitor", 0.0, 1.0, false, false, false),
            ("monitor", 0.8, 1.0, true, false, false),
            ("monitor2", 0.2, 1.5, false, false, false),
            ("monitor+monitor2", 0.5, 1.2, false, false, false),
            ("monitor+monitor2", 0.5, 0.7, true, false, false),
            ("monitor", 1.2, 1.0, false, false, true),
            ("chat", 1.0, 1.0, false, false, false),
            ("stream", 1.0, 1.0, false, false, false),
            ("auxout", 1.0, 1.0, false, false, false),
            ("monitor+chat", 0.5, 1.0, false, false, false),
        })
        {
            mixer.SetMixVolume("monitor", a);
            mixer.SetMixVolume("monitor2", b);
            mixer.SetMixMuted("monitor", muteA);
            mixer.SetMixMuted("monitor2", muteB);
            Assert.Null(mixer.SetMonitorFeed("test_master_output", feed));
            double expected = direct ? 0.1 * Math.Pow(a, 3) :
                (feed.Split('+').Contains("monitor") && !muteA ? 0.1 * Math.Pow(0.8 * a, 3) : 0) +
                (feed.Split('+').Contains("monitor2") && !muteB ? 0.1 * Math.Pow(0.6 * b, 3) : 0) +
                (feed.Split('+').Contains("chat") ? 0.1 * Math.Pow(0.7, 3) : 0) +
                (feed.Split('+').Contains("stream") ? 0.1 * Math.Pow(0.4, 3) : 0) +
                (feed.Split('+').Contains("auxout") ? 0.1 * Math.Pow(0.3, 3) : 0);
            Capture(expected, direct);
        }

        mixer.SetMixVolume("monitor", 1);
        mixer.SetMixMuted("monitor", false);
        // A summed feed links every mix at unity; a second output keeps
        // its own feed.
        Assert.Null(mixer.SetMonitorFeed("test_master_output", "monitor+chat"));
        Capture(.1 * (Math.Pow(.8, 3) + Math.Pow(.7, 3)), false);
        pw.CreateNullSink("test_feed_other", "Other output");
        mixer.SetMonitorOutputs(["test_master_output", "test_feed_other"]);
        Assert.Null(mixer.SetMonitorFeed("test_feed_other", "chat"));
        Capture(.1 * Math.Pow(.7, 3), false, "test_feed_other");
        Capture(.1 * (Math.Pow(.8, 3) + Math.Pow(.7, 3)), false);

    }

    private static void Capture(double expected, bool direct, string output = "test_master_output")
    {
        var result = ProcessRunner.Run("python3", ["-c", """
            import struct, subprocess, sys, tempfile
            rate = 48000
            # Separate combine streams can acquire different latencies. A sine
            # can cancel itself when Monitor A and B are summed, although both
            # gains are correct. DC tests the gain independently of that phase.
            samples = struct.pack('<ff', 0.1, 0.1) * (rate * 2)
            args = ['--format=f32', '--rate=48000', '--channels=2']
            # Older pw-cat versions use raw audio on stdin/stdout implicitly.
            if '--raw' in subprocess.check_output(['pw-cat', '--help'], text=True):
                args.append('--raw')
            capture = tempfile.TemporaryFile()
            record = subprocess.Popen(['pw-cat', '--record', *args, '--target', sys.argv[3], '--properties={ stream.capture.sink = true }', '-'], stdout=capture, stderr=subprocess.PIPE)
            play = None
            try:
                play = subprocess.Popen(['pw-cat', '--playback', *args, '--target', sys.argv[2], '-'], stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
                _, playback_errors = play.communicate(samples, timeout=10)
                assert play.returncode == 0, playback_errors.decode()
                record.terminate()
                record.communicate(timeout=5)
                capture.seek(0)
                audio = capture.read()
                values = struct.unpack('<' + 'f' * (len(audio) // 4), audio)
                assert len(values) > rate, 'Capture did not run'
                expected = float(sys.argv[1])
                # The capture starts before playback and is cut when it
                # ends, so the settled window is found from the audio
                # itself: 100 ms inside the first and last frame carrying
                # signal. A resampler on a combine leg rings for a few
                # samples at the DC edges, well inside that margin.
                frames = [values[i:i + 2] for i in range(0, len(values), 2)]
                floor = max(expected / 2, 0.001)
                carrying = [0, len(frames) - 1] if expected == 0 else [i for i, frame in enumerate(frames) if max(map(abs, frame)) > floor]
                assert carrying, 'No audio reached the output; links=' + subprocess.check_output(['pw-link', '-l'], text=True)
                margin = rate // 10
                steady = frames[carrying[0] + margin : carrying[-1] + 1 - margin]
                assert len(steady) >= rate, f'Only {len(steady)} settled frames'
                settled = [value for frame in steady for value in frame]
                matching = sum(abs(value - expected) < 0.002 for value in settled) / len(settled)
                peak = max(map(abs, values))
                assert matching > 0.99 and peak < 1.1 * expected + 0.002, f'Wrong master gain: peak={peak}, expected={expected}, matching={matching}; links=' + subprocess.check_output(['pw-link', '-l'], text=True)
                print(f'Expected {expected:.5f}: peak={peak:.5f}, matching={matching:.1%}')
            finally:
                for process in (play, record):
                    if process is None: continue
                    if process.poll() is None: process.kill()
                    process.wait()
                capture.close()
            """, expected.ToString(System.Globalization.CultureInfo.InvariantCulture),
            direct ? "OpenXLR_mix_monitor" : "OpenXLR_ch_test", output], TimeSpan.FromSeconds(20));
        Assert.True(result.Ok, result.StdoutText + result.Stderr);
    }

}

internal sealed class MonitorPipeWireFactAttribute : FactAttribute
{
    public MonitorPipeWireFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENXLR_TEST_MONITOR_VOLUME") != "1")
            Skip = "Run tools/test-monitor-volume.py against a private PipeWire server.";
    }
}
