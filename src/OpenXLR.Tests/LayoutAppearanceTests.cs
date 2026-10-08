using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using OpenXLR.Tui;
using App = OpenXLR.Tui.App;
using OpenXLR.UI;

namespace OpenXLR.Tests;

/// <summary>
/// Channel and mix appearance: what the daemon accepts and saves, that it
/// never touches routing, how the window and the terminal mixer draw it, and
/// the fallback channel that no longer depends on the layout order.
/// </summary>
[Collection("xdg-config")]
public sealed class LayoutAppearanceTests
{
    [Theory]
    [InlineData("<svg/>", "#123456", false)]
    [InlineData("♪", "red", false)]
    [InlineData("♪", "#fff", false)]
    [InlineData("♪", "#ffffff\"", false)]
    [InlineData("♪", "#12345G", false)]
    [InlineData("♪", "#12abEF", true)]
    [InlineData("", null, true)]
    public void OnlyListedIconsAndHexColoursAreAccepted(string icon, string? colour, bool valid)
    {
        using var mixer = new Mixer();
        var value = new LayoutAppearance(icon, colour);
        Assert.Equal(valid, LayoutAppearance.IsValid(value));
        var command = new Command { Cmd = "setLayoutAppearance", Channel = "game", Appearance = value };
        Assert.Equal(valid, CommandValidation.Check(command, mixer, _ => null) is null);
    }

