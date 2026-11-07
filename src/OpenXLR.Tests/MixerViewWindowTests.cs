using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

/// <summary>
/// The compact and mini views in a real window. Run from the window layout
/// test, on its UI thread, with preferences of its own.
/// </summary>
internal static class MixerViewWindowTests
{
    internal static void Check()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-view-window-").FullName;
        string? config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        SkinEntry skin = SkinService.Current;
        bool touch = SkinService.TouchControls;
        MainWindow? window = null;
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
            window = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
            var model = (MainViewModel)window.DataContext!;
            typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(model, [JsonNode.Parse("""
                {"mixes":[{"id":"monitor","name":"Monitor A","kind":"monitor"},{"id":"monitor2","name":"Monitor B","kind":"monitor"},
                          {"id":"chat","name":"Chat","kind":"virtualMic"}],
                 "channels":[{"id":"music","name":"Music with a long display name","levels":{"monitor":0.5,"monitor2":0.7,"chat":0.4}},
                             {"id":"game","name":"Game","levels":{"monitor":1,"monitor2":1,"chat":1},"appearance":{"hidden":true}}]}
                """)]);
            string outputName = string.Concat(Enumerable.Repeat("Long headphone and speaker name ", 8));
            SliderSync.ReleaseTouchGuards();
            typeof(MainViewModel).GetMethod("ApplyDevices", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(model,
                [new JsonArray(new JsonObject { ["name"] = "headphones", ["description"] = outputName, ["kind"] = "Sink" }),
                 JsonNode.Parse("""{"monitorOutputs":["headphones"],"outputVolume":0.42}""")]);
            window.Show();
            Layout(window, 1040, 900);
            Assert.False(Picker(window, "CompactChannelPicker").IsEffectivelyVisible);
            Assert.Equal(3, Tiles<MixViewModel>(window, "ClassicMasterTile").Length);

            Assert.Null(model.ChooseMixerView(MainViewModel.CompactView));
            Layout(window, 640, 900);
            Assert.True(Picker(window, "CompactChannelPicker").IsEffectivelyVisible);
            Assert.False(Picker(window, "CompactMixPicker").IsEffectivelyVisible);
            Assert.Equal(["music"], Tiles<ChannelViewModel>(window, "ClassicChannelTile").Select(t => t.Id));
            Assert.Equal(3, Sends(window).Length);
            model.SelectedCompactChannel = model.Channels.Single(c => c.Id == "game");
            Layout(window, 640, 900);
            Assert.Equal(["game"], Tiles<ChannelViewModel>(window, "ClassicChannelTile").Select(t => t.Id));
            Capture(window, "compact-640");

            Assert.Null(model.ChooseMixerView(MainViewModel.MiniView));
            model.SelectedCompactMix = model.Mixes.Single(m => m.Id == "monitor2");
            Layout(window, 460, 640);
            Assert.False(window.FindControl<Expander>("InputsTile")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<Expander>("HeadphonesTile")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<Expander>("ApplicationsTile")!.IsEffectivelyVisible);
            Assert.True(window.FindControl<Expander>("MonitorTile")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<ToggleButton>("ArrangeButton")!.IsEffectivelyVisible);
            Assert.True(Picker(window, "CompactMixPicker").IsEffectivelyVisible);
            Assert.Equal(["monitor2"], Tiles<MixViewModel>(window, "ClassicMasterTile").Select(t => t.Id));
            Assert.Equal(["monitor2"], Sends(window).Select(s => s.MixId));
            foreach (Control control in window.GetVisualDescendants().OfType<Control>()
                         .Where(c => c.IsEffectivelyVisible && c is ComboBox or Slider))
                AssertInside(control, window);
            CheckMonitorControls(window, stacked: true);
            Capture(window, "mini-460");
            foreach (string id in new[] { SkinPackage.DefaultId, "opendeck" })
            {
                SkinService.Apply(SkinCatalog.Find(id)!);
                foreach (bool enlarged in new[] { false, true })
                {
                    SkinService.ApplyControlSizing(enlarged);
                    foreach (double width in new[] { 360d, 460, 640, 1040, 460 })
                    {
                        Layout(window, width, 640);
                        CheckMonitorControls(window, stacked: width < 600);
                    }
                    Capture(window, $"mini-monitor-{id}-{enlarged}");
                }
            }
            SkinService.ApplyControlSizing(touch);
            SkinService.Apply(skin);

            Assert.Null(model.ChooseMixerView(MainViewModel.FullView));
            Layout(window, 1040, 900);
            Assert.True(window.FindControl<Expander>("InputsTile")!.IsEffectivelyVisible);
            Assert.Equal(["music"], Tiles<ChannelViewModel>(window, "ClassicChannelTile").Select(t => t.Id));   // game stays hidden
            Assert.Equal(3, Tiles<MixViewModel>(window, "ClassicMasterTile").Length);
            CheckMonitorControls(window, stacked: false);

            // Monitor B hidden: its master tile and every send row into it leave the classic mixer.
            ToggleButton showHidden = window.FindControl<ToggleButton>("ShowHiddenButton")!;
            Assert.True(showHidden.IsEffectivelyVisible);   // Game is hidden already
            typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(model, [JsonNode.Parse("""
                {"mixes":[{"id":"monitor","name":"Monitor A","kind":"monitor"},{"id":"monitor2","name":"Monitor B","kind":"monitor","appearance":{"hidden":true}},
                          {"id":"chat","name":"Chat","kind":"virtualMic"}],
                 "channels":[{"id":"music","name":"Music with a long display name","levels":{"monitor":0.5,"monitor2":0.7,"chat":0.4}},
                             {"id":"game","name":"Game","levels":{"monitor":1,"monitor2":1,"chat":1},"appearance":{"hidden":true}}]}
                """)]);
            Layout(window, 1040, 900);
            Assert.Equal(["monitor", "chat"], Tiles<MixViewModel>(window, "ClassicMasterTile").Select(m => m.Id));
            Assert.Equal(["monitor", "chat"], Sends(window).Select(s => s.MixId));
            Assert.Equal(0.7, model.Channels.Single(c => c.Id == "music").Sends.Single(s => s.MixId == "monitor2").Level);
            Assert.True(showHidden.IsEffectivelyVisible);
            AssertInside(showHidden, window);
            Assert.Equal(Localizer.Text("ShowHiddenHint"), ToolTip.GetTip(showHidden));
            Capture(window, "monitor-b-hidden-1040");

            // Show hidden brings both back in their places, faded once, and is kept as a window preference.
            showHidden.IsChecked = true;
            Layout(window, 1040, 900);
            Assert.True(model.ShowHidden);
            Assert.True(UiSettings.Load().ShowHiddenItems);
            double faded = (double)window.FindResource("Ox.Mixer.HiddenOpacity")!;
            Assert.True(faded < 1);
            Assert.Equal(["monitor", "monitor2", "chat"], Tiles<MixViewModel>(window, "ClassicMasterTile").Select(m => m.Id));
            Assert.Equal(["music", "game"], Tiles<ChannelViewModel>(window, "ClassicChannelTile").Select(c => c.Id));
            Border Tile(object item, string name) => window.GetVisualDescendants().OfType<Border>()
                .Single(b => ReferenceEquals(b.DataContext, item) && b.Name == name);
            MixViewModel monitor2 = model.Mixes.Single(m => m.Id == "monitor2");
            ChannelViewModel music = model.Channels.Single(c => c.Id == "music"), game = model.Channels.Single(c => c.Id == "game");
            Assert.Equal(faded, Tile(monitor2, "ClassicMasterTile").Opacity, 3);
            Assert.Equal(1, Tile(model.Mixes[0], "ClassicMasterTile").Opacity, 3);
            Assert.Equal(faded, Tile(game, "ClassicChannelTile").Opacity, 3);
            Assert.Equal(1, Tile(music, "ClassicChannelTile").Opacity, 3);
            StackPanel Row(SendViewModel send) => window.GetVisualDescendants().OfType<StackPanel>()
                .Single(p => ReferenceEquals(p.DataContext, send) && p.Margin.Bottom == 8);
            Assert.Equal(faded, Row(music.Sends.Single(s => s.MixId == "monitor2")).Opacity, 3);
            Assert.Equal(1, Row(music.Sends.Single(s => s.MixId == "monitor")).Opacity, 3);
            // Inside Game's faded strip the row into Monitor B does not fade a second time.
            Assert.Equal(1, Row(game.Sends.Single(s => s.MixId == "monitor2")).Opacity, 3);
            Capture(window, "monitor-b-revealed-1040");

            // The popped-out mixer draws the same.
            MixerWindow popped = window.PopOutMixer();
            Layout(popped, 1040, 700);
            Assert.Equal(["monitor", "monitor2", "chat"], Tiles<MixViewModel>(popped, "ClassicMasterTile").Select(m => m.Id));
            model.ShowHidden = false;
            Layout(popped, 1040, 700);
            Assert.Equal(["monitor", "chat"], Tiles<MixViewModel>(popped, "ClassicMasterTile").Select(m => m.Id));
            popped.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Layout(window, 1040, 900);
            Assert.False(showHidden.IsChecked);
            Assert.False(UiSettings.Load().ShowHiddenItems);
        }
        finally
        {
            window?.Close();
            SliderSync.ReleaseTouchGuards();
            SkinService.ApplyControlSizing(touch);
            SkinService.Apply(skin);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
            Directory.Delete(dir, true);
        }
    }

    private static ComboBox Picker(Window window, string name) => window.FindControl<ComboBox>(name)!;

    private static void CheckMonitorControls(Window window, bool stacked)
    {
        var monitor = window.FindControl<Expander>("MonitorTile")!;
        var picker = Assert.Single(monitor.GetVisualDescendants().OfType<DropDownButton>());
        var slider = window.FindControl<Slider>("OutputVolumeSlider")!;
        Assert.True(slider.Bounds.Width >= 140, $"The monitor slider has only {slider.Bounds.Width:F1}px.");
        double minimum = stacked ? 180 : 140;
        Assert.True(picker.Bounds.Width >= minimum, $"The monitor picker has only {picker.Bounds.Width:F1}px.");
        Assert.Equal(stacked, slider.TranslatePoint(default, monitor)!.Value.Y
            >= picker.TranslatePoint(default, monitor)!.Value.Y + picker.Bounds.Height);
        AssertInside(picker, window);
        AssertInside(slider, window);
        var model = (MainViewModel)window.DataContext!;
        Assert.Equal(model.MonitorSummary, ToolTip.GetTip(picker));
        Assert.Contains(picker.GetVisualDescendants().OfType<TextBlock>(),
            t => t.Text == model.MonitorSummary && t.TextLayout.TextLines.Any(l => l.HasCollapsed));
        Assert.Equal(0.42, slider.Value);
        Assert.Equal(0.42, model.OutputVolume);
    }

    private static T[] Tiles<T>(Window window, string name) where T : class => [.. window.GetVisualDescendants().OfType<Border>()
        .Where(b => b.DataContext is T && b.Name == name && b.IsEffectivelyVisible).Select(b => (T)b.DataContext!)];

    private static SendViewModel[] Sends(Window window) => [.. window.GetVisualDescendants().OfType<Slider>()
        .Where(s => s.DataContext is SendViewModel && s.IsEffectivelyVisible).Select(s => (SendViewModel)s.DataContext!)];
}
