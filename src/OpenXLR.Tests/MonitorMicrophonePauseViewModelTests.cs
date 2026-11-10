using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class MonitorMicrophonePauseViewModelTests
{
    [Fact]
    public async Task ReportsDoNotEchoAndAnOlderDaemonLeavesTheOptInOff()
    {
        await using var f = await Fixture.Start();
        f.Report(true, true);
        Assert.True(f.Model.MonitorMuteMicrophones);
        Assert.True(f.Model.MicrophonePausePending);
        Assert.NotNull(f.Model.MonitorMicrophoneNote);
        f.Report(new JsonObject());
        Assert.False(f.Model.MonitorMuteMicrophones);
        Assert.False(f.Model.MicrophonePausePending);
        Assert.Null(f.Model.MonitorMicrophoneNote);
        Assert.False(f.Commands.TryRead(out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RapidChangesRetainTheLatestIntentAndIgnoreOldReplies(bool oldReplyFails)
    {
        await using var f = await Fixture.Start();
        f.Report(false, false);
        f.Model.MonitorMuteMicrophones = true;
        JsonNode first = await f.Next();
        Assert.True(first["value"]!.GetValue<bool>());
        f.Model.MonitorMuteMicrophones = false;
        JsonNode second = await f.Next();
        Assert.False(second["value"]!.GetValue<bool>());
        f.Report(true, true);
        Assert.False(f.Model.MonitorMuteMicrophones);
        f.Status("newer status");
        await f.Reply(first, oldReplyFails ? "old failure" : null);
        Assert.False(f.Model.MonitorMuteMicrophones);
        Assert.Equal("newer status", f.Model.Status);
        Assert.Null(f.Model.MonitorMicrophoneError);
        f.Report(false, false);
        await f.Reply(second);
        await Until(() => !f.Pending);
        f.Report(true, false);
        Assert.True(f.Model.MonitorMuteMicrophones);
        Assert.False(f.Commands.TryRead(out _));
    }

    [Fact]
    public async Task TheSharedVolumeIsNotSentWhileItsFirstOutputIsLocked()
    {
        await using var f = await Fixture.Start();
        try
        {
            f.Report(new JsonObject
            {
                ["outputVolumeLock"] = new JsonObject { ["device"] = "headset", ["volume"] = 1.25 },
                ["monitorVolumeLocked"] = true,
            });
            // What the slider writes back when the desktop closes the boost range under it.
            f.Model.OutputVolume = 1;
            SliderSync.FlushPending();

            f.Report(new JsonObject { ["outputVolumeLock"] = null });
            f.Model.OutputVolume = .9;
            SliderSync.FlushPending();
            JsonNode sent = await f.Next();
            Assert.Equal("setOutputVolume", sent["cmd"]!.GetValue<string>());
            Assert.Equal(.9, sent["value"]!.GetValue<double>());
            Assert.False(f.Commands.TryRead(out _));
        }
        finally { SliderSync.Forget("outvol"); }
    }

    [Fact]
    public async Task ARejectedLatestChangeRestoresTheReportAndUsesTheExistingStatus()
    {
        await using var f = await Fixture.Start();
        f.Report(true, false);
        f.Model.MonitorMuteMicrophones = false;
        JsonNode command = await f.Next();
        await f.Reply(command, "could not save the microphone option");
        await Until(() => !f.Pending);
        Assert.True(f.Model.MonitorMuteMicrophones);
        Assert.Equal("could not save the microphone option", f.Model.Status);
        Assert.Equal("could not save the microphone option", f.Model.MonitorMicrophoneNote);
        Assert.False(f.Commands.TryRead(out _));
        f.Model.MonitorMuteMicrophones = false;
        JsonNode repaired = await f.Next();
        Assert.Null(f.Model.MonitorMicrophoneError);
        f.Report(false, false);
        await f.Reply(repaired);
        await Until(() => !f.Pending);
        Assert.Null(f.Model.MonitorMicrophoneNote);
    }

    [Fact]
    public async Task RemovingTheMixerCancelsItsEditAndIgnoresALateFailure()
    {
        await using var f = await Fixture.Start();
        f.Report(false, true);
        f.Model.MonitorMuteMicrophones = true;
        JsonNode command = await f.Next();
        f.Report(null);
        Assert.False(f.Model.MonitorMuteMicrophones);
        Assert.False(f.Model.MicrophonePausePending);
        Assert.False(f.Pending);
        f.Status("new session");
        await f.Reply(command, "obsolete failure");
        Assert.Equal("new session", f.Model.Status);
        f.Report(true, false);
        Assert.True(f.Model.MonitorMuteMicrophones);
        Assert.False(f.Commands.TryRead(out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MonitoringCommandsWaitForAndReturnTheirOwnAcknowledgement(bool toggle)
    {
        await using var f = await Fixture.Start();
        Task<string?> request = toggle ? f.Client.ToggleMonitorMutedAsync() : f.Client.SetMonitorMuteMicrophonesAsync(true);
        JsonNode command = await f.Next();
        Assert.Equal(toggle ? "toggleMonitorMuted" : "setMonitorMuteMicrophones", command["cmd"]!.GetValue<string>());
        if (!toggle) Assert.True(command["value"]!.GetValue<bool>());
        Assert.False(request.IsCompleted);
        await f.Reply(command, "rejected");
        Assert.Equal("rejected", await request.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    private static async Task Until(Func<bool> ready)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.True(ready());
    }

    private sealed class Fixture(SocketTestServer server, DaemonClient client, WebSocket socket,
        ChannelReader<JsonNode> commands) : IAsyncDisposable
    {
        internal MainViewModel Model { get; } = new(client);
        internal DaemonClient Client => client;
        internal ChannelReader<JsonNode> Commands => commands;
        internal bool Pending => typeof(MainViewModel).GetField("_pendingMonitorMuteMicrophones", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(Model) is not null;

        internal static async Task<Fixture> Start()
        {
            var connected = new TaskCompletionSource<WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
            var commands = Channel.CreateUnbounded<JsonNode>();
            var server = await SocketTestServer.Start(async (socket, stop) =>
            {
                connected.TrySetResult(socket);
                while (!stop.IsCancellationRequested)
                {
                    JsonNode command = await SocketTestServer.Receive(socket, stop);
                    if (command["cmd"]?.GetValue<string>() is "setMonitorMuteMicrophones" or "toggleMonitorMuted" or "setOutputVolume")
                        commands.Writer.TryWrite(command);
                }
            });
            var client = new DaemonClient(server.Url);
            var online = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.ConnectionChanged += up => { if (up) online.TrySetResult(); };
            client.Start();
            await online.Task.WaitAsync(TimeSpan.FromSeconds(3));
            return new(server, client, await connected.Task.WaitAsync(TimeSpan.FromSeconds(3)), commands.Reader);
        }

        internal void Report(bool option, bool pending) => Report(new JsonObject
        {
            ["monitorMuteMicrophones"] = option, ["microphonePausePending"] = pending,
        });

        internal void Report(JsonNode? mixer)
        {
            typeof(MainViewModel).GetField("_applying", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Model, true);
            try
            {
                typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Model, [mixer]);
            }
            finally { typeof(MainViewModel).GetField("_applying", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Model, false); }
        }

        internal void Status(string text) => typeof(MainViewModel).GetProperty(nameof(MainViewModel.Status))!.SetValue(Model, text);
        internal Task<JsonNode> Next() => commands.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        internal async Task Reply(JsonNode command, string? error = null)
        {
            await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>(), error }, CancellationToken.None);
            // Wait for this acknowledgement to leave the client's pending map.
            string id = command["requestId"]!.GetValue<string>();
            await Until(() =>
            {
                object lifecycle = typeof(DaemonClient).GetField("_lifecycle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
                lock (lifecycle)
                    return !((System.Collections.IDictionary)typeof(DaemonClient)
                        .GetField("_edits", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!).Contains(id);
            });
        }

        public async ValueTask DisposeAsync()
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }
}
