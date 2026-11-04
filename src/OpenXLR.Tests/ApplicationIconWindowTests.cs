using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Views;
using static OpenXLR.Tests.WindowLayoutTests;
using Path = System.IO.Path;

namespace OpenXLR.Tests;

/// <summary>
/// Application icons on a real window, run inside the window layout and
/// skin layout processes: a fake icon theme with a PNG and an SVG icon, an
/// application with an image path, one whose icon the theme does not have
/// and one with no icon at all. Every row shows its icon, or its initial on
/// a badge, at the token's size, centred on the name beside it and clear of
/// the row's other controls.
/// </summary>
internal static class ApplicationIconWindowTests
{
    private static string? _root;
    private static bool _svg;

    /// <summary>The icon each fixture application gets, by label; null draws the initial.</summary>
    internal static readonly IReadOnlyDictionary<string, string?> Icons = new Dictionary<string, string?>
    {
        ["Google Chrome"] = "test-chrome",
        ["Google Chrome with a very long window title"] = "test-chrome",
        ["Discord"] = "test-discord",
        ["Karere"] = "PATH",
        ["Signal"] = "not-in-the-theme",
        ["Steam"] = null,
    };

    /// <summary>A theme with the fixture icons, made once per process and used by the window from now on.</summary>
    internal static void Install()
    {
        if (_root is not null) return;
        _root = Directory.CreateTempSubdirectory("openxlr-icon-window-").FullName;
        string hicolor = Path.Combine(_root, "icons/hicolor");
        Directory.CreateDirectory(Path.Combine(hicolor, "48x48/apps"));
        Directory.CreateDirectory(Path.Combine(hicolor, "scalable/apps"));
        File.WriteAllText(Path.Combine(hicolor, "index.theme"), """
            [Icon Theme]
            Name=hicolor
            Directories=48x48/apps,scalable/apps

            [48x48/apps]
            Size=48
            Type=Fixed

            [scalable/apps]
            Size=128
            Type=Scalable
            MinSize=8
            MaxSize=512
            """);
        Png(Path.Combine(hicolor, "48x48/apps/test-chrome.png"), Color.Parse("#4285f4"));
        Png(Path.Combine(_root, "karere.png"), Color.Parse("#25d366"));
        string svg = Path.Combine(hicolor, "scalable/apps/test-discord.svg");
        File.WriteAllText(svg, """
            <svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" viewBox="0 0 48 48">
              <rect x="2" y="2" width="44" height="44" rx="10" fill="#5865f2"/>
              <circle cx="17" cy="24" r="5" fill="#ffffff"/><circle cx="31" cy="24" r="5" fill="#ffffff"/>
            </svg>
            """);
        // GdkPixbuf and its SVG module are a desktop's, not a test runner's.
        _svg = SvgRaster.Render(svg, 18) is not null;
        ApplicationIconImages.Lookup = new IconThemeLookup([Path.Combine(_root, "icons")], [Path.Combine(_root, "pixmaps")], null);
    }

    private static void Png(string file, Color colour)
    {
        var face = new Border { Width = 48, Height = 48, CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(colour),
            Child = new Ellipse { Width = 20, Height = 20, Fill = Brushes.White } };
        face.Measure(new Size(48, 48));
        face.Arrange(new Rect(0, 0, 48, 48));
        using var bitmap = new RenderTargetBitmap(new PixelSize(48, 48));
        bitmap.Render(face);
        bitmap.Save(file, PngBitmapEncoderOptions.Default);
    }

    /// <summary>Give the fixture applications their icons.</summary>
    internal static void Assign(IEnumerable<AppStreamViewModel> apps)
    {
        Install();
        foreach (AppStreamViewModel app in apps)
            app.Icon = Icons.TryGetValue(app.Label, out string? icon) && icon == "PATH"
                ? Path.Combine(_root!, "karere.png")
                : Icons.GetValueOrDefault(app.Label);
    }

    private static bool Expected(AppStreamViewModel app)
        => app.Icon is { } icon && (icon.StartsWith('/') || icon == "test-chrome" || icon == "test-discord" && _svg);

