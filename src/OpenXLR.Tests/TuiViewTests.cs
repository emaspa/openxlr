using System.Text.Json;
using OpenXLR.Tui;

namespace OpenXLR.Tests;

/// <summary>
/// What the terminal mixer does with a state message and a key press: the
/// state it reads, the tabs it draws, and the commands it sends. The link is
/// fed a message rather than a socket, so none of this needs a daemon.
/// </summary>
public sealed class TuiViewTests
{
    /// <summary>A state message shaped like the daemon's, cut to what the tabs read.</summary>
    private const string StateJson = """
        {
          "type": "state",
          "daemonVersion": "0.1.41",
          "warning": null,
          "connected": true,
          "device": { "vendor": "Elgato", "model": "Wave XLR Pro", "usbId": "0fd9:00b4", "note": null },
          "capabilities": { "gain": true, "mute": true, "lowCut": true, "phantom": true, "clipGuard": true,
                            "compressor": true, "voiceTune": true, "expander": true, "hpVolume": true,
                            "crossfade": true, "outputRouting": true, "auxInput": true, "lowImpedance": true,
                            "builtInDefaults": true, "xlrInputs": 2, "hpOutputs": 2 },
          "state": { "gainDb": 52, "mute": false, "lowCut": true, "expander": false, "voiceTune": true,
                     "voiceTuneStrength": 50, "gain2Db": 30, "mute2": true, "phantom": false,
                     "hpVolumeDb": 0, "crossfade": 200, "auxLevelDb": -6, "auxLevelLock": false,
                     "outHp1": false, "outUsbAux": true, "gainLocked": false },
          "mixer": {
            "mixes": [
              { "id": "monitor", "name": "Monitor A", "volume": 1, "muted": false, "kind": "monitor" },
              { "id": "stream", "name": "Stream", "volume": 0.8, "muted": false, "kind": "virtualMic" }
            ],
            "channels": [
              { "id": "xlr1", "name": "XLR 1", "levels": { "monitor": 1, "stream": 0.5 },
                "mutedIn": ["monitor"], "hardware": true },
              { "id": "music", "name": "Music", "levels": { "monitor": 0.8, "stream": 1 },
                "mutedIn": [], "hardware": false }
            ],
            "monitorOutputs": ["alsa_output.arctis"],
            "monitorFeeds": { "alsa_output.arctis": "monitor" },
            "outputVolume": 0.5,
            "auxPortEnabled": true,
            "lowCutHz": 80,
            "softClipGuard": true,
            "softClipGuardAvailable": true,
            "enforcedDefaultSink": "OpenXLR_ch_system",
            "enforcedDefaultSource": "OpenXLR_chat",
            "streams": [
              { "id": 669, "label": "Spotify", "identity": "spotify", "channelId": "music",
                "active": true, "running": true }
            ],
            "inserts": {
              "xlr1": [
                { "insert": { "id": "abc", "kind": "vst3", "plugin": "PLUG", "label": "Elgato EQ",
                              "bypass": false, "nativeHost": true, "params": { "12": 0.25 } },
                  "error": null }
              ]
            },
            "layoutWarning": null
          },
          "devices": [
            { "name": "alsa_output.arctis", "description": "Arctis Nova Elite", "kind": 0, "isOwn": false,
              "isPhysical": true, "volume": 0.5, "muted": false },
            { "name": "alsa_output.katana", "description": "Katana", "kind": 0, "isOwn": false,
              "isPhysical": true, "volume": 0.52, "muted": false }
          ],
          "profiles": ["default"],
          "activeProfile": "default",
          "recallOnConnect": "default",
          "detected": [ { "usbId": "0fd9:00b4", "name": "Elgato Wave XLR Pro", "active": true } ]
        }
        """;

    private static (App App, List<string> Sent) Ready(int tab = 0)
    {
        DaemonLink link = new();
        List<string> sent = [];
        link.Sent += json => sent.Add(json);
        link.Receive(StateJson);
        App app = new(link, Theme.Material);
        app.ShowTab(tab);
        // A frame settles the selections the way a running terminal would.
        app.Draw(new Screen(140, 36));
        return (app, sent);
    }

    private static JsonElement Command(List<string> sent)
    {
        string last = Assert.Single(sent);
        return JsonDocument.Parse(last).RootElement;
    }

    private static string Text(JsonElement command, string property) =>
        command.GetProperty(property).GetString() ?? string.Empty;

    // --- the state ---

