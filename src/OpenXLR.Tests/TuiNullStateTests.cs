using System.Text.Json.Nodes;
using OpenXLR.Tui;

namespace OpenXLR.Tests;

/// <summary>
/// A state with a null where the terminal mixer does not expect one, at any
/// depth: the reader keeps the field's default or leaves the entry out, every
/// tab draws it, and a frame or a key that throws anyway shows the error and
/// leaves the terminal mixer running.
/// </summary>
public sealed class TuiNullStateTests
{
    /// <summary>A state that reaches every section the tabs draw: each kind of channel, mix, list and map.</summary>
    private const string StateJson = """
        {
          "type": "state", "daemonVersion": "0.8.1", "connected": true, "warning": "check the cable",
          "device": { "vendor": "Elgato", "model": "Wave XLR Pro", "usbId": "0fd9:00b4", "note": "firmware 1.2" },
          "capabilities": { "gain": true, "mute": true, "lowCut": true, "phantom": true, "clipGuard": true,
                            "compressor": true, "voiceTune": true, "expander": true, "hpVolume": true,
                            "crossfade": true, "outputRouting": true, "auxInput": true, "ducking": true,
                            "hardwareMixing": true, "xlrJacks": true, "xlrInputs": 2, "hpOutputs": 2 },
          "state": { "gainDb": 40, "mute": false, "gain2Db": 30, "mute2": true, "lowCut": true, "hpVolumeDb": -6 },
          "mixer": {
            "routeAppsByDefault": true, "appVolumeSupported": true, "blockSize": 256, "blockSizes": [128, 256, 512],
            "mixes": [
              { "id": "monitor", "name": "Monitor A", "volume": 1, "muted": false, "kind": "monitor",
                "monitorDry": false, "hardwareSection": 1, "maximizer": 20,
                "appearance": { "icon": "headphones", "colour": "#ff8800", "hidden": false, "defaultIcon": "headphones" },
                "eq": { "enabled": true, "backend": "software", "available": true, "error": null, "processing": "software",
                        "bands": [ { "enabled": true, "type": "peak", "frequency": 1000, "gain": 3, "q": 0.7 } ] } },
              { "id": "stream", "name": "Stream", "volume": 0.8, "muted": true, "kind": "virtualMic",
                "editable": true, "deletable": true,
                "appearance": { "icon": "", "colour": null, "hidden": false, "defaultIcon": "broadcast" } }
            ],
            "channels": [
              { "id": "xlr1", "name": "XLR 1", "levels": { "monitor": 1, "stream": 0.5 }, "mutedIn": ["monitor"],
                "hardware": true, "microphone": true, "chainWidth": 1, "present": true, "volume": 0.9, "linkedSends": true,
                "binding": { "kind": "source", "source": "alsa_input.usb-mic", "monoChannel": 0, "unit": null },
                "bindingConnected": true, "bindingWarning": null,
                "unitInput": { "unit": "0fd9:00b4@1", "input": 1, "primary": true },
                "appearance": { "icon": "mic", "colour": null, "hidden": false, "defaultIcon": "mic" },
                "eq": { "enabled": true, "backend": "device", "available": true, "error": null, "processing": "device",
                        "bands": [ { "enabled": true, "type": "lowCut", "frequency": 80, "gain": 0, "q": 0.7 } ],
                        "device": { "unit": "0fd9:00b4@1", "input": 1, "name": "Wave XLR Pro", "firmware": "1.0.15",
                                    "ready": false, "reason": "firmwareOld", "note": "update", "minimumFirmware": "1.0.16" } },
                "noise": { "enabled": true, "engine": "deepFilterNet", "strength": 30, "engines": ["rnnoise", "deepFilterNet"],
                           "active": "deepFilterNet", "latencyMs": 20, "available": true, "error": null },
                "voice": { "enabled": true, "voice": "custom", "pitch": 2, "formant": -1, "active": "custom",
                           "latencyMs": 12, "available": true, "error": null },
                "compressor": { "enabled": true, "threshold": -20, "ratio": 3, "attack": 5, "release": 80, "knee": 6,
                                "makeup": 2, "available": true, "error": null },
                "expander": { "enabled": false, "threshold": -50, "ratio": 2, "range": 20, "attack": 1, "release": 100,
                              "hold": 50, "available": true, "error": null } },
              { "id": "xlr2", "name": "XLR 2", "levels": { "monitor": 0.4 }, "mutedIn": [], "hardware": true,
                "present": true, "binding": { "kind": "unit", "pair": 1 }, "bindingConnected": false,
                "bindingWarning": "no signal" },
              { "id": "headset", "name": "Headset", "levels": { "monitor": 0.7, "stream": 0.7 }, "mutedIn": [],
                "hardware": false, "captureSource": "alsa_input.headset", "capturePair": 0, "captureConnected": true,
                "microphone": true, "volume": 1, "linkedSends": false,
                "captureGain": { "gainDb": 3, "minDb": -20, "maxDb": 12, "hardware": false, "muted": false },
                "ownClock": { "on": true, "running": true, "delayMs": 5, "underruns": 1, "overruns": 0 } },
              { "id": "music", "name": "Music", "levels": { "monitor": 0.8, "stream": 1 }, "mutedIn": [],
                "hardware": false, "volume": 0.6, "linkedSends": false, "binding": { "kind": "apps" } },
              { "id": "sfx", "name": "SFX", "levels": { "monitor": 1, "stream": 1 }, "mutedIn": [], "hardware": false,
                "volume": 0.5, "appearance": { "icon": "", "colour": null, "hidden": true, "defaultIcon": "burst" } }
            ],
            "monitorOutputs": ["alsa_output.arctis", "bluez_output.buds"],
            "monitorFeeds": { "alsa_output.arctis": "monitor", "bluez_output.buds": "monitor" },
            "monitorIdleRelease": ["bluez_output.buds"],
            "defaultMonitorMix": "monitor", "outputVolume": 0.5, "monitorMuted": false,
            "outputVolumeLock": { "device": "alsa_output.arctis", "volume": 0.4 },
            "auxPortEnabled": true, "lowCutHz": 80, "softClipGuard": true, "softClipGuardAvailable": true,
            "enforcedDefaultSink": "OpenXLR_ch_system", "enforcedDefaultSource": "OpenXLR_chat",
            "streams": [
              { "id": 669, "label": "Spotify", "identity": "spotify", "channelId": "music", "active": true,
                "running": true, "volume": 0.9, "liveVolume": 0.9 }
            ],
            "inserts": {
              "xlr1": [
                { "insert": { "id": "abc", "kind": "lv2", "plugin": "urn:eq", "label": "EQ", "bypass": false,
                              "nativeHost": true, "params": { "gain": 0.25 } },
                  "error": "the plugin crashed", "nativeHostRunning": true, "nativeUiBlocked": true,
                  "nativeUiBlockReason": "blocked" }
              ],
              "mix:monitor": [
                { "insert": { "id": "def", "kind": "clap", "plugin": "com.limiter", "bypass": true } }
              ]
            },
            "layoutWarning": "layout note",
            "hardwareMixing": { "wanted": true, "active": true, "reason": null, "note": "the unit mixes",
                                "disabled": { "eq:mix:monitor": "the unit mixes this" },
                                "entryOffsetsDb": { "xlr1": -6 } },
            "soundboard": {
              "channel": "sfx", "enabled": true, "available": true, "error": null, "formats": ["wav", "ogg"],
              "pages": ["", "Jokes"], "playing": ["airhorn"], "auditioning": "airhorn",
              "clips": [
                { "id": "airhorn", "name": "Airhorn", "seconds": 2.4, "gain": 0.8, "mode": "overlap", "loop": false,
                  "icon": "", "colour": "#00ff00", "loaded": true, "status": "ready", "error": null, "playing": true, "page": "" },
                { "id": "broken", "name": "Broken", "seconds": 1, "gain": 0.5, "mode": "hold", "loop": true,
                  "icon": "burst", "colour": null, "loaded": false, "status": "error", "error": "cannot decode",
                  "playing": false, "page": "Jokes" }
              ]
            }
          },
          "devices": [
            { "name": "alsa_output.arctis", "description": "Arctis", "kind": 0, "isOwn": false, "isPhysical": true,
              "volume": 0.4, "muted": false },
            { "name": "bluez_output.buds", "description": "Buds", "kind": 0, "isOwn": false, "isPhysical": true },
            { "name": "alsa_input.usb-mic", "description": "USB Mic", "kind": 1, "isOwn": false, "isPhysical": true,
              "channels": 2, "positions": ["FL", "FR"], "drivenUnit": null },
            { "name": "alsa_input.headset", "description": "Headset", "kind": 1, "isOwn": false, "isPhysical": true,
              "channels": 1, "positions": ["MONO"] }
          ],
          "profiles": ["default", "stream"], "activeProfile": "default", "recallOnConnect": "stream",
          "detected": [ { "usbId": "0fd9:00b4", "name": "Elgato Wave XLR Pro", "active": true } ],
          "waveInterfaces": [
            { "id": "0fd9:00b4@1", "name": "Wave XLR Pro", "active": true, "enabled": true, "connected": true,
              "capabilities": { "gain": true }, "state": { "gainDb": 40 } },
            { "id": "0fd9:00ca@2", "name": "Wave:3", "active": false, "enabled": true, "connected": true,
              "capabilities": { "gain": true, "mute": true }, "state": { "gainDb": 20, "mute": false } }
          ],
          "ducking": { "enabled": true, "keys": ["xlr1", "headset"], "key": "xlr1", "channels": ["music"],
                       "mixes": ["monitor"], "sensitivity": "medium", "amount": -12, "speed": { "attack": 10, "release": 300 },
                       "processing": "device", "backend": null, "available": true, "error": null,
                       "device": { "unit": "0fd9:00b4@1", "name": "Wave XLR Pro", "ready": false, "reason": "partialMix",
                                   "note": "a channel is not ducked", "outputs": ["monitor"], "held": true } },
          "history": { "undo": { "step": 4, "kind": "sendLevel", "channel": "music", "mix": "monitor", "group": null,
                                 "count": 1, "label": "Music in Monitor A" },
                       "redo": null, "undoSteps": 4, "redoSteps": 0, "note": null }
        }
        """;

