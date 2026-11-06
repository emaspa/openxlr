using System.Reflection;
using System.Text.Json.Nodes;
using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class MonitorEqPresetTests
{
    [Fact]
    public async Task MixStateUpdatesOnlyItsOwnPickerAndAnOlderDaemonHidesIt()
    {
        await using var client = new DaemonClient();
        var a = new MixViewModel(client, "monitor", "A");
        var b = new MixViewModel(client, "monitor2", "B");
        a.ApplyFromDaemon(JsonNode.Parse("""{"kind":"monitor","monitorEq":{"preset":"bass-boost"}}""")!);
        b.ApplyFromDaemon(JsonNode.Parse("""{"kind":"monitor","monitorEq":{}}""")!);
        Assert.True(a.Inserts.HasMonitorEq);
        Assert.Equal("bass-boost", a.Inserts.SelectedMonitorEq!.Id);
        Assert.True(b.Inserts.HasMonitorEq);
        Assert.Null(b.Inserts.SelectedMonitorEq!.Id);
        Assert.Equal([null, "voice-boost", "fps", "bass-boost", "soft-treble"], a.Inserts.MonitorEqOptions.Select(p => p.Id));

        a.ApplyFromDaemon(JsonNode.Parse("""{"kind":"monitor"}""")!);
        Assert.False(a.Inserts.HasMonitorEq);
        Assert.Null(a.Inserts.SelectedMonitorEq);
        Assert.True(b.Inserts.HasMonitorEq);
    }

    [Fact]
    public async Task AReplyFromTheOldConnectionCannotChangeTheNewPicker()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            JsonNode command = await SocketTestServer.Receive(socket, stop);
            received.SetResult();
            await release.Task.WaitAsync(stop);
            await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>(),
                error = "the old request failed" }, stop);
        });
        await using var client = new DaemonClient(server.Url);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += connected => { if (connected) ready.TrySetResult(); };
        client.Start();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var inserts = new MixViewModel(client, "monitor", "Monitor").Inserts;
        inserts.ApplyMonitorEq(JsonNode.Parse("""{"preset":"fps"}"""));
        var change = (Task)typeof(InsertsViewModel).GetMethod("ChooseMonitorEqAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(inserts, [inserts.MonitorEqOptions.Single(p => p.Id == "bass-boost")])!;
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(inserts.MonitorEqBusy);

        inserts.ResetForNewConnection();
        Assert.False(inserts.HasMonitorEq);
        Assert.False(inserts.MonitorEqBusy);
        Assert.Null(inserts.SelectedMonitorEq);
        inserts.ApplyMonitorEq(JsonNode.Parse("""{"preset":"soft-treble"}"""));

        release.SetResult();
        await change.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("soft-treble", inserts.SelectedMonitorEq!.Id);
        Assert.Null(inserts.MonitorEqError);
    }

    [Fact]
    public async Task TheClientSendsTheMixIdAndExplicitNullForFlat()
    {
        var received = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            var command = await SocketTestServer.Receive(socket, stop);
            received.SetResult(command);
            await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>() }, stop);
        });
        await using var client = new DaemonClient(server.Url);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += connected => { if (connected) ready.TrySetResult(); };
        client.Start();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(await client.SetMonitorEqPresetAsync("earbuds", null));
        JsonObject command = (await received.Task.WaitAsync(TimeSpan.FromSeconds(5))).AsObject();
        Assert.Equal("setMonitorEqPreset", command["cmd"]!.GetValue<string>());
        Assert.Equal("earbuds", command["mix"]!.GetValue<string>());
        Assert.True(command.ContainsKey("value"));
        Assert.Null(command["value"]);
    }
}
