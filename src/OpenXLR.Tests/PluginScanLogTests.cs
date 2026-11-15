using System.Text;
using OpenXLR.Core;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// The scanner output a failed scan keeps. The summary is clipped to a head
/// and a tail on purpose, and that is what lost the first exception of a
/// bridged plugin's Wine stack overflow in the field: the bridge's start-up
/// banner filled the head and the unwind filled the tail. These tests build
/// that shape synthetically, with a marker where the exception was.
/// </summary>
[Collection("xdg-config")]
public sealed class PluginScanLogTests
{
    private const string Marker = "OPENXLR-REPRO-EXCEPTION-3f9c1d";

    /// <summary>A start-up banner, one exception, then a long repeated unwind.</summary>
    private static string WineFailureOutput(int unwindLines = 2500)
    {
        var text = new StringBuilder();
        for (int i = 0; i < 40; i++)
            text.Append("21:04:26 [bridge] Initializing yabridge 5.1.1, start-up line ").Append(i).Append('\n');
        text.Append("21:04:27 wine: Unhandled exception: ").Append(Marker).Append(" stack overflow in thread 0024\n");
        for (int i = 0; i < unwindLines; i++)
            text.Append("21:04:27 err:seh:dwarf_virtual_unwind unknown CFA opcode 2e at frame ").Append(i).Append('\n');
        return text.ToString();
    }

    private static PluginScanReport ReportFor(string kind)
        => PluginScanDiagnostics.Snapshot().Single(r => r.Kind == kind);

