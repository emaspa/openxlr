using System.Text.Json.Nodes;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class SoundCheckTests
{
    [Fact]
    public void AMissingHelperIsRefusedAtOnceWithTheReason()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"openxlr-no-host-{Guid.NewGuid():N}", "openxlr-lv2-host");
        var started = System.Diagnostics.Stopwatch.StartNew();
        var error = Assert.Throws<InvalidOperationException>(() => new PipeWireAdapter().CreateSoundCheck("xlr1", out _, missing));
        Assert.Equal(Mixer.SoundCheckNeedsHost, error.Message);
        Assert.Contains("EnableNativeLv2Host", error.Message);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("record")]
    [InlineData("loop")]
    [InlineData("live")]
    public void ABuildWithoutTheHelperRefusesEverythingButStop(string action)
    {
        using var mixer = new Mixer();
        Assert.Equal(Mixer.SoundCheckNeedsHost, CommandValidation.Check(
            new Command { Cmd = "soundCheck", Channel = "xlr1", Action = action }, mixer, _ => null, nativeHostInstalled: false));
        Assert.Null(CommandValidation.Check(
            new Command { Cmd = "soundCheck", Channel = "xlr1", Action = "stop" }, mixer, _ => null, nativeHostInstalled: false));
    }

    [Theory]
    [InlineData("xlr1", "record", true)]
    [InlineData("xlr2", "loop", true)]
    [InlineData("xlr1", "live", true)]
    [InlineData("xlr1", "stop", true)]
    [InlineData("system", "record", false)]
    [InlineData("mix:monitor", "record", false)]
    [InlineData(null, "record", false)]
    [InlineData("xlr1", "unknown", false)]
    [InlineData("xlr1", null, false)]
    public void OnlyTheXlrChannelsAndTheFourActionsAreAccepted(string? channel, string? action, bool valid)
    {
        using var mixer = new Mixer();
        Assert.Equal(valid, CommandValidation.Check(
            new Command { Cmd = "soundCheck", Channel = channel, Action = action }, mixer, _ => null, nativeHostInstalled: true) is null);
    }

    [Fact]
    public void NoDeviceDoesNotCreateARecordingAndStopIsIdempotent()
    {
        using var mixer = new Mixer();
        Assert.Throws<InvalidOperationException>(() => mixer.SoundCheck("xlr1", "record"));
        Assert.Throws<ArgumentException>(() => mixer.SoundCheck("system", "record"));
        Assert.Throws<ArgumentException>(() => mixer.SoundCheck("xlr1", "invalid"));
        mixer.SoundCheck("xlr1", "stop");
        mixer.SoundCheck("xlr1", "stop");
        mixer.SoundCheck("xlr2", "stop");
        Assert.Equal("idle", mixer.Snapshot().SoundCheck.Mode);
        Assert.Null(mixer.Snapshot().SoundCheck.Channel);
        Assert.Null(mixer.SoundCheckChannel);
        Assert.DoesNotContain("soundCheck", System.Text.Json.JsonSerializer.Serialize(mixer.ExportSettings()), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OldRepliesCannotChangeANewConnectionsErrorOrUnlockItsPendingCommand(bool closing, bool failed)
    {
        var received = new[] { new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var release = new[] { new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            var replies = new List<Task>();
            for (int i = 0; i < 2;)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                if (command["cmd"]!.GetValue<string>() == "auth") continue;
                int index = i++;
                received[index].TrySetResult();
                replies.Add(Reply());
                async Task Reply()
                {
                    await release[index].Task.WaitAsync(stop);
                    await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>(), error = index == 1 ? "current failure" : failed ? "old failure" : null }, stop);
                }
            }
            await Task.WhenAll(replies);
            await Task.Delay(Timeout.Infinite, stop);
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var view = new SoundCheckViewModel(client, "xlr1");
        Task old = closing ? view.StopOnCloseAsync() : view.RunAsync("record");
        await received[0].Task.WaitAsync(TimeSpan.FromSeconds(5));
        view.Reset();
        Task current = view.RunAsync("record");
        await received[1].Task.WaitAsync(TimeSpan.FromSeconds(5));
        view.Apply(JsonNode.Parse("""{"channel":"xlr1","mode":"live","seconds":1,"error":"current state"}"""));
        release[0].SetResult();
        await old;
        Assert.Equal("current state", view.Error);
        Assert.False(view.CanRecord);
        release[1].SetResult();
        await current;
        Assert.Equal("current failure", view.Error);
        Assert.True(view.CanRecord);
    }

    [Fact]
    public async Task AStoppedSessionsErrorBelongsOnlyToItsMicrophoneAndCanBeDismissed()
    {
        await using var client = new DaemonClient();
        var first = new SoundCheckViewModel(client, "xlr1");
        var second = new SoundCheckViewModel(client, "xlr2");
        var state = JsonNode.Parse("""{"channel":"xlr1","mode":"idle","seconds":0,"error":"Sound Check ended after ten minutes."}""");
        first.Apply(state); second.Apply(state);
        Assert.NotNull(first.Error);
        Assert.False(first.Active);
        Assert.True(first.CanStop);
        Assert.Null(second.Error);
        Assert.False(second.CanStop);
        first.Apply(JsonNode.Parse("""{"channel":null,"mode":"idle","seconds":0}"""));
        Assert.Null(first.Error);
        Assert.False(first.CanStop);
    }

    [Fact]
    public async Task TheViewUsesServerProgressAndNeverLoopsAnEmptySample()
    {
        await using var client = new DaemonClient();
        var view = new SoundCheckViewModel(client, "xlr1");
        Assert.True(view.CanRecord);
        Assert.False(view.CanLoop);
        view.Apply(JsonNode.Parse("""{"channel":"xlr1","mode":"recording","seconds":0.05}"""));
        Assert.False(view.CanRecord);
        Assert.False(view.CanLoop);
        view.Apply(JsonNode.Parse("""{"channel":"xlr1","mode":"recording","seconds":1.5}"""));
        Assert.True(view.CanLoop);
        Assert.Contains("Recording", view.Status);
        view.Apply(JsonNode.Parse("""{"channel":"xlr1","mode":"looping","seconds":1.5}"""));
        Assert.Contains("Looping", view.Status);
        Assert.True(view.CanStop);
        view.Apply(JsonNode.Parse("""{"channel":"xlr2","mode":"looping","seconds":1.5}"""));
        Assert.False(view.Active);
        Assert.False(view.CanLoop);
        view.Apply(JsonNode.Parse("""{"channel":"xlr1","mode":"looping","seconds":1.5}"""));
        view.Apply(null);
        Assert.False(view.CanLoop);
        Assert.False(view.CanStop);
        Assert.Contains("No sample", view.Status);
        await view.RunAsync("record");
        Assert.NotNull(view.Error);
    }
}
