using OpenXLR.Core;

namespace OpenXLR.Daemon;

/// <summary>
/// One daemon per user. The lock file lives next to the control socket in the
/// runtime directory and is held for the life of the process; the kernel
/// releases it when the process ends, however it ends. A second instance,
/// started by hand while the service runs, stops here with a clear message
/// instead of waiting a minute for a port it will never get.
/// </summary>
public static class DaemonLock
{
    public static string Path => OpenXlrPaths.DaemonLockPath;

    /// <summary>
    /// How long contention is retried. Clients from 0.3.0 to 0.7.1 that cannot
    /// reach the control socket test the lock with a shared lock they drop at
    /// once; a daemon starting in that instant waits it out instead of
    /// reporting another daemon.
    /// </summary>
    internal static readonly TimeSpan ContentionGrace = TimeSpan.FromMilliseconds(200);

    /// <summary>The held lock, or null when another process holds it.</summary>
    public static FileStream? TryAcquire(string? path = null)
    {
        path ??= Path;
        ControlSocketListener.PrepareDirectory(System.IO.Path.GetDirectoryName(path)!);
        long deadline = Environment.TickCount64 + (long)ContentionGrace.TotalMilliseconds;
        while (true)
        {
            try
            {
                // FileShare.None is an exclusive advisory lock on Unix; the
                // stream is never written, only held.
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            // Unix EWOULDBLOCK or the runtime's mapped sharing violation means
            // contention. Directory, permission and other I/O failures must reach
            // the caller rather than claim that a healthy daemon already exists.
            catch (IOException ex) when ((ex.HResult & 0xffff) is 11 or 32)
            {
                if (Environment.TickCount64 >= deadline) return null;
                Thread.Sleep(20);
            }
        }
    }

    /// <summary>
    /// Remove the API token a release before the control socket left behind.
    /// Call while holding the lock: every release that wrote a token held this
    /// same lock while it ran, so the token can no longer belong to a live
    /// daemon. Under the runtime directory logout would clear it; without one
    /// it sits in the configuration directory until removed here.
    /// </summary>
    public static void RemoveLegacyToken(string? path = null)
    {
        path ??= OpenXlrPaths.LegacyTokenPath;
        try
        {
            // A link is removed as a link; its target is never touched.
            if (File.Exists(path) || new FileInfo(path).LinkTarget is not null) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* not ours to remove */ }
    }
}