    [Fact]
    public void TheFixtureDrawsEveryTabWithoutAFailure()
    {
        App app = Ready(StateJson);
        Assert.NotNull(app.Link.State);
        Assert.Null(Exercise(app));
    }

    /// <summary>
    /// Every field and every list entry of the state, one at a time, made
    /// null: the state is read with the field's default or without the
    /// entry, or skipped when a number or a switch is null, and either way
    /// every tab draws and takes its keys without a failure.
    /// </summary>
    [Fact]
    public void ANullAnywhereInTheStateDrawsEveryTab()
    {
        List<string> failures = [];
        int applied = 0, variants = 0;
        foreach ((string path, string json) in NullVariants(JsonNode.Parse(StateJson)!, "$"))
        {
            variants++;
            DaemonLink link = new();
            link.Receive(StateJson);
            Snapshot? good = link.State;
            link.Receive(json);
            if (!ReferenceEquals(link.State, good)) applied++;
            App app = new(link, Theme.Material, time: new ManualTime());
            if (Exercise(app) is { } failure) failures.Add($"{path}: {failure}");
        }
        Assert.True(variants > 200, $"only {variants} variants");
        // Most fields are text, lists, maps and objects, which now keep a default.
        Assert.True(applied > variants / 2, $"{applied} of {variants} applied");
        Assert.Empty(failures);
    }

