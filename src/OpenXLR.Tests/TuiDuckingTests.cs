using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.Tui;

namespace OpenXLR.Tests;

/// <summary>
/// The terminal mixer's Ducking group on the Inputs tab, on any device: the
/// switch, a strip of keys, one of channels and one of mixes whose keys
/// each flip one id, the three presets, and on a
/// unit that can run it, the choice between the unit and this computer with
/// the reason it does not. Every key sends one command and the rows follow
/// the next state. A daemon from before channels and mixes gets the rest.
/// </summary>
public sealed class TuiDuckingTests
{
    private const string Unit = "0fd9:00b4@00112233445566aa";

    /// <summary>A Wave XLR Pro with the default nine channels, a capture microphone and five mixes.</summary>
    private const string ProJson = """
        {
          "type": "state",
          "daemonVersion": "0.1.71",
          "connected": true,
          "device": { "vendor": "Elgato", "model": "Wave XLR Pro", "usbId": "0fd9:00b4" },
          "capabilities": { "gain": true, "mute": true, "xlrInputs": 2, "xlrJacks": true, "hpOutputs": 2,
                            "hpVolume": true, "outputRouting": true, "auxInput": true, "ducking": true },
          "state": { "gainDb": 40, "mute": false },
          "ducking": { "enabled": false, "keys": ["xlr1"], "key": "xlr1", "channels": ["game", "music", "xlr2"], "mixes": ["monitor"],
                       "sensitivity": "medium", "amount": "medium", "speed": "medium", "processing": "device",
                       "backend": "device", "available": true, "error": null,
                       "device": { "unit": "0fd9:00b4@00112233445566aa", "name": "Wave XLR Pro", "firmware": null,
                                   "ready": true, "reason": null, "note": null, "outputs": ["monitor"], "held": true,
                                   "readback": { "sensitivity": "medium", "amount": "medium", "speed": "medium", "pairs": [1], "entries": [4, 7, 8] } } },
          "mixer": {
            "mixes": [
              { "id": "monitor", "name": "Monitor A", "volume": 1, "muted": false, "kind": "monitor" },
              { "id": "monitor2", "name": "Monitor B", "volume": 1, "muted": false, "kind": "monitor" },
              { "id": "stream", "name": "Stream", "volume": 1, "muted": false, "kind": "virtualMic" },
              { "id": "chat", "name": "Chat", "volume": 1, "muted": false, "kind": "virtualMic" },
              { "id": "auxout", "name": "Aux", "volume": 1, "muted": false, "kind": "auxPort" }
            ],
            "channels": [
              { "id": "xlr1", "name": "XLR 1", "hardware": true, "microphone": true, "levels": {}, "mutedIn": [] },
              { "id": "xlr2", "name": "XLR 2", "hardware": true, "microphone": true, "levels": {}, "mutedIn": [] },
              { "id": "aux", "name": "Aux In", "hardware": true, "microphone": false, "levels": {}, "mutedIn": [] },
              { "id": "game", "name": "Game", "microphone": false, "levels": {}, "mutedIn": [], "volume": 1 },
              { "id": "music", "name": "Music", "microphone": false, "levels": {}, "mutedIn": [], "volume": 1 },
              { "id": "browser", "name": "Browser", "microphone": false, "levels": {}, "mutedIn": [], "volume": 1 },
              { "id": "system", "name": "System", "microphone": false, "levels": {}, "mutedIn": [], "volume": 1 },
              { "id": "voicechat", "name": "Voice Chat", "microphone": false, "levels": {}, "mutedIn": [], "volume": 1 },
              { "id": "sfx", "name": "SFX", "microphone": false, "levels": {}, "mutedIn": [], "volume": 1 },
              { "id": "cap1", "name": "Guest mic", "microphone": true, "captureSource": "alsa_input.usb-Yeti",
                "captureMonoChannel": 0, "levels": {}, "mutedIn": [], "volume": 1 }
            ],
            "monitorOutputs": [],
            "lowCutHz": 0,
            "softClipGuard": false,
            "softClipGuardAvailable": true,
            "inserts": {}
          },
          "devices": [],
          "profiles": []
        }
        """;

