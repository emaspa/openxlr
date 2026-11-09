using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Localization;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

/// <summary>
/// Options in tabs: which card each tab holds, that the window keeps its size
/// as the tabs change, that each tab fits the window, the keys that move
/// between tabs, the tab a reopened window shows, and the welcome guide's
/// shortcuts landing on the tab that holds their card.
/// </summary>
internal static class OptionsTabsWindowTests
{
    /// <summary>The tab names' keys, in the order of the strip.</summary>
    internal static readonly string[] TabKeys =
        ["OptionsTabGeneral", "OptionsTabAudio", "OptionsTabPlugins", "OptionsTabAppearance", "OptionsTabSupport"];

    /// <summary>The cards each tab holds, by the key of their heading, top to bottom.</summary>
    internal static readonly (OptionsTab Tab, string[] Headings)[] Cards =
    [
        (OptionsTab.General, ["ATLOGIN", "WINDOW", "SettingsBackup", "DESKTOP", "UPDATES"]),
        (OptionsTab.Audio, ["AUDIO", "SYSTEMDEFAULTDEVICES", "INTERFACE"]),
        (OptionsTab.Plugins, ["PLUGINS"]),
        (OptionsTab.Appearance, ["APPEARANCE"]),
        (OptionsTab.Support, ["SUPPORT"]),
    ];

    /// <summary>Each shortcut the welcome guide has into Options, and the tab its card is on.</summary>
    internal static readonly (string Section, OptionsTab Tab)[] GuideSections =
    [
        ("GuideStartup", OptionsTab.General),
        ("GuideAudio", OptionsTab.Audio),
        ("GuideDefaults", OptionsTab.Audio),
        ("GuideAppearance", OptionsTab.Appearance),
        ("GuideSupport", OptionsTab.Support),
    ];

    internal static TabItem[] Tabs(OptionsWindow options) =>
        [.. options.FindControl<TabControl>("OptionsTabs")!.Items.OfType<TabItem>()];

    /// <summary>The scrolled page a tab shows.</summary>
    internal static ScrollViewer Page(OptionsWindow options, OptionsTab tab) =>
        Assert.IsType<ScrollViewer>(Tabs(options)[(int)tab].Content);

    /// <summary>Show a tab and lay the window out with it, as a click on the tab would.</summary>
    internal static void Show(OptionsWindow options, OptionsTab tab)
    {
        options.SelectedTab = tab;
        Dispatcher.UIThread.RunJobs();
        options.UpdateLayout();
    }

    /// <summary>
    /// What <paramref name="pick"/> finds in each tab while it is shown, for a
    /// check over the whole of Options: only the tab on show is in the window.
    /// The tab that was showing is shown again afterwards.
    /// </summary>
    internal static List<T> InEveryTab<T>(OptionsWindow options, Func<Visual, IEnumerable<T>> pick)
    {
        OptionsTab before = options.SelectedTab;
        var found = new List<T>();
        try
        {
            foreach (OptionsTab tab in Enum.GetValues<OptionsTab>())
            {
                Show(options, tab);
                found.AddRange(pick(options));
            }
        }
        finally { Show(options, before); }
        return found;
    }

