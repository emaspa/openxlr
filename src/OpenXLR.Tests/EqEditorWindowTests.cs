using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;
using OpenXLR.UI.Views;
using static OpenXLR.Tests.InputBindingModelTests;
using static OpenXLR.Tests.WindowLayoutTests;
using DaemonBand = OpenXLR.Core.Mixing.EqBand;
using DaemonChange = OpenXLR.Core.Mixing.EqBandChange;
using DaemonEq = OpenXLR.Core.Mixing.MicEq;

namespace OpenXLR.Tests;

/// <summary>
/// The microphone EQ editor on real windows against a fake daemon that keeps
/// the EQ with the daemon's own model and snapping. The EQ button sits right
/// after each microphone's binding chip in the classic INPUTS panel, at the
/// chip's height and clear of it with a long source name, an input rack's effects part and the Broadcast and
/// Wave matrix row heads, inside its head at 640, 1040 and 2400 or 2560
/// pixels in Standard and Touch sizing, and it opens one editor per
/// microphone. The editor fits from 640 to 2400 pixels; a drag with the real
/// pointer sends setEqBand with snapped values at most every 20 ms and the
/// editor then shows what the daemon reports; the wheel, the keyboard, a
/// double click and the type menu each send their one change; the switch and
/// the comparison send setEqEnabled; paste and reset send setEq, a profile
/// loads with loadEqProfile, and saving, renaming and deleting a saved one send
/// their own command, the built-in profiles listed first; the footer's
/// switch between the unit and software sends setEqProcessing; and nothing
/// else goes out. A state without an EQ draws no button. Run on its own,
/// with OPENXLR_TEST_EQ=1 under xvfb-run, so the window layout run keeps
/// within its watchdog.
/// </summary>
[Collection("xdg-config")]
public sealed class EqEditorWindowTests
{
    private static readonly string[] EqCommands = ["setEq", "setEqEnabled", "setEqBand", "setEqProcessing"];
    private static readonly string[] ProfileCommands = ["saveEqProfile", "renameEqProfile", "deleteEqProfile", "loadEqProfile", "listEqProfiles"];
    private static readonly System.Text.Json.JsonSerializerOptions Web = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>A Wave XLR Pro with XLR 1 and XLR 2, each with an EQ the daemon's own model keeps.</summary>
    private sealed class EqDaemon
    {
        private readonly object _gate = new();
        /// <summary>Every EQ by what the editor names it: the microphones, and each mix as <c>mix:</c> and its id.</summary>
        public readonly Dictionary<string, DaemonEq> Eqs = new()
        {
            ["xlr1"] = DaemonEq.Default, ["xlr2"] = DaemonEq.Default, ["mix:monitor"] = DaemonEq.Default, ["mix:stream"] = DaemonEq.Default,
        };
        public bool WithEq = true;
        public bool VariableBands;
        /// <summary>XLR 1's unit: null for none, <c>unit</c> when its DSP can run the EQ, <c>old</c> when it cannot.</summary>
        public string? Device;
        /// <summary>XLR 2's unit: null for none, <c>pro</c> for the Wave XLR Pro's second input on 1.0.14, <c>proOld</c> on 1.0.13.</summary>
        public string? Device2;
        /// <summary>Why an <c>old</c> unit cannot run the EQ, as the daemon names it.</summary>
        public string Reason = "firmwareOld";
        /// <summary>Each microphone's choice of where its EQ runs.</summary>
        public readonly Dictionary<string, string> Processing = new() { ["xlr1"] = "device", ["xlr2"] = "device" };
        /// <summary>A daemon from before the hardware EQ: no choice and no unit in the state.</summary>
        public bool Legacy;
        /// <summary>A capture source's description for XLR 2 to take in place of the interface's second input; null for the jack.</summary>
        public string? Xlr2Source;
        public readonly ConcurrentQueue<(long At, JsonNode Command)> Commands = new();

        /// <summary>Released to push a state without a command, as the daemon does on its own changes.</summary>
        public readonly SemaphoreSlim Pushes = new(0);

        public JsonNode State()
        {
            lock (_gate)
            {
                string channels = Xlr("xlr1", 0, Input(1)) + "," + (Xlr2Source is null ? Xlr("xlr2", 1, Input(2))
                    : """{"id":"xlr2","name":"XLR 2","hardware":true,"present":true,"levels":{"monitor":1},"binding":{"kind":"source","source":"longmic","pair":0,"monoChannel":0},"bindingConnected":true}""");
                string limits = """
                    ,"eqLimits":{"bands":4,"types":["lowCut","lowShelf","peak","notch","highShelf","highCut"],
                      "frequency":{"min":20,"max":20000,"step":1},"gain":{"min":-12,"max":12,"step":0.25},
                      "q":{"min":0.1,"max":10,"log10Step":0.016}}
                    """;
                JsonNode state = JsonNode.Parse(ProState(channels, mixerExtra: WithEq ? limits : "", withMix: true))!;
                if (Xlr2Source is not null)
                    state["devices"]!.AsArray().Add(new JsonObject { ["name"] = "longmic", ["description"] = Xlr2Source, ["kind"] = 1, ["channels"] = 1 });
                state["mixer"]!["mixes"]!.AsArray().Add(JsonNode.Parse("""{"id":"stream","name":"Stream","kind":"virtualMic"}"""));
                // Each mix's output EQ, which always runs on this computer.
                foreach (JsonNode? mix in state["mixer"]!["mixes"]!.AsArray())
                    if (WithEq && Eqs.TryGetValue("mix:" + mix!["id"]!.GetValue<string>(), out DaemonEq? mixEq))
                        mix["eq"] = Json(mixEq, null, "software", Reason);
                foreach (JsonNode? channel in state["mixer"]!["channels"]!.AsArray())
                {
                    channel!["volume"] = 0.8;
                    if (!WithEq) continue;
                    string id = channel["id"]!.GetValue<string>();
                    channel["eq"] = Json(Eqs[id], id == "xlr1" ? Device : Device2, Processing[id], Reason);
                    if (Legacy)
                    {
                        // It ran every EQ in software.
                        channel["eq"]!["backend"] = Eqs[id].Enabled ? "software" : null;
                        channel["eq"]!.AsObject().Remove("processing");
                        channel["eq"]!.AsObject().Remove("device");
                    }
                }
                if (VariableBands && WithEq)
                {
                    state["mixer"]!["eqLimits"]!["minBands"] = 1;
                    state["mixer"]!["eqLimits"]!["maxBands"] = 16;
                }
                // The daemon's EQ profiles, from its own store.
                OpenXLR.Core.Mixing.EqProfilesState profiles = OpenXLR.Core.Mixing.EqProfileStore.State();
                state["eqProfiles"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(profiles.Profiles, Web));
                state["eqProfileProblem"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(profiles.Problem, Web));
                state["type"] = "state";
                state["daemonVersion"] = AppVersion.Current;
                return state;
            }
        }

        private static JsonNode Json(DaemonEq eq, string? device, string processing, string reason) => new JsonObject
        {
            ["enabled"] = eq.Enabled,
            ["bands"] = new JsonArray([.. eq.Bands.Select(b => (JsonNode)new JsonObject
            {
                ["enabled"] = b.Enabled, ["type"] = b.Type, ["frequency"] = b.Frequency, ["gain"] = b.Gain, ["q"] = b.Q,
            })]),
            ["backend"] = device is "unit" or "pro" && processing == "device" && eq.Bands.Count <= 4 ? "device" : eq.Enabled ? "software" : null,
            ["available"] = true,
            ["error"] = null,
            ["processing"] = processing,
            ["device"] = device is null ? null : device.StartsWith("pro", StringComparison.Ordinal) ? new JsonObject
            {
                ["unit"] = "0fd9:00b4@00112233445566aa", ["input"] = 2, ["name"] = "Wave XLR Pro",
                ["firmware"] = device == "pro" ? "1.0.14" : "1.0.13", ["ready"] = device == "pro",
                ["reason"] = device == "pro" ? null : "firmwareOld", ["minimumFirmware"] = "1.0.14",
                ["note"] = device == "pro" ? null : "This Wave XLR Pro has firmware 1.0.13. From 1.0.14 the unit runs the EQ itself; Elgato's updater is in Wave Link on Windows or macOS.",
            } : new JsonObject
            {
                ["unit"] = "0fd9:00c7@0123456789abcdef", ["input"] = 1, ["name"] = "Wave XLR Dock MK.2",
                ["firmware"] = device == "unit" ? "1.0.15" : "1.0.0", ["ready"] = device == "unit",
                ["reason"] = device == "unit" ? null : reason, ["minimumFirmware"] = "1.0.15",
                ["note"] = device == "unit" ? null : "This Wave XLR Dock MK.2 has firmware 1.0.0. From 1.0.15 the unit runs the EQ itself; Elgato's updater is in Wave Link on Windows or macOS.",
            },
        };

        /// <summary>Take a command as the daemon does: checked, snapped, kept. Null, or why it was refused.</summary>
        public string? Handle(JsonNode cmd)
        {
            string name = cmd["cmd"]!.GetValue<string>();
            if (ProfileCommands.Contains(name)) return HandleProfile(name, cmd);
            if (!EqCommands.Contains(name)) return null;
            string channel = Target(cmd);
            lock (_gate)
            {
                DaemonEq eq = Eqs[channel];
                switch (name)
                {
                    case "setEqEnabled":
                        Eqs[channel] = eq with { Enabled = cmd["value"]!.GetValue<bool>() };
                        break;
                    case "setEqProcessing":
                        string value = cmd["value"]!.GetValue<string>();
                        Assert.Contains(value, new[] { "device", "software" });
                        Processing[channel] = value;
                        break;
                    case "setEq":
                        Eqs[channel] = DaemonEq.Read(System.Text.Json.JsonDocument.Parse(cmd["eq"]!.ToJsonString()).RootElement, out string? error)
                            ?? throw new InvalidOperationException(error);
                        break;
                    default:
                        int band = cmd["band"]!.GetValue<int>();
                        var change = new DaemonChange(cmd["enabled"]?.GetValue<bool>(), cmd["type"]?.GetValue<string>(),
                            cmd["frequency"]?.GetValue<double>(), cmd["gain"]?.GetValue<double>(), cmd["q"]?.GetValue<double>());
                        DaemonBand changed = change.ApplyTo(eq.Bands[band - 1]);
                        Assert.Null(DaemonEq.Check(changed));
                        Eqs[channel] = eq.WithBand(band, DaemonEq.Snap(changed));
                        break;
                }
            }
            return null;
        }

        /// <summary>What an EQ command names: exactly one of a microphone's <c>channel</c> and a <c>mix</c>, never both.</summary>
        private static string Target(JsonNode cmd)
        {
            string? channel = cmd["channel"]?.GetValue<string>(), mix = cmd["mix"]?.GetValue<string>();
            Assert.True(channel is null != mix is null, "an EQ command names a channel or a mix: " + cmd.ToJsonString());
            return channel ?? "mix:" + mix;
        }

        /// <summary>The EQ profile commands, on the daemon's own store; a load becomes the setEq it is.</summary>
        private string? HandleProfile(string name, JsonNode cmd)
        {
            try
            {
                switch (name)
                {
                    case "saveEqProfile":
                        var bands = DaemonEq.Read(System.Text.Json.JsonDocument.Parse(
                            new JsonObject { ["enabled"] = true, ["bands"] = cmd["bands"]!.DeepClone() }.ToJsonString()).RootElement, out string? error)
                            ?? throw new InvalidOperationException(error);
                        OpenXLR.Core.Mixing.EqProfileStore.Save(cmd["name"]!.GetValue<string>(), bands.Bands, cmd["replace"]?.GetValue<bool>() == true);
                        return null;
                    case "renameEqProfile":
                        OpenXLR.Core.Mixing.EqProfileStore.Rename(cmd["name"]!.GetValue<string>(), cmd["newName"]!.GetValue<string>());
                        return null;
                    case "deleteEqProfile":
                        OpenXLR.Core.Mixing.EqProfileStore.Delete(cmd["name"]!.GetValue<string>());
                        return null;
                    case "listEqProfiles":
                        OpenXLR.Core.Mixing.EqProfileStore.State(reread: true);
                        return null;
                    default:
                        var load = new OpenXLR.Daemon.Command
                        {
                            Cmd = name, Channel = cmd["channel"]?.GetValue<string>(), Mix = cmd["mix"]?.GetValue<string>(),
                            Profile = cmd["profile"]!.GetValue<string>(),
                        };
                        OpenXLR.Daemon.Command setEq = OpenXLR.Daemon.MixerService.ProfileEq(load, OpenXLR.Core.Mixing.EqLimits.Instance, out string? refused)
                            ?? throw new InvalidDataException(refused);
                        lock (_gate) Eqs[Target(cmd)] = DaemonEq.Read(setEq.Eq, out _)!;
                        return null;
                }
            }
            catch (Exception ex) when (OpenXLR.Core.Mixing.EqProfileStore.IsFailure(ex)) { return $"{name}: {ex.Message}"; }
        }
    }

