using System.Text.Json.Nodes;
using OpenXLR.Core.Mixing;
using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class NativeEditorCompatibilityTests
{
    private const string DeEsser = "ABCDEF019182FAEB4D616E75466C7665";

    [Fact]
    public void BlockingTheEditorKeepsNativeProcessingAndGeneratedControlsAvailable()
    {
        var owner = new InsertsViewModel(new DaemonClient(), "mix:monitor", 2);
        owner.PluginChoices.Add(new(DeEsser, "Elgato De-Esser", "Dynamics", new JsonArray(), true, true, "vst3", true));
        owner.Apply(JsonNode.Parse($$"""
            [{"insert":{"id":"de","kind":"vst3","plugin":"{{DeEsser}}","nativeHost":true,"bypass":false},
              "nativeHostRunning":true,"nativeUiBlocked":true,"nativeUiBlockReason":"Use OpenXLR controls."}]
            """));
        var insert = Assert.Single(owner.Items);
        Assert.True(insert.NativeHost);
        Assert.True(insert.NativeHostRunning);
        Assert.True(insert.NativeHostInstalled);
        Assert.True(insert.NativeEditorSupported);
        Assert.True(insert.IsActive);
        Assert.True(insert.CanTurnNativeHostOn);
        Assert.False(insert.NativeEditorAvailable);
        Assert.True(insert.NativeUiBlocked);
        Assert.Equal("Use OpenXLR controls.", insert.NativeUiBlockReason);
        Assert.Equal("Open this plugin's controls", insert.ControlsHint);

        owner.PluginChoices.Clear();
        owner.PluginChoices.Add(new(DeEsser, "Elgato De-Esser", "Dynamics", new JsonArray(), true, true, "vst3"));
        insert.ApplyFromDaemon(JsonNode.Parse("""{"nativeHost":true,"bypass":false}""")!, null, true);
        Assert.False(insert.NativeUiBlocked);
        Assert.True(insert.NativeEditorAvailable);
        Assert.Equal("Open this plugin's own editor", insert.ControlsHint);
    }

    [Fact]
    public void StatePolicyWorksBeforeTheCatalogueArrives()
    {
        var owner = new InsertsViewModel(new DaemonClient(), "mix:monitor", 2);
        var insert = new InsertViewModel(owner, "de", DeEsser, "De-Esser", "vst3");
        insert.ApplyFromDaemon(JsonNode.Parse("""{"nativeHost":true,"bypass":false}""")!, null, true, true, "Known editor issue.");
        Assert.True(insert.NativeUiBlocked);
        Assert.False(insert.NativeEditorAvailable);
        Assert.True(insert.NativeHostRunning);
        Assert.False(insert.HasError);
    }

    [Fact]
    public void DaemonEditorGateRunsBeforeOpeningAHostAndLeavesTheChainUntouched()
    {
        var mixer = new Mixer();
        mixer.SetInserts("mix:monitor", [new() { Id = "de", Kind = "vst3", Plugin = DeEsser, NativeHost = true }]);
        var error = Assert.Throws<InvalidOperationException>(() => mixer.ShowInsertUi("mix:monitor", "de", _ => "Use OpenXLR controls."));
        Assert.Contains("Native editor disabled", error.Message);
        Assert.Equal(new[] { ("vst3", DeEsser) }, mixer.InsertPlugins());
    }

    [Fact]
    public void EditorPolicyMetadataDoesNotChangeHostFeatureSupport()
    {
        var plugin = new PluginInfo("vst3", DeEsser, "De-Esser", "Dynamics", 2, 2, "in", "out", [], [], [], [])
        { HasNativeUi = true, NativeUiBlocked = true };
        Assert.True(plugin.Supported);
        Assert.True(plugin.NativeEditorSupported);
        Assert.Equal((plugin with { NativeUiBlocked = false }).NativeEditorAvailable, plugin.NativeEditorAvailable);
    }
}