    [Fact]
    public void AStateMessageIsReadIntoTheThingsTheTabsDraw()
    {
        Snapshot? state = Snapshot.Parse(StateJson);

        Assert.NotNull(state);
        Assert.True(state.Connected);
        Assert.Equal("Wave XLR Pro", state.Device?.Model);
        Assert.Equal(2, state.Count("xlrInputs"));
        Assert.True(state.Can("phantom"));
        Assert.Equal(52, state.Number("gainDb"));
        Assert.True(state.Flag("lowCut"));

        MixEntry monitor = state.Mixer.Mixes[0];
        Assert.Equal("Monitor A", monitor.Name);
        Assert.Equal(1.5, monitor.Ceiling);          // a monitor mix goes above unity
        Assert.Equal(1.0, state.Mixer.Mixes[1].Ceiling);

        ChannelEntry xlr1 = state.Mixer.Channels[0];
        Assert.Equal(0.5, xlr1.Level("stream"));
        Assert.True(xlr1.IsMuted("monitor"));
        Assert.False(xlr1.IsMuted("stream"));
        Assert.True(xlr1.Hardware);

        Assert.Equal(0.25, state.Mixer.Inserts["xlr1"][0].Insert.Params["12"]);
        Assert.Equal("spotify", state.Mixer.Streams[0].Identity);
    }

    [Fact]
    public void AMessageThatIsNotAStateLeavesTheLastOneAlone()
    {
        DaemonLink link = new();
        link.Receive(StateJson);
        link.Receive("""{"type":"meters","levels":{"ch:xlr1":[0.4,0.6],"mix:monitor":[0.1,0.1]}}""");
        link.Receive("not json at all");

        Assert.NotNull(link.State);
        Assert.Equal(0.6, link.Meter("ch", "xlr1"));
        Assert.Equal(0, link.Meter("ch", "music"));
    }

    [Fact]
    public void AnErrorFromTheDaemonIsHeldForTheBottomLine()
    {
        DaemonLink link = new();
        link.Receive("""{"type":"error","message":"unknown cmd 'setOutputRoute'"}""");
        Assert.Equal("unknown cmd 'setOutputRoute'", link.LastError);

        link.ClearError();
        Assert.Null(link.LastError);
    }

    // --- the tabs ---

    [Fact]
    public void EveryTabDrawsWithAStateAndWithoutOne()
    {
        (App app, _) = Ready();
        Screen screen = new(140, 36);
        for (int tab = 0; tab < app.Views.Count; tab++)
        {
            app.ShowTab(tab);
            app.Draw(screen);
        }

        App empty = new(new DaemonLink(), Theme.Material);
        for (int tab = 0; tab < empty.Views.Count; tab++)
        {
            empty.ShowTab(tab);
            empty.Draw(screen);
        }
    }

    [Fact]
    public void TheNumberKeysAndTabWalkTheTabs()
    {
        (App app, _) = Ready();
        Assert.Equal("Mixer", app.Current.Title);

        app.Handle(new KeyPress(Key.Char, '3'));
        Assert.Equal("Outputs", app.Current.Title);

        app.Handle(new KeyPress(Key.Tab));
        Assert.Equal("Apps", app.Current.Title);

        app.Handle(new KeyPress(Key.BackTab));
        Assert.Equal("Outputs", app.Current.Title);
    }

    [Fact]
    public void QuitIsQAndControlC()
    {
        (App app, _) = Ready();
        app.Handle(new KeyPress(Key.Char, 'q'));
        Assert.False(app.Running);

        (App other, _) = Ready();
        other.Handle(new KeyPress(Key.Char, 'c', Ctrl: true));
        Assert.False(other.Running);
    }

    // --- the mixer grid ---

    [Fact]
    public void SpaceOnAMixMasterMutesTheMix()
    {
        (App app, List<string> sent) = Ready();
        app.Handle(new KeyPress(Key.Char, ' '));

        JsonElement command = Command(sent);
        Assert.Equal("setMixMuted", Text(command, "cmd"));
        Assert.Equal("monitor", Text(command, "mix"));
        Assert.True(command.GetProperty("value").GetBoolean());
    }

    [Fact]
    public void SpaceOnASendMutesThatOneSend()
    {
        (App app, List<string> sent) = Ready();
        app.Handle(new KeyPress(Key.Down));      // XLR 1
        app.Handle(new KeyPress(Key.Right));     // the Stream column
        app.Handle(new KeyPress(Key.Char, ' '));

        JsonElement command = Command(sent);
        Assert.Equal("setChannelMuted", Text(command, "cmd"));
        Assert.Equal("xlr1", Text(command, "channel"));
        Assert.Equal("stream", Text(command, "mix"));
        Assert.True(command.GetProperty("value").GetBoolean());
    }