    /// <summary>Every visible application row, chip or line, wears its icon or its initial properly.</summary>
    internal static void Check(Window window, string where)
    {
        ApplicationIconView[] views = Views(window);
        Assert.NotEmpty(views);
        // The pictures are found and decoded on a worker; wait for them.
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (views.Any(v => Expected((AppStreamViewModel)v.DataContext!) != v.ShowsPicture) && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
            views = Views(window);
        }
        double size = (double)window.FindResource("Ox.AppIcon.Size")!;
        foreach (ApplicationIconView view in views)
        {
            var app = (AppStreamViewModel)view.DataContext!;
            string here = $"{app.Label} {where}";
            Assert.True(Expected(app) == view.ShowsPicture, $"{here}: picture {view.ShowsPicture}, expected {Expected(app)}");
            Assert.Equal(size, view.Bounds.Width, 0.5);
            Assert.Equal(size, view.Bounds.Height, 0.5);
            Control row = view.GetVisualAncestors().OfType<Control>()
                .First(b => b.Name is "ApplicationChip" or "ApplicationLine" or "ApplicationTile" or "ApplicationRow");
            Assert.Same(app, row.DataContext);
            AssertInside(view, row);
            TextBlock label = row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == app.Label && t.IsEffectivelyVisible);
            var led = row.GetVisualDescendants().OfType<OpenXLR.UI.Skinning.StatusLed>().Single();
            var picker = row.GetVisualDescendants().OfType<ComboBox>().Single();
            // A tile's name runs on under its clip; the part that shows is the clip.
            Control shown = label.Parent as OpenXLR.UI.Layout.ScrollingName ?? (Control)label;
            // The Manage window's rows end with Forget.
            AssertNoOverlap([led, view, shown, picker, .. row.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible)]);
            // Before the name, centred on it.
            Point icon = view.TranslatePoint(new Point(0, size / 2), window)!.Value;
            Point name = label.TranslatePoint(new Point(0, label.Bounds.Height / 2), window)!.Value;
            Assert.True(icon.X < name.X, $"{here}: the icon is not before the name");
            Assert.True(Math.Abs(icon.Y - name.Y) <= 1.5, $"{here}: the icon is {icon.Y - name.Y} off the name's centre");
            if (!view.ShowsPicture)
            {
                TextBlock initial = view.GetVisualDescendants().OfType<TextBlock>().Single();
                Assert.True(initial.IsEffectivelyVisible, here);
                Assert.Equal(ApplicationIconView.InitialOf(app.Label), initial.Text);
                AssertInside(initial, view);
            }
        }
    }

    /// <summary>
    /// The Manage window lists every known application with the icon its tile
    /// shows: one icon per row, bound to that row's application, a remembered
    /// app that is not running included, and a name too long for its column
    /// still trimmed inside it, clear of the icon and the channel picker.
    /// </summary>
    internal static void CheckManage(MainViewModel vm)
    {
        const string longName = "Quiet application with a window title far too long for the Manage window's name column";
        var channels = new ChannelChoice[] { new("browser", "Browser"), new("game", "Game") };
        var remembered = new AppStreamViewModel(new DaemonClient(), "signal-desktop", "Signal Desktop", channels);
        remembered.ApplyFromDaemon("game", false, false);
        var lengthy = new AppStreamViewModel(new DaemonClient(), "quiet", longName, channels);
        lengthy.ApplyFromDaemon("browser", false, false);
        Install();
        remembered.Icon = "test-discord";
        vm.Apps.Add(remembered);
        vm.Apps.Add(lengthy);
        var window = new AppsWindow { DataContext = vm };
        try
        {
            window.Show();
            Layout(window, 620, 700);
            ApplicationIconView[] views = Views(window);
            Assert.Equal(vm.Apps.Count, views.Length);
            Assert.Equal(vm.Apps, views.Select(v => v.DataContext));
            foreach (ApplicationIconView view in views)
            {
                var app = (AppStreamViewModel)view.DataContext!;
                Assert.Equal(app.Icon, view.Icon);
                Assert.Equal(app.Label, view.Label);
            }
            Check(window, "in Manage");

            // The icons sit in one column, so the names start in one too.
            Assert.Single(views.Select(v => Math.Round(v.TranslatePoint(default, window)!.Value.X, 1)).Distinct());

            TextBlock name = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == longName);
            Assert.Equal(TextTrimming.CharacterEllipsis, name.TextTrimming);
            var whole = new TextBlock { Text = longName, FontFamily = name.FontFamily, FontSize = name.FontSize, FontWeight = name.FontWeight };
            whole.Measure(Size.Infinity);
            Assert.True(whole.DesiredSize.Width > name.Bounds.Width + 1, $"the long name is not cut: {name.Bounds.Width} of {whole.DesiredSize.Width}");
            AssertInside(name, (Control)name.Parent!);
            AssertInside(name, window);
        }
        finally
        {
            window.Close();
            vm.Apps.Remove(remembered);
            vm.Apps.Remove(lengthy);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static ApplicationIconView[] Views(Window window)
        => [.. window.GetVisualDescendants().OfType<ApplicationIconView>().Where(v => v.IsEffectivelyVisible)];
}
