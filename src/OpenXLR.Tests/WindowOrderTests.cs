using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Skinning;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class WindowOrderTests
{
    private static readonly string[] Sections = ["InputsTile", "HeadphonesTile", "MonitorTile", "ApplicationsTile", "SubmixerTile"];

    [WindowOrderFact]
    public async Task RealDraggingPreservesSkinsControlsAndSavedOrder()
    {
        var commands = new ConcurrentQueue<JsonNode>();
        using var rejectGate = new SemaphoreSlim(0);
        int rejectNext = 0;
        JsonNode state = JsonNode.Parse("""
            {"type":"state","mixer":{"channels":[
            {"id":"xlr1","name":"XLR 1","hardware":true},
            {"id":"game","name":"Game"},{"id":"music","name":"Music"}],
            "mixes":[{"id":"monitor","name":"Monitor A","kind":"monitor"},
            {"id":"monitor2","name":"Monitor B","kind":"monitor"}],"inserts":{}}}
            """)!;
        state["daemonVersion"] = AppVersion.Current;
        for (int i = 0; i < 12; i++)
            state["mixer"]!["channels"]!.AsArray().Add(new JsonObject { ["id"] = "extra" + i, ["name"] = "Extra " + i });
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            await SocketTestServer.Receive(socket, stop); // authentication frame
            await SocketTestServer.Send(socket, state, stop);
            while (!stop.IsCancellationRequested)
            {
                JsonNode command = await SocketTestServer.Receive(socket, stop);
                if (command["cmd"]!.GetValue<string>() == "listPlugins")
                {
                    await SocketTestServer.Send(socket, new { type = "plugins", plugins = Array.Empty<object>() }, stop);
                    await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>() }, stop);
                    continue;
                }
                commands.Enqueue(command);
                if (command["cmd"]!.GetValue<string>() != "setDisplayOrder") continue;
                if (Interlocked.Exchange(ref rejectNext, 0) == 1)
                {
                    await rejectGate.WaitAsync(stop);
                    await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>(), error = "disk full" }, stop);
                    continue;
                }
                foreach (string field in new[] { "channels", "mixes" })
                {
                    var byId = state["mixer"]![field]!.AsArray().ToDictionary(n => n!["id"]!.GetValue<string>());
                    state["mixer"]![field] = new JsonArray(command[field]!.AsArray()
                        .Select(id => byId[id!.GetValue<string>()]!.DeepClone()).ToArray());
                }
                await SocketTestServer.Send(socket, state, stop);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>() }, stop);
            }
        });
        string config = Directory.CreateTempSubdirectory("openxlr-order-").FullName;
        string? oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string? oldRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        string? oldBus = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        MainWindow? main = null;
        Exception? failure = null;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var quit = new CancellationTokenSource();
        var thread = new Thread(() =>
        {
            try
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
                Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", config);
                Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", "unix:path=/nonexistent");
                new UiSettings { CollapsedSections = Sections, CheckForUpdates = false }.Save();
                AppBuilder.Configure<App>().UseSkia().UseHarfBuzz().UseX11().SetupWithoutStarting();
                main = Open();
                ready.TrySetResult();
                Dispatcher.UIThread.MainLoop(quit.Token);
            }
            catch (Exception ex) { failure = ex; ready.TrySetResult(); }
            finally
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", oldConfig);
                Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", oldRuntime);
                Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", oldBus);
            }
        }) { IsBackground = true };
        thread.Start();
        Assert.True(ready.Task.Wait(TimeSpan.FromSeconds(30)));
        if (failure is not null) throw failure;
        using var pointer = new XPointer();
        try
        {
            Wait(() => Ui(() => ((MainViewModel)main!.DataContext!).CanEditLayout));
            Click(Ui(() => main!.FindControl<ToggleButton>("ArrangeButton")!));
            Wait(() => Ui(() => Handle("SubmixerTile").IsEffectivelyVisible));
            Border original = Ui(() => (Border)main!.FindControl<Expander>("InputsTile")!.Parent!);
            Drag("SubmixerTile", "InputsTile", false);
            Wait(() => Ui(() => Order()[0] == "SubmixerTile"));
            Drag("MonitorTile", "ApplicationsTile", true);
            Wait(() => Ui(() => Order()[^1] == "MonitorTile"));
            Assert.Equal(Ui(Order), Ui(() => UiSettings.Load().SectionOrder));
            Assert.All(Sections, id => Assert.False(Ui(() => main!.FindControl<Expander>(id)!.IsExpanded)));

            // A click on the grip, a cancelled drag, and release outside the
            // window must not toggle, save, or reorder the tile.
            string[] before = Ui(Order);
            Click(Handle("InputsTile"));
            Assert.False(Ui(() => main!.FindControl<Expander>("InputsTile")!.IsExpanded));
            Drag("InputsTile", "SubmixerTile", false, cancel: true);
            Assert.Equal(before, Ui(Order));
            Move(Center(Handle("InputsTile"))); pointer.SetButton(true);
            Thread.Sleep(50); pointer.MoveTo(2000, 1300); Thread.Sleep(100); pointer.SetButton(false);
            Thread.Sleep(100); Assert.Equal(before, Ui(Order));

            // The keyboard moves the same tile, including across collapsed sections.
            Click(Handle("InputsTile")); pointer.Key(0xff54); // Down
            Wait(() => Ui(() => Order()[2] == "InputsTile"));
            before = Ui(Order);
            string path = Path.Combine(config, "openxlr", "ui.json");
            File.Move(path, path + ".saved"); Directory.CreateDirectory(path);
            try
            {
                Drag("InputsTile", "SubmixerTile", false);
                Wait(() => Ui(() => main!.FindControl<TextBlock>("ArrangementNote")!.Text!.Contains("Could not save")));
                Assert.Equal(before, Ui(Order));
            }
            finally { Directory.Delete(path); File.Move(path + ".saved", path); }

            string? originalBackground = Ui(() => original.Background?.ToString());
            Ui(() => { Assert.Empty(SkinService.Choose("opendeck")); return true; });
            Assert.NotEqual(originalBackground, Ui(() => original.Background?.ToString()));
            Assert.Same(original, Ui(() => main!.FindControl<Expander>("InputsTile")!.Parent));
            Assert.Equal(before, Ui(Order));
            Assert.Equal("opendeck", Ui(() => UiSettings.Load().Skin));
            Drag("MonitorTile", "InputsTile", false);
            Wait(() => Ui(() => Order()[2] == "MonitorTile"));
            Assert.Equal("opendeck", Ui(() => UiSettings.Load().Skin));
            string[] saved = Ui(Order);
            Ui(() => { main!.Close(); main = Open(); return true; });
            Wait(() => Ui(() => ((MainViewModel)main!.DataContext!).CanEditLayout));
            Assert.Equal(saved, Ui(Order));
            Assert.False(Ui(() => main!.FindControl<ToggleButton>("ArrangeButton")!.IsChecked == true));
            Click(Ui(() => main!.FindControl<ToggleButton>("ArrangeButton")!));
            Ui(() => { main!.FindControl<Expander>("SubmixerTile")!.IsExpanded = true; return true; });
            Wait(() => Ui(() => Handle("music").IsEffectivelyVisible));
            var music = Ui(() => ((MainViewModel)main!.DataContext!).Channels.Single(c => c.Id == "music"));
            Volatile.Write(ref rejectNext, 1);
            Drag("music", "xlr1", false);
            Wait(() => commands.Count == 1);
            Assert.Equal("xlr1", Ui(() => ((MainViewModel)main!.DataContext!).Channels[0].Id));
            Drag("game", "xlr1", false); // another drop while saving must not race the first
            rejectGate.Release();
            Wait(() => Ui(() => main!.FindControl<TextBlock>("ArrangementNote")!.Text == "disk full"));
            Assert.Single(commands);
            Assert.Equal("xlr1", Ui(() => ((MainViewModel)main!.DataContext!).Channels[0].Id));
            Drag("music", "xlr1", false);
            Wait(() => Ui(() => ((MainViewModel)main!.DataContext!).Channels[0].Id == "music"));
            Assert.Same(music, Ui(() => ((MainViewModel)main!.DataContext!).Channels[0]));
            Drag("monitor2", "monitor", false);
            Wait(() => Ui(() => ((MainViewModel)main!.DataContext!).Mixes[0].Id == "monitor2"));
            Assert.All(commands, c => Assert.Equal("setDisplayOrder", c["cmd"]!.GetValue<string>()));
            Assert.Equal(3, commands.Count);
            Drag("music", "monitor", false);
            Assert.Equal(3, commands.Count); // a channel cannot be dropped into the mix row
            Assert.Equal("music", Ui(() => ((MainViewModel)main!.DataContext!).Channels[0].Id));

            // Hold at the horizontal viewport edge, then cancel. The timer
            // scrolls only while dragging and never sends an ordering command.
            var scroll = Ui(() => Handle("music").GetVisualAncestors().OfType<ScrollViewer>().First());
            Move(Center(Handle("music"))); pointer.SetButton(true); Thread.Sleep(50);
            PixelPoint edge = Ui(() => scroll.PointToScreen(new Point(scroll.Bounds.Width - 5, 40)));
            Move(edge);
            Wait(() => Ui(() => scroll.Offset.X > 30));
            pointer.Key(0xff1b); pointer.SetButton(false); Thread.Sleep(100);
            double offset = Ui(() => scroll.Offset.X);
            Thread.Sleep(150); Assert.Equal(offset, Ui(() => scroll.Offset.X));
            Assert.Equal(3, commands.Count);
            Ui(() => { Assert.Empty(SkinService.Choose("material")); return true; });
            Assert.Equal(saved, Ui(Order));
            Click(Ui(() => main!.FindControl<Button>("ResetSectionsButton")!));
            Wait(() => Ui(() => Order().SequenceEqual(Sections)));
            Assert.Null(Ui(() => UiSettings.Load().Skin));
            Ui(() => { main!.Width = 640; return true; });
            Wait(() => Ui(() => main!.ClientSize.Width == 640));
            Thread.Sleep(100);
            Ui(() =>
            {
                foreach (string id in Sections)
                {
                    var header = (StackPanel)main!.FindControl<Expander>(id)!.Header!;
                    foreach (Control control in header.Children.Where(c => c.IsEffectivelyVisible))
                    {
                        Point at = control.TranslatePoint(default, main!)!.Value;
                        Assert.InRange(at.X + control.Bounds.Width, 0, main!.ClientSize.Width);
                    }
                }
                return true;
            });
        }
        finally
        {
            pointer.SetButton(false);
            Ui(() => { main?.Close(); quit.Cancel(); return true; });
            thread.Join(TimeSpan.FromSeconds(15));
            Directory.Delete(config, true);
        }

        MainWindow Open()
        {
            var window = new MainWindow(new DaemonClient(server.Url))
            { Width = 1040, Height = 1000, Position = new PixelPoint(30, 30), WindowStartupLocation = WindowStartupLocation.Manual };
            window.Show();
            return window;
        }
        string[] Order() => main!.FindControl<StackPanel>("SectionCards")!.Children.OfType<Border>()
            .Select(b => ((Expander)b.Child!).Name!).ToArray();
        Button Handle(string id) => Ui(() => main!.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Classes.Contains("reorderHandle") && (b.Tag switch
            { string name => name, ChannelViewModel c => c.Id, MixViewModel m => m.Id, _ => "" }) == id));
        PixelPoint Center(Control c) => Ui(() => c.PointToScreen(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2)));
        void Move(PixelPoint point) { pointer.MoveTo(point.X, point.Y); Thread.Sleep(80); }
        void Click(Control control) { Move(Center(control)); pointer.Click(); Thread.Sleep(100); }
        void Drag(string source, string destination, bool after, bool cancel = false)
        {
            Move(Center(Handle(source))); pointer.SetButton(true); Thread.Sleep(60);
            PixelPoint target = Ui(() =>
            {
                Button handle = Handle(destination);
                bool section = handle.Tag is string;
                Border card = handle.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains(section ? "card" : "tile"));
                return card.PointToScreen(section ? new Point(20, card.Bounds.Height * (after ? .75 : .25))
                    : new Point(card.Bounds.Width * (after ? .75 : .25), 20));
            });
            Move(target);
            if (cancel) { pointer.Key(0xff1b); Thread.Sleep(80); }
            pointer.SetButton(false); Thread.Sleep(150);
        }
    }

    private static T Ui<T>(Func<T> action) => Dispatcher.UIThread.Invoke(action);
    private static void Wait(Func<bool> condition)
    {
        for (int i = 0; i < 100; i++) { if (condition()) return; Thread.Sleep(50); }
        Assert.True(condition(), "The expected window state did not arrive.");
    }
}

public sealed class WindowOrderFactAttribute : FactAttribute
{
    public WindowOrderFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENXLR_TEST_ORDER") != "1")
            Skip = "Run separately with OPENXLR_TEST_ORDER=1 under xvfb-run, filtered to WindowOrderTests.";
    }
}
