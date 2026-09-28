using System.Reflection;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class ExclusiveGroupTests
{
    private static ExclusiveGroupDefinition Group(params string[] members) => new("mics", "Microphones", members);

    [Fact]
    public void RestorePrunesDeletedMembersDropsOverlapsAndCopiesMembership()
    {
        string[] members = ["xlr1", "xlr2", "removed"];
        var restored = ExclusiveGroupsModel.Restore([
            Group(members), new("other", "Other", ["xlr2", "music"]), null!,
            new("bad", "Bad", ["gone", "music"])], MixerConfig.Default().Channels);
        Assert.Equal(new[] { "xlr1", "xlr2" }, Assert.Single(restored).Channels);
        members[0] = "music";
        Assert.Equal("xlr1", restored[0].Channels[0]);
        var config = MixerConfig.Default() with { ExclusiveGroups = [Group("music", "system")] };
        Assert.Empty(config.WithoutChannel("music").ExclusiveGroups);
    }

    [Fact]
    public void SavedGroupsAreBoundedAndProfileOverlapsAreRejected()
    {
        var channels = Enumerable.Range(0, 34).Select(i => new ChannelDefinition("ch" + i, "Channel " + i)).ToArray();
        var groups = Enumerable.Range(0, 17).Select(i => new ExclusiveGroupDefinition("g" + i, "Group " + i, ["ch" + (i * 2), "ch" + (i * 2 + 1)])).ToList();
        Assert.Equal(16, ExclusiveGroupsModel.Restore(groups, channels).Count);
        Assert.Throws<System.Text.Json.JsonException>(() => SavedMixerValidation.Validate(new OpenXLR.Core.MixerScene { ExclusiveGroups = groups }));
        Assert.Throws<System.Text.Json.JsonException>(() => SavedMixerValidation.Validate(new OpenXLR.Core.MixerScene
        { ExclusiveGroups = [Group("xlr1", "xlr2"), new("overlap", "Overlap", ["xlr1", "music"])] }));
    }

    [Theory]
    [InlineData(null, "Mic", "xlr1", "xlr2")]
    [InlineData("bad:id", "Mic", "xlr1", "xlr2")]
    [InlineData("mic", " ", "xlr1", "xlr2")]
    [InlineData("mic", "Mic\n", "xlr1", "xlr2")]
    [InlineData("mic", "Mic", "xlr1", "xlr1")]
    [InlineData("mic", "Mic", "xlr1", null)]
    public void MalformedGroupsAreRejected(string? id, string? name, string? a, string? b)
        => Assert.NotNull(ExclusiveGroupsModel.Validate(new(id!, name!, [a!, b!])));

    [Fact]
    public void CommandsValidateMissingAndUnknownFields()
    {
        using var mixer = new Mixer();
        Assert.NotNull(CommandValidation.Check(new() { Cmd = "setExclusiveGroup" }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(new() { Cmd = "setExclusiveGroup", Name = "Mic", Channels = ["xlr1", "missing"] }, mixer, _ => null));
        Assert.Null(CommandValidation.Check(new() { Cmd = "setExclusiveGroup", Name = "Mic", Channels = ["xlr1", "xlr2"] }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(new() { Cmd = "cycleExclusiveGroup", Group = "mics" }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(new() { Cmd = "deleteExclusiveGroup", Group = "../bad" }, mixer, _ => null));
        Assert.NotNull(ExclusiveGroupsModel.Validate(Group(Enumerable.Range(0, 36).Select(i => "ch" + i).ToArray())));
    }

    [Fact]
    public void SelectionIsIndependentPerMixPreservesLevelsAndClosesPeersFirst()
    {
        using var f = new Sends();
        f.Mixer.SetExclusiveGroup(null, "Microphones", ["xlr1", "xlr2"], _ => null);
        f.Mixer.SetLevel("xlr1", "monitor", .42);
        f.Mixer.SetChannelMuted("xlr1", "monitor", false);
        f.Mixer.SetChannelMuted("xlr2", "stream", false);
        f.Clear();
        f.Mixer.SetChannelMuted("xlr2", "monitor", false);
        Assert.True(f.Mixer.IsChannelMutedIn("xlr1", "monitor"));
        Assert.False(f.Mixer.IsChannelMutedIn("xlr2", "monitor"));
        Assert.True(f.Mixer.IsChannelMutedIn("xlr1", "stream"));
        Assert.False(f.Mixer.IsChannelMutedIn("xlr2", "stream"));
        Assert.Equal(.42, f.Mixer.ExportSettings().Levels["xlr1|monitor"]);
        Assert.Equal(new[] { "set-sink-input-mute 10 1", "set-sink-input-mute 20 0" }, f.Mutes);
        f.Mixer.SetChannelMuted("xlr2", "monitor", true);
        Assert.True(f.Mixer.IsChannelMutedIn("xlr1", "monitor"));
        Assert.True(f.Mixer.IsChannelMutedIn("xlr2", "monitor"));
    }

    [Fact]
    public void FailedPeerMuteKeepsNewMemberSilentUntilReconciliation()
    {
        using var f = new Sends();
        f.Mixer.SetExclusiveGroup(null, "Microphones", ["xlr1", "xlr2"], _ => null);
        f.Mixer.SetChannelMuted("xlr1", "monitor", false);
        f.FailMute = true;
        f.Clear();
        f.Mixer.SetChannelMuted("xlr2", "monitor", false);
        Assert.DoesNotContain("set-sink-input-mute 20 0", f.Mutes);
        Assert.Contains("set-sink-input-mute 20 1", f.Mutes);
        Assert.Contains("xlr2|monitor", f.Field<HashSet<string>>("_pendingCells"));
        f.FailMute = false;
        for (int i = 0; i < 5; i++) f.Mixer.EnsureCellLevels();
        Assert.Contains("set-sink-input-mute 20 0", f.Mutes);
        Assert.Empty(f.Field<HashSet<string>>("_pendingCells"));
    }

    [Fact]
    public void ConflictingProfilesCloseTheGroupWhileValidScenesKeepTheirSelection()
    {
        using var f = new Sends();
        f.Mixer.SetExclusiveGroup(null, "Microphones", ["xlr1", "xlr2"], _ => null);
        f.Mixer.SetChannelMuted("xlr1", "monitor", false);
        var scene = f.Mixer.ExportScene();
        f.Mixer.SetChannelMuted("xlr2", "monitor", false);
        f.Mixer.ApplyScene(scene);
        Assert.False(f.Mixer.IsChannelMutedIn("xlr1", "monitor"));
        Assert.True(f.Mixer.IsChannelMutedIn("xlr2", "monitor"));
        f.Mixer.ApplyScene(scene with { ChannelMuted = [] });
        foreach (var ch in new[] { "xlr1", "xlr2" })
            foreach (var mix in new[] { "monitor", "stream" }) Assert.True(f.Mixer.IsChannelMutedIn(ch, mix));
        Assert.Single(f.Mixer.ExportSettings().ExclusiveGroups);
        f.Mixer.ApplyScene(scene with { ExclusiveGroups = null });
        Assert.Single(f.Mixer.ExportSettings().ExclusiveGroups);
        f.Mixer.ApplyScene(scene with { ExclusiveGroups = [] });
        Assert.Empty(f.Mixer.ExportSettings().ExclusiveGroups);
        f.Mixer.ApplyScene(scene);
        Assert.Single(f.Mixer.ExportSettings().ExclusiveGroups);
        var bad = scene with { ExclusiveGroups = [null!], MixVolumes = new() { ["monitor"] = .01 } };
        Assert.Throws<System.Text.Json.JsonException>(() => f.Mixer.ApplyScene(bad));
        Assert.Equal(1, f.Mixer.Snapshot().Mixes[0].Volume);
    }

    [Fact]
    public void GroupEditsAreDurableBoundedAndRollbackWithoutAudioChanges()
    {
        using var f = new Sends();
        f.Mixer.SetChannelMuted("xlr1", "monitor", false);
        f.Mixer.SetChannelMuted("xlr2", "monitor", false);
        f.Clear();
        Assert.Throws<IOException>(() => f.Mixer.SetExclusiveGroup(null, "Microphones", ["xlr1", "xlr2"], _ => "disk full"));
        Assert.Empty(f.Mixer.ExportSettings().ExclusiveGroups);
        Assert.False(f.Mixer.IsChannelMutedIn("xlr1", "monitor"));
        Assert.False(f.Mixer.IsChannelMutedIn("xlr2", "monitor"));
        Assert.Empty(f.Mutes);
        MixerSettings? saved = null;
        f.Mixer.SetExclusiveGroup(null, "Microphones", ["xlr1", "xlr2"], s => { saved = s; return null; });
        Assert.True(f.Mixer.IsChannelMutedIn("xlr1", "monitor"));
        Assert.Contains("xlr1|monitor", saved!.ChannelMuted);
        Assert.Single(MixerConfig.FromSettings(saved).ExclusiveGroups);
        Assert.Throws<InvalidOperationException>(() => f.Mixer.SetExclusiveGroup(null, "Overlap", ["xlr1", "xlr2"], _ => null));
        Assert.Throws<InvalidOperationException>(() => f.Mixer.SetExclusiveGroup("missing", "Unknown", ["xlr1", "xlr2"], _ => null));
        f.Mixer.SetChannelMuted("xlr1", "monitor", false);
        Assert.Throws<IOException>(() => f.Mixer.DeleteExclusiveGroup("microphones", _ => "disk full"));
        Assert.True(f.Mixer.IsChannelGrouped("xlr1"));
        f.Mixer.DeleteExclusiveGroup("microphones", _ => null);
        Assert.False(f.Mixer.IsChannelGrouped("xlr1"));
        Assert.False(f.Mixer.IsChannelMutedIn("xlr1", "monitor"));
        Assert.True(f.Mixer.IsChannelMutedIn("xlr2", "monitor"));
    }

    [Fact]
    public void RapidCyclesResolveFromCurrentStateAndSnapshotsDoNotAliasMembership()
    {
        using var f = new Sends();
        f.Mixer.SetExclusiveGroup(null, "Microphones", ["xlr1", "xlr2"], _ => null);
        for (int i = 0; i < 7; i++) f.Mixer.CycleExclusiveGroup("microphones", "stream");
        Assert.False(f.Mixer.IsChannelMutedIn("xlr1", "stream"));
        Assert.True(f.Mixer.IsChannelMutedIn("xlr2", "stream"));
        Assert.True(f.Mixer.IsChannelMutedIn("xlr1", "monitor"));
        ((string[])f.Mixer.Snapshot().ExclusiveGroups[0].Channels)[0] = "system";
        Assert.True(f.Mixer.IsChannelGrouped("xlr1"));
    }

    private sealed class Sends : IDisposable
    {
        private readonly string _dir = Directory.CreateTempSubdirectory("openxlr-exclusive-").FullName;
        private readonly string? _path = Environment.GetEnvironmentVariable("PATH");
        public Mixer Mixer { get; }
        public string[] Mutes => File.ReadAllLines(Path.Combine(_dir, "writes")).Where(s => s.StartsWith("set-sink-input-mute", StringComparison.Ordinal)).ToArray();
        public bool FailMute { set { if (value) File.WriteAllText(Path.Combine(_dir, "fail"), ""); else File.Delete(Path.Combine(_dir, "fail")); } }
        public void Clear() => File.WriteAllText(Path.Combine(_dir, "writes"), "");
        public T Field<T>(string name) => (T)typeof(Mixer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Mixer)!;
        public Sends()
        {
            ExecutableScript.Write(Path.Combine(_dir, "pactl"), """
                dir=${0%/*}
                echo "$*" >> "$dir/writes"
                case "$*" in
                    'list sinks short') printf '1\tOpenXLR_mix_monitor\n2\tOpenXLR_mix_stream\n' ;;
                    'list sink-inputs') /bin/cat "$dir/legs" ;;
                    'set-sink-input-mute 10 1') [ ! -f "$dir/fail" ] ;;
                esac
                """);
            ExecutableScript.Write(Path.Combine(_dir, "pw-dump"), "echo '[]'");
            ExecutableScript.Write(Path.Combine(_dir, "pw-link"), "exit 0");
            File.WriteAllText(Path.Combine(_dir, "legs"), string.Concat(new[] { 1, 2 }.SelectMany(ch => new[] { 0, 1 }.Select(m =>
                $"Sink Input #{ch * 10 + m}\nOwner Module: {ch}\nSink: {m + 1}\n"))));
            Environment.SetEnvironmentVariable("PATH", _dir);
            Mixer = new(new PipeWireAdapter());
            typeof(Mixer).GetField("_built", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Mixer, true);
            typeof(Mixer).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Mixer, new MixerConfig
            { Channels = [new("xlr1", "Mic 1"), new("xlr2", "Mic 2")], Mixes = [new("monitor", "Monitor", MixKind.Monitor), new("stream", "Stream", MixKind.VirtualMic)] });
            foreach (int ch in new[] { 1, 2 })
            {
                Field<Dictionary<string, uint>>("_combineModules")["xlr" + ch] = (uint)ch;
                foreach (int m in new[] { 0, 1 })
                {
                    string cell = $"xlr{ch}|{(m == 0 ? "monitor" : "stream")}";
                    Field<HashSet<string>>("_cells").Add(cell);
                    Field<HashSet<string>>("_muted").Add(cell);
                    Field<Dictionary<string, double>>("_levels")[cell] = 1;
                    Field<Dictionary<string, int>>("_legIndex")[cell] = ch * 10 + m;
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
