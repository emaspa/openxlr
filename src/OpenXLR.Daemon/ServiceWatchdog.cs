using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace OpenXLR.Daemon;

/// <summary>A completed failed operation is alive; one that never returns is not.</summary>
internal sealed class ServiceProgress(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _last = (clock ?? TimeProvider.System).GetTimestamp();

    public void Mark() => Interlocked.Exchange(ref _last, _clock.GetTimestamp());
    public bool IsRecent(TimeSpan limit) => _clock.GetElapsedTime(Interlocked.Read(ref _last)) < limit;
}

/// <summary>
/// Reports readiness and progress without acquiring device or mixer locks.
/// Only systemd-supervised launches with NOTIFY_SOCKET enable this service.
/// </summary>
internal sealed class ServiceWatchdog(
    DeviceManager devices, MixerService mixer, IHostApplicationLifetime lifetime,
    ILogger<ServiceWatchdog> log) : BackgroundService
{
    internal static TimeSpan? WatchdogInterval(string? usec, string? pid, int currentPid)
    {
        if (pid is not null && (!int.TryParse(pid, out int owner) || owner != currentPid)) return null;
        return long.TryParse(usec, NumberStyles.None, CultureInfo.InvariantCulture, out long value)
               && value >= 3_000 && value <= 86_400_000_000
            ? TimeSpan.FromTicks(value * 10) : null;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        string? address = Environment.GetEnvironmentVariable("NOTIFY_SOCKET");
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(address)) return;
        TimeSpan? interval = WatchdogInterval(Environment.GetEnvironmentVariable("WATCHDOG_USEC"),
            Environment.GetEnvironmentVariable("WATCHDOG_PID"), Environment.ProcessId);
        TimeSpan period = interval / 3 ?? TimeSpan.FromSeconds(20);
        TimeSpan freshness = interval / 2 ?? TimeSpan.FromSeconds(30);
        using var timer = new PeriodicTimer(period);
        // Hosted services start before the host fires ApplicationStarted. Keep
        // readiness on that event path instead of waiting for the next watchdog
        // tick (20 seconds with the packaged 60-second watchdog).
        Task<bool> readyNotification = NotifyWhenStartedAsync(
            lifetime.ApplicationStarted, address, stop);
        bool ready = false, stalled = false;
        try
        {
            do
            {
                bool healthy = devices.Progress.IsRecent(freshness) && mixer.IsResponsive(freshness);
                if (readyNotification.IsCompleted)
                {
                    if (!ready)
                    {
                        ready = await readyNotification;
                        if (!ready)
                        {
                            log.LogWarning("Could not report readiness to systemd");
                            readyNotification = NotifyAsync(address, "READY=1", stop);
                        }
                    }
                    if (interval is null && ready) return;
                    if (healthy && interval is not null
                        && !await NotifyAsync(address, "WATCHDOG=1", stop))
                        log.LogWarning("Could not report progress to systemd");
                }
                else if (healthy)
                {
                    // StartAsync can build many nodes. Extend the startup
                    // deadline only while real worker operations complete.
                    await NotifyAsync(address, "EXTEND_TIMEOUT_USEC=60000000", stop);
                }

                if (!healthy && !stalled)
                    log.LogWarning("Watchdog heartbeat withheld: device or mixer stopped making progress");
                else if (healthy && stalled)
                    log.LogInformation("Watchdog worker progress recovered");
                stalled = !healthy;
            } while (await timer.WaitForNextTickAsync(stop));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    internal static async Task<bool> NotifyWhenStartedAsync(
        CancellationToken applicationStarted, string address, CancellationToken stop)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = applicationStarted.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(), started);
        await started.Task.WaitAsync(stop);
        return await NotifyAsync(address, "READY=1", stop);
    }

    internal static async Task<bool> NotifyAsync(string address, string message, CancellationToken stop)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
            var endpoint = new UnixDomainSocketEndPoint(address.StartsWith('@') ? "\0" + address[1..] : address);
            await socket.SendToAsync(Encoding.UTF8.GetBytes(message), SocketFlags.None, endpoint, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or OperationCanceledException)
        {
            return false;
        }
    }
}
