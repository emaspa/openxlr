using OpenXLR.Core;

namespace OpenXLR.Daemon;

/// <summary>
/// One daemon per user. The lock file lives next to the token in the
/// runtime directory and is held for the life of the process; the kernel
/// releases it when the process ends, however it ends. A second instance,
/// started by hand while the service runs, stops here with a clear message
/// instead of waiting a minute for a port it will never get.
/// </summary>
public static class DaemonLock
{
    public static string Path => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(OpenXlrPaths.TokenPath)!, "daemon.lock");

    /// <summary>The held lock, or null when another process holds it.</summary>
    public static FileStream? TryAcquire(string? path = null)
    {
        path ??= Path;
        OpenXlrPaths.EnsurePrivateDir(System.IO.Path.GetDirectoryName(path)!);
        try
        {
            // FileShare.None is an exclusive advisory lock on Unix; the
            // stream is never written, only held.
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
