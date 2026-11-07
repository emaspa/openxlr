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
using OpenXLR.UI.Views;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

/// <summary>
/// The templates a skin's layout picks: an input rack down a narrow left
/// column and two mix banks of upright strips beside it, at 640, 1040 and
/// 2560 pixels, with nothing clipped or overlapping, the strips' parts in
/// the skin's order and the hidden ones out of the tab order, the pickers
/// listing every mix and switching, the channel meter beside a send named as
/// the channel's, capability gating in the rack, one Arrange handle per
/// channel, a send list in banks one above the other, and the way back to
/// the classic templates with every view model kept.
/// </summary>
internal static partial class SkinLayoutWindowTests
{
    /// <summary>
    /// The plan's arrangement: the header across the top, an input rack in a
    /// 280 pixel column, two banks of upright strips beside it, the rest below.
    /// </summary>
    private const string BanksSkin = """
        {"schema":1,"name":"Banks","tokens":{"Ox.Card.Background":"#22252b"},"layout":{"version":1,
         "main":{"kind":"grid","id":"desktop","columns":[{"size":280},{"star":1}],"rows":["auto","auto","auto"],
          "children":[
           {"kind":"region","region":"header","row":0,"column":0,"columnSpan":2},
           {"kind":"region","region":"inputs","row":1,"column":0},
           {"kind":"region","region":"submixer","row":1,"column":1},
           {"kind":"stack","id":"lower","orientation":"vertical","row":2,"column":0,"columnSpan":2,"children":[
             {"kind":"region","region":"headphones"},{"kind":"region","region":"monitor"},{"kind":"region","region":"applications"}]}]},
         "regions":{
          "inputs":{"template":"input-rack","strip":{"partsOrder":["identity","gain","meter"],"hidden":["effects"],
                    "label":{"style":"icon-and-text"},"meter":{"orientation":"horizontal","placement":"below-label"}}},
          "submixer":{"template":"mix-banks","visibleBanks":2,"selectionDefaults":["monitor","gone","stream"],
                      "strip":{"template":"vertical-strip","partsOrder":["identity","meter","fader","mute"],
                               "fader":{"orientation":"vertical"},"meter":{"orientation":"vertical","placement":"beside-fader"}},
                      "master":{"template":"master-strip"}}}}}
        """;

    /// <summary>The same window with the submixer as three send lists one above the other, reversed faders, icons for names and keys for pickers.</summary>
    private const string ListSkin = """
        {"schema":1,"name":"List","tokens":{"Ox.Card.Background":"#20232a"},"layout":{"version":1,
         "regions":{
          "submixer":{"template":"mix-banks","orientation":"vertical","visibleBanks":3,"picker":"buttons",
                      "strip":{"template":"send-list","hidden":["value"],"label":{"style":"icon"},
                               "fader":{"direction":"reversed"},"meter":{"placement":"beside-label"}},
                      "master":{"template":"master-card"}}}}}
        """;

    private static readonly string[] BankFixtureMixes = ["monitor", "monitor2", "stream", "chat", "auxout"];

