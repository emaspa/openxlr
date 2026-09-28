using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class ExclusiveGroupClientTests
{
    [Fact]
    public async Task GroupEditorSendsStableIdsAndPreservesDaemonErrors()
    {
        var received = new ConcurrentQueue<JsonNode>();
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                if (command["cmd"]?.GetValue<string>() is not ("setExclusiveGroup" or "deleteExclusiveGroup")) continue;
                received.Enqueue(command);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>(),
                    error = command["cmd"]!.GetValue<string>() == "deleteExclusiveGroup" ? "disk full" : null }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(await client.SetExclusiveGroupAsync(null, "Microphones", ["xlr1", "headset"]).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(await client.SetExclusiveGroupAsync("microphones", "Studio", ["headset", "xlr1"]).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("disk full", await client.DeleteExclusiveGroupAsync("microphones").WaitAsync(TimeSpan.FromSeconds(5)));
        var commands = received.ToArray();
        Assert.Equal(3, commands.Length);
        Assert.Null(commands[0]["group"]);
        Assert.Equal("microphones", commands[1]["group"]!.GetValue<string>());
        Assert.Equal(new[] { "headset", "xlr1" }, commands[1]["channels"]!.AsArray().Select(ch => ch!.GetValue<string>()));
        Assert.Equal("microphones", commands[2]["group"]!.GetValue<string>());
        Assert.Equal(3, commands.Select(c => c["requestId"]!.GetValue<string>()).Distinct().Count());
    }
}
