using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;
using OpenXLR.UI.Views;
using static OpenXLR.Tests.InputBindingModelTests;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

/// <summary>
/// A strip bound to another supported unit, on real windows against a fake
/// daemon: the dialog lists the unit's node and offers to drive it, the save
/// sends the binding, the kept capture channel and the drive in that order,
/// and once the daemon names the unit in the strip's <c>unitInput</c> the
/// strip draws that unit's gain and switches in the rack, the matrix and the
/// classic INPUTS panel, with the same view the additional interfaces window
/// draws. The controls write <c>setWaveControl</c> to that unit and go when
/// it is no longer driven; the chip follows the unit's renamed node.
/// </summary>
internal static partial class InputBindingWindowTests
{
    private const string Wave3 = "0fd9:0070@00000000000000c3";
    private const string Wave3Node = "alsa_input.usb-Elgato_Systems_Elgato_Wave_3_W3SER-00.mono-fallback";
    private const string Wave3Renamed = "alsa_input.usb-Elgato_Systems_Elgato_Wave_3_W3SER-00.analog-stereo";
    private const string Wave3Name = "Elgato Wave:3 (1-4)";

    /// <summary>A Wave XLR Pro primary with a Wave:3 attached beside it, changed by the commands it accepts.</summary>
    private sealed class DrivenDaemon
    {
        public bool Bound, Kept, Enabled, Renamed, CaptureCarried, OldCaptureUnit, WithMix = true;
        public int Gain = 25;
        public bool Mute;

        public JsonNode State()
        {
            string xlr2 = !Bound ? Xlr("xlr2", 1, Input(2))
                : "{\"id\":\"xlr2\",\"name\":\"Mic 2\",\"hardware\":true,\"present\":true,\"levels\":{\"monitor\":1},"
                  + "\"binding\":{\"kind\":\"source\",\"source\":\"" + Wave3Node + "\",\"pair\":0,\"monoChannel\":0,\"unit\":\"" + Wave3 + "\"},"
                  + "\"bindingConnected\":true"
                  + (Enabled ? ",\"unitInput\":{\"unit\":\"" + Wave3 + "\",\"input\":1,\"primary\":false}" : "") + "}";
            // The Pro's second microphone, kept as a capture channel, carries the Pro's input 2.
            string kept = "{\"id\":\"promic2\",\"name\":\"Pro mic 2\",\"levels\":{\"monitor\":1},\"captureSource\":\"" + ProNode + "\","
                + "\"capturePair\":0,\"captureMonoChannel\":2,\"captureConnected\":true,\"volume\":0.8,"
                + "\"binding\":{\"kind\":\"source\",\"source\":\"" + ProNode + "\",\"pair\":0,\"monoChannel\":2},\"bindingConnected\":true,"
                + "\"unitInput\":" + Input(2) + "}";
            string channels = Xlr("xlr1", 0, Input(1)) + "," + xlr2 + (Kept ? "," + kept : "");
            JsonNode state = JsonNode.Parse(ProState(channels, withMix: WithMix))!;
            state["devices"]!.AsArray().Add(JsonNode.Parse("{\"name\":\"" + (Renamed ? Wave3Renamed : Wave3Node) + "\",\"description\":\""
                + (Renamed ? "Elgato Wave:3 Analog Stereo" : "Elgato Wave:3 Mono") + "\",\"kind\":1,\"channels\":"
                + (Renamed ? "2,\"positions\":[\"FL\",\"FR\"]" : "1,\"positions\":[\"MONO\"]") + ",\"drivenUnit\":\"" + Wave3 + "\"}"));
            state["detected"]!.AsArray().Add(JsonNode.Parse("{\"usbId\":\"" + Wave3 + "\",\"name\":\"Wave:3\",\"active\":false}"));
            state["waveInterfaces"]!.AsArray().Add(JsonNode.Parse("{\"id\":\"" + Wave3 + "\",\"name\":\"" + Wave3Name + "\",\"active\":false,"
                + "\"enabled\":" + (Enabled ? "true" : "false") + ",\"connected\":" + (Enabled ? "true" : "false") + ","
                + "\"captureHint\":\"Wave_3_W3SER-\",\"captureModelHint\":\"\","
                + "\"capabilities\":{\"gain\":true,\"mute\":true,\"clipGuard\":true,\"xlrInputs\":1,\"xlrJacks\":false,\"gainMaxDb\":40},"
                + (Enabled ? "\"state\":{\"gainDb\":" + Gain + ",\"mute\":" + (Mute ? "true" : "false") + ",\"clipGuard\":true}" : "\"state\":null") + "}"));
            if (CaptureCarried)
            {
                JsonArray list = state["mixer"]!["channels"]!.AsArray();
                JsonNode strip = list.Single(c => c!["id"]!.GetValue<string>() == "xlr2")!;
                strip["unitInput"] = null;
                strip["binding"]!["unit"] = null;
                strip["binding"]!["source"] = "webcam";
                list.Add(JsonNode.Parse($$$"""
                    {"id":"wave3mic","name":"Wave:3 microphone","captureSource":"{{{Wave3Node}}}",
                     "captureMonoChannel":0,"capturePair":0,"captureConnected":true,"bindingConnected":true,
                     "binding":{"kind":"source","source":"{{{Wave3Node}}}","pair":0,"monoChannel":0,"unit":"{{{Wave3}}}"},
                     "volume":0.8,"microphone":true,"chainWidth":1,"levels":{"monitor":1}}
                    """));
                if (OldCaptureUnit) list.Last()!["binding"]!["unit"] = null;
                if (Enabled) list.Last()!["unitInput"] = JsonNode.Parse($$"""{"unit":"{{Wave3}}","input":1,"primary":false}""");
            }
            state["type"] = "state";
            return state;
        }

