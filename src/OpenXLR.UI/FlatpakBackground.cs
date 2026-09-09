using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace OpenXLR.UI;

/// <summary>Ask the desktop to keep audio running and optionally start it at login.</summary>
internal static class FlatpakBackground
{
    public static async Task<bool> RequestAsync(bool autostart, System.Threading.CancellationToken cancellation = default)
    {
        using var connection = new DBusConnection(DBusAddress.Session!);
        await connection.ConnectAsync();
        string token = "openxlr_" + Guid.NewGuid().ToString("N");
        string path = "/org/freedesktop/portal/desktop/request/"
            + connection.UniqueName!.TrimStart(':').Replace('.', '_') + "/" + token;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable watch = await connection.AddMatchAsync(
            new MatchRule { Type = MessageType.Signal, Sender = "org.freedesktop.portal.Desktop",
                Path = path, Interface = "org.freedesktop.portal.Request", Member = "Response" },
            static (message, _) =>
            {
                var reader = message.GetBodyReader();
                uint code = reader.ReadUInt32();
                var results = reader.ReadDictionaryOfStringToVariantValue();
                return (code, results);
            },
            notification =>
            {
                if (notification.HasValue)
                {
                    var (code, results) = notification.Value;
                    completion.TrySetResult(code == 0 && results.TryGetValue(autostart ? "autostart" : "background", out var granted)
                        && granted.GetBool());
                }
                else if (notification.Exception is { } error) completion.TrySetException(error);
            }, emitOnCapturedContext: false);
        using var timeout = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await connection.CallMethodAsync(CreateRequest(), static (message, _) => message.GetBodyReader().ReadObjectPathAsString())
                .WaitAsync(timeout.Token);
            return await completion.Task.WaitAsync(timeout.Token);
        }
        finally
        {
            if (!completion.Task.IsCompleted)
            {
                try { await connection.CallMethodAsync(CreateClose()).WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception) { /* Disconnecting also releases the request. */ }
            }
        }

        MessageBuffer CreateClose()
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(destination: "org.freedesktop.portal.Desktop", path: path,
                @interface: "org.freedesktop.portal.Request", member: "Close");
            return writer.CreateMessage();
        }

        MessageBuffer CreateRequest()
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(destination: "org.freedesktop.portal.Desktop",
                path: "/org/freedesktop/portal/desktop", @interface: "org.freedesktop.portal.Background",
                member: "RequestBackground", signature: "sa{sv}");
            writer.WriteString("");
            writer.WriteDictionary(new Dictionary<string, VariantValue>
            {
                ["handle_token"] = token, ["reason"] = "Keep OpenXLR's audio routing and effects running when its window is closed.",
                ["autostart"] = autostart,
                ["commandline"] = new Tmds.DBus.Protocol.Array<string>(["openxlr", "--background"]),
            });
            return writer.CreateMessage();
        }
    }
}