    private static void CheckTemplates(string dir)
    {
        Write(dir, "banks", BanksSkin);
        Write(dir, "list", ListSkin);
        // A rack of keys: a hi-fi skin's own tokens, its images given flat
        // colours, with the badge chips, check keys and lamp-lettered faces.
        Write(dir, "keys", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "skin-rack-keys.json")));
        var window = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
        try
        {
            var model = (MainViewModel)window.DataContext!;
            typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(model, [JsonNode.Parse("""
                {"mixes":[{"id":"monitor","name":"Monitor A","kind":"monitor"},{"id":"monitor2","name":"Monitor B","kind":"monitor"},
                          {"id":"stream","name":"Stream","kind":"virtualMic",
                           "eq":{"enabled":false,"bands":[{"enabled":true,"type":"peak","frequency":1000,"gain":0,"q":0.707}],"available":true,"processing":"software"}},
                          {"id":"chat","name":"Chat","kind":"virtualMic"},
                          {"id":"auxout","name":"Aux","kind":"auxPort"}],
                 "channels":[{"id":"xlr1","name":"XLR 1","hardware":true,"levels":{"monitor":0.8,"stream":0.6},"volume":0.75},
                             {"id":"xlr2","name":"XLR 2","hardware":true,"volume":1},
                             {"id":"aux","name":"Aux In","hardware":true},
                             {"id":"music","name":"Music with a long display name","levels":{"monitor":0.5,"stream":0.4},"appearance":{"icon":"♫"}},
                             {"id":"game","name":"Game","levels":{"monitor":1,"stream":1},"volume":1},
                             {"id":"mic2","name":"Second microphone","captureSource":"alsa_input.usb","captureConnected":true,"volume":0.5}],
                 "inserts":{}}
                """)]);
            ChannelViewModel[] channels = [.. model.Channels];
            MixViewModel[] mixes = [.. model.Mixes];
            SendViewModel[] sends = [.. model.Channels.SelectMany(c => c.Sends)];
            window.Show();
            Layout(window, 1040, 900);
            Wear("banks");
            Layout(window, 1040, 900);
            // A skin that sets the optional styles keeps every strip its own
            // height through a reload and through a change of skin and back.
            // Rebuilding those styles in place once restyled every control,
            // and a strip measured while its hidden handle briefly counted
            // stayed a handle's height taller than what it holds.
            {
                Wear("keys");
                Layout(window, 1920, 1200);
                string Heights() => string.Join(" | ", BankCells(window).Select(b => string.Join(",", b.Select(c => c.Bounds.Height.ToString("0.#")))));
                string worn = Heights();
                Assert.Empty(SkinService.Reload());
                Layout(window, 1920, 1200);
                Assert.Equal(worn, Heights());
                Wear("banks");
                Layout(window, 1920, 1200);
                Wear("keys");
                Layout(window, 1920, 1200);
                Assert.Equal(worn, Heights());
                Wear("banks");
                Layout(window, 1040, 900);
            }

            var panel = window.FindControl<MixerPanel>("MixerPanel")!;
            var rack = window.FindControl<InputRackView>("InputRack")!;
            Assert.True(rack.IsVisible);
            Assert.False(window.FindControl<StackPanel>("HardwarePanel")!.IsVisible);
            Assert.NotNull(window.MixBanks);
            Assert.Same(window.MixBanks, panel.Banks);
            Assert.True(panel.BankItems.IsVisible);
            // The banks stand in for the classic tiles, master headings and all.
            Assert.Null(window.FindControl<TextBlock>("InputChannelHeading"));
            Assert.False(window.FindControl<ItemsControl>("ChannelTiles")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<TextBlock>("OutputMixHeading")!.IsEffectivelyVisible);
            MixerPresentationViewModel banks = window.MixBanks!;
            Assert.Equal(["monitor", "stream"], banks.Selection);

            // The regions' narrowest widths come from the templates: the rack
            // fits its 280 pixel column, the banks ask for less than a classic section.
            double strip = (double)window.FindResource("Ox.Mixer.StripWidth")!;
            Assert.Equal(3 * strip + SkinLayoutDefaults.CardPadding,
                SkinLayoutDefaults.RegionMinWidth(SkinLayoutRegions.Inputs, SkinService.Layout, new RegionSizes(strip, 232, 12, 2, 6)));
            Border Card(string tile) => (Border)window.FindControl<Expander>(tile)!.Parent!;
            Border[] cards = [.. Usual.Select(Card)];
            var scroll = window.FindControl<ScrollViewer>("SectionScroll")!;
            int mixEqs = 0;
            foreach (double width in new[] { 640d, 1040, 2560 })
            {
                Layout(window, width, 1200);
                string where = $"at {width}";
                AssertNoOverlap([.. cards.Where(c => c.IsVisible), window.FindControl<Border>("HeaderCard")!]);
                foreach (Border card in cards.Where(c => c.IsVisible)) AssertControlsInside(card, scroll);
                LocalizationRenderingTests.CheckLongMixerActions(window, Card("SubmixerTile"));
                Assert.Equal(280, Card("InputsTile").Bounds.Width, 0.5);
                double banksNeed = SkinLayoutDefaults.RegionMinWidth(SkinLayoutRegions.Submixer, SkinService.Layout,
                    new RegionSizes(strip, (double)window.FindResource("Ox.Mixer.MixWidth")!,
                        (double)window.FindResource("Ox.Mixer.BankSpacing")!, 2, banks.VisibleChannels));
                Assert.True(banksNeed < SkinLayoutDefaults.RowSectionMinWidth, $"the banks ask for {banksNeed}");
                Assert.True(Card("SubmixerTile").Bounds.Width >= banksNeed - 0.5, where);

                // Every input strip inside the rack, one under another.
                Border[] strips = [.. rack.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "InputStrip" && b.IsEffectivelyVisible)];
                Assert.Equal(6, strips.Length);
                AssertNoOverlap(strips);
                foreach (Border s in strips)
                {
                    AssertInside(s, Card("InputsTile"));
                    foreach (Control c in s.GetVisualDescendants().OfType<Control>()
                                 .Where(c => c is TextBlock or Slider or Button or ToggleButton && c.IsEffectivelyVisible && c.Bounds.Width > 0))
                        AssertInside(c, s);
                }

                // Two banks side by side, every strip upright and inside its bank's scroller.
                Border[] bankTiles = Banks(window);
                Assert.Equal(2, bankTiles.Length);
                AssertNoOverlap(bankTiles);
                Assert.True(bankTiles[1].TranslatePoint(default, window)!.Value.X > bankTiles[0].TranslatePoint(default, window)!.Value.X, where);
                double length = (double)window.FindResource("Ox.Mixer.VerticalFaderLength")!;
                foreach (Border bank in bankTiles)
                {
                    AssertInside(bank, Card("SubmixerTile"));
                    var cells = bank.GetVisualDescendants().OfType<ScrollViewer>().Single(v => v.Name == "BankCells");
                    AssertInside(cells, bank);
                    var master = bank.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "BankMaster");
                    AssertInside(master, bank);
                    AssertNoOverlap([cells, master]);
                    mixEqs += MixEqOverChain(master, where);
                    foreach (Slider fader in bank.GetVisualDescendants().OfType<Slider>().Where(f => f.IsEffectivelyVisible))
                    {
                        Assert.Equal(Orientation.Vertical, fader.Orientation);
                        Assert.Equal(length, fader.Bounds.Height, 0.5);
                        Assert.True(fader.Bounds.Height > fader.Bounds.Width * 2, where);
                    }
                    foreach (LevelMeter meter in bank.GetVisualDescendants().OfType<LevelMeter>().Where(m => m.IsEffectivelyVisible))
                    {
                        Assert.Equal(Orientation.Vertical, meter.Orientation);
                        Assert.Equal(4, meter.Bounds.Width, 0.5);
                        Assert.True(meter.Bounds.Height >= length - 0.5, $"an upright meter is {meter.Bounds.Height} tall {where}");
                    }
                    if (cells.Extent.Width > cells.Viewport.Width + 1)
                        Assert.Equal(ScrollBarVisibility.Auto, cells.HorizontalScrollBarVisibility);
                }
                foreach (Border cell in BankCells(window).SelectMany(c => c)) AssertPartsApart(cell);
                foreach (Border s in strips) AssertPartsApart(s);
                Capture(window, $"skin-layout-banks-{width}");
            }
            // Stream's master strip in the second bank, at every width.
            Assert.Equal(3, mixEqs);
            Layout(window, 1040, 1200);

            // The rack's parts in the skin's order; effects left out and out of the tab order.
            Border xlr1 = RackStrip(rack, "xlr1");
            double Top(string part) => Part(xlr1, part).TranslatePoint(default, xlr1)!.Value.Y;
            Assert.True(Top(SkinStripParts.Identity) < Top(SkinStripParts.Gain), "identity before gain");
            Assert.True(Top(SkinStripParts.Gain) < Top(SkinStripParts.Meter), "gain before meter");
            Assert.True(Top(SkinStripParts.Meter) < Top(SkinStripParts.HardwareControls), "the parts left out of partsOrder follow");
            // The binding, left out of partsOrder too, follows in its default place: before the switches.
            Assert.True(Top(SkinStripParts.Meter) < Top(SkinStripParts.Binding), "the binding follows the ordered parts");
            Assert.True(Top(SkinStripParts.Binding) < Top(SkinStripParts.HardwareControls), "the binding keeps its default place");
            Assert.True(Part(xlr1, SkinStripParts.Binding).IsEffectivelyVisible);
            Assert.Equal(Localizer.Text("Xlr1Jack"), Part(xlr1, SkinStripParts.Binding).GetVisualDescendants().OfType<TextBlock>().First().Text);
            Assert.Empty(RackStrip(rack, "music").GetVisualDescendants().OfType<BindingChip>());
            Control effects = Part(xlr1, SkinStripParts.Effects);
            Assert.False(effects.IsVisible);
            Control[] focusable = Focusable(window);
            Assert.DoesNotContain(focusable, c => c.GetSelfAndVisualAncestors().Contains(effects));
            Assert.Contains(focusable, c => c.Name == "RackGain");
            // XLR 1's own volume stands under its gain in the gain part, a
            // whole fader inside the strip; Aux In, which the daemon reports no
            // volume for here, keeps its level alone.
            Slider rackGain = xlr1.GetVisualDescendants().OfType<Slider>().Single(f => f.Name == "RackGain");
            Grid rackVolume = xlr1.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "RackVolume");
            Assert.True(rackVolume.IsEffectivelyVisible);
            Slider xlr1Volume = rackVolume.GetVisualDescendants().OfType<Slider>().Single();
            Assert.Equal(0.75, xlr1Volume.Value);
            Assert.Equal(Localizer.Language == "en" ? "XLR 1 volume" : Localizer.Format("ChannelVolumeName", "XLR 1"), AutomationProperties.GetName(xlr1Volume));
            Assert.True(xlr1Volume.TranslatePoint(default, xlr1)!.Value.Y >= rackGain.TranslatePoint(new Point(0, rackGain.Bounds.Height), xlr1)!.Value.Y - 0.5);
            Assert.Same(Part(xlr1, SkinStripParts.Gain), rackVolume.Parent);
            AssertInside(xlr1Volume, xlr1);
            Assert.False(RackStrip(rack, "aux").GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "RackVolume").IsEffectivelyVisible);
            // The direct path's mark shows beside the volume of the channel it carries.
            TextBlock rackNote = rackVolume.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "RackVolumeNote");
            Assert.False(rackNote.IsEffectivelyVisible);
            StopConnecting(window);
            model.Channels.Single(c => c.Id == "xlr1").DirectMonitored = true;
            Layout(window, 1040, 1200);
            rackNote = RackStrip(rack, "xlr1").GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "RackVolumeNote");
            Assert.True(rackNote.IsEffectivelyVisible);
            AssertInside(rackNote, RackStrip(rack, "xlr1"));
            Assert.Equal(Localizer.Text("ChannelVolumeDirectTip"), ToolTip.GetTip(rackNote));
            Capture(window, "skin-layout-rack-direct-1040");
            model.Channels.Single(c => c.Id == "xlr1").DirectMonitored = false;
            Layout(window, 1040, 1200);
            // An application channel the daemon reports no volume for has only
            // its name and meter, whatever the skin orders.
            Border music = RackStrip(rack, "music");
            Assert.Null(music.GetVisualDescendants().OfType<Slider>().FirstOrDefault(s => s.IsEffectivelyVisible));
            // One it reports a volume for has that volume as its gain.
            Border mic2 = RackStrip(rack, "mic2");
            Slider mic2Volume = Assert.Single(mic2.GetVisualDescendants().OfType<Slider>(), s => s.IsEffectivelyVisible);
            Assert.Equal(0.5, mic2Volume.Value);
            Assert.Equal(Localizer.Language == "en" ? "Second microphone volume" : Localizer.Format("ChannelVolumeName", "Second microphone"), AutomationProperties.GetName(mic2Volume));
            Assert.Equal("♫", music.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible).Text);
            Assert.Equal("Music with a long display name", AutomationProperties.GetName(
                music.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible)));

            // Capability gating is the INPUTS panel's own.
            var phantom = rack.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == "RackPhantom");
            Assert.True(phantom.IsEffectivelyVisible);
            model.CapPhantom = false;
            Layout(window, 1040, 1200);
            Assert.False(phantom.IsEffectivelyVisible);
            model.CapPhantom = true;
            var gain = rack.GetVisualDescendants().OfType<Slider>().Single(s => s.Name == "RackGain");
            Assert.True(gain.IsEnabled);
            // The client is not connected, so the lock is only the window's state here.
            model.GainLocked = true;
            Layout(window, 1040, 1200);
            Assert.False(gain.IsEnabled);
            model.GainLocked = false;
            Border xlr2 = RackStrip(rack, "xlr2");
            Assert.True(xlr2.IsVisible);
            model.HasXlr2 = false;
            Layout(window, 1040, 1200);
            Assert.False(xlr2.IsVisible);
            model.HasXlr2 = true;
            Layout(window, 1040, 1200);

            // Every bank's picker lists every mix the view shows; a pick changes the cells to that mix's sends.
            ComboBox[] pickers = Pickers(window);
            Assert.Equal(2, pickers.Length);
            foreach (ComboBox picker in pickers)
                Assert.Equal(BankFixtureMixes, picker.Items.Cast<MixViewModel>().Select(m => m.Id));
            Assert.Equal(Localizer.Format("BankPickerName", 1), AutomationProperties.GetName(pickers[0]));
            pickers[0].SelectedItem = model.Mixes.Single(m => m.Id == "chat");
            Layout(window, 1040, 1200);
            Assert.Equal(["chat", "stream"], banks.Selection);
            foreach (BankCellViewModel cell in banks.Banks[0].Cells)
                Assert.Same(cell.Channel.Sends.Single(s => s.MixId == "chat"), cell.Send);
            pickers[0].SelectedItem = model.Mixes.Single(m => m.Id == "stream");
            Layout(window, 1040, 1200);
            Assert.Equal(["stream", "chat"], banks.Selection);
            Assert.Equal(["stream", "chat"], UiSettings.Load().SkinBanksFor("banks", 1));
            Assert.Same(model.Mixes.Single(m => m.Id == "chat"), Pickers(window)[1].SelectedItem);

            // The meter beside a send is the channel's, and says so; the fader names both ends.
            Border gameCell = BankCells(window)[0].Single(b => ((BankCellViewModel)b.DataContext!).Channel.Id == "game");
            LevelMeter[] meters = [.. gameCell.GetVisualDescendants().OfType<LevelMeter>()];
            Assert.Equal([Localizer.Format("ChannelLevelLeft", "Game"), Localizer.Format("ChannelLevelRight", "Game")],
                meters.Select(AutomationProperties.GetName));
            Assert.All(meters, m => Assert.DoesNotContain("Stream", AutomationProperties.GetName(m)));
            Assert.Equal(Localizer.Text("ChannelMeterTip"), ToolTip.GetTip((Control)meters[0].Parent!));
            Slider send = gameCell.GetVisualDescendants().OfType<Slider>().Single();
            Assert.Equal(Localizer.Format("BankSendName", "Game", "Stream"), AutomationProperties.GetName(send));
            Assert.Same(model.Channels.Single(c => c.Id == "game").Sends.Single(s => s.MixId == "stream"), send.DataContext is BankCellViewModel c2 ? c2.Send : null);
            send.Value = 0.25;
            Assert.Equal(0.25, model.Channels.Single(c => c.Id == "game").Sends.Single(s => s.MixId == "stream").Level);
            // partsOrder lists four parts; the value, left out of it, follows them.
            Assert.True(Part(gameCell, SkinStripParts.Value).IsVisible);
            Assert.True(Part(gameCell, SkinStripParts.Mute).TranslatePoint(default, gameCell)!.Value.Y
                < Part(gameCell, SkinStripParts.Value).TranslatePoint(default, gameCell)!.Value.Y);

            // One Arrange handle per channel, in the rack; none in the banks.
            Arrange(window, true);
            Button[] handles = [.. window.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("reorderHandle") && b.Tag is ChannelViewModel && b.IsEffectivelyVisible)];
            Assert.Equal(model.Channels.Count(c => c.IsEditable), handles.Length);
            Assert.All(handles, h => Assert.Contains(rack, h.GetVisualAncestors()));
            Assert.Equal(handles.Length, handles.Select(h => h.Tag).Distinct().Count());
            Arrange(window, false);

            MixerPopOutWindowTests.CheckBanks(window, model);

            // Send lists in banks one above the other, the master card under each list.
            Wear("list");
            Layout(window, 1040, 1200);
            Assert.False(rack.IsVisible);
            Assert.True(window.FindControl<StackPanel>("HardwarePanel")!.IsVisible);
            Assert.Same(banks, window.MixBanks);
            Border[] lists = Banks(window);
            Assert.Equal(3, lists.Length);
            Assert.True(lists[1].TranslatePoint(default, window)!.Value.Y > lists[0].TranslatePoint(default, window)!.Value.Y);
            AssertNoOverlap(lists);
            Border row = BankCells(window)[0].Single(b => ((BankCellViewModel)b.DataContext!).Channel.Id == "music");
            Slider reversed = row.GetVisualDescendants().OfType<Slider>().Single();
            Assert.Equal(Orientation.Horizontal, reversed.Orientation);
            Assert.True(reversed.IsDirectionReversed);
            Assert.True(Part(row, SkinStripParts.Identity).TranslatePoint(default, row)!.Value.X
                < Part(row, SkinStripParts.Fader).TranslatePoint(default, row)!.Value.X);
            Assert.False(Part(row, SkinStripParts.Value).IsVisible);
            // The icon stands for the name, which a screen reader still hears.
            TextBlock icon = Part(row, SkinStripParts.Identity).GetVisualDescendants().OfType<TextBlock>().Single(t => t.IsVisible);
            Assert.Equal("♫", icon.Text);
            Assert.Equal("Music with a long display name", AutomationProperties.GetName(icon));
            mixEqs = 0;
            foreach (Border list in lists)
            {
                AssertInside(list, Card("SubmixerTile"));
                Border master = list.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "BankMaster");
                AssertInside(master, list);
                mixEqs += MixEqOverChain(master, "in a master card");
            }
            Assert.Equal(1, mixEqs);
            foreach (Border cell in BankCells(window).SelectMany(c => c)) AssertPartsApart(cell);

            // Each bank picks with a row of keys instead of the dropdown: one
            // key per mix, the shown mix's key lit, and a press makes the
            // dropdown's pick, trading places with the bank that showed it.
            Assert.Empty(Pickers(window));
            ToggleButton[][] keys = [.. lists.Select(BankKeys)];
            Assert.All(keys, row => Assert.Equal(BankFixtureMixes, row.Select(k => ((BankChoiceViewModel)k.DataContext!).Mix.Id)));
            Assert.All(keys, row => Assert.Equal(["Monitor A", "Monitor B", "Stream", "Chat", "Aux"], row.Select(k => AutomationProperties.GetName(k))));
            Assert.Equal(Localizer.Format("BankPickerName", 2),
                AutomationProperties.GetName(lists[1].GetVisualDescendants().OfType<ItemsControl>().Single(i => i.Name == "BankKeys")));
            string[] shown = [.. banks.Selection];
            for (int b = 0; b < lists.Length; b++)
                Assert.Equal(BankFixtureMixes.Select(id => id == shown[b]), keys[b].Select(k => k.IsChecked == true));
            foreach (Border list in lists)
                foreach (ToggleButton key in BankKeys(list)) AssertInside(key, list);
            Capture(window, "skin-layout-send-lists-1040");
            PressKey(keys[0].Single(k => ((BankChoiceViewModel)k.DataContext!).Mix.Id == shown[2]));
            Layout(window, 1040, 1200);
            Assert.Equal([shown[2], shown[1], shown[0]], banks.Selection);
            Assert.Equal(banks.Selection, UiSettings.Load().SkinBanksFor("list", 1));
            for (int b = 0; b < lists.Length; b++)
                Assert.Equal(BankFixtureMixes.Select(id => id == banks.Selection[b]), BankKeys(Banks(window)[b]).Select(k => k.IsChecked == true));
            // The lit key stays lit when pressed again, and nothing moves.
            ToggleButton lit = BankKeys(Banks(window)[1]).Single(k => k.IsChecked == true);
            PressKey(lit);
            Layout(window, 1040, 1200);
            Assert.True(lit.IsChecked);
            Assert.Equal([shown[2], shown[1], shown[0]], banks.Selection);

            // Back to the classic templates: the same channel, mix and send objects throughout.
            Wear(SkinPackage.DefaultId);
            Layout(window, 1040, 900);
            Assert.Null(window.MixBanks);
            Assert.Null(window.InputRackModel);
            Assert.False(panel.BankItems.IsVisible);
            Assert.True(window.FindControl<ItemsControl>("ChannelTiles")!.IsEffectivelyVisible);
            Assert.True(window.FindControl<TextBlock>("OutputMixHeading")!.IsEffectivelyVisible);
            Assert.True(window.FindControl<StackPanel>("HardwarePanel")!.IsVisible);
            Assert.Equal(channels, model.Channels);
            Assert.Equal(mixes, model.Mixes);
            Assert.Equal(sends, model.Channels.SelectMany(c => c.Sends));
        }
        finally
        {
            window.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// A strip's shown parts do not overlap one another, and every text and
    /// control in a part stays inside it: a long name shortens rather than
    /// running into the meter beside it.
    /// </summary>
    private static void AssertPartsApart(Border strip)
    {
        StripPanel panel = strip.GetVisualDescendants().OfType<StripPanel>().First();
        Control[] parts = [.. panel.Children.Where(c => c.IsVisible && c.Bounds.Width > 0)];
        AssertNoOverlap(parts);
        foreach (Control part in parts)
        {
            AssertInside(part, strip);
            foreach (Control inner in part.GetVisualDescendants().OfType<Control>()
                         .Where(c => c is TextBlock or Slider or ToggleButton && c.IsEffectivelyVisible && c.Bounds.Width > 0))
                AssertInside(inner, part);
        }
    }

    /// <summary>
    /// A bank master's EQ button stands over its plugin chain's, the order
    /// the signal meets them, as on a microphone. 1 when the master shows an
    /// EQ, else 0.
    /// </summary>
    private static int MixEqOverChain(Border master, string where)
    {
        Button[] buttons = [.. master.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Classes.Contains("fx"))];
        Button? eq = buttons.SingleOrDefault(b => b.Name is "MixEq" or "MixEqCompact");
        if (eq is null) return 0;
        Button chain = Assert.Single(buttons, b => b != eq);
        double Top(Control c) => c.TranslatePoint(default, master)!.Value.Y;
        Assert.True(Top(eq) + eq.Bounds.Height <= Top(chain) + 0.5, $"The mix EQ is not over its plugin chain {where}");
        AssertNoOverlap([eq, chain]);
        return 1;
    }

    /// <summary>The bank tiles in the window, first bank first.</summary>
    internal static Border[] Banks(Window window) =>
        [.. window.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "Bank" && b.IsEffectivelyVisible)];

    /// <summary>The keys of a bank's picker drawn as keys, in the mixer's order.</summary>
    internal static ToggleButton[] BankKeys(Border bank) =>
        [.. bank.GetVisualDescendants().OfType<ToggleButton>().Where(k => k.Name == "BankKey" && k.IsEffectivelyVisible)];

    /// <summary>A press as the key takes one: it flips its own state, then raises Click.</summary>
    private static void PressKey(ToggleButton key)
    {
        key.SetCurrentValue(ToggleButton.IsCheckedProperty, key.IsChecked != true);
        key.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    internal static ComboBox[] Pickers(Window window) =>
        [.. window.GetVisualDescendants().OfType<ComboBox>().Where(b => b.Name == "BankPicker" && b.IsEffectivelyVisible)];

    /// <summary>Each bank's shown cells.</summary>
    internal static Border[][] BankCells(Window window) =>
        [.. Banks(window).Select(bank => bank.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Name == "BankCell" && b.IsEffectivelyVisible).ToArray())];

    private static Border RackStrip(InputRackView rack, string channel) =>
        rack.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Name == "InputStrip" && b.DataContext is InputStripViewModel s && s.Channel.Id == channel);

    /// <summary>The control that stands for a part in a strip.</summary>
    internal static Control Part(Visual strip, string part) =>
        strip.GetVisualDescendants().OfType<Control>().First(c => StripPanel.GetPart(c) == part);

    /// <summary>Every control focus visits from Options onwards, once round.</summary>
    private static Control[] Focusable(MainWindow main)
    {
        var visited = new List<Control>();
        IInputElement? element = main.FindControl<Button>("OptionsButton");
        for (int i = 0; i < 2000 && element is Control control; i++)
        {
            if (visited.Contains(control)) break;
            visited.Add(control);
            element = main.FocusManager!.FindNextElement(NavigationDirection.Next, new FindNextElementOptions { FocusedElement = element });
        }
        return [.. visited];
    }
}