    [EqWindowFact]
    public void TheEditorOpensFromEverySkinAndSendsOnlyTheEqCommands()
    {
        string root = Path.Combine(Path.GetTempPath(), "openxlr-eq-window-" + Guid.NewGuid());
        string?[] old = [.. new[] { "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_DATA_DIRS", "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS" }
            .Select(Environment.GetEnvironmentVariable)];
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(root, "config"));
                Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(root, "data"));
                Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(root, "none"));
                Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", Path.Combine(root, "config"));
                Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", "unix:path=/nonexistent");
                Directory.CreateDirectory(Path.Combine(root, "config"));
                WriteRackSkin(root);
                WindowLayoutTests.PinMaterial();
                AppBuilder.Configure<App>().UseSkia().UseHarfBuzz().UseX11().SetupWithoutStarting();
                Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
                Task pending = Check();
                DateTime deadline = DateTime.UtcNow.AddSeconds(240);
                while (!pending.IsCompleted && DateTime.UtcNow < deadline)
                {
                    Dispatcher.UIThread.RunJobs();
                    Thread.Sleep(5);
                }
                Assert.True(pending.IsCompleted, "The EQ editor check hung.");
                pending.GetAwaiter().GetResult();
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                string[] names = ["XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_DATA_DIRS", "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS"];
                for (int i = 0; i < names.Length; i++) Environment.SetEnvironmentVariable(names[i], old[i]);
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }) { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(270)), "The EQ editor check hung.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>An input rack skin: the rack's strips with every part, the matrix left to the default submixer.</summary>
    private static void WriteRackSkin(string root)
    {
        string folder = Path.Combine(root, "data", "openxlr", "skins", "rack");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "skin.json"), """
            {"schema":1,"name":"Rack","tokens":{"Ox.Card.Background":"#22262e"},"layout":{"version":1,
             "regions":{"inputs":{"template":"input-rack"}}}}
            """);
    }

    private static async Task Check()
    {
        var daemon = new EqDaemon();
        var clock = Stopwatch.StartNew();
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            var sending = new SemaphoreSlim(1);
            async Task Send(object payload)
            {
                await sending.WaitAsync(stop);
                try { await SocketTestServer.Send(socket, payload, stop); }
                finally { sending.Release(); }
            }
            await Send(daemon.State());
            Task pushes = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    await daemon.Pushes.WaitAsync(stop);
                    await Send(daemon.State());
                }
            }, stop);
            while (!stop.IsCancellationRequested)
            {
                JsonNode cmd = await SocketTestServer.Receive(socket, stop);
                string name = cmd["cmd"]?.GetValue<string>() ?? "";
                // The window reads the plugin catalogue for the mixes' chains; an empty one.
                if (name == "listPlugins")
                {
                    await Send(new { type = "plugins", plugins = Array.Empty<object>() });
                    continue;
                }
                // An editor coming forward asks for the profiles read again; a read, not a change.
                if (name == "listEqProfiles")
                {
                    daemon.Handle(cmd);
                    await Send(daemon.State());
                    continue;
                }
                daemon.Commands.Enqueue((clock.ElapsedMilliseconds, cmd));
                string? refused = daemon.Handle(cmd);
                await Send(daemon.State());
                if (cmd["requestId"] is { } requestId)
                    await Send(new { type = "commandResult", requestId = requestId.GetValue<string>(), error = refused });
            }
        });

        SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
        SkinService.ApplyControlSizing(false);
        var window = new MainWindow(new DaemonClient(server.Url));
        var main = (MainViewModel)window.DataContext!;
        var editors = new List<Window>();
        try
        {
            window.Show();
            await Until(() => main.HasMixer && main.Channels.Count(c => c.HasEq) == 2);
            ChannelViewModel xlr1 = main.Channels.Single(c => c.Id == "xlr1"), xlr2 = main.Channels.Single(c => c.Id == "xlr2");
            MicEqViewModel eq1 = xlr1.Eq!, eq2 = xlr2.Eq!;
            // The daemon's defaults, as the daemon reported them.
            Assert.Equal([36d, 310, 2172, 15049], eq1.Bands.Select(b => b.Frequency));
            Assert.Equal(DaemonEq.DefaultQ, eq1.Bands[0].Q);
            Assert.False(eq1.Enabled);

            // Classic INPUTS: each microphone's EQ right after its chip on the input line, at every
            // width and sizing. The EQ took no row of its own, and the software row, which the Pro
            // has no use for, stays closed.
            Assert.Null(window.FindControl<WrapPanel>("Software2Controls"));
            foreach (string skin in new[] { SkinPackage.DefaultId, "tokyo-night" })
            {
                SkinLayoutWindowTests.Wear(skin);
                foreach (bool touch in new[] { false, true })
                {
                    SkinService.ApplyControlSizing(touch);
                    foreach (double width in skin == SkinPackage.DefaultId ? new[] { 640d, 1040, 2400 } : [640d, 1040])
                    {
                        Layout(window, width, 1100);
                        string where = $"classic {skin}, {Sizing(touch)} at {width}";
                        CheckClassicEq(window, where);
                        Assert.False(window.FindControl<WrapPanel>("SoftwareControls")!.IsEffectivelyVisible, where);
                        foreach (string name in new[] { "Xlr1Eq", "Xlr2Eq" })
                        {
                            Button button = window.FindControl<Button>(name)!;
                            Assert.Equal(Localizer.Text("Eq"), button.Content);
                            Assert.DoesNotContain("fxOn", button.Classes);
                        }
                        Assert.Equal(Localizer.Format("EqButtonNameOff", "XLR 1"), AutomationProperties.GetName(window.FindControl<Button>("Xlr1Eq")!));
                        if (width == 1040) Capture(window, $"eq-classic-{skin}-{Sizing(touch)}-1040");
                    }
                }
            }
            SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
            SkinService.ApplyControlSizing(false);
            Layout(window, 1040, 1100);

            // One editor per microphone: a second click raises the open one.
            Click(window.FindControl<Button>("Xlr1Eq")!);
            EqWindow editor = await Editor(window, eq1);
            editors.Add(editor);
            Click(window.FindControl<Button>("Xlr1Eq")!);
            Dispatcher.UIThread.RunJobs();
            Assert.Single(window.OwnedWindows.OfType<EqWindow>(), w => w.DataContext == eq1);
            Assert.Equal(Localizer.Format("EqWindowTitle", "XLR 1"), editor.Title);
            CheckEditor(editor, eq1);

            // The real pointer drags band 2: frequency sideways, gain up.
            daemon.Commands.Clear();
            await Drag(editor, daemon, eq1);
            await Gestures(editor, daemon, eq1);
            await Controls(editor, daemon, eq1, window);
            EqWindow second = await CopyAndReset(editor, daemon, eq1, eq2, window);
            editors.Add(second);
            await Profiles(editor, second, daemon, eq1, eq2);
            await Tooltips(editor);
            await Skins(window, editor, main);
            await Hardware(window, editor, daemon, eq1);
            await ProHardware(window, second, daemon, eq2);
            await LongSource(window, daemon, main);
            await VariableBandEditor(editor, daemon, eq1);
            editors.Add(await MixEqEditor(window, daemon, main));
            Assert.DoesNotContain(daemon.Commands, c => !EqCommands.Contains(c.Command["cmd"]!.GetValue<string>()));

            // A state without the EQ, from an older daemon: no button anywhere, and the editors go.
            eq1.Enabled = true;
            await Until(() => daemon.Eqs["xlr1"].Enabled);
            eq1.Comparing = true;
            await Until(() => !daemon.Eqs["xlr1"].Enabled);
            daemon.Commands.Clear();
            daemon.WithEq = false;
            daemon.Pushes.Release();
            await Until(() => main.Channels.All(c => !c.HasEq) && main.Mixes.All(m => !m.HasEq));
            Pump(50);
            Assert.False(editor.IsVisible);
            Assert.False(second.IsVisible);
            // Retiring the EQ must not restore comparison into a replacement with this id.
            Assert.Empty(daemon.Commands);
            foreach (string name in new[] { "Xlr1Eq", "Xlr2Eq" })
                Assert.False(window.FindControl<Button>(name)!.IsEffectivelyVisible);
            Assert.False(window.FindControl<WrapPanel>("SoftwareControls")!.IsEffectivelyVisible);
            SkinLayoutWindowTests.Wear("broadcast");
            Layout(window, 1040, 1100);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.Name is "MatrixEq" or "MatrixMixEq" && b.IsEffectivelyVisible);
            Assert.All(window.MixMatrix!.Rows, r => Assert.False(r.ShowsEq));
            SkinLayoutWindowTests.CheckMatrixCells(window, window.MixMatrix!, "broadcast without an EQ");
        }
        finally
        {
            foreach (Window editor in editors) editor.Close();
            window.Close();
            SkinService.ApplyControlSizing(false);
            SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static string Sizing(bool touch) => touch ? "touch" : "standard";

    /// <summary>
    /// A mix's EQ: the button over each mix's plugin chain in the matrix
    /// head, as the signal meets them, inside the head at every width; the same editor, named for the
    /// mix, with no unit and no choice of where; its commands name the mix
    /// and no channel; and its profile list holds every built-in profile, the
    /// voice curves and the listening curves alike.
    /// </summary>
    private static async Task<EqWindow> MixEqEditor(MainWindow window, EqDaemon daemon, MainViewModel main)
    {
        MixViewModel stream = main.Mixes.Single(m => m.Id == "stream");
        MicEqViewModel eq = stream.Eq!;
        Assert.True(eq.IsMix);
        Assert.Equal("mix:stream", eq.Channel);
        foreach (string skin in new[] { "broadcast", "wave" })
        {
            SkinLayoutWindowTests.Wear(skin);
            foreach (double width in new[] { 1040d, 2400 })
            {
                Layout(window, width, 1100);
                string where = $"{skin} at {width}";
                Button[] buttons = [.. window.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "MatrixMixEq" && b.IsEffectivelyVisible)];
                Assert.Equal(main.Mixes.Count(m => m.HasEq && m.DisplayVisible && m.Visible), buttons.Length);
                foreach (Button button in buttons)
                {
                    Border head = button.GetVisualAncestors().OfType<Border>().First(b => b.Name == "MatrixColumn");
                    AssertInside(button, head);
                    // Over the chain, as the signal meets them.
                    Button chain = head.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("matrixEffects"));
                    AssertNoOverlap([button, chain]);
                    Assert.True(button.TranslatePoint(new Point(0, button.Bounds.Height), head)!.Value.Y
                        <= chain.TranslatePoint(default, head)!.Value.Y + 0.5, where);
                    Assert.Equal(Localizer.Text("Eq"), button.Content);
                    Assert.DoesNotContain("fxOn", button.Classes);
                    Assert.Equal(Localizer.Text("EqButtonTipMix"), ToolTip.GetTip(button));
                }
                SkinLayoutWindowTests.CheckMatrixCells(window, window.MixMatrix!, "mix EQ " + where);
            }
        }
        Layout(window, 1040, 1100);
        Button open = window.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Name == "MatrixMixEq" && b.DataContext is OpenXLR.UI.Layout.MatrixColumnViewModel { Mix.Id: "stream" });
        Assert.Equal(Localizer.Format("EqButtonNameOff", "Stream"), AutomationProperties.GetName(open));
        Click(open);
        EqWindow editor = await Editor(window, eq);
        Assert.Equal(Localizer.Format("EqWindowTitle", "Stream"), editor.Title);
        Assert.False(editor.FindControl<WrapPanel>("EqProcessing")!.IsVisible);
        Assert.False(editor.FindControl<Border>("EqHardwareBadge")!.IsVisible);
        ComboBox list = editor.FindControl<ComboBox>("EqProfileList")!;
        string[] listed = [.. list.Items.OfType<EqProfile>().Select(p => p.Id)];
        Assert.Equal(EqPresets.All.Select(p => p.Id), listed.Where(id => id.StartsWith("builtin:", StringComparison.Ordinal)));

        daemon.Commands.Clear();
        eq.Enabled = true;
        JsonNode on = Assert.Single(await Sent(daemon, 1));
        Assert.Equal("setEqEnabled", on["cmd"]!.GetValue<string>());
        Assert.Equal("stream", on["mix"]!.GetValue<string>());
        Assert.Null(on["channel"]);
        await Until(() => daemon.Eqs["mix:stream"].Enabled && eq.IsLit);
        Assert.Contains("fxOn", open.Classes);
        Assert.Equal(Localizer.Text("EqStatusSoftwareMix"), eq.Status);
        eq.Bands[1].SetGain(-3);
        JsonNode band = Assert.Single(await Sent(daemon, 1));
        Assert.Equal("stream", band["mix"]!.GetValue<string>());
        Assert.Equal(2, band["band"]!.GetValue<int>());
        await Until(() => daemon.Eqs["mix:stream"].Bands[1].Gain == -3);
        // A listening curve, two bands, loads on the mix as one loadEqProfile
        // that names it, from a daemon whose EQ takes 1 to 16 bands.
        bool variable = daemon.VariableBands;
        daemon.VariableBands = true;
        daemon.Pushes.Release();
        await Until(() => eq.VariableBands);
        daemon.Commands.Clear();
        list.SelectedItem = list.Items.OfType<EqProfile>().Single(p => p.Id == "builtin:footsteps");
        JsonNode load = Assert.Single(await Sent(daemon, 1));
        Assert.Equal("loadEqProfile", load["cmd"]!.GetValue<string>());
        Assert.Equal("stream", load["mix"]!.GetValue<string>());
        await Until(() => daemon.Eqs["mix:stream"].Bands.Count == 2 && eq.Bands.Count == 2);
        // The other mix's EQ was not touched.
        Assert.Equal(DaemonEq.Default.Bands, daemon.Eqs["mix:monitor"].Bands);
        daemon.VariableBands = variable;
        daemon.Pushes.Release();
        await Until(() => eq.VariableBands == variable);
        daemon.Commands.Clear();
        return editor;
    }

    /// <summary>
    /// XLR 2 bound to a source with a long description: the chip trims its
    /// name and the EQ keeps its place after it, inside the window, at the
    /// narrow width in both sizings.
    /// </summary>
    private static async Task LongSource(MainWindow window, EqDaemon daemon, MainViewModel main)
    {
        const string name = "USB Audio Device Analog Stereo Microphone With A Very Long Description, As Some Interfaces Report Theirs";
        SkinLayoutWindowTests.Wear(SkinPackage.DefaultId);
        daemon.Xlr2Source = name;
        daemon.Pushes.Release();
        ChannelViewModel xlr2 = main.Channels.Single(c => c.Id == "xlr2");
        await Until(() => xlr2.IsSourceBound && xlr2.BindingLabel.StartsWith(name, StringComparison.Ordinal));
        try
        {
            foreach (bool touch in new[] { false, true })
            {
                SkinService.ApplyControlSizing(touch);
                foreach (double width in new[] { 640d, 1040 })
                {
                    Layout(window, width, 1100);
                    string where = $"long source, {Sizing(touch)} at {width}";
                    CheckClassicEq(window, where);
                    TextBlock text = window.FindControl<Grid>("Xlr2ChipRow")!.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ChipText");
                    if (width == 640) Assert.True(text.TextLayout.TextLines.Any(l => l.HasCollapsed), $"the long name is not trimmed, {where}");
                    if (width == 640) Capture(window, $"eq-classic-long-source-{Sizing(touch)}-640");
                }
            }
        }
        finally
        {
            SkinService.ApplyControlSizing(false);
            daemon.Xlr2Source = null;
            daemon.Pushes.Release();
        }
        await Until(() => xlr2.IsUnitBound);
    }

    /// <summary>
    /// The classic INPUTS panel's EQ buttons: each on its microphone's input
    /// line right after the binding chip, as tall as the chip and level with
    /// it, with the chip's padding and lettering, clear of the chip and inside
    /// the line and the window. A line that is not drawn is skipped.
    /// </summary>
    internal static void CheckClassicEq(Window window, string where)
    {
        foreach (string n in new[] { "1", "2" })
        {
            Grid line = window.FindControl<Grid>($"Xlr{n}Binding")!, row = window.FindControl<Grid>($"Xlr{n}ChipRow")!;
            Button button = window.FindControl<Button>($"Xlr{n}Eq")!;
            if (!line.IsEffectivelyVisible) continue;
            Assert.True(button.IsEffectivelyVisible, $"XLR {n}'s EQ hidden, {where}");
            Button chip = row.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "Chip");
            foreach (Control part in new Control[] { chip, button })
            {
                AssertInside(part, line);
                AssertInside(part, window);
            }
            AssertNoOverlap([chip, button]);
            Point at = button.TranslatePoint(default, row)!.Value, chipAt = chip.TranslatePoint(default, row)!.Value;
            Assert.True(at.X >= chipAt.X + chip.Bounds.Width, $"XLR {n}'s EQ at {at.X} starts inside the chip ending at {chipAt.X + chip.Bounds.Width}, {where}");
            Assert.True(at.X - chipAt.X - chip.Bounds.Width <= 8, $"XLR {n}'s EQ stands {at.X - chipAt.X - chip.Bounds.Width} after its chip, {where}");
            Assert.True(Math.Abs(chipAt.Y - at.Y) <= 0.5 && Math.Abs(chip.Bounds.Height - button.Bounds.Height) <= 0.5,
                $"XLR {n}: the chip runs {chipAt.Y:0.#}+{chip.Bounds.Height:0.#}, its EQ {at.Y:0.#}+{button.Bounds.Height:0.#}, {where}");
            Assert.Equal(chip.Padding, button.Padding);
            Assert.Equal(chip.BorderThickness, button.BorderThickness);
            Assert.Equal(chip.MinHeight, button.MinHeight);
            Assert.Equal(chip.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ChipText").FontSize, button.FontSize);
            Assert.Equal(chip.CornerRadius, button.CornerRadius);
        }
    }

    /// <summary>
    /// Where the EQ runs: a badge in the editor's header while the unit's own
    /// processor runs it, inside the editor from 640 to 2400 pixels; a note
    /// above the footer when the unit is on firmware before 1.0.15; the EQ
    /// button's tooltip saying the same; neither while the state names no unit.
    /// The footer's switch between the unit and this computer shows only while
    /// a unit is offered and the state names the choice: the real pointer on
    /// the other side sends setEqProcessing, on the side already chosen sends
    /// nothing, and the switch follows the daemon. With the longest note the
    /// footer, the bands and the graph still fit from 640 to 2400 pixels in
    /// both sizings.
    /// </summary>
    private static async Task Hardware(MainWindow window, EqWindow editor, EqDaemon daemon, MicEqViewModel eq)
    {
        Border badge = editor.FindControl<Border>("EqHardwareBadge")!;
        TextBlock note = editor.FindControl<TextBlock>("EqDeviceNote")!;
        WrapPanel choice = editor.FindControl<WrapPanel>("EqProcessing")!;
        ToggleButton onUnit = editor.FindControl<ToggleButton>("EqOnUnit")!, inSoftware = editor.FindControl<ToggleButton>("EqInSoftware")!;
        Assert.False(badge.IsVisible);
        Assert.False(note.IsVisible);
        Assert.False(choice.IsVisible);
        daemon.Device = "unit";
        daemon.Pushes.Release();
        await Until(() => eq.OnDevice);
        Pump(30);
        foreach (double width in new[] { 640d, 1040, 2400 })
        {
            Layout(editor, width, width < 1000 ? 620 : 760);
            Assert.True(badge.IsEffectivelyVisible, $"the badge is hidden at {width}");
            AssertInside(badge, editor);
            foreach (Control action in editor.FindControl<WrapPanel>("EqActions")!.Children) AssertNoOverlap([badge, action]);
            Assert.False(note.IsVisible);
        }
        Assert.Equal(Localizer.Format("EqHardwareBadgeTip", "Wave XLR Dock MK.2"), ToolTip.GetTip(badge));
        Assert.Equal(Localizer.Text("EqHardwareBadge"), badge.GetVisualDescendants().OfType<TextBlock>().Single().Text);
        if (eq.Enabled && !eq.Comparing)
            Assert.Equal(Localizer.Format("EqStatusDeviceNamed", "Wave XLR Dock MK.2"), editor.FindControl<TextBlock>("EqStatus")!.Text);
        Assert.Equal(Localizer.Format("EqButtonTipDevice", "Wave XLR Dock MK.2"), ToolTip.GetTip(window.FindControl<Button>("Xlr1Eq")!));

        // The switch, on the unit.
        Assert.True(choice.IsEffectivelyVisible);
        Assert.True(onUnit.IsChecked);
        Assert.False(inSoftware.IsChecked);
        Assert.Equal(Localizer.Text("EqProcessingDevice"), onUnit.Content);
        Assert.Equal(Localizer.Text("EqProcessingSoftware"), inSoftware.Content);
        Assert.Equal(Localizer.Format("EqProcessingDeviceTip", "Wave XLR Dock MK.2"), ToolTip.GetTip(onUnit));
        Assert.Equal(Localizer.Text("EqProcessingSoftwareTip"), ToolTip.GetTip(inSoftware));
        CheckFooter(editor, "on the unit");
        Layout(editor, 820, 600);
        Capture(editor, "eq-editor-on-the-unit");
        CaptureBothSizings(editor, "eq-footer-on-the-unit");

        // The real pointer on the side already chosen sends nothing and leaves it lit.
        daemon.Commands.Clear();
        PointerClick(editor, onUnit);
        Pump(120);
        Assert.Empty(daemon.Commands);
        Assert.True(onUnit.IsChecked);
        Assert.False(inSoftware.IsChecked);

        // In software: one setEqProcessing, and the editor follows the daemon.
        PointerClick(editor, inSoftware);
        JsonNode sent = (await Sent(daemon, 1))[0];
        Assert.Equal("setEqProcessing", sent["cmd"]!.GetValue<string>());
        Assert.Equal("xlr1", sent["channel"]!.GetValue<string>());
        Assert.Equal("software", sent["value"]!.GetValue<string>());
        await Until(() => eq.Processing == "software" && !eq.OnDevice);
        Pump(30);
        Assert.True(inSoftware.IsChecked);
        Assert.False(onUnit.IsChecked);
        Assert.False(badge.IsVisible);
        // The switch says it is software by choice; the note under it has nothing to add.
        Assert.False(note.IsVisible);
        if (eq.Enabled && !eq.Comparing)
            Assert.Equal(Localizer.Text("EqStatusSoftware"), editor.FindControl<TextBlock>("EqStatus")!.Text);
        Assert.Equal(Localizer.Text("EqButtonTip") + " " + Localizer.Format("EqDeviceBySoftware", "Wave XLR Dock MK.2"),
            ToolTip.GetTip(window.FindControl<Button>("Xlr1Eq")!));
        CheckFooter(editor, "in software");
        Layout(editor, 820, 600);
        CaptureBothSizings(editor, "eq-footer-in-software");
        PointerClick(editor, inSoftware);
        Pump(120);
        Assert.Empty(daemon.Commands);
        Assert.True(inSoftware.IsChecked);

        // Back on the unit.
        PointerClick(editor, onUnit);
        sent = (await Sent(daemon, 1))[0];
        Assert.Equal("device", sent["value"]!.GetValue<string>());
        await Until(() => eq.OnDevice);
        Pump(30);
        Assert.True(onUnit.IsChecked);
        Assert.True(badge.IsVisible);

        // Older firmware: the switch lights where the EQ runs, the unit's side cannot be pressed, and the
        // note under it says why the unit does not run it.
        daemon.Device = "old";
        daemon.Pushes.Release();
        await Until(() => !eq.OnDevice && eq.HasDeviceNote);
        Pump(30);
        Assert.False(badge.IsVisible);
        Assert.True(note.IsEffectivelyVisible);
        Assert.True(choice.IsEffectivelyVisible);
        Assert.False(onUnit.IsChecked);
        Assert.True(inSoftware.IsChecked);
        Assert.False(onUnit.IsEnabled);
        Assert.Equal(note.Text, ToolTip.GetTip(onUnit));
        Assert.Equal(Localizer.Format("EqDeviceFirmwareOld", "Wave XLR Dock MK.2", "1.0.0", "1.0.15"), note.Text);
        Layout(editor, 820, 600);
        Capture(editor, "eq-editor-older-firmware");
        CaptureBothSizings(editor, "eq-footer-older-firmware");

        // The longest note this language has, from 640 to 2400 pixels in both sizings.
        (string reason, string text) = new[]
        {
            ("firmwareOld", Localizer.Format("EqDeviceFirmwareOld", "Wave XLR Dock MK.2", "1.0.0", "1.0.15")),
            ("firmwareUnknown", Localizer.Format("EqDeviceFirmwareUnknown", "Wave XLR Dock MK.2", "1.0.15")),
            ("unsupportedBlock", Localizer.Format("EqDeviceUnsupported", "Wave XLR Dock MK.2")),
            ("failed", Localizer.Format("EqDeviceFailed", "Wave XLR Dock MK.2")),
        }.MaxBy(r => r.Item2.Length);
        daemon.Reason = reason;
        daemon.Pushes.Release();
        await Until(() => eq.DeviceNote == text);
        Pump(30);
        Assert.Equal(text, note.Text);
        CheckFooter(editor, $"the longest note ({reason})");
        Layout(editor, 820, 600);
        daemon.Reason = "firmwareOld";

        // A daemon from before the choice: no switch, whatever it says about the unit.
        daemon.Device = "unit";
        daemon.Legacy = true;
        daemon.Pushes.Release();
        await Until(() => !eq.HasProcessingChoice && !eq.HasDeviceNote);
        Pump(30);
        Assert.False(choice.IsVisible);
        Assert.False(badge.IsVisible);
        daemon.Legacy = false;

        daemon.Device = null;
        daemon.Pushes.Release();
        await Until(() => !eq.HasDeviceNote && !eq.HasProcessingChoice && eq.Backend != "device");
        Pump(30);
        Assert.False(badge.IsVisible);
        Assert.False(note.IsVisible);
        Assert.False(choice.IsVisible);
        Assert.Equal(Localizer.Text("EqButtonTip"), ToolTip.GetTip(window.FindControl<Button>("Xlr1Eq")!));
    }

    /// <summary>
    /// XLR 2 on the Wave XLR Pro's second input: the badge, the EQ button's
    /// tooltip and the switch name the Pro, and the footer fits from 640 to
    /// 2400 pixels; on firmware before 1.0.14 the note gives the Pro's own
    /// minimum, not the MK.2 family's. XLR 1's editor is not involved.
    /// </summary>
    private static async Task ProHardware(MainWindow window, EqWindow editor, EqDaemon daemon, MicEqViewModel eq)
    {
        Border badge = editor.FindControl<Border>("EqHardwareBadge")!;
        TextBlock note = editor.FindControl<TextBlock>("EqDeviceNote")!;
        WrapPanel choice = editor.FindControl<WrapPanel>("EqProcessing")!;
        ToggleButton onUnit = editor.FindControl<ToggleButton>("EqOnUnit")!;
        Assert.False(badge.IsVisible);
        daemon.Device2 = "pro";
        daemon.Pushes.Release();
        await Until(() => eq.OnDevice);
        Pump(30);
        Layout(editor, 1040, 760);
        Assert.True(badge.IsEffectivelyVisible);
        Assert.True(choice.IsEffectivelyVisible);
        Assert.True(onUnit.IsChecked);
        Assert.False(note.IsVisible);
        await OpensWithEveryBand(eq, window);
        Assert.Equal(Localizer.Format("EqHardwareBadgeTip", "Wave XLR Pro"), ToolTip.GetTip(badge));
        Assert.Equal(Localizer.Format("EqButtonTipDevice", "Wave XLR Pro"), ToolTip.GetTip(window.FindControl<Button>("Xlr2Eq")!));
        Assert.Equal(Localizer.Format("EqProcessingDeviceTip", "Wave XLR Pro"), ToolTip.GetTip(onUnit));
        Assert.Equal(Localizer.Text("EqButtonTip"), ToolTip.GetTip(window.FindControl<Button>("Xlr1Eq")!));
        CheckFooter(editor, "on the Wave XLR Pro");
        Layout(editor, 820, 600);
        Capture(editor, "eq-editor-on-the-pro");

        daemon.Device2 = "proOld";
        daemon.Pushes.Release();
        await Until(() => !eq.OnDevice && eq.HasDeviceNote);
        Pump(30);
        Assert.False(badge.IsVisible);
        Assert.True(note.IsEffectivelyVisible);
        Assert.Equal(Localizer.Format("EqDeviceFirmwareOld", "Wave XLR Pro", "1.0.13", "1.0.14"), note.Text);
        CheckFooter(editor, "the Wave XLR Pro on older firmware");
        Layout(editor, 820, 600);
        Capture(editor, "eq-editor-pro-older-firmware");

        daemon.Device2 = null;
        daemon.Pushes.Release();
        await Until(() => !eq.HasDeviceNote && !eq.HasProcessingChoice);
        Pump(30);
        Assert.False(badge.IsVisible);
        Assert.False(choice.IsVisible);
    }

    /// <summary>
    /// The footer from 640 to 2400 pixels in Standard and Touch sizing: the
    /// switch, the note and the status line drawn whole inside the editor, in
    /// that order and clear of each other and of the bands, with the graph
    /// still tall enough to work on.
    /// </summary>
    private static void CheckFooter(EqWindow editor, string state)
    {
        WrapPanel choice = editor.FindControl<WrapPanel>("EqProcessing")!;
        TextBlock note = editor.FindControl<TextBlock>("EqDeviceNote")!, status = editor.FindControl<TextBlock>("EqStatus")!;
        ScrollViewer bands = editor.FindControl<ScrollViewer>("EqBandScroll")!;
        foreach (bool touch in new[] { false, true })
        {
            SkinService.ApplyControlSizing(touch);
            foreach (double width in new[] { 640d, 1040, 1800, 2400 })
            {
                Layout(editor, width, width < 1000 ? 620 : 760);
                string where = $"footer {state}, {Sizing(touch)} at {width}";
                Control[] parts = [.. new Control[] { choice, note, status }.Where(c => c.IsVisible)];
                foreach (Control part in parts) AssertInside(part, editor);
                foreach (Control part in choice.Children) AssertInside(part, editor);
                AssertNoOverlap([.. parts, bands]);
                AssertNoOverlap([.. choice.Children]);
                // Top to bottom: the bands, then the switch, the note and the status line.
                double last = bands.TranslatePoint(new Point(0, bands.Bounds.Height), editor)!.Value.Y;
                foreach (Control part in parts)
                {
                    double top = part.TranslatePoint(default, editor)!.Value.Y;
                    Assert.True(top >= last - 0.5, $"{part.Name} starts at {top:0.#}, above {last:0.#}, {where}");
                    last = top + part.Bounds.Height;
                }
                // Each text is drawn whole, wrapped rather than cut.
                foreach (TextBlock text in parts.OfType<TextBlock>())
                    Assert.DoesNotContain(text.TextLayout.TextLines, l => l.HasCollapsed);
                Assert.True(editor.Graph.Plot.Height > 120, $"the graph's plot is {editor.Graph.Plot.Height} tall, {where}");
                foreach (EqHandle handle in editor.Graph.Handles) AssertInside(handle, editor);
            }
        }
        SkinService.ApplyControlSizing(false);
    }

    /// <summary>
    /// A new editor opens tall enough to show every band without a scroll bar,
    /// with the profile row and the choice of where the EQ runs above and below them.
    /// </summary>
    private static async Task OpensWithEveryBand(MicEqViewModel eq, Window owner)
    {
        foreach (string skin in new[] { SkinPackage.DefaultId, "broadcast" })
        {
            SkinLayoutWindowTests.Wear(skin);
            await OpensWithEveryBand(eq, owner, skin);
        }
        SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
    }

    private static async Task OpensWithEveryBand(MicEqViewModel eq, Window owner, string skin)
    {
        var fresh = new EqWindow { DataContext = eq };
        try
        {
            fresh.Show(owner);
            ScrollViewer bands = fresh.FindControl<ScrollViewer>("EqBandScroll")!;
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (!(bands.Viewport.Height > 0 && bands.Extent.Height <= bands.Viewport.Height + 0.5) && DateTime.UtcNow < deadline)
            {
                Pump(30);
                await Task.Yield();
            }
            Assert.True(bands.Viewport.Height > 0 && bands.Extent.Height <= bands.Viewport.Height + 0.5,
                $"{skin}: the bands need {bands.Extent.Height:0.#} and get {bands.Viewport.Height:0.#} in an editor {fresh.Bounds.Height:0.#} tall");
            AssertInside(fresh.FindControl<StackPanel>("EqFooter")!, fresh);
        }
        finally { fresh.Close(); }
    }

    /// <summary>The editor at its opening size in Standard and Touch sizing.</summary>
    private static void CaptureBothSizings(EqWindow editor, string name)
    {
        foreach (bool touch in new[] { false, true })
        {
            SkinService.ApplyControlSizing(touch);
            Layout(editor, 820, 600);
            Capture(editor, $"{name}-{Sizing(touch)}");
        }
        SkinService.ApplyControlSizing(false);
        Layout(editor, 820, 600);
    }

    /// <summary>Press and release the real pointer over the middle of a control.</summary>
    private static void PointerClick(Window window, Control control)
    {
        using var pointer = new XPointer();
        window.Activate();
        Pump(40);
        PixelPoint at = control.PointToScreen(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2));
        pointer.MoveTo(at.X, at.Y);
        Pump(40);
        pointer.Click();
        Pump(40);
        pointer.MoveTo(0, 0);
    }

    /// <summary>The editor fits at every width: its actions, the graph, the handles and every band's fields.</summary>
    private static async Task VariableBandEditor(EqWindow editor, EqDaemon daemon, MicEqViewModel eq)
    {
        daemon.Legacy = false; daemon.VariableBands = true;
        daemon.Device = "unit"; daemon.Processing["xlr1"] = "device";
        daemon.Pushes.Release();
        await Until(() => eq.VariableBands);
        // Four bands with Add band above them and Remove beside each, on the unit:
        // the editor opens with every row showing.
        Assert.Equal(4, eq.Bands.Count);
        await OpensWithEveryBand(eq, editor.Owner as Window ?? editor);
        while (eq.CanAddBand) Click(editor.FindControl<Button>("EqAddBand")!);
        await Until(() => daemon.State()["mixer"]!["channels"]![0]!["eq"]!["bands"]!.AsArray().Count == 16 && !eq.OnDevice);
        SkinService.ApplyControlSizing(true);
        Layout(editor, 640, 600);
        Assert.Equal(16, editor.Graph.Handles.Count);
        Assert.False(editor.FindControl<Button>("EqAddBand")!.IsEnabled);
        Assert.True(editor.FindControl<ScrollViewer>("EqBandScroll")!.Extent.Height > editor.FindControl<ScrollViewer>("EqBandScroll")!.Viewport.Height);
        Grid row = editor.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "EqBandRow" && g.DataContext == eq.Bands[15]);
        row.BringIntoView(); Pump(40);
        AssertInside(row, editor);
        AssertNoOverlap(row.Children.ToArray());
        row.Children.OfType<NumericUpDown>().Single(n => n.Name == "EqBandGain").Value = 6;
        await Until(() => daemon.State()["mixer"]!["channels"]![0]!["eq"]!["bands"]![15]!["gain"]!.GetValue<double>() == 6);
        Capture(editor, "eq-variable-sixteen-touch-640");
        CheckVariableLanguages(eq);
        Button remove = editor.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "EqRemoveBand" && b.DataContext == eq.Bands[1]);
        Click(remove);
        Assert.Equal(15, eq.Bands.Count);
        await Until(() => daemon.Commands.LastOrDefault().Command?["eq"]?["bands"] is JsonArray bands && bands.Count == 15);
        int sent = daemon.Commands.Count;
        Click(remove);
        Assert.Equal(sent, daemon.Commands.Count);
        eq.Reset();
        await Until(() => eq.Bands.Count == 4 && eq.OnDevice);
        Assert.Equal(4, editor.Graph.Handles.Count);
        daemon.VariableBands = false;
        SkinService.ApplyControlSizing(false);
    }

    private static void CheckVariableLanguages(MicEqViewModel eq)
    {
        string? language = UiSettings.Load().Language;
        try
        {
            foreach (string choice in new[] { "en", "de", "es" })
            {
                Assert.Null((UiSettings.Load() with { Language = choice }).Save());
                Localizer.Initialize();
                var window = new EqWindow { DataContext = eq };
                try
                {
                    window.Show();
                    foreach (double height in new[] { 480d, 600 })
                    {
                        Layout(window, 640, height);
                        foreach (int band in new[] { 0, 15 })
                        {
                            Grid row = window.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "EqBandRow" && g.DataContext == eq.Bands[band]);
                            row.BringIntoView(); Pump(40);
                            AssertInside(row, window);
                            AssertNoOverlap(row.Children.Where(c => c.IsVisible).ToArray());
                            foreach (NumericUpDown number in row.Children.OfType<NumericUpDown>())
                            {
                                TextBox box = number.GetVisualDescendants().OfType<TextBox>().Single();
                                var widest = new TextBlock { Text = "+12.00", FontSize = box.FontSize, FontFamily = box.FontFamily };
                                widest.Measure(Size.Infinity);
                                Assert.True(box.Bounds.Width - box.Padding.Left - box.Padding.Right >= widest.DesiredSize.Width,
                                    $"{number.Name} clips its value in {choice} at 640x{height}");
                            }
                        }
                        AssertInside(window.FindControl<StackPanel>("EqFooter")!, window);
                        Capture(window, $"eq-variable-{choice}-640-{height}");
                    }
                }
                finally { window.Close(); }
            }
        }
        finally
        {
            Assert.Null((UiSettings.Load() with { Language = language }).Save());
            Localizer.Initialize();
        }
    }

    private static void CheckEditor(EqWindow editor, MicEqViewModel eq)
    {
        foreach (bool touch in new[] { false, true })
        {
            SkinService.ApplyControlSizing(touch);
            foreach (double width in new[] { 640d, 1040, 1800, 2400 })
            {
                Layout(editor, width, width < 1000 ? 620 : 760);
                string where = $"editor, {Sizing(touch)} at {width}";
                foreach (Control action in editor.FindControl<WrapPanel>("EqActions")!.Children)
                    AssertInside(action, editor);
                EqGraph graph = editor.Graph;
                AssertInside(graph, editor);
                Assert.True(graph.Plot.Height > 120, $"the graph's plot is {graph.Plot.Height} tall, {where}");
                Assert.Equal(4, graph.Handles.Count);
                foreach (EqHandle handle in graph.Handles)
                {
                    AssertInside(handle, editor);
                    Assert.Equal(handle.Band.Description, AutomationProperties.GetName(handle));
                    Assert.Equal(handle.Band.Tip, ToolTip.GetTip(handle));
                }
                foreach (Grid row in editor.GetVisualDescendants().OfType<Grid>().Where(g => g.Name == "EqBandRow"))
                {
                    Control[] fields = [.. row.Children.Where(c => c.IsVisible)];
                    Assert.Equal(6, fields.Length);
                    foreach (Control field in fields) AssertInside(field, editor);
                    AssertNoOverlap(fields);
                    // Every number is drawn whole in its field: "15049" and "+12.00" at the most.
                    foreach (NumericUpDown number in row.Children.OfType<NumericUpDown>())
                    {
                        Assert.Equal(width >= EqWindow.NarrowWidth, number.ShowButtonSpinner);
                        TextBox box = number.GetVisualDescendants().OfType<TextBox>().Single();
                        var widest = new TextBlock { Text = "+12.00", FontSize = box.FontSize, FontFamily = box.FontFamily };
                        widest.Measure(Size.Infinity);
                        Assert.True(box.Bounds.Width - box.Padding.Left - box.Padding.Right >= widest.DesiredSize.Width,
                            $"{number.Name} leaves {box.Bounds.Width:0.#} for its value, {where}");
                    }
                }
                AssertInside(editor.FindControl<TextBlock>("EqStatus")!, editor);
                Capture(editor, $"eq-editor-{Sizing(touch)}-{width}");
            }
        }
        SkinService.ApplyControlSizing(false);
        Layout(editor, 820, 600);
    }

    /// <summary>
    /// Press band 2's handle, move it right and up in small steps over about
    /// half a second, release. The daemon hears setEqBand for band 2 only,
    /// each value on the daemon's steps, no more often than once a send
    /// interval, ending on where the handle stopped; the editor then shows the
    /// daemon's values.
    /// </summary>
    private static async Task Drag(EqWindow editor, EqDaemon daemon, MicEqViewModel eq)
    {
        EqGraph graph = editor.Graph;
        EqHandle handle = graph.Handles[1];
        using var pointer = new XPointer();
        PixelPoint start = handle.PointToScreen(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2));
        pointer.MoveTo(start.X, start.Y);
        Pump(60);
        var watch = Stopwatch.StartNew();
        pointer.SetButton(true);
        Pump(40);
        for (int i = 1; i <= 50; i++)
        {
            pointer.MoveTo(start.X + i * 4, start.Y - i * 2);
            Pump(10);
            if (i == 30) Capture(editor, "eq-editor-drag");
        }
        Assert.True(graph.IsDragging);
        pointer.SetButton(false);
        Pump(80);
        Assert.False(graph.IsDragging);
        await Until(() => daemon.Eqs["xlr1"].Bands[1].Frequency == eq.Bands[1].Frequency && daemon.Eqs["xlr1"].Bands[1].Gain == eq.Bands[1].Gain);

        (long At, JsonNode Command)[] sent = [.. daemon.Commands];
        // Every command of the drag left after the watch started and before
        // the daemon had them all, a send interval at least after the one
        // before it: however late a loaded machine runs the pointer's moves
        // or the sender's timer, there is room for no more than this many.
        double dragged = watch.Elapsed.TotalMilliseconds;
        Assert.NotEmpty(sent);
        Assert.All(sent, c =>
        {
            Assert.Equal("setEqBand", c.Command["cmd"]!.GetValue<string>());
            Assert.Equal("xlr1", c.Command["channel"]!.GetValue<string>());
            Assert.Equal(2, c.Command["band"]!.GetValue<int>());
            Assert.Null(c.Command["type"]);
            Assert.Null(c.Command["enabled"]);
            if (c.Command["frequency"] is { } hz) Assert.Equal(Math.Round(hz.GetValue<double>()), hz.GetValue<double>());
            if (c.Command["gain"] is { } db) Assert.Equal(DaemonEq.SnapGain(db.GetValue<double>()), db.GetValue<double>());
        });
        // At most one a send interval, and the last one is where the handle stopped.
        Assert.True(sent.Length <= dragged / MicEqViewModel.SendInterval.TotalMilliseconds + 1,
            $"{sent.Length} changes in a drag of {dragged:0} ms");
        Assert.True(sent.Length >= 5, $"only {sent.Length} changes in a drag of {dragged:0} ms");
        Assert.True(eq.Bands[1].Frequency > 400, $"the handle moved to {eq.Bands[1].Frequency} Hz");
        Assert.True(eq.Bands[1].Gain > 2, $"the handle moved to {eq.Bands[1].Gain} dB");
        // The editor shows what the daemon reported.
        Assert.Equal(daemon.Eqs["xlr1"].Bands[1].Frequency, eq.Bands[1].Frequency);
        Assert.Equal(daemon.Eqs["xlr1"].Bands[1].Gain, eq.Bands[1].Gain);
        Assert.Equal(EqText.Frequency(eq.Bands[1].Frequency), eq.Bands[1].FrequencyText);
        daemon.Commands.Clear();
    }

    /// <summary>The commands the daemon took since the last call, once at least <paramref name="count"/> came.</summary>
    private static async Task<JsonNode[]> Sent(EqDaemon daemon, int count)
    {
        await Until(() => daemon.Commands.Count >= count);
        Pump(60);
        var taken = new List<JsonNode>();
        while (daemon.Commands.TryDequeue(out var c)) taken.Add(c.Command);
        Assert.True(taken.Count == count, $"expected {count} commands, the daemon took {string.Join(", ", taken.Select(c => c.ToJsonString()))}");
        return [.. taken];
    }

    private static void AssertBand(JsonNode cmd, int band, string field, JsonNode? value)
    {
        Assert.Equal("setEqBand", cmd["cmd"]!.GetValue<string>());
        Assert.Equal(band, cmd["band"]!.GetValue<int>());
        string[] fields = [.. cmd.AsObject().Select(p => p.Key).Where(k => k is not ("cmd" or "channel" or "band"))];
        Assert.Equal([field], fields);
        Assert.Equal(value?.ToJsonString(), cmd[field]!.ToJsonString());
    }

    private const ulong Right = 0xff53, Up = 0xff52, PageUp = 0xff55, Space = 0x20, ShiftKey = 0xffe1, Tab = 0xff09;

    /// <summary>
    /// The wheel, the keyboard, a double click and the type menu, with the
    /// real pointer and keys, each send one setEqBand with the one field
    /// they change, on the daemon's steps.
    /// </summary>
    private static async Task Gestures(EqWindow editor, EqDaemon daemon, MicEqViewModel eq)
    {
        EqGraph graph = editor.Graph;
        using var pointer = new XPointer();
        PixelPoint Centre(EqHandle h) => h.PointToScreen(new Point(h.Bounds.Width / 2, h.Bounds.Height / 2));

        // One wheel notch up over band 3: Q one step of the grid up.
        EqBandViewModel band3 = eq.Bands[2];
        double q = band3.Q;
        PixelPoint at = Centre(graph.Handles[2]);
        pointer.MoveTo(at.X, at.Y);
        Pump(60);
        pointer.SetButton(true, 4);
        pointer.SetButton(false, 4);
        Pump(60);
        AssertBand((await Sent(daemon, 1))[0], 3, "q", DaemonEq.SnapQ(Math.Pow(10, DaemonEq.QLog10(q) + DaemonEq.QLog10Step)));
        Assert.Equal(DaemonEq.SnapQ(Math.Pow(10, DaemonEq.QLog10(q) + DaemonEq.QLog10Step)), band3.Q);
        Assert.Same(band3, eq.SelectedBand);

        // The keyboard on the selected handle: a twelfth of an octave, a quarter decibel with Shift, a Q step, the switch.
        graph.Handles[2].Focus();
        pointer.Focus(editor.TryGetPlatformHandle()!.Handle);
        Pump(40);
        Assert.True(graph.Handles[2].IsFocused);
        double hz = band3.Frequency;
        pointer.Key(Right);
        Pump(40);
        AssertBand((await Sent(daemon, 1))[0], 3, "frequency", DaemonEq.SnapFrequency(hz * Math.Pow(2, 1.0 / 12)));
        pointer.SetKey(ShiftKey, true);
        pointer.Key(Up);
        pointer.SetKey(ShiftKey, false);
        Pump(40);
        AssertBand((await Sent(daemon, 1))[0], 3, "gain", 0.25);
        q = band3.Q;
        pointer.Key(PageUp);
        Pump(40);
        AssertBand((await Sent(daemon, 1))[0], 3, "q", DaemonEq.SnapQ(Math.Pow(10, DaemonEq.QLog10(q) + DaemonEq.QLog10Step)));
        pointer.Key(Space);
        Pump(40);
        AssertBand((await Sent(daemon, 1))[0], 3, "enabled", false);
        pointer.Key(Space);
        Pump(40);
        AssertBand((await Sent(daemon, 1))[0], 3, "enabled", true);
        // Tab goes on to band 4's handle.
        pointer.Key(Tab);
        Pump(40);
        Assert.True(graph.Handles[3].IsFocused);
        Assert.Same(eq.Bands[3], eq.SelectedBand);

        // A double click switches band 4, which starts off, on.
        Assert.False(eq.Bands[3].Enabled);
        at = Centre(graph.Handles[3]);
        pointer.MoveTo(at.X, at.Y);
        Pump(40);
        pointer.Click();
        Pump(20);
        pointer.Click();
        Pump(60);
        AssertBand((await Sent(daemon, 1))[0], 4, "enabled", true);
        Assert.True(eq.Bands[3].Enabled);

        // A right click opens band 3's type menu: the six types with their icons; Notch takes the gain field away.
        at = Centre(graph.Handles[2]);
        pointer.MoveTo(at.X, at.Y);
        Pump(40);
        pointer.SetButton(true, 3);
        pointer.SetButton(false, 3);
        Pump(120);
        ContextMenu menu = graph.Handles[2].ContextMenu!;
        Assert.True(menu.IsOpen);
        MenuItem[] items = [.. menu.Items.OfType<MenuItem>()];
        Assert.Equal(EqTypeChoice.All.Select(c => c.Name).Append(Localizer.Text("EqBandOn")), items.Select(i => i.Header));
        Assert.Equal(Localizer.Language == "en"
            ? ["High pass", "Low shelf", "Bell", "Notch", "High shelf", "Low pass", "Band on"]
            : new[] { "EqTypeLowCut", "EqTypeLowShelf", "EqTypePeak", "EqTypeNotch", "EqTypeHighShelf", "EqTypeHighCut", "EqBandOn" }.Select(Localizer.Text), items.Select(i => (string)i.Header!));
        Assert.All(items.Take(6), i => Assert.IsType<Avalonia.Controls.Shapes.Path>(i.Icon));
        Assert.True(items[2].IsChecked);
        Capture(editor, "eq-editor-type-menu", menu);
        items[3].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
        AssertBand((await Sent(daemon, 1))[0], 3, "type", "notch");
        Pump(40);
        Assert.False(BandField<NumericUpDown>(editor, 3, "EqBandGain").IsEnabled);
        Assert.False(band3.UsesGain);
        // The gain is kept for the type that comes back.
        Assert.Equal(0.25, band3.Gain);
    }

    /// <summary>A field in one band's row of the editor.</summary>
    private static T BandField<T>(EqWindow editor, int band, string name) where T : Control =>
        editor.GetVisualDescendants().OfType<Grid>().Where(g => g.Name == "EqBandRow").ElementAt(band - 1)
            .GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    /// <summary>
    /// The rows' fields, the switch and the comparison: a type and a value
    /// typed in send setEqBand, the Q spinner steps on the grid, the switch
    /// sends setEqEnabled and lights the INPUTS button once the daemon runs
    /// the EQ, and the comparison switches it off and on again while the
    /// switch stays on.
    /// </summary>
    private static async Task Controls(EqWindow editor, EqDaemon daemon, MicEqViewModel eq, MainWindow window)
    {
        BandField<ComboBox>(editor, 2, "EqBandType").SelectedItem = EqTypeChoice.Of("lowShelf");
        AssertBand((await Sent(daemon, 1))[0], 2, "type", "lowShelf");
        BandField<NumericUpDown>(editor, 2, "EqBandFrequency").Value = 1000.4m;
        AssertBand((await Sent(daemon, 1))[0], 2, "frequency", 1000);
        Assert.Equal(1000m, BandField<NumericUpDown>(editor, 2, "EqBandFrequency").Value);
        // The spinner's 0.01 is finer than the grid near 1, so it moves one grid step.
        NumericUpDown qField = BandField<NumericUpDown>(editor, 2, "EqBandQ");
        double q = eq.Bands[1].Q;
        qField.Value += 0.01m;
        AssertBand((await Sent(daemon, 1))[0], 2, "q", DaemonEq.SnapQ(Math.Pow(10, DaemonEq.QLog10(q) + DaemonEq.QLog10Step)));
        Assert.Equal(Math.Round((decimal)eq.Bands[1].Q, 2), qField.Value);
        // A gain typed past the range is held to it.
        BandField<NumericUpDown>(editor, 2, "EqBandGain").Value = 30m;
        AssertBand((await Sent(daemon, 1))[0], 2, "gain", 12);

        ToggleButton on = editor.FindControl<ToggleButton>("EqSwitch")!, compare = editor.FindControl<ToggleButton>("EqCompare")!;
        Assert.False(compare.IsEnabled);
        on.IsChecked = true;
        JsonNode sent = (await Sent(daemon, 1))[0];
        Assert.Equal("setEqEnabled", sent["cmd"]!.GetValue<string>());
        Assert.True(sent["value"]!.GetValue<bool>());
        await Until(() => eq.IsLit);
        Layout(window, 1040, 1100);
        Assert.Contains("fxOn", window.FindControl<Button>("Xlr1Eq")!.Classes);
        Assert.Equal(Localizer.Format("EqButtonNameOn", "XLR 1"), AutomationProperties.GetName(window.FindControl<Button>("Xlr1Eq")!));
        Assert.Equal(Localizer.Text("EqStatusSoftware"), editor.FindControl<TextBlock>("EqStatus")!.Text);
        Capture(editor, "eq-editor-on");

        compare.IsChecked = true;
        sent = (await Sent(daemon, 1))[0];
        Assert.Equal("setEqEnabled", sent["cmd"]!.GetValue<string>());
        Assert.False(sent["value"]!.GetValue<bool>());
        await Until(() => !daemon.Eqs["xlr1"].Enabled);
        Pump(60);
        Assert.True(on.IsChecked);
        Assert.True(eq.Comparing);
        Assert.Equal(Localizer.Text("EqStatusComparing"), editor.FindControl<TextBlock>("EqStatus")!.Text);
        compare.IsChecked = false;
        sent = (await Sent(daemon, 1))[0];
        Assert.True(sent["value"]!.GetValue<bool>());
        await Until(() => eq.IsLit);
    }

    /// <summary>
    /// Copy on XLR 1 and paste on XLR 2 send one setEq with XLR 1's EQ; Reset
    /// asks first, then sends one setEq with the default bands and the switch
    /// as it was. Closing an editor while it compares switches the EQ back on.
    /// </summary>
    private static async Task<EqWindow> CopyAndReset(EqWindow editor, EqDaemon daemon, MicEqViewModel eq1, MicEqViewModel eq2, MainWindow window)
    {
        Button paste = editor.FindControl<Button>("EqPaste")!;
        Assert.False(paste.IsEnabled);
        Click(editor.FindControl<Button>("EqCopy")!);
        Pump(20);
        Assert.True(paste.IsEnabled);
        Click(window.FindControl<Button>("Xlr2Eq")!);
        EqWindow second = await Editor(window, eq2);
        Assert.NotSame(editor, second);
        Click(second.FindControl<Button>("EqPaste")!);
        JsonNode sent = (await Sent(daemon, 1))[0];
        Assert.Equal("setEq", sent["cmd"]!.GetValue<string>());
        Assert.Equal("xlr2", sent["channel"]!.GetValue<string>());
        await Until(() => daemon.Eqs["xlr2"] == daemon.Eqs["xlr1"]);
        Assert.Equal(eq1.Current, eq2.Current);
        Capture(second, "eq-editor-pasted");

        Click(second.FindControl<Button>("EqReset")!);
        Window confirm = await Dialog(second);
        Button yes = confirm.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("danger"));
        Assert.Equal(Localizer.Text("EqReset"), yes.Content);
        Click(yes);
        sent = (await Sent(daemon, 1))[0];
        Assert.Equal("setEq", sent["cmd"]!.GetValue<string>());
        await Until(() => daemon.Eqs["xlr2"] == DaemonEq.Default with { Enabled = true });
        Assert.Equal([36d, 310, 2172, 15049], eq2.Bands.Select(b => b.Frequency));
        Assert.True(eq2.Enabled);

        // Closing while comparing brings the EQ back.
        second.FindControl<ToggleButton>("EqCompare")!.IsChecked = true;
        Assert.False((await Sent(daemon, 1))[0]["value"]!.GetValue<bool>());
        second.Close();
        Assert.True((await Sent(daemon, 1))[0]["value"]!.GetValue<bool>());
        Click(window.FindControl<Button>("Xlr2Eq")!);
        return await Editor(window, eq2);
    }

    /// <summary>
    /// EQ profiles through the editors' own list, buttons and dialogs, against
    /// the daemon's store: the built-in profiles come first in both editors,
    /// named in the UI's language and marked as built in; picking one in XLR
    /// 2's list sends one loadEqProfile, and it cannot be renamed or deleted.
    /// Save on XLR 1 asks for a name and sends saveEqProfile, XLR 1's list
    /// shows it and XLR 2's lists it at once, choosing nothing and sending
    /// nothing; picking it there sends one loadEqProfile and XLR 2 takes XLR
    /// 1's bands with the EQ on. An edit clears the choice, Save then offers the
    /// picked name, and picking the profile again loads it again. Saving under
    /// a name in use asks before replacing it; Rename and Delete ask and act on
    /// the chosen profile in both editors. A file the daemon cannot read whole
    /// shows its line under the row, wrapped inside the editor, and nothing is
    /// sent. The row fits from 640 to 2400 pixels in both sizings and at 640
    /// in every language.
    /// </summary>
    private static async Task Profiles(EqWindow first, EqWindow second, EqDaemon daemon, MicEqViewModel eq1, MicEqViewModel eq2)
    {
        string path = OpenXLR.Core.Mixing.EqProfileStore.FilePath;
        if (File.Exists(path)) File.Delete(path);
        foreach (EqWindow editor in new[] { first, second }) editor.Profiles!.Reread();
        await Until(() => first.Profiles!.Profiles.Count == EqPresets.All.Count && second.Profiles!.Profiles.Count == EqPresets.All.Count);
        CheckProfileRow(first, "profiles");
        ComboBox list1 = first.FindControl<ComboBox>("EqProfileList")!, list2 = second.FindControl<ComboBox>("EqProfileList")!;
        Assert.Equal(Localizer.Text("EqProfileChoose"), list1.PlaceholderText);
        Assert.Equal(Localizer.Text("EqProfileListName"), AutomationProperties.GetName(list1));
        Assert.Equal(Localizer.Text("EqProfileListTip"), ToolTip.GetTip(list1));
        // No Load button: the list loads.
        Assert.Null(first.FindControl<Button>("EqProfileLoad"));
        Assert.Equal(["EqProfileLabel", "EqProfileList", "EqProfileSave", "EqProfileRename", "EqProfileDelete"],
            first.FindControl<WrapPanel>("EqProfileActions")!.Children.Select(c => c.Name));
        // Neither EQ holds a listed profile.
        Assert.Null(list1.SelectedItem);
        Assert.Null(list2.SelectedItem);
        foreach (string name in new[] { "EqProfileRename", "EqProfileDelete" })
            Assert.False(first.FindControl<Button>(name)!.IsEnabled, name);
        Assert.True(first.FindControl<Button>("EqProfileSave")!.IsEnabled);
        Assert.Equal(Localizer.Text("Save"), first.FindControl<Button>("EqProfileSave")!.Content);

        // The built-in profiles: Mic setup's presets, first, in the UI's language and marked as built in.
        Assert.Equal(EqPresets.All.Select(p => p.Id), first.Profiles!.Profiles.Select(p => p.Id));
        Assert.Equal(EqPresets.All.Select(p => Localizer.Format("EqProfileBuiltInName", MicSetupPreset.NameOf(p.Key)!)),
            list1.Items.Cast<EqProfile>().Select(p => p.ToString()));
        // Picking one in the list is the load: one loadEqProfile, and nothing more as the state comes back.
        daemon.Commands.Clear();
        list2.SelectedItem = second.Profiles!.Profiles[2];
        JsonNode sent = (await Sent(daemon, 1))[0];
        Assert.Equal("loadEqProfile", sent["cmd"]!.GetValue<string>());
        Assert.Equal("xlr2", sent["channel"]!.GetValue<string>());
        Assert.Equal("builtin:broadcast", sent["profile"]!.GetValue<string>());
        await Until(() => daemon.Eqs["xlr2"].Equals(OpenXLR.Core.Mixing.EqPresets.Broadcast.Eq));
        await Until(() => MicSetupPresets.Broadcast.Holds(eq2.Current, eq2.Limits));
        Pump(100);
        Assert.Empty(daemon.Commands);
        Assert.Same(second.Profiles!.Profiles[2], list2.SelectedItem);
        Assert.Contains(Localizer.Format("EqProfileBuiltInName", MicSetupPresets.Broadcast.Name),
            list2.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text));
        Assert.False(second.FindControl<Button>("EqProfileRename")!.IsEnabled);
        Assert.False(second.FindControl<Button>("EqProfileDelete")!.IsEnabled);
        Assert.Null(second.Profiles!.Error);
        Capture(second, "eq-editor-profile-builtin");

        // Save on XLR 1: the dialog asks for a name and the daemon keeps the bands as shown.
        Click(first.FindControl<Button>("EqProfileSave")!);
        Window dialog = await Dialog(first);
        Assert.Equal(Localizer.Text("EqProfileSaveTitle"), dialog.Title);
        TextBox input = dialog.GetVisualDescendants().OfType<TextBox>().Single();
        Assert.Equal("", input.Text);
        Assert.Equal(EqProfileLibrary.MaximumNameLength, input.MaxLength);
        Capture(dialog, "eq-profile-save-dialog");
        input.Text = "Podcast";
        Button ok = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.IsDefault);
        Assert.Equal(Localizer.Text("Save"), ok.Content);
        Click(ok);
        // One command: the refresh it brings to both lists loads nothing.
        sent = (await Sent(daemon, 1))[0];
        Assert.Equal("saveEqProfile", sent["cmd"]!.GetValue<string>());
        Assert.Equal("Podcast", sent["name"]!.GetValue<string>());
        Assert.False(sent["replace"]!.GetValue<bool>());
        await Until(() => first.Profiles!.Profiles.Count == EqPresets.All.Count + 1 && first.Profiles.Selected is { Builtin: false });
        EqProfile saved = first.Profiles!.Profiles[EqPresets.All.Count];
        Assert.Equal("Podcast", saved.Name);
        Assert.Equal("saved:Podcast", saved.Id);
        Assert.Equal(eq1.Current.Bands, saved.Bands);
        Assert.Same(saved, list1.SelectedItem);
        Assert.True(first.FindControl<Button>("EqProfileRename")!.IsEnabled);
        // XLR 2's editor lists it and keeps the profile its EQ holds.
        Assert.Equal(EqPresets.All.Count + 1, second.Profiles!.Profiles.Count);
        Assert.Equal("builtin:broadcast", ((EqProfile)list2.SelectedItem!).Id);
        Pump(60);
        Assert.Empty(daemon.Commands);

        // Pick it on XLR 2: one loadEqProfile, and XLR 2 takes XLR 1's bands with the EQ on.
        list2.SelectedItem = second.Profiles!.Profiles[EqPresets.All.Count];
        sent = (await Sent(daemon, 1))[0];
        Assert.Equal("loadEqProfile", sent["cmd"]!.GetValue<string>());
        Assert.Equal("xlr2", sent["channel"]!.GetValue<string>());
        Assert.Equal("saved:Podcast", sent["profile"]!.GetValue<string>());
        await Until(() => daemon.Eqs["xlr2"].Bands.SequenceEqual(daemon.Eqs["xlr1"].Bands) && daemon.Eqs["xlr2"].Enabled);
        await Until(() => eq1.Current.Bands.SequenceEqual(eq2.Current.Bands));
        Assert.True(eq2.Enabled);
        Pump(60);
        Assert.Empty(daemon.Commands);
        Assert.Equal("saved:Podcast", ((EqProfile)list2.SelectedItem!).Id);
        Assert.True(second.FindControl<Button>("EqProfileRename")!.IsEnabled);
        Capture(second, "eq-editor-profile-loaded");

        // An edit on XLR 2 clears its choice; Save offers the picked name, and picking it again loads it again.
        decimal gain = (decimal)eq2.Bands[1].Gain;
        BandField<NumericUpDown>(second, 2, "EqBandGain").Value = gain > 0 ? gain - 1 : gain + 1;
        Assert.Equal("setEqBand", (await Sent(daemon, 1))[0]["cmd"]!.GetValue<string>());
        await Until(() => list2.SelectedItem is null);
        Assert.False(second.FindControl<Button>("EqProfileRename")!.IsEnabled);
        Assert.Equal(Localizer.Text("EqProfileChoose"), list2.PlaceholderText);
        Click(second.FindControl<Button>("EqProfileSave")!);
        dialog = await Dialog(second);
        Assert.Equal("Podcast", dialog.GetVisualDescendants().OfType<TextBox>().Single().Text);
        Click(dialog.GetVisualDescendants().OfType<Button>().Single(b => b.IsCancel));
        await Until(() => !dialog.IsVisible);
        list2.SelectedItem = second.Profiles!.Profiles[EqPresets.All.Count];
        sent = (await Sent(daemon, 1))[0];
        Assert.Equal("loadEqProfile", sent["cmd"]!.GetValue<string>());
        Assert.Equal("saved:Podcast", sent["profile"]!.GetValue<string>());
        await Until(() => eq1.Current.Bands.SequenceEqual(eq2.Current.Bands) && daemon.Eqs["xlr2"].Bands.SequenceEqual(daemon.Eqs["xlr1"].Bands));
        Pump(60);
        Assert.Empty(daemon.Commands);
        Assert.Equal("saved:Podcast", ((EqProfile)list2.SelectedItem!).Id);

        // Saving under the chosen name asks before replacing it.
        Click(first.FindControl<Button>("EqProfileSave")!);
        dialog = await Dialog(first);
        Assert.Equal("Podcast", dialog.GetVisualDescendants().OfType<TextBox>().Single().Text);
        Click(dialog.GetVisualDescendants().OfType<Button>().Single(b => b.IsDefault));
        await Until(() => first.OwnedWindows.Any(w => w.IsVisible && w.Classes.Contains("dialog") && w != dialog));
        Window replace = await Dialog(first);
        Assert.Equal(Localizer.Text("EqProfileReplaceTitle"), replace.Title);
        Button yes = replace.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("danger"));
        Assert.Equal(Localizer.Text("EqProfileReplace"), yes.Content);
        Click(yes);
        await Until(() => !replace.IsVisible);
        sent = (await Sent(daemon, 1))[0];
        Assert.Equal("saveEqProfile", sent["cmd"]!.GetValue<string>());
        Assert.True(sent["replace"]!.GetValue<bool>());
        Assert.Single(OpenXLR.Core.Mixing.EqProfileStore.Read().Profiles);
        Assert.Null(first.Profiles!.Error);

        // Rename on XLR 2: the dialog starts from the name, and both editors follow without loading anything.
        Click(second.FindControl<Button>("EqProfileRename")!);
        dialog = await Dialog(second);
        Assert.Equal(Localizer.Format("EqProfileRenameTitle", "Podcast"), dialog.Title);
        input = dialog.GetVisualDescendants().OfType<TextBox>().Single();
        Assert.Equal("Podcast", input.Text);
        input.Text = "Voice";
        Click(dialog.GetVisualDescendants().OfType<Button>().Single(b => b.IsDefault));
        await Until(() => first.Profiles!.Profiles.LastOrDefault()?.Name == "Voice" && list1.SelectedItem is EqProfile { Name: "Voice" });
        Assert.Equal("Voice", ((EqProfile)list2.SelectedItem!).Name);
        Assert.Equal("Voice", ((EqProfile)list1.SelectedItem!).Name);
        Assert.Equal("renameEqProfile", (await Sent(daemon, 1))[0]["cmd"]!.GetValue<string>());

        // Delete asks first.
        Click(second.FindControl<Button>("EqProfileDelete")!);
        Window confirm = await Dialog(second);
        Assert.Equal(Localizer.Text("EqProfileDeleteTitle"), confirm.Title);
        Click(confirm.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("danger")));
        await Until(() => first.Profiles!.Profiles.Count == EqPresets.All.Count && second.Profiles!.Profiles.Count == EqPresets.All.Count);
        Assert.Null(list1.SelectedItem);
        Assert.Null(list2.SelectedItem);
        Assert.False(second.FindControl<Button>("EqProfileDelete")!.IsEnabled);
        Assert.Empty(OpenXLR.Core.Mixing.EqProfileStore.Read().Profiles);
        Assert.Equal("deleteEqProfile", (await Sent(daemon, 1))[0]["cmd"]!.GetValue<string>());

        // A file the daemon cannot read whole: one line under the row, wrapped inside the editor,
        // nothing sent and the file kept. The daemon reads it again when an editor asks, as it comes forward.
        string damaged = "{\"version\":1,\"profiles\":[{\"name\":\"Broken\"}]}";
        File.WriteAllText(path, damaged);
        first.Profiles!.Reread();
        TextBlock error = first.FindControl<TextBlock>("EqProfileError")!;
        await Until(() => error.IsVisible);
        Assert.Equal(Localizer.Format("EqProfileFileUnreadable", 1, path), error.Text);
        Assert.Contains("warn", error.Classes);
        CheckProfileRow(first, "a damaged file");
        Layout(first, 820, 600);
        Capture(first, "eq-editor-profile-error");
        Assert.False(await first.Profiles!.Save("New", false));
        Assert.Equal(damaged, File.ReadAllText(path));

        // A profile with six bands on this four-band EQ: picking it is refused, said why, nothing sent,
        // and the line stays when the editor comes forward again.
        string band = "{\"enabled\":true,\"type\":\"peak\",\"frequency\":1000,\"gain\":0,\"q\":1}";
        File.WriteAllText(path, "{\"version\":1,\"profiles\":[{\"name\":\"Six\",\"bands\":[" + string.Join(",", Enumerable.Repeat(band, 6)) + "]}]}");
        first.Profiles!.Reread();
        await Until(() => !error.IsVisible && first.Profiles!.Profiles.Count == EqPresets.All.Count + 1);
        list1.SelectedItem = first.Profiles!.Profiles[EqPresets.All.Count];
        Pump(20);
        string refused = Localizer.Format("EqProfileBandCount", "Six", 6, 4, 4);
        Assert.Equal(refused, error.Text);
        second.Activate();
        Pump(60);
        first.Activate();
        Pump(60);
        Assert.Equal(refused, error.Text);
        Assert.True(error.IsVisible);
        Assert.Empty(daemon.Commands);

        File.Delete(path);
        first.Profiles!.Reread();
        await Until(() => !error.IsVisible && first.Profiles!.Profiles.Count == EqPresets.All.Count);
        Assert.Null(list1.SelectedItem);
        Assert.Empty(daemon.Commands);

        CheckProfileLanguages(eq1);
    }

    /// <summary>The profile row from 640 to 2400 pixels in both sizings: inside the editor, its parts apart, above the graph.</summary>
    private static void CheckProfileRow(EqWindow editor, string state)
    {
        WrapPanel actions = editor.FindControl<WrapPanel>("EqProfileActions")!;
        TextBlock error = editor.FindControl<TextBlock>("EqProfileError")!;
        foreach (bool touch in new[] { false, true })
        {
            SkinService.ApplyControlSizing(touch);
            foreach (double width in new[] { 640d, 1040, 2400 })
            {
                Layout(editor, width, width < 1000 ? 620 : 760);
                string where = $"profile row with {state}, {Sizing(touch)} at {width}";
                foreach (Control part in actions.Children) AssertInside(part, editor);
                AssertNoOverlap([.. actions.Children]);
                if (error.IsVisible)
                {
                    AssertInside(error, editor);
                    Assert.DoesNotContain(error.TextLayout.TextLines, l => l.HasCollapsed);
                }
                foreach (Control action in editor.FindControl<WrapPanel>("EqActions")!.Children)
                    AssertNoOverlap([.. actions.Children, action]);
                double bottom = editor.FindControl<StackPanel>("EqProfileRow")!.TranslatePoint(
                    new Point(0, editor.FindControl<StackPanel>("EqProfileRow")!.Bounds.Height), editor)!.Value.Y;
                Assert.True(editor.Graph.TranslatePoint(default, editor)!.Value.Y >= bottom - 0.5, $"the graph starts above the profile row, {where}");
                Assert.True(editor.Graph.Plot.Height > 120, $"the graph's plot is {editor.Graph.Plot.Height} tall, {where}");
                if (width == 640) Capture(editor, $"eq-profile-row-{state.Replace(' ', '-')}-{Sizing(touch)}-640");
            }
        }
        SkinService.ApplyControlSizing(false);
        Layout(editor, 820, 600);
    }

    /// <summary>The profile row's label, list and buttons fit a 640 pixel editor in every language, in both sizings.</summary>
    private static void CheckProfileLanguages(MicEqViewModel eq)
    {
        string? language = UiSettings.Load().Language;
        try
        {
            foreach (string choice in new[] { "en", "de", "es", "it", "tl", "ceb", "sr-Cyrl" })
            {
                Assert.Null((UiSettings.Load() with { Language = choice }).Save());
                Localizer.Initialize();
                var window = new EqWindow { DataContext = eq };
                try
                {
                    window.Show();
                    foreach (bool touch in new[] { false, true })
                    {
                        SkinService.ApplyControlSizing(touch);
                        Layout(window, 640, 480);
                        WrapPanel actions = window.FindControl<WrapPanel>("EqProfileActions")!;
                        foreach (Control part in actions.Children) AssertInside(part, window);
                        AssertNoOverlap([.. actions.Children]);
                        foreach (Button button in actions.Children.OfType<Button>())
                        {
                            TextBlock text = button.GetVisualDescendants().OfType<TextBlock>().Single();
                            Assert.DoesNotContain(text.TextLayout.TextLines, l => l.HasCollapsed);
                        }
                        Assert.True(window.Graph.Plot.Height > 80, $"the graph's plot is {window.Graph.Plot.Height} tall in {choice}, {Sizing(touch)} at 640x480");
                        Capture(window, $"eq-profile-row-{choice}-{Sizing(touch)}-640");
                    }
                }
                finally
                {
                    window.Close();
                    SkinService.ApplyControlSizing(false);
                }
            }
        }
        finally
        {
            Assert.Null((UiSettings.Load() with { Language = language }).Save());
            Localizer.Initialize();
        }
    }

    /// <summary>The confirmation the window opened, once it is shown.</summary>
    private static async Task<Window> Dialog(Window owner)
    {
        await Until(() => owner.OwnedWindows.Any(w => w.IsVisible && w.Classes.Contains("dialog")));
        return owner.OwnedWindows.Single(w => w.IsVisible && w.Classes.Contains("dialog"));
    }

    /// <summary>A handle and a region show their tooltips under the real pointer, and the click still reaches the handle.</summary>
    private static async Task Tooltips(EqWindow editor)
    {
        using var pointer = new XPointer();
        editor.Activate();
        Layout(editor, 820, 600);
        EqGraph graph = editor.Graph;
        pointer.MoveTo(0, 0);
        Pump(200);
        EqHandle handle = graph.Handles[0];
        PixelPoint at = handle.PointToScreen(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2));
        pointer.MoveTo(at.X, at.Y);
        await UntilPumped(() => ToolTip.GetIsOpen(handle));
        Assert.Equal(handle.Band.Tip, ToolTip.GetTip(handle));
        Capture(editor, "eq-editor-handle-tooltip");
        pointer.Click();
        Pump(80);
        Assert.Same(handle.Band, handle.Band.Owner.SelectedBand);

        pointer.MoveTo(0, 0);
        Pump(200);
        EqRegion presence = graph.RegionLabels[3];
        at = presence.PointToScreen(new Point(presence.Bounds.Width / 2, presence.Bounds.Height / 2));
        pointer.MoveTo(at.X, at.Y);
        await UntilPumped(() => ToolTip.GetIsOpen(presence));
        Assert.Equal(EqCurve.RegionHint(3), ToolTip.GetTip(presence));
        Assert.Equal(Localizer.Text("EqRegionPresence"), AutomationProperties.GetName(presence));
        pointer.MoveTo(0, 0);
        Pump(100);
    }

    private static async Task UntilPumped(Func<bool> ready)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ready() && DateTime.UtcNow < deadline)
        {
            Pump(30);
            await Task.Yield();
        }
        Assert.True(ready());
    }

    /// <summary>
    /// The EQ button in the Broadcast and Wave matrix row heads and in an
    /// input rack: over the chain's button in the effects part, inside the
    /// head or strip at every size, opening the same editor. A skin change
    /// repaints the open editor in the new skin's colours.
    /// </summary>
    private static async Task Skins(MainWindow window, EqWindow editor, MainViewModel main)
    {
        MicEqViewModel eq1 = main.Channels.Single(c => c.Id == "xlr1").Eq!;
        foreach (string skin in new[] { "broadcast", "wave" })
        {
            SkinLayoutWindowTests.Wear(skin);
            MixMatrixViewModel matrix = window.MixMatrix!;
            Assert.NotNull(matrix);
            foreach (bool touch in new[] { false, true })
            {
                SkinService.ApplyControlSizing(touch);
                foreach (double width in new[] { 640d, 1040, 2560 })
                {
                    Layout(window, width, 1100);
                    string where = $"{skin}, {Sizing(touch)} at {width}";
                    SkinLayoutWindowTests.CheckMatrixCells(window, matrix, where);
                    foreach (string channel in new[] { "xlr1", "xlr2" })
                    {
                        MatrixRowViewModel row = matrix.Rows.Single(r => r.Channel.Id == channel);
                        Assert.True(row.ShowsEq && row.Tall, $"{channel} {where}");
                        Border head = SkinLayoutWindowTests.RowHead(window, channel);
                        Border source = head.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "MatrixSource");
                        Button button = head.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "MatrixEq");
                        Button chain = head.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("matrixEffects"));
                        Assert.True(button.IsEffectivelyVisible, where);
                        AssertInside(button, source);
                        AssertInside(button, head);
                        Point top = button.TranslatePoint(default, head)!.Value;
                        Assert.True(top.Y >= -0.5 && top.Y + button.Bounds.Height <= head.Bounds.Height + 0.5, $"{channel}: EQ outside its head {where}");
                        AssertNoOverlap([button, chain]);
                        // Over the chain, as the signal meets them.
                        Assert.True(top.Y + button.Bounds.Height <= chain.TranslatePoint(default, head)!.Value.Y + 0.5, where);
                        Assert.Equal(eq1 == row.Channel.Eq ? Localizer.Format("EqButtonNameOn", "XLR 1") : Localizer.Format("EqButtonNameOn", "XLR 2"),
                            AutomationProperties.GetName(button));
                        Assert.Contains("fxOn", button.Classes);
                        // The gain keeps the width it had beside the chain alone.
                        Slider gain = head.GetVisualDescendants().OfType<Slider>().First(s => s.IsEffectivelyVisible);
                        Assert.True(gain.Bounds.Width >= 40, $"{channel}: the gain is {gain.Bounds.Width} wide {where}");
                    }
                    if (width is 1040 or 640) Capture(window, $"eq-{skin}-button-{Sizing(touch)}-{width}");
                }
            }
            SkinService.ApplyControlSizing(false);
            Layout(window, 1040, 1100);
            Click(SkinLayoutWindowTests.RowHead(window, "xlr1").GetVisualDescendants().OfType<Button>().Single(b => b.Name == "MatrixEq"));
            Pump(40);
            Assert.Same(editor, EqWindows.Find(eq1));
            Assert.True(editor.IsVisible);
            // The open editor wears the skin.
            Layout(editor, 820, 600);
            Assert.Equal(((ISolidColorBrush)window.FindResource("Ox.Eq.Background")!).Color, SkinService.LiveBrush("Ox.Eq.Background").Color);
            Capture(editor, $"eq-editor-{skin}");
            foreach (bool touch in new[] { false, true })
            {
                SkinService.ApplyControlSizing(touch);
                Layout(editor, 820, 600);
                foreach (EqHandle handle in editor.Graph.Handles) AssertInside(handle, editor);
                Capture(editor, $"eq-editor-{skin}-{Sizing(touch)}");
            }
            SkinService.ApplyControlSizing(false);
        }

        // An input rack: the EQ button before the chain's in each microphone strip's effects part.
        SkinLayoutWindowTests.Wear("rack");
        Assert.NotNull(window.InputRackModel);
        foreach (bool touch in new[] { false, true })
        {
            SkinService.ApplyControlSizing(touch);
            foreach (double width in new[] { 640d, 1040, 2400 })
            {
                Layout(window, width, 1100);
                string where = $"rack, {Sizing(touch)} at {width}";
                foreach (string channel in new[] { "xlr1", "xlr2" })
                {
                    Border strip = window.GetVisualDescendants().OfType<Border>()
                        .Single(b => b.Name == "InputStrip" && b.DataContext is InputStripViewModel s && s.Channel.Id == channel);
                    Button button = strip.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "RackEq");
                    Assert.True(button.IsEffectivelyVisible, where);
                    AssertInside(button, strip);
                    WrapPanel effects = strip.GetVisualDescendants().OfType<WrapPanel>().Single(p => p.Name == "RackEffects");
                    Assert.Same(button, effects.Children[0]);
                    AssertNoOverlap([.. effects.Children.Where(c => c.IsVisible)]);
                    foreach (Control child in effects.Children.Where(c => c.IsVisible)) AssertInside(child, strip);
                }
                if (width == 1040) Capture(window, $"eq-rack-button-{Sizing(touch)}-1040");
            }
        }
        SkinService.ApplyControlSizing(false);
        Layout(window, 1040, 1100);
        Click(window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "RackEq" && b.IsEffectivelyVisible));
        Pump(40);
        Assert.Same(editor, EqWindows.Find(eq1));
        SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
        Layout(window, 1040, 1100);
        await Task.CompletedTask;
    }

    /// <summary>The editor the click opened for this EQ.</summary>
    private static async Task<EqWindow> Editor(Window owner, MicEqViewModel eq)
    {
        await Until(() => owner.OwnedWindows.OfType<EqWindow>().Any(w => w.DataContext == eq && w.IsVisible));
        EqWindow editor = owner.OwnedWindows.OfType<EqWindow>().Single(w => w.DataContext == eq);
        Layout(editor, 820, 600);
        return editor;
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>Let the window take its input and draw for a while.</summary>
    private static void Pump(int milliseconds)
    {
        using var stop = new CancellationTokenSource();
        DispatcherTimer.RunOnce(stop.Cancel, TimeSpan.FromMilliseconds(milliseconds));
        Dispatcher.UIThread.MainLoop(stop.Token);
    }

    private static async Task Until(Func<bool> ready, int seconds = 10)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.True(ready());
    }
}

public sealed class EqWindowFactAttribute : FactAttribute
{
    public EqWindowFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENXLR_TEST_EQ") != "1")
            Skip = "Run separately with OPENXLR_TEST_EQ=1 under xvfb-run, filtered to EqEditorWindowTests.";
    }
}
