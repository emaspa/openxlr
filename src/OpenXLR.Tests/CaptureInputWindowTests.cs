using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
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
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

/// <summary>Capture chain commands and compatibility, in their own process to keep the layout run short.</summary>
[Collection("xdg-config")]
public sealed class CaptureInputWindowTests
{
    internal static JsonNode State()
    {
        JsonNode state = JsonNode.Parse(InputBindingModelTests.ProState(
            InputBindingModelTests.Xlr("xlr1", 0, InputBindingModelTests.Input(1)) + "," +
            InputBindingModelTests.Xlr("xlr2", 1, InputBindingModelTests.Input(2)) + """
            ,{"id":"aux","name":"Aux In","hardware":true,"volume":1,"levels":{"monitor":1}},
            {"id":"guest","name":"Guest microphone with a long name","captureSource":"usb.guest","captureMonoChannel":0,
             "captureConnected":true,"microphone":true,"chainWidth":1,"volume":0.8,"levels":{"monitor":1}},
            {"id":"camera","name":"Stereo capture card","captureSource":"usb.camera","capturePair":1,
             "captureConnected":true,"microphone":false,"chainWidth":2,"volume":0.6,"levels":{"monitor":1}},
            {"id":"offline","name":"Disconnected microphone","captureSource":"usb.missing","captureMonoChannel":2,
             "captureConnected":false,"microphone":true,"chainWidth":1,"volume":1,"levels":{"monitor":1},
             "bindingWarning":"The selected microphone is not connected."}
            """, withMix: true))!;
        state["type"] = "state";
        state["daemonVersion"] = AppVersion.Current;
        state["capabilities"]!["auxInput"] = true;
        state["devices"]!.AsArray().Add(JsonNode.Parse("""{"name":"usb.guest","description":"USB microphone with a long source description that must fit beside its connection light","kind":1,"channels":1}"""));
        // The daemon reports an EQ on every capture input, the two mono microphones and the stereo pair.
        foreach (JsonNode? channel in state["mixer"]!["channels"]!.AsArray())
            if (channel!["id"]!.GetValue<string>() is "guest" or "offline" or "camera") channel["eq"] = CaptureEq(enabled: channel["id"]!.GetValue<string>() == "guest");
        state["mixer"]!["inserts"]!["guest"] = JsonNode.Parse("""
            [{"insert":{"id":"compressor","plugin":"mono","kind":"lv2","label":"Guest compressor with a long label","params":{}},"latencyMilliseconds":2.5}]
            """);
        return state;
    }

    /// <summary>A capture microphone's EQ as the daemon reports it: in software, no unit.</summary>
    internal static JsonNode CaptureEq(bool enabled) => new JsonObject
    {
        ["enabled"] = enabled,
        ["bands"] = new JsonArray([.. OpenXLR.Core.Mixing.MicEq.Default.Bands.Select(b => (JsonNode)new JsonObject
        {
            ["enabled"] = b.Enabled, ["type"] = b.Type, ["frequency"] = b.Frequency, ["gain"] = b.Gain, ["q"] = b.Q,
        })]),
        ["backend"] = enabled ? "software" : null,
        ["available"] = true,
        ["error"] = null,
        ["processing"] = "software",
        ["device"] = null,
    };

