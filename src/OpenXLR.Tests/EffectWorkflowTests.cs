using System.Text.Json.Nodes;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class EffectWorkflowTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("openxlr-chain-presets-").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    public EffectWorkflowTests() => Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _directory);
    private static EffectChainData Chain() => new(1, 2, JsonNode.Parse("""
        [{"id":"one","kind":"lv2","plugin":"urn:test","label":"Speech","bypass":true,"nativeHost":false,"params":{"gain":0.5}}]
        """)!.AsArray());

    [Fact]
    public void CopiesAreIndependentAndPasteGetsFreshIds()
    {
        var original = Chain();
        var copy = original.Copy(true);
        Assert.NotEqual(original.Inserts[0]!["id"]!.GetValue<string>(), copy.Inserts[0]!["id"]!.GetValue<string>());
        copy.Inserts[0]!["params"]!["gain"] = .9;
        Assert.Equal(.5, original.Inserts[0]!["params"]!["gain"]!.GetValue<double>());
        Assert.Equal("one", original.Copy().Inserts[0]!["id"]!.GetValue<string>());
        copy.Validate();
    }

    [Fact]
    public void PresetsRoundTripWithoutAliasingOrOverwritingAnExistingName()
    {
        var chain = Chain();
        EffectChainPresets.Save(" Speech ", chain);
        chain.Inserts[0]!["label"] = "Changed";
        var stored = Assert.Single(EffectChainPresets.Read());
        Assert.Equal("Speech", stored.Name);
        Assert.Equal("Speech", stored.Chain.Inserts[0]!["label"]!.GetValue<string>());
        Assert.Throws<InvalidDataException>(() => EffectChainPresets.Save("speech", chain));
        Assert.Throws<InvalidDataException>(() => EffectChainPresets.Save("\n", chain));
        string path = OpenXLR.Core.OpenXlrPaths.ConfigFile("effect-chain-presets.json");
        if (OperatingSystem.IsLinux()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        EffectChainPresets.Delete("SPEECH");
        Assert.Empty(EffectChainPresets.Read());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("[{\"name\":\"bad\",\"chain\":{\"version\":42,\"channels\":2,\"inserts\":[]}}]")]
    public void CorruptPresetsAreNeverSilentlyReplaced(string text)
    {
        string path = OpenXLR.Core.OpenXlrPaths.ConfigFile("effect-chain-presets.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        Assert.ThrowsAny<Exception>(() => EffectChainPresets.Save("new", Chain()));
        Assert.Equal(text, File.ReadAllText(path));
    }

    [Fact]
    public void StorageAndChainLimitsAreEnforcedBeforeSaving()
    {
        for (int i = 0; i < 64; i++) EffectChainPresets.Save($"Preset {i}", Chain());
        Assert.Throws<InvalidDataException>(() => EffectChainPresets.Save("overflow", Chain()));
        var chain = Chain();
        chain.Inserts.Add(chain.Inserts[0]!.DeepClone());
        Assert.Throws<InvalidDataException>(chain.Validate);
        chain = Chain();
        chain.Inserts[0]!["params"]!["gain"] = double.NaN;
        Assert.Throws<InvalidDataException>(chain.Validate);
        chain = Chain();
        chain.Inserts[0]!["bypass"] = "yes";
        Assert.Throws<InvalidDataException>(chain.Validate);
        var oversized = Chain();
        oversized.Inserts.Clear();
        for (int i = 0; i < 17; i++) oversized.Inserts.Add(Chain().Copy(true).Inserts[0]!.DeepClone());
        Assert.Throws<InvalidDataException>(oversized.Validate);
        string file = OpenXLR.Core.OpenXlrPaths.ConfigFile("effect-chain-presets.json");
        File.WriteAllBytes(file, new byte[8 * 1024 * 1024 + 1]);
        Assert.Throws<InvalidDataException>(() => EffectChainPresets.Delete("Preset 0"));
    }

    [Fact]
    public async Task RenamePreservesIdentityParametersAndExistingViewModels()
    {
        using var mixer = new Mixer();
        mixer.SetInserts("xlr1", [new() { Id = "one", Kind = "lv2", Plugin = "urn:test", Params = new() { ["gain"] = .5 } }]);
        var command = new Command { Cmd = "renameInsert", Channel = "xlr1", InsertId = "one", Name = "Speech" };
        Assert.Null(CommandValidation.Check(command, mixer, _ => null));
        mixer.RenameInsert("xlr1", "one", "Speech");
        Assert.Equal("Speech", mixer.InsertInChain("xlr1", "one")!.Label);
        Assert.Equal(.5, mixer.InsertInChain("xlr1", "one")!.Params["gain"]);
        Assert.NotNull(CommandValidation.Check(command with { Name = "\n" }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(command with { InsertId = "gone" }, mixer, _ => null));
        await using var client = new DaemonClient();
        var view = new InsertsViewModel(client, "xlr1");
        view.Apply(new JsonArray(new JsonObject { ["insert"] = Chain().Inserts[0]!.DeepClone() }));
        var insert = Assert.Single(view.Items);
        var changed = Chain(); changed.Inserts[0]!["label"] = "New name";
        view.Apply(new JsonArray(new JsonObject { ["insert"] = changed.Inserts[0]!.DeepClone() }));
        Assert.Same(insert, Assert.Single(view.Items));
        Assert.Equal("New name", insert.Label);
    }

    [Fact]
    public async Task InvalidPresetActionsBecomeVisibleErrorsWithoutEscapingTheViewModel()
    {
        await using var client = new DaemonClient();
        var view = new InsertsViewModel(client, "mix:monitor", 2);
        view.PresetName = "";
        view.SavePreset();
        Assert.NotNull(view.WorkflowError);
        view.PresetName = "Speech";
        view.SavePreset();
        Assert.Null(view.WorkflowError);
        view.SavePreset();
        Assert.Contains("already exists", view.WorkflowError);
        Assert.False(await view.ApplyEffectsAsync(Chain() with { Version = 42 }));
        Assert.Contains("Unsupported", view.WorkflowError);

        string path = OpenXLR.Core.OpenXlrPaths.ConfigFile("effect-chain-presets.json");
        File.WriteAllText(path, "null");
        view.ReadPresets();
        Assert.NotNull(view.WorkflowError);
        view.SelectedPreset = new("Speech", Chain());
        view.DeletePreset();
        Assert.NotNull(view.WorkflowError);
        Assert.Equal("null", File.ReadAllText(path));
    }

    [Fact]
    public async Task ReplacingAPluginUnderTheSameSlotIdReplacesItsControlsAndFormatMetadata()
    {
        await using var client = new DaemonClient();
        var view = new InsertsViewModel(client, "xlr1");
        view.PluginChoices.Add(new PluginChoice("shared-id", "LV2", "", new JsonArray(), NativeEditorAvailable: true, Kind: "lv2"));
        view.PluginChoices.Add(new PluginChoice("shared-id", "CLAP", "", new JsonArray(), Kind: "clap"));
        view.Apply(JsonNode.Parse("""[{"insert":{"id":"one","kind":"lv2","plugin":"shared-id","label":"Old"}}]"""));
        var old = Assert.Single(view.Items);
        Assert.True(old.NativeHostInstalled);
        view.Apply(JsonNode.Parse("""[{"insert":{"id":"one","kind":"clap","plugin":"shared-id","label":"New"}}]"""));
        var current = Assert.Single(view.Items);
        Assert.NotSame(old, current);
        Assert.Equal("clap", current.Kind);
        Assert.False(current.NativeHostInstalled);
        Assert.False(current.NativeEditorSupported);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComparisonWaitsForAcknowledgementAndDiscardsAnOldConnectionReply(bool reset)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                if (command["cmd"]!.GetValue<string>() == "auth") continue;
                Assert.Equal("setInserts", command["cmd"]!.GetValue<string>());
                Assert.Equal(.5, command["inserts"]![0]!["params"]!["gain"]!.GetValue<double>());
                received.TrySetResult();
                await release.Task.WaitAsync(stop);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>() }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start(); await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var view = new InsertsViewModel(client, "mix:monitor", 2);
        view.Apply(new JsonArray(new JsonObject { ["insert"] = Chain().Inserts[0]!.DeepClone() }));
        view.StoreComparison(false);
        var hearing = view.HearComparisonAsync(false);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(view.CanEditEffects);
        Assert.Equal("Stored A", view.Comparison);
        if (reset) view.ResetForNewConnection();
        release.SetResult(); await hearing;
        Assert.Equal(reset ? "" : "Hearing A", view.Comparison);
        Assert.True(view.CanEditEffects);
        if (reset)
        {
            await view.HearComparisonAsync(false);
            Assert.Equal("Store A first.", view.WorkflowError);
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _previous);
        Directory.Delete(_directory, true);
    }
}