    [Fact]
    public void ANullFieldKeepsItsDefaultAndANullEntryIsLeftOut()
    {
        JsonNode root = JsonNode.Parse(StateJson)!;
        JsonNode channels = root["mixer"]!["channels"]!;
        channels[0]!["levels"] = null;
        channels[0]!["mutedIn"] = new JsonArray("monitor", null);
        channels[0]!["appearance"] = null;
        channels[0]!["name"] = null;
        channels.AsArray().Insert(1, null);
        root["mixer"]!["mixes"]![1]!["name"] = null;
        root["mixer"]!["mixes"]![1]!["kind"] = null;
        root["mixer"]!["inserts"]!["xlr1"]![0] = null;
        root["mixer"]!["inserts"]!["mix:monitor"] = null;
        root["mixer"]!["monitorFeeds"]!["alsa_output.arctis"] = null;
        root["mixer"]!["soundboard"]!["clips"]![0]!["page"] = null;
        root["profiles"] = new JsonArray(null, "default");

        DaemonLink link = new();
        link.Receive(root.ToJsonString());
        Snapshot state = Assert.IsType<Snapshot>(link.State);
        ChannelEntry xlr1 = state.Mixer.Channels[0];
        Assert.Equal(5, state.Mixer.Channels.Count);
        Assert.Empty(xlr1.Levels);
        Assert.Equal(["monitor"], xlr1.MutedIn);
        Assert.Equal(string.Empty, xlr1.Name);
        Assert.NotNull(xlr1.Appearance);
        Assert.Equal(string.Empty, state.Mixer.Mixes[1].Name);
        Assert.Equal("virtualMic", state.Mixer.Mixes[1].Kind);
        Assert.Empty(state.Mixer.Inserts["xlr1"]);
        Assert.False(state.Mixer.Inserts.ContainsKey("mix:monitor"));
        Assert.False(state.Mixer.MonitorFeeds.ContainsKey("alsa_output.arctis"));
        Assert.Equal(string.Empty, state.Mixer.Soundboard!.Clips[0].Page);
        Assert.Equal(["default"], state.Profiles);

        Assert.Null(Exercise(new App(link, Theme.Material, time: new ManualTime())));
    }

