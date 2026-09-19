using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenXLR.Tui;

/// <summary>
/// The connection to the daemon: the documented WebSocket, the token from the
/// runtime directory, and a reconnect loop so the terminal survives a daemon
/// restart the way the window does.
///
/// Everything arrives on the receive task and is handed to the drawing loop as
/// one immutable snapshot, so the two never share a mutable object.
/// </summary>
internal sealed class DaemonLink : IAsyncDisposable
{
    private const int Port = 37890;

    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _meterGate = new();
    private readonly Dictionary<string, MeterTrace> _meters = [];
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private ClientWebSocket? _socket;
    private Task? _pump;
    private readonly SemaphoreSlim _sendGate = new(1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> _requests = new();
    private readonly Lock _pluginGate = new();
    private bool _pluginsWanted;
    private bool _pluginsRequested;
    private (string Channel, string Id, string Kind, string Plugin)[] _chains = [];

    public IReadOnlyDictionary<(string Kind, string Plugin), PluginEntry> Plugins { get; private set; } =
        new Dictionary<(string, string), PluginEntry>();

    public bool PluginsLoaded { get; private set; }

    /// <summary>The latest state, or null before the first one arrives.</summary>
    public Snapshot? State { get; private set; }

    /// <summary>What the connection is doing, for the status line.</summary>
    public string Status { get; private set; } = "connecting";

    /// <summary>True between a successful authentication and the socket closing.</summary>
    public bool Connected { get; private set; }

    /// <summary>The last error the daemon sent back, shown once and then cleared.</summary>
    public string? LastError { get; private set; }

    /// <summary>Raised on the receive task whenever something worth redrawing arrived.</summary>
    public event Action? Changed;

    /// <summary>Every command this client sends, as it goes out. The tests listen here.</summary>
    internal event Action<string>? Sent;

    public void Start() => _pump ??= Task.Run(PumpAsync);

    /// <summary>The live level for a channel or a mix, 0 to 1, or 0 when it is not metering.</summary>
    public double Meter(string prefix, string id) => StereoMeter(prefix, id).Level;

    public MeterReading StereoMeter(string prefix, string id)
    {
        lock (_meterGate)
        {
            return _meters.TryGetValue($"{prefix}:{id}", out MeterTrace? trace)
                ? trace.Read(_clock.Elapsed.TotalSeconds) : default;
        }
    }

    public double[] MeterHistory(string prefix, string id)
    {
        lock (_meterGate)
            return _meters.TryGetValue($"{prefix}:{id}", out MeterTrace? trace)
                ? trace.History(_clock.Elapsed.TotalSeconds) : new double[MeterTrace.Samples];
    }

    /// <summary>Sends one command object, as the API names them.</summary>
    public void Send(string command, Action<JsonObject>? fill = null)
    {
        JsonObject body = new() { ["cmd"] = command };
        fill?.Invoke(body);
        string json = body.ToJsonString();
        Sent?.Invoke(json);
        _ = SendAsync(json);
    }

    public void ClearError() => LastError = null;

    /// <summary>Activate catalogue updates when the Inserts section is first shown.</summary>
    public void EnsurePlugins()
    {
        lock (_pluginGate)
        {
            _pluginsWanted = true;
            if (_pluginsRequested || State is null || !Connected) return;
            _pluginsRequested = true;
            Send("listPlugins");
        }
    }

    private void RefreshPlugins()
    {
        lock (_pluginGate)
        {
            _pluginsRequested = false;
            PluginsLoaded = false;
            if (_pluginsWanted) EnsurePlugins();
        }
    }

    private void UpdateChains(Snapshot snapshot)
    {
        lock (_pluginGate)
        {
            var chains = snapshot.Mixer.Inserts.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .SelectMany(pair => pair.Value.Select(entry =>
                    (pair.Key, entry.Insert.Id, entry.Insert.Kind, entry.Insert.Plugin))).ToArray();
            if (_chains.SequenceEqual(chains))
            {
                if (_pluginsWanted) EnsurePlugins();
                return;
            }
            _chains = chains;
            HashSet<(string, string)> used = chains.Select(slot => (slot.Kind, slot.Plugin)).ToHashSet();
            Plugins = Plugins.Where(pair => used.Contains(pair.Key)).ToDictionary();
            RefreshPlugins();
        }
    }

    /// <summary>Match an editor outcome to its request, never to an unrelated error.</summary>
    public Task<string?> Request(string command, Action<JsonObject> fill)
    {
        string id = Guid.NewGuid().ToString("N");
        TaskCompletionSource<string?> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests[id] = result;
        _ = ExpireRequest(id, result);
        Send(command, body => { fill(body); body["requestId"] = id; });
        return result.Task;
    }

    private async Task ExpireRequest(string id, TaskCompletionSource<string?> result)
    {
        try { await result.Task.WaitAsync(TimeSpan.FromSeconds(20), _stopping.Token).ConfigureAwait(false); }
        catch (TimeoutException) { result.TrySetResult("The daemon did not answer the editor request"); }
        catch (OperationCanceledException) { result.TrySetResult("Disconnected from the daemon"); }
        finally { _requests.TryRemove(id, out _); }
    }

    private async Task SendAsync(string json)
    {
        ClientWebSocket? socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open) return;
        try
        {
            await _sendGate.WaitAsync(_stopping.Token).ConfigureAwait(false);
            try
            {
                if (socket.State != WebSocketState.Open) return;
                await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, _stopping.Token)
                    .ConfigureAwait(false);
                // Defaults can send hundreds of controls. Serialize writes and
                // stay within the daemon's sustained 100 commands per second.
                await Task.Delay(11, _stopping.Token).ConfigureAwait(false);
            }
            finally { _sendGate.Release(); }
        }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
    }

    private async Task PumpAsync()
    {
        int backoffSeconds = 1;
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                await SessionAsync().ConfigureAwait(false);
                backoffSeconds = 1;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error) when (error is WebSocketException or IOException or HttpRequestException)
            {
                Status = "daemon not reachable";
            }

            EndSession();
            if (_stopping.IsCancellationRequested) return;
            try { await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), _stopping.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            backoffSeconds = Math.Min(backoffSeconds * 2, 10);
        }
    }

    /// <summary>Drop replies and cached descriptions when the socket closes.</summary>
    internal void EndSession()
    {
        Connected = false;
        foreach (var request in _requests.Values) request.TrySetResult("Disconnected from the daemon");
        lock (_pluginGate)
        {
            _pluginsRequested = false;
            PluginsLoaded = false;
            Plugins = new Dictionary<(string, string), PluginEntry>();
        }
        Changed?.Invoke();
    }

    private async Task SessionAsync()
    {
        string? token = OpenXlrPaths.ReadToken();
        if (string.IsNullOrEmpty(token))
        {
            Status = "no token, is the daemon running";
            await Task.Delay(TimeSpan.FromSeconds(2), _stopping.Token).ConfigureAwait(false);
            return;
        }

        using ClientWebSocket socket = new();
        _socket = socket;
        Status = "connecting";
        Changed?.Invoke();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{Port}/ws"), _stopping.Token).ConfigureAwait(false);

        // The token is the first thing on the socket; anything else closes it.
        JsonObject auth = new() { ["cmd"] = "auth", ["token"] = token };
        await socket.SendAsync(Encoding.UTF8.GetBytes(auth.ToJsonString()), WebSocketMessageType.Text, true,
            _stopping.Token).ConfigureAwait(false);

        Connected = true;
        Status = "connected";
        await SendAsync("{\"cmd\":\"getState\"}").ConfigureAwait(false);

        byte[] buffer = new byte[64 * 1024];
        using MemoryStream message = new();
        while (socket.State == WebSocketState.Open && !_stopping.IsCancellationRequested)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, _stopping.Token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                Status = result.CloseStatusDescription is { Length: > 0 } reason ? reason : "closed";
                return;
            }
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            // Decode only a complete message: a large catalogue can split a
            // parameter name's UTF-8 character between socket reads.
            Handle(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
            message.SetLength(0);
            if (message.Capacity > buffer.Length) message.Capacity = buffer.Length;
        }
    }

    /// <summary>Feeds one message in as if it had arrived, which is how the tests drive this.</summary>
    internal void Receive(string json) => Handle(json);

    private void Handle(string json)
    {
        string? type;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            type = document.RootElement.TryGetProperty("type", out JsonElement value) ? value.GetString() : null;
        }
        catch (JsonException) { return; }

        switch (type)
        {
            case "state":
                if (Snapshot.Parse(json) is { } snapshot)
                {
                    Connected = true;
                    State = snapshot;
                    UpdateChains(snapshot);
                    Status = snapshot.Connected ? "connected" : "no interface";
                    Changed?.Invoke();
                }
                break;

            case "meters":
                ReadMeters(json);
                Changed?.Invoke();
                break;

            case "error":
                LastError = Message(json);
                Changed?.Invoke();
                break;

            case "commandResult":
                if (Message(json, "requestId") is { } id && _requests.TryRemove(id, out var request))
                {
                    request.TrySetResult(Message(json, "error"));
                    Changed?.Invoke();
                    break;
                }
                if (Message(json, "error") is { Length: > 0 } failure)
                {
                    LastError = failure;
                    Changed?.Invoke();
                }
                break;

            case "plugins":
                lock (_pluginGate)
                {
                    try
                    {
                        Plugins = PluginCatalog.Read(json, _chains.Select(slot => (slot.Kind, slot.Plugin)).ToHashSet());
                        PluginsLoaded = true;
                    }
                    catch (JsonException) { LastError = "Could not read the plugin controls"; }
                }
                Changed?.Invoke();
                break;

            case "nativeEditorRulesChanged":
                RefreshPlugins();
                Changed?.Invoke();
                break;
        }
    }

    private static string? Message(string json, string property = "message")
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(property, out JsonElement value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }

    private void ReadMeters(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("levels", out JsonElement levels)) return;
            if (levels.ValueKind != JsonValueKind.Object) return;
            lock (_meterGate)
            {
                double now = _clock.Elapsed.TotalSeconds;
                HashSet<string> present = [];
                foreach (JsonProperty entry in levels.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.Array) continue;
                    double[] pair = entry.Value.EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.Number)
                        .Take(2).Select(item => item.GetDouble()).ToArray();
                    if (pair.Length == 0) continue;
                    present.Add(entry.Name);
                    if (!_meters.TryGetValue(entry.Name, out MeterTrace? trace))
                        _meters[entry.Name] = trace = new MeterTrace();
                    trace.Push(pair[0], pair.Length > 1 ? pair[1] : pair[0], now);
                }
                foreach (string missing in _meters.Keys.Where(key => !present.Contains(key)).ToArray())
                    _meters.Remove(missing);
            }
        }
        catch (JsonException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        try { if (_pump is not null) await _pump.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        catch (OperationCanceledException) { }
        _socket?.Dispose();
        _stopping.Dispose();
    }
}