        public void Handle(JsonNode cmd)
        {
            switch (cmd["cmd"]?.GetValue<string>())
            {
                case "setInputBinding": Bound = cmd["source"]?.GetValue<string>() == Wave3Node; break;
                case "createCaptureChannel": Kept = true; break;
                case "setWaveInterfaceEnabled":
                    Enabled = cmd["value"]!.GetValue<bool>();
                    if (!Enabled && cmd["keepChannels"]?.GetValue<bool>() != true) CaptureCarried = false;
                    break;
                case "setWaveControl" when cmd["control"]?.GetValue<string>() == "gain": Gain = cmd["value"]!.GetValue<int>(); break;
                case "setWaveControl" when cmd["control"]?.GetValue<string>() == "mute": Mute = cmd["value"]!.GetValue<bool>(); break;
            }
        }
    }

    /// <summary>The state with XLR 2 bound to the Wave:3, driven as an additional interface.</summary>
    internal static string DrivenState(bool enabled = true, bool withMix = true, bool captureCarried = false)
        => new DrivenDaemon { Bound = true, Kept = true, Enabled = enabled, WithMix = withMix, CaptureCarried = captureCarried }.State().ToJsonString();

    internal static void CheckDrivenUnit()
    {
        Task pending = CheckDriven();
        DateTime deadline = DateTime.UtcNow.AddSeconds(90);
        while (!pending.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(pending.IsCompleted, "The driven unit window check hung.");
        pending.GetAwaiter().GetResult();
    }

    private static async Task CheckDriven()
    {
        var commands = new ConcurrentQueue<JsonNode>();
        var daemon = new DrivenDaemon();
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            await SocketTestServer.Send(socket, daemon.State(), stop);
            while (!stop.IsCancellationRequested)
            {
                JsonNode cmd = await SocketTestServer.Receive(socket, stop);
                if (cmd["cmd"]?.GetValue<string>() is not ("setInputBinding" or "createCaptureChannel" or "setWaveInterfaceEnabled" or "setWaveControl" or "set")) continue;
                commands.Enqueue(cmd);
                daemon.Handle(cmd);
                await SocketTestServer.Send(socket, daemon.State(), stop);
                // A switch's write is not a request, so it waits for no answer.
                if (cmd["requestId"] is { } requestId)
                    await SocketTestServer.Send(socket, new { type = "commandResult", requestId = requestId.GetValue<string>(), error = (string?)null }, stop);
            }
        });

