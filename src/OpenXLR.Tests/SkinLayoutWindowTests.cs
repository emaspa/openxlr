using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

/// <summary>
/// A skin's layout on a real window: the sections in the skin's order, in a
/// row that keeps every section whole and scrolls, grids and docks that
/// place the header and the sections without overlap at any width, region
/// options, header actions and their overflow, the tab order, hidden
/// sections and their way back, swaps from the keyboard, the user's moves
/// kept per skin, Reset clearing only the skin in force, and Material
/// bringing the legacy order back. Run from the window layout test, on its UI
/// thread, with preferences and skins of its own.
/// </summary>
internal static partial class SkinLayoutWindowTests
{
    private static readonly string[] Usual = ["InputsTile", "HeadphonesTile", "MonitorTile", "ApplicationsTile", "SubmixerTile"];
    private static readonly string[] Legacy = ["HeadphonesTile", "SubmixerTile", "InputsTile", "MonitorTile", "ApplicationsTile"];
    private static readonly string[] ColumnOrder = ["SubmixerTile", "MonitorTile", "InputsTile", "HeadphonesTile", "ApplicationsTile"];
    private static readonly string[] RowOrder = ["MonitorTile", "InputsTile", "SubmixerTile", "HeadphonesTile", "ApplicationsTile"];

    /// <summary>The arrangement saved under "column-first" before the window opens: applications, inputs, then the rest in the skin's order.</summary>
    private static readonly Dictionary<string, string> ColumnMoves = new()
    {
        ["main:0"] = "applications", ["main:1"] = "inputs", ["main:2"] = "submixer", ["main:3"] = "monitor", ["main:4"] = "headphones",
    };

    /// <summary>Two columns: INPUTS and MONITOR, HEADPHONES and APPLICATIONS, then the submixer across both.</summary>
    private const string GridSkin = """
        {"schema":1,"name":"Grid","tokens":{"Ox.Card.Background":"#262a33"},"layout":{"version":1,
         "main":{"kind":"grid","id":"desktop","spacing":12,"columns":[{"star":1},{"star":1}],"rows":["auto","auto","auto"],
          "children":[
           {"kind":"region","region":"inputs","row":0,"column":0},
           {"kind":"region","region":"monitor","row":0,"column":1},
           {"kind":"region","region":"headphones","row":1,"column":0},
           {"kind":"region","region":"applications","row":1,"column":1},
           {"kind":"region","region":"submixer","row":2,"column":0,"columnSpan":2}]},
         "regions":{"header":{"actions":["flow","profiles"],"overflow":["restart-daemon","about"]}}}}
        """;

    /// <summary>
    /// The header docked at the bottom, INPUTS down the left with a
    /// preferred width, a nested stack on the right whose HEADPHONES asks for
    /// a width it cannot have there, APPLICATIONS hidden along the top and
    /// the submixer filling the rest.
    /// </summary>
    private const string DockSkin = """
        {"schema":1,"name":"Dock","tokens":{"Ox.Card.Background":"#2a262e"},"layout":{"version":1,
         "main":{"kind":"dock","id":"window","spacing":10,"children":[
           {"kind":"region","region":"header","dock":"bottom"},
           {"kind":"region","region":"inputs","dock":"left"},
           {"kind":"stack","id":"side","orientation":"vertical","dock":"right","children":[
             {"kind":"region","region":"headphones"},{"kind":"region","region":"monitor"}]},
           {"kind":"region","region":"applications","dock":"top"},
           {"kind":"region","region":"submixer","dock":"fill"}]},
         "regions":{"inputs":{"width":700},"headphones":{"width":900,"height":260},
                    "applications":{"visibility":"hidden"},"monitor":{"visibility":"collapsed"}}}}
        """;