    [Fact]
    public void TheSavedLogKeepsTheExceptionTheSummaryClipsOutOfTheMiddle()
    {
        string dir = Directory.CreateTempSubdirectory("plugin-scan-log-").FullName;
        string kind = Guid.NewGuid().ToString("N");
        try
        {
            string bundle = Path.Combine(dir, "TDR Kotelnikov.vst3");
            File.WriteAllText(bundle, "fixture");
            string output = WineFailureOutput();
            var logs = new PluginScanLogStore(Path.Combine(dir, "logs"));
            ProcessResult Describe(string _) => new(137, [], output, true, false);

            var found = HostScan.Run(kind, "unused", [dir], Vst3Catalog.Bundles, Describe,
                new ScanCache(Path.Combine(dir, "cache")), logs);

            Assert.Empty(found);
            var entry = Assert.Single(ReportFor(kind).Entries, e => e.Outcome == "timeout");
            // The summary stays the size every diagnostics reply can carry,
            // and at that size it still cannot hold the exception.
            Assert.Equal(2048, entry.Detail!.Length);
            Assert.DoesNotContain(Marker, entry.Detail);

            Assert.Equal(PluginScanLogStore.IdFor(kind, bundle), entry.LogId);
            Assert.Null(entry.LogNote);
            string saved = File.ReadAllText(Path.Combine(dir, "logs", entry.LogId + ".log"));
            Assert.Contains(Marker, saved);
            Assert.Contains("start-up line 0", saved);
            Assert.True(output.Length < PluginScanLogStore.StreamCapBytes, "the whole of it fits");
            Assert.Contains("frame 2499", saved);
            Assert.Contains("outcome: timeout", saved);
            Assert.Contains("exitCode: 137", saved);
            Assert.Contains("attempt: 1", saved);
            Assert.Contains("cached: false", saved);
            Assert.Contains("bundle: " + bundle, saved);
            Assert.Contains("OpenXLR killed the process tree", saved);
            Assert.Contains("does not run anything", saved);
            Assert.Contains($"--- stderr ({output.Length} bytes, whole) ---", saved);
            Assert.Contains("--- stdout (0 bytes, whole) ---", saved);

            var failure = Assert.Single(PluginScanDiagnostics.Failures([ReportFor(kind)]));
            Assert.Equal(entry.LogId, failure.LogId);
            Assert.Contains("The scanner output was saved for the diagnostics archive.",
                PluginScanDiagnostics.Sentence([failure]));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ALogPastItsLimitsSaysHowMuchWasDroppedAndFromWhere()
    {
        string dir = Directory.CreateTempSubdirectory("plugin-scan-log-bounds-").FullName;
        string kind = Guid.NewGuid().ToString("N");
        try
        {
            string bundle = Path.Combine(dir, "Chatty.vst3");
            File.WriteAllText(bundle, "fixture");
            string output = WineFailureOutput(unwindLines: 12000);
            byte[] description = Encoding.UTF8.GetBytes("{\"plugins\":[" + new string('x', 200_000));
            Assert.True(output.Length > PluginScanLogStore.StreamCapBytes);
            var logs = new PluginScanLogStore(Path.Combine(dir, "logs"));
            // A cap breach: the runner stopped reading and killed the tree.
            ProcessResult Describe(string _) => new(0, description, output, false, true);

            HostScan.Run(kind, "unused", [dir], Vst3Catalog.Bundles, Describe,
                new ScanCache(Path.Combine(dir, "cache")), logs);

            var entry = Assert.Single(ReportFor(kind).Entries, e => e.Outcome == "output-limit");
            string saved = File.ReadAllText(Path.Combine(dir, "logs", entry.LogId + ".log"));
            Assert.Contains(Marker, saved);   // the head is kept, and the exception is in it
            Assert.Contains($"--- stderr ({PluginScanLogStore.StreamCapBytes} of {output.Length} kept, "
                + $"{output.Length - PluginScanLogStore.StreamCapBytes} dropped from the end by this log's limit", saved);
            Assert.Contains($"--- stdout ({PluginScanLogStore.StdoutCapBytes} of {description.Length} kept, "
                + $"{description.Length - PluginScanLogStore.StdoutCapBytes} dropped from the end by this log's limit", saved);
            Assert.Contains("the scan's own output limit stopped the read before the helper finished", saved);
            Assert.Contains("the scan output limit was reached", saved);
            Assert.True(new FileInfo(Path.Combine(dir, "logs", entry.LogId + ".log")).Length
                < PluginScanLogStore.StreamCapBytes + PluginScanLogStore.StdoutCapBytes + 4096);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ADeepTraceKeepsTheFirstFaultAndTheEndOfARepeatedUnwind()
    {
        string dir = Directory.CreateTempSubdirectory("plugin-wine-trace-").FullName;
        string? before = Environment.GetEnvironmentVariable(PluginHostEnvironment.WineTraceVariable);
        string kind = Guid.NewGuid().ToString("N");
        try
        {
            Environment.SetEnvironmentVariable(PluginHostEnvironment.WineTraceVariable, "1");
            string bundle = Path.Combine(dir, "Hangs.vst3");
            File.WriteAllText(bundle, "fixture");
            var output = new StringBuilder(WineFailureOutput(0));
            for (int i = 0; i < 50000; i++)
                output.Append($"21:04:27.{i:D6} [bridge] {i:X8}:fixme:unwind:execute_cfa_instructions unknown CFA opcode 2e at 000000001234\n");
            output.Append("err:virtual:virtual_setup_exception stack overflow\n");
            output.Append("trace: scan VST3 class 0: release\n");
            string stderr = output.ToString();
            Assert.InRange(Encoding.UTF8.GetByteCount(stderr), PluginScanLogStore.TraceStreamCapBytes + 1, PluginScanLogStore.TraceCaptureBytes);
            HostScan.Run(kind, "unused", [dir], Vst3Catalog.Bundles,
                _ => new(137, [], stderr, true, false), new ScanCache(Path.Combine(dir, "cache")),
                new PluginScanLogStore(Path.Combine(dir, "logs")));
            var entry = Assert.Single(ReportFor(kind).Entries, e => e.Outcome == "timeout");
            Assert.Equal(PluginScanDiagnostics.TraceDetailCharacters, entry.Detail!.Length);
            Assert.Contains(Marker, entry.Detail);
            string saved = File.ReadAllText(Path.Combine(dir, "logs", entry.LogId + ".log"));
            Assert.Contains(Marker, saved);
            Assert.Contains("start-up line 0", saved);
            Assert.Contains("[OpenXLR: 49997 further identical lines omitted]", saved);
            Assert.Contains("virtual_setup_exception stack overflow", saved);
            Assert.Contains("class 0: release", saved);
            Assert.Contains("wineTrace: true", saved);
            Assert.Contains("captureLimit: stderr 8388608 bytes", saved);
            Assert.Contains("limits: stderr 1048576 bytes", saved);
            Assert.DoesNotContain("dropped from the end", saved);
            Assert.True(saved.Length < 10000);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PluginHostEnvironment.WineTraceVariable, before);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ADeepTraceBeyondTheOrdinaryLimitReachesTheArchiveWithinItsBudgets()
    {
        string dir = Directory.CreateTempSubdirectory("plugin-trace-archive-").FullName;
        try
        {
            var logs = new PluginScanLogStore(Path.Combine(dir, "plugin-scan-logs"));
            string stderr = new string('x', 600 * 1024) + "\n" + WineFailureOutput(20000);
            var attempt = new PluginScanAttempt("vst3", "/plugins/Hangs.vst3", "timeout",
                DateTimeOffset.UtcNow, TimeSpan.FromSeconds(60), 137, true, false) { WineTrace = true };
            var saved = logs.Save(attempt, [], false, stderr, false);
            string path = Path.Combine(logs.Directory, saved.Id + ".log");
            string text = File.ReadAllText(path);
            Assert.Contains(Marker, text);
            Assert.Contains("dropped from the end by this log's limit", text);
            Assert.InRange(new FileInfo(path).Length, PluginScanLogStore.TraceStreamCapBytes,
                PluginScanLogStore.TraceStreamCapBytes + 16 * 1024);
            string archive = Path.Combine(dir, "archive");
            await OpenXLR.UI.Diagnostics.WriteScanLogsAsync(logs.Directory, null, archive, []);
            string collected = File.ReadAllText(Path.Combine(archive, "plugin-scan-logs", saved.Id + ".log"));
            Assert.Contains(Marker, collected);
            Assert.Contains("frame 1000", collected);
            Assert.DoesNotContain("the archive kept the first", collected);

            for (int i = 0; i < 6; i++)
                logs.Save(attempt with { Bundle = $"/plugins/Other{i}.vst3" }, [], false, stderr, false);
            Assert.InRange(Directory.GetFiles(logs.Directory, "*.log").Sum(f => new FileInfo(f).Length),
                1, PluginScanLogStore.MaxTotalBytes);
            // The collector enforces its own budget even on a directory the
            // daemon has not trimmed yet, including a file beyond its cap.
            for (int i = 0; i < 6; i++)
                File.WriteAllText(Path.Combine(logs.Directory, $"vst3-large{i}.log"), new string('z', 2 * 1024 * 1024));
            string bounded = Path.Combine(dir, "bounded-archive");
            await OpenXLR.UI.Diagnostics.WriteScanLogsAsync(logs.Directory, null, bounded, []);
            string[] files = Directory.GetFiles(Path.Combine(bounded, "plugin-scan-logs"), "*.log");
            Assert.All(files, f => Assert.InRange(new FileInfo(f).Length, 1, 1152 * 1024 + 128));
            Assert.InRange(files.Sum(f => new FileInfo(f).Length), 1, PluginScanLogStore.MaxTotalBytes + files.Length * 128);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("21:04:27.123 ")]
    [InlineData("[2026-09-17 21:04:27.123] [Plugin] ")]
    public void RepeatedLinesIgnoreTheEnvelopeButKeepDifferentFaults(string prefix)
    {
        var text = new StringBuilder();
        foreach (string payload in new[] { "opcode 2e at 00000123", "opcode 20 at 00000123", "opcode 20 at 00000456" })
            for (int i = 0; i < 366; i++)
                text.Append($"{prefix}12.123:{i:X4}:0040:fixme:unwind:execute_cfa_instructions unknown CFA {payload}\n");
        text.Append("err:seh:second exception\n");
        string collapsed = PluginScanLogStore.CollapseLines(text.ToString());
        Assert.Equal(3, collapsed.Split("[OpenXLR: 363 further identical lines omitted]").Length - 1);
        Assert.Contains("opcode 2e at 00000123", collapsed);
        Assert.Contains("opcode 20 at 00000123", collapsed);
        Assert.Contains("opcode 20 at 00000456", collapsed);
        Assert.EndsWith("err:seh:second exception\n", collapsed);
        Assert.Equal("one\ntwo\nthree", PluginScanLogStore.CollapseLines("one\ntwo\nthree"));
        Assert.Equal("", PluginScanLogStore.CollapseLines(""));
        Assert.Equal("one\r\ntwo\r\nthree", PluginScanLogStore.CollapseLines("one\r\ntwo\r\nthree"));
        Assert.Equal("one\rtwo\rthree", PluginScanLogStore.CollapseLines("one\rtwo\rthree"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedByteLimitsAlsoBoundUnicodeAndHeaderValues(bool trace)
    {
        string output = "first exception\n" + string.Concat(Enumerable.Repeat("界🙂", 300000));
        var attempt = new PluginScanAttempt("vst3", output, "timeout", DateTimeOffset.UtcNow, TimeSpan.Zero,
            137, true, false, Detail: output) { WineTrace = trace, WineDebug = output };
        string saved = PluginScanLogStore.Compose("vst3-1234", attempt, 1, [], false, output, false);
        Assert.Contains("first exception", saved);
        Assert.DoesNotContain("\uFFFD", saved);
        int limit = trace ? PluginScanLogStore.TraceStreamCapBytes : PluginScanLogStore.StreamCapBytes;
        Assert.InRange(Encoding.UTF8.GetByteCount(saved), limit, limit + 16 * 1024);
    }

    [Fact]
    public void EveryFailedOutcomeWithScannerOutputIsSavedAndNoSuccessfulOneIs()
    {
        string dir = Directory.CreateTempSubdirectory("plugin-scan-log-outcomes-").FullName;
        string kind = Guid.NewGuid().ToString("N");
        string logDir = Path.Combine(dir, "logs");
        try
        {
            string[] names = ["good", "failed", "timeout", "large", "invalid", "empty"];
            string[] bundles = [.. names.Select(n => Path.Combine(dir, n + ".vst3"))];
            foreach (string path in bundles) File.WriteAllText(path, "fixture");
            ProcessResult Describe(string path) => Path.GetFileNameWithoutExtension(path) switch
            {
                "failed" => new(9, [], "the scanner failed: " + Marker, false, false),
                "timeout" => new(-1, [], "hung while loading", true, false),
                "large" => new(0, [], "too much output", false, true),
                "invalid" => new(0, Encoding.UTF8.GetBytes("{bad json"), "warning: odd module", false, false),
                "empty" => new(0, Encoding.UTF8.GetBytes("{\"plugins\":[]}"), "no factory", false, false),
                _ => new(0, Encoding.UTF8.GetBytes(
                    "{\"plugins\":[{\"id\":\"good\",\"name\":\"EQ\",\"audioIns\":2,\"audioOuts\":2}]}"), "", false, false),
            };
            var logs = new PluginScanLogStore(logDir);

            // A missing Windows module is the one failing outcome this cannot
            // reach, since it is only decided for the real "vst3" format, whose
            // evidence another test class owns; PluginSourceRecoveryTests has it.
            HostScan.Run(kind, "unused", [dir], (_, _) => bundles, Describe,
                new ScanCache(Path.Combine(dir, "cache")), logs);
            var entries = ReportFor(kind).Entries
                .ToDictionary(e => Path.GetFileNameWithoutExtension(e.Path.TrimEnd('/')), e => e, StringComparer.Ordinal);

            foreach (string failing in new[] { "failed", "timeout", "large", "invalid" })
            {
                Assert.NotNull(entries[failing].LogId);
                Assert.True(File.Exists(Path.Combine(logDir, entries[failing].LogId + ".log")), failing);
            }
            Assert.Contains(Marker, File.ReadAllText(Path.Combine(logDir, entries["failed"].LogId + ".log")));
            Assert.Contains("exitCode: 9", File.ReadAllText(Path.Combine(logDir, entries["failed"].LogId + ".log")));
            // A helper whose exit status could not be read says so rather than inventing one.
            Assert.Contains("exitCode: unknown", File.ReadAllText(Path.Combine(logDir, entries["timeout"].LogId + ".log")));
            // The malformed description is the evidence, so stdout carries it.
            string invalid = File.ReadAllText(Path.Combine(logDir, entries["invalid"].LogId + ".log"));
            Assert.Contains("{bad json", invalid);
            Assert.Contains("warning: odd module", invalid);
            Assert.Contains("detail: ", invalid);

            // A bundle that answered, and one that answered with nothing, are
            // not failures and leave nothing behind.
            Assert.Null(entries["good"].LogId);
            Assert.Null(entries["empty"].LogId);
            Assert.Equal(4, Directory.GetFiles(logDir, "*.log").Length);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ACachedDescriptionThatWillNotParseIsSavedAsSuchWithoutAnotherLaunch()
    {
        string dir = Directory.CreateTempSubdirectory("plugin-scan-log-cached-").FullName;
        string kind = Guid.NewGuid().ToString("N");
        try
        {
            string bundle = Path.Combine(dir, "Stale.vst3");
            File.WriteAllText(bundle, "fixture");
            var cache = new ScanCache(Path.Combine(dir, "cache"));
            cache.Store(bundle, Encoding.UTF8.GetBytes("{\"plugins\":[{\"id\":"));
            cache.Save();
            var logs = new PluginScanLogStore(Path.Combine(dir, "logs"));
            int launches = 0;
            ProcessResult Describe(string _) { launches++; return new(0, [], "", false, false); }

            HostScan.Run(kind, "unused", [dir], Vst3Catalog.Bundles, Describe,
                new ScanCache(Path.Combine(dir, "cache")), logs);

            Assert.Equal(0, launches);
            var entry = Assert.Single(ReportFor(kind).Entries, e => e.Outcome == "invalid-description");
            string saved = File.ReadAllText(Path.Combine(dir, "logs", entry.LogId + ".log"));
            Assert.Contains("cached: true", saved);
            Assert.Contains("no process ran: these bytes came from the scan cache", saved);
            Assert.Contains("exitCode: unknown", saved);
            Assert.Contains("{\"plugins\":[{\"id\":", saved);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// A bundle that hangs the scanner costs the whole deadline, and the
    /// field report that prompted this had three of them: every catalogue the
    /// daemon built on its own spent three minutes to end up where it started.
    /// It is asked once, then passed by until it changes or the user rescans.
    /// </summary>
    [Fact]
    public void ABundleThatFailedIsSkippedUntilItChangesOrTheUserRescans()
    {
        string dir = Directory.CreateTempSubdirectory("plugin-scan-skip-").FullName;
        string kind = Guid.NewGuid().ToString("N");
        try
        {
            string bundle = Path.Combine(dir, "Hangs.vst3");
            File.WriteAllText(bundle, "fixture");
            string cacheDir = Path.Combine(dir, "cache");
            int calls = 0;
            ProcessResult Timeout(string _) { calls++; return new(137, [], "hung", true, false); }

            // The first scan pays for it; the ones the daemon runs by itself
            // do not, even with the cache reloaded from disk.
            Assert.Empty(HostScan.Run(kind, "unused", [dir], Vst3Catalog.Bundles, Timeout, new ScanCache(cacheDir)));
            Assert.Empty(HostScan.Run(kind, "unused", [dir], Vst3Catalog.Bundles, Timeout, new ScanCache(cacheDir)));
            Assert.Equal(1, calls);
            var report = PluginScanDiagnostics.Snapshot().Single(r => r.Kind == kind);
            Assert.Single(report.Entries, e => e.Outcome == "skipped-failed");
            // The skip is evidence, not a warning: the archive shows it, and
            // the window says nothing a second time.
            Assert.Empty(PluginScanDiagnostics.Failures([report]));

            // A rescan asks again.
            Assert.Empty(HostScan.Run(kind, "unused", [dir], Vst3Catalog.Bundles, Timeout,
                new ScanCache(cacheDir), retryFailures: true));
            Assert.Equal(2, calls);

            // So does the bundle changing on disk.
            File.WriteAllText(bundle, "fixture, rebuilt");
            Assert.Empty(HostScan.Run(kind, "unused", [dir], Vst3Catalog.Bundles, Timeout, new ScanCache(cacheDir)));
            Assert.Equal(3, calls);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// Each bundle can take a minute, so a scan that is stopped halfway must
    /// not lose what it already learnt: a daemon started again would spend
    /// the same minutes on the same bundles, and a hanging one would be asked
    /// again at every start.
    /// </summary>
    [Fact]
    public void EachScannedBundleIsInTheCacheBeforeTheNextOneIsAsked()
    {
        string dir = Directory.CreateTempSubdirectory("plugin-scan-cache-progress-").FullName;
        string kind = Guid.NewGuid().ToString("N");
        try
        {
            string described = Path.Combine(dir, "a.vst3"), hangs = Path.Combine(dir, "b.vst3"), last = Path.Combine(dir, "c.vst3");
            foreach (string bundle in new[] { described, hangs, last }) File.WriteAllText(bundle, "fixture");
            string cacheDir = Path.Combine(dir, "cache");
            bool knewDescribed = false, knewHang = false;
            ProcessResult Describe(string path)
            {
                if (path == hangs) return new(137, [], "hung", true, false);
                if (path == last)
                {
                    var onDisk = new ScanCache(cacheDir);
                    knewDescribed = onDisk.Lookup(described) is not null;
                    knewHang = onDisk.KnownFailure(hangs);
                }
                string id = Path.GetFileNameWithoutExtension(path);
                return new(0, Encoding.UTF8.GetBytes($"{{\"plugins\":[{{\"id\":\"{id}\",\"name\":\"EQ\",\"audioIns\":2,\"audioOuts\":2}}]}}"), "", false, false);
            }

            Assert.Equal(2, HostScan.Run(kind, "unused", [dir], Vst3Catalog.Bundles, Describe, new ScanCache(cacheDir)).Count);
            Assert.True(knewDescribed, "a described bundle was not saved before the next scan");
            Assert.True(knewHang, "a failed bundle was not saved before the next scan");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ABundleThatFailsAgainReplacesItsOwnLogAndCountsTheAttempt()
    {
        string dir = Directory.CreateTempSubdirectory("plugin-scan-log-attempts-").FullName;
        string kind = Guid.NewGuid().ToString("N");
        string logDir = Path.Combine(dir, "logs");
        try
        {
            string bundle = Path.Combine(dir, "Repeat.vst3");
            File.WriteAllText(bundle, "fixture");
            var logs = new PluginScanLogStore(logDir);
            int round = 0;
            ProcessResult Describe(string _) => new(9, [], "failure round " + ++round, false, false);

            // Three rescans, since only a scan the user asked for looks at a
            // bundle that already failed. Each one counts a new attempt.
            for (int i = 0; i < 3; i++)
                HostScan.Run(kind, "unused", [dir], Vst3Catalog.Bundles, Describe,
                    new ScanCache(Path.Combine(dir, "cache")), logs, retryFailures: true);

            Assert.Single(Directory.GetFiles(logDir, "*.log"));
            string saved = File.ReadAllText(Path.Combine(logDir, PluginScanLogStore.IdFor(kind, bundle) + ".log"));
            Assert.Contains("attempt: 3", saved);
            Assert.Contains("failure round 3", saved);
            Assert.DoesNotContain("failure round 2", saved);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ALongBundleHeaderStillCountsThePreviousAttempt()
    {
        string dir = Directory.CreateTempSubdirectory("plugin-long-header-").FullName;
        try
        {
            var logs = new PluginScanLogStore(dir);
            var attempt = new PluginScanAttempt("vst3", "/plugins/" + new string('x', 3000), "timeout",
                DateTimeOffset.UtcNow, TimeSpan.Zero, 137, true, false);
            logs.Save(attempt, [], false, "failure", false);
            var saved = logs.Save(attempt, [], false, "failure", false);
            string text = File.ReadAllText(Path.Combine(dir, saved.Id + ".log"));
            Assert.Contains(" [truncated]\n", text);
            Assert.Contains("attempt: 2\n", text);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void RetentionKeepsTheNewestLogsWithinBothBounds()
    {
        string dir = Directory.CreateTempSubdirectory("plugin-scan-log-retention-").FullName;
        try
        {
            var logs = new PluginScanLogStore(dir);
            var written = new List<string>();
            for (int i = 0; i < PluginScanLogStore.MaxFiles + 6; i++)
            {
                var attempt = new PluginScanAttempt("vst3", $"/plugins/Bundle{i}.vst3", "scan-failed",
                    DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), 9, false, false);
                PluginScanLogRef saved = logs.Save(attempt, [], false, "failure " + i, false);
                Assert.NotNull(saved.Id);
                written.Add(saved.Id!);
                // The newest is the one kept, and mtime is what orders them.
                File.SetLastWriteTimeUtc(Path.Combine(dir, saved.Id + ".log"), DateTime.UnixEpoch.AddMinutes(i));
            }
            logs.Trim();

            string[] kept = [.. Directory.GetFiles(dir, "*.log").Select(Path.GetFileNameWithoutExtension)!];
            Assert.Equal(PluginScanLogStore.MaxFiles, kept.Length);
            Assert.All(written.TakeLast(PluginScanLogStore.MaxFiles), id => Assert.Contains(id, kept));
            Assert.All(written.Take(6), id => Assert.DoesNotContain(id, kept));

            // A directory already past the byte bound is trimmed to it, newest first.
            foreach (string id in kept) File.WriteAllBytes(Path.Combine(dir, id + ".log"), new byte[512 * 1024]);
            logs.Trim();
            long total = Directory.GetFiles(dir, "*.log").Sum(f => new FileInfo(f).Length);
            Assert.True(total <= PluginScanLogStore.MaxTotalBytes, $"{total} bytes kept");
            Assert.NotEmpty(Directory.GetFiles(dir, "*.log"));

            // An interrupted write leaves a temporary file; an old one is
            // cleared, and one another process may still be writing is not.
            string abandoned = Path.Combine(dir, "vst3-0123456789abcdef.log.9999.tmp");
            string inFlight = Path.Combine(dir, "vst3-fedcba9876543210.log.9998.tmp");
            File.WriteAllText(abandoned, "half written");
            File.WriteAllText(inFlight, "half written");
            File.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow.AddHours(-2));
            logs.Trim();
            Assert.False(File.Exists(abandoned));
            Assert.True(File.Exists(inFlight));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AStoreThatCannotWriteLeavesTheScanAndItsErrorIntact()
    {
        string dir = Directory.CreateTempSubdirectory("plugin-scan-log-unwritable-").FullName;
        string kind = Guid.NewGuid().ToString("N");
        try
        {
            string bundle = Path.Combine(dir, "Blocked.vst3");
            File.WriteAllText(bundle, "fixture");
            // A file where the directory would have to be: nothing can be written under it.
            string blocker = Path.Combine(dir, "not-a-directory");
            File.WriteAllText(blocker, "in the way");
            var logs = new PluginScanLogStore(Path.Combine(blocker, "logs"));
            ProcessResult Describe(string _) => new(9, [], "the original error " + Marker, false, false);

            var found = HostScan.Run(kind, "unused", [dir], Vst3Catalog.Bundles, Describe,
                new ScanCache(Path.Combine(dir, "cache")), logs);

            Assert.Empty(found);
            var entry = Assert.Single(ReportFor(kind).Entries, e => e.Outcome == "scan-failed");
            Assert.Null(entry.LogId);
            Assert.StartsWith("scanner output was not saved: ", entry.LogNote);
            // The error the scan was recording survives the failure to save it.
            Assert.Contains(Marker, entry.Detail);
            Assert.Equal(9, entry.ExitCode);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>A path is whatever the file system allows, and a header field is one line.</summary>
    [Fact]
    public void AHeaderValueCannotWriteAnotherHeaderLine()
    {
        string composed = PluginScanLogStore.Compose("vst3-0000111122223333",
            new PluginScanAttempt("vst3", "/plugins/odd\nattempt: 4242\nname.vst3", "scan-failed",
                DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(2), 9, false, false,
                Detail: "first line\r\nsecond line"),
            1, [], false, "output", false);

        Assert.Contains("bundle: /plugins/odd attempt: 4242 name.vst3\n", composed);
        Assert.Contains("attempt: 1\n", composed);
        Assert.DoesNotContain("\nattempt: 4242", composed);
        Assert.Contains("detail: first line second line\n", composed);
    }

    [Fact]
    public void AnIdIsStableForABundleAndSafeAsAFileName()
    {
        string first = PluginScanLogStore.IdFor("vst3", "/home/a/.vst3/TDR Kotelnikov.vst3");
        Assert.Equal(first, PluginScanLogStore.IdFor("vst3", "/home/a/.vst3/TDR Kotelnikov.vst3"));
        Assert.NotEqual(first, PluginScanLogStore.IdFor("clap", "/home/a/.vst3/TDR Kotelnikov.vst3"));
        Assert.NotEqual(first, PluginScanLogStore.IdFor("vst3", "/home/a/.vst3/Other.vst3"));
        Assert.StartsWith("vst3-", first);
        Assert.All(first, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c == '-', first));
        Assert.True(PluginScanLogStore.IsLogName(first + ".log"));

        // Nothing that could leave the directory, name another file or hide.
        foreach (string name in new[] { "../escape.log", "a/b.log", ".log", "plain.txt", "log", "", "x.log.tmp" })
            Assert.False(PluginScanLogStore.IsLogName(name), name);
    }

    [Fact]
    public void TheDefaultDirectorySitsUnderTheUserCacheBesideTheScanCache()
    {
        string logs = PluginScanLogStore.DefaultDirectory;
        Assert.Equal("plugin-scan-logs", Path.GetFileName(logs));
        Assert.Equal(Path.GetDirectoryName(ScanCache.DefaultDirectory), Path.GetDirectoryName(logs));
    }

    /// <summary>An entry an older OpenXLR recorded has neither field and still reads.</summary>
    [Fact]
    public void EvidenceWithoutALogStillLoadsAndReportsItsFailure()
    {
        var capture = new PluginScanDiagnostics.Capture("older-record-test");
        capture.Add("/plugins/Old.vst3", "timeout", exitCode: 137, detail: "clipped output");
        PluginScanReport report = capture.Complete();

        var entry = Assert.Single(report.Entries);
        Assert.Null(entry.LogId);
        Assert.Null(entry.LogNote);
        var failure = Assert.Single(PluginScanDiagnostics.Failures([report]));
        Assert.Null(failure.LogId);
        Assert.Equal("1 bundle could not be read: Old.vst3 (timed out); the daemon's log says more.",
            PluginScanDiagnostics.Sentence([failure]));

        string json = System.Text.Json.JsonSerializer.Serialize(report,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        using var parsed = System.Text.Json.JsonDocument.Parse(json);
        var serialized = parsed.RootElement.GetProperty("entries")[0];
        Assert.Equal(System.Text.Json.JsonValueKind.Null, serialized.GetProperty("logId").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, serialized.GetProperty("logNote").ValueKind);
    }
}
