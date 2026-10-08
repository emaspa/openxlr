using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using OpenXLR.UI;
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
            window.Show();
            Layout(window, 1040, 900);
            Assert.False(Picker(window, "CompactChannelPicker").IsEffectivelyVisible);
            Assert.Equal(3, Tiles<MixViewModel>(window, 232).Length);

            Assert.Null(model.ChooseMixerView(MainViewModel.CompactView));
            Layout(window, 640, 900);
            Assert.True(Picker(window, "CompactChannelPicker").IsEffectivelyVisible);
            Assert.False(Picker(window, "CompactMixPicker").IsEffectivelyVisible);
            Assert.Equal(["music"], Tiles<ChannelViewModel>(window, 132).Select(t => t.Id));
            Assert.Equal(3, Sends(window).Length);
            model.SelectedCompactChannel = model.Channels.Single(c => c.Id == "game");
            Layout(window, 640, 900);
            Assert.Equal(["game"], Tiles<ChannelViewModel>(window, 132).Select(t => t.Id));
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
            Assert.Equal(["monitor2"], Tiles<MixViewModel>(window, 232).Select(t => t.Id));
            Assert.Equal(["monitor2"], Sends(window).Select(s => s.MixId));
            foreach (Control control in window.GetVisualDescendants().OfType<Control>()
                         .Where(c => c.IsEffectivelyVisible && c is ComboBox or Slider))
                AssertInside(control, window);
            Capture(window, "mini-460");

            Assert.Null(model.ChooseMixerView(MainViewModel.FullView));
            Layout(window, 1040, 900);
            Assert.True(window.FindControl<Expander>("InputsTile")!.IsEffectivelyVisible);
            Assert.Equal(["music"], Tiles<ChannelViewModel>(window, 132).Select(t => t.Id));   // game stays hidden
            Assert.Equal(3, Tiles<MixViewModel>(window, 232).Length);
        }
        finally
        {
            window?.Close();
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
            Directory.Delete(dir, true);
        }
    }

    private static ComboBox Picker(Window window, string name) => window.FindControl<ComboBox>(name)!;

    private static T[] Tiles<T>(Window window, double width) where T : class => [.. window.GetVisualDescendants().OfType<Border>()
        .Where(b => b.DataContext is T && b.Width == width && b.IsEffectivelyVisible).Select(b => (T)b.DataContext!)];

    private static SendViewModel[] Sends(Window window) => [.. window.GetVisualDescendants().OfType<Slider>()
        .Where(s => s.DataContext is SendViewModel && s.IsEffectivelyVisible).Select(s => (SendViewModel)s.DataContext!)];
}
