using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

/// <summary>The header follows daemon state, and each saved profile's row confirms overwrites and deletion.</summary>
internal static class ProfileRowWindowTests
{
    internal static void Check()
    {
        Task pending = CheckRows();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!pending.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(pending.IsCompleted, "Profile row check hung.");
        pending.GetAwaiter().GetResult();
    }

    private static async Task CheckRows()
    {
        var commands = new ConcurrentQueue<string>();
        var accepted = new TaskCompletionSource<WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            accepted.TrySetResult(socket);
            while (!stop.IsCancellationRequested)
            {
                JsonNode command = await SocketTestServer.Receive(socket, stop);
                string cmd = command["cmd"]!.GetValue<string>();
                if (cmd is "saveProfile" or "deleteProfile")
                    commands.Enqueue($"{cmd} {command["name"]!.GetValue<string>()}");
            }
        });
        var client = new DaemonClient(server.Url);
        var main = new MainWindow(client);
        Window? dialog = null;
        void OnConfirm(Window window) => dialog = window;
        Dialogs.ConfirmOpening += OnConfirm;
        try
        {
            WebSocket socket = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            main.Show();
            var marker = main.FindControl<TextBlock>("ActiveProfileLabel")!;
            Assert.False(marker.IsVisible);
            await SocketTestServer.Send(socket, new { type = "state", profiles = new[] { "main", "main9" },
                activeProfile = "main", recallOnConnect = "main9" }, CancellationToken.None);
            await WaitFor(() => marker.IsVisible);
            Assert.Equal(Localizer.Format("ActiveProfileLabel", "main"), marker.Text);

            var profiles = main.FindControl<DropDownButton>("ProfilesButton")!;
            var flyout = (Flyout)profiles.Flyout!;
            flyout.ShowAt(profiles);
            Dispatcher.UIThread.RunJobs();
            var content = (Control)flyout.Content!;

            // Cancelling the question leaves the profile alone; accepting deletes it.
            Assert.Equal(Localizer.Text("DeleteProfileTitle"), await Answer(Row(content, "main9", "DeleteThisProfile"), accept: false));
            Assert.Contains("main9", Message(dialog!));
            Assert.Equal(Localizer.Text("DeleteProfileTitle"), await Answer(Row(content, "main9", "DeleteThisProfile"), accept: true));
            await WaitFor(() => commands.Count >= 1);
            Assert.Equal(["deleteProfile main9"], commands);

            // A row's Save needs no typed name and asks the same question a typed name does.
            commands.Clear();
            Button save = Row(content, "main", "SaveOverThisProfile");
            Assert.Equal(Localizer.Text("Save"), save.Content);
            Assert.Equal(Localizer.Text("OverwriteProfileTitle"), await Answer(save, accept: false));
            Assert.Equal(Localizer.Text("OverwriteProfileTitle"), await Answer(save, accept: true));
            await WaitFor(() => commands.Count >= 1);
            Assert.Equal(["saveProfile main"], commands);
            flyout.Hide();

            // The daemon omits activeProfile after the active profile is deleted.
            await SocketTestServer.Send(socket, new { type = "state", profiles = Array.Empty<string>() }, CancellationToken.None);
            await WaitFor(() => !marker.IsVisible);

            // An external recall can change the marker without a click in this window.
            string longName = new('W', 60);
            main.Width = 520;
            await SocketTestServer.Send(socket, new { type = "state", profiles = new[] { longName },
                activeProfile = longName }, CancellationToken.None);
            await WaitFor(() => marker.IsVisible && marker.Text == Localizer.Format("ActiveProfileLabel", longName));
            main.UpdateLayout();
            WindowLayoutTests.AssertInside(marker, main);
            Assert.Equal(longName, ToolTip.GetTip(marker));

            socket.Abort();
            await WaitFor(() => !marker.IsVisible);
        }
        finally
        {
            Dialogs.ConfirmOpening -= OnConfirm;
            main.Quit();
            await client.DisposeAsync();
        }

        async Task<string> Answer(Button button, bool accept)
        {
            dialog = null;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (dialog is null && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.NotNull(dialog);
            Window shown = dialog!;
            while (!shown.IsVisible && DateTime.UtcNow < deadline) await Task.Delay(10);
            var choice = shown.GetLogicalDescendants().OfType<Button>()
                .Single(b => accept ? b.Classes.Contains("danger") : b.IsCancel);
            choice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(50);
            return shown.Title!;
        }
    }

    private static Button Row(Control content, string profile, string tipKey)
        => content.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Tag as string == profile && ToolTip.GetTip(b) as string == Localizer.Text(tipKey));

    private static string Message(Window dialog)
        => dialog.GetLogicalDescendants().OfType<TextBlock>().First().Text!;

    private static async Task WaitFor(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(ready(), "The profile window or socket did not reach the expected state.");
    }
}
