using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Layout;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

/// <summary>
/// The Soundboard window against a fake daemon: the Soundboard button stands
/// in the SUBMIXER header right after Ducking, lights while a clip plays,
/// stays in the main window while the mixer is popped out and leaves the
/// tiles level; it, the tray and Options open the one window, a pad plays on Space and a hold pad releases when the key
/// comes up, a pad that cannot play is off, the meters light the ring and the
/// bar, Stop all and the pad's menu send their commands, the edit window
/// sends what changed, a refused import shows the daemon's sentence, a
/// deleted SFX shows its sentence and the button that creates it again, and
/// the whole window fits at its narrowest and at 2400 logical pixels in the
/// font <c>OPENXLR_LAYOUT_FONT</c> names. Run from the window layout test, on
/// its UI thread. <see cref="CheckSkins"/> runs from the skin test: the
/// header button over the classic tiles, the mix banks and the mix matrix,
/// and the pads in the skins' own faces.
/// </summary>
internal static class SoundboardWindowTests
{
    /// <summary>A reason a clip cannot load, longer than one line of a pad.</summary>
    private const string LongError = "The file could not be decoded: this installation's decoder does not read the format it is in.";

    private sealed class Daemon
    {
        public bool MixerOff;
        public bool Available = true;
        public bool Enabled = true;
        public bool HasSfx = true;
        public string? Error;
        public string? Auditioning;
        public readonly HashSet<string> Playing = [];
        public readonly List<JsonObject> Clips =
        [
            Clip("airhorn", "Airhorn", 2.4, 0.8, "overlap", "#FF7A00"),
            Clip("drumroll", "Drum roll with a name long enough to wrap", 6, 1, "hold", null, icon: "music", loop: true),
            Clip("broken", "Broken", 0, 1, "restart", "#1A237E", error: LongError),
        ];

        private static JsonObject Clip(string id, string name, double seconds, double gain, string mode, string? colour,
            string icon = "", bool loop = false, string? error = null) => new()
        {
            ["id"] = id, ["name"] = name, ["seconds"] = seconds, ["gain"] = gain, ["mode"] = mode, ["loop"] = loop,
            ["icon"] = icon, ["colour"] = colour, ["loaded"] = error is null, ["error"] = error, ["playing"] = false,
        };

        public JsonObject Board()
        {
            var clips = new JsonArray();
            // As the daemon says it: a reason exactly when the board cannot play, and every clip off then.
            string? error = !Available ? SoundboardViewModelTests.NeedsHost : !HasSfx ? Error ?? SoundboardViewModelTests.Deleted
                : !Enabled ? SoundboardViewModelTests.Off : null;
            foreach (JsonObject clip in Clips)
            {
                var copy = (JsonObject)clip.DeepClone();
                copy["playing"] = Playing.Contains(clip["id"]!.GetValue<string>());
                bool held = clip["error"] is null;
                copy["status"] = error is not null ? "off" : held ? "ready" : "error";
                copy["loaded"] = error is null && held;
                clips.Add(copy);
            }
            return new JsonObject
            {
                ["channel"] = HasSfx ? "sfx" : null, ["enabled"] = Enabled, ["available"] = Available, ["error"] = error,
                ["formats"] = Available ? new JsonArray("wav", "flac", "ogg", "opus", "mp3") : new JsonArray(),
                ["clips"] = clips, ["playing"] = new JsonArray([.. Playing.Select(p => (JsonNode)p)]),
                ["auditioning"] = Auditioning,
                ["pages"] = new JsonArray([.. Pages().Select(p => (JsonNode)p)]),
            };
        }

        /// <summary>The pages as the daemon orders them: the default page first while it has a clip, then by first clip.</summary>
        private List<string> Pages()
        {
            List<string> pages = [.. Clips.Select(c => c["page"]?.GetValue<string>() ?? "").Distinct()];
            return [.. pages.Where(p => p.Length == 0), .. pages.Where(p => p.Length > 0)];
        }

        public JsonNode State()
        {
            JsonNode state = JsonNode.Parse(InputBindingModelTests.ProState(""))!;
            state["type"] = "state";
            state["mixer"] = MixerOff ? null : Mixer(HasSfx, Board());
            // Ducking reported, so its header button shows beside the Soundboard button.
            state["ducking"] = new JsonObject
            {
                ["enabled"] = false, ["key"] = "xlr1", ["channels"] = new JsonArray("game"), ["mixes"] = new JsonArray("monitor"),
                ["sensitivity"] = "medium", ["amount"] = "medium", ["speed"] = "medium", ["processing"] = "software",
                ["backend"] = "software", ["available"] = true, ["error"] = null, ["device"] = null,
            };
            return state;
        }

        /// <summary>Apply a command as the daemon would; the error it answers with, or null.</summary>
        public string? Handle(JsonNode cmd)
        {
            string? clip = cmd["clip"]?.GetValue<string>();
            JsonObject? entry = Clips.FirstOrDefault(c => c["id"]!.GetValue<string>() == clip);
            switch (cmd["cmd"]!.GetValue<string>())
            {
                case "setSoundboard":
                    Enabled = cmd["enabled"]!.GetValue<bool>();
                    if (Enabled) { HasSfx = true; Error = null; }
                    return null;
                case "playClip":
                    if (!Enabled || !HasSfx) return SoundboardViewModelTests.Off;
                    if (entry!["mode"]!.GetValue<string>() == "toggle" && !Playing.Add(clip!)) Playing.Remove(clip!);
                    else Playing.Add(clip!);
                    return null;
                case "releaseClip":
                    Playing.Remove(clip!);
                    return null;
                case "stopClip":
                    Playing.Remove(clip!);
                    if (Auditioning == clip) Auditioning = null;
                    return null;
                case "stopClips":
                    Playing.Clear();
                    Auditioning = null;
                    return null;
                case "auditionClip":
                    if (!Enabled || !HasSfx) return SoundboardViewModelTests.Off;
                    Auditioning = clip;
                    return null;
                case "stopAudition":
                    Auditioning = null;
                    return null;
                case "updateClip":
                    foreach (string field in new[] { "name", "gain", "mode", "loop", "icon", "colour", "page" })
                        if (cmd[field] is { } value) entry![field] = field == "colour" && value.GetValue<string>() == "" ? null : value.DeepClone();
                    return null;
                case "setClipOrder":
                    List<string> order = [.. cmd["clips"]!.AsArray().Select(c => c!.GetValue<string>())];
                    Clips.Sort((a, b) => order.IndexOf(a["id"]!.GetValue<string>()).CompareTo(order.IndexOf(b["id"]!.GetValue<string>())));
                    return null;
                case "renameClipPage":
                    foreach (JsonObject onPage in Clips.Where(c => (c["page"]?.GetValue<string>() ?? "") == cmd["page"]!.GetValue<string>()))
                        onPage["page"] = cmd["name"]!.GetValue<string>();
                    return null;
                case "deleteClip":
                    Clips.Remove(entry!);
                    Playing.Remove(clip!);
                    return null;
                default:
                    return null;
            }
        }
    }

