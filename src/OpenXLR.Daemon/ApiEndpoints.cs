using System.Net.Http.Headers;
using System.Text;
using OpenXLR.Core;
using System.Text.Json;

namespace OpenXLR.Daemon;

internal static class ApiEndpoints
{
    internal const int MaxCommandBytes = 64 * 1024;
    internal static bool Authorized(HttpRequest request, string? secret)
        => request.Headers.Authorization.Count == 1 &&
           AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var header) &&
           header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) &&
           header.Parameter is { Length: 64 } token &&
           ApiToken.Matches(secret, JsonSerializer.SerializeToUtf8Bytes(new { cmd = "auth", token }));

    internal static bool IsJson(string? contentType)
        => MediaTypeHeaderValue.TryParse(contentType, out var value) &&
           value.MediaType?.Equals("application/json", StringComparison.OrdinalIgnoreCase) == true &&
           (value.CharSet is null || value.CharSet.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase));

    internal static async Task<string?> ReadCommandAsync(Stream body, CancellationToken stop)
    {
        using var content = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await body.ReadAsync(buffer, stop)) != 0)
        {
            if (content.Length + read > MaxCommandBytes) return null;
            content.Write(buffer, 0, read);
        }
        return new UTF8Encoding(false, true).GetString(content.ToArray());
    }

    internal static void Map(WebApplication app, string? secret)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/v1"))
            {
                context.Response.Headers.CacheControl = "no-store";
                if (!LoopbackOrigin.IsAllowed(context.Request.Headers.Origin))
                { context.Response.StatusCode = 403; return; }
                if (context.Request.Path != "/api/v1/events" && !Authorized(context.Request, secret))
                {
                    context.Response.Headers.WWWAuthenticate = "Bearer";
                    context.Response.StatusCode = 401;
                    return;
                }
            }
            await next(context);
        });

        app.MapGet("/healthz", () => Results.Json(new { status = "alive" }));
        app.MapGet("/api/v1", () => Results.Json(new { apiVersion = "1", state = "/api/v1/state",
            plugins = "/api/v1/plugins", commands = "/api/v1/commands", events = "/api/v1/events" }));
        app.MapGet("/api/v1/state", (WebSocketHub hub) => Results.Json(hub.Snapshot()));
        app.MapGet("/api/v1/plugins", async (WebSocketHub hub) =>
            Results.Json(await hub.ExecuteForApiAsync("{\"cmd\":\"listPlugins\"}")));
        var budget = new CommandBudget();
        var commandGate = new SemaphoreSlim(1, 1);
        app.MapPost("/api/v1/commands", async (HttpContext context, WebSocketHub hub) =>
        {
            if (!IsJson(context.Request.ContentType)) return Results.StatusCode(415);
            if (context.Request.ContentLength > MaxCommandBytes) return Results.StatusCode(413);
            lock (budget) if (!budget.TryTake()) return Results.StatusCode(429);
            // One in-flight HTTP mutation, without an unbounded command queue.
            if (!await commandGate.WaitAsync(0, context.RequestAborted)) return Results.StatusCode(429);
            try
            {
                string? text;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                try { text = await ReadCommandAsync(context.Request.Body, deadline.Token); }
                catch (DecoderFallbackException) { return Results.BadRequest(); }
                catch (OperationCanceledException) { return Results.StatusCode(408); }
                if (text is null) return Results.StatusCode(413);
                ApiCommandResult result = await hub.ExecuteForApiAsync(text);
                return Results.Json(result, statusCode: result.Ok ? 200 : 400);
            }
            finally { commandGate.Release(); }
        });
        app.Map("/api/v1/events", async (HttpContext context, WebSocketHub hub) =>
        {
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
            {
                KeepAliveInterval = SocketGuard.KeepAliveInterval,
                KeepAliveTimeout = SocketGuard.KeepAliveTimeout,
            });
            await hub.HandleAsync(socket);
        });
    }
}

internal sealed record ApiCommandResult(string ApiVersion, bool Ok, IReadOnlyList<object> Messages);
