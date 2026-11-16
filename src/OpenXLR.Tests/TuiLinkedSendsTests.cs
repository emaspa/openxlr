using System.Text.Json.Nodes;
using OpenXLR.Tui;

namespace OpenXLR.Tests;

/// <summary>
/// The mixer tab's link key: l links or unlinks the selected channel's
/// sends with setLinkedSends, the strip marks a linked channel, the line
/// under the desk says which way the link is set, and a channel the daemon
/// reports no link for, from an older daemon, offers none.
/// </summary>
public sealed class TuiLinkedSendsTests
{
    private const string StateJson = """
        {
          "type": "state", "daemonVersion": "0.8.2", "connected": true,
          "capabilities": {}, "state": {}, "devices": [], "profiles": [],
          "mixer": {
            "mixes": [
              { "id": "monitor", "name": "Monitor A", "volume": 1, "muted": false, "kind": "monitor" },
              { "id": "stream", "name": "Stream", "volume": 1, "muted": false, "kind": "virtualMic" }
            ],
            "channels": [
              { "id": "game", "name": "Game", "levels": { "monitor": 0.4, "stream": 0.9 }, "mutedIn": [],
                "hardware": false, "volume": 1, "linkedSends": false },
              { "id": "music", "name": "Music", "levels": { "monitor": 0.6, "stream": 0.6 }, "mutedIn": ["stream"],
                "hardware": false, "volume": 1, "linkedSends": true }
            ],
            "monitorOutputs": [], "inserts": {}
          }
        }
        """;

    private sealed class Session
    {
        public DaemonLink Link { get; } = new();
        public App App { get; }
        public List<JsonObject> Sent { get; } = [];
        public JsonNode State { get; } = JsonNode.Parse(StateJson)!;
        public Screen Screen { get; } = new(140, 36);

        public Session(Action<JsonNode>? change = null)
        {
            change?.Invoke(State);
            Link.Sent += json => Sent.Add(JsonNode.Parse(json)!.AsObject());
            Echo();
            App = new(Link, Theme.Material, time: new ManualTime());
            App.Draw(Screen);
        }

        public void Echo() => Link.Receive(State.ToJsonString());
        public void Key(Key key) => App.Handle(new KeyPress(key));
        public void Key(char key) => App.Handle(new KeyPress(OpenXLR.Tui.Key.Char, key));

        /// <summary>Row 0 is the masters, 1 the first channel, and so on.</summary>
        public void Select(int row)
        {
            Key(OpenXLR.Tui.Key.Home);
            for (int step = 0; step < row; step++) Key(OpenXLR.Tui.Key.Down);
        }

        public string[] Draw()
        {
            App.Draw(Screen);
            return [.. Enumerable.Range(0, Screen.Height)
                .Select(y => new string(Enumerable.Range(0, Screen.Width).Select(x => Screen.At(x, y).Ch).ToArray()))];
        }

        public string Frame() => string.Join('\n', Draw());

        /// <summary>The line under the desk that names the send under the cursor.</summary>
        public string Focus() => Draw()[Screen.Height - 2];

        public JsonObject Command() => Assert.Single(Sent);
    }

    [Fact]
    public void LUnlinksALinkedChannelAndLinksItAgain()
    {
        Session session = new();
        session.Select(2);
        session.Draw();
        Assert.Contains("Music > Monitor A", session.Focus(), StringComparison.Ordinal);
        Assert.Contains("sends linked", session.Focus(), StringComparison.Ordinal);

        session.Key('l');
        JsonObject command = session.Command();
        Assert.Equal("setLinkedSends", command["cmd"]!.GetValue<string>());
        Assert.Equal("music", command["channel"]!.GetValue<string>());
        Assert.False(command["value"]!.GetValue<bool>());

        // The daemon's next state carries the link, which the strip and the line follow.
        session.Sent.Clear();
        session.State["mixer"]!["channels"]![1]!["linkedSends"] = false;
        session.Echo();
        Assert.Contains("sends not linked", session.Focus(), StringComparison.Ordinal);
        Assert.DoesNotContain(MixerView.LinkMark, session.Frame());

        session.Key('l');
        command = session.Command();
        Assert.Equal("music", command["channel"]!.GetValue<string>());
        Assert.True(command["value"]!.GetValue<bool>());
    }

    [Fact]
    public void TheStripOfALinkedChannelCarriesTheMark()
    {
        Session session = new();
        string[] rows = session.Draw();
        // Music is linked and its send into Monitor A is 60%; Game is not.
        Assert.Single(rows, row => row.Contains($"60% {MixerView.LinkMark}", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains("40%", StringComparison.Ordinal)
            && !row.Contains($"40% {MixerView.LinkMark}", StringComparison.Ordinal));

        // The same in Stream, where the linked send has the same level.
        session.Key(OpenXLR.Tui.Key.Right);
        Assert.Contains($"60% {MixerView.LinkMark}", session.Frame(), StringComparison.Ordinal);
        Assert.Single(session.Frame(), MixerView.LinkMark);
    }

    [Fact]
    public void TheMarkFollowsTheWordOnAMicrophoneWhoseSendIsMuted()
    {
        // On the strip carrying the interface's microphone, the key is the
        // microphone's own mute, and a muted send shows as a word.
        Session session = new(state => state["mixer"]!["channels"]!.AsArray().Insert(0, JsonNode.Parse("""
            { "id": "xlr1", "name": "XLR 1", "levels": { "monitor": 0.5, "stream": 0.5 }, "mutedIn": ["monitor"],
              "hardware": true, "linkedSends": true }
            """)));
        Assert.Contains($"muted {MixerView.LinkMark}", session.Frame(), StringComparison.Ordinal);
    }

    [Fact]
    public void AChannelWithoutTheFieldOffersNoLink()
    {
        Session session = new(state =>
        {
            foreach (JsonNode? channel in state["mixer"]!["channels"]!.AsArray())
                channel!.AsObject().Remove("linkedSends");
        });
        session.Select(2);
        Assert.DoesNotContain("sends", session.Focus().Replace("Sends to", "", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.DoesNotContain(MixerView.LinkMark, session.Frame());

        session.Key('l');
        Assert.Empty(session.Sent);
        Assert.Contains("Linked sends need a newer daemon", session.Draw()[session.Screen.Height - 1],
            StringComparison.Ordinal);
    }

    [Fact]
    public void LOnTheMastersSendsNothing()
    {
        Session session = new();
        session.Select(0);
        session.Key('l');
        Assert.Empty(session.Sent);
        Assert.Contains("Choose a channel to link its sends", session.Draw()[session.Screen.Height - 1],
            StringComparison.Ordinal);
    }

    [Fact]
    public void LWithoutTheDaemonSaysItWasNotSent()
    {
        Session session = new();
        session.Select(1);
        session.Link.EndSession();
        session.Key('l');
        Assert.Empty(session.Sent);
        Assert.Contains(DaemonLink.NotConnected, session.Draw()[session.Screen.Height - 1], StringComparison.Ordinal);
    }

    [Fact]
    public void TheKeyIsInTheLegendAndTheHelp()
    {
        Session session = new();
        Assert.Contains("l link sends", session.App.Current.Keys, StringComparison.Ordinal);
        session.Key('?');
        Assert.Contains("l links a channel's sends", session.Frame(), StringComparison.Ordinal);
    }
}