    private static string Edit(Action<JsonNode> change)
    {
        JsonNode root = JsonNode.Parse(ProJson)!;
        change(root);
        return root.ToJsonString();
    }

    private static JsonNode Ducking(JsonNode root) => root["ducking"]!;

    /// <summary>The keys as the daemon reports them: the list, and the first as <c>key</c>.</summary>
    private static void SetKeys(JsonNode root, params string[] keys)
    {
        Ducking(root)["keys"] = new JsonArray([.. keys.Select(key => (JsonNode?)JsonValue.Create(key))]);
        Ducking(root)["key"] = keys[0];
    }

    private static KeyPress Space => new(Key.Char, ' ');

    /// <summary>An XLR Dock MK.2: no hardware ducking, so <c>device</c> is null and XLR 2 is not on the unit.</summary>
    private static string DockJson(Action<JsonNode>? change = null) => Edit(root =>
    {
        root["device"] = new JsonObject { ["vendor"] = "Elgato", ["model"] = "XLR Dock MK.2", ["usbId"] = "0fd9:00c7" };
        root["capabilities"]!.AsObject().Remove("ducking");
        root["capabilities"]!["xlrInputs"] = 1;
        root["mixer"]!["channels"]![1]!["present"] = false;
        root["mixer"]!["channels"]![2]!["present"] = false;
        Ducking(root)["channels"] = new JsonArray("game", "music");
        Ducking(root)["backend"] = null;
        Ducking(root)["device"] = null;
        change?.Invoke(root);
    });

    private static (App App, List<string> Sent) Ready(string json, int width = 160, int height = 80)
    {
        DaemonLink link = new();
        List<string> sent = [];
        link.Sent += sent.Add;
        link.Receive(json);
        App app = new(link, Theme.Material);
        app.ShowTab(2);
        app.Draw(new Screen(width, height));
        return (app, sent);
    }

    private static string[] Draw(App app, int width = 160, int height = 80)
    {
        Screen screen = new(width, height);
        app.Draw(screen);
        return [.. Enumerable.Range(0, screen.Height)
            .Select(y => new string(Enumerable.Range(0, screen.Width).Select(x => screen.At(x, y).Ch).ToArray()))];
    }

    /// <summary>
    /// Walk down the Inputs tab until the selected row is the one with this
    /// label, and return that row from its marker on. Two cards share a line
    /// on a wide terminal, so the marker two cells before the label is what
    /// says which row it is, and the gap after it tells Keys from Keys heard.
    /// The walk starts from the top, so a row above the cursor is found too.
    /// </summary>
    private static string Select(App app, string label, int width = 160, int height = 80)
    {
        for (int i = 0; i < 20; i++) app.Handle(new KeyPress(Key.PageUp));
        for (int i = 0; i < 160; i++)
        {
            string? focused = Draw(app, width, height).FirstOrDefault(line =>
                line.IndexOf(label, StringComparison.Ordinal) is int at and >= 2 && line[at - 2] == '┃'
                && line.AsSpan(at + label.Length).StartsWith("  ", StringComparison.Ordinal));
            if (focused is not null) return focused[(focused.IndexOf(label, StringComparison.Ordinal) - 2)..];
            app.Handle(new KeyPress(Key.Down));
        }
        throw new Xunit.Sdk.XunitException($"No row named {label} on the Inputs tab.");
    }

    private static bool Shows(App app, string label, int width = 160, int height = 80) =>
        Draw(app, width, height).Any(line => line.Contains(label, StringComparison.Ordinal));

    private static JsonElement Command(List<string> sent, string name = "setDucking")
    {
        JsonElement command = JsonDocument.Parse(Assert.Single(sent)).RootElement;
        Assert.Equal(name, command.GetProperty("cmd").GetString());
        sent.Clear();
        return command.GetProperty("value");
    }

    private static string[] Ids(JsonElement list) => [.. list.EnumerateArray().Select(item => item.GetString()!)];

