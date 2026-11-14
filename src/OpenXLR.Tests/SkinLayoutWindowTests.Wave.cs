using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

/// <summary>
/// The shipped Wave skin on the window the Broadcast check has set up:
/// Broadcast's arrangement in Wave's palette and sizes. HEADPHONES and
/// MONITOR's toolbar side by side, the matrix under them across the window,
/// INPUTS hidden until Arrange mode shows it, APPLICATIONS in columns under
/// the matrix, nothing folded and every header action in the row. At 640,
/// 1040 and 2560 pixels, in Standard and Touch sizing, nothing is clipped or
/// overlaps, every channel has a cell in every mix, XLR 1 stacks its gain
/// over its volume and the direct path's mark stays inside its head. A
/// hidden mix leaves the matrix and Show hidden brings it back faded.
/// </summary>
internal static partial class SkinLayoutWindowTests
{
    private static void CheckWave(MainWindow window, MainViewModel model, string mixer)
    {
        Border Card(string tile) => (Border)window.FindControl<Expander>(tile)!.Parent!;
        MethodInfo apply = typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Wear("wave");
        Layout(window, 1040, 1400);
        Assert.Equal("wave", SkinService.Current.Id);
        Assert.True(window.FindControl<Expander>("HeadphonesTile")!.IsExpanded);
        Assert.True(window.FindControl<Expander>("ApplicationsTile")!.IsExpanded);
        Assert.False(UiSettings.Load().HasSeenSkinLayout("wave", 1));

        // The header above the tree; HEADPHONES, MONITOR and the matrix in the grid, the rest under it.
        var header = window.FindControl<Border>("HeaderCard")!;
        Assert.Same(window.FindControl<StackPanel>("MixerContent")!, header.Parent);
        StackPanel desk = Assert.IsType<StackPanel>(window.LayoutHost.PanelFor("desk"));
        Grid grid = Assert.IsType<Grid>(window.LayoutHost.PanelFor("mixes"));
        Assert.Same(desk, window.FindControl<ScrollViewer>("SectionScroll")!.Content);
        Assert.Same(desk, grid.Parent);
        foreach (string tile in new[] { "HeadphonesTile", "MonitorTile", "SubmixerTile" }) Assert.Same(grid, Card(tile).Parent);
        Assert.Same(desk, Card("ApplicationsTile").Parent);
        Assert.Equal(8, desk.Spacing);
        Assert.Equal(8, grid.ColumnSpacing);
        Assert.Equal(["ProfilesButton", "HistoryButtons", "FlowButton", "ArrangeButton", "RestartDaemonButton", "HeaderVersion", "AboutButton", "HeaderOverflow"],
            window.FindControl<WrapPanel>("HeaderActions")!.Children.Select(c => c.Name));
        Assert.Empty(window.FindControl<StackPanel>("HeaderOverflowItems")!.Children);

        // The matrix and the toolbar, no banks and no rack.
        Assert.False(Card("InputsTile").IsVisible);
        Assert.Null(window.MixBanks);
        Assert.Null(window.InputRackModel);
        MixMatrixViewModel matrix = Assert.IsType<MixMatrixViewModel>(window.MixMatrix);
        Assert.Equal(SkinLayoutTemplates.OutputToolbar, window.MonitorTemplate);
        Assert.Equal(BroadcastMixes, matrix.Columns.Select(c => c.Mix.Id));
        Assert.Equal(["xlr1", "aux", "music", "board", "game", "cam"], matrix.Rows.Select(r => r.Channel.Id));
        Assert.True(matrix.OwnsHandles);
        Assert.True(matrix.Rows.Single(r => r.Channel.Id == "xlr1").Stacked);
        foreach (MatrixRowViewModel row in matrix.Rows)
            Assert.True(RowHead(window, row.Channel.Id).GetVisualDescendants().OfType<LayoutIconBlock>().Single().IsEffectivelyVisible, row.Channel.Id);

        // Every size with the direct path on, so XLR 1's mark is drawn too.
        model.Channels.Single(c => c.Id == "xlr1").DirectMonitored = true;
        foreach (bool enlarged in new[] { false, true })
        {
            SkinService.ApplyControlSizing(enlarged);
            foreach (double width in new[] { 640d, 1040, 2560 })
            {
                Layout(window, width, 1400);
                string where = $"wave, {(enlarged ? "touch" : "standard")} at {width}";
                CheckBroadcastAt(window, model, where);
                Border head = RowHead(window, "xlr1");
                TextBlock mark = head.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "MatrixVolumeNote");
                Assert.True(mark.IsEffectivelyVisible, where);
                AssertInside(mark, head);
                Capture(window, $"skin-wave-{(enlarged ? "touch" : "standard")}-{width}");
            }
        }
        SkinService.ApplyControlSizing(false);
        model.Channels.Single(c => c.Id == "xlr1").DirectMonitored = false;
        CheckProcessing(window, model, "wave");
        Layout(window, 1040, 1400);

