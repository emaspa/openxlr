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

    private async Task SendAsync(string json)
    {
        ClientWebSocket? socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open) return;
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, _stopping.Token)
                .ConfigureAwait(false);
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

            Connected = false;
            Changed?.Invoke();
            if (_stopping.IsCancellationRequested) return;
            try { await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), _stopping.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            backoffSeconds = Math.Min(backoffSeconds * 2, 10);
        }
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
        StringBuilder message = new();
        while (socket.State == WebSocketState.Open && !_stopping.IsCancellationRequested)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, _stopping.Token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                Status = result.CloseStatusDescription is { Length: > 0 } reason ? reason : "closed";
                return;
            }
            message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage) continue;
            Handle(message.ToString());
            message.Clear();
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
                    State = snapshot;
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
                if (Message(json, "error") is { Length: > 0 } failure)
                {
                    LastError = failure;
                    Changed?.Invoke();
                }
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