    [Fact]
    public void TheSwitchAndThePresetsEachSendTheOneFieldTheyChange()
    {
        (App app, List<string> sent) = Ready(ProJson);
        Select(app, "Ducking");
        app.Handle(new KeyPress(Key.Char, ' '));
        JsonElement value = Command(sent);
        Assert.True(value.GetProperty("enabled").GetBoolean());
        Assert.Single(value.EnumerateObject());

        Assert.Contains("medium, -12 dBFS", Select(app, "Sensitivity"), StringComparison.Ordinal);
        app.Handle(new KeyPress(Key.Right));
        Assert.Equal("high", Command(sent).GetProperty("sensitivity").GetString());

        Assert.Contains("medium, 12 dB", Select(app, "Amount"), StringComparison.Ordinal);
        app.Handle(new KeyPress(Key.Left));
        Assert.Equal("low", Command(sent).GetProperty("amount").GetString());

        Select(app, "Speed");
        app.Handle(new KeyPress(Key.Right));
        Assert.Equal("fast", Command(sent).GetProperty("speed").GetString());
    }

    [Fact]
    public void NumbersShowAsTheirPresetOrAsACustomChoiceThatSendsNothing()
    {
        (App app, List<string> sent) = Ready(Edit(root =>
        {
            Ducking(root)["sensitivity"] = -30;
            Ducking(root)["amount"] = 24;
            Ducking(root)["speed"] = new JsonObject { ["attack"] = 45, ["release"] = 900 };
            Ducking(root)["device"]!["readback"]!["speed"] = new JsonObject { ["attack"] = 45, ["release"] = 900 };
        }));
        // The state still parses: a number the UI's sliders set is no reason to drop the whole snapshot.
        Assert.Contains("custom, -30 dBFS", Select(app, "Sensitivity"), StringComparison.Ordinal);
        app.Handle(new KeyPress(Key.Right));
        Assert.Equal("low", Command(sent).GetProperty("sensitivity").GetString());
        // A number a preset writes reads as that preset.
        Assert.Contains("high, 24 dB", Select(app, "Amount"), StringComparison.Ordinal);
        Assert.Contains("custom, 45/900 ms", Select(app, "Speed"), StringComparison.Ordinal);
        app.Handle(new KeyPress(Key.Left));
        Assert.Equal("fast", Command(sent).GetProperty("speed").GetString());
        Assert.Equal(24, app.Link.State!.Ducking!.Depth);
    }

    [Fact]
    public void TheKeysStripAddsAKeyAtTheEndAndTakesItOutOfTheChannels()
    {
        (App app, List<string> sent) = Ready(ProJson);
        string strip = Select(app, "Keys");
        Assert.Contains("✓ XLR 1", strip, StringComparison.Ordinal);
        Assert.Contains("XLR 2", strip, StringComparison.Ordinal);
        Assert.DoesNotContain("✓ XLR 2", strip, StringComparison.Ordinal);
        // Aux In and the application channels key ducking as the microphones do.
        Assert.Contains("Aux In", strip, StringComparison.Ordinal);
        Assert.Contains("Game", strip, StringComparison.Ordinal);

        // XLR 1 is the only key, and the last key stays: nothing is sent.
        app.Handle(Space);
        Assert.Empty(sent);
        // XLR 2 is ducked, so making it a key takes it off the channels in the same command.
        app.Handle(new KeyPress(Key.Right));
        app.Handle(Space);
        JsonElement value = Command(sent);
        Assert.Equal(["xlr1", "xlr2"], Ids(value.GetProperty("keys")));
        Assert.Equal(["game", "music"], Ids(value.GetProperty("channels")));
        Assert.False(value.TryGetProperty("key", out _));
        // Aux In is not ducked: the keys alone. The daemon has not answered
        // yet, so the press builds on the last one rather than dropping XLR 2.
        app.Handle(new KeyPress(Key.Right));
        app.Handle(Space);
        value = Command(sent);
        Assert.Equal(["xlr1", "xlr2", "aux"], Ids(value.GetProperty("keys")));
        Assert.False(value.TryGetProperty("channels", out _));
        // Once the daemon's state arrives, presses start from it again.
        app.Link.Receive(Edit(root => SetKeys(root, "xlr1", "aux")));
        app.Handle(Space);
        Assert.Equal(["xlr1"], Ids(Command(sent).GetProperty("keys")));

        // With several keys every one is ticked, any of them can go, and none is offered as a channel.
        app.Link.Receive(Edit(root => SetKeys(root, "xlr1", "voicechat")));
        app.Handle(new KeyPress(Key.Home));
        Assert.Contains("✓ XLR 1", Select(app, "Keys"), StringComparison.Ordinal);
        // The strip scrolls to keep the cursor in view: Voice Chat is the eighth.
        for (int i = 0; i < 7; i++) app.Handle(new KeyPress(Key.Right));
        Assert.Contains("✓ Voice Chat", Select(app, "Keys"), StringComparison.Ordinal);
        app.Handle(new KeyPress(Key.Home));
        app.Handle(Space);
        Assert.Equal(["voicechat"], Ids(Command(sent).GetProperty("keys")));
        string channels = Select(app, "Channels");
        Assert.DoesNotContain("XLR 1", channels, StringComparison.Ordinal);
        Assert.DoesNotContain("Voice Chat", channels, StringComparison.Ordinal);
        Assert.Contains("Music", channels, StringComparison.Ordinal);
    }

