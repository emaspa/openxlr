using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed partial class DspAudioIntegrationTests
{
    [DspPipeWireFact]
    public void AFailedLv2ChainUsesTheNativeHostWithoutChangingAnotherInstance()
    {
        string directory = Directory.CreateTempSubdirectory("openxlr-lv2-fallback-").FullName;
        string? oldPath = Environment.GetEnvironmentVariable("PATH");
        string? oldCli = Environment.GetEnvironmentVariable("OPENXLR_TEST_REAL_CLI");
        string? oldAttempts = Environment.GetEnvironmentVariable("OPENXLR_TEST_LV2_ATTEMPTS");
        string attempts = Path.Combine(directory, "attempts");
        string cli = oldPath!.Split(Path.PathSeparator).Select(p => Path.Combine(p, "pw-cli")).First(File.Exists);
        var pw = new PipeWireAdapter();
        try
        {
            ExecutableScript.Write(Path.Combine(directory, "pw-cli"), """
                case "$*" in *OpenXLR_lc_unaffected*) echo attempted >> "$OPENXLR_TEST_LV2_ATTEMPTS";; esac
                case "$*" in *OpenXLR_lc_fallback*) echo 'LV2 loader is unavailable' >&2; exit 1;; esac
                exec "$OPENXLR_TEST_REAL_CLI" "$@"
                """);
            Environment.SetEnvironmentVariable("OPENXLR_TEST_REAL_CLI", cli);
            Environment.SetEnvironmentVariable("OPENXLR_TEST_LV2_ATTEMPTS", attempts);
            Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + oldPath);
            var insert = new InsertDefinition { Id = "gate", Kind = "lv2", Plugin = "http://lsp-plug.in/plugins/lv2/gate_mono", Params = new() { ["enabled"] = 0 } };
            Assert.Contains(PluginCatalog.Refresh(), p => p.Plugin == insert.Plugin);
            var failed = pw.CreateMicFilter("fallback", 0, false, [insert]);
            var host = Assert.Single(failed.InsertStages).Stage.NativeHost;
            Assert.NotNull(host);
            Assert.True(host.IsRunning);
            Assert.False(insert.NativeHost);
            var other = pw.CreateMicFilter("unaffected", 0, false, [insert]);
            // An older PipeWire loader may need the fallback here as well. The
            // second instance must still attempt its saved host independently.
            Assert.Equal("attempted", File.ReadAllText(attempts).Trim());
            Assert.False(insert.NativeHost);
            Assert.True(other.IsAlive);
            pw.SetFilterControl(failed, "i0:enabled", 1);
            Assert.True(host.IsRunning);
        }
        finally
        {
            pw.TearDown();
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Environment.SetEnvironmentVariable("OPENXLR_TEST_REAL_CLI", oldCli);
            Environment.SetEnvironmentVariable("OPENXLR_TEST_LV2_ATTEMPTS", oldAttempts);
            Directory.Delete(directory, true);
        }
    }
}
