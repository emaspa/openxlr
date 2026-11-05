using System.Text.Json;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// One pw-dump subscription. Objects arrive complete, keyed by registry id;
/// immutable clones let readers keep a snapshot while the next batch arrives.
/// Disconnects discard the entire registry before ids can be reused.
/// </summary>
internal sealed class PipeWireGraph : IDisposable
{
    // Leave room under the daemon's 256 MiB heap limit for JSON token tables,
    // changed-object clones and the previous snapshot while a reader uses it.
    internal const int ByteLimit = 8 * 1024 * 1024;
    private const int RegistryTextLimit = 16 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Dictionary<uint, (JsonElement Value, int Size)> _objects = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly Action<string>? _note;
    private readonly Task _worker;
    private readonly string _executable;
    private int _disposed;
    private JsonElement[]? _snapshot;
    private bool _ready;
    private long _size;
    private long _defaultRevision, _portRevision, _clockRevision;
    internal long DefaultRevision { get { lock (_gate) return _defaultRevision; } }

    internal PipeWireGraph(Action<string>? note = null, string executable = "pw-dump")
    {
        _note = note;
        _executable = executable;
        _worker = Task.Run(RunAsync);
    }

    /// <summary>True while a complete registry is held; false while reconnecting.</summary>
    internal bool IsReady { get { lock (_gate) return _ready; } }

    internal JsonElement[] Read()
    {
        lock (_gate)
        {
            if (!_ready) throw new IOException("PipeWire registry subscription is reconnecting");
            return _snapshot ??= _objects.Values.Select(o => o.Value).ToArray();
        }
    }

    // Keep the version and the snapshot in step even during reconnect.
    internal (long Revision, JsonElement[]? Snapshot)? ReadPortSnapshot(long revision)
    {
        lock (_gate)
        {
            if (!_ready) return null;
            return (_portRevision, revision == _portRevision ? null : Read());
        }
    }

    /// <summary>
    /// Moves when a node arrives, leaves, changes state or changes a property
    /// the mixer's clock is read from, and when a link arrives or leaves:
    /// what can hand a graph to another driver. Null while reconnecting.
    /// </summary>
    internal long? ClockRevision { get { lock (_gate) return _ready ? _clockRevision : null; } }

