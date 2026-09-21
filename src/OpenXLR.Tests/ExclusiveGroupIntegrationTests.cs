using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public void ExclusiveGroupsSwitchRealAudioIndependentlyAndSurviveRestart()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        var mixes = new[] { new MixDefinition("monitor", "Monitor A", MixKind.Monitor), new("monitor2", "Monitor B", MixKind.Monitor) };
        mixer.Build(new MixerConfig
        {
            Mixes = mixes,
            Channels = new[] { "test", "other" }.Select(id => new ChannelDefinition(id, id)
            { Levels = mixes.ToDictionary(m => m.Id, _ => 1.0), MutedIn = mixes.Select(m => m.Id).ToHashSet() }).ToArray(),
        });
        mixer.SetExclusiveGroup(null, "Inputs", ["test", "other"], _ => null);
        mixer.SetChannelMuted("test", "monitor", false);
        mixer.SetChannelMuted("other", "monitor2", false);
        mixer.EnsureCellLevels();
        Capture(.1, false, "OpenXLR_mix_monitor");
        Capture(0, false, "OpenXLR_mix_monitor2");
        mixer.SetChannelMuted("other", "monitor", false);
        Capture(0, false, "OpenXLR_mix_monitor");
        mixer.CycleExclusiveGroup("inputs", "monitor2");
        Capture(.1, false, "OpenXLR_mix_monitor2");
        Assert.True(mixer.IsChannelMutedIn("test", "monitor"));
        var saved = mixer.ExportSettings();
        mixer.Build(MixerConfig.FromSettings(saved));
        mixer.ApplySettings(saved);
        mixer.EnsureCellLevels();
        Assert.Single(mixer.Snapshot().ExclusiveGroups);
        Capture(0, false, "OpenXLR_mix_monitor");
        Capture(.1, false, "OpenXLR_mix_monitor2");
        Assert.Throws<IOException>(() => mixer.DeleteExclusiveGroup("inputs", _ => "disk full"));
        Assert.Single(mixer.Snapshot().ExclusiveGroups);
        mixer.DeleteApplicationChannel("other", _ => null);
        Assert.Empty(mixer.Snapshot().ExclusiveGroups);
        Capture(.1, false, "OpenXLR_mix_monitor2");
    }
}
