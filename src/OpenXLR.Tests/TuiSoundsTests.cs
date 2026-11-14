using System.Text.Json.Nodes;
using OpenXLR.Tui;

namespace OpenXLR.Tests;

/// <summary>
/// The terminal mixer's Sounds tab: what it draws from the state's
/// soundboard and the clip meters, and the commands its keys send. The link
/// is fed messages rather than a socket, so none of this needs a daemon.
/// </summary>
public sealed class TuiSoundsTests
{
    private const int SoundsTab = 8;

    private const string StateJson = """
        {
          "type": "state", "daemonVersion": "0.3.2", "connected": true,
          "capabilities": {}, "state": {},
          "mixer": {
            "mixes": [ { "id": "stream", "name": "Stream", "volume": 1, "muted": false, "kind": "virtualMic" } ],
            "channels": [
              { "id": "sfx", "name": "SFX", "levels": { "stream": 1 }, "mutedIn": [], "hardware": false, "volume": 0.6,
                "appearance": { "icon": "", "colour": null, "hidden": false, "defaultIcon": "burst" } }
            ],
            "inserts": {},
            "soundboard": {
              "channel": "sfx", "enabled": true, "available": true, "error": null,
              "formats": ["wav", "flac", "ogg"],
              "clips": [
                { "id": "airhorn", "name": "Airhorn", "seconds": 2.4, "gain": 0.8, "mode": "overlap", "loop": false,
                  "icon": "", "colour": null, "loaded": true, "status": "ready", "error": null, "playing": false },
                { "id": "drumroll", "name": "Drum roll", "seconds": 6.25, "gain": 1, "mode": "hold", "loop": true,
                  "icon": "", "colour": null, "loaded": true, "status": "ready", "error": null, "playing": false },
                { "id": "broken", "name": "Broken", "seconds": 1, "gain": 0.5, "mode": "toggle", "loop": false,
                  "icon": "", "colour": null, "loaded": false, "status": "error",
                  "error": "The player could not decode this clip.", "playing": false }
              ],
              "playing": []
            }
          },
          "devices": [], "profiles": []
        }
        """;

    private sealed class Session
    {
        public DaemonLink Link { get; } = new();
        public App App { get; }
        public List<JsonObject> Sent { get; } = [];
        public JsonNode State { get; } = JsonNode.Parse(StateJson)!;
        public Screen Screen { get; }
        public JsonNode Board => State["mixer"]!["soundboard"]!;
        public JsonNode Clip(int index) => Board["clips"]![index]!;
        public ManualTime Clock { get; } = new();

        public Session(Theme? theme = null, int width = 80, int height = 24)
        {
            Link.Sent += json => Sent.Add(JsonNode.Parse(json)!.AsObject());
            Echo();
            App = new(Link, theme ?? Theme.Material, time: Clock);
            App.ShowTab(SoundsTab);
            Screen = new(width, height);
            Draw();
        }

        public void Echo() => Link.Receive(State.ToJsonString());
        public void Key(Key key, bool ctrl = false) => App.Handle(new KeyPress(key, Ctrl: ctrl));
        public void Key(char key) => App.Handle(new KeyPress(OpenXLR.Tui.Key.Char, key));
        public void Type(string text) { foreach (char letter in text) Key(letter); }

        public string[] Draw()
        {
            App.Draw(Screen);
            return [.. Enumerable.Range(0, Screen.Height)
                .Select(y => new string(Enumerable.Range(0, Screen.Width).Select(x => Screen.At(x, y).Ch).ToArray()))];
        }

        public string Frame() => string.Join('\n', Draw());

        public JsonObject Command() => Assert.Single(Sent);

        public string[] Commands() => [.. Sent.Select(command => command["cmd"]!.GetValue<string>())];

        /// <summary>The daemon's answer to a command sent with a requestId.</summary>
        public void Reply(JsonObject command, string? error)
        {
            Link.Receive(new JsonObject
            {
                ["type"] = "commandResult", ["requestId"] = command["requestId"]!.GetValue<string>(), ["error"] = error,
            }.ToJsonString());
            Draw();
        }

        /// <summary>Waits past the key repeat window, as a person pressing again would.</summary>
        public void Later() => Clock.Advance(TimeSpan.FromSeconds(1));

        /// <summary>Presses the selected hold clip and has the daemon accept it.</summary>
        public void Hold()
        {
            Key(OpenXLR.Tui.Key.Enter);
            JsonObject press = Command();
            Assert.Equal("playClip", press["cmd"]!.GetValue<string>());
            Sent.Clear();
            Reply(press, null);
            Assert.Empty(Sent);
        }

        public void Playing(int index, bool playing)
        {
            Clip(index)["playing"] = playing;
            Echo();
            Draw();
        }

        /// <summary>The row a text sits on, and its column.</summary>
        public (int X, int Y) Find(string text)
        {
            string[] lines = Draw();
            for (int y = 0; y < lines.Length; y++)
                if (lines[y].IndexOf(text, StringComparison.Ordinal) is int x and >= 0) return (x, y);
            throw new Xunit.Sdk.XunitException($"No {text} on the screen:\n{string.Join('\n', lines)}");
        }
    }

