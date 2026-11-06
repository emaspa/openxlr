using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// The audio clock choice without an audio server: the WirePlumber rule the
/// daemon writes and reads back, the node names it takes, when the choice
/// waits for a restart, the command checks, and the commands refused where
/// the daemon cannot change the host's WirePlumber.
/// </summary>
public sealed class AudioClockRuleTests : IDisposable
{
    private const string Mic = "alsa_input.usb-Elgato_Systems_Elgato_Wave_XLR_0123456789-00.mono-fallback";
    private const string Card = "alsa_input.usb-Elgato_Elgato_4K_X_ABCDEF-02.analog-stereo";
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-clock-rule-").FullName;
    private string RulePath => Path.Combine(_directory, "wireplumber", "wireplumber.conf.d", AudioClockRule.FileName);

    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public void TheRuleGivesTheExactNodeAPriorityAboveEveryOther()
    {
        string rule = AudioClockRule.Render(Mic);
        Assert.Contains($"{{ node.name = \"{Mic}\" }}", rule);
        Assert.Contains("priority.driver = 3000", rule);
        Assert.Contains("monitor.alsa.rules = [", rule);
        // A match on node.name alone: no regular expression, no other node.
        Assert.DoesNotContain("~", rule.Split('\n').Single(l => l.Contains("node.name", StringComparison.Ordinal)));
        Assert.Equal(1, rule.Split('\n').Count(l => l.Contains("node.name", StringComparison.Ordinal)));
        Assert.True(AudioClockRule.PriorityDriver > 2600, "a pro-audio input has 2600");
        // After the packaged rules, 50 to 55, and the manual's hand-written 56.
        Assert.True(string.CompareOrdinal(AudioClockRule.FileName, "56-openxlr-clock.conf") > 0);
        Assert.DoesNotMatch("[\\u2013\\u2014]", rule);
    }

    [Theory]
    [InlineData(Mic, true)]
    [InlineData("alsa_output.usb-Elgato_Elgato_Wave_XLR_Pro_0123456789-00.pro-output-0", true)]
    [InlineData("alsa_output.pci-0000_00_1f.3.analog-stereo", true)]
    [InlineData("alsa_input.x\" } ] other = { \"", false)]
    [InlineData("alsa_input.with space", false)]
    [InlineData("alsa_input.line\nbreak", false)]
    [InlineData("alsa_input.brace}", false)]
    [InlineData("alsa_input.", false)]
    [InlineData("~alsa_input.usb-.*", false)]
    [InlineData("!alsa_input.usb", false)]
    [InlineData("OpenXLR_mix_monitor", false)]
    [InlineData("bluez_input.00_11_22_33_44_55", false)]
    [InlineData("alsa_input.usb-Café-00.mono-fallback", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyANodeNameTheAlsaMonitorMakesIsTaken(string? node, bool valid)
    {
        Assert.Equal(valid, AudioClockRule.IsValidNode(node));
        if (!valid && node is not null) Assert.Throws<ArgumentException>(() => AudioClockRule.Render(node));
    }

    [Fact]
    public void ANameTooLongForTheRuleIsRefused()
    {
        Assert.True(AudioClockRule.IsValidNode("alsa_input." + new string('a', 244)));
        Assert.False(AudioClockRule.IsValidNode("alsa_input." + new string('a', 245)));
    }

    [Fact]
    public void TheFileIsTheRecordOfTheChoiceAndAutomaticDeletesIt()
    {
        Assert.Equal(new AudioClockRule.RuleFile(null, null), AudioClockRule.Read(RulePath));
        AudioClockRule.Save(Mic, RulePath);
        Assert.Equal(AudioClockRule.Render(Mic), File.ReadAllText(RulePath));
        Assert.Equal(new AudioClockRule.RuleFile(Mic, null), AudioClockRule.Read(RulePath));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                File.GetUnixFileMode(RulePath));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(RulePath)!));

        AudioClockRule.Save(Card, RulePath);
        Assert.Equal(Card, AudioClockRule.Read(RulePath).Node);

