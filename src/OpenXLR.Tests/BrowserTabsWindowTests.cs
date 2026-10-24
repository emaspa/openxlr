using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;
using OpenXLR.UI.Views;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

/// <summary>
/// Browser tabs open in their own window from the button beside Manage. Tab
/// names, origin, capture state and controls fit that window and a narrow
/// column; APPLICATIONS and Manage apps no longer carry the list.
/// </summary>
internal static class BrowserTabsWindowTests
{
    internal static void Check()
    {
        FieldInfo culture = typeof(Localizer).GetField("_culture", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? previous = culture.GetValue(null);
        try
        {
            foreach (string language in new[] { "en", "de", Localizer.Language }.Distinct(StringComparer.Ordinal))
            foreach (bool touch in new[] { false, true })
            {
                culture.SetValue(null, CultureInfo.GetCultureInfo(language));
                SkinService.ApplyControlSizing(touch);
                CheckAt(language, touch);
            }
        }
        finally
        {
            culture.SetValue(null, previous);
            SkinService.ApplyControlSizing(false);
        }
    }

    private static void CheckAt(string language, bool touch)
    {
        var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var main = new MainWindow(client);
        client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
        var model = (MainViewModel)main.DataContext!;
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.DaemonConnected))!.SetValue(model, true);
        foreach (string name in new[] { "InputsTile", "HeadphonesTile", "MonitorTile", "SubmixerTile" })
            main.FindControl<Expander>(name)!.IsExpanded = false;
        var button = main.FindControl<Button>("ManageBrowserTabs")!;
        var manage = main.FindControl<Button>("ManageApps")!;
        var apps = new AppsWindow { DataContext = model };
        var column = new Window { Content = new BrowserTabsView { DataContext = model } };
        string suffix = $"{language}-{(touch ? "touch" : "standard")}";
        try
        {
            main.Show();
            Layout(main, 640, 1000);
            // A daemon without browser routing: no button, nothing to open.
            Assert.False(button.IsVisible);

            BrowserTabViewModelTests.Report(model, true, new JsonArray(
                BrowserTabViewModelTests.Tab(title: "A long browser tab title with a video, a playlist and more details that must remain within the row", channel: "music", remember: true, capturing: true),
                BrowserTabViewModelTests.Tab(id: 8, title: "", channel: null),
                BrowserTabViewModelTests.Tab(id: 9, title: "A call waiting for its audio", channel: "chat")));
            Layout(main, 640, 1000);

            // Beside Manage, worn the same way, and nothing of the tab list left inline.
            Assert.True(button.IsEffectivelyVisible);
            Assert.True(button.IsEffectivelyEnabled);
            Assert.Same(manage.Parent, button.Parent);
            Assert.Equal(manage.Padding, button.Padding);
            Assert.Equal(manage.FontSize, button.FontSize);
            Assert.Equal(manage.MinHeight, button.MinHeight);
            Assert.Equal(manage.VerticalAlignment, button.VerticalAlignment);
            Assert.True(button.TranslatePoint(default, main)!.Value.X >= manage.TranslatePoint(default, main)!.Value.X + manage.Bounds.Width);
            AssertNoOverlap([manage, button]);
            HeaderFitsEveryLanguage(main, button, manage);
            AssertNoTabList(main);
            Capture(main, $"browser-tabs-main-{suffix}-640");

            apps.Show();
            Layout(apps, 620, 700);
            AssertNoTabList(apps);
            apps.Close();

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            BrowserTabsWindow window = Assert.Single(main.OwnedWindows.OfType<BrowserTabsWindow>());
            Assert.Same(model, window.DataContext);
            // Asking again brings the same window forward.
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Same(window, Assert.Single(main.OwnedWindows.OfType<BrowserTabsWindow>()));
            Assert.Same(window, main.OpenBrowserTabs());

            Layout(window, 620, 700);
            BrowserTabsView view = window.GetVisualDescendants().OfType<BrowserTabsView>().Single();
            CheckRows(window, view);
            var option = view.FindControl<CheckBox>("PreserveBrowserOutputs")!;
            Assert.True(option.IsEffectivelyVisible);
            Assert.True(option.IsChecked);
            AssertInside(option, window);
            // The option sits above the rows, not among them.
            Border first = view.GetVisualDescendants().OfType<Border>().First(b => b.Name == "BrowserTabRow");
            Assert.True(option.TranslatePoint(default, window)!.Value.Y + option.Bounds.Height
                <= first.TranslatePoint(default, window)!.Value.Y);
            Assert.False(view.FindControl<TextBlock>("BrowserTabsEmpty")!.IsVisible);
            Capture(window, $"browser-tabs-window-{suffix}");
            window.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(main.OwnedWindows.OfType<BrowserTabsWindow>());

            // With routing off and no tab reported, the window still opens: it
            // is where routing is turned on.
            BrowserTabViewModelTests.Report(model, false, new JsonArray());
            Layout(main, 640, 1000);
            Assert.True(button.IsEffectivelyVisible && button.IsEffectivelyEnabled);
            BrowserTabsWindow empty = main.OpenBrowserTabs();
            Dispatcher.UIThread.RunJobs();
            Assert.NotSame(window, empty);
            Layout(empty, 620, 700);
            BrowserTabsView emptyView = empty.GetVisualDescendants().OfType<BrowserTabsView>().Single();
            var emptyOption = emptyView.FindControl<CheckBox>("PreserveBrowserOutputs")!;
            Assert.True(emptyOption.IsEffectivelyVisible);
            Assert.False(emptyOption.IsChecked);
            Assert.True(emptyOption.IsEnabled);
            TextBlock emptyLine = emptyView.FindControl<TextBlock>("BrowserTabsEmpty")!;
            Assert.True(emptyLine.IsEffectivelyVisible);
            Assert.Equal(Localizer.Text("BrowserTabsEmpty"), emptyLine.Text);
            AssertInside(emptyLine, empty);
            Assert.DoesNotContain(emptyView.GetVisualDescendants().OfType<Border>(), b => b.Name == "BrowserTabRow");
            var guide = empty.FindControl<Button>("BrowserTabsManual")!;
            Assert.True(guide.IsEffectivelyVisible);
            AssertInside(guide, empty);
            Capture(empty, $"browser-tabs-empty-{suffix}");
            empty.Close();
            Dispatcher.UIThread.RunJobs();

            // A daemon that stops reporting browser routing hides the button again.
            typeof(MainViewModel).GetMethod("ResetBrowserTabs", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(button.IsVisible);
            BrowserTabViewModelTests.Report(model, true, new JsonArray(
                BrowserTabViewModelTests.Tab(title: "A long browser tab title with a video, a playlist and more details that must remain within the row", channel: "music", remember: true, capturing: true),
                BrowserTabViewModelTests.Tab(id: 8, title: "", channel: null),
                BrowserTabViewModelTests.Tab(id: 9, title: "A call waiting for its audio", channel: "chat")));

            // A skin can give the window's content a narrow column. Controls wrap below the tab name.
            column.Show();
            Layout(column, 260, 1000);
            CheckRows(column, (BrowserTabsView)column.Content!);
            Capture(column, $"browser-tabs-column-{suffix}-260");
        }
        finally
        {
            foreach (Window owned in main.OwnedWindows.ToArray()) owned.Close();
            apps.Close(); column.Close(); main.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static void AssertNoTabList(Window window)
    {
        Assert.Empty(window.GetVisualDescendants().OfType<BrowserTabsView>());
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Control>(), c => c.Name is
            "BrowserTabRows" or "BrowserTabRow" or "PreserveBrowserOutputs");
    }

    /// <summary>
    /// The APPLICATIONS header holds its heading, Manage and Browser tabs on
    /// one line beside the chevron, in every shipped language.
    /// </summary>
    private static void HeaderFitsEveryLanguage(MainWindow main, Button button, Button manage)
    {
        var header = (StackPanel)button.Parent!;
        TextBlock heading = header.Children.OfType<TextBlock>().Single();
        var room = (Control)header.GetVisualParent()!;
        (string? text, object? manageCaption, object? tabsCaption) = (heading.Text, manage.Content, button.Content);
        var failures = new List<string>();
        try
        {
            foreach (LanguageChoice choice in Localizer.Languages)
            {
                CultureInfo culture = CultureInfo.GetCultureInfo(choice.Id!);
                heading.Text = Localizer.Get("APPLICATIONS", culture);
                manage.Content = Localizer.Get("Manage", culture);
                button.Content = Localizer.Get("BrowserTabs", culture);
                main.UpdateLayout();
                double end = button.TranslatePoint(default, room)!.Value.X + button.Bounds.Width;
                if (end > room.Bounds.Width + 0.5)
                    failures.Add($"{choice.Id}: '{button.Content}' ends at {end:F1} of {room.Bounds.Width:F1}");
            }
        }
        finally
        {
            (heading.Text, manage.Content, button.Content) = (text, manageCaption, tabsCaption);
            main.UpdateLayout();
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static void CheckRows(Window window, BrowserTabsView view)
    {
        Border[] rows = [.. view.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "BrowserTabRow")];
        Assert.Equal(3, rows.Length);
        AssertNoOverlap(rows);
        foreach (Border row in rows)
        {
            AssertInside(row, window);
            var tab = (BrowserTabViewModel)row.DataContext!;
            // Activate shows only on the row it can start: a channel, not routed yet.
            Button activate = row.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "BrowserTabActivate");
            Assert.Equal(tab.Id == 9, activate.IsEffectivelyVisible);
            Assert.Equal(tab.CanActivate, activate.IsEffectivelyVisible);
            Control[] controls = [.. row.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c.Name is
                "BrowserTabTitle" or "BrowserTabSite" or "BrowserTabChannel" or "BrowserTabRemember" or "BrowserTabForgetWebsite"
                    or "BrowserTabActivate" or "BrowserTabStatus")];
            Assert.Equal(tab.CanActivate ? 7 : 6, controls.Length);
            AssertNoOverlap(controls);
            if (tab.CanActivate)
            {
                // Worn like the button beside it.
                var forget = (Button)controls.Single(c => c.Name == "BrowserTabForgetWebsite");
                Assert.Equal(forget.Padding, activate.Padding);
                Assert.Equal(forget.FontSize, activate.FontSize);
                Assert.Equal(forget.Background, activate.Background);
                Assert.Equal(Localizer.Text("BrowserTabActivationRequired"), tab.StatusText);
            }
            foreach (Control control in controls) AssertInside(control, row);
            ComboBox picker = controls.OfType<ComboBox>().Single();
            Assert.True(picker.Bounds.Width >= 180);
            Assert.True(picker.IsEnabled);
            var title = (TextBlock)controls.Single(c => c.Name == "BrowserTabTitle");
            Assert.Equal(((BrowserTabViewModel)row.DataContext!).Title, ToolTip.GetTip(title));
        }
    }
}
