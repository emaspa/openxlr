using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.Tui;

namespace OpenXLR.Tests;

/// <summary>
/// The terminal mixer's compressor and expander: the Inputs tab says whether
/// each runs, offers its switch and threshold, and its other settings while
/// it is on, each sent as one setExpander or setCompressor field, snapped; a
/// processor that cannot run gets the reason alone.
/// </summary>
public sealed class TuiDynamicsTests
{
    private const string Json = """
        {
          "type": "state",
          "daemonVersion": "0.3.6",
          "connected": true,
          "device": { "vendor": "Elgato", "model": "Wave XLR", "usbId": "0fd9:007d" },
          "capabilities": { "gain": true, "mute": true, "xlrInputs": 1, "xlrJacks": true, "hpOutputs": 1 },
          "state": { "gainDb": 40, "mute": false },
          "mixer": {
            "mixes": [ { "id": "monitor", "name": "Monitor A", "volume": 1, "muted": false, "kind": "monitor" } ],
            "channels": [
              { "id": "xlr1", "name": "XLR 1", "levels": { "monitor": 1 }, "mutedIn": [], "hardware": true, "microphone": true,
                "compressor": { "enabled": false, "threshold": -18, "ratio": 3, "attack": 5, "release": 150, "knee": 6, "makeup": 0,
                                "available": true, "error": null },
                "expander": { "enabled": false, "threshold": -50, "ratio": 3, "range": 24, "attack": 1, "release": 150, "hold": 50,
                              "available": true, "error": null } },
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

    private static string Edit(string processor, Action<JsonNode> change)
    {
        JsonNode root = JsonNode.Parse(Json)!;
        change(root["mixer"]!["channels"]![0]![processor]!);
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
        for (int i = 0; i < 120; i++)
        {
            string? focused = Draw(app).FirstOrDefault(line => line.IndexOf(label, StringComparison.Ordinal) is int at and >= 2 && line[at - 2] == '┃');
            if (focused is not null) return focused[(focused.IndexOf(label, StringComparison.Ordinal) - 2)..];
            app.Handle(new KeyPress(Key.Down));
        }
        throw new Xunit.Sdk.XunitException($"No row named {label} on the Inputs tab.");
    }

    [Fact]
    public void TheStatusSaysWhetherItRunsAndOnlySwitchesAndThresholdsShowWhileOff()
    {
        ChannelEntry xlr1 = Snapshot.Parse(Json)!.Mixer.Channels[0];
        Assert.Equal("off", xlr1.Compressor!.Status);
        Assert.Equal(24, xlr1.Expander!.Range);
        Assert.Equal("on, on this computer",
            Snapshot.Parse(Edit("compressor", c => c["enabled"] = true))!.Mixer.Channels[0].Compressor!.Status);
        Assert.Null(Snapshot.Parse(Json)!.Mixer.Channels[1].Compressor);

        string all = string.Join('\n', Draw(Ready(Json).App));
        Assert.Contains("Dynamics on this computer", all, StringComparison.Ordinal);
        Select(Ready(Json).App, "XLR 1 exp threshold");
        Select(Ready(Json).App, "XLR 1 comp threshold");
        Assert.DoesNotContain("XLR 1 comp ratio", all, StringComparison.Ordinal);
        Assert.DoesNotContain("Music compressor", all, StringComparison.Ordinal);
    }

    [Fact]
    public void EachKeySendsItsOneFieldSnapped()
    {
        (App app, List<string> sent) = Ready(Json);
        Select(app, "XLR 1 comp on");
        app.Handle(new KeyPress(Key.Enter));
        JsonElement command = JsonDocument.Parse(Assert.Single(sent)).RootElement;
        Assert.Equal("setCompressor", command.GetProperty("cmd").GetString());
        Assert.Equal("xlr1", command.GetProperty("channel").GetString());
        Assert.True(command.GetProperty("enabled").GetBoolean());

        // On, the other settings show; the threshold steps by two decibels.
        sent.Clear();
        (app, sent) = Ready(Edit("compressor", c => c["enabled"] = true));
        Select(app, "XLR 1 comp threshold");
        app.Handle(new KeyPress(Key.Right));
        command = JsonDocument.Parse(Assert.Single(sent)).RootElement;
        Assert.Equal(-16, command.GetProperty("threshold").GetDouble());
        Assert.False(command.TryGetProperty("enabled", out _));
        Assert.Contains("3:1", Select(app, "XLR 1 comp ratio"), StringComparison.Ordinal);
        Assert.Contains("6 dB", Select(app, "XLR 1 comp knee"), StringComparison.Ordinal);
        sent.Clear();
        Select(app, "XLR 1 comp make-up");
        app.Handle(new KeyPress(Key.Right));
        command = JsonDocument.Parse(Assert.Single(sent)).RootElement;
        Assert.Equal("setCompressor", command.GetProperty("cmd").GetString());
        Assert.Equal(1, command.GetProperty("makeup").GetDouble());

        // The expander's hold, on the same pattern.
        (app, sent) = Ready(Edit("expander", e => e["enabled"] = true));
        Assert.Contains("50 ms", Select(app, "XLR 1 exp hold"), StringComparison.Ordinal);
        app.Handle(new KeyPress(Key.Left));
        command = JsonDocument.Parse(Assert.Single(sent)).RootElement;
        Assert.Equal("setExpander", command.GetProperty("cmd").GetString());
        Assert.InRange(command.GetProperty("hold").GetDouble(), 0, 49);
    }

    [Fact]
    public void AProcessorThatCannotRunShowsOnlyWhy()
    {
        string[] frame = Draw(Ready(Edit("expander", e => { e["available"] = false; e["error"] = "needs the native plugin host"; })).App);
        Assert.Contains(frame, line => line.Contains("XLR 1 expander", StringComparison.Ordinal)
            && line.Contains("needs the native plugin host", StringComparison.Ordinal));
        Assert.DoesNotContain(frame, line => line.Contains("XLR 1 exp threshold", StringComparison.Ordinal));
    }
}