    [Fact]
    public void AStereoCaptureInputIsNoKeyAndAMissingKeyStaysShown()
    {
        (App app, List<string> sent) = Ready(Edit(root =>
        {
            root["mixer"]!["channels"]!.AsArray().Add(JsonNode.Parse("""
                { "id": "cap2", "name": "Console", "microphone": false, "captureSource": "alsa_input.usb-Console",
                  "levels": {}, "mutedIn": [], "volume": 1 }
                """));
            SetKeys(root, "xlr1", "gone");
        }));
        Select(app, "Keys");
        app.Handle(new KeyPress(Key.End));
        string strip = Select(app, "Keys");
        // The capture microphone keys ducking; the stereo capture input does not.
        Assert.Contains("Guest mic", strip, StringComparison.Ordinal);
        Assert.DoesNotContain("Console", strip, StringComparison.Ordinal);
        // A saved key the layout no longer has stays shown at the end, ticked, and can be taken out.
        Assert.Contains("✓ gone (missing)", strip, StringComparison.Ordinal);
        Assert.Equal(11, app.Link.State!.Mixer.Channels.Count(InputsView.CanKey) + 1);
        app.Handle(Space);
        Assert.Equal(["xlr1"], Ids(Command(sent).GetProperty("keys")));
        // The stereo capture input is still a channel to lower, the strip's last.
        Select(app, "Channels");
        app.Handle(new KeyPress(Key.End));
        Assert.Contains("Console", Select(app, "Channels"), StringComparison.Ordinal);
    }

    [Fact]
    public void ADaemonThatSendsOnlyKeyHasThatOneKeyAndANinthKeyIsNotAdded()
    {
        (App app, List<string> sent) = Ready(Edit(root =>
        {
            Ducking(root).AsObject().Remove("keys");
            Ducking(root)["key"] = "cap1";
        }));
        Assert.Equal(["cap1"], app.Link.State!.Ducking!.KeyList);
        Select(app, "Keys");
        app.Handle(new KeyPress(Key.End));
        Assert.Contains("✓ Guest mic", Select(app, "Keys"), StringComparison.Ordinal);
        app.Handle(new KeyPress(Key.Home));
        app.Handle(Space);
        Assert.Equal(["cap1", "xlr1"], Ids(Command(sent).GetProperty("keys")));
        // The key is no channel to lower: the strip's last channel is SFX.
        Select(app, "Channels");
        app.Handle(new KeyPress(Key.End));
        string channels = Select(app, "Channels");
        Assert.Contains("SFX", channels, StringComparison.Ordinal);
        Assert.DoesNotContain("Guest mic", channels, StringComparison.Ordinal);

        // Eight keys are the most: a ninth sends nothing, and one can still be taken out.
        (App full, List<string> quiet) = Ready(Edit(root =>
            SetKeys(root, "xlr1", "xlr2", "aux", "game", "music", "browser", "system", "voicechat")));
        Select(full, "Keys");
        for (int i = 0; i < 8; i++) full.Handle(new KeyPress(Key.Right));
        full.Handle(Space);
        Assert.Empty(quiet);
        full.Handle(new KeyPress(Key.Home));
        full.Handle(Space);
        Assert.Equal(["xlr2", "aux", "game", "music", "browser", "system", "voicechat"], Ids(Command(quiet).GetProperty("keys")));
    }

