using System.Reflection;
using OpenXLR.Core;
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

        // Deleting a member of a pair dissolves the group.
        var config = MixerConfig.Default() with { ExclusiveGroups = [Group("music", "system")] };
        Assert.Empty(config.WithoutChannel("music").ExclusiveGroups);

        var channels = Enumerable.Range(0, 40).Select(i => new ChannelDefinition("ch" + i, "Channel " + i)).ToArray();
        var many = Enumerable.Range(0, 20).Select(i => new ExclusiveGroupDefinition("g" + i, "Group " + i, ["ch" + (i * 2), "ch" + (i * 2 + 1)]));
        Assert.Equal(ExclusiveGroupsModel.MaxGroups, ExclusiveGroupsModel.Restore(many, channels).Count);
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
    public void SavedGroupsSurviveTheSettingsFileAndMalformedOnesAreDropped()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-groups-").FullName;
        try
        {
            string path = Path.Combine(dir, "mixer.json");
            File.WriteAllText(path, """
                {"exclusiveGroups":[
                  {"id":"mics","name":"Microphones","channels":["xlr1","xlr2"]},
                  {"id":"Bad","name":"Bad","channels":["music","system"]},
                  null]}
                """);
            MixerSettings settings = MixerSettings.Load(path, out string? warning)!;
            Assert.Contains("exclusiveGroups", warning);
            Assert.Equal("mics", Assert.Single(settings.ExclusiveGroups).Id);
            Assert.Equal(new[] { "xlr1", "xlr2" }, Assert.Single(MixerConfig.FromSettings(settings).ExclusiveGroups).Channels);

            File.WriteAllText(path, "{}");
            Assert.Empty(MixerSettings.Load(path, out warning)!.ExclusiveGroups);
            Assert.Null(warning);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CommandsValidateMissingAndUnknownFields()
    {
        using var mixer = new Mixer();
        Assert.NotNull(CommandValidation.Check(new() { Cmd = "setExclusiveGroup" }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(new() { Cmd = "setExclusiveGroup", Name = "Mic", Channels = ["xlr1", "missing"] }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(new() { Cmd = "setExclusiveGroup", Group = "../bad", Name = "Mic", Channels = ["xlr1", "xlr2"] }, mixer, _ => null));
        Assert.Null(CommandValidation.Check(new() { Cmd = "setExclusiveGroup", Name = "Mic", Channels = ["xlr1", "xlr2"] }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(new() { Cmd = "cycleExclusiveGroup" }, mixer, _ => null));
        Assert.Null(CommandValidation.Check(new() { Cmd = "cycleExclusiveGroup", Group = "mics" }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(new() { Cmd = "deleteExclusiveGroup", Group = "../bad" }, mixer, _ => null));
        Assert.NotNull(ExclusiveGroupsModel.Validate(Group([.. Enumerable.Range(0, ExclusiveGroupsModel.MaxMembers + 1).Select(i => "ch" + i)])));
    }

    [Fact]
    public void UnmutingAMemberMutesTheOthersInEveryMixFirstAndKeepsLevels()
    {
        using var f = new Sends();
        f.Mixer.SetExclusiveGroup(null, "Microphones", ["xlr1", "xlr2"], _ => null);
        f.Mixer.SetLevel("xlr1", "monitor", .42);
        f.Mixer.SetChannelMuted("xlr1", "monitor", false);
        f.Mixer.SetChannelMuted("xlr1", "stream", false);
        f.Clear();

        f.Mixer.SetChannelMuted("xlr2", "monitor", false);
        Assert.True(f.Mixer.IsChannelMutedIn("xlr1", "monitor"));
        Assert.True(f.Mixer.IsChannelMutedIn("xlr1", "stream"));
        Assert.False(f.Mixer.IsChannelMutedIn("xlr2", "monitor"));
        Assert.True(f.Mixer.IsChannelMutedIn("xlr2", "stream"));   // only the send that was unmuted opens
        Assert.Equal(.42, f.Mixer.ExportSettings().Levels["xlr1|monitor"]);
        Assert.Equal(new[] { "set-sink-input-mute 10 1", "set-sink-input-mute 11 1", "set-sink-input-mute 20 0" }, f.Mutes);

        // Muting the open member leaves the group silent; nothing reopens.
        f.Mixer.SetChannelMuted("xlr2", "monitor", true);
        foreach (string ch in new[] { "xlr1", "xlr2" })
            foreach (string mix in new[] { "monitor", "stream" }) Assert.True(f.Mixer.IsChannelMutedIn(ch, mix));
        Assert.Equal("microphones", f.Mixer.Snapshot().Channels.Single(c => c.Id == "xlr1").ExclusiveGroup);
    }

    [Fact]
    public void FailedPeerMuteKeepsTheNewMemberSilentUntilReconciliation()
    {
        using var f = new Sends();
        f.Mixer.SetExclusiveGroup(null, "Microphones", ["xlr1", "xlr2"], _ => null);
        f.Mixer.SetChannelMuted("xlr1", "stream", false);
        f.FailMute = true;   // xlr1's stream send cannot be muted
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
    public void AProfileThatOpensTwoMembersClosesTheGroup()
    {
        using var f = new Sends();
        f.Mixer.SetExclusiveGroup(null, "Microphones", ["xlr1", "xlr2"], _ => null);
        f.Mixer.SetChannelMuted("xlr1", "monitor", false);
        MixerScene scene = f.Mixer.ExportScene();
        f.Mixer.SetChannelMuted("xlr2", "stream", false);
        f.Mixer.ApplyScene(scene);
        Assert.False(f.Mixer.IsChannelMutedIn("xlr1", "monitor"));
        Assert.True(f.Mixer.IsChannelMutedIn("xlr2", "stream"));

        // Saved before the group existed: both members open, in different mixes.
        f.Mixer.ApplyScene(scene with { ChannelMuted = ["xlr1|stream", "xlr2|monitor"] });
        foreach (string ch in new[] { "xlr1", "xlr2" })
            foreach (string mix in new[] { "monitor", "stream" }) Assert.True(f.Mixer.IsChannelMutedIn(ch, mix));
        Assert.Single(f.Mixer.ExportSettings().ExclusiveGroups);
    }

    [Fact]
    public void GroupEditsAreSavedFirstAndAFailedSaveChangesNothing()
    {
        using var f = new Sends();
        f.Mixer.SetChannelMuted("xlr1", "monitor", false);
        f.Mixer.SetChannelMuted("xlr2", "stream", false);
        f.Clear();
        Assert.Throws<IOException>(() => f.Mixer.SetExclusiveGroup(null, "Microphones", ["xlr1", "xlr2"], _ => "disk full"));
        Assert.Empty(f.Mixer.ExportSettings().ExclusiveGroups);
        Assert.False(f.Mixer.IsChannelMutedIn("xlr1", "monitor"));
        Assert.False(f.Mixer.IsChannelMutedIn("xlr2", "stream"));
        Assert.Empty(f.Mutes);

        // Two members open when the group is made: every member closes.
        MixerSettings? saved = null;
        f.Mixer.SetExclusiveGroup(null, "Microphones", ["xlr1", "xlr2"], s => { saved = s; return null; });
        Assert.True(f.Mixer.IsChannelMutedIn("xlr1", "monitor"));
        Assert.True(f.Mixer.IsChannelMutedIn("xlr2", "stream"));
        Assert.Contains("xlr1|monitor", saved!.ChannelMuted);
        Assert.Equal("microphones", Assert.Single(MixerConfig.FromSettings(saved).ExclusiveGroups).Id);

        Assert.Throws<InvalidOperationException>(() => f.Mixer.SetExclusiveGroup(null, "Overlap", ["xlr1", "xlr2"], _ => null));
        Assert.Throws<InvalidOperationException>(() => f.Mixer.SetExclusiveGroup("missing", "Unknown", ["xlr1", "xlr2"], _ => null));
        Assert.Throws<InvalidOperationException>(() => f.Mixer.SetExclusiveGroup(null, "Ghost", ["xlr1", "ghost"], _ => null));
        f.Mixer.SetExclusiveGroup("microphones", "Mics", ["xlr2", "xlr1"], _ => null);
        Assert.Equal(new[] { "xlr2", "xlr1" }, Assert.Single(f.Mixer.Snapshot().ExclusiveGroups).Channels);

        f.Mixer.SetChannelMuted("xlr1", "monitor", false);
        Assert.Throws<IOException>(() => f.Mixer.DeleteExclusiveGroup("microphones", _ => "disk full"));
        Assert.True(f.Mixer.IsChannelGrouped("xlr1"));
        f.Mixer.DeleteExclusiveGroup("microphones", _ => null);
        Assert.False(f.Mixer.IsChannelGrouped("xlr1"));
        Assert.False(f.Mixer.IsChannelMutedIn("xlr1", "monitor"));   // deleting keeps the mutes
        f.Mixer.SetChannelMuted("xlr2", "monitor", false);
        Assert.False(f.Mixer.IsChannelMutedIn("xlr1", "monitor"));   // and the rule is gone
    }

    [Fact]
    public void CyclingHandsTheOpenMixesToTheNextMember()
    {
        using var f = new Sends();
        f.Mixer.SetExclusiveGroup(null, "Microphones", ["xlr1", "xlr2"], _ => null);
        Assert.Throws<InvalidOperationException>(() => f.Mixer.CycleExclusiveGroup("microphones"));
        Assert.Throws<InvalidOperationException>(() => f.Mixer.CycleExclusiveGroup("missing"));

        f.Mixer.SetChannelMuted("xlr1", "stream", false);
        f.Mixer.CycleExclusiveGroup("microphones");
        Assert.True(f.Mixer.IsChannelMutedIn("xlr1", "stream"));
        Assert.False(f.Mixer.IsChannelMutedIn("xlr2", "stream"));
        Assert.True(f.Mixer.IsChannelMutedIn("xlr2", "monitor"));
        for (int i = 0; i < 7; i++) f.Mixer.CycleExclusiveGroup("microphones");
        Assert.False(f.Mixer.IsChannelMutedIn("xlr1", "stream"));
        Assert.True(f.Mixer.IsChannelMutedIn("xlr2", "stream"));
        Assert.True(f.Mixer.IsChannelMutedIn("xlr1", "monitor"));

        ((string[])f.Mixer.Snapshot().ExclusiveGroups[0].Channels)[0] = "system";
        Assert.True(f.Mixer.IsChannelGrouped("xlr1"));
    }

    /// <summary>
    /// The Pro's zero-latency path carries XLR 1 to the jacks outside the
    /// mixer, where the group cannot mute it, so a grouped XLR 1 never uses
    /// it: the service asks for the path and the mixer says no, and grouping
    /// XLR 1 while the path is on puts the microphone back on its send.
    /// </summary>
    [Fact]
    public void GroupedXlr1NeverTakesTheProHardwareMonitorPath()
    {
        using var f = new Sends();
        f.Mixer.SetChannelMuted("xlr1", "monitor", false);
        Assert.True(f.Mixer.SetHardwareMicMonitor(true));
        f.Clear();
        f.Mixer.SetLevel("xlr1", "monitor", 1);
        Assert.Contains("set-sink-input-mute 10 1", f.Mutes);   // the hardware path carries it

        f.Clear();
        f.Mixer.SetExclusiveGroup(null, "Microphones", ["xlr1", "xlr2"], _ => null);
        Assert.Contains("set-sink-input-mute 10 0", f.Mutes);   // the software send carries it now
        Assert.False(f.Mixer.SetHardwareMicMonitor(true));

        f.Mixer.DeleteExclusiveGroup("microphones", _ => null);
        Assert.True(f.Mixer.SetHardwareMicMonitor(true));
    }

    private sealed class Sends : IDisposable
    {
        private readonly string _dir = Directory.CreateTempSubdirectory("openxlr-exclusive-").FullName;
        private readonly string? _path = Environment.GetEnvironmentVariable("PATH");
        public Mixer Mixer { get; }
        public string[] Mutes => [.. File.ReadAllLines(Path.Combine(_dir, "writes")).Where(s => s.StartsWith("set-sink-input-mute", StringComparison.Ordinal))];
        public bool FailMute { set { if (value) File.WriteAllText(Path.Combine(_dir, "fail"), ""); else File.Delete(Path.Combine(_dir, "fail")); } }
        public void Clear() => File.WriteAllText(Path.Combine(_dir, "writes"), "");
        public T Field<T>(string name) => (T)typeof(Mixer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Mixer)!;

        // Two channels (xlr1, xlr2) by two mixes (monitor, stream), every send
        // muted at unity. Leg indexes are channel * 10 + mix: 10, 11, 20, 21.
        public Sends()
        {
            ExecutableScript.Write(Path.Combine(_dir, "pactl"), """
                dir=${0%/*}
                echo "$*" >> "$dir/writes"
                case "$*" in
                    'list sinks short') printf '1\tOpenXLR_mix_monitor\n2\tOpenXLR_mix_stream\n' ;;
                    'list sink-inputs') /bin/cat "$dir/legs" ;;
                    'set-sink-input-mute 11 1') [ ! -f "$dir/fail" ] ;;
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
            {
                Channels = [new("xlr1", "Mic 1"), new("xlr2", "Mic 2")],
                Mixes = [new("monitor", "Monitor", MixKind.Monitor), new("stream", "Stream", MixKind.VirtualMic)],
            });
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
