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
/// The input binding on real windows against a fake daemon: the dialog from
/// each of its entry points (Options, a rack strip's chip, a matrix row's
/// name and the header's link), the keep box sending
/// <c>createCaptureChannel</c> after the binding, a refused save kept open
/// with the daemon's reply, the interface's controls moving between XLR 1 and
/// the capture channel as <c>unitInput</c> moves, the warning line, the
/// header's LED meaning the interface, and the classic INPUTS panel's chip
/// and heading. Run from the window layout test, on its UI thread.
/// </summary>
internal static partial class InputBindingWindowTests
{
    private const string Bluetooth = "bluez_input.headset.monitor-like_name";

    /// <summary>What the fake daemon's mixer holds, changed by the commands it accepts.</summary>
    private sealed class Daemon
    {
        public string? Xlr1Source;
        public int Xlr1Port;
        public bool ProMic;
        public bool Connected = true;
        public int Rejects = 1;

        public JsonNode State()
        {
            string xlr1 = Xlr1Source is null
                ? Xlr("xlr1", 0, Connected ? Input(1) : null)
                : "{\"id\":\"xlr1\",\"name\":\"Mic 1\",\"hardware\":true,\"present\":true,\"levels\":{\"monitor\":1},"
                  + "\"binding\":{\"kind\":\"source\",\"source\":\"" + Xlr1Source + "\",\"pair\":0,\"monoChannel\":" + Xlr1Port + "},"
                  + "\"bindingConnected\":true}";
            // A strip on the interface wins the unit's input; the capture channel carries it only while XLR 1 is bound elsewhere.
            string channels = xlr1 + "," + Xlr("xlr2", 1, Connected ? Input(2) : null)
                + (ProMic ? "," + InputBindingModelTests.ProMic(Connected && Xlr1Source is not null ? Input(1) : null) : "");
            JsonNode state = JsonNode.Parse(ProState(channels, Connected, withMix: true))!;
            JsonArray devices = state["devices"]!.AsArray();
            devices.Add(JsonNode.Parse("""{"name":"mono.mic","description":"Mono microphone","kind":1,"channels":1,"positions":["MONO"]}"""));
            devices.Add(JsonNode.Parse("{\"name\":\"" + Bluetooth + "\",\"description\":\"Bluetooth headset\",\"kind\":1,\"channels\":1}"));
            devices.Add(JsonNode.Parse("""{"name":"OpenXLR_chat","description":"OpenXLR Chat","kind":1,"isOwn":true}"""));
            // Each microphone strip with its EQ, which stands after the chip in the classic panel. The EQ
            // goes first, so the steps below still find a channel's text ending at its binding.
            JsonArray list = state["mixer"]!["channels"]!.AsArray();
            for (int i = 0; i < list.Count; i++)
                if (list[i]!["id"]!.GetValue<string>() is "xlr1" or "xlr2")
                {
                    var withEq = new JsonObject { ["eq"] = JsonNode.Parse("""{"enabled":true,"bands":[],"backend":"software","available":true}""") };
                    foreach ((string key, JsonNode? value) in list[i]!.AsObject()) withEq[key] = value?.DeepClone();
                    list[i] = withEq;
                }
            state["type"] = "state";
            return state;
        }

        /// <summary>Apply a command the way the daemon does, or refuse it; null when accepted.</summary>
        public string? Handle(JsonNode cmd)
        {
            switch (cmd["cmd"]?.GetValue<string>())
            {
                case "setInputBinding":
                    if (Rejects-- > 0) return "Settings could not be saved.";
                    if (cmd["channel"]?.GetValue<string>() != "xlr1") return "unexpected strip";
                    Xlr1Source = cmd["source"]?.GetValue<string>();
                    Xlr1Port = cmd["captureMonoChannel"]?.GetValue<int>() ?? 0;
                    return null;
                case "createCaptureChannel":
                    ProMic = true;
                    return null;
                default:
                    return null;
            }
        }
    }

