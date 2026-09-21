using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class LayoutAppearanceTests
{
    [Theory]
    [InlineData("<svg/>", "#123456", false)]
    [InlineData("♪", "red", false)]
    [InlineData("♪", "#ffffff\"", false)]
    [InlineData("♪", "#123456", true)]
    [InlineData("", null, true)]
    public void OnlyBoundedAppearanceDataReachesTheMixer(string icon, string? colour, bool valid)
    {
        using var mixer = new Mixer();
        var value = new LayoutAppearance(icon, colour);
        Assert.Equal(valid, LayoutAppearance.IsValid(value));
        var command = new Command { Cmd = "setLayoutAppearance", Channel = "game", Appearance = value };
        Assert.Equal(valid, CommandValidation.Check(command, mixer, _ => null) is null);
        Assert.NotNull(CommandValidation.Check(command with { Mix = "monitor" }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(command with { Channel = "gone" }, mixer, _ => null));
        Assert.False(LayoutAppearance.IsValid(value with { Order = -1 }));
        Assert.False(LayoutAppearance.IsValid(value with { Order = int.MaxValue }));
    }

    [Fact]
    public void AppearanceAndDisplayOrderAreTransactionalAndDoNotChangeAudioConfiguration()
    {
        using var mixer = new Mixer();
        SetField(mixer, "_built", true);
        var original = (MixerConfig)typeof(Mixer).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mixer)!;
        string before = JsonSerializer.Serialize(mixer.ExportSettings());
        Assert.Throws<IOException>(() => mixer.SetLayoutAppearance("channel:game", new("♫", "#12ABCD", true), _ => "disk full"));
        Assert.Equal(before, JsonSerializer.Serialize(mixer.ExportSettings()));
        mixer.SetLayoutAppearance("channel:game", new("♫", "#12ABCD", true), _ => null);
        var channels = original.Channels.Reverse().Select(c => c.Id).ToArray();
        var mixes = original.Mixes.Reverse().Select(m => m.Id).ToArray();
        string decorated = JsonSerializer.Serialize(mixer.ExportSettings());
        Assert.Throws<IOException>(() => mixer.SetDisplayOrder(channels, mixes, _ => "disk full"));
        Assert.Equal(decorated, JsonSerializer.Serialize(mixer.ExportSettings()));
        Assert.Throws<InvalidOperationException>(() => mixer.SetDisplayOrder(channels.Skip(1).ToArray(), mixes, _ => null));
        Assert.Throws<InvalidOperationException>(() => mixer.SetDisplayOrder(Enumerable.Repeat("game", channels.Length).ToArray(), mixes, _ => null));
        mixer.SetDisplayOrder(channels, mixes, _ => null);
        Assert.Same(original, typeof(Mixer).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mixer));
        var snapshot = mixer.Snapshot();
        Assert.Equal(channels, snapshot.Channels.Select(c => c.Id));
        Assert.Equal(mixes, snapshot.Mixes.Select(m => m.Id));
        Assert.True(snapshot.Channels.Single(c => c.Id == "game").Appearance.Hidden);
        int? order = mixer.ExportSettings().Appearance["channel:game"].Order;
        mixer.SetLayoutAppearance("channel:game", new("◆"), _ => null);
        Assert.Equal(order, mixer.ExportSettings().Appearance["channel:game"].Order);
        Assert.Throws<InvalidOperationException>(() => mixer.SetLayoutAppearance("mix:monitor", new(Hidden: true), _ => null));
        SetField(mixer, "_built", false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoutingButtonUsesEditableDisplayOrderAndSurfacesSaveFailure(bool fail)
    {
        var received = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                if (command["cmd"]!.GetValue<string>() == "auth") continue;
                received.TrySetResult(command);
                await SocketTestServer.Send(socket, new { type = "commandResult",
                    requestId = command["requestId"]!.GetValue<string>(), error = fail ? "disk full" : null }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var vm = new MainViewModel(client);
        using var mixer = new Mixer();
        SetField(mixer, "_built", true);
        try
        {
            MixerConfig original = MixerConfig.Default();
            mixer.SetDisplayOrder(original.Channels.Reverse().Select(c => c.Id).ToArray(),
                original.Mixes.Reverse().Select(m => m.Id).ToArray(), _ => null);
            var state = JsonSerializer.SerializeToNode(mixer.Snapshot(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [state]);
            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
            client.Start();
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(fail ? "disk full" : null, await vm.UseDisplayOrderForRouting());
            JsonNode command = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("setLayoutOrder", command["cmd"]!.GetValue<string>());
            string[] channels = command["channels"]!.Deserialize<string[]>()!;
            string[] mixes = command["mixes"]!.Deserialize<string[]>()!;
            Assert.Equal(original.Channels.Where(c => c.InputPair is null).Reverse().Select(c => c.Id), channels);
            Assert.Equal(original.Mixes.Where(m => m.Kind == MixKind.VirtualMic).Reverse().Select(m => m.Id), mixes);
            Assert.Equal(channels[0], original.WithOrder(channels, mixes).ResolveApplicationChannel("unknown"));
            if (fail) Assert.Equal("disk full", vm.Status);
        }
        finally { SetField(mixer, "_built", false); }
    }

    [Fact]
    public async Task CompactSelectionSurvivesRenamesAndFallsBackAfterRemovalWithoutLosingSends()
    {
        string path = Directory.CreateTempSubdirectory("openxlr-appearance-").FullName;
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", path);
            await using var client = new DaemonClient();
            var vm = new MainViewModel(client);
            Apply(vm, "game", "music");
            var game = vm.Channels[0];
            var music = vm.Channels[1];
            vm.SelectedCompactChannel = music;
            vm.CompactMixer = true;
            Assert.False(game.DisplayVisible);
            Assert.True(music.DisplayVisible);
            Apply(vm, "game", "music");
            Assert.Same(music, vm.SelectedCompactChannel);
            Assert.Equal(0.42, Assert.Single(music.Sends).Level);
            vm.CompactMixer = false;
            Assert.False(game.DisplayVisible); // hidden in full view only
            Assert.True(music.DisplayVisible);
            vm.SelectedCompactChannel = game;
            vm.CompactMixer = true;
            Assert.True(game.DisplayVisible); // compact selection can reveal a hidden channel
            Apply(vm, "music");
            Assert.Same(music, vm.SelectedCompactChannel);
            Assert.True(music.DisplayVisible);
            Apply(vm);
            Assert.Null(vm.SelectedCompactChannel);
            Assert.Empty(vm.Channels);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(path, true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("stream")]
    [InlineData("monitor+monitor2")]
    public async Task ReorderingMixPresentationDoesNotChangeTheDisplayedDefaultOutputFeed(string? explicitFeed)
    {
        using var mixer = new Mixer();
        SetField(mixer, "_built", true);
        try
        {
            var config = MixerConfig.Default();
            mixer.SetDisplayOrder(config.Channels.Select(c => c.Id).ToArray(),
                config.Mixes.Reverse().Select(m => m.Id).ToArray(), _ => null);
            var state = JsonSerializer.SerializeToNode(mixer.Snapshot(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            if (explicitFeed is not null) state["monitorFeeds"]!["headset"] = explicitFeed;
            await using var client = new DaemonClient();
            var vm = new MainViewModel(client);
            var devices = JsonNode.Parse("""[{"name":"headset","description":"Headset","kind":0,"isOwn":false}]""");
            typeof(MainViewModel).GetMethod("ApplyDevices", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [devices, state]);
            Assert.Equal(explicitFeed ?? mixer.JackMonitorMix, Assert.Single(vm.MonitorOutputs).Feed!.Id);
            Assert.Single(vm.MonitorOutputs[0].Feeds, f => f.Id.Contains('+'));
            Assert.Equal(mixer.JackMonitorMix, state["primaryMonitorMix"]?.GetValue<string>());
        }
        finally { SetField(mixer, "_built", false); }
    }

    [Fact]
    public void InvalidSavedPresentationCannotSuppressHealthyEntries()
    {
        var saved = new MixerSettings { Appearance = new()
        {
            ["channel:music"] = new("♫", "#123456"),
            ["channel:game"] = new("<image/>"),
            ["mix:monitor"] = null!,
        }};
        var sanitized = SavedMixerValidation.Sanitize(saved, out var dropped);
        Assert.Single(sanitized.Appearance);
        Assert.NotEmpty(dropped);
    }

    private static void SetField(Mixer mixer, string name, object value) =>
        typeof(Mixer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mixer, value);

    private static void Apply(MainViewModel vm, params string[] channels)
    {
        var state = new JsonObject
        {
            ["mixes"] = new JsonArray(new JsonObject { ["id"] = "monitor", ["name"] = "Monitor A" }),
            ["channels"] = new JsonArray(channels.Select(id => (JsonNode)new JsonObject
            {
                ["id"] = id, ["name"] = id, ["levels"] = new JsonObject { ["monitor"] = 0.42 },
                ["appearance"] = new JsonObject { ["icon"] = "♫", ["colour"] = "#123456", ["hidden"] = id == "game" },
            }).ToArray()),
        };
        typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [state]);
    }
}
