using OpenXLR.UI.Localization;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenXLR.UI;

/// <summary>
/// Follows Plasma's Raise maximum volume preference, which sets the ceiling of
/// the desktop's output sliders. KDE's own helpers read and write it, so KConfig
/// defaults, locking and change notification stay KDE's; the file is watched
/// rather than polled. One worker runs the helpers in order, and the window
/// receives at most one queued update however fast changes arrive.
/// </summary>
internal sealed class PlasmaVolumeRange(Action<bool> apply, Action<string?> report, Action<Action> dispatch) : IDisposable, IAsyncDisposable
{
    private const string ConfigFile = "plasmaparc", Group = "General", Key = "RaiseMaximumVolume";
    private static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(3);

    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private FileSystemWatcher? _watcher;
    private Task _worker = Task.CompletedTask;
    private bool _running, _disposed;
    private bool? _pendingWrite;
    private long _revision;
    // What the next dispatch hands the window: the range read for a revision,
    // and a status change. Only one dispatch is queued at a time.
    private (long Revision, bool Boost)? _range;
    private (bool Changed, string? Text) _status;
    private bool _queued;
    // Worker only: the error on show came from a read, so the next good read clears it.
    private bool _readError;

    internal static bool IsPlasma => (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "")
        .Split(':').Contains("KDE", StringComparer.OrdinalIgnoreCase);

    internal void Start()
    {
        try
        {
            Directory.CreateDirectory(OpenXlrPaths.ConfigHome);
            _watcher = new FileSystemWatcher(OpenXlrPaths.ConfigHome, ConfigFile)
                { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
            _watcher.Changed += (_, _) => Refresh();
            _watcher.Created += (_, _) => Refresh();
            _watcher.Deleted += (_, _) => Refresh();
            _watcher.Renamed += (_, _) => Refresh();
            _watcher.Error += (_, _) => Refresh();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _watcher?.Dispose();
            _watcher = null;
            Publish(null, 0, Localizer.Format("CannotFollowPlasmaRange", ex.Message));
        }
        Refresh();
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
            // The window always ends up showing what the desktop holds, so a
            // change the desktop did not take is read back rather than undone here.
            string? error = null;
            if (write is bool requested)
            {
                try
                {
                    var result = await ProcessRunner.RunAsync("kwriteconfig6",
                        ["--file", ConfigFile, "--group", Group, "--key", Key, "--type", "bool", "--notify", requested ? "true" : "false"],
                        HelperTimeout, stdoutCap: 4096, stderrCap: 4096, cancel: _lifetime.Token);
                    if (!result.Ok) error = Localizer.Text("CannotChangePlasmaRangeSave");
                }
                catch (Exception ex)
                {
                    error = Localizer.Format("CannotChangePlasmaRange", ex.Message);
                }
            }
            try
            {
                bool boost = await ReadAsync(_lifetime.Token);
                if (error is null && write.HasValue && boost != write.Value)
                    error = Localizer.Text("CannotChangePlasmaRangeKept");
                // A write reports its own outcome. A plain read clears only a
                // read error, so a refresh cannot hide a failed save.
                Publish(boost, revision, error, statusChanged: write.HasValue || _readError);
                _readError = false;
            }
            catch (Exception ex)
            {
                // Missing helpers, a malformed preference or shutdown. The
                // window's own controls stay usable; the error is shown once.
                if (!_lifetime.IsCancellationRequested)
                    Publish(null, revision, error ?? Localizer.Format("CannotReadPlasmaRange", ex.Message));
                _readError = error is null;
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

    private void Publish(bool? boost, long revision, string? error, bool statusChanged = true)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (boost is bool value) _range = (revision, value);
            if (statusChanged) _status = (true, error);
            if (_queued) return;
            _queued = true;
        }
        dispatch(Drain);
    }

    private void Drain()
    {
        (long Revision, bool Boost)? range;
        (bool Changed, string? Text) status;
        lock (_gate)
        {
            range = _range;
            status = _status;
            _range = null;
            _status = default;
            _queued = false;
            if (_disposed) return;
            // A read that a newer request has overtaken is not applied.
            if (range is { } r && r.Revision != _revision) range = null;
        }
        if (range is { } latest) apply(latest.Boost);
        if (status.Changed) report(status.Text);
    }

    private static async Task<bool> ReadAsync(CancellationToken cancel)
    {
        // Without --type bool, kreadconfig6 prints the value instead of
        // answering through its exit code, which would hide a failed run.
        var result = await ProcessRunner.RunAsync("kreadconfig6",
            ["--file", ConfigFile, "--group", Group, "--key", Key, "--default", "false"],
            HelperTimeout, stdoutCap: 4096, stderrCap: 4096, cancel: cancel);
        if (!result.Ok) throw new IOException(Localizer.Text("DesktopPreferenceUnreadable"));
        return result.StdoutText.Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "on" or "1" => true,
            "false" or "no" or "off" or "0" => false,
            _ => throw new IOException(Localizer.Text("DesktopInvalidVolumeRange")),
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
            _range = null;
            _status = default;
            _lifetime.Cancel();
            if (!_running) _lifetime.Dispose();
        }
        _watcher?.Dispose();
    }
}
