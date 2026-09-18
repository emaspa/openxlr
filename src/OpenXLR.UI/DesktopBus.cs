using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace OpenXLR.UI;

/// <summary>Small typed calls for the desktop portal and KWin, on a private UI connection.</summary>
internal sealed class DesktopBus(DBusConnection connection)
{
    internal const string Portal = "org.freedesktop.portal.Desktop";
    internal const string PortalPath = "/org/freedesktop/portal/desktop";
    internal const string Shortcuts = "org.freedesktop.portal.GlobalShortcuts";
    internal delegate void Body(ref MessageWriter writer);
    internal DBusConnection Connection => connection;

    /// <summary>The user's session bus, as the desktop and the tests set it.</summary>
    internal static string SessionAddress()
        => Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") ?? DBusAddress.Session
           ?? throw new InvalidOperationException("No session bus is available.");

    internal Task<T> Call<T>(string destination, string path, string iface, string method,
        string? signature, Body? body, MessageValueReader<T> reader, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination, path, iface, method, signature);
            body?.Invoke(ref writer);
            return AwaitReply(connection.CallMethodAsync(writer.CreateMessage(), reader), cancel);
        }
        finally { writer.Dispose(); }
    }

    private async Task<T> AwaitReply<T>(Task<T> pending, CancellationToken cancel)
    {
        try { return await pending.WaitAsync(TimeSpan.FromSeconds(3), cancel).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // The protocol library has no cancellation for individual method
            // replies. A timed-out waiter alone leaves its reply registered.
            // Close this private desktop session to release all pending calls;
            // its observers report the disconnection and Apply can reconnect.
            connection.Dispose();
            try { await pending.ConfigureAwait(false); }
            catch (Exception) { /* observe the reply completed by disconnection */ }
            throw;
        }
    }

    internal Task Empty(string destination, string path, string iface, string method,
        string? signature = null, Body? body = null, CancellationToken cancel = default)
        => Call(destination, path, iface, method, signature, body, static (_, _) => true, cancel);

    internal async Task<Dictionary<string, VariantValue>> Request(string method, string signature,
        Func<string, Body> body, CancellationToken cancel)
    {
        string token = "openxlr_" + Guid.NewGuid().ToString("N");
        string path = "/org/freedesktop/portal/desktop/request/" + connection.UniqueName![1..].Replace('.', '_') + "/" + token;
        var done = new TaskCompletionSource<(uint Code, Dictionary<string, VariantValue> Data)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable response = await connection.AddMatchAsync(new MatchRule
        {
            Type = MessageType.Signal, Sender = Portal, Path = path,
            Interface = "org.freedesktop.portal.Request", Member = "Response",
        }, static (message, _) =>
        {
            var reader = message.GetBodyReader();
            return (reader.ReadUInt32(), reader.ReadDictionaryOfStringToVariantValue());
        }, notification =>
        {
            if (notification.IsCompletion) done.TrySetException(notification.Exception);
            else done.TrySetResult(notification.Value);
        }, emitOnCapturedContext: false, flags: ObserverFlags.EmitOnConnectionClosed | ObserverFlags.EmitOnReaderFailed);
        try
        {
            string actual = await Call(Portal, PortalPath, Shortcuts, method, signature, body(token),
                static (message, _) => message.GetBodyReader().ReadObjectPath().ToString(), cancel);
            if (actual != path) throw new InvalidOperationException("the desktop portal returned an unexpected request path");
            var result = await done.Task.WaitAsync(TimeSpan.FromMinutes(2), cancel);
            if (result.Code != 0) throw new InvalidOperationException(result.Code == 1
                ? "Shortcut configuration was cancelled." : "The desktop refused the shortcut request.");
            return result.Data;
        }
        catch
        {
            try { await Empty(Portal, path, "org.freedesktop.portal.Request", "Close"); }
            catch (Exception) { /* the request may already be closed or the portal gone */ }
            throw;
        }
    }
}
