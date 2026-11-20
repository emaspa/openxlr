using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

#if OPENXLR_UI
namespace OpenXLR.UI;
#elif OPENXLR_TUI
namespace OpenXLR.Tui;
#else
namespace OpenXLR.Core;
#endif

/// <summary>
/// Watches the skin directories for changes to one skin, so a client wearing
/// it can read it again without a Reload. A theme tool that rewrites a skin
/// (a template run when the desktop's colours change) is followed with no
/// hook on its side.
///
/// Every skin directory that exists is watched with its subfolders, and only
/// changes under the folder named after the followed id count: an edited
/// file, an image beside it, the folder replaced, removed or put back, or a
/// copy that shadows a built-in skin appearing. Tools write a file in several
/// steps, often a temporary file renamed over the old one, so the callback
/// runs once the folder has been quiet for <see cref="Settle"/>, on a timer
/// thread. A directory created after the watch started is not seen.
/// </summary>
internal sealed class SkinFolderWatch : IDisposable
{
    /// <summary>How long the folder stays quiet before the skin is read again.</summary>
    internal static readonly TimeSpan DefaultSettle = TimeSpan.FromMilliseconds(300);

    private readonly object _gate = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Action _changed;
    private readonly Timer _timer;
    private string? _id;
    private bool _disposed;

    /// <param name="roots">The skin directories, each holding one folder per skin.</param>
    /// <param name="changed">Called on a timer thread once the followed skin has settled.</param>
    /// <param name="settle">The quiet time; <see cref="DefaultSettle"/> when null.</param>
    internal SkinFolderWatch(IEnumerable<string> roots, Action changed, TimeSpan? settle = null)
    {
        _changed = changed;
        Settle = settle ?? DefaultSettle;
        _timer = new Timer(_ => Fire());
        foreach (string root in roots)
        {
            FileSystemWatcher? watcher = null;
            try
            {
                if (!Directory.Exists(root)) continue;
                watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                        | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                watcher.Changed += (_, e) => Seen(e.Name);
                watcher.Created += (_, e) => Seen(e.Name);
                watcher.Deleted += (_, e) => Seen(e.Name);
                watcher.Renamed += (_, e) => { Seen(e.Name); Seen(e.OldName); };
                // An overflow loses events, so the skin is read again in case
                // one of them was its own.
                watcher.Error += (_, _) => Schedule();
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Out of inotify watches, or a directory that went away: that
                // root is left unwatched and Reload still reads it.
                watcher?.Dispose();
            }
        }
    }

    internal TimeSpan Settle { get; }

    /// <summary>How many skin directories are being watched, for the tests.</summary>
    internal int Watching => _watchers.Count;

    /// <summary>
    /// The id of the skin to follow, or null to follow none. Material is not
    /// a folder, so <c>default</c> follows none too.
    /// </summary>
    internal string? Id
    {
        get { lock (_gate) return _id; }
        set
        {
            lock (_gate)
            {
                _id = value is { Length: > 0 } and not "default" ? value : null;
                if (_id is null) _timer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }
    }

    /// <summary>A path relative to a root: its first part is the skin folder it belongs to.</summary>
    private void Seen(string? relative)
    {
        if (relative is not { Length: > 0 }) return;
        int slash = relative.IndexOf(Path.DirectorySeparatorChar);
        string folder = slash < 0 ? relative : relative[..slash];
        lock (_gate)
        {
            if (_id is null || !string.Equals(folder, _id, StringComparison.Ordinal)) return;
        }
        Schedule();
    }

    private void Schedule()
    {
        lock (_gate)
        {
            if (_disposed || _id is null) return;
            // Each event pushes the read back, so a burst of writes is one read.
            _timer.Change(Settle, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        lock (_gate)
        {
            if (_disposed || _id is null) return;
        }
        _changed();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        foreach (FileSystemWatcher watcher in _watchers) watcher.Dispose();
        _timer.Dispose();
    }
}
