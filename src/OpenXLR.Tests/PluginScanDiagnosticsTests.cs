using System.Text;
using Microsoft.Extensions.Logging;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed class PluginScanDiagnosticsTests
{
    [Fact]
    public void BridgeDiagnosticsRunsStatusOnlyAndPreservesItsFailure()
    {
        if (!OperatingSystem.IsLinux()) return;
        string dir = Directory.CreateTempSubdirectory("bridge-diagnostics-").FullName;
        try
        {
            string controller = ExecutableScript.Write(Path.Combine(dir, "yabridgectl"),
                "[ \"$1\" = status ] || exit 99\necho 'Example.vst3 -> missing wrapper'\necho 'bridge libraries missing' >&2\nexit 17\n");
            var installer = new PluginInstaller(dir, dir, dir, controller, null, winePrefix: Path.Combine(dir, "prefix"));
            var data = System.Text.Json.JsonSerializer.SerializeToElement(installer.Diagnostics(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            Assert.Equal(controller, data.GetProperty("controller").GetString());
            Assert.Equal(17, data.GetProperty("status").GetProperty("exitCode").GetInt32());
            Assert.Contains("missing wrapper", data.GetProperty("status").GetProperty("output").GetString());
            Assert.Contains("libraries missing", data.GetProperty("status").GetProperty("error").GetString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailedAndCachedBundlesRemainVisibleWithoutChangingDiscovery()
    {
        string dir = Directory.CreateTempSubdirectory("plugin-scan-evidence-").FullName;
        string kind = Guid.NewGuid().ToString("N");
        try
        {
            string[] names = ["good", "duplicate", "failed", "timeout", "large", "invalid", "empty"];
            string[] bundles = names.Select(n => Path.Combine(dir, n + ".vst3")).ToArray();
            foreach (string path in bundles) File.WriteAllText(path, "fixture");
            int goodCalls = 0;
            ProcessResult Describe(string path)
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (name == "failed") return new(9, [], "Wine: required module is missing", false, false);
                if (name == "timeout") return new(-1, [], "hung while loading", true, false);
                if (name == "large") return new(0, [], "too much output", false, true);
                if (name == "invalid") return new(0, Encoding.UTF8.GetBytes("{bad json"), "", false, false);
                if (name == "empty") return new(0, Encoding.UTF8.GetBytes("{\"plugins\":[]}"), "no factory", false, false);
                goodCalls++;
                return new(0, Encoding.UTF8.GetBytes("{\"plugins\":[{\"id\":\"same-id\",\"name\":\"EQ\",\"audioIns\":2,\"audioOuts\":2}]}"), "", false, false);
            }
            var cache = new ScanCache(Path.Combine(dir, "cache"));
            var result = HostScan.Run(kind, "unused", [dir, Path.Combine(dir, "missing")], _ => bundles, Describe, cache);
            Assert.Single(result);
            var report = PluginScanDiagnostics.Snapshot().Single(r => r.Kind == kind);
            Assert.Contains(report.Entries, e => e.Outcome == "scan-failed" && e.ExitCode == 9 && e.Detail!.Contains("required module"));
            Assert.Contains(report.Entries, e => e.Outcome == "timeout");
            Assert.Contains(report.Entries, e => e.Outcome == "output-limit");
            Assert.Contains(report.Entries, e => e.Outcome == "invalid-description");
            Assert.Contains(report.Entries, e => e.Outcome == "no-plugins");
            Assert.Contains(report.Entries, e => e.Outcome == "directory-missing");
            Assert.Contains(report.Entries, e => e.Duplicates == 1);
            Assert.Equal(2, goodCalls);

            result = HostScan.Run(kind, "unused", [dir], _ => bundles, Describe, cache);
            Assert.Single(result);
            Assert.Equal(2, goodCalls); // reading evidence does not invalidate the cache
            report = PluginScanDiagnostics.Snapshot().Single(r => r.Kind == kind);
            Assert.Equal(2, report.Entries.Count(e => e.Outcome == "ok" && e.Cached));
            Assert.Contains(report.Entries, e => e.Outcome == "invalid-description" && e.Cached);

            // A cache filled by an older helper answers nothing: whatever the
            // scanner has learnt since reaches bundles that never changed.
            result = HostScan.Run(kind, "unused", [dir], _ => bundles, Describe,
                new ScanCache(Path.Combine(dir, "cache"), "another-helper"));
            Assert.Single(result);
            Assert.Equal(4, goodCalls);
            report = PluginScanDiagnostics.Snapshot().Single(r => r.Kind == kind);
            Assert.DoesNotContain(report.Entries, e => e.Cached);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void EvidenceHasEntryAndTextBoundsAndKeepsLateFailures()
    {
        var capture = new PluginScanDiagnostics.Capture("bounded-test");
        for (int i = 0; i < 200; i++) capture.Add("/plugin/" + i, "ok");
        capture.Add(new string('p', 9000), "scan-failed", detail: "first" + new string('e', 9000) + "last");
        var report = capture.Complete();
        Assert.Equal(128, report.Entries.Count);
        Assert.Equal(73, report.Omitted);
        var failure = Assert.Single(report.Entries, e => e.Outcome == "scan-failed");
        Assert.True(failure.Path.Length < 4200);
        Assert.EndsWith("[truncated]", failure.Path);
        Assert.True(failure.Detail!.Length < 2100);
        Assert.StartsWith("first", failure.Detail);
        Assert.EndsWith("last", failure.Detail);
    }

    // Replay the recorded timeout and exit code with synthetic long stderr.
    // The reporter's tail was lost, so this does not identify why Wine hung.
    [Fact]
    public void ATimedOutBundleKeepsBothEndsOfTheScannerOutputAndIsNamedAsAFailure()
    {
        string dir = Directory.CreateTempSubdirectory("plugin-scan-tail-").FullName;
        string kind = Guid.NewGuid().ToString("N");
        try
        {
            string bundle = Path.Combine(dir, "TDR Kotelnikov.vst3");
            File.WriteAllText(bundle, "fixture");
            string banner = string.Join("\n", Enumerable.Range(0, 60)
                .Select(i => $"11:44:07 [bridge] Initializing yabridge, start-up line {i}"));
            string output = banner + "\nSYNTHETIC-TAIL scanner diagnostic after the startup banner";
            Assert.True(output.Length > 2048);
            var cache = new ScanCache(Path.Combine(dir, "cache"));

            int calls = 0;
            ProcessResult Describe(string _) { calls++; return new(137, [], output, true, false); }
            var result = HostScan.Run(kind, "unused", [dir], Vst3Catalog.Bundles, Describe, cache);

            Assert.Empty(result);
            var report = PluginScanDiagnostics.Snapshot().Single(r => r.Kind == kind);
            var entry = Assert.Single(report.Entries, e => e.Outcome == "timeout");
            Assert.Equal(137, entry.ExitCode);
            Assert.False(entry.Cached);
            Assert.Contains("start-up line 0", entry.Detail);
            Assert.Contains("SYNTHETIC-TAIL", entry.Detail);
            Assert.Contains("[truncated]", entry.Detail);
            Assert.Equal(2048, entry.Detail!.Length);

            var failure = Assert.Single(PluginScanDiagnostics.Failures([report]));
            Assert.Equal(kind, failure.Kind);
            Assert.Equal(bundle, failure.Path);
            Assert.Equal("timeout", failure.Outcome);
            Assert.Equal(137, failure.ExitCode);
            Assert.Equal("1 bundle could not be read: TDR Kotelnikov.vst3 (timed out); the daemon's log says more.",
                PluginScanDiagnostics.Sentence([failure]));

            var log = new ScanLogger();
            PluginScanLog.Write(log);
            var warning = Assert.Single(log.Lines, line => line.Text.Contains(kind));
            Assert.Equal(LogLevel.Warning, warning.Level);
            Assert.Contains(bundle, warning.Text);
            Assert.Contains("timeout (exit 137)", warning.Text);

            // Rescan retries a timeout, including through a reloaded disk cache.
            Assert.Empty(HostScan.Run(kind, "unused", [dir], Vst3Catalog.Bundles, Describe,
                new ScanCache(Path.Combine(dir, "cache"))));
            Assert.Equal(2, calls);

            // When the same bundle answers, the old warning disappears and
            // the successful description can be cached without another launch.
            ProcessResult Recovered(string _) => new(0, Encoding.UTF8.GetBytes(
                "{\"plugins\":[{\"id\":\"recovered\",\"name\":\"Recovered\",\"audioIns\":2,\"audioOuts\":2}]}"), "", false, false);
            Assert.Single(HostScan.Run(kind, "unused", [dir], Vst3Catalog.Bundles, Recovered, cache));
            Assert.Single(HostScan.Run(kind, "unused", [dir], Vst3Catalog.Bundles, Describe, cache));
            Assert.Equal(2, calls);
            report = PluginScanDiagnostics.Snapshot().Single(r => r.Kind == kind);
            Assert.Contains(report.Entries, e => e.Outcome == "ok" && e.Cached);
            Assert.Empty(PluginScanDiagnostics.Failures([report]));
            log.Lines.Clear();
            PluginScanLog.Write(log);
            Assert.DoesNotContain(log.Lines, line => line.Text.Contains(kind));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short error")]
    public void ShortDetailsAreUnchanged(string? text)
        => Assert.Equal(text, PluginScanDiagnostics.ClipEnds(text, 2048));

    private sealed class ScanLogger : ILogger
    {
        internal readonly List<(LogLevel Level, string Text)> Lines = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error,
            Func<TState, Exception?, string> format) => Lines.Add((level, format(state, error)));
    }

    [Fact]
    public void TheFailureSentenceNamesThreeBundlesAndCountsTheRest()
    {
        var capture = new PluginScanDiagnostics.Capture("failure-sentence-test");
        capture.Add("/plugins/Ok.vst3", "ok", plugins: 1);
        capture.Add("/plugins/absent", "directory-missing");
        capture.Add("/plugins/Empty.vst3", "no-plugins");
        capture.Add("/usr/lib/openxlr/daemon/openxlr-lv2-host", "host-missing");
        capture.Add("/plugins/A.vst3", "timeout", exitCode: 137);
        capture.Add("/plugins/B.vst3", "scan-failed", exitCode: 9);
        capture.Add("/plugins/C.vst3", "invalid-description");
        capture.Add("/plugins/D.vst3", "output-limit");
        capture.Add("/plugins/E.clap", "start-error");

        var failures = PluginScanDiagnostics.Failures([capture.Complete()]);

        // A folder that is not there, a bundle with nothing in it and a host
        // that was never installed are states, not failures to report.
        Assert.Equal(5, failures.Count);
        Assert.Equal("5 bundles could not be read: A.vst3 (timed out), B.vst3 (the scanner failed), "
            + "C.vst3 (its description could not be read) and 2 more; the daemon's log says more.",
            PluginScanDiagnostics.Sentence(failures));
        Assert.Equal("", PluginScanDiagnostics.Sentence([]));
    }

    [Fact]
    public void DirectoryAndLaunchErrorsDoNotHideOtherBundles()
    {
        string dir = Directory.CreateTempSubdirectory("plugin-scan-error-").FullName;
        string kind = Guid.NewGuid().ToString("N");
        try
        {
            var cache = new ScanCache(Path.Combine(dir, "cache"));
            HostScan.Run(kind, "unused", [dir], _ => throw new UnauthorizedAccessException("cannot read folder"), _ => throw new Exception(), cache);
            Assert.Contains(PluginScanDiagnostics.Snapshot().Single(r => r.Kind == kind).Entries, e => e.Outcome == "directory-error");
            HostScan.Run(kind, "unused", [dir], _ => [Path.Combine(dir, "plugin.vst3")], _ => throw new IOException("loader missing"), cache);
            Assert.Contains(PluginScanDiagnostics.Snapshot().Single(r => r.Kind == kind).Entries, e => e.Outcome == "start-error" && e.Detail == "loader missing");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