    [Fact]
    public void AMutedSendIsUnmutedByTheSameKey()
    {
        (App app, List<string> sent) = Ready();
        app.Handle(new KeyPress(Key.Down));      // XLR 1, muted in Monitor A
        app.Handle(new KeyPress(Key.Char, ' '));

        Assert.False(Command(sent).GetProperty("value").GetBoolean());
    }

    [Fact]
    public void TheLevelKeysMoveOneSendWithinItsRange()
    {
        (App app, List<string> sent) = Ready();
        app.Handle(new KeyPress(Key.Down));
        app.Handle(new KeyPress(Key.Right));     // XLR 1 into Stream, at 0.5
        app.Handle(new KeyPress(Key.Char, '+'));

        JsonElement command = Command(sent);
        Assert.Equal("setLevel", Text(command, "cmd"));
        Assert.Equal("xlr1", Text(command, "channel"));
        Assert.Equal(0.55, command.GetProperty("value").GetDouble(), 3);
    }

    [Fact]
    public void TheFineKeysMoveASendByOnePoint()
    {
        (App app, List<string> sent) = Ready();
        app.Handle(new KeyPress(Key.Down));
        app.Handle(new KeyPress(Key.Right));     // XLR 1 into Stream, at 0.5
        app.Handle(new KeyPress(Key.Char, ']'));
        Assert.Equal(0.51, JsonDocument.Parse(sent[0]).RootElement.GetProperty("value").GetDouble(), 3);

        app.Handle(new KeyPress(Key.Char, '['));
        Assert.Equal(0.49, JsonDocument.Parse(sent[1]).RootElement.GetProperty("value").GetDouble(), 3);
    }

    [Fact]
    public void ANumberKeyIsATabAndNotALevel()
    {
        (App app, List<string> sent) = Ready();
        app.Handle(new KeyPress(Key.Char, '2'));

        Assert.Equal("Inputs", app.Current.Title);
        Assert.Empty(sent);
    }

    [Fact]
    public void AMonitorMixTakesItsVolumeAboveUnityAndTheOthersDoNot()
    {
        // A monitor mix is at unity here and goes up; the virtual microphone
        // beside it is already at its ceiling and stays there.
        DaemonLink link = new();
        List<string> sent = [];
        link.Sent += json => sent.Add(json);
        link.Receive(StateJson.Replace(
            "\"id\": \"stream\", \"name\": \"Stream\", \"volume\": 0.8",
            "\"id\": \"stream\", \"name\": \"Stream\", \"volume\": 1",
            StringComparison.Ordinal));
        App app = new(link, Theme.Material);
        app.Draw(new Screen(140, 36));

        app.Handle(new KeyPress(Key.Char, '+'));
        Assert.Equal(1.05, JsonDocument.Parse(sent[0]).RootElement.GetProperty("value").GetDouble(), 3);

        app.Handle(new KeyPress(Key.Right));     // the Stream master
        app.Handle(new KeyPress(Key.Char, '+'));
        Assert.Equal(1.0, JsonDocument.Parse(sent[1]).RootElement.GetProperty("value").GetDouble(), 3);
    }

    [Fact]
    public void AHardwareChannelCannotBeRenamedOrRemoved()
    {
        (App app, List<string> sent) = Ready();
        app.Handle(new KeyPress(Key.Down));      // XLR 1
        app.Handle(new KeyPress(Key.Char, 'r'));
        app.Handle(new KeyPress(Key.Char, 'd'));

        Assert.Empty(sent);
        Assert.False(app.Prompting);
    }

    [Fact]
    public void RenamingAnApplicationChannelAsksForTheNameFirst()
    {
        (App app, List<string> sent) = Ready();
        app.Handle(new KeyPress(Key.Down));
        app.Handle(new KeyPress(Key.Down));      // Music
        app.Handle(new KeyPress(Key.Char, 'r'));

        Assert.True(app.Prompting);
        Assert.Empty(sent);

        foreach (char letter in "Games") app.Handle(new KeyPress(Key.Char, letter));
        app.Handle(new KeyPress(Key.Enter));

        JsonElement command = Command(sent);
        Assert.Equal("renameChannel", Text(command, "cmd"));
        Assert.Equal("music", Text(command, "channel"));
        Assert.Equal("MusicGames", Text(command, "name"));
    }

