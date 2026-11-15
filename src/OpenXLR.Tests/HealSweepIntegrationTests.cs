using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class MonitorVolumeIntegrationTests
{
    /// <summary>
    /// Every mix sink, post sink and virtual microphone counts for the graph
    /// being whole, not only the first mix's sink: one unloaded from outside
    /// is reported while the first mix stays.
    /// </summary>
    [MonitorPipeWireFact]
    public void EveryMixPostSinkAndVirtualMicrophoneCountsForTheGraph()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw);
        try
        {
            mixer.Build(new MixerConfig
            {
                Channels = [new("music", "Music")],
                Mixes = [new("monitor", "Monitor", MixKind.Monitor), new("stream", "Stream", MixKind.VirtualMic), new("chat", "Chat", MixKind.VirtualMic)],
            });
            WaitForRecovery(() => mixer.MissingGraphNodes() is { Count: 0 });
            foreach (string node in new[] { "OpenXLR_chat", "OpenXLR_post_stream", "OpenXLR_mix_chat" })
            {
                ProcessResult unloaded = ProcessRunner.Run("pactl", ["unload-module", ModuleOf(node)]);
                Assert.True(unloaded.Ok, unloaded.Stderr);
                WaitForRecovery(() => mixer.MissingGraphNodes()?.Contains(node) == true);
                // What the sweep probed before: the first mix is still there.
                Assert.True(pw.SinkExists("OpenXLR_mix_monitor"));
            }
        }
        finally { pw.TearDown(); }
    }

    /// <summary>
    /// A channel sink unloaded from outside: the reload's own unload fails,
    /// since the module is gone, and the sink is made again with its sends
    /// instead of the channel being dropped from the heal for good.
    /// </summary>
    [MonitorPipeWireFact]
    public void AChannelSinkUnloadedFromOutsideComesBackWithItsSends()
    {
        var pw = new PipeWireAdapter();
        using var registry = pw.WatchGraph();
        using var mixer = new Mixer(pw) { LegMissingSweeps = 2 };
        try
        {
            mixer.Build(new MixerConfig
            {
                Channels = [new("music", "Music")],
                Mixes = [new("monitor", "Monitor", MixKind.Monitor), new("stream", "Stream", MixKind.VirtualMic)],
            });
            WaitForRecovery(() => { mixer.EnsureCellLevels(); return LegsOf(pw, "OpenXLR_ch_music").Count == 2; });
            string module = ModuleOf("OpenXLR_ch_music");
            ProcessResult unloaded = ProcessRunner.Run("pactl", ["unload-module", module]);
            Assert.True(unloaded.Ok, unloaded.Stderr);
            WaitForRecovery(() => pw.SinkExists("OpenXLR_ch_music") == false);

            Assert.True(SpinWait.SpinUntil(() =>
            {
                mixer.EnsureCellLevels();
                return pw.SinkExists("OpenXLR_ch_music") == true && LegsOf(pw, "OpenXLR_ch_music") is { Count: 2 } legs
                    && legs.ContainsKey("OpenXLR_mix_monitor") && legs.ContainsKey("OpenXLR_mix_stream");
            }, TimeSpan.FromSeconds(10)), "the channel's sink did not come back");
            Assert.Single(CombineModules("OpenXLR_ch_music"));
        }
        finally { pw.TearDown(); }
    }

    /// <summary>
    /// A reload whose unload fails while the old sink still plays keeps that
    /// sink as the channel's: the next reload unloads it, and the channel
    /// never ends up with two combines or none.
    /// </summary>
    [MonitorPipeWireFact]
    public void AFailedUnloadKeepsTheChannelsSink()
    {
        string folder = Directory.CreateTempSubdirectory("openxlr-unload-").FullName;
        string? previousPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            string realPactl = (previousPath ?? "").Split(Path.PathSeparator).Select(p => Path.Combine(p, "pactl")).First(File.Exists);
            File.WriteAllText(Path.Combine(folder, "real"), realPactl);
            ExecutableScript.Write(Path.Combine(folder, "pactl"), """
                folder=$(dirname "$0")
                if [ "$1" = unload-module ] && [ -f "$folder/refuse" ] && [ "$2" = "$(cat "$folder/refuse")" ]; then
                    echo 'Failure: Timeout' >&2
                    exit 1
                fi
                exec "$(cat "$folder/real")" "$@"
                """);
            Environment.SetEnvironmentVariable("PATH", folder + Path.PathSeparator + previousPath);
            var pw = new PipeWireAdapter();
            using var registry = pw.WatchGraph();
            using var mixer = new Mixer(pw);
            try
            {
                mixer.Build(new MixerConfig
                {
                    Channels = [new("music", "Music")],
                    Mixes = [new("monitor", "Monitor", MixKind.Monitor), new("stream", "Stream", MixKind.VirtualMic)],
                });
                string module = ModuleOf("OpenXLR_ch_music");
                File.WriteAllText(Path.Combine(folder, "refuse"), module);
                Assert.Throws<InvalidOperationException>(() => mixer.RenameApplicationChannel("music", "Tunes", _ => null));
                Assert.Equal([module], CombineModules("OpenXLR_ch_music"));
                Assert.Contains(mixer.Snapshot().Channels, c => c.Id == "music");

                File.Delete(Path.Combine(folder, "refuse"));
                // The server can hand the freed module id out again, so the
                // proof is one combine under the name, published as renamed.
                mixer.RenameApplicationChannel("music", "Songs", _ => null);
                Assert.Single(CombineModules("OpenXLR_ch_music"));
                Assert.Contains("OpenXLR Songs", ProcessRunner.Run("pactl", ["list", "sinks"]).StdoutText, StringComparison.Ordinal);
            }
            finally { pw.TearDown(); }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Directory.Delete(folder, true);
        }
    }

    /// <summary>
    /// One strip whose wiring fails in the sweep (its low cut's filter chain
    /// cannot start) leaves the other strip to be wired in the same pass, and
    /// carries the reason as its warning until a later pass wires it.
    /// </summary>
    [MonitorPipeWireFact]
    public void AStripThatCannotBeWiredLeavesTheOtherStripsToTheSweep()
    {
        string folder = Directory.CreateTempSubdirectory("openxlr-strip-heal-").FullName;
        string? previousPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            string realCli = (previousPath ?? "").Split(Path.PathSeparator).Select(p => Path.Combine(p, "pw-cli")).First(File.Exists);
            File.WriteAllText(Path.Combine(folder, "real"), realCli);
            ExecutableScript.Write(Path.Combine(folder, "pw-cli"), """
                folder=$(dirname "$0")
                if [ -f "$folder/refuse" ] && [ "$1 $2 $3" = "-m load-module libpipewire-module-filter-chain" ]; then
                    echo 'simulated filter creation failure' >&2
                    exit 1
                fi
                exec "$(cat "$folder/real")" "$@"
                """);
            Environment.SetEnvironmentVariable("PATH", folder + Path.PathSeparator + previousPath);
            var pw = new PipeWireAdapter();
            using var registry = pw.WatchGraph();
            using var mixer = new Mixer(pw);
            try
            {
                pw.CreateNullSink("heal_signal", "Strip heal test");
                uint first = pw.CreateVirtualMic("heal_source_1", "heal_signal.monitor", "Strip heal source 1");
                uint second = pw.CreateVirtualMic("heal_source_2", "heal_signal.monitor", "Strip heal source 2");
                mixer.SetInputBinding("xlr1", new("heal_source_1"));
                mixer.SetInputBinding("xlr2", new("heal_source_2"));
                mixer.Build(new MixerConfig
                {
                    Channels = [new("xlr1", "Mic") { InputPair = 0 }, new("xlr2", "Mic 2") { InputPair = 1 }],
                    Mixes = [new("monitor", "Monitor", MixKind.Monitor)],
                });
                PrimaryWait(() => { mixer.EnsureInputFeeds(); return Strip(mixer, "xlr1").BindingConnected && Strip(mixer, "xlr2").BindingConnected; });
                mixer.SetLowCutHz(80);

                // Both sources go and come back while XLR 1's chain cannot start.
                pw.UnloadModule(first);
                pw.UnloadModule(second);
                PrimaryWait(() => { mixer.EnsureInputFeeds(); return !Strip(mixer, "xlr1").BindingConnected && !Strip(mixer, "xlr2").BindingConnected; });
                File.WriteAllText(Path.Combine(folder, "refuse"), "");
                pw.CreateVirtualMic("heal_source_1", "heal_signal.monitor", "Strip heal source 1");
                pw.CreateVirtualMic("heal_source_2", "heal_signal.monitor", "Strip heal source 2");
                WaitForRecovery(() => pw.ListPorts("heal_source_1", "capture", true).Count == 2 && pw.ListPorts("heal_source_2", "capture", true).Count == 2);

                PrimaryWait(() => { mixer.EnsureInputFeeds(); return Strip(mixer, "xlr2").BindingConnected; });
                Assert.Equal(2, GraphLinksInto("OpenXLR_ch_xlr2").Length);
                Assert.False(Strip(mixer, "xlr1").BindingConnected);
                Assert.Contains("could not be wired", Strip(mixer, "xlr1").BindingWarning);
                Assert.Null(Strip(mixer, "xlr2").BindingWarning);

                File.Delete(Path.Combine(folder, "refuse"));
                PrimaryWait(() => { mixer.EnsureInputFeeds(); return Strip(mixer, "xlr1").BindingConnected; });
                Assert.Null(Strip(mixer, "xlr1").BindingWarning);
            }
            finally { pw.TearDown(); }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Directory.Delete(folder, true);
        }

        static ChannelStatus Strip(Mixer mixer, string id) => mixer.Snapshot().Channels.Single(c => c.Id == id);
    }

    /// <summary>The module that published the node <paramref name="name"/>, from pactl's module list.</summary>
    private static string ModuleOf(string name) => ProcessRunner.Run("pactl", ["list", "modules", "short"]).StdoutText.Split('\n')
        .Where(line => line.Contains($"sink_name={name} ", StringComparison.Ordinal) || line.Contains($"source_name={name} ", StringComparison.Ordinal))
        .Select(line => line.Split('\t')[0]).Single();

    /// <summary>The combine modules loaded under the sink name <paramref name="sink"/>.</summary>
    private static string[] CombineModules(string sink) => [.. ProcessRunner.Run("pactl", ["list", "modules", "short"]).StdoutText.Split('\n')
        .Where(line => line.Contains("module-combine-sink", StringComparison.Ordinal) && line.Contains($"sink_name={sink} ", StringComparison.Ordinal))
        .Select(line => line.Split('\t')[0])];

    /// <summary>The legs of the one combine named <paramref name="sink"/>, or none while it is not loaded.</summary>
    private static IReadOnlyDictionary<string, int> LegsOf(PipeWireAdapter pw, string sink)
        => CombineModules(sink) is [string module] ? pw.FindCombineLegs(uint.Parse(module)) : new Dictionary<string, int>();
}
