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
            foreach (JsonElement item in batch.EnumerateArray())
            {
                uint id = PipeWireSnapshot.RegistryId(item);
                (JsonElement Value, int Size) old = updates.TryGetValue(id, out var pending)
                    ? pending : _objects.GetValueOrDefault(id);
                sizeAfter -= old.Size;
                if (old.Value.ValueKind != JsonValueKind.Undefined) countAfter--;
                if (PipeWireSnapshot.IsRemoval(item)) updates[id] = default;
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
