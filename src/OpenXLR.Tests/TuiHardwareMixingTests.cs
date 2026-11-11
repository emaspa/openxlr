using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.Tui;

namespace OpenXLR.Tests;

/// <summary>
/// Hardware mixing in the terminal mixer, as the UI's Options offer it: the
/// choice shows on a Wave XLR Pro only; choosing the unit asks the daemon
/// for the dry run and lists what it says before the one row that switches;
/// a layout over the pair budget has no such row; back to this computer is
/// one command. While the mode runs, a mix chain it turns off says why and
/// takes no plugin, and an audition is refused here with the daemon's words.
/// </summary>
public sealed class TuiHardwareMixingTests
{
    private const int OptionsTab = 7;

    private const string ProJson = """
        {
          "type": "state",
          "daemonVersion": "0.5.1",
          "connected": true,
          "device": { "vendor": "Elgato", "model": "Wave XLR Pro", "usbId": "0fd9:00b4" },
          "capabilities": { "gain": true, "mute": true, "xlrInputs": 2, "hpOutputs": 2, "outputRouting": true, "hardwareMixing": true },
          "state": { "gainDb": 40, "mute": false },
          "mixer": {
            "mixes": [
              { "id": "monitor", "name": "Monitor A", "volume": 1, "muted": false, "kind": "monitor" },
              { "id": "stream", "name": "Stream", "volume": 1, "muted": false, "kind": "virtualMic" }
            ],
            "channels": [
              { "id": "xlr1", "name": "XLR 1", "hardware": true, "microphone": true, "levels": {}, "mutedIn": [], "chainWidth": 1 },
              { "id": "game", "name": "Game", "levels": {}, "mutedIn": [], "volume": 1 }
            ],
            "monitorOutputs": [],
            "inserts": {},
            "hardwareMixing": { "wanted": false, "active": false, "reason": null, "note": null }
          },
          "devices": [],
          "profiles": []
        }
        """;

    private const string PlanJson = """
        {
          "type": "mixingPlan", "wanted": true, "active": true, "reason": null, "note": null,
          "pairs": { "game": 1 }, "sections": {}, "jacks": {}, "cells": {}, "clamped": [],
          "disabled": { "soundCheck": "Mic setup's checks are not available while the Wave XLR Pro builds the mixes." },
          "entryOffsetsDb": { "hp1": 21, "hp2": 28.94, "lineout": 21 }
        }
        """;

    private static string Edit(Action<JsonNode> change)
    {
        JsonNode root = JsonNode.Parse(ProJson)!;
        change(root);
        return root.ToJsonString();
    }

    private static (App App, List<string> Sent) Ready(string json, int tab = OptionsTab)
    {
        DaemonLink link = new();
        List<string> sent = [];
        link.Sent += sent.Add;
        link.Receive(json);
        App app = new(link, Theme.Material);
        app.ShowTab(tab);
        app.Draw(new Screen(160, 80));
        return (app, sent);
    }

    private static string[] Draw(App app)
    {
        Screen screen = new(160, 80);
        app.Draw(screen);
        return [.. Enumerable.Range(0, screen.Height)
            .Select(y => new string(Enumerable.Range(0, screen.Width).Select(x => screen.At(x, y).Ch).ToArray()))];
    }

    private static bool Shows(App app, string text) => Draw(app).Any(line => line.Contains(text, StringComparison.Ordinal));

    private static void Select(App app, string label)
    {
        for (int i = 0; i < 20; i++) app.Handle(new KeyPress(Key.PageUp));
        for (int i = 0; i < 80; i++)
        {
            if (Draw(app).Any(line => line.IndexOf(label, StringComparison.Ordinal) is int at and >= 2 && line[at - 2] == '┃')) return;
            app.Handle(new KeyPress(Key.Down));
        }
        throw new Xunit.Sdk.XunitException($"No row named {label}.");
    }

    private static JsonElement Command(List<string> sent)
    {
        JsonElement command = JsonDocument.Parse(Assert.Single(sent)).RootElement;
        sent.Clear();
        return command;
    }

    [Fact]
    public void TheUnitIsChosenOnlyAfterTheDryRunAndBackToTheComputerIsOneCommand()
    {
        (App app, List<string> sent) = Ready(ProJson);
        Select(app, "Pro output mixes");
        Assert.True(Shows(app, "This computer"));

        // Choosing the unit sends the dry run, nothing that switches.
        app.Handle(new KeyPress(Key.Right));
        JsonElement check = Command(sent);
        Assert.Equal("setMixing", check.GetProperty("cmd").GetString());
        Assert.Equal("hardware", check.GetProperty("value").GetString());
        Assert.True(check.GetProperty("check").GetBoolean());
        Assert.True(Shows(app, "asking the daemon"));

        app.Link.Receive(PlanJson);
        Assert.True(Shows(app, "Mic setup's checks are not available"));
        Assert.True(Shows(app, "turned down 28.9 dB first"));
        Select(app, "Mix on the unit");
        app.Handle(new KeyPress(Key.Enter));
        JsonElement on = Command(sent);
        Assert.Equal("hardware", on.GetProperty("value").GetString());
        Assert.False(on.TryGetProperty("check", out _));

        // Wanted and running: the badge's words, and one command back.
        app.Link.Receive(Edit(root => root["mixer"]!["hardwareMixing"] = new JsonObject { ["wanted"] = true, ["active"] = true }));
        Assert.True(Shows(app, "the Wave XLR Pro builds the mixes its jacks play"));
        Assert.False(Shows(app, " switch "));
        Select(app, "Pro output mixes");
        app.Handle(new KeyPress(Key.Left));
        JsonElement off = Command(sent);
        Assert.Equal("software", off.GetProperty("value").GetString());
    }

