using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;

namespace OpenXLR.Tests;

/// <summary>
/// A skin on real windows: that it reaches them, that switching repaints the
/// ones already open, that going back restores the shipped appearance exactly,
/// and that neither built-in appearance breaks the layout or the labels a
/// screen reader reads.
///
/// Run separately under Xvfb; Avalonia owns one UI thread per process.
/// </summary>
[Collection("xdg-config")]
public sealed partial class SkinWindowTests
{
    // Avalonia sets its platform up once per process, so the whole class is one
    // test with three parts rather than three tests that cannot share a thread.
    [SkinFact]
    public void ASkinReachesEveryWindowAndTheApplicationSurvivesABadOne()
    {
        Run((main, options, flow) =>
        {
            SkinsReachOpenWindowsAndTheDefaultComesBackExactly(main, options, flow);
            AnInstalledSkinIsFoundAndItsImageIsUsed(main);
            ALayoutSkinTurnsTheSectionsWithoutRebuildingThem(main);
            ASwitchToMixBanksKeepsEveryViewModelAndSendsNothing(main);
            AStructuralSwitchKeepsFocusFoldsAndScrollAndLeavesNothingBehind(main);
            ABrokenSkinLeavesTheWindowUsableAndSaysWhy(main);
            ControlAppearancesSwitchAndTheFaderStillWorks(main);
            NeitherShippedAppearanceBreaksTheLayoutOrTheLabels(main, options, flow);
            EveryBuiltInSkinKeepsTheMainLabelsReadable(main);
            ChannelTileAlignmentWindowTests.CheckSkins();
            EveryWindowWearsTheSkin(main);
            ACheckBoxLabelTakesItsOwnToken(options);
            IndicatorsAndMetersDrawInsideTheBoxTheyAreGiven();
            AMeterIsColouredByPositionAndNotByItsReading();
            AContinuousBarIsWholeWhereItsZonesMeet();
            AnUprightMeterFillsFromTheBottomOnTheSameScale();
            ButtonsCarryTheirStateInTheirLettering(main);
            EveryRectangularControlWearsTheSameFace(main, options);
            EveryKeyIsDrawnLikeTheMuteKey();
            TheApplicationsWindowKeepsItsBottomControlsReachable();
            ThePluginBypassKeyIsLegibleInBothAppearances();
            LampKeysAndCheckKeysWearTheSkinsFaces();
            TheOptionsTabsWearTheSkin(options);
            TheWindowActuallyRepaintsWhenTheSkinChanges(main);
            ASkinCanLeaveTheBindingOut();
            TheInputReadoutsWearTheSkin();
            CaptureChainsUseTheRackAndMatrixParts();
            SoundboardWindowTests.CheckSkins();
            VoiceChangerWindowTests.CheckSkins();
            DynamicsWindowTests.CheckSkins();
            AnAdditionalUnitsControlsFollowTheStripsOwnParts();
            AppearanceModeWindowTests.Check(main, options, flow);
            TouchControlsWindowTests.Check(main, options, flow);
            AudioClockWindowTests.Check(main, options, flow);
        });
    }

    private static void SkinsReachOpenWindowsAndTheDefaultComesBackExactly(
        MainWindow main, OptionsWindow options, FlowWindow flow)
    {
        {
            // What the shipped appearance looks like, measured rather than assumed.
            var card = Cards(main).First();
            IBrush? defaultWindow = main.Background;
            IBrush? defaultCard = card.Background;
            Thickness defaultCardBorder = card.BorderThickness;
            Color defaultLedOn = SkinService.LiveBrush("Ox.Led.On").Color;
            Color defaultMeterHot = SkinService.LiveBrush("Ox.Meter.Hot").Color;
            Slider fader = main.GetVisualDescendants().OfType<Slider>().First();
            double defaultThumbWidth = ThumbWidth(main);

            // The application leaves the Fluent theme alone until a skin asks.
            Assert.False(Application.Current!.Resources.ContainsKey("ButtonBackground"));

            IReadOnlyList<string> errors = SkinService.Apply(SkinCatalog.Find("opendeck")!);
            Pump(main);
            Assert.Empty(errors);

            // The window, the card and the indicators all moved.
            Assert.NotEqual(defaultWindow?.ToString(), main.Background?.ToString());
            Assert.IsType<LinearGradientBrush>(card.Background);
            Assert.True(card.BorderThickness.Left > defaultCardBorder.Left);
            // The meter gains its warning and top zones, which the shipped
            // appearance does not draw because all three colours match there.
            Assert.NotEqual(defaultMeterHot, SkinService.LiveBrush("Ox.Meter.Hot").Color);
            Assert.NotEqual(SkinService.LiveBrush("Ox.Meter.Fill").Color,
                SkinService.LiveBrush("Ox.Meter.Warning").Color);
            Assert.NotEqual(SkinService.LiveBrush("Ox.Meter.Warning").Color,
                SkinService.LiveBrush("Ox.Meter.Hot").Color);

            // The fader is the machined cap, through the Fluent theme's own keys.
            Assert.True(ThumbWidth(main) > defaultThumbWidth, $"thumb stayed {ThumbWidth(main)}");
            Assert.IsType<RadialGradientBrush>(Application.Current.Resources["SliderThumbBackground"]);
            // Every key wears the same face the mute key does; the pixel
            // comparison further down is what actually holds them together.
            Assert.IsType<LinearGradientBrush>(Application.Current.Resources["ButtonBackground"]);
            Assert.Equal(Color.Parse("#3ecf7a"), Application.Current.Resources["SystemAccentColor"]);

            // Windows that were already open follow, not only the mixer.
            Assert.IsType<LinearGradientBrush>(Cards(options).First().Background);
            Assert.IsType<LinearGradientBrush>(flow.Background);
            Assert.True(flow.GetVisualDescendants().OfType<Button>()
                .Any(b => b.Classes.Contains("flowNode")), "the flow graph lost its cards");

            // And back: every token returns to its default and every key the
            // skin borrowed from Fluent is handed back.
            SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
            Pump(main);
            Assert.Equal(defaultWindow?.ToString(), main.Background?.ToString());
            Assert.Equal(defaultCard?.ToString(), card.Background?.ToString());
            Assert.Equal(defaultCardBorder, card.BorderThickness);
            Assert.Equal(defaultMeterHot, SkinService.LiveBrush("Ox.Meter.Hot").Color);
            Assert.Equal(defaultThumbWidth, ThumbWidth(main));
            Assert.False(Application.Current.Resources.ContainsKey("ButtonBackground"));
            Assert.False(Application.Current.Resources.ContainsKey("SystemAccentColor"));
        }
    }

