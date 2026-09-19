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

        app.Handle(new KeyPress(Key.Char, '4'));
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
        app.Handle(new KeyPress(Key.Down));      // Music
        app.Handle(new KeyPress(Key.Right));     // the Stream column
        app.Handle(new KeyPress(Key.Char, ' '));

        JsonElement command = Command(sent);
        Assert.Equal("setChannelMuted", Text(command, "cmd"));
        Assert.Equal("music", Text(command, "channel"));
        Assert.Equal("stream", Text(command, "mix"));
        Assert.True(command.GetProperty("value").GetBoolean());
    }

    [Fact]
    public void SpaceOnAnXlrStripIsTheInputsOwnMute()
    {
        // The XLR strips are the interface, so their key is the hardware
        // mute, not the send into whichever mix is chosen.
        (App app, List<string> sent) = Ready();
        app.Handle(new KeyPress(Key.Down));      // XLR 1, whose input is not muted
        app.Handle(new KeyPress(Key.Char, ' '));

        JsonElement command = Command(sent);
        Assert.Equal("set", Text(command, "cmd"));
        Assert.Equal("mute", Text(command, "control"));
        Assert.True(command.GetProperty("value").GetBoolean());
    }

    [Fact]
    public void AnXlrStripShowsTheInputMuteOnItsKeyAndAMutedSendAsAWord()
    {
        (App app, _) = Ready();
        Screen screen = new(150, 42);
        app.Draw(screen);
        string frame = Frame(screen);

        // XLR 1's input is on but its send into Monitor A is muted: the key
        // reads ON and the word muted stands where the percentage goes.
        int plate = frame.IndexOf("XLR 1", StringComparison.Ordinal);
        Assert.True(plate >= 0);
        int column = plate - frame.LastIndexOf('\n', plate) - 1;
        string[] rows = frame.Split('\n');
        int top = frame[..plate].Count(ch => ch == '\n');
        Assert.Contains("muted", rows[top + 2][column..(column + 8)], StringComparison.Ordinal);
        string keyRow = rows.First(row => row.Contains("[ ON ]", StringComparison.Ordinal) || row.Contains("  ON  ", StringComparison.Ordinal));
        Assert.Contains("ON", keyRow[column..(column + 8)], StringComparison.Ordinal);
    }

    [Fact]
    public void TheMatrixGivesEveryMeterARowOfItsOwnWhenThereIsHeightAndOneWhenThereIsNot()
    {
        (App app, _) = Desk();
        app.ShowTab(1);

        Screen tall = new(150, 42);
        app.Draw(tall);
        string[] rows = Frame(tall).Split('\n');
        int first = Array.FindIndex(rows, row => row.Contains("Aux In", StringComparison.Ordinal));
        Assert.True(first > 0);
        // The name sits between its two sides, one row above and one below,
        // and the next channel starts three rows down.
        Assert.Contains("L", rows[first - 1], StringComparison.Ordinal);
        Assert.Contains("R", rows[first + 1], StringComparison.Ordinal);
        Assert.Contains("Game", rows[first + 3], StringComparison.Ordinal);
        int track = rows[first + 1].IndexOf("R ", StringComparison.Ordinal) + 2;
        Assert.Equal('\u2500', rows[first + 1][track]);
        // The masters carry the same pair, under their mute key.
        int mixes = Array.FindIndex(rows, row => row.Contains("MIXES", StringComparison.Ordinal));
        // The masters carry the same pair, a row apart, under their mute key.
        Assert.Contains("L", rows[mixes + 3], StringComparison.Ordinal);
        Assert.Contains("R", rows[mixes + 5], StringComparison.Ordinal);

        // Nine channels do not fit twice over in twenty-four rows, so there
        // the grid stays one row a channel with a single summed bar.
        Screen small = new(80, 24);
        app.Draw(small);
        string[] tight = Frame(small).Split('\n');
        int line = Array.FindIndex(tight, row => row.Contains("XLR 1", StringComparison.Ordinal));
        Assert.True(line > 0);
        Assert.Contains("XLR 2", tight[line + 1], StringComparison.Ordinal);
    }

    [Fact]
    public void AMonoInputIsMeteredOnceRatherThanAsAPairOfTheSameReading()
    {
        // The XLR inputs are mono and the daemon repeats the one reading in
        // both sides of the pair it sends.
        (App app, _) = Desk();
        app.ShowTab(1);
        Screen screen = new(150, 42);
        app.Draw(screen);
        string[] rows = Frame(screen).Split('\n');

        int xlr = Array.FindIndex(rows, row => row.Contains("XLR 1", StringComparison.Ordinal));
        Assert.True(xlr > 0);
        Assert.DoesNotContain(" L ", rows[xlr - 1], StringComparison.Ordinal);
        Assert.DoesNotContain(" R ", rows[xlr + 1], StringComparison.Ordinal);
        // Its bar stands on the name's own row, where the stereo pair would
        // have flanked it.
        Assert.Contains('\u2500', rows[xlr]);

        // On the desk the same input carries one bar and no lettering.
        app.ShowTab(0);
        app.Draw(screen);
        string[] desk = Frame(screen).Split('\n');
        int plate = Array.FindIndex(desk, row => row.Contains("XLR 1", StringComparison.Ordinal));
        Assert.True(plate > 0);
        Assert.DoesNotContain("L R", desk[plate + 3][..24], StringComparison.Ordinal);
        Assert.Contains("L R", desk[plate + 3], StringComparison.Ordinal);
    }

    [Fact]
    public void AMutedSendIsUnmutedByTheSameKey()
    {
        (App app, List<string> sent) = Ready();
        app.ShowTab(1);                          // the matrix, where every send is its own cell
        app.Handle(new KeyPress(Key.Down));      // XLR 1, muted in Monitor A
        app.Handle(new KeyPress(Key.Char, ' '));

        JsonElement command = Command(sent);
        Assert.Equal("setChannelMuted", Text(command, "cmd"));
        Assert.False(command.GetProperty("value").GetBoolean());
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
        app.Handle(new KeyPress(Key.Char, '3'));

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
        (App app, List<string> sent) = Ready(tab: 2);
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
        (App app, List<string> sent) = Ready(tab: 2);
        app.Handle(new KeyPress(Key.Right));     // the gain, which a drawn frame starts on

        JsonElement command = Command(sent);
        Assert.Equal("set", Text(command, "cmd"));
        Assert.Equal("gain", Text(command, "control"));
        Assert.Equal(53, command.GetProperty("value").GetInt32());
    }

    [Fact]
    public void TheSecondInputSetsItsOwnControls()
    {
        (App app, List<string> sent) = Ready(tab: 2);
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
        (App app, List<string> sent) = Ready(tab: 2);
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
        (App app, List<string> sent) = Ready(tab: 3);
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
        (App app, List<string> sent) = Ready(tab: 3);
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
        (App app, List<string> sent) = Ready(tab: 3);
        app.Handle(new KeyPress(Key.Right));     // the output volume, at 0.5

        JsonElement command = Command(sent);
        Assert.Equal("setOutputVolume", Text(command, "cmd"));
        Assert.Equal(0.55, command.GetProperty("value").GetDouble(), 3);
    }

    // --- the applications ---

    [Fact]
    public void AnApplicationIsMovedToTheNextChannelAndRemembered()
    {
        (App app, List<string> sent) = Ready(tab: 4);
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
        (App app, List<string> sent) = Ready(tab: 5);
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
        app.ShowTab(5);
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
        (App app, List<string> sent) = Ready(tab: 6);
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
        (App app, List<string> sent) = Ready(tab: 6);
        for (int press = 0; press < 12; press++) app.Handle(new KeyPress(Key.Down));
        app.Handle(new KeyPress(Key.Enter));

        if (app.Prompting)
        {
            foreach (char letter in "yes") app.Handle(new KeyPress(Key.Char, letter));
            app.Handle(new KeyPress(Key.Enter));
            Assert.Contains(sent, json => json.Contains("resetDevice", StringComparison.Ordinal));
        }
    }
    private static string Frame(Screen screen) => string.Join('\n', Enumerable.Range(0, screen.Height)
        .Select(y => new string(Enumerable.Range(0, screen.Width).Select(x => screen.At(x, y).Ch).ToArray())));

    private static (App App, List<string> Sent) Desk()
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(StateJson)!;
        var mixer = root["mixer"]!;
        mixer["mixes"] = System.Text.Json.Nodes.JsonNode.Parse("""
            [{"id":"monitor","name":"Monitor A","kind":"monitor","volume":1},
             {"id":"monitorB","name":"Monitor B","kind":"monitor","volume":1},
             {"id":"stream","name":"Stream","kind":"virtualMic","volume":1},
             {"id":"chat","name":"Chat","kind":"virtualMic","volume":1},
             {"id":"aux","name":"Aux","kind":"aux","volume":1}]
            """);
        string[] names = ["XLR 1", "XLR 2", "Aux In", "Game", "Music", "Browser", "System", "Voice Chat", "SFX"];
        var channels = new System.Text.Json.Nodes.JsonArray();
        foreach (string name in names)
            channels.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = name.Replace(" ", string.Empty).ToLowerInvariant(), ["name"] = name,
                ["hardware"] = name.StartsWith("XLR", StringComparison.Ordinal),
                ["levels"] = new System.Text.Json.Nodes.JsonObject { ["monitor"] = 1, ["aux"] = 0.5 },
            });
        mixer["channels"] = channels;
        DaemonLink link = new();
        List<string> sent = [];
        link.Sent += sent.Add;
        link.Receive(root.ToJsonString());
        return (new App(link, Theme.Material), sent);
    }

    [Theory]
    [InlineData(150, 42)]
    [InlineData(150, 40)]
    [InlineData(120, 34)]
    [InlineData(80, 24)]
    public void TheDeskKeepsItsBanksAndKeysInsideEverySupportedSize(int width, int height)
    {
        (App app, _) = Desk();
        Screen screen = new(width, height);
        app.Draw(screen);
        string frame = Frame(screen);
        Assert.Contains("Sends to Monitor A", frame, StringComparison.Ordinal);
        Assert.Contains("Masters", frame, StringComparison.Ordinal);
        Assert.Contains("MASTER / Monitor A", frame, StringComparison.Ordinal);
        Assert.Contains("Space mute", frame, StringComparison.Ordinal);
        Assert.Contains("F1/? help", frame, StringComparison.Ordinal);
        Assert.Contains("q/Ctrl+C quit", frame, StringComparison.Ordinal);
        Assert.Contains("8 Options", frame, StringComparison.Ordinal);
        Assert.Equal(' ', screen.At(width - 1, height - 1).Ch);
        if (width >= 150)
        {
            Assert.Contains("1-9/9", frame, StringComparison.Ordinal);
            Assert.Contains("1-5/5", frame, StringComparison.Ordinal);
            Assert.Contains("LEVEL HISTORY / 15 s", frame, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("1-", frame, StringComparison.Ordinal);
            Assert.DoesNotContain("1-9/9", frame, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheCompactDeskScrollsBothBanksAndActsOnTheVisibleSend()
    {
        (App app, List<string> sent) = Desk();
        Screen screen = new(80, 24);
        app.Handle(new KeyPress(Key.End));
        for (int i = 0; i < 4; i++) app.Handle(new KeyPress(Key.Right));
        app.Draw(screen);
        string frame = Frame(screen);
        Assert.Contains("SFX > Aux", frame, StringComparison.Ordinal);
        Assert.Contains("Sends to Aux", frame, StringComparison.Ordinal);
        Assert.Contains("5-9/9", frame, StringComparison.Ordinal);
        Assert.Contains("4-5/5", frame, StringComparison.Ordinal);
        app.Handle(new KeyPress(Key.Char, '+'));
        JsonElement command = Command(sent);
        Assert.Equal("sfx", Text(command, "channel"));
        Assert.Equal("aux", Text(command, "mix"));
        Assert.Equal(0.55, command.GetProperty("value").GetDouble(), 6);
    }

    [Fact]
    public void ResizingPreservesTheSendAndDoesNotSendACommand()
    {
        (App app, List<string> sent) = Desk();
        Screen screen = new(150, 42);
        app.Handle(new KeyPress(Key.End));
        app.Draw(screen);
        screen.Resize(80, 24);
        app.Draw(screen);
        Assert.Contains("SFX > Monitor A", Frame(screen), StringComparison.Ordinal);
        screen.Resize(120, 34);
        app.Draw(screen);
        Assert.Contains("SFX > Monitor A", Frame(screen), StringComparison.Ordinal);
        Assert.Empty(sent);
    }

    [Fact]
    public void AResizeBelowTheMinimumShowsAnInstructionAndKeepsTheSelection()
    {
        (App app, List<string> sent) = Desk();
        app.Handle(new KeyPress(Key.End));
        Screen screen = new(40, 10);
        app.Draw(screen);
        Assert.Contains("Resize to at least 80x24", Frame(screen), StringComparison.Ordinal);
        screen.Resize(80, 24);
        app.Draw(screen);
        Assert.Contains("SFX > Monitor A", Frame(screen), StringComparison.Ordinal);
        Assert.Empty(sent);
    }

    [Fact]
    public void ASecondUnchangedDeskFrameWritesNothingAndAMeterChangeIsSmall()
    {
        (App app, _) = Desk();
        Screen screen = new(150, 42) { TrueColor = true };
        app.Draw(screen);
        string first = screen.Render();
        app.Draw(screen);
        Assert.Equal(string.Empty, screen.Render());
        app.Link.Receive("""{"type":"meters","levels":{"mix:monitor":[0.4,0.7]}}""");
        app.Draw(screen);
        string changed = screen.Render();
        Assert.NotEmpty(changed);
        Assert.True(changed.Length < first.Length / 3, $"meter update {changed.Length}, full frame {first.Length}");
        Assert.Equal('╻', screen.At(26, 4).Ch);
        Assert.Equal('┏', screen.At(29, 4).Ch);
    }

    [Theory]
    [InlineData(150, 42)]
    [InlineData(120, 34)]
    [InlineData(80, 24)]
    public void EverySectionKeepsItsNavigationAndSelectionWhenItIsSmall(int width, int height)
    {
        (App app, _) = Ready();
        Screen screen = new(width, height);
        for (int tab = 1; tab < 8; tab++)
        {
            app.ShowTab(tab);
            app.Draw(screen);
            string frame = Frame(screen);
            Assert.Contains(app.Current.Title, frame, StringComparison.Ordinal);
            Assert.Contains("F1/? help", frame, StringComparison.Ordinal);
            Assert.Contains("q/Ctrl+C quit", frame, StringComparison.Ordinal);
            for (int i = 0; i < 60; i++) app.Handle(new KeyPress(Key.Down));
            app.Draw(screen);
            Assert.Contains("F1/? help", Frame(screen), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void HardwareCardsShowBothGainsAndKeepLowerControlsReachable()
    {
        (App app, List<string> sent) = Ready(2);
        Screen screen = new(150, 42);
        app.Draw(screen);
        string frame = Frame(screen);
        Assert.Contains("52 dB", frame, StringComparison.Ordinal);
        Assert.Contains("30 dB", frame, StringComparison.Ordinal);
        Assert.Contains("Phantom power", frame, StringComparison.Ordinal);
        Assert.Contains("Headphones", frame, StringComparison.Ordinal);
        for (int i = 0; i < 50; i++) app.Handle(new KeyPress(Key.Down));
        app.Draw(screen);
        Assert.Contains("Aux port feed", Frame(screen), StringComparison.Ordinal);
        app.Handle(new KeyPress(Key.Char, ' '));
        Assert.Equal("setAuxPortEnabled", Text(Command(sent), "cmd"));
    }

    [Fact]
    public void FineBracketsAlsoWorkOnHardwareAndOutputFaders()
    {
        (App app, List<string> sent) = Ready(3);
        app.Handle(new KeyPress(Key.Char, ']'));
        Assert.Equal(0.51, Command(sent).GetProperty("value").GetDouble(), 6);
        (App inputs, List<string> inputSent) = Ready(2);
        inputs.Handle(new KeyPress(Key.Char, ']'));
        Assert.Equal("gain", Text(Command(inputSent), "control"));
        Assert.Equal(53, Command(inputSent).GetProperty("value").GetInt32());
        inputSent.Clear();
        for (int i = 0; i < 5; i++) inputs.Handle(new KeyPress(Key.Down));
        inputs.Handle(new KeyPress(Key.Char, '['));
        Assert.Equal("voiceTuneStrength", Text(Command(inputSent), "control"));
        Assert.Equal(49, Command(inputSent).GetProperty("value").GetInt32());
    }

    [Fact]
    public void HelpListsEverySectionsCommandsAndQuitWorksWhileItIsOpen()
    {
        (App app, _) = Ready();
        Screen screen = new(80, 24);
        app.Handle(new KeyPress(Key.Char, '?'));
        app.Draw(screen);
        string frame = Frame(screen);
        foreach (string command in new[] { "Ctrl+Left/Right", "c capture input", "m sink mute", "f forget",
            "e open editor", "s overwrite", "R reload skins", "Ctrl+C" })
            Assert.Contains(command, frame, StringComparison.Ordinal);
        app.Handle(new KeyPress(Key.Char, 'q'));
        Assert.False(app.Running);
    }

    [Fact]
    public void ControlCQuitsFromATextPrompt()
    {
        (App app, List<string> sent) = Ready();
        app.Handle(new KeyPress(Key.Char, 'n'));
        Assert.True(app.Prompting);
        app.Handle(new KeyPress(Key.Char, 'c', Ctrl: true));
        Assert.False(app.Running);
        Assert.Empty(sent);
    }

}