    /// <summary>A mixer with three mixes, the hardware inputs, three application channels with SFX among them, and the board.</summary>
    internal static JsonObject Mixer(bool hasSfx, JsonObject board)
    {
        var channels = new JsonArray();
        void Add(string id, string name, bool hardware = false, string icon = "")
            => channels.Add(new JsonObject
            {
                ["id"] = id, ["name"] = name, ["hardware"] = hardware, ["present"] = true, ["volume"] = id == "sfx" ? 0.7 : 1.0,
                ["levels"] = new JsonObject { ["monitor"] = 1, ["stream"] = 1, ["chat"] = 0.5 },
                ["appearance"] = new JsonObject { ["icon"] = "", ["colour"] = null, ["hidden"] = false, ["defaultIcon"] = icon },
            });
        Add("xlr1", "XLR 1", hardware: true, icon: "mic");
        Add("game", "Game", icon: "game");
        Add("music", "Music", icon: "music");
        if (hasSfx) Add("sfx", "SFX", icon: "burst");
        Add("voicechat", "Voice Chat", icon: "chat");
        return new JsonObject
        {
            ["mixes"] = new JsonArray(
                new JsonObject { ["id"] = "monitor", ["name"] = "Monitor A", ["kind"] = "monitor", ["volume"] = 1 },
                new JsonObject { ["id"] = "stream", ["name"] = "Stream", ["kind"] = "virtualMic", ["volume"] = 1 },
                new JsonObject { ["id"] = "chat", ["name"] = "Chat", ["kind"] = "virtualMic", ["volume"] = 1 }),
            ["channels"] = channels,
            ["inserts"] = new JsonObject(),
            ["soundboard"] = board,
        };
    }

    internal static void Check()
    {
        Task pending = CheckAll();
        DateTime deadline = DateTime.UtcNow.AddSeconds(120);
        while (!pending.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Assert.True(pending.IsCompleted, "The soundboard window check hung.");
        pending.GetAwaiter().GetResult();
    }

    private static async Task CheckAll()
    {
        var commands = new ConcurrentQueue<JsonNode>();
        var daemon = new Daemon();
        FakeSocket? live = null;
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            var holder = new FakeSocket(socket, stop);
            await holder.Push(daemon.State());
            live = holder;
            while (!stop.IsCancellationRequested)
            {
                JsonNode cmd = await SocketTestServer.Receive(socket, stop);
                string name = cmd["cmd"]?.GetValue<string>() ?? "";
                if (name is "listPlugins" or "getPluginSetup") continue;
                string? id = cmd["requestId"]?.GetValue<string>();
                if (name == "importClip")
                {
                    commands.Enqueue(cmd);
                    bool ok = cmd["path"]!.GetValue<string>().EndsWith(".wav", StringComparison.Ordinal);
                    string message = ok ? "Applause was added to the soundboard." : "The clip is 31.0 s long; the limit is 30 s.";
                    if (ok) daemon.Clips.Add(new JsonObject
                    {
                        ["id"] = "applause", ["name"] = "Applause", ["seconds"] = 3.0, ["gain"] = 1.0, ["mode"] = "overlap", ["loop"] = false,
                        ["icon"] = "", ["colour"] = null, ["loaded"] = true, ["error"] = null, ["playing"] = false,
                    });
                    await holder.Push(daemon.State());
                    await holder.Push(new JsonObject { ["type"] = "clipImport", ["ok"] = ok, ["message"] = message, ["clip"] = ok ? "applause" : null });
                    await holder.Push(new JsonObject { ["type"] = "commandResult", ["requestId"] = id, ["error"] = ok ? null : message });
                    continue;
                }
                if (name == "restoreStarterSounds")
                {
                    commands.Enqueue(cmd);
                    await holder.Push(new JsonObject { ["type"] = "starterSounds", ["ok"] = true, ["message"] = "Every starter sound is on the soundboard already.",
                        ["clips"] = new JsonArray(), ["skipped"] = new JsonArray() });
                    await holder.Push(new JsonObject { ["type"] = "commandResult", ["requestId"] = id, ["error"] = null });
                    continue;
                }
                if (!name.Contains("Clip", StringComparison.Ordinal) && name is not ("setSoundboard" or "stopAudition")) continue;
                string? error = daemon.Handle(cmd);
                commands.Enqueue(cmd);
                // As the daemon does: the state first, then the answer to the request.
                await holder.Push(daemon.State());
                if (id is not null) await holder.Push(new JsonObject { ["type"] = "commandResult", ["requestId"] = id, ["error"] = error });
            }
        });

