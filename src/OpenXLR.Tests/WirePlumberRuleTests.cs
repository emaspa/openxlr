using System.Text.RegularExpressions;
using OpenXLR.Core;

namespace OpenXLR.Tests;

/// <summary>
/// The WirePlumber rules under packaging/: each capture-hold rule matches its
/// own device's capture node and nothing else attached next to it, and every
/// hardware rule is installed by each package recipe and by the source install.
/// The owned-node state compatibility rule is active only on WirePlumber 0.4.
/// </summary>
public sealed class WirePlumberRuleTests
{
    private const string Dock = "50-xlr-dock-capture-hold.conf";
    private const string Mk1 = "52-openxlr-mk1-capture-hold.conf";
    private const string Wave3 = "53-openxlr-wave3-capture-hold.conf";
    private const string Mk2Playback = "55-openxlr-wave3-mk2-playback.conf";
    private const string OwnedState = "54-openxlr-owned-state.lua";

    // Node names as udev and the ALSA monitor build them: the product string
    // with spaces and colons turned into underscores, the serial, then the
    // interface and the profile's device name.
    public static TheoryData<string, string?> Nodes => new()
    {
        { "alsa_input.usb-Elgato_Systems_Elgato_XLR_Dock_0123456789-00.pro-input-0", Dock },
        { "alsa_input.usb-Elgato_Systems_Elgato_Wave_XLR_0123456789-00.pro-input-0", Mk1 },
        { "alsa_input.usb-Elgato_Systems_Elgato_Wave_XLR_0123456789-00.analog-stereo", Mk1 },
        { "alsa_input.usb-Elgato_Systems_Elgato_Wave_3_A011A53710KH3Y-00.mono-fallback", Wave3 },
        { "alsa_input.usb-Elgato_Systems_Elgato_Wave_3_A011A53710KH3Y-00.pro-input-0", Wave3 },
        { "alsa_output.usb-Elgato_Systems_Elgato_Wave_3_A011A53710KH3Y-00.analog-stereo", null },
        { "alsa_input.usb-Elgato_Elgato_Wave_3_MK.2_AD0JH55014KSN3-00.analog-stereo", Wave3 },
        { "alsa_input.usb-Elgato_Elgato_Wave_3_MK.2_AD0JH55014KSN3-00.pro-input-0", Wave3 },
        { "alsa_output.usb-Elgato_Elgato_Wave_3_MK.2_AD0JH55014KSN3-00.analog-stereo", null },
        // A dot in the model name is literal, not any character.
        { "alsa_input.usb-Elgato_Elgato_Wave_3_MKx2_AD0JH55014KSN3-00.analog-stereo", null },
        { "alsa_input.usb-Elgato_Systems_Elgato_Wave_1_A011A53710KH3Y-00.mono-fallback", null },
        { "alsa_input.usb-Elgato_Elgato_Wave_XLR_Pro_0123456789-00.pro-input-0", null },
        { "alsa_input.usb-Elgato_Elgato_Wave_XLR_Pro_0123456789-00.multichannel-input", null },
    };

    [Theory]
    [MemberData(nameof(Nodes))]
    public void EachCaptureHoldRuleMatchesOnlyItsDevice(string node, string? rule)
    {
        string[] matching = [.. new[] { Dock, Mk1, Wave3 }.Where(file => NodePattern(file).IsMatch(node))];
        Assert.Equal(rule is null ? [] : [rule], matching);
    }

    [Theory]
    [InlineData("alsa_output.usb-Elgato_Elgato_Wave_3_MK.2_AD0JH55014KSN3-00.analog-stereo", true)]
    [InlineData("alsa_output.usb-Elgato_Elgato_Wave_3_MK.2_AD0JH55014KSN3-00.pro-output-0", true)]
    [InlineData("alsa_input.usb-Elgato_Elgato_Wave_3_MK.2_AD0JH55014KSN3-00.analog-stereo", false)]
    [InlineData("alsa_output.usb-Elgato_Systems_Elgato_Wave_3_A011A53710KH3Y-00.analog-stereo", false)]
    [InlineData("alsa_output.usb-Elgato_Elgato_Wave_XLR_MK.2_0123456789-00.analog-stereo", false)]
    [InlineData("alsa_output.usb-Elgato_Elgato_Wave_XLR_Pro_0123456789-00.pro-output-0", false)]
    public void TheMk2PlaybackRuleMatchesOnlyTheWave3Mk2Outputs(string node, bool matches)
        => Assert.Equal(matches, NodePattern(Mk2Playback).IsMatch(node));

    [Fact]
    public void TheMk2PlaybackRuleAsksForTheUnitsFloatFormat()
    {
        string text = File.ReadAllText(Path.Combine(Root(), "packaging", Mk2Playback));
        // FLOAT_LE comes with three channels only on this unit: FL FR FC.
        Assert.Contains("audio.format = \"F32LE\"", text);
        Assert.Contains("audio.channels = 3", text);
        Assert.Contains("audio.position = \"FL,FR,FC\"", text);
    }

