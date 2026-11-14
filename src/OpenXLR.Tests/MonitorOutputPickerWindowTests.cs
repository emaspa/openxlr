using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;

namespace OpenXLR.Tests;

/// <summary>
/// The MONITOR picker on a real window against a fake daemon, while a
/// selected output's sink leaves the device list and comes back: the window
/// sends nothing on its own, the missing output stays in the picker ticked
/// and marked, and ticking another output keeps it in the selection the
/// window sends. Run from the window layout test, on its UI thread.
/// </summary>
internal static class MonitorOutputPickerWindowTests
{
    private const string Headset = "alsa_output.usb-SteelSeries_Arctis_Nova_Elite-00.analog-stereo";
    private const string Speakers = "alsa_output.usb-Creative_Technology_Ltd_Sound_BlasterX_Katana-00.analog-stereo";

    /// <summary>What the fake daemon holds; the headset's sink comes and goes.</summary>
    private sealed class Daemon
    {
        public volatile bool HeadsetPresent = true;
        public volatile string[] Selected = [Headset];
        public ConcurrentDictionary<string, string> Feeds { get; } = new();

        public JsonNode State()
        {
            var devices = new JsonArray();
            if (HeadsetPresent)
                devices.Add(new JsonObject { ["name"] = Headset, ["description"] = "Arctis Nova Elite", ["kind"] = 0, ["isPhysical"] = true });
            devices.Add(new JsonObject { ["name"] = Speakers, ["description"] = "Katana", ["kind"] = 0, ["isPhysical"] = true });
            devices.Add(new JsonObject { ["name"] = "OpenXLR_ch_system", ["description"] = "OpenXLR System", ["kind"] = 0, ["isOwn"] = true });
            return new JsonObject
            {
                ["type"] = "state",
                ["devices"] = devices,
                ["mixer"] = new JsonObject
                {
                    ["mixes"] = new JsonArray([..
                        new[] {
                        new JsonObject { ["id"] = "monitor", ["name"] = "Monitor A", ["kind"] = "monitor" },
                        new JsonObject { ["id"] = "monitor2", ["name"] = "Monitor B", ["kind"] = "monitor" } }
                        .Concat(Enumerable.Range(1, 18).Select(i => new JsonObject {
                            ["id"] = "custom" + i, ["name"] = "A very long renamed headphone monitor mix " + i, ["kind"] = "monitor" }))]),
                    ["channels"] = new JsonArray(),
                    ["monitorOutputs"] = new JsonArray([.. Selected.Select(s => (JsonNode)JsonValue.Create(s))]),
                    ["monitorFeeds"] = new JsonObject(Feeds.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, JsonValue.Create(pair.Value)))),
                    ["enforcedDefaultSink"] = "OpenXLR_ch_system",
                },
            };
        }
    }

    internal static void Check()
    {
        Task pending = CheckAll();
        DateTime deadline = DateTime.UtcNow.AddSeconds(60);
        while (!pending.IsCompleted && DateTime.UtcNow < deadline)
        {
            using var stop = new CancellationTokenSource();
            DispatcherTimer.RunOnce(stop.Cancel, TimeSpan.FromMilliseconds(20));
            Dispatcher.UIThread.MainLoop(stop.Token);
        }
        Assert.True(pending.IsCompleted, "The monitor output picker check hung.");
        pending.GetAwaiter().GetResult();
    }

    private static async Task CheckAll()
    {
        var daemon = new Daemon();
        var sent = new ConcurrentQueue<string[]>();
        var feeds = new ConcurrentQueue<(string Device, string Mix)>();
        var push = new SemaphoreSlim(0);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            await SocketTestServer.Send(socket, daemon.State(), stop);
            Task<JsonNode> receive = SocketTestServer.Receive(socket, stop);
            Task wake = push.WaitAsync(stop);
            while (!stop.IsCancellationRequested)
            {
                if (await Task.WhenAny(receive, wake) == wake)
                {
                    await wake;
                    wake = push.WaitAsync(stop);
                    await SocketTestServer.Send(socket, daemon.State(), stop);
                    continue;
                }
                JsonNode cmd = await receive;
                receive = SocketTestServer.Receive(socket, stop);
                if (cmd["cmd"]?.GetValue<string>() == "setMonitorFeed")
                {
                    string device = cmd["device"]!.GetValue<string>(), mix = cmd["mix"]!.GetValue<string>();
                    daemon.Feeds[device] = mix;
                    feeds.Enqueue((device, mix));
                }
                else if (cmd["cmd"]?.GetValue<string>() == "setMonitorOutputs")
                {
                    string[] devices = [.. cmd["devices"]!.AsArray().Select(d => d!.GetValue<string>())];
                    sent.Enqueue(devices);
                    daemon.Selected = devices;
                }
                else continue;
                await SocketTestServer.Send(socket, daemon.State(), stop);
                if (cmd["requestId"] is { } request)
                    await SocketTestServer.Send(socket, new { type = "commandResult", requestId = request.GetValue<string>(), error = (string?)null }, stop);
            }
        });

        var window = new MainWindow(new DaemonClient(server.Url));
        var main = (MainViewModel)window.DataContext!;
        try
        {
            window.Show();
            await Until(() => main.MonitorOutputs.Count == 2 && Item(main, Headset)?.IsSelected == true);
            WindowLayoutTests.Layout(window, 1040, 1200);
            var picker = window.FindControl<DropDownButton>("MonitorDestination")!;
            Assert.True(picker.IsEffectivelyVisible);
            var list = (ItemsControl)((ScrollViewer)((Flyout)picker.Flyout!).Content!).Content!;
            bool touch = SkinService.TouchControls;
            try
            {
                foreach (bool large in new[] { false, true })
                {
                    SkinService.ApplyControlSizing(large);
                    WindowLayoutTests.Layout(window, 640, 1200);
                    picker.BringIntoView();
                    picker.Flyout.ShowAt(picker);
                    await Until(() => list.GetVisualDescendants().OfType<DropDownButton>().Count() == 2);
                    DropDownButton feedButton = list.GetVisualDescendants().OfType<DropDownButton>()
                        .Single(button => ((MonitorOutputItem)button.DataContext!).Name == Headset);
                    feedButton.Flyout!.ShowAt(feedButton);
                    // the mix list scrolls above the Bluetooth option
                    var scroll = ((StackPanel)((Flyout)feedButton.Flyout).Content!).Children.OfType<ScrollViewer>().Single();
                    var mixList = (ItemsControl)scroll.Content!;
                    await Until(() => mixList.GetVisualDescendants().OfType<CheckBox>().Count() == 20);
                    window.UpdateLayout();
                    Assert.InRange(scroll.Bounds.Height, 1, 320);
                    Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
                    CheckBox[] choices = [.. mixList.GetVisualDescendants().OfType<CheckBox>()];
                    Assert.False(choices[0].IsEnabled);
                    if (large) Assert.All(choices, choice => Assert.True(choice.Bounds.Height >= SkinTokens.TouchTarget));
                    WindowLayoutTests.Capture(window, "output-mixes-640-" + (large ? "touch" : "standard"), mixList);
                    using var pointer = new XPointer();
                    pointer.Focus(TopLevel.GetTopLevel(mixList)!.TryGetPlatformHandle()!.Handle);
                    Assert.True(choices[1].Focus());
                    pointer.Key(XPointer.Escape);
                    await Until(() => !feedButton.Flyout.IsOpen);
                    picker.Flyout.Hide();
                }
            }
            finally { SkinService.ApplyControlSizing(touch); }
            WindowLayoutTests.Layout(window, 1040, 1200);

            picker.Flyout.ShowAt(picker);
            await Until(() => list.GetVisualDescendants().OfType<DropDownButton>().Count() == 2);
            var chooseMix = list.GetVisualDescendants().OfType<DropDownButton>()
                .Single(button => ((MonitorOutputItem)button.DataContext!).Name == Headset);
            chooseMix.Flyout!.ShowAt(chooseMix);
            var chooseList = (ItemsControl)((StackPanel)((Flyout)chooseMix.Flyout).Content!).Children.OfType<ScrollViewer>().Single().Content!;
            await Until(() => chooseList.GetVisualDescendants().OfType<CheckBox>().Count() == 20);
            var second = chooseList.GetVisualDescendants().OfType<CheckBox>().ElementAt(1);
            using (var pointer = new XPointer())
            {
                PixelPoint at = second.PointToScreen(new Point(second.Bounds.Width / 2, second.Bounds.Height / 2));
                pointer.MoveTo(at.X, at.Y);
                pointer.Click();
            }
            await Until(() => Item(main, Headset)!.Feed?.Id == "monitor+monitor2" && daemon.Feeds.GetValueOrDefault(Headset) == "monitor+monitor2");
            Assert.True(feeds.TryDequeue(out var combined));
            Assert.Equal((Headset, "monitor+monitor2"), combined);
            chooseMix.Flyout.Hide();
            picker.Flyout.Hide();

            // The headset's sink goes away and comes back, with the picker
            // open (its rows realised) and closed. Nothing is sent, and the
            // missing headset stays listed, ticked and marked as missing.
            foreach (bool open in new[] { true, false })
            {
                if (open) picker.Flyout.ShowAt(picker);
                Dispatcher.UIThread.RunJobs();
                await Push(main, push, () => daemon.HeadsetPresent = false);
                MonitorOutputItem? missing = Item(main, Headset);
                Assert.NotNull(missing);
                Assert.True(missing.IsSelected);
                Assert.Equal(Localizer.Format("MonitorOutputMissing", "Arctis Nova Elite"), missing.Label);
                Assert.False(Item(main, Speakers)!.IsSelected);
                Assert.Equal(missing.Label, main.MonitorSummary);
                if (open)
                {
                    await Until(() => list.GetVisualDescendants().OfType<CheckBox>().Count() == 2);
                    CheckBox[] boxes = [.. list.GetVisualDescendants().OfType<CheckBox>()];
                    Assert.Equal([Speakers, Headset], boxes.Select(b => ((MonitorOutputItem)b.DataContext!).Name));
                    Assert.Equal([false, true], boxes.Select(b => b.IsChecked == true));
                }
                await Push(main, push, () => daemon.HeadsetPresent = true);
                Assert.True(Item(main, Headset)!.IsSelected);
                Assert.Equal("Arctis Nova Elite", Item(main, Headset)!.Label);
                Assert.False(Item(main, Speakers)!.IsSelected);
                Assert.Empty(sent);
                if (open) picker.Flyout.Hide();
            }

            // Ticking the speakers while the headset is missing adds them;
            // the headset is not dropped because its sink is absent.
            await Push(main, push, () => daemon.HeadsetPresent = false);
            picker.Flyout.ShowAt(picker);
            await Until(() => list.GetVisualDescendants().OfType<CheckBox>().Count() == 2);
            Box(list, Speakers).IsChecked = true;
            await Until(() => !sent.IsEmpty);
            Assert.True(sent.TryDequeue(out string[]? both));
            Assert.Equal([Headset, Speakers], both);

            // Unticking the missing headset is the user's choice, and it goes.
            await Until(() => Item(main, Speakers)?.IsSelected == true
                && list.GetVisualDescendants().OfType<CheckBox>().Count() == 2);
            Box(list, Headset).IsChecked = false;
            await Until(() => !sent.IsEmpty);
            Assert.True(sent.TryDequeue(out string[]? speakersOnly));
            Assert.Equal([Speakers], speakersOnly);
            await Until(() => Item(main, Headset) is null);
            picker.Flyout.Hide();
            Assert.Empty(sent);
        }
        finally { window.Close(); }
    }

    private static MonitorOutputItem? Item(MainViewModel main, string name)
        => main.MonitorOutputs.SingleOrDefault(o => o.Name == name);

    private static CheckBox Box(ItemsControl list, string name)
        => list.GetVisualDescendants().OfType<CheckBox>().Single(b => ((MonitorOutputItem)b.DataContext!).Name == name);

    /// <summary>Change the fake daemon, push its state and wait until the window has applied it.</summary>
    private static async Task Push(MainViewModel main, SemaphoreSlim push, Action change)
    {
        int applied = 0;
        void OnApplied() => applied++;
        main.StateApplied += OnApplied;
        try
        {
            change();
            push.Release();
            await Until(() => applied > 0);
            // A few more dispatcher rounds, so anything the change set off reaches the socket.
            await Task.Delay(200);
        }
        finally { main.StateApplied -= OnApplied; }
    }

    private static async Task Until(Func<bool> ready)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.True(ready());
    }
}
