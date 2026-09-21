using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public void FailedLayoutDeletionStillRetriesAMuteRejectedByPipeWire()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        mixer.Build(new MixerConfig
        {
            Channels = [new("game", "Game"), new("music", "Music")],
            Mixes = [new("monitor", "Monitor", MixKind.Monitor), new("chat", "Chat", MixKind.VirtualMic)],
        });
        string directory = Directory.CreateTempSubdirectory("openxlr-layout-rollback-").FullName;
        string? path = Environment.GetEnvironmentVariable("PATH");
        string realPactl = (path ?? "").Split(Path.PathSeparator)
            .Select(p => Path.GetFullPath(Path.Combine(p, "pactl"))).First(File.Exists);
        try
        {
            ExecutableScript.Write(Path.Combine(directory, "pactl"), $$"""
                #!/bin/sh
                exec python3 - "$@" <<'PY'
                import os, sys
                args = sys.argv[1:]
                if args and args[0] == 'set-sink-input-mute':
                    sys.exit(1)
                os.execv({{JsonSerializer.Serialize(realPactl)}}, ['pactl', *args])
                PY
                """);
            foreach (bool removeMix in new[] { false, true })
            {
                mixer.SetChannelMuted("game", "chat", false);
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    mixer.EnsureCellLevels();
                    return ReadMute() == false;
                }, TimeSpan.FromSeconds(5)));
                try
                {
                    Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + path);
                    mixer.SetChannelMuted("game", "chat", true);
                }
                finally { Environment.SetEnvironmentVariable("PATH", path); }
                Assert.False(ReadMute());
                Assert.Throws<IOException>(() =>
                {
                    if (removeMix) mixer.DeleteVirtualMix("chat", _ => "disk full");
                    else mixer.DeleteApplicationChannel("game", _ => "disk full");
                });
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    mixer.EnsureCellLevels();
                    return ReadMute() == true;
                }, TimeSpan.FromSeconds(5)), "The pending mute must reach PipeWire after deletion rolls back.");
            }
        }
        finally { Directory.Delete(directory, true); }

        bool? ReadMute()
        {
            string[] modules = pw.Run("pactl", "list", "short", "modules").Split('\n');
            uint module = uint.Parse(modules.Single(line => line.Contains("sink_name=OpenXLR_ch_game ", StringComparison.Ordinal)
                || line.Contains("sink_name=OpenXLR_bus_game ", StringComparison.Ordinal)).Split('\t')[0]);
            if (!pw.FindCombineLegs(module).TryGetValue("OpenXLR_mix_chat", out int index)) return null;
            using JsonDocument inputs = JsonDocument.Parse(pw.Run("pactl", "--format=json", "list", "sink-inputs"));
            foreach (var input in inputs.RootElement.EnumerateArray())
                if (input.GetProperty("index").GetInt32() == index) return input.GetProperty("mute").GetBoolean();
            return null;
        }
    }
}
