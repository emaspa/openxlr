using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenXLR.Tui;

/// <summary>
/// The connection to the daemon: the documented WebSocket over the private
/// Unix socket, and a reconnect loop so the terminal survives a daemon
/// restart the way the window does.
///
/// Everything arrives on the receive task and is handed to the drawing loop as
/// one immutable snapshot, so the two never share a mutable object.
/// </summary>
internal sealed class DaemonLink : IAsyncDisposable
{
    private const int MaxMessageBytes = 8 * 1024 * 1024;
    private readonly Uri? _uri;

    private readonly Func<CancellationToken, Task<DaemonPresence>>? _probe;

    public DaemonLink(string? url = null) : this(url, null) { }

    /// <summary>A link to an explicit endpoint probes no daemon unless it is given a probe.</summary>
    internal DaemonLink(string? url, Func<CancellationToken, Task<DaemonPresence>>? probe)
    {
        _uri = url is null ? null : new Uri(url);
        _probe = probe ?? (url is null ? token => DaemonProbe.ProbeAsync(cancel: token) : null);
    }

    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _meterGate = new();
    private readonly Dictionary<string, MeterTrace> _meters = [];
    private readonly Dictionary<string, (double Decibels, double At)> _reductions = [];
    private readonly Dictionary<string, (double Position, int Voices, double At)> _clips = [];
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private ClientWebSocket? _socket;
    private Task? _pump;
    private long _sessionRevision;
    private long _nextChannelRevision;
    private readonly ConcurrentDictionary<string, (long Revision, int Bands)> _channelRevisions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sendGate = new(1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> _requests = new();
    private readonly Lock _pluginGate = new();
    private bool _pluginsWanted;
    private bool _pluginsRequested;
    private (string Channel, string Id, string Kind, string Plugin)[] _chains = [];

    public IReadOnlyDictionary<(string Kind, string Plugin), PluginEntry> Plugins { get; private set; } =
        new Dictionary<(string, string), PluginEntry>();

    public bool PluginsLoaded { get; private set; }

    /// <summary>Every installed plugin, for the picker; empty until the catalogue arrives.</summary>
    public IReadOnlyList<PluginChoice> Choices { get; private set; } = [];

    /// <summary>The latest state, or null before the first one arrives.</summary>
    public Snapshot? State { get; private set; }

    /// <summary>What the connection is doing, for the status line.</summary>
    public string Status { get; private set; } = "connecting";

    /// <summary>
    /// A sentence for the header while no daemon can be reached and the reason
    /// needs the user: a daemon from before the update, or another user's
    /// socket. Null otherwise.
    /// </summary>
    public string? Notice { get; private set; }

    /// <summary>True between a successful authentication and the socket closing.</summary>
    public bool Connected { get; private set; }

    /// <summary>Changes when a connection ends, even if it reconnects before the next frame.</summary>
    internal long SessionRevision => Interlocked.Read(ref _sessionRevision);

    /// <summary>A local lifetime for a channel id and its numbered EQ table.</summary>
    internal long ChannelRevision(string channel) => _channelRevisions.GetValueOrDefault(channel).Revision;

    /// <summary>The answer to the last clip import, or null before the first.</summary>
    public ClipImportOutcome? LastImport { get; private set; }

    /// <summary>True from an import's request until its answer, a timeout or a closed connection.</summary>
    public bool Importing => Volatile.Read(ref _importing) != 0;

    private int _importing;
    private long _importSerial;
    private (string? Clip, string? Message) _importReply;

    /// <summary>
    /// Import a file into the soundboard, onto <paramref name="page"/>, the
    /// default page when empty. The outcome lands in
    /// <see cref="LastImport"/> whichever tab is shown, with the clip id from
    /// the daemon's <c>clipImport</c> answer. Without a connection it fails
    /// at once rather than waiting for the timeout.
    /// </summary>
    public void ImportClip(string path, string page = "")
    {
        if (!Connected)
        {
            Finish(false, "Not connected to the daemon, so the file was not imported", null);
            return;
        }
        if (Interlocked.Exchange(ref _importing, 1) != 0) return;
        _importReply = (null, null);
        // Copying and checking a long file can take longer than a fader write.
        Request("importClip", body =>
        {
            body["path"] = path;
            if (page.Length > 0) body["page"] = page;
        }, TimeSpan.FromMinutes(2)).ContinueWith(task =>
        {
            string? error = task.IsCompletedSuccessfully ? task.Result : "The import did not finish";
            (string? clip, string? message) = _importReply;
            Volatile.Write(ref _importing, 0);
            Finish(error is null, error ?? message ?? "The clip was added to the soundboard.", error is null ? clip : null);
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Ask the daemon to add back the starter sounds the library no longer
    /// has. The daemon's sentence lands in <see cref="LastImport"/> as an
    /// import's does, also when nothing was missing.
    /// </summary>
    public void RestoreStarterSounds()
    {
        if (!Connected)
        {
            Finish(false, "Not connected to the daemon, so the starter sounds were not restored", null);
            return;
        }
        if (Interlocked.Exchange(ref _importing, 1) != 0) return;
        _importReply = (null, null);
        // Each clip is copied and checked by the helper, as an import is.
        Request("restoreStarterSounds", _ => { }, TimeSpan.FromMinutes(2)).ContinueWith(task =>
        {
            string? error = task.IsCompletedSuccessfully ? task.Result : "The restore did not finish";
            string? message = _importReply.Message;
            Volatile.Write(ref _importing, 0);
            Finish(error is null, error ?? message ?? "The starter sounds were restored.", null);
        }, TaskScheduler.Default);
    }

    private void Finish(bool ok, string message, string? clip)
    {
        LastImport = new ClipImportOutcome(Interlocked.Increment(ref _importSerial), ok, message, clip);
        Changed?.Invoke();
    }

    /// <summary>
    /// The last dry run of hardware mixing the daemon answered, or null
    /// before the first. The Options tab lists it under the switch until the
    /// mode is chosen or the check is asked again.
    /// </summary>
    public HardwareMixingEntry? MixingPlan { get; private set; }

    /// <summary>Whether a dry run is on its way; the Options tab says so instead of an old one.</summary>
    public bool CheckingMixing { get; private set; }

    /// <summary>Ask the daemon what hardware mixing would do with the layout as it stands. Nothing changes.</summary>
    public void CheckMixing()
    {
        if (!Connected || CheckingMixing) return;
        CheckingMixing = true;
        MixingPlan = null;
        Request("setMixing", body =>
        {
            body["value"] = "hardware";
            body["check"] = true;
        }).ContinueWith(task =>
        {
            CheckingMixing = false;
            if (!task.IsCompletedSuccessfully || task.Result is not null) LastError = task.IsCompletedSuccessfully ? task.Result : "The check did not finish";
            Changed?.Invoke();
        }, TaskScheduler.Default);
    }

    /// <summary>Forget the dry run, once the switch it was for has been sent.</summary>
    public void ForgetMixingPlan() => MixingPlan = null;

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

    /// <summary>
    /// The largest ducking reduction now, in dB, across the mixes a software
    /// stage runs in; 0 while none reports, and once a reading is a second old.
    /// </summary>
    public double DuckReduction()
    {
        lock (_meterGate)
        {
            double now = _clock.Elapsed.TotalSeconds;
            return _reductions.Values.Where(reading => now - reading.At < 1)
                .Select(reading => reading.Decibels).DefaultIfEmpty(0).Max();
        }
    }

    /// <summary>
    /// How far a soundboard clip's newest voice is, 0 to 1, and how many
    /// voices play it, from its <c>clip:&lt;id&gt;</c> meter; null while the
    /// clip is silent and once a reading is a second old.
    /// </summary>
    public (double Position, int Voices)? ClipProgress(string id)
    {
        lock (_meterGate)
        {
            return _clips.TryGetValue($"clip:{id}", out var reading) && _clock.Elapsed.TotalSeconds - reading.At < 1
                ? (reading.Position, reading.Voices) : null;
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

    /// <summary>Match a command outcome to its request, never to an unrelated error.</summary>
    /// <param name="timeout">How long the daemon has to answer; 20 seconds unless the command takes longer.</param>
    public Task<string?> Request(string command, Action<JsonObject> fill, TimeSpan? timeout = null)
    {
        string id = Guid.NewGuid().ToString("N");
        TaskCompletionSource<string?> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests[id] = result;
        _ = ExpireRequest(id, result, timeout ?? TimeSpan.FromSeconds(20));
        Send(command, body => { fill(body); body["requestId"] = id; });
        return result.Task;
    }

    private async Task ExpireRequest(string id, TaskCompletionSource<string?> result, TimeSpan timeout)
    {
        try { await result.Task.WaitAsync(timeout, _stopping.Token).ConfigureAwait(false); }
        catch (TimeoutException) { result.TrySetResult("The daemon did not answer the request"); }
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
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { return; }
            catch (Exception error) when (error is WebSocketException or IOException or HttpRequestException or OperationCanceledException)
            {
                Status = "daemon not reachable";
                // A failed connect, not a dropped session: say why if a reason is known.
                if (!Connected && _probe is not null)
                {
                    DaemonPresence presence;
                    try { presence = await _probe(_stopping.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception probeError) when (probeError is IOException or System.Net.Sockets.SocketException) { presence = DaemonPresence.Reachable; }
                    (Status, Notice) = presence switch
                    {
                        DaemonPresence.OlderDaemon => ("restart the daemon", DaemonProbe.Describe(presence)),
                        DaemonPresence.OtherUser => ("socket refused", DaemonProbe.Describe(presence)),
                        DaemonPresence.NotRunning => ("daemon not running", null),
                        _ => ("daemon not reachable", null),
                    };
                }
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
        Interlocked.Increment(ref _sessionRevision);
        _channelRevisions.Clear();
        Connected = false;
        foreach (var request in _requests.Values) request.TrySetResult("Disconnected from the daemon");
        lock (_pluginGate)
        {
            _pluginsRequested = false;
            PluginsLoaded = false;
            Plugins = new Dictionary<(string, string), PluginEntry>();
            Choices = [];
        }
        Changed?.Invoke();
    }

    private async Task SessionAsync()
    {
        using var link = new ControlConnection(_uri);
        ClientWebSocket socket = link.Socket;
        _socket = socket;
        Status = "connecting";
        Changed?.Invoke();
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(5));
            await link.ConnectAsync(connect.Token).ConfigureAwait(false);
        }

        Connected = true;
        Status = "connected";
        Notice = null;
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
            if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > MaxMessageBytes)
                throw new WebSocketException("Invalid or oversized daemon message.");
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
            JsonElement root = document.RootElement;
            type = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out JsonElement value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException) { return; }

        switch (type)
        {
            case "state":
                if (Snapshot.Parse(json) is { } snapshot)
                {
                    // A mix's EQ is kept beside the channels' under "mix:" and its id.
                    var eqs = snapshot.Mixer.Channels.Select(c => (Key: c.Id, c.Eq))
                        .Concat(snapshot.Mixer.Mixes.Select(m => (Key: "mix:" + m.Id, m.Eq))).ToList();
                    var channels = eqs.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
                    foreach (string removed in _channelRevisions.Keys)
                        if (!channels.Contains(removed)) _channelRevisions.TryRemove(removed, out _);
                    foreach ((string key, EqEntry? eq) in eqs)
                    {
                        int bands = eq?.Bands?.Count ?? 0;
                        _channelRevisions.AddOrUpdate(key,
                            _ => (Interlocked.Increment(ref _nextChannelRevision), bands),
                            (_, prior) => prior.Bands == bands ? prior : (Interlocked.Increment(ref _nextChannelRevision), bands));
                    }
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
                        PluginCatalogue catalogue = PluginCatalog.Read(json,
                            _chains.Select(slot => (slot.Kind, slot.Plugin)).ToHashSet());
                        Plugins = catalogue.Entries;
                        Choices = catalogue.Choices;
                        PluginsLoaded = true;
                    }
                    catch (JsonException) { LastError = "Could not read the plugin controls"; }
                }
                Changed?.Invoke();
                break;

            case "clipImport":
                // The commandResult that follows ends the request; this names the clip.
                _importReply = (Message(json, "clip"), Message(json));
                break;

            case "starterSounds":
                // As for clipImport: the sentence, before the commandResult.
                _importReply = (null, Message(json));
                break;

            case "mixingPlan":
                // The dry run's fields sit at the top of the message, as in the state.
                try { MixingPlan = JsonSerializer.Deserialize<HardwareMixingEntry>(json, Snapshot.Options); }
                catch (JsonException) { LastError = "Could not read the hardware mixing check"; }
                // The answer is here; the commandResult that follows only closes the request.
                CheckingMixing = false;
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
                    // A ducking stage reports its reduction in dB, not a level on the meter scale.
                    if (entry.Name.StartsWith("duck:", StringComparison.Ordinal))
                    {
                        _reductions[entry.Name] = (Math.Clamp(pair[0], 0, 120), now);
                        continue;
                    }
                    // A soundboard clip reports where it is and how many voices play it, not a level.
                    if (entry.Name.StartsWith("clip:", StringComparison.Ordinal))
                    {
                        int voices = pair.Length > 1 ? (int)Math.Clamp(pair[1], 0, 64) : 1;
                        _clips[entry.Name] = (Math.Clamp(pair[0], 0, 1), voices, now);
                        continue;
                    }
                    if (!_meters.TryGetValue(entry.Name, out MeterTrace? trace))
                        _meters[entry.Name] = trace = new MeterTrace();
                    trace.Push(pair[0], pair.Length > 1 ? pair[1] : pair[0], now);
                }
                foreach (string missing in _meters.Keys.Where(key => !present.Contains(key)).ToArray())
                    _meters.Remove(missing);
                foreach (string missing in _reductions.Keys.Where(key => !present.Contains(key)).ToArray())
                    _reductions.Remove(missing);
                foreach (string missing in _clips.Keys.Where(key => !present.Contains(key)).ToArray())
                    _clips.Remove(missing);
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

/// <summary>How a clip import ended: <see cref="Serial"/> counts imports, so a client sees each answer once.</summary>
internal sealed record ClipImportOutcome(long Serial, bool Ok, string Message, string? Clip);