    /// <summary>
    /// A skin dropped into the user's data directory, with an image beside it:
    /// the catalogue finds it, the picker's id selects it, and the image is
    /// decoded from the skin's own folder.
    /// </summary>
    private static void AnInstalledSkinIsFoundAndItsImageIsUsed(MainWindow main)
    {
        string data = Path.Combine(Path.GetTempPath(), "openxlr-skindata-" + Guid.NewGuid());
        string folder = Path.Combine(data, "openxlr", "skins", "with-art");
        string? oldHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string? oldDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        Directory.CreateDirectory(folder);
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", data);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(data, "none"));
            WritePng(Path.Combine(folder, "faceplate.png"), 64, 64);
            File.WriteAllText(Path.Combine(folder, "skin.json"), """
                {"schema":1,"name":"With art","tokens":{
                  "Ox.Tile.Background": {"type":"image","source":"faceplate.png","stretch":"uniformToFill"},
                  "Ox.Text.Primary": "#fafafa"
                }}
                """);

            SkinEntry entry = Assert.Single(SkinCatalog.Discover(), e => e.Id == "with-art");
            Assert.Empty(entry.Errors);
            Assert.Empty(SkinService.Apply(entry));
            Pump(main);

            Border tile = main.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("tile"));
            var brush = Assert.IsType<ImageBrush>(tile.Background);
            Assert.Equal(new Size(64, 64), ((IImage)brush.Source!).Size);
        }
        finally
        {
            SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", oldHome);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", oldDirs);
            Directory.Delete(data, recursive: true);
        }
    }

    /// <summary>
    /// A skin whose layout runs the sections in a row, put on and taken off
    /// while the window is open: the sections turn and reorder, and they are
    /// the same cards, expanders and contents with the same data context
    /// throughout. A skin that only changes colours leaves the sections
    /// exactly where they are, without touching the panel at all.
    /// </summary>
    private static void ALayoutSkinTurnsTheSectionsWithoutRebuildingThem(MainWindow main)
    {
        string data = Path.Combine(Path.GetTempPath(), "openxlr-skinlayout-" + Guid.NewGuid());
        string folder = Path.Combine(data, "openxlr", "skins", "row");
        string? oldHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string? oldDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        Directory.CreateDirectory(folder);
        string[] tiles = ["InputsTile", "HeadphonesTile", "MonitorTile", "ApplicationsTile", "SubmixerTile"];
        var panel = main.FindControl<StackPanel>("SectionCards")!;
        Expander[] expanders = [.. tiles.Select(name => main.FindControl<Expander>(name)!)];
        Control[] cards = [.. expanders.Select(e => (Control)e.Parent!)];
        object?[] contents = [.. expanders.Select(e => e.Content)];
        object? context = main.DataContext;
        int contextChanges = 0, panelChanges = 0;
        void ContextChanged(object? sender, EventArgs args) => contextChanges++;
        void PanelChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args) => panelChanges++;
        string[] Order() => [.. panel.Children.Select(c => ((Expander)((Border)c).Child!).Name!)];
        string[] before = Order();
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", data);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(data, "none"));
            File.WriteAllText(Path.Combine(folder, "skin.json"), """
                {"schema":1,"name":"Row","tokens":{"Ox.Card.Background":"#30343c"},
                 "layout":{"version":1,"main":{"kind":"stack","id":"row","orientation":"horizontal","spacing":20,"children":[
                   {"kind":"region","region":"submixer"},{"kind":"region","region":"monitor"},
                   {"kind":"region","region":"inputs"},{"kind":"region","region":"headphones"},
                   {"kind":"region","region":"applications"}]}}}
                """);
            foreach (Control control in expanders.Concat(cards)) control.DataContextChanged += ContextChanged;
            panel.Children.CollectionChanged += PanelChanged;

            // Colours alone: the panel is not touched.
            Assert.Empty(SkinService.Apply(SkinCatalog.Find("opendeck")!));
            Pump(main);
            Assert.Equal(0, panelChanges);
            Assert.Equal(before, Order());
            Assert.Equal(Avalonia.Layout.Orientation.Vertical, panel.Orientation);

            SkinEntry row = Assert.Single(SkinCatalog.Discover(), e => e.Id == "row");
            Assert.Empty(row.Errors);
            Assert.Empty(SkinService.Apply(row));
            Pump(main);
            Layout(main, 1040, 900);
            Assert.Equal(Avalonia.Layout.Orientation.Horizontal, panel.Orientation);
            Assert.Equal(20, panel.Spacing);
            Assert.Equal(["SubmixerTile", "MonitorTile", "InputsTile", "HeadphonesTile", "ApplicationsTile"], Order());
            Capture(main, "layout-row");

            // A grid that takes the header in too, and back to the row: the
            // same cards and header, moved, with their data context held.
            string gridFolder = Path.Combine(data, "openxlr", "skins", "grid");
            Directory.CreateDirectory(gridFolder);
            File.WriteAllText(Path.Combine(gridFolder, "skin.json"), """
                {"schema":1,"name":"Grid","tokens":{"Ox.Card.Background":"#30343c"},
                 "layout":{"version":1,"main":{"kind":"grid","id":"desktop","columns":[{"star":1},{"star":1}],"rows":["auto","auto","auto"],"children":[
                   {"kind":"region","region":"header","row":0,"column":0,"columnSpan":2},
                   {"kind":"region","region":"inputs","row":1,"column":0},
                   {"kind":"dock","id":"right","row":1,"column":1,"rowSpan":2,"children":[
                     {"kind":"region","region":"monitor","dock":"top"},{"kind":"region","region":"submixer","dock":"fill"}]},
                   {"kind":"stack","id":"left","orientation":"vertical","row":2,"column":0,"children":[
                     {"kind":"region","region":"headphones"},{"kind":"region","region":"applications"}]}]}}}
                """);
            Control header = main.FindControl<Border>("HeaderCard")!;
            header.DataContextChanged += ContextChanged;
            try
            {
                SkinEntry grid = Assert.Single(SkinCatalog.Discover(), e => e.Id == "grid");
                Assert.Empty(grid.Errors);
                Assert.Empty(SkinService.Apply(grid));
                Pump(main);
                Layout(main, 1600, 900);
                Assert.Empty(panel.Children);
                Grid gridPanel = Assert.IsType<Grid>(main.FindControl<ScrollViewer>("SectionScroll")!.Content);
                Assert.Same(gridPanel, header.Parent);
                Assert.Same(gridPanel, cards[0].Parent);
                Assert.IsType<DockPanel>(cards[4].Parent);
                Assert.IsType<StackPanel>(cards[1].Parent);
                for (int i = 0; i < tiles.Length; i++)
                {
                    Assert.Same(cards[i], expanders[i].Parent);
                    Assert.Same(contents[i], expanders[i].Content);
                    Assert.Same(context, expanders[i].DataContext);
                }
                Assert.Same(context, header.DataContext);
                Capture(main, "layout-grid");

                Assert.Empty(SkinService.Apply(row));
                Pump(main);
                Layout(main, 1040, 900);
                Assert.Equal(["SubmixerTile", "MonitorTile", "InputsTile", "HeadphonesTile", "ApplicationsTile"], Order());
                Assert.Same(main.FindControl<StackPanel>("MixerContent"), header.Parent);
                Assert.Same(panel, main.FindControl<ScrollViewer>("SectionScroll")!.Content);
            }
            finally { header.DataContextChanged -= ContextChanged; }

            // And back, through a colour-only skin, to the shipped appearance.
            Assert.Empty(SkinService.Apply(SkinCatalog.Find("opendeck")!));
            Pump(main);
            Assert.Equal(Avalonia.Layout.Orientation.Vertical, panel.Orientation);
            Assert.Equal(before, Order());
            SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
            Pump(main);
            Assert.Equal(before, Order());

            Assert.Equal(expanders, tiles.Select(name => main.FindControl<Expander>(name)!));
            for (int i = 0; i < tiles.Length; i++)
            {
                Assert.Same(cards[i], expanders[i].Parent);
                Assert.Same(contents[i], expanders[i].Content);
                Assert.Same(context, expanders[i].DataContext);
                Assert.Contains(cards[i], panel.Children);
            }
            Assert.Same(context, main.DataContext);
            Assert.Equal(0, contextChanges);
        }
        finally
        {
            foreach (Control control in expanders.Concat(cards)) control.DataContextChanged -= ContextChanged;
            panel.Children.CollectionChanged -= PanelChanged;
            SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", oldHome);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", oldDirs);
            Directory.Delete(data, recursive: true);
        }
    }

    /// <summary>
    /// A skin that draws the submixer as mix banks, put on and taken off
    /// while the window is connected to a daemon: the banks hand out the same
    /// channel, mix and send objects the tiles drew, a colour-only skin in
    /// between leaves the banks as they are, and nothing goes to the daemon,
    /// no level, no mute and no layout order. The shipped Broadcast skin's
    /// mix matrix, switched to Material and back and to banks and back,
    /// keeps to the same rules.
    /// </summary>
    private static void ASwitchToMixBanksKeepsEveryViewModelAndSendsNothing(MainWindow main)
    {
        string data = Path.Combine(Path.GetTempPath(), "openxlr-skinbanks-" + Guid.NewGuid());
        string folder = Path.Combine(data, "openxlr", "skins", "banks");
        string? oldHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string? oldDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        object? context = main.DataContext;
        var commands = new System.Collections.Concurrent.ConcurrentQueue<string>();
        T Await<T>(Task<T> task)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(5);
            }
            Assert.True(task.IsCompleted, "the fake daemon did not answer");
            return task.GetAwaiter().GetResult();
        }
        SocketTestServer server = Await(SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                JsonNode command = await SocketTestServer.Receive(socket, stop);
                commands.Enqueue(command["cmd"]!.GetValue<string>());
                if (command["requestId"] is { } requestId)
                    await SocketTestServer.Send(socket, new { type = "commandResult", requestId = requestId.GetValue<string>() }, stop);
            }
        }));
        var client = new DaemonClient(server.Url);
        try
        {
            var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.ConnectionChanged += up => { if (up) connected.TrySetResult(true); };
            client.Start();
            Await(connected.Task);
            var vm = new MainViewModel(client);
            typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [JsonNode.Parse("""
                {"mixes":[{"id":"monitor","name":"Monitor A","volume":1.2,"kind":"monitor"},{"id":"stream","name":"Stream","volume":0.5},
                          {"id":"chat","name":"Chat","volume":0.7}],
                 "channels":[{"id":"music","name":"Music","levels":{"monitor":0.5,"stream":0.4,"chat":0.3},"mutedIn":["chat"]},
                             {"id":"game","name":"Game","levels":{"monitor":1,"stream":0.2,"chat":0.9}}]}
                """)]);
            vm.Mixes[0].VolumeRange.Boost = true;
            main.DataContext = vm;
            Pump(main);
            ChannelViewModel[] channels = [.. vm.Channels];
            MixViewModel[] mixes = [.. vm.Mixes];
            SendViewModel[] sends = [.. vm.Channels.SelectMany(c => c.Sends)];
            (double, bool)[] values = [.. sends.Select(s => (s.Level, s.Muted))];
            double[] volumes = [.. mixes.Select(m => m.Volume)];

            Directory.CreateDirectory(folder);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", data);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(data, "none"));
            File.WriteAllText(Path.Combine(folder, "skin.json"), """
                {"schema":1,"name":"Banks","tokens":{"Ox.Card.Background":"#30343c"},
                 "layout":{"version":1,"regions":{"submixer":{"template":"mix-banks","visibleBanks":3,
                   "selectionDefaults":["chat","monitor"],"master":{"template":"master-strip"}},
                   "monitor":{"template":"output-toolbar"},"applications":{"template":"application-list"}}}}
                """);
            SkinEntry banks = Assert.Single(SkinCatalog.Discover(), e => e.Id == "banks");
            Assert.Empty(banks.Errors);
            var panel = main.FindControl<OpenXLR.UI.Views.MixerPanel>("MixerPanel")!;
            foreach (int round in new[] { 1, 2 })
            {
                Assert.Empty(SkinService.Apply(banks));
                Pump(main);
                Layout(main, 1040, 900);
                Assert.NotNull(panel.Banks);
                Assert.True(panel.BankItems.IsEffectivelyVisible);
                Assert.Equal(["chat", "monitor", "stream"], panel.Banks!.Selection);
                foreach (var bank in panel.Banks.Banks)
                {
                    Assert.Contains(bank.Mix!, mixes);
                    foreach (var cell in bank.Cells) Assert.Contains(cell.Send!, sends);
                }
                // Colours alone leave the banks as they are.
                var before = panel.Banks;
                SkinService.ChooseControlSizing(SkinService.TouchControls, out _);
                Pump(main);
                Assert.Same(before, panel.Banks);
                Capture(main, $"layout-banks-{round}");

                Assert.Empty(SkinService.Apply(SkinCatalog.Find("opendeck")!));
                Pump(main);
                Layout(main, 1040, 900);
                Assert.Null(panel.Banks);
                Assert.False(panel.BankItems.IsVisible);
            }
            SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
            Pump(main);

            // Each matrix skin to Material and back at narrow and wide sizes: its matrix hands out the
            // same mixes and sends, every cell the channel's own, and sends nothing.
            SkinEntry broadcast = Assert.Single(SkinCatalog.BuiltIn(), e => e.Id == "broadcast");
            foreach (SkinEntry matrixSkin in new[] { broadcast, SkinCatalog.Find("wave")! })
            foreach (double width in new[] { 640d, 1040, 1920 })
            {
                Assert.Empty(SkinService.Apply(matrixSkin));
                Pump(main);
                Layout(main, width, 900);
                Assert.Null(panel.Banks);
                Assert.Null(main.InputRackModel);
                Assert.NotNull(panel.Matrix);
                Assert.Same(main.MixMatrix, panel.Matrix);
                Assert.True(panel.MatrixItems.IsEffectivelyVisible);
                Assert.Equal(mixes, panel.Matrix!.Columns.Select(c => c.Mix));
                Assert.Equal(channels, panel.Matrix.Rows.Select(r => r.Channel));
                foreach (var row in panel.Matrix.Rows)
                    foreach (var cell in row.Cells) Assert.Same(row.Channel.Sends.Single(s => s.MixId == cell.Mix.Id), cell.Send);
                // Colours alone leave the matrix as it is.
                var matrix = panel.Matrix;
                SkinService.ChooseControlSizing(SkinService.TouchControls, out _);
                Pump(main);
                Assert.Same(matrix, panel.Matrix);
                Capture(main, $"layout-{matrixSkin.Id}-{width:0}");
                Assert.Empty(SkinService.Apply(new SkinEntry(SkinPackage.Default, [])));
                Pump(main);
                Layout(main, 1040, 900);
                Assert.Null(panel.Matrix);
                Assert.False(panel.MatrixItems.IsVisible);
                Assert.Equal(channels, vm.Channels);
                Assert.Equal(mixes, vm.Mixes);
                Assert.Equal(sends, vm.Channels.SelectMany(c => c.Sends));
            }
            // A palette-only skin keeps the classic strips and every send.
            foreach (double width in new[] { 640d, 1040, 1920 })
            {
                Assert.Empty(SkinService.Apply(SkinCatalog.Find("nyan-pink")!));
                Pump(main);
                Layout(main, width, 900);
                Assert.Null(panel.Matrix);
                Assert.Null(panel.Banks);
                Assert.Equal(channels, vm.Channels);
                Assert.Equal(mixes, vm.Mixes);
                Assert.Equal(sends, vm.Channels.SelectMany(c => c.Sends));
                Capture(main, $"layout-nyan-pink-{width:0}");
                Assert.Empty(SkinService.Apply(new SkinEntry(SkinPackage.Default, [])));
                Pump(main);
            }
            // Banks straight to the matrix and back: one presentation at a time, the same objects.
            Assert.Empty(SkinService.Apply(banks));
            Pump(main);
            Assert.Empty(SkinService.Apply(broadcast));
            Pump(main);
            Layout(main, 1040, 900);
            Assert.Null(panel.Banks);
            Assert.NotNull(panel.Matrix);
            Assert.False(panel.BankItems.IsVisible);
            Assert.Empty(SkinService.Apply(banks));
            Pump(main);
            Layout(main, 1040, 900);
            Assert.Null(panel.Matrix);
            Assert.NotNull(panel.Banks);
            SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
            Pump(main);

            Assert.Equal(channels, vm.Channels);
            Assert.Equal(mixes, vm.Mixes);
            Assert.Equal(sends, vm.Channels.SelectMany(c => c.Sends));
            Assert.Equal(values, sends.Select(s => (s.Level, s.Muted)));
            Assert.Equal(volumes, mixes.Select(m => m.Volume));
            // A layout edit waits for its answer, so everything sent before it has arrived.
            Assert.Null(Await(client.DeleteExclusiveGroupAsync("barrier")));
            string[] sent = [.. commands];
            Assert.Equal("deleteExclusiveGroup", sent[^1]);
            // The model asks for the plugin catalogue when it connects; nothing else may have been sent.
            Assert.All(sent[..^1], cmd => Assert.True(cmd.StartsWith("list", StringComparison.Ordinal) || cmd.StartsWith("get", StringComparison.Ordinal),
                $"the skin switch sent {cmd}"));
        }
        finally
        {
            main.DataContext = context;
            SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
            Pump(main);
            Await(client.DisposeAsync().AsTask().ContinueWith(_ => true));
            Await(server.DisposeAsync().AsTask().ContinueWith(_ => true));
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", oldHome);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", oldDirs);
            if (Directory.Exists(data)) Directory.Delete(data, recursive: true);
        }
    }

    /// <summary>
    /// An upright meter reads the scale a flat one does, turned: cells of whole
    /// pixels from the bottom edge up, the top cell of a full meter in the hot
    /// colour, and a continuous bar lit from the bottom to its level.
    /// </summary>
    private static void AnUprightMeterFillsFromTheBottomOnTheSameScale()
    {
        var ground = new SolidColorBrush(Color.FromRgb(0, 0, 0));
        var meter = new LevelMeter
        {
            Orientation = Avalonia.Layout.Orientation.Vertical,
            Presentation = MeterPresentation.Segmented, Segments = 20, SegmentGap = 2, Level = 0.5,
            Width = 7, Height = 140, CornerRadius = new CornerRadius(1),
            Track = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)),
            Fill = new SolidColorBrush(Color.FromRgb(0x3e, 0xcf, 0x7a)),
            Hot = new SolidColorBrush(Color.FromRgb(0xff, 0x3c, 0x4e)),
        };
        var host = new Border { Background = ground, Child = meter };
        var probe = new Window { Content = host, Width = 7, Height = 140, WindowDecorations = WindowDecorations.None };
        probe.Show();
        Layout(probe, 7, 140);
        try
        {
            var size = new PixelSize((int)host.Bounds.Width, (int)host.Bounds.Height);
            int column = size.Width / 2;
            bool Green(byte[] p, int y) { int i = (y * size.Width + column) * 4; return p[i + 1] > 0x60 && p[i + 2] < 0xa0; }
            byte[] pixels = Read(host, size);
            var runs = new List<(int Start, int End)>();
            bool inRun = false;
            for (int y = size.Height - 1; y >= 0; y--)
            {
                bool lit = Green(pixels, y);
                if (lit && !inRun) { runs.Add((y, y)); inRun = true; }
                else if (lit) runs[^1] = (runs[^1].Start, y);
                else inRun = false;
            }
            Assert.Equal(10, runs.Count);
            Assert.Equal(size.Height - 1, runs[0].Start);     // the bottom cell is not shaved
            Assert.True(runs[^1].End > size.Height / 2 - 8, "the lit cells run up from the bottom, not down from the top");
            int[] heights = [.. runs.Select(r => r.Start - r.End + 1)];
            Assert.True(heights.Max() == heights.Min(), $"cells came out uneven: {string.Join(",", heights)}");

            meter.Level = 1;
            Layout(probe, 7, 140);
            pixels = Read(host, size);
            int top = column * 4;
            Assert.True(pixels[top + 1] is > 0x20 and < 0x60 && (pixels[top] > 0xc0 || pixels[top + 2] > 0xc0),
                "the top cell of a full upright meter is not in the peak colour");

            meter.Presentation = MeterPresentation.Continuous;
            meter.Level = 0.5;
            Layout(probe, 7, 140);
            pixels = Read(host, size);
            Assert.True(Green(pixels, size.Height - 2), "a continuous upright bar is not lit at the bottom");
            Assert.True(Green(pixels, size.Height / 2 + 2), "a continuous upright bar at half is not lit below the middle");
            Assert.False(Green(pixels, size.Height / 2 - 4), "a continuous upright bar at half is lit above the middle");
            Assert.False(Green(pixels, 1), "a continuous upright bar at half is lit at the top");
        }
        finally { probe.Close(); }
    }

    /// <summary>A plain opaque PNG, written by hand so the test carries no binary.</summary>
    private static void WritePng(string path, int width, int height)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height));
        using (var context = bitmap.CreateDrawingContext())
            context.DrawRectangle(new SolidColorBrush(Color.Parse("#384a38")), null,
                new Rect(0, 0, width, height));
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }

    private static void ABrokenSkinLeavesTheWindowUsableAndSaysWhy(MainWindow main)
    {
        {
            string folder = Path.Combine(Path.GetTempPath(), "openxlr-badskin-" + Guid.NewGuid());
            Directory.CreateDirectory(folder);
            try
            {
                File.WriteAllText(Path.Combine(folder, "skin.json"), """
                    {"schema":1,"name":"Bad","tokens":{
                      "Ox.Card.Background": "#2b2b2b",
                      "Ox.Text.Primary": "not a colour",
                      "Ox.Meter.Height": 9000,
                      "Ox.Nonsense": "#ff0000",
                      "Ox.Tile.Background": {"type":"image","source":"../../../etc/passwd"}
                    }}
                    """);
                SkinEntry entry = SkinCatalog.Read(folder, "bad", SkinOrigin.User)!;
                IReadOnlyList<string> errors = SkinService.Apply(entry);
                Pump(main);

                Assert.Equal(4, errors.Count);
                // The one good value applied; everything else kept its default.
                var card = Cards(main).First();
                Assert.Equal(Color.Parse("#2b2b2b"), ((ISolidColorBrush)card.Background!).Color);
                TextBlock label = main.GetVisualDescendants().OfType<TextBlock>()
                    .First(t => t.Classes.Contains("title"));
                Assert.Equal(Color.Parse("#e6e9f0"), ((ISolidColorBrush)label.Foreground!).Color);
                JsonObject shared = (JsonObject)JsonNode.Parse(File.ReadAllText(
                    OpenXLR.UI.OpenXlrPaths.ConfigFile("deck-palette.json")))!;
                Assert.Equal("bad", shared["skin"]!.GetValue<string>());
                Assert.Equal(((ISolidColorBrush)card.Background!).Color,
                    Color.Parse(shared["tokens"]!["Ox.Card.Background"]!.GetValue<string>()));
                Assert.Equal(((ISolidColorBrush)label.Foreground!).Color,
                    Color.Parse(shared["tokens"]!["Ox.Text.Primary"]!.GetValue<string>()));
                LevelMeter meter = main.GetVisualDescendants().OfType<LevelMeter>().First();
                Assert.Equal(4, meter.Bounds.Height);
                Assert.Equal(MeterPresentation.Continuous, meter.Presentation);
            }
            finally
            {
                SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    private static void NeitherShippedAppearanceBreaksTheLayoutOrTheLabels(
        MainWindow main, OptionsWindow options, FlowWindow flow)
    {
        {
            // What a skin must not do is make a control harder to hit than the
            // shipped appearance makes it, so the default is measured first and
            // the skin is held to it.
            var smallest = new Dictionary<string, double>();
            foreach (string id in new[] { "default", "opendeck" })
            {
                SkinService.Apply(SkinCatalog.Find(id)!);
                Pump(main);
                foreach (double width in new[] { 640d, 1040, 1800 })
                {
                    Layout(main, width, 900);
                    string where = $"{id} at {width}";

                    // The seven input toggles stay usable and inside the window.
                    foreach (var row in new[] { main.FindControl<WrapPanel>("InputControls")!, main.FindControl<WrapPanel>("Input2Controls")! })
                        foreach (var toggle in row.Children.Where(c => c.IsVisible))
                        {
                            Assert.True(toggle.Bounds.Width > 40, $"a toggle shrank to {toggle.Bounds.Width}. {where}");
                            AssertInside(toggle, main, where);
                            Smallest(smallest, id, $"toggle at {width}", toggle.Bounds.Height);
                        }

                    // Faders keep a draggable cap and a hittable track.
                    foreach (Slider slider in main.GetVisualDescendants().OfType<Slider>().Where(s => s.IsVisible))
                    {
                        AssertInside(slider, main, where);
                        Smallest(smallest, id, $"fader at {width}", slider.Bounds.Height);
                    }

                    // Meters keep a usable size whichever presentation they wear.
                    foreach (LevelMeter meter in main.GetVisualDescendants().OfType<LevelMeter>()
                                 .Where(m => m.IsVisible))
                    {
                        Assert.InRange(meter.Bounds.Height, 2, 24);
                        Assert.True(meter.Bounds.Width > 0, where);
                    }
                }

                // The names a screen reader reads do not come from the skin.
                Assert.Contains(main.GetVisualDescendants().OfType<ToggleButton>(),
                    t => Avalonia.Automation.AutomationProperties.GetName(t) == Localizer.Text("BypassThisPlugin"));
                Assert.NotNull(options.FindControl<ComboBox>("SkinPicker"));
                Assert.Equal(Localizer.Text("Skin"), Avalonia.Automation.AutomationProperties
                    .GetName(options.FindControl<ComboBox>("SkinPicker")!));

                // A focused control still shows a focus adorner.
                var focusable = main.GetVisualDescendants().OfType<Slider>().First(s => s.IsVisible);
                focusable.Focus();
                Pump(main);
                Assert.True(focusable.IsFocused, $"focus was refused in {id}");

                Capture(main, $"mixer-{id}");
                foreach (OptionsTab tab in Enum.GetValues<OptionsTab>())
                {
                    OptionsTabsWindowTests.Show(options, tab);
                    Pump(options);
                    Capture(options, $"options-{id}-{tab.ToString().ToLowerInvariant()}");
                }
                Capture(flow, $"flow-{id}");
            }
            SkinService.Apply(new SkinEntry(SkinPackage.Default, []));

            foreach (string what in smallest.Keys.Where(k => k.StartsWith("default:", StringComparison.Ordinal)))
            {
                double shipped = smallest[what];
                double skinned = smallest["opendeck:" + what["default:".Length..]];
                Assert.True(skinned >= shipped - 0.5,
                    $"the skin shrank the {what["default:".Length..]} from {shipped} to {skinned}");
            }
        }
    }

    /// <summary>
    /// Every shipped palette keeps the classic window's labels whole and
    /// readable. These checks name the classic controls, so a skin with a
    /// layout is worn here with its colours and control appearances under a
    /// different id and without its arrangement; Broadcast's own arrangement
    /// is checked, labels included, by the skin layout test.
    /// </summary>
    private static void EveryBuiltInSkinKeepsTheMainLabelsReadable(MainWindow main)
    {
        try
        {
            foreach (SkinEntry entry in new[] { new SkinEntry(SkinPackage.Default, []) }.Concat(SkinCatalog.BuiltIn()))
            {
                SkinEntry skin = entry.Package.Layout is null
                    ? entry
                    : entry with { Package = entry.Package with { Id = entry.Id + "-palette", Layout = null } };
                SkinService.ApplyTextSize(100);
                Assert.Empty(SkinService.Apply(skin));
                double normal = (double)main.FindResource("Ox.Label.FontSize")!;
                foreach (double width in new[] { 640d, 1040, 2560 })
                {
                    Layout(main, width, 900);
                    MainWindowLabelTests.Check(main);
                    WindowLayoutTests.AssertMonitorMute(main);
                    if (width == 640) MainWindowLabelTests.CheckLongDeviceLabels(main);
                    Capture(main, $"labels-{skin.Id}-{width}");
                }
                SkinService.ApplyTextSize(80);
                Assert.Equal(normal * .8, (double)main.FindResource("Ox.Label.FontSize")!, .01);
                SkinService.ApplyTextSize(120);
                Layout(main, 640, 900);
                Assert.Equal(normal * 1.2, (double)main.FindResource("Ox.Label.FontSize")!, .01);
                MainWindowLabelTests.Check(main);
                WindowLayoutTests.AssertMonitorMute(main);
            }
        }
        finally
        {
            SkinService.ApplyTextSize(100);
            SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
        }
    }

    /// <summary>Keep the smallest measurement of one kind of control per skin.</summary>
    private static void Smallest(Dictionary<string, double> into, string skin, string what, double value)
    {
        string key = $"{skin}:{what}";
        into[key] = into.TryGetValue(key, out double seen) ? Math.Min(seen, value) : value;
    }

    /// <summary>
    /// The control appearances a skin selects: that the shape really changes,
    /// that it changes back, and that the fader the console appearance draws is
    /// still the framework's fader underneath. The template hands Track, the
    /// two repeat buttons and the Thumb to Slider under the names it looks for,
    /// so dragging and the keyboard have to keep working; if they did not, a
    /// skin would be able to break a mixer control, which is the whole risk of
    /// owning a template.
    /// </summary>
    private static void ControlAppearancesSwitchAndTheFaderStillWorks(MainWindow main)
    {
        Slider fader = main.GetVisualDescendants().OfType<Slider>().First(s => s.IsVisible);
        LevelMeter meter = main.GetVisualDescendants().OfType<LevelMeter>().First();
        StatusLed led = main.GetVisualDescendants().OfType<StatusLed>().First();
        // A control that is never shown is never measured, so it has no
        // template to inspect; take one the window actually draws.
        ToggleButton mute = main.GetVisualDescendants().OfType<ToggleButton>()
            .First(t => t.Classes.Contains("mute") && t.IsVisible);

        // The shipped appearance publishes no control theme at all.
        Assert.Null(fader.Theme);
        Assert.Null(mute.Theme);
        Assert.Equal(MeterPresentation.Continuous, meter.Presentation);
        Assert.Equal(LedPresentation.Flat, led.Presentation);
        Assert.DoesNotContain(main.GetVisualDescendants().OfType<Thumb>(),
            t => t.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Rectangle>().Any());

        SkinService.Apply(SkinCatalog.Find("opendeck")!);
        Pump(main);

        Assert.NotNull(fader.Theme);
        Assert.NotNull(mute.Theme);
        Assert.Equal(MeterPresentation.Segmented, meter.Presentation);
        Assert.Equal(14, meter.Segments);
        // The ladder is coloured by position, so its zones exist whatever the
        // reading is: the amber and red cells are not the same brush as green.
        Assert.NotNull(meter.Warning);
        Assert.NotEqual(((ISolidColorBrush)meter.Fill!).Color, ((ISolidColorBrush)meter.Warning!).Color);
        Assert.NotEqual(((ISolidColorBrush)meter.Warning!).Color, ((ISolidColorBrush)meter.Hot!).Color);
        Assert.InRange(meter.WarningLevel, 0.01, meter.HotLevel);
        Assert.InRange(meter.HotLevel, meter.WarningLevel, 1);
        Assert.Equal(LedPresentation.Lamp, led.Presentation);
        Assert.True(led.Glow > 0, "the lamp has no halo");
        // The cap the console appearance draws carries its grip line.
        Thumb thumb = main.GetVisualDescendants().OfType<Thumb>().First();
        Assert.NotEmpty(thumb.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Rectangle>());
        // The bevel the cap variant lays over a mute button.
        Assert.Contains(mute.GetVisualDescendants().OfType<Border>(), b => b.Name == "Bevel");

        // The parts Slider drives are all there, under the names it looks for.
        Assert.NotNull(fader.GetVisualDescendants().OfType<Track>().FirstOrDefault(t => t.Name == "PART_Track"));
        Assert.NotNull(fader.GetVisualDescendants().OfType<RepeatButton>()
            .FirstOrDefault(b => b.Name == "PART_DecreaseButton"));
        Assert.NotNull(fader.GetVisualDescendants().OfType<RepeatButton>()
            .FirstOrDefault(b => b.Name == "PART_IncreaseButton"));

        // The keyboard still moves it.
        double before = fader.Value;
        fader.Focus();
        Pump(main);
        fader.RaiseEvent(new KeyEventArgs
            { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right, KeyModifiers = KeyModifiers.None });
        Pump(main);
        Assert.True(fader.Value > before, $"the keyboard did not move the console fader ({before} to {fader.Value})");

        // And Slider found every part it drives. These are the fields it wires
        // its paging and its dragging to; a template that renamed one of them
        // would leave the groove dead with nothing else to show for it.
        foreach (string part in new[] { "_track", "_decreaseButton", "_increaseButton" })
            Assert.NotNull(typeof(Slider)
                .GetField(part, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fader));

        double paged = fader.Value;
        // And the Track still places the cap from the value, which is what
        // makes a drag land where the pointer is.
        Track track = fader.GetVisualDescendants().OfType<Track>().First(t => t.Name == "PART_Track");
        Thumb cap = fader.GetVisualDescendants().OfType<Thumb>().First();
        fader.Value = fader.Minimum;
        Layout(main, main.Width, main.Height);
        double atLeft = cap.TranslatePoint(default, track)!.Value.X;
        fader.Value = fader.Maximum;
        Layout(main, main.Width, main.Height);
        double atRight = cap.TranslatePoint(default, track)!.Value.X;
        Assert.InRange(atLeft, -0.5, 1.5);
        Assert.InRange(atRight, track.Bounds.Width - cap.Bounds.Width - 1.5, track.Bounds.Width);
        fader.Value = paged;

        SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
        Pump(main);
        Assert.Null(fader.Theme);
        Assert.Null(mute.Theme);
        Assert.Equal(MeterPresentation.Continuous, meter.Presentation);
        Assert.Equal(LedPresentation.Flat, led.Presentation);
        // The framework's fader is back, and still works.
        double restored = fader.Value;
        fader.Focus();
        fader.RaiseEvent(new KeyEventArgs
            { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Left, KeyModifiers = KeyModifiers.None });
        Pump(main);
        Assert.True(fader.Value < restored, "the keyboard stopped working after the skin was taken off");
    }

    /// <summary>A Mic setup on its Listen step with a session, a take and every note showing, so the skin reaches them all.</summary>
    private static MicSetupViewModel ListeningSetup(DaemonClient client)
    {
        var check = new MicSetupViewModel(client, "xlr1") { Step = MicSetupStep.Listen, ProDirect = true };
        check.Apply(JsonNode.Parse("""{"channel":"xlr1","mode":"looping","seconds":1.5,"error":"A refusal","raw":true,"take":1,"monitorLatencyMs":42}"""));
        check.ApplyTake(JsonNode.Parse("""{"type":"soundCheckTake","channel":"xlr1","take":1,"seconds":1.5,"peaks":[0.1,0.5,0.9,0.3]}"""));
        check.ApplyPosition(new JsonArray(0.4));
        return check;
    }

    /// <summary>
    /// A Mic setup on its Shape step with the unit's filters, XLR 1's software
    /// ones, an EQ, a preset status and the chain's plugins showing.
    /// </summary>
    private static MicSetupViewModel ShapingSetup(MainViewModel vm, DaemonClient client, MicEqViewModel eq)
    {
        var noise = new MicNoiseViewModel(client, "xlr1", "XLR 1");
        noise.Apply(System.Text.Json.Nodes.JsonNode.Parse("""
            {"enabled":true,"engine":"rnnoise","strength":30,"engines":["rnnoise","deepfilternet"],
             "active":"rnnoise","latencyMs":30,"available":true,"error":null}
            """));
        var check = new MicSetupViewModel(client, "xlr1", vm.Inserts) { Unit = vm.UnitInput1, Software = vm, Eq = eq, Noise = noise };
        check.Place(MicSetupUnitKind.XlrJack, "Elgato Wave XLR Pro", 1);
        check.Step = MicSetupStep.Shape;
        return check;
    }

    /// <summary>A Mic setup on its Position step for a microphone on an XLR jack, with phantom power to switch.</summary>
    private static MicSetupViewModel PlacingSetup(MainViewModel vm, DaemonClient client)
    {
        var check = new MicSetupViewModel(client, "xlr1") { Unit = vm.UnitInput1 };
        check.Place(MicSetupUnitKind.XlrJack, "Elgato Wave XLR Pro", 1);
        return check;
    }

    /// <summary>
    /// The windows the mixer opens on demand, each built and checked: no
    /// surface is left with the shipped colour, and no text is left with no
    /// colour at all, which is what a token name typed wrong would look like.
    /// </summary>
    private static void EveryWindowWearsTheSkin(MainWindow main)
    {
        var vm = (MainViewModel)main.DataContext!;
        var client = new DaemonClient();
        using var eq = new MicEqViewModel(client, "xlr1", "XLR 1");
        vm.ApplyWaveInterfaces(JsonNode.Parse("""
            [{"id":"0fd9:00b4@0123456789abcdef","name":"Elgato Wave XLR Pro (1-2)","active":false,"enabled":true,"connected":true,
              "captureHint":"Wave_XLR_Pro_A1-","captureModelHint":"","warning":"A unit warning",
              "capabilities":{"gain":true,"mute":true,"phantom":true,"lowCut":true,"clipGuard":true,"xlrInputs":2},
              "state":{"gainDb":30,"gain2Db":20}},
             {"id":"0fd9:007d@fedcba9876543210","name":"Elgato Wave XLR (1-3)","active":true,"enabled":false,"connected":true,
              "captureHint":"Wave_XLR_B2-","captureModelHint":"","capabilities":{"gain":true},"state":{"gainDb":30}}]
            """));
        (string name, Window window)[] windows =
        [
            ("about", new AboutWindow()),
            ("apps", new AppsWindow { DataContext = vm }),
            ("browser-tabs", new BrowserTabsWindow { DataContext = vm }),
            ("layout", new MixerSetupWindow { DataContext = vm }),
            ("exclusive-groups", new ExclusiveGroupsWindow(vm)),
            ("ducking", new DuckingWindow { DataContext = vm.Ducking }),
            ("soundboard", new SoundboardWindow { DataContext = vm.Soundboard }),
            ("voice-changer", new VoiceChangerWindow { DataContext = vm.VoiceChanger }),
            ("compressor", new DynamicsWindow { DataContext = new MicDynamicsViewModel(client, MicDynamicsViewModel.CompressorKind, "xlr1", "XLR 1") }),
            ("expander", new DynamicsWindow { DataContext = new MicDynamicsViewModel(client, MicDynamicsViewModel.ExpanderKind, "xlr1", "XLR 1") }),
            ("mix-inserts", new MixInsertsWindow { DataContext = vm.Mixes[0].Inserts }),
            ("insert-controls", new InsertControlsWindow { DataContext = vm.Inserts.Items[0] }),
            ("plugin-picker", new PluginPickerWindow { DataContext = vm.Inserts }),
            ("plugin-presets", new PluginPresetWindow(vm.Inserts.Items[0])),
            ("effect-workflow", new EffectWorkflowWindow { DataContext = vm.Inserts }),
            ("native-editors", new NativeEditorRulesWindow()),
            ("plugin-folders", new PluginFoldersWindow()),
            ("mic-setup", new MicSetupWindow { DataContext = vm.Inserts.MicSetup }),
            ("mic-setup-listen", new MicSetupWindow { DataContext = ListeningSetup(client) }),
            ("mic-setup-position", new MicSetupWindow { DataContext = PlacingSetup(vm, client) }),
            ("mic-setup-shape", new MicSetupWindow { DataContext = ShapingSetup(vm, client, eq) }),
            ("updates", new UpdatesWindow { DataContext = vm.Updates }),
            ("wave-interfaces", new WaveInterfacesWindow(vm)),
            ("hardware-mixing", new HardwareMixingWindow(new HardwareMixingConfirmViewModel(vm.HardwareMixing))),
        ];
        try
        {
            SkinService.Apply(SkinCatalog.Find("opendeck")!);
            foreach ((string name, Window window) in windows)
            {
                window.Show();
                Pump(window);
                foreach (Border surface in window.GetVisualDescendants().OfType<Border>()
                             .Where(b => b.Classes.Contains("card") || b.Classes.Contains("tile")))
                    Assert.True(surface.Background is LinearGradientBrush,
                        $"{name}: a {(surface.Classes.Contains("card") ? "card" : "tile")} kept the shipped surface");

                foreach (TextBlock text in window.GetVisualDescendants().OfType<TextBlock>()
                             .Where(t => t.IsVisible && t.Text is { Length: > 0 }))
                    Assert.True(text.Foreground is not null,
                        $"{name}: \"{text.Text}\" has no colour, so a token name is wrong");

                Capture(window, $"window-{name}");
            }
        }
        finally
        {
            foreach ((_, Window window) in windows)
                try { window.Close(); } catch (Exception) { }
            SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
            vm.ApplyWaveInterfaces(null);
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// A token the window reads through a style of its own rather than through
    /// a theme key the token is bridged to. An application style beats the
    /// control theme, so the bridge alone left the checkbox label taking a
    /// different token and this one doing nothing: a value documented and dead.
    /// Every token a skin can set has to reach something.
    /// </summary>
    private static void ACheckBoxLabelTakesItsOwnToken(OptionsWindow options)
    {
        OptionsTabsWindowTests.Show(options, OptionsTab.General);
        CheckBox box = options.GetVisualDescendants().OfType<CheckBox>().First();
        Color shipped = ((ISolidColorBrush)box.Foreground!).Color;
        try
        {
            SkinReadResult read = SkinReader.Read("probe",
                """{"schema":1,"name":"Probe","tokens":{"Ox.Check.Foreground":"#ff00ff"}}""",
                SkinOrigin.BuiltIn, null);
            SkinService.Apply(new SkinEntry(read.Package!, []));
            Pump(options);
            Assert.Equal(Color.Parse("#ff00ff"), ((ISolidColorBrush)box.Foreground!).Color);
        }
        finally
        {
            SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
            Pump(options);
        }
        Assert.Equal(shipped, ((ISolidColorBrush)box.Foreground!).Color);
    }

    /// <summary>
    /// The proof that a skin change reaches the pixels and not only the
    /// properties. The indicator brushes repaint in place rather than being
    /// replaced, so nothing invalidates the custom-drawn meters and lamps
    /// unless the controls listen for the change; this is what would catch
    /// that going wrong.
    /// </summary>
    private static void TheWindowActuallyRepaintsWhenTheSkinChanges(MainWindow main)
    {
        SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
        Layout(main, 1040, 900);
        byte[] first = Pixels(main);
        byte[] again = Pixels(main);
        Assert.Equal(first, again);   // the same appearance draws the same window

        SkinService.Apply(SkinCatalog.Find("opendeck")!);
        Layout(main, 1040, 900);
        Assert.NotEqual(first, Pixels(main));

        SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
        Layout(main, 1040, 900);
        Assert.Equal(first, Pixels(main));   // and taking it off puts the window back
    }

    private static byte[] Pixels(Window window)
    {
        var size = new PixelSize(Math.Max(1, (int)window.Bounds.Width), Math.Max(1, (int)window.Bounds.Height));
        using var bitmap = new RenderTargetBitmap(size);
        bitmap.Render(window);
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    /// <summary>
    /// The lamp and the segmented meter draw themselves, so nothing but this
    /// keeps them inside the box the layout gave them. The halo used to be
    /// drawn wider than the control: a rounded card or a clipping row then cut
    /// a slice out of the indicator, which is what a lamp at the left edge of
    /// an insert row showed. The meter's cells used to land on fractional
    /// positions, which made the first and last one look shaved.
    /// </summary>
    private static void IndicatorsAndMetersDrawInsideTheBoxTheyAreGiven()
    {
        SkinService.Apply(SkinCatalog.Find("opendeck")!);
        var ground = new SolidColorBrush(Color.FromRgb(0, 0, 0));

        // The lamp, with the widest halo a skin may ask for, inside a host that
        // leaves a margin all round. Anything painted in that margin is a part
        // of the indicator a real window would have cut off.
        var led = new StatusLed
        {
            IsOn = true, Presentation = LedPresentation.Lamp, Width = 11, Height = 11,
            OnBrush = new SolidColorBrush(Color.FromRgb(0x3e, 0xcf, 0x7a)),
            Bezel = new SolidColorBrush(Color.FromRgb(0x0a, 0x0b, 0x0d)),
            BezelThickness = 1.5, Glow = 1, CoreScale = 0.55,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        const int margin = 8;
        var host = new Border { Background = ground, Padding = new Thickness(margin), Child = led };
        var probe = new Window { Content = host, Width = 11 + margin * 2, Height = 11 + margin * 2, WindowDecorations = WindowDecorations.None };
        probe.Show();
        Layout(probe, 11 + margin * 2, 11 + margin * 2);
        try
        {
            var size = new PixelSize((int)host.Bounds.Width, (int)host.Bounds.Height);
            byte[] pixels = Read(host, size);
            Rect box = new(led.TranslatePoint(default, host)!.Value, led.Bounds.Size);
            var outside = new List<string>();
            for (int y = 0; y < size.Height; y++)
                for (int x = 0; x < size.Width; x++)
                {
                    int i = (y * size.Width + x) * 4;
                    if (pixels[i] == 0 && pixels[i + 1] == 0 && pixels[i + 2] == 0) continue;
                    if (x + 1 <= box.X || x >= box.Right || y + 1 <= box.Y || y >= box.Bottom)
                        outside.Add($"({x},{y})");
                }
            Assert.True(outside.Count == 0,
                $"the lamp painted {outside.Count} pixels outside its {box} box: {string.Join(" ", outside.Take(8))}");
            // And it did paint something, so the check above is not vacuous.
            Assert.Contains(Enumerable.Range(0, size.Width * size.Height),
                i => pixels[i * 4] != 0 || pixels[i * 4 + 1] != 0 || pixels[i * 4 + 2] != 0);
        }
        finally { probe.Close(); }

        // The meter's cells: every one a whole number of pixels, the first
        // starting at the left edge, the last reaching the right one.
        var meter = new LevelMeter
        {
            Presentation = MeterPresentation.Segmented, Segments = 20, SegmentGap = 2, Level = 0.5,
            Width = 140, Height = 7, CornerRadius = new CornerRadius(1),
            Track = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)),
            Fill = new SolidColorBrush(Color.FromRgb(0x3e, 0xcf, 0x7a)),
            Hot = new SolidColorBrush(Color.FromRgb(0xff, 0x3c, 0x4e)),
        };
        var meterHost = new Border { Background = ground, Child = meter };
        var meterProbe = new Window { Content = meterHost, Width = 140, Height = 7, WindowDecorations = WindowDecorations.None };
        meterProbe.Show();
        Layout(meterProbe, 140, 7);
        try
        {
            var size = new PixelSize((int)meterHost.Bounds.Width, (int)meterHost.Bounds.Height);
            byte[] pixels = Read(meterHost, size);
            int row = size.Height / 2;
            // Runs of lit cells across the middle of the bar.
            var runs = new List<(int Start, int End)>();
            bool inRun = false;
            for (int x = 0; x < size.Width; x++)
            {
                int i = (row * size.Width + x) * 4;
                bool lit = pixels[i + 1] > 0x60 && pixels[i + 2] < 0xa0;   // the green fill
                if (lit && !inRun) { runs.Add((x, x)); inRun = true; }
                else if (lit) runs[^1] = (runs[^1].Start, x);
                else inRun = false;
            }
            Assert.Equal(10, runs.Count);                    // half of twenty cells
            Assert.Equal(0, runs[0].Start);                  // the first cell is not shaved
            int[] widths = [.. runs.Select(r => r.End - r.Start + 1)];
            Assert.True(widths.Max() == widths.Min(),
                $"cells came out uneven: {string.Join(",", widths)}");

            // With the meter full, the last cell has to reach the right edge,
            // and a full meter is over the peak, so it reads in the hot colour.
            meter.Level = 1;
            Layout(meterProbe, 140, 7);
            pixels = Read(meterHost, size);
            int last = (row * size.Width + size.Width - 1) * 4;
            string tail = string.Join(",", Enumerable.Range(size.Width - 6, 6)
                .Select(x => $"{pixels[(row * size.Width + x) * 4]:x2}{pixels[(row * size.Width + x) * 4 + 1]:x2}"
                             + $"{pixels[(row * size.Width + x) * 4 + 2]:x2}"));
            // Red whichever way round the channels are stored; green is 0xcf
            // in the middle channel, the track is near black in all three.
            Assert.True(pixels[last + 1] is > 0x20 and < 0x60
                        && (pixels[last] > 0xc0 || pixels[last + 2] > 0xc0),
                $"the last cell of a full meter does not reach the right edge in the peak colour; tail={tail}");
        }
        finally { meterProbe.Close(); }

        SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
    }

    private static byte[] Read(Control control, PixelSize size)
    {
        using var bitmap = new RenderTargetBitmap(size);
        bitmap.Render(control);
        var buffer = new byte[size.Width * size.Height * 4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(
            buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), buffer.Length, size.Width * 4); }
        finally { handle.Free(); }
        return buffer;
    }

    /// <summary>
    /// A meter's colours belong to the scale, not to the reading. The window
    /// used to repaint the whole bar red once the level passed a threshold,
    /// which said "you are loud" by lying about how loud the quiet end was.
    /// Each cell now takes the colour of its own place on the scale, so the
    /// green end stays green at every level and the amber and red cells only
    /// light when the signal actually reaches them.
    ///
    /// The scale is the one OpenXLR.Core's MeterReader produces: RMS dBFS with
    /// 0 at -60 dB and 1 at 0 dB. The shipped OpenDeck thresholds are therefore
    /// -18 dBFS for amber and -6 dBFS for red.
    /// </summary>
    private void AMeterIsColouredByPositionAndNotByItsReading()
    {
        SkinService.Apply(SkinCatalog.Find("opendeck")!);
        var meter = new LevelMeter
        {
            Presentation = MeterPresentation.Segmented, Segments = 14, SegmentGap = 2,
            Width = 160, Height = 8, CornerRadius = new CornerRadius(4),
            Track = new SolidColorBrush(Color.FromRgb(0x0d, 0x0d, 0x0d)),
            Fill = new SolidColorBrush(Color.FromRgb(0x2e, 0xcc, 0x71)),
            Warning = new SolidColorBrush(Color.FromRgb(0xf2, 0xc1, 0x4e)),
            Hot = new SolidColorBrush(Color.FromRgb(0xff, 0x4b, 0x4b)),
            WarningLevel = 0.7, HotLevel = 0.9,
        };
        var host = new Border { Background = new SolidColorBrush(Color.FromRgb(0, 0, 0)), Child = meter };
        var probe = new Window { Content = host, Width = 160, Height = 8,
            WindowDecorations = WindowDecorations.None };
        probe.Show();
        try
        {
            // -42 dBFS: quiet. -12 dBFS: into the amber zone. -1.5 dBFS: into the red.
            foreach ((string name, double level, int green, int amber, int red) in new[]
                     {
                         // A cell lights when the signal reaches its far edge,
                         // and wears the colour of where that edge sits.
                         ("low", (-42.0 + 60) / 60, 5, 0, 0),
                         ("mid", (-12.0 + 60) / 60, 9, 3, 0),
                         ("high", (-1.5 + 60) / 60, 9, 3, 2),
                     })
            {
                meter.Level = level;
                Layout(probe, 160, 8);
                (int Green, int Amber, int Red) lit = CountCells(host);
                Assert.True((lit.Green, lit.Amber, lit.Red) == (green, amber, red),
                    $"at {name} ({level:F3}) the ladder lit {lit}, expected ({green}, {amber}, {red})");
                Capture(probe, $"meter-{name}");
            }

            // The green cells are the same green whether the meter is quiet or
            // pinned, which is the regression the old whole-bar recolour caused.
            meter.Level = 0.3;
            Layout(probe, 160, 8);
            int quietGreen = CountCells(host).Green;
            meter.Level = 1;
            Layout(probe, 160, 8);
            (int Green, int Amber, int Red) loud = CountCells(host);
            Assert.True(loud.Green >= quietGreen && loud.Green == 9,
                $"the top of the scale repainted the green cells: {loud}");
            Assert.True(loud.Red > 0, "a full meter lights no red cells");
        }
        finally { probe.Close(); SkinService.Apply(new SkinEntry(SkinPackage.Default, [])); }
    }

    /// <summary>
    /// A continuous bar is one bar, whatever zone it has reached. The shipped
    /// appearance paints all three zones the same green, so crossing -18 dBFS
    /// or -6 dBFS should change nothing about how the bar looks. Every span
    /// used to be rounded at both of its ends and to begin wherever the
    /// arithmetic put it rather than on a pixel, so a threshold cut a notch
    /// across the four-pixel bar and the monitor mixes read as a long bar
    /// followed by a short second one.
    /// </summary>
    private static void AContinuousBarIsWholeWhereItsZonesMeet()
    {
        var track = Color.FromRgb(0x14, 0x16, 0x1b);
        var green = Color.FromRgb(0x3e, 0xcf, 0x7a);
        var meter = new LevelMeter
        {
            Presentation = MeterPresentation.Continuous,
            Width = 212, Height = 4, CornerRadius = new CornerRadius(2),
            Track = new SolidColorBrush(track),
            Fill = new SolidColorBrush(green),
            Warning = new SolidColorBrush(green),
            Hot = new SolidColorBrush(green),
            WarningLevel = 0.7, HotLevel = 0.9,
        };
        var host = new Border { Background = new SolidColorBrush(Color.FromRgb(0, 0, 0)), Child = meter };
        var probe = new Window { Content = host, Width = 212, Height = 4,
            WindowDecorations = WindowDecorations.None };
        probe.Show();
        try
        {
            // -15.5 dBFS is past the amber threshold, -2 dBFS past the red one,
            // and a full bar is past both.
            foreach (double level in new[] { (-15.5 + 60) / 60, (-2.0 + 60) / 60, 1.0 })
            {
                meter.Level = level;
                Layout(probe, 212, 4);
                var size = new PixelSize((int)host.Bounds.Width, (int)host.Bounds.Height);
                byte[] pixels = Read(host, size);

                // How much of a column the fill covers, read off the middle
                // channel, which is green whichever way round the ends are
                // stored. A column between the bar's two rounded caps is
                // covered whole.
                double Covered(int x)
                {
                    double most = 0;
                    for (int y = 0; y < size.Height; y++)
                        most = Math.Max(most, (pixels[(y * size.Width + x) * 4 + 1] - track.G)
                                              / (double)(green.G - track.G));
                    return most;
                }

                const int cap = 2;   // the corner radius, where the caps curve away
                int last = Enumerable.Range(0, size.Width).Last(x => Covered(x) >= 0.5);
                Assert.True(last > size.Width / 2, $"at {level:F3} the bar hardly drew at all, ending at {last}");
                int[] gaps = [.. Enumerable.Range(cap, Math.Max(0, last - 2 * cap)).Where(x => Covered(x) < 0.9)];
                Assert.True(gaps.Length == 0,
                    $"at {level:F3} the bar is notched at "
                    + string.Join(" ", gaps.Take(8).Select(x => $"{x} ({Covered(x):P0} covered)")));
            }
        }
        finally { probe.Close(); }
    }

    /// <summary>How many cells of each zone colour are lit across the middle of a ladder.</summary>
    private static (int Green, int Amber, int Red) CountCells(Control host)
    {
        var size = new PixelSize((int)host.Bounds.Width, (int)host.Bounds.Height);
        byte[] pixels = Read(host, size);
        int row = size.Height / 2;
        int green = 0, amber = 0, red = 0;
        string last = "";
        for (int x = 0; x < size.Width; x++)
        {
            int i = (row * size.Width + x) * 4;
            // Channel order is not guaranteed, so the two ends are read as the
            // pair they are and the middle channel tells the zones apart.
            byte lo = Math.Min(pixels[i], pixels[i + 2]), hi = Math.Max(pixels[i], pixels[i + 2]);
            byte mid = pixels[i + 1];
            string zone = hi > 0xd0 && mid > 0x90 ? "amber"
                : hi > 0xd0 && mid < 0x80 ? "red"
                : mid > 0x90 && lo < 0x90 ? "green" : "";
            if (zone.Length > 0 && zone != last)
            {
                if (zone == "green") green++; else if (zone == "amber") amber++; else red++;
            }
            last = zone;
        }
        return (green, amber, red);
    }

    /// <summary>
    /// Every kind of button wears the same near-black face and says what it is
    /// doing with the colour of its lettering, rather than by filling itself in.
    /// </summary>
    private static void ButtonsCarryTheirStateInTheirLettering(MainWindow main)
    {
        SkinService.Apply(SkinCatalog.Find("opendeck")!);
        Layout(main, 1040, 900);
        IResourceDictionary resources = Application.Current!.Resources;

        Color Of(string key) => ((ISolidColorBrush)resources[key]!).Color;
        static bool Greenish(Color c) => c.G > 0xa0 && c.G > c.R + 0x40;
        static bool Reddish(Color c) => c.R > 0xc0 && c.R > c.G + 0x60;

        // A plain button, a switched-on toggle and a passing mute are lit green.
        Assert.True(Greenish(Of("ButtonForeground")), "a plain button is not lit");
        Assert.True(Greenish(Of("ToggleButtonForegroundChecked")), "a switched-on toggle is not lit");
        Assert.True(Greenish(Of("Ox.Mute.Foreground")), "a passing mute button is not lit");
        // A toggle that is off, a muted button, a bypassed plugin and a
        // destructive button all read red.
        Assert.True(Reddish(Of("ToggleButtonForeground")), "a switched-off toggle is not red");
        Assert.True(Reddish(Of("Ox.Mute.ForegroundChecked")), "a muted button is not red");
        Assert.True(Reddish(Of("Ox.Bypass.ForegroundBypassed")), "a bypassed plugin toggle is not red");
        Assert.True(Greenish(Of("Ox.Bypass.Foreground")), "a processing plugin toggle is not green");
        Assert.True(Reddish(Of("Ox.Danger.Foreground")), "a destructive button is not red");
        // The mute button is unchanged: green while audio passes, red when muted.
        Assert.True(Greenish(Of("Ox.Mute.Foreground")) && Reddish(Of("Ox.Mute.ForegroundChecked")),
            "the mute button no longer says green for passing and red for muted");
        // A key that cannot be used keeps the red and fades instead, so colour
        // alone never has to separate "off" from "unavailable".
        Assert.True(Reddish(Of("ToggleButtonForegroundDisabled")), "a disabled toggle is not red");
        Assert.True((double)Application.Current!.Resources["Ox.Control.DisabledOpacity"]! < 1,
            "a disabled key does not fade, so it reads the same as an unlit one");

        // No button fills itself with the state colour: every face stays dark.
        foreach (string key in new[] { "ButtonBackground", "ToggleButtonBackground",
                     "ToggleButtonBackgroundChecked", "ComboBoxBackground" })
        {
            object face = resources[key]!;
            Color[] stops = face switch
            {
                ISolidColorBrush solid => [solid.Color],
                IGradientBrush gradient => [.. gradient.GradientStops.Select(g => g.Color)],
                _ => [],
            };
            Assert.All(stops, c => Assert.True(Luminance(c) < 0.05,
                $"{key} has a stop at luminance {Luminance(c):F3}, which is a fill and not a face"));
        }

        // A plugin's bypass is checked when the plugin is out of the path, so
        // its lettering runs the other way round to an ordinary toggle. Tested
        // on a button of its own: nothing here touches an insert or its daemon.
        var bypass = new ToggleButton { Content = "B", Classes = { "insertmini", "bypass" } };
        var ordinary = new ToggleButton { Content = "Low Cut" };
        var host = new StackPanel { Children = { bypass, ordinary } };
        var probe = new Window { Content = host, Width = 120, Height = 90,
            WindowDecorations = WindowDecorations.None };
        probe.Show();
        try
        {
            static Color Letters(ToggleButton t) => ((ISolidColorBrush)t.GetVisualDescendants()
                .OfType<ContentPresenter>().First(c => c.Name == "PART_ContentPresenter").Foreground!).Color;

            Layout(probe, 120, 90);
            Assert.True(Greenish(Letters(bypass)), "a plugin that is processing is not lit green");
            Assert.True(Reddish(Letters(ordinary)), "a switched-off toggle is not red");

            bypass.IsChecked = true;
            ordinary.IsChecked = true;
            Layout(probe, 120, 90);
            Assert.True(Reddish(Letters(bypass)), "a bypassed plugin is not red");
            Assert.True(Greenish(Letters(ordinary)), "a switched-on toggle is not green");
        }
        finally { probe.Close(); }

        // And the window agrees: the muted mix's button is lettered in red.
        var vm = (MainViewModel)main.DataContext!;
        Assert.True(vm.Mixes[2].Muted, "the fixture's third mix is not muted");
        ToggleButton muted = main.GetVisualDescendants().OfType<ToggleButton>()
            .First(t => t.Classes.Contains("mute") && t.IsVisible && t.IsChecked == true
                        && t.DataContext == vm.Mixes[2]);
        ContentPresenter presenter = muted.GetVisualDescendants().OfType<ContentPresenter>()
            .First(c => c.Name == "PART_ContentPresenter");
        Assert.Equal(Of("Ox.Mute.ForegroundChecked"), ((ISolidColorBrush)presenter.Foreground!).Color);

        SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
    }

    /// <summary>
    /// One raised face for every rectangular control. The mute button used to
    /// be the only key with a bevel while the toggles, the actions and the
    /// dropdowns beside it stayed flat. They share one template and one gloss
    /// now, so this checks the face is really on all of them, that it comes
    /// off again with the skin, and that nothing the controls do was traded
    /// away for it.
    /// </summary>
    private static void EveryRectangularControlWearsTheSameFace(MainWindow main, OptionsWindow options)
    {
        // The keys: what a user sees as a rectangular button. Repeat buttons are
        // the two halves of a fader's groove, the toggle in a section header is
        // the header strip itself, and a checkbox is a tick beside a label.
        // None of those is a key, and each keeps its own appearance.
        static Control[] Rectangular(Visual root) =>
            [.. root.GetVisualDescendants().OfType<Control>()
                .Where(c => c is Button or ToggleButton or DropDownButton
                            && c is not RepeatButton and not CheckBox and not RadioButton
                            && c.IsEffectivelyVisible
                            && c.GetVisualDescendants().OfType<ContentPresenter>()
                                .Any(p => p.Name == "PART_ContentPresenter"))];

        static IBrush? Gloss(Control c) => c.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Name == "Bevel")?.Background;

        SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
        Layout(main, 1040, 900);
        Layout(options, OptionsWindow.DesignWidth, options.Height);
        foreach (Control control in Rectangular(main).Concat(OptionsTabsWindowTests.InEveryTab(options, Rectangular)))
            Assert.True(Gloss(control) is null,
                $"the shipped appearance gave a {control.GetType().Name} a raised face");

        SkinService.Apply(SkinCatalog.Find("opendeck")!);
        Layout(main, 1040, 900);
        Layout(options, OptionsWindow.DesignWidth, options.Height);

        var kinds = new HashSet<string>();
        var flat = new List<string>();
        IBrush? shared = null;
        foreach (Control control in Rectangular(main).Concat(OptionsTabsWindowTests.InEveryTab(options, Rectangular)))
        {
            string what = $"{control.GetType().Name} \"{(control as ContentControl)?.Content}\"";
            IBrush? gloss = Gloss(control);
            if (gloss is null) { flat.Add(what); continue; }
            kinds.Add(control.GetType().Name);
            // The same brush object, not merely the same colour: one gloss.
            shared ??= gloss;
            Assert.Same(shared, gloss);
        }
        Assert.True(flat.Count == 0, $"these kept a flat face: {string.Join(", ", flat.Take(6))}");
        // The plain action, the toggle, the mute and the dropdown all took it.
        Assert.Contains("Button", kinds);
        Assert.Contains("ToggleButton", kinds);
        Assert.Contains("DropDownButton", kinds);
        Assert.Contains(Rectangular(main).OfType<ToggleButton>(),
            t => t.Classes.Contains("mute") && Gloss(t) is not null);

        // The face did not cost the controls their behaviour.
        var dropDown = Rectangular(main).OfType<DropDownButton>().First();
        Assert.NotNull(dropDown.GetVisualDescendants().OfType<ContentPresenter>()
            .FirstOrDefault(c => c.Name == "PART_ContentPresenter"));
        Assert.NotNull(dropDown.Flyout);
        var action = Rectangular(main).OfType<Button>()
            .First(b => b is not ToggleButton and not DropDownButton && b.IsEffectivelyEnabled);
        int clicks = 0;
        void OnClick(object? sender, RoutedEventArgs args) => clicks++;
        Window[] before = main.OwnedWindows.ToArray();
        action.Click += OnClick;
        try
        {
            action.Focus();
            Pump(main);
            Assert.True(action.IsFocused, "a key no longer takes focus");
            action.RaiseEvent(new KeyEventArgs
                { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.None });
            Pump(main);
            Assert.Equal(1, clicks);
        }
        finally
        {
            action.Click -= OnClick;
            // The real action may open a dialog. Close the ones this probe
            // opened, so the X server input later in the run reaches the mixer.
            foreach (Window window in main.OwnedWindows.Except(before).ToArray()) window.Close();
            Pump(main);
        }

        // A toggle still toggles, and its lettering still follows the state.
        var toggle = Rectangular(main).OfType<ToggleButton>()
            .First(t => !t.Classes.Contains("mute") && t.IsEffectivelyEnabled && t.IsChecked == false);
        toggle.IsChecked = true;
        Layout(main, 1040, 900);
        ContentPresenter face = toggle.GetVisualDescendants().OfType<ContentPresenter>()
            .First(c => c.Name == "PART_ContentPresenter");
        Assert.Equal(((ISolidColorBrush)Application.Current!.Resources["ToggleButtonForegroundChecked"]!).Color,
            ((ISolidColorBrush)face.Foreground!).Color);
        toggle.IsChecked = false;
        Layout(main, 1040, 900);

        SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
        Layout(main, 1040, 900);
        foreach (Control control in Rectangular(main))
            Assert.True(Gloss(control) is null, "the raised face outlived the skin");
    }

    /// <summary>
    /// The mute button is the reference key. Every other rectangular control
    /// has to be drawn the same way: the same face, the same ring, the same
    /// square corners, in every state. Comparing brushes was not enough to
    /// catch this, because a checked toggle held a different brush with a
    /// green ring and a different border width and still passed; so the
    /// controls are rendered and their edges compared as pixels.
    /// </summary>
    private static void EveryKeyIsDrawnLikeTheMuteKey()
    {
        SkinService.Apply(SkinCatalog.Find("opendeck")!);
        const int w = 120, h = 30;

        var mute = new ToggleButton { Content = "Mute", Classes = { "mute" }, Width = w, Height = h };
        var toggle = new ToggleButton { Content = "Low Cut", Width = w, Height = h };
        var action = new Button { Content = "Flow", Width = w, Height = h };
        var ghost = new Button { Content = "About", Classes = { "ghost" }, Width = w, Height = h };
        var drop = new DropDownButton { Content = "Profiles", Width = w, Height = h };
        Control[] keys = [mute, toggle, action, ghost, drop];

        var panel = new StackPanel
        {
            Spacing = 6, Margin = new Thickness(6),
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x16)),
            Children = { mute, toggle, action, ghost, drop },
        };
        var probe = new Window { Content = panel, Width = w + 12, Height = keys.Length * (h + 6) + 12,
            WindowDecorations = WindowDecorations.None };
        probe.Show();
        try
        {
            Layout(probe, w + 12, keys.Length * (h + 6) + 12);
            foreach (Control key in keys)
                Assert.Equal(new Size(w, h), key.Bounds.Size);

            // The outline of each key, read off the rendered window.
            string Edges(Control key)
            {
                var size = new PixelSize(w, h);
                byte[] px = Read(key, size);
                string At(int x, int y)
                {
                    int i = (y * w + x) * 4;
                    return $"{px[i]:x2}{px[i + 1]:x2}{px[i + 2]:x2}a{px[i + 3]:x2}";
                }
                // The four corners and the middle of each side.
                return string.Join(" ", new[]
                {
                    At(0, 0), At(w - 1, 0), At(0, h - 1), At(w - 1, h - 1),
                    At(w / 2, 0), At(w / 2, h - 1), At(0, h / 2), At(w - 1, h / 2),
                });
            }

            static int Alpha(string pixel) => Convert.ToInt32(pixel[^2..], 16);
            string reference = Edges(mute);
            foreach (Control key in keys.Skip(1))
                Assert.True(Edges(key) == reference,
                    $"{key.GetType().Name} \"{(key as ContentControl)?.Content}\" is not drawn like the mute key:\n"
                    + $"  mute {reference}\n  this {Edges(key)}");

            // Square: the key paints its own corners. A rounded key leaves them
            // to whatever is behind it, which on its own is nothing at all.
            Assert.All(reference.Split(' ').Take(4),
                corner => Assert.True(Alpha(corner) > 0x80, $"a corner is not painted: {corner}"));

            // Every state keeps the same outline, so a checked toggle does not
            // grow a ring of its own.
            toggle.IsChecked = true;
            Layout(probe, w + 12, keys.Length * (h + 6) + 12);
            Assert.True(Edges(toggle) == reference,
                $"a switched-on toggle is ringed differently:\n  mute {reference}\n  this {Edges(toggle)}");
            mute.IsChecked = true;
            Layout(probe, w + 12, keys.Length * (h + 6) + 12);
            Assert.True(Edges(mute) == reference, "a muted key is ringed differently");
            toggle.IsChecked = false;
            mute.IsChecked = false;

            // A key that cannot be used keeps its shape and fades. That is the
            // cue that does not depend on colour, which matters because an
            // unlit toggle and an unavailable action are both lettered red.
            action.IsEnabled = false;
            toggle.IsEnabled = false;
            Layout(probe, w + 12, keys.Length * (h + 6) + 12);
            foreach (Control key in new Control[] { action, toggle })
            {
                string[] edges = Edges(key).Split(' ');
                Assert.All(edges.Take(4), corner => Assert.True(Alpha(corner) > 0x20,
                    $"a disabled {key.GetType().Name} lost its square corner"));
                Assert.True(Alpha(edges[0]) < Alpha(reference.Split(' ')[0]),
                    $"a disabled {key.GetType().Name} is as solid as an enabled one");

            }
            action.IsEnabled = true;
            toggle.IsEnabled = true;

            // Focus is visible, and the dropdown still opens and closes.
            drop.Focus();
            Pump(probe);
            Assert.True(drop.IsFocused, "a key no longer takes focus");
            drop.Flyout = new Flyout { Content = new TextBlock { Text = "profile" } };
            drop.Flyout.ShowAt(drop);
            Pump(probe);
            Assert.True(drop.Flyout.IsOpen, "the dropdown did not open");
            drop.Flyout.Hide();
            Pump(probe);
            Assert.False(drop.Flyout.IsOpen, "the dropdown did not close");

            Capture(probe, "keys-opendeck");

            // And the shipped appearance is left alone: its keys are the
            // framework's, which are not square and not all alike.
            SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
            Layout(probe, w + 12, keys.Length * (h + 6) + 12);
            Capture(probe, "keys-default");
            Assert.Contains(Edges(action).Split(' ').Take(4), corner => Alpha(corner) < 0x40);
        }
        finally { probe.Close(); SkinService.Apply(new SkinEntry(SkinPackage.Default, [])); }
    }

    /// <summary>
    /// The applications window sizes itself to its cards and cannot be resized.
    /// With enough known applications its bottom ran off the screen and took
    /// the add controls and Close with it. The list has to give way instead.
    /// </summary>
    private static void TheApplicationsWindowKeepsItsBottomControlsReachable()
    {
        var vm = new MainViewModel(new DaemonClient());
        AppRoutingViewModelTests.Report(vm, true);
        // More applications than could ever fit, with labels long enough to
        // push the rows wide as well as deep.
        for (int i = 0; i < 60; i++)
        {
            string name = i % 3 == 0
                ? $"An application with a genuinely long window title {i}" : $"App {i}";
            var app = new AppStreamViewModel(new DaemonClient(), name, name,
                [new ChannelChoice("browser", "Browser"), new ChannelChoice("game", "Game")]);
            app.ApplyFromDaemon("browser", true, true);
            vm.Apps.Add(app);
        }

        foreach (string id in new[] { "default", "opendeck" })
        {
            SkinService.Apply(SkinCatalog.Find(id)!);
            var window = new AppsWindow { DataContext = vm };
            window.Show();
            try
            {
                // The screenshot's size, and a short working area on top of it.
                foreach (double usable in new[] { 740d, 520 })
                {
                    window.MaxHeight = usable;
                    Layout(window, 620, usable);
                    string where = $"{id} at {usable} high";
                    Assert.True(window.Bounds.Height <= usable + 0.5,
                        $"{where}: the window grew to {window.Bounds.Height}");

                    Button add = window.GetVisualDescendants().OfType<Button>()
                        .First(b => (b.Content as string) == Localizer.Text("Add"));
                    Button close = window.GetVisualDescendants().OfType<Button>()
                        .First(b => (b.Content as string) == Localizer.Text("Close"));
                    ComboBox picker = window.FindControl<ComboBox>("InstalledPicker")!;
                    ScrollViewer list = window.FindControl<ScrollViewer>("KnownApps")!;
                    CheckBox automatic = window.FindControl<CheckBox>("RouteAppsByDefault")!;
                    Assert.True(automatic.IsEffectivelyVisible);
                    Assert.True(automatic.IsChecked);
                    Assert.Contains(list, automatic.GetVisualAncestors());

                    foreach (Control control in new Control[] { add, close, picker })
                    {
                        Assert.True(control.Bounds.Height > 0, $"{where}: {control} was not laid out");
                        Point origin = control.TranslatePoint(default, window) ?? default;
                        Assert.True(origin.Y >= -0.5
                            && origin.Y + control.Bounds.Height <= window.ClientSize.Height + 0.5,
                            $"{where}: {(control as ContentControl)?.Content ?? "the picker"} is off the "
                            + $"bottom at {origin.Y + control.Bounds.Height} of {window.ClientSize.Height}");
                        Assert.True(control.Focusable && control.IsEffectivelyEnabled,
                            $"{where}: a bottom control cannot be reached from the keyboard");
                    }

                    // The list gave way, and it scrolls rather than clipping.
                    Assert.True(list.Bounds.Height > 0, $"{where}: the list collapsed");
                    Assert.True(list.Extent.Height > list.Viewport.Height,
                        $"{where}: the list does not scroll, so rows are lost");
                    close.Focus();
                    Pump(window);
                    Assert.True(close.IsFocused, $"{where}: Close did not take focus");
                }
                Capture(window, $"apps-{id}");
            }
            finally { window.Close(); }
        }
        SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
    }

    /// <summary>
    /// A key the UI letters in a lamp colour wears the skin's lit or alert face
    /// and ring when the skin sets them, and the ordinary face when it does not,
    /// so no other appearance changes. Under <c>check: key</c> a check box is a
    /// key with the bevel, lettered and lit as a toggle is. Under <c>chip: badge</c>
    /// a source chip is a flat badge lettered in the information colour, and a
    /// skin that sets <c>Ox.Value.Foreground</c> letters the readouts in it. A
    /// plugin's name in an insert list becomes the same badge, without its lamp.
    /// </summary>
    private static void LampKeysAndCheckKeysWearTheSkinsFaces()
    {
        var off = new Button { Content = "Inserts", Classes = { "fx", "fxOff" } };
        var on = new Button { Content = "EQ", Classes = { "fx", "fxOn", "chip" } };
        var plain = new Button { Content = "Inserts", Classes = { "fx" } };
        var bypass = new ToggleButton { Content = "B", Classes = { "insertmini", "bypass" }, IsChecked = true };
        var mute = new ToggleButton { Content = "M", Classes = { "mute" }, IsChecked = true };
        var danger = new Button { Content = "Delete", Classes = { "danger" } };
        var check = new CheckBox { Content = "Own clock" };
        var chipText = new TextBlock { Name = "ChipText", Text = "XLR 1 jack", Classes = { "body" } };
        var chipLamp = new StatusLed { IsOn = true, Margin = new Thickness(0, 0, 6, 0) };
        var chip = new Button
        {
            Content = new Grid { ColumnDefinitions = new("Auto,*"), Children = { chipLamp, chipText } },
            Classes = { "bindingChip" },
        };
        Grid.SetColumn(chipText, 1);
        var readout = new TextBlock { Text = "52 dB", Classes = { "val" } };
        var insertLamp = new StatusLed { IsOn = false, Kind = LedKind.Insert, Classes = { "insertLamp" } };
        var insertName = new TextBlock { Text = "Voice Focus", Classes = { "insertName", "body" } };
        var insertChip = new Border { Child = insertName, Classes = { "insertChip", "off" } };
        var probe = new Window
        {
            Content = new StackPanel { Children = { off, on, plain, bypass, mute, danger, check, chip, readout, insertLamp, insertChip } },
            Width = 300, Height = 480, WindowDecorations = WindowDecorations.None,
        };
        probe.Show();
        try
        {
            ContentPresenter Face(Control c)
            {
                Layout(probe, 300, 480);
                return c.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
            }
            Color Fill(Control c) => ((ISolidColorBrush)Face(c).Background!).Color;
            Color Ring(Control c) => ((ISolidColorBrush)Face(c).BorderBrush!).Color;
            Color Letters(Control c) => ((ISolidColorBrush)Face(c).Foreground!).Color;
            bool Bevelled(Control c) => c.GetVisualDescendants().OfType<Border>().Any(b => b.Name == "Bevel");

            // Without the tokens a key lettered in a lamp colour keeps the ordinary face.
            foreach (SkinEntry plainSkin in new[] { new SkinEntry(SkinPackage.Default, []), SkinCatalog.Find("opendeck")! })
            {
                SkinService.Apply(plainSkin);
                Assert.Same(Face(plain).Background, Face(off).Background);
                Assert.Same(Face(plain).Background, Face(on).Background);
                Assert.Equal(new Thickness(0), on.BorderThickness);
                Assert.False(Bevelled(check), $"{plainSkin.Id} drew a check box as a key");
                // The chip wears the skin's button, and the readout the primary text.
                Assert.Same(chip.Theme, plain.Theme);
                Assert.True(chipLamp.IsVisible);
                Assert.True(insertLamp.IsVisible);
                Assert.Null(insertChip.Background);
                Assert.Equal(((ISolidColorBrush)Application.Current!.FindResource("Ox.Text.Primary")!).Color,
                    ((ISolidColorBrush)readout.Foreground!).Color);
            }

            SkinReadResult read = SkinReader.Read("lamps", """
                {"schema":1,"name":"Lamps","controls":{"button":"cap","mute":"cap","check":"key","chip":"badge"},"tokens":{
                  "Ox.Alert.Background":"#400000","Ox.Alert.BackgroundPointerOver":"#500000","Ox.Alert.BorderBrush":"#ff0000",
                  "Ox.Lit.Background":"#003040","Ox.Lit.BackgroundPointerOver":"#004050","Ox.Lit.BorderBrush":"#00ffff",
                  "Ox.Chip.BorderThickness":1,"Ox.Danger.BorderBrush":"#ff8000","Ox.Mute.BorderBrushChecked":"#ff00ff",
                  "Ox.Control.Background":"#101010","Ox.Control.BackgroundChecked":"#002030",
                  "Ox.Toggle.Foreground":"#008090","Ox.Toggle.ForegroundChecked":"#00e0ff",
                  "Ox.Badge.Background":"#061418","Ox.Text.Info":"#40e0ff","Ox.Value.Foreground":"#30d0f0"}}
                """, SkinOrigin.BuiltIn, null);
            Assert.Empty(read.Errors);
            Assert.Empty(SkinService.Apply(new SkinEntry(read.Package!, read.Errors)));
            Assert.Equal(Color.Parse("#400000"), Fill(off));
            Assert.Equal(Color.Parse("#ff0000"), Ring(off));
            Assert.Equal(Color.Parse("#003040"), Fill(on));
            Assert.Equal(Color.Parse("#00ffff"), Ring(on));
            Assert.Equal(new Thickness(1), on.BorderThickness);
            Assert.Equal(Color.Parse("#101010"), Fill(plain));
            // A bypassed plugin's key is the alert key; a processing one is a plain key.
            Assert.Equal(Color.Parse("#400000"), Fill(bypass));
            bypass.IsChecked = false;
            Assert.NotEqual(Color.Parse("#400000"), Fill(bypass));
            Assert.Equal(Color.Parse("#ff00ff"), Ring(mute));
            Assert.Equal(Color.Parse("#ff8000"), Ring(danger));
            // The check box is a key: the bevel, the toggle's lettering, lit while ticked.
            Assert.True(Bevelled(check), "check: key drew no key");
            Assert.Equal(Color.Parse("#008090"), Letters(check));
            check.IsChecked = true;
            Assert.Equal(Color.Parse("#002030"), Fill(check));
            Assert.Equal(Color.Parse("#00e0ff"), Letters(check));
            // The chip is the badge: flat, the badge colour, lettered in the information colour.
            Assert.False(Bevelled(chip), "chip: badge drew a key");
            Assert.Equal(Color.Parse("#061418"), Fill(chip));
            Assert.Equal(Color.Parse("#40e0ff"), ((ISolidColorBrush)chipText.Foreground!).Color);
            // A badge has no lamp; a source that is not linked is lettered in the alert colour.
            Assert.False(chipLamp.IsVisible, "chip: badge kept its lamp");
            chip.Classes.Add("unlinked");
            Layout(probe, 300, 480);
            Assert.Equal(Color.Parse("#ff3c4e"), ((ISolidColorBrush)chipText.Foreground!).Color);
            chip.Classes.Remove("unlinked");
            // A plugin's name is the same badge, without its lamp, red while bypassed.
            Assert.False(insertLamp.IsVisible, "chip: badge kept the insert's lamp");
            Assert.Equal(Color.Parse("#061418"), ((ISolidColorBrush)insertChip.Background!).Color);
            Assert.Equal(Color.Parse("#ff3c4e"), ((ISolidColorBrush)insertName.Foreground!).Color);
            insertChip.Classes.Remove("off");
            Layout(probe, 300, 480);
            Assert.Equal(Color.Parse("#40e0ff"), ((ISolidColorBrush)insertName.Foreground!).Color);
            Assert.Equal(Color.Parse("#30d0f0"), ((ISolidColorBrush)readout.Foreground!).Color);
            Capture(probe, "lamp-keys");

            // And back: the plain faces and the tick box return.
            SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
            bypass.IsChecked = true;
            Assert.Same(Face(plain).Background, Face(off).Background);
            Assert.Equal(new Thickness(0), on.BorderThickness);
            Assert.False(Bevelled(check));
            Assert.Same(chip.Theme, plain.Theme);
            Assert.True(chipLamp.IsVisible);
            Assert.True(insertLamp.IsVisible);
            Assert.Null(insertChip.Background);
            Assert.NotEqual(Color.Parse("#30d0f0"), ((ISolidColorBrush)readout.Foreground!).Color);
        }
        finally { probe.Close(); }
    }

    /// <summary>
    /// The plugin bypass key says what the audio is doing rather than what the
    /// checkbox under it holds: lit while the plugin processes, alert while it
    /// is bypassed. The shipped appearance has no such colours, so it letters
    /// the key plainly, and it has to stay readable in both states.
    /// </summary>
    private static void ThePluginBypassKeyIsLegibleInBothAppearances()
    {
        var bypass = new ToggleButton { Content = "B", Classes = { "insertmini", "bypass" } };
        var probe = new Window { Content = bypass, Width = 60, Height = 40,
            WindowDecorations = WindowDecorations.None };
        probe.Show();
        try
        {
            Color Letters()
            {
                Layout(probe, 60, 40);
                return ((ISolidColorBrush)bypass.GetVisualDescendants().OfType<ContentPresenter>()
                    .First(c => c.Name == "PART_ContentPresenter").Foreground!).Color;
            }
            static bool Greenish(Color c) => c.G > 0xa0 && c.G > c.R + 0x40;
            static bool Reddish(Color c) => c.R > 0xc0 && c.R > c.G + 0x60;

            SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
            foreach (bool bypassed in new[] { false, true })
            {
                bypass.IsChecked = bypassed;
                Color c = Letters();
                Assert.True(c == Colors.White,
                    $"the shipped appearance letters a {(bypassed ? "bypassed" : "processing")} "
                    + $"plugin {c} instead of white");
            }

            // Switching live, not only at startup.
            SkinService.Apply(SkinCatalog.Find("opendeck")!);
            bypass.IsChecked = false;
            Assert.True(Greenish(Letters()), "Deck does not letter a processing plugin green");
            bypass.IsChecked = true;
            Assert.True(Reddish(Letters()), "Deck does not letter a bypassed plugin red");

            // And back again.
            SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
            Assert.Equal(Colors.White, Letters());
            bypass.IsChecked = false;
            Assert.Equal(Colors.White, Letters());

            // A key that cannot be used stays legible rather than taking the
            // bypass lettering.
            bypass.IsEnabled = false;
            Assert.NotEqual(Colors.White, Letters());
            bypass.IsEnabled = true;
        }
        finally { probe.Close(); SkinService.Apply(new SkinEntry(SkinPackage.Default, [])); }
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            double x = v / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    /// <summary>
    /// The tabs Options is split into are the application's own drawing, so
    /// they follow the skin: the labels in the heading and readout colours,
    /// the bar under the chosen tab in the accent or the skin's own
    /// Ox.Tab.Indicator, and the label at Ox.Tab.FontSize. Each tab keeps its
    /// cards whichever skin is worn.
    /// </summary>
    private static void TheOptionsTabsWearTheSkin(OptionsWindow options)
    {
        try
        {
            foreach (string id in new[] { "default", "opendeck", "catppuccin-latte" })
            {
                SkinService.Apply(SkinCatalog.Find(id)!);
                Pump(options);
                OptionsTabsWindowTests.CheckAppearance(options, id);
                foreach (var (tab, headings) in OptionsTabsWindowTests.Cards)
                {
                    OptionsTabsWindowTests.Show(options, tab);
                    Pump(options);
                    Assert.Equal(headings.Select(Localizer.Text), Cards(options)
                        .Select(card => card.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("h")).Text));
                    foreach (Border card in Cards(options).Where(c => c.IsEffectivelyVisible))
                        AssertInside(card, options, $"{id}, {tab}");
                }
            }

            // A skin that names the tab's own values.
            SkinReadResult read = SkinReader.Read("probe",
                """{"schema":1,"name":"Probe","tokens":{"Ox.Tab.Indicator":"#ff00ff","Ox.Tab.FontSize":20,"Ox.Text.Secondary":"#00ff00"}}""",
                SkinOrigin.BuiltIn, null);
            Assert.Empty(read.Errors);
            SkinService.Apply(new SkinEntry(read.Package!, []));
            Pump(options);
            OptionsTabsWindowTests.CheckAppearance(options, "probe");
            TabItem chosen = OptionsTabsWindowTests.Tabs(options)[(int)OptionsTab.Audio];
            Assert.Equal(20, chosen.FontSize);
            Assert.Equal(Color.Parse("#ff00ff"), ((ISolidColorBrush)chosen.GetVisualDescendants().OfType<Border>()
                .Single(b => b.Name == "PART_SelectedPipe").Background!).Color);
            Assert.Equal(Color.Parse("#00ff00"),
                ((ISolidColorBrush)OptionsTabsWindowTests.Tabs(options)[(int)OptionsTab.General].Foreground!).Color);
        }
        finally
        {
            SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
            Pump(options);
        }
        // And the shipped appearance comes back.
        OptionsTabsWindowTests.CheckAppearance(options, "default again");
        Assert.Equal(14, OptionsTabsWindowTests.Tabs(options)[0].FontSize);
    }

    // --- fixture ---

    private static IEnumerable<Border> Cards(Visual root) =>
        root.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("card"));

    private static double ThumbWidth(Visual root) =>
        root.GetVisualDescendants().OfType<Thumb>().First().Bounds.Width;

    private static void AssertInside(Control child, Window window, string where)
    {
        Point origin = child.TranslatePoint(default, window) ?? default;
        Assert.True(origin.X >= -0.5 && origin.X + child.Bounds.Width <= window.ClientSize.Width + 0.5,
            $"{child.GetType().Name} runs out of the window. {where}");
    }

    private static void Layout(Window window, double width, double height)
    {
        window.Width = width;
        window.Height = height;
        Pump(window);
        window.Measure(new Size(width, height));
        window.Arrange(new Rect(0, 0, width, height));
        Pump(window);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        using var stop = new CancellationTokenSource();
        DispatcherTimer.RunOnce(stop.Cancel, TimeSpan.FromMilliseconds(60));
        Dispatcher.UIThread.MainLoop(stop.Token);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A screenshot of a window, for looking at the result by eye.</summary>
    private static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("OPENXLR_SKIN_ARTIFACTS") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        var size = new PixelSize(Math.Max(1, (int)window.Bounds.Width), Math.Max(1, (int)window.Bounds.Height));
        using var bitmap = new RenderTargetBitmap(size);
        bitmap.Render(window);
        bitmap.Save(Path.Combine(directory, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    /// <summary>
    /// Build the mixer, Options and the flow graph on fixed fixture state, hand
    /// them to the test, and tear the windows down again. No daemon and no
    /// audio: the client is stopped and the view model is filled by hand.
    /// </summary>
    private static void Run(Action<MainWindow, OptionsWindow, FlowWindow> body)
    {
        string config = Path.Combine(Path.GetTempPath(), "openxlr-skin-" + Guid.NewGuid());
        string? oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string? oldRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        string? oldBus = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        string? oldSkin = Environment.GetEnvironmentVariable("OPENXLR_SKIN");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            List<Window> windows = [];
            try
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
                Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", config);
                Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", "unix:path=/nonexistent");
                Environment.SetEnvironmentVariable("OPENXLR_SKIN", null);
                WindowLayoutTests.PinMaterial();
                AppBuilder.Configure<App>().UseSkia().UseHarfBuzz().UseX11().SetupWithoutStarting();
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;

                var main = new MainWindow();
                windows.Add(main);
                ((DaemonClient)typeof(MainWindow).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(main)!).DisposeAsync().AsTask().GetAwaiter().GetResult();
                Dispatcher.UIThread.RunJobs();

                var client = new DaemonClient();
                var vm = new MainViewModel(client);

                static void AddInsert(InsertsViewModel owner)
                {
                    const string uri = "http://lsp-plug.in/plugins/lv2/graphic_equalizer_x16_mono";
                    owner.PluginChoices.Add(new(uri, "LSP Graphic Equalizer x16 Mono", "EQ",
                        JsonNode.Parse("""[{"symbol":"gain","name":"Input gain","min":-60,"max":12,"default":0}]""")!,
                        true, true));
                    owner.Apply(JsonNode.Parse("""
                        [{"insert":{"id":"eq","plugin":"http://lsp-plug.in/plugins/lv2/graphic_equalizer_x16_mono",
                        "label":"LSP Graphic Equalizer x16 Mono","nativeHost":true},"nativeHostRunning":true}]
                        """));
                }

                typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(vm, [JsonNode.Parse("""{"mixes":[],"channels":[],"inserts":{}}""")]);
                AddInsert(vm.Inserts);
                for (int i = 0; i < 3; i++)
                {
                    var mix = new MixViewModel(client, "mix" + i, i switch
                    {
                        0 => "Monitor A", 1 => "Stream", _ => "Chat",
                    });
                    mix.ApplyFromDaemon(new JsonObject
                    {
                        ["volume"] = 0.7 - i * 0.2, ["muted"] = i == 2,
                        ["meter"] = new JsonArray(0.6, 0.4),
                    });
                    AddInsert(mix.Inserts);
                    vm.Mixes.Add(mix);
                }
                string[] mixIds = [.. vm.Mixes.Select(m => m.Id)];
                foreach (string name in new[] { "XLR 1", "Browser", "Game", "Voice Chat" })
                {
                    var channel = new ChannelViewModel(client, name.ToLowerInvariant().Replace(' ', '-'), name, mixIds);
                    channel.MeterL = 0.55;
                    channel.MeterR = 0.95;
                    vm.Channels.Add(channel);
                }
                foreach (string name in new[] { "Google Chrome", "Discord", "Steam" })
                {
                    var app = new AppStreamViewModel(client, name, name,
                        [new ChannelChoice("browser", "Browser"), new ChannelChoice("game", "Game")]);
                    app.ApplyFromDaemon("browser", true, true);
                    vm.Apps.Add(app);
                    vm.ActiveApps.Add(app);
                }
                main.DataContext = null;
                main.DataContext = vm;
                main.Show();

                var options = new OptionsWindow(new OptionsViewModel(client, vm));
                windows.Add(options);
                options.Show();
                var flow = new FlowWindow(vm);
                windows.Add(flow);
                flow.Show();
                Pump(main);

                body(main, options, flow);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                foreach (Window window in windows)
                    try { window.Close(); } catch (Exception) { }
                Dispatcher.UIThread.RunJobs();
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", oldConfig);
                Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", oldRuntime);
                Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", oldBus);
                Environment.SetEnvironmentVariable("OPENXLR_SKIN", oldSkin);
                try { Directory.Delete(config, recursive: true); } catch (IOException) { }
            }
        });
        thread.Start();
        thread.Join();
        // Rethrowing the exception object directly would drop the frames the
        // failure actually came from.
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

public sealed class SkinFactAttribute : FactAttribute
{
    public SkinFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENXLR_TEST_SKIN") != "1")
            Skip = "Run separately with OPENXLR_TEST_SKIN=1 under xvfb-run, filtered to SkinWindowTests.";
    }
}