    [Theory]
    [InlineData(Dock)]
    [InlineData(Mk1)]
    [InlineData(Wave3)]
    public void CaptureHoldRulesKeepTheSourceRunning(string file)
    {
        string text = File.ReadAllText(Path.Combine(Root(), "packaging", file));
        Assert.Contains("session.suspend-timeout-seconds = 0", text);
        Assert.Contains("node.pause-on-idle = false", text);
        Assert.Contains("node.always-process = true", text);
    }

    // WirePlumber's own priority.driver for a USB capture node off pro-audio
    // is 2100, or 2109 on an analog- profile: what a capture card or a webcam
    // gets too. A pro-audio input, the Wave XLR Pro's among them, gets 2600.
    private const int UsbInputDefaultMax = 2109;
    private const int ProAudioInput = 2600;

    [Theory]
    [InlineData(Dock)]
    [InlineData(Mk1)]
    [InlineData(Wave3)]
    public void CaptureHoldRulesHandTheClockToTheUnitOffProAudio(string file)
    {
        string text = File.ReadAllText(Path.Combine(Root(), "packaging", file));
        MatchCollection priorities = Regex.Matches(text, @"priority\.driver = (\d+)");
        int priority = Assert.Single(priorities.Select(m => int.Parse(m.Groups[1].Value)).Distinct());
        Assert.Equal(2200, priority);
        Assert.InRange(priority, UsbInputDefaultMax + 1, ProAudioInput - 1);
        // Only the capture node is lifted, and only off pro-audio, where the
        // unit has 2600 already and a lower number would hand the clock away.
        string block = PriorityBlock(text);
        MatchCollection matches = Regex.Matches(block, @"\{ node\.name = ""~([^""]+)"", device\.profile\.name = ""!~\^pro-\.\*"" \}");
        Assert.NotEmpty(matches);
        Assert.All(matches, m => Assert.StartsWith(@"alsa_input\\.", m.Groups[1].Value));
        Assert.Equal(matches.Count, Regex.Matches(block, @"node\.name").Count);
        Assert.DoesNotContain("priority.session =", text);
        // The clock rule names the same devices as the hold rule above it.
        Assert.Equal(
            [.. Regex.Matches(text[..text.IndexOf(block, StringComparison.Ordinal)], @"node\.name = ""(~[^""]+)""").Select(m => m.Groups[1].Value)],
            matches.Select(m => "~" + m.Groups[1].Value));
    }

    private static string PriorityBlock(string text)
    {
        int at = Regex.Match(text, @"priority\.driver = \d").Index;
        Assert.True(at > 0);
        int start = text.LastIndexOf("matches = [", at, StringComparison.Ordinal);
        Assert.True(start > 0);
        return text[start..at];
    }

    [Fact]
    public void EveryRuleIsInstalledEverywhere()
    {
        string root = Root();
        string[] rules = [.. Directory.GetFiles(Path.Combine(root, "packaging"), "5*.conf").Select(path => Path.GetFileName(path)).Order()];
        Assert.Contains(Wave3, rules);
        Assert.Contains(Mk2Playback, rules);
        Assert.Equal(rules, OpenXLR.UI.FlatpakSetup.Rules.Order());
        string debian = File.ReadAllText(Path.Combine(root, "debian", "rules"));
        string spec = File.ReadAllText(Path.Combine(root, "packaging", "rpm", "openxlr.spec"));
        string nix = File.ReadAllText(Path.Combine(root, "packaging", "nix", "package.nix"));
        string source = File.ReadAllText(Path.Combine(root, "docs", "install-from-source.md"));
        foreach (string rule in rules)
        {
            Assert.Contains($"install -Dm644 packaging/{rule} \\", debian);
            Assert.Contains($"$(PKG)/usr/share/wireplumber/wireplumber.conf.d/{rule}", debian);
            Assert.Contains($"install -Dm644 packaging/{rule} \\", spec);
            Assert.Contains($"\n%{{_datadir}}/wireplumber/wireplumber.conf.d/{rule}\n", spec);
            Assert.Contains($"packaging/{rule}", nix);
            Assert.Contains($"cp packaging/{rule} ~/.config/wireplumber/wireplumber.conf.d/", source);
            Assert.Contains($"cmp -s packaging/{rule} ~/.config/wireplumber/wireplumber.conf.d/{rule} && rm --", source);
        }
    }

