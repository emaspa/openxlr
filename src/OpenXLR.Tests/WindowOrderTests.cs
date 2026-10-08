using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Skinning;

namespace OpenXLR.Tests;

/// <summary>
/// Arrange mode driven with a real pointer and keyboard through XTEST, against
/// a fake daemon. Run from the window layout test, on its UI thread, because
/// Avalonia is set up once per process.
/// </summary>
internal static class WindowOrderTests
{
    private static readonly string[] Sections = ["InputsTile", "HeadphonesTile", "MonitorTile", "ApplicationsTile", "SubmixerTile"];

    /// <summary>Run the check; the caller's thread owns Avalonia's dispatcher.</summary>
    internal static void Check()
    {
        var commands = new ConcurrentQueue<JsonNode>();
        using var rejectGate = new SemaphoreSlim(0);
        int rejectNext = 0;
        JsonNode state = JsonNode.Parse("""
            {"type":"state","mixer":{"channels":[
            {"id":"xlr1","name":"XLR 1","hardware":true},
            {"id":"game","name":"Game"},{"id":"music","name":"Music"}],
            "mixes":[{"id":"monitor","name":"Monitor A","kind":"monitor","editable":false},
            {"id":"stream","name":"Stream","kind":"virtualMic","editable":true},
            {"id":"chat","name":"Chat","kind":"virtualMic","editable":true}],"inserts":{}}}
            """)!;
        state["daemonVersion"] = AppVersion.Current;
        for (int i = 0; i < 12; i++)
            state["mixer"]!["channels"]!.AsArray().Add(new JsonObject { ["id"] = "extra" + i, ["name"] = "Extra " + i });
        // Started off this thread: its continuations must not wait for the dispatcher.
        SocketTestServer server = Task.Run(() => SocketTestServer.Start(async (socket, stop) =>
        {
            await SocketTestServer.Receive(socket, stop); // authentication frame
            await SocketTestServer.Send(socket, state, stop);
            while (!stop.IsCancellationRequested)
            {
                JsonNode command = await SocketTestServer.Receive(socket, stop);
                string cmd = command["cmd"]!.GetValue<string>();
                commands.Enqueue(command);
                string? requestId = command["requestId"]?.GetValue<string>();
                if (cmd == "listPlugins")
                    await SocketTestServer.Send(socket, new { type = "plugins", plugins = Array.Empty<object>() }, stop);
                if (cmd != "setLayoutOrder")
                {
                    if (requestId is not null) await SocketTestServer.Send(socket, new { type = "commandResult", requestId }, stop);
                    continue;
                }
                if (Interlocked.Exchange(ref rejectNext, 0) == 1)
                {
                    await rejectGate.WaitAsync(stop);
                    await SocketTestServer.Send(socket, new { type = "commandResult", requestId, error = "disk full" }, stop);
                    continue;
                }
                // The daemon's own rule: structural items keep their place,
                // the editable ones follow the command.
                foreach (string field in new[] { "channels", "mixes" })
                {
                    JsonArray current = state["mixer"]![field]!.AsArray();
                    var byId = current.ToDictionary(n => n!["id"]!.GetValue<string>());
                    var ordered = command[field]!.AsArray().Select(id => byId[id!.GetValue<string>()]!).ToList();
                    int next = 0;
                    state["mixer"]![field] = new JsonArray([.. current.Select(n =>
                        (JsonNode)(ordered.Contains(n!) ? ordered[next++] : n!).DeepClone())]);
                }
                await SocketTestServer.Send(socket, state, stop);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId }, stop);
            }
        })).GetAwaiter().GetResult();

        string config = Directory.CreateTempSubdirectory("openxlr-order-").FullName;
        string? oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
        MainWindow? main = null;
        try
        {
            new UiSettings { CollapsedSections = Sections, CheckForUpdates = false }.Save();
            main = Open();
            using var quit = new CancellationTokenSource();
            Task driver = Task.Run(Drive);
            driver.ContinueWith(_ => quit.Cancel(), TaskScheduler.Default);
            Dispatcher.UIThread.MainLoop(quit.Token);
            driver.GetAwaiter().GetResult();
        }
        finally
        {
            main?.Close();
            SkinService.Choose(SkinPackage.DefaultId, out _);
            Dispatcher.UIThread.RunJobs();
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", oldConfig);
            Task.Run(async () => await server.DisposeAsync()).GetAwaiter().GetResult();
            Directory.Delete(config, true);
        }

        void Drive()
        {
            using var pointer = new XPointer();
            try { Steps(pointer); }
            finally
            {
                pointer.SetButton(false, 3);
                pointer.SetButton(false);
            }
        }

        void Steps(XPointer pointer)
        {
            Wait(() => Ui(() => Model().CanEditLayout));
            Assert.False(Ui(() => Handle("SubmixerTile").IsEffectivelyVisible));
            Click(Ui(() => main!.FindControl<ToggleButton>("ArrangeButton")!));
            Wait(() => Ui(() => Handle("SubmixerTile").IsEffectivelyVisible));
            Ui(() => { main!.FindControl<Expander>("SubmixerTile")!.IsExpanded = true; return true; });
            Wait(() => Ui(() => Handle("music").IsEffectivelyVisible));
            // Hardware inputs and Monitor A keep their place, so they have no handle.
            Assert.False(Ui(() => Handle("xlr1").IsEffectivelyVisible));
            Assert.False(Ui(() => Handle("monitor").IsEffectivelyVisible));
            Ui(() => { main!.FindControl<Expander>("SubmixerTile")!.IsExpanded = false; return true; });

            // Sections: a drag lands before or after the section under the pointer.
            Border inputsCard = Ui(() => (Border)main!.FindControl<Expander>("InputsTile")!.Parent!);
            Drag("SubmixerTile", "InputsTile", false);
            Wait(() => Ui(() => Order()[0] == "SubmixerTile"));
            Drag("MonitorTile", "ApplicationsTile", true);
            Wait(() => Ui(() => Order()[^1] == "MonitorTile"));
            Assert.Equal(Ui(Order), Ui(() => UiSettings.Load().SectionOrder));
            Assert.All(Sections, id => Assert.False(Ui(() => main!.FindControl<Expander>(id)!.IsExpanded)));

            // A drag held still does not restyle its target on every scroll tick,
            // and the arrow follows the half of the card under the pointer.
            Move(Center(Handle("InputsTile"))); pointer.SetButton(true);
            Move(Center(Handle("SubmixerTile")));
            Button destination = Handle("SubmixerTile");
            Wait(() => Ui(() => destination.Classes.Contains("reorderTarget")));
            int targetChanges = 0;
            void ContentChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
            {
                if (e.Property == ContentControl.ContentProperty) Interlocked.Increment(ref targetChanges);
            }
            void ClassesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
                => Interlocked.Increment(ref targetChanges);
            Ui(() => { destination.PropertyChanged += ContentChanged; destination.Classes.CollectionChanged += ClassesChanged; return true; });
            try
            {
                Thread.Sleep(250);
                Assert.Equal(0, Volatile.Read(ref targetChanges));
                Move(Ui(() =>
                {
                    var card = (Border)main!.FindControl<Expander>("SubmixerTile")!.Parent!;
                    return card.PointToScreen(new Point(20, card.Bounds.Height * .75));
                }));
                Wait(() => Ui(() => Equals(destination.Content, "↓")));
                Move(Center(destination));
                Wait(() => Ui(() => Equals(destination.Content, "↑")));
            }
            finally
            {
                Ui(() => { destination.PropertyChanged -= ContentChanged; destination.Classes.CollectionChanged -= ClassesChanged; return true; });
            }

            // Another button released mid-drag does not finish it; Escape cancels it.
            string[] before = Ui(Order);
            pointer.SetButton(true, 3); Thread.Sleep(80);
            pointer.SetButton(false, 3); Thread.Sleep(80);
            Assert.Equal(before, Ui(Order));
            Assert.True(Ui(() => destination.Classes.Contains("reorderTarget")));
            pointer.Key(XPointer.Escape); Thread.Sleep(80);
            pointer.SetButton(false); Thread.Sleep(100);
            Assert.Equal(before, Ui(Order));
            Assert.Equal("↕", Ui(() => destination.Content));

            // A click on a handle neither moves nor toggles its section, and a
            // release outside the window moves nothing.
            Click(Handle("InputsTile"));
            Assert.False(Ui(() => main!.FindControl<Expander>("InputsTile")!.IsExpanded));
            Move(Center(Handle("InputsTile"))); pointer.SetButton(true); Thread.Sleep(50);
            pointer.MoveTo(2400, 1400); Thread.Sleep(100); pointer.SetButton(false); Thread.Sleep(100);
            Assert.Equal(before, Ui(Order));

            // The keyboard moves the focused handle's section one place.
            Click(Handle("InputsTile"));
            int at = Array.IndexOf(before, "InputsTile");
            pointer.Key(XPointer.Down);
            Wait(() => Ui(() => Array.IndexOf(Order(), "InputsTile") == at + 1));

            // An order that cannot be saved still applies for this run and says why.
            before = Ui(Order);
            string path = Path.Combine(config, "openxlr", "ui.json");
            File.Move(path, path + ".saved");
            Directory.CreateDirectory(path);
            try
            {
                Drag("HeadphonesTile", before[0], false);
                Wait(() => Ui(() => main!.FindControl<TextBlock>("ArrangementNote")!.Text!.Contains("could not be saved")));
                Assert.Equal("HeadphonesTile", Ui(() => Order()[0]));
            }
            finally { Directory.Delete(path); File.Move(path + ".saved", path); }
            Drag("HeadphonesTile", "SubmixerTile", true);
            Wait(() => Ui(() => UiSettings.Load().SectionOrder.SequenceEqual(Order())));
            Assert.Equal("HeadphonesTile", Ui(() => Order()[1]));

            // A skin change repaints the same cards in place and keeps the order.
            before = Ui(Order);
            string? background = Ui(() => inputsCard.Background?.ToString());
            Ui(() => { Assert.Empty(SkinService.Choose("opendeck", out _)); return true; });
            Assert.NotEqual(background, Ui(() => inputsCard.Background?.ToString()));
            Assert.Same(inputsCard, Ui(() => main!.FindControl<Expander>("InputsTile")!.Parent));
            Assert.Equal(before, Ui(Order));
            Ui(() => { Assert.Empty(SkinService.Choose(SkinPackage.DefaultId, out _)); return true; });

            // A new window opens with the saved order and Arrange off.
            Ui(() => { main!.Close(); main = Open(); return true; });
            Wait(() => Ui(() => Model().CanEditLayout));
            Assert.Equal(before, Ui(Order));
            Assert.False(Ui(() => main!.FindControl<ToggleButton>("ArrangeButton")!.IsChecked == true));
            Click(Ui(() => main!.FindControl<ToggleButton>("ArrangeButton")!));
            Ui(() => { main!.FindControl<Expander>("SubmixerTile")!.IsExpanded = true; return true; });
            Wait(() => Ui(() => Handle("music").IsEffectivelyVisible));

            // A tile move is a setLayoutOrder of the editable items; the tiles
            // move when the daemon's state says so, not before.
            int sent = Layout().Length;
            var music = Ui(() => Model().Channels.Single(c => c.Id == "music"));
            Volatile.Write(ref rejectNext, 1);
            Drag("music", "game", false);
            Wait(() => Layout().Length == sent + 1);
            Assert.Equal("game", Ui(() => Model().Channels[1].Id));
            Drag("extra0", "game", false); // ignored while the first one waits
            rejectGate.Release();
            Wait(() => Ui(() => main!.FindControl<TextBlock>("ArrangementNote")!.Text == "disk full"));
            Assert.Equal(sent + 1, Layout().Length);
            Assert.Equal("game", Ui(() => Model().Channels[1].Id));

            Drag("music", "game", false);
            Wait(() => Ui(() => Model().Channels[1].Id == "music"));
            Assert.Same(music, Ui(() => Model().Channels[1]));
            JsonNode last = Layout()[^1];
            Assert.Equal(["music", "game", .. Enumerable.Range(0, 12).Select(i => "extra" + i)],
                last["channels"]!.AsArray().Select(n => n!.GetValue<string>()));
            Assert.Equal(["stream", "chat"], last["mixes"]!.AsArray().Select(n => n!.GetValue<string>()));
            Assert.Equal("xlr1", Ui(() => Model().Channels[0].Id));

            Drag("chat", "stream", false);
            Wait(() => Ui(() => Model().Mixes[1].Id == "chat"));
            Assert.Equal("monitor", Ui(() => Model().Mixes[0].Id));
            Assert.Equal(["chat", "stream"], Layout()[^1]["mixes"]!.AsArray().Select(n => n!.GetValue<string>()));
            int moves = Layout().Length;
            Drag("music", "chat", false);
            Thread.Sleep(150);
            Assert.Equal(moves, Layout().Length); // a channel cannot land among the mixes

            // Held at the edge of the channel row, a drag scrolls it; cancelled,
            // it sends nothing and the scrolling stops.
            var scroll = Ui(() => Handle("music").GetVisualAncestors().OfType<ScrollViewer>().First());
            Move(Center(Handle("music"))); pointer.SetButton(true); Thread.Sleep(50);
            Move(Ui(() => scroll.PointToScreen(new Point(scroll.Bounds.Width - 5, 40))));
            Wait(() => Ui(() => scroll.Offset.X > 30));
            pointer.Key(XPointer.Escape); pointer.SetButton(false); Thread.Sleep(100);
            double offset = Ui(() => scroll.Offset.X);
            Thread.Sleep(150);
            Assert.Equal(offset, Ui(() => scroll.Offset.X));
            Assert.Equal(moves, Layout().Length);

            // Arranging sends no audio command at all.
            Assert.All(commands, c => Assert.Matches("^(setLayoutOrder|get[A-Za-z]*|list[A-Za-z]*)$", c["cmd"]!.GetValue<string>()));

            Click(Ui(() => main!.FindControl<Button>("ResetSectionsButton")!));
            Wait(() => Ui(() => Order().SequenceEqual(Sections)));
            Assert.Equal(Sections, Ui(() => UiSettings.Load().SectionOrder));

            void Move(PixelPoint point) { pointer.MoveTo(point.X, point.Y); Thread.Sleep(80); }
            void Click(Control control) { Move(Center(control)); pointer.Click(); Thread.Sleep(100); }
            void Drag(string source, string target, bool after)
            {
                Move(Center(Handle(source))); pointer.SetButton(true); Thread.Sleep(60);
                Move(Ui(() =>
                {
                    Button handle = Handle(target);
                    bool section = handle.Tag is string;
                    Border card = handle.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains(section ? "card" : "tile"));
                    return card.PointToScreen(section ? new Point(20, card.Bounds.Height * (after ? .75 : .25))
                        : new Point(card.Bounds.Width * (after ? .75 : .25), 20));
                }));
                pointer.SetButton(false); Thread.Sleep(150);
            }
        }

        JsonNode[] Layout() => [.. commands.Where(c => c["cmd"]!.GetValue<string>() == "setLayoutOrder")];
        MainViewModel Model() => (MainViewModel)main!.DataContext!;
        MainWindow Open()
        {
            var window = new MainWindow(new DaemonClient(server.Url))
            { Width = 1040, Height = 1000, Position = new PixelPoint(30, 30), WindowStartupLocation = WindowStartupLocation.Manual };
            window.Show();
            return window;
        }
        string[] Order() => [.. main!.FindControl<StackPanel>("SectionCards")!.Children.OfType<Border>()
            .Select(b => ((Expander)b.Child!).Name!)];
        Button Handle(string id) => Ui(() => main!.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Classes.Contains("reorderHandle") && (b.Tag switch
            { string name => name, ChannelViewModel c => c.Id, MixViewModel m => m.Id, _ => "" }) == id));
        PixelPoint Center(Control control)
        {
            PixelPoint center = default;
            // Layout can place a control before the input tree has it there,
            // after a section opens or a window appears; wait until a hit test
            // at its centre finds it.
            Wait(() => Ui(() =>
            {
                if (!control.IsEffectivelyVisible || control.Bounds.Width <= 0 || control.Bounds.Height <= 0) return false;
                var local = new Point(control.Bounds.Width / 2, control.Bounds.Height / 2);
                if (control.TranslatePoint(local, main!) is not { } point) return false;
                var hit = main!.InputHitTest(point) as Visual;
                if (hit != control && hit?.GetVisualAncestors().Contains(control) != true) return false;
                center = control.PointToScreen(local);
                return true;
            }));
            return center;
        }
    }

    private static T Ui<T>(Func<T> action) => Dispatcher.UIThread.Invoke(action);

    private static void Wait(Func<bool> condition)
    {
        for (int i = 0; i < 100; i++) { if (condition()) return; Thread.Sleep(50); }
        Assert.True(condition(), "The expected window state did not arrive.");
    }
}
