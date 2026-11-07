using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Skinning;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

/// <summary>
/// APPLICATIONS in columns, as the shipped Broadcast and Wave skins draw it:
/// with 1, 6, 7 and 13 applications, at 640, 1600 and 2560 pixels, in
/// Standard and Touch sizing, the tiles stand in columns of at most four in
/// the applications' order, the fifth starting the next column, and a
/// column that does not fit goes on below, whole, with nothing scrolling
/// sideways. Every tile is the same width whatever its name and channel: its
/// name is the skin's name width and its picker one width in every tile, wide
/// enough for the longest channel. A name longer than its room is clipped at
/// rest and glides to its end while the pointer is over its tile. The default
/// skin's chips keep the width of their own name and channel.
/// </summary>
internal static partial class SkinLayoutWindowTests
{
    private static readonly string[] ColumnApps =
    [
        "Looking Glass", "Discord", "Google Chrome", "Karere", "Signal", "Steam", "Firefox",
        "Spotify", "OBS Studio", "VLC media player", "Telegram", "Zoom", "Thunderbird",
    ];

    /// <summary>A name longer than the name width, as a helper stream of Discord's calls itself.</summary>
    private const string LongAppName = "discord_hardware_mute_background_audio_process";

    private static void CheckApplicationColumns()
    {
        foreach (string id in new[] { "broadcast", "wave" })
        {
            JsonNode skin;
            using (Stream stream = typeof(SkinCatalog).Assembly.GetManifestResourceStream($"OpenXLR.UI.Assets.Skins.{id}.skin.json")!)
                skin = JsonNode.Parse(stream)!;
            JsonNode applications = skin["layout"]!["regions"]!["applications"]!;
            Assert.Equal(SkinLayoutTemplates.ApplicationColumns, applications["template"]!.GetValue<string>());
            Assert.Equal(4, applications["perColumn"]!.GetValue<int>());
        }

        var window = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
        try
        {
            var model = (MainViewModel)window.DataContext!;
            typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(model, [JsonNode.Parse("""
                {"mixes":[{"id":"monitor","name":"Monitor A","kind":"monitor"},{"id":"stream","name":"Stream","kind":"virtualMic"}],
                 "channels":[{"id":"music","name":"Music","levels":{"monitor":0.5}},{"id":"game","name":"Game"},
                             {"id":"chat","name":"Voice Chat"}]}
                """)]);
            ChannelChoice[] choices = [new("music", "Music"), new("game", "Game"), new("chat", "Voice Chat")];
            AppStreamViewModel Make(string name, int i)
            {
                var app = new AppStreamViewModel(new DaemonClient("ws://127.0.0.1:1/ws"), name.ToLowerInvariant(), name, choices);
                app.ApplyFromDaemon(choices[i % choices.Length].Id, i % 4 != 1, true);
                model.Apps.Add(app);
                return app;
            }
            AppStreamViewModel[] all = [.. ColumnApps.Select(Make)];
            window.Show();
            Layout(window, 1040, 900);

            foreach (string id in new[] { "broadcast", "wave" })
            foreach (bool touch in new[] { false, true })
            {
                SkinService.ApplyControlSizing(touch);
                Wear(id);
                foreach (int count in new[] { 1, 6, 7, 13 })
                {
                    ShowApps(model, all, count);
                    foreach (double width in new[] { 640d, 1600, 2560 })
                    {
                        Layout(window, width, 1400);
                        CheckColumnsAt(window, model, $"{id}, {count} applications, {(touch ? "touch" : "standard")} at {width}");
                        if (count == 13 && !touch) Capture(window, $"skin-{id}-applications-13-{width}");
                    }
                }
            }
            SkinService.ApplyControlSizing(false);

            // A name longer than its room, once: clipped at rest, to its end under the pointer and back.
            Wear("broadcast");
            ShowApps(model, [.. all, Make(LongAppName, 0)], all.Length + 1);
            Layout(window, 1600, 1400);
            CheckColumnsAt(window, model, "broadcast with a long name at 1600");
            CheckLongName(window, model);
            model.ActiveApps.Remove(model.ActiveApps.Single(a => a.Label == LongAppName));

            // Classic chips wrap while keeping their different natural widths.
            Wear(SkinPackage.DefaultId);
            ShowApps(model, all, all.Length);
            Layout(window, 1600, 1400);
            Border[] chips = ApplicationChips(window);
            Assert.Equal(ColumnApps.Length, chips.Length);
            Assert.Empty(ApplicationTiles(window));
            Assert.Equal(1, Assert.IsType<TileColumnsPanel>(window.FindControl<ItemsControl>("ApplicationRows")!.ItemsPanelRoot).PerColumn);
            Assert.True(chips.Max(c => c.Bounds.Width) - chips.Min(c => c.Bounds.Width) > 20, "the default chips are all one width");
            Capture(window, "default-applications-13-1600");
        }
        finally
        {
            SkinService.ApplyControlSizing(false);
            window.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// The long name's tile is as wide as the others; its name is clipped at its
    /// start and still while the pointer is away, glides to its end while the
    /// pointer is over the tile, and back when it leaves. A name that fits does
    /// not move. The tooltip carries the whole name.
    /// </summary>
    private static void CheckLongName(MainWindow window, MainViewModel model)
    {
        Border Tile(string label) => ApplicationTiles(window).Single(t => ((AppStreamViewModel)t.DataContext!).Label == label);
        ScrollingName NameOf(Border tile) => tile.GetVisualDescendants().OfType<ScrollingName>().Single();
        Border tile = Tile(LongAppName), other = Tile("Signal");
        ScrollingName name = NameOf(tile), fits = NameOf(other);
        var text = (TextBlock)name.Child!;
        Assert.Equal(LongAppName, ToolTip.GetTip(name));
        Assert.True(name.ClipToBounds);
        Assert.True(text.Bounds.Width > name.Bounds.Width + 8, $"the long name is {text.Bounds.Width:0.#} wide in {name.Bounds.Width:0.#}");
        Assert.Equal(0, fits.Overflow);
        double Left() => text.TranslatePoint(default, name)!.Value.X;
        double Right() => Left() + text.Bounds.Width;
        Assert.Equal(0, Left(), 0.5);
        Capture(window, "skin-broadcast-applications-long-name-rest");

        // InputElement sets IsPointerOver as the pointer moves; the tile's name binds to it.
        PropertyInfo over = typeof(InputElement).GetProperty(nameof(InputElement.IsPointerOver))!;
        over.SetValue(tile, true);
        over.SetValue(other, true);
        Assert.True(name.Scrolls);
        Assert.True(fits.Scrolls);
        Assert.False(fits.Gliding);
        Assert.True(Pump(() => !name.Gliding), "the long name did not reach its end");
        Assert.Equal(name.Overflow, name.Offset, 0.5);
        Assert.Equal(name.Bounds.Width, Right(), 0.5);
        Assert.Equal(0, fits.Offset);
        Capture(window, "skin-broadcast-applications-long-name-pointer");

        over.SetValue(tile, false);
        over.SetValue(other, false);
        Assert.False(name.Scrolls);
        Assert.True(Pump(() => !name.Gliding), "the long name did not come back");
        Assert.Equal(0, name.Offset, 0.5);
        Assert.Equal(0, Left(), 0.5);
    }

    /// <summary>Runs the window until <paramref name="done"/> holds, for at most five seconds.</summary>
    private static bool Pump(Func<bool> done)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < deadline)
        {
            using var stop = new CancellationTokenSource();
            Avalonia.Threading.DispatcherTimer.RunOnce(stop.Cancel, TimeSpan.FromMilliseconds(50));
            Avalonia.Threading.Dispatcher.UIThread.MainLoop(stop.Token);
        }
        return done();
    }

