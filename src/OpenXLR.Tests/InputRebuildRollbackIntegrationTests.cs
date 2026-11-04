using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    [MonitorPipeWireFact]
    public void FailedLowCutCreationKeepsThePreviousSettingAndCanBeRetried()
        => FailedInputRebuild("start");

    [MonitorPipeWireFact]
    public void FailedInputPortLookupRemovesTheCandidateAndKeepsThePreviousChain()
        => FailedInputRebuild("lookup");

    [MonitorPipeWireFact]
    public void FailedFilterReadinessLookupRemovesTheCandidateAndKeepsThePreviousChain()
        => FailedInputRebuild("readiness");

    private static void FailedInputRebuild(string failure)
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        pw.CreateNullSink("rebuild_signal", "Input rebuild test");
        pw.CreateVirtualMic("rebuild_source", "rebuild_signal.monitor", "Input rebuild source");
        mixer.SetInputBinding("xlr1", new("rebuild_source"));
        mixer.Build(new MixerConfig
        {
            Channels = [new("xlr1", "Mic") { InputPair = 0 }],
            Mixes = [new("monitor", "Monitor", MixKind.Monitor)],
        });
        // The new source publishes its ports a moment after its node. Until
        // they are listed the build leaves the strip silent ("rebuild_source
        // has no port 1") and the daemon's sweep wires it, so the baseline is
        // taken once the sweep has the strip connected.
        PrimaryWait(() => { mixer.EnsureInputFeeds(); return mixer.Snapshot().Channels.Single(c => c.Id == "xlr1").BindingConnected; });
        mixer.SetLowCutHz(80);
        string[] previousNodes = MixDelayIntegrationTests.OwnNodes(pw);
        var previousLinks = GraphLinksInto("OpenXLR_ch_xlr1");
        Assert.Equal(2, previousLinks.Length);
        string directory = Directory.CreateTempSubdirectory("openxlr-input-rebuild-").FullName;
        string? originalPath = Environment.GetEnvironmentVariable("PATH");
        string realCli = FindProgram("pw-cli"), realLink = FindProgram("pw-link");
        try
        {
            File.WriteAllText(Path.Combine(directory, "failure"), failure);
            ExecutableScript.Write(Path.Combine(directory, "pw-cli"), $$"""
                exec python3 - "$@" <<'PY'
                import json, os, pathlib, re, sys
                root = pathlib.Path({{JsonSerializer.Serialize(directory)}})
                if sys.argv[1:4] == ['-m', 'load-module', 'libpipewire-module-filter-chain']:
                    names = re.findall(r'node\.name = (\S+)', sys.argv[4])
                    (root / 'candidate').write_text(json.dumps({'sink': names[0], 'source': names[1], 'pid': os.getpid()}))
                    if (root / 'failure').read_text() == 'start':
                        sys.stderr.write('simulated filter creation failure\n')
                        sys.exit(1)
                os.execv({{JsonSerializer.Serialize(realCli)}}, [{{JsonSerializer.Serialize(realCli)}}, *sys.argv[1:]])
                PY
                """);
            ExecutableScript.Write(Path.Combine(directory, "pw-link"), $$"""
                exec python3 - "$@" <<'PY'
                import json, pathlib, subprocess, sys
                root = pathlib.Path({{JsonSerializer.Serialize(directory)}})
                args = sys.argv[1:]
                if (root / 'candidate').exists() and (root / 'failure').exists() and (root / 'failure').read_text() == 'readiness':
                    (root / 'failure').unlink()
                    sys.stderr.write('simulated filter readiness lookup failure\n')
                    sys.exit(1)
                if (root / 'candidate').exists() and (root / 'failure').exists() and (root / 'failure').read_text() == 'lookup':
                    candidate = json.loads((root / 'candidate').read_text())
                    if args in (['-i'], ['-o']):
                        if (root / 'sink-ready').exists() and (root / 'source-ready').exists():
                            (root / 'failure').unlink()
                            sys.stderr.write('simulated post-start port lookup failure\n')
                            sys.exit(1)
                        result = subprocess.run([{{JsonSerializer.Serialize(realLink)}}, *args], capture_output=True)
                        key, prefixes = ('sink', ('playback', 'input')) if args == ['-i'] else ('source', ('capture', 'output'))
                        if any(candidate[key] + ':' + prefix in result.stdout.decode() for prefix in prefixes):
                            (root / (key + '-ready')).touch()
                        sys.stdout.buffer.write(result.stdout)
                        sys.stderr.buffer.write(result.stderr)
                        sys.exit(result.returncode)
                import os
                os.execv({{JsonSerializer.Serialize(realLink)}}, [{{JsonSerializer.Serialize(realLink)}}, *args])
                PY
                """);

            try
            {
                // Exercise direct-helper failures after losing a live registry.
                // With the subscription up, readiness needs no pw-link read.
                if (failure != "start") registry.Dispose();
                Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + originalPath);
                InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => mixer.SetLowCutHz(120));
                Assert.Contains(failure switch
                {
                    "start" => "simulated filter creation",
                    "readiness" => "simulated filter readiness",
                    _ => "simulated post-start",
                }, error.Message);
            }
            finally { Environment.SetEnvironmentVariable("PATH", originalPath); }

            using var candidate = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "candidate")));
            int pid = candidate.RootElement.GetProperty("pid").GetInt32();
            Assert.True(SpinWait.SpinUntil(() => !Directory.Exists("/proc/" + pid), TimeSpan.FromSeconds(5)),
                "The rejected filter holder must stop before another rebuild can start.");
            Assert.Equal(80, mixer.LowCutHz);
            Assert.Equal(80, mixer.ExportSettings().LowCutHz);
            Assert.True(SpinWait.SpinUntil(() => previousNodes.SequenceEqual(MixDelayIntegrationTests.OwnNodes(pw)), TimeSpan.FromSeconds(5)));
            Assert.Equal(previousLinks, GraphLinksInto("OpenXLR_ch_xlr1"));

            mixer.SetLowCutHz(120);

            Assert.Equal(120, mixer.LowCutHz);
            Assert.NotEqual(previousLinks, GraphLinksInto("OpenXLR_ch_xlr1"));
            Assert.Equal(2, GraphLinksInto("OpenXLR_ch_xlr1").Length);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Directory.Delete(directory, recursive: true);
        }

        string FindProgram(string name) => (originalPath ?? "").Split(Path.PathSeparator)
            .Select(path => Path.GetFullPath(Path.Combine(path, name))).First(File.Exists);
    }
}
