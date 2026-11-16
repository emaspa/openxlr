using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// Linked sends in the settings file and a profile. The hub's side, the
/// command, the fader and undo, is in <see cref="HistoryHubTests"/>; the
/// audio and a mix added while linked are measured on a private PipeWire
/// server in <see cref="MonitorVolumeIntegrationTests"/>.
/// </summary>
public sealed class LinkedSendsTests
{
    [Fact]
    public void TheSettingsFileKeepsTheLinksAndDropsAStrayEntry()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-linked-").FullName;
        try
        {
            string path = Path.Combine(dir, "mixer.json");
            Assert.Null(new MixerSettings { LinkedSends = ["game", "xlr1"] }.Save(path));
            Assert.Equal(["game", "xlr1"], JsonNode.Parse(File.ReadAllText(path))!["linkedSends"]!.AsArray().Select(n => n!.GetValue<string>()));
            Assert.Equal(["game", "xlr1"], MixerSettings.Load(path)!.LinkedSends);

            // A file written before linked sends links nothing.
            File.WriteAllText(path, """{"levels":{"game|monitor":0.5}}""");
            MixerSettings old = MixerSettings.Load(path, out string? warning)!;
            Assert.Null(warning);
            Assert.Empty(old.LinkedSends);

            // A hand edit's stray entry goes on its own; the rest stays.
            File.WriteAllText(path, """{"linkedSends":["game",3,null],"levels":{"game|monitor":0.5}}""");
            MixerSettings edited = MixerSettings.Load(path, out warning)!;
            Assert.Equal(["game"], edited.LinkedSends);
            Assert.Contains("linkedSends", warning);
            Assert.Equal(0.5, edited.Levels["game|monitor"]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void TheMixerSavesItsLinksAndTakesThemBackForChannelsItHas()
    {
        using var mixer = new Mixer();
        Set(mixer, "_config", MixerConfig.Default());
        Assert.Empty(mixer.ExportSettings().LinkedSends);

        // What ApplySettings takes from the file, before the graph is built
        // as at start, and again once it is: the links are the layout's.
        typeof(Mixer).GetMethod("RestoreLinkedSendsLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(mixer, [new List<string> { "system", "xlr1", "gone" }]);
        Assert.True(mixer.SendsLinked("system"));
        Assert.True(mixer.SendsLinked("xlr1"));
        Assert.False(mixer.SendsLinked("gone"));
        Assert.False(mixer.SendsLinked("game"));
        Assert.Equal(["system", "xlr1"], mixer.ExportSettings().LinkedSends);

        mixer.SetLinkedSends("system", false);
        Assert.Equal(["xlr1"], mixer.ExportSettings().LinkedSends);
        Assert.Throws<ArgumentException>(() => mixer.SetLinkedSends("gone", true));

        // A profile names every channel, linked or not.
        Dictionary<string, bool> scene = mixer.ExportScene().LinkedSends!;
        Assert.Equal(MixerConfig.Default().Channels.Select(c => c.Id).Order(), scene.Keys.Order());
        Assert.True(scene["xlr1"]);
        Assert.False(scene["system"]);
    }

    [Fact]
    public void AProfileCarriesTheLinksAndAnOlderOneCarriesNone()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        Assert.Null(JsonSerializer.Deserialize<MixerScene>("""{"levels":{"game|monitor":0.5}}""", options)!.LinkedSends);
        string json = JsonSerializer.Serialize(new MixerScene { LinkedSends = new() { ["game"] = true, ["music"] = false } }, options);
        Assert.True(JsonNode.Parse(json)!["linkedSends"]!["game"]!.GetValue<bool>());
        Assert.False(JsonSerializer.Deserialize<MixerScene>(json, options)!.LinkedSends!["music"]);
    }

    [Fact]
    public void TheStateReportsTheLinkOnEveryChannel()
    {
        using var mixer = new Mixer();
        Set(mixer, "_config", MixerConfig.Default());
        mixer.SetLinkedSends("game", true);
        string state = JsonSerializer.Serialize(mixer.Snapshot(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        JsonArray channels = JsonNode.Parse(state)!["channels"]!.AsArray();
        Assert.All(channels, c => Assert.NotNull(c!["linkedSends"]));
        Assert.True(channels.Single(c => c!["id"]!.GetValue<string>() == "game")!["linkedSends"]!.GetValue<bool>());
        Assert.False(channels.Single(c => c!["id"]!.GetValue<string>() == "music")!["linkedSends"]!.GetValue<bool>());
    }

    private static void Set(Mixer mixer, string name, object value)
        => typeof(Mixer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(mixer, value);
}
