using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace OpenXLR.Daemon;

/// <summary>
/// Fans device state out to every connected WebSocket client and routes their
/// commands into the device and mixer services.
/// </summary>
public sealed class WebSocketHub
{
    private const int MaxCommandBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly DeviceManager _devices;
    private readonly MixerService _mixer;
    private readonly ILogger<WebSocketHub> _log;
    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly CancellationToken _stopping;
    private readonly StateBroadcastQueue _stateBroadcasts;

    public WebSocketHub(DeviceManager devices, MixerService mixer, ILogger<WebSocketHub> log,
        IHostApplicationLifetime lifetime)
    {
        _devices = devices;
        _mixer = mixer;
        _log = log;
        _stopping = lifetime.ApplicationStopping;
        _stateBroadcasts = new(() =>
        {
            if (!_clients.IsEmpty) Broadcast(Snapshot());
        }, _log);
        _devices.StateChanged += ignored => _stateBroadcasts.Signal();
        _devices.DeviceArrived += devId => _ = RecallOnArrivalAsync(devId);
        _mixer.Changed += _stateBroadcasts.Signal;
        _ = _stateBroadcasts.RunAsync(_stopping);
        _mixer.MetersUpdated += () =>
        {
            if (_clients.IsEmpty) return;
            IReadOnlyDictionary<string, double[]>? levels = _mixer.Meters();
            if (levels is { Count: > 0 }) Broadcast(new MetersMessage(levels));
        };
    }

    private readonly ConcurrentDictionary<string, string> _activeProfile = new();

    internal StateMessage Snapshot() =>
        _devices.Snapshot() with
        {
            DaemonVersion = OpenXLR.Daemon.DaemonVersion.Current,
            Warning = string.Join(" ", new[] { _devices.Warning, _mixer.PersistenceWarning }.Where(w => w is not null)) is { Length: > 0 } w ? w : null,
            ActiveProfile = ActiveDeviceId() is string apId && _activeProfile.TryGetValue(apId, out string? ap) ? ap : null,
            Mixer = _mixer.Snapshot(),
            Devices = _mixer.Devices(),
            Profiles = ActiveDeviceId() is string devId ? OpenXLR.Core.ProfileStore.List(devId) : [],
            RecallOnConnect = ActiveDeviceId() is string rcId ? OpenXLR.Core.ProfileStore.RecallOnConnect(rcId) : null,
            Detected = [.. _devices.Detected().Select(d => new DetectedDevice(d.UsbId, d.Name, d.Active))],
        };

    private static readonly TimeSpan AuthDeadline = TimeSpan.FromSeconds(5);

    public async Task HandleAsync(WebSocket socket)
    {
        if (!await AuthenticateAsync(socket)) return;
        var client = new Client(socket, _stopping);
        try
        {
            await client.SendAsync(Serialize(Snapshot()));
            _clients[client.Id] = client;
            await ReceiveLoop(client);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException
                                   or IOException or InvalidOperationException)
        {
        }
        finally
        {
            _clients.TryRemove(client.Id, out _);
            client.Dispose();
        }
    }