        var window = new MainWindow(new DaemonClient(server.Url));
        if (Environment.GetEnvironmentVariable("OPENXLR_LAYOUT_FONT") is { Length: > 0 } font)
            window.FontFamily = new FontFamily(font);
        var main = (MainViewModel)window.DataContext!;
        var client = (DaemonClient)typeof(MainWindow).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var options = new OptionsWindow { DataContext = new OptionsViewModel(client, main) };
        SoundboardWindow? board = null;
        try
        {
            window.Show();
            await Until(() => main.Soundboard.IsReported && main.Soundboard.Clips.Count == 3);
            Layout(window, 1040, 900);

            // The SUBMIXER header carries the Soundboard button right after Ducking, dark while nothing plays;
            // no tile carries a button of its own, and every tile's rows stay level.
            await Until(() => main.Ducking.IsReported);
            Layout(window, 1040, 900);
            Button fromHeader = AssertHeaderButton(window, "classic tiles");
            Assert.True(Find<Button>(window, "DuckingButton").IsEffectivelyVisible);
            Assert.DoesNotContain("fxOn", fromHeader.Classes);
            AssertTilesLevel(window, "classic tiles");
            Capture(window, "soundboard-classic-tiles");

            fromHeader.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            board = SoundboardWindow.Current!;
            Assert.True(board.IsVisible);
            Assert.Empty(window.OwnedWindows.OfType<SoundboardWindow>());
            Assert.Same(main.Soundboard, board.DataContext);
            // The tray's entry raises the same window rather than opening a second.
            window.OpenSoundboard();
            Assert.Same(board, SoundboardWindow.Current);

            // The mixer popped out: the header stays in the main window, and its button opens the board under it.
            board.Close();
            Dispatcher.UIThread.RunJobs();
            MixerWindow popped = window.PopOutMixer();
            Layout(popped, 800, 480);
            Layout(window, 1040, 900);
            Assert.Same(fromHeader, AssertHeaderButton(window, "with the mixer popped out"));
            Assert.DoesNotContain(popped.GetVisualDescendants().OfType<Button>(), b => b.Name == "SoundboardButton");
            fromHeader.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            board = SoundboardWindow.Current!;
            // No owner: an owned board is minimized with the mixer, and pressing a pad raises the mixer.
            Assert.Null(board.Owner);
            Assert.Same(main.Soundboard, board.DataContext);
            popped.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.Null(window.DetachedMixer);
            Layout(window, 1040, 900);
            Assert.Same(board, SoundboardWindow.Current);

            // Options, AUDIO: the button beside Ducking raises the same window, which stays the mixer's,
            // so closing Options never closes the board.
            options.Show(window);
            OptionsTabsWindowTests.Show(options, OptionsTab.Audio);
            Layout(options, OptionsWindow.DesignWidth, 900);
            Button fromOptions = options.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SoundboardButton");
            Assert.True(fromOptions.IsEffectivelyVisible);
            Assert.Equal(Localizer.Text("Soundboard"), fromOptions.Content);
            AssertInside(fromOptions, options);
            AssertNoOverlap([options.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DuckingButton"), fromOptions]);
            fromOptions.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(options.OwnedWindows.OfType<SoundboardWindow>());
            Assert.Same(board, SoundboardWindow.Current);
            Assert.Null(board.Owner);
            Assert.True(Find<Button>(board, "SoundboardClose").IsCancel);
            if (Environment.GetEnvironmentVariable("OPENXLR_LAYOUT_FONT") is { Length: > 0 } boardFont)
                board.FontFamily = new FontFamily(boardFont);
            Layout(board, 760, 620);

            SoundPad Pad(string id) => board.GetVisualDescendants().OfType<SoundPad>().Single(p => ((SoundClipViewModel)p.DataContext!).Id == id);
            Control Part(string id, string name) => board.GetVisualDescendants().OfType<Control>()
                .Single(c => c.Name == name && c.DataContext is SoundClipViewModel clip && clip.Id == id);
            async Task<JsonNode> Sent(Action change)
            {
                int before = commands.Count;
                change();
                await Until(() => commands.Count == before + 1);
                return commands.ToArray()[^1];
            }
            void Key(Control pad, Avalonia.Input.Key key, bool down) => pad.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = down ? InputElement.KeyDownEvent : InputElement.KeyUpEvent, Key = key, Source = pad,
            });

            // The daemon's board, and nothing sent for it.
            Assert.Equal(["airhorn", "drumroll", "broken"], board.GetVisualDescendants().OfType<SoundPad>().Select(p => ((SoundClipViewModel)p.DataContext!).Id));
            Assert.True(Pad("airhorn").IsEnabled);
            Assert.False(Pad("broken").IsEnabled);
            Assert.Equal(LongError, ((TextBlock)Part("broken", "SoundPadDetail")).Text);
            Assert.Contains("warn", Part("broken", "SoundPadDetail").Classes);
            Assert.Equal(LongError, Avalonia.Automation.AutomationProperties.GetHelpText(Pad("broken")));
            // A clip's colour fills its pad, with lettering that reads on it: orange is light, so dark.
            Assert.True(Part("airhorn", "SoundPadFill").IsEffectivelyVisible);
            Assert.Equal(Color.Parse("#FF7A00"), ((ISolidColorBrush)((Border)Part("airhorn", "SoundPadFill")).Background!).Color);
            foreach (string lettering in new[] { "SoundPadName", "SoundPadDetail", "SoundPadIcon" })
                Assert.Same(Application.Current!.FindResource("Ox.Soundboard.ForegroundOnLight"), ((TextBlock)Part("airhorn", lettering)).Foreground);
            Assert.False(Part("airhorn", "SoundPadColour").IsEffectivelyVisible);
            // No colour: the plain face, as before.
            Assert.False(Part("drumroll", "SoundPadFill").IsEffectivelyVisible);
            Assert.False(Part("drumroll", "SoundPadColour").IsEffectivelyVisible);
            // A clip that cannot load keeps the plain face, so its name and warning read in the skin's
            // colours, with its own colour as a bar along the top.
            Assert.False(Part("broken", "SoundPadFill").IsEffectivelyVisible);
            Assert.True(Part("broken", "SoundPadColour").IsEffectivelyVisible);
            Assert.Equal(Color.Parse("#1A237E"), ((ISolidColorBrush)((Border)Part("broken", "SoundPadColour")).Background!).Color);
            foreach (string lettering in new[] { "SoundPadName", "SoundPadIcon" })
            {
                Assert.Empty(Part("broken", lettering).Classes.Intersect(["layoutAccent", "onLight", "onDark"]));
                Assert.False(((TextBlock)Part("broken", lettering)).Foreground is ISolidColorBrush { Color: var tint } && tint == Color.Parse("#1A237E"));
            }
            Assert.Same(Application.Current!.FindResource("Ox.Soundboard.ForegroundOnLight"), ((Button)Part("airhorn", "SoundPadMenu")).Foreground);
            Capture(board, "soundboard-error-pad");
            Assert.False(Find<StackPanel>(board, "SoundboardNoticeArea").IsEffectivelyVisible);
            Slider master = Find<Slider>(board, "SoundboardVolume");
            Assert.True(master.IsEffectivelyVisible);
            Assert.Equal(0.7, master.Value, 3);
            Assert.Same(main.Channels.Single(c => c.Id == "sfx"), main.Soundboard.Channel);
            Assert.Empty(commands);

            // Space plays a pad in its clip's own mode; holding the key plays once; a hold pad releases when it comes up.
            JsonNode played = await Sent(() => Key(Pad("airhorn"), Avalonia.Input.Key.Space, true));
            Assert.Equal(("playClip", "airhorn"), (played["cmd"]!.GetValue<string>(), played["clip"]!.GetValue<string>()));
            Key(Pad("airhorn"), Avalonia.Input.Key.Space, true);
            Key(Pad("airhorn"), Avalonia.Input.Key.Space, false);
            await Until(() => main.Soundboard.Clips[0].Playing);
            Assert.Single(commands);
            await Sent(() => Key(Pad("drumroll"), Avalonia.Input.Key.Enter, true));
            JsonNode released = await Sent(() => Key(Pad("drumroll"), Avalonia.Input.Key.Enter, false));
            Assert.Equal("releaseClip", released["cmd"]!.GetValue<string>());

            // The meters light the ring and the bar; the header's Soundboard button lights while a clip plays.
            await live!.Push(JsonNode.Parse("""{"type":"meters","levels":{"ch:sfx":[0.4,0.4],"clip:airhorn":[0.5,1]}}""")!);
            await Until(() => Part("airhorn", "SoundPadRing").IsEffectivelyVisible && ((ProgressBar)Part("airhorn", "SoundPadProgress")).Value == 0.5);
            Assert.True(Part("airhorn", "SoundPadProgress").IsEffectivelyVisible);
            Assert.Contains("fxOn", fromHeader.Classes);
            Assert.Contains("fxOn", Find<Button>(board, "SoundboardStopAll").Classes);
            foreach (double width in new[] { 440d, 2400 })
            {
                Layout(board, width, 900);
                AssertFits(board, $"playing at {width}");
                Capture(board, $"soundboard-playing-{(int)width}");
            }
            Layout(board, 760, 620);

            // Stop all, then the pad's menu: stop off while silent, move later sends the whole order.
            Assert.Equal("stopClips", (await Sent(() => Find<Button>(board, "SoundboardStopAll").RaiseEvent(new RoutedEventArgs(Button.ClickEvent))))["cmd"]!.GetValue<string>());
            await live.Push(JsonNode.Parse("""{"type":"meters","levels":{"ch:sfx":[0,0]}}""")!);
            await Until(() => !main.Soundboard.AnyPlaying && !Part("airhorn", "SoundPadRing").IsEffectivelyVisible);
            Assert.DoesNotContain("fxOn", fromHeader.Classes);
            MenuFlyout menu = board.PadMenu(main.Soundboard, main.Soundboard.Clips[0]);
            MenuItem[] items = [.. menu.Items.OfType<MenuItem>()];
            Assert.Equal(["SoundClipEdit", "SoundClipAudition", "SoundClipStop", "SoundClipEarlier", "SoundClipLater", "SoundClipDelete"],
                items.Select(i => i.Name));
            Assert.Equal(Localizer.Text("SoundClipAudition"), items[1].Header);
            Assert.True(items[1].IsEnabled);
            Assert.False(items[2].IsEnabled);
            Assert.False(items[3].IsEnabled);

            // The audition: the clip in the monitor mix alone. The pad says so on
            // its second line and does not light, since nothing plays into SFX,
            // and its menu offers to stop the audition instead.
            JsonNode audition = await Sent(() => items[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)));
            Assert.Equal(("auditionClip", "airhorn"), (audition["cmd"]!.GetValue<string>(), audition["clip"]!.GetValue<string>()));
            await Until(() => main.Soundboard.Clips[0].Auditioned
                && ((TextBlock)Part("airhorn", "SoundPadDetail")).Text == Localizer.Text("SoundClipAuditioning"));
            Assert.False(Part("airhorn", "SoundPadRing").IsEffectivelyVisible);
            Assert.False(main.Soundboard.AnyPlaying);
            AssertFits(board, "auditioning");
            MenuItem[] whileAuditioned = [.. board.PadMenu(main.Soundboard, main.Soundboard.Clips[0]).Items.OfType<MenuItem>()];
            MenuItem stopAudition = whileAuditioned.Single(i => i.Name == "SoundClipAudition");
            Assert.Equal(Localizer.Text("SoundClipStopAudition"), stopAudition.Header);
            Assert.True(stopAudition.IsEnabled);
            // Stop is on while the clip is auditioned: it ends the audition too.
            Assert.True(whileAuditioned.Single(i => i.Name == "SoundClipStop").IsEnabled);
            // The audition ends by itself while the menu is open: the entry still
            // stops, and never starts the clip again.
            daemon.Auditioning = null;
            await live.Push(daemon.State());
            await Until(() => !main.Soundboard.Clips[0].Auditioned);
            Assert.Equal("stopAudition", (await Sent(() => stopAudition.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent))))["cmd"]!.GetValue<string>());
            // The other way round: a menu opened on a silent clip auditions it,
            // even when an audition of it started meanwhile.
            MenuItem startAudition = board.PadMenu(main.Soundboard, main.Soundboard.Clips[0]).Items.OfType<MenuItem>().Single(i => i.Name == "SoundClipAudition");
            Assert.Equal(Localizer.Text("SoundClipAudition"), startAudition.Header);
            daemon.Auditioning = "airhorn";
            await live.Push(daemon.State());
            await Until(() => main.Soundboard.Clips[0].Auditioned);
            Assert.Equal("auditionClip", (await Sent(() => startAudition.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent))))["cmd"]!.GetValue<string>());
            // The pad's Stop ends the audition.
            MenuItem stopPad = board.PadMenu(main.Soundboard, main.Soundboard.Clips[0]).Items.OfType<MenuItem>().Single(i => i.Name == "SoundClipStop");
            JsonNode stopped = await Sent(() => stopPad.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)));
            Assert.Equal(("stopClip", "airhorn"), (stopped["cmd"]!.GetValue<string>(), stopped["clip"]!.GetValue<string>()));
            await Until(() => !main.Soundboard.Clips[0].Auditioned);
            // A pad that cannot play cannot be auditioned either.
            Assert.False(board.PadMenu(main.Soundboard, main.Soundboard.Clips.Single(c => c.Id == "broken")).Items.OfType<MenuItem>()
                .Single(i => i.Name == "SoundClipAudition").IsEnabled);

            JsonNode order = await Sent(() => items[4].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)));
            Assert.Equal("""["drumroll","airhorn","broken"]""", order["clips"]!.ToJsonString());
            await Until(() => board.GetVisualDescendants().OfType<SoundPad>().Select(p => ((SoundClipViewModel)p.DataContext!).Id).First() == "drumroll");
            // The keyboard stays on the pad it moved.
            await Until(() => board.GetVisualDescendants().OfType<SoundPad>().Any(p => p.DataContext is SoundClipViewModel { Id: "airhorn" } && p.IsFocused));

            // The edit window: the gain and the mode go in one updateClip, and the pad shows the daemon's answer.
            SoundClipViewModel airhorn = main.Soundboard.Clips.Single(c => c.Id == "airhorn");
            Task editing = board.EditAsync(airhorn);
            await Until(() => board.OwnedWindows.OfType<SoundClipWindow>().Any());
            SoundClipWindow edit = board.OwnedWindows.OfType<SoundClipWindow>().Single();
            Layout(edit, 460, 640);
            AssertFitsDialog(edit);
            Capture(edit, "soundboard-edit");
            // The swatches fill the colour field and follow it; the field is what Save sends.
            Button[] swatches = [.. Find<WrapPanel>(edit, "SoundClipSwatches").Children.OfType<Button>()];
            Assert.Equal(OpenXLR.UI.LayoutColours.Palette.Count + 1, swatches.Length);
            // The clip's own orange is no palette colour: no swatch is ringed, not even no colour.
            Assert.DoesNotContain(swatches, Ring);
            swatches.Single(b => Equals(b.Tag, "#1976D2")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("#1976D2", edit.Edit!.Colour);
            Assert.True(Ring(swatches.Single(b => Equals(b.Tag, "#1976D2"))));
            Assert.Equal(Localizer.Text("ColourBlue"), Avalonia.Automation.AutomationProperties.GetName(swatches.Single(b => Equals(b.Tag, "#1976D2"))));
            Layout(edit, 460, 640);
            Capture(edit, "soundboard-edit-swatches");
            Find<TextBox>(edit, "SoundClipColour").Text = "#ff7a00";
            Assert.DoesNotContain(swatches, Ring);
            Assert.Equal("#ff7a00", edit.Edit.Colour);
            Find<Slider>(edit, "SoundClipGain").Value = 40;
            Find<ComboBox>(edit, "SoundClipMode").SelectedIndex = 2;
            JsonNode update = await Sent(() => Find<Button>(edit, "SoundClipSave").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            Assert.Equal("""{"gain":0.4,"mode":"toggle"}""", Fields(update));
            await editing.WaitAsync(TimeSpan.FromSeconds(10));
            await Until(() => airhorn.Mode == "toggle" && airhorn.Detail == Localizer.Format("SoundClipDetail", 2.4, Localizer.Text("SoundClipModeToggle")));

            // A hold pad held down is released when it turns off: the board stops under it.
            await Sent(() => Key(Pad("drumroll"), Avalonia.Input.Key.Enter, true));
            daemon.Enabled = false;
            await live.Push(daemon.State());
            await Until(() => commands.ToArray()[^1]["cmd"]!.GetValue<string>() == "releaseClip");
            Assert.Equal("drumroll", commands.ToArray()[^1]["clip"]!.GetValue<string>());
            daemon.Enabled = true;
            await live.Push(daemon.State());
            await Until(() => Pad("drumroll").IsEffectivelyEnabled);
            // Held, then moved: the press releases its own clip exactly once, whether the pad goes
            // with the move or the window loses focus first, and a key up afterwards sends nothing.
            int beforeHold = commands.Count;
            await Sent(() => Key(Pad("drumroll"), Avalonia.Input.Key.Enter, true));
            await board.MoveAsync(main.Soundboard, main.Soundboard.Clips.Single(c => c.Id == "drumroll"), +1);
            await Until(() => main.Soundboard.Clips[1].Id == "drumroll");
            board.ReleaseHeldPads();
            Key(Pad("drumroll"), Avalonia.Input.Key.Enter, false);
            board.ReleaseHeldPads();
            await Until(() => commands.Skip(beforeHold).Any(c => c["cmd"]!.GetValue<string>() == "releaseClip"));
            await Task.Delay(200);
            JsonNode[] holdReleases = [.. commands.Skip(beforeHold).Where(c => c["cmd"]!.GetValue<string>() == "releaseClip")];
            Assert.Single(holdReleases);
            Assert.Equal("drumroll", holdReleases[0]["clip"]!.GetValue<string>());

            // A finger: a scroll that starts on a pad plays nothing, a tap plays on lifting,
            // and a hold pad plays on the touch and releases when the finger lifts.
            SoundPad tapped = Pad("airhorn");
            int beforeTouch = commands.Count;
            tapped.PointerDown(PointerType.Touch, new Point(20, 30));
            tapped.PointerMove(new Point(20, 30 + SoundPad.TouchSlop + 20));
            tapped.PointerUp(new Point(20, 30 + SoundPad.TouchSlop + 20));
            await Task.Delay(150);
            Assert.Equal(beforeTouch, commands.Count);
            tapped.PointerDown(PointerType.Touch, new Point(20, 30));
            Assert.Equal(beforeTouch, commands.Count);
            JsonNode tap = await Sent(() => tapped.PointerUp(new Point(23, 32)));
            Assert.Equal(("playClip", "airhorn"), (tap["cmd"]!.GetValue<string>(), tap["clip"]!.GetValue<string>()));
            SoundPad holding = Pad("drumroll");
            Assert.Equal("playClip", (await Sent(() => holding.PointerDown(PointerType.Touch, new Point(20, 30))))["cmd"]!.GetValue<string>());
            Assert.Equal("releaseClip", (await Sent(() => holding.PointerUp(new Point(60, 30))))["cmd"]!.GetValue<string>());
            // The mouse keeps press to play.
            Assert.Equal("playClip", (await Sent(() => tapped.PointerDown(PointerType.Mouse, new Point(20, 30))))["cmd"]!.GetValue<string>());
            tapped.PointerUp(new Point(20, 30));
            await Sent(() => Find<Button>(board, "SoundboardStopAll").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));

            // A refused import shows the daemon's own sentence; an accepted one adds its pad.
            await main.Soundboard.ImportAsync(["/home/user/long.flac"]);
            TextBlock status = board.FindControl<TextBlock>("SoundboardStatus")!;
            await Until(() => status.IsEffectivelyVisible && status.Classes.Contains("warn"));
            Assert.Equal("The clip is 31.0 s long; the limit is 30 s.", status.Text);
            await main.Soundboard.ImportAsync(["/home/user/applause.wav"]);
            await Until(() => main.Soundboard.Clips.Count == 4 && status.Text == "Applause was added to the soundboard." && !status.Classes.Contains("warn"));

            // The library's export and import stand with the other actions, and fit at both widths.
            Assert.True(Find<Button>(board, "SoundboardExportLibrary").IsEffectivelyEnabled);
            Assert.True(Find<Button>(board, "SoundboardImportLibrary").IsEffectivelyEnabled);
            // Restore starter sounds stands beside them and shows the daemon's sentence.
            Button restore = Find<Button>(board, "SoundboardRestoreStarter");
            Assert.True(restore.IsEffectivelyEnabled);
            Assert.Equal("restoreStarterSounds", (await Sent(() => restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))))["cmd"]!.GetValue<string>());
            await Until(() => status.Text == "Every starter sound is on the soundboard already." && !status.Classes.Contains("warn"));
            Grid pageBar = Find<Grid>(board, "SoundboardPageBar");
            Assert.False(pageBar.IsEffectivelyVisible);

            // Pages: a tab each, the pads of the page shown, the menu for a named page.
            daemon.Clips.Single(c => c["id"]!.GetValue<string>() == "drumroll")["page"] = "Jingles with a long page name for the tab";
            await live.Push(daemon.State());
            Button[] Tabs() => [.. board.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "SoundboardPageTab")];
            // The bar shows before a layout pass has made its tabs.
            await Until(() => pageBar.IsEffectivelyVisible && Tabs().Length == 2);
            Assert.Equal([Localizer.Text("SoundboardMainPage"), "Jingles with a long page name for the tab"],
                Tabs().Select(t => ((SoundPageViewModel)t.DataContext!).Title));
            Assert.Contains("fxOn", Tabs()[0].Classes);
            Assert.DoesNotContain(board.GetVisualDescendants().OfType<SoundPad>(), p => ((SoundClipViewModel)p.DataContext!).Id == "drumroll");
            Assert.False(Find<Button>(board, "SoundboardPageMenu").IsEffectivelyEnabled);
            Tabs()[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => board.GetVisualDescendants().OfType<SoundPad>().Select(p => ((SoundClipViewModel)p.DataContext!).Id).SequenceEqual(["drumroll"]));
            Assert.Contains("fxOn", Tabs()[1].Classes);
            Assert.True(Find<Button>(board, "SoundboardPageMenu").IsEffectivelyEnabled);
            Assert.Equal(["SoundboardRenamePage", "SoundboardRemovePage"], board.PageMenu(main.Soundboard).Items.OfType<MenuItem>().Select(i => i.Name));
            foreach (double width in new[] { 440d, 2400 })
            {
                Layout(board, width, 900);
                AssertFits(board, $"with pages at {width}");
                Capture(board, $"soundboard-pages-{(int)width}");
            }
            Layout(board, 760, 620);
            JsonNode renamed = await Sent(() => _ = main.Soundboard.RenamePageAsync("Beats"));
            Assert.Equal(("Jingles with a long page name for the tab", "Beats"), (renamed["page"]!.GetValue<string>(), renamed["name"]!.GetValue<string>()));
            await Until(() => Tabs().Length == 2 && ((SoundPageViewModel)Tabs()[1].DataContext!).Title == "Beats" && Tabs()[1].Classes.Contains("fxOn"));
            JsonNode emptied = await Sent(() => _ = main.Soundboard.RemovePageAsync());
            Assert.Equal("", emptied["name"]!.GetValue<string>());
            await Until(() => !pageBar.IsEffectivelyVisible && board.GetVisualDescendants().OfType<SoundPad>().Count() == 4);

            // A drag of files over the window says where a drop puts them.
            TextBlock dropHint = Find<TextBlock>(board, "SoundboardDropHint");
            Assert.False(dropHint.IsEffectivelyVisible);
            main.Soundboard.Dropping = true;
            await Until(() => dropHint.IsEffectivelyVisible && dropHint.Text == Localizer.Format("SoundboardDropHint", Localizer.Text("SoundboardMainPage")));
            Layout(board, 440, 700);
            AssertFits(board, "while files are dragged over it");
            main.Soundboard.Dropping = false;
            Layout(board, 760, 620);

            // SFX deleted: the daemon's sentence and the switch, which creates SFX again; the header keeps its button.
            const string deleted = "The soundboard is off because the SFX channel was deleted. Turn it on again to create SFX, muted in every mix.";
            daemon.HasSfx = false;
            daemon.Enabled = false;
            daemon.Error = deleted;
            await live.Push(daemon.State());
            TextBlock notice = Find<TextBlock>(board, "SoundboardNotice");
            CheckBox turnOn = Find<CheckBox>(board, "SoundboardEnabled");
            await Until(() => notice.IsEffectivelyVisible && notice.Text == deleted && turnOn.IsEffectivelyVisible && turnOn.IsChecked == false);
            Assert.True(turnOn.IsEffectivelyEnabled);
            Assert.False(Pad("airhorn").IsEnabled);
            Assert.False(Find<StackPanel>(board, "SoundboardMaster").IsEffectivelyVisible);
            Assert.True(fromHeader.IsEffectivelyVisible);
            foreach (double width in new[] { 440d, 2400 })
            {
                Layout(board, width, 900);
                AssertFits(board, $"without SFX at {width}");
                Capture(board, $"soundboard-no-sfx-{(int)width}");
            }
            JsonNode on = await Sent(() => turnOn.IsChecked = true);
            Assert.True(on["enabled"]!.GetValue<bool>());
            await Until(() => main.Soundboard.CanPlay && !notice.IsEffectivelyVisible && Pad("airhorn").IsEnabled);

            // The software mixer stops: the pads stay, the same ones, unable to play, and the window says why.
            SoundPad kept = Pad("airhorn");
            daemon.MixerOff = true;
            await live.Push(daemon.State());
            await Until(() => notice.IsEffectivelyVisible && notice.Text == Localizer.Text("SoundboardMixerOff") && !kept.IsEnabled);
            // No mixer, no header button; the tray and Options still reach the window.
            Assert.False(fromHeader.IsEffectivelyVisible);
            Assert.Same(kept, Pad("airhorn"));
            Assert.Equal(4, main.Soundboard.Clips.Count);
            Assert.False(Find<StackPanel>(board, "SoundboardMaster").IsEffectivelyVisible);
            Assert.False(Find<Button>(board, "SoundboardAddFiles").IsEnabled);
            Layout(board, 760, 620);
            AssertFits(board, "with the mixer off");
            Capture(board, "soundboard-mixer-off");
            daemon.MixerOff = false;
            await live.Push(daemon.State());
            await Until(() => kept.IsEnabled && !notice.IsEffectivelyVisible && fromHeader.IsEffectivelyVisible);

            // No native helper: the sentence, and nothing to add or turn on.
            daemon.Available = false;
            await live.Push(daemon.State());
            await Until(() => notice.IsEffectivelyVisible && notice.Text == SoundboardViewModelTests.NeedsHost);
            Assert.False(Find<Button>(board, "SoundboardAddFiles").IsEnabled);
            Assert.False(turnOn.IsEffectivelyVisible);
            Layout(board, 440, 700);
            AssertFits(board, "without the helper");

            // Minimizing the mixer leaves the open board where it is, and the tray opens it
            // on its own while the mixer is minimized.
            window.WindowState = WindowState.Minimized;
            DateTime minimizing = DateTime.UtcNow.AddSeconds(2);
            while (window.WindowState != WindowState.Minimized && DateTime.UtcNow < minimizing) await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
            Assert.True(board.IsVisible);
            Assert.NotEqual(WindowState.Minimized, board.WindowState);
            board.Close();
            Dispatcher.UIThread.RunJobs();
            window.OpenSoundboard();
            Dispatcher.UIThread.RunJobs();
            board = SoundboardWindow.Current!;
            Assert.True(board.IsVisible);
            Assert.Null(board.Owner);
            window.WindowState = WindowState.Normal;
        }
        finally
        {
            board?.Close();
            options.Close();
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static string Fields(JsonNode command)
    {
        var copy = command.DeepClone().AsObject();
        foreach (string drop in new[] { "cmd", "clip", "requestId" }) copy.Remove(drop);
        return copy.ToJsonString();
    }

    /// <summary>
    /// The SUBMIXER header's Soundboard button: shown, right after Ducking in
    /// the row and on screen, labelled, inside the window and clear of the
    /// header's other controls.
    /// </summary>
    internal static Button AssertHeaderButton(Window window, string where)
    {
        Button button = Find<Button>(window, "SoundboardButton");
        Button ducking = Find<Button>(window, "DuckingButton");
        var row = (WrapPanel)button.Parent!;
        Assert.Same(row, ducking.Parent);
        Assert.True(row.Children.IndexOf(button) == row.Children.IndexOf(ducking) + 1, $"{where}: the Soundboard button does not follow Ducking.");
        Assert.True(button.IsEffectivelyVisible, $"{where}: the header has no Soundboard button.");
        Assert.Equal(Localizer.Text("Soundboard"), button.Content);
        Assert.Equal(Localizer.Text("SoundboardButtonTip"), ToolTip.GetTip(button));
        Assert.True(button.Bounds.Height > 0 && button.Bounds.Width > 0, $"{where}: the Soundboard button has no size.");
        AssertInside(button, window);
        AssertNoOverlap([.. row.Children.Where(c => c.IsEffectivelyVisible)]);
        if (ducking.IsEffectivelyVisible)
        {
            // After Ducking on its line, or on a later line where the header wraps.
            Point d = ducking.TranslatePoint(default, row)!.Value, b = button.TranslatePoint(default, row)!.Value;
            Assert.True(b.X >= d.X + ducking.Bounds.Width - 0.5 || b.Y >= d.Y + ducking.Bounds.Height - 0.5,
                $"{where}: the Soundboard button at {b} stands before Ducking at {d}.");
        }
        return button;
    }

    /// <summary>
    /// The classic tiles stay level: every tile's name line is as tall, so
    /// every meter and send row starts at one height across the panel.
    /// </summary>
    internal static void AssertTilesLevel(Window window, string where)
    {
        Grid[] lines = [.. window.GetVisualDescendants().OfType<Grid>().Where(g => g.Name == "ChannelNameLine" && g.IsEffectivelyVisible)];
        Assert.True(lines.Length > 1, $"{where}: fewer than two tiles.");
        double[] heights = [.. lines.Select(g => Math.Round(g.Bounds.Height, 1)).Distinct()];
        Assert.True(heights.Length == 1, $"{where}: the name lines are {string.Join(", ", heights)} tall.");
        double[] meters = [.. lines.Select(g => ((StackPanel)g.Parent!).Children[((StackPanel)g.Parent!).Children.IndexOf(g) + 1])
            .Select(m => Math.Round(m.TranslatePoint(default, window)!.Value.Y, 1)).Distinct()];
        Assert.True(meters.Length == 1, $"{where}: the meters start at {string.Join(", ", meters)}.");
    }

    /// <summary>Every visible control and text sits inside the window's width, and the pads never scroll sideways.</summary>
    private static void AssertFits(SoundboardWindow window, string where)
    {
        foreach (Control control in window.GetVisualDescendants().OfType<Control>()
                     .Where(c => c.IsEffectivelyVisible && c is TextBlock or Button or CheckBox or Slider or ProgressBar))
        {
            if (control is TextBlock { Text: null or { Length: 0 } }) continue;
            try { AssertInside(control, window); }
            catch (Xunit.Sdk.XunitException ex) { throw new Xunit.Sdk.XunitException($"{where}: {ex.Message}"); }
        }
        ScrollViewer scroll = Find<ScrollViewer>(window, "SoundboardScroll");
        Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 0.5, $"{where}: the pads scroll sideways.");
        // The overlay scroll bar paints over the scroller's right edge; the pads stay clear of it.
        foreach (Panel cell in window.GetVisualDescendants().OfType<Panel>().Where(p => p.Name == "SoundPadCell"))
            Assert.True(cell.TranslatePoint(new Point(cell.Bounds.Width, 0), scroll)!.Value.X <= scroll.Viewport.Width - 12,
                $"{where}: a pad runs under the scroll bar.");
        // A pad fills its cell, and its text stays inside it.
        foreach (SoundPad pad in window.GetVisualDescendants().OfType<SoundPad>())
            Assert.True(Math.Abs(pad.Bounds.Width - ((Control)pad.Parent!).Bounds.Width) < 0.5
                && Math.Abs(pad.Bounds.Height - ((Control)pad.Parent!).Bounds.Height) < 0.5,
                $"{where}: a pad is {pad.Bounds.Size} in a {((Control)pad.Parent!).Bounds.Size} cell.");
        foreach (SoundPad pad in window.GetVisualDescendants().OfType<SoundPad>())
            foreach (TextBlock text in pad.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Text is { Length: > 0 }))
            {
                Point origin = text.TranslatePoint(default, pad)!.Value;
                Assert.True(origin.X >= -0.5 && origin.X + text.Bounds.Width <= pad.Bounds.Width + 0.5
                    && origin.Y + text.Bounds.Height <= pad.Bounds.Height + 0.5, $"{where}: \"{text.Text}\" runs out of its pad.");
            }
    }

    private static void AssertFitsDialog(Window dialog)
    {
        foreach (Control control in dialog.GetVisualDescendants().OfType<Control>()
                     .Where(c => c.IsEffectivelyVisible && c is TextBlock or Button or CheckBox or Slider or ComboBox or TextBox))
        {
            if (control is TextBlock { Text: null or { Length: 0 } }) continue;
            if (control.GetVisualAncestors().OfType<Popup>().Any()) continue;
            AssertInside(control, dialog);
        }
    }

    // --- under the skins ---

    /// <summary>
    /// The header's Soundboard button over the classic tiles, an input rack
    /// with mix banks and the mix matrix of Broadcast and Wave: right after
    /// Ducking, lit while a clip plays, inside the window and opening the
    /// window, with no button left on a strip; and the pads under the default, OpenDeck and
    /// Broadcast skins, each in its button face with the soundboard tokens.
    /// Run from the skin test, on its UI thread.
    /// </summary>
    internal static void CheckSkins()
    {
        string data = Path.Combine(Path.GetTempPath(), "openxlr-sound-skins-" + Guid.NewGuid());
        string? oldHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        string? oldDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        var window = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
        StopConnecting(window);
        SoundboardWindow? board = null;
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", data);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", Path.Combine(data, "none"));
            string folder = Path.Combine(data, "openxlr", "skins", "sounds-rack");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "skin.json"), """
                {"schema":1,"name":"sounds-rack","tokens":{},"layout":{"version":1,"regions":{"inputs":{"template":"input-rack"},"submixer":{"template":"mix-banks"}}}}
                """);
            var main = (MainViewModel)window.DataContext!;
            var daemon = new Daemon();
            daemon.Playing.Add("airhorn");
            // Through text, as the socket hands it over: numbers then read as any number type.
            JsonNode state = JsonNode.Parse(daemon.State().ToJsonString())!;
            CaptureInputWindowTests.Apply(main, state);
            main.Soundboard.ApplyMeters(JsonNode.Parse("""{"clip:airhorn":[0.6,1]}""")!.AsObject());
            window.Show();
            foreach (string skin in new[] { "default", "sounds-rack", "broadcast", "wave" })
            {
                Assert.Empty(SkinService.Apply(skin == "default" ? new SkinEntry(SkinPackage.Default, [])
                    : Assert.Single(SkinCatalog.Discover(), e => e.Id == skin)));
                Layout(window, 1280, 1200);
                Button sounds = AssertHeaderButton(window, skin);
                Assert.True(sounds.Classes.Contains("fxOn"), $"{skin}: the Soundboard button does not light while a clip plays.");
                Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && Equals(b.Content, Localizer.Text("Soundboard")));
                if (skin == "default") AssertTilesLevel(window, skin);
                Shot(window, $"soundboard-header-{skin}");
                sounds.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                board = SoundboardWindow.Current;
                Assert.NotNull(board);
                Assert.Same(main.Soundboard, board!.DataContext);
                board.Close();
                Dispatcher.UIThread.RunJobs();
            }

            // The pads in each skin's button face, sized and ringed by the soundboard tokens.
            foreach (string skin in new[] { "default", "opendeck", "broadcast" })
            {
                Assert.Empty(SkinService.Apply(skin == "default" ? new SkinEntry(SkinPackage.Default, [])
                    : Assert.Single(SkinCatalog.Discover(), e => e.Id == skin)));
                board = SoundboardWindow.Open(window, main.Soundboard);
                Layout(board, 760, 620);
                SoundPad pad = board.GetVisualDescendants().OfType<SoundPad>().First(p => ((SoundClipViewModel)p.DataContext!).Id == "airhorn");
                Panel cell = (Panel)pad.Parent!;
                Assert.Equal((double)Application.Current!.FindResource("Ox.Soundboard.PadWidth")!, cell.Bounds.Width, 1);
                Assert.Equal((double)Application.Current!.FindResource("Ox.Soundboard.PadHeight")!, cell.Bounds.Height, 1);
                Assert.Same(Application.Current!.FindResource("Ox.Button.Theme") as Avalonia.Styling.ControlTheme, pad.Theme);
                Border ring = cell.Children.OfType<Border>().Single(b => b.Name == "SoundPadRing");
                Assert.True(ring.IsEffectivelyVisible);
                Assert.Same(Application.Current!.FindResource("Ox.Soundboard.Playing"), ring.BorderBrush);
                foreach (TextBlock text in board.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible && t.Text is { Length: > 0 }))
                    Assert.True(text.Foreground is not null, $"{skin}: \"{text.Text}\" has no colour.");
                Shot(board, $"soundboard-{skin}");
                board.Close();
                Dispatcher.UIThread.RunJobs();
                board = null;
            }

            // Touch sizing: larger pads, and the enlarged menu button keeps clear of every pad's name and detail.
            Assert.Empty(SkinService.Apply(new SkinEntry(SkinPackage.Default, [])));
            bool touch = SkinService.TouchControls;
            try
            {
                SkinService.ApplyControlSizing(true);
                board = SoundboardWindow.Open(window, main.Soundboard);
                Layout(board, 760, 720);
                foreach (Panel cell in board.GetVisualDescendants().OfType<Panel>().Where(p => p.Name == "SoundPadCell"))
                {
                    Assert.True(cell.Bounds.Width >= 176 && cell.Bounds.Height >= 120, $"a touch pad is {cell.Bounds.Size}.");
                    Button menu = cell.Children.OfType<Button>().Single(b => b.Name == "SoundPadMenu");
                    Assert.True(menu.Bounds.Height >= SkinTokens.TouchTarget - 0.5, $"the menu button is {menu.Bounds.Size}.");
                    foreach (TextBlock text in cell.GetVisualDescendants().OfType<TextBlock>()
                                 .Where(t => t.Name is "SoundPadName" or "SoundPadDetail" && t.IsEffectivelyVisible))
                        AssertNoOverlap([menu, text]);
                }
                Shot(board, "soundboard-touch");
                board.Close();
                Dispatcher.UIThread.RunJobs();
                board = null;
            }
            finally { SkinService.ApplyControlSizing(touch); }
        }
        finally
        {
            board?.Close();
            SkinService.Apply(new SkinEntry(SkinPackage.Default, []));
            window.Close();
            Dispatcher.UIThread.RunJobs();
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", oldHome);
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", oldDirs);
            try { Directory.Delete(data, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>A screenshot for looking at by eye, where either window job keeps its pictures.</summary>
    private static void Shot(TopLevel window, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("OPENXLR_SKIN_ARTIFACTS") is { Length: > 0 } skins ? skins
            : Environment.GetEnvironmentVariable("OPENXLR_LAYOUT_ARTIFACTS");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        using var bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)window.Bounds.Width), Math.Max(1, (int)window.Bounds.Height)));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(directory, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    /// <summary>The fake daemon's socket: one send at a time, from its loop or from the test.</summary>
    private sealed class FakeSocket(System.Net.WebSockets.WebSocket socket, CancellationToken stop)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public async Task Push(JsonNode message)
        {
            await _gate.WaitAsync(stop);
            try { await SocketTestServer.Send(socket, message, stop); }
            finally { _gate.Release(); }
        }
    }

    private static T Find<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static async Task Until(Func<bool> ready)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(5);
        Assert.True(ready());
    }
}
