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
using ControlSocketListener = OpenXLR.Core.ControlSocketListener;
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
        Task pending = OfferRestartsTheServiceAndConnects();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!pending.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(pending.IsCompleted, "The older daemon restart check hung.");
        pending.GetAwaiter().GetResult();
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
    private static WebApplication Daemon(ControlSocketListener listener)
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
                await SocketTestServer.Send(socket, new { type = "state", connected = false, daemonVersion = AppVersion.Current,
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
