using System.ComponentModel;
using System.Net.Sockets;
using System.Text;
using OpenXLR.Core.Mixing;
using OpenXLR.Daemon;

namespace OpenXLR.Tests;

public sealed class ServiceWatchdogTests
{
    [Theory]
    [InlineData("60000000", null, 42, 60)]
    [InlineData("60000000", "42", 42, 60)]
    [InlineData("60000000", "43", 42, 0)]
    [InlineData("60000000", "bad", 42, 0)]
    [InlineData(null, null, 42, 0)]
    [InlineData("-1", null, 42, 0)]
    [InlineData("0", null, 42, 0)]
    [InlineData("9223372036854775807", null, 42, 0)]
    public void IntervalHonoursSystemdProcessOwnership(string? interval, string? pid, int current, int seconds)
        => Assert.Equal(seconds, ServiceWatchdog.WatchdogInterval(interval, pid, current)?.TotalSeconds ?? 0);

    [Fact]
    public void HungWorkerExpiresButCompletedWorkRenewsProgress()
    {
        var clock = new ManualClock();
        var progress = new ServiceProgress(clock);
        Assert.True(progress.IsRecent(TimeSpan.FromSeconds(30)));
        clock.Now += TimeSpan.FromSeconds(30).Ticks;
        Assert.False(progress.IsRecent(TimeSpan.FromSeconds(30)));
        progress.Mark();
        Assert.True(progress.IsRecent(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void LongOperationCanReportEachCompletedStep()
    {
        var clock = new ManualClock();
        var progress = new ServiceProgress(clock);
        for (int i = 0; i < 20; i++)
        {
            clock.Now += TimeSpan.FromSeconds(5).Ticks;
            Assert.True(progress.IsRecent(TimeSpan.FromSeconds(30)));
            progress.Mark();
        }
        Assert.Equal(TimeSpan.FromSeconds(100).Ticks, clock.Now);
    }

    [Fact]
    public void HelperSuccessAndFailureBothReportCompletion()
    {
        int completed = 0;
        var adapter = new PipeWireAdapter(() => completed++);
        Assert.NotEmpty(adapter.Run("dotnet", "--version"));
        Assert.Equal(1, completed);
        Assert.Throws<Win32Exception>(() => adapter.Run("openxlr-no-such-helper-" + Guid.NewGuid().ToString("N")));
        Assert.Equal(2, completed);
    }

    [Fact]
    public async Task NotificationsReachARealUnixDatagramSocket()
    {
        string directory = Directory.CreateTempSubdirectory("ow-").FullName;
        string address = Path.Combine(directory, "notify");
        try
        {
            using var listener = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(address));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            foreach (string message in new[] { "READY=1", "WATCHDOG=1", "EXTEND_TIMEOUT_USEC=60000000" })
            {
                Assert.True(await ServiceWatchdog.NotifyAsync(address, message, stop.Token));
                byte[] buffer = new byte[128];
                int received = await listener.ReceiveAsync(buffer, SocketFlags.None, stop.Token);
                Assert.Equal(message, Encoding.UTF8.GetString(buffer, 0, received));
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ReadinessIsSentWhenApplicationStartedFires()
    {
        string directory = Directory.CreateTempSubdirectory("ow-").FullName;
        string address = Path.Combine(directory, "notify");
        try
        {
            using var listener = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(address));
            using var applicationStarted = new CancellationTokenSource();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            Task<bool> notification = ServiceWatchdog.NotifyWhenStartedAsync(
                applicationStarted.Token, address, stop.Token);
            Assert.False(notification.IsCompleted);

            applicationStarted.Cancel();
            Assert.True(await notification);
            byte[] buffer = new byte[128];
            int received = await listener.ReceiveAsync(buffer, SocketFlags.None, stop.Token);
            Assert.Equal("READY=1", Encoding.UTF8.GetString(buffer, 0, received));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task MissingNotifySocketDoesNotThrowOrHang()
    {
        string address = Path.Combine(Path.GetTempPath(), "ow-missing-" + Guid.NewGuid().ToString("N"));
        Assert.False(await ServiceWatchdog.NotifyAsync(address, "READY=1", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class ManualClock : TimeProvider
    {
        public long Now { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Now;
    }
}
