using System.Text;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

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
            string controller = Path.Combine(dir, "yabridgectl");
            File.WriteAllText(controller, "#!/bin/sh\n[ \"$1\" = status ] || exit 99\necho 'Example.vst3 -> missing wrapper'\necho 'bridge libraries missing' >&2\nexit 17\n");
            File.SetUnixFileMode(controller, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
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
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void EvidenceHasEntryAndTextBoundsAndKeepsLateFailures()
    {
        var capture = new PluginScanDiagnostics.Capture("bounded-test");
        for (int i = 0; i < 200; i++) capture.Add("/plugin/" + i, "ok");
        capture.Add(new string('p', 9000), "scan-failed", detail: new string('e', 9000));
        var report = capture.Complete();
        Assert.Equal(128, report.Entries.Count);
        Assert.Equal(73, report.Omitted);
        var failure = Assert.Single(report.Entries, e => e.Outcome == "scan-failed");
        Assert.True(failure.Path.Length < 4200);
        Assert.True(failure.Detail!.Length < 2100);
        Assert.EndsWith("[truncated]", failure.Detail);
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