    [Fact]
    public void EscapeLeavesAPromptWithoutSendingAnything()
    {
        (App app, List<string> sent) = Ready();
        app.Handle(new KeyPress(Key.Down));
        app.Handle(new KeyPress(Key.Down));
        app.Handle(new KeyPress(Key.Char, 'r'));
        app.Handle(new KeyPress(Key.Escape));

        Assert.False(app.Prompting);
        Assert.Empty(sent);
    }

    [Fact]
    public void DeletingAChannelNeedsTheWordYes()
    {
        (App app, List<string> sent) = Ready();
        app.Handle(new KeyPress(Key.Down));
        app.Handle(new KeyPress(Key.Down));      // Music
        app.Handle(new KeyPress(Key.Char, 'd'));
        Assert.True(app.Prompting);

        foreach (char letter in "no") app.Handle(new KeyPress(Key.Char, letter));
        app.Handle(new KeyPress(Key.Enter));
        Assert.Empty(sent);

        app.Handle(new KeyPress(Key.Char, 'd'));
        foreach (char letter in "yes") app.Handle(new KeyPress(Key.Char, letter));
        app.Handle(new KeyPress(Key.Enter));

        JsonElement command = Command(sent);
        Assert.Equal("deleteChannel", Text(command, "cmd"));
        Assert.Equal("music", Text(command, "channel"));
    }

    // --- the hardware ---

    [Fact]
    public void SpaceOnAHardwareToggleSetsThatControl()
    {
        (App app, List<string> sent) = Ready(tab: 1);
        app.Handle(new KeyPress(Key.Down));      // Gain is selected already, so this is Mute
        app.Handle(new KeyPress(Key.Char, ' '));

        JsonElement command = Command(sent);
        Assert.Equal("set", Text(command, "cmd"));
        Assert.Equal("mute", Text(command, "control"));
        Assert.True(command.GetProperty("value").GetBoolean());
    }

    [Fact]
    public void TheGainMovesInWholeDecibels()
    {
        (App app, List<string> sent) = Ready(tab: 1);
        app.Handle(new KeyPress(Key.Right));     // the gain, which a drawn frame starts on

        JsonElement command = Command(sent);
        Assert.Equal("set", Text(command, "cmd"));
        Assert.Equal("gain", Text(command, "control"));
        Assert.Equal(53, command.GetProperty("value").GetInt32());
    }

    [Fact]
    public void TheSecondInputSetsItsOwnControls()
    {
        (App app, List<string> sent) = Ready(tab: 1);
        // Down from XLR 1's gain: mute, low cut, expander, voice tune,
        // strength, phantom, clip guard, compressor, then XLR 2's gain.
        for (int press = 0; press < 9; press++) app.Handle(new KeyPress(Key.Down));
        app.Handle(new KeyPress(Key.Left));

        JsonElement command = Command(sent);
        Assert.Equal("gain2", Text(command, "control"));
        Assert.Equal(29, command.GetProperty("value").GetInt32());
    }

    [Fact]
    public void TheSoftwareLowCutIsOneOfThreeValues()
    {
        (App app, List<string> sent) = Ready(tab: 1);
        for (int press = 0; press < 20; press++) app.Handle(new KeyPress(Key.Down));
        while (sent.Count == 0 && app.Current.Title == "Inputs")
        {
            app.Handle(new KeyPress(Key.Right));
            if (sent.Count > 0) break;
            app.Handle(new KeyPress(Key.Down));
        }

        Assert.NotEmpty(sent);
    }

    // --- the outputs ---

    [Fact]
    public void SpaceSelectsASinkForTheMonitorMixes()
    {
        (App app, List<string> sent) = Ready(tab: 2);
        app.Handle(new KeyPress(Key.Down));      // the Arctis, already selected
        app.Handle(new KeyPress(Key.Down));      // the Katana
        app.Handle(new KeyPress(Key.Char, ' '));

        JsonElement command = Command(sent);
        Assert.Equal("setMonitorOutputs", Text(command, "cmd"));
        string[] devices = [.. command.GetProperty("devices").EnumerateArray().Select(item => item.GetString() ?? "")];
        Assert.Equal(["alsa_output.arctis", "alsa_output.katana"], devices);
    }

    [Fact]
    public void TheArrowsWalkWhatFeedsASelectedOutput()
    {
        (App app, List<string> sent) = Ready(tab: 2);
        app.Handle(new KeyPress(Key.Down));      // the Arctis, fed by Monitor A
        app.Handle(new KeyPress(Key.Right));

        JsonElement command = Command(sent);
        Assert.Equal("setMonitorFeed", Text(command, "cmd"));
        Assert.Equal("alsa_output.arctis", Text(command, "device"));
        Assert.Equal("stream", Text(command, "mix"));
    }

