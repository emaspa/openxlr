using Avalonia.Controls;
using Avalonia.Threading;
using System.Collections.Concurrent;
using OpenXLR.UI;
using System.Text.Json.Nodes;

namespace OpenXLR.Tests;

internal static class EffectWorkflowWindowTests
{
    internal static void CheckControlOwnership(Window owner, DaemonClient client)
    {
        Task pending = CheckQueuedParameters();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!pending.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(pending.IsCompleted, "Effect parameter check hung.");
        pending.GetAwaiter().GetResult();
        var first = new InsertsViewModel(client, "xlr1");
        var second = new InsertsViewModel(client, "xlr2");
        first.Apply(Chain("urn:one"));
        second.Apply(Chain("urn:one"));
        var original = Assert.Single(first.Items);
        InsertWindows.OpenControls(owner, original);
        InsertWindows.OpenControls(owner, Assert.Single(second.Items));
        var windows = owner.OwnedWindows.OfType<InsertControlsWindow>().ToArray();
        try
        {
            Assert.Equal(2, windows.Length);
            var old = Assert.Single(windows, w => ReferenceEquals(w.DataContext, original));
            var other = Assert.Single(windows, w => ReferenceEquals(w.DataContext, second.Items[0]));
            first.Apply(Chain("urn:replacement"));
            Assert.False(old.IsVisible);
            Assert.True(other.IsVisible);
            InsertWindows.OpenControls(owner, Assert.Single(first.Items));
            Assert.Contains(owner.OwnedWindows, w => ReferenceEquals(w.DataContext, first.Items[0]));
            second.Apply(null);
            Assert.False(other.IsVisible);
        }
        finally
        {
            foreach (var window in owner.OwnedWindows.OfType<InsertControlsWindow>().ToArray()) window.Close();
        }
    }

    private static async Task CheckQueuedParameters()
    {
        var commands = new ConcurrentQueue<JsonNode>();
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                if (command["cmd"]!.GetValue<string>() == "auth") continue;
                Assert.Equal("setInsertParam", command["cmd"]!.GetValue<string>());
                commands.Enqueue(command);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var first = new InsertsViewModel(client, "xlr1");
        var second = new InsertsViewModel(client, "xlr2");
        first.Apply(Chain("urn:one"));
        second.Apply(Chain("urn:one"));
        first.Items[0].SendParam("gain", .2);
        second.Items[0].SendParam("gain", .4);
        await WaitFor(() => commands.Count == 2);
        Assert.Equal(new[] { "xlr1", "xlr2" }, commands.Select(c => c["channel"]!.GetValue<string>()).Order());
        foreach (var command in commands)
            Assert.Equal(command["channel"]!.GetValue<string>() == "xlr1" ? .2 : .4, command["value"]!.GetValue<double>());
        // A surviving control supplies a barrier for the next timer tick.
        // Deleted, replaced or disconnected controls must never reach that tick.
        foreach (string action in new[] { "replace", "remove", "disconnect" })
        {
            first.Apply(Chain("urn:one"));
            first.Items[0].SendParam("gain", .8);
            if (action == "disconnect") first.ResetForNewConnection();
            else first.Apply(action == "remove" ? null : Chain("urn:replacement"));
            int count = commands.Count;
            second.Items[0].SendParam("gain", .6);
            await WaitFor(() => commands.Count > count);
            await Task.Delay(150);
            Assert.Equal(count + 1, commands.Count);
            Assert.Equal("xlr2", commands.Last()["channel"]!.GetValue<string>());
        }
        second.ResetForNewConnection();
        static async Task WaitFor(Func<bool> ready)
        {
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.True(ready(), "Queued parameter commands did not arrive.");
        }
    }

    private static JsonNode Chain(string plugin) => new JsonArray(new JsonObject
    {
        ["insert"] = new JsonObject { ["id"] = "shared", ["kind"] = "lv2", ["plugin"] = plugin },
    });
}