    internal static void Apply(MainViewModel main, JsonNode state)
        => typeof(MainViewModel).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [state]);

    /// <summary>Five ordinary resizes on the existing layout test's UI thread.</summary>
    internal static void ThreeCaptureInputsFitClassicWidths()
    {
        var window = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
        StopConnecting(window);
        try
        {
            if (Environment.GetEnvironmentVariable("OPENXLR_LAYOUT_FONT") is { Length: > 0 } font)
                window.FontFamily = new Avalonia.Media.FontFamily(font);
            var main = (MainViewModel)window.DataContext!;
            Apply(main, State());
            window.Show();
            Assert.Equal(["guest", "camera", "offline"], main.CaptureInputs.Select(c => c.Id));
            foreach (double width in new[] { 640d, 760, 1040, 1800, 2400 })
            {
                Layout(window, width, 900);
                StackPanel[] blocks = [.. window.GetVisualDescendants().OfType<StackPanel>().Where(p => p.Name == "CaptureInputBlock")];
                Assert.Equal(3, blocks.Length);
                foreach (StackPanel block in blocks)
                {
                    AssertInside(block, window);
                    Assert.All(block.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible
                        && c is (Button and not RepeatButton) or Slider or TextBlock or CaptureSourceChip), c => AssertInside(c, block));
                    Assert.Single(block.GetVisualDescendants().OfType<Button>(), b => b.Name == "CaptureInserts" && b.IsEffectivelyVisible);
                    // A capture input's EQ, on a mono port or a stereo pair, sits right after its source chip, level with it.
                    var input = (ChannelViewModel)block.DataContext!;
                    Button eq = block.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CaptureEq");
                    Assert.True(eq.IsEffectivelyVisible, input.Id);
                    CheckCaptureEq(block, eq, $"{input.Id} at {width}");
                    // A name without an icon starts where the chip and the labels under it start.
                    TextBlock name = block.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("strip") && t.IsEffectivelyVisible);
                    CaptureSourceChip chip = block.GetVisualDescendants().OfType<CaptureSourceChip>().Single();
                    Assert.Equal(chip.TranslatePoint(default, block)!.Value.X, name.TranslatePoint(default, block)!.Value.X, 0.5);
                }
                Assert.True(blocks[0].TranslatePoint(default, window)!.Value.Y < blocks[1].TranslatePoint(default, window)!.Value.Y);
                Assert.True(blocks[1].TranslatePoint(default, window)!.Value.Y < blocks[2].TranslatePoint(default, window)!.Value.Y);
                Capture(window, $"capture-inputs-{width}");
            }
            FlowNode guest = FlowGraph.From(main).Nodes.Single(n => n.Key == "ch:guest");
            Assert.Contains("Guest compressor", guest.Processing);
            JsonNode state = State();
            JsonArray channels = state["mixer"]!["channels"]!.AsArray();
            JsonNode moved = channels[5]!;
            channels.RemoveAt(5);
            channels.Insert(3, moved);
            Apply(main, state);
            Assert.Equal(["offline", "guest", "camera"], main.CaptureInputs.Select(c => c.Id));
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// A capture microphone's EQ button: after the source chip with no more
    /// than the XLR chip's gap, as tall as the chip and level with it, with
    /// the chip's padding and lettering, inside the block.
    /// </summary>
    internal static void CheckCaptureEq(Control block, Button eq, string where)
    {
        CaptureSourceChip chip = block.GetVisualDescendants().OfType<CaptureSourceChip>().Single();
        Button face = chip.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "Chip");
        AssertInside(eq, block);
        AssertNoOverlap([face, eq]);
        Point at = eq.TranslatePoint(default, block)!.Value, chipAt = face.TranslatePoint(default, block)!.Value;
        Assert.True(at.X >= chipAt.X + face.Bounds.Width, $"the EQ starts inside the chip, {where}");
        Assert.True(at.X - chipAt.X - face.Bounds.Width <= 8, $"the EQ stands {at.X - chipAt.X - face.Bounds.Width} after its chip, {where}");
        Assert.True(Math.Abs(chipAt.Y - at.Y) <= 0.5 && Math.Abs(face.Bounds.Height - eq.Bounds.Height) <= 0.5,
            $"the chip runs {chipAt.Y:0.#}+{face.Bounds.Height:0.#}, its EQ {at.Y:0.#}+{eq.Bounds.Height:0.#}, {where}");
        Assert.Equal(chip.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ChipText").FontSize, eq.FontSize);
        Assert.Equal(face.Padding, eq.Padding);
        Assert.Equal(face.MinHeight, eq.MinHeight);
        Assert.Equal(Localizer.Text("Eq"), eq.Content);
    }

    [CaptureWindowFact]
    public void CaptureButtonsAndMicSetupSendTheirIdsAndOlderStatesRemoveTheButtons()
    {
        string root = Path.Combine(Path.GetTempPath(), "openxlr-capture-window-" + Guid.NewGuid());
        string[] names = ["XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_DATA_DIRS", "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS"];
        string?[] old = [.. names.Select(Environment.GetEnvironmentVariable)];
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                for (int i = 0; i < names.Length - 1; i++) Environment.SetEnvironmentVariable(names[i], Path.Combine(root, names[i]));
                Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", "unix:path=/nonexistent");
                Directory.CreateDirectory(root);
                WindowLayoutTests.PinMaterial();
                AppBuilder.Configure<App>().UseSkia().UseHarfBuzz().UseX11().SetupWithoutStarting();
                Task pending = CheckCommands();
                DateTime deadline = DateTime.UtcNow.AddSeconds(90);
                while (!pending.IsCompleted && DateTime.UtcNow < deadline)
                {
                    Dispatcher.UIThread.RunJobs();
                    Thread.Sleep(5);
                }
                Assert.True(pending.IsCompleted, "The capture chain window check hung.");
                pending.GetAwaiter().GetResult();
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                for (int i = 0; i < names.Length; i++) Environment.SetEnvironmentVariable(names[i], old[i]);
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }) { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "The capture chain window check hung.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static async Task CheckCommands()
    {
        var commands = new ConcurrentQueue<JsonNode>();
        var soundCommands = new ConcurrentQueue<JsonNode>();
        var meterWatches = new ConcurrentQueue<JsonNode>();
        var eqCommands = new ConcurrentQueue<JsonNode>();
        var bypassCommands = new ConcurrentQueue<JsonNode>();
        var gate = new object();
        JsonNode state = State();
        using var pushes = new SemaphoreSlim(0);
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            using var sending = new SemaphoreSlim(1);
            async Task Send(object payload)
            {
                await sending.WaitAsync(stop);
                try { await SocketTestServer.Send(socket, payload, stop); }
                finally { sending.Release(); }
            }
            JsonNode Snapshot() { lock (gate) return state.DeepClone(); }
            await Send(Snapshot());
            Task push = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    await pushes.WaitAsync(stop);
                    await Send(Snapshot());
                }
            }, stop);
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    JsonNode cmd = await SocketTestServer.Receive(socket, stop);
                    string? name = cmd["cmd"]?.GetValue<string>();
                    if (name == "listPlugins")
                    {
                        await Send(new { type = "plugins", plugins = new[]
                        {
                            new { plugin = "mono", name = "Mono compressor", kind = "lv2", widths = new[] { 1 }, @params = Array.Empty<object>() },
                            new { plugin = "stereo", name = "Stereo compressor", kind = "lv2", widths = new[] { 2 }, @params = Array.Empty<object>() },
                        } });
                    }
                    else if (name == "setInserts")
                    {
                        commands.Enqueue(cmd);
                        lock (gate)
                            state["mixer"]!["inserts"]![cmd["channel"]!.GetValue<string>()] = new JsonArray(
                                [.. cmd["inserts"]!.AsArray().Select(i => (JsonNode)new JsonObject { ["insert"] = i!.DeepClone() })]);
                        await Send(Snapshot());
                    }
                    else if (name is "setEq" or "setEqEnabled" or "setEqBand" or "setEqProcessing")
                    {
                        eqCommands.Enqueue(cmd);
                        if (name == "setEqEnabled")
                            lock (gate)
                                state["mixer"]!["channels"]!.AsArray().Single(c => c!["id"]!.GetValue<string>() == cmd["channel"]!.GetValue<string>())!["eq"]
                                    = CaptureEq(cmd["value"]!.GetValue<bool>());
                        else if (name == "setEq")
                            lock (gate)
                            {
                                // The whole EQ, as the daemon keeps it: Mic setup's presets and their undo.
                                JsonNode eq = CaptureEq(cmd["eq"]!["enabled"]!.GetValue<bool>());
                                eq["bands"] = cmd["eq"]!["bands"]!.DeepClone();
                                state["mixer"]!["channels"]!.AsArray().Single(c => c!["id"]!.GetValue<string>() == cmd["channel"]!.GetValue<string>())!["eq"] = eq;
                            }
                        await Send(Snapshot());
                    }
                    else if (name == "setInsertBypass")
                    {
                        bypassCommands.Enqueue(cmd);
                        lock (gate)
                            foreach (JsonNode? slot in state["mixer"]!["inserts"]![cmd["channel"]!.GetValue<string>()]!.AsArray())
                                if (slot!["insert"]!["id"]!.GetValue<string>() == cmd["insertId"]!.GetValue<string>())
                                    slot["insert"]!["bypass"] = cmd["value"]!.GetValue<bool>();
                        await Send(Snapshot());
                    }
                    else if (name == "soundCheck")
                    {
                        soundCommands.Enqueue(cmd);
                        string action = cmd["action"]!.GetValue<string>();
                        lock (gate)
                            state["mixer"]!["soundCheck"] = new JsonObject
                            {
                                ["channel"] = action == "stop" ? null : cmd["channel"]!.GetValue<string>(),
                                ["mode"] = action switch { "record" => "recording", "loop" => "looping", "live" => "live", _ => "idle" },
                                ["seconds"] = action == "stop" ? 0 : 1.5,
                                // A take exists once something was recorded, as the helper counts it.
                                ["take"] = action is "loop" or "live" ? 1 : 0,
                                ["monitorLatencyMs"] = 42.7,
                            };
                        await Send(Snapshot());
                    }
                    else if (name is "setSoundCheckRaw" or "setSoundCheckHearMyself")
                    {
                        soundCommands.Enqueue(cmd);
                        lock (gate)
                        {
                            var session = state["mixer"]!["soundCheck"]!.AsObject();
                            session[name == "setSoundCheckRaw" ? "raw" : "hearMyself"] = cmd["value"]!.GetValue<bool>();
                            if (name == "setSoundCheckHearMyself") session["heard"] = cmd["value"]!.GetValue<bool>();
                        }
                        await Send(Snapshot());
                    }
                    else if (name == "watchInputMeter")
                    {
                        // The meter before the chain, with a clip in it.
                        meterWatches.Enqueue(cmd);
                        await Send(new { type = "meters", levels = new Dictionary<string, double[]>
                        {
                            [$"in:{cmd["channel"]!.GetValue<string>()}"] = [.5, .8, 1],
                            [$"take:{cmd["channel"]!.GetValue<string>()}"] = [.25],
                        } });
                    }
                    else if (name == "getSoundCheckTake")
                    {
                        string? owner;
                        lock (gate) owner = state["mixer"]!["soundCheck"]?["channel"]?.GetValue<string>();
                        await Send(new { type = "soundCheckTake", channel = owner, take = owner is null ? 0 : 1, seconds = 1.5,
                            peaks = Enumerable.Range(0, 200).Select(i => i / 200.0).ToArray() });
                    }
                    // Like the daemon: a reply, then the commandResult that says which request it answers.
                    if (cmd["requestId"] is { } requestId)
                        await Send(new { type = "commandResult", requestId = requestId.GetValue<string>(), error = (string?)null });
                }
            }
            finally
            {
                // Server disposal cancels both loops before it returns.
                try { await push; } catch (OperationCanceledException) { }
            }
        });
        SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
        var window = new MainWindow(new DaemonClient(server.Url));
        var main = (MainViewModel)window.DataContext!;
        try
        {
            window.Show();
            await Until(() => main.CaptureInputs.Count == 3 && main.CaptureInputs.All(c => c.Inserts?.PluginChoices.Count == 1),
                () => $"{main.CaptureInputs.Count} capture inputs, choices {string.Join(",", main.CaptureInputs.Select(c => $"{c.Id}:{c.Inserts?.PluginChoices.Count}:{c.Inserts?.Note}"))}, connected {main.DaemonConnected}, status {main.Status}");
            foreach ((string id, string plugin) in new[] { ("guest", "mono"), ("camera", "stereo") })
            {
                Layout(window, 1040, 900);
                Button button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CaptureInserts"
                    && b.DataContext is ChannelViewModel c && c.Id == id && b.IsEffectivelyVisible);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => window.OwnedWindows.OfType<MixInsertsWindow>().Any(w => w.IsVisible));
                MixInsertsWindow chain = window.OwnedWindows.OfType<MixInsertsWindow>().Single(w => w.IsVisible);
                Layout(chain, 560, 360);
                InsertsViewModel model = main.CaptureInputs.Single(c => c.Id == id).Inserts!;
                Assert.Same(model, chain.DataContext);
                Assert.Equal(id == "guest", model.CanMicSetup);
                Button micSetup = chain.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Localizer.Text("MicSetup")));
                Assert.Equal(id == "guest", micSetup.IsEffectivelyVisible);
                if (id == "guest")
                {
                    await MicSetupWindowTests.CheckCaptureChain(main, chain, soundCommands, meterWatches, eqCommands, bypassCommands);
                    // Mic setup removed the guest's plugin: one chain with nothing in it.
                    JsonNode removed = Assert.Single(commands, c => c["channel"]!.GetValue<string>() == id);
                    Assert.Empty(removed["inserts"]!.AsArray());
                }
                chain.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Localizer.Text("AddPlugin")))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => chain.OwnedWindows.OfType<PluginPickerWindow>().Any(w => w.IsVisible));
                PluginPickerWindow picker = chain.OwnedWindows.OfType<PluginPickerWindow>().Single(w => w.IsVisible);
                ListBox list = picker.FindControl<ListBox>("List")!;
                PluginChoice choice = Assert.Single(list.ItemsSource!.Cast<PluginChoice>());
                Assert.Equal(plugin, choice.Uri);
                list.SelectedItem = choice;
                picker.FindControl<Button>("AddButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => commands.Count(c => c["channel"]!.GetValue<string>() == id) == (id == "guest" ? 2 : 1));
                JsonNode command = commands.Last(c => c["channel"]!.GetValue<string>() == id);
                Assert.Equal("setInserts", command["cmd"]!.GetValue<string>());
                Assert.Equal(plugin, command["inserts"]!.AsArray().Last()!["plugin"]!.GetValue<string>());
                chain.Close();
            }
            await CaptureEqEditor(window, main, eqCommands);
            ChannelViewModel guest = main.CaptureInputs[0];
            InsertsViewModel kept = guest.Inserts!;
            lock (gate) state["mixer"]!["channels"]![3]!["name"] = "Renamed guest";
            pushes.Release();
            await Until(() => guest.Name == "Renamed guest");
            Assert.Same(kept, guest.Inserts);
            Assert.Equal("Renamed guest", kept.Title);
            InsertWindows.OpenChain(window, kept, guest.Id);
            // The header can own Mic setup, so closing the chain alone does not retire it.
            MicSetupWindow setup = MicSetupWindow.Open(window, kept.MicSetup);
            await Until(() => setup.IsVisible);
            int stopsBeforeRemoval = soundCommands.Count(c => c["action"]?.GetValue<string>() == "stop");
            lock (gate)
                foreach (JsonNode? channel in state["mixer"]!["channels"]!.AsArray())
                {
                    channel!.AsObject().Remove("chainWidth");
                    channel.AsObject().Remove("microphone");
                }
            pushes.Release();
            await Until(() => main.CaptureInputs.All(c => !c.HasInserts));
            Layout(window, 1040, 900);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.Name == "CaptureInserts" && b.IsEffectivelyVisible);
            Assert.DoesNotContain(window.OwnedWindows.OfType<MixInsertsWindow>(), w => w.IsVisible);
            Assert.True(kept.IsDetached);
            await Until(() => !setup.IsVisible);
            Assert.Equal(stopsBeforeRemoval, soundCommands.Count(c => c["action"]?.GetValue<string>() == "stop"));
            Assert.All(main.CaptureInputs, c => Assert.False(c.Microphone));
            // Mic setup's removal, then one plugin added to each input.
            Assert.Equal(3, commands.Count);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The guest microphone's EQ chip in classic INPUTS opens the editor XLR 1
    /// uses, on the guest's own EQ, with no switch between the unit and this
    /// computer, no hardware badge and no unit note, and a status line that
    /// names no low cut or ClipGuard. Its switch sends setEqEnabled for the
    /// guest. The stereo pair's chip, off, opens the same editor on its own EQ,
    /// whose switch sends setEqEnabled for the stereo pair.
    /// </summary>
    private static async Task CaptureEqEditor(MainWindow window, MainViewModel main, ConcurrentQueue<JsonNode> eqCommands)
    {
        Layout(window, 1040, 900);
        Button Chip(string id) => window.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Name == "CaptureEq" && b.DataContext is ChannelViewModel c && c.Id == id);
        Button stereo = Chip("camera");
        Assert.True(stereo.IsEffectivelyVisible);
        Assert.DoesNotContain("fxOn", stereo.Classes);
        Assert.Equal(Localizer.Text("EqButtonTipCapture"), ToolTip.GetTip(stereo));
        Button button = Chip("guest");
        Assert.True(button.IsEffectivelyVisible);
        Assert.Contains("fxOn", button.Classes);
        Assert.Equal(Localizer.Text("EqButtonTipCapture"), ToolTip.GetTip(button));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => window.OwnedWindows.OfType<EqWindow>().Any(w => w.IsVisible));
        EqWindow editor = window.OwnedWindows.OfType<EqWindow>().Single(w => w.IsVisible);
        try
        {
            MicEqViewModel eq = main.CaptureInputs.Single(c => c.Id == "guest").Eq!;
            Assert.Same(eq, editor.DataContext);
            foreach (double width in new[] { 640d, 820 })
            {
                Layout(editor, width, 600);
                Assert.Equal(Localizer.Format("EqWindowTitle", "Guest microphone with a long name"), editor.Title);
                Assert.False(editor.FindControl<WrapPanel>("EqProcessing")!.IsEffectivelyVisible);
                Assert.False(editor.FindControl<Border>("EqHardwareBadge")!.IsEffectivelyVisible);
                Assert.True(editor.FindControl<Border>("EqSoftwareBadge")!.IsEffectivelyVisible);
                Assert.False(editor.FindControl<TextBlock>("EqDeviceNote")!.IsEffectivelyVisible);
                TextBlock status = editor.FindControl<TextBlock>("EqStatus")!;
                Assert.Equal(Localizer.Text("EqStatusSoftwareCapture"), status.Text);
                Assert.True(status.IsEffectivelyVisible);
                AssertInside(status, editor);
                Capture(editor, $"capture-eq-editor-{width}");
            }
            editor.FindControl<ToggleButton>("EqSwitch")!.IsChecked = false;
            await Until(() => eqCommands.Any(), () => "no EQ command");
            JsonNode sent = Assert.Single(eqCommands);
            Assert.Equal("setEqEnabled", sent["cmd"]!.GetValue<string>());
            Assert.Equal("guest", sent["channel"]!.GetValue<string>());
            Assert.False(sent["value"]!.GetValue<bool>());
            await Until(() => editor.FindControl<TextBlock>("EqStatus")!.Text == Localizer.Text("EqStatusOff"));
            Assert.DoesNotContain("fxOn", button.Classes);
        }
        finally { editor.Close(); }

        while (eqCommands.TryDequeue(out _)) { }
        stereo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => window.OwnedWindows.OfType<EqWindow>().Any(w => w.IsVisible));
        EqWindow pair = window.OwnedWindows.OfType<EqWindow>().Single(w => w.IsVisible);
        try
        {
            Assert.Same(main.CaptureInputs.Single(c => c.Id == "camera").Eq, pair.DataContext);
            Assert.Equal(Localizer.Format("EqWindowTitle", "Stereo capture card"), pair.Title);
            Assert.False(pair.FindControl<WrapPanel>("EqProcessing")!.IsEffectivelyVisible);
            Assert.Equal(Localizer.Text("EqStatusOff"), pair.FindControl<TextBlock>("EqStatus")!.Text);
            pair.FindControl<ToggleButton>("EqSwitch")!.IsChecked = true;
            await Until(() => eqCommands.Any(), () => "no EQ command");
            JsonNode sent = Assert.Single(eqCommands);
            Assert.Equal("setEqEnabled", sent["cmd"]!.GetValue<string>());
            Assert.Equal("camera", sent["channel"]!.GetValue<string>());
            Assert.True(sent["value"]!.GetValue<bool>());
        }
        finally { pair.Close(); }
    }

    private static async Task Until(Func<bool> ready, Func<string>? describe = null)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(15);
        Assert.True(ready(), "The capture chain state did not arrive. " + describe?.Invoke());
    }
}

public sealed class CaptureWindowFactAttribute : FactAttribute
{
    public CaptureWindowFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENXLR_TEST_CAPTURE") != "1")
            Skip = "Run separately with OPENXLR_TEST_CAPTURE=1 under xvfb-run, filtered to CaptureInputWindowTests.";
    }
}
