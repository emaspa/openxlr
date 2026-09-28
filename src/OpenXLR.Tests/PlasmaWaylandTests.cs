using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Microsoft.AspNetCore.Builder;
using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class PlasmaWaylandTests
{
    private MainWindow? _window;

    [PlasmaWaylandFact]
    public void PlasmaAndTheWindowShareRangesAcrossHidingAndRestoring()
    {
        // The runner supplies private settings, D-Bus and KWin sockets. Refuse
        // a bare opt-in against somebody's real desktop configuration.
        string root = Environment.GetEnvironmentVariable("OPENXLR_TEST_DESKTOP_ROOT") ?? "";
        Assert.True(Path.IsPathFullyQualified(root) && File.Exists(Path.Combine(root, "isolated-session")));
        Assert.Equal(Path.Combine(root, "config"), Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"));
        Assert.Equal(Path.Combine(root, "runtime"), Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"));
        Assert.Equal("wayland", Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"));
        Assert.False(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")));
        Assert.False(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")));
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // Use the application's shipped backend selection. Today this
                // is XWayland inside the private Wayland compositor.
                OpenXLR.UI.Program.BuildAvaloniaApp().SetupWithoutStarting();
                new UiSettings { MinimizeToTray = true, CheckForUpdates = false }.Save();
                Task check = Check(root);
                using var stop = new CancellationTokenSource();
                using var timeout = DispatcherTimer.RunOnce(stop.Cancel, TimeSpan.FromSeconds(90));
                _ = check.ContinueWith(_ => Dispatcher.UIThread.Post(stop.Cancel), TaskScheduler.Default);
                Dispatcher.UIThread.MainLoop(stop.Token);
                Assert.True(check.IsCompleted, "Plasma/Wayland acceptance timed out.");
                check.GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(root, "acceptance-passed"), "ok");
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                // Match application shutdown: stop dispatching after Quit,
                // instead of constructing another application on this dispatcher.
                try { _window?.Quit(); }
                catch (Exception ex) { failure ??= ex; }
            }
        }) { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(100)), "The desktop test thread did not exit.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private async Task Check(string root)
    {
        int phase = 0;
        string route = "/acceptance/" + Guid.NewGuid().ToString("N");
        var events = new ConcurrentDictionary<string, bool>();
        var commands = new ConcurrentQueue<JsonNode>();
        double output = .67;
        var levels = new ConcurrentDictionary<string, double>(new[] { new KeyValuePair<string, double>("monitor", .67), new("monitor2", .5) });
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            await SocketTestServer.Send(socket, new { type = "state", devices = Array.Empty<object>(),
                mixer = new { outputVolume = Volatile.Read(ref output), mixes = levels.OrderBy(pair => pair.Key)
                    .Select(pair => new { id = pair.Key, name = pair.Key, kind = "monitor", volume = pair.Value }).ToArray() } }, stop);
            while (!stop.IsCancellationRequested)
            {
                var command = await SocketTestServer.Receive(socket, stop);
                if (command["cmd"]?.GetValue<string>() == "setOutputVolume") Volatile.Write(ref output, command["value"]!.GetValue<double>());
                if (command["cmd"]?.GetValue<string>() == "setMixVolume") levels[command["mix"]!.GetValue<string>()] = command["value"]!.GetValue<double>();
                commands.Enqueue(command);
            }
        }, app =>
        {
            app.MapGet(route + "/phase", () => Volatile.Read(ref phase));
            app.MapPost(route + "/{name}", (string name) => { events[name] = true; return "ok"; });
        });
        using var stopQml = new CancellationTokenSource();
        Task<OpenXLR.UI.ProcessResult>? qml = null;
        MainWindow? window = null;
        DaemonClient? client = null;
        try
        {
            client = new DaemonClient(server.Url);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SliderSync.ReleaseTouchGuards();
                _window = window = new MainWindow(client, syncDesktopVolume: true);
                window.Show();
                Assert.Equal("XID", window.TryGetPlatformHandle()!.HandleDescriptor);
            });
            await Wait(() => ((MainViewModel)window!.DataContext!).Mixes.Count == 2);
            var vm = await Dispatcher.UIThread.InvokeAsync(() => (MainViewModel)window!.DataContext!);
            var slider = await Dispatcher.UIThread.InvokeAsync(() => window!.FindControl<Slider>("OutputVolumeSlider")!);
            string fixture = Path.Combine(root, "tst_plasma_volume.qml");
            string template = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tst_plasma_volume.qml"));
            File.WriteAllText(fixture, template.Replace("@ENDPOINT@", server.Url.Replace("ws:", "http:").Replace("/ws", route)));
            qml = OpenXLR.UI.ProcessRunner.RunAsync(Environment.GetEnvironmentVariable("OPENXLR_TEST_QML_RUNNER")!,
                ["-platform", "wayland", "-input", fixture], TimeSpan.FromSeconds(75), cancel: stopQml.Token,
                environment: new Dictionary<string, string> { ["QT_QUICK_BACKEND"] = "software" });
            await Wait(() => events.ContainsKey("ready"));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Assert.Equal(.67, slider.Value);
                Assert.Equal("67%", vm.OutputVolumeText);
                window!.FindControl<ToggleButton>("OutputBoost")!.IsChecked = true;
            });
            await Wait(() => events.ContainsKey("enabled") && slider.Maximum == 1.5);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Assert.Equal(.67, slider.Value);
                Assert.Equal("67%", vm.OutputVolumeText);
                slider.Value = 1.5;
                Assert.Equal("150%", vm.OutputVolumeText);
                vm.Mixes[0].Volume = 1.2;
            });
            await Wait(() => Volatile.Read(ref output) == 1.5 && levels["monitor"] == 1.2);
            Volatile.Write(ref phase, 2);
            await Wait(() => events.ContainsKey("disabled") && slider.Maximum == 1 && Volatile.Read(ref output) == 1 && levels["monitor"] == 1);
            Assert.Equal(.5, levels["monitor2"]);
            Volatile.Write(ref phase, 3);
            await Wait(() => events.ContainsKey("reenabled") && slider.Maximum == 1.5);
            await Dispatcher.UIThread.InvokeAsync(() => { Assert.Equal(1, slider.Value); slider.Value = .67; window!.Close(); });
            await Wait(() => !window!.IsVisible && Volatile.Read(ref output) == .67);
            Volatile.Write(ref phase, 4);
            await Wait(() => events.ContainsKey("hidden-disabled") && slider.Maximum == 1);
            await Dispatcher.UIThread.InvokeAsync(() => { window!.ShowMixer(); Assert.Equal(.67, slider.Value); });
            Volatile.Write(ref phase, 5);
            await Wait(() => events.ContainsKey("reopen-enabled") && slider.Maximum == 1.5);
            await Dispatcher.UIThread.InvokeAsync(() => { window!.Close(); });
            await Wait(() => !window!.IsVisible);
            await Dispatcher.UIThread.InvokeAsync(() => window!.ShowMixer());
            await Dispatcher.UIThread.InvokeAsync(() => Assert.Equal(.67, window!.FindControl<Slider>("OutputVolumeSlider")!.Value));
            Volatile.Write(ref phase, 6);
            var result = await qml;
            Assert.True(result.Ok, result.StdoutText + "\n" + result.Stderr);
            Assert.Contains("Totals: 3 passed, 0 failed", result.StdoutText);
            Assert.Contains(commands, c => c["cmd"]?.GetValue<string>() == "setOutputVolume" && c["value"]?.GetValue<double>() == 1.5);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(root, "failure.log"), ex.ToString());
            throw;
        }
        finally
        {
            stopQml.Cancel();
            if (qml is not null)
            {
                var result = await qml;
                File.WriteAllText(Path.Combine(root, "qml.log"), result.StdoutText + "\n" + result.Stderr);
            }
            if (client is not null) await client.DisposeAsync();
        }
    }

    private static async Task Wait(Func<bool> ready, [CallerArgumentExpression(nameof(ready))] string? condition = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await Dispatcher.UIThread.InvokeAsync(ready) && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(await Dispatcher.UIThread.InvokeAsync(ready), "Plasma and OpenXLR did not converge: " + condition);
    }
}

internal sealed class PlasmaWaylandFactAttribute : FactAttribute
{
    public PlasmaWaylandFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENXLR_TEST_PLASMA_WAYLAND") != "1")
            Skip = "Run tools/test-plasma-volume.py for an isolated Plasma/Wayland session.";
    }
}