    internal static void Check()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-skin-layout-window-").FullName;
        string? config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string? dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string? dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        SkinEntry skin = SkinService.Current;
        MainWindow? window = null;
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(dir, "config"));
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(dir, "data"));
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(dir, "none"));
            Install(dir, "column-first", "vertical", 16, ColumnOrder);
            Install(dir, "row", "horizontal", 8, RowOrder);
            Write(dir, "grid", GridSkin);
            Write(dir, "dock", DockSkin);
            // The legacy order, and an arrangement made under another skin
            // that Reset in "row" must leave alone.
            Assert.Null(new UiSettings { CheckForUpdates = false, SectionOrder = Legacy }
                .WithSkinArrangement("column-first", 1, ColumnMoves, null).Save());

            SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
            window = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
            var model = (MainViewModel)window.DataContext!;
            typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(model, [JsonNode.Parse("""
                {"mixes":[{"id":"monitor","name":"Monitor A","kind":"monitor"},{"id":"stream","name":"Stream","kind":"virtualMic"}],
                 "channels":[{"id":"music","name":"Music","levels":{"monitor":0.5,"stream":0.4}},
                             {"id":"game","name":"Game","levels":{"monitor":1,"stream":1}},
                             {"id":"chat","name":"Voice chat","levels":{"monitor":1,"stream":1}}]}
                """)]);
            window.Show();
            Layout(window, 1040, 900);
            MainWindow main = window;

            Expander[] expanders = [.. Usual.Select(name => main.FindControl<Expander>(name)!)];
            Border[] cards = [.. expanders.Select(e => (Border)e.Parent!)];
            object?[] contents = [.. expanders.Select(e => e.Content)];
            Assert.All(cards, card => Assert.True(card.IsVisible, $"{((Expander)card.Child!).Name} is hidden in the fixture"));
            Assert.Equal(Legacy, Order(main));

            // A vertical layout: the skin's order and gap, top to bottom.
            Wear("column-first");
            Layout(main, 1040, 900);
            // The arrangement saved under this skin goes over its own order.
            Assert.Equal(["ApplicationsTile", "InputsTile", "SubmixerTile", "MonitorTile", "HeadphonesTile"], Order(main));
            Assert.Equal(Order(main), OnScreen(main, byX: false));
            Assert.Equal(16, Cards(main).Spacing);
            Assert.Equal(Orientation.Vertical, Cards(main).Orientation);
            Assert.Equal("↕", SectionHandle(main, "InputsTile").Content);

            // A horizontal layout: one row, every section whole, scrolling.
            Wear("row");
            Layout(main, 1040, 900);
            Assert.Equal(RowOrder, Order(main));
            Assert.Equal(Orientation.Horizontal, Cards(main).Orientation);
            Assert.Equal(RowOrder, OnScreen(main, byX: true));
            CheckRow(main, cards);
            Assert.Equal("↔", SectionHandle(main, "InputsTile").Content);
            Capture(main, "skin-layout-row-1040");

            // A collapsed section in the row is its header, not the row's height.
            Expander monitor = main.FindControl<Expander>("MonitorTile")!;
            monitor.IsExpanded = false;
            Layout(main, 1040, 900);
            Border monitorCard = (Border)monitor.Parent!;
            Assert.True(monitorCard.Bounds.Height + 1 < cards.Max(c => c.Bounds.Height),
                $"a collapsed section is {monitorCard.Bounds.Height} tall in a row of {cards.Max(c => c.Bounds.Height)}");
            Assert.Equal(VerticalAlignment.Top, monitorCard.VerticalAlignment);
            monitor.IsExpanded = true;
            Layout(main, 1040, 900);

            // The keyboard moves a section along the row, and the move is
            // kept for this skin alone.
            Arrange(main, true);
            Button handle = SectionHandle(main, "MonitorTile");
            handle.Focus();
            handle.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right, Source = handle });
            Layout(main, 1040, 900);
            string[] moved = ["InputsTile", "MonitorTile", "SubmixerTile", "HeadphonesTile", "ApplicationsTile"];
            Assert.Equal(moved, Order(main));
            UiSettings saved = UiSettings.Load();
            Assert.Equal(Legacy, saved.SectionOrder);
            Assert.Equal(new Dictionary<string, string> { ["main:0"] = "inputs", ["main:1"] = "monitor" },
                saved.SkinArrangementFor("row", 1)!.Slots);
            Assert.Equal(ColumnMoves, saved.SkinArrangementFor("column-first", 1)!.Slots);
            Assert.Equal(Localizer.Text("PutTheSectionsBackInTheSkinsOrder"),
                ToolTip.GetTip(main.FindControl<Button>("ResetSectionsButton")!));
            Assert.Equal(Localizer.Text("ArrangeSwapHint"), main.FindControl<TextBlock>("ArrangementNote")!.Text);

            // The move survives a switch away and back.
            Wear(SkinPackage.DefaultId);
            Wear("row");
            Layout(main, 1040, 900);
            Assert.Equal(moved, Order(main));

            // Reset clears only this skin's arrangement and returns to its order.
            main.FindControl<Button>("ResetSectionsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(main, 1040, 900);
            Assert.Equal(RowOrder, Order(main));
            saved = UiSettings.Load();
            Assert.Null(saved.SkinArrangementFor("row", 1));
            Assert.Equal(ColumnMoves, saved.SkinArrangementFor("column-first", 1)!.Slots);
            Assert.Equal(Legacy, saved.SectionOrder);
            Arrange(main, false);

            CheckGrid(main, cards);
            CheckDock(main, cards);
            CheckStale();
            CheckTemplates(dir);
            CheckCompactTemplates(dir);
            CheckBroadcast();
            CheckFreshStart();
            CheckApplicationColumns();

            // Material brings the legacy order back, in the column it always was.
            Wear(SkinPackage.DefaultId);
            Layout(main, 1040, 900);
            Assert.Equal(Legacy, Order(main));
            Assert.Equal(Legacy, OnScreen(main, byX: false));
            Assert.Equal(Orientation.Vertical, Cards(main).Orientation);
            Assert.Equal(SkinLayoutDefaults.Spacing, Cards(main).Spacing);
            Assert.Equal(ScrollBarVisibility.Disabled, main.FindControl<ScrollViewer>("SectionScroll")!.HorizontalScrollBarVisibility);
            Assert.Same(Cards(main), main.FindControl<ScrollViewer>("SectionScroll")!.Content);
            Assert.All(cards, card => Assert.True(double.IsNaN(card.Width)));
            Assert.All(cards, card => Assert.True(card.IsVisible));
            Assert.Equal(Localizer.Text("PutTheFiveSectionsBackInTheir"),
                ToolTip.GetTip(main.FindControl<Button>("ResetSectionsButton")!));
            var content = main.FindControl<StackPanel>("MixerContent")!;
            Assert.Same(main.FindControl<Border>("HeaderCard"), content.Children[0]);
            Assert.Equal(new Thickness(0, 0, 0, 8), content.Children[0].Margin);
            Assert.InRange(content.Bounds.Width, 1040 - 72, 1040 - 16);
            Assert.All(cards, card => AssertInside(card, main));
            Assert.Equal(["ProfilesButton", "ArrangeButton", "FlowButton", "RestartDaemonButton", "HeaderVersion", "AboutButton", "HeaderOverflow"],
                main.FindControl<WrapPanel>("HeaderActions")!.Children.Select(c => c.Name));
            Assert.False(main.FindControl<DropDownButton>("HeaderOverflow")!.IsVisible);
            Assert.Empty(main.FindControl<StackPanel>("HeaderOverflowItems")!.Children);

            // Nothing was rebuilt along the way.
            Assert.Equal(expanders, Usual.Select(name => main.FindControl<Expander>(name)!));
            Assert.Equal(cards, expanders.Select(e => (Border)e.Parent!));
            Assert.Equal(contents, expanders.Select(e => e.Content));
            Assert.All(expanders, e => Assert.Same(model, e.DataContext));
            CheckSeenMarkerBounds();
            CheckFirstFoldSave(main, dir);
            CheckFoldSaveErrors(main, dir);
        }
        finally
        {
            window?.Close();
            SkinService.Apply(skin);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", dataHome);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", dataDirs);
            Directory.Delete(dir, true);
        }
    }

    private static void CheckSeenMarkerBounds()
    {
        string path = Path.Combine(UiSettings.ConfigDir, "ui.json");
        string saved = File.ReadAllText(path);
        SkinLayoutKey[] Keys(string prefix) => [.. Enumerable.Range(0, UiSettings.MaxSkinArrangements)
            .Select(i => new SkinLayoutKey { Skin = prefix + i, Version = 1 })];
        SkinLayoutKey[] newer = Keys("new-");
        MainWindow? olderWindow = null;
        try
        {
            Assert.Null(new UiSettings { SkinLayoutsSeen = Keys("old-") }.Save());
            olderWindow = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
            olderWindow.Show();
            // Another window has since filled the bounded store with newer skins.
            Assert.Null(new UiSettings { SkinLayoutsSeen = newer }.Save());
            olderWindow.FindControl<Expander>("MonitorTile")!.IsExpanded = false;
            UiSettings current = UiSettings.Load();
            Assert.Equal(newer, current.SkinLayoutsSeen);
            Assert.Contains("MonitorTile", current.CollapsedSections);
        }
        finally
        {
            olderWindow?.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            OpenXLR.Core.OpenXlrPaths.WriteAtomic(path, saved);
        }
    }

    private static void CheckFirstFoldSave(MainWindow main, string dir)
    {
        string path = Path.Combine(UiSettings.ConfigDir, "ui.json");
        var monitor = main.FindControl<Expander>("MonitorTile")!;
        monitor.IsExpanded = false;
        monitor.IsExpanded = true;
        string saved = File.ReadAllText(path);
        Assert.DoesNotContain("MonitorTile", UiSettings.Load().CollapsedSections);
        Write(dir, "atomic-fold", DockSkin);
        void RepairOnFold(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property != Expander.IsExpandedProperty || monitor.IsExpanded || !Directory.Exists(path)) return;
            Directory.Delete(path);
            OpenXLR.Core.OpenXlrPaths.WriteAtomic(path, saved);
        }
        // Registered after the window's handler: a separate fold save fails,
        // then this repair lets the old marker-only save incorrectly succeed.
        monitor.PropertyChanged += RepairOnFold;
        try
        {
            File.Delete(path);
            Directory.CreateDirectory(path);
            Wear("atomic-fold");
            Assert.False(monitor.IsExpanded);
            UiSettings restored = UiSettings.Load(out string? error);
            Assert.Null(error);
            Assert.True(restored.HasSeenSkinLayout("atomic-fold", 1));
            Assert.Contains("MonitorTile", restored.CollapsedSections);
        }
        finally
        {
            monitor.PropertyChanged -= RepairOnFold;
            if (Directory.Exists(path)) Directory.Delete(path);
            OpenXLR.Core.OpenXlrPaths.WriteAtomic(path, saved);
            Wear(SkinPackage.DefaultId);
            monitor.IsExpanded = true;
        }
    }

    private static void CheckFoldSaveErrors(MainWindow main, string dir)
    {
        string path = Path.Combine(UiSettings.ConfigDir, "ui.json");
        string saved = File.ReadAllText(path);
        const string malformed = "{not valid JSON";
        var monitor = main.FindControl<Expander>("MonitorTile")!;
        var note = main.FindControl<TextBlock>("ArrangementNote")!;
        Write(dir, "unsaved-fold", DockSkin);
        Arrange(main, false);
        try
        {
            OpenXLR.Core.OpenXlrPaths.WriteAtomic(path, malformed);
            UiSettings.Load(out string? error);
            Assert.NotNull(error);
            monitor.IsExpanded = false;
            Assert.False(monitor.IsExpanded); // The view still changes for this run.
            Assert.Equal(malformed, File.ReadAllText(path));
            Assert.True(note.IsVisible);
            Assert.Equal(error, note.Text);
            Layout(main, 640, 900);
            Capture(main, "skin-layout-fold-save-error-640");

            OpenXLR.Core.OpenXlrPaths.WriteAtomic(path, saved);
            monitor.IsExpanded = true;
            Assert.False(note.IsVisible);
            Assert.DoesNotContain("MonitorTile", UiSettings.Load().CollapsedSections);
            monitor.IsExpanded = false;
            Assert.Contains("MonitorTile", UiSettings.Load().CollapsedSections);
            Assert.False(note.IsVisible);

            // Even an already folded tile must report the skin's failed initial save.
            OpenXLR.Core.OpenXlrPaths.WriteAtomic(path, malformed);
            Wear("unsaved-fold");
            Assert.False(monitor.IsExpanded);
            Assert.Equal(malformed, File.ReadAllText(path));
            Assert.True(note.IsVisible);
            Assert.Equal(error, note.Text);
            monitor.IsExpanded = true;
            Wear(SkinPackage.DefaultId);
            Wear("unsaved-fold");
            Assert.True(monitor.IsExpanded); // The default is not reapplied within this run.
            Assert.Equal(malformed, File.ReadAllText(path));

            OpenXLR.Core.OpenXlrPaths.WriteAtomic(path, saved);
            monitor.IsExpanded = false;
            Assert.False(note.IsVisible);
            Assert.Contains("MonitorTile", UiSettings.Load().CollapsedSections);
            Assert.True(UiSettings.Load().HasSeenSkinLayout("unsaved-fold", 1));
            monitor.IsExpanded = true;
            Assert.DoesNotContain("MonitorTile", UiSettings.Load().CollapsedSections);
            var fresh = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
            try
            {
                fresh.Show();
                Layout(fresh, 640, 900);
                Assert.True(fresh.FindControl<Expander>("MonitorTile")!.IsExpanded);
                Assert.True(UiSettings.Load().HasSeenSkinLayout("unsaved-fold", 1));
            }
            finally
            {
                fresh.Close();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            }
        }
        finally
        {
            OpenXLR.Core.OpenXlrPaths.WriteAtomic(path, saved);
            Wear(SkinPackage.DefaultId);
        }
    }

    /// <summary>
    /// The grid: every section in its cell at 640, 1040 and 2560 pixels,
    /// none overlapping another and every control inside its section, the
    /// tree scrolling sideways when two columns do not fit while the header
    /// keeps the window's width. Then the header's actions in the skin's
    /// order with the rest in the overflow menu, the tab order through the
    /// cells, and swaps from the keyboard: the arrows and the menu.
    /// </summary>
    private static void CheckGrid(MainWindow main, Border[] cards)
    {
        Wear("grid");
        Border Card(string tile) => (Border)main.FindControl<Expander>(tile)!.Parent!;
        var scroll = main.FindControl<ScrollViewer>("SectionScroll")!;
        Grid grid = Assert.IsType<Grid>(main.LayoutHost.PanelFor("desktop"));
        Assert.Same(grid, scroll.Content);
        Assert.Equal(12, grid.ColumnSpacing);
        Assert.Equal(12, grid.RowSpacing);
        foreach (double width in new[] { 640d, 1040, 2560 })
        {
            Layout(main, width, 1000);
            string where = $"at {width}";
            Assert.All(cards, card => Assert.Same(grid, card.Parent));
            Point At(string tile) => Card(tile).TranslatePoint(default, grid)!.Value;
            Assert.Equal(At("InputsTile").Y, At("MonitorTile").Y, 0.5);
            Assert.True(At("InputsTile").X < At("MonitorTile").X, where);
            Assert.Equal(At("InputsTile").X, At("HeadphonesTile").X, 0.5);
            Assert.True(At("HeadphonesTile").Y > At("InputsTile").Y, where);
            Assert.Equal(At("MonitorTile").X, At("ApplicationsTile").X, 0.5);
            Assert.True(At("SubmixerTile").Y > At("ApplicationsTile").Y, where);
            Assert.Equal(At("InputsTile").X, At("SubmixerTile").X, 0.5);
            AssertNoOverlap(cards);
            foreach (Border card in cards)
            {
                Assert.True(card.Bounds.Width >= SkinLayoutDefaults.RowSectionMinWidth - 0.5, $"{card.Bounds.Width} wide {where}");
                AssertControlsInside(card, scroll);
                Assert.True(card.TranslatePoint(new Point(card.Bounds.Width, 0), grid)!.Value.X <= grid.Bounds.Width + 0.5, where);
            }
            Assert.True(Card("SubmixerTile").Bounds.Width > Card("InputsTile").Bounds.Width * 2, where);
            AssertInside(main.FindControl<Grid>("WindowHeader")!, main);
            var content = main.FindControl<StackPanel>("MixerContent")!;
            Assert.InRange(content.Bounds.Width, width - 72, width - 16);
            bool fits = width >= 2 * SkinLayoutDefaults.RowSectionMinWidth + 12 + 32;
            Assert.Equal(fits ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto, scroll.HorizontalScrollBarVisibility);
            if (fits)
                Assert.Equal(scroll.Viewport.Width, grid.Bounds.Width, 1);
            else
            {
                Assert.True(scroll.Extent.Width > scroll.Viewport.Width + 1, $"the grid does not scroll {where}");
                scroll.Offset = new Vector(scroll.Extent.Width - scroll.Viewport.Width, 0);
                Layout(main, width, 1000);
                double right = Card("MonitorTile").TranslatePoint(new Point(Card("MonitorTile").Bounds.Width, 0), scroll)!.Value.X;
                Assert.InRange(right, 0, scroll.Viewport.Width + 0.5);
                scroll.Offset = default;
                Layout(main, width, 1000);
            }
            Capture(main, $"skin-layout-grid-{width}");
        }
        Layout(main, 1040, 900);

        // The header: the skin's order, the rest after it, two in the menu, Options where it always is.
        var actions = main.FindControl<WrapPanel>("HeaderActions")!;
        var overflow = main.FindControl<DropDownButton>("HeaderOverflow")!;
        var items = main.FindControl<StackPanel>("HeaderOverflowItems")!;
        Assert.Equal(["FlowButton", "ProfilesButton", "ArrangeButton", "HeaderVersion", "HeaderOverflow"], actions.Children.Select(c => c.Name));
        Assert.Equal(["RestartDaemonButton", "AboutButton"], items.Children.Select(c => c.Name));
        Assert.True(overflow.IsVisible);
        Assert.Equal(Localizer.Text("MoreActions"), AutomationProperties.GetName(overflow));
        var options = main.FindControl<Button>("OptionsButton")!;
        Assert.Same(main.FindControl<Grid>("WindowHeader"), options.Parent);
        Assert.Equal(0, Grid.GetColumn(options));
        Assert.True(options.IsEffectivelyVisible);
        Assert.Same(main.DataContext, items.DataContext);

        // The menu holds the same About button, and its click still opens About and closes the menu.
        overflow.Flyout!.ShowAt(overflow);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var about = main.FindControl<Button>("AboutButton")!;
        Assert.True(about.IsEffectivelyVisible);
        int owned = main.OwnedWindows.Count;
        about.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, about));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Window dialog = Assert.IsType<AboutWindow>(main.OwnedWindows.Last());
        Assert.Equal(owned + 1, main.OwnedWindows.Count);
        Assert.False(overflow.Flyout.IsOpen);
        dialog.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // Focus goes through the header, then the cells by row and by column.
        Assert.Equal(["header", "inputs", "monitor", "headphones", "applications", "submixer"], TabRegions(main));

        // The arrows trade places with the next section that way in the grid.
        Arrange(main, true);
        Assert.Equal("⇄", SectionHandle(main, "InputsTile").Content);
        Button inputs = SectionHandle(main, "InputsTile");
        inputs.Focus();
        inputs.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down, Source = inputs });
        Layout(main, 1040, 900);
        Assert.Equal((1, 0), (Grid.GetRow(Card("InputsTile")), Grid.GetColumn(Card("InputsTile"))));
        Assert.Equal((0, 0), (Grid.GetRow(Card("HeadphonesTile")), Grid.GetColumn(Card("HeadphonesTile"))));
        Assert.True(inputs.IsFocused);

        // Enter opens a menu of every other section; choosing one trades places across the grid.
        inputs.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = inputs });
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        MenuFlyout menu = Assert.IsType<MenuFlyout>(main.SwapMenu);
        MenuItem[] choices = [.. menu.Items.OfType<MenuItem>()];
        Assert.Equal(new[] { "HEADPHONES", "MONITOR", "APPLICATIONS", "SUBMIXER" }.Select(n => Localizer.Format("SwapWithSection", Localizer.Text(n))),
            choices.Select(c => (string)c.Header!));
        choices.Single(c => (string)c.Tag! == "submixer").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.Hide();
        Layout(main, 1040, 900);
        Assert.Equal((2, 0, 2), (Grid.GetRow(Card("InputsTile")), Grid.GetColumn(Card("InputsTile")), Grid.GetColumnSpan(Card("InputsTile"))));
        Assert.Equal((1, 0, 1), (Grid.GetRow(Card("SubmixerTile")), Grid.GetColumn(Card("SubmixerTile")), Grid.GetColumnSpan(Card("SubmixerTile"))));
        AssertNoOverlap(cards);
        Assert.Equal(new Dictionary<string, string> { ["desktop:0,0"] = "headphones", ["desktop:1,0"] = "submixer", ["desktop:2,0"] = "inputs" },
            UiSettings.Load().SkinArrangementFor("grid", 1)!.Slots);
        Assert.Equal(["header", "headphones", "monitor", "submixer", "applications", "inputs"], TabRegions(main));

        main.FindControl<Button>("ResetSectionsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout(main, 1040, 900);
        Assert.Equal((0, 0), (Grid.GetRow(Card("InputsTile")), Grid.GetColumn(Card("InputsTile"))));
        Assert.Null(UiSettings.Load().SkinArrangementFor("grid", 1));
        Arrange(main, false);
    }

    /// <summary>
    /// The dock: the header at the bottom of the tree with the application's
    /// notes still above it, INPUTS at its preferred width on the left, a
    /// preferred width ignored in a column, a preferred height as a floor,
    /// MONITOR folded the first time only, APPLICATIONS hidden and out of the
    /// tab order until Arrange mode shows it, and Reset hiding it again.
    /// </summary>
    private static void CheckDock(MainWindow main, Border[] cards)
    {
        Expander monitor = main.FindControl<Expander>("MonitorTile")!;
        Assert.True(monitor.IsExpanded);
        Wear("dock");
        Border Card(string tile) => (Border)main.FindControl<Expander>(tile)!.Parent!;
        var header = main.FindControl<Border>("HeaderCard")!;
        var content = main.FindControl<StackPanel>("MixerContent")!;
        DockPanel dock = Assert.IsType<DockPanel>(main.LayoutHost.PanelFor("window"));
        StackPanel side = Assert.IsType<StackPanel>(main.LayoutHost.PanelFor("side"));
        Assert.Same(dock, header.Parent);
        Assert.Equal(default, header.Margin);
        Assert.Same(main.FindControl<StackPanel>("ApplicationNotices"), content.Children[0]);
        Assert.DoesNotContain(header, content.Children);
        Assert.Same(dock, side.Parent);
        Assert.Equal(10, dock.HorizontalSpacing);

        // Folded on first sight, and recorded so it is not folded again.
        Assert.False(monitor.IsExpanded);
        Assert.True(UiSettings.Load().HasSeenSkinLayout("dock", 1));
        Assert.Contains("MonitorTile", UiSettings.Load().CollapsedSections);
        monitor.IsExpanded = true;

        Border applications = Card("ApplicationsTile");
        Assert.False(applications.IsVisible);
        Assert.False(RegionHost.GetShown(applications));
        foreach (double width in new[] { 640d, 1040, 2560 })
        {
            Layout(main, width, 1000);
            string where = $"at {width}";
            Border[] shown = [.. cards.Where(c => c.IsVisible)];
            Assert.Equal(4, shown.Length);
            AssertNoOverlap([.. shown, header]);
            double headerTop = header.TranslatePoint(default, dock)!.Value.Y;
            Assert.All(shown, card => Assert.True(card.TranslatePoint(new Point(0, card.Bounds.Height), dock)!.Value.Y <= headerTop + 0.5, where));
            Assert.Equal(dock.Bounds.Width, header.Bounds.Width, 1);
            Assert.Equal(700, Card("InputsTile").Bounds.Width, 0.5);
            Assert.Equal(0, Card("InputsTile").TranslatePoint(default, dock)!.Value.X, 0.5);
            Assert.True(Card("SubmixerTile").TranslatePoint(default, dock)!.Value.X >= 700 + 10 - 0.5, where);
            double sideLeft = side.TranslatePoint(default, dock)!.Value.X;
            Assert.True(sideLeft >= Card("SubmixerTile").TranslatePoint(new Point(Card("SubmixerTile").Bounds.Width, 0), dock)!.Value.X - 0.5, where);
            // In the column a preferred width is ignored: HEADPHONES is as wide as MONITOR under it.
            Assert.Equal(Card("MonitorTile").Bounds.Width, Card("HeadphonesTile").Bounds.Width, 0.5);
            Assert.Equal(SkinLayoutDefaults.RowSectionMinWidth, Card("HeadphonesTile").Bounds.Width, 0.5);
            Assert.True(Card("HeadphonesTile").Bounds.Height >= 260 - 0.5, where);
            Assert.True(Card("SubmixerTile").Bounds.Width >= SkinLayoutDefaults.RowSectionMinWidth - 0.5, where);
            var scroll = main.FindControl<ScrollViewer>("SectionScroll")!;
            foreach (Border card in shown) AssertControlsInside(card, scroll);
            Assert.True(dock.Bounds.Width >= 700 + 10 + 600 + 10 + 600 - 0.5, where);
            Capture(main, $"skin-layout-dock-{width}");
        }
        Layout(main, 2560, 1000);

        // Focus follows the dock's order, the header first, and skips the hidden section.
        Assert.Equal(["header", "inputs", "headphones", "monitor", "submixer"], TabRegions(main));

        // Arrange lists the hidden section with a way back, kept for the skin.
        var hidden = main.FindControl<WrapPanel>("HiddenSections")!;
        Assert.False(hidden.IsVisible);
        Arrange(main, true);
        Assert.True(hidden.IsVisible);
        Button show = Assert.Single(hidden.Children.OfType<Button>());
        Assert.Equal(Localizer.Format("ShowSection", Localizer.Text("APPLICATIONS")), show.Content);
        show.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout(main, 2560, 1000);
        Assert.True(applications.IsVisible);
        Assert.False(hidden.IsVisible);
        Assert.Equal(["applications"], UiSettings.Load().SkinArrangementFor("dock", 1)!.Shown);
        AssertNoOverlap([.. cards, header]);
        Arrange(main, false);
        Assert.Equal(["header", "inputs", "headphones", "monitor", "applications", "submixer"], TabRegions(main));

        // A switch away and back keeps it shown and does not fold MONITOR again.
        Wear("grid");
        Wear("dock");
        Layout(main, 2560, 1000);
        Assert.True(applications.IsVisible);
        Assert.True(monitor.IsExpanded);

        Arrange(main, true);
        main.FindControl<Button>("ResetSectionsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout(main, 2560, 1000);
        Assert.False(applications.IsVisible);
        Assert.True(hidden.IsVisible);
        Assert.Null(UiSettings.Load().SkinArrangementFor("dock", 1));
        Arrange(main, false);
        Layout(main, 1040, 900);
    }

    /// <summary>
    /// A saved arrangement that names a place the tree no longer has is
    /// dropped whole when a window resolves it, said once in that window's
    /// note, and gone for the next window. A window that opens wearing the
    /// skin resolves it on its own, so the other windows of the test run
    /// cannot get there first through the shared ui.json.
    /// </summary>
    private static void CheckStale()
    {
        Wear("grid");
        Assert.Null(UiSettings.Load().WithSkinArrangement("grid", 1,
            new Dictionary<string, string> { ["desktop:0,0"] = "monitor", ["desktop:0,1"] = "inputs", ["gone:0"] = "submixer" }, null).Save());
        foreach (bool first in new[] { true, false })
        {
            var fresh = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
            try
            {
                fresh.Show();
                Layout(fresh, 1040, 900);
                var note = fresh.FindControl<TextBlock>("ArrangementNote")!;
                Assert.Equal(first, note.IsVisible);
                Assert.Equal(0, Grid.GetColumn((Control)fresh.FindControl<Expander>("InputsTile")!.Parent!));
                Assert.Equal(0, Grid.GetRow((Control)fresh.FindControl<Expander>("InputsTile")!.Parent!));
                Assert.Null(UiSettings.Load().SkinArrangementFor("grid", 1));
                if (!first) continue;
                Assert.Equal(Localizer.Text("ArrangementDropped"), note.Text);
                Arrange(fresh, true);
                Arrange(fresh, false);
                Assert.False(note.IsVisible);
            }
            finally
            {
                fresh.Close();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            }
        }
    }

    /// <summary>The region of each control focus visits from Options onwards, each region once in turn.</summary>
    private static string[] TabRegions(MainWindow main)
    {
        var hosts = new Dictionary<Control, string> { [main.FindControl<Border>("HeaderCard")!] = "header" };
        foreach (string region in SkinLayoutRegions.Sections)
            hosts[(Control)main.FindControl<Expander>(SectionArrangement.TileFor(region))!.Parent!] = region;
        var regions = new List<string>();
        IInputElement? element = main.FindControl<Button>("OptionsButton");
        for (int i = 0; i < 400 && element is not null; i++)
        {
            if (element is Visual visual && visual.GetSelfAndVisualAncestors().OfType<Control>().FirstOrDefault(hosts.ContainsKey) is { } host
                && (regions.Count == 0 || regions[^1] != hosts[host]))
            {
                // Back at a region already visited: focus has come round.
                if (regions.Contains(hosts[host])) break;
                regions.Add(hosts[host]);
            }
            element = main.FocusManager!.FindNextElement(NavigationDirection.Next, new FindNextElementOptions { FocusedElement = element });
            if (ReferenceEquals(element, main.FindControl<Button>("OptionsButton"))) break;
        }
        return [.. regions];
    }

    /// <summary>Every control a section draws itself sits inside it; a scroller of its own, like the channel row, is checked by it.</summary>
    private static void AssertControlsInside(Border card, ScrollViewer scroll)
    {
        foreach (Control child in card.GetVisualDescendants().OfType<Control>()
                     .Where(c => c is TextBlock or Slider or Button or ComboBox && c.IsEffectivelyVisible && c.Bounds.Width > 0
                                 && ReferenceEquals(c.GetVisualAncestors().OfType<ScrollViewer>().First(), scroll)))
            AssertInside(child, card);
    }

    /// <summary>
    /// The row: one line along the top, sections side by side and apart, each
    /// at least its minimum width with its controls inside it, the header at
    /// the window's width, and the row scrolling to its last section.
    /// </summary>
    private static void CheckRow(MainWindow main, Border[] cards)
    {
        var scroll = main.FindControl<ScrollViewer>("SectionScroll")!;
        double top = cards[0].TranslatePoint(default, main)!.Value.Y;
        foreach (Border card in cards)
        {
            Assert.InRange(card.TranslatePoint(default, main)!.Value.Y, top - 0.5, top + 0.5);
            Assert.True(card.Bounds.Width >= SkinLayoutDefaults.RowSectionMinWidth, $"a section is {card.Bounds.Width} wide");
            AssertControlsInside(card, scroll);
        }
        AssertNoOverlap(cards);

        var content = main.FindControl<StackPanel>("MixerContent")!;
        Assert.InRange(content.Bounds.Width, 1040 - 72, 1040 - 16);
        AssertInside(main.FindControl<Grid>("WindowHeader")!, main);

        Assert.Equal(ScrollBarVisibility.Auto, scroll.HorizontalScrollBarVisibility);
        Assert.True(scroll.Extent.Width > scroll.Viewport.Width + 1,
            $"the row is {scroll.Extent.Width} wide in a {scroll.Viewport.Width} viewport");
        scroll.Offset = new Vector(scroll.Extent.Width - scroll.Viewport.Width, 0);
        Layout(main, 1040, 900);
        Border last = cards.OrderBy(c => c.TranslatePoint(default, scroll)!.Value.X).Last();
        double right = last.TranslatePoint(default, scroll)!.Value.X + last.Bounds.Width;
        Assert.InRange(right, 0, scroll.Viewport.Width + 0.5);
        scroll.Offset = default;
        Layout(main, 1040, 900);
    }

    private static void Install(string dir, string id, string orientation, double spacing, string[] order)
    {
        string children = string.Join(",", order.Select(tile =>
            $"{{\"kind\":\"region\",\"region\":\"{SectionArrangement.RegionFor(tile)}\"}}"));
        Write(dir, id, $"{{\"schema\":1,\"name\":\"{id}\",\"tokens\":{{\"Ox.Card.Background\":\"#22262e\"}},"
            + $"\"layout\":{{\"version\":1,\"main\":{{\"kind\":\"stack\",\"id\":\"main\",\"orientation\":\"{orientation}\","
            + $"\"spacing\":{spacing},\"children\":[{children}]}}}}}}");
    }

    private static void Write(string dir, string id, string json)
    {
        string folder = Path.Combine(dir, "data", "openxlr", "skins", id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "skin.json"), json);
    }

    internal static void Wear(string id)
    {
        SkinEntry entry = SkinCatalog.Find(id)!;
        Assert.Empty(entry.Errors);
        Assert.Empty(SkinService.Apply(entry));
    }

    private static void Arrange(MainWindow main, bool on)
    {
        var arrange = main.FindControl<ToggleButton>("ArrangeButton")!;
        arrange.IsChecked = on;
        arrange.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout(main, main.ClientSize.Width, main.ClientSize.Height);
    }

    private static StackPanel Cards(MainWindow main) => main.FindControl<StackPanel>("SectionCards")!;

    private static string[] Order(MainWindow main) =>
        [.. Cards(main).Children.OfType<Border>().Select(b => ((Expander)b.Child!).Name!)];

    /// <summary>The sections as they stand on screen, down the window or across it.</summary>
    private static string[] OnScreen(MainWindow main, bool byX) =>
        [.. Cards(main).Children.OfType<Border>()
            .OrderBy(b => byX ? b.TranslatePoint(default, main)!.Value.X : b.TranslatePoint(default, main)!.Value.Y)
            .Select(b => ((Expander)b.Child!).Name!)];

    private static Button SectionHandle(MainWindow main, string tile) =>
        ((Panel)main.FindControl<Expander>(tile)!.Header!).Children.OfType<Button>()
            .Single(b => b.Classes.Contains("reorderHandle"));
}
