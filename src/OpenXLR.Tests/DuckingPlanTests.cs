using System.Reflection;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class DuckingPlanTests
{
    internal static DuckingPlan Plan(string key = "xlr1", params string[] cells)
        => new(key, cells.ToHashSet(StringComparer.Ordinal), -12, 12, 30, 200, true);

    [Fact]
    public void BusNamesCannotCollideWithLayoutIdsAndTheHelperUsesTheDuckBackend()
    {
        var mix = new MixDefinition("stream", "Stream", MixKind.VirtualMic);
        Assert.Equal("OpenXLR_mix_stream.duck", mix.DuckBusName);
        Assert.Equal("OpenXLR_duck_stream", mix.DuckStageName);
        Assert.Equal(["duck"], NativePluginHost.Arguments(new() { Id = "test", Kind = "duck", Plugin = "duck" }, null));
    }

    [Fact]
    public void APlanCopiesItsCellsAndRejectsTheKeyUnknownCellsAndInvalidControls()
    {
        using var f = new Sends();
        var cells = new HashSet<string> { "music|monitor" };
        DuckingPlan plan = Plan() with { Cells = cells };
        f.Mixer.SetDuckingPlan(plan);
        cells.Add("xlr1|monitor");
        Assert.Single(f.Field<DuckingPlan>("_duckPlan").Cells);
        Assert.Throws<ArgumentException>(() => f.Mixer.SetDuckingPlan(plan));
        Assert.Throws<ArgumentException>(() => f.Mixer.SetDuckingPlan(Plan("gone", "music|monitor")));
        Assert.Throws<ArgumentException>(() => f.Mixer.SetDuckingPlan(Plan("xlr1", "music|missing")));
        Assert.Throws<ArgumentException>(() => f.Mixer.SetDuckingPlan(Plan("xlr1", "bad")));
        foreach (DuckingPlan invalid in new[]
        {
            Plan() with { ThresholdDb = double.NaN }, Plan() with { ThresholdDb = -61 },
            Plan() with { DepthDb = 41 }, Plan() with { DepthDb = double.PositiveInfinity },
            Plan() with { AttackMs = 0 }, Plan() with { AttackMs = 5001 },
            Plan() with { ReleaseMs = 0 }, Plan() with { ReleaseMs = 10001 },
        }) Assert.Throws<ArgumentOutOfRangeException>(() => f.Mixer.SetDuckingPlan(invalid));
        Assert.Equal(["music|monitor"], f.Field<DuckingPlan>("_duckPlan").Cells);
    }

    [Fact]
    public void OnlyTargetCellsUseTheBusAndCloseTheOtherLegBeforeOpening()
    {
        using var f = new Sends();
        f.Mixer.SetDuckingPlan(Plan("xlr1", "music|monitor", "music|stream"));
        f.Clear();
        f.Mixer.SetLevel("music", "monitor", .42);
        Assert.Equal(["set-sink-input-volume 30 0%", "set-sink-input-mute 30 1",
            "set-sink-input-volume 32 42%", "set-sink-input-mute 32 0"], f.CellWrites);
        f.Clear();
        f.Mixer.SetLevel("chat", "monitor", .7);
        Assert.Equal(["set-sink-input-volume 42 0%", "set-sink-input-mute 42 1",
            "set-sink-input-volume 40 70%", "set-sink-input-mute 40 0"], f.CellWrites);
        f.Clear();
        f.Mixer.SetDuckingPlan(Plan("xlr1", "chat|monitor", "music|stream"));
        int unlink = Array.FindIndex(f.Writes, s => s.StartsWith("link -d OpenXLR_mix_monitor.duck:", StringComparison.Ordinal));
        int direct = Array.IndexOf(f.Writes, "set-sink-input-volume 30 42%");
        Assert.True(unlink >= 0 && direct > unlink, string.Join('\n', f.Writes));
        Assert.Equal(((.42, false), (0.0, true)), f.Targets("music", "monitor"));
        Assert.Equal(((0.0, true), (.7, false)), f.Targets("chat", "monitor"));
        Assert.DoesNotContain(f.Writes, s => s.Contains("module-combine-sink", StringComparison.Ordinal));
    }

    [Fact]
    public void BusLegsCarryChannelAndMixVolumesMutesGroupsAndHardwareSilencing()
    {
        using var f = new Sends();
        f.Mixer.SetDuckingPlan(Plan("xlr1", "music|monitor", "music|stream"));
        f.Mixer.SetChannelVolume("music", .5);
        f.Mixer.SetLevel("music", "stream", .8);
        f.Mixer.SetMixVolume("stream", .5);
        Assert.Equal(((0.0, true), (.2, false)), f.Targets("music", "stream"));
        f.Mixer.SetMixVolume("monitor", .4);
        Assert.Equal(((0.0, true), (.5, false)), f.Targets("music", "monitor"));
        f.Mixer.SetMixMuted("stream", true);
        Assert.Equal(((0.0, true), (.2, true)), f.Targets("music", "stream"));
        f.Mixer.SetChannelMuted("music", "monitor", true);
        Assert.True(f.Targets("music", "monitor").Bus.Muted);
        f.Mixer.SetExclusiveGroup(null, "Players", ["music", "chat"], _ => null);
        f.Mixer.SetChannelMuted("music", "monitor", false);
        f.Mixer.SetChannelMuted("chat", "stream", false);
        Assert.True(f.Targets("music", "monitor").Bus.Muted);
        f.Mixer.DeleteExclusiveGroup("players", _ => null);
        f.Mixer.SetChannelMuted("music", "monitor", false);
        f.Field<string?[]>("_hardwareMicChannels")[0] = "music";
        Assert.True(f.Targets("music", "monitor").Bus.Muted);
        Assert.False(f.Mixer.IsChannelMutedIn("music", "monitor"));
    }

    [Fact]
    public void KeyOpenRequiresAnAudibleSendAfterMixAndGroupMutes()
    {
        using var f = new Sends();
        f.Mixer.SetDuckingPlan(Plan("xlr1", "music|monitor"));
        f.Mixer.SetChannelMuted("xlr1", "monitor", true);
        Assert.True(f.KeyOpen);
        f.Mixer.SetMixMuted("stream", true);
        Assert.False(f.KeyOpen);
        f.Mixer.SetMixMuted("stream", false);
        f.Mixer.SetLevel("xlr1", "stream", 0);
        Assert.False(f.KeyOpen);
        f.Mixer.SetChannelMuted("xlr1", "monitor", false);
        Assert.True(f.KeyOpen);
        f.Mixer.SetMixMuted("monitor", true);
        Assert.False(f.KeyOpen);
        f.Mixer.SetMixMuted("monitor", false);
        f.Mixer.SetMixVolume("monitor", 0);
        Assert.False(f.KeyOpen);
        f.Mixer.SetMixVolume("monitor", 1);
        f.Mixer.SetChannelVolume("xlr1", 0);
        Assert.False(f.KeyOpen);
        f.Mixer.SetChannelVolume("xlr1", 1);
        Assert.True(f.KeyOpen);
        f.Mixer.SetExclusiveGroup(null, "Mics", ["xlr1", "xlr2"], _ => null);
        Assert.False(f.KeyOpen);
        f.Mixer.SetChannelMuted("xlr1", "monitor", false);
        Assert.True(f.KeyOpen);
        f.Field<HashSet<string>>("_pendingCells").Add("xlr2|stream");
        Assert.False(f.KeyOpen);
        f.Field<HashSet<string>>("_pendingCells").Clear();
        f.Mixer.SetChannelMuted("xlr2", "stream", false);
        Assert.False(f.KeyOpen);
    }

    [Fact]
    public void AMicrophoneKeyClosesOnlyWhileMicrophonesArePausedWithMonitoring()
    {
        // A paused microphone's sink is muted, but its monitor still feeds the key.
        using var f = new Sends();
        f.Mixer.SetDuckingPlan(Plan("xlr1", "music|monitor"));
        f.Mixer.SetMonitorMuted(true);
        Assert.True(f.KeyOpen);
        f.Mixer.SetMonitorMuteMicrophones(true);
        Assert.False(f.KeyOpen);
        f.Mixer.SetDuckingPlan(Plan("xlr2", "music|monitor"));
        Assert.False(f.KeyOpen);
        f.Mixer.SetDuckingPlan(Plan("chat", "music|monitor"));
        Assert.True(f.KeyOpen);
        f.Mixer.SetDuckingPlan(Plan("xlr1", "music|monitor"));
        f.Mixer.SetMonitorMuted(false);
        Assert.True(f.KeyOpen);
        f.Mixer.SetMonitorMuted(true);
        Assert.False(f.KeyOpen);
        f.Mixer.SetMonitorMuteMicrophones(false);
        Assert.True(f.KeyOpen);
    }

    [Fact]
    public void AFailedInactiveLegWriteNeverOpensTheOtherPath()
    {
        using var f = new Sends();
        f.Mixer.SetDuckingPlan(Plan("xlr1", "music|monitor"));
        f.FailDirectMute = true;
        f.Clear();
        f.Mixer.SetLevel("music", "monitor", .4);
        Assert.DoesNotContain(f.CellWrites, s => s.StartsWith("set-sink-input-volume 32", StringComparison.Ordinal));
        Assert.Contains("music|monitor", f.Field<HashSet<string>>("_pendingCells"));
        f.FailDirectMute = false;
        f.Mixer.EnsureCellLevels();
        Assert.Contains("set-sink-input-volume 32 40%", f.CellWrites);
    }

    [Fact]
    public void AFailedBusPreparationRestoresDirectSendsBeforeReportingTheError()
    {
        using var f = new Sends();
        f.FailDirectMute = true;
        f.Mixer.SetDuckingPlan(Plan("xlr1", "music|monitor"));
        Assert.NotNull(f.Mixer.DuckingStatus.Error);
        Assert.Equal(((1.0, false), (0.0, true)), f.Targets("music", "monitor"));
        Assert.Contains("set-sink-input-volume 30 100%", f.CellWrites);
        Assert.DoesNotContain(f.Writes, s => s.StartsWith("link OpenXLR_mix_monitor.duck:", StringComparison.Ordinal));
        Assert.Empty(f.Field<Dictionary<string, int>>("_duckLegIndex"));
    }

    [Fact]
    public void MissingHelperUsesTheBypassAndRemovingThePlanRestoresDirectSends()
    {
        using var f = new Sends();
        f.Mixer.SetDuckingPlan(Plan("xlr1", "music|monitor", "music|stream"));
        Assert.False(f.Mixer.DuckingStatus.Available);
        Assert.Equal(Mixer.DuckingNeedsHost, f.Mixer.DuckingStatus.Error);
        Assert.Empty(f.Mixer.DuckingStatus.RunningMixes);
        Assert.Contains("link OpenXLR_mix_monitor.duck:monitor_FL OpenXLR_mix_monitor:playback_FL", f.Writes);
        Assert.Contains("link OpenXLR_mix_stream.duck:monitor_FR OpenXLR_mix_stream:playback_FR", f.Writes);
        f.Clear();
        f.Mixer.SetDuckingPlan(null);
        Assert.Equal(((1.0, false), (0.0, true)), f.Targets("music", "monitor"));
        Assert.Equal(2, f.Writes.Count(s => s.StartsWith("unload-module", StringComparison.Ordinal)));
        Assert.Empty(f.Field<Dictionary<string, int>>("_duckLegIndex"));
    }

    [Fact]
    public void AnOffPlanStartsNothingAndForgettingTheGraphUnloadsNoReusedModuleIds()
    {
        using var f = new Sends();
        DuckingPlan plan = Plan("xlr1", "music|monitor");
        f.Mixer.SetDuckingPlan(plan with { Enabled = false });
        Assert.Empty(f.Writes);
        f.Mixer.SetDuckingPlan(plan);
        Assert.NotEmpty(f.Field<Dictionary<string, int>>("_duckLegIndex"));
        f.Clear();
        f.Mixer.ForgetGraph();
        Assert.DoesNotContain(f.Writes, s => s.StartsWith("unload-module", StringComparison.Ordinal));
        Assert.Empty(f.Field<Dictionary<string, int>>("_duckLegIndex"));
        Assert.Equal(plan.Cells, f.Field<DuckingPlan>("_duckPlan").Cells);
    }

    [Fact]
    public async Task ReadingMetersDoesNotWaitForGraphEdits()
    {
        using var f = new Sends();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task edit = Task.Run(() =>
        {
            lock (f.Field<object>("_gate"))
            {
                entered.SetResult();
                release.Wait(TimeSpan.FromSeconds(5));
            }
        });
        await entered.Task;
        try { Assert.Empty(await Task.Run(f.Mixer.ReadMeters).WaitAsync(TimeSpan.FromSeconds(1))); }
        finally
        {
            release.Set();
            await edit;
        }
    }

    [Fact]
    public void LayoutPruningDropsDeletedCellsKeepsRenamesAndDropsADeletedKey()
    {
        MixerConfig config = MixerConfig.Default();
        DuckingPlan plan = Plan("xlr1", "music|monitor", "music|stream", "game|stream");
        Assert.Equal(plan.Cells.Order(), plan.Prune(config.WithChannelName("music", "Songs").WithMixName("stream", "Live"))!.Cells.Order());
        Assert.Equal(["game|stream"], plan.Prune(config.WithoutChannel("music"))!.Cells);
        Assert.Equal(["music|monitor"], plan.Prune(config.WithoutMix("stream"))!.Cells);
        Assert.Null(plan.Prune(config with { Channels = [.. config.Channels.Where(c => c.Id != "xlr1")] }));
    }

    private sealed class Sends : IDisposable
    {
        private readonly string _dir = Directory.CreateTempSubdirectory("openxlr-duck-plan-").FullName;
        private readonly string? _path = Environment.GetEnvironmentVariable("PATH");
        public Mixer Mixer { get; }
        public string[] Writes => File.ReadAllLines(Path.Combine(_dir, "writes"));
        public string[] CellWrites => [.. Writes.Where(s => s.StartsWith("set-sink-input-", StringComparison.Ordinal))];
        public void Clear() => File.WriteAllText(Path.Combine(_dir, "writes"), "");
        public bool FailDirectMute { set { if (value) File.WriteAllText(Path.Combine(_dir, "fail"), ""); else File.Delete(Path.Combine(_dir, "fail")); } }
        public T Field<T>(string name) => (T)typeof(Mixer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Mixer)!;
        public void Set(string name, object value) => typeof(Mixer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Mixer, value);
        public bool KeyOpen => (bool)Call("DuckKeyOpenLocked")!;
        public ((double Level, bool Muted) Direct, (double Level, bool Muted) Bus) Targets(string channel, string mix)
            => (((double, bool), (double, bool)))Call("CellLegTargetsLocked", channel, mix, false)!;
        private object? Call(string name, params object[] args)
            => typeof(Mixer).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Mixer, args);

        public Sends()
        {
            ExecutableScript.Write(Path.Combine(_dir, "pactl"), """
                dir=${0%/*}
                echo "$*" >> "$dir/writes"
                case "$*" in
                    'list sinks short') printf '1\tOpenXLR_mix_monitor\n2\tOpenXLR_mix_stream\n3\tOpenXLR_mix_monitor.duck\n4\tOpenXLR_mix_stream.duck\n' ;;
                    'list sink-inputs') /bin/cat "$dir/legs" ;;
                    'load-module module-null-sink sink_name=OpenXLR_mix_monitor.duck '*) echo 101 ;;
                    'load-module module-null-sink sink_name=OpenXLR_mix_stream.duck '*) echo 102 ;;
                    'set-sink-input-mute 30 1') [ ! -f "$dir/fail" ] ;;
                esac
                """);
            ExecutableScript.Write(Path.Combine(_dir, "pw-dump"), "echo '[]'");
            ExecutableScript.Write(Path.Combine(_dir, "pw-link"), """
                dir=${0%/*}
                echo "link $*" >> "$dir/writes"
                case "$*" in
                    '-o') /bin/cat "$dir/outputs" ;;
                    '-i') /bin/cat "$dir/inputs" ;;
                esac
                """);
            File.WriteAllText(Path.Combine(_dir, "legs"), string.Concat(Enumerable.Range(1, 4).SelectMany(ch => Enumerable.Range(0, 4).Select(m =>
                $"Sink Input #{ch * 10 + m}\nOwner Module: {ch}\nSink: {m + 1}\n"))));
            File.WriteAllLines(Path.Combine(_dir, "outputs"), new[] { "monitor", "stream" }.SelectMany(m =>
                new[] { $"OpenXLR_mix_{m}.duck:monitor_FL", $"OpenXLR_mix_{m}.duck:monitor_FR" }));
            File.WriteAllLines(Path.Combine(_dir, "inputs"), new[] { "monitor", "stream" }.SelectMany(m =>
                new[] { $"OpenXLR_mix_{m}:playback_FL", $"OpenXLR_mix_{m}:playback_FR" }));
            Environment.SetEnvironmentVariable("PATH", _dir);
            Mixer = new(new PipeWireAdapter { DuckHost = Path.Combine(_dir, "missing-host") });
            Set("_built", true);
            Set("_config", new MixerConfig
            {
                Channels = [new("xlr1", "Mic 1") { InputPair = 0 }, new("xlr2", "Mic 2") { InputPair = 1 },
                    new("music", "Music"), new("chat", "Chat")],
                Mixes = [new("monitor", "Monitor", MixKind.Monitor), new("stream", "Stream", MixKind.VirtualMic)],
            });
            string[] ids = ["xlr1", "xlr2", "music", "chat"];
            for (int ch = 0; ch < ids.Length; ch++)
            {
                Field<Dictionary<string, uint>>("_combineModules")[ids[ch]] = (uint)ch + 1;
                foreach (string mix in new[] { "monitor", "stream" })
                {
                    string cell = $"{ids[ch]}|{mix}";
                    Field<HashSet<string>>("_cells").Add(cell);
                    Field<Dictionary<string, double>>("_levels")[cell] = 1;
                    Field<Dictionary<string, int>>("_legIndex")[cell] = (ch + 1) * 10 + (mix == "stream" ? 1 : 0);
                }
            }
            Clear();
        }

        public void Dispose()
        {
            try { Mixer.Dispose(); }
            finally { Environment.SetEnvironmentVariable("PATH", _path); Directory.Delete(_dir, true); }
        }
    }
}