    internal static void Check()
    {
        Task pending = CheckAll();
        DateTime deadline = DateTime.UtcNow.AddSeconds(90);
        while (!pending.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(pending.IsCompleted, "The input binding window check hung.");
        pending.GetAwaiter().GetResult();
    }

    private static async Task CheckAll()
    {
        var commands = new ConcurrentQueue<JsonNode>();
        var daemon = new Daemon();
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            await SocketTestServer.Send(socket, daemon.State(), stop);
            while (!stop.IsCancellationRequested)
            {
                JsonNode cmd = await SocketTestServer.Receive(socket, stop);
                if (cmd["cmd"]?.GetValue<string>() is not ("setInputBinding" or "createCaptureChannel")) continue;
                commands.Enqueue(cmd);
                string? error = daemon.Handle(cmd);
                await SocketTestServer.Send(socket, daemon.State(), stop);
                await SocketTestServer.Send(socket, new { type = "commandResult", requestId = cmd["requestId"]!.GetValue<string>(), error }, stop);
            }
        });

        var window = new MainWindow(new DaemonClient(server.Url));
        var main = (MainViewModel)window.DataContext!;
        var client = (DaemonClient)typeof(MainWindow).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var options = new OptionsWindow { DataContext = new OptionsViewModel(client, main) };
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
        try
        {
            window.Show();
            await Until(() => main.HasMixer && main.Channels.Count == 2 && main.DeviceConnected);
            ChannelViewModel xlr1 = main.Channels.Single(c => c.Id == "xlr1");
            StatusLed led = window.GetVisualDescendants().OfType<StatusLed>()
                .Single(l => AutomationProperties.GetName(l) == Localizer.Text("InterfaceConnection"));
            Assert.True(led.IsOn);

            // The classic INPUTS panel: XLR 1's chip on the jack, the unit's controls with XLR 1 as always.
            Layout(window, 1040, 900);
            var chipRow = window.FindControl<Grid>("Xlr1Binding")!;
            Assert.True(chipRow.IsEffectivelyVisible);
            Assert.Equal(Localizer.Text("Xlr1Jack"), Chip(chipRow).GetVisualDescendants().OfType<TextBlock>().Single().Text);
            Assert.False(window.FindControl<Grid>("Input1Heading")!.IsEffectivelyVisible);
            Assert.True(window.FindControl<WrapPanel>("InputControls")!.IsEffectivelyVisible);
            foreach (bool touch in new[] { true, false })
            {
                SkinService.ApplyControlSizing(touch);
                foreach (double width in new[] { 640d, 1040 })
                {
                    Layout(window, width, 900);
                    EqEditorWindowTests.CheckClassicEq(window, $"jack, touch {touch} at {width}");
                }
            }
            MainWindowLabelTests.CheckFixedColumnLabels(window, Localizer.Format("StripInputLabel", "XLR 1"),
                Localizer.Format("StripInputLabel", "XLR 2"), Localizer.Text("XLR1Gain"));
            foreach (double width in new[] { 640d, 1040 }) { Layout(window, width, 900); Capture(window, $"binding-classic-jack-{width}"); }

            // From Options: one row per bindable strip, each with its chip.
            options.Show();
            Layout(options, 980, 800);
            ItemsControl rows = options.FindControl<ItemsControl>("InputBindingRows")!;
            Button[] chips = [.. rows.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "Chip")];
            Assert.Equal(2, chips.Length);
            Assert.Equal(Localizer.Format("InputBindingChipName", "XLR 1", Localizer.Text("Xlr1Jack")), AutomationProperties.GetName(chips[0]));
            foreach (Button chip in chips) AssertInside(chip, options);
            options.FindControl<StackPanel>("InputBindings")!.BringIntoView();
            Layout(options, 980, 800);
            Capture(options, "binding-options");
            chips[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Window dialog = await Owned(options);
            Assert.Equal(Localizer.Format("InputBindingTitle", "XLR 1"), dialog.Title);
            ComboBox source = Find<ComboBox>(dialog, "InputBindingSource"), port = Find<ComboBox>(dialog, "InputBindingPort");
            CheckBox keep = Find<CheckBox>(dialog, "KeepUnitMicrophone");
            Button save = Find<Button>(dialog, "ApplyInputBinding");
            // The jack first, then every capture source but OpenXLR's own and the driven interface's.
            Assert.Equal([Localizer.Format("InputBindingInterfaceJack", 1), "Webcam", "Mono microphone", "Bluetooth headset"],
                source.Items.OfType<DeviceChoice>().Select(c => c.Label));
            Assert.Equal(0, source.SelectedIndex);
            Assert.False(port.IsEnabled);
            Assert.False(keep.IsVisible);
            Assert.True(save.IsEnabled);
            source.SelectedIndex = 3;
            Assert.True(Find<TextBlock>(dialog, "InputBindingBluetooth").IsVisible);
            source.SelectedIndex = 2;
            Assert.Equal(["1 (MONO)"], Labels(port));
            Assert.False(Find<TextBlock>(dialog, "InputBindingBluetooth").IsVisible);
            source.SelectedIndex = 1;
            Assert.Equal(["1 (FL)", "2 (FR)"], Labels(port));
            Assert.Equal(0, port.SelectedIndex);   // a new source starts at its first port
            port.SelectedIndex = 1;
            // Replacing a strip with a connected interface behind it offers to keep its microphone, ticked.
            Assert.True(keep.IsVisible);
            Assert.True(keep.IsChecked);
            foreach (double width in new[] { 480d, 360d })
            {
                Layout(dialog, width, 420);
                Assert.Equal(width, dialog.ClientSize.Width);
                foreach (Control control in new Control[] { source, port, keep, save }) AssertInside(control, dialog);
                Capture(dialog, $"binding-dialog-{width}");
            }

            // A refused save stays open with the daemon's reply and sends nothing else.
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Find<TextBlock>(dialog, "InputBindingError").Text?.Contains("could not be saved", StringComparison.Ordinal) == true);
            Assert.True(dialog.IsVisible);
            JsonNode refused = Assert.Single(commands);
            Assert.Equal("setInputBinding", refused["cmd"]!.GetValue<string>());
            Assert.Equal("xlr1", refused["channel"]!.GetValue<string>());
            Assert.Equal("cam", refused["source"]!.GetValue<string>());
            Assert.Equal(1, refused["captureMonoChannel"]!.GetValue<int>());
            Capture(dialog, "binding-dialog-error");