    private static string Text(JsonObject command, string name) => command[name]!.GetValue<string>();

    private static Theme Latte() => Theme.FromJson(
        File.ReadAllText(Path.Combine(Root(), "src", "OpenXLR.UI", "Assets", "Skins", "catppuccin-latte", "skin.json")),
        "catppuccin-latte", "Catppuccin Latte");

    private static string Root()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docs", "skins.md")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory.FullName;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheTabDrawsEveryClipInsideAnEightyByTwentyFourTerminalInTheSkinsColours(bool light)
    {
        Theme theme = light ? Latte() : Theme.Material;
        Session session = new(theme);
        session.Clip(0)["playing"] = true;
        session.Board["playing"] = new JsonArray("airhorn");
        session.Echo();
        session.Link.Receive("""{"type":"meters","levels":{"clip:airhorn":[0.5,2]}}""");
        string frame = session.Frame();

        Assert.Contains("9 Snd", frame, StringComparison.Ordinal);
        Assert.Contains("8 Options", frame, StringComparison.Ordinal);
        Assert.Contains("Sounds", frame, StringComparison.Ordinal);
        Assert.Contains("Board on", frame, StringComparison.Ordinal);
        Assert.Contains("60%", frame, StringComparison.Ordinal);
        Assert.Contains("3 clips, 1 playing", frame, StringComparison.Ordinal);
        Assert.Contains("Plays wav, flac and ogg files", frame, StringComparison.Ordinal);
        Assert.Contains("Airhorn", frame, StringComparison.Ordinal);
        Assert.Contains("2.4 s", frame, StringComparison.Ordinal);
        Assert.Contains("6.3 s", frame, StringComparison.Ordinal);
        Assert.Contains("hold", frame, StringComparison.Ordinal);
        Assert.Contains("loop", frame, StringComparison.Ordinal);
        Assert.Contains("80%", frame, StringComparison.Ordinal);
        Assert.Contains("ready", frame, StringComparison.Ordinal);
        Assert.Contains("error: The player", frame, StringComparison.Ordinal);
        Assert.Contains("x2", frame, StringComparison.Ordinal);
        Assert.Contains("Enter play  s stop  S stop all  i import", frame, StringComparison.Ordinal);
        Assert.Contains("F1/? help", frame, StringComparison.Ordinal);
        Assert.Equal(' ', session.Screen.At(79, 23).Ch);

        // The selected row wears the skin's selection, the playing marker its
        // lamp, and the bar the meter's fill for half the room it has.
        (int nameX, int row) = session.Find("Airhorn");
        Assert.Equal(theme.Selection, session.Screen.At(nameX, row).Back);
        (int markX, int markRow) = session.Find("▶");
        Assert.Equal(row, markRow);
        Assert.Equal(theme.LedOn, session.Screen.At(markX, row).Fore);
        // A clip with no icon of its own wears SFX's default before its name.
        Assert.NotEqual(' ', session.Screen.At(nameX - 2, row).Ch);
        string line = session.Draw()[row];
        int start = line.IndexOf('█', StringComparison.Ordinal);
        int filled = line.Count(ch => ch == '█');
        int track = line.Count(ch => ch == '─');
        Assert.True(start > nameX);
        Assert.Equal(theme.MeterFill, session.Screen.At(start, row).Fore);
        Assert.InRange(filled, track - 1, track + 1);
        (int brokenX, int brokenRow) = session.Find("Broken");
        Assert.Equal(theme.Card, session.Screen.At(brokenX, brokenRow).Back);
    }

