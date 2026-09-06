using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenXLR.UI;

/// <summary>
/// The UI's link to the daemon: one WebSocket, reconnecting on its own, raising
/// <see cref="StateReceived"/> for every pushed state. State is handed over as a
/// JsonNode so the UI can bind to it without duplicating the daemon's records.
/// </summary>
public sealed class DaemonClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly Uri _uri;
    private readonly CancellationTokenSource _cts = new();
    private ClientWebSocket? _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _lifecycle = new();
    private Task? _runTask;
    private Task? _disposeTask;
    private bool _disposed;
    private readonly Dictionary<string, PendingQuery> _queries = new();
    private readonly Dictionary<string, TaskCompletionSource<string?>> _commands = new();
    private const int MaxMessageBytes = 8 * 1024 * 1024;

    private sealed class PendingQuery
    {
        public readonly TaskCompletionSource<JsonNode?> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Callers;
    }

    public DaemonClient(string url = "ws://127.0.0.1:37890/ws") => _uri = new Uri(url);

    public event Action<JsonNode>? StateReceived;
    public string? LastStateJson { get; private set; }

    public Task<JsonNode?> RequestDiagnosticsAsync(TimeSpan timeout)
        => QueryAsync("diagnostics", "getDiagnostics", timeout);

    public Task<JsonNode?> RequestPluginsAsync(TimeSpan timeout)
        => QueryAsync("plugins", "listPlugins", timeout);

    private async Task<JsonNode?> QueryAsync(string type, string command, TimeSpan timeout)
    {
        PendingQuery query;
        bool send;
        lock (_lifecycle)
        {
            if (_disposed) return null;
            send = !_queries.TryGetValue(type, out query!);
            if (send) _queries[type] = query = new();
            query.Callers++;
        }
        try
        {
            if (send && !await SendAsync(new { cmd = command }, reportErrors: false))
                query.Reply.TrySetResult(null);
            return await query.Reply.Task.WaitAsync(timeout);
        }
        catch (TimeoutException) { return null; }
        finally
        {
            lock (_lifecycle)
            {
                if (--query.Callers == 0 && _queries.TryGetValue(type, out var current)
                    && ReferenceEquals(current, query)) _queries.Remove(type);
            }
        }
    }

    private void CompleteQuery(string type, JsonNode? reply)
    {
        lock (_lifecycle)
            if (_queries.TryGetValue(type, out var query)) query.Reply.TrySetResult(reply);
    }

    public event Action<string>? ErrorReceived;
    public event Action<JsonNode>? MetersReceived;
    public event Action<bool>? ConnectionChanged;

    public void Start()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _runTask ??= Task.Run(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var socket = new ClientWebSocket();
                _socket = socket;
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                connect.CancelAfter(TimeSpan.FromSeconds(5));
                await socket.ConnectAsync(_uri, connect.Token);
                string auth = JsonSerializer.Serialize(new Dictionary<string, object>
                    { ["cmd"] = "auth", ["token"] = OpenXlrPaths.ReadToken() ?? "" });
                await socket.SendAsync(Encoding.UTF8.GetBytes(auth), WebSocketMessageType.Text, true, connect.Token);
                ConnectionChanged?.Invoke(true);
                await ReceiveLoop(socket);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
            }
            finally
            {
                _socket?.Dispose();
                _socket = null;
                lock (_lifecycle)
                {
                    foreach (var query in _queries.Values) query.Reply.TrySetResult(null);
                    _queries.Clear();
                    foreach (var command in _commands.Values)
                        command.TrySetResult("Connection lost. Check the current layout before retrying.");
                    _commands.Clear();
                }
                ConnectionChanged?.Invoke(false);
            }
            try { await Task.Delay(1000, _cts.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ReceiveLoop(ClientWebSocket socket)
    {
        var buf = new byte[16 * 1024];
        while (socket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
        {
            using var ms = new System.IO.MemoryStream();
            WebSocketReceiveResult res;
            do
            {
                res = await socket.ReceiveAsync(buf, _cts.Token);
                if (res.MessageType == WebSocketMessageType.Close)
                {
                    if (res.CloseStatus == WebSocketCloseStatus.PolicyViolation && res.CloseStatusDescription is { Length: > 0 } why)
                        ErrorReceived?.Invoke($"daemon refused this window: {why}");
                    return;
                }
                if (res.MessageType != WebSocketMessageType.Text || ms.Length + res.Count > MaxMessageBytes)
                    throw new WebSocketException("Invalid or oversized daemon message.");
                ms.Write(buf, 0, res.Count);
            } while (!res.EndOfMessage);

            string text = Encoding.UTF8.GetString(ms.ToArray());
            JsonNode? node;
            try { node = JsonNode.Parse(text); }
            catch (JsonException) { continue; }
            if (node is not JsonObject) continue;

            string? type = node["type"]?.GetValue<string>();
            if (type == "error") ErrorReceived?.Invoke(node["message"]?.GetValue<string>() ?? "unknown error");
            else if (type == "state") { LastStateJson = text; StateReceived?.Invoke(node); }
            else if (type == "diagnostics") CompleteQuery(type, node);
            else if (type == "plugins") CompleteQuery(type, node["plugins"]);
            else if (type == "commandResult" && node["requestId"]?.GetValue<string>() is string id)
            {
                TaskCompletionSource<string?>? command;
                lock (_lifecycle) _commands.TryGetValue(id, out command);
                command?.TrySetResult(node["error"]?.GetValue<string>());
            }
            else if (type == "meters" && node["levels"] is JsonNode levels) MetersReceived?.Invoke(levels);
        }
    }

    public Task SetActiveDeviceAsync(string usbId)
        => SendAsync(new Dictionary<string, object> { ["cmd"] = "setActiveDevice", ["device"] = usbId });

    public Task SaveProfileAsync(string name)
        => SendAsync(new Dictionary<string, object> { ["cmd"] = "saveProfile", ["name"] = name });

    public Task LoadProfileAsync(string name)
        => SendAsync(new Dictionary<string, object> { ["cmd"] = "loadProfile", ["name"] = name });

    public Task DeleteProfileAsync(string name)
        => SendAsync(new Dictionary<string, object> { ["cmd"] = "deleteProfile", ["name"] = name });

    public Task SetRecallOnConnectAsync(string? name)
        => SendAsync(new Dictionary<string, object> { ["cmd"] = "setRecallOnConnect", ["name"] = name ?? "" });

    public Task ResetDeviceAsync()
        => SendAsync(new Dictionary<string, object> { ["cmd"] = "resetDevice" });

    public Task SetControlAsync(string control, object value)
        => SendAsync(new Dictionary<string, object> { ["cmd"] = "set", ["control"] = control, ["value"] = value });

    public Task SetLevelAsync(string channel, string mix, double value)
        => SendAsync(new Dictionary<string, object>
        { ["cmd"] = "setLevel", ["channel"] = channel, ["mix"] = mix, ["value"] = value });

    public Task SetChannelMutedAsync(string channel, string mix, bool muted)
        => SendAsync(new Dictionary<string, object>
        { ["cmd"] = "setChannelMuted", ["channel"] = channel, ["mix"] = mix, ["value"] = muted });

    public Task SetMixVolumeAsync(string mix, double value)
        => SendAsync(new Dictionary<string, object> { ["cmd"] = "setMixVolume", ["mix"] = mix, ["value"] = value });

    public Task SetMixMutedAsync(string mix, bool muted)
        => SendAsync(new Dictionary<string, object> { ["cmd"] = "setMixMuted", ["mix"] = mix, ["value"] = muted });

    public Task<string?> CreateChannelAsync(string name)
        => EditLayoutAsync(new() { ["cmd"] = "createChannel", ["name"] = name });

    public Task<string?> RenameChannelAsync(string channel, string name)
        => EditLayoutAsync(new() { ["cmd"] = "renameChannel", ["channel"] = channel, ["name"] = name });

    public Task<string?> DeleteChannelAsync(string channel)
        => EditLayoutAsync(new() { ["cmd"] = "deleteChannel", ["channel"] = channel });

    public Task<string?> CreateMixAsync(string name)
        => EditLayoutAsync(new() { ["cmd"] = "createMix", ["name"] = name });

    public Task<string?> RenameMixAsync(string mix, string name)
        => EditLayoutAsync(new() { ["cmd"] = "renameMix", ["mix"] = mix, ["name"] = name });

    public Task<string?> DeleteMixAsync(string mix)
        => EditLayoutAsync(new() { ["cmd"] = "deleteMix", ["mix"] = mix });

    private async Task<string?> EditLayoutAsync(Dictionary<string, object> payload)
    {
        string id = Guid.NewGuid().ToString("N");
        var waiter = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lifecycle)
        {
            if (_disposed) return "Daemon disconnected; no change was sent.";
            _commands[id] = waiter;
        }
        payload["requestId"] = id;
        try
        {
            if (!await SendAsync(payload)) return "Daemon disconnected; no change was sent.";
            return await waiter.Task.WaitAsync(TimeSpan.FromSeconds(45));
        }
        catch (TimeoutException) { return "No confirmation from daemon. Check the current layout before retrying."; }
        finally { lock (_lifecycle) _commands.Remove(id); }
    }

    public Task SetMonitorOutputAsync(string? device)
        => SendAsync(new Dictionary<string, object?> { ["cmd"] = "setMonitorOutput", ["device"] = device });

    public Task SetMonitorOutputsAsync(IReadOnlyList<string> devices)
        => SendAsync(new Dictionary<string, object?> { ["cmd"] = "setMonitorOutputs", ["devices"] = devices });

    public Task SetMonitorFeedAsync(string device, string mix)
        => SendAsync(new Dictionary<string, object?> { ["cmd"] = "setMonitorFeed", ["device"] = device, ["mix"] = mix });

    public Task SetOutputVolumeAsync(double value)
        => SendAsync(new Dictionary<string, object> { ["cmd"] = "setOutputVolume", ["value"] = value });

    public Task SetEnforcedDefaultsAsync(string? sink, string? source)
        => SendAsync(new Dictionary<string, object?>
        { ["cmd"] = "setEnforcedDefaults", ["sink"] = sink, ["source"] = source });

    public Task AssignAppAsync(string identity, string channel, string? label = null)
        => SendAsync(new Dictionary<string, object?> { ["cmd"] = "assignApp", ["identity"] = identity, ["channel"] = channel, ["label"] = label });

    public Task SetAuxPortEnabledAsync(bool on)
        => SendAsync(new Dictionary<string, object> { ["cmd"] = "setAuxPortEnabled", ["value"] = on });

    public Task SetLowCutHzAsync(int hz)
        => SendAsync(new Dictionary<string, object> { ["cmd"] = "setLowCutHz", ["value"] = hz });

    public Task SetSoftClipGuardAsync(bool on)
        => SendAsync(new Dictionary<string, object> { ["cmd"] = "setSoftClipGuard", ["value"] = on });

    public Task SetInsertsAsync(string channel, IReadOnlyList<object> inserts)
        => SendAsync(new Dictionary<string, object> { ["cmd"] = "setInserts", ["channel"] = channel, ["inserts"] = inserts });

    public Task SetInsertBypassAsync(string channel, string insertId, bool bypass)
        => SendAsync(new Dictionary<string, object>
        { ["cmd"] = "setInsertBypass", ["channel"] = channel, ["insertId"] = insertId, ["value"] = bypass });

    public Task SetInsertParamAsync(string channel, string insertId, string symbol, double value)
        => SendAsync(new Dictionary<string, object>
        { ["cmd"] = "setInsertParam", ["channel"] = channel, ["insertId"] = insertId, ["symbol"] = symbol, ["value"] = value });

    public Task ForgetAppAsync(string identity)
        => SendAsync(new Dictionary<string, object?> { ["cmd"] = "forgetApp", ["identity"] = identity });

    public Task AssignStreamAsync(int streamId, string channel)
        => SendAsync(new Dictionary<string, object>
        { ["cmd"] = "assignStream", ["streamId"] = streamId, ["channel"] = channel });

    private async Task<bool> SendAsync(object payload, bool reportErrors = true)
    {
        ClientWebSocket? s;
        CancellationToken stop;
        lock (_lifecycle)
        {
            s = _disposed ? null : _socket;
            stop = _disposed ? new CancellationToken(true) : _cts.Token;
        }
        if (s is null || s.State != WebSocketState.Open)
        {
            if (reportErrors) ErrorReceived?.Invoke("Daemon disconnected; no change was sent.");
            return false;
        }
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        bool acquired = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await _sendLock.WaitAsync(timeout.Token);
            acquired = true;
            await s.SendAsync(bytes, WebSocketMessageType.Text, true, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            if (reportErrors) ErrorReceived?.Invoke("Connection lost; the change could not be confirmed.");
            return false;
        }
        finally { if (acquired) _sendLock.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycle)
        {
            _disposed = true;
            return new(_disposeTask ??= Task.Run(DisposeCoreAsync));
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _cts.CancelAsync();
        if (_runTask is not null) await _runTask;
        _cts.Dispose();
    }
}
