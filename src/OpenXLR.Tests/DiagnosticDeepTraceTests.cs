using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class DiagnosticDeepTraceTests : IDisposable
{
    private readonly string _proc = Directory.CreateTempSubdirectory("openxlr-proc-").FullName;

    [Fact]
    public void ReportsOwnedNamesAndOnlyAudioProcessMemory()
    {
        File.WriteAllText(Path.Combine(_proc, "meminfo"), "MemTotal: 128000 kB\nMemAvailable: 64000 kB\nSwapFree: 0 kB\nUnknown: 1 kB\n");
        WriteStatus("100", "OpenXLR.UI", metrics: "VmRSS: 900 kB\nVmHWM: 1024 kB\nVmSwap: 0 kB\nThreads: 3\nState: S (sleeping)\n");
        WriteStatus("101", "OpenXLR.Daemon", metrics: "VmRSS: 600 kB\nVmLck: 40 kB\nRssAnon: 520 kB\nVmSwap: -5 kB\nVmSize: 2 MB\n");
        WriteStatus("102", "openxlr-lv2-hos", metrics: "VmRSS: 300 kB\n");
        WriteStatus("103", "browser", metrics: "VmRSS: 400 kB\n");
        WriteStatus("104", "browser");
        WriteStatus("105", "wireplumber", uid: "1001 1001 1001 1001");
        WriteStatus("106", "different-effective-user", uid: "1000 1001 1001 1001");
        WriteStatus("107", "bad-uid", uid: "1000");
        WriteStatus("108", "/private/name");
        WriteStatus("109", "token\\nprivate");
        WriteStatus("ignored", "non-numeric-directory");
        Directory.CreateDirectory(Path.Combine(_proc, "110")); // Process exited before status could be read.
        WriteStatus("111", "oversized-status", metrics: "extra: " + new string('x', 17 * 1024));
        File.WriteAllText(Path.Combine(_proc, "103", "cmdline"), "private command line");
        File.WriteAllText(Path.Combine(_proc, "103", "environ"), "private environment");

        var context = Diagnostics.ReadProcessContext(_proc, 1000, 100);

        Assert.True(context.Available);
        Assert.False(context.Truncated);
        Assert.Equal(2, context.Unreadable);
        Assert.Equal(3, context.SystemMemoryKiB.Count);
        Assert.Equal(64000, context.SystemMemoryKiB["MemAvailable"]);
        Assert.NotNull(context.Window);
        Assert.Equal('S', context.Window.State);
        Assert.Equal(3, context.Window.Threads);
        Assert.Equal(1024, context.Window.MemoryKiB["VmHWM"]);
        Assert.Equal(0, context.Window.MemoryKiB["VmSwap"]);
        Assert.Collection(context.AudioProcesses,
            daemon =>
            {
                Assert.Equal("OpenXLR.Daemon", daemon.Name);
                Assert.Equal(3, daemon.MemoryKiB.Count);
                Assert.Equal(40, daemon.MemoryKiB["VmLck"]);
            },
            host => Assert.Equal("openxlr-lv2-hos", host.Name));
        Assert.Equal(
            [new("OpenXLR.Daemon", 1), new("OpenXLR.UI", 1), new("browser", 2), new("openxlr-lv2-hos", 1)],
            context.Programs);
    }

    [Fact]
    public void BoundsDistinctProgramsAndStillCountsSeenNames()
    {
        for (int i = 1; i <= 600; i++) WriteStatus(i.ToString(), $"program-{i}");
        var context = Diagnostics.ReadProcessContext(_proc, 1000, 1);
        Assert.True(context.Truncated);
        Assert.Equal(512, context.Programs.Length);
        Assert.NotNull(context.Window);
    }

    [Fact]
    public void AHiddenProcViewLeavesAnUnavailableSnapshot()
    {
        var context = Diagnostics.ReadProcessContext(Path.Combine(_proc, "missing"), 1000, 100);
        Assert.False(context.Available);
        Assert.Null(context.Window);
        Assert.Empty(context.Programs);
        Assert.Empty(context.SystemMemoryKiB);
    }

    private void WriteStatus(string directory, string name, string uid = "1000 1000 1000 1000", string metrics = "")
    {
        string path = Directory.CreateDirectory(Path.Combine(_proc, directory)).FullName;
        File.WriteAllText(Path.Combine(path, "status"), $"Name:\t{name}\nUid:\t{uid}\n{metrics}");
    }

    public void Dispose() => Directory.Delete(_proc, true);
}