    private async Task RunAsync()
    {
        int delay = 250;
        bool reported = false;
        while (!_stopping.IsCancellationRequested)
        {
            long started = Environment.TickCount64;
            try
            {
                await ProcessRunner.RunStreamingAsync(_executable, ["--monitor", "--no-colors"],
                    async (stream, cancel) =>
                    {
                        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                        startup.CancelAfter(TimeSpan.FromSeconds(5));
                        await ReadBatchesAsync(stream, batch =>
                        {
                            if (!Apply(batch)) return;
                            startup.CancelAfter(Timeout.InfiniteTimeSpan);
                            if (reported) { _note?.Invoke("PipeWire registry subscription reconnected"); reported = false; }
                        }, startup.Token).ConfigureAwait(false);
                    }, _stopping.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException
                or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                if (!reported && !_stopping.IsCancellationRequested)
                {
                    _note?.Invoke($"PipeWire registry subscription unavailable: {ex.Message}; retrying");
                    reported = true;
                }
            }
            finally
            {
                lock (_gate)
                {
                    // A helper that emits an initial registry and immediately
                    // dies is still failing. Do not reset its retry backoff.
                    if (_ready && Environment.TickCount64 - started >= 5000) delay = 250;
                    _ready = false;
                    _defaultRevision++;
                    _portRevision++;
                    _clockRevision++;
                    _objects.Clear();
                    _snapshot = null;
                    _size = 0;
                    Monitor.PulseAll(_gate);
                }
            }
            try { await Task.Delay(delay, _stopping.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            delay = Math.Min(delay * 2, 5000);
        }
    }

    private bool Apply(JsonElement batch)
    {
        lock (_gate)
        {
            var updates = new Dictionary<uint, (JsonElement Value, int Size)>();
            long sizeAfter = _size;
            int countAfter = _objects.Count;
            bool portsChanged = false, clockChanged = false;
            foreach (JsonElement item in batch.EnumerateArray())
            {
                uint id = PipeWireSnapshot.RegistryId(item);
                if (IsDefaultMetadata(item) || (_objects.TryGetValue(id, out var previous) && IsDefaultMetadata(previous.Value)))
                    _defaultRevision++;
                (JsonElement Value, int Size) old = updates.TryGetValue(id, out var pending)
                    ? pending : _objects.GetValueOrDefault(id);
                sizeAfter -= old.Size;
                if (old.Value.ValueKind != JsonValueKind.Undefined) countAfter--;
                bool removed = PipeWireSnapshot.IsRemoval(item);
                // Pulse falls back to another device when the default one
                // leaves, without a metadata write; read the defaults again
                // whenever a device arrives, leaves or is renamed.
                if (DeviceName(old.Value) != DeviceName(removed ? default : item))
                    _defaultRevision++;
                if (!portsChanged) portsChanged = PortInputsChanged(old.Value, removed ? default : item);
                if (!clockChanged) clockChanged = ClockChanged(old.Value, removed ? default : item);
                if (removed) updates[id] = default;
                else
                {
                    // The text budget also bounds UTF-16 expansion when decoding
                    // properties. Object count bounds overhead from tiny objects.
                    int size = checked(item.GetRawText().Length * 2);
                    sizeAfter += size;
                    countAfter++;
                    if (sizeAfter > RegistryTextLimit || countAfter > 65536)
                        throw new JsonException("PipeWire registry exceeds its memory budget");
                    updates[id] = (item.Clone(), size);
                }
            }
            // Publish a whole valid batch. A malformed later object must not
            // leave a partially applied registry visible to another thread.
            foreach (var (id, update) in updates)
                if (update.Value.ValueKind == JsonValueKind.Undefined) _objects.Remove(id);
                else _objects[id] = update;
            _size = sizeAfter;
            _snapshot = null;
            if (portsChanged) _portRevision++;
            if (clockChanged) _clockRevision++;
            // Removals can arrive before pw-dump's initial core-sync batch.
            // Only that full batch contains the Core object on a new connection.
            if (!_ready)
                _ready = updates.Values.Any(update => update.Value.ValueKind == JsonValueKind.Object &&
                    update.Value.TryGetProperty("type", out JsonElement type) &&
                    type.ValueKind == JsonValueKind.String && type.GetString() == "PipeWire:Interface:Core");
            Monitor.PulseAll(_gate);
            return _ready;
        }
    }

    // A node's params and latency change many times a second; only what the
    // mixer's clock is read from (MixerClock.Read), a node's state and links
    // coming and going can move it. PipeWire does not announce a changed
    // node.driver-id to subscribers, so these are the signs to read it again.
    private static readonly string[] ClockKeys =
        ["node.name", "node.description", "node.driver-id", "priority.driver", "clock.name", "device.id", "device.api"];

    private static bool ClockChanged(JsonElement previous, JsonElement current)
    {
        if (IsType(previous, "PipeWire:Interface:Link") || IsType(current, "PipeWire:Interface:Link"))
            return IsType(previous, "PipeWire:Interface:Link") != IsType(current, "PipeWire:Interface:Link");
        JsonElement? before = NodeInfo(previous), after = NodeInfo(current);
        if (before is not { } old) return after is not null;
        if (after is not { } now) return true;
        if (!Same(old, now, "state")) return true;
        if (!old.TryGetProperty("props", out JsonElement a) || !now.TryGetProperty("props", out JsonElement b)
            || a.ValueKind != JsonValueKind.Object || b.ValueKind != JsonValueKind.Object) return true;
        foreach (string key in ClockKeys)
            if (!Same(a, b, key)) return true;
        return false;
    }

    private static bool Same(JsonElement a, JsonElement b, string key)
    {
        bool had = a.TryGetProperty(key, out JsonElement x), has = b.TryGetProperty(key, out JsonElement y);
        return had == has && (!had || JsonElement.DeepEquals(x, y));
    }

    private static JsonElement? NodeInfo(JsonElement item)
        => IsType(item, "PipeWire:Interface:Node")
            && item.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object ? info : null;

    private static bool PortInputsChanged(JsonElement previous, JsonElement current)
    {
        // Ports report latency and format changes many times a second; only
        // what a port list is built from (direction, owner, name, order)
        // makes it stale.
        if (IsType(previous, "PipeWire:Interface:Port") || IsType(current, "PipeWire:Interface:Port"))
            return PortIdentity(previous) != PortIdentity(current);
        JsonElement? before = PortNodeName(previous), after = PortNodeName(current);
        return before is { } oldName
            ? after is not { } newName || !JsonElement.DeepEquals(oldName, newName)
            : after is not null;
    }

    private static (string? Direction, string? Node, string? Name, string? Order)? PortIdentity(JsonElement item)
    {
        if (!IsType(item, "PipeWire:Interface:Port")
            || !item.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object) return null;
        string? Text(JsonElement parent, string key)
            => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(key, out var value)
                && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined ? value.ToString() : null;
        JsonElement props = info.TryGetProperty("props", out var p) ? p : default;
        return (Text(info, "direction"), Text(props, "node.id"), Text(props, "port.name"), Text(props, "port.id"));
    }

    private static bool IsType(JsonElement item, string expected)
        => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String && type.ValueEquals(expected);

    private static JsonElement? PortNodeName(JsonElement item)
        => IsType(item, "PipeWire:Interface:Node")
            && item.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
            && info.TryGetProperty("props", out var props) && props.ValueKind == JsonValueKind.Object
            && props.TryGetProperty("node.name", out var name) && name.ValueKind == JsonValueKind.String ? name : null;

    // The name of an audio device node (a sink, a source or a duplex unit,
    // not a stream), or null for anything else.
    private static string? DeviceName(JsonElement item)
        => PortNodeName(item) is { } name
            && item.GetProperty("info").GetProperty("props").TryGetProperty("media.class", out var kind)
            && kind.ValueKind == JsonValueKind.String && kind.GetString() is { } value
            && value.StartsWith("Audio/", StringComparison.Ordinal) ? name.GetString() : null;

    private static bool IsDefaultMetadata(JsonElement item)
        => IsType(item, "PipeWire:Interface:Metadata")
            && item.TryGetProperty("props", out var props) && props.ValueKind == JsonValueKind.Object
            && props.TryGetProperty("metadata.name", out var name)
            && name.ValueKind == JsonValueKind.String && name.ValueEquals("default");

    /// <summary>Wait for startup or a specific observed write, never for idle events.</summary>
    internal bool WaitFor(Func<JsonElement[], bool> predicate, TimeSpan timeout)
    {
        long end = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        lock (_gate)
        {
            while (!_stopping.IsCancellationRequested)
            {
                if (_ready && predicate(Read())) return true;
                long remaining = end - Environment.TickCount64;
                if (remaining <= 0) break;
                Monitor.Wait(_gate, (int)Math.Min(remaining, int.MaxValue));
            }
            return false;
        }
    }

    /// <summary>
    /// Frame concatenated JSON arrays in one pass, including strings split
    /// across reads. Parse only complete batches, with a fixed per-batch cap.
    /// </summary>
    internal static async Task ReadBatchesAsync(Stream stream, Action<JsonElement> apply,
        CancellationToken cancel, int byteLimit = ByteLimit)
    {
        byte[] buffer = new byte[16384];
        using var frame = new MemoryStream();
        int depth = 0;
        bool quoted = false, escaped = false;
        int count;
        while ((count = await stream.ReadAsync(buffer, cancel).ConfigureAwait(false)) != 0)
        {
            int start = 0;
            for (int i = 0; i < count; i++)
            {
                byte b = buffer[i];
                if (depth == 0)
                {
                    if (b is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t') { start = i + 1; continue; }
                    if (b != '[') throw new JsonException("Expected a PipeWire object array");
                }
                if (frame.Length + i - start + 1 > byteLimit) throw new JsonException("PipeWire batch exceeds its byte budget");
                if (quoted)
                {
                    if (escaped) escaped = false;
                    else if (b == '\\') escaped = true;
                    else if (b == '"') quoted = false;
                }
                else if (b == '"') quoted = true;
                else if (b is (byte)'[' or (byte)'{') depth++;
                else if (b is (byte)']' or (byte)'}')
                {
                    if (--depth == 0)
                    {
                        frame.Write(buffer, start, i - start + 1);
                        using JsonDocument doc = JsonDocument.Parse(frame.GetBuffer().AsMemory(0, (int)frame.Length));
                        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Expected array");
                        apply(doc.RootElement);
                        frame.SetLength(0);
                        if (frame.Capacity > 1024 * 1024) frame.Capacity = buffer.Length;
                        start = i + 1;
                    }
                }
                if (depth > 64) throw new JsonException("PipeWire batch exceeds JSON depth limit");
            }
            if (start < count) frame.Write(buffer, start, count - start);
        }
        if (depth != 0) throw new JsonException("Incomplete PipeWire batch");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopping.Cancel();
        lock (_gate) Monitor.PulseAll(_gate);
        _worker.GetAwaiter().GetResult();
        _stopping.Dispose();
    }
}
