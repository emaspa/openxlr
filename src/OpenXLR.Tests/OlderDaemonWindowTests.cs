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
/// After an upgrade the UI restarts a known older daemon automatically, or
/// offers the manual restart when the old port cannot report its version.
/// Runs inside WindowLayoutTests, with a private runtime directory.
/// </summary>
internal static class OlderDaemonWindowTests
{
    internal static void Check()
    {
        Check(automatic: false);
        Check(automatic: true);
    }

    private static void Check(bool automatic)
    {
        Task pending = RestartAndConnect(automatic);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!pending.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(pending.IsCompleted, "The older daemon restart check hung.");
        pending.GetAwaiter().GetResult();
    }

    private static async Task RestartAndConnect(bool automatic)
    {
        string bin = Directory.CreateTempSubdirectory("openxlr-older-daemon-").FullName;
        string log = Path.Combine(bin, "calls");
        string? previousPath = Environment.GetEnvironmentVariable("PATH");
        string? previousFlatpak = Environment.GetEnvironmentVariable("FLATPAK_ID");
        ExecutableScript.Write(Path.Combine(bin, "systemctl"),
            "#!/bin/sh\nprintf '%s\\n' \"$*\" >> " + InstallationDoctor.Quote(log) + "\nexit 0\n");
        Environment.SetEnvironmentVariable("PATH", bin);
        Environment.SetEnvironmentVariable("FLATPAK_ID", null);
        string restartVerb = automatic ? "try-restart" : "restart";
        bool Restarted() => File.Exists(log) &&
            File.ReadAllLines(log).Contains($"--user {restartVerb} openxlr-daemon.service");
        // For the legacy case the probe stands in for the old loopback port.
        var client = new DaemonClient(null, _ =>
            Task.FromResult(automatic || Restarted() ? Presence.NotRunning : Presence.OlderDaemon));
        var window = new MainWindow(client);
        ControlSocketListener? listener = null;
        WebApplication? daemon = null;
        try
        {
            window.EnableAutomaticDaemonRestart();
            if (automatic)
            {
                listener = ControlSocketListener.Bind();
                daemon = Daemon(listener, "0.1.0");
                await daemon.StartAsync();
            }
            window.Show();
            var vm = (MainViewModel)window.DataContext!;
            await WaitFor(() => vm.DaemonVersionMismatch, "The restart offer did not appear.");
            string expected = automatic
                ? Localizer.Format("DaemonVersionNote", "0.1.0", AppVersion.Current)
                : Localizer.Format("OlderDaemonCannotConnectNote", AppVersion.Current);
            Dispatcher.UIThread.RunJobs();
            var note = window.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == expected);
            Assert.True(note.IsEffectivelyVisible);

            if (!automatic)
            {
                Assert.Equal(Localizer.Text("OlderDaemonRunning"), vm.StatusLine);
                Assert.Equal(Localizer.Text("OlderDaemonRunningSentence"), vm.MixerPlaceholder);
                Assert.False(File.Exists(log));
                var restart = ((DockPanel)note.Parent!).Children.OfType<Button>().Single();
                Assert.Equal(Localizer.Text("RestartDaemon"), restart.Content);
                restart.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }

            await WaitFor(() => Restarted() && vm.DaemonRestart.CanRestart, "The service did not restart.");
            Assert.Equal(automatic
                ? ["--user is-active --quiet openxlr-daemon.service", "--user try-restart openxlr-daemon.service"]
                : ["--user reset-failed openxlr-daemon.service", "--user restart openxlr-daemon.service"], File.ReadAllLines(log));

            if (daemon is not null) await StopDaemon(daemon);
            daemon = null;
            listener?.Dispose();
            listener = ControlSocketListener.Bind();
            daemon = Daemon(listener, AppVersion.Current);
            await daemon.StartAsync();
            await WaitFor(() => vm.DaemonConnected && !vm.DaemonVersionMismatch, "The UI did not connect to the new socket.");
            Dispatcher.UIThread.RunJobs();
            Assert.False(note.IsEffectivelyVisible);
            Assert.Null(vm.DaemonRestart.Status);
            Assert.Equal(2, File.ReadAllLines(log).Length);
        }
        finally
        {
            window.Close();
            await client.DisposeAsync();
            if (daemon is not null) await StopDaemon(daemon);
            listener?.Dispose();
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Environment.SetEnvironmentVariable("FLATPAK_ID", previousFlatpak);
            Directory.Delete(bin, recursive: true);
        }
    }

    private static async Task StopDaemon(WebApplication daemon)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await daemon.StopAsync(stop.Token);
        await daemon.DisposeAsync();
    }

    /// <summary>One state on the control socket, then it reads until closed.</summary>
    private static WebApplication Daemon(ControlSocketListener listener, string version)
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
                await SocketTestServer.Send(socket, new { type = "state", connected = false, daemonVersion = version,
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
