using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;

namespace OpenXLR.Tests;

internal static class SoundCheckWindowTests
{
    // Use the existing layout test's real X11 application and dispatcher.
    internal static void CheckPendingClose()
    {
        Task check = Verify();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!check.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(check.IsCompleted, "Sound Check close test hung.");
        check.GetAwaiter().GetResult();
    }

    private static async Task Verify()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                if (command["cmd"]!.GetValue<string>() == "auth") continue;
                Assert.Equal("stop", command["action"]!.GetValue<string>());
                received.TrySetResult();
                await release.Task.WaitAsync(stop);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>() }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var window = new SoundCheckWindow { DataContext = new SoundCheckViewModel(client, "xlr1") };
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        try
        {
            window.Show();
            window.Close();
            await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(window.IsVisible);
            Assert.False(window.IsEnabled);
            Assert.All(window.GetVisualDescendants().OfType<Button>(), button => Assert.False(button.IsEffectivelyEnabled));
            release.TrySetResult();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(window.IsVisible);
        }
        finally { release.TrySetResult(); window.Close(); }
    }
}
