using System.Reflection;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class MonitorPauseUnlinkTests
{
    private const string LinkedGraph = """
        [
          {"id":1,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"monitor"}}},
          {"id":2,"type":"PipeWire:Interface:Node","info":{"props":{"node.name":"speakers"}}},
          {"id":11,"type":"PipeWire:Interface:Port","info":{"props":{"node.id":"1","port.name":"monitor_FL"}}},
          {"id":21,"type":"PipeWire:Interface:Port","info":{"props":{"node.id":"2","port.name":"playback_FL"}}},
          {"id":30,"type":"PipeWire:Interface:Link","info":{"output-port-id":11,"input-port-id":21}}
        ]
        """;

    [Theory]
    [InlineData(LinkedGraph, false)]
    [InlineData("{", false)]
    [InlineData("unavailable", false)]
    [InlineData("[]", true)]
    public void ARefusedDisconnectUsesAFreshSnapshotInsteadOfCachedAbsence(string graph, bool removed)
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-pause-confirm-").FullName;
        string? previousPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            File.WriteAllText(Path.Combine(directory, "graph"), "[]");
            ExecutableScript.Write(Path.Combine(directory, "pw-dump"), """
                dir=${0%/*}
                printf 'dump\n' >> "$dir/dumps"
                if [ -f "$dir/unavailable" ]; then exit 1; fi
                /bin/cat "$dir/graph"
                """);
            ExecutableScript.Write(Path.Combine(directory, "pw-link"), """
                dir=${0%/*}
                printf '%s\n' "$*" >> "$dir/calls"
                echo 'temporary disconnect refusal' >&2
                exit 1
                """);
            Environment.SetEnvironmentVariable("PATH", directory);
            var adapter = new PipeWireAdapter();
            Assert.Empty(adapter.OwnSinkLevels());
            File.WriteAllText(Path.Combine(directory, "graph"), graph);
            if (graph == "unavailable") File.WriteAllText(Path.Combine(directory, "unavailable"), "");

            Assert.Equal(removed, adapter.EnsureUnlinked(new PortLink([("monitor:monitor_FL", "speakers:playback_FL")])));
            Assert.Equal(["-d monitor:monitor_FL speakers:playback_FL"], File.ReadAllLines(Path.Combine(directory, "calls")));
            Assert.Equal(2, File.ReadAllLines(Path.Combine(directory, "dumps")).Length);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARefusedDisconnectKeepsItsOwnerThroughSelectionChangesUntilItCanBeRemoved(bool deletingMonitor)
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-pause-unlink-").FullName;
        string? previousPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            File.WriteAllText(Path.Combine(directory, "graph"), LinkedGraph);
            File.WriteAllText(Path.Combine(directory, "refuse"), "");
            ExecutableScript.Write(Path.Combine(directory, "pw-dump"), """
                dir=${0%/*}
                /bin/cat "$dir/graph"
                """);
            ExecutableScript.Write(Path.Combine(directory, "pw-link"), """
                dir=${0%/*}
                printf '%s\n' "$*" >> "$dir/calls"
                if [ -f "$dir/refuse" ]; then echo 'temporary disconnect refusal' >&2; exit 1; fi
                printf '[]' > "$dir/graph"
                """);
            Environment.SetEnvironmentVariable("PATH", directory);
            using var mixer = new Mixer(new PipeWireAdapter());
            var mixRepairs = Field<Dictionary<string, (int Failures, long RetryAt)>>("_desktopMixMuteResets");
            if (deletingMonitor)
            {
                typeof(Mixer).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mixer,
                    new MixerConfig { Channels = [], Mixes = [new("monitor", "Speakers", MixKind.Monitor), new("monitor2", "Closed", MixKind.Monitor)] });
                Field<HashSet<string>>("_mixMuted").Add("monitor2");
                mixRepairs["OpenXLR_mix_monitor"] = (3, 123);
                mixRepairs["OpenXLR_mix_monitor2"] = (5, 456);
            }
            typeof(Mixer).GetField("_built", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mixer, true);
            if (deletingMonitor)
            {
                mixer.RenameMix("monitor", "Renamed speakers", _ => null);
                Assert.Equal((3, 123L), mixRepairs["OpenXLR_mix_monitor"]);
            }
            var routes = Field<Dictionary<string, PortLink>>("_monitorRoutes");
            routes["speakers"] = new PortLink([("monitor:monitor_FL", "speakers:playback_FL")]);
            Field<List<string>>("_monitorOutputs").Add("speakers");

            mixer.SetMonitorMuted(true);
            Assert.Single(routes);
            mixer.SetMonitorOutputs([]);
            Assert.Single(routes);
            bool persisted = false;
            if (deletingMonitor)
            {
                Assert.Throws<InvalidOperationException>(() => mixer.DeleteMix("monitor", _ => { persisted = true; return null; }));
                Assert.False(persisted);
                Assert.True(mixer.HasMix("monitor"));
                Assert.Equal(["monitor2"], mixer.ExportSettings().MixMuted);
                Assert.Equal((3, 123L), mixRepairs["OpenXLR_mix_monitor"]);
            }
            else Assert.Throws<InvalidOperationException>(() => mixer.SetMonitorMuted(false));
            Assert.True(mixer.MonitorMuted);
            Assert.Single(routes);
            File.Delete(Path.Combine(directory, "refuse"));

            Assert.True(mixer.EnsureMonitorRoutes());
            Assert.Empty(routes);
            string[] calls = File.ReadAllLines(Path.Combine(directory, "calls"));
            Assert.All(calls, call => Assert.Equal("-d monitor:monitor_FL speakers:playback_FL", call));
            Assert.False(mixer.EnsureMonitorRoutes());
            mixer.SetMonitorMuted(true);
            Assert.Equal(calls, File.ReadAllLines(Path.Combine(directory, "calls")));
            if (deletingMonitor)
            {
                Assert.Throws<IOException>(() => mixer.DeleteMix("monitor", _ => "disk full"));
                Assert.True(mixer.HasMix("monitor"));
                Assert.True(mixer.MonitorMuted);
                Assert.Equal((3, 123L), mixRepairs["OpenXLR_mix_monitor"]);
                Assert.Equal((5, 456L), mixRepairs["OpenXLR_mix_monitor2"]);
                mixer.DeleteMix("monitor", _ => { persisted = true; return null; });
                Assert.True(persisted);
                Assert.False(mixer.HasMix("monitor"));
                Assert.Equal(["monitor2"], mixer.ExportSettings().MixMuted);
                Assert.Equal("OpenXLR_mix_monitor2", Assert.Single(mixRepairs).Key);
                Assert.Equal((5, 456L), mixRepairs["OpenXLR_mix_monitor2"]);
            }
            else mixer.SetMonitorMuted(false);
            Assert.False(mixer.MonitorMuted);

            T Field<T>(string name) => (T)typeof(Mixer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mixer)!;
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("selection", "speakers", "same", "blocked")]
    [InlineData("profile", "speakers#hp1", "same", "blocked")]
    [InlineData("settings", "speakers", "same", "blocked")]
    [InlineData("selection", "speakers", "replaced", "blocked")]
    [InlineData("profile", "speakers#hp1", "replaced", "blocked")]
    [InlineData("settings", "speakers", "replaced", "blocked")]
    [InlineData("selection", "speakers", "missing", "blocked")]
    [InlineData("profile", "speakers#hp1", "missing", "blocked")]
    [InlineData("settings", "speakers", "missing", "blocked")]
    [InlineData("selection", "speakers", "same", "neighbor")]
    [InlineData("settings", "speakers", "same", "empty")]
    public void RemovingAPausedOutputKeepsItsNativeMuteUntilItsRetainedRoutesAreGone(string transition, string output, string identity, string routeState)
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-paused-native-release-").FullName;
        string? previousPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            string target = routeState == "neighbor" ? "speakers:other" : "speakers";
            File.WriteAllText(Path.Combine(directory, "graph"), LinkedGraph.Replace("\"speakers\"", "\"" + target + "\"", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(directory, "refuse"), "");
            File.WriteAllText(Path.Combine(directory, "mute"), "false");
            File.WriteAllText(Path.Combine(directory, "serial"), "1");
            File.WriteAllText(Path.Combine(directory, "calls"), "");
            ExecutableScript.Write(Path.Combine(directory, "pw-dump"), """
                dir=${0%/*}
                /bin/cat "$dir/graph"
                """);
            ExecutableScript.Write(Path.Combine(directory, "pw-link"), """
                dir=${0%/*}
                if [ "$1" != -d ]; then exit 0; fi
                if [ -f "$dir/refuse" ]; then exit 1; fi
                printf '[]' > "$dir/graph"
                """);
            ExecutableScript.Write(Path.Combine(directory, "pactl"), """
                dir=${0%/*}
                printf '%s\n' "$*" >> "$dir/calls"
                if [ "$1" = --format=json ]; then
                    if [ -f "$dir/missing" ]; then printf '[]'; exit; fi
                    mute=$(/bin/cat "$dir/mute")
                    serial=$(/bin/cat "$dir/serial")
                    printf '[{"index":1,"name":"speakers","mute":%s,"properties":{"object.serial":"%s"}}]' "$mute" "$serial"
                elif [ "$1" = get-sink-volume ]; then
                    printf 'Volume: front-left: 65536 / 100%% / 0.00 dB\n'
                elif [ "$1" = set-sink-mute ] && [ "$2" = speakers ]; then
                    case "$3" in
                        0) printf false > "$dir/mute" ;;
                        1) printf true > "$dir/mute" ;;
                        toggle)
                            mute=$(/bin/cat "$dir/mute")
                            if [ "$mute" = true ]; then printf false > "$dir/mute"; else printf true > "$dir/mute"; fi ;;
                    esac
                fi
                """);
            Environment.SetEnvironmentVariable("PATH", directory);
            using var mixer = new Mixer(new PipeWireAdapter());
            typeof(Mixer).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mixer,
                new MixerConfig { Channels = [], Mixes = [new("monitor", "Monitor", MixKind.Monitor)] });
            typeof(Mixer).GetField("_built", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mixer, true);
            mixer.SetMonitorOutputs([output]);
            var routes = Field<Dictionary<string, PortLink>>("_monitorRoutes");
            routes[routeState == "neighbor" ? target : Mixer.OutputRouteKey(output)] = new PortLink([("monitor:monitor_FL", target + ":playback_FL")]);

            mixer.SetMonitorMuted(true);
            Assert.Equal("true", File.ReadAllText(Path.Combine(directory, "mute")));
            if (routeState == "empty") routes[Mixer.OutputRouteKey(output)] = new PortLink([]);
            if (transition == "profile") mixer.ApplyScene(new() { MonitorOutputs = [] });
            else if (transition == "settings")
                mixer.ApplySettings(mixer.ExportSettings() with { MonitorOutputs = [], MonitorOutput = null });
            else mixer.SetMonitorOutputs([]);

            Assert.True(mixer.MonitorMuted);
            if (routeState != "blocked")
            {
                mixer.SyncDeviceVolumes();
                Assert.Equal("false", File.ReadAllText(Path.Combine(directory, "mute")));
                Assert.Empty(Field<Dictionary<string, bool>>("_monitorOutputMutes"));
                File.Delete(Path.Combine(directory, "refuse"));
                mixer.EnsureMonitorRoutes();
                return;
            }
            Assert.Single(routes);
            Assert.Equal("true", File.ReadAllText(Path.Combine(directory, "mute")));
            mixer.SyncDeviceVolumes();
            Assert.Equal("true", File.ReadAllText(Path.Combine(directory, "mute")));
            Assert.DoesNotContain("set-sink-mute speakers 0", File.ReadAllLines(Path.Combine(directory, "calls")));

            File.Delete(Path.Combine(directory, "refuse"));
            Assert.True(mixer.EnsureMonitorRoutes());
            Assert.Empty(routes);
            if (identity == "replaced") File.WriteAllText(Path.Combine(directory, "serial"), "2");
            if (identity == "missing") File.WriteAllText(Path.Combine(directory, "missing"), "");
            mixer.SyncDeviceVolumes();
            mixer.SyncDeviceVolumes();
            Assert.Equal(identity == "same" ? "false" : "true", File.ReadAllText(Path.Combine(directory, "mute")));
            Assert.Empty(Field<Dictionary<string, bool>>("_monitorOutputMutes"));
            if (identity != "same")
                Assert.DoesNotContain("set-sink-mute speakers 0", File.ReadAllLines(Path.Combine(directory, "calls")));

            T Field<T>(string name) => (T)typeof(Mixer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mixer)!;
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void AConsumedNativeResumeRetriesUntilThePreviousRoutesCanBeRemoved()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-pause-resume-").FullName;
        string? previousPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            File.WriteAllText(Path.Combine(directory, "graph"), LinkedGraph);
            File.WriteAllText(Path.Combine(directory, "refuse"), "");
            ExecutableScript.Write(Path.Combine(directory, "pw-dump"), """
                dir=${0%/*}
                /bin/cat "$dir/graph"
                """);
            ExecutableScript.Write(Path.Combine(directory, "pw-link"), """
                dir=${0%/*}
                printf '%s\n' "$*" >> "$dir/calls"
                if [ -f "$dir/refuse" ]; then echo 'temporary disconnect refusal' >&2; exit 1; fi
                printf '[]' > "$dir/graph"
                """);
            Environment.SetEnvironmentVariable("PATH", directory);
            using var mixer = new Mixer(new PipeWireAdapter());
            typeof(Mixer).GetField("_built", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mixer, true);
            var routes = Field<Dictionary<string, PortLink>>("_monitorRoutes");
            routes["speakers"] = new PortLink([("monitor:monitor_FL", "speakers:playback_FL")]);
            mixer.SetMonitorMuted(true);
            Assert.Single(routes);

            // Native acknowledgement has completed. Applying its resume
            // intent must remain retryable while the old route refuses it.
            Assert.False((bool)typeof(Mixer).GetMethod("ToggleDesktopMonitorIntentLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(mixer, null)!);
            Assert.True(mixer.MonitorMuted);
            Assert.Equal(false, Field<bool?>("_desktopMonitorIntent"));
            Assert.False(mixer.SyncDeviceVolumes());
            Assert.True(mixer.MonitorMuted);
            Assert.Equal(false, Field<bool?>("_desktopMonitorIntent"));
            Assert.Single(routes);

            File.Delete(Path.Combine(directory, "refuse"));
            Assert.True(mixer.SyncDeviceVolumes());
            Assert.False(mixer.MonitorMuted);
            Assert.Null(Field<bool?>("_desktopMonitorIntent"));
            Assert.Empty(routes);
            string[] calls = File.ReadAllLines(Path.Combine(directory, "calls"));
            Assert.All(calls, call => Assert.Equal("-d monitor:monitor_FL speakers:playback_FL", call));
            Assert.False(mixer.SyncDeviceVolumes());
            Assert.Equal(calls, File.ReadAllLines(Path.Combine(directory, "calls")));

            T Field<T>(string name) => (T)typeof(Mixer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mixer)!;
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Directory.Delete(directory, true);
        }
    }
}