    /// <summary>The heading of each card in a tab, top to bottom, shown or not.</summary>
    private static string[] Headings(TabItem tab) =>
        [.. tab.GetLogicalDescendants().OfType<Border>().Where(b => b.Classes.Contains("card"))
            .Select(card => card.GetLogicalDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("h")).Text!)];

    internal static void Check(OptionsWindow options)
    {
        TabItem[] tabs = Tabs(options);
        Assert.Equal(TabKeys.Select(Localizer.Text), tabs.Select(t => t.Header));
        // A screen reader reads each tab by its name.
        foreach (TabItem tab in tabs) Assert.Equal(tab.Header, AutomationProperties.GetName(tab));
        // The row of tabs is one stop for Tab, and the arrow keys move along it.
        var strip = options.FindControl<TabControl>("OptionsTabs")!.GetVisualDescendants()
            .OfType<ItemsPresenter>().Single(p => p.Name == "PART_ItemsPresenter").Panel!;
        Assert.Equal(KeyboardNavigationMode.Once, KeyboardNavigation.GetTabNavigation(strip));

        // Every card is in its tab, and nothing in a tab runs out of the window
        // or makes the page scroll sideways. The window keeps its size as the
        // tabs change, so nothing moves under the pointer.
        // The status line and Close stay outside the tabs, on show with every one.
        var tabControl = options.FindControl<TabControl>("OptionsTabs")!;
        Assert.DoesNotContain(tabControl, options.FindControl<TextBlock>("PreferenceError")!.GetLogicalAncestors());
        Size size = options.ClientSize;
        foreach (var (tab, headings) in Cards)
        {
            Show(options, tab);
            Assert.Equal(size, options.ClientSize);
            Button close = options.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Localizer.Text("Close")));
            Assert.True(close.IsEffectivelyVisible);
            AssertInside(close, options);
            Assert.True(tabs[(int)tab].IsSelected);
            Assert.Equal(headings.Select(Localizer.Text), Headings(tabs[(int)tab]));
            ScrollViewer page = Page(options, tab);
            Assert.True(page.IsEffectivelyVisible, $"{tab} is not on show");
            Assert.True(page.Extent.Width <= page.Viewport.Width + 1,
                $"{tab} is {page.Extent.Width} wide in a page of {page.Viewport.Width}");
            foreach (Control control in page.GetVisualDescendants().OfType<Control>()
                .Where(c => c.IsEffectivelyVisible && c is Border { Classes: var classes } && classes.Contains("card")
                            || c.IsEffectivelyVisible && c is Button or ComboBox or CheckBox))
                AssertInside(control, options);
            Capture(options, $"options-tab-{tab.ToString().ToLowerInvariant()}");
        }
        // A tab the window has no room for scrolls inside it.
        Show(options, OptionsTab.Audio);
        Layout(options, options.ClientSize.Width, 420);
        ScrollViewer audio = Page(options, OptionsTab.Audio);
        Assert.True(audio.Extent.Height > audio.Viewport.Height,
            $"the Audio tab ({audio.Extent.Height}) fits a 420 window, so this no longer tests a scrolled tab");
        Layout(options, size.Width, size.Height);

        CheckKeys(options);
        CheckGuideSections(options);
        CheckSessionTab(options);
    }

    /// <summary>Ctrl+Tab and Ctrl+Page Down step forward through the tabs, with Shift or Page Up back, round the ends.</summary>
    private static void CheckKeys(OptionsWindow options)
    {
        void Press(Key key, KeyModifiers modifiers)
        {
            var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers };
            // Raised where the focus would be: inside the page on show.
            Page(options, options.SelectedTab).RaiseEvent(args);
            Assert.True(args.Handled, $"{modifiers}+{key} was not taken");
            // The focus moves to the new tab, and nothing it does there takes the step back.
            OptionsTab reached = options.SelectedTab;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(reached, options.SelectedTab);
        }

        Show(options, OptionsTab.General);
        Press(Key.Tab, KeyModifiers.Control);
        Assert.Equal(OptionsTab.Audio, options.SelectedTab);
        Press(Key.PageDown, KeyModifiers.Control);
        Assert.Equal(OptionsTab.Plugins, options.SelectedTab);
        Press(Key.PageUp, KeyModifiers.Control);
        Press(Key.Tab, KeyModifiers.Control | KeyModifiers.Shift);
        Assert.Equal(OptionsTab.General, options.SelectedTab);
        Press(Key.Tab, KeyModifiers.Control | KeyModifiers.Shift);
        Assert.Equal(OptionsTab.Support, options.SelectedTab);
        Press(Key.PageDown, KeyModifiers.Control);
        Assert.Equal(OptionsTab.General, options.SelectedTab);
        // Plain Tab is left to move the focus.
        var plain = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab };
        options.FindControl<ComboBox>("LaunchBehavior")!.RaiseEvent(plain);
        Assert.Equal(OptionsTab.General, options.SelectedTab);
    }

    /// <summary>
    /// Each guide shortcut shows the tab that holds its card, from whichever
    /// tab was on show, and brings the card's heading into view even when the
    /// page had been scrolled away from it.
    /// </summary>
    internal static void CheckGuideSections(OptionsWindow options)
    {
        foreach (var (section, tab) in GuideSections)
        {
            ScrollViewer page = Page(options, tab);
            Show(options, tab);
            page.Offset = new Vector(0, page.Extent.Height);
            options.UpdateLayout();
            Show(options, tab == OptionsTab.Support ? OptionsTab.General : OptionsTab.Support);

            options.ShowGuideSection(section);
            Dispatcher.UIThread.RunJobs();
            options.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(tab, options.SelectedTab);
            var heading = options.FindControl<TextBlock>(section)!;
            Assert.True(heading.IsEffectivelyVisible, $"{section} is not on show");
            double top = heading.TranslatePoint(default, page)!.Value.Y;
            Assert.True(top >= -0.5 && top + heading.Bounds.Height <= page.Viewport.Height + 0.5,
                $"{section} is at {top} in a page {page.Viewport.Height} tall");
        }
    }

    /// <summary>A new Options window opens on the tab the last one showed; a new run opens on General.</summary>
    private static void CheckSessionTab(OptionsWindow options)
    {
        Show(options, OptionsTab.Plugins);
        var again = new OptionsWindow { DataContext = options.DataContext };
        Assert.Equal(OptionsTab.Plugins, again.SelectedTab);
        OptionsWindow.ForgetLastTab();
        var fresh = new OptionsWindow { DataContext = options.DataContext };
        Assert.Equal(OptionsTab.General, fresh.SelectedTab);
        Assert.Equal(OptionsTab.Plugins, options.SelectedTab);
    }

    /// <summary>
    /// The tab names of every shipped language, at the text size on show, sit
    /// on one row inside the window and show whole.
    /// </summary>
    internal static void CheckTranslations(OptionsWindow options)
    {
        TabItem[] tabs = Tabs(options);
        object?[] shown = [.. tabs.Select(t => t.Header)];
        var failures = new List<string>();
        try
        {
            foreach (LanguageChoice language in Localizer.Languages)
            {
                CultureInfo culture = CultureInfo.GetCultureInfo(language.Id!);
                for (int i = 0; i < tabs.Length; i++) tabs[i].Header = Localizer.Get(TabKeys[i], culture);
                options.UpdateLayout();
                double row = tabs[0].TranslatePoint(default, options)!.Value.Y;
                foreach (TabItem tab in tabs)
                {
                    Point origin = tab.TranslatePoint(default, options)!.Value;
                    if (Math.Abs(origin.Y - row) > 0.5)
                        failures.Add($"{language.Id}: '{tab.Header}' wrapped to a second row");
                    if (origin.X + tab.Bounds.Width > options.ClientSize.Width + 0.1)
                        failures.Add($"{language.Id}: '{tab.Header}' ends at {origin.X + tab.Bounds.Width:0.#} in a window {options.ClientSize.Width} wide");
                }
                try { MainWindowLabelTests.CheckWhole(tabs.SelectMany(t => t.GetVisualDescendants().OfType<TextBlock>()), language.Id!); }
                catch (Xunit.Sdk.XunitException ex) { failures.Add(ex.Message); }
            }
        }
        finally
        {
            for (int i = 0; i < tabs.Length; i++) tabs[i].Header = shown[i];
            options.UpdateLayout();
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// The tabs are drawn from the tokens: the label in the heading colour,
    /// the chosen one in the readout colour with its bar, and the label's
    /// size from Ox.Tab.FontSize.
    /// </summary>
    internal static void CheckAppearance(OptionsWindow options, string where)
    {
        Show(options, OptionsTab.Audio);
        TabItem[] tabs = Tabs(options);
        object Resource(string key) => options.FindResource(key)!;
        foreach (TabItem tab in tabs)
        {
            Border pipe = tab.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_SelectedPipe");
            Assert.Equal(tab.IsSelected, pipe.IsVisible);
            Assert.Equal((double)Resource("Ox.Tab.FontSize"), tab.FontSize);
            if (tab.IsPointerOver) continue;
            Color expected = Colour(Resource(tab.IsSelected ? "Ox.Text.Primary" : "Ox.Text.Secondary"));
            Assert.True(expected == Colour(tab.Foreground!),
                $"{where}: the {tab.Header} tab is {Colour(tab.Foreground!)}, not {expected}");
            if (tab.IsSelected)
                Assert.Equal(Colour(Resource("TabItemHeaderSelectedPipeFill")), Colour(pipe.Background!));
        }
    }

    private static Color Colour(object brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;
}
