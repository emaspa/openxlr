using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OpenXLR.UI;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace OpenXLR.Tests;

internal static class UserMonitorWindowTests
{
    internal static void Check()
    {
        Task pending = CheckEditor();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!pending.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(pending.IsCompleted, "Mix editor check hung.");
        pending.GetAwaiter().GetResult();
    }

    private static async Task CheckEditor()
    {
        var commands = new ConcurrentQueue<JsonNode>();
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            await SocketTestServer.Send(socket, State("virtualMic"), stop);
            while (!stop.IsCancellationRequested)
            {
                JsonNode command = await SocketTestServer.Receive(socket, stop);
                if (command["cmd"]!.GetValue<string>() != "createMix") continue;
                commands.Enqueue(command);
                string kind = command["kind"]!.GetValue<string>();
                await SocketTestServer.Send(socket, State(kind), stop);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>(), error = (string?)null }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        var model = new MainViewModel(client);
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var readyDeadline = DateTime.UtcNow.AddSeconds(3);
        while (!model.CanEditLayout && DateTime.UtcNow < readyDeadline) await Task.Delay(10);
        Assert.True(model.CanEditLayout);
        var editor = new MixerSetupWindow { DataContext = model };
        editor.Show();
        try
        {
            var name = editor.FindControl<TextBox>("MixName")!;
            var kind = editor.FindControl<ComboBox>("NewMixKind")!;
            var add = editor.FindControl<Button>("AddMix")!;
            foreach (int selected in new[] { 0, 1 })
            {
                var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnApplied() => applied.TrySetResult();
                model.StateApplied += OnApplied;
                kind.SelectedIndex = selected;
                name.Text = "New mix";
                Assert.True(add.IsEnabled);
                add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (name.Text != "" && DateTime.UtcNow < deadline) await Task.Delay(10);
                Assert.Equal("", name.Text);
                await applied.Task.WaitAsync(TimeSpan.FromSeconds(3));
                model.StateApplied -= OnApplied;
                Assert.Equal(selected == 0 ? "virtualMic" : "monitor", commands.Last()["kind"]!.GetValue<string>());
                Assert.True(model.Mixes.Single(m => m.Id == "newmix").IsEditable);
                Assert.False(model.Mixes.Single(m => m.Id == "monitor").IsEditable);
                Assert.Equal(new[] { "monitor", "monitor2", "monitor+monitor2", "newmix" },
                    model.MonitorOutputs.Single().Feeds.Select(f => f.Id));
                Assert.Equal(selected == 1, model.Mixes.Single(m => m.Id == "newmix").IsMonitor);
            }
        }
        finally { editor.Close(); }
    }

    private static object State(string kind) => new { type = "state", devices = new[] { new { kind = 0, name = "output" } },
                    mixer = new { mixes = new[] {
                        new { id = "monitor", name = "Monitor A", kind = "monitor", editable = false },
                        new { id = "monitor2", name = "Monitor B", kind = "monitor", editable = false },
                        new { id = "newmix", name = "New mix", kind, editable = true } }, channels = Array.Empty<object>() } };
}