    [Fact]
    public void AWideTerminalNamesTheTabInFull()
    {
        Session session = new(width: 140, height: 36);
        Assert.Contains("9 Sounds", session.Frame(), StringComparison.Ordinal);
        Assert.Contains("8 Options", session.Frame(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheBoardsSentenceShowsWhenItCannotPlayAndTheClipsReasonWhenSelected()
    {
        Session session = new();
        session.Board["enabled"] = false;
        session.Board["error"] = "The soundboard is off. Turn it on first.";
        foreach (JsonNode? clip in session.Board["clips"]!.AsArray()) clip!["status"] = "off";
        session.Echo();
        string frame = session.Frame();
        Assert.Contains("Board off", frame, StringComparison.Ordinal);
        Assert.Contains("The soundboard is off. Turn it on first.", frame, StringComparison.Ordinal);
        Assert.Contains("off", frame, StringComparison.Ordinal);

        session.Key('b');
        Assert.Equal("setSoundboard", Text(session.Command(), "cmd"));
        Assert.True(session.Command()["enabled"]!.GetValue<bool>());

        session.Board["clips"]![2]!["status"] = "error";
        session.Echo();
        session.Key(Key.End);
        Assert.Contains("The player could not decode this clip.", session.Draw()[^3], StringComparison.Ordinal);
    }

    [Fact]
    public void AnOlderDaemonSaysItHasNoSoundboard()
    {
        Session session = new();
        session.State["mixer"]!.AsObject().Remove("soundboard");
        session.Echo();
        Assert.Contains("This daemon has no soundboard", session.Frame(), StringComparison.Ordinal);
        session.Key(Key.Enter);
        Assert.Empty(session.Sent);
    }

    [Fact]
    public void EnterAndSpacePlayTheSelectedClipAndTheStopKeysStopIt()
    {
        Session session = new();
        session.Key(Key.Enter);
        JsonObject play = session.Command();
        Assert.Equal("playClip", Text(play, "cmd"));
        Assert.Equal("airhorn", Text(play, "clip"));
        Assert.False(play.ContainsKey("mode"));
        session.Sent.Clear();

        session.Key(Key.Down);
        session.Key(Key.Down);
        session.Key(' ');
        Assert.Equal("broken", Text(session.Command(), "clip"));
        session.Sent.Clear();

        session.Key('s');
        Assert.Equal("stopClip", Text(session.Command(), "cmd"));
        Assert.Equal("broken", Text(session.Command(), "clip"));
        session.Sent.Clear();

        session.Key('S');
        Assert.Equal("stopClips", Text(session.Command(), "cmd"));
    }

    /// <summary>
    /// a auditions the selected clip in the monitor mixes; on the clip the
    /// daemon says it auditions, a stops it. A key held down repeats, and the
    /// repeat sends nothing. The row and the line under the list say so.
    /// </summary>
    [Fact]
    public void AAuditionsTheSelectedClipAndStopsItsAudition()
    {
        Session session = new();
        session.Key('a');
        JsonObject audition = session.Command();
        Assert.Equal("auditionClip", Text(audition, "cmd"));
        Assert.Equal("airhorn", Text(audition, "clip"));
        session.Sent.Clear();
        // The key repeating sends nothing.
        session.Key('a');
        Assert.Empty(session.Sent);

        session.Board["auditioning"] = "airhorn";
        session.Echo();
        (_, int row) = session.Find("Airhorn");
        Assert.Contains("auditioning", session.Draw()[row], StringComparison.Ordinal);
        Assert.Contains("Auditioning in the monitor mixes only", session.Frame(), StringComparison.Ordinal);
        // An audition is not a press: the clip does not count as playing.
        Assert.DoesNotContain("playing", session.Draw()[0..4].Aggregate((a, b) => a + b), StringComparison.Ordinal);

        session.Later();
        session.Key('a');
        Assert.Equal(["stopAudition"], session.Commands());
        session.Sent.Clear();

        // Another clip's key auditions that clip in its place.
        session.Later();
        session.Key(Key.Down);
        session.Key('a');
        Assert.Equal("drumroll", Text(session.Command(), "clip"));
        session.Sent.Clear();

        // A clip playing and auditioned at once says both.
        session.Clip(0)["playing"] = true;
        session.Echo();
        Assert.Contains(" audition", session.Draw()[session.Find("Airhorn").Y], StringComparison.Ordinal);
    }

    [Fact]
    public void AHoldClipIsHeldOnceAcceptedAndLetGoOnTheNextPress()
    {
        Session session = new();
        session.Key(Key.Down);
        Assert.Contains("the first press plays it, the next press lets it go", session.Frame(), StringComparison.Ordinal);
        session.Key(Key.Enter);
        JsonObject press = session.Command();
        Assert.Equal("drumroll", Text(press, "clip"));
        Assert.Contains("Starting", session.Frame(), StringComparison.Ordinal);
        session.Sent.Clear();
        session.Reply(press, null);
        session.Playing(1, true);
        string frame = session.Frame();
        Assert.Contains("held", frame, StringComparison.Ordinal);
        Assert.Contains("press Enter or Space again to let it go", frame, StringComparison.Ordinal);

        session.Later();
        session.Key(' ');
        Assert.Equal("releaseClip", Text(session.Command(), "cmd"));
        Assert.Equal("drumroll", Text(session.Command(), "clip"));
        session.Sent.Clear();
        session.Playing(1, false);
        Assert.Empty(session.Sent);
    }

    [Fact]
    public void TheSecondPressReleasesOnTheHoldAloneEvenBeforeTheClipShowsPlaying()
    {
        Session session = new();
        session.Key(Key.Down);
        session.Hold();
        session.Later();
        session.Key(Key.Enter);
        Assert.Equal("releaseClip", Text(session.Command(), "cmd"));
    }

    [Fact]
    public void AHeldClipThatEndsByItselfIsReleasedAndPlaysAgainOnTheNextPress()
    {
        Session session = new();
        session.Key(Key.Down);
        session.Hold();
        session.Playing(1, true);
        Assert.Empty(session.Sent);
        session.Playing(1, false);
        Assert.Equal("releaseClip", Text(session.Command(), "cmd"));
        session.Sent.Clear();
        Assert.DoesNotContain("held", session.Frame(), StringComparison.Ordinal);

        session.Later();
        session.Key(Key.Enter);
        Assert.Equal("playClip", Text(session.Command(), "cmd"));
    }

    [Fact]
    public void AHoldThatNeverStartsIsLetGoAfterAWhile()
    {
        Session session = new();
        session.Key(Key.Down);
        session.Hold();
        session.Clock.Advance(TimeSpan.FromSeconds(1));
        session.Draw();
        Assert.Empty(session.Sent);
        session.Clock.Advance(TimeSpan.FromSeconds(2));
        session.Draw();
        Assert.Equal("releaseClip", Text(session.Command(), "cmd"));
    }

    [Fact]
    public void ALoadingClipKeepsItsHold()
    {
        Session session = new();
        session.Key(Key.Down);
        session.Hold();
        session.Playing(1, true);
        session.Clip(1)["status"] = "loading";
        session.Playing(1, false);
        Assert.Empty(session.Sent);
    }

    [Fact]
    public void AnotherClientMovingTheClipOutOfHoldLetsTheHoldGo()
    {
        Session session = new();
        session.Key(Key.Down);
        session.Hold();
        session.Playing(1, true);
        session.Clip(1)["mode"] = "overlap";
        session.Echo();
        session.Draw();
        Assert.Equal("releaseClip", Text(session.Command(), "cmd"));
        session.Sent.Clear();
        session.Later();
        session.Key(Key.Enter);
        Assert.Equal("playClip", Text(session.Command(), "cmd"));
        Assert.False(session.Command().ContainsKey("requestId"));
    }

    [Fact]
    public void ARefusedPressIsNotHeldAndShowsTheDaemonsSentence()
    {
        Session session = new();
        session.Key(Key.Down);
        session.Key(Key.Enter);
        JsonObject press = session.Command();
        session.Sent.Clear();
        session.Reply(press, "The soundboard is off. Turn it on first.");
        Assert.Contains("The soundboard is off. Turn it on first.", session.Draw()[^1], StringComparison.Ordinal);
        session.Later();
        session.Key(Key.Enter);
        Assert.Equal("playClip", Text(session.Command(), "cmd"));
    }

    [Fact]
    public void ADoublePressWithinTheRepeatWindowSendsOnePress()
    {
        Session session = new();
        session.Key(Key.Down);
        session.Key(Key.Enter);
        session.Clock.Advance(TimeSpan.FromMilliseconds(300));
        session.Key(' ');
        JsonObject press = session.Command();
        Assert.Equal("playClip", Text(press, "cmd"));

        // A second press past the window while the daemon has not answered lets go once it does.
        session.Later();
        session.Key(Key.Enter);
        Assert.Single(session.Sent);
        session.Sent.Clear();
        session.Reply(press, null);
        Assert.Equal("releaseClip", Text(session.Command(), "cmd"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void HoldingEnterDownDoesNotAlternatePlayAndStop(int clip)
    {
        Session session = new();
        for (int i = 0; i < clip; i++) session.Key(Key.Down);
        session.Key(Key.Enter);
        JsonObject press = session.Command();
        // A desktop's repeat starts after half a second or so, then comes every 40 ms.
        session.Clock.Advance(TimeSpan.FromMilliseconds(660));
        session.Key(Key.Enter);
        for (int i = 0; i < 30; i++)
        {
            session.Clock.Advance(TimeSpan.FromMilliseconds(40));
            session.Key(Key.Enter);
        }
        Assert.Single(session.Sent);
        if (clip == 1)
        {
            session.Sent.Clear();
            session.Reply(press, null);
            Assert.Empty(session.Sent);
            session.Later();
            session.Key(Key.Enter);
            Assert.Equal("releaseClip", Text(session.Command(), "cmd"));
        }
    }

    [Fact]
    public void AnOverlapClipPlaysOnEveryPress()
    {
        Session session = new();
        session.Key(Key.Enter);
        session.Key(Key.Enter);
        Assert.Equal(["playClip", "playClip"], session.Commands());
    }

    [Fact]
    public void StoppingAHeldClipLetsTheHoldGo()
    {
        Session session = new();
        session.Key(Key.Down);
        session.Hold();
        session.Playing(1, true);
        session.Key('s');
        Assert.Equal(["stopClip", "releaseClip"], session.Commands());
        session.Sent.Clear();
        session.Playing(1, false);
        Assert.Empty(session.Sent);
    }

    [Fact]
    public void AClosedConnectionEndsTheHoldsAndNoPressGoesOutWhileDisconnected()
    {
        Session session = new();
        session.Key(Key.Down);
        session.Hold();
        session.Playing(1, true);

        session.Link.EndSession();
        session.Later();
        session.Key(Key.Enter);
        Assert.Empty(session.Sent);
        Assert.Contains("Not connected to the daemon", session.Draw()[^1], StringComparison.Ordinal);

        session.Echo();
        session.Draw();
        session.Later();
        session.Key(Key.Enter);
        JsonObject press = session.Command();
        Assert.Equal("playClip", Text(press, "cmd"));
        session.Sent.Clear();
        session.Key('S');
        Assert.Equal(["stopClips"], session.Commands());
        session.Sent.Clear();
        session.Reply(press, null);
        Assert.Equal("releaseClip", Text(session.Command(), "cmd"));
    }

    [Fact]
    public void APressWaitingWhenTheConnectionClosesIsNeverHeld()
    {
        Session session = new();
        session.Key(Key.Down);
        session.Key(Key.Enter);
        session.Sent.Clear();
        session.Link.EndSession();
        session.Echo();
        session.Draw();
        session.Later();
        session.Key(Key.Enter);
        Assert.Equal("playClip", Text(session.Command(), "cmd"));
    }

    [Theory]
    [InlineData('+', 0, 0.85)]
    [InlineData('-', 0, 0.75)]
    [InlineData(']', 0, 0.81)]
    [InlineData('[', 0, 0.79)]
    [InlineData('-', 2, 0.45)]
    public void GainKeysStepTheClipsGain(char key, int clip, double expected)
    {
        Session session = new();
        for (int i = 0; i < clip; i++) session.Key(Key.Down);
        session.Key(key);
        JsonObject command = session.Command();
        Assert.Equal("updateClip", Text(command, "cmd"));
        Assert.Equal(expected, command["gain"]!.GetValue<double>(), 6);
        Assert.Single(command, pair => pair.Key is "gain" or "name" or "mode" or "loop");
    }

    [Fact]
    public void GainStopsAtItsTopWithoutSendingAnything()
    {
        Session session = new();
        session.Key(Key.Down);
        session.Key('+');
        Assert.Empty(session.Sent);
    }

    [Fact]
    public void ModeAndLoopKeysStepTheClipsSettings()
    {
        Session session = new();
        session.Key('m');
        Assert.Equal("restart", Text(session.Command(), "mode"));
        session.Sent.Clear();
        session.Key('l');
        Assert.True(session.Command()["loop"]!.GetValue<bool>());
        session.Sent.Clear();

        // Hold becomes overlap; the hold goes once the daemon reports the new mode.
        session.Key(Key.Down);
        session.Hold();
        session.Playing(1, true);
        session.Key('m');
        Assert.Equal("overlap", Text(session.Command(), "mode"));
        session.Sent.Clear();
        session.Clip(1)["mode"] = "overlap";
        session.Echo();
        session.Draw();
        Assert.Equal("releaseClip", Text(session.Command(), "cmd"));
        session.Sent.Clear();
        session.Key('l');
        Assert.False(session.Command()["loop"]!.GetValue<bool>());
    }

    [Fact]
    public void ControlAndAnArrowMoveTheClipAndTheSelectionFollows()
    {
        Session session = new();
        session.Key(Key.Down, ctrl: true);
        JsonObject command = session.Command();
        Assert.Equal("setClipOrder", Text(command, "cmd"));
        Assert.Equal(["drumroll", "airhorn", "broken"], command["clips"]!.AsArray().Select(id => id!.GetValue<string>()));
        session.Sent.Clear();

        var clips = session.Board["clips"]!.AsArray();
        JsonNode first = clips[0]!;
        clips.RemoveAt(0);
        clips.Insert(1, first);
        session.Echo();
        session.Draw();
        session.Key(Key.Enter);
        Assert.Equal("airhorn", Text(session.Command(), "clip"));
        session.Sent.Clear();

        session.Key(Key.Up);
        session.Key(Key.Up, ctrl: true);
        Assert.Empty(session.Sent);
    }

    [Fact]
    public void RenameSendsTheNameAndDeleteNeedsTheWordYes()
    {
        Session session = new();
        session.Key('r');
        Assert.True(session.App.Prompting);
        for (int i = 0; i < "Airhorn".Length; i++) session.Key(Key.Backspace);
        session.Type("Horn");
        session.Key(Key.Enter);
        Assert.Equal("Horn", Text(session.Command(), "name"));
        Assert.Equal("airhorn", Text(session.Command(), "clip"));
        session.Sent.Clear();

        session.Key('d');
        session.Type("no");
        session.Key(Key.Enter);
        Assert.Empty(session.Sent);
        session.Key('d');
        session.Type("yes");
        session.Key(Key.Enter);
        Assert.Equal("deleteClip", Text(session.Command(), "cmd"));
        Assert.Equal("airhorn", Text(session.Command(), "clip"));
    }

    [Fact]
    public void TheMasterIsSfxsOwnVolume()
    {
        Session session = new();
        session.Key('>');
        JsonObject command = session.Command();
        Assert.Equal("setChannelVolume", Text(command, "cmd"));
        Assert.Equal("sfx", Text(command, "channel"));
        Assert.Equal(0.65, command["value"]!.GetValue<double>(), 6);
        session.Sent.Clear();
        session.Key(',');
        Assert.Equal(0.59, session.Command()["value"]!.GetValue<double>(), 6);
        session.Sent.Clear();

        session.Board["channel"] = null;
        session.Board["error"] = "The soundboard is off because the SFX channel was deleted.";
        session.Echo();
        Assert.Contains("no SFX channel", session.Frame(), StringComparison.Ordinal);
        session.Key('>');
        Assert.Empty(session.Sent);
        Assert.Contains("The SFX channel was deleted", session.Frame(), StringComparison.Ordinal);
    }

    private static JsonObject NewClip(string id, string name) => new()
    {
        ["id"] = id, ["name"] = name, ["seconds"] = 3, ["gain"] = 1, ["mode"] = "overlap",
        ["loop"] = false, ["icon"] = "", ["colour"] = null, ["loaded"] = false, ["status"] = "loading",
        ["error"] = null, ["playing"] = false,
    };

    /// <summary>The daemon's answer to an import, in its order: clipImport, the state, then the commandResult.</summary>
    private static void AnswerImport(Session session, JsonObject command, bool ok, string message, string? clip)
    {
        session.Link.Receive(new JsonObject
        {
            ["type"] = "clipImport", ["ok"] = ok, ["message"] = message, ["clip"] = clip,
        }.ToJsonString());
        session.Echo();
        long before = session.Link.LastImport?.Serial ?? 0;
        session.Link.Receive(new JsonObject
        {
            ["type"] = "commandResult", ["requestId"] = Text(command, "requestId"), ["error"] = ok ? null : message,
        }.ToJsonString());
        // The link records the outcome on the request's continuation.
        Assert.True(SpinWait.SpinUntil(() => (session.Link.LastImport?.Serial ?? 0) > before, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void AnImportSendsTheTypedPathAndShowsTheDaemonsRefusal()
    {
        Session session = new();
        session.Key('i');
        Assert.True(session.App.Prompting);
        string path = "/home/someone/Music/" + new string('a', 120) + ".wav";
        session.Type(path);
        // A long path shows its end, where the typing is.
        Assert.Contains("aaaa.wav", session.Draw()[^1], StringComparison.Ordinal);
        session.Key(Key.Enter);
        JsonObject command = session.Command();
        Assert.Equal("importClip", Text(command, "cmd"));
        Assert.Equal(path, Text(command, "path"));
        Assert.Contains("Importing", session.Frame(), StringComparison.Ordinal);

        AnswerImport(session, command, false, "The clip is 31.0 s long; the limit is 30 s.", null);
        string[] lines = session.Draw();
        Assert.Contains("The clip is 31.0 s long; the limit is 30 s.", lines[^1], StringComparison.Ordinal);
        Assert.Contains("The clip is 31.0 s long; the limit is 30 s.", lines[^3], StringComparison.Ordinal);
    }

    [Fact]
    public void AnImportAnsweredOnAnotherTabIsShownThereAndSelectsTheClipTheDaemonNamed()
    {
        Session session = new();
        session.Key('i');
        session.Type("/tmp/applause.flac");
        session.Key(Key.Enter);
        JsonObject command = session.Command();
        session.Sent.Clear();
        session.App.ShowTab(0);
        session.Draw();

        // The new clip is not the last one, so only the daemon's id finds it.
        session.Board["clips"]!.AsArray().Insert(0, NewClip("applause", "Applause"));
        AnswerImport(session, command, true, "Applause was added to the soundboard.", "applause");
        Assert.Contains("Applause was added to the soundboard.", session.Draw()[^1], StringComparison.Ordinal);

        session.Clock.Advance(TimeSpan.FromSeconds(5));
        session.App.ShowTab(SoundsTab);
        string frame = session.Frame();
        Assert.Contains("Applause was added to the soundboard.", frame, StringComparison.Ordinal);
        session.Key(Key.Enter);
        Assert.Equal("applause", Text(session.Command(), "clip"));
    }

    [Fact]
    public void RRestoresTheStarterSoundsAndShowsTheDaemonsSentence()
    {
        Session session = new();
        session.Key('R');
        JsonObject command = session.Command();
        Assert.Equal("restoreStarterSounds", Text(command, "cmd"));
        Assert.Contains("Importing the starter sounds", session.Frame(), StringComparison.Ordinal);

        session.Link.Receive(new JsonObject
        {
            ["type"] = "starterSounds", ["ok"] = true, ["message"] = "Every starter sound is on the soundboard already.",
            ["clips"] = new JsonArray(), ["skipped"] = new JsonArray(),
        }.ToJsonString());
        long before = session.Link.LastImport?.Serial ?? 0;
        session.Link.Receive(new JsonObject { ["type"] = "commandResult", ["requestId"] = Text(command, "requestId"), ["error"] = null }.ToJsonString());
        Assert.True(SpinWait.SpinUntil(() => (session.Link.LastImport?.Serial ?? 0) > before, TimeSpan.FromSeconds(5)));
        Assert.Contains("Every starter sound is on the soundboard already.", session.Frame(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnImportFailsAtOnceWithoutAConnection()
    {
        Session session = new();
        session.Key('i');
        session.Type("/tmp/applause.flac");
        session.Link.EndSession();
        session.Key(Key.Enter);
        Assert.Empty(session.Sent);
        Assert.False(session.Link.Importing);
        string[] lines = session.Draw();
        Assert.Contains("Not connected to the daemon", lines[^1], StringComparison.Ordinal);
        Assert.Contains("Not connected to the daemon", lines[^3], StringComparison.Ordinal);
    }

    [Fact]
    public void APathLongerThanTheDaemonTakesIsRefusedHere()
    {
        Session session = new();
        session.Key('i');
        session.Type("~/" + new string('a', 4094));
        session.Key(Key.Enter);
        Assert.Empty(session.Sent);
        Assert.Contains("the daemon takes at most 4096", session.Frame(), StringComparison.Ordinal);
    }

    [Fact]
    public void ALongPromptNeverStartsOnHalfACharacter()
    {
        Session session = new();
        session.Key('i');
        // The prompt is " Path of the file to import: ", 29 cells; 78 fit after the ellipsis.
        string text = new string('a', 100) + "\U0001F600" + new string('b', 77);
        session.Type(text);
        session.Draw();
        Assert.Equal('…', session.Screen.At(0, 23).Ch);
        Assert.False(char.IsLowSurrogate(session.Screen.At(1, 23).Ch));
    }

    [Fact]
    public void AnEmptyLibrarySaysHowToImportAndStillTakesTheBoardKeys()
    {
        Session session = new();
        session.Board["clips"] = new JsonArray();
        session.Echo();
        Assert.Contains("No clips yet. Press i", session.Frame(), StringComparison.Ordinal);
        session.Key(Key.Enter);
        Assert.Empty(session.Sent);
        session.Key('S');
        Assert.Equal("stopClips", Text(session.Command(), "cmd"));
    }

    [Fact]
    public void ManyClipsScrollToKeepTheSelectionInView()
    {
        Session session = new();
        JsonArray clips = [];
        for (int i = 0; i < 40; i++)
            clips.Add(new JsonObject
            {
                ["id"] = $"clip{i}", ["name"] = $"Clip {i:00}", ["seconds"] = 1, ["gain"] = 1, ["mode"] = "overlap",
                ["loop"] = false, ["icon"] = "", ["colour"] = null, ["loaded"] = true, ["status"] = "ready",
                ["error"] = null, ["playing"] = false,
            });
        session.Board["clips"] = clips;
        session.Echo();
        session.Key(Key.End);
        string frame = session.Frame();
        Assert.Contains("Clip 39", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("Clip 00", frame, StringComparison.Ordinal);
        Assert.Contains("F1/? help", frame, StringComparison.Ordinal);
        session.Key(Key.Enter);
        Assert.Equal("clip39", Text(session.Command(), "clip"));
        session.Sent.Clear();

        // A page is ten clips, as on the Mixer tab.
        session.Key(Key.Home);
        session.Key(Key.PageDown);
        session.Key(Key.Enter);
        Assert.Equal("clip10", Text(session.Command(), "clip"));
        session.Sent.Clear();
        session.Key(Key.PageUp);
        session.Key(Key.Enter);
        Assert.Equal("clip0", Text(session.Command(), "clip"));
    }

    [Fact]
    public void ClipMetersAreAPositionAndAVoiceCountNotALevel()
    {
        DaemonLink link = new();
        link.Receive("""{"type":"meters","levels":{"clip:airhorn":[0.25,3],"ch:sfx":[0.5,0.5]}}""");
        Assert.Equal((0.25, 3), link.ClipProgress("airhorn")!.Value);
        Assert.Null(link.ClipProgress("drumroll"));
        Assert.Equal(0, link.Meter("clip", "airhorn"));
        link.Receive("""{"type":"meters","levels":{"ch:sfx":[0.5,0.5]}}""");
        Assert.Null(link.ClipProgress("airhorn"));
    }

    /// <summary>Puts the drum roll on Intro and the broken clip on Memes, as the daemon reports pages.</summary>
    private static void Pages(Session session)
    {
        session.Clip(1)["page"] = "Intro";
        session.Clip(2)["page"] = "Memes";
        session.Board["pages"] = new JsonArray("", "Intro", "Memes");
        session.Echo();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PagesStandAsTabsOverTheListOfThePageShown(bool light)
    {
        Theme theme = light ? Latte() : Theme.Material;
        Session session = new(theme);
        // One page, the default one: no tabs.
        Assert.DoesNotContain(" Main ", session.Frame(), StringComparison.Ordinal);
        Pages(session);
        string frame = session.Frame();
        (int mainX, int tabs) = session.Find(" Main ");
        Assert.Equal(tabs, session.Find(" Intro ").Y);
        Assert.Equal(tabs, session.Find(" Memes ").Y);
        Assert.Equal(theme.Selection, session.Screen.At(mainX, tabs).Back);
        Assert.Equal(theme.Card, session.Screen.At(session.Find(" Intro ").X, tabs).Back);
        // The columns stand under the tabs, and the header still counts the whole board.
        Assert.Equal(tabs + 1, session.Find("CLIP").Y);
        Assert.Contains("3 clips", frame, StringComparison.Ordinal);
        Assert.Contains("Airhorn", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("Drum roll", frame, StringComparison.Ordinal);
        Assert.Equal(' ', session.Screen.At(79, 23).Ch);

        session.Key('p');
        frame = session.Frame();
        Assert.Contains("Drum roll", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("Airhorn", frame, StringComparison.Ordinal);
        Assert.Equal(theme.Selection, session.Screen.At(session.Find(" Intro ").X, tabs).Back);
        session.Key(Key.Enter);
        Assert.Equal("drumroll", Text(session.Command(), "clip"));
        session.Sent.Clear();

        // Round the start to the last page.
        session.Key('P');
        session.Key('P');
        Assert.Contains("Broken", session.Frame(), StringComparison.Ordinal);
        Assert.DoesNotContain("Drum roll", session.Frame(), StringComparison.Ordinal);
    }

    [Fact]
    public void ManyPagesKeepTheShownTabInSight()
    {
        Session session = new();
        string[] names = [.. Enumerable.Range(1, 12).Select(i => $"Page number {i}")];
        session.Clip(2)["page"] = names[^1];
        session.Board["pages"] = new JsonArray([.. new[] { "" }.Concat(names).Select(n => (JsonNode?)n)]);
        session.Echo();
        Assert.Contains("›", session.Frame(), StringComparison.Ordinal);
        session.Key('P');
        string frame = session.Frame();
        Assert.Contains("Page number 12", frame, StringComparison.Ordinal);
        Assert.Contains("‹", frame, StringComparison.Ordinal);
        Assert.Contains("Broken", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void ClipsMoveBetweenPagesAndAPageIsRenamedOrEmptied()
    {
        Session session = new();
        Pages(session);
        // On the default page, the airhorn trades places with the next clip
        // there, the broken one moved off it aside.
        session.Clip(2)["page"] = "";
        session.Board["pages"] = new JsonArray("", "Intro");
        session.Echo();
        session.Draw();
        session.Key(Key.Down, ctrl: true);
        Assert.Equal(["broken", "drumroll", "airhorn"], session.Command()["clips"]!.AsArray().Select(c => c!.GetValue<string>()));
        session.Sent.Clear();

        // g moves the selected clip to a page by name, and the tab follows it once the daemon says so.
        session.Key(Key.Home);
        session.Key('g');
        session.Key(Key.Backspace);
        session.Type("intro");
        session.Key(Key.Enter);
        JsonObject move = session.Command();
        Assert.Equal(("updateClip", "airhorn", "intro"), (Text(move, "cmd"), Text(move, "clip"), Text(move, "page")));
        session.Sent.Clear();
        session.Clip(0)["page"] = "Intro";
        session.Echo();
        string frame = session.Frame();
        Assert.Contains("Airhorn", frame, StringComparison.Ordinal);
        Assert.Contains("Drum roll", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("Broken", frame, StringComparison.Ordinal);

        // i imports onto the page shown.
        session.Key('i');
        session.Type("/tmp/applause.flac");
        session.Key(Key.Enter);
        Assert.Equal("Intro", Text(session.Command(), "page"));
        session.Sent.Clear();

        // G renames the page shown; - moves its clips to the default page.
        session.Key('G');
        for (int i = 0; i < "Intro".Length; i++) session.Key(Key.Backspace);
        session.Type("Jingles");
        session.Key(Key.Enter);
        JsonObject rename = session.Command();
        Assert.Equal(("renameClipPage", "Intro", "Jingles"), (Text(rename, "cmd"), Text(rename, "page"), Text(rename, "name")));
        session.Sent.Clear();
        session.Key('G');
        for (int i = 0; i < "Intro".Length; i++) session.Key(Key.Backspace);
        session.Type("-");
        session.Key(Key.Enter);
        Assert.Equal("", Text(session.Command(), "name"));
        session.Sent.Clear();

        // The default page keeps its name.
        session.Key('p');
        Assert.DoesNotContain("Broken", session.Frame(), StringComparison.Ordinal);
        session.Key('p');
        Assert.Contains("Broken", session.Frame(), StringComparison.Ordinal);
        session.Key('G');
        Assert.False(session.App.Prompting);
        Assert.Empty(session.Sent);
    }

    [Fact]
    public void ATypedPathIsMadeAbsoluteTheWayTheShellWouldHaveIt()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, "Music", "horn.wav"), SoundsView.ImportPath("~/Music/horn.wav"));
        Assert.Equal("/tmp/a b.wav", SoundsView.ImportPath("'/tmp/a b.wav'"));
        Assert.Equal("/tmp/a b.wav", SoundsView.ImportPath("\"/tmp/a b.wav\" "));
        Assert.Equal("/tmp/a b.wav", SoundsView.ImportPath("file:///tmp/a%20b.wav"));
        Assert.Equal("/tmp/a b.wav", SoundsView.ImportPath("file://localhost/tmp/a%20b.wav"));
        Assert.Equal("/tmp/take#2.wav", SoundsView.ImportPath("file:///tmp/take#2.wav"));
        Assert.Equal("/tmp/My Clip (1).wav", SoundsView.ImportPath("/tmp/My\\ Clip\\ \\(1\\).wav"));
        Assert.Equal("/tmp/a\\b.wav", SoundsView.ImportPath("'/tmp/a\\b.wav'"));
        Assert.Equal(Path.Combine(Environment.CurrentDirectory, "horn.wav"), SoundsView.ImportPath("horn.wav"));
    }

    [Fact]
    public void HelpListsTheSoundsKeys()
    {
        Session session = new(width: 80, height: 24);
        session.Key('?');
        string frame = session.Frame();
        for (int i = 0; i < 13; i++) session.Key(Key.Down);
        frame += session.Frame();
        foreach (string text in new[] { "a hold clip plays until pressed again", "S stop all", "i import a file", "R restore the starter sounds", "SFX volume",
                     "a audition in the monitor mixes, not the stream", "G rename the page shown" })
            Assert.Contains(text, frame, StringComparison.Ordinal);
    }
}
