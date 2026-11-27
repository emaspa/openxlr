using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using OpenXLR.Core.Mixing;
using Xunit.Abstractions;

namespace OpenXLR.Tests;

/// <summary>
/// The LV2 catalogue read through the native host's scanner: the same
/// catalogue the in-process reading gives, and a scanner that crashes,
/// stalls or prints the wrong thing costs that scan and nothing else.
/// </summary>
public sealed class Lv2ScanHelperTests(ITestOutputHelper output)
{
    private static bool LilvInstalled()
    {
        if (!NativeLibrary.TryLoad("liblilv-0.so.0", out IntPtr library)) return false;
        NativeLibrary.Free(library);
        return true;
    }

    /// <summary>
    /// Everything a catalogue entry holds, the JSON-ignored latency flag
    /// included. lilv keeps a plugin's required features and a port's scale
    /// points in collections ordered by where each node sits in memory, so
    /// their order differs between two processes reading the same bundle;
    /// they are compared in a fixed order.
    /// </summary>
    private static string Snapshot(IReadOnlyList<PluginInfo> plugins)
        => JsonSerializer.Serialize(plugins.Select(p => p with
        {
            RequiredFeatures = [.. p.RequiredFeatures.Order(StringComparer.Ordinal)],
            UnsupportedFeatures = [.. p.UnsupportedFeatures.Order(StringComparer.Ordinal)],
            NativeUiRequiredFeatures = [.. p.NativeUiRequiredFeatures.Order(StringComparer.Ordinal)],
            Params = [.. p.Params.Select(q => q with { ScalePoints = [.. q.ScalePoints.OrderBy(x => x.Value).ThenBy(x => x.Label, StringComparer.Ordinal)] })],
        }).Select(p => new { p, p.ReportsLatency }));

