using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.Tui;

namespace OpenXLR.Tests;

/// <summary>
/// u and U in the terminal mixer: undo and redo on every tab, with the
/// UI's Ctrl+Z and Ctrl+Y beside them, letters in a prompt, the answer on
/// the bottom line until the next key, and both keys in the F1 table.
/// </summary>
public sealed class TuiHistoryTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private const string Step = """{"step":41,"kind":"sendLevel","channel":"xlr1","mix":"monitor","count":1,"label":"XLR 1 level in Monitor A"}""";

    private static string State(string? undo = null, string? redo = null, string? note = null, bool history = true)
    {
        JsonNode root = JsonNode.Parse("""
            {"type":"state","connected":true,"capabilities":{"xlrInputs":1},"state":{},"devices":[],"profiles":[],
             "mixer":{"channels":[{"id":"xlr1","name":"XLR 1","hardware":true,"microphone":true,"levels":{"monitor":0.5}}],
                      "mixes":[{"id":"monitor","name":"Monitor A","volume":1,"muted":false,"kind":"monitor"}],
                      "monitorOutputs":[],"inserts":{}}}
            """)!;
        root["mixer"]!["channels"]![0]!["eq"] = JsonSerializer.SerializeToNode(MicEq.Default, Web);
        root["mixer"]!["channels"]![0]!["eq"]!["available"] = true;
        if (history)
            root["history"] = new JsonObject
            {
                ["undo"] = undo is null ? null : JsonNode.Parse(undo),
                ["redo"] = redo is null ? null : JsonNode.Parse(redo),
                ["undoSteps"] = undo is null ? 0 : 1,
                ["redoSteps"] = redo is null ? 0 : 1,
                ["note"] = note,
            };
        return root.ToJsonString();
    }

    private static (App App, List<JsonObject> Sent) Ready(string? state = null)
    {
        DaemonLink link = new();
        List<JsonObject> sent = [];
        link.Sent += json => sent.Add(JsonNode.Parse(json)!.AsObject());
        link.Receive(state ?? State(undo: Step));
        return (new App(link, Theme.Material, time: new ManualTime()), sent);
    }

    private static JsonObject[] History(List<JsonObject> sent) =>
        [.. sent.Where(command => command["cmd"]!.GetValue<string>() is "undo" or "redo")];

    /// <summary>The daemon's answer to a request: the state that reflects it, then the result.</summary>
    private static void Answer(App app, JsonObject command, string state, string? error = null)
    {
        app.Link.Receive(state);
        app.Link.Receive(new JsonObject
        {
            ["type"] = "commandResult",
            ["requestId"] = command["requestId"]!.GetValue<string>(),
            ["error"] = error,
        }.ToJsonString());
    }

    private static string[] Frame(App app, int width = 120, int height = 32)
    {
        Screen screen = new(width, height);
        app.Draw(screen);
        return [.. Enumerable.Range(0, screen.Height)
            .Select(y => new string(Enumerable.Range(0, screen.Width).Select(x => screen.At(x, y).Ch).ToArray()))];
    }

    private static string Bottom(App app) => Frame(app)[^1];

    [Fact]
    public void UAndCtrlZUndoAndCapitalUAndCtrlYRedoOnEveryTab()
    {
        var (app, sent) = Ready();
        KeyPress[] keys =
        [
            new(Key.Char, 'u'), new(Key.Char, 'U'),
            new(Key.Char, 'z', Ctrl: true), new(Key.Char, 'y', Ctrl: true), new(Key.Char, 'Z', Ctrl: true),
        ];
        string[] expected = ["undo", "redo", "undo", "redo", "redo"];
        for (int tab = 0; tab < app.Views.Count; tab++)
        {
            app.ShowTab(tab);
            Frame(app);
            sent.Clear();
            foreach (KeyPress key in keys) app.Handle(key);
            JsonObject[] commands = History(sent);
            Assert.Equal(expected, commands.Select(command => command["cmd"]!.GetValue<string>()));
            // A request, so a refusal comes back to this client, and no step id:
            // the step is not named before the press.
            Assert.All(commands, command =>
            {
                Assert.False(string.IsNullOrEmpty(command["requestId"]?.GetValue<string>()));
                Assert.Null(command["step"]);
            });
            Assert.Equal(sent.Count, commands.Length);
        }
    }

    [Fact]
    public void AFaderMoveGoesOutBeforeTheUndoThatFollowsIt()
    {
        var (app, sent) = Ready();
        app.ShowTab(1);
        Frame(app);
        app.Handle(new KeyPress(Key.Down));
        sent.Clear();
        app.Handle(new KeyPress(Key.Char, ']'));
        app.Handle(new KeyPress(Key.Char, 'u'));
        Assert.Equal(["setLevel", "undo"], sent.Select(command => command["cmd"]!.GetValue<string>()));
    }

    [Fact]
    public void TheUndoneStepIsNamedOnTheBottomLineUntilTheNextKey()
    {
        var (app, sent) = Ready();
        app.Handle(new KeyPress(Key.Char, 'u'));
        Assert.DoesNotContain("Undone", Bottom(app), StringComparison.Ordinal);
        // After an undo the step is the one redo would take.
        Answer(app, History(sent)[^1], State(redo: Step));
        Assert.Contains("Undone: XLR 1 level in Monitor A", Bottom(app), StringComparison.Ordinal);
        Assert.Contains("Undone: XLR 1 level in Monitor A", Bottom(app), StringComparison.Ordinal);
        app.Handle(new KeyPress(Key.Down));
        Assert.DoesNotContain("Undone", Bottom(app), StringComparison.Ordinal);

        app.Handle(new KeyPress(Key.Char, 'U'));
        Answer(app, History(sent)[^1], State(undo: Step));
        Assert.Contains("Redone: XLR 1 level in Monitor A", Bottom(app), StringComparison.Ordinal);
    }

    [Fact]
    public void ASkippedPartShowsTheDaemonsNote()
    {
        var (app, sent) = Ready();
        app.Handle(new KeyPress(Key.Char, 'u'));
        const string Note = "XLR 1 level in Monitor A was changed since, so it was left as it is.";
        Answer(app, History(sent)[^1], State(redo: Step, note: Note));
        Assert.Contains(Note, Bottom(app), StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusalIsShownAsTheDaemonSaysIt()
    {
        var (app, sent) = Ready(State());
        app.Handle(new KeyPress(Key.Char, 'u'));
        Answer(app, History(sent)[^1], State(), "undo: nothing to undo");
        Assert.Contains("undo: nothing to undo", Bottom(app), StringComparison.Ordinal);
        app.Handle(new KeyPress(Key.Down));
        Assert.DoesNotContain("nothing to undo", Bottom(app), StringComparison.Ordinal);
    }

    [Fact]
    public void APromptKeepsTheLettersAndTheCtrlKeys()
    {
        var (app, sent) = Ready();
        string typed = string.Empty;
        app.Ask("Name", "", text => typed = text);
        app.Handle(new KeyPress(Key.Char, 'u'));
        app.Handle(new KeyPress(Key.Char, 'U'));
        app.Handle(new KeyPress(Key.Char, 'z', Ctrl: true));
        app.Handle(new KeyPress(Key.Enter));
        Assert.Equal("uU", typed);
        Assert.Empty(History(sent));
    }

    [Fact]
    public void WithoutTheDaemonOrItsHistoryNothingIsSent()
    {
        var link = new DaemonLink();
        List<string> sent = [];
        link.Sent += sent.Add;
        var app = new App(link, Theme.Material, time: new ManualTime());
        app.Handle(new KeyPress(Key.Char, 'u'));
        Assert.Contains("Not undone: the daemon is not connected", Bottom(app), StringComparison.Ordinal);
        app.Handle(new KeyPress(Key.Char, 'U'));
        Assert.Contains("Not redone: the daemon is not connected", Bottom(app), StringComparison.Ordinal);

        // A daemon from before the history leaves the field out.
        link.Receive(State(history: false));
        app.Handle(new KeyPress(Key.Char, 'u'));
        Assert.Contains("Undo needs a newer daemon", Bottom(app), StringComparison.Ordinal);
        Assert.DoesNotContain(sent, json => json.Contains("\"undo\"", StringComparison.Ordinal) || json.Contains("\"redo\"", StringComparison.Ordinal));
    }

    [Fact]
    public void TheHelpTableListsBothKeys()
    {
        var (app, _) = Ready();
        app.Handle(new KeyPress(Key.F1));
        string[] frame = Frame(app, 120, 60);
        Assert.Contains(frame, line => line.Contains("Undo      u or Ctrl+Z undoes the last mixer change; U or Ctrl+Y redoes.", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUndoLetsTheEqEditorShowTheValueItPutBack()
    {
        var (app, sent) = Ready();
        app.ShowTab(2);
        Select(app, "XLR 1 bands");
        app.Handle(new KeyPress(Key.Enter));
        Select(app, "Band 2 gain");
        app.Handle(new KeyPress(Key.Right));
        Assert.Equal(.25, sent[^1]["gain"]!.GetValue<double>());
        // The editor holds its own edit over an older report for a second.
        app.Link.Receive(State(undo: Step));
        Assert.Contains(Frame(app, 160, 60), line => line.Contains("Band 2 gain", StringComparison.Ordinal) && line.Contains("+0.25 dB", StringComparison.Ordinal));
        // An undo from here lets it go at once, so the report shows.
        app.Handle(new KeyPress(Key.Char, 'u'));
        Answer(app, History(sent)[^1], State(redo: Step));
        Assert.Contains(Frame(app, 160, 60), line => line.Contains("Band 2 gain", StringComparison.Ordinal) && line.Contains("0 dB", StringComparison.Ordinal)
            && !line.Contains("+0.25", StringComparison.Ordinal));
    }

    private static void Select(App app, string label)
    {
        for (int i = 0; i < 100; i++) app.Handle(new KeyPress(Key.Up));
        for (int i = 0; i < 100; i++)
        {
            if (Frame(app, 160, 60).Any(line => line.IndexOf(label, StringComparison.Ordinal) is int at and >= 2 && line[at - 2] == '┃')) return;
            app.Handle(new KeyPress(Key.Down));
        }
        throw new Xunit.Sdk.XunitException("No selected row " + label);
    }
}