    private async Task<bool> AuthenticateAsync(WebSocket socket)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
        deadline.CancelAfter(AuthDeadline);
        (SocketGuard.Outcome outcome, byte[]? message) = await SocketGuard.ReceiveMessageAsync(
            socket, new byte[4 * 1024], MaxCommandBytes, SocketGuard.MessageDeadline, deadline.Token);
        if (outcome != SocketGuard.Outcome.Message || message is null)
        {
            if (!_stopping.IsCancellationRequested)
                await SocketGuard.CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "authentication timeout");
            return false;
        }
        if (!ApiToken.Accepts(message))
        {
            _log.LogWarning("control API: a client presented no valid token and was refused");
            await SocketGuard.CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "unauthorized");
            return false;
        }
        return true;
    }

    private async Task ReceiveLoop(Client client)
    {
        var buf = new byte[8 * 1024];
        while (client.Socket.State == WebSocketState.Open)
        {
            (SocketGuard.Outcome outcome, byte[]? message) = await SocketGuard.ReceiveMessageAsync(
                client.Socket, buf, MaxCommandBytes, SocketGuard.MessageDeadline, _stopping);
            if (outcome != SocketGuard.Outcome.Message || message is null) return;

            if (!client.Budget.TryTake())
            {
                await SocketGuard.CloseAsync(client.Socket, WebSocketCloseStatus.PolicyViolation, "too many commands");
                return;
            }
            await Dispatch(client, Encoding.UTF8.GetString(message));
        }
    }

    private Task Dispatch(Client client, string text)
        => DispatchAsync(message => client.SendAsync(Serialize(message)), text);

    internal async Task<ApiCommandResult> ExecuteForApiAsync(string text)
    {
        var messages = new List<object>();
        await DispatchAsync(message => { messages.Add(message); return Task.CompletedTask; }, text);
        bool success = !messages.Any(message => message is ErrorMessage ||
            message is CommandResultMessage { Error: not null });
        return new("1", success, messages);
    }

    private async Task DispatchAsync(Func<object, Task> reply, string text)
    {
        Command? cmd;
        try { cmd = JsonSerializer.Deserialize<Command>(text, Json); }
        catch (JsonException ex) { await reply(new ErrorMessage($"bad json: {ex.Message}")); return; }
        if (cmd is null) { await reply(new ErrorMessage("command must be an object")); return; }

        switch (cmd.Cmd)
        {
            case "auth":
                break;
            case "getState":
                await reply(Snapshot());
                break;
            case "getDiagnostics":
                await reply(new DiagnosticsMessage(_devices.DumpBlocks()));
                break;
            case "listPlugins":
                IReadOnlyList<OpenXLR.Core.Mixing.PluginInfo> plugins = await Task.Run(() => OpenXLR.Core.Mixing.Lv2Catalog.Plugins);
                await reply(new PluginsMessage(plugins));
                break;
            case "set":
                if (cmd.Control is null) { await reply(new ErrorMessage("set: missing 'control'")); break; }
                string? err = _devices.Apply(cmd.Control, cmd.Value);
                if (err is not null) await reply(new ErrorMessage(err));
                break;
            case "createChannel":
            case "renameChannel":
            case "deleteChannel":
            case "createMix":
            case "renameMix":
            case "deleteMix":
            case "setLayoutOrder":
            case "setLevel":
            case "setChannelMuted":
            case "setMixVolume":
            case "setMixMuted":
            case "assignStream":
            case "assignApp":
            case "forgetApp":
            case "setMonitorOutput":
            case "setMonitorOutputs":
            case "setMonitorFeed":
            case "setOutputVolume":
            case "setEnforcedDefaults":
            case "setAuxPortEnabled":
            case "setLowCutHz":
            case "setSoftClipGuard":
            case "setInserts":
            case "setInsertBypass":
            case "setInsertParam":
                string? mixErr = _mixer.Apply(cmd);
                if (cmd.RequestId is not null)
                {
                    // Authoritative state is deliberately queued before the
                    // matching result. After the result, an editor can safely
                    // re-enable controls against the layout that actually won.
                    await reply(Snapshot());
                    await reply(new CommandResultMessage(cmd.RequestId, mixErr));
                }
                else if (mixErr is not null)
                {
                    await reply(new ErrorMessage(mixErr));
                    await reply(Snapshot());
                }
                break;
            case "setActiveDevice":
                if (cmd.Device is null) { await reply(new ErrorMessage("setActiveDevice: missing 'device'")); break; }
                string? devSelErr = _devices.SetActiveDevice(cmd.Device);
                if (devSelErr is not null) await reply(new ErrorMessage(devSelErr));
                break;
            case "saveProfile":
            case "loadProfile":
            case "deleteProfile":
                string? profErr = HandleProfile(cmd);
                if (profErr is not null) await reply(new ErrorMessage(profErr));
                else Broadcast(Snapshot());
                break;
            case "setRecallOnConnect":
                string? recallErr = HandleRecallOnConnect(cmd);
                if (recallErr is not null) await reply(new ErrorMessage(recallErr));
                else Broadcast(Snapshot());
                break;
            case "resetDevice":
                string? resetErr = _devices.ResetToDefaults();
                if (resetErr is not null) await reply(new ErrorMessage(resetErr));
                else _log.LogInformation("reset {dev} to its firmware defaults", ActiveDeviceId());
                break;
            default:
                await reply(new ErrorMessage($"unknown cmd '{cmd.Cmd}'"));
                break;
        }
    }

    private string? ApplyNamedProfile(string devId, string name)
    {
        OpenXLR.Core.Profile? p = OpenXLR.Core.ProfileStore.Load(devId, name);
        if (p is null) return $"no profile named '{name}'";
        string? devErr = p.Device is null ? null : _devices.ApplyProfile(p.Device);
        string? mixErr = p.Mixer is null || !_mixer.SubmixerEnabled ? null : _mixer.ApplyScene(p.Mixer);
        if (devErr is null && mixErr is null) _activeProfile[devId] = name;
        return devErr ?? mixErr;
    }

    private string? HandleRecallOnConnect(Command cmd)
    {
        if (ActiveDeviceId() is not string devId) return "setRecallOnConnect: no device connected";
        try
        {
            if (string.IsNullOrWhiteSpace(cmd.Name))
            {
                OpenXLR.Core.ProfileStore.SetRecallOnConnect(devId, null);
                return null;
            }
            string? name = OpenXLR.Core.ProfileStore.SanitizeName(cmd.Name);
            if (name is null) return "setRecallOnConnect: invalid 'name'";
            if (OpenXLR.Core.ProfileStore.Load(devId, name) is null) return $"no profile named '{name}'";
            OpenXLR.Core.ProfileStore.SetRecallOnConnect(devId, name);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private async Task RecallOnArrivalAsync(string devId)
    {
        string? name;
        try { name = OpenXLR.Core.ProfileStore.RecallOnConnect(devId); }
        catch (Exception ex) { _log.LogWarning("recall on connect: {msg}", ex.Message); name = null; }
        if (name is null)
        {
            string? status = _devices.RestoreLastState();
            if (status is not null)
            {
                _log.LogInformation("{status}", status);
                Broadcast(Snapshot());
            }
            return;
        }
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (_mixer.SubmixerEnabled && !_mixer.Built && DateTime.UtcNow < deadline && !_stopping.IsCancellationRequested)
            await Task.Delay(250, _stopping).ContinueWith(_ => { }, TaskScheduler.Default);
        if (_stopping.IsCancellationRequested || ActiveDeviceId() != devId) { _devices.MarkRestored(); return; }
        string? err = ApplyNamedProfile(devId, name);
        _devices.MarkRestored();
        if (err is null) _log.LogInformation("recalled profile '{name}' on connect of {dev}", name, devId);
        else _log.LogWarning("recall of profile '{name}' on connect of {dev}: {err}", name, devId, err);
        Broadcast(Snapshot());
    }

    private string? ActiveDeviceId() => _devices.Snapshot().Device?.UsbId;

    private string? HandleProfile(Command cmd)
    {
        string? name = OpenXLR.Core.ProfileStore.SanitizeName(cmd.Name);
        if (name is null) return $"{cmd.Cmd}: missing or invalid 'name'";
        if (ActiveDeviceId() is not string devId) return $"{cmd.Cmd}: no device connected";
        try
        {
            switch (cmd.Cmd)
            {
                case "saveProfile":
                    OpenXLR.Core.ProfileStore.Save(devId, name, new OpenXLR.Core.Profile
                    {
                        Device = _devices.Snapshot().State,
                        Mixer = _mixer.ExportScene(),
                    });
                    _activeProfile[devId] = name;
                    return null;
                case "loadProfile":
                    return ApplyNamedProfile(devId, name);
                case "deleteProfile":
                    if (_activeProfile.TryGetValue(devId, out string? current) && current == name)
                        _activeProfile.TryRemove(devId, out _);
                    return OpenXLR.Core.ProfileStore.Delete(devId, name) ? null : $"no profile named '{name}'";
                default:
                    return $"unknown profile command '{cmd.Cmd}'";
            }
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private void Broadcast(object message)
    {
        string text;
        try { text = Serialize(message); }
        catch (Exception ex)
        {
            _log.LogError("cannot serialize {type}: {msg}", message.GetType().Name, ex.Message);
            return;
        }
        foreach (Client c in _clients.Values)
        {
            if (!c.TrySend(text))
                _log.LogDebug("client {id} send queue full; dropping {type}",
                    c.Id, message.GetType().Name);
        }
    }

    private static string Serialize(object o) => JsonSerializer.Serialize(o, Json);

    private sealed class Client : IDisposable
    {
        private const int QueueCapacity = 32;
        private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

        public Guid Id { get; } = Guid.NewGuid();
        public WebSocket Socket { get; }
        public CommandBudget Budget { get; } = new();
        private readonly Channel<PendingSend> _outgoing;
        private readonly CancellationTokenSource _lifetime;
        private readonly Task _sendPump;

        public Client(WebSocket socket, CancellationToken stopping)
        {
            Socket = socket;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            _outgoing = Channel.CreateBounded<PendingSend>(new BoundedChannelOptions(QueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
            _sendPump = Task.Run(SendPumpAsync);
        }

        public bool TrySend(string text)
            => Socket.State == WebSocketState.Open &&
               _outgoing.Writer.TryWrite(new PendingSend(text, null));

        public Task SendAsync(string text)
        {
            if (Socket.State != WebSocketState.Open) return Task.CompletedTask;
            var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_outgoing.Writer.TryWrite(new PendingSend(text, sent))) return sent.Task;
            try { Socket.Abort(); } catch (ObjectDisposedException) { }
            return Task.CompletedTask;
        }

        private async Task SendPumpAsync()
        {
            Exception? failure = null;
            PendingSend? active = null;
            try
            {
                await foreach (PendingSend pending in _outgoing.Reader.ReadAllAsync(_lifetime.Token))
                {
                    active = pending;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    timeout.CancelAfter(SendTimeout);
                    await Socket.SendAsync(Encoding.UTF8.GetBytes(pending.Text),
                        WebSocketMessageType.Text, true, timeout.Token);
                    pending.Completion?.TrySetResult();
                    active = null;
                }
            }
            catch (Exception ex)
            {
                failure = ex;
                active?.Completion?.TrySetException(ex);
                try { Socket.Abort(); } catch (ObjectDisposedException) { }
            }
            finally
            {
                failure ??= new OperationCanceledException("client connection closed");
                _outgoing.Writer.TryComplete(failure);
                while (_outgoing.Reader.TryRead(out PendingSend? pending))
                    pending.Completion?.TrySetException(failure);
            }
        }

        public void Dispose()
        {
            _outgoing.Writer.TryComplete();
            _lifetime.Cancel();
            try { Socket.Dispose(); } catch (ObjectDisposedException) { }
            _ = _sendPump.ContinueWith(_ => _lifetime.Dispose(), TaskScheduler.Default);
        }

        private sealed record PendingSend(string Text, TaskCompletionSource? Completion);
    }
}
