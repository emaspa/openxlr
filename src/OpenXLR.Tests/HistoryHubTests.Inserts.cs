using System.Reflection;
using System.Text.Json;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

/// <summary>
/// Undo and redo of insert chains through the hub: the chain is one value,
/// a control drag is one step pushed back into the running chain, a bypass
/// and a removed plugin build the chain again, a chain key's burst is one
/// step, and an editor's later change leaves the step alone.
/// </summary>
public sealed partial class HistoryHubTests
{
    private static readonly PluginInfo Comp = new("lv2", "urn:test:comp", "Comp", "", 1, 1, "in", "out",
        [
            new("threshold", "Threshold", -60, 0, -20, false, false, false, false, []),
            new("ratio", "Ratio", 1, 20, 4, false, false, false, false, []),
        ], [], ["in"], ["out"]);

    private static readonly PluginInfo Gate = new("lv2", "urn:test:gate", "Gate", "", 1, 1, "in", "out",
        [new("range", "Range", -90, 0, -40, false, false, false, false, [])], [], ["in"], ["out"]);

    private static readonly PluginInfo Widener = new("lv2", "urn:test:widener", "Widener", "", 2, 2, "in_l", "out_l",
        [new("width", "Width", 0, 2, 1, false, false, false, false, [])], [], ["in_l", "in_r"], ["out_l", "out_r"]);

