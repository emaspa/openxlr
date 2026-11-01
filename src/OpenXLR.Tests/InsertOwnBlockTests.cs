using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using OpenXLR.UI;

namespace OpenXLR.Tests;

/// <summary>
/// An insert run on its own block: the switch is saved with the insert,
/// carried in the state, set by one command, part of the chain's structure,
/// and handed to the native host as <c>--own-block</c>.
/// </summary>
[Collection("xdg-config")]
public sealed class InsertOwnBlockTests
{
    private static readonly InsertDefinition Insert = new() { Id = "voice", Kind = "vst3", Plugin = "0123456789ABCDEF0123456789ABCDEF" };
    private static readonly PluginInfo Plugin = new("vst3", Insert.Plugin, "Voice", "", 1, 1, "in", "out", [], [], ["in"], ["out"])
    {
        Path = "/plugins/Voice.vst3",
    };

    private static JsonElement Value(object value) => JsonSerializer.SerializeToElement(value);

    private static string? Check(Mixer mixer, object value, string channel = "xlr1", string insertId = "voice", bool hostInstalled = true)
        => CommandValidation.Check(new() { Cmd = "setInsertOwnBlock", Channel = channel, InsertId = insertId, Value = Value(value) },
            mixer, _ => Plugin, nativeHostInstalled: hostInstalled);

    [Fact]
    public void TheCommandNeedsAnInsertABooleanAndTheHostToTurnItOn()
    {
        using var mixer = new Mixer();
        mixer.SetInserts("xlr1", [Insert]);
        Assert.Null(Check(mixer, true));
        Assert.Null(Check(mixer, false));
        Assert.Equal("setInsertOwnBlock: select an existing insert", Check(mixer, true, insertId: "missing"));
        Assert.Equal("setInsertOwnBlock: select an existing insert", Check(mixer, true, channel: "nowhere"));
        Assert.Equal("setInsertOwnBlock: value must be a boolean", Check(mixer, 1));
        Assert.Equal("setInsertOwnBlock: the native plugin host is not installed on this machine",
            Check(mixer, true, hostInstalled: false));
        // Turning it off never needs the host, so a chain can always go back.
        Assert.Null(Check(mixer, false, hostInstalled: false));
        Assert.Null(CommandValidation.Check(new() { Cmd = "setInsertOwnBlock", Channel = "xlr1", InsertId = "voice", Value = Value(true) },
            mixer, _ => Plugin, nativeHostInstalled: true));
    }

    [Fact]
    public void AnLv2InsertOnItsOwnBlockNeedsTheHostWhateverItsSavedHost()
    {
        using var mixer = new Mixer();
        var lv2 = new InsertDefinition { Id = "gate", Kind = "lv2", Plugin = "urn:test:gate", OwnBlock = true };
        var gate = new PluginInfo("lv2", lv2.Plugin, "Gate", "", 1, 1, "in", "out", [], [], ["in"], ["out"]);
        Assert.True(lv2.NeedsNativeHost);
        Assert.False(lv2.RunsNatively);
        Assert.False((lv2 with { OwnBlock = false }).NeedsNativeHost);
        Assert.Equal("setInserts: the native plugin host is not installed on this machine",
            CommandValidation.Check(new() { Cmd = "setInserts", Channel = "xlr1", Inserts = [lv2] }, mixer, _ => gate, nativeHostInstalled: false));
        Assert.Null(CommandValidation.Check(new() { Cmd = "setInserts", Channel = "xlr1", Inserts = [lv2] }, mixer, _ => gate, nativeHostInstalled: true));
    }

