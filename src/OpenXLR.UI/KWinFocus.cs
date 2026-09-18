using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace OpenXLR.UI;

/// <summary>Read only the current process id through a short-lived KWin script.</summary>
internal sealed class KWinFocus(DesktopBus bus) : IPathMethodHandler
{
    internal const string Service = "org.openxlr.Desktop";
    public string Path => "/org/openxlr/Desktop";
    public bool HandlesChildPaths => false;
    private readonly SemaphoreSlim _oneRequest = new(1);
    private volatile string? _cookie, _kwinOwner;
    private volatile TaskCompletionSource<uint>? _answer;

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml(["""
                <interface name="org.openxlr.Desktop">
                  <method name="GetFocusedProcess"><arg type="u" direction="out"/></method>
                  <method name="ReportFocus"><arg type="s" direction="in"/><arg type="s" direction="in"/></method>
                </interface>
                """u8.ToArray()]);
        }
        else if (context.Request.InterfaceAsString == Service && context.Request.MemberAsString == "GetFocusedProcess"
            && context.Request.SignatureAsString == "")
        {
            // KWin reports back on this same connection. Do not hold its
            // message reader while waiting for that callback.
            context.DisposesAsynchronously = true;
            _ = ReplyFocusAsync(context);
        }
        else if (context.Request.InterfaceAsString == Service && context.Request.MemberAsString == "ReportFocus"
            && context.Request.SignatureAsString == "ss")
        {
            var reader = context.Request.GetBodyReader();
            string cookie = reader.ReadString(), process = reader.ReadString();
            if (context.Request.SenderAsString == _kwinOwner && cookie == _cookie
                && uint.TryParse(process, NumberStyles.None, CultureInfo.InvariantCulture, out uint pid))
                _answer?.TrySetResult(pid);
            using var reply = context.CreateReplyWriter(null);
            context.Reply(reply.CreateMessage());
        }
        else context.ReplyUnknownMethodError();
        return ValueTask.CompletedTask;
    }

    private async Task ReplyFocusAsync(MethodContext context)
    {
        try
        {
            uint pid = await ReadAsync(context.RequestAborted);
            using var reply = context.CreateReplyWriter("u");
            reply.WriteUInt32(pid);
            context.Reply(reply.CreateMessage());
        }
        catch (Exception)
        {
            try { context.ReplyError("org.openxlr.Desktop.Unavailable", "Focused routing requires a responsive KDE Plasma session."); }
            catch (Exception) { /* the requesting connection has already closed */ }
        }
        finally { context.Dispose(); }
    }

    internal async Task<uint> ReadAsync(CancellationToken cancel)
    {
        if (!await _oneRequest.WaitAsync(0, cancel)) throw new InvalidOperationException("a focus query is already running");
        const string scriptName = "openxlr-focus-query";
        string file = OpenXlrPaths.ConfigFile("desktop-focus.js");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        // The scripting calls ride a connection of their own. A compositor
        // that answers late costs this one query, not the shortcut session
        // that the shared connection holds; KWin still reports back there.
        using var scripting = new DBusConnection(DesktopBus.SessionAddress());
        var kwin = new DesktopBus(scripting);
        try
        {
            await scripting.ConnectAsync().AsTask().WaitAsync(deadline.Token);
            _kwinOwner = await kwin.Call("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus",
                "GetNameOwner", "s", (ref MessageWriter w) => w.WriteString("org.kde.KWin"),
                static (message, _) => message.GetBodyReader().ReadString(), deadline.Token);
            await Unload(deadline.Token);
            _cookie = Guid.NewGuid().ToString("N");
            _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
            // Serialize every data value as a JavaScript string literal. No
            // window titles, executable paths or user text enter the script.
            OpenXlrPaths.WriteAtomic(file, $"""
                const window = workspace.activeWindow;
                callDBus({JsonSerializer.Serialize(bus.Connection.UniqueName)}, "/org/openxlr/Desktop",
                    "org.openxlr.Desktop", "ReportFocus", {JsonSerializer.Serialize(_cookie)}, String(window ? window.pid : 0));
                """);
            int id = await kwin.Call("org.kde.KWin", "/Scripting", "org.kde.kwin.Scripting", "loadScript", "ss",
                (ref MessageWriter w) => { w.WriteString(file); w.WriteString(scriptName); },
                static (message, _) => message.GetBodyReader().ReadInt32(), deadline.Token);
            if (id < 0) throw new InvalidOperationException("KWin refused the focus query");
            await kwin.Empty("org.kde.KWin", "/Scripting/Script" + id.ToString(CultureInfo.InvariantCulture),
                "org.kde.kwin.Script", "run", cancel: deadline.Token);
            return await _answer.Task.WaitAsync(deadline.Token);
        }
        finally
        {
            _cookie = _kwinOwner = null;
            _answer = null;
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await Unload(cleanup.Token);
            }
            catch (Exception) { /* next query removes a script left by a disappearing compositor */ }
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _oneRequest.Release();
        }

        Task Unload(CancellationToken token) => kwin.Call("org.kde.KWin", "/Scripting", "org.kde.kwin.Scripting",
            "unloadScript", "s", (ref MessageWriter w) => w.WriteString(scriptName),
            static (message, _) => message.GetBodyReader().ReadBool(), token);
    }
}
