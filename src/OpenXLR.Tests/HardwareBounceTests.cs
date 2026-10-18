using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public async Task HardwareBouncesKeepANewerMonitorSelection()
    {
        foreach (bool aux in new[] { false, true })
            await DuringHardwareBounce(aux,
                mixer => mixer.SetMonitorOutputs(["test_bounce_new_output"]),
                mixer =>
                {
                    Assert.Equal(["test_bounce_new_output"], mixer.MonitorOutputs);
                    WaitForRecovery(() => IncomingRouteLinks("test_bounce_new_output").Length == 2);
                });
    }

    [MonitorPipeWireFact]
    public async Task HardwareBouncesDoNotRestoreRoutesAfterTearDown()
    {
        foreach (bool aux in new[] { false, true })
            await DuringHardwareBounce(aux, mixer => mixer.TearDown(), mixer =>
            {
                Assert.False(mixer.Built);
                Assert.Empty(mixer.MonitorOutputs);
                Assert.Empty(IncomingRouteLinks("test_Wave_XLR_bounce"));
            });
    }

    private static async Task DuringHardwareBounce(bool aux, Action<Mixer> change, Action<Mixer> check)
    {
        const string output = "test_Wave_XLR_bounce";
        string directory = Directory.CreateTempSubdirectory("openxlr-bounce-").FullName;
        string? path = Environment.GetEnvironmentVariable("PATH");
        string realPactl = (path ?? "").Split(Path.PathSeparator)
            .Select(p => Path.GetFullPath(Path.Combine(p, "pactl"))).First(File.Exists);
        string paused = Path.Combine(directory, "paused"), resume = Path.Combine(directory, "resume");
        uint? module = null;
        Task? bounce = null;
        try
        {
            var loaded = ProcessRunner.Run("pactl", ["load-module", "module-null-sink", "sink_name=" + output,
                "channels=12", "channel_map=" + string.Join(',', Enumerable.Range(0, 12).Select(i => "aux" + i))]);
            Assert.True(loaded.Ok, loaded.Stderr);
            module = uint.Parse(loaded.StdoutText.Trim());
            var pw = new PipeWireAdapter();
            using var registry = pw.WatchGraph();
            using var mixer = new Mixer(pw);
            WaitForRecovery(() => pw.FindNodeId(output) is not null);
            mixer.SetInputDeviceHint(output, hardwareOutputRouting: true);
            mixer.Build(new MixerConfig { Channels = [], Mixes =
                [new("monitor", "Monitor", MixKind.Monitor), new("aux", "Aux", MixKind.AuxPort)] });
            pw.CreateNullSink("test_bounce_new_output", "New monitor output");
            mixer.SetMonitorOutputs([output + "#hp1"]);
            WaitForRecovery(() => IncomingRouteLinks(output).Length == 4);
            ExecutableScript.Write(Path.Combine(directory, "pactl"), $$"""
                #!/bin/sh
                exec python3 - "$@" <<'PY'
                import os, pathlib, sys, time
                args = sys.argv[1:]
                if args == ['suspend-sink', {{JsonSerializer.Serialize(output)}}, '1']:
                    pathlib.Path({{JsonSerializer.Serialize(paused)}}).touch()
                    deadline = time.monotonic() + 8
                    while not pathlib.Path({{JsonSerializer.Serialize(resume)}}).exists():
                        if time.monotonic() >= deadline: sys.exit(1)
                        time.sleep(0.01)
                os.execv({{JsonSerializer.Serialize(realPactl)}}, ['pactl', *args])
                PY
                """);
            Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + path);
            bounce = Task.Run(aux ? mixer.BounceAuxTarget : mixer.BounceMonitorHardwareOutput);
            try
            {
                Assert.True(SpinWait.SpinUntil(() => File.Exists(paused), TimeSpan.FromSeconds(5)),
                    "The hardware bounce did not reach suspend-sink.");
                change(mixer);
            }
            finally
            {
                File.WriteAllText(resume, "");
                await bounce.WaitAsync(TimeSpan.FromSeconds(8));
            }
            check(mixer);
        }
        finally
        {
            try
            {
                File.WriteAllText(resume, "");
                if (bounce is not null) await bounce.WaitAsync(TimeSpan.FromSeconds(8));
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", path);
                if (module is uint id) ProcessRunner.Run("pactl", ["unload-module", id.ToString()]);
                Directory.Delete(directory, true);
            }
        }
    }
}
