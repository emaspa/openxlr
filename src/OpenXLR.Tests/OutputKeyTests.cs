using System.Text.Json;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class OutputKeyTests
{
    [Theory]
    [InlineData("""{"cmd":"adjustOutputVolume","value":0.05}""", true)]
    [InlineData("""{"cmd":"adjustOutputVolume","device":"headset","value":-0.5}""", true)]
    [InlineData("""{"cmd":"adjustOutputVolume","value":0.51}""", false)]
    [InlineData("""{"cmd":"adjustOutputVolume","value":1e999}""", false)]
    [InlineData("""{"cmd":"adjustOutputVolume"}""", false)]
    [InlineData("""{"cmd":"toggleOutputMute"}""", true)]
    [InlineData("""{"cmd":"toggleOutputMute","device":""}""", false)]
    [InlineData("""{"cmd":"setOutputDeviceVolume","value":1.5}""", true)]
    [InlineData("""{"cmd":"setOutputDeviceVolume","device":"headset","value":0}""", true)]
    [InlineData("""{"cmd":"setOutputDeviceVolume","value":1.51}""", false)]
    [InlineData("""{"cmd":"setOutputDeviceVolume","value":-0.01}""", false)]
    [InlineData("""{"cmd":"setOutputDeviceVolume","value":"loud"}""", false)]
    [InlineData("""{"cmd":"setOutputDeviceVolume"}""", false)]
    [InlineData("""{"cmd":"setOutputDeviceVolume","device":"a\nb","value":1}""", false)]
    [InlineData("""{"cmd":"setOutputVolumeLock","value":true}""", true)]
    [InlineData("""{"cmd":"setOutputVolumeLock","value":false}""", true)]
    [InlineData("""{"cmd":"setOutputVolumeLock","value":1}""", false)]
    [InlineData("""{"cmd":"setOutputVolumeLock"}""", false)]
    [InlineData("""{"cmd":"setMainOutput"}""", false)]
    [InlineData("""{"cmd":"setMainOutput","device":"headset\nother"}""", false)]
    [InlineData("""{"cmd":"setMainOutput","device":"@monitor"}""", true)]
    public void CommandsValidateOptionalTargetsAndBoundedSteps(string json, bool valid)
    {
        using var mixer = new Mixer();
        Command command = JsonSerializer.Deserialize<Command>(json)!;
        Assert.Equal(valid, CommandValidation.Check(command, mixer, _ => null) is null);
    }

    [Fact]
    public void OutputBindingsStayBoundedAndKeepDistinctStableKeys()
    {
        Assert.Equal(DesktopKeySettings.MainKey("a.b"), DesktopKeySettings.MainKey("a.b"));
        Assert.NotEqual(DesktopKeySettings.MainKey("a.b"), DesktopKeySettings.MainKey("a_b"));
        var normalized = new DesktopKeySettings { OutputControls = true, OutputDevice = "headset#hp1",
            MainOutputs = [null!, "", "123", "@DEFAULT_SINK@", "@monitor", "@monitor", .. Enumerable.Range(0, 30).Select(i => "sink" + i)] }.Normalize();
        Assert.False(normalized.OutputControls);
        Assert.Equal(16, normalized.MainOutputs.Count);
        Assert.Equal("@monitor", normalized.MainOutputs[0]);
    }

    [Fact]
    public async Task MonitoringPauseKeyIsIndependentOfTheOutputTargetAndSurvivesReload()
    {
        await using var client = new DaemonClient();
        using var keys = new DesktopKeys(client);
        Assert.DoesNotContain("monitor_mute", keys.Actions(new DesktopKeySettings()).Keys);
        var settings = new DesktopKeySettings { Enabled = true, MonitorControls = true,
            OutputControls = true, OutputDevice = "unavailable#hp1" };
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string directory = Path.Combine(Path.GetTempPath(), "openxlr-monitor-key-" + Guid.NewGuid());
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", directory);
            settings.Save();
            DesktopKeySettings restored = DesktopKeySettings.Load();
            Assert.True(restored.Enabled);
            Assert.True(restored.MonitorControls);
            Assert.False(restored.OutputControls);
            Assert.Equal(["monitor_mute"], keys.Actions(restored).Keys);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task PortalActionsSendTheSameBoundedCommandsAsDeckKeys()
    {
        var received = new List<System.Text.Json.Nodes.JsonNode>();
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                if (command["requestId"] is not { } id) continue;
                received.Add(command);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = id.GetValue<string>() }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var keys = new DesktopKeys(client);
        var actions = keys.Actions(new DesktopKeySettings { MonitorControls = true, OutputControls = true, OutputDevice = "headset", MainOutputs = ["speakers", "@monitor"] });
        foreach (string id in new[] { "output_up", "output_down", "output_mute", DesktopKeySettings.MainKey("speakers"), DesktopKeySettings.MainKey("@monitor"), "monitor_mute" })
            Assert.Null(await actions[id].Invoke!());
        Assert.Equal(["adjustOutputVolume", "adjustOutputVolume", "toggleOutputMute", "setMainOutput", "setMainOutput", "toggleMonitorMuted"], received.Select(n => n["cmd"]!.GetValue<string>()));
        Assert.Equal(.05, received[0]["value"]!.GetValue<double>());
        Assert.Equal(-.05, received[1]["value"]!.GetValue<double>());
        Assert.Equal("headset", received[2]["device"]!.GetValue<string>());
        Assert.Equal("@monitor", received[4]["device"]!.GetValue<string>());
        Assert.Null(received[5]["device"]);
        Assert.Null(received[5]["value"]);
    }
}