    private static void ShowApps(MainViewModel model, AppStreamViewModel[] all, int count)
    {
        model.ActiveApps.Clear();
        foreach (AppStreamViewModel app in all.Take(count)) model.ActiveApps.Add(app);
    }

    /// <summary>Tiles flow down columns of up to four, then across or into the next band.</summary>
    private static void CheckColumnsAt(MainWindow window, MainViewModel model, string where)
    {
        const int maximumPerColumn = 4;
        var rows = window.FindControl<ItemsControl>("ApplicationRows")!;
        TileColumnsPanel panel = Assert.IsType<TileColumnsPanel>(rows.ItemsPanelRoot);
        Assert.Equal(maximumPerColumn, panel.PerColumn);
        Border card = (Border)window.FindControl<Expander>("ApplicationsTile")!.Parent!;
        Assert.True(card.IsVisible, where);
        AppStreamViewModel[] order = [.. model.ActiveApps];
        Border[] all = ApplicationTiles(window);
        Assert.Equal(order.Length, all.Length);
        Assert.Empty(ApplicationLines(window));
        Assert.Empty(ApplicationChips(window));
        Border[] chips = [.. order.Select(app => all.Single(c => c.DataContext == app))];
        Point At(int i) => chips[i].TranslatePoint(default, panel)!.Value;
        double Right(int i) => At(i).X + chips[i].Bounds.Width;
        // What a chip takes in its column: its width and its margin, which spaces the columns.
        double Taken(int i) => chips[i].Margin.Left + chips[i].Bounds.Width + chips[i].Margin.Right;
        double Bottom(int i) => At(i).Y + chips[i].Bounds.Height;
        int perColumn = Enumerable.Range(1, chips.Length - 1).FirstOrDefault(i => At(i).X > 0.5);
        if (perColumn == 0) perColumn = Math.Min(maximumPerColumn, chips.Length);
        Assert.InRange(perColumn, 1, maximumPerColumn);

        AssertNoOverlap(chips);
        CheckTileParts(window, chips, where);
        for (int i = 0; i < chips.Length; i++)
        {
            string chip = $"{order[i].Label} ({i}) {where}";
            AssertInside(chips[i], card);
            if (i == 0)
            {
                Assert.Equal(0, At(0).X, 0.5);
                Assert.Equal(0, At(0).Y, 0.5);
            }
            else if (i % perColumn != 0)
            {
                // Down the column, under the one before.
                Assert.True(Math.Abs(At(i).X - At(i - 1).X) <= 0.5, $"{chip}: at x {At(i).X:0.#}, the chip above at {At(i - 1).X:0.#}");
                Assert.True(At(i).Y >= Bottom(i - 1) - 0.5, $"{chip}: at y {At(i).Y:0.#}, the chip above ends at {Bottom(i - 1):0.#}");
            }
            else
            {
                // A new column: to the right of the last one, level with its top, or a whole row of columns below.
                int top = i - perColumn;
                double columnRight = Enumerable.Range(top, perColumn).Max(Right);
                bool beside = Math.Abs(At(i).Y - At(top).Y) <= 0.5 && At(i).X >= columnRight - 0.5;
                bool below = Math.Abs(At(i).X) <= 0.5 && At(i).Y >= Enumerable.Range(0, i).Max(Bottom) - 0.5;
                Assert.True(beside || below, $"{chip}: at {At(i)}, the column before starts at {At(top)} and ends at x {columnRight:0.#}");
                // It goes below only when it would not fit beside.
                double end = Enumerable.Range(top, perColumn).Max(j => At(j).X - chips[j].Margin.Left + Taken(j));
                double columnWidth = Enumerable.Range(i, Math.Min(perColumn, chips.Length - i)).Max(Taken);
                if (below) Assert.True(end + columnWidth > panel.Bounds.Width + 0.5, $"{chip} went below with room beside");
            }
        }
        // Two or three columns of these chips fit side by side from 1600 pixels.
        if (window.Bounds.Width >= 1600)
            Assert.All(Enumerable.Range(0, chips.Length).Where(i => i % perColumn == 0), i => Assert.Equal(0, At(i).Y, 0.5));
        // Nothing scrolls sideways for the applications.
        var scroll = window.FindControl<ScrollViewer>("SectionScroll")!;
        Assert.True(panel.Bounds.Width <= card.Bounds.Width, where);
        if (scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled)
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1, $"the page is {scroll.Extent.Width} wide in {scroll.Viewport.Width} {where}");
        ApplicationIconWindowTests.Check(window, where);
    }

    /// <summary>
    /// Every tile one width, its name the skin's name width and its picker one
    /// width at one place in every tile, so pickers line up down a column; the
    /// picker holds the longest channel it lists, and its selection shows whole.
    /// </summary>
    private static void CheckTileParts(MainWindow window, Border[] tiles, string where)
    {
        double nameWidth = (double)window.FindResource("Ox.AppColumns.NameWidth")!;
        double maximum = (double)window.FindResource("Ox.AppColumns.TileMaxWidth")!;
        (double Width, double Name, double PickerX, double PickerWidth) first = default;
        for (int i = 0; i < tiles.Length; i++)
        {
            string tile = $"{((AppStreamViewModel)tiles[i].DataContext!).Label} {where}";
            ScrollingName name = tiles[i].GetVisualDescendants().OfType<ScrollingName>().Single();
            ComboBox picker = tiles[i].GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "ApplicationChannel");
            if (maximum > 0) Assert.True(name.Bounds.Width >= nameWidth - 0.5, tile);
            else Assert.Equal(nameWidth, name.Bounds.Width, 0.5);
            AssertInside(name, tiles[i]);
            AssertInside(picker, tiles[i]);
            ItemsControl sizer = picker.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "ApplicationChannelSizer");
            TextBlock[] names = [.. sizer.GetVisualDescendants().OfType<TextBlock>()];
            Assert.Equal(picker.ItemCount, names.Length);
            Assert.Equal(names.Max(t => t.Bounds.Width), sizer.Bounds.Width, 0.5);
            TextBlock selected = picker.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.Text == ((ChannelChoice)picker.SelectedItem!).Name && !names.Contains(t));
            AssertInside(sizer, picker);
            AssertInside(selected, picker);
            (double Width, double Name, double PickerX, double PickerWidth) here =
                (tiles[i].Bounds.Width, name.Bounds.Width, picker.TranslatePoint(default, tiles[i])!.Value.X, picker.Bounds.Width);
            if (i == 0) { first = here; continue; }
            // Tiles that grow into spare width all grow alike, so a short last
            // band lines up with the bands above it.
            Assert.True(Math.Abs(here.Width - first.Width) <= 0.5, $"{tile}: {here.Width:0.#} wide, the first tile {first.Width:0.#}");
            Assert.True(Math.Abs(here.PickerX - first.PickerX) <= 0.5, $"{tile}: picker at {here.PickerX:0.#}, the first tile's at {first.PickerX:0.#}");
            Assert.True(Math.Abs(here.PickerWidth - first.PickerWidth) <= 0.5, $"{tile}: picker {here.PickerWidth:0.#} wide, the first tile's {first.PickerWidth:0.#}");
        }
    }
}
