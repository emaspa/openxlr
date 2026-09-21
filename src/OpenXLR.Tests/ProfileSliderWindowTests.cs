using Avalonia.Threading;
using OpenXLR.UI;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace OpenXLR.Tests;

internal static class ProfileSliderWindowTests
{
    internal static void Check()
    {
        Task pending = CheckOrdering();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!pending.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(pending.IsCompleted, "Profile command check hung.");
        pending.GetAwaiter().GetResult();
    }

    private static async Task CheckOrdering()
    {
        var commands = new ConcurrentQueue<string>();
        int gain = 20, savedGain = 20;
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                JsonNode command = await SocketTestServer.Receive(socket, stop);
                string cmd = command["cmd"]!.GetValue<string>();
                if (cmd == "set") gain = command["value"]!.GetValue<int>();
                else if (cmd == "saveProfile") savedGain = gain;
                else if (cmd == "loadProfile")
                {
                    bool reject = command["name"]!.GetValue<string>() == "missing";
                    if (!reject) gain = savedGain;
                    await SocketTestServer.Send(socket, new { type = "state", connected = true,
                        state = new { gainDb = gain } }, stop);
                }
                else if (cmd != "setOutputVolume") continue;
                commands.Enqueue(cmd);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        var model = new MainViewModel(client);
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        foreach (string operation in new[] { "saveProfile", "loadProfile", "missing" })
        {
            commands.Clear();
            var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnApplied() => applied.TrySetResult();
            model.StateApplied += OnApplied;
            try
            {
                model.GainDb = operation == "saveProfile" ? 45 : operation == "loadProfile" ? 60 : 65;
                if (operation == "saveProfile") model.SaveProfile("test");
                else model.LoadProfile(operation == "missing" ? "missing" : "test");
                // An unrelated slider provides a barrier for the next timer tick.
                model.OutputVolume = operation == "saveProfile" ? .2 : operation == "loadProfile" ? .3 : .4;
                await WaitFor(() => commands.Count >= 3);
                Assert.Equal(new[] { "set", operation == "saveProfile" ? "saveProfile" : "loadProfile", "setOutputVolume" }, commands);
                Assert.Equal(45, savedGain);
                Assert.Equal(operation == "missing" ? 65 : 45, gain);
                if (operation != "saveProfile")
                {
                    await applied.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    Assert.Equal(gain, model.GainDb);
                }
            }
            finally { model.StateApplied -= OnApplied; }
        }
    }

    private static async Task WaitFor(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(ready(), "Profile commands did not arrive.");
    }
}
