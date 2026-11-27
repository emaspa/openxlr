using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// The pure parts of rebuilding one chain at a time: what counts as a
/// chain's structure, the control values a recall pushes live, and which
/// chains a recall replaces.
/// </summary>
public sealed class InputChainRebuildTests
{
    private static InsertDefinition Insert(string id, string plugin = "urn:test:eq", string kind = "lv2",
        bool bypass = false, bool native = false, string? label = null, double gain = 0.5)
        => new() { Id = id, Kind = kind, Plugin = plugin, Bypass = bypass, NativeHost = native, Label = label,
            Params = new() { ["gain"] = gain } };

    [Fact]
    public void ControlValuesAndLabelsLeaveTheStructureAsItIs()
    {
        ChainStructure running = ChainStructure.Of([Insert("a"), Insert("b", "urn:test:gate")], 80, true);
        Assert.Equal(running, ChainStructure.Of([Insert("a", gain: 0.9, label: "Voice EQ"), Insert("b", "urn:test:gate", gain: 0)], 80, true));
        // A CLAP or VST3 plugin always runs in the native host, whatever the
        // saved flag says, so the flag alone changes nothing.
        Assert.Equal(ChainStructure.Of([Insert("c", "com.test.eq", "clap")]),
            ChainStructure.Of([Insert("c", "com.test.eq", "clap", native: true)]));
    }

    [Fact]
    public void PluginKindOrderBypassHostAndSoftDspChangeTheStructure()
    {
        ChainStructure running = ChainStructure.Of([Insert("a"), Insert("b", "urn:test:gate")]);
        Assert.NotEqual(running, ChainStructure.Of([Insert("b", "urn:test:gate"), Insert("a")]));
        Assert.NotEqual(running, ChainStructure.Of([Insert("a"), Insert("b", "urn:test:gate", bypass: true)]));
        Assert.NotEqual(running, ChainStructure.Of([Insert("a", native: true), Insert("b", "urn:test:gate")]));
        Assert.NotEqual(running, ChainStructure.Of([Insert("a", "urn:test:other"), Insert("b", "urn:test:gate")]));
        Assert.NotEqual(running, ChainStructure.Of([Insert("a", kind: "clap"), Insert("b", "urn:test:gate")]));
        Assert.NotEqual(running, ChainStructure.Of([Insert("a")]));
        Assert.NotEqual(running, ChainStructure.Of([Insert("a"), Insert("b", "urn:test:gate")], lowCutHz: 80));
        Assert.NotEqual(running, ChainStructure.Of([Insert("a"), Insert("b", "urn:test:gate")], clipGuard: true));
    }

    [Fact]
    public void ParamChangesNameEveryNewChangedAndDroppedValue()
    {
        InsertDefinition before = Insert("a") with { Params = new() { ["gain"] = 0.5, ["freq"] = 100, ["q"] = 1 } };
        InsertDefinition after = Insert("a") with { Params = new() { ["gain"] = 0.5, ["freq"] = 200, ["mix"] = 1 } };
        Assert.Equal(new ParamChange[] { new("a", "freq", 200), new("a", "mix", 1), new("a", "q", null) },
            Mixer.ParamChanges([before, Insert("b")], [after, Insert("b")]).OrderBy(c => c.Symbol));
        Assert.Empty(Mixer.ParamChanges([Insert("a"), Insert("b")], [Insert("a"), Insert("b")]));
    }

    [Fact]
    public void ASceneWithoutAnInputKeyLeavesThatChainAndClearsAMixItDoesNotName()
    {
        List<InsertDefinition> xlr2 = [Insert("x2")];
        var current = new Dictionary<string, List<InsertDefinition>>
        {
            ["xlr1"] = [Insert("x1")],
            ["xlr2"] = xlr2,
            ["mix:stream"] = [Insert("s")],
            ["mix:chat"] = [Insert("c")],
        };
        var profile = new Dictionary<string, List<InsertDefinition>>
        {
            ["xlr1"] = [Insert("x1", gain: 0.25)],
            ["mix:stream"] = [],
        };
        var next = Mixer.RecallChains(current, profile, key => key.StartsWith("mix:", StringComparison.Ordinal), keepWiring: true);
        Assert.Same(xlr2, next["xlr2"]);
        Assert.Equal(0.25, Assert.Single(next["xlr1"]).Params["gain"]);
        Assert.Empty(next["mix:stream"]);
        Assert.False(next.ContainsKey("mix:chat"), "A mix the profile does not name has no inserts, as before.");

        // The recalled lists are copies: a later control move does not
        // write into the profile that was loaded.
        next["xlr1"][0].Params["gain"] = 1;
        Assert.Equal(0.25, profile["xlr1"][0].Params["gain"]);
        Assert.NotSame(profile["mix:stream"], next["mix:stream"]);
    }