    [Fact]
    public void TheCommandNamesExactlyOneKnownChannelOrMixAndHidesOnlyChannels()
    {
        using var mixer = new Mixer();
        var command = new Command { Cmd = "setLayoutAppearance", Channel = "xlr1", Appearance = new("●", null, true) };
        Assert.Null(CommandValidation.Check(command, mixer, _ => null));
        Assert.Null(CommandValidation.Check(command with { Channel = null, Mix = "monitor", Appearance = new("●") }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(command with { Channel = null, Mix = "monitor" }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(command with { Mix = "monitor" }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(command with { Channel = null }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(command with { Channel = "gone" }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(command with { Appearance = null }, mixer, _ => null));
    }

    [Fact]
    public void TheWireFormatReadsIntoTheCommand()
    {
        var command = JsonSerializer.Deserialize<Command>(
            """{"cmd":"setLayoutAppearance","channel":"music","appearance":{"icon":"♫","colour":"#12ABCD","hidden":true}}""",
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
        Assert.Equal(new LayoutAppearance("♫", "#12ABCD", true), command.Appearance);
        var partial = JsonSerializer.Deserialize<Command>("""{"cmd":"setLayoutAppearance","mix":"stream","appearance":{"icon":"◆"}}""",
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
        Assert.Equal(new LayoutAppearance("◆"), partial.Appearance);
    }

    [Fact]
    public void AppearanceIsSavedTransactionallyAndLeavesTheLayoutAlone()
    {
        using var mixer = new Mixer();
        SetField(mixer, "_built", true);
        try
        {
            object config = GetField(mixer, "_config");
            string before = JsonSerializer.Serialize(mixer.ExportSettings());
            Assert.Throws<IOException>(() => mixer.SetLayoutAppearance("channel:game", new("♫", "#12ABCD", true), _ => "disk full"));
            Assert.Equal(before, JsonSerializer.Serialize(mixer.ExportSettings()));

            MixerSettings? saved = null;
            mixer.SetLayoutAppearance("channel:game", new("♫", "#12ABCD", true), settings => { saved = settings; return null; });
            mixer.SetLayoutAppearance("mix:stream", new("◆", "#ABC123"), _ => null);
            Assert.Equal(new LayoutAppearance("♫", "#12ABCD", true), saved!.Appearance["channel:game"]);
            Assert.Same(config, GetField(mixer, "_config"));

            MixerState state = mixer.Snapshot();
            Assert.Equal(MixerConfig.Default().Channels.Select(c => c.Id), state.Channels.Select(c => c.Id));
            Assert.True(state.Channels.Single(c => c.Id == "game").Appearance.Hidden);
            Assert.Equal("◆", state.Mixes.Single(m => m.Id == "stream").Appearance.Icon);
            Assert.Equal(LayoutAppearance.Default, state.Channels.Single(c => c.Id == "music").Appearance);

            Assert.Throws<InvalidOperationException>(() => mixer.SetLayoutAppearance("mix:monitor", new(Hidden: true), _ => null));
            Assert.Throws<InvalidOperationException>(() => mixer.SetLayoutAppearance("channel:gone", new("●"), _ => null));

            // Back to the default leaves nothing behind in the file.
            mixer.SetLayoutAppearance("mix:stream", LayoutAppearance.Default, _ => null);
            Assert.False(mixer.ExportSettings().Appearance.ContainsKey("mix:stream"));
        }
        finally { SetField(mixer, "_built", false); }
    }

    [Fact]
    public void AHandEditedEntryIsDroppedWithoutLosingTheRest()
    {
        var saved = new MixerSettings
        {
            Appearance = new()
            {
                ["channel:music"] = new("♫", "#123456"),
                ["channel:game"] = new("<image/>"),
                ["mix:monitor"] = new(Hidden: true),
                ["mix:chat"] = null!,
                ["stream"] = new("●"),
            },
        };
        MixerSettings sanitized = SavedMixerValidation.Sanitize(saved, out IReadOnlyList<string> dropped);
        Assert.Equal("channel:music", Assert.Single(sanitized.Appearance).Key);
        Assert.Contains(dropped, note => note.StartsWith("appearance:", StringComparison.Ordinal));
    }

    [Fact]
    public void TheFallbackChannelIsSystemOrTheFirstByIdWhateverTheOrder()
    {
        var config = MixerConfig.Default();
        string[] apps = [.. config.Channels.Where(c => c.InputPair is null).Select(c => c.Id)];
        Assert.Equal("system", config.FallbackApplicationChannel!.Id);
        Assert.Equal("system", config.WithOrder([.. apps.Reverse()], ["stream", "chat"]).ResolveApplicationChannel("gone"));

        var withoutSystem = config.WithoutChannel("system");
        string[] rest = [.. apps.Where(id => id != "system")];
        Assert.Equal("browser", withoutSystem.ResolveApplicationChannel("gone"));
        Assert.Equal("browser", withoutSystem.WithOrder([.. rest.Reverse()], ["stream", "chat"]).ResolveApplicationChannel("gone"));
        Assert.Equal("browser", withoutSystem.WithOrder(["sfx", .. rest.Where(id => id != "sfx")], ["stream", "chat"])
            .FallbackApplicationChannel!.Id);
        Assert.Equal("ignore", withoutSystem.ResolveApplicationChannel("ignore"));
        Assert.Equal("browser", withoutSystem.ResolveApplicationChannel("xlr1"));
    }

    [Fact]
    public async Task TheWindowLeavesOutAHiddenStripButKeepsItsChannel()
    {
        await using var client = new DaemonClient();
        var model = new MainViewModel(client);
        Apply(model, new JsonObject { ["icon"] = "♫", ["colour"] = "#123456", ["hidden"] = true });
        ChannelViewModel game = model.Channels.Single(c => c.Id == "game");
        ChannelViewModel music = model.Channels.Single(c => c.Id == "music");
        Assert.False(game.DisplayVisible);
        Assert.True(game.Visible);
        Assert.True(music.DisplayVisible);
        Assert.Equal("♫", game.Appearance.Icon);
        Assert.True(game.Appearance.HasColour);
        Assert.Equal(0.42, Assert.Single(game.Sends).Level);
        Assert.Contains(game, model.Channels);

        Apply(model, null);
        Assert.True(game.DisplayVisible);
        Assert.False(game.Appearance.HasColour);
        Assert.Equal("", game.Appearance.Icon);
    }

    [Fact]
    public void TheTerminalDrawsTheIconAndColourAndSkipsAHiddenStrip()
    {
        DaemonLink link = new();
        link.Receive(TuiState("""{"icon":"♫","colour":"#12ABCD"}""", """{"hidden":true}"""));
        App app = new(link, Theme.Material);
        app.ShowTab(0);
        Screen screen = new(140, 36);
        app.Draw(screen);

        MixerSnapshot mixer = link.State!.Mixer;
        Assert.Equal(["music", "game", "sfx"], mixer.Shown.Select(c => c.Id));
        Assert.Equal(["music", "sfx"], mixer.Strips.Select(c => c.Id));
        (int x, int y) = Find(screen, "♫ Music");
        Assert.Equal(new Rgb(0x12, 0xAB, 0xCD), screen.At(x + 2, y).Fore);
        Assert.Equal((-1, -1), Find(screen, "Game"));
    }

    [Fact]
    public void ControlArrowsMoveAStripPastAHiddenNeighbour()
    {
        DaemonLink link = new();
        List<string> sent = [];
        link.Sent += json => sent.Add(json);
        link.Receive(TuiState("{}", """{"hidden":true}"""));
        App app = new(link, Theme.Material);
        app.ShowTab(0);
        app.Draw(new Screen(140, 36));

        app.Handle(new KeyPress(Key.Down));                  // Music, the first strip
        app.Handle(new KeyPress(Key.Right, Ctrl: true));
        JsonElement command = JsonDocument.Parse(Assert.Single(sent)).RootElement;
        Assert.Equal("setLayoutOrder", command.GetProperty("cmd").GetString());
        // Music passes the hidden Game and the strip beside it on screen.
        Assert.Equal(["game", "sfx", "music"], command.GetProperty("channels").EnumerateArray().Select(e => e.GetString()));

        sent.Clear();
        app.Handle(new KeyPress(Key.Up));
        app.Handle(new KeyPress(Key.Up));                    // back to the masters row, no channel
        app.Handle(new KeyPress(Key.Down));
        app.Handle(new KeyPress(Key.Left, Ctrl: true));      // the first strip has nowhere to go
        Assert.Empty(sent);
    }

    private static string TuiState(string music, string game) => $$"""
        {
          "type": "state", "connected": true,
          "mixer": {
            "mixes": [ { "id": "monitor", "name": "Monitor A", "volume": 1, "kind": "monitor" } ],
            "channels": [
              { "id": "music", "name": "Music", "levels": { "monitor": 1 }, "mutedIn": [], "appearance": {{music}} },
              { "id": "game", "name": "Game", "levels": { "monitor": 1 }, "mutedIn": [], "appearance": {{game}} },
              { "id": "sfx", "name": "SFX", "levels": { "monitor": 1 }, "mutedIn": [] }
            ]
          }
        }
        """;

    private static (int X, int Y) Find(Screen screen, string text)
    {
        for (int y = 0; y < screen.Height; y++)
        {
            string row = new([.. Enumerable.Range(0, screen.Width).Select(x => screen.At(x, y).Ch)]);
            int x = row.IndexOf(text, StringComparison.Ordinal);
            if (x >= 0) return (x, y);
        }
        return (-1, -1);
    }

    private static void Apply(MainViewModel model, JsonNode? gameAppearance)
    {
        var state = new JsonObject
        {
            ["mixes"] = new JsonArray(new JsonObject { ["id"] = "monitor", ["name"] = "Monitor A" }),
            ["channels"] = new JsonArray([.. new[] { "game", "music" }.Select(id => (JsonNode)new JsonObject
            {
                ["id"] = id, ["name"] = id, ["levels"] = new JsonObject { ["monitor"] = 0.42 },
                ["appearance"] = id == "game" ? gameAppearance?.DeepClone() : null,
            })]),
        };
        typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, [state]);
    }

    private static object GetField(Mixer mixer, string name) =>
        typeof(Mixer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mixer)!;

    private static void SetField(Mixer mixer, string name, object value) =>
        typeof(Mixer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mixer, value);
}