    [Fact]
    public void LegacyStateRuleHonorsOnlyTheOptOutOfOwnedNodes()
    {
        string rule = File.ReadAllText(Path.Combine(Root(), "packaging", OwnedState));
        Assert.Matches(@"table\.insert\(stream_defaults\.rules,", rule);
        Assert.Matches("""\{\s*"node\.name",\s*"matches",\s*"OpenXLR_\*"\s*\}""", rule);
        Assert.Matches("""\{\s*"state\.restore-props",\s*"equals",\s*"false"\s*\}""", rule);
        Assert.Matches("""apply_properties\s*=\s*\{\s*\["state\.restore-props"\]\s*=\s*false\s*,""", rule);
    }

    [Theory]
    [InlineData("wireplumber", "0.4.17", true)]
    [InlineData("wireplumber", "0.5.18", false)]
    [InlineData("wireplumber 0.4.17", "0.5.18", false)]
    [InlineData("wireplumber 0.5.18", "0.4.17", true)]
    public async Task DebianInstallsTheLegacyRuleOnlyForTheLinkedLibrary(string program, string library, bool installed)
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-wireplumber-package-").FullName;
        try
        {
            string[] lines = File.ReadAllLines(Path.Combine(Root(), "debian", "rules"));
            int start = Array.FindIndex(lines, line => line.StartsWith("\twp_version=", StringComparison.Ordinal));
            Assert.True(start >= 0, "The package has no WirePlumber version probe.");
            int end = Array.FindIndex(lines, start, line => line == "\tfi");
            Assert.True(end > start, "The package's WirePlumber install condition is incomplete.");
            string script = string.Join('\n', lines[start..(end + 1)])
                .Replace("$$", "$", StringComparison.Ordinal)
                .Replace("$(PKG)", "\"$1\"", StringComparison.Ordinal)
                .Replace($"packaging/{OwnedState}", "\"$2\"", StringComparison.Ordinal);
            string package = Path.Combine(directory, "package");
            string source = Path.Combine(Root(), "packaging", OwnedState);
            ExecutableScript.Write(Path.Combine(directory, "wireplumber"), """
                [ "$1" = --version ] || exit 3
                printf '%s\n' "$OPENXLR_TEST_WP_VERSION"
                """);
            ProcessResult result = await ProcessRunner.RunAsync("sh", ["-ec", script, "package-rule", package, source],
                environment: new Dictionary<string, string>
                {
                    ["PATH"] = directory + ":" + Environment.GetEnvironmentVariable("PATH"),
                    ["OPENXLR_TEST_WP_VERSION"] = $"{program}\nCompiled with libwireplumber {library}\nLinked with libwireplumber {library}",
                });
            Assert.True(result.Ok, result.Stderr);
            string active = Path.Combine(package, "usr", "share", "wireplumber", "main.lua.d", OwnedState);
            Assert.Equal(installed, File.Exists(active));
            if (installed) Assert.Equal(File.ReadAllText(source), File.ReadAllText(active));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void LegacyStateRuleHasItsOwnPackageDependencyAndSourceInstructions()
    {
        string root = Root();
        string control = File.ReadAllText(Path.Combine(root, "debian", "control"));
        Assert.Matches("(?s)Build-Depends:.*?\\bwireplumber\\b.*?Standards-Version:", control);
        string source = File.ReadAllText(Path.Combine(root, "docs", "install-from-source.md"));
        Assert.Contains($"cp packaging/{OwnedState} ~/.config/wireplumber/main.lua.d/", source);
        Assert.Contains($"cmp -s packaging/{OwnedState} ~/.config/wireplumber/main.lua.d/{OwnedState} && rm --", source);
        Assert.Contains("Linked with libwireplumber 0.4.x", source);
        Assert.Contains("do not install this Lua", source);
        string postinst = File.ReadAllText(Path.Combine(root, "debian", "postinst"));
        Assert.Contains($"[ -f /usr/share/wireplumber/main.lua.d/{OwnedState} ]", postinst);
        Assert.Contains("  systemctl --user restart wireplumber\n", postinst);
        foreach (string recipe in new[] { "packaging/rpm/openxlr.spec", "packaging/nix/package.nix" })
            Assert.DoesNotContain(OwnedState, File.ReadAllText(Path.Combine(root, recipe)));
        Assert.DoesNotContain(OwnedState, OpenXLR.UI.FlatpakSetup.Rules);
    }

    /// <summary>
    /// The rule's node.name regexes, unescaped from their SPA JSON strings
    /// and joined: a node matches the rule when it matches any of them.
    /// </summary>
    private static Regex NodePattern(string file)
    {
        string text = File.ReadAllText(Path.Combine(Root(), "packaging", file));
        MatchCollection matches = Regex.Matches(text, "node\\.name = \"~((?:[^\"\\\\]|\\\\.)*)\"");
        Assert.True(matches.Count > 0, $"{file} has no node.name regex");
        return new Regex(string.Join('|', matches.Select(m => "(?:" + Regex.Replace(m.Groups[1].Value, @"\\(.)", "$1") + ")")));
    }

    private static string Root()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docs", "skins.md")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory.FullName;
    }
}