        var window = new MainWindow(new DaemonClient(server.Url));
        var main = (MainViewModel)window.DataContext!;
        using var rack = new InputRackViewModel(main);
        using var matrix = new MixMatrixViewModel(main);
        var surface = new Window
        {
            Width = 640, Height = 900,
            Content = new ScrollViewer { Content = new StackPanel { Spacing = 12, Children =
            {
                new InputRackView { DataContext = rack }, new MixMatrixView { DataContext = matrix },
            } } },
        };
        var units = new WaveInterfacesWindow(main);
        try
        {
            window.Show();
            surface.Show();
            await Until(() => main.HasMixer && main.Channels.Count == 2 && main.WaveInterfaces.Count == 2);
            ChannelViewModel xlr2 = main.Channels.Single(c => c.Id == "xlr2");
            Assert.False(xlr2.CarriesExtraUnit);

            // From XLR 2's chip: the Wave:3's node is listed, the Pro's own is its jack.
            Layout(surface, 640, 900);
            Chip(RackStrip(surface, "xlr2")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Window dialog = await Owned(surface);
            ComboBox source = Find<ComboBox>(dialog, "InputBindingSource");
            CheckBox drive = Find<CheckBox>(dialog, "DriveInterface"), keep = Find<CheckBox>(dialog, "KeepUnitMicrophone");
            Assert.Equal([Localizer.Format("InputBindingInterfaceJack", 2), "Webcam", "Elgato Wave:3 Mono"],
                source.Items.OfType<DeviceChoice>().Select(c => c.Label));
            Assert.False(drive.IsVisible);
            source.SelectedIndex = 1;
            Assert.False(drive.IsVisible);   // a webcam is nothing to drive
            source.SelectedIndex = 2;
            // An attached unit that is not driven: offered, ticked.
            Assert.True(drive.IsVisible);
            Assert.True(drive.IsChecked);
            Assert.True(keep.IsVisible);
            Assert.Equal(["1 (MONO)"], Labels(Find<ComboBox>(dialog, "InputBindingPort")));
            foreach (double width in new[] { 480d, 360d })
            {
                Layout(dialog, width, 420);
                foreach (Control control in new Control[] { source, keep, drive, Find<Button>(dialog, "ApplyInputBinding") })
                    AssertInside(control, dialog);
                Capture(dialog, $"driven-dialog-offer-{width}");
            }

            // Saved: the binding, the Pro's second microphone kept, then the drive.
            Find<Button>(dialog, "ApplyInputBinding").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !dialog.IsVisible);
            JsonNode[] sent = [.. commands];
            Assert.Equal(["setInputBinding", "createCaptureChannel", "setWaveInterfaceEnabled"], sent.Select(c => c["cmd"]!.GetValue<string>()));
            Assert.Equal("xlr2", sent[0]["channel"]!.GetValue<string>());
            Assert.Equal(Wave3Node, sent[0]["source"]!.GetValue<string>());
            Assert.Equal(2, sent[1]["captureMonoChannel"]!.GetValue<int>());
            Assert.Equal(Wave3, sent[2]["device"]!.GetValue<string>());
            Assert.True(sent[2]["value"]!.GetValue<bool>());

            // Driven, and the daemon names it on the strip: the Wave:3's controls are there.
            await Until(() => xlr2.CarriesExtraUnit);
            Assert.Equal(0, xlr2.ShownUnitInput);
            Assert.Same(main.WaveInterfaces.Single(w => w.Id == Wave3).Input1, xlr2.ExtraUnitInput);
            Assert.Equal("Elgato Wave:3 Mono", xlr2.BindingLabel);
            foreach (double width in new[] { 640d, 1040, 2400 })
            {
                Layout(surface, width, 900);
                Border strip = RackStrip(surface, "xlr2");
                Slider gain = WaveGain(strip);
                Assert.True(gain.IsEffectivelyVisible);
                Assert.Equal(25, gain.Value);
                Assert.Equal(Localizer.Format("UnitGainName", Wave3Name), AutomationProperties.GetName(gain));
                Assert.True(strip.GetVisualDescendants().OfType<WaveUnitControls>().Single(c => c.Name == "RackExtraSwitches").IsEffectivelyVisible);
                Assert.True(WaveMute(strip).IsEffectivelyVisible);
                // The Pro's own input 2 is not drawn here; it plays on the kept channel.
                Assert.DoesNotContain(strip.GetVisualDescendants().OfType<Slider>(), s => s.IsEffectivelyVisible && s.DataContext is InputStripViewModel);
                foreach (Control c in strip.GetVisualDescendants().OfType<Control>()
                             .Where(c => c is TextBlock or Slider or Button or ToggleButton && c.IsEffectivelyVisible && c.Bounds.Width > 0))
                    AssertInside(c, strip);
                Border head = SkinLayoutWindowTests.RowHead(surface, "xlr2");
                Slider headGain = head.GetVisualDescendants().OfType<Slider>().Single(s => s.Name == "MatrixExtraGain");
                Assert.True(headGain.IsEffectivelyVisible);
                Assert.Equal(25, headGain.Value);
                ToggleButton headMute = Assert.Single(head.GetVisualDescendants().OfType<ToggleButton>(), t => t.IsEffectivelyVisible && t.Classes.Contains("matrixMute"));
                Assert.Equal("MatrixExtraMute", headMute.Name);
                AssertInside(headGain, head);
                AssertInside(headMute, head);
                foreach (MatrixRowViewModel row in matrix.Rows)
                    SkinLayoutWindowTests.CheckProcessingPlace(SkinLayoutWindowTests.RowHead(surface, row.Channel.Id), row, $"driven at {width}");
                Capture(surface, $"driven-rack-matrix-{width}");
            }

            // Each row's microphone switches: XLR 1 the Pro's first input's, the
            // kept channel the Pro's second input's, as the classic panel shows
            // them, and XLR 2 the Wave:3's, as the rack shows them, less the mute
            // the head draws. A switch there sends what the classic one sends.
            Layout(surface, 1040, 900);
            Layout(window, 1040, 900);
            foreach ((string row, string classic) in new[] { ("xlr1", "InputControls"), ("promic2", "Input2Controls") })
            {
                Button processing = SkinLayoutWindowTests.ProcessingButton(surface, row)!;
                // The phantom power lamp is the unit's, drawn while its phantom power is on.
                Assert.Equal(matrix.Rows.Single(r => r.Channel.Id == row).PhantomOn,
                    processing.GetVisualDescendants().OfType<StatusLed>().Single().IsEffectivelyVisible);
                ToggleButton[] switches = SkinLayoutWindowTests.OpenSwitches(surface, processing);
                Assert.Equal(SkinLayoutWindowTests.ClassicSwitches(window, classic), switches.Select(SkinLayoutWindowTests.Lettering));
                Assert.Equal(6, switches.Length);
                if (row == "promic2") Capture(surface, "driven-processing-kept-channel", SkinLayoutWindowTests.FlyoutPanel(processing));
                processing.Flyout!.Hide();
            }
            Button wave3 = SkinLayoutWindowTests.ProcessingButton(surface, "xlr2")!;
            // The Wave:3 has no phantom power, so no lamp.
            Assert.False(wave3.GetVisualDescendants().OfType<StatusLed>().Single().IsEffectivelyVisible);
            ToggleButton[] unitSwitches = SkinLayoutWindowTests.OpenSwitches(surface, wave3);
            WaveUnitControls rackSwitches = RackStrip(surface, "xlr2").GetVisualDescendants().OfType<WaveUnitControls>().Single(c => c.Name == "RackExtraSwitches");
            Assert.Equal(rackSwitches.GetVisualDescendants().OfType<ToggleButton>().Where(t => t.IsEffectivelyVisible && t.Name != "WaveUnitMute")
                .Select(SkinLayoutWindowTests.Lettering), unitSwitches.Select(SkinLayoutWindowTests.Lettering));
            Assert.Equal([Localizer.Text("ClipGuard")], unitSwitches.Select(SkinLayoutWindowTests.Lettering));
            Capture(surface, "driven-processing-additional-unit", SkinLayoutWindowTests.FlyoutPanel(wave3));

            async Task<JsonNode> Written(Func<JsonNode, bool> match, int before)
            {
                await Until(() => commands.Count(match) > before);
                return commands.Last(match);
            }
            static bool Set(JsonNode c) => c["cmd"]!.GetValue<string>() == "set";
            static bool WaveSet(JsonNode c) => c["cmd"]!.GetValue<string>() == "setWaveControl" && c["control"]!.GetValue<string>() == "clipGuard";
            unitSwitches.Single().IsChecked = !unitSwitches.Single().IsChecked;
            JsonNode fromFlyout = await Written(WaveSet, 0);
            wave3.Flyout!.Hide();
            await Until(() => main.WaveInterfaces.Single(w => w.Id == Wave3).ClipGuard);
            ToggleButton rackClipGuard = rackSwitches.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.IsEffectivelyVisible && t.Name != "WaveUnitMute");
            rackClipGuard.IsChecked = !rackClipGuard.IsChecked;
            JsonNode fromRack = await Written(WaveSet, 1);
            Assert.Equal(Wave3, fromFlyout["device"]!.GetValue<string>());
            Assert.Equal(Without(fromRack), Without(fromFlyout));

