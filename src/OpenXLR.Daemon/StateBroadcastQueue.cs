using System.Threading.Channels;

namespace OpenXLR.Daemon;

/// <summary>
/// Carries a dirty flag, not a snapshot: bursts retain at most one pending
/// update and event producers never acquire the device or mixer locks.
/// </summary>
internal sealed class StateBroadcastQueue(Action broadcast, ILogger log, string subject = "State broadcast")
{
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            AllowSynchronousContinuations = false,
        });

    private string? _lastError;   // the single reader's own

    internal void Signal() => _changes.Writer.TryWrite(true);

    internal async Task RunAsync(CancellationToken stop)
    {
        try
        {
            await foreach (bool ignored in _changes.Reader.ReadAllAsync(stop))
            {
                if (stop.IsCancellationRequested) break;
                try
                {
                    broadcast();
                    if (_lastError is not null)
                    {
                        _lastError = null;
                        log.LogInformation("{Subject} recovered", subject);
                    }
                }
                catch (Exception ex)
                {
                    // A failure that repeats with every change would fill the
                    // journal with the same stack trace. The first of each
                    // distinct message carries it; repeats go to debug.
                    if (ex.Message != _lastError)
                    {
                        _lastError = ex.Message;
                        log.LogWarning(ex, "{Subject} failed (repeats logged at debug level)", subject);
                    }
                    else log.LogDebug("{Subject} failed again: {Message}", subject, ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
}
