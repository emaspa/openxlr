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

    // Reuse the snapshot's types and stable ids; no second mixer or command model.
    internal static IResult MixerResource(OpenXLR.Core.Mixing.MixerState? mixer, string resource, string? id = null)
    {
        if (mixer is null) return Results.StatusCode(503);
        object? value = resource switch
        {
            "mixer" => mixer,
            "channels" => id is null ? mixer.Channels : mixer.Channels.FirstOrDefault(c => c.Id == id),
            "mixes" => id is null ? mixer.Mixes : mixer.Mixes.FirstOrDefault(m => m.Id == id),
            "inserts" => id is null ? mixer.Inserts : mixer.Inserts.GetValueOrDefault(id),
            _ => null,
        };
        return value is null ? Results.NotFound() : Results.Json(value);
    }

    /// <summary>
    /// The token is read per request: the daemon publishes it only once the
    /// port is bound (see ApiToken), which is after these endpoints are
    /// mapped, and a restart rotates it.
    /// </summary>
    internal static void Map(WebApplication app, bool enabled = true)
    {
        // Bind access control to the resolved endpoints, not a request-path
        // exception. Events use first-frame authentication in WebSocketHub.
        var budget = new CommandBudget();
        var commandGate = new SemaphoreSlim(1, 1);
        var api = app.MapGroup("/api/v1").AddEndpointFilter(async (invocation, next) =>
        {
            HttpContext context = invocation.HttpContext;
            context.Response.Headers.CacheControl = "no-store";
            if (!LoopbackOrigin.IsAllowed(context.Request.Headers.Origin))
                return Results.StatusCode(403);
            if (!Authorized(context.Request, ApiToken.Current))
            {
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return Results.StatusCode(401);
            }
            if (!enabled) return Results.StatusCode(503);
            lock (budget) if (!budget.TryTake()) return Results.StatusCode(429);
            return await next(invocation);
        });

        app.MapGet("/healthz", () => Results.Json(new { status = "alive" }));
        api.MapGet("", () => Results.Json(new { apiVersion = "1", state = "/api/v1/state",
            devices = "/api/v1/devices", profiles = "/api/v1/profiles", mixer = "/api/v1/mixer",
            channels = "/api/v1/channels", mixes = "/api/v1/mixes", inserts = "/api/v1/inserts",
            plugins = "/api/v1/plugins", pluginSetup = "/api/v1/plugin-setup",
            pluginDiagnostics = "/api/v1/plugin-diagnostics", diagnostics = "/api/v1/diagnostics",
            editorRules = "/api/v1/editor-rules", commands = "/api/v1/commands", events = "/api/v1/events" }));
        api.MapGet("/state", (WebSocketHub hub) => Results.Json(hub.Snapshot()));
        api.MapGet("/devices", (WebSocketHub hub) =>
        {
            var state = hub.Snapshot();
            return Results.Json(new { state.Connected, state.Device, state.Capabilities, state.Detected, state.Devices });
        });
        api.MapGet("/profiles", (WebSocketHub hub) =>
        {
            var state = hub.Snapshot();
            return Results.Json(new { state.Profiles, state.ActiveProfile, state.RecallOnConnect });
        });
        api.MapGet("/mixer", (WebSocketHub hub) => MixerResource(hub.Snapshot().Mixer, "mixer"));
        foreach (string resource in new[] { "channels", "mixes", "inserts" })
        {
            api.MapGet("/" + resource, (WebSocketHub hub) => MixerResource(hub.Snapshot().Mixer, resource));
            api.MapGet("/" + resource + "/{id}", (string id, WebSocketHub hub) => MixerResource(hub.Snapshot().Mixer, resource, id));
        }
        foreach (var (path, command) in new[] { ("plugins", "listPlugins"), ("plugin-setup", "getPluginSetup"),
            ("plugin-diagnostics", "getPluginDiagnostics"), ("diagnostics", "getDiagnostics"), ("editor-rules", "getNativeEditorRules") })
        {
            api.MapGet("/" + path, async (HttpContext context, WebSocketHub hub) =>
            {
                if (!await commandGate.WaitAsync(0, context.RequestAborted)) return Results.StatusCode(429);
                try
                {
                    var result = await hub.ExecuteForApiAsync(JsonSerializer.Serialize(new { cmd = command }));
                    return Results.Json(result, statusCode: result.Ok ? 200 : 400);
                }
                finally { commandGate.Release(); }
            });
        }
        api.MapPost("/commands", async (HttpContext context, WebSocketHub hub) =>
        {
            if (!IsJson(context.Request.ContentType)) return Results.StatusCode(415);
            if (context.Request.ContentLength > MaxCommandBytes) return Results.StatusCode(413);
            // One in-flight HTTP command, without an unbounded command queue.
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
            context.Response.Headers.CacheControl = "no-store";
            if (!LoopbackOrigin.IsAllowed(context.Request.Headers.Origin))
            { context.Response.StatusCode = 403; return; }
            if (!enabled) { context.Response.StatusCode = 503; return; }
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
