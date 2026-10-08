using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;
using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class LayoutAppearanceTests
{
    [Theory]
    [InlineData("<svg/>", "#123456", false)]
    [InlineData("♪", "red", false)]
    [InlineData("♪", "#ffffff\"", false)]
    [InlineData("♪", "#123456", true)]
    [InlineData("", null, true)]
    public void OnlyBoundedAppearanceDataReachesTheMixer(string icon, string? colour, bool valid)
    {
        using var mixer = new Mixer();
        var value = new LayoutAppearance(icon, colour);
        Assert.Equal(valid, LayoutAppearance.IsValid(value));
        var command = new Command { Cmd = "setLayoutAppearance", Channel = "game", Appearance = value };
        Assert.Equal(valid, CommandValidation.Check(command, mixer, _ => null) is null);
        Assert.NotNull(CommandValidation.Check(command with { Mix = "monitor" }, mixer, _ => null));
        Assert.NotNull(CommandValidation.Check(command with { Channel = "gone" }, mixer, _ => null));
    }

    [Fact]
    public void AppearanceIsTransactionalAndDoesNotChangeAudioConfiguration()
    {
        using var mixer = new Mixer();
        SetField(mixer, "_built", true);
        var original = (MixerConfig)typeof(Mixer).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mixer)!;
        string before = JsonSerializer.Serialize(mixer.ExportSettings());
        Assert.Throws<IOException>(() => mixer.SetLayoutAppearance("channel:game", new("♫", "#12ABCD", true), _ => "disk full"));
        Assert.Equal(before, JsonSerializer.Serialize(mixer.ExportSettings()));
        mixer.SetLayoutAppearance("channel:game", new("♫", "#12ABCD", true), _ => null);
        Assert.Same(original, typeof(Mixer).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mixer));
        Assert.True(mixer.Snapshot().Channels.Single(c => c.Id == "game").Appearance.Hidden);
        mixer.SetLayoutAppearance("channel:game", new("◆"), _ => null);
        Assert.Throws<InvalidOperationException>(() => mixer.SetLayoutAppearance("mix:monitor", new(Hidden: true), _ => null));
        SetField(mixer, "_built", false);
    }

    [Fact]
    public async Task HidingAChannelKeepsItsSendsAndStableIdentity()
    {
        await using var client = new DaemonClient();
        var model = new MainViewModel(client);
        Apply(model, "game", "music");
        var game = model.Channels.Single(c => c.Id == "game");
        Assert.False(game.DisplayVisible);
        Assert.Equal(0.42, Assert.Single(game.Sends).Level);
        Apply(model, "game", "music");
        Assert.Same(game, model.Channels.Single(c => c.Id == "game"));
        Assert.True(model.Channels.Single(c => c.Id == "music").DisplayVisible);
        Apply(model, "music");
        Assert.Single(model.Channels);
    }

    [Fact]
    public void MissingApplicationChannelsFallBackByIdAfterReordering()
    {
        MixerConfig config = MixerConfig.Default();
        string[] channels = config.Channels.Where(c => c.IsApplication).Select(c => c.Id).Reverse().ToArray();
        string[] mixes = config.Mixes.Where(m => m.Kind == MixKind.VirtualMic).Select(m => m.Id).ToArray();
        MixerConfig reordered = config.WithOrder(channels, mixes);
        Assert.Equal("browser", config.ResolveApplicationChannel("missing"));
        Assert.Equal("browser", reordered.ResolveApplicationChannel("missing"));
        Assert.Equal("music", reordered.ResolveApplicationChannel("music"));
        Assert.Equal("ignore", reordered.ResolveApplicationChannel("ignore"));
    }

    [Fact]
    public void InvalidSavedPresentationCannotSuppressHealthyEntries()
    {
        var saved = new MixerSettings { Appearance = new()
        {
            ["channel:music"] = new("♫", "#123456"),
            ["channel:game"] = new("<image/>"),
            ["mix:monitor"] = null!,
        }};
        var sanitized = SavedMixerValidation.Sanitize(saved, out var dropped);
        Assert.Single(sanitized.Appearance);
        Assert.NotEmpty(dropped);
    }

    private static void SetField(Mixer mixer, string name, object value) =>
        typeof(Mixer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mixer, value);

    private static void Apply(MainViewModel vm, params string[] channels)
    {
        var state = new JsonObject
        {
            ["mixes"] = new JsonArray(new JsonObject { ["id"] = "monitor", ["name"] = "Monitor A" }),
            ["channels"] = new JsonArray(channels.Select(id => (JsonNode)new JsonObject
            {
                ["id"] = id, ["name"] = id, ["levels"] = new JsonObject { ["monitor"] = 0.42 },
                ["appearance"] = new JsonObject { ["icon"] = "♫", ["colour"] = "#123456", ["hidden"] = id == "game" },
            }).ToArray()),
        };
        typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [state]);
    }
}
