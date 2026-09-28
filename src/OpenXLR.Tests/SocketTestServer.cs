using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace OpenXLR.Tests;

/// <summary>Real loopback WebSockets; no audio service, devices or user settings are touched.</summary>
internal sealed class SocketTestServer(WebApplication app) : IAsyncDisposable
{
    public string Url => app.Urls.Single().Replace("http:", "ws:", StringComparison.Ordinal) + "/ws";

    public static async Task<SocketTestServer> Start(Func<WebSocket, CancellationToken, Task> handler,
        Action<WebApplication>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.UseWebSockets();
        configure?.Invoke(app);
        app.Map("/ws", async (HttpContext context) =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            try { await handler(socket, app.Lifetime.ApplicationStopping); }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
        });
        await app.StartAsync();
        return new(app);
    }

    public static Task Send(WebSocket socket, object payload, CancellationToken stop)
        => socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload), WebSocketMessageType.Text, true, stop);

    public static async Task<JsonNode> Receive(WebSocket socket, CancellationToken stop)
    {
        var buffer = new byte[4096];
        using var bytes = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, stop);
            if (result.MessageType == WebSocketMessageType.Close) throw new OperationCanceledException();
            bytes.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return JsonNode.Parse(Encoding.UTF8.GetString(bytes.ToArray()))!;
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await app.StopAsync(timeout.Token);
        await app.DisposeAsync();
    }
}
