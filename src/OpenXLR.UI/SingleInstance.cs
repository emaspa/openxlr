using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace OpenXLR.UI;

/// <summary>
/// One window per user. The first instance listens on a Unix socket next
/// to the daemon's control socket; a later launch (the application menu, a second
/// autostart, a shell) finds it, asks it to show its window and exits at
/// once, so nothing ever opens twice. A socket file whose owner is gone is
/// replaced, so a crash never blocks the next start.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string Show = "show";
    private readonly Socket _listener;
    private readonly string _path;
    private readonly object _showGate = new();
    private Action? _showRequested;
    private bool _showPending;
    private int _disposed;

    /// <summary>Runs on a background thread when another launch asks for the window.</summary>
    public Action? ShowRequested
    {
        get { lock (_showGate) return _showRequested; }
        set
        {
            bool show;
            lock (_showGate)
            {
                _showRequested = value;
                show = _showPending && value is not null;
                if (show) _showPending = false;
            }
            if (show) value!();
        }
    }

    private void RequestShow()
    {
        Action? show;
        lock (_showGate)
        {
            show = _showRequested;
            if (show is null) _showPending = true;
        }
        show?.Invoke();
    }

    private SingleInstance(Socket listener, string path) { _listener = listener; _path = path; }

    public static string DefaultPath => Path.Combine(OpenXlrPaths.RuntimeDir,
        FlatpakEnvironment.IsFlatpak ? "flatpak-ui.sock" : "ui.sock");

    /// <summary>
    /// The listening primary, or null when a running window took the request
    /// and this process should end.
    /// </summary>
    public static SingleInstance? TryBecomePrimary(string? path = null)
    {
        path ??= DefaultPath;
        // The daemon's rules for the directory: a link, another user's
        // directory or a parent others can write to is not used, since
        // whoever controls it could keep this UI from opening.
        try { OpenXlrPaths.EnsurePrivateRuntimeDir(Path.GetDirectoryName(path)!); }
        catch (Exception) { return Fallback(); }
        if (AskRunningWindow(path)) return null;
        return Listen(path) ?? (AskRunningWindow(path) ? null : Fallback());

        // No socket at all (an unwritable or untrusted runtime directory):
        // run without the guard rather than refuse to start.
        static SingleInstance? Fallback() => new(new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified), "");
    }

    private static bool AskRunningWindow(string path)
    {
        if (!File.Exists(path)) return false;
        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            client.Connect(new UnixDomainSocketEndPoint(path));
            client.Send(Encoding.ASCII.GetBytes(Show + "\n"));
            client.Shutdown(SocketShutdown.Send);
            return true;
        }
        catch (SocketException)
        {
            // Nobody listens: a leftover from a window that did not exit cleanly.
            try { File.Delete(path); } catch (Exception) { }
            return false;
        }
    }

    private static SingleInstance? Listen(string path)
    {
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(4);
        }
        catch (SocketException)
        {
            listener.Dispose();
            return null;   // another launch bound it first
        }
        var instance = new SingleInstance(listener, path);
        new Thread(instance.Accept) { IsBackground = true, Name = "single-instance" }.Start();
        return instance;
    }

    private void Accept()
    {
        var buf = new byte[64];
        while (true)
        {
            Socket peer;
            try { peer = _listener.Accept(); }
            catch (Exception) { return; }   // disposed
            using (peer)
            {
                try
                {
                    long deadline = Environment.TickCount64 + 2000;
                    int n = 0;
                    while (n < buf.Length)
                    {
                        long remaining = deadline - Environment.TickCount64;
                        if (remaining <= 0) break;
                        peer.ReceiveTimeout = (int)remaining;
                        int read = peer.Receive(buf.AsSpan(n));
                        if (read == 0) break;
                        n += read;
                        int end = Array.IndexOf(buf, (byte)'\n', 0, n);
                        if (end >= 0) { n = end; break; }
                    }
                    if (Encoding.ASCII.GetString(buf, 0, n).Trim() == Show) RequestShow();
                }
                catch (Exception) { /* a peer that says nothing is ignored */ }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _listener.Dispose(); } catch (Exception) { }
        if (_path.Length > 0) { try { File.Delete(_path); } catch (Exception) { } }
    }
}