    [Fact]
    public void TheScannerAndTheInProcessReadingGiveTheSameCatalogue()
    {
        if (!LilvInstalled() || !NativePluginHost.HostInstalled) return;   // a build without the native host
        string directory = Directory.CreateTempSubdirectory("openxlr-lv2-parity-").FullName;
        try
        {
            WriteBundles(directory);
            IReadOnlyList<PluginInfo> inProcess = Lv2Catalog.InProcess(directory);
            Lv2Catalog.Lv2Scan scan = Lv2Catalog.FromHelper(NativePluginHost.Executable, directory, null, Kind());
            Assert.True(scan.Complete);
            IReadOnlyList<PluginInfo> scanned = scan.Plugins;
            Assert.Equal(Snapshot(inProcess), Snapshot(scanned));

            PluginInfo rich = Assert.Single(scanned, p => p.Plugin == "urn:openxlr:test:rich");
            Assert.Equal("Rich é \"quoted\" \\ test\t", rich.Name);
            Assert.Equal(["in_l", "in_r"], rich.InputSymbols);
            Assert.Equal(["out_l", "out_r"], rich.OutputSymbols);
            Assert.Equal(["mode", "on", "freq", "nodefault"], rich.Params.Select(p => p.Symbol));
            PluginParam mode = rich.Params[0];
            Assert.True(mode.Integer && mode.Enumeration && !mode.Toggled);
            Assert.Equal(["Off", "On", "Auto"], mode.ScalePoints.OrderBy(p => p.Value).Select(p => p.Label));
            Assert.True(rich.Params[1].Toggled);
            Assert.True(rich.Params[2].Logarithmic);
            Assert.Equal((double)0.1f, rich.Params[2].Default);
            Assert.Equal((0.0, 1.0, 0.0), (rich.Params[3].Min, rich.Params[3].Max, rich.Params[3].Default));
            Assert.True(rich.ReportsLatency);
            Assert.True(rich.HasNativeUi);
            Assert.Equal(["http://lv2plug.in/ns/extensions/ui#idleInterface"], rich.NativeUiRequiredFeatures);
            Assert.Equal(["http://example.org/feature#x"], rich.UnsupportedFeatures);
            Assert.DoesNotContain(scanned, p => p.Plugin is "urn:openxlr:test:generator" or "urn:openxlr:test:monster"
                || p.Plugin.StartsWith("http://kxstudio.sf.net/carla", StringComparison.Ordinal));
            Assert.DoesNotContain(scanned, p => p.Plugin is "urn:openxlr:test:bytes" or "urn:openxlr:test:broken");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>
    /// The plugins installed on the machine running the tests, read both
    /// ways. Nothing is asserted about what is installed, only that the two
    /// readings agree on it.
    /// </summary>
    [Fact]
    public void TheInstalledPluginsReadTheSameBothWays()
    {
        if (!LilvInstalled() || !NativePluginHost.HostInstalled) return;
        IReadOnlyList<PluginInfo> inProcess = Lv2Catalog.InProcess(null);
        Lv2Catalog.Lv2Scan scan = Lv2Catalog.FromHelper(NativePluginHost.Executable, null, null, Kind());
        Assert.True(scan.Complete);
        IReadOnlyList<PluginInfo> scanned = scan.Plugins;
        output.WriteLine($"{scanned.Count} plugins, {scanned.Sum(p => p.Params.Count)} controls, "
            + $"{scanned.Sum(p => p.Params.Sum(q => q.ScalePoints.Count))} scale points, "
            + $"{scanned.Count(p => p.HasNativeUi)} with an X11 UI, {scanned.Count(p => p.ReportsLatency == true)} reporting latency, "
            + $"{scanned.Count(p => !p.Supported)} unsupported");
        Assert.Equal(inProcess.Select(p => p.Plugin), scanned.Select(p => p.Plugin));
        Assert.Equal(Snapshot(inProcess), Snapshot(scanned));
    }

    [Fact]
    public void AFakeScannersCatalogueIsHeldToTheCatalogueRules()
    {
        if (!OperatingSystem.IsLinux()) return;
        string directory = Directory.CreateTempSubdirectory("openxlr-lv2-fake-").FullName;
        try
        {
            string json = """
                {"plugins":[
                 {"uri":"urn:a","name":"Alpha","class":"Delay","latency":true,"params":[
                   {"symbol":"time","name":"Time","min":0,"max":2,"default":5,"toggled":false,"integer":false,"logarithmic":true,"enum":false,
                    "points":[{"label":"Short","value":0.25},{"label":"Bad","value":"x"},{"value":1}]},
                   {"symbol":"wide","name":"Wide","min":1e39,"max":2,"default":0},
                   {"symbol":"upside","name":"Upside","min":3,"max":2,"default":2}],
                  "audioIns":1,"audioOuts":1,"inputs":["in"],"outputs":["out"],"features":["http://lv2plug.in/ns/ext/urid#map"],
                  "ui":{"features":["http://lv2plug.in/ns/extensions/ui#parent"]}},
                 {"uri":"urn:a","name":"Duplicate","audioIns":1,"audioOuts":1,"inputs":["in"],"outputs":["out"]},
                 {"uri":"urn:generator","name":"Tone","audioIns":0,"audioOuts":1,"inputs":[],"outputs":["out"]},
                 {"uri":"http://kxstudio.sf.net/carla/plugins/rack","name":"Rack","audioIns":2,"audioOuts":2,"inputs":["a","b"],"outputs":["c","d"]},
                 {"name":"No URI","audioIns":1,"audioOuts":1,"inputs":["in"],"outputs":["out"]},
                 {"uri":"urn:b","audioIns":2,"audioOuts":2,"inputs":["l","r"],"outputs":["l","r"]}
                ]}
                """;
            string scanner = Fake(directory, $"cat <<'EOF'\n{json}\nEOF\necho 'trace: scan LV2: complete' >&2\n");
            Lv2Catalog.Lv2Scan scan = Lv2Catalog.FromHelper(scanner, directory, null, Kind());
            Assert.True(scan.Complete);
            IReadOnlyList<PluginInfo> plugins = scan.Plugins;
            Assert.Equal(["urn:a", "urn:b"], plugins.Select(p => p.Plugin));
            PluginInfo alpha = plugins[0];
            Assert.Equal(("Alpha", "Delay", true, true), (alpha.Name, alpha.Category, alpha.ReportsLatency == true, alpha.HasNativeUi));
            PluginParam time = Assert.Single(alpha.Params);
            Assert.Equal((0.0, 2.0, 2.0, true), (time.Min, time.Max, time.Default, time.Logarithmic));
            Assert.Equal([new ScalePoint("Short", 0.25)], time.ScalePoints);
            Assert.Equal(["http://lv2plug.in/ns/extensions/ui#parent"], alpha.NativeUiRequiredFeatures);
            Assert.Empty(alpha.UnsupportedFeatures);
            Assert.Equal("urn:b", plugins[1].Name);
            Assert.Equal(("l", "l"), (plugins[1].InputSymbol, plugins[1].OutputSymbol));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("crash", "scan-failed")]
    [InlineData("hang", "timeout")]
    [InlineData("garbage", "invalid-description")]
    [InlineData("truncated", "invalid-description")]
    [InlineData("trailing", "invalid-description")]
    [InlineData("list", "invalid-description")]
    [InlineData("flood", "output-limit")]
    [InlineData("failure", "scan-failed")]
    public void AScannerThatFailsCompletesNoCatalogueAndSaysWhy(string behaviour, string outcome)
    {
        if (!OperatingSystem.IsLinux()) return;
        string directory = Directory.CreateTempSubdirectory("openxlr-lv2-broken-").FullName;
        string kind = Kind();
        try
        {
            string markers = "echo 'trace: scan LV2: load bundles' >&2\n"
                + "echo 'trace: scan LV2 plugin 0: bundle /plugins/good.lv2/' >&2\n"
                + "echo 'lilv: error: trace: scan LV2 plugin 9: bundle /not/a/marker.lv2/' >&2\n"
                + "echo 'trace: scan LV2 plugin 1: bundle /plugins/broken.lv2/' >&2\n";
            string body = behaviour switch
            {
                "crash" => markers + "printf '{\"plugins\":['\nkill -SEGV $$\n",
                "hang" => markers + "exec sleep 30\n",
                "garbage" => "echo 'not a catalogue'\n",
                "truncated" => "printf '{\"plugins\":[{\"uri\":\"urn:a\"'\n",
                "trailing" => "printf '{\"plugins\":[]} {\"plugins\":[]}'\n",
                "list" => "printf '[]'\n",
                "flood" => markers + "head -c 4096 /dev/zero | tr '\\0' ' '\n",
                _ => markers + "echo 'out of memory' >&2\nexit 3\n",
            };
            var logs = new PluginScanLogStore(Path.Combine(directory, "logs"));
            // One run: running again without the bundle is the next tests' subject.
            Lv2Catalog.Lv2Scan scan = Lv2Catalog.FromHelper(Fake(directory, body), "/plugins", logs, kind,
                timeout: TimeSpan.FromSeconds(behaviour == "hang" ? 1 : 10), stdoutCap: 1024, maxRetries: 0);
            Assert.False(scan.Complete);
            Assert.Empty(scan.Plugins);
            PluginScanEntry entry = Assert.Single(PluginScanDiagnostics.Snapshot().Single(r => r.Kind == kind).Entries);
            Assert.Equal(outcome, entry.Outcome);
            // A crash or a stall is put down to the bundle being read; output
            // past the cap and unreadable output are the whole scan's.
            Assert.Equal(behaviour is "crash" or "hang" or "failure" ? "/plugins/broken.lv2/" : "/plugins", entry.Path);
            if (behaviour == "failure")
            {
                Assert.Equal(3, entry.ExitCode);
                Assert.Contains("out of memory", entry.Detail);
            }
            Assert.NotNull(entry.LogId);
            Assert.Single(PluginScanDiagnostics.Failures(PluginScanDiagnostics.Snapshot().Where(r => r.Kind == kind)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void AnEmptyCatalogueIsAnAnswerAndAMissingScannerIsNot()
    {
        if (!OperatingSystem.IsLinux()) return;
        string directory = Directory.CreateTempSubdirectory("openxlr-lv2-empty-").FullName;
        try
        {
            string kind = Kind();
            Lv2Catalog.Lv2Scan scan = Lv2Catalog.FromHelper(Fake(directory, "echo '{\"plugins\":[]}'\n"), directory, null, kind);
            Assert.True(scan.Complete);
            Assert.Empty(scan.Plugins);
            Assert.Equal("no-plugins", Assert.Single(PluginScanDiagnostics.Snapshot().Single(r => r.Kind == kind).Entries).Outcome);

            kind = Kind();
            Assert.False(Lv2Catalog.FromHelper(Path.Combine(directory, "missing"), directory, null, kind).Complete);
            Assert.Equal("start-error", Assert.Single(PluginScanDiagnostics.Snapshot().Single(r => r.Kind == kind).Entries).Outcome);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void AFailedScanKeepsTheLastCatalogueAScanCompleted()
    {
        IReadOnlyList<PluginInfo>? lastGood = null;
        PluginInfo plugin = new("lv2", "urn:a", "A", "", 1, 1, "in", "out", [], [], ["in"], ["out"]);
        PluginInfo other = plugin with { Plugin = "urn:b" };
        Assert.Empty(Lv2Catalog.KeepLastGood(ref lastGood, new([], false)));
        // Right after the daemon starts, what a failed scan did describe.
        IReadOnlyList<PluginInfo> described = [other];
        Assert.Same(described, Lv2Catalog.KeepLastGood(ref lastGood, new(described, false)));
        Assert.Null(lastGood);
        IReadOnlyList<PluginInfo> first = [plugin];
        Assert.Same(first, Lv2Catalog.KeepLastGood(ref lastGood, new(first, true)));
        Assert.Same(first, Lv2Catalog.KeepLastGood(ref lastGood, new([], false)));
        Assert.Same(first, Lv2Catalog.KeepLastGood(ref lastGood, new(described, false)));
        IReadOnlyList<PluginInfo> empty = [];
        Assert.Same(empty, Lv2Catalog.KeepLastGood(ref lastGood, new(empty, true)));
        Assert.Same(empty, Lv2Catalog.KeepLastGood(ref lastGood, new(described, false)));
    }

    [Fact]
    public void OnlyTheLastMarkerAtTheStartOfALineNamesTheBundle()
    {
        Assert.Null(Lv2Catalog.Culprit(""));
        Assert.Null(Lv2Catalog.Culprit("trace: scan LV2: load bundles\n"));
        Assert.Equal("/a.lv2/", Lv2Catalog.Culprit("trace: scan LV2 plugin 0: bundle /a.lv2/"));
        Assert.Equal("/a.lv2/", Lv2Catalog.Culprit("trace: scan LV2 manifest 12: bundle /a.lv2/\n"));
        Assert.Equal("/a.lv2/", Lv2Catalog.Culprit("trace: scan LV2 plugin 0: bundle /a.lv2/\nwarning: trace: scan LV2 plugin 1: bundle /b.lv2/\n"));
        Assert.Equal("/na%C3%AFve.lv2/", Lv2Catalog.Culprit("trace: scan LV2 plugin 3: bundle /na%C3%AFve.lv2/\n"));
        Assert.Null(Lv2Catalog.Culprit("trace: scan LV2 plugin 0: bundle \n"));
        Assert.Null(Lv2Catalog.Culprit("trace: scan LV2 plugin x: bundle /a.lv2/\n"));
        // Past a bundle's marker, a later step names no bundle.
        Assert.Null(Lv2Catalog.Culprit("trace: scan LV2 manifest 0: bundle /a.lv2/\ntrace: scan LV2: specifications and plugin classes\n"));
        Assert.Null(Lv2Catalog.Culprit("trace: scan LV2 plugin 0: bundle /a.lv2/\ntrace: scan LV2: finish\n"));
        Assert.Null(Lv2Catalog.Culprit("trace: scan LV2 manifest 0: bundle /a.lv2/\ntrace: scan LV2: skip bundle /b.lv2/\n"));
    }

    [Fact]
    public void AMarkerTextIsThePathEscapedAndReadsBack()
    {
        Assert.Equal("/usr/lib/lv2/a b.lv2/", Lv2Catalog.MarkerText("/usr/lib/lv2/a b.lv2/"));
        Assert.Equal("/lv2/na%C3%AFve%25%0A.lv2/", Lv2Catalog.MarkerText("/lv2/naïve%\n.lv2/"));
        foreach (string path in new[] { "/usr/lib/lv2/a b.lv2/", "/lv2/naïve%\n.lv2/", "/lv2/50%.lv2/", "/lv2/日本.lv2/" })
            Assert.Equal(path, Lv2Catalog.MarkerPath(Lv2Catalog.MarkerText(path)));
        // A stray % that is not an escape is kept as written.
        Assert.Equal("/lv2/50%zz/", Lv2Catalog.MarkerPath("/lv2/50%zz/"));
        // Clipped where the helper clips, never inside an escape.
        string text = Lv2Catalog.MarkerText("/" + new string('a', 509) + "é");
        Assert.Equal(510, text.Length);
        Assert.Equal(Lv2Catalog.MaxMarkerBytes, Lv2Catalog.MarkerText(new string('b', 600)).Length);
    }

    [Fact]
    public void APartialCatalogueKeepsEveryFinishedPlugin()
    {
        const string one = "{\"uri\":\"urn:a\",\"name\":\"A\",\"audioIns\":1,\"audioOuts\":1,\"inputs\":[\"in\"],\"outputs\":[\"out\"]}";
        const string two = "{\"uri\":\"urn:b\",\"name\":\"B\",\"params\":[{\"symbol\":\"x\",\"min\":0,\"max\":1}],\"audioIns\":1,\"audioOuts\":1,\"inputs\":[\"in\"],\"outputs\":[\"out\"]}";
        Assert.Empty(Lv2Catalog.ParsePartial(""u8));
        Assert.Empty(Lv2Catalog.ParsePartial("{\"plugins\":["u8));
        Assert.Equal(["urn:a"], Lv2Catalog.ParsePartial(Encoding.UTF8.GetBytes("{\"plugins\":[" + one)).Select(p => p.Plugin));
        // The plugin being written when the helper stopped is left out,
        // however much of it was already there.
        for (int cut = 1; cut < two.Length; cut++)
            Assert.Equal(["urn:a"], Lv2Catalog.ParsePartial(Encoding.UTF8.GetBytes("{\"plugins\":[" + one + "," + two[..cut])).Select(p => p.Plugin));
        IReadOnlyList<PluginInfo> both = Lv2Catalog.ParsePartial(Encoding.UTF8.GetBytes("{\"plugins\":[" + two + "," + one + ","));
        Assert.Equal(["urn:a", "urn:b"], both.Select(p => p.Plugin));
        Assert.Equal("x", Assert.Single(both[1].Params).Symbol);
        Assert.Equal(["urn:a"], Lv2Catalog.ParsePartial(Encoding.UTF8.GetBytes("{\"plugins\":[" + one + "]}")).Select(p => p.Plugin));
        Assert.Empty(Lv2Catalog.ParsePartial("not json"u8));
    }

    /// <summary>
    /// A scanner that crashes on one bundle and stalls on another, unless
    /// told to skip them, and lists every other bundle as a plugin. Each run
    /// adds its arguments as a line to <paramref name="calls"/>.
    /// </summary>
    private static string Fussy(string directory, string calls, IEnumerable<string> bundles)
        => Fake(directory, $$"""
            echo "$*" >> '{{calls}}'
            skips=" $* "
            echo 'trace: scan LV2: load bundles' >&2
            printf '{"plugins":['
            separator=''
            n=0
            for bundle in {{string.Join(' ', bundles.Select(b => "'" + b + "'"))}}; do
              case "$skips" in *" --skip $bundle "*) echo "trace: scan LV2: skip bundle $bundle" >&2; continue;; esac
              echo "trace: scan LV2 plugin $n: bundle $bundle" >&2
              n=$((n + 1))
              case "$bundle" in */crash*) kill -SEGV $$;; */hang*) exec sleep 30;; esac
              name=$(basename "$bundle" .lv2)
              printf '%s{"uri":"urn:%s","name":"%s","audioIns":1,"audioOuts":1,"inputs":["in"],"outputs":["out"]}' "$separator" "$name" "$name"
              separator=','
            done
            echo ']}'
            echo 'trace: scan LV2: complete' >&2
            """);

    private static List<string> Bundles(string directory, params string[] names)
        => [.. names.Select(name => Directory.CreateDirectory(Path.Combine(directory, "plugins", name + ".lv2")).FullName + "/")];

    private static string[] Calls(string calls) => File.Exists(calls) ? File.ReadAllLines(calls) : [];

    [Fact]
    public void ACrashAndAStallCostTheirBundlesAndAreRememberedUntilTheyChange()
    {
        if (!OperatingSystem.IsLinux()) return;
        string directory = Directory.CreateTempSubdirectory("openxlr-lv2-skip-").FullName;
        try
        {
            List<string> bundles = Bundles(directory, "first", "crash", "second", "hang", "third");
            string calls = Path.Combine(directory, "calls");
            string scanner = Fussy(directory, calls, bundles);
            string cacheDirectory = Path.Combine(directory, "cache");
            var logs = new PluginScanLogStore(Path.Combine(directory, "logs"));
            TimeSpan timeout = TimeSpan.FromSeconds(2);

            string kind = Kind();
            Lv2Catalog.Lv2Scan scan = Lv2Catalog.FromHelper(scanner, "/plugins", logs, kind, timeout,
                failures: new ScanCache(cacheDirectory), budget: TimeSpan.FromSeconds(30));
            Assert.True(scan.Complete);
            Assert.Equal(["urn:first", "urn:second", "urn:third"], scan.Plugins.Select(p => p.Plugin));
            Assert.Equal(["scan-lv2 /plugins", $"scan-lv2 --skip {bundles[1]} /plugins",
                $"scan-lv2 --skip {bundles[1]} --skip {bundles[3]} /plugins"], Calls(calls));
            PluginScanReport report = PluginScanDiagnostics.Snapshot().Single(r => r.Kind == kind);
            Assert.Equal([(bundles[1], "scan-failed"), (bundles[3], "timeout"), ("/plugins", "ok")],
                report.Entries.Select(e => (e.Path, e.Outcome)));
            Assert.All(report.Entries.Take(2), e => Assert.NotNull(e.LogId));
            Assert.Equal(2, PluginScanDiagnostics.Failures([report]).Count);

            // The next scan leaves both out from the start, and says so
            // where skipped bundles are listed.
            File.Delete(calls);
            kind = Kind();
            long begun = System.Diagnostics.Stopwatch.GetTimestamp();
            scan = Lv2Catalog.FromHelper(scanner, "/plugins", logs, kind, timeout, failures: new ScanCache(cacheDirectory));
            Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(begun) < timeout);
            Assert.True(scan.Complete);
            Assert.Equal(3, scan.Plugins.Count);
            Assert.Equal([$"scan-lv2 --skip {bundles[1]} --skip {bundles[3]} /plugins"], Calls(calls));
            report = PluginScanDiagnostics.Snapshot().Single(r => r.Kind == kind);
            Assert.Equal(2, report.SkippedFailedCount);
            Assert.Equal([(bundles[1], "scan-failed"), (bundles[3], "timeout")],
                report.SkippedFailedBundles.Select(b => (b.Path, b.Outcome)));
            Assert.Empty(PluginScanDiagnostics.Failures([report]));

            // A changed bundle is read again; the unchanged one stays out.
            File.Delete(calls);
            File.WriteAllText(Path.Combine(bundles[1], "manifest.ttl"), "# fixed\n");
            scan = Lv2Catalog.FromHelper(scanner, "/plugins", logs, Kind(), timeout, failures: new ScanCache(cacheDirectory));
            Assert.True(scan.Complete);
            Assert.Equal([$"scan-lv2 --skip {bundles[3]} /plugins",
                $"scan-lv2 --skip {bundles[3]} --skip {bundles[1]} /plugins"], Calls(calls));

            // A rescan the user asked for reads every bundle again.
            File.Delete(calls);
            scan = Lv2Catalog.FromHelper(scanner, "/plugins", logs, Kind(), timeout, failures: new ScanCache(cacheDirectory),
                retryFailures: true, budget: TimeSpan.FromSeconds(30));
            Assert.True(scan.Complete);
            Assert.Equal(3, Calls(calls).Length);
            Assert.Equal([bundles[1], bundles[3]], new ScanCache(cacheDirectory).CurrentFailures().Select(f => f.Bundle));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ABundleThatScansAgainIsForgotten()
    {
        if (!OperatingSystem.IsLinux()) return;
        string directory = Directory.CreateTempSubdirectory("openxlr-lv2-forget-").FullName;
        try
        {
            List<string> bundles = Bundles(directory, "first", "crash");
            string calls = Path.Combine(directory, "calls");
            string cacheDirectory = Path.Combine(directory, "cache");
            Lv2Catalog.FromHelper(Fussy(directory, calls, bundles), "/plugins", null, Kind(), failures: new ScanCache(cacheDirectory));
            Assert.Equal([bundles[1]], new ScanCache(cacheDirectory).FailedBundles());
            // The bundle is fixed. An automatic scan still leaves it out and
            // keeps the failure; the user's rescan reads it and drops it.
            string fixedScanner = Fake(directory, "echo '{\"plugins\":[]}'\n");
            Assert.True(Lv2Catalog.FromHelper(fixedScanner, "/plugins", null, Kind(), failures: new ScanCache(cacheDirectory)).Complete);
            Assert.Equal([bundles[1]], new ScanCache(cacheDirectory).FailedBundles());
            Assert.True(Lv2Catalog.FromHelper(fixedScanner, "/plugins", null, Kind(), failures: new ScanCache(cacheDirectory),
                retryFailures: true).Complete);
            Assert.Empty(new ScanCache(cacheDirectory).FailedBundles());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void RetriesStopAtTheLimitAndKeepWhatWasDescribed()
    {
        if (!OperatingSystem.IsLinux()) return;
        string directory = Directory.CreateTempSubdirectory("openxlr-lv2-limit-").FullName;
        try
        {
            List<string> bundles = Bundles(directory, "first", "crash-a", "second", "crash-b", "third", "crash-c", "fourth");
            string calls = Path.Combine(directory, "calls");
            string cacheDirectory = Path.Combine(directory, "cache");
            Lv2Catalog.Lv2Scan scan = Lv2Catalog.FromHelper(Fussy(directory, calls, bundles), "/plugins", null, Kind(),
                failures: new ScanCache(cacheDirectory), maxRetries: 1);
            Assert.False(scan.Complete);
            Assert.Equal(2, Calls(calls).Length);
            // The second run described the plugins before its crash.
            Assert.Equal(["urn:first", "urn:second"], scan.Plugins.Select(p => p.Plugin));
            // Both crashes are remembered, so the next scan starts past them.
            Assert.Equal([bundles[1], bundles[3]], new ScanCache(cacheDirectory).CurrentFailures().Select(f => f.Bundle));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void TheBudgetBoundsARunOfStalls()
    {
        if (!OperatingSystem.IsLinux()) return;
        string directory = Directory.CreateTempSubdirectory("openxlr-lv2-budget-").FullName;
        try
        {
            List<string> bundles = Bundles(directory, "first", "hang-a", "second", "hang-b", "third", "hang-c");
            string calls = Path.Combine(directory, "calls");
            string cacheDirectory = Path.Combine(directory, "cache");
            string kind = Kind();
            // The first run stalls for its whole 2 second deadline. The 3.5
            // second budget then leaves the second run at most 1.5 seconds,
            // less than a deadline, so the budget is what cuts it. Those 1.5
            // seconds also absorb a slow machine starting and stopping the
            // first run; with half a second to spare, CI sometimes used it
            // all and never began the second run.
            long begun = System.Diagnostics.Stopwatch.GetTimestamp();
            Lv2Catalog.Lv2Scan scan = Lv2Catalog.FromHelper(Fussy(directory, calls, bundles), "/plugins", null, kind,
                TimeSpan.FromSeconds(2), failures: new ScanCache(cacheDirectory), budget: TimeSpan.FromSeconds(3.5));
            Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(begun) < TimeSpan.FromSeconds(7));
            Assert.False(scan.Complete);
            Assert.Equal(2, Calls(calls).Length);
            Assert.Equal(["urn:first", "urn:second"], scan.Plugins.Select(p => p.Plugin));
            Assert.Equal([(bundles[1], "timeout"), (bundles[3], "timeout")],
                PluginScanDiagnostics.Snapshot().Single(r => r.Kind == kind).Entries.Select(e => (e.Path, e.Outcome)));
            // Only the run that had the whole deadline judged its bundle; the
            // one cut short by the budget did not.
            Assert.Equal([bundles[1]], new ScanCache(cacheDirectory).CurrentFailures().Select(f => f.Bundle));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void AHelperThatIgnoresTheSkipIsNotAskedAgain()
    {
        if (!OperatingSystem.IsLinux()) return;
        string directory = Directory.CreateTempSubdirectory("openxlr-lv2-stubborn-").FullName;
        try
        {
            string calls = Path.Combine(directory, "calls");
            string scanner = Fake(directory, $"echo \"$*\" >> '{calls}'\n"
                + "echo 'trace: scan LV2 plugin 0: bundle /plugins/broken.lv2/' >&2\nkill -SEGV $$\n");
            Lv2Catalog.Lv2Scan scan = Lv2Catalog.FromHelper(scanner, "/plugins", null, Kind());
            Assert.False(scan.Complete);
            Assert.Equal(["scan-lv2 /plugins", "scan-lv2 --skip /plugins/broken.lv2/ /plugins"], Calls(calls));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    /// <summary>
    /// The real helper against bundles that stall lilv: one whose manifest is
    /// a pipe nobody writes, read while the manifests are, and one whose
    /// plugin data is, read when that plugin is described. Its name is not
    /// ASCII, so the marker text has escapes in it.
    /// </summary>
    [Fact]
    public void TheHelperLeavesOutTheBundlesThatStalledIt()
    {
        if (!LilvInstalled() || !NativePluginHost.HostInstalled) return;
        string directory = Directory.CreateTempSubdirectory("openxlr-lv2-stall-").FullName;
        try
        {
            WriteBundles(directory);
            string stall = Directory.CreateDirectory(Path.Combine(directory, "stall.lv2")).FullName;
            Assert.Equal(0, MakeFifo(Path.Combine(stall, "manifest.ttl"), 0b110_000_000));
            string slow = Directory.CreateDirectory(Path.Combine(directory, "slöw.lv2")).FullName;
            File.WriteAllText(Path.Combine(slow, "manifest.ttl"), Prefixes
                + "<urn:openxlr:test:slow> a lv2:Plugin ; lv2:binary <slow.so> ; rdfs:seeAlso <slow.ttl> .\n");
            Assert.Equal(0, MakeFifo(Path.Combine(slow, "slow.ttl"), 0b110_000_000));
            string cacheDirectory = Path.Combine(directory, "cache");
            string kind = Kind();
            Lv2Catalog.Lv2Scan scan = Lv2Catalog.FromHelper(NativePluginHost.Executable, directory, null, kind,
                TimeSpan.FromSeconds(3), failures: new ScanCache(cacheDirectory), budget: TimeSpan.FromSeconds(30));
            Assert.True(scan.Complete);
            // The bundles WriteBundles makes list one insert; the stalled ones none.
            Assert.Equal(["urn:openxlr:test:rich"], scan.Plugins.Select(p => p.Plugin));
            Assert.Equal([(stall + "/", "timeout"), (slow + "/", "timeout")], PluginScanDiagnostics.Snapshot()
                .Single(r => r.Kind == kind).Entries.Where(e => e.Outcome != "ok").Select(e => (e.Path, e.Outcome)));
            Assert.Equal([slow + "/", stall + "/"], new ScanCache(cacheDirectory).CurrentFailures().Select(f => f.Bundle));

            long begun = System.Diagnostics.Stopwatch.GetTimestamp();
            Lv2Catalog.Lv2Scan again = Lv2Catalog.FromHelper(NativePluginHost.Executable, directory, null, Kind(),
                TimeSpan.FromSeconds(3), failures: new ScanCache(cacheDirectory));
            Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(begun) < TimeSpan.FromSeconds(3));
            Assert.True(again.Complete);
            Assert.Equal(Snapshot(scan.Plugins), Snapshot(again.Plugins));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string Kind() => "lv2-test-" + Guid.NewGuid().ToString("N");

    private static string Fake(string directory, string body)
        => ExecutableScript.Write(Path.Combine(directory, "scanner-" + Guid.NewGuid().ToString("N")), body);

    private const string Prefixes = """
        @prefix lv2: <http://lv2plug.in/ns/lv2core#> .
        @prefix doap: <http://usefulinc.com/ns/doap#> .
        @prefix pprops: <http://lv2plug.in/ns/ext/port-props#> .
        @prefix rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#> .
        @prefix rdfs: <http://www.w3.org/2000/01/rdf-schema#> .
        @prefix ui: <http://lv2plug.in/ns/extensions/ui#> .

        """;

    private const string StereoPorts = """
          lv2:port [ a lv2:AudioPort, lv2:InputPort ; lv2:index 0 ; lv2:symbol "in_l" ; lv2:name "In L" ],
            [ a lv2:AudioPort, lv2:InputPort ; lv2:index 1 ; lv2:symbol "in_r" ; lv2:name "In R" ],
            [ a lv2:AudioPort, lv2:OutputPort ; lv2:index 2 ; lv2:symbol "out_l" ; lv2:name "Out L" ],
            [ a lv2:AudioPort, lv2:OutputPort ; lv2:index 3 ; lv2:symbol "out_r" ; lv2:name "Out R" ]
        """;

    private static void Bundle(string directory, string name, string uri, byte[] data)
    {
        string bundle = Directory.CreateDirectory(Path.Combine(directory, name + ".lv2")).FullName;
        File.WriteAllText(Path.Combine(bundle, "manifest.ttl"), Prefixes
            + $"<{uri}> a lv2:Plugin ; lv2:binary <{name}.so> ; rdfs:seeAlso <{name}.ttl> .\n");
        File.WriteAllBytes(Path.Combine(bundle, name + ".ttl"), data);
    }

    private static void Bundle(string directory, string name, string uri, string data)
        => Bundle(directory, name, uri, Encoding.UTF8.GetBytes(Prefixes + data));

    /// <summary>Every kind of port and metadata the catalogue reads, and the plugins it leaves out.</summary>
    private static void WriteBundles(string directory)
    {
        Bundle(directory, "rich", "urn:openxlr:test:rich", $$"""
            <urn:openxlr:test:rich> a lv2:Plugin, lv2:CompressorPlugin ;
              doap:name "Rich é \"quoted\" \\ test\t" ;
              lv2:binary <rich.so> ;
              lv2:requiredFeature <http://lv2plug.in/ns/ext/urid#map>, <http://example.org/feature#x> ;
              ui:ui <urn:openxlr:test:rich#ui> ;
            {{StereoPorts}},
                [ a lv2:ControlPort, lv2:InputPort ; lv2:index 4 ; lv2:symbol "mode" ; lv2:name "Mode" ;
                  lv2:default 1 ; lv2:minimum 0 ; lv2:maximum 2 ; lv2:portProperty lv2:integer, lv2:enumeration ;
                  lv2:scalePoint [ rdfs:label "Off" ; rdf:value 0 ], [ rdfs:label "On" ; rdf:value 1 ], [ rdfs:label "Auto" ; rdf:value 2 ] ],
                [ a lv2:ControlPort, lv2:InputPort ; lv2:index 5 ; lv2:symbol "on" ; lv2:name "On" ;
                  lv2:default 0 ; lv2:minimum 0 ; lv2:maximum 1 ; lv2:portProperty lv2:toggled ],
                [ a lv2:ControlPort, lv2:InputPort ; lv2:index 6 ; lv2:symbol "freq" ; lv2:name "Frequency" ;
                  lv2:default 0.1 ; lv2:minimum 0.01 ; lv2:maximum 20000 ; lv2:portProperty pprops:logarithmic ],
                [ a lv2:ControlPort, lv2:InputPort ; lv2:index 7 ; lv2:symbol "hidden" ; lv2:name "Hidden" ;
                  lv2:portProperty pprops:notOnGUI ],
                [ a lv2:ControlPort, lv2:OutputPort ; lv2:index 8 ; lv2:symbol "latency" ; lv2:name "Latency" ;
                  lv2:portProperty lv2:reportsLatency ; lv2:designation lv2:latency ],
                [ a lv2:ControlPort, lv2:InputPort ; lv2:index 9 ; lv2:symbol "nodefault" ; lv2:name "No default" ],
                [ a lv2:ControlPort, lv2:InputPort ; lv2:index 10 ; lv2:symbol "endless" ; lv2:name "Endless" ;
                  lv2:default 0 ; lv2:minimum 0 ; lv2:maximum 1e999 ] .
            <urn:openxlr:test:rich#ui> a ui:X11UI ; ui:binary <rich_ui.so> ; lv2:requiredFeature ui:idleInterface .
            """);
        Bundle(directory, "generator", "urn:openxlr:test:generator", """
            <urn:openxlr:test:generator> a lv2:Plugin ; doap:name "Generator" ; lv2:binary <generator.so> ;
              lv2:port [ a lv2:AudioPort, lv2:OutputPort ; lv2:index 0 ; lv2:symbol "out" ; lv2:name "Out" ] .
            """);
        Bundle(directory, "carla", "http://kxstudio.sf.net/carla/plugins/test", $$"""
            <http://kxstudio.sf.net/carla/plugins/test> a lv2:Plugin ; doap:name "Rack" ; lv2:binary <carla.so> ;
            {{StereoPorts}} .
            """);
        var monster = new StringBuilder("<urn:openxlr:test:monster> a lv2:Plugin ; doap:name \"Monster\" ; lv2:binary <monster.so> ;\n"
            + StereoPorts);
        for (int i = 4; i < Lv2Catalog.MaxPorts + 10; i++)
            monster.Append($",\n    [ a lv2:ControlPort, lv2:InputPort ; lv2:index {i} ; lv2:symbol \"c{i}\" ; lv2:name \"C {i}\" ]");
        Bundle(directory, "monster", "urn:openxlr:test:monster", monster.Append(" .\n").ToString());
        // Data files lilv refuses, for bytes that are not UTF-8 and for
        // syntax: each costs its own plugin, not the scan.
        byte[] head = Encoding.UTF8.GetBytes(Prefixes + "<urn:openxlr:test:bytes> a lv2:Plugin ; doap:name \"Bad ");
        byte[] tail = Encoding.UTF8.GetBytes(" name\" ; lv2:binary <bytes.so> ;\n" + StereoPorts + " .\n");
        Bundle(directory, "bytes", "urn:openxlr:test:bytes", [.. head, 0xff, 0xc3, 0x28, 0xe2, 0x82, .. tail]);
        Bundle(directory, "broken", "urn:openxlr:test:broken", "<urn:openxlr:test:broken> a lv2:Plugin ; this is not turtle");
    }
}
