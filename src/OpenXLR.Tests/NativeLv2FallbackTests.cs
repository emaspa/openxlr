using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

// Part of the DSP suite so the private PipeWire run picks it up by class name.
public sealed partial class DspAudioIntegrationTests
{
    private const string GateMono = "http://lsp-plug.in/plugins/lv2/gate_mono";

    /// <summary>
    /// A pw-cli in front of the real one that refuses every filter chain whose
    /// sink node is named in <paramref name="refused"/>, the way a PipeWire
    /// without its LV2 loader does, and logs every chain it is asked for.
    /// </summary>
    private static void WithRefusingFilterChain(string[] refused, Action<Func<string[]>> test)
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-lv2-fallback-").FullName;
        string attempts = Path.Combine(directory, "attempts");
        string? oldPath = Environment.GetEnvironmentVariable("PATH");
        string cli = oldPath!.Split(Path.PathSeparator).Select(p => Path.Combine(p, "pw-cli")).First(File.Exists);
        string refusals = string.Join('\n', refused.Select(node =>
            $"case \"$*\" in *\"node.name = {node} \"*) echo 'LV2 loader is unavailable' >&2; exit 1;; esac"));
        ExecutableScript.Write(Path.Combine(directory, "pw-cli"), $$"""
            case "$*" in *libpipewire-module-filter-chain*)
              printf '%s\n' "$*" | grep -o 'capture.props = { node.name = [^ ]*' | sed 's/.* //' >> '{{attempts}}';;
            esac
            {{refusals}}
            exec '{{cli}}' "$@"
            """);
        Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + oldPath);
        try
        {
            test(() => File.Exists(attempts) ? File.ReadAllLines(attempts) : []);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Directory.Delete(directory, true);
        }
    }

    [DspPipeWireFact]
    public void ARefusedLv2InsertRunsInTheNativeHostWithoutChangingItsSavedChoice()
    {
        var pw = new PipeWireAdapter();
        WithRefusingFilterChain(["OpenXLR_lc_fallback_in"], attempts =>
        {
            try
            {
                var insert = new InsertDefinition { Id = "gate", Kind = "lv2", Plugin = GateMono, Params = new() { ["enabled"] = 0 } };
                Assert.Contains(PluginCatalog.Refresh(), p => p.Plugin == insert.Plugin);

                var refused = pw.CreateMicFilter("fallback", 80, false, [insert]);
                (string id, FilterHandle stage) = Assert.Single(refused.InsertStages);
                Assert.Equal("gate", id);
                Assert.NotNull(stage.NativeHost);
                Assert.True(stage.NativeHost.IsRunning);
                Assert.Contains("LV2 loader is unavailable", stage.FilterChainError);
                Assert.False(insert.NativeHost);
                pw.SetFilterControl(refused, "i0:enabled", 1);
                Assert.True(refused.IsAlive);

                // Another instance of the same plugin tries the filter chain
                // again for itself. A PipeWire without the LV2 loader refuses
                // it too, and then it falls back on its own.
                var other = pw.CreateMicFilter("unaffected", 0, false, [insert]);
                Assert.Contains("OpenXLR_lc_unaffected_in", attempts());
                Assert.True(other.IsAlive);
                Assert.All(other.InsertStages, s => Assert.NotNull(s.Stage.FilterChainError));
            }
            finally { pw.TearDown(); }
        });
    }

    [DspPipeWireFact]
    public void EachInsertOfARefusedChainIsDecidedOnItsOwn()
    {
        var pw = new PipeWireAdapter();
        WithRefusingFilterChain(["OpenXLR_lc_mixed_in", "OpenXLR_lc_mixed_in_stage_1_in"], attempts =>
        {
            try
            {
                var first = new InsertDefinition { Id = "first", Kind = "lv2", Plugin = GateMono, Params = new() { ["enabled"] = 0 } };
                var second = first with { Id = "second" };
                Assert.Contains(PluginCatalog.Refresh(), p => p.Plugin == GateMono);

                var chain = pw.CreateMicFilter("mixed", 0, false, [first, second]);
                Assert.Equal(["first", "second"], chain.InsertStages.Select(s => s.Id));
                Assert.Contains("OpenXLR_lc_mixed_in_stage_0_in", attempts());
                Assert.Contains("OpenXLR_lc_mixed_in_stage_1_in", attempts());
                FilterHandle refused = chain.InsertStages[1].Stage;
                Assert.NotNull(refused.NativeHost);
                Assert.Contains("LV2 loader is unavailable", refused.FilterChainError);
                // The first insert stays in the filter chain where PipeWire
                // can load it; where it cannot, it says why it moved.
                FilterHandle kept = chain.InsertStages[0].Stage;
                Assert.Equal(kept.NativeHost is null, kept.FilterChainError is null);
                pw.SetFilterControl(chain, "i1:enabled", 1);
                Assert.True(chain.IsAlive);
                pw.StopFilter(chain);

                // An insert saved to the native host keeps its own host and
                // carries no refusal beside a refused one.
                var saved = pw.CreateMicFilter("mixed", 0, false, [first with { NativeHost = true }, second]);
                Assert.All(saved.InsertStages, s => Assert.NotNull(s.Stage.NativeHost));
                Assert.Null(saved.InsertStages[0].Stage.FilterChainError);
                Assert.Contains("LV2 loader is unavailable", saved.InsertStages[1].Stage.FilterChainError);
                pw.StopFilter(saved);
            }
            finally { pw.TearDown(); }
        });
    }
}
