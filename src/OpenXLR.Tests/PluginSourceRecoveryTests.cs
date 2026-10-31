using System.Text;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

public sealed class PluginSourceRecoveryTests
{
    private const string Description = """
        {"plugins":[{"id":"compressor","name":"Compressor","audioIns":2,"audioOuts":2}]}
        """;

    [Fact]
    public void CachedWrapperFollowsOriginalWindowsModuleUpdatesAndRemoval()
    {
        if (!OperatingSystem.IsLinux()) return;
        string dir = Directory.CreateTempSubdirectory("vst3-source-cache-").FullName;
        try
        {
            string bundle = Path.Combine(dir, "Compressor.vst3");
            string win = Directory.CreateDirectory(Path.Combine(bundle, "Contents", "x86_64-win")).FullName;
            string linux = Directory.CreateDirectory(Path.Combine(bundle, "Contents", "x86_64-linux")).FullName;
            string original = Path.Combine(dir, "original.vst3");
            File.WriteAllText(original, "MZ original plugin");
            File.SetLastWriteTimeUtc(original, DateTime.UtcNow.AddDays(-2));
            string wrapper = Path.Combine(linux, "Compressor.so");
            File.WriteAllText(wrapper, "ELF wrapper");
            File.SetLastWriteTimeUtc(wrapper, DateTime.UtcNow);
            File.CreateSymbolicLink(Path.Combine(win, "Compressor.vst3"), Path.GetRelativePath(win, original));
            var cache = new ScanCache(Path.Combine(dir, "cache"), "test-scanner");
            byte[] description = Encoding.UTF8.GetBytes(Description);
            cache.Store(bundle, description);
            Assert.Equal(description, cache.Lookup(bundle));

            // Same size and a wrapper newer than both versions of the source.
            // Neither the link's stamp nor the newest file detects this update.
            File.SetLastWriteTimeUtc(original, DateTime.UtcNow.AddDays(-1));
            Assert.Null(cache.Lookup(bundle));
            cache.Store(bundle, description);
            Assert.Equal(description, cache.Lookup(bundle));
            File.Delete(original);
            Assert.Null(cache.Lookup(bundle));
            Assert.Contains(original, HostScan.MissingWindowsModuleDetail(bundle));

            File.WriteAllText(original, "MZ restored updated plugin");
            Assert.Null(cache.Lookup(bundle));
            cache.Store(bundle, description);
            Assert.Equal(description, cache.Lookup(bundle));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FilePluginLinksAreInvalidatedWhenTheirTargetChanges()
    {
        if (!OperatingSystem.IsLinux()) return;
        string dir = Directory.CreateTempSubdirectory("plugin-file-link-").FullName;
        try
        {
            string original = Path.Combine(dir, "original");
            string link = Path.Combine(dir, "effect.clap");
            File.WriteAllText(original, "first");
            File.CreateSymbolicLink(link, original);
            var cache = new ScanCache(Path.Combine(dir, "cache"));
            cache.Store(link, Encoding.UTF8.GetBytes(Description));
            Assert.NotNull(cache.Lookup(link));
            File.WriteAllText(original, "replacement with new parameters");
            Assert.Null(cache.Lookup(link));
            File.Delete(original);
            Assert.Null(cache.Lookup(link));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void RetargetingAPluginLinkIsNotACacheHitEvenWithAnIdenticalTargetStamp()
    {
        if (!OperatingSystem.IsLinux()) return;
        string dir = Directory.CreateTempSubdirectory("plugin-link-retarget-").FullName;
        try
        {
            // Two builds of the same plugin, same length and same timestamp,
            // telling the host about different parameters.
            string first = Path.Combine(dir, "first.clap");
            string second = Path.Combine(dir, "second.clap");
            File.WriteAllText(first, "AAAA");
            File.WriteAllText(second, "BBBB");
            DateTime when = DateTime.UtcNow.AddDays(-3);
            File.SetLastWriteTimeUtc(first, when);
            File.SetLastWriteTimeUtc(second, when);
            string link = Path.Combine(dir, "effect.clap");
            File.CreateSymbolicLink(link, first);
            var cache = new ScanCache(Path.Combine(dir, "cache"), "test-scanner");
            byte[] description = Encoding.UTF8.GetBytes(Description);
            cache.Store(link, description);
            Assert.Equal(description, cache.Lookup(link));

            File.Delete(link);
            File.CreateSymbolicLink(link, second);
            Assert.Null(cache.Lookup(link));
            cache.Store(link, description);
            Assert.Equal(description, cache.Lookup(link));

            // The same retarget one level down in a bundle.
            string bundle = Path.Combine(dir, "Effect.vst3");
            string win = Directory.CreateDirectory(Path.Combine(bundle, "Contents", "x86_64-win")).FullName;
            string module = Path.Combine(win, "Effect.vst3");
            File.CreateSymbolicLink(module, first);
            cache.Store(bundle, description);
            Assert.Equal(description, cache.Lookup(bundle));
            File.Delete(module);
            File.CreateSymbolicLink(module, second);
            Assert.Null(cache.Lookup(bundle));

            // And a bundle that is a link to one of two identical folders,
            // the shape the managed Windows folder uses.
            string here = MakeCopy(Path.Combine(dir, "here.vst3"), when);
            string there = MakeCopy(Path.Combine(dir, "there.vst3"), when);
            string linked = Path.Combine(dir, "Linked.vst3");
            Directory.CreateSymbolicLink(linked, here);
            cache.Store(linked, description);
            Assert.Equal(description, cache.Lookup(linked));
            Directory.Delete(linked);
            Directory.CreateSymbolicLink(linked, there);
            Assert.Null(cache.Lookup(linked));
        }
        finally { Directory.Delete(dir, recursive: true); }

        static string MakeCopy(string bundle, DateTime when)
        {
            string linux = Directory.CreateDirectory(Path.Combine(bundle, "Contents", "x86_64-linux")).FullName;
            string module = Path.Combine(linux, "Linked.so");
            File.WriteAllText(module, "ELF");
            File.SetLastWriteTimeUtc(module, when);
            return bundle;
        }
    }

    [Fact]
    public void AnUnrelatedDanglingLinkInABundleStillAllowsACacheHit()
    {
        if (!OperatingSystem.IsLinux()) return;
        string dir = Directory.CreateTempSubdirectory("plugin-dangling-arch-").FullName;
        try
        {
            // The host loads Contents/x86_64-linux only; a leftover link for
            // another architecture says nothing about this bundle.
            string bundle = Path.Combine(dir, "Example.vst3");
            string linux = Directory.CreateDirectory(Path.Combine(bundle, "Contents", "x86_64-linux")).FullName;
            string other = Directory.CreateDirectory(Path.Combine(bundle, "Contents", "i386-linux")).FullName;
            File.WriteAllText(Path.Combine(linux, "Example.so"), "ELF wrapper");
            string dangling = Path.Combine(other, "Example.so");
            string missing = Path.Combine(dir, "gone.so");
            File.CreateSymbolicLink(dangling, missing);
            var cache = new ScanCache(Path.Combine(dir, "cache"), "test-scanner");
            byte[] description = Encoding.UTF8.GetBytes(Description);
            cache.Store(bundle, description);
            Assert.Equal(description, cache.Lookup(bundle));

            // A broken link that starts resolving is still a change.
            File.WriteAllText(missing, "ELF 32-bit wrapper");
            Assert.Null(cache.Lookup(bundle));
            cache.Store(bundle, description);
            Assert.Equal(description, cache.Lookup(bundle));
            File.Delete(missing);
            Assert.Null(cache.Lookup(bundle));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("file")]
    [InlineData("linked-file")]
    [InlineData("bundle")]
    [InlineData("architecture")]
    public void RetargetingAParentDirectoryInvalidatesDescriptionsAndFailures(string shape)
    {
        if (!OperatingSystem.IsLinux()) return;
        string dir = Directory.CreateTempSubdirectory("plugin-parent-link-").FullName;
        try
        {
            string first = Directory.CreateDirectory(Path.Combine(dir, "first")).FullName;
            string second = Directory.CreateDirectory(Path.Combine(dir, "second")).FullName;
            string relative = shape is "file" or "linked-file" ? "Effect.clap" : shape == "bundle"
                ? "Effect.vst3/Contents/x86_64-linux/Effect.so" : "Effect.so";
            DateTime when = DateTime.UtcNow.AddDays(-3);
            foreach (string folder in new[] { first, second })
            {
                string module = Path.Combine(folder, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(module)!);
                File.WriteAllText(module, folder == first ? "AAAA" : "BBBB");
                File.SetLastWriteTimeUtc(module, when);
            }
            string link = shape == "architecture"
                ? Path.Combine(dir, "Effect.vst3", "Contents", "x86_64-linux") : Path.Combine(dir, "current");
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            Directory.CreateSymbolicLink(link, first);
            string bundle = shape is "file" or "linked-file" ? Path.Combine(link, "Effect.clap") : shape == "bundle"
                ? Path.Combine(link, "Effect.vst3") : Path.Combine(dir, "Effect.vst3");
            if (shape == "linked-file")
            {
                string selected = Path.Combine(dir, "Linked.clap");
                File.CreateSymbolicLink(selected, bundle);
                bundle = selected;
            }
            string cacheDirectory = Path.Combine(dir, "cache");
            var cache = new ScanCache(cacheDirectory, "test-scanner");
            byte[] description = Encoding.UTF8.GetBytes(Description);
            cache.Store(bundle, description);
            cache.Save();
            Assert.Equal(description, new ScanCache(cacheDirectory, "test-scanner").Lookup(bundle));

            Directory.Delete(link);
            Directory.CreateSymbolicLink(link, second);
            Assert.Null(cache.Lookup(bundle));
            cache.StoreFailure(bundle, "timeout");
            cache.Save();
            cache = new ScanCache(cacheDirectory, "test-scanner");
            Assert.True(cache.KnownFailure(bundle));
            Directory.Delete(link);
            Directory.CreateSymbolicLink(link, first);
            Assert.False(cache.KnownFailure(bundle));
            cache.Store(bundle, description);
            Assert.Equal(description, cache.Lookup(bundle));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ABundleWhoseWindowsModuleIsGoneIsNotCachedAsUsable()
    {
        if (!OperatingSystem.IsLinux()) return;
        string dir = Directory.CreateTempSubdirectory("vst3-source-uncached-").FullName;
        try
        {
            string bundle = Path.Combine(dir, "Compressor.vst3");
            string win = Directory.CreateDirectory(Path.Combine(bundle, "Contents", "x86_64-win")).FullName;
            string linux = Directory.CreateDirectory(Path.Combine(bundle, "Contents", "x86_64-linux")).FullName;
            File.WriteAllText(Path.Combine(linux, "Compressor.so"), "ELF wrapper");
            File.CreateSymbolicLink(Path.Combine(win, "Compressor.vst3"), Path.Combine(dir, "gone.vst3"));
            var cache = new ScanCache(Path.Combine(dir, "cache"), "test-scanner");
            Assert.Empty(HostScan.Run("vst3", "unused", [dir], Vst3Catalog.Bundles,
                _ => throw new InvalidOperationException("A missing Windows module must never load the bridge."),
                cache, retryFailures: true));
            var entry = Assert.Single(PluginScanDiagnostics.Snapshot().Single(r => r.Kind == "vst3").Entries,
                e => e.Path == bundle);
            Assert.Equal("windows-module-missing", entry.Outcome);
            Assert.Null(cache.Lookup(bundle));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("x86_64-win", false)]
    [InlineData("i386-win", true)]
    public void MissingWindowsSourceSkipsCachedWrappersAndRecoversAfterRestoration(string architecture, bool retry)
    {
        if (!OperatingSystem.IsLinux()) return;
        string dir = Directory.CreateTempSubdirectory("vst3-preflight-").FullName;
        try
        {
            string bundle = Path.Combine(dir, "Compressor.vst3");
            string win = Directory.CreateDirectory(Path.Combine(bundle, "Contents", architecture)).FullName;
            string original = Path.Combine(dir, "original.bin");
            File.CreateSymbolicLink(Path.Combine(win, "Compressor.vst3"), "../../../original.bin");
            var cache = new ScanCache(Path.Combine(dir, "cache"), "test-scanner");
            cache.Store(bundle, Encoding.UTF8.GetBytes(Description));
            int launches = 0;
            ProcessResult Describe(string _) { launches++; return new(0, Encoding.UTF8.GetBytes(Description), "", false, false); }
            Assert.Empty(HostScan.Run("vst3", "unused", [dir], Vst3Catalog.Bundles, Describe, cache, retryFailures: retry));
            Assert.Equal(0, launches);
            Assert.Null(cache.Lookup(bundle));
            File.WriteAllText(original, "MZ restored plugin");
            Assert.Single(HostScan.Run("vst3", "unused", [dir], Vst3Catalog.Bundles, Describe, cache));
            Assert.Equal(1, launches);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void MissingBundleLinkIsReportedWithoutStartingTheScanner()
    {
        if (!OperatingSystem.IsLinux()) return;
        string dir = Directory.CreateTempSubdirectory("missing-plugin-link-").FullName;
        string kind = Guid.NewGuid().ToString("N");
        try
        {
            File.CreateSymbolicLink(Path.Combine(dir, "Delay.vst3"), Path.Combine(dir, "gone"));
            Assert.Empty(HostScan.Run(kind, "unused", [dir], Vst3Catalog.Bundles,
                _ => throw new InvalidOperationException("must not run"), new ScanCache(Path.Combine(dir, "cache"))));
            var report = PluginScanDiagnostics.Snapshot().Single(r => r.Kind == kind);
            var failure = Assert.Single(PluginScanDiagnostics.Failures([report]));
            Assert.Equal("source-missing", failure.Outcome);
            Assert.Contains("Restore the original plugin", PluginScanDiagnostics.Sentence([failure]));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ALinkLoopDoesNotPreventAnotherBundleFromBeingScanned()
    {
        if (!OperatingSystem.IsLinux()) return;
        string dir = Directory.CreateTempSubdirectory("plugin-source-loop-").FullName;
        string kind = Guid.NewGuid().ToString("N");
        try
        {
            File.CreateSymbolicLink(Path.Combine(dir, "a.vst3"), "b.vst3");
            File.CreateSymbolicLink(Path.Combine(dir, "b.vst3"), "a.vst3");
            File.WriteAllText(Path.Combine(dir, "good.vst3"), "fixture");
            int scans = 0;
            var plugins = HostScan.Run(kind, "unused", [dir], Vst3Catalog.Bundles,
                _ => { scans++; return new(0, Encoding.UTF8.GetBytes(Description), "", false, false); },
                new ScanCache(Path.Combine(dir, "cache")));
            Assert.Single(plugins);
            Assert.Equal(1, scans);
            Assert.Equal(2, PluginScanDiagnostics.Failures(
                PluginScanDiagnostics.Snapshot().Where(r => r.Kind == kind)).Count);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData(false, false, "windows-module-missing")]
    [InlineData(true, false, "timeout")]
    [InlineData(false, true, "output-limit")]
    public void YabridgeMissingSourceFailureKeepsTheExitCodeAndRecoveryAdvice(bool timedOut, bool truncated, string expected)
    {
        string dir = Directory.CreateTempSubdirectory("vst3-source-report-").FullName;
        try
        {
            string bundle = Path.Combine(dir, "Compressor.vst3");
            Directory.CreateDirectory(bundle);
            Assert.Empty(HostScan.Run("vst3", "unused", [dir], Vst3Catalog.Bundles,
                _ => new ProcessResult(1, [], "Compressor.vst3 does not contain a Windows VST3 module.", timedOut, truncated),
                new ScanCache(Path.Combine(dir, "cache"))));
            var report = PluginScanDiagnostics.Snapshot().Single(r => r.Kind == "vst3");
            var entry = Assert.Single(report.Entries, e => e.Path == bundle);
            Assert.Equal(expected, entry.Outcome);
            Assert.Equal(1, entry.ExitCode);
            if (!timedOut && !truncated)
            {
                Assert.Contains("Restore the original Windows plugin", entry.Detail);
                Assert.Contains("original Windows plugin is missing", PluginScanDiagnostics.Sentence(PluginScanDiagnostics.Failures([report])));
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