            // Saved: the binding first, then the interface's own microphone as a capture channel at XLR 1's port.
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !dialog.IsVisible);
            JsonNode[] sent = [.. commands];
            Assert.Equal(["setInputBinding", "setInputBinding", "createCaptureChannel"], sent.Select(c => c["cmd"]!.GetValue<string>()));
            JsonNode created = sent[2];
            Assert.Equal(ProNode, created["source"]!.GetValue<string>());
            Assert.Equal(0, created["capturePair"]!.GetValue<int>());
            Assert.Equal(0, created["captureMonoChannel"]!.GetValue<int>());
            Assert.Equal(Localizer.Format("InterfaceInputChannelName", "Wave XLR Pro", 1), created["name"]!.GetValue<string>());
            await Until(() => xlr1.IsSourceBound && main.UnitInput1.Carrier?.Id == "promic");
            Assert.True(main.DeviceConnected);
            Assert.True(led.IsOn);   // still the interface's light
            Layout(options, 980, 800);
            Assert.Equal(Localizer.Format("InputBindingSourcePort", "Webcam", 2),
                Chip(rows).GetVisualDescendants().OfType<TextBlock>().Single().Text);

            // The rack and the matrix: the gain and switches moved from XLR 1 to the capture channel.
            surface.Show();
            foreach (double width in new[] { 640d, 1040 })
            {
                Layout(surface, width, 900);
                Border rackXlr1 = RackStrip(surface, "xlr1"), rackMic = RackStrip(surface, "promic");
                TextBlock rackName = rackXlr1.GetVisualDescendants().OfType<TextBlock>()
                    .Single(t => t.IsEffectivelyVisible && t.Classes.Contains("strip") && t.Text == xlr1.Name);
                Assert.Equal(0, rackName.Bounds.X, .1);
                Assert.DoesNotContain(rackXlr1.GetVisualDescendants().OfType<Slider>(), s => s.IsEffectivelyVisible && s.Name == "RackGain");
                Assert.False(rackXlr1.GetVisualDescendants().OfType<WrapPanel>().Single(p => p.Name == "RackInputControls").IsEffectivelyVisible);
                Assert.Equal(Localizer.Format("InputBindingSourcePort", "Webcam", 2), Chip(rackXlr1).GetVisualDescendants().OfType<TextBlock>().Single().Text);
                Slider unitGain = rackMic.GetVisualDescendants().OfType<Slider>().Single(s => s.Name == "RackUnitGain");
                Assert.True(unitGain.IsEffectivelyVisible);
                Assert.Equal(Localizer.Text("XLR1Gain"), AutomationProperties.GetName(unitGain));
                Assert.Equal(main.GainDb, unitGain.Value);
                Assert.True(rackMic.GetVisualDescendants().OfType<WrapPanel>().Single(p => p.Name == "RackUnitControls").IsEffectivelyVisible);
                Assert.Contains(rackMic.GetVisualDescendants().OfType<Slider>(), s => s.IsEffectivelyVisible && s.DataContext is InputStripViewModel && s != unitGain);
                foreach (Border strip in new[] { rackXlr1, rackMic })
                    foreach (Control c in strip.GetVisualDescendants().OfType<Control>()
                                 .Where(c => c is TextBlock or Slider or Button or ToggleButton && c.IsEffectivelyVisible && c.Bounds.Width > 0))
                        AssertInside(c, strip);
                Border headMic = SkinLayoutWindowTests.RowHead(surface, "promic"), headXlr1 = SkinLayoutWindowTests.RowHead(surface, "xlr1");
                Assert.True(headMic.GetVisualDescendants().OfType<Slider>().Single(s => s.Name == "MatrixUnitGain").IsEffectivelyVisible);
                Assert.Single(headMic.GetVisualDescendants().OfType<ToggleButton>(), t => t.IsEffectivelyVisible && t.Classes.Contains("matrixMute"));
                Assert.DoesNotContain(headXlr1.GetVisualDescendants().OfType<Slider>(), s => s.IsEffectivelyVisible);
                CheckBindingLine(headXlr1, Localizer.Format("InputBindingSourcePort", "Webcam", 2));
                Capture(surface, $"binding-rack-matrix-{width}");
            }
            MicGain(surface).Value = 33;
            Assert.Equal(33, main.GainDb);

            // The source goes offline: the LED goes out and the strip says why, in the rack, Options and the panel.
            ApplyState(main, daemon.State().ToJsonString().Replace("\"bindingConnected\":true}", "\"bindingConnected\":false,\"bindingWarning\":\"Webcam is not connected.\"}", StringComparison.Ordinal));
            Layout(surface, 640, 900);
            Assert.False(xlr1.BindingConnected);
            Border offline = RackStrip(surface, "xlr1");
            Assert.False(Chip(offline).GetVisualDescendants().OfType<StatusLed>().Single().IsOn);
            Assert.Contains(offline.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Webcam is not connected.");
            Assert.Contains("Webcam is not connected.", ToolTip.GetTip(Chip(offline)) as string, StringComparison.Ordinal);
            Assert.Contains("Webcam is not connected.", ToolTip.GetTip(SkinLayoutWindowTests.RowHead(surface, "xlr1")
                .GetVisualDescendants().OfType<Button>().Single(b => b.Name == "MatrixBinding")) as string, StringComparison.Ordinal);
            Layout(options, 980, 800);
            Assert.Contains(rows.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Webcam is not connected.");
            Capture(surface, "binding-rack-offline-640");

            // The classic panel while the capture channel carries the interface's microphone: the chip,
            // the interface's controls under their heading after XLR 1's own processing, the crossfade line.
            foreach (double width in new[] { 640d, 1040 })
            {
                Layout(window, width, 900);
                Grid heading = window.FindControl<Grid>("Input1Heading")!;
                Assert.True(heading.IsEffectivelyVisible);
                Assert.Equal(Localizer.Format("UnitInputPlaysOn", "Elgato Wave XLR Pro", 1, "Pro mic"),
                    heading.GetVisualDescendants().OfType<TextBlock>().First().Text);
                WrapPanel controls = window.FindControl<WrapPanel>("InputControls")!;
                Assert.True(controls.IsEffectivelyVisible);
                Assert.Equal(3, Grid.GetRow(window.FindControl<StackPanel>("Input1Block")!));
                Assert.True(controls.TranslatePoint(default, window)!.Value.Y > window.FindControl<Grid>("Xlr1Binding")!.TranslatePoint(default, window)!.Value.Y);
                TextBlock crossfade = window.FindControl<TextBlock>("CrossfadeNote")!;
                Assert.True(crossfade.IsEffectivelyVisible);
                Assert.Equal(Localizer.Format("CrossfadeMicPlaysOn", "Pro mic"), crossfade.Text);
                AssertInside(crossfade, window);
                EqEditorWindowTests.CheckClassicEq(window, $"webcam at {width}");
                Capture(window, $"binding-classic-elsewhere-{width}");
            }
            // A source with a long description: the chip trims it and the EQ keeps its place after it.
            const string longName = "Webcam With A Description Long Enough To Run Past The Narrow Window's Input Line Twice Over";
            ApplyState(main, daemon.State().ToJsonString().Replace("\"description\":\"Webcam\"", "\"description\":\"" + longName + "\"", StringComparison.Ordinal));
            Assert.StartsWith(longName, xlr1.BindingLabel, StringComparison.Ordinal);
            foreach (bool touch in new[] { false, true })
            {
                SkinService.ApplyControlSizing(touch);
                Layout(window, 640, 900);
                EqEditorWindowTests.CheckClassicEq(window, $"long source, touch {touch} at 640");
                Assert.Contains(window.FindControl<Grid>("Xlr1ChipRow")!.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ChipText")
                    .TextLayout.TextLines, l => l.HasCollapsed);
                Capture(window, $"binding-classic-long-{(touch ? "touch" : "standard")}-640");
            }
            SkinService.ApplyControlSizing(false);
            ApplyState(main, daemon.State().ToJsonString());

            // From a rack strip's chip: back to the interface's jack, which sends no port.
            Chip(RackStrip(surface, "xlr1")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            dialog = await Owned(surface);
            source = Find<ComboBox>(dialog, "InputBindingSource");
            Assert.Equal("cam", ((DeviceChoice)source.SelectedItem!).Name);
            Assert.Equal(1, Find<ComboBox>(dialog, "InputBindingPort").SelectedIndex);   // the saved port, selected again
            Assert.False(Find<CheckBox>(dialog, "KeepUnitMicrophone").IsVisible);   // nothing to keep on a bound strip
            source.SelectedIndex = 0;
            Find<Button>(dialog, "ApplyInputBinding").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !dialog.IsVisible);
            JsonNode cleared = commands.Last();
            Assert.Equal("setInputBinding", cleared["cmd"]!.GetValue<string>());
            Assert.Null(cleared["source"]);
            Assert.Null(cleared["captureMonoChannel"]);
            await Until(() => xlr1.IsUnitBound && xlr1.ShownUnitInput == 1);
            Layout(surface, 640, 900);
            Assert.True(RackStrip(surface, "xlr1").GetVisualDescendants().OfType<Slider>().Single(s => s.Name == "RackGain").IsEffectivelyVisible);
            Assert.False(RackStrip(surface, "promic").GetVisualDescendants().OfType<Slider>().Single(s => s.Name == "RackUnitGain").IsEffectivelyVisible);
            Assert.False(window.FindControl<TextBlock>("CrossfadeNote")!.IsEffectivelyVisible);

            // From a matrix row's name: XLR 2's dialog, which a cancel closes without a command.
            int before = commands.Count;
            SkinLayoutWindowTests.RowHead(surface, "xlr2").GetVisualDescendants().OfType<Button>().Single(b => b.Name == "MatrixBinding")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            dialog = await Owned(surface);
            Assert.Equal(Localizer.Format("InputBindingTitle", "XLR 2"), dialog.Title);
            Assert.Equal(Localizer.Format("InputBindingInterfaceJack", 2), ((DeviceChoice)Find<ComboBox>(dialog, "InputBindingSource").Items[0]!).Label);
            Find<Button>(dialog, "CancelInputBinding").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !dialog.IsVisible);
            Assert.Equal(before, commands.Count);

            // No interface attached and XLR 1 waiting for one: the header's LED is off and its link opens the dialog.
            daemon.Connected = false;
            ApplyState(main, daemon.State().ToJsonString());
            Layout(window, 1040, 900);
            Assert.False(led.IsOn);
            Assert.False(window.FindControl<TextBlock>("DeviceStatus")!.IsEffectivelyVisible);
            Button link = window.FindControl<Button>("ChooseMicrophoneLink")!;
            Assert.True(link.IsEffectivelyVisible);
            AssertInside(link, window);
            Capture(window, "binding-header-1040");
            link.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            dialog = await Owned(window);
            Assert.Equal(Localizer.Format("InputBindingTitle", "XLR 1"), dialog.Title);
            Find<ComboBox>(dialog, "InputBindingSource").SelectedIndex = 1;
            Assert.False(Find<CheckBox>(dialog, "KeepUnitMicrophone").IsChecked == true && main.DeviceConnected);
            Find<Button>(dialog, "CancelInputBinding").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !dialog.IsVisible);
            // A USB microphone driven: its input is a built-in capsule, which the dialog does not call a jack.
            daemon.Connected = true;
            ApplyState(main, daemon.State().ToJsonString()
                .Replace("\"xlrInputs\":2", "\"xlrInputs\":1,\"xlrJacks\":false", StringComparison.Ordinal)
                .Replace("\"name\":\"XLR 1\"", "\"name\":\"Mic 1\"", StringComparison.Ordinal));
            Layout(options, 980, 800);
            Assert.Equal(Localizer.Text("UnitCapsule"), Chip(rows).GetVisualDescendants().OfType<TextBlock>().Single().Text);
            Chip(rows).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            dialog = await Owned(options);
            Assert.Equal(Localizer.Format("InputBindingTitle", "Mic 1"), dialog.Title);
            Assert.Equal(Localizer.Format("InputBindingUnitCapsule", main.DeviceName),
                ((DeviceChoice)Find<ComboBox>(dialog, "InputBindingSource").Items[0]!).Label);
            Find<Button>(dialog, "CancelInputBinding").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !dialog.IsVisible);
            Layout(window, 1040, 900);
            Assert.Equal(Localizer.Format("StripInputLabel", "Mic 1"),
                window.FindControl<Grid>("Xlr1Binding")!.GetVisualDescendants().OfType<TextBlock>().First().Text);
            Capture(window, "binding-classic-capsule-1040");
            daemon.Connected = false;

            // XLR 1 bound elsewhere: the plain status again.
            ApplyState(main, daemon.State().ToJsonString().Replace(
                "\"binding\":{\"kind\":\"unit\",\"pair\":0}", "\"binding\":{\"kind\":\"source\",\"source\":\"cam\",\"pair\":0,\"monoChannel\":0}", StringComparison.Ordinal));
            Layout(window, 1040, 900);
            Assert.False(link.IsEffectivelyVisible);
            Assert.True(window.FindControl<TextBlock>("DeviceStatus")!.IsEffectivelyVisible);
        }
        finally
        {
            SkinService.ApplyControlSizing(false);
            foreach (Window owner in new[] { options, surface, window })
                foreach (Window owned in owner.OwnedWindows.ToArray()) owned.Close();
            surface.Close();
            options.Close();
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }

        static Slider MicGain(Window surface) =>
            RackStrip(surface, "promic").GetVisualDescendants().OfType<Slider>().Single(s => s.Name == "RackUnitGain");
    }

    /// <summary>An XLR row's binding line in the matrix: inside its name column, lit and trimmed, under the name.</summary>
    internal static void CheckBindingLine(Border head, string label)
    {
        Border name = head.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "MatrixRowName");
        Grid line = head.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "MatrixBindingLine");
        Button target = head.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "MatrixBinding");
        Assert.True(line.IsEffectivelyVisible);
        Assert.True(target.IsEffectivelyVisible);
        Assert.Equal(label, line.GetVisualDescendants().OfType<TextBlock>().Single().Text);
        foreach (Control part in new Control[] { line, target })
        {
            Point at = part.TranslatePoint(default, head)!.Value;
            Assert.True(at.Y >= -0.5 && at.Y + part.Bounds.Height <= head.Bounds.Height + 0.5,
                $"{part.Name} runs from {at.Y} to {at.Y + part.Bounds.Height} in a head {head.Bounds.Height} tall");
            AssertInside(part, name);
        }
    }

    private static Border RackStrip(Visual root, string channel) =>
        root.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Name == "InputStrip" && b.DataContext is InputStripViewModel s && s.Channel.Id == channel);

    /// <summary>The first binding chip's button under a control.</summary>
    private static Button Chip(Visual root) =>
        root.GetVisualDescendants().OfType<BindingChip>().First().GetVisualDescendants().OfType<Button>().Single(b => b.Name == "Chip");

    private static void ApplyState(MainViewModel model, string state)
    {
        typeof(MainViewModel).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, [JsonNode.Parse(state)!]);
        Dispatcher.UIThread.RunJobs();
    }

    private static string[] Labels(ComboBox ports) => [.. ports.Items.OfType<InputBindingDialog.PortChoice>().Select(p => p.Label)];

    private static T Find<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    /// <summary>The dialog a window opened, once it is shown.</summary>
    private static async Task<Window> Owned(Window owner)
    {
        await Until(() => owner.OwnedWindows.Any(w => w.IsVisible));
        Window dialog = owner.OwnedWindows.Single(w => w.IsVisible);
        Layout(dialog, 480, 420);
        return dialog;
    }

    private static async Task Until(Func<bool> ready)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.True(ready());
    }
}