        // A muted send's cell greys out as Broadcast's does, its mute at full strength.
        Border muted = MatrixCell(window, "music", "stream");
        Assert.Contains("dimmed", muted.Classes);
        Assert.DoesNotContain("tinted", muted.Classes);
        Assert.Equal((double)window.FindResource("Ox.Matrix.MutedOpacity")!, muted.GetVisualDescendants().OfType<MeterFader>().Single().Opacity);
        Assert.Equal(1, muted.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Classes.Contains("matrixMute")).Opacity);

        // INPUTS comes back from Arrange mode under the matrix, and Reset hides it again.
        Arrange(window, true);
        Button show = Assert.Single(window.FindControl<WrapPanel>("HiddenSections")!.Children.OfType<Button>());
        Assert.Equal(Localizer.Format("ShowSection", Localizer.Text("INPUTS")), show.Content);
        show.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout(window, 1040, 1400);
        Assert.True(Card("InputsTile").IsVisible);
        Assert.True(Card("InputsTile").TranslatePoint(default, desk)!.Value.Y
            >= Card("SubmixerTile").TranslatePoint(default, desk)!.Value.Y + Card("SubmixerTile").Bounds.Height - 0.5);
        window.FindControl<Button>("ResetSectionsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout(window, 1040, 1400);
        Assert.False(Card("InputsTile").IsVisible);
        Arrange(window, false);

        // Monitor B hidden leaves the matrix; Show hidden puts it back in its place, faded.
        JsonNode hidden = JsonNode.Parse(mixer)!;
        hidden["mixes"]![1]!["appearance"]!["hidden"] = true;
        apply.Invoke(model, [hidden]);
        Layout(window, 1040, 1400);
        Assert.Equal(BroadcastMixes.Where(id => id != "monitor2"), matrix.Columns.Select(c => c.Mix.Id));
        CheckBroadcastAt(window, model, "wave, Monitor B hidden");
        var showHidden = window.FindControl<ToggleButton>("ShowHiddenButton")!;
        Assert.True(showHidden.IsEffectivelyVisible);
        showHidden.IsChecked = true;
        Layout(window, 1040, 1400);
        Assert.Equal(BroadcastMixes, matrix.Columns.Select(c => c.Mix.Id));
        CheckMatrixCells(window, matrix, "wave, Monitor B shown");
        Assert.Equal((double)window.FindResource("Ox.Mixer.HiddenOpacity")!, ColumnHead(window, "monitor2").Opacity, 3);
        Capture(window, "skin-wave-monitor-b-shown");
        showHidden.IsChecked = false;
        apply.Invoke(model, [JsonNode.Parse(mixer)]);
        Layout(window, 1040, 1400);
        Assert.Equal(BroadcastMixes, matrix.Columns.Select(c => c.Mix.Id));
    }
}
