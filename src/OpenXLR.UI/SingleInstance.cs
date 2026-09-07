using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace OpenXLR.UI;

/// <summary>
/// One window per user. The first instance listens on a Unix socket next
/// to the daemon's token; a later launch (the application menu, a second
/// autostart, a shell) finds it, asks it to show its window and exits at
/// once, so nothing ever opens twice. A socket file whose owner is gone is
/// replaced, so a crash never blocks the next start.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string Show = "show";
    private readonly Socket _listener;
    private readonly string _path;

    /// <summary>Runs on a background thread when another launch asks for the window.</summary>
    public Action? ShowRequested { get; set; }

    private SingleInstance(Socket listener, string path) { _listener = listener; _path = path; }

    public static string DefaultPath => Path.Combine(Path.GetDirectoryName(OpenXlrPaths.TokenPath)!, "ui.sock");

    /// <summary>
    /// The listening primary, or null when a running window took the request
    /// and this process should end.
    /// </summary>
    public static SingleInstance? TryBecomePrimary(string? path = null)
    {
        path ??= DefaultPath;
        try { OpenXlrPaths.EnsurePrivateDir(Path.GetDirectoryName(path)!); }
        catch (Exception) { return Listen(path) ?? Fallback(); }
        if (AskRunningWindow(path)) return null;
        return Listen(path) ?? (AskRunningWindow(path) ? null : Fallback());

        // No socket at all (an unwritable runtime directory): run without the
        // guard rather than refuse to start.
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
                    peer.ReceiveTimeout = 2000;
                    int n = peer.Receive(buf);
                    if (Encoding.ASCII.GetString(buf, 0, n).Trim() == Show) ShowRequested?.Invoke();
                }
                catch (Exception) { /* a peer that says nothing is ignored */ }
            }
        }
    }

    public void Dispose()
    {
        try { _listener.Dispose(); } catch (Exception) { }
        if (_path.Length > 0) { try { File.Delete(_path); } catch (Exception) { } }
    }
}