    [Fact]
    public void ALayoutOverTheBudgetSaysWhyAndCannotSwitch()
    {
        (App app, List<string> sent) = Ready(ProJson);
        Select(app, "Pro output mixes");
        app.Handle(new KeyPress(Key.Right));
        sent.Clear();
        app.Link.Receive("""
            { "type": "mixingPlan", "wanted": true, "active": false, "reason": "budget",
              "note": "Hardware mixing on the Wave XLR Pro streams at most 7 channels; this layout has 8 that need a USB pair.",
              "disabled": {}, "entryOffsetsDb": {} }
            """);
        Assert.True(Shows(app, "streams at most 7 channels"));
        Assert.False(Shows(app, " switch "));
    }

    [Fact]
    public void TheChoiceIsOnlyOfferedOnAUnitThatCanRunIt()
    {
        (App app, _) = Ready(Edit(root =>
        {
            root["capabilities"]!.AsObject().Remove("hardwareMixing");
            root["mixer"]!.AsObject().Remove("hardwareMixing");
        }));
        Assert.False(Shows(app, "Pro output mixes"));

        // Wanted with no such unit attached: shown, so it can be switched back.
        (App waiting, _) = Ready(Edit(root =>
        {
            root["capabilities"]!.AsObject().Remove("hardwareMixing");
            root["mixer"]!["hardwareMixing"] = new JsonObject
            {
                ["wanted"] = true, ["active"] = false, ["reason"] = "unitAway", ["note"] = "The Wave XLR Pro is not connected.",
            };
        }));
        Assert.True(Shows(waiting, "Pro output mixes"));
        Assert.True(Shows(waiting, "The Wave XLR Pro is not connected."));
    }

    [Fact]
    public void TheMaximizerOfEachMixTheUnitBuildsIsSetOnTheOutputsTab()
    {
        const string why = "The Wave XLR Pro builds Stream summed with another mix, so it runs no maximizer on it. Give a jack Stream alone to use one.";
        (App app, List<string> sent) = Ready(Edit(root =>
        {
            root["mixer"]!["mixes"]![0]!["hardwareSection"] = 0;
            root["mixer"]!["mixes"]![0]!["maximizer"] = 40;
            root["mixer"]!["mixes"]![1]!["hardwareSection"] = 18;
            root["mixer"]!["hardwareMixing"] = new JsonObject
            {
                ["wanted"] = true, ["active"] = true,
                ["disabled"] = new JsonObject { ["maximizer:stream"] = why },
            };
        }), tab: 3);
        Select(app, "Monitor A maximizer");
        Assert.Contains(Draw(app), line => line.Contains("maximizer on the unit", StringComparison.OrdinalIgnoreCase));
        app.Handle(new KeyPress(Key.Right));
        JsonElement command = Command(sent);
        Assert.Equal("setMixMaximizer", command.GetProperty("cmd").GetString());
        Assert.Equal("monitor", command.GetProperty("mix").GetString());
        Assert.Equal(45, command.GetProperty("value").GetInt32());
        // The reduction the unit reports now, beside the amount.
        app.Link.Receive("""{"type":"meters","levels":{"max:monitor":[6.5]}}""");
        Assert.True(Shows(app, "-6.5 dB now"));
        // A mix the unit builds only summed says why and takes no amount.
        Select(app, "Stream maximizer");
        Assert.True(Shows(app, why[..60]));
        app.Handle(new KeyPress(Key.Right));
        Assert.Empty(sent);
    }

    [Fact]
    public void TheMaximizerIsNotOfferedWhileTheComputerBuildsTheMixes()
    {
        (App app, _) = Ready(Edit(root => root["mixer"]!["mixes"]![0]!["maximizer"] = 40), tab: 3);
        for (int i = 0; i < 20; i++) app.Handle(new KeyPress(Key.Down));
        Assert.DoesNotContain(Draw(app), line => line.Contains("maximizer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AChainTheModeTurnsOffSaysWhyAndTakesNoPlugin()
    {
        const string why = "The Wave XLR Pro builds Monitor A for its jacks, so no insert runs on it there.";
        (App app, _) = Ready(Edit(root =>
        {
            root["mixer"]!["hardwareMixing"] = new JsonObject
            {
                ["wanted"] = true, ["active"] = true,
                ["disabled"] = new JsonObject { ["inserts:mix:monitor"] = why },
            };
        }), tab: 5);
        // The chain list starts at XLR 1; the next chains are the mixes.
        for (int i = 0; i < 2 && !Shows(app, why); i++) app.Handle(new KeyPress(Key.Right));
        Assert.True(Shows(app, why));
        app.Handle(new KeyPress(Key.Char, 'a'));
        // No picker opened: its filter prompt is not drawn.
        Assert.False(Shows(app, "Filter"));
    }
}