    [Fact]
    public void TheOutputVolumeIsTheOneTheSelectedSinksShare()
    {
        (App app, List<string> sent) = Ready(tab: 2);
        app.Handle(new KeyPress(Key.Right));     // the output volume, at 0.5

        JsonElement command = Command(sent);
        Assert.Equal("setOutputVolume", Text(command, "cmd"));
        Assert.Equal(0.55, command.GetProperty("value").GetDouble(), 3);
    }

    // --- the applications ---

    [Fact]
    public void AnApplicationIsMovedToTheNextChannelAndRemembered()
    {
        (App app, List<string> sent) = Ready(tab: 3);
        app.Handle(new KeyPress(Key.Char, 'i')); // Spotify, on Music

        JsonElement command = Command(sent);
        Assert.Equal("assignApp", Text(command, "cmd"));
        Assert.Equal("spotify", Text(command, "identity"));
        Assert.Equal("ignore", Text(command, "channel"));
    }

    // --- the inserts ---

    [Fact]
    public void SpaceBypassesOnePlugin()
    {
        (App app, List<string> sent) = Ready(tab: 4);
        app.Handle(new KeyPress(Key.Down));      // past the chain chooser, onto Elgato EQ
        app.Handle(new KeyPress(Key.Char, ' '));

        JsonElement command = Command(sent);
        Assert.Equal("setInsertBypass", Text(command, "cmd"));
        Assert.Equal("xlr1", Text(command, "channel"));
        Assert.Equal("abc", Text(command, "insertId"));
        Assert.True(command.GetProperty("value").GetBoolean());
    }

    [Fact]
    public void RemovingAPluginKeepsTheControlValuesOfTheOnesThatStay()
    {
        DaemonLink link = new();
        List<string> sent = [];
        link.Sent += json => sent.Add(json);
        const string chain = "\"xlr1\": [";
        const string withKeeper = chain +
            "{ \"insert\": { \"id\": \"keepme\", \"kind\": \"lv2\", \"plugin\": \"URI\"," +
            " \"label\": \"Keep\", \"bypass\": true, \"params\": { \"gain\": 0.75 } }," +
            " \"error\": null },";
        link.Receive(StateJson.Replace(chain, withKeeper, StringComparison.Ordinal));
        App app = new(link, Theme.Material);
        app.ShowTab(4);
        app.Draw(new Screen(140, 36));

        app.Handle(new KeyPress(Key.Down));      // Keep
        app.Handle(new KeyPress(Key.Down));      // Elgato EQ
        app.Handle(new KeyPress(Key.Char, 'd'));
        foreach (char letter in "yes") app.Handle(new KeyPress(Key.Char, letter));
        app.Handle(new KeyPress(Key.Enter));

        JsonElement command = Command(sent);
        Assert.Equal("setInserts", Text(command, "cmd"));
        JsonElement inserts = command.GetProperty("inserts");
        JsonElement kept = Assert.Single(inserts.EnumerateArray());
        Assert.Equal("keepme", kept.GetProperty("id").GetString());
        Assert.Equal(0.75, kept.GetProperty("params").GetProperty("gain").GetDouble());
        Assert.True(kept.GetProperty("bypass").GetBoolean());
    }

    // --- the profiles ---

    [Fact]
    public void EnterLoadsAProfileAndRAsksForItOnConnect()
    {
        (App app, List<string> sent) = Ready(tab: 5);
        app.Handle(new KeyPress(Key.Enter));     // the one saved profile

        Assert.Equal("loadProfile", Text(Command(sent), "cmd"));

        sent.Clear();
        app.Handle(new KeyPress(Key.Char, 'r'));
        JsonElement recall = Command(sent);
        Assert.Equal("setRecallOnConnect", Text(recall, "cmd"));
        Assert.Equal("default", Text(recall, "name"));
    }

    [Fact]
    public void ResettingTheDeviceNeedsTheWordYes()
    {
        (App app, List<string> sent) = Ready(tab: 5);
        for (int press = 0; press < 12; press++) app.Handle(new KeyPress(Key.Down));
        app.Handle(new KeyPress(Key.Enter));

        if (app.Prompting)
        {
            foreach (char letter in "yes") app.Handle(new KeyPress(Key.Char, letter));
            app.Handle(new KeyPress(Key.Enter));
            Assert.Contains(sent, json => json.Contains("resetDevice", StringComparison.Ordinal));
        }
    }
}