        AudioClockRule.Save(null, RulePath);
        Assert.False(File.Exists(RulePath));
        AudioClockRule.Save(null, RulePath);   // nothing to delete is no error
        Assert.Throws<ArgumentException>(() => AudioClockRule.Save("OpenXLR_mix_monitor", RulePath));
    }

    [Fact]
    public void AFileSomeoneElseWroteIsNeitherReplacedNorRemoved()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RulePath)!);
        const string theirs = "monitor.alsa.rules = []\n";
        File.WriteAllText(RulePath, theirs);
        AudioClockRule.RuleFile read = AudioClockRule.Read(RulePath);
        Assert.Null(read.Node);
        Assert.Contains("not written by OpenXLR", read.Problem);
        Assert.Throws<IOException>(() => AudioClockRule.Save(Mic, RulePath));
        Assert.Throws<IOException>(() => AudioClockRule.Save(null, RulePath));
        Assert.Equal(theirs, File.ReadAllText(RulePath));

        // Ours, edited by hand into something that names no single node.
        File.WriteAllText(RulePath, AudioClockRule.Render(Mic).Replace(Mic, "alsa_input.x\" y", StringComparison.Ordinal));
        Assert.Null(AudioClockRule.Read(RulePath).Node);
        Assert.NotNull(AudioClockRule.Read(RulePath).Problem);
        AudioClockRule.Save(Card, RulePath);
        Assert.Equal(Card, AudioClockRule.Read(RulePath).Node);
    }

    [Fact]
    public void ALinkInPlaceOfTheRuleIsRefused()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RulePath)!);
        string target = Path.Combine(_directory, "elsewhere.conf");
        File.WriteAllText(target, AudioClockRule.Render(Mic));
        File.CreateSymbolicLink(RulePath, target);
        Assert.NotNull(AudioClockRule.Read(RulePath).Problem);
        Assert.Throws<IOException>(() => AudioClockRule.Save(Card, RulePath));
        Assert.Throws<IOException>(() => AudioClockRule.Save(null, RulePath));
        Assert.Equal(AudioClockRule.Render(Mic), File.ReadAllText(target));
    }

    private static MixerClock Clock(string driver, int driverPriority, params (string Name, int Priority)[] followers)
        => new(new ClockDriver(driver, "Driver", driverPriority),
            [.. followers.Select(f => new ClockFollower(f.Name, "Follower", "capture", [], false, f.Priority, true))]);

    private static readonly AudioClockOption[] Options = [AudioClockOption.For(Mic, "Wave XLR"), AudioClockOption.For(Card, "Elgato 4K X")];

    [Fact]
    public void AChosenNodeWaitsUntilItCarriesTheRulesPriority()
    {
        // Written, WirePlumber not restarted: the microphone still has its own number.
        AudioClockChoice choice = AudioClockChoice.From(Mic, Clock(Card, 2109, (Mic, 2100)), removed: false, Options);
        Assert.Equal(Mic, choice.Node);
        Assert.True(choice.Pending);
        Assert.False(choice.Active);

        // After the restart it has 3000 and drives.
        choice = AudioClockChoice.From(Mic, Clock(Mic, 3000, (Card, 2109)), removed: false, Options);
        Assert.False(choice.Pending);
        Assert.True(choice.Active);

        // It has the rule but is not in the mixer's graph's driver seat: a
        // restart would not change that, so nothing waits.
        choice = AudioClockChoice.From(Mic, Clock(Card, 4000, (Mic, 3000)), removed: false, Options);
        Assert.False(choice.Pending);
        Assert.False(choice.Active);

        // Unplugged or idle: not in the graph, nothing to wait for.
        choice = AudioClockChoice.From(Mic, Clock(Card, 2109), removed: false, Options);
        Assert.False(choice.Pending);
        Assert.False(AudioClockChoice.From(Mic, null, removed: false, Options).Pending);
    }

    [Fact]
    public void AutomaticWaitsOnlyWhileARuleThisRunDeletedStillHolds()
    {
        Assert.True(AudioClockChoice.From(null, Clock(Mic, 3000, (Card, 2109)), removed: true, Options).Pending);
        Assert.False(AudioClockChoice.From(null, Clock(Mic, 2200, (Card, 2109)), removed: true, Options).Pending);
        // A 3000 from a rule of the user's own, with nothing deleted here.
        Assert.False(AudioClockChoice.From(null, Clock(Mic, 3000, (Card, 2109)), removed: false, Options).Pending);
        Assert.False(AudioClockChoice.From(null, Clock(Mic, 3000), removed: true, Options).Active);
    }

    [Fact]
    public void TheOptionsAreAlsaNodesOnceAndKeepTheChosenOneWhileItIsAway()
    {
        AudioClockOption[] options = [.. Options, AudioClockOption.For(Mic, "Again"), AudioClockOption.For("OpenXLR_mix_monitor", "Mix")];
        AudioClockChoice choice = AudioClockChoice.From(null, null, removed: false, options);
        Assert.Equal([Mic, Card], choice.Options.Select(o => o.Node));
        Assert.Equal("input", choice.Options[0].Kind);
        Assert.Equal("output", AudioClockOption.For("alsa_output.usb-X-00.analog-stereo", "X").Kind);

        const string away = "alsa_input.usb-Away-00.mono-fallback";
        choice = AudioClockChoice.From(away, null, removed: false, Options);
        Assert.Equal(away, choice.Options.Last().Node);
        Assert.Equal(away, choice.Options.Last().Description);
    }

    [Fact]
    public void TheStateCarriesTheChoiceUnderClockChoice()
    {
        var state = new MixerState { Channels = [], Mixes = [], ClockChoice = AudioClockChoice.From(Mic, Clock(Card, 2109, (Mic, 2100)), false, Options) with { Problem = "p" } };
        JsonElement json = JsonSerializer.SerializeToElement(state, new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetProperty("clockChoice");
        Assert.Equal(Mic, json.GetProperty("node").GetString());
        Assert.True(json.GetProperty("pending").GetBoolean());
        Assert.False(json.GetProperty("active").GetBoolean());
        Assert.Equal("p", json.GetProperty("problem").GetString());
        JsonElement option = json.GetProperty("options")[0];
        Assert.Equal(Mic, option.GetProperty("node").GetString());
        Assert.Equal("Wave XLR", option.GetProperty("description").GetString());
        Assert.Equal("input", option.GetProperty("kind").GetString());
    }

    [Theory]
    [InlineData("""{"cmd":"setAudioClock"}""", true)]
    [InlineData("""{"cmd":"setAudioClock","value":null}""", true)]
    [InlineData("""{"cmd":"setAudioClock","value":"alsa_output.usb-X-00.analog-stereo"}""", true)]
    [InlineData("""{"cmd":"setAudioClock","value":"alsa_output.usb-X\"-00"}""", false)]
    [InlineData("""{"cmd":"setAudioClock","value":"OpenXLR_mix_monitor"}""", false)]
    [InlineData("""{"cmd":"setAudioClock","value":3000}""", false)]
    [InlineData("""{"cmd":"setAudioClock","value":true}""", false)]
    [InlineData("""{"cmd":"setAudioClock","value":["alsa_input.usb-X-00.mono"]}""", false)]
    [InlineData("""{"cmd":"restartAudioServices"}""", true)]
    public void TheCommandsAreChecked(string json, bool valid)
    {
        Command cmd = JsonSerializer.Deserialize<Command>(json)!;
        using var mixer = new Mixer();
        Assert.Equal(valid, CommandValidation.Check(cmd, mixer, _ => null) is null);
        if (cmd.Cmd == "setAudioClock") Assert.Equal(valid, CommandValidation.CheckAudioClock(cmd) is null);
    }

    [Fact]
    public async Task WhereTheHostsWirePlumberIsOutOfReachBothCommandsAreRefused()
    {
        string calls = Path.Combine(_directory, "calls");
        string systemctl = ExecutableScript.Write(Path.Combine(_directory, "systemctl"), $"#!/bin/sh\necho \"$@\" >> '{calls}'\n");
        var config = new ConfigurationBuilder().Build();
        using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => []);
        using var service = new MixerService(NullLogger<MixerService>.Instance, config, devices)
        {
            ClockRulePath = RulePath, Systemctl = systemctl, ClockChoiceAvailable = false,
        };
        Assert.Contains("Flatpak", service.SetAudioClock(null));
        Assert.Contains("Flatpak", service.SetAudioClock(Mic));
        Assert.Contains("Flatpak", await service.RestartAudioServicesAsync());
        Assert.False(File.Exists(RulePath));
        Assert.False(File.Exists(calls));
    }

    [Fact]
    public async Task TheRestartRunsSystemctlForWirePlumberOnlyAndReportsAFailure()
    {
        string calls = Path.Combine(_directory, "calls");
        string fail = Path.Combine(_directory, "fail");
        string systemctl = ExecutableScript.Write(Path.Combine(_directory, "systemctl"),
            $"#!/bin/sh\necho \"$@\" >> '{calls}'\nif [ -e '{fail}' ]; then echo 'Unit wireplumber.service not found.' >&2; exit 5; fi\n");
        var config = new ConfigurationBuilder().Build();
        using var devices = new DeviceManager(NullLogger<DeviceManager>.Instance, config, () => []);
        using var service = new MixerService(NullLogger<MixerService>.Instance, config, devices)
        {
            ClockRulePath = RulePath, Systemctl = systemctl, ClockChoiceAvailable = true,
        };
        Assert.Null(await service.RestartAudioServicesAsync());
        Assert.Equal("--user restart wireplumber.service\n", File.ReadAllText(calls));
        File.WriteAllText(fail, "");
        Assert.Contains("Unit wireplumber.service not found.", await service.RestartAudioServicesAsync());

        // Without a mixer only Automatic can be chosen: there is no device to name.
        Assert.Contains("clockChoice.options", service.SetAudioClock(Mic));
        Assert.Null(service.SetAudioClock(null));
        Assert.False(File.Exists(RulePath));
    }
}
