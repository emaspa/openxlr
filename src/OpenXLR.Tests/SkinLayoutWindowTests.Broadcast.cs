using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;
using OpenXLR.UI.Views;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

/// <summary>
/// The shipped Broadcast skin on a real window, the way a user meets it: the
/// header across the top, HEADPHONES and MONITOR's toolbar side by side under
/// it, the submixer as a mix matrix across the window, a row per channel and
/// a column per mix, INPUTS hidden until Arrange mode shows it, APPLICATIONS
/// under the matrix in columns, nothing folded and every header action in the row. At 640, 1040 and 2560
/// pixels, in Standard and Touch sizing, with six mixes, an interface
/// without XLR 2, a capture channel that is not connected and a 40 character
/// name, nothing is clipped or overlaps and every channel has a cell in
/// every mix. Then the compact and mini views, the matrix in its own window,
/// and the way back to Material with every object kept.
/// </summary>
internal static partial class SkinLayoutWindowTests
{
    private static readonly string[] BroadcastMixes = ["monitor", "monitor2", "stream", "chat", "auxout", "music-bed"];

    private const string LongName = "Stream soundboard with stingers and beds";

    private static void CheckBroadcast()
    {
        var window = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
        bool touch = SkinService.TouchControls;
        try
        {
            var model = (MainViewModel)window.DataContext!;
            string mixer = FillBroadcastModel(model);
            MethodInfo apply = typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!;
            ChannelViewModel[] channels = [.. model.Channels];
            MixViewModel[] mixes = [.. model.Mixes];
            SendViewModel[] sends = [.. model.Channels.SelectMany(c => c.Sends)];
            window.Show();
            Layout(window, 1040, 900);

            Expander headphones = window.FindControl<Expander>("HeadphonesTile")!;
            Expander applications = window.FindControl<Expander>("ApplicationsTile")!;
            Assert.True(headphones.IsExpanded);
            Expander[] expanders = [.. Usual.Select(name => window.FindControl<Expander>(name)!)];
            Border[] cards = [.. expanders.Select(e => (Border)e.Parent!)];
            object?[] contents = [.. expanders.Select(e => e.Content)];
            Border Card(string tile) => (Border)window.FindControl<Expander>(tile)!.Parent!;

            Wear("broadcast");
            Layout(window, 1040, 900);
            Assert.Equal("broadcast", SkinService.Current.Id);
            // Nothing is folded, the first time or later, so there is no first fold to record.
            Assert.True(headphones.IsExpanded);
            Assert.True(applications.IsExpanded);
            Assert.False(UiSettings.Load().HasSeenSkinLayout("broadcast", 1));

            // The header stays above the tree, across the window.
            var header = window.FindControl<Border>("HeaderCard")!;
            var content = window.FindControl<StackPanel>("MixerContent")!;
            Assert.Same(content, header.Parent);
            StackPanel desk = Assert.IsType<StackPanel>(window.LayoutHost.PanelFor("desk"));
            Grid grid = Assert.IsType<Grid>(window.LayoutHost.PanelFor("mixes"));
            var scroll = window.FindControl<ScrollViewer>("SectionScroll")!;
            Assert.Same(desk, scroll.Content);
            Assert.Same(desk, grid.Parent);
            Assert.Same(grid, Card("MonitorTile").Parent);
            Assert.Same(grid, Card("SubmixerTile").Parent);
            Assert.Same(grid, Card("HeadphonesTile").Parent);
            Assert.Same(desk, Card("ApplicationsTile").Parent);
            // Every action in the row, in the skin's order, and no overflow menu.
            Assert.Equal(["ProfilesButton", "FlowButton", "ArrangeButton", "RestartDaemonButton", "HeaderVersion", "AboutButton", "HeaderOverflow"],
                window.FindControl<WrapPanel>("HeaderActions")!.Children.Select(c => c.Name));
            Assert.Empty(window.FindControl<StackPanel>("HeaderOverflowItems")!.Children);
            Assert.False(window.FindControl<DropDownButton>("HeaderOverflow")!.IsVisible);

            // The matrix, not banks and no rack: INPUTS is hidden, the row heads carry its controls.
            Assert.False(Card("InputsTile").IsVisible);
            Assert.Null(window.MixBanks);
            Assert.Null(window.InputRackModel);
            MixMatrixViewModel matrix = Assert.IsType<MixMatrixViewModel>(window.MixMatrix);
            Assert.Equal(SkinLayoutTemplates.OutputToolbar, window.MonitorTemplate);
            Assert.Equal(BroadcastMixes, matrix.Columns.Select(c => c.Mix.Id));
            Assert.Equal(["xlr1", "aux", "music", "board", "game", "cam"], matrix.Rows.Select(r => r.Channel.Id));
            Assert.True(matrix.OwnsHandles);
            Assert.Equal(Localizer.Text("MatrixOneOutput"), matrix.Columns[0].OutputsText);
            Assert.Equal(Localizer.Text("MatrixNoOutput"), matrix.Columns[1].OutputsText);
            Assert.Equal(Localizer.Text("VirtualMicrophone"), matrix.Columns[2].OutputsText);

            // Every row head and column head wears an icon before its name: the
            // default by kind, or the one chosen, a glyph as text and a name as a
            // drawing. A mix's own colour reaches its icon.
            Layout(window, 1040, 1400);
            foreach (string id in new[] { "xlr1", "aux", "music", "board", "game", "cam" })
            {
                LayoutIconBlock icon = RowHead(window, id).GetVisualDescendants().OfType<LayoutIconBlock>().Single();
                Assert.True(icon.IsEffectivelyVisible, id);
                Assert.Equal(id != "music", icon.DrawsPath);
                Assert.True(icon.Bounds.Width > 8 && icon.Bounds.Height > 8, id);
            }
            Assert.Equal("♫", RowHead(window, "music").GetVisualDescendants().OfType<LayoutIconBlock>().Single().Text);
            Assert.Equal("camera", RowHead(window, "cam").GetVisualDescendants().OfType<LayoutIconBlock>().Single().Shown);
            foreach (MatrixColumnViewModel column in matrix.Columns)
            {
                LayoutIconBlock icon = ColumnHead(window, column.Mix.Id).GetVisualDescendants().OfType<LayoutIconBlock>().Single();
                Assert.True(icon.IsEffectivelyVisible && icon.DrawsPath, column.Mix.Id);
                TextBlock name = ColumnHead(window, column.Mix.Id).GetVisualDescendants().OfType<TextBlock>()
                    .Single(t => t is not LayoutIconBlock && t.Text == column.Mix.Name);
                Assert.True(icon.TranslatePoint(default, name)!.Value.X < 0, column.Mix.Id);
            }
            Assert.Equal("music", ColumnHead(window, "music-bed").GetVisualDescendants().OfType<LayoutIconBlock>().Single().Shown);
            Assert.Equal(Color.Parse("#FF7A00"),
                ((ISolidColorBrush)ColumnHead(window, "stream").GetVisualDescendants().OfType<LayoutIconBlock>().Single().Foreground!).Color);

            foreach (bool enlarged in new[] { false, true })
            {
                SkinService.ApplyControlSizing(enlarged);
                foreach (double width in new[] { 640d, 1040, 2560 })
                {
                    Layout(window, width, 1400);
                    CheckBroadcastAt(window, model, $"{(enlarged ? "touch" : "standard")} at {width}");
                    Capture(window, enlarged ? $"skin-broadcast-touch-{width}" : $"skin-broadcast-{width}");
                }
            }
            int previousTextSize = SkinService.TextSizePercent;
            try
            {
                SkinService.ApplyTextSize(120);
                foreach (bool enlarged in new[] { false, true })
                {
                    SkinService.ApplyControlSizing(enlarged);
                    Layout(window, 640, 1400);
                    CheckBroadcastAt(window, model, $"120% text, {(enlarged ? "touch" : "standard")}");
                }
            }
            finally
            {
                SkinService.ApplyTextSize(previousTextSize);
                SkinService.ApplyControlSizing(false);
            }
            CheckProcessing(window, model, "broadcast");

            // XLR 1 bound to a webcam: the daemon names it Mic 1, its row keeps
            // its name, its binding line and its chain, and loses the interface's
            // gain and mute, which no channel carries here.
            const string Webcam = "alsa_input.usb-046d_Logi_4K_Stream_Edition_C971DF30-03.analog-stereo";
            string bound = mixer.Replace("""{"id":"xlr1","name":"XLR 1","hardware":true,""",
                "{\"id\":\"xlr1\",\"name\":\"Mic 1\",\"hardware\":true,\"binding\":{\"kind\":\"source\",\"source\":\"" + Webcam
                + "\",\"pair\":0,\"monoChannel\":0},\"bindingConnected\":true,", StringComparison.Ordinal)
                .Replace("""{"monitor":0.8,"stream":0.6},"volume":0.7,"appearance":{"defaultIcon":"mic"}""",
                    """{"monitor":0.8,"stream":0.6},"volume":0.7,"appearance":{"defaultIcon":"microphone"}""", StringComparison.Ordinal);
            Assert.NotEqual(mixer, bound);
            typeof(MainViewModel).GetMethod("ApplyDevices", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(model, [JsonNode.Parse("""
                [{"kind":0,"name":"speakers","description":"Desk speakers with a long description from the driver"},
                 {"kind":0,"name":"headset","description":"USB headset"},
                 {"kind":1,"name":"WEBCAM","description":"Logi 4K Stream Edition","channels":2,"positions":["FL","FR"]}]
                """.Replace("WEBCAM", Webcam)), JsonNode.Parse(bound)]);
            apply.Invoke(model, [JsonNode.Parse(bound)]);
            foreach (bool enlarged in new[] { false, true })
            {
                SkinService.ApplyControlSizing(enlarged);
                foreach (double width in new[] { 640d, 1040, 2560 })
                {
                    Layout(window, width, 1400);
                    string where = $"bound {(enlarged ? "touch" : "standard")} at {width}";
                    CheckBroadcastAt(window, model, where);
                    Border head = RowHead(window, "xlr1");
                    Assert.Contains(head.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Mic 1");
                    // Its own volume stays, the head's one fader, with the meter under it.
                    Slider micVolume = Assert.Single(head.GetVisualDescendants().OfType<Slider>(), s => s.IsEffectivelyVisible);
                    Assert.Equal("MatrixVolume", micVolume.Name);
                    Assert.Equal(0.7, micVolume.Value);
                    Assert.Equal(Localizer.Language == "en" ? "Mic 1 volume" : Localizer.Format("ChannelVolumeName", "Mic 1"), AutomationProperties.GetName(micVolume));
                    Assert.Equal(matrix.MeterUnderFader,
                        head.GetVisualDescendants().OfType<MeterFader>().Single(f => f.IsEffectivelyVisible).Meters.IsEffectivelyVisible);
                    Assert.Equal(RowHeightOf(window), head.Bounds.Height, 0.5);
                    Assert.DoesNotContain(head.GetVisualDescendants().OfType<ToggleButton>(), t => t.IsEffectivelyVisible && t.Classes.Contains("matrixMute"));
                    Assert.Single(head.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && b.Classes.Contains("matrixEffects"));
                    Assert.Equal("microphone", head.GetVisualDescendants().OfType<LayoutIconBlock>().Single().Shown);
                    if (!enlarged) Capture(window, $"skin-broadcast-bound-{width}");
                }
            }
            SkinService.ApplyControlSizing(false);
            apply.Invoke(model, [JsonNode.Parse(mixer)]);
            Layout(window, 1040, 1400);

            // A muted send's cell greys out: its fader fades while its mute keeps the
            // muted colour at full strength, and the cell keeps its tile surface.
            double mutedOpacity = (double)window.FindResource("Ox.Matrix.MutedOpacity")!;
            Border muted = MatrixCell(window, "music", "stream");
            Assert.Contains("dimmed", muted.Classes);
            Assert.DoesNotContain("tinted", muted.Classes);
            Assert.Equal(1, muted.Opacity);
            Assert.Equal(((ISolidColorBrush)window.FindResource("Ox.Tile.Background")!).Color, ((ISolidColorBrush)muted.Background!).Color);
            Assert.Equal(mutedOpacity, muted.GetVisualDescendants().OfType<MeterFader>().Single().Opacity);
            ToggleButton mute = muted.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Classes.Contains("matrixMute"));
            Assert.True(mute.IsChecked);
            Assert.Equal(1, mute.Opacity);
            Assert.Equal(((ISolidColorBrush)window.FindResource("Ox.Mute.BackgroundChecked")!).Color,
                ((ISolidColorBrush)mute.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First().Background!).Color);
            Border open = MatrixCell(window, "music", "monitor");
            Assert.DoesNotContain("dimmed", open.Classes);
            Assert.Equal(1, open.GetVisualDescendants().OfType<MeterFader>().Single().Opacity);
            // The send's level is no part beside the fader: a chip in the buttons' face at the
            // top centre of the cell, shown only while the fader is being moved.
            Border chip = open.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "MatrixValueOverFader");
            Slider openSlider = open.GetVisualDescendants().OfType<Slider>().Single();
            Assert.DoesNotContain(open.GetVisualDescendants().OfType<TextBlock>(), b => b.IsEffectivelyVisible && b.Classes.Contains("val"));
            Assert.False(chip.IsEffectivelyVisible);
            FaderInput.SetIsAdjusting(openSlider, true);
            Layout(window, 1040, 1400);
            Assert.True(chip.IsEffectivelyVisible);
            Assert.Equal(model.Channels.Single(c => c.Id == "music").Sends.Single(s => s.MixId == "monitor").LevelText,
                chip.GetVisualDescendants().OfType<TextBlock>().Single().Text);
            Assert.Equal(((ISolidColorBrush)window.FindResource("Ox.Control.Background")!).Color, ((ISolidColorBrush)chip.Background!).Color);
            Assert.Equal(((ISolidColorBrush)window.FindResource("Ox.Control.BorderBrush")!).Color, ((ISolidColorBrush)chip.BorderBrush!).Color);
            // Centred to the pixel: layout rounding can put it half a pixel off.
            Assert.InRange(chip.TranslatePoint(new Point(chip.Bounds.Width / 2, 0), open)!.Value.X - open.Bounds.Width / 2, -1, 1);
            Assert.InRange(chip.TranslatePoint(default, open)!.Value.Y, 0, 1);
            AssertInside(chip, open);
            FaderInput.SetIsAdjusting(openSlider, false);
            Layout(window, 1040, 1400);
            Assert.False(chip.IsEffectivelyVisible);
            mute.IsChecked = false;
            Assert.False(model.Channels.Single(c => c.Id == "music").Sends.Single(s => s.MixId == "stream").Muted);
            Layout(window, 1040, 1400);
            Assert.DoesNotContain("dimmed", muted.Classes);
            Assert.Equal(1, muted.GetVisualDescendants().OfType<MeterFader>().Single().Opacity);
            mute.IsChecked = true;
            Layout(window, 1040, 1400);
            Assert.Contains("dimmed", muted.Classes);

            // A capture channel that is not connected keeps its row: its name,
            // and its own volume with its meter under it, as XLR 1's gain has.
            Border cam = RowHead(window, "cam");
            Assert.True(cam.IsEffectivelyVisible);
            Slider camVolume = Assert.Single(cam.GetVisualDescendants().OfType<Slider>(), s => s.IsEffectivelyVisible);
            Assert.Equal(0.75, camVolume.Value);
            Assert.Equal(Localizer.Language == "en" ? "Capture card volume" : Localizer.Format("ChannelVolumeName", "Capture card"), AutomationProperties.GetName(camVolume));
            // The interface's gain is not drawn: this channel does not carry the interface's microphone.
            Assert.True(cam.GetVisualDescendants().OfType<MeterFader>().Single(f => f.IsEffectivelyVisible).Meters.IsEffectivelyVisible);
            Assert.False(Part(cam, SkinStripParts.Meter).IsEffectivelyVisible);
            camVolume.Value = 0.3;
            Assert.Equal(0.3, model.Channels.Single(c => c.Id == "cam").Volume);
            // A channel the daemon reports no volume for, as an older daemon
            // does, keeps its name and its meter only.
            Border game = RowHead(window, "game");
            Assert.Null(game.GetVisualDescendants().OfType<Slider>().FirstOrDefault(s => s.IsEffectivelyVisible));
            Assert.True(Part(game, SkinStripParts.Meter).IsEffectivelyVisible);
            Assert.Contains(game.GetVisualDescendants().OfType<LevelMeter>(), m => m.IsEffectivelyVisible);
            // XLR 1's head has its gain over its meter, its own volume under the
            // gain, its mute and its chain; the row grows to hold both faders.
            Border xlr1 = RowHead(window, "xlr1");
            Slider[] xlr1Faders = [.. xlr1.GetVisualDescendants().OfType<Slider>().Where(s => s.IsEffectivelyVisible)
                .OrderBy(s => s.TranslatePoint(default, xlr1)!.Value.Y)];
            Assert.Equal(2, xlr1Faders.Length);
            Assert.Equal(model.UnitInput1.GainName, AutomationProperties.GetName(xlr1Faders[0]));
            Assert.Equal(Localizer.Language == "en" ? "XLR 1 volume" : Localizer.Format("ChannelVolumeName", "XLR 1"), AutomationProperties.GetName(xlr1Faders[1]));
            Assert.Equal(0.7, xlr1Faders[1].Value);
            MeterFader[] xlr1Tracks = [.. xlr1.GetVisualDescendants().OfType<MeterFader>().Where(f => f.IsEffectivelyVisible)
                .OrderBy(f => f.TranslatePoint(default, xlr1)!.Value.Y)];
            Assert.True(xlr1Tracks[0].Meters.IsEffectivelyVisible);
            Assert.False(xlr1Tracks[1].Meters.IsEffectivelyVisible);
            Assert.True(matrix.Rows.Single(r => r.Channel.Id == "xlr1").Stacked);
            xlr1Faders[1].Value = 0.5;
            Assert.Equal(0.5, model.Channels.Single(c => c.Id == "xlr1").Volume);
            // Aux In stacks its level over its volume the same way, or keeps the volume alone without the stage.
            Assert.Equal(model.CapAuxInput ? 2 : 1, RowHead(window, "aux").GetVisualDescendants().OfType<Slider>().Count(s => s.IsEffectivelyVisible));
            // While the Wave XLR Pro's direct path carries XLR 1 to the jacks, a
            // mark beside its volume and the volume's tooltip say it reaches the
            // mixes only; the row keeps its height.
            ChannelViewModel xlr1Channel = model.Channels.Single(c => c.Id == "xlr1");
            double xlr1Height = xlr1.Bounds.Height;
            TextBlock note = xlr1.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "MatrixVolumeNote");
            Assert.False(note.IsEffectivelyVisible);
            Assert.Equal(Localizer.Text("ChannelVolumeTip"), ToolTip.GetTip(xlr1Faders[1]));
            StopConnecting(window);
            xlr1Channel.DirectMonitored = true;
            foreach (bool enlarged in new[] { false, true })
            {
                SkinService.ApplyControlSizing(enlarged);
                foreach (double width in new[] { 640d, 1040, 2560 })
                {
                    Layout(window, width, 1400);
                    string where = $"direct path, {(enlarged ? "touch" : "standard")} at {width}";
                    Border head = RowHead(window, "xlr1");
                    TextBlock mark = head.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "MatrixVolumeNote");
                    Assert.True(mark.IsEffectivelyVisible, where);
                    AssertInside(mark, head);
                    Assert.Equal(Localizer.Text("ChannelVolumeDirectTip"), ToolTip.GetTip(mark));
                    Assert.Equal(Localizer.Text("ChannelVolumeDirectTip"),
                        ToolTip.GetTip(head.GetVisualDescendants().OfType<Slider>().Single(f => f.Name == "MatrixVolume")));
                    CheckMatrixCells(window, matrix, where);
                    if (width == 1040) Capture(window, $"skin-broadcast-direct-{(enlarged ? "touch" : "standard")}-1040");
                }
            }
            SkinService.ApplyControlSizing(false);
            Layout(window, 1040, 1400);
            Assert.Equal(xlr1Height, RowHead(window, "xlr1").Bounds.Height, 0.5);
            xlr1Channel.DirectMonitored = false;
            Layout(window, 1040, 1400);
            Assert.False(RowHead(window, "xlr1").GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "MatrixVolumeNote").IsEffectivelyVisible);
            Assert.Single(xlr1.GetVisualDescendants().OfType<ToggleButton>(), t => t.IsEffectivelyVisible && t.Classes.Contains("matrixMute"));
            Assert.Single(xlr1.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && b.Classes.Contains("matrixEffects"));
            // XLR 2 has no row, comes back when the interface has it, and goes again.
            Assert.DoesNotContain(matrix.Rows, r => r.Channel.Id == "xlr2");
            model.HasXlr2 = true;
            apply.Invoke(model, [JsonNode.Parse(mixer.Replace("\"present\":false", "\"present\":true"))]);
            Layout(window, 1040, 1400);
            Assert.Contains(matrix.Rows, r => r.Channel.Id == "xlr2");
            CheckBroadcastAt(window, model, "with XLR 2");
            CheckProcessingXlr2(window, model);
            model.HasXlr2 = false;
            apply.Invoke(model, [JsonNode.Parse(mixer)]);
            Layout(window, 1040, 1400);
            Assert.DoesNotContain(matrix.Rows, r => r.Channel.Id == "xlr2");

            // INPUTS comes back from Arrange mode, under the matrix, and Reset hides it again.
            var hidden = window.FindControl<WrapPanel>("HiddenSections")!;
            Arrange(window, true);
            Button show = Assert.Single(hidden.Children.OfType<Button>());
            Assert.Equal(Localizer.Format("ShowSection", Localizer.Text("INPUTS")), show.Content);
            // In Arrange mode a channel's handle is on its row head, a mix's on its column head, once each.
            Button[] handles = [.. window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("reorderHandle") && b.IsEffectivelyVisible)];
            Assert.Equal(["music", "board", "game", "cam"],
                handles.Select(h => h.Tag).OfType<ChannelViewModel>().Select(c => c.Id));
            Assert.Equal(["stream", "chat", "music-bed"], handles.Select(h => h.Tag).OfType<MixViewModel>().Select(m => m.Id));
            show.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(window, 1040, 1400);
            Assert.True(Card("InputsTile").IsVisible);
            Assert.True(window.FindControl<StackPanel>("HardwarePanel")!.IsEffectivelyVisible);
            Assert.True(Card("InputsTile").TranslatePoint(default, desk)!.Value.Y
                >= Card("SubmixerTile").TranslatePoint(default, desk)!.Value.Y + Card("SubmixerTile").Bounds.Height - 0.5);
            AssertNoOverlap([.. cards.Where(c => c.IsVisible), header]);
            window.FindControl<Button>("ResetSectionsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(window, 1040, 1400);
            Assert.False(Card("InputsTile").IsVisible);
            Arrange(window, false);

            // The compact view: the chosen channel alone, in every mix.
            model.SelectedCompactChannel = model.Channels.Single(c => c.Id == "board");
            Assert.Null(model.ChooseMixerView(MainViewModel.CompactView));
            foreach (double width in new[] { 640d, 1040 })
            {
                Layout(window, width, 1200);
                string where = $"compact at {width}";
                Assert.Equal(["board"], matrix.Rows.Select(r => r.Channel.Id));
                Assert.Equal(BroadcastMixes, matrix.Columns.Select(c => c.Mix.Id));
                CheckMatrixCells(window, matrix, where);
                Border[] shown = [.. cards.Where(c => c.IsVisible)];
                AssertNoOverlap([.. shown, header]);
                foreach (Border card in shown) AssertControlsInside(card, scroll);
                Assert.True(Card("SubmixerTile").IsVisible, where);
            }
            Capture(window, "skin-broadcast-compact-1040");

            // The mini view: one channel in one mix, the other sections set aside.
            model.SelectedCompactMix = model.Mixes.Single(m => m.Id == "stream");
            Assert.Null(model.ChooseMixerView(MainViewModel.MiniView));
            foreach (double width in new[] { 640d, 1040 })
            {
                Layout(window, width, 1000);
                string where = $"mini at {width}";
                Assert.False(Card("InputsTile").IsVisible, where);
                Assert.False(Card("HeadphonesTile").IsVisible, where);
                Assert.False(Card("ApplicationsTile").IsVisible, where);
                Assert.Equal(["board"], matrix.Rows.Select(r => r.Channel.Id));
                Assert.Equal(["stream"], matrix.Columns.Select(c => c.Mix.Id));
                CheckMatrixCells(window, matrix, where);
                Border[] shown = [.. cards.Where(c => c.IsVisible)];
                AssertNoOverlap([.. shown, header]);
                foreach (Border card in shown) AssertControlsInside(card, scroll);
            }
            Capture(window, "skin-broadcast-mini-1040");
            Assert.Null(model.ChooseMixerView(MainViewModel.FullView));
            Layout(window, 1040, 1200);
            Assert.Equal(BroadcastMixes, matrix.Columns.Select(c => c.Mix.Id));
            Assert.Same(matrix, window.MixMatrix);

            // Monitor B hidden: its column head and its cell in every row go,
            // and the columns left stand over their cells.
            string monitorBHidden = mixer.Replace("""{"id":"monitor2","name":"Monitor B","kind":"monitor","appearance":{"defaultIcon":"headset"}}""",
                """{"id":"monitor2","name":"Monitor B","kind":"monitor","appearance":{"defaultIcon":"headset","hidden":true}}""");
            Assert.NotEqual(mixer, monitorBHidden);
            apply.Invoke(model, [JsonNode.Parse(monitorBHidden)]);
            string[] withoutB = [.. BroadcastMixes.Where(id => id != "monitor2")];
            var showHidden = window.FindControl<ToggleButton>("ShowHiddenButton")!;
            foreach (double width in new[] { 640d, 1040, 2560 })
            {
                Layout(window, width, 1400);
                string where = $"Monitor B hidden at {width}";
                Assert.Equal(withoutB, matrix.Columns.Select(c => c.Mix.Id));
                Assert.DoesNotContain(window.GetVisualDescendants().OfType<Border>(),
                    b => b.Name is "MatrixColumn" or "MatrixCell" && b.DataContext is MatrixColumnViewModel { Mix.Id: "monitor2" } or MatrixCellViewModel { Mix.Id: "monitor2" });
                CheckMatrixCells(window, matrix, where);
                CheckBroadcastAt(window, model, where);
                Assert.True(showHidden.IsEffectivelyVisible, where);
            }
            Assert.Same(mixes[1], model.Mixes.Single(m => m.Id == "monitor2"));
            Layout(window, 1040, 1400);
            Capture(window, "skin-broadcast-monitor-b-hidden");

            // Show hidden puts the column back in its place, faded, cells and all.
            showHidden.IsChecked = true;
            Layout(window, 1040, 1400);
            Assert.True(model.ShowHidden);
            Assert.Equal(BroadcastMixes, matrix.Columns.Select(c => c.Mix.Id));
            CheckMatrixCells(window, matrix, "Monitor B shown");
            double faded = (double)window.FindResource("Ox.Mixer.HiddenOpacity")!;
            Assert.Equal(faded, ColumnHead(window, "monitor2").Opacity, 3);
            Assert.Equal(faded, MatrixCell(window, "music", "monitor2").Opacity, 3);
            Assert.Equal(1, ColumnHead(window, "monitor").Opacity, 3);
            Assert.Equal(1, MatrixCell(window, "music", "monitor").Opacity, 3);
            Capture(window, "skin-broadcast-monitor-b-shown");
            showHidden.IsChecked = false;
            apply.Invoke(model, [JsonNode.Parse(mixer)]);
            Layout(window, 1040, 1200);
            Assert.False(model.ShowHidden);
            Assert.False(showHidden.IsEffectivelyVisible);
            Assert.Equal(BroadcastMixes, matrix.Columns.Select(c => c.Mix.Id));

            // Detached: the matrix goes with the panel into the mixer's window and comes back.
            MixerPopOutWindowTests.CheckMatrix(window, model);

            // To Material and back: the same sections, channels, mixes and sends throughout.
            Wear(SkinPackage.DefaultId);
            Layout(window, 1040, 900);
            Assert.Null(window.MixMatrix);
            Assert.True(Card("InputsTile").IsVisible);
            Wear("broadcast");
            Layout(window, 1040, 900);
            Assert.NotNull(window.MixMatrix);
            // Still open on a second wearing.
            Assert.True(headphones.IsExpanded);
            Assert.True(applications.IsExpanded);
            Assert.Equal(expanders, Usual.Select(name => window.FindControl<Expander>(name)!));
            Assert.Equal(cards, expanders.Select(e => (Border)e.Parent!));
            Assert.Equal(contents, expanders.Select(e => e.Content));
            Assert.Equal(channels, model.Channels);
            Assert.Equal(mixes, model.Mixes);
            Assert.Equal(sends, model.Channels.SelectMany(c => c.Sends));
            foreach (MatrixRowViewModel row in window.MixMatrix!.Rows)
                foreach (MatrixCellViewModel cell in row.Cells)
                    Assert.Contains(cell.Send!, sends);

            // Wave: the same arrangement and the same matrix in its own palette and sizes.
            CheckWave(window, model, mixer);
            Wear(SkinPackage.DefaultId);
            Layout(window, 1040, 900);
        }
        finally
        {
            SkinService.ApplyControlSizing(touch);
            window.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// A new install: no ui.json and no skin folders of the user's. The launch
    /// puts Broadcast on without writing anything, and a window built after it
    /// opens on the mix matrix from the start, not by a switch, and fits at
    /// the widths and sizings the Broadcast checks use.
    /// </summary>
    private static void CheckFreshStart()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-fresh-start-").FullName;
        string[] names = ["XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_DATA_DIRS", SkinService.OverrideVariable];
        string?[] old = [.. names.Select(Environment.GetEnvironmentVariable)];
        SkinEntry skin = SkinService.Current;
        string mode = SkinService.Mode;
        bool touch = SkinService.TouchControls;
        Avalonia.Styling.ThemeVariant? variant = Application.Current!.RequestedThemeVariant;
        MainWindow? window = null;
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(dir, "config"));
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(dir, "data"));
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(dir, "none"));
            Environment.SetEnvironmentVariable(SkinService.OverrideVariable, null);
            string uiJson = Path.Combine(dir, "config", "openxlr", "ui.json");

            SkinService.Initialize();
            Assert.Equal(SkinPackage.StartingId, SkinService.Current.Id);
            Assert.False(SkinService.TouchControls);
            Assert.False(File.Exists(uiJson));

            window = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
            var model = (MainViewModel)window.DataContext!;
            FillBroadcastModel(model);
            window.Show();
            Layout(window, 1040, 1400);
            Assert.IsType<MixMatrixViewModel>(window.MixMatrix);
            Assert.Null(window.MixBanks);
            Assert.False(((Border)window.FindControl<Expander>("InputsTile")!.Parent!).IsVisible);
            foreach (bool enlarged in new[] { false, true })
            {
                SkinService.ApplyControlSizing(enlarged);
                foreach (double width in new[] { 640d, 1040, 2560 })
                {
                    Layout(window, width, 1400);
                    CheckBroadcastAt(window, model, $"fresh start {(enlarged ? "touch" : "standard")} at {width}");
                }
            }
            SkinService.ApplyControlSizing(false);
            Layout(window, 1040, 1400);
            Capture(window, "skin-broadcast-fresh-start");
            Assert.Equal("broadcast", SkinService.Current.Id);
            // Whatever the window keeps for itself, the skin choice is still nobody's.
            Assert.Null(UiSettings.Load().Skin);
        }
        finally
        {
            window?.Close();
            for (int i = 0; i < names.Length; i++) Environment.SetEnvironmentVariable(names[i], old[i]);
            SkinService.ApplyMode(mode, skin);
            SkinService.ApplyControlSizing(touch);
            Application.Current!.RequestedThemeVariant = variant;
            SkinService.Apply(skin);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// The fixture every Broadcast check draws: six mixes, an interface
    /// without XLR 2, a capture channel that is not connected, a 40 character
    /// name, two applications and levels under the faders. Returns the mixer
    /// state it applied, for the checks that change it.
    /// </summary>
    private static string FillBroadcastModel(MainViewModel model)
    {
        Assert.Equal(40, LongName.Length);
        string mixer = """
            {"mixes":[{"id":"monitor","name":"Monitor A","kind":"monitor","appearance":{"defaultIcon":"headset"}},
                      {"id":"monitor2","name":"Monitor B","kind":"monitor","appearance":{"defaultIcon":"headset"}},
                      {"id":"stream","name":"Stream","kind":"virtualMic","appearance":{"defaultIcon":"broadcast","colour":"#FF7A00"}},
                      {"id":"chat","name":"Chat","kind":"virtualMic","appearance":{"defaultIcon":"people"}},
                      {"id":"auxout","name":"Aux","kind":"auxPort","appearance":{"defaultIcon":"jack"}},
                      {"id":"music-bed","name":"Music bed","kind":"virtualMic","appearance":{"icon":"music","defaultIcon":"podcast"}}],
             "channels":[{"id":"xlr1","name":"XLR 1","hardware":true,"levels":{"monitor":0.8,"stream":0.6},"volume":0.7,"appearance":{"defaultIcon":"mic"}},
                         {"id":"xlr2","name":"XLR 2","hardware":true,"present":false,"appearance":{"defaultIcon":"mic"}},
                         {"id":"aux","name":"Aux In","hardware":true,"volume":1,"appearance":{"defaultIcon":"jack"}},
                         {"id":"music","name":"Music","levels":{"monitor":0.5,"stream":0.4},"mutedIn":["stream"],"volume":0.9,"appearance":{"icon":"♫","defaultIcon":"music"}},
                         {"id":"board","name":"LONGNAME","levels":{"monitor":1,"stream":1},"volume":1,"appearance":{"defaultIcon":"apps"}},
                         {"id":"game","name":"Game","levels":{"monitor":1,"stream":1},"appearance":{"defaultIcon":"game"}},
                         {"id":"cam","name":"Capture card","captureSource":"alsa_input.usb-capture","captureConnected":false,"volume":0.75,"appearance":{"defaultIcon":"camera"}}],
             "monitorOutputs":["speakers"],"outputVolume":0.6,"inserts":{}}
            """.Replace("LONGNAME", LongName);
        typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(model, [JsonNode.Parse(mixer)]);
        typeof(MainViewModel).GetMethod("ApplyDevices", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(model, [JsonNode.Parse("""
            [{"kind":0,"name":"speakers","description":"Desk speakers with a long description from the driver"},
             {"kind":0,"name":"headset","description":"USB headset"}]
            """), JsonNode.Parse(mixer)]);
        ChannelChoice[] choices = [new("music", "Music"), new("game", "Game"), new("board", LongName)];
        foreach (string name in new[] { "Google Chrome with a very long window title", "Discord" })
        {
            var app = new AppStreamViewModel(new DaemonClient("ws://127.0.0.1:1/ws"), name, name, choices);
            app.ApplyFromDaemon("music", true, true);
            model.Apps.Add(app);
            model.ActiveApps.Add(app);
        }
        ApplicationIconWindowTests.Assign(model.Apps);
        // A Wave XLR Pro's view of the inputs: one XLR input, so XLR 2 is
        // not there, and the daemon says so of its channel too.
        model.HasXlr2 = false;
        // Levels under the faders, so the screenshots show the meter as the faders' track.
        double[] levels = [0.82, 0, 0.4, 0.62, 0.95, 0.55, 0];
        for (int i = 0; i < model.Channels.Count; i++)
        {
            model.Channels[i].MeterL = levels[i];
            model.Channels[i].MeterR = levels[i] * 0.9;
        }
        foreach (MixViewModel mix in model.Mixes) { mix.MeterL = 0.6; mix.MeterR = 0.58; }
        return mixer;
    }

    /// <summary>
    /// One size of the Broadcast window, or of Wave's, which arranges it the
    /// same way: HEADPHONES at the top left, the toolbar at the top right, the
    /// matrix across the window under them, APPLICATIONS under the matrix,
    /// nothing overlapping or clipped, and every cell of the matrix in its
    /// place.
    /// </summary>
    private static void CheckBroadcastAt(MainWindow window, MainViewModel model, string where)
    {
        Border Card(string tile) => (Border)window.FindControl<Expander>(tile)!.Parent!;
        var desk = (StackPanel)window.LayoutHost.PanelFor("desk")!;
        Point At(string tile) => Card(tile).TranslatePoint(default, desk)!.Value;
        double Right(string tile) => At(tile).X + Card(tile).Bounds.Width;
        double Bottom(string tile) => At(tile).Y + Card(tile).Bounds.Height;
        var header = window.FindControl<Border>("HeaderCard")!;
        var scroll = window.FindControl<ScrollViewer>("SectionScroll")!;
        string[] shownTiles = ["MonitorTile", "SubmixerTile", "ApplicationsTile", "HeadphonesTile"];
        Border[] cards = [.. shownTiles.Select(Card)];
        Assert.All(cards, card => Assert.True(card.IsVisible, $"{((Expander)card.Child!).Name} is hidden {where}"));
        Assert.False(Card("InputsTile").IsVisible, where);
        AssertNoOverlap([.. cards, header]);
        foreach (Border card in cards) AssertControlsInside(card, scroll);
        AssertInside(window.FindControl<Grid>("WindowHeader")!, window);

        // HEADPHONES at the top left, the toolbar at the top right, the matrix under them across the whole width.
        Assert.Equal(0, At("HeadphonesTile").X, 0.5);
        Assert.Equal(0, At("HeadphonesTile").Y, 0.5);
        Assert.True(Right("HeadphonesTile") <= At("MonitorTile").X + 0.5, $"HEADPHONES reaches {Right("HeadphonesTile")}, the toolbar starts at {At("MonitorTile").X} {where}");
        Assert.True(Right("HeadphonesTile") > desk.Bounds.Width / 3, $"HEADPHONES ends at {Right("HeadphonesTile")} {where}");
        Assert.Equal(0, At("MonitorTile").Y, 0.5);
        Assert.Equal(desk.Bounds.Width, Right("MonitorTile"), 0.5);
        Assert.True(At("MonitorTile").X > desk.Bounds.Width / 3, $"the toolbar starts at {At("MonitorTile").X} {where}");
        Assert.Equal(0, At("SubmixerTile").X, 0.5);
        Assert.Equal(desk.Bounds.Width, Card("SubmixerTile").Bounds.Width, 0.5);
        Assert.True(At("SubmixerTile").Y >= Bottom("MonitorTile") - 0.5, where);
        Assert.True(At("SubmixerTile").Y >= Bottom("HeadphonesTile") - 0.5, where);
        // APPLICATIONS under the matrix, across the whole width.
        Assert.Equal(0, At("ApplicationsTile").X, 0.5);
        Assert.Equal(desk.Bounds.Width, Card("ApplicationsTile").Bounds.Width, 0.5);
        Assert.True(At("ApplicationsTile").Y >= Bottom("SubmixerTile") - 0.5, where);

        // The toolbar's controls inside it, the picker above the volume in its narrow column.
        var picker = window.FindControl<DropDownButton>("MonitorDestination")!;
        var volume = window.FindControl<Slider>("OutputVolumeSlider")!;
        foreach (Control part in new Control[] { picker, volume }) AssertInside(part, Card("MonitorTile"));
        AssertNoOverlap([picker, volume]);

        MixMatrixViewModel matrix = window.MixMatrix!;
        Assert.Equal(model.HasXlr2 ? 7 : 6, matrix.Rows.Count);
        // Every mix but one hidden in the layout editor.
        Assert.Equal(BroadcastMixes.Count(id => model.Mixes.Single(m => m.Id == id).DisplayVisible), matrix.Columns.Count);
        CheckMatrixCells(window, matrix, where);

        // XLR 1's binding line stands under its name inside the name column, at every size.
        if (matrix.Rows.Any(r => r.Channel.Id == "xlr1"))
            InputBindingWindowTests.CheckBindingLine(RowHead(window, "xlr1"), model.Channels.Single(c => c.Id == "xlr1").BindingLabel);

        // The 40 character name shortens inside its row head rather than running out of it.
        Border board = RowHead(window, "board");
        TextBlock name = board.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible && t.Text == LongName);
        AssertInside(name, name.GetVisualAncestors().OfType<Border>().First(b => b.Name == "MatrixRowName"));

        // The applications in columns of tiles inside their section (CheckApplicationColumns has the rest).
        Border[] chips = ApplicationTiles(window);
        Assert.Equal(model.ActiveApps.Count, chips.Length);
        Assert.Empty(ApplicationLines(window));
        Assert.Empty(ApplicationChips(window));
        foreach (Border chip in chips) AssertInside(chip, Card("ApplicationsTile"));
        ApplicationIconWindowTests.Check(window, where);

        // HEADPHONES beside the toolbar need a window about 1000 wide; the tree fits a
        // wider window, a matrix wider than its card scrolls inside the card, and a
        // narrower window scrolls the tree sideways under a header that keeps its width.
        double gap = ((Grid)window.LayoutHost.PanelFor("mixes")).ColumnSpacing;
        bool fits = window.Bounds.Width >= SkinLayoutDefaults.RowSectionMinWidth + gap + 360 + 32;
        Assert.Equal(fits ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto, scroll.HorizontalScrollBarVisibility);
        if (fits)
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1, $"the tree is {scroll.Extent.Width} wide in {scroll.Viewport.Width} {where}");
        else
        {
            Assert.True(scroll.Extent.Width > scroll.Viewport.Width + 1, $"the tree does not scroll {where}");
            Assert.InRange(header.Bounds.Width, window.Bounds.Width - 72, window.Bounds.Width - 16);
        }
    }

    /// <summary>
    /// The matrix as drawn: a column head per column and a row of a head and
    /// a cell per column for each row, every cell the token's size, the heads
    /// over their cells, the parts of each cell and row head apart and inside
    /// it, and a matrix wider than its card scrolling sideways to its last
    /// column.
    /// </summary>
    internal static void CheckMatrixCells(Window window, MixMatrixViewModel matrix, string where)
    {
        MixMatrixView view = window.GetVisualDescendants().OfType<MixMatrixView>().Single(v => v.IsEffectivelyVisible);
        ScrollViewer scroller = view.Scroller;
        double cellWidth = (double)window.FindResource("Ox.Matrix.CellWidth")!;
        double rowHeight = (double)window.FindResource("Ox.Matrix.RowHeight")!;
        double nameWidth = (double)window.FindResource("Ox.Matrix.NameWidth")!;
        Border[] heads = [.. view.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "MatrixColumn" && b.IsEffectivelyVisible)];
        Assert.Equal(matrix.Columns.Select(c => c.Mix), heads.Select(h => ((MatrixColumnViewModel)h.DataContext!).Mix));
        StackPanel[] rows = [.. view.GetVisualDescendants().OfType<StackPanel>().Where(p => p.Name == "MatrixRow" && p.IsEffectivelyVisible)];
        Assert.Equal(matrix.Rows.Select(r => r.Channel), rows.Select(r => ((MatrixRowViewModel)r.DataContext!).Channel));
        AssertNoOverlap(heads);
        foreach (Border head in heads)
        {
            Assert.Equal(cellWidth, head.Bounds.Width, 0.5);
            foreach (Control inner in head.GetVisualDescendants().OfType<Control>()
                         .Where(c => c is TextBlock or Slider or ToggleButton or Button && c.IsEffectivelyVisible && c.Bounds.Width > 0))
                AssertInside(inner, head);
        }
        foreach (StackPanel row in rows)
        {
            var model = (MatrixRowViewModel)row.DataContext!;
            Border head = row.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "MatrixRowHead");
            // The name and source columns, inside the tile's own border where the skin draws one.
            double scale = window.RenderScaling;
            double border = (Math.Round(head.BorderThickness.Left * scale)
                + Math.Round(head.BorderThickness.Right * scale)) / scale;
            Assert.Equal(nameWidth + cellWidth + border, head.Bounds.Width, 0.5);
            // A head that stacks a gain over the volume, or the EQ over the chain,
            // takes what the two need, never less than the row height; every
            // other head is the row height.
            if (model.Tall) Assert.True(head.Bounds.Height >= rowHeight - 0.5, $"{model.Channel.Id} is {head.Bounds.Height} tall {where}");
            else Assert.Equal(rowHeight, head.Bounds.Height, 0.5);
            Border source = head.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "MatrixSource");
            AssertPartsApart(source);
            CheckLevels(head, model, where);
            CheckProcessingPlace(head, model, where);
            Border[] cells = [.. row.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "MatrixCell" && b.IsEffectivelyVisible)];
            Assert.Equal(matrix.Columns.Count, cells.Length);
            AssertNoOverlap([head, .. cells]);
            for (int i = 0; i < cells.Length; i++)
            {
                var cell = (MatrixCellViewModel)cells[i].DataContext!;
                Assert.Same(model.Channel, cell.Channel);
                Assert.Same(matrix.Columns[i].Mix, cell.Mix);
                Assert.Same(cell.Channel.Sends.SingleOrDefault(s => s.MixId == cell.Mix.Id), cell.Send);
                Assert.Equal(cellWidth, cells[i].Bounds.Width, 0.5);
                Assert.Equal(head.Bounds.Height, cells[i].Bounds.Height, 0.5);
                // A column's head stands over its cells.
                Assert.Equal(heads[i].TranslatePoint(default, view)!.Value.X, cells[i].TranslatePoint(default, view)!.Value.X, 0.5);
                if (cell.IsEmpty) continue;
                AssertPartsApart(cells[i]);
                Slider fader = cells[i].GetVisualDescendants().OfType<Slider>().Single();
                Assert.Equal(Orientation.Horizontal, fader.Orientation);
                Assert.Equal(cell.SendName, Avalonia.Automation.AutomationProperties.GetName(fader));
                MeterFader track = cells[i].GetVisualDescendants().OfType<MeterFader>().Single();
                Assert.Equal(matrix.MeterUnderFader, track.Meters.IsEffectivelyVisible);
                Assert.All(track.Meters.Children.OfType<LevelMeter>(), m =>
                    Assert.Contains(cell.Channel.Name, Avalonia.Automation.AutomationProperties.GetName(m), StringComparison.Ordinal));
            }
        }
        if (scroller.Extent.Width > scroller.Viewport.Width + 1)
        {
            Assert.Equal(ScrollBarVisibility.Auto, scroller.HorizontalScrollBarVisibility);
            scroller.Offset = new Vector(scroller.Extent.Width - scroller.Viewport.Width, 0);
            Layout(window, window.ClientSize.Width, window.ClientSize.Height);
            Border last = heads[^1];
            double right = last.TranslatePoint(new Point(last.Bounds.Width, 0), scroller)!.Value.X;
            Assert.InRange(right, 0, scroller.Viewport.Width + 0.5);
            scroller.Offset = default;
            Layout(window, window.ClientSize.Width, window.ClientSize.Height);
        }
    }

    /// <summary>
    /// A row head's gain part: the gain over the channel's volume when it
    /// stacks them, each a whole fader inside the head, the gain above, apart
    /// and as long as each other; a lone volume or gain otherwise. The volume
    /// fader names its channel, and the meter runs under one of them at most.
    /// </summary>
    internal static void CheckLevels(Border head, MatrixRowViewModel model, string where)
    {
        if (!model.Spec.Parts.Contains(SkinStripParts.Gain)) return;
        StackPanel levels = head.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "MatrixLevels");
        Slider[] faders = [.. levels.GetVisualDescendants().OfType<Slider>().Where(s => s.IsEffectivelyVisible)
            .OrderBy(s => s.TranslatePoint(default, head)!.Value.Y)];
        Slider? volume = faders.SingleOrDefault(s => s.Name == "MatrixVolume");
        Assert.Equal(model.ShowsVolume, volume is not null);
        Assert.Equal((model.HasGainFader ? 1 : 0) + (model.ShowsVolume ? 1 : 0), faders.Length);
        foreach (Slider fader in faders) AssertInside(fader, head);
        if (volume is not null)
        {
            Assert.Equal(Localizer.Format("ChannelVolumeName", model.Channel.Name), Avalonia.Automation.AutomationProperties.GetName(volume));
            Assert.Equal(model.Channel.VolumeTip, ToolTip.GetTip(volume));
        }
        if (model.Stacked)
        {
            Assert.Same(volume, faders[1]);
            double gainBottom = faders[0].TranslatePoint(new Point(0, faders[0].Bounds.Height), head)!.Value.Y;
            Assert.True(gainBottom <= faders[1].TranslatePoint(default, head)!.Value.Y + 0.5,
                $"{model.Channel.Id}: the gain and the volume overlap {where}");
            // Both start at the part's left edge and keep most of its width; a
            // lock or the direct path's mark takes only the end of a line.
            foreach (Slider fader in faders)
            {
                Assert.Equal(0, fader.TranslatePoint(default, levels)!.Value.X, 0.5);
                Grid line = Assert.IsType<Grid>(fader.Parent!.Parent);
                // The gap belongs to neither control when comparing their shares.
                double usable = line.Bounds.Width - line.ColumnSpacing * (line.ColumnDefinitions.Count - 1);
                Assert.True(fader.Bounds.Width >= usable * 0.6,
                    $"{model.Channel.Id}: a fader {fader.Bounds.Width:0.#} wide in {usable:0.#} usable pixels {where}");
            }
        }
        MeterFader[] tracks = [.. levels.GetVisualDescendants().OfType<MeterFader>().Where(m => m.IsEffectivelyVisible)];
        Assert.True(tracks.Count(m => m.Meters.IsEffectivelyVisible) <= 1, $"{model.Channel.Id} draws two meters {where}");
    }

    private static double RowHeightOf(Window window) => (double)window.FindResource("Ox.Matrix.RowHeight")!;

    /// <summary>A channel's row head in the matrix.</summary>
    internal static Border RowHead(Visual root, string channel) =>
        root.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Name == "MatrixRowHead" && b.DataContext is MatrixRowViewModel r && r.Channel.Id == channel);

    /// <summary>The head at the top of a mix's column.</summary>
    internal static Border ColumnHead(Visual root, string mix) =>
        root.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Name == "MatrixColumn" && b.DataContext is MatrixColumnViewModel c && c.Mix.Id == mix);

    /// <summary>The cell where a channel's row crosses a mix's column.</summary>
    internal static Border MatrixCell(Visual root, string channel, string mix) =>
        root.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Name == "MatrixCell" && b.DataContext is MatrixCellViewModel c && c.Channel.Id == channel && c.Mix.Id == mix);
}
