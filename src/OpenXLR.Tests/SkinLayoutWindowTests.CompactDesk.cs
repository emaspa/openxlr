using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using System.Reflection;
using System.Text.Json.Nodes;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Skinning;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

internal static partial class SkinLayoutWindowTests
{
    private static void CheckCompactDesk()
    {
        var window = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
        try
        {
            var model = (MainViewModel)window.DataContext!;
            FillBroadcastModel(model);
            model.Apps.Clear();
            model.ActiveApps.Clear();
            ChannelChoice[] choices = [new("music", "Music"), new("game", "Game"), new("chat", "Voice Chat")];
            AppStreamViewModel[] apps = [.. ColumnApps.Select((name, index) =>
            {
                var app = new AppStreamViewModel(new DaemonClient("ws://127.0.0.1:1/ws"), name, name, choices);
                app.ApplyFromDaemon(choices[index % choices.Length].Id, true, true);
                model.Apps.Add(app);
                return app;
            })];
            ApplicationIconWindowTests.Assign(model.Apps);
            Expander[] sections = [.. Usual.Select(name => window.FindControl<Expander>(name)!)];
            object?[] contents = [.. sections.Select(section => section.Content)];
            window.Show();
            Wear("compact-desk");
            Assert.Equal(["ProfilesButton", "FlowButton", "ArrangeButton", "HeaderOverflow"],
                window.FindControl<WrapPanel>("HeaderActions")!.Children.Select(c => c.Name));
            Assert.Equal(["RestartDaemonButton", "HeaderVersion", "AboutButton"],
                window.FindControl<StackPanel>("HeaderOverflowItems")!.Children.Select(c => c.Name));
            Assert.True(window.FindControl<DropDownButton>("HeaderOverflow")!.IsVisible);

            foreach (bool touch in new[] { false, true })
            foreach (bool second in new[] { false, true })
            {
                SkinService.ApplyControlSizing(touch);
                model.HasHp2 = second;
                model.CapCrossfade = second;
                foreach (int count in new[] { 3, 13 })
                {
                    ShowApps(model, apps, count);
                    foreach (double width in new[] { 640d, 1280, 1600, 2560 })
                    {
                        Layout(window, width, 900);
                        string where = $"Compact Desk: {width}, {count} apps, two jacks {second}, touch {touch}";
                        Border Card(string name) => (Border)window.FindControl<Expander>(name)!.Parent!;
                        var scroll = window.FindControl<ScrollViewer>("SectionScroll")!;
                        Border headphones = Card("HeadphonesTile"), monitor = Card("MonitorTile"), mixer = Card("SubmixerTile");
                        Assert.Equal(600, headphones.Bounds.Width, 0.5);
                        AssertNoOverlap([headphones, monitor, mixer, Card("ApplicationsTile")]);
                        foreach (string name in new[] { "HeadphonesTile", "MonitorTile", "ApplicationsTile" })
                            AssertControlsInside(Card(name), scroll);
                        Assert.Equal(window.LayoutHost.Root.Bounds.Width, mixer.Bounds.Width, 0.5);
                        if (width >= 1280)
                        {
                            Assert.True(monitor.Bounds.Width > 360, where);
                            Assert.Equal(window.FindControl<StackPanel>("MixerContent")!.Bounds.Width, mixer.Bounds.Width, 0.5);
                        }
                        var rows = window.FindControl<ItemsControl>("ApplicationRows")!;
                        var panel = Assert.IsType<TileColumnsPanel>(rows.ItemsPanelRoot);
                        Assert.Equal(1, panel.PerColumn);
                        Border[] tiles = ApplicationTiles(window);
                        Assert.Equal(count, tiles.Length);
                        AssertNoOverlap(tiles);
                        CheckTileParts(window, tiles, where);
                        foreach (Border tile in tiles) AssertInside(tile, Card("ApplicationsTile"));
                        if (width >= 1280 && count == 3)
                        {
                            Assert.All(tiles, tile => Assert.Equal(0, tile.TranslatePoint(default, panel)!.Value.Y, 0.5));
                            Assert.All(tiles, tile => Assert.True(tile.Bounds.Width <= panel.MaxTileWidth));
                            double edge = tiles[^1].TranslatePoint(new Point(tiles[^1].Bounds.Width, 0), panel)!.Value.X;
                            Assert.True(edge >= Math.Min(panel.Bounds.Width, count * panel.MaxTileWidth) - count * 5, where);
                        }
                        Assert.NotNull(window.MixMatrix);
                        Assert.Equal(6, window.MixMatrix.Columns.Count);
                        CheckMatrixCells(window, window.MixMatrix, where);
                        Assert.False(Card("InputsTile").IsVisible);
                        if (count == 3 && !second && !touch)
                            Capture(window, $"skin-compact-desk-{width}");
                        if (width == 1280 && count == 13 && second)
                            Capture(window, $"skin-compact-desk-many-{(touch ? "touch" : "standard")}-1280");
                    }
                }
            }
            CheckWideMixerLayouts(window, model);
            // Returning to another skin keeps the same section contents and application objects.
            ShowApps(model, apps, apps.Length);
            Wear(SkinPackage.DefaultId);
            Layout(window, 1280, 900);
            Assert.Null(window.MixMatrix);
            for (int i = 0; i < sections.Length; i++) Assert.Same(contents[i], sections[i].Content);
            Assert.Equal(apps, model.ActiveApps);
        }
        finally
        {
            SkinService.ApplyControlSizing(false);
            window.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }

    private static void CheckWideMixerLayouts(MainWindow window, MainViewModel model)
    {
        model.HasHp2 = model.CapCrossfade = false;
        SkinService.ApplyControlSizing(false);
        var mixer = JsonNode.Parse("""
            {"mixes":[{"id":"monitor","name":"Monitor A","kind":"monitor"},
                       {"id":"monitor2","name":"Monitor B","kind":"monitor"},
                       {"id":"chat","name":"Chat","kind":"virtualMic"}],
             "channels":[{"id":"xlr1","name":"XLR 1","hardware":true,"volume":1,
                          "binding":{"kind":"unit","pair":0},"bindingConnected":true,
                          "unitInput":{"unit":"0fd9:00a6","input":1,"primary":true},
                          "levels":{"monitor":1,"monitor2":1,"chat":1},"mutedIn":["monitor","monitor2"]},
                         {"id":"music","name":"Music","volume":1,"levels":{"monitor":1,"monitor2":1,"chat":1}},
                         {"id":"game","name":"Game","volume":1,"levels":{"monitor":0.5,"monitor2":0.5,"chat":1}},
                         {"id":"browser","name":"Browser","volume":1,"levels":{"monitor":1,"monitor2":1,"chat":1}},
                         {"id":"system","name":"System","volume":1,"levels":{"monitor":0.6,"monitor2":0.6,"chat":1}},
                         {"id":"voicechat","name":"Voice Chat","volume":1,"levels":{"monitor":1,"monitor2":1,"chat":1}}],
             "monitorOutputs":["headset"],"outputVolume":0.6,"inserts":{}}
            """)!;
        foreach (JsonNode channel in mixer["channels"]!.AsArray().OfType<JsonNode>())
            channel["appearance"] = new JsonObject { ["defaultIcon"] = LayoutDefaultIcons.ForChannel(
                channel["id"]!.GetValue<string>(), channel["hardware"]?.GetValue<bool>() == true, null) };
        foreach (JsonNode mix in mixer["mixes"]!.AsArray().OfType<JsonNode>())
            mix["appearance"] = new JsonObject { ["defaultIcon"] = LayoutDefaultIcons.ForMix(
                mix["id"]!.GetValue<string>(), mix["kind"]!.GetValue<string>()) };
        typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(model, [mixer]);
        typeof(MainViewModel).GetMethod("ApplyDevices", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(model,
            [JsonNode.Parse("""[{"name":"headset","kind":0,"description":"CORSAIR VIRTUOSO MAX WIRELESS Gaming Receiver + Monitor"}]"""), mixer]);
        model.Apps.Clear();
        model.ActiveApps.Clear();
        ChannelChoice[] choices = [new("browser", "Browser"), new("voicechat", "Voice Chat"), new("system", "System")];
        foreach (string name in new[] { "ChatGPT", "Discord", "Google Chrome", "Thunderbird Beta" })
        {
            var app = new AppStreamViewModel(new DaemonClient("ws://127.0.0.1:1/ws"), name, name, choices);
            app.ApplyFromDaemon(choices[model.Apps.Count % choices.Length].Id, true, true);
            model.Apps.Add(app);
            model.ActiveApps.Add(app);
        }
        ApplicationIconWindowTests.Assign(model.Apps);
        Layout(window, 1600, 800);
        var matrixView = window.GetVisualDescendants().OfType<OpenXLR.UI.Views.MixMatrixView>().Single(v => v.IsEffectivelyVisible);
        // Give the four cells and the name column an exact multiple before
        // the frame is counted. Stretching must leave room for the frame.
        double exact = (double)window.FindResource("Ox.Matrix.NameWidth")! + 4 * 300 + 3 * SkinLayoutDefaults.MatrixSpacing;
        Layout(window, window.ClientSize.Width + exact - matrixView.Bounds.Width, 800);
        Assert.True(matrixView.Scroller.Extent.Width <= matrixView.Scroller.Viewport.Width + .5,
            "The matrix gained a scrollbar from its own border at a width where the cells fit.");
        foreach (string skin in new[] { "compact-desk", "broadcast", "wave" })
        {
            Wear(skin);
            foreach (double width in new[] { 640d, 960, 1280, 1600, 2400, 2560, 3147, 640 })
            {
                Layout(window, width, 800);
                string where = $"{skin}, three mixes at {width}";
                Assert.Equal(3, window.MixMatrix!.Columns.Count);
                CheckMatrixCells(window, window.MixMatrix, where);
                if (width >= 1280)
                {
                    var card = (Border)window.FindControl<Expander>("SubmixerTile")!.Parent!;
                    Border last = ColumnHead(window, "chat");
                    double end = last.TranslatePoint(new Point(last.Bounds.Width, 0), card)!.Value.X;
                    double limit = (double)window.FindResource("Ox.Matrix.CellMaxWidth")!;
                    double names = (double)window.FindResource("Ox.Matrix.NameWidth")!;
                    Assert.True(end >= Math.Min(card.Bounds.Width * .94, names + 4 * limit), where);
                }
                if (width >= 2560)
                {
                    var rows = window.FindControl<ItemsControl>("ApplicationRows")!;
                    var panel = Assert.IsType<TileColumnsPanel>(rows.ItemsPanelRoot);
                    Border[] tiles = ApplicationTiles(window);
                    Assert.Equal(4, tiles.Length);
                    Assert.All(tiles, tile => Assert.Equal(0, tile.TranslatePoint(default, panel)!.Value.Y, .5));
                    Assert.True(tiles.Max(tile => tile.TranslatePoint(new Point(tile.Bounds.Width, 0), panel)!.Value.X)
                        >= Math.Min(panel.Bounds.Width * .95, tiles.Length * (panel.MaxTileWidth - 4)),
                        $"Four application tiles should use the wide row: {where}.");
                    AssertNoOverlap(tiles);
                }
                if (width is 640 or 3147) Capture(window, $"skin-{skin}-three-mixes-{width}");
            }
        }
        Wear(SkinPackage.DefaultId);
        double WidthOf(string itemsName)
        {
            var items = window.FindControl<ItemsControl>(itemsName)!;
            var panel = Assert.IsType<TileColumnsPanel>(items.ItemsPanelRoot);
            return panel.Children.First(child => child.Bounds.Width > 0).Bounds.Width;
        }

        Layout(window, 960, 800);
        double channels = WidthOf("ChannelTiles"), masters = WidthOf("MixMasterTiles"), apps = WidthOf("ApplicationRows");
        Layout(window, 3147, 800);
        Assert.True(WidthOf("ChannelTiles") > channels + 40, "classic channel tiles should grow on a wide window");
        Assert.True(WidthOf("MixMasterTiles") > masters + 40, "classic mix masters should grow on a wide window");
        Assert.True(WidthOf("ApplicationRows") > apps + 40, "classic application chips should grow on a wide window");
        Capture(window, "skin-material-wide-3147");
    }
}