    [Fact]
    public void TheChannelsAndMixesStripsFlipOneIdEachAndLeaveTheKeyOut()
    {
        (App app, List<string> sent) = Ready(ProJson);
        string channels = Select(app, "Channels");
        Assert.DoesNotContain("XLR 1", channels, StringComparison.Ordinal);
        Assert.Contains("✓ XLR 2", channels, StringComparison.Ordinal);
        Assert.Contains("Aux In", channels, StringComparison.Ordinal);
        Assert.DoesNotContain("✓ Aux In", channels, StringComparison.Ordinal);
        Assert.Contains("✓ Game", channels, StringComparison.Ordinal);

        // The cursor starts on the first channel; space takes XLR 2 off the list.
        app.Handle(new KeyPress(Key.Char, ' '));
        Assert.Equal(["game", "music"], Ids(Command(sent).GetProperty("channels")));
        // Two to the right is Game, which is ducked; Enter takes it off too, the rest kept in order.
        app.Handle(new KeyPress(Key.Right));
        app.Handle(new KeyPress(Key.Right));
        Assert.Empty(sent);
        app.Handle(new KeyPress(Key.Enter));
        Assert.Equal(["music", "xlr2"], Ids(Command(sent).GetProperty("channels")));
        // Browser is not ducked: it joins at the end.
        app.Handle(new KeyPress(Key.Right));
        app.Handle(new KeyPress(Key.Right));
        app.Handle(new KeyPress(Key.Char, ' '));
        Assert.Equal(["game", "music", "xlr2", "browser"], Ids(Command(sent).GetProperty("channels")));
        // The ends hold, and Home goes back to the first.
        for (int i = 0; i < 20; i++) app.Handle(new KeyPress(Key.Right));
        app.Handle(new KeyPress(Key.Char, ' '));
        Assert.Equal(["game", "music", "xlr2", "cap1"], Ids(Command(sent).GetProperty("channels")));
        app.Handle(new KeyPress(Key.Home));
        app.Handle(new KeyPress(Key.Char, ' '));
        Assert.Equal(["game", "music"], Ids(Command(sent).GetProperty("channels")));

        string mixes = Select(app, "Mixes");
        Assert.Contains("✓ Monitor A", mixes, StringComparison.Ordinal);
        Assert.Contains("Stream", mixes, StringComparison.Ordinal);
        Assert.DoesNotContain("✓ Stream", mixes, StringComparison.Ordinal);
        app.Handle(new KeyPress(Key.Right));
        app.Handle(new KeyPress(Key.Right));
        app.Handle(new KeyPress(Key.Char, ' '));
        Assert.Equal(["monitor", "stream"], Ids(Command(sent).GetProperty("mixes")));
        app.Handle(new KeyPress(Key.Home));
        app.Handle(new KeyPress(Key.Enter));
        Assert.Empty(Ids(Command(sent).GetProperty("mixes")));

        // The rows follow the daemon: once it reports Stream ducked, the key is lit.
        app.Link.Receive(Edit(root => Ducking(root)["mixes"] = new JsonArray("monitor", "stream")));
        Assert.Contains("✓ Stream", Select(app, "Mixes"), StringComparison.Ordinal);
        // The key changed to Guest mic: XLR 1 is offered and the guest is not.
        app.Link.Receive(Edit(root => SetKeys(root, "cap1")));
        channels = Select(app, "Channels");
        Assert.Contains("XLR 1", channels, StringComparison.Ordinal);
        Assert.DoesNotContain("Guest mic", channels, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSwitchSaysWhereDuckingRunsOrWhatIsWrong()
    {
        string Note(Action<JsonNode> change) => Select(Ready(Edit(root =>
        {
            Ducking(root)["enabled"] = true;
            change(Ducking(root));
        })).App, "Ducking");

        Assert.DoesNotContain("on the unit", Select(Ready(ProJson).App, "Ducking"), StringComparison.Ordinal);
        Assert.Contains("on the unit", Note(_ => { }), StringComparison.Ordinal);
        Assert.Contains("not held yet", Note(d => d["device"]!["held"] = false), StringComparison.Ordinal);
        Assert.Contains("in software", Note(d => d["backend"] = "software"), StringComparison.Ordinal);
        Assert.Contains("not running", Note(d => d["backend"] = null), StringComparison.Ordinal);
        Assert.Contains("cannot run", Note(d =>
        {
            d["backend"] = null;
            d["available"] = false;
        }), StringComparison.Ordinal);
    }

    [Fact]
    public void RunsOnOffersTheUnitOrThisComputerAndSaysWhereTheKeyIsHeard()
    {
        (App app, List<string> sent) = Ready(ProJson);
        Assert.Contains("this unit", Select(app, "Runs on"), StringComparison.Ordinal);
        Assert.Contains("on the unit, before inserts", Select(app, "Keys heard"), StringComparison.Ordinal);
        Assert.False(Shows(app, "Not on unit"));
        Select(app, "Runs on");
        app.Handle(new KeyPress(Key.Char, ' '));
        Assert.Equal("software", Command(sent, "setDuckingProcessing").GetString());

        // Software chosen: the key is heard after the microphone's processing, and no reason is given.
        (App software, List<string> back) = Ready(Edit(root =>
        {
            Ducking(root)["processing"] = "software";
            Ducking(root)["backend"] = "software";
        }));
        Assert.Contains("this computer", Select(software, "Runs on"), StringComparison.Ordinal);
        Assert.Contains("after the EQ and inserts", Select(software, "Keys heard"), StringComparison.Ordinal);
        Assert.False(Shows(software, "Not on unit"));
        Select(software, "Runs on");
        software.Handle(new KeyPress(Key.Left));
        Assert.Equal("device", Command(back, "setDuckingProcessing").GetString());

        // The unit is chosen but the setup does not fit it: the reason in a few words.
        foreach ((string reason, string why) in new[]
        {
            ("mixerOff", "the software mixer is off"),
            ("keyNotSupported", "a key the unit cannot hear"),
            ("mixElsewhere", "a ducked mix plays elsewhere too"),
            ("sharedOutput", "the jacks play a mix not ducked"),
            ("partialMix", "a channel in the mix is not ducked"),
            ("keyInMix", "a key is heard in a ducked mix"),
            ("failed", "the unit did not keep the ducking"),
            ("unsupportedBlock", "the unit did not answer a read"),
            ("somethingNew", "The daemon's own sentence."),
        })
        {
            (App unfit, _) = Ready(Edit(root =>
            {
                Ducking(root)["backend"] = "software";
                Ducking(root)["device"]!["ready"] = false;
                Ducking(root)["device"]!["reason"] = reason;
                Ducking(root)["device"]!["note"] = "The daemon's own sentence.";
            }));
            Assert.Contains(why, Select(unfit, "Not on unit"), StringComparison.Ordinal);
            Assert.Contains("this unit", Select(unfit, "Runs on"), StringComparison.Ordinal);
            Assert.Contains("after the EQ and inserts", Select(unfit, "Keys heard"), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WithoutAProTheGroupIsThereWithoutTheChoiceOfWhereItRuns()
    {
        (App app, List<string> sent) = Ready(DockJson());
        Assert.True(Shows(app, "Ducking"));
        Assert.False(Shows(app, "Runs on"));
        Assert.False(Shows(app, "Keys heard"));
        Assert.False(Shows(app, "Not on unit"));
        // XLR 2 is not on this unit, so it is no channel to lower either.
        Assert.DoesNotContain("XLR 2", Select(app, "Channels"), StringComparison.Ordinal);
        Select(app, "Ducking");
        app.Handle(new KeyPress(Key.Enter));
        Assert.True(Command(sent).GetProperty("enabled").GetBoolean());

        // No interface at all: the same group.
        (App none, List<string> quiet) = Ready(DockJson(root =>
        {
            root["connected"] = false;
            root.AsObject().Remove("device");
            root.AsObject().Remove("capabilities");
            root.AsObject().Remove("state");
        }));
        Select(none, "Ducking");
        none.Handle(new KeyPress(Key.Char, ' '));
        Assert.True(Command(quiet).GetProperty("enabled").GetBoolean());

        // The native host is missing: the switch says it cannot run and the daemon says why.
        (App missing, _) = Ready(DockJson(root =>
        {
            Ducking(root)["enabled"] = true;
            Ducking(root)["available"] = false;
            Ducking(root)["error"] = "Ducking needs the OpenXLR native plugin host, which this installation does not include.";
        }));
        Assert.Contains("cannot run", Select(missing, "Ducking"), StringComparison.Ordinal);
        // The sentence is long, so it wraps onto the rows below rather than being cut at the card's edge.
        Assert.Contains("Ducking needs the OpenXLR native plugin", Select(missing, "Software"), StringComparison.Ordinal);
        string[] frame = Draw(missing);
        Assert.Contains(frame, line => line.Contains("host, which this installation does not", StringComparison.Ordinal));
        Assert.Contains(frame, line => line.Contains("include.", StringComparison.Ordinal));

        // A daemon that sends no ducking gets no group, whatever the unit can do.
        (App older, _) = Ready(Edit(root => root.AsObject().Remove("ducking")));
        Assert.False(Shows(older, "Ducking"));
        Assert.False(Shows(older, "Sensitivity"));
    }

    [Fact]
    public void HardwareControlModeSaysDuckingNeedsTheSoftwareMixer()
    {
        (App app, List<string> sent) = Ready(Edit(root => root.AsObject().Remove("mixer")));
        Assert.Contains("needs the software mixer", Select(app, "Ducking"), StringComparison.Ordinal);
        app.Handle(new KeyPress(Key.Char, ' '));
        Assert.Empty(sent);
        Assert.False(Shows(app, "Sensitivity"));
    }

    [Fact]
    public void AnOlderStateWithoutChannelsOrMixesKeepsTheRest()
    {
        (App app, List<string> sent) = Ready(Edit(root =>
        {
            JsonObject ducking = Ducking(root).AsObject();
            ducking.Remove("channels");
            ducking.Remove("mixes");
            ducking.Remove("processing");
            ducking.Remove("available");
            ducking.Remove("error");
            ducking["targets"] = new JsonArray("monitor");
        }));
        Assert.True(Shows(app, "Ducking"));
        Assert.False(Shows(app, "Channels"));
        Assert.False(Shows(app, "Mixes"));
        Assert.False(Shows(app, "Runs on"));
        Select(app, "Keys");
        app.Handle(new KeyPress(Key.Right));
        app.Handle(Space);
        JsonElement value = Command(sent);
        Assert.Equal(["xlr1", "xlr2"], Ids(value.GetProperty("keys")));
        Assert.Single(value.EnumerateObject());
        Select(app, "Amount");
        app.Handle(new KeyPress(Key.Right));
        Assert.Equal("high", Command(sent).GetProperty("amount").GetString());
    }

    [Fact]
    public void TheReductionBarShowsTheLargestCutWhileSoftwareRuns()
    {
        string json = Edit(root =>
        {
            Ducking(root)["enabled"] = true;
            Ducking(root)["amount"] = "high";
            Ducking(root)["processing"] = "software";
            Ducking(root)["backend"] = "software";
        });
        (App app, _) = Ready(json);
        Assert.Contains("0 dB", Select(app, "Lowered by"), StringComparison.Ordinal);
        app.Link.Receive("""{"type":"meters","levels":{"ch:music":[0.5,0.5],"duck:monitor":[9.4],"duck:stream":[14.2]}}""");
        Assert.Equal(14.2, app.Link.DuckReduction(), 3);
        string row = Select(app, "Lowered by");
        Assert.Contains("14 dB", row, StringComparison.Ordinal);
        Assert.Contains('█', row);
        // The reduction is no level: the channel meters are as they were, and a mix's meter is not the duck's.
        Assert.Equal(0.5, app.Link.Meter("ch", "music"), 3);
        Assert.Equal(0, app.Link.Meter("duck", "monitor"));
        // A stage that stops reporting drops out of the reading.
        app.Link.Receive("""{"type":"meters","levels":{"duck:monitor":[3]}}""");
        Assert.Contains("3 dB", Select(app, "Lowered by"), StringComparison.Ordinal);

        // On the unit the meters carry nothing for ducking, so there is no bar.
        Assert.False(Shows(Ready(ProJson).App, "Lowered by"));
    }

    /// <summary>
    /// At 80x24 every ducking row fits on its line: the labels whole, every
    /// value and reason whole, and the channel strip scrolls to keep the
    /// cursor in view with a mark on the side that hides keys.
    /// </summary>
    [Fact]
    public void EveryRowFitsAnEightyByTwentyFourTerminal()
    {
        const int width = 80, height = 24;
        (App app, List<string> sent) = Ready(Edit(root =>
        {
            Ducking(root)["enabled"] = true;
            Ducking(root)["sensitivity"] = "medium";
            Ducking(root)["backend"] = "software";
            Ducking(root)["device"]!["ready"] = false;
            Ducking(root)["device"]!["reason"] = "partialMix";
        }), width, height);
        Assert.Contains("in software", Select(app, "Ducking", width, height), StringComparison.Ordinal);
        Assert.Contains("✓ XLR 1", Select(app, "Keys", width, height), StringComparison.Ordinal);
        Assert.Contains("medium, -12 dBFS  >", Select(app, "Sensitivity", width, height), StringComparison.Ordinal);
        Assert.Contains("medium, 12 dB", Select(app, "Amount", width, height), StringComparison.Ordinal);
        Assert.Contains("this unit", Select(app, "Runs on", width, height), StringComparison.Ordinal);
        Assert.Contains("after the EQ and inserts", Select(app, "Keys heard", width, height), StringComparison.Ordinal);
        Assert.Contains("a channel in the mix is not ducked", Select(app, "Not on unit", width, height), StringComparison.Ordinal);
        Assert.Contains("dB", Select(app, "Lowered by", width, height), StringComparison.Ordinal);
        sent.Clear();

        // Eight channels do not fit one line: the strip starts at the first and marks the rest.
        string strip = Select(app, "Channels", width, height);
        Assert.Contains("✓ XLR 2", strip, StringComparison.Ordinal);
        Assert.EndsWith("›", strip.TrimEnd().TrimEnd('│').TrimEnd(), StringComparison.Ordinal);
        Assert.DoesNotContain("‹", strip, StringComparison.Ordinal);
        // At the last channel the first ones scroll away and the mark moves to the left.
        for (int i = 0; i < 8; i++) app.Handle(new KeyPress(Key.Right));
        strip = Select(app, "Channels", width, height);
        Assert.Contains("Guest mic", strip, StringComparison.Ordinal);
        Assert.Contains("‹", strip, StringComparison.Ordinal);
        Assert.DoesNotContain("›", strip, StringComparison.Ordinal);
        Assert.DoesNotContain("XLR 2", strip, StringComparison.Ordinal);
        app.Handle(new KeyPress(Key.Char, ' '));
        Assert.Equal(["game", "music", "xlr2", "cap1"], Ids(Command(sent).GetProperty("channels")));

        // The fifth mix is out of sight until the cursor reaches it.
        string mixes = Select(app, "Mixes", width, height);
        Assert.Contains("✓ Monitor A", mixes, StringComparison.Ordinal);
        Assert.DoesNotContain(" Aux ", mixes, StringComparison.Ordinal);
        Assert.EndsWith("›", mixes.TrimEnd().TrimEnd('│').TrimEnd(), StringComparison.Ordinal);
        app.Handle(new KeyPress(Key.End));
        Assert.Contains(" Aux ", Select(app, "Mixes", width, height), StringComparison.Ordinal);
    }
}
