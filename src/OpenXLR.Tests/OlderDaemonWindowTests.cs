using System.Net.WebSockets;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OpenXLR.Core;
using OpenXLR.Daemon;
using OpenXLR.UI;
using OpenXLR.UI.Localization;
using Presence = OpenXLR.UI.DaemonPresence;

namespace OpenXLR.Tests;

/// <summary>
/// After an upgrade the previous release's daemon keeps running on its old
/// port. The UI must offer the restart in its version banner, run the same
/// restart as the header button, and connect once the new daemon's socket
/// appears. Runs inside WindowLayoutTests, which owns the UI thread and
/// points XDG_RUNTIME_DIR at a private directory.
/// </summary>
internal static class OlderDaemonWindowTests
{
    internal static void Check()
    {
        Task pending = CheckAll();
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!pending.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(pending.IsCompleted, "The older daemon restart check hung.");
        pending.GetAwaiter().GetResult();
    }

    private static async Task CheckAll()
    {
        await OfferRestartsTheServiceAndConnects();
        await UpgradeQuestionLeavesAudioAloneUntilRestart();
        var owner = new Window { Width = 400, Height = 200 };
        owner.Show();
        try
        {
            foreach (int answer in new[] { 0, 1, 2 })
            {
                void Answer(Window dialog)
                {
                    dialog.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
                    {
                        var buttons = dialog.GetVisualDescendants().OfType<Button>().ToArray();
                        var later = buttons.Single(button => Equals(button.Content, Localizer.Text("Later")));
                        Assert.True(later.IsCancel);
                        Assert.True(later.IsDefault);
                        if (answer == 0) dialog.Close();
                        else buttons.Single(button => Equals(button.Content,
                            Localizer.Text(answer == 1 ? "Later" : "Restart")))
                            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    });
                }
                Dialogs.ConfirmOpening += Answer;
                try
                {
                    bool accepted = await Dialogs.ConfirmAsync(owner, Localizer.Text("DaemonUpgradeTitle"),
                        Localizer.Text("DaemonUpgradeQuestion"), Localizer.Text("Restart"), Localizer.Text("Later"));
                    Assert.Equal(answer == 2, accepted);
                }
                finally { Dialogs.ConfirmOpening -= Answer; }
            }
        }
        finally { owner.Close(); }
    }

    private static async Task UpgradeQuestionLeavesAudioAloneUntilRestart()
    {
        string bin = Directory.CreateTempSubdirectory("ox-upgrade-window-").FullName;
        string log = Path.Combine(bin, "restarts");
        string? previousPath = Environment.GetEnvironmentVariable("PATH");
        string? previousFlatpak = Environment.GetEnvironmentVariable("FLATPAK_ID");
        ExecutableScript.Write(Path.Combine(bin, "systemctl"), $$"""
            #!/bin/sh
            case "$*" in
              '--user show --property=MainPID --value openxlr-daemon.service') echo {{Environment.ProcessId}} ;;
              '--user --job-mode=fail try-restart openxlr-daemon.service')
                echo restart >> {{InstallationDoctor.Quote(log)}}
                exit 1 ;;
              *) exit 99 ;;
            esac
            """);
        Environment.SetEnvironmentVariable("PATH", bin);
        Environment.SetEnvironmentVariable("FLATPAK_ID", null);
        using var listener = ControlSocketListener.Bind();
        await using var daemon = Daemon(listener, "0.0.1");
        try
        {
            await daemon.StartAsync();
            foreach (bool accept in new[] { false, true })
            {
                foreach (string marker in Directory.GetFiles(OpenXLR.UI.OpenXlrPaths.RuntimeDir, "upgrade-prompt-*")) File.Delete(marker);
                await using var client = new DaemonClient();
                var window = new MainWindow(client);
                window.EnableDaemonUpgradePrompt();
                int questions = 0;
                void Answer(Window dialog)
                {
                    if (dialog.Title != Localizer.Text("DaemonUpgradeTitle")) return;
                    questions++;
                    dialog.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
                    {
                        Assert.False(File.Exists(log));
                        if (!accept) dialog.Close();
                        else dialog.GetVisualDescendants().OfType<Button>()
                            .Single(button => Equals(button.Content, Localizer.Text("Restart")))
                            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    });
                }
                Dialogs.ConfirmOpening += Answer;
                try
                {
                    window.Show();
                    await WaitFor(() => questions == 1, "The upgrade question did not appear.");
                    var vm = (MainViewModel)window.DataContext!;
                    if (accept)
                    {
                        await WaitFor(() => vm.DaemonRestart.Status == Localizer.Text("RestartFailed"), "The restart failure was not shown.");
                        Assert.Equal(["restart"], File.ReadAllLines(log));
                        Assert.True(window.IsVisible);
                    }
                    else
                    {
                        await Task.Delay(100);
                        Assert.False(File.Exists(log));
                        Assert.True(vm.DaemonConnected);
                    }
                    Assert.Equal(1, questions);
                }
                finally
                {
                    Dialogs.ConfirmOpening -= Answer;
                    window.Quit();
                }
            }
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await daemon.StopAsync(stop.Token);
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Environment.SetEnvironmentVariable("FLATPAK_ID", previousFlatpak);
            Directory.Delete(bin, true);
        }
    }

    private static async Task OfferRestartsTheServiceAndConnects()
    {
        string bin = Directory.CreateTempSubdirectory("openxlr-older-daemon-").FullName;
        string log = Path.Combine(bin, "calls");
        string? previousPath = Environment.GetEnvironmentVariable("PATH");
        string? previousFlatpak = Environment.GetEnvironmentVariable("FLATPAK_ID");
        ExecutableScript.Write(Path.Combine(bin, "systemctl"),
            "#!/bin/sh\nprintf '%s\\n' \"$*\" >> " + InstallationDoctor.Quote(log) + "\nexit 0\n");
        Environment.SetEnvironmentVariable("PATH", bin);
        Environment.SetEnvironmentVariable("FLATPAK_ID", null);
        bool Restarted() => File.Exists(log) && File.ReadAllText(log).Contains("restart openxlr-daemon.service", StringComparison.Ordinal);
        // The probe stands in for the lock and the old port: an older daemon
        // until the service is restarted, then none until the socket appears.
        var client = new DaemonClient(null, _ => Task.FromResult(Restarted() ? Presence.NotRunning : Presence.OlderDaemon));
        var window = new MainWindow(client);
        ControlSocketListener? listener = null;
        WebApplication? daemon = null;
        try
        {
            window.Show();
            var vm = (MainViewModel)window.DataContext!;
            await WaitFor(() => vm.DaemonVersionMismatch, "The restart offer did not appear.");
            string expected = Localizer.Format("OlderDaemonCannotConnectNote", AppVersion.Current);
            Dispatcher.UIThread.RunJobs();
            var note = window.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == expected);
            Assert.True(note.IsEffectivelyVisible);
            Assert.Equal(Localizer.Text("OlderDaemonRunning"), vm.StatusLine);
            Assert.Equal(Localizer.Text("OlderDaemonRunningSentence"), vm.MixerPlaceholder);

            // The banner's own button, beside the note.
            var restart = ((DockPanel)note.Parent!).Children.OfType<Button>().Single();
            Assert.Equal(Localizer.Text("RestartDaemon"), restart.Content);
            restart.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(Restarted, "The banner did not restart the user service.");
            Assert.Equal(["--user reset-failed openxlr-daemon.service", "--user restart openxlr-daemon.service"], File.ReadAllLines(log));

            // The restarted daemon binds its socket and the UI connects to it.
            listener = ControlSocketListener.Bind();
            daemon = Daemon(listener);
            await daemon.StartAsync();
            await WaitFor(() => vm.DaemonConnected && !vm.DaemonVersionMismatch, "The UI did not connect to the new socket.");
            Dispatcher.UIThread.RunJobs();
            Assert.False(note.IsEffectivelyVisible);
            Assert.Null(vm.DaemonRestart.Status);
        }
        finally
        {
            window.Close();
            await client.DisposeAsync();
            if (daemon is not null)
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await daemon.StopAsync(stop.Token);
                await daemon.DisposeAsync();
            }
            listener?.Dispose();
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Environment.SetEnvironmentVariable("FLATPAK_ID", previousFlatpak);
            Directory.Delete(bin, recursive: true);
        }
    }

    /// <summary>A daemon of this build on the control socket: one state, then it reads until closed.</summary>
    private static WebApplication Daemon(ControlSocketListener listener, string? version = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        ControlApi.Configure(builder, listener);
        var app = builder.Build();
        app.UseWebSockets();
        app.Map("/ws", async (HttpContext context) =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            CancellationToken stop = app.Lifetime.ApplicationStopping;
            try
            {
                await SocketTestServer.Send(socket, new { type = "state", connected = false, daemonVersion = version ?? AppVersion.Current,
                    profiles = Array.Empty<object>(), detected = Array.Empty<object>(), devices = Array.Empty<object>(),
                    mixer = new { channels = Array.Empty<object>(), mixes = Array.Empty<object>(), inserts = new { } } }, stop);
                while (true) await SocketTestServer.Receive(socket, stop);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
        });
        return app;
    }

    private static async Task WaitFor(Func<bool> ready, string failure)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(ready(), failure);
    }
}
