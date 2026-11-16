using OpenXLR.Core.Mixing;
using Xunit;

namespace OpenXLR.Tests;

/// <summary>
/// The layout warning for the audio servers' open-file limits, read from a
/// fake /proc tree: pipewire-pulse from three quarters of its limit, PipeWire
/// from half, since a WirePlumber restart makes it hand every node over again.
/// </summary>
public sealed class FileLimitWarningTests : IDisposable
{
    private readonly string _proc = Directory.CreateTempSubdirectory("openxlr-proc-").FullName;

    public FileLimitWarningTests()
    {
        Directory.CreateDirectory(Path.Combine(_proc, "self"));
        File.WriteAllText(Path.Combine(_proc, "self", "status"), "Name:\tdotnet\nUid:\t1000\t1000\t1000\t1000\n");
    }

    public void Dispose() => Directory.Delete(_proc, recursive: true);

    private void Server(int pid, string comm, int open, int limit, string uid = "1000")
    {
        string dir = Directory.CreateDirectory(Path.Combine(_proc, pid.ToString(System.Globalization.CultureInfo.InvariantCulture))).FullName;
        File.WriteAllText(Path.Combine(dir, "comm"), comm + "\n");
        File.WriteAllText(Path.Combine(dir, "status"), $"Name:\t{comm}\nUid:\t{uid}\t{uid}\t{uid}\t{uid}\n");
        File.WriteAllText(Path.Combine(dir, "limits"),
            "Limit                     Soft Limit           Hard Limit           Units     \n" +
            $"Max open files            {limit}                 524288               files     \n");
        string fd = Directory.CreateDirectory(Path.Combine(dir, "fd")).FullName;
        for (int i = 0; i < open; i++) File.WriteAllText(Path.Combine(fd, i.ToString(System.Globalization.CultureInfo.InvariantCulture)), "");
    }

    private string? Warning()
    {
        using var mixer = new Mixer(new PipeWireAdapter { ProcRoot = _proc });
        return mixer.FileLimitWarning();
    }

    [Fact]
    public void PipeWireAtHalfItsLimitWarnsAboutAWirePlumberRestart()
    {
        // The desk's graph: 712 of 1024 steady, and a WirePlumber restart
        // ran out during the handover.
        Server(1657, "pipewire", 712, 1024);
        Server(1700, "pipewire-pulse", 400, 65536);
        string? warning = Warning();
        Assert.NotNull(warning);
        Assert.StartsWith("PipeWire has 712 of its 1024 open files in use", warning, StringComparison.Ordinal);
        Assert.Contains("WirePlumber", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("pipewire-pulse has", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void RaisedLimitsWarnAboutNothing()
    {
        Server(1657, "pipewire", 712, 65536);
        Server(1700, "pipewire-pulse", 400, 65536);
        Assert.Null(Warning());
    }

    [Fact]
    public void PipeWireBelowHalfAndPulseBelowThreeQuartersAreQuiet()
    {
        Server(1657, "pipewire", 511, 1024);
        Server(1700, "pipewire-pulse", 767, 1024);
        Assert.Null(Warning());
    }

    [Fact]
    public void BothServersNearTheirLimitsAreBothNamed()
    {
        Server(1657, "pipewire", 600, 1024);
        Server(1700, "pipewire-pulse", 800, 1024);
        string? warning = Warning();
        Assert.NotNull(warning);
        Assert.StartsWith("pipewire-pulse has 800 of its 1024 open files in use", warning, StringComparison.Ordinal);
        Assert.Contains("PipeWire has 600 of its 1024 open files in use", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void AnotherUsersServerIsNotRead()
    {
        Server(1657, "pipewire", 712, 1024, uid: "1001");
        Assert.Null(Warning());
    }
}
