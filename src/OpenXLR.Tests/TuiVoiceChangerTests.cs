using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.Tui;

namespace OpenXLR.Tests;

/// <summary>
/// The terminal mixer's voice changer: the Inputs tab says what each
/// microphone's voice changer runs, one choice row goes through Off and every
/// voice with one setVoiceChanger per key, the custom voice adds its pitch and
/// formant rows, and a microphone whose voice changer cannot run gets the
/// reason alone.
/// </summary>
public sealed class TuiVoiceChangerTests
{
    private const string Json = """
        {
          "type": "state",
          "daemonVersion": "0.3.5",
          "connected": true,
          "device": { "vendor": "Elgato", "model": "Wave XLR", "usbId": "0fd9:007d" },
          "capabilities": { "gain": true, "mute": true, "xlrInputs": 1, "xlrJacks": true, "hpOutputs": 1 },
          "state": { "gainDb": 40, "mute": false },
          "mixer": {
            "mixes": [ { "id": "monitor", "name": "Monitor A", "volume": 1, "muted": false, "kind": "monitor" } ],
            "channels": [
              { "id": "xlr1", "name": "XLR 1", "levels": { "monitor": 1 }, "mutedIn": [], "hardware": true, "microphone": true,
                "voice": { "enabled": false, "voice": "deep", "pitch": 0, "formant": 0, "active": null, "available": true, "error": null } },
              { "id": "music", "name": "Music", "levels": { "monitor": 1 }, "mutedIn": [], "hardware": false, "volume": 1 }
            ],
            "monitorOutputs": [],
            "lowCutHz": 0,
            "softClipGuard": false,
            "inserts": {}
          },
          "devices": [],
          "profiles": []
        }
        """;

    private static string Edit(Action<JsonNode> change)
    {
        JsonNode root = JsonNode.Parse(Json)!;
        change(root["mixer"]!["channels"]![0]!["voice"]!);
        return root.ToJsonString();
    }

    private static (App App, List<string> Sent) Ready(string json)
    {
        DaemonLink link = new();
        List<string> sent = [];
        link.Sent += sent.Add;
        link.Receive(json);
        App app = new(link, Theme.Material);
        app.ShowTab(2);
        app.Draw(new Screen(160, 60));
        return (app, sent);
    }

    private static string[] Draw(App app)
    {
        Screen screen = new(160, 60);
        app.Draw(screen);
        return [.. Enumerable.Range(0, screen.Height)
            .Select(y => new string(Enumerable.Range(0, screen.Width).Select(x => screen.At(x, y).Ch).ToArray()))];
    }

    /// <summary>Walk down the Inputs tab until the selected row is the one with this label.</summary>
    private static string Select(App app, string label)
    {
        for (int i = 0; i < 80; i++)
        {
            string? focused = Draw(app).FirstOrDefault(line => line.IndexOf(label, StringComparison.Ordinal) is int at and >= 2 && line[at - 2] == '┃');
            if (focused is not null) return focused[(focused.IndexOf(label, StringComparison.Ordinal) - 2)..];
            app.Handle(new KeyPress(Key.Down));
        }
        throw new Xunit.Sdk.XunitException($"No row named {label} on the Inputs tab.");
    }

    [Fact]
    public void TheStatusSaysWhatRuns()
    {
        VoiceEntry Of(Action<JsonNode> change) => Snapshot.Parse(Edit(change))!.Mixer.Channels[0].Voice!;
        Assert.Equal("off", Of(_ => { }).Status);
        Assert.Equal("on, starting", Of(v => v["enabled"] = true).Status);
        Assert.Equal("on, Dracula", Of(v => { v["enabled"] = true; v["voice"] = "dracula"; v["active"] = "dracula"; }).Status);
        Assert.Equal("on, Dracula, adds 33 ms",
            Of(v => { v["enabled"] = true; v["voice"] = "dracula"; v["active"] = "dracula"; v["latencyMs"] = 33.3; }).Status);
        Assert.Equal("cannot run", Of(v => v["available"] = false).Status);
        Assert.Equal("no host", Of(v => { v["available"] = false; v["error"] = "no host"; }).Status);
        Assert.Null(Snapshot.Parse(Json)!.Mixer.Channels[1].Voice);

        string all = string.Join('\n', Draw(Ready(Json).App));
        Assert.Contains("Voice changer", all, StringComparison.Ordinal);
        Assert.DoesNotContain("Music voice", all, StringComparison.Ordinal);
        Assert.DoesNotContain("XLR 1 pitch", all, StringComparison.Ordinal);
    }

    [Fact]
    public void OneKeyChoosesTheNextVoiceInOneCommand()
    {
        (App app, List<string> sent) = Ready(Json);
        Assert.Contains("Off", Select(app, "XLR 1 voice  "), StringComparison.Ordinal);
        app.Handle(new KeyPress(Key.Right));
        JsonElement command = JsonDocument.Parse(Assert.Single(sent)).RootElement;
        Assert.Equal("setVoiceChanger", command.GetProperty("cmd").GetString());
        Assert.Equal("xlr1", command.GetProperty("channel").GetString());
        Assert.True(command.GetProperty("enabled").GetBoolean());
        Assert.Equal("custom", command.GetProperty("voice").GetString());

        // On the custom voice, Left goes back to Off with the switch alone, and the shifts show.
        sent.Clear();
        app.Link.Receive(Edit(v => { v["enabled"] = true; v["voice"] = "custom"; v["active"] = "custom"; v["pitch"] = -3; }));
        Assert.Contains("Custom", Select(app, "XLR 1 voice  "), StringComparison.Ordinal);
        app.Handle(new KeyPress(Key.Left));
        command = JsonDocument.Parse(Assert.Single(sent)).RootElement;
        Assert.False(command.GetProperty("enabled").GetBoolean());
        Assert.False(command.TryGetProperty("voice", out _));
        Assert.Contains("-3 st", Select(app, "XLR 1 pitch"), StringComparison.Ordinal);
        sent.Clear();
        app.Handle(new KeyPress(Key.Right));
        command = JsonDocument.Parse(Assert.Single(sent)).RootElement;
        Assert.Equal(-2, command.GetProperty("pitch").GetDouble());
        Assert.False(command.TryGetProperty("enabled", out _));
        Select(app, "XLR 1 formant");
    }

    [Fact]
    public void AVoiceChangerThatCannotRunShowsOnlyWhy()
    {
        string[] frame = Draw(Ready(Edit(v => { v["available"] = false; v["error"] = "needs the native plugin host"; })).App);
        Assert.Contains(frame, line => line.Contains("XLR 1 voice changer", StringComparison.Ordinal)
            && line.Contains("needs the native plugin host", StringComparison.Ordinal));
        Assert.DoesNotContain(frame, line => line.Replace("XLR 1 voice changer", "", StringComparison.Ordinal).Contains("XLR 1 voice", StringComparison.Ordinal));
    }
}