            Button kept = SkinLayoutWindowTests.ProcessingButton(surface, "promic2")!;
            ToggleButton keptLowCut = SkinLayoutWindowTests.OpenSwitches(surface, kept).Single(t => SkinLayoutWindowTests.Lettering(t) == Localizer.Text("LowCut"));
            keptLowCut.IsChecked = true;
            JsonNode keptWrite = await Written(Set, 0);
            kept.Flyout!.Hide();
            // The classic button shows it on; off and on again, it writes the same.
            ToggleButton classicLowCut = window.FindControl<WrapPanel>("Input2Controls")!.Children.OfType<ToggleButton>()
                .Single(t => SkinLayoutWindowTests.Lettering(t) == Localizer.Text("LowCut"));
            Assert.True(classicLowCut.IsChecked);
            classicLowCut.IsChecked = false;
            await Written(Set, 1);
            classicLowCut.IsChecked = true;
            JsonNode classicWrite = await Written(Set, 2);
            Assert.Equal("lowCut2", keptWrite["control"]!.GetValue<string>());
            Assert.Equal(Without(classicWrite), Without(keptWrite));

            // The controls write to the Wave:3, not to the primary.
            Layout(surface, 640, 900);
            WaveGain(RackStrip(surface, "xlr2")).Value = 31;
            await Until(() => commands.Any(c => c["cmd"]!.GetValue<string>() == "setWaveControl" && c["control"]!.GetValue<string>() == "gain"));
            JsonNode written = commands.Last(c => c["cmd"]!.GetValue<string>() == "setWaveControl");
            Assert.Equal(Wave3, written["device"]!.GetValue<string>());
            Assert.Equal(31, written["value"]!.GetValue<int>());
            WaveMute(RackStrip(surface, "xlr2")).IsChecked = true;
            await Until(() => commands.Any(c => c["cmd"]!.GetValue<string>() == "setWaveControl" && c["control"]!.GetValue<string>() == "mute"));
            await Until(() => main.WaveInterfaces.Single(w => w.Id == Wave3).Mute);
            await Until(() => SkinLayoutWindowTests.RowHead(surface, "xlr2").GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == "MatrixExtraMute").IsChecked == true);

            // The classic INPUTS panel: the Wave:3 under XLR 2's chip, the Pro's input 2 under its heading.
            foreach (double width in new[] { 640d, 1040 })
            {
                Layout(window, width, 900);
                Grid extra = window.FindControl<Grid>("Xlr2ExtraUnit")!;
                Assert.True(extra.IsEffectivelyVisible);
                Assert.Equal(31, WaveGain(extra).Value);
                AssertInside(WaveGain(extra), window);
                Assert.True(window.FindControl<Grid>("Input2Heading")!.IsEffectivelyVisible);
                Assert.Equal(Localizer.Format("UnitInputPlaysOn", "Elgato Wave XLR Pro", 2, "Pro mic 2"),
                    window.FindControl<Grid>("Input2Heading")!.GetVisualDescendants().OfType<TextBlock>().First().Text);
                Capture(window, $"driven-classic-{width}");
            }

            // The additional interfaces window draws the same view for the unit.
            units.Show();
            Layout(units, 680, 620);
            WaveUnitControls shared = units.GetVisualDescendants().OfType<WaveUnitControls>()
                .Single(c => c.DataContext is WaveUnitInputViewModel { Unit.Id: Wave3 });
            Assert.True(shared.IsEffectivelyVisible);
            Assert.Equal(31, WaveGain(shared).Value);
            Assert.True(WaveMute(shared).IsChecked);
            Capture(units, "driven-additional-interfaces");

            // A profile switch renames the node: the chip names what plays now.
            daemon.Renamed = true;
            ApplyState(main, daemon.State().ToJsonString());
            Assert.Equal(Localizer.Format("InputBindingSourcePort", "Elgato Wave:3 Analog Stereo", 1), xlr2.BindingLabel);
            Assert.True(xlr2.CarriesExtraUnit);

            // No longer driven: the binding stays, the controls go.
            daemon.Enabled = false;
            daemon.Renamed = false;
            ApplyState(main, daemon.State().ToJsonString());
            Layout(surface, 640, 900);
            Assert.False(xlr2.CarriesExtraUnit);
            Assert.True(xlr2.IsSourceBound);
            Assert.DoesNotContain(RackStrip(surface, "xlr2").GetVisualDescendants().OfType<WaveUnitControls>(), c => c.IsEffectivelyVisible);
            Assert.DoesNotContain(SkinLayoutWindowTests.RowHead(surface, "xlr2").GetVisualDescendants().OfType<Slider>(), s => s.IsEffectivelyVisible);
            Assert.Equal("Elgato Wave:3 Mono", Chip(RackStrip(surface, "xlr2")).GetVisualDescendants().OfType<TextBlock>().Single().Text);

            // A capture carrier gets the same controls in classic INPUTS, under its own name.
            daemon.Enabled = true;
            daemon.CaptureCarried = true;
            ApplyState(main, daemon.State().ToJsonString());
            ChannelViewModel capture = main.Channels.Single(c => c.Id == "wave3mic");
            StackPanel CaptureBlock() => window.GetVisualDescendants().OfType<StackPanel>()
                .Single(p => p.Name == "CaptureInputBlock" && p.DataContext == capture);
            foreach (double width in new[] { 640d, 1040 })
            {
                Layout(window, width, 1100);
                StackPanel block = CaptureBlock();
                Assert.True(block.IsEffectivelyVisible);
                Assert.True(WaveGain(block).IsEffectivelyVisible);
                Assert.Equal(31, WaveGain(block).Value);
                Assert.True(WaveMute(block).IsChecked);
                AssertInside(WaveGain(block), window);
                Assert.Contains(block.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Wave3Name && t.IsEffectivelyVisible);
                Assert.False(window.FindControl<Grid>("Xlr2ExtraUnit")!.IsEffectivelyVisible);
                Capture(window, $"driven-classic-capture-{width}");
            }
            WaveGain(CaptureBlock()).Value = 37;
            await Until(() => commands.Any(c => c["cmd"]!.GetValue<string>() == "setWaveControl"
                && c["control"]!.GetValue<string>() == "gain" && c["value"]!.GetValue<int>() == 37));
            Assert.Equal(Wave3, commands.Last(c => c["cmd"]!.GetValue<string>() == "setWaveControl")["device"]!.GetValue<string>());
            // A profile switch renames the node. The mute's reply is the last
            // state the daemon sends, so the window ends on the new name.
            daemon.Renamed = true;
            WaveMute(CaptureBlock()).IsChecked = false;
            await Until(() => !main.WaveInterfaces.Single(w => w.Id == Wave3).Mute);
            await Until(() => capture.BindingLabel == Localizer.Format("InputBindingSourcePort", "Elgato Wave:3 Analog Stereo", 1));
            Assert.True(capture.CarriesExtraUnit);

            // Unticking asks before any command. Cancel and window close keep the unit enabled.
            CheckBox EnableBox() => units.GetVisualDescendants().OfType<CheckBox>()
                .Single(c => c.DataContext is WaveInterfaceViewModel { Id: Wave3 });
            async Task<Window> Untick()
            {
                CheckBox box = EnableBox();
                box.IsChecked = false;
                box.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => units.OwnedWindows.Any());
                Window confirmation = units.OwnedWindows.Single();
                Assert.False(confirmation.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked == true);
                return confirmation;
            }
            int beforeDisable = commands.Count;
            Window cancelDisable = await Untick();
            Assert.Equal(beforeDisable, commands.Count);
            cancelDisable.GetVisualDescendants().OfType<Button>().Single(b => b.IsCancel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => EnableBox().IsChecked == true && main.WaveInterfaces.Single(w => w.Id == Wave3).CanEnable);
            Assert.Equal(beforeDisable, commands.Count);
            Window closeDisable = await Untick();
            closeDisable.Close();
            await Until(() => EnableBox().IsChecked == true && main.WaveInterfaces.Single(w => w.Id == Wave3).CanEnable);
            Assert.Equal(beforeDisable, commands.Count);

            Window keepDisable = await Untick();
            keepDisable.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked = true;
            Capture(keepDisable, "driven-keep-capture-confirmation");
            keepDisable.GetVisualDescendants().OfType<Button>().Single(b => b.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !main.WaveInterfaces.Single(w => w.Id == Wave3).Enabled && !capture.CarriesExtraUnit);
            JsonNode keptCommand = commands.Last(c => c["cmd"]!.GetValue<string>() == "setWaveInterfaceEnabled");
            Assert.True(keptCommand["keepChannels"]!.GetValue<bool>());
            Assert.Contains(capture, main.CaptureInputs);
            Layout(window, 640, 1100);
            Assert.DoesNotContain(CaptureBlock().GetVisualDescendants().OfType<WaveUnitControls>(), c => c.IsEffectivelyVisible);

            // The default choice sends false and removes the capture input after acknowledgement.
            daemon.Enabled = true;
            ApplyState(main, daemon.State().ToJsonString());
            Window deleteDisable = await Untick();
            deleteDisable.GetVisualDescendants().OfType<Button>().Single(b => b.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !main.Channels.Any(c => c.Id == "wave3mic"));
            JsonNode deletedCommand = commands.Last(c => c["cmd"]!.GetValue<string>() == "setWaveInterfaceEnabled");
            Assert.False(deletedCommand["keepChannels"]!.GetValue<bool>());

            // An older daemon cannot honor keepChannels. It gets a deletion confirmation only.
            daemon.Enabled = true;
            daemon.CaptureCarried = true;
            daemon.OldCaptureUnit = true;
            ApplyState(main, daemon.State().ToJsonString());
            Window oldDisable = await Untick();
            Assert.False(oldDisable.GetVisualDescendants().OfType<CheckBox>().Single().IsEffectivelyVisible);
            Assert.Contains(oldDisable.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Localizer.Format("DisableInterfaceCaptureDeleteDetail", Wave3Name));
            oldDisable.GetVisualDescendants().OfType<Button>().Single(b => b.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !main.Channels.Any(c => c.Id == "wave3mic"));
            Assert.False(commands.Last(c => c["cmd"]!.GetValue<string>() == "setWaveInterfaceEnabled")["keepChannels"]!.GetValue<bool>());
        }
        finally
        {
            foreach (Window owner in new[] { surface, window })
                foreach (Window owned in owner.OwnedWindows.ToArray()) owned.Close();
            units.Close();
            surface.Close();
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }

        // A command as the daemon reads it, without the id each request carries.
        static string Without(JsonNode command)
        {
            JsonObject copy = command.DeepClone().AsObject();
            copy.Remove("requestId");
            return copy.ToJsonString();
        }

        // The gain of the view that draws it, and the mute of the view that draws the switches.
        static Slider WaveGain(Visual root) => Views(root, WaveUnitControlsPart.Switches).GetVisualDescendants().OfType<Slider>().Single(s => s.Name == "WaveUnitGain");
        static ToggleButton WaveMute(Visual root) => Views(root, WaveUnitControlsPart.Gain).GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == "WaveUnitMute");
        static WaveUnitControls Views(Visual root, WaveUnitControlsPart not) => root is WaveUnitControls self ? self
            : root.GetVisualDescendants().OfType<WaveUnitControls>().First(c => c.Part != not);
    }
}