    /// <summary>The fake catalogue the hub checks commands against; the mixer itself finds none of these installed.</summary>
    private static void UsePlugins(Fixture f)
    {
        var service = (MixerService)typeof(WebSocketHub).GetField("_mixer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Hub)!;
        service.FindPlugin = i => new[] { Comp, Gate, Widener }.FirstOrDefault(p => p.Kind == i.Kind && p.Plugin == i.Plugin);
    }

    private static string Chain(string key, params object[] inserts)
        => JsonSerializer.Serialize(new { cmd = "setInserts", channel = key, inserts });

    private static object Insert(string id, PluginInfo plugin, object? parameters = null, string? label = null)
        => new { id, kind = plugin.Kind, plugin = plugin.Plugin, label, @params = parameters ?? new { } };

    private static IReadOnlyList<InsertDefinition> Inserts(Fixture f, string key)
        => ((InsertChain)f.Value(SceneField.Inserts, key)!).Inserts;

    private static ChainStructure? Built(Fixture f, string key)
        => ((Dictionary<string, ChainStructure>)typeof(Mixer).GetField("_chainStructures", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(f.Mixer)!).GetValueOrDefault(key);

    [Fact]
    public async Task AControlDragIsOneStepPushedBackWithoutBuildingTheChainAgain()
    {
        using var f = new Fixture();
        UsePlugins(f);
        await f.Ok(Chain("xlr1", Insert("comp", Comp, new { threshold = -20 })));
        f.Now += 2000;
        foreach (int threshold in new[] { -24, -30, -36 })
        {
            await f.Ok($$"""{"cmd":"setInsertParam","channel":"xlr1","insertId":"comp","symbol":"threshold","value":{{threshold}}}""");
            f.Now += 80;
        }
        HistoryStepInfo step = f.State().History!.Undo!;
        Assert.Equal(("insertParam", "xlr1", null, "comp", 1, "Comp settings on Mic 1"),
            (step.Kind, step.Channel, step.Mix, step.Insert, step.Count, step.Label));
        Assert.Equal(2, f.State().History!.UndoSteps);

        ChainStructure? running = Built(f, "xlr1");
        Assert.NotNull(running);
        await f.Ok("""{"cmd":"undo"}""");
        Assert.Equal(-20, Assert.Single(Inserts(f, "xlr1")).Params["threshold"]);
        // The same plugins in the same order: the values went into the
        // running chain, which was not built again.
        Assert.Same(running, Built(f, "xlr1"));
        await f.Ok("""{"cmd":"redo"}""");
        Assert.Equal(-36, Assert.Single(Inserts(f, "xlr1")).Params["threshold"]);
        Assert.Same(running, Built(f, "xlr1"));
    }

    [Fact]
    public async Task ABypassIsUndoneByBuildingTheChainAgainAsTheCommandDoes()
    {
        using var f = new Fixture();
        UsePlugins(f);
        await f.Ok(Chain("xlr1", Insert("comp", Comp)));
        f.Now += 2000;
        await f.Ok("""{"cmd":"setInsertBypass","channel":"xlr1","insertId":"comp","value":true}""");
        HistoryStepInfo step = f.State().History!.Undo!;
        Assert.Equal(("insertBypass", "comp", "Comp bypass on Mic 1"), (step.Kind, step.Insert, step.Label));

        ChainStructure? bypassed = Built(f, "xlr1");
        await f.Ok("""{"cmd":"undo"}""");
        Assert.False(Assert.Single(Inserts(f, "xlr1")).Bypass);
        // A bypassed insert is left out of the graph, so the chain is built again.
        Assert.NotSame(bypassed, Built(f, "xlr1"));
        Assert.False(Assert.Single(Built(f, "xlr1")!.Slots).Bypass);
        await f.Ok("""{"cmd":"redo"}""");
        Assert.True(Assert.Single(Inserts(f, "xlr1")).Bypass);
    }

    [Fact]
    public async Task ARemovedPluginComesBackWithItsNameAndSavedValues()
    {
        using var f = new Fixture();
        UsePlugins(f);
        await f.Ok(Chain("xlr1", Insert("comp", Comp, new { threshold = -30, ratio = 8 }, "Voice comp"), Insert("gate", Gate)));
        f.Now += 2000;
        await f.Ok(Chain("xlr1", Insert("gate", Gate)));
        HistoryStepInfo step = f.State().History!.Undo!;
        Assert.Equal(("insertChain", "xlr1", null, "Mic 1 inserts"), (step.Kind, step.Channel, step.Insert, step.Label));
        // Where an insert runs is not part of the step: turning the gate's
        // own block on since leaves the step as it was.
        f.Mixer.SetInsertOwnBlock("xlr1", "gate", true);

        await f.Ok("""{"cmd":"undo"}""");
        IReadOnlyList<InsertDefinition> chain = Inserts(f, "xlr1");
        Assert.Equal(["comp", "gate"], chain.Select(i => i.Id));
        Assert.Equal("Voice comp", chain[0].Label);
        Assert.Equal(new Dictionary<string, double> { ["threshold"] = -30, ["ratio"] = 8 }, chain[0].Params);
        Assert.True(chain[1].OwnBlock);
        Assert.Equal(["comp", "gate"], Built(f, "xlr1")!.Slots.Select(s => s.Id));

        await f.Ok("""{"cmd":"redo"}""");
        Assert.Equal(["gate"], Inserts(f, "xlr1").Select(i => i.Id));
        Assert.True(Assert.Single(Inserts(f, "xlr1")).OwnBlock);
        await f.Ok("""{"cmd":"undo"}""");
        await f.Ok("""{"cmd":"undo"}""");
        Assert.Empty(Inserts(f, "xlr1"));
    }

    [Fact]
    public async Task AChainKeysBurstIsOneStep()
    {
        using var f = new Fixture();
        UsePlugins(f);
        await f.Ok(Chain("xlr1", Insert("comp", Comp), Insert("gate", Gate), Insert("comp2", Comp, label: "Second")));
        f.Now += 2000;
        // A Deck chain key sends one setInsertBypass per insert.
        foreach (string id in new[] { "comp", "gate", "comp2" })
        {
            await f.Ok($$"""{"cmd":"setInsertBypass","channel":"xlr1","insertId":"{{id}}","value":true}""");
            f.Now += 5;
        }
        HistoryStepInfo step = f.State().History!.Undo!;
        Assert.Equal(("insertBypass", 3, "Comp bypass on Mic 1 and 2 more"), (step.Kind, step.Count, step.Label));
        Assert.Equal(2, f.State().History!.UndoSteps);

        await f.Ok("""{"cmd":"undo"}""");
        Assert.All(Inserts(f, "xlr1"), i => Assert.False(i.Bypass));
        await f.Ok("""{"cmd":"redo"}""");
        Assert.All(Inserts(f, "xlr1"), i => Assert.True(i.Bypass));
    }

    [Fact]
    public async Task AControlTheEditorMovedSinceLeavesTheStepAlone()
    {
        using var f = new Fixture();
        UsePlugins(f);
        await f.Ok(Chain("xlr1", Insert("comp", Comp)));
        f.Now += 2000;
        await f.Ok("""{"cmd":"setInsertParam","channel":"xlr1","insertId":"comp","symbol":"threshold","value":-30}""");
        // Moved outside the history, as a plugin's own editor does.
        f.Mixer.SetInsertParam("xlr1", "comp", "ratio", 10);

        Assert.Equal("undo: Comp settings on Mic 1 was skipped: it changed since", await f.Refused("""{"cmd":"undo"}"""));
        Assert.Equal(new Dictionary<string, double> { ["threshold"] = -30, ["ratio"] = 10 }, Assert.Single(Inserts(f, "xlr1")).Params);
    }

    [Fact]
    public async Task ARenameIsUndoneAndTheRunningChainKept()
    {
        using var f = new Fixture();
        UsePlugins(f);
        await f.Ok(Chain("xlr1", Insert("comp", Comp)));
        f.Now += 2000;
        await f.Ok("""{"cmd":"renameInsert","channel":"xlr1","insertId":"comp","name":"Broadcast"}""");
        HistoryStepInfo step = f.State().History!.Undo!;
        Assert.Equal(("insertName", "Broadcast name on Mic 1"), (step.Kind, step.Label));

        ChainStructure? running = Built(f, "xlr1");
        await f.Ok("""{"cmd":"undo"}""");
        Assert.Null(Assert.Single(Inserts(f, "xlr1")).Label);
        Assert.Same(running, Built(f, "xlr1"));
    }

    [Fact]
    public async Task AMixesChainIsItsOwnStep()
    {
        using var f = new Fixture();
        UsePlugins(f);
        await f.Ok(Chain("mix:stream", Insert("wide", Widener, new { width = 1.5 })));
        HistoryStepInfo step = f.State().History!.Undo!;
        Assert.Equal(("insertChain", null, "stream", "Stream inserts"), (step.Kind, step.Channel, step.Mix, step.Label));
        Assert.Empty(Inserts(f, "xlr1"));

        await f.Ok("""{"cmd":"undo"}""");
        Assert.Empty(Inserts(f, "mix:stream"));
        await f.Ok("""{"cmd":"redo"}""");
        Assert.Equal(1.5, Assert.Single(Inserts(f, "mix:stream")).Params["width"]);
    }
}