    [Fact]
    public void AProfileRecallKeepsTheWiringOfEveryInsertThatStays()
    {
        // Mic, capture input and mix chains are all keys of one table.
        var current = new Dictionary<string, List<InsertDefinition>>
        {
            ["xlr1"] =
            [
                Insert("pinned", "0123", "vst3") with { WindowsHost = "openxlr", OwnBlock = true },
                Insert("cycle", "urn:test:gate"),
                Insert("swapped", "urn:test:old") with { OwnBlock = true, WindowsHost = "openxlr" },
                Insert("current-only", "4567", "vst3") with { WindowsHost = "openxlr", OwnBlock = true },
            ],
            ["capture:game"] = [Insert("c", "com.test.verb", "clap") with { OwnBlock = true }],
            ["mix:stream"] = [Insert("s", "89AB", "vst3") with { WindowsHost = "openxlr" }],
        };
        var profile = new Dictionary<string, List<InsertDefinition>>
        {
            ["xlr1"] =
            [
                // The profile was saved before the pin and with the own block off.
                Insert("pinned", "0123", "vst3", gain: 0.25),
                // Saved on its own block; the chain runs it in the cycle now.
                Insert("cycle", "urn:test:gate") with { OwnBlock = true },
                // The same id with another plugin is another insert.
                Insert("swapped", "urn:test:new"),
                // Not in the current chain: it comes back as the profile has it.
                Insert("profile-only", "CDEF", "vst3") with { WindowsHost = "openxlr", OwnBlock = true },
            ],
            ["capture:game"] = [Insert("c", "com.test.verb", "clap")],
            ["mix:stream"] = [Insert("s", "89AB", "vst3") with { OwnBlock = true }],
            ["mix:chat"] = [Insert("new-chain", "0123", "vst3") with { WindowsHost = "openxlr", OwnBlock = true }],
        };
        var next = Mixer.RecallChains(current, profile, key => key.StartsWith("mix:", StringComparison.Ordinal), keepWiring: true);
        Assert.Equal(
            [("pinned", "openxlr", true), ("cycle", null, false), ("swapped", null, false), ("profile-only", "openxlr", true)],
            next["xlr1"].Select(i => (i.Id, i.WindowsHost, i.OwnBlock)));
        // Everything else is the profile's.
        Assert.Equal(0.25, next["xlr1"][0].Params["gain"]);
        Assert.Equal("urn:test:new", next["xlr1"][2].Plugin);
        Assert.True(Assert.Single(next["capture:game"]).OwnBlock);
        Assert.Equal(("openxlr", false), (Assert.Single(next["mix:stream"]).WindowsHost, next["mix:stream"][0].OwnBlock));
        Assert.Equal(("openxlr", true), (Assert.Single(next["mix:chat"]).WindowsHost, next["mix:chat"][0].OwnBlock));
        // The profile is not written to.
        Assert.Null(profile["xlr1"][0].WindowsHost);
        Assert.False(profile["xlr1"][0].OwnBlock);

        // The saved settings are this machine's own wiring and carry it as they are.
        var restored = Mixer.RecallChains(current, profile, _ => false, keepWiring: false);
        Assert.Equal([(null, false), (null, true), (null, false), ("openxlr", true)],
            restored["xlr1"].Select(i => (i.WindowsHost, i.OwnBlock)));
    }

    [Fact]
    public void SavedSettingsLeaveEveryChainTheyDoNotName()
    {
        List<InsertDefinition> chat = [Insert("c")];
        var current = new Dictionary<string, List<InsertDefinition>> { ["xlr1"] = [Insert("x1")], ["mix:chat"] = chat };
        var next = Mixer.RecallChains(current, new Dictionary<string, List<InsertDefinition>> { ["xlr1"] = [] }, _ => false, keepWiring: false);
        Assert.Empty(next["xlr1"]);
        Assert.Same(chat, next["mix:chat"]);
    }

    [Fact]
    public void ANewProfileNamesEveryInputChainEvenAnEmptyOne()
    {
        using var mixer = new Mixer();
        MixerScene scene = mixer.ExportScene();
        Assert.NotNull(scene.Inserts);
        Assert.Empty(scene.Inserts!["xlr1"]);
        Assert.Empty(scene.Inserts["xlr2"]);
        Assert.False(scene.Inserts.ContainsKey("aux"), "Aux In has no insert chain.");
        mixer.SetInserts("xlr2", [Insert("x2")]);
        Assert.Single(mixer.ExportScene().Inserts!["xlr2"]);
        Assert.Empty(mixer.ExportScene().Inserts!["xlr1"]);
    }
}
