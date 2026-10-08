using System.Text.Json.Nodes;
using OpenXLR.UI;

namespace OpenXLR.Tests;

/// <summary>The additional interfaces window's model: retired units, late replies and the source picker.</summary>
public sealed class WaveInterfaceViewModelTests
{
    [Fact]
    public async Task ARetiredInterfaceTakesNoMoreChangesOrState()
    {
        // A gain drag would start the slider timer on the UI thread, so the
        // guards are checked without one.
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var main = new MainViewModel(client);
        var model = new WaveInterfaceViewModel(client, main, "0fd9:007d@0123456789abcdef");
        model.Apply(JsonNode.Parse("""{"connected":true,"capabilities":{"gain":true,"mute":true},"state":{"gainDb":30}}""")!);
        Assert.True(model.CanControl);
        model.Retire();
        Assert.False(model.CanControl); Assert.False(model.CanEnable); Assert.False(model.Connected);
        model.Gain = 60; model.Mute = true;
        Assert.Equal(30, model.Gain); Assert.False(model.Mute);
        model.Apply(JsonNode.Parse("""{"connected":true,"state":{"gainDb":40}}""")!);
        Assert.Equal(30, model.Gain); Assert.False(model.Connected);
        await model.AddInputAsync(); Assert.Null(model.Error);
    }

    [Fact]
    public async Task TheSourcePickerUsesTheSerialFragmentThenTheModelFragmentAndNeverGuesses()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var main = new MainViewModel(client);
        main.Inputs.Add(new AudioDeviceItem("alsa_input.usb-Elgato_Wave_XLR_unit_A-00.analog-stereo", "Wave XLR", false, true));
        var model = new WaveInterfaceViewModel(client, main, "0fd9:007d@0123456789abcdef");
        model.Apply(JsonNode.Parse("""{"connected":true,"captureHint":"Wave_XLR_unitA-","captureModelHint":"Wave_XLR"}""")!);
        Assert.Equal("alsa_input.usb-Elgato_Wave_XLR_unit_A-00.analog-stereo", Assert.Single(model.Sources).Name);
        Assert.Same(model.Sources[0], model.Source);
        main.Inputs.Add(new AudioDeviceItem("alsa_input.usb-Elgato_Wave_XLR_unit_B-00.analog-stereo", "Wave XLR", false, true));
        model.Apply(JsonNode.Parse("""{"connected":true,"captureHint":"Wave_XLR_unitA-","captureModelHint":"Wave_XLR"}""")!);
        Assert.Equal(2, model.Sources.Count);
        // The earlier choice stays; a fresh picker with two candidates chooses nothing.
        var fresh = new WaveInterfaceViewModel(client, main, "0fd9:007d@0123456789abcdee");
        fresh.Apply(JsonNode.Parse("""{"connected":true,"captureHint":"Wave_XLR_unitA-","captureModelHint":"Wave_XLR"}""")!);
        Assert.Null(fresh.Source);
    }

    [Fact]
    public async Task ADelayedEnableErrorCannotChangeARetiredInterface()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                if (command["cmd"]?.GetValue<string>() != "setWaveInterfaceEnabled") continue;
                received.TrySetResult(); await release.Task.WaitAsync(stop);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>(), error = "stale error" }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start(); await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The window's model posts connection changes to the UI thread; one on
        // a client that never connects keeps this test off it.
        await using var idle = new DaemonClient("ws://127.0.0.1:1/ws");
        var model = new WaveInterfaceViewModel(client, new MainViewModel(idle), "0fd9:007d@0123456789abcdef");
        var enabling = model.EnableAsync(true);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        model.Retire(); release.TrySetResult(); await enabling;
        Assert.False(model.Enabled); Assert.Null(model.Error); Assert.False(model.CanEnable);
    }
}
