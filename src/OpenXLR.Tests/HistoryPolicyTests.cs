using System.Text.Json;
using System.Text.RegularExpressions;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>The undo policy: every command has a class, and the controls kept out of the history stay out.</summary>
public sealed partial class HistoryPolicyTests
{
    [GeneratedRegex(@"`(\w+)`")]
    private static partial Regex CommandName();

    [GeneratedRegex(@"(?:case|or) ""(\w+)""")]
    private static partial Regex CaseLabel();

    private static HashSet<string> DocumentedCommands()
    {
        string[] lines = File.ReadAllLines(Path.Combine(MicEqTests.RepoRoot, "docs", "api.md"));
        int header = Array.FindIndex(lines, l => l.StartsWith("| Command | Fields | Purpose |", StringComparison.Ordinal));
        Assert.True(header >= 0, "docs/api.md has no command table");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in lines.Skip(header + 2).TakeWhile(l => l.StartsWith('|')))
            foreach (Match m in CommandName().Matches(line.Split('|')[1])) names.Add(m.Groups[1].Value);
        return names;
    }

    private static HashSet<string> DispatchedCommands()
    {
        string hub = File.ReadAllText(Path.Combine(MicEqTests.RepoRoot, "src", "OpenXLR.Daemon", "WebSocketHub.cs"));
        return [.. CaseLabel().Matches(hub).Select(m => m.Groups[1].Value)];
    }

    [Fact]
    public void EveryDocumentedAndDispatchedCommandHasAClassAndNothingElseDoes()
    {
        HashSet<string> documented = DocumentedCommands();
        HashSet<string> dispatched = DispatchedCommands();
        Assert.Contains("undo", documented);
        Assert.Contains("redo", dispatched);
        string[] unclassified = [.. documented.Union(dispatched).Except(HistoryPolicy.Classes.Keys).Order()];
        string[] unknown = [.. HistoryPolicy.Classes.Keys.Except(documented.Union(dispatched)).Order()];
        Assert.Equal([], unclassified);
        Assert.Equal([], unknown);
    }

    [Theory]
    [InlineData("setLevel")]
    [InlineData("setChannelVolume")]
    [InlineData("setMixVolume")]
    [InlineData("setChannelMuted")]
    [InlineData("setMixMuted")]
    [InlineData("cycleExclusiveGroup")]
    public void SendsVolumesAndMutesAreRecorded(string command)
        => Assert.Equal(HistoryClass.Recorded, HistoryPolicy.ClassOf(command));

    [Theory]
    [InlineData("loadProfile")]
    [InlineData("deleteChannel")]
    [InlineData("deleteMix")]
    [InlineData("setMixing")]
    [InlineData("setActiveDevice")]
    [InlineData("resetDevice")]
    public void ProfileLoadsDeletesAndModeSwitchesClearTheHistory(string command)
        => Assert.Equal(HistoryClass.Barrier, HistoryPolicy.ClassOf(command));

    // Phantom power and the hardware mute go through set and setWaveControl,
    // the locks through set and setOutputVolumeLock; the capture mute and the
    // monitoring pause are live switches.
    [Theory]
    [InlineData("set")]
    [InlineData("setWaveControl")]
    [InlineData("setOutputVolumeLock")]
    [InlineData("setCaptureMuted")]
    [InlineData("toggleCaptureMuted")]
    [InlineData("setMonitorMuted")]
    [InlineData("toggleMonitorMuted")]
    [InlineData("setMonitorMuteMicrophones")]
    [InlineData("toggleOutputMute")]
    [InlineData("saveProfile")]
    [InlineData("deleteProfile")]
    public void PhantomLocksAndLiveSwitchesAreNeverRecorded(string command)
    {
        Assert.Equal(HistoryClass.Ignored, HistoryPolicy.ClassOf(command));
        Command cmd = JsonSerializer.Deserialize<Command>(
            $$"""{"cmd":"{{command}}","control":"phantom","channel":"xlr1","mix":"monitor","device":"x","value":true}""")!;
        Assert.Null(HistoryPolicy.TargetOf(cmd, c => [c], _ => []));
    }

    [Fact]
    public void ASendMuteComparesTheWholeExclusiveGroupAndAFaderOnlyItsCell()
    {
        HistoryTarget mute = HistoryPolicy.TargetOf(new Command { Cmd = "setChannelMuted", Channel = "xlr1", Mix = "monitor" },
            _ => ["xlr1", "xlr2"], _ => [])!;
        Assert.True(mute.Keep(new(SceneField.ChannelMuted, "xlr2|stream")));
        Assert.False(mute.Keep(new(SceneField.ChannelMuted, "music|monitor")));

        HistoryTarget fader = HistoryPolicy.TargetOf(new Command { Cmd = "setLevel", Channel = "xlr1", Mix = "monitor" },
            _ => [], _ => [])!;
        Assert.Equal([SceneField.Level], fader.Fields);
        Assert.True(fader.Keep(new(SceneField.Level, "xlr1|monitor")));
        Assert.False(fader.Keep(new(SceneField.Level, "xlr1|stream")));
    }

    [Fact]
    public void TheKindsAreDocumented()
    {
        string api = File.ReadAllText(Path.Combine(MicEqTests.RepoRoot, "docs", "api.md"));
        foreach (string kind in HistoryPolicy.Kinds) Assert.Contains($"`{kind}`", api);
    }

    [Theory]
    [InlineData("""{"cmd":"undo"}""", true)]
    [InlineData("""{"cmd":"undo","step":null}""", true)]
    [InlineData("""{"cmd":"redo","step":7}""", true)]
    [InlineData("""{"cmd":"undo","step":0}""", false)]
    [InlineData("""{"cmd":"undo","step":1.5}""", false)]
    [InlineData("""{"cmd":"undo","step":"7"}""", false)]
    public void TheStepGuardIsAWholeNumberOfAtLeastOne(string json, bool valid)
        => Assert.Equal(valid, CommandValidation.CheckHistory(JsonSerializer.Deserialize<Command>(json)!) is null);
}
