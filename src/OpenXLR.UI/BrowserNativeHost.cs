using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace OpenXLR.UI;

internal sealed record BrowserNativeTab(int Id, string Site, string Title, bool Capturing, string? Error = null, string? Activation = null);

/// <summary>A bounded native-messaging bridge. Only browser routing crosses its standard output.</summary>
internal sealed class BrowserNativeHost(DaemonClient client, Stream input, Stream output, Func<long>? clock = null)
{
    internal const string ExtensionId = "beeohanoffjpcofbjiiamkdammhcihaf";
    internal const int MaxMessageBytes = 1024 * 1024;
    private readonly object _gate = new();
    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly Channel<bool> _updates = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private sealed record QueuedMessage(JsonObject Message, long Generation);
    private readonly Channel<QueuedMessage> _outgoing = Channel.CreateBounded<QueuedMessage>(new BoundedChannelOptions(8)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private JsonNode? _state;
    private JsonObject? _lastPublished;
    private string? _unavailableReason;
    private string? _session;
    private long _generation, _tabsVersion, _sentVersion = -1;
    private int _sessionFailures;
    private long _sessionRetryAt;
    private bool _sessionFailureReported;
    private IReadOnlyList<BrowserNativeTab> _tabs = [];

    internal static bool TryRun(string[] args)
    {
        bool command = args.FirstOrDefault() == "browser-host";
        string? origin = command ? args.ElementAtOrDefault(1) : args.FirstOrDefault();
        if (!command && origin?.StartsWith("chrome-extension://", StringComparison.Ordinal) != true) return false;
        if ((!command || origin is not null) && origin != "chrome-extension://" + ExtensionId + "/")
        {
            Console.Error.WriteLine("OpenXLR browser host: unrecognized extension.");
            Environment.ExitCode = 2;
            return true;
        }
        Environment.ExitCode = Serve(new BrowserNativeHost(new DaemonClient(), Console.OpenStandardInput(), Console.OpenStandardOutput()));
        return true;
    }

    /// <summary>
    /// Run until the browser closes the connection. A broken or invalid
    /// message ends the host with exit code 2 and a line on standard error,
    /// never with an unhandled exception: the browser then reports a closed
    /// host instead of a crash.
    /// </summary>
    internal static int Serve(BrowserNativeHost host, TextWriter? log = null)
    {
        try
        {
            host.RunAsync().GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or OperationCanceledException)
        {
            (log ?? Console.Error).WriteLine("OpenXLR browser host: the native messaging connection closed or sent invalid data.");
            return 2;
        }
    }

    internal async Task RunAsync(CancellationToken cancel = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        void State(JsonNode node)
        {
            lock (_gate) _state = node;
            _updates.Writer.TryWrite(true);
        }
        void Connection(bool up)
        {
            if (up) return; // The first authenticated state starts the session query.
            lock (_gate)
            {
                _generation++;
                _session = null;
                _state = null;
                _sentVersion = -1;
                ResetSessionRetryLocked();
            }
            _updates.Writer.TryWrite(true);
        }
        void Activate(int tabId, string site)
        {
            // Pass on a request only for a tab this browser listed under that
            // origin. The extension checks the tab again before acting on it.
            long generation;
            lock (_gate)
            {
                if (_session is null || !_tabs.Any(tab => tab.Id == tabId && tab.Site == site)) return;
                generation = _generation;
            }
            _outgoing.Writer.TryWrite(new(new JsonObject { ["type"] = "activate", ["tabId"] = tabId, ["site"] = site }, generation));
        }
        client.StateReceived += State;
        client.ConnectionChanged += Connection;
        client.BrowserTabActivationReceived += Activate;
        var incoming = Channel.CreateBounded<QueuedMessage>(new BoundedChannelOptions(8)
            { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
        Task writer = WriteOutputAsync(lifetime);
        Task pump = PumpStateAsync(lifetime.Token);
        Task commands = DispatchInputAsync(incoming.Reader, lifetime);
        try
        {
            client.Start();
            while (await ReadFrameAsync(input, lifetime.Token) is { } message)
            {
                long generation;
                lock (_gate) generation = _generation;
                await incoming.Writer.WriteAsync(new(message, generation), lifetime.Token);
            }
        }
        finally
        {
            client.StateReceived -= State;
            client.ConnectionChanged -= Connection;
            client.BrowserTabActivationReceived -= Activate;
            await lifetime.CancelAsync();
            _updates.Writer.TryComplete();
            _outgoing.Writer.TryComplete();
            incoming.Writer.TryComplete();
            await client.DisposeAsync();
            try { await Task.WhenAll(writer, pump, commands); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
    }

    private async Task DispatchInputAsync(ChannelReader<QueuedMessage> incoming, CancellationTokenSource lifetime)
    {
        try
        {
            await foreach (QueuedMessage message in incoming.ReadAllAsync(lifetime.Token))
                await ProcessInputAsync(message.Message, message.Generation, lifetime.Token);
        }
        catch
        {
            await lifetime.CancelAsync();
            throw;
        }
    }

    private async Task PumpStateAsync(CancellationToken cancel)
    {
        await foreach (bool update in _updates.Reader.ReadAllAsync(cancel))
        {
            JsonNode? state;
            string? session;
            long generation;
            lock (_gate) { state = _state; session = _session; generation = _generation; }
            if (state is null)
            {
                await UnavailableAsync("The OpenXLR daemon connection was lost. Browser audio uses its normal output.", generation, cancel);
                continue;
            }
            if (state["mixer"] is not JsonObject mixer)
            {
                await UnavailableAsync("OpenXLR is waiting for its audio mixer before browser routing can start.", generation, cancel);
                continue;
            }
            if (mixer["preserveBrowserOutputs"] is not JsonValue feature || !feature.TryGetValue<bool>(out bool supported)
                || mixer["browserTabs"] is not JsonArray)
            {
                await UnavailableAsync("Update OpenXLR to support browser tab routing.", generation, cancel);
                continue;
            }
            lock (_gate)
            {
                if (generation != _generation) continue;
                if (_unavailableReason is not null)
                {
                    _unavailableReason = null;
                    _lastPublished = null; // A supported state also clears the prior status in the extension.
                }
                session = _session;
                if (session is null && _clock() < _sessionRetryAt) continue;
            }
            if (session is null)
            {
                session = await client.RequestBrowserSessionAsync(TimeSpan.FromSeconds(5));
                lock (_gate)
                {
                    if (generation != _generation) continue;
                    _session = session;
                    if (session is not null)
                    {
                        if (_sessionFailureReported) _lastPublished = null;
                        ResetSessionRetryLocked();
                    }
                }
                if (session is null)
                {
                    bool notify;
                    int delay;
                    lock (_gate)
                    {
                        if (generation != _generation) continue;
                        _sessionFailures = Math.Min(_sessionFailures + 1, 6);
                        delay = SessionRetryDelayMilliseconds(_sessionFailures);
                        _sessionRetryAt = _clock() + delay;
                        notify = !_sessionFailureReported;
                        _sessionFailureReported = true;
                    }
                    await PublishAsync(new JsonObject { ["type"] = "state", ["preserveBrowserOutputs"] = false,
                        ["channels"] = new JsonArray(), ["tabs"] = new JsonArray() }, generation, cancel);
                    if (notify) await ErrorAsync("OpenXLR could not establish a browser routing session. Retrying automatically.", generation, cancel);
                    _ = WakeSessionRetryAsync(generation, delay, cancel);
                    continue;
                }
                await SyncTabsAsync(cancel);
            }
            lock (_gate)
            {
                if (generation != _generation || _session != session) continue;
                state = _state;
            }
            if (state is not null)
            {
                await PublishAsync(FilterState(state, session), generation, cancel);
            }
        }
    }

    internal static int SessionRetryDelayMilliseconds(int failures)
        => Math.Min(30_000, 1_000 << Math.Clamp(failures - 1, 0, 5));

    private void ResetSessionRetryLocked()
    {
        _sessionFailures = 0;
        _sessionRetryAt = 0;
        _sessionFailureReported = false;
    }

    private async Task WakeSessionRetryAsync(long generation, int milliseconds, CancellationToken cancel)
    {
        try { await Task.Delay(milliseconds, cancel); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return; }
        lock (_gate)
            if (generation != _generation || _session is not null) return;
        _updates.Writer.TryWrite(true);
    }

    private async Task UnavailableAsync(string reason, long generation, CancellationToken cancel)
    {
        bool notify;
        lock (_gate)
        {
            if (generation != _generation) return;
            _session = null;
            _sentVersion = -1;
            ResetSessionRetryLocked();
            notify = _unavailableReason != reason;
            _unavailableReason = reason;
        }
        await PublishAsync(new JsonObject { ["type"] = "state", ["preserveBrowserOutputs"] = false,
            ["channels"] = new JsonArray(), ["tabs"] = new JsonArray() }, generation, cancel);
        if (notify) await ErrorAsync(reason, generation, cancel);
    }

    private async Task<bool> PublishAsync(JsonObject message, long generation, CancellationToken cancel)
    {
        lock (_gate)
            if (generation != _generation || JsonNode.DeepEquals(_lastPublished, message)) return false;
        await _outgoing.Writer.WriteAsync(new(message, generation), cancel);
        lock (_gate)
            if (generation == _generation) _lastPublished = message;
        return true;
    }

    private async Task ProcessInputAsync(JsonObject message, long generation, CancellationToken cancel)
    {
        string? type = String(message, "type");
        switch (type)
        {
            case "tabs":
                IReadOnlyList<BrowserNativeTab> tabs = ReadTabs(message["tabs"]);
                lock (_gate) { _tabs = tabs; _tabsVersion++; }
                await SyncTabsAsync(cancel);
                break;
            case "enable" when message["value"] is JsonValue flag && flag.TryGetValue<bool>(out bool enabled):
                await CommandAsync(() => client.SetPreserveBrowserOutputsAsync(enabled), generation, cancel);
                break;
            case "assign":
                if (message["tabId"] is not JsonValue idNode || !idNode.TryGetValue<int>(out int id) || id < 0
                    || String(message, "site") is not { } site || !ValidSite(site)
                    || message["remember"] is not JsonValue rememberNode || !rememberNode.TryGetValue<bool>(out bool remember)
                    || !message.ContainsKey("channel") || message["channel"] is not null && String(message, "channel") is null)
                    throw new InvalidDataException("Invalid browser assignment.");
                string? channel = String(message, "channel");
                if (channel is not null && !ValidChannel(channel)) throw new InvalidDataException("Invalid browser channel.");
                string? session;
                lock (_gate) session = _session;
                if (session is null) await ErrorAsync("The OpenXLR browser connection is not ready.", generation, cancel);
                else await CommandAsync(() => client.AssignBrowserTabAsync(session, id, site, channel, remember), generation, cancel);
                break;
            default:
                throw new InvalidDataException("Invalid browser-host message.");
        }
    }

    private async Task SyncTabsAsync(CancellationToken cancel)
    {
        await _commands.WaitAsync(cancel);
        try
        {
            string? session;
            IReadOnlyList<BrowserNativeTab> tabs;
            long version, generation;
            lock (_gate)
            {
                session = _session; tabs = _tabs; version = _tabsVersion; generation = _generation;
                if (session is null || _sentVersion == version) return;
            }
            string? error = await client.SyncBrowserTabsAsync(session, tabs);
            lock (_gate)
            {
                if (generation != _generation || _session != session) return;
                if (error is null) _sentVersion = version;
            }
            if (error is not null) await ErrorAsync(error == DaemonClient.BrowserTabsTooLarge
                ? DaemonClient.BrowserTabsTooLarge : "OpenXLR could not update the browser tabs.", generation, cancel);
        }
        finally { _commands.Release(); }
    }

    private async Task CommandAsync(Func<Task<string?>> invoke, long generation, CancellationToken cancel)
    {
        await _commands.WaitAsync(cancel);
        try
        {
            // A valid control accepted on the old connection must not be
            // replayed. Metadata takes the separate resynchronization path.
            Task<string?> pending;
            lock (_gate)
            {
                if (generation != _generation) return;
                pending = invoke();
            }
            if (await pending is { } error)
                await ErrorAsync(error == "assignBrowserTab: the tab changed website; select it again"
                    ? "The tab changed website. Select it again in the browser extension."
                    : "OpenXLR could not apply the browser routing change. Check the OpenXLR window.", generation, cancel);
        }
        finally { _commands.Release(); }
    }

    private ValueTask ErrorAsync(string error, long generation, CancellationToken cancel)
        => _outgoing.Writer.WriteAsync(new(new JsonObject { ["type"] = "error", ["error"] = error }, generation), cancel);

    private async Task WriteOutputAsync(CancellationTokenSource lifetime)
    {
        try
        {
            await foreach (QueuedMessage message in _outgoing.Reader.ReadAllAsync(lifetime.Token))
            {
                lock (_gate)
                    if (message.Generation != _generation) continue;
                await WriteFrameAsync(output, message.Message, lifetime.Token);
            }
        }
        catch (IOException) { await lifetime.CancelAsync(); }
    }

    internal static async Task<JsonObject?> ReadFrameAsync(Stream stream, CancellationToken cancel = default)
    {
        byte[] header = new byte[4];
        int first = await stream.ReadAsync(header.AsMemory(0, 1), cancel);
        if (first == 0) return null;
        try { await stream.ReadExactlyAsync(header.AsMemory(1), cancel); }
        catch (EndOfStreamException) { throw new InvalidDataException("Truncated native message header."); }
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (size is 0 or > MaxMessageBytes) throw new InvalidDataException("Native message is outside the size limit.");
        byte[] data = new byte[(int)size];
        try { await stream.ReadExactlyAsync(data, cancel); }
        catch (EndOfStreamException) { throw new InvalidDataException("Truncated native message body."); }
        return JsonNode.Parse(data, documentOptions: new JsonDocumentOptions { MaxDepth = 12, AllowDuplicateProperties = false }) as JsonObject
            ?? throw new InvalidDataException("Native message must be an object.");
    }

    internal static async Task WriteFrameAsync(Stream stream, JsonObject message, CancellationToken cancel = default)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(message);
        if (data.Length > MaxMessageBytes) throw new InvalidDataException("Native response is outside the size limit.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)data.Length);
        await stream.WriteAsync(header, cancel);
        await stream.WriteAsync(data, cancel);
        await stream.FlushAsync(cancel);
    }

    internal static IReadOnlyList<BrowserNativeTab> ReadTabs(JsonNode? node)
    {
        if (node is not JsonArray array || array.Count > 128) throw new InvalidDataException("Invalid browser tab list.");
        var ids = new HashSet<int>();
        var tabs = new List<BrowserNativeTab>(array.Count);
        foreach (JsonNode? item in array)
        {
            if (item is not JsonObject tab || tab["id"] is not JsonValue idNode || !idNode.TryGetValue<int>(out int id)
                || id < 0 || !ids.Add(id) || String(tab, "site") is not { } site
                || String(tab, "title") is not { Length: <= 160 } title || title.Any(char.IsControl)
                || tab["capturing"] is not JsonValue capture || !capture.TryGetValue<bool>(out bool capturing)
                || tab["error"] is not null && (String(tab, "error") is not { Length: <= 200 } error || error.Any(char.IsControl))
                || tab["activation"] is not null && String(tab, "activation") is not ("starting" or "popup" or "browser"))
                throw new InvalidDataException("Invalid browser tab metadata.");
            // An address the daemon cannot take in the browser's own form, such
            // as a host holding "*" or an IPv4-mapped IPv6 address, leaves that
            // tab unlisted. The other tabs still register.
            if (ValidSite(site)) tabs.Add(new(id, site, title, capturing, String(tab, "error"), String(tab, "activation")));
        }
        return tabs;
    }

    /// <summary>A bare origin exactly as the daemon will report it back.</summary>
    private static bool ValidSite(string site) => string.Equals(BrowserOrigin.Canonical(site), site, StringComparison.Ordinal);

    private static bool ValidChannel(string channel)
        => channel is { Length: > 0 and <= 36 } && channel[0] is >= 'a' and <= 'z'
           && channel.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');

    private static string? String(JsonObject node, string key)
        => node[key] is JsonValue value && value.TryGetValue<string>(out string? text) ? text : null;

    internal static JsonObject FilterState(JsonNode state, string session)
    {
        var channels = new JsonArray();
        if (state["mixer"]?["channels"] is JsonArray channelList)
            foreach (JsonNode? item in channelList.Take(128))
                if (item is JsonObject channel && !Bool(channel, "hardware")
                    && channel["captureSource"] is null && String(channel, "id") is { } id && ValidChannel(id)
                    && String(channel, "name") is { Length: > 0 and <= 80 } name && !name.Any(char.IsControl))
                    channels.Add(new JsonObject { ["id"] = id, ["name"] = name, ["label"] = "OpenXLR " + name });
        var tabs = new JsonArray();
        if (state["mixer"]?["browserTabs"] is JsonArray browserTabs)
            foreach (JsonNode? item in browserTabs)
                if (tabs.Count < 128 && item is JsonObject tab && String(tab, "session") == session
                    && tab["id"] is JsonValue idNode && idNode.TryGetValue<int>(out int id) && id >= 0
                    && String(tab, "site") is { } site && ValidSite(site)
                    && String(tab, "title") is { Length: <= 160 } title && !title.Any(char.IsControl)
                    && (tab["channelId"] is null || String(tab, "channelId") is { } destination && ValidChannel(destination)))
                {
                    var copy = new JsonObject { ["id"] = id, ["site"] = site, ["title"] = title,
                        ["channelId"] = String(tab, "channelId"), ["remember"] = Bool(tab, "remember"), ["capturing"] = Bool(tab, "capturing") };
                    if (String(tab, "error") is { Length: <= 200 } error && !error.Any(char.IsControl)) copy["error"] = error;
                    tabs.Add(copy);
                }
        return new JsonObject { ["type"] = "state",
            ["preserveBrowserOutputs"] = state["mixer"] is JsonObject mixer && Bool(mixer, "preserveBrowserOutputs"),
            ["channels"] = channels, ["tabs"] = tabs };
    }

    private static bool Bool(JsonObject node, string key)
        => node[key] is JsonValue value && value.TryGetValue<bool>(out bool flag) && flag;
}
