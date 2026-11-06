using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.Tui;

namespace OpenXLR.Tests;

/// <summary>
/// The terminal mixer's output EQ: the Outputs tab lists each mix's EQ and
/// opens the microphone EQ's band editor on it, whose commands name the mix
/// and no channel; hardware mixing on the Wave XLR Pro holds a mix's EQ off
/// with the daemon's sentence and no editor.
/// </summary>
public sealed class TuiMixEqTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private const string HeldSentence = "The Wave XLR Pro builds Monitor A for its jacks, so its EQ is off while hardware mixing runs. It comes back with software mixing.";

    private static string State(bool held = false)
    {
        JsonNode root = JsonNode.Parse("""
            {"type":"state","connected":true,"capabilities":{"xlrInputs":1},"state":{},
             "mixer":{"channels":[],"inserts":{},
                      "mixes":[{"id":"monitor","name":"Monitor A","kind":"monitor"},{"id":"stream","name":"Stream","kind":"virtualMic"}]},
             "devices":[],"profiles":[]}
            """)!;
        foreach (JsonNode? mix in root["mixer"]!["mixes"]!.AsArray())
        {
            mix!["eq"] = JsonSerializer.SerializeToNode(MicEq.Default, Web);
            mix["eq"]!["available"] = true;
            mix["eq"]!["processing"] = "software";
        }
        if (held)
            root["mixer"]!["hardwareMixing"] = new JsonObject
            {
                ["wanted"] = true, ["active"] = true, ["disabled"] = new JsonObject { ["eq:mix:monitor"] = HeldSentence },
            };
        return root.ToJsonString();
    }

    private static (App App, List<JsonObject> Sent) Ready(string state)
    {
        DaemonLink link = new();
        List<JsonObject> sent = [];
        link.Sent += json => sent.Add(JsonNode.Parse(json)!.AsObject());
        link.Receive(state);
        App app = new(link, Theme.Material);
        app.ShowTab(3);
        return (app, sent);
    }

    private static string[] Frame(App app, int width = 160, int height = 80)
    {
        Screen screen = new(width, height);
        app.Draw(screen);
        return [.. Enumerable.Range(0, screen.Height)
            .Select(y => new string(Enumerable.Range(0, screen.Width).Select(x => screen.At(x, y).Ch).ToArray()))];
    }

    private static void Select(App app, string label)
    {
        for (int i = 0; i < 100; i++) app.Handle(new KeyPress(Key.Up));
        for (int i = 0; i < 100; i++)
        {
            if (Frame(app).Any(line => line.IndexOf(label, StringComparison.Ordinal) is int at and >= 2 && line[at - 2] == '┃')) return;
            app.Handle(new KeyPress(Key.Down));
        }
        throw new Xunit.Sdk.XunitException("No selected row " + label);
    }

    [Fact]
    public void OutputsOpensAMixsEqAndItsCommandsNameTheMix()
    {
        var (app, sent) = Ready(State());
        Assert.Contains(Frame(app), line => line.Contains("OUTPUT EQ", StringComparison.Ordinal));
        Select(app, "Stream bands");
        app.Handle(new KeyPress(Key.Enter));
        Assert.Contains(Frame(app), line => line.Contains("STREAM / OUTPUT EQ", StringComparison.OrdinalIgnoreCase));
        Select(app, "EQ enabled");
        app.Handle(new KeyPress(Key.Char, ' '));
        Select(app, "Band 2 gain");
        app.Handle(new KeyPress(Key.Right));
        Assert.Equal(["setEqEnabled", "setEqBand"], sent.Select(c => c["cmd"]!.GetValue<string>()));
        Assert.All(sent, c =>
        {
            Assert.Equal("stream", c["mix"]!.GetValue<string>());
            Assert.False(c.ContainsKey("channel"));
        });
        Assert.Equal(2, sent[1]["band"]!.GetValue<int>());
        Assert.Equal(.25, sent[1]["gain"]!.GetValue<double>());
        app.Handle(new KeyPress(Key.Escape));
        Assert.Contains(Frame(app), line => line.Contains("Output volume", StringComparison.Ordinal));
    }

    [Fact]
    public void HardwareMixingHoldsAMixsEqOffWithTheDaemonsSentence()
    {
        var (app, _) = Ready(State(held: true));
        string[] frame = Frame(app, width: 400);
        Assert.Contains(frame, line => line.Contains("Monitor A EQ", StringComparison.Ordinal) && line.Contains("Wave XLR Pro builds Monitor A", StringComparison.Ordinal));
        Assert.DoesNotContain(frame, line => line.Contains("Monitor A bands", StringComparison.Ordinal));
        Assert.Contains(frame, line => line.Contains("Stream bands", StringComparison.Ordinal));
    }
}