    [Fact]
    public void TheSwitchIsSavedWithTheInsertAndCarriedInTheState()
    {
        using var mixer = new Mixer();
        mixer.SetInserts("xlr1", [Insert]);
        mixer.SetInsertOwnBlock("xlr1", "voice", true);
        mixer.SetInsertOwnBlock("xlr1", "missing", false);   // an unknown insert changes nothing
        Assert.True(Assert.Single(mixer.Snapshot().Inserts["xlr1"]).Insert.OwnBlock);

        string folder = Path.Combine(Path.GetTempPath(), $"openxlr-own-block-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "mixer.json");
        try
        {
            Assert.Null(mixer.ExportSettings().Save(path));
            JsonNode saved = JsonNode.Parse(File.ReadAllText(path))!;
            Assert.True(saved["inserts"]!["xlr1"]![0]!["ownBlock"]!.GetValue<bool>());
            MixerSettings loaded = Assert.IsType<MixerSettings>(MixerSettings.Load(path, out string? warning));
            // An unbuilt mixer's own layout may lose entries on the way back;
            // the insert keeps its switch either way.
            Assert.DoesNotContain("inserts", warning ?? "");
            Assert.True(Assert.Single(loaded.Inserts!["xlr1"]).OwnBlock);
        }
        finally { Directory.Delete(folder, true); }

        using JsonDocument state = JsonSerializer.SerializeToDocument(mixer.Snapshot(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(state.RootElement.GetProperty("inserts").GetProperty("xlr1")[0].GetProperty("insert").GetProperty("ownBlock").GetBoolean());

        mixer.SetInsertOwnBlock("xlr1", "voice", false);
        Assert.False(Assert.Single(mixer.Snapshot().Inserts["xlr1"]).Insert.OwnBlock);
    }

    [Fact]
    public void AnOlderSaveWithoutTheFieldRunsInTheCycle()
    {
        var insert = JsonSerializer.Deserialize<InsertDefinition>("""{"id":"voice","kind":"vst3","plugin":"x"}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.False(insert.OwnBlock);
        Assert.DoesNotContain("--own-block", NativePluginHost.Arguments(insert, "/plugins/Voice.vst3"));
    }

    [Fact]
    public void TheSwitchIsPartOfTheChainStructureSoTheHostStartsAgain()
    {
        ChainStructure running = ChainStructure.Of([Insert]);
        Assert.NotEqual(running, ChainStructure.Of([Insert with { OwnBlock = true }]));
        Assert.Equal(running, ChainStructure.Of([Insert with { Label = "Renamed" }]));
    }

    [Fact]
    public void TheHostIsToldToRunThePluginOnItsOwnBlock()
    {
        var on = Insert with { OwnBlock = true };
        Assert.Equal(["--own-block", "vst3", "/plugins/Voice.vst3", Insert.Plugin], NativePluginHost.Arguments(on, "/plugins/Voice.vst3"));
        Assert.Equal(["vst3", "/plugins/Voice.vst3", Insert.Plugin], NativePluginHost.Arguments(Insert, "/plugins/Voice.vst3"));
        Assert.Equal(["--own-block", "clap", "/plugins/a.clap", "com.test.a"],
            NativePluginHost.Arguments(new() { Id = "a", Kind = "clap", Plugin = "com.test.a", OwnBlock = true }, "/plugins/a.clap"));
        Assert.Equal(["--own-block", "lv2", "urn:test:gate"],
            NativePluginHost.Arguments(new() { Id = "g", Kind = "lv2", Plugin = "urn:test:gate", OwnBlock = true }, null));
        // The built-in stages keep to PipeWire's cycle whatever a record says.
        Assert.Equal(["eq"], NativePluginHost.Arguments(new() { Id = "x", Kind = "eq", Plugin = "eq", OwnBlock = true }, null));
    }

    [Fact]
    public async Task TheUiShowsTheSwitchAndSendsOneCommand()
    {
        var received = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                JsonNode command = await SocketTestServer.Receive(socket, stop);
                if (command["cmd"]?.GetValue<string>() == "setInsertOwnBlock") received.TrySetResult(command);
                if (command["requestId"] is { } requestId)
                    await SocketTestServer.Send(socket, new { type = "commandResult", requestId = requestId.GetValue<string>() }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var owner = new InsertsViewModel(client, "xlr1");
        owner.Apply(Chain(ownBlock: true));
        InsertViewModel insert = Assert.Single(owner.Items);
        Assert.True(insert.OwnBlock);
        Assert.True(insert.CanChooseOwnBlock, "A switch that is on stays offered so it can be turned off.");
        Assert.True(JsonSerializer.SerializeToNode(insert.ToPayload())!["ownBlock"]!.GetValue<bool>());

        owner.Apply(Chain(ownBlock: false));
        Assert.False(insert.OwnBlock);
        Assert.Null(JsonSerializer.SerializeToNode(insert.ToPayload())!["ownBlock"]);
        Assert.False(received.Task.IsCompleted, "Applying the daemon's state sends nothing back.");

        insert.OwnBlock = true;
        JsonNode sent = await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("xlr1", sent["channel"]!.GetValue<string>());
        Assert.Equal("fx", sent["insertId"]!.GetValue<string>());
        Assert.True(sent["value"]!.GetValue<bool>());
    }

    [Fact]
    public void AnEffectPresetMayCarryTheSwitchAsABoolean()
    {
        static EffectChainData Data(JsonNode? ownBlock) => new(1, 1, new JsonArray(new JsonObject
        {
            ["id"] = "fx", ["kind"] = "vst3", ["plugin"] = "x", ["ownBlock"] = ownBlock, ["params"] = new JsonObject(),
        }));
        Data(true).Validate();
        Data(null).Validate();
        Assert.Throws<InvalidDataException>(() => Data("yes").Validate());
    }

    private static JsonArray Chain(bool ownBlock) => new(new JsonObject
    {
        ["insert"] = new JsonObject
        {
            ["id"] = "fx", ["kind"] = "vst3", ["plugin"] = "urn:test:fx", ["ownBlock"] = ownBlock,
            ["params"] = new JsonObject(),
        },
        ["nativeHostRunning"] = true,
    });
}