    [Fact]
    public void ANullWholeSectionDrawsAsAnEmptyOne()
    {
        JsonNode root = JsonNode.Parse(StateJson)!;
        root["mixer"] = null;
        root["capabilities"] = null;
        root["state"] = null;
        root["devices"] = null;

        DaemonLink link = new();
        link.Receive(root.ToJsonString());
        Snapshot state = Assert.IsType<Snapshot>(link.State);
        Assert.Empty(state.Mixer.Mixes);
        Assert.Empty(state.Capabilities);
        App app = new(link, Theme.Material, time: new ManualTime());
        Assert.Null(Exercise(app));
        app.ShowTab(0);
        Screen screen = new(140, 36);
        app.Draw(screen);
        Assert.Contains("The mixer is not built yet", Frame(screen), StringComparison.Ordinal);
    }

    [Fact]
    public void AFrameThatThrowsShowsTheErrorAndKeepsTheOtherTabs()
    {
        DaemonLink link = new();
        link.Receive(StateJson);
        Throwing broken = new();
        App app = new(link, Theme.Material, time: new ManualTime(), views: [broken, new MixerView()]);
        Screen screen = new(140, 36);

        app.Draw(screen);
        Assert.IsType<InvalidOperationException>(app.LastFailure);
        Assert.Contains("this tab could not be drawn", Row(screen, 0), StringComparison.Ordinal);
        Assert.Contains("Could not draw Broken: no such field", Row(screen, 1), StringComparison.Ordinal);
        Assert.Contains("2 Mixer", Frame(screen), StringComparison.Ordinal);
        Assert.Contains("help", Row(screen, screen.Height - 1), StringComparison.Ordinal);

        // The frame stays up, so the keys still choose another tab.
        app.Handle(new KeyPress(Key.Char, '2'));
        app.Draw(screen);
        Assert.Contains("Elgato Wave XLR Pro", Row(screen, 0), StringComparison.Ordinal);
        Assert.DoesNotContain("Could not draw", Frame(screen), StringComparison.Ordinal);

        // Back on the tab once it draws again, the error is gone.
        broken.Fails = false;
        app.Handle(new KeyPress(Key.Char, '1'));
        app.Draw(screen);
        Assert.DoesNotContain("Could not draw", Frame(screen), StringComparison.Ordinal);
        Assert.Contains("Elgato Wave XLR Pro", Row(screen, 0), StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyThatThrowsSaysSoOnTheBottomLine()
    {
        DaemonLink link = new();
        link.Receive(StateJson);
        App app = new(link, Theme.Material, time: new ManualTime(), views: [new Throwing { Fails = false }]);
        Screen screen = new(140, 36);
        app.Draw(screen);

        app.Handle(new KeyPress(Key.Char, 'x'));
        Assert.True(app.Running);
        Assert.IsType<InvalidOperationException>(app.LastFailure);
        app.Draw(screen);
        Assert.Contains("That key failed in Broken: no such field", Row(screen, screen.Height - 1),
            StringComparison.Ordinal);
    }

    /// <summary>A tab that throws while drawing, while <see cref="Fails"/> holds, and on every key.</summary>
    private sealed class Throwing : View
    {
        public bool Fails { get; set; } = true;
        public override string Title => "Broken";
        public override string Keys => "x break";

        public override void Draw(Screen screen, Rect area, App app)
        {
            if (Fails) throw new InvalidOperationException("no such field");
            screen.Text(area.X, area.Y, "drawn", app.Theme.TextPrimary, app.Theme.Card);
        }

        public override bool Handle(KeyPress key, App app) => throw new InvalidOperationException("no such field");
    }

    private static App Ready(string json)
    {
        DaemonLink link = new();
        link.Receive(json);
        return new App(link, Theme.Material, time: new ManualTime());
    }

    /// <summary>
    /// Every tab, drawn wide with the sidebar and narrow with the tab row,
    /// then walked down its rows and across its columns with a frame after
    /// each key, so the detail lines of each selected item are drawn too.
    /// The first failure the frame caught, or null.
    /// </summary>
    private static string? Exercise(App app)
    {
        Screen wide = new(160, 50), narrow = new(100, 28);
        for (int tab = 0; tab < app.Views.Count; tab++)
        {
            app.ShowTab(tab);
            app.Draw(wide);
            app.Draw(narrow);
            for (int step = 0; step < 10; step++)
            {
                app.Handle(new KeyPress(step < 8 ? Key.Down : Key.Right));
                app.Draw(wide);
            }
            if (app.LastFailure is { } failure) return $"{app.Views[tab].Title}: {failure}";
        }
        return null;
    }

    /// <summary>The state with one field or one list entry set to null, for each one at every depth.</summary>
    private static IEnumerable<(string Path, string Json)> NullVariants(JsonNode root, string path)
    {
        List<(string Path, Action<JsonNode?> Set, JsonNode Child)> slots = [];
        Walk(root, path, slots);
        foreach ((string at, Action<JsonNode?> set, JsonNode child) in slots)
        {
            // A null in the child's place, then the child itself back, so the
            // slots found under it still point into the tree.
            set(null);
            yield return (at, root.ToJsonString());
            set(child);
        }
    }

    private static void Walk(JsonNode node, string path, List<(string, Action<JsonNode?>, JsonNode)> slots)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (string name in obj.Select(pair => pair.Key).ToList())
                {
                    if (name == "type" && path == "$") continue;
                    if (obj[name] is not { } child) continue;
                    slots.Add(($"{path}.{name}", value => obj[name] = value, child));
                    Walk(child, $"{path}.{name}", slots);
                }
                break;
            case JsonArray array:
                for (int index = 0; index < array.Count; index++)
                {
                    if (array[index] is not { } child) continue;
                    int at = index;
                    slots.Add(($"{path}[{index}]", value => array[at] = value, child));
                    Walk(child, $"{path}[{index}]", slots);
                }
                break;
        }
    }

    private static string Row(Screen screen, int y) =>
        new(Enumerable.Range(0, screen.Width).Select(x => screen.At(x, y).Ch).ToArray());

    private static string Frame(Screen screen) =>
        string.Join('\n', Enumerable.Range(0, screen.Height).Select(y => Row(screen, y)));
}
