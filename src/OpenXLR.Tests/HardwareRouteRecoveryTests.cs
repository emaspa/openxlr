using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public void HardwareInputRepairsAPartialFeedAndReturnsAfterReplug()
    {
        const string source = "test_Wave_XLR_capture";
        const string channel = "OpenXLR_ch_xlr1";
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        pw.CreateNullSink("test_capture_parent", "Input fixture");
        uint input = pw.CreateVirtualMic(source, "test_capture_parent.monitor", "Hardware input fixture");
        WaitForRecovery(() => pw.ListDevices().Any(d => d.Name == source));
        mixer.SetInputDeviceHint(source);
        RejectNextRouteLink(source + ":", channel + ":playback_FR", () =>
            mixer.Build(new MixerConfig { Channels = [new("xlr1", "XLR 1") { InputPair = 0 }],
                Mixes = [new("monitor", "Monitor", MixKind.Monitor)] }));
        WaitForRecovery(() => IncomingRouteLinks(channel).Length == 1);
        uint surviving = Assert.Single(IncomingRouteLinks(channel));
        WaitForRecovery(() => { mixer.EnsureInputFeeds(); return IncomingRouteLinks(channel).Length == 2; });
        Assert.Contains(surviving, IncomingRouteLinks(channel));
        Stable(() => mixer.EnsureInputFeeds(), channel, 2);

        pw.UnloadModule(input);
        WaitForRecovery(() => pw.FindNodeId(source) is null);
        mixer.EnsureInputFeeds();
        Assert.Empty(IncomingRouteLinks(channel));
        pw.CreateVirtualMic(source, "test_capture_parent.monitor", "Hardware input returned");
        WaitForRecovery(() => { mixer.EnsureInputFeeds(); return IncomingRouteLinks(channel).Length == 2; });
        Stable(() => mixer.EnsureInputFeeds(), channel, 2);
    }

    [MonitorPipeWireFact]
    public void AuxReturnRepairsAPartialFeedAndReturnsAfterReplug()
    {
        const string output = "test_Wave_XLR_aux";
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        uint? module = null;
        try
        {
            module = LoadAuxOutput();
            WaitForRecovery(() => pw.FindNodeId(output) is not null);
            mixer.SetInputDeviceHint(output, hardwareOutputRouting: true);
            mixer.Build(new MixerConfig { Channels = [], Mixes = [new("aux", "Aux", MixKind.AuxPort)] });
            WaitForRecovery(() => IncomingRouteLinks(output).Length == 2);
            Stable(() => mixer.EnsureAuxRoute(), output, 2);

            Assert.True(ProcessRunner.Run("pactl", ["unload-module", module.Value.ToString()]).Ok);
            module = null;
            WaitForRecovery(() => pw.FindNodeId(output) is null);
            mixer.EnsureAuxRoute();
            module = LoadAuxOutput();
            WaitForRecovery(() => { mixer.EnsureAuxRoute(); return IncomingRouteLinks(output).Length == 2; });
            Stable(() => mixer.EnsureAuxRoute(), output, 2);

            mixer.SetAuxPortEnabled(false);
            RejectNextRouteLink("OpenXLR_mix_aux:", output + ":playback_AUX11", () => mixer.SetAuxPortEnabled(true));
            WaitForRecovery(() => IncomingRouteLinks(output).Length == 1);
            WaitForRecovery(() => { mixer.EnsureAuxRoute(); return IncomingRouteLinks(output).Length == 2; });
            Stable(() => mixer.EnsureAuxRoute(), output, 2);
        }
        finally
        {
            if (module is uint id) ProcessRunner.Run("pactl", ["unload-module", id.ToString()]);
        }

        static uint LoadAuxOutput()
        {
            ProcessResult result = ProcessRunner.Run("pactl", ["load-module", "module-null-sink", "sink_name=" + output,
                "channels=12", "channel_map=" + string.Join(',', Enumerable.Range(0, 12).Select(i => "aux" + i))]);
            Assert.True(result.Ok, result.Stderr);
            return uint.Parse(result.StdoutText.Trim());
        }
    }

    private static void RejectNextRouteLink(string fromPrefix, string target, Action action)
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-rejected-route-").FullName;
        string? path = Environment.GetEnvironmentVariable("PATH");
        string realLink = (path ?? "").Split(Path.PathSeparator)
            .Select(p => Path.GetFullPath(Path.Combine(p, "pw-link"))).First(File.Exists);
        string rejected = Path.Combine(directory, "rejected");
        try
        {
            ExecutableScript.Write(Path.Combine(directory, "pw-link"), $$"""
                #!/bin/sh
                exec python3 - "$@" <<'PY'
                import os, pathlib, sys
                args = sys.argv[1:]
                rejected = pathlib.Path({{JsonSerializer.Serialize(rejected)}})
                if len(args) == 2 and args[0].startswith({{JsonSerializer.Serialize(fromPrefix)}}) and args[1] == {{JsonSerializer.Serialize(target)}} and not rejected.exists():
                    rejected.touch()
                    sys.exit(1)
                os.execv({{JsonSerializer.Serialize(realLink)}}, ['pw-link', *args])
                PY
                """);
            Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + path);
            action();
            Assert.True(File.Exists(rejected), "The simulated link failure was not reached.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
            Directory.Delete(directory, true);
        }
    }

    private static uint[] IncomingRouteLinks(string node)
    {
        using JsonDocument graph = JsonDocument.Parse(ProcessRunner.Run("pw-dump", []).Stdout);
        JsonElement target = graph.RootElement.EnumerateArray().FirstOrDefault(item =>
            item.GetProperty("type").GetString() == "PipeWire:Interface:Node"
            && item.GetProperty("info").GetProperty("props").TryGetProperty("node.name", out var name)
            && name.GetString() == node);
        if (target.ValueKind == JsonValueKind.Undefined) return [];
        uint id = target.GetProperty("id").GetUInt32();
        return graph.RootElement.EnumerateArray().Where(item =>
            item.GetProperty("type").GetString() == "PipeWire:Interface:Link"
            && item.GetProperty("info").GetProperty("input-node-id").GetUInt32() == id)
            // Registry IDs are reused after a link is removed; serials identify its lifetime.
            .Select(item => item.GetProperty("info").GetProperty("props").GetProperty("object.serial").GetUInt32()).Order().ToArray();
    }

    private static void Stable(Func<bool> ensure, string target, int count)
    {
        uint[] before = IncomingRouteLinks(target);
        Assert.Equal(count, before.Length);
        for (int i = 0; i < 3; i++) Assert.False(ensure());
        Assert.Equal(before, IncomingRouteLinks(target));
    }

    private static void WaitForRecovery(Func<bool> condition)
        => Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5)));
}
