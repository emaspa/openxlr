using System.Text.Json;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class MixerLayoutSettingsTests
{
    [Fact]
    public void OldSettingsRetainEveryDefaultNodeAndHardwareMute()
    {
        var expected = MixerConfig.Default();
        var actual = MixerConfig.FromSettings(new MixerSettings());
        Assert.Equal(expected.Mixes, actual.Mixes);
        Assert.Equal(expected.Channels.Select(c => (c.Id, c.Name, c.InputPair)),
            actual.Channels.Select(c => (c.Id, c.Name, c.InputPair)));
        foreach (var c in expected.Channels)
        {
            var restored = actual.Channels.Single(a => a.Id == c.Id);
            Assert.Equal(c.Levels, restored.Levels);
            Assert.True(c.MutedIn.SetEquals(restored.MutedIn));
        }
    }

    [Fact]
    public void CustomLayoutPreservesBothMonitorsAuxAndHardware()
    {
        var config = MixerConfig.FromSettings(new MixerSettings
        {
            UserChannels = [new("podcast", "Interview"), new("music", "My music")],
            UserMixes = [new("recording", "Archive")],
        });
        Assert.Equal(["monitor", "monitor2", "recording", "auxout"], config.Mixes.Select(m => m.Id));
        Assert.Equal(["xlr1", "xlr2", "aux", "podcast", "music"], config.Channels.Select(c => c.Id));
        Assert.Equal("OpenXLR_ch_podcast", config.Channels[3].SinkName);
        Assert.Equal("Interview", config.Channels[3].Name);
        Assert.All(config.Channels, c => Assert.Equal(config.Mixes.Select(m => m.Id), c.Levels.Keys));
        Assert.Contains("auxout", config.Channels[2].MutedIn);
        Assert.Equal(0, config.Channels[2].Levels["auxout"]);
    }

    [Fact]
    public void CorruptEntriesCannotReplaceStructuralNodesOrCreateIgnoreSink()
    {
        var settings = JsonSerializer.Deserialize<MixerSettings>("""
            {"UserChannels":[null,{"Id":null,"Name":"X"},{"Id":"ignore","Name":"X"},
              {"Id":"xlr1","Name":"X"},{"Id":"bad|id","Name":"X"},
              {"Id":"ok","Name":"Good"},{"Id":"ok","Name":"Duplicate"},
              {"Id":"bad","Name":"Line\nbreak"}],
             "UserMixes":[{"Id":"monitor2","Name":"Bad"},{"Id":"auxout","Name":"Bad"},null]}
            """)!;
        var config = MixerConfig.FromSettings(settings);
        Assert.Equal(["xlr1", "xlr2", "aux", "ok"], config.Channels.Select(c => c.Id));
        Assert.Equal(["monitor", "monitor2", "auxout"], config.Mixes.Select(m => m.Id));
        Assert.Equal("Monitor B", config.Mixes[1].Name);
    }

    [Fact]
    public void GraphSizeIsBoundedAndEmptyApplicationsHaveSafeFallback()
    {
        var config = MixerConfig.FromSettings(new MixerSettings
        {
            UserChannels = Enumerable.Range(0, 100).Select(i => new UserChannelDefinition($"c{i}", "C")).ToList(),
            UserMixes = Enumerable.Range(0, 100).Select(i => new UserMixDefinition($"m{i}", "M")).ToList(),
        });
        Assert.Equal(MixerConfig.MaxApplicationChannels, config.Channels.Count(c => c.InputPair is null));
        Assert.Equal(MixerConfig.MaxVirtualMixes, config.Mixes.Count(m => m.Kind == MixKind.VirtualMic));
        var empty = MixerConfig.FromSettings(new MixerSettings { UserChannels = [], UserMixes = [] });
        Assert.Equal("system", Assert.Single(empty.Channels, c => c.InputPair is null).Id);
        Assert.DoesNotContain(empty.Mixes, m => m.Kind == MixKind.VirtualMic);
    }

    [Fact]
    public void MissingAppRulesCannotRouteIntoHardwareAndIgnoreSurvives()
    {
        var config = MixerConfig.FromSettings(new MixerSettings { UserChannels = [new("podcast", "Podcast")] });
        Assert.Equal("podcast", config.ResolveApplicationChannel("game"));
        Assert.Equal("podcast", config.ResolveApplicationChannel("xlr1"));
        Assert.Equal("podcast", config.ResolveApplicationChannel("podcast"));
        Assert.Equal("ignore", config.ResolveApplicationChannel("ignore"));
    }

    [Fact]
    public void RoundTripPreservesOrderNamesAndMonitorFeeds()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-layout-").FullName;
        string path = Path.Combine(dir, "mixer.json");
        try
        {
            var settings = new MixerSettings
            {
                UserChannels = [new("second", "Second"), new("first", "First")],
                UserMixes = [], MonitorFeeds = new() { ["headset"] = "monitor+monitor2" },
            };
            Assert.Null(settings.Save(path));
            var loaded = Assert.IsType<MixerSettings>(MixerSettings.Load(path));
            Assert.Equal(settings.UserChannels, loaded.UserChannels);
            Assert.Empty(loaded.UserMixes!);
            Assert.Equal(settings.MonitorFeeds, loaded.MonitorFeeds);
        }
        finally { Directory.Delete(dir, true); }
    }
}
