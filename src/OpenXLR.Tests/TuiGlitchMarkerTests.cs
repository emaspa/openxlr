using System.Text.Json.Nodes;
using OpenXLR.Tui;

namespace OpenXLR.Tests;

/// <summary>
/// Ctrl+G in the terminal mixer marks a glitch on any tab, a text prompt
/// included, with one labelled command and a line at the bottom that says
/// so; with no daemon it sends nothing and says why.
/// </summary>
public sealed class TuiGlitchMarkerTests
{
    private const string State = """
        { "type": "state", "connected": false, "capabilities": {}, "state": {}, "devices": [], "profiles": [],
          "mixer": { "channels": [], "mixes": [{ "id": "monitor", "name": "Monitor A", "volume": 1, "muted": false, "kind": "monitor" }],
                     "monitorOutputs": [], "inserts": {} } }
        """;

    [Fact]
    public void CtrlGMarksAGlitchFromAnyTab()
    {
        var link = new DaemonLink();
        List<string> sent = [];
        link.Sent += sent.Add;
        link.Receive(State);
        var app = new App(link, Theme.Material, time: new ManualTime());
        for (int tab = 0; tab < app.Views.Count; tab++)
        {
            app.ShowTab(tab);
            app.Handle(new KeyPress(Key.Char, 'g', Ctrl: true));
        }
        Assert.Contains("Glitch marked at ", Bottom(app), StringComparison.Ordinal);
        // A prompt takes every key but this one.
        app.Ask("Name", "", _ => { });
        app.Handle(new KeyPress(Key.Char, 'g', Ctrl: true));
        Assert.True(app.Prompting);

        // Drawing a tab can ask the daemon for something of its own; only the markers count here.
        JsonNode[] marks = [.. sent.Select(json => JsonNode.Parse(json)!).Where(c => c["cmd"]!.GetValue<string>() == "markGlitch")];
        Assert.Equal(app.Views.Count + 1, marks.Length);
        Assert.All(marks, command => Assert.Equal("terminal mixer", command["label"]!.GetValue<string>()));
    }

    [Fact]
    public void WithoutTheDaemonNothingIsSent()
    {
        var link = new DaemonLink();
        List<string> sent = [];
        link.Sent += sent.Add;
        var app = new App(link, Theme.Material, time: new ManualTime());
        app.Handle(new KeyPress(Key.Char, 'g', Ctrl: true));
        Assert.Empty(sent);
        Assert.Contains("Not marked: the daemon is not connected", Bottom(app), StringComparison.Ordinal);
    }

    private static string Bottom(App app)
    {
        Screen screen = new(120, 32);
        app.Draw(screen);
        return new string(Enumerable.Range(0, screen.Width).Select(x => screen.At(x, screen.Height - 1).Ch).ToArray());
    }
}
