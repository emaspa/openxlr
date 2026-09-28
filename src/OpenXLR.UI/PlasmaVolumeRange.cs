using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenXLR.UI;

/// <summary>
/// Plasma owns the output slider ceiling, independently of PipeWire's level.
/// Use KConfig's helpers for defaults, locking and change notification; watch
/// its file rather than polling or parsing a second copy of the KConfig format.
/// </summary>
internal sealed class PlasmaVolumeRange(Action<bool> apply, Action<string?> report, Action<Action> dispatch) : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private FileSystemWatcher? _watcher;
    private bool _running, _disposed;
    private bool? _pendingWrite;
    private long _revision;
    private Task _worker = Task.CompletedTask;
    private (long Revision, bool? Boost, string? Error)? _publication;
    private bool _publicationQueued;
    // Only the serialized worker accesses the last failed write.
    private bool? _failedWrite;
    private string? _writeError;

    internal static bool IsPlasma => (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "")
        .Split(':').Contains("KDE", StringComparer.OrdinalIgnoreCase);

    internal void Start()
    {
        try
        {
            Directory.CreateDirectory(OpenXlrPaths.ConfigHome);
            _watcher = new FileSystemWatcher(OpenXlrPaths.ConfigHome, "plasmaparc")
                { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
            _watcher.Changed += (_, _) => Refresh();
            _watcher.Created += (_, _) => Refresh();
            _watcher.Deleted += (_, _) => Refresh();
            _watcher.Renamed += (_, _) => Refresh();
            _watcher.Error += (_, _) => Refresh();
            _watcher.EnableRaisingEvents = true;
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _watcher?.Dispose();
            _watcher = null;
            report($"Cannot follow Plasma's volume range: {ex.Message}");
        }
    }

    internal void Set(bool boost) => Schedule(boost);
    internal void Refresh() => Schedule(null);

    private void Schedule(bool? write)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (write.HasValue) _pendingWrite = write;
            _revision++;
            if (_running) return;
            _running = true;
            _worker = Task.Run(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            long revision;
            bool? write;
            lock (_gate)
            {
                if (_disposed) { StopWorker(); return; }
                revision = _revision;
                write = _pendingWrite;
                _pendingWrite = null;
            }
            try
            {
                if (write.HasValue)
                {
                    _failedWrite = null;
                    _writeError = null;
                    var result = await ProcessRunner.RunAsync("kwriteconfig6",
                        ["--file", "plasmaparc", "--group", "General", "--key", "RaiseMaximumVolume",
                         "--type", "bool", "--notify", write.Value ? "true" : "false"],
                        TimeSpan.FromSeconds(3), stdoutCap: 4096, stderrCap: 4096, cancel: _lifetime.Token);
                    if (!result.Ok) throw new IOException("The desktop preference could not be saved.");
                }
                bool boost = await ReadAsync(_lifetime.Token);
                if (write.HasValue && boost != write.Value)
                    throw new IOException("The desktop did not keep the requested volume range.");
                if (_failedWrite == boost) { _failedWrite = null; _writeError = null; }
                Publish(revision, boost, _writeError);
            }
            catch (Exception ex)
            {
                // Helpers may be absent, the preference locked by the desktop,
                // or the process cancelled. Keep local controls usable.
                bool? actual = null;
                if (write.HasValue && !_lifetime.IsCancellationRequested)
                {
                    try { actual = await ReadAsync(_lifetime.Token); }
                    catch (Exception) { /* keep the read/write error below */ }
                }
                string error = $"Cannot synchronize Plasma's volume range: {ex.Message}";
                if (write.HasValue) { _failedWrite = write; _writeError = error; }
                Publish(revision, actual, error);
            }
            lock (_gate)
            {
                if (_disposed || revision == _revision) { StopWorker(); return; }
            }
        }
    }

    // Called under _gate, after the last helper has finished with its token.
    private void StopWorker()
    {
        _running = false;
        if (_disposed) _lifetime.Dispose();
    }

    private void Publish(long revision, bool? boost, string? error)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _publication = (revision, boost, error);
            if (_publicationQueued) return;
            _publicationQueued = true;
        }
        dispatch(Drain);
    }

    private void Drain()
    {
        lock (_gate)
        {
            var latest = _publication;
            _publication = null;
            _publicationQueued = false;
            if (_disposed || latest is not { } update || update.Revision != _revision) return;
            if (update.Boost.HasValue) apply(update.Boost.Value);
            report(update.Error);
        }
    }

    private static async Task<bool> ReadAsync(CancellationToken cancel)
    {
        // Without --type bool, kreadconfig prints the value instead of using
        // its exit code as the boolean (which hides execution failures).
        var result = await ProcessRunner.RunAsync("kreadconfig6",
            ["--file", "plasmaparc", "--group", "General", "--key", "RaiseMaximumVolume", "--default", "false"],
            TimeSpan.FromSeconds(3), stdoutCap: 4096, stderrCap: 4096, cancel: cancel);
        if (!result.Ok) throw new IOException("The desktop preference could not be read.");
        return result.StdoutText.Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "on" or "1" => true,
            "false" or "no" or "off" or "0" => false,
            _ => throw new IOException("The desktop returned an invalid volume range."),
        };
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _worker.ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _publication = null;
            _lifetime.Cancel();
            if (!_running) _lifetime.Dispose();
        }
        _watcher?.Dispose();
    }
}
