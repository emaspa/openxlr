using System.Collections.Concurrent;
using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Devices;

namespace OpenXLR.Daemon;

/// <summary>
/// One attached or remembered interface. <paramref name="CaptureHint"/> is the
/// PipeWire name fragment of its capture source, with its serial when it has
/// one; <paramref name="CaptureModelHint"/> is the model fragment to try when
/// that finds nothing, empty while another attached unit could answer to it.
/// A client uses both verbatim.
/// </summary>
public sealed record WaveInterfaceState(string Id, string Name, bool Active, bool Enabled,
    bool Connected, string CaptureHint, string CaptureModelHint, DeviceCapabilities Capabilities,
    DeviceState? State, string? Warning);

/// <summary>
/// Interfaces driven next to the primary one. Each enabled unit gets its own
/// device manager, with the primary's USB isolation, reconnect backoff, gain
/// lock, phantom settling and remembered settings, opened at its exact USB
/// address and remembering its settings under its instance id. The primary
/// always wins a unit: claiming one stops its additional manager first.
/// </summary>
public sealed class WaveInterfaces : BackgroundService
{
    private readonly DeviceManager _primary;
    private readonly ILogger<DeviceManager> _deviceLog;
    private readonly IConfiguration _configuration;
    private readonly Func<IReadOnlyList<IAudioDevice>> _detect;
    private readonly object _preferencesGate = new();
    private readonly ConcurrentDictionary<string, DeviceManager> _sessions = new(StringComparer.Ordinal);
    private IReadOnlyList<IAudioDevice> _available = [];
    private HashSet<string> _enabled = new(StringComparer.Ordinal);
    private string? _reserved;
    private string? _preferencesError;
    private string? _discoveryError;
    private volatile bool _stopping;
    private readonly object _sessionsGate = new();
    private const int MaximumAdditional = 4;
    private static string PathName => OpenXlrPaths.ConfigFile("wave-interfaces.json");
    public event Action? Changed;

    /// <summary>A preferences file that could not be read or written, or a failed USB scan; null otherwise.</summary>
    public string? Warning => Volatile.Read(ref _preferencesError) ?? Volatile.Read(ref _discoveryError);

    public WaveInterfaces(DeviceManager primary, ILogger<DeviceManager> deviceLog, IConfiguration configuration)
        : this(primary, deviceLog, configuration, DeviceRegistry.DetectAll) { }

    internal WaveInterfaces(DeviceManager primary, ILogger<DeviceManager> deviceLog, IConfiguration configuration, Func<IReadOnlyList<IAudioDevice>> detect)
    {
        _detect = detect;
        _primary = primary; _deviceLog = deviceLog; _configuration = configuration;
        _primary.ClaimingDevice += ReservePrimary;
        try
        {
            if (File.Exists(PathName))
            {
                string[] ids = JsonSerializer.Deserialize<string[]>(File.ReadAllText(PathName))
                    ?? throw new JsonException("not a list of ids");
                if (ids.Length > MaximumAdditional || ids.Any(id => !ValidId(id)) || ids.Distinct().Count() != ids.Length)
                    throw new JsonException("not a list of at most four distinct instance ids");
                _enabled = ids.ToHashSet(StringComparer.Ordinal);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // No additional unit is driven; the next change writes the file again.
            _preferencesError = $"Additional interface choices could not be read ({ex.Message}); none is enabled.";
        }
    }

    internal static bool ValidId(string? id) => UsbLocation.IsInstanceId(id);

    private void ReservePrimary(string id)
    {
        DeviceManager? session;
        lock (_sessionsGate) { Volatile.Write(ref _reserved, id); _sessions.TryRemove(id, out session); }
        if (session is not null)
        {
            session.SuspendSession();
            _ = StopSessionAsync(session);
        }
    }

    private async Task StopSessionAsync(DeviceManager session)
    {
        try { await session.StopAsync(CancellationToken.None); }
        catch (Exception ex) { _deviceLog.LogWarning(ex, "Additional interface shutdown failed"); }
        finally { session.Dispose(); }
    }

    public IReadOnlyList<WaveInterfaceState> Snapshot()
    {
        var available = Volatile.Read(ref _available);
        string? primary = _primary.ActiveInfo?.InstanceId;
        HashSet<string> enabled;
        lock (_preferencesGate) enabled = [.. _enabled];
        var states = available.Select(device =>
        {
            string id = device.Info.InstanceId;
            bool active = id == primary;
            StateMessage? state = active ? _primary.Snapshot() : _sessions.TryGetValue(id, out var session) ? session.Snapshot() : null;
            return new WaveInterfaceState(id, device.Info.DisplayName + (device.Info.Location is { } location ? $" ({location.Port})" : ""),
                active, enabled.Contains(id), state?.Connected ?? false, device.Info.NodeNameFragment,
                device.Info.FragmentsAmong(available.Select(d => d.Info)) is [_, string model] ? model : "",
                device.Capabilities, state?.State, active ? _primary.Warning : _sessions.GetValueOrDefault(id)?.Warning);
        }).ToList();
        // An enabled unit that is unplugged or moved stays listed, so it can
        // be disabled and its slot freed without plugging it back in.
        var attached = available.Select(device => device.Info.InstanceId).ToHashSet(StringComparer.Ordinal);
        states.AddRange(enabled.Where(id => !attached.Contains(id)).Order(StringComparer.Ordinal).Select(id =>
            new WaveInterfaceState(id, $"Interface not attached ({id})", false, true, false, "", "", new(), null,
                "This interface is not attached. Disable it to forget it.")));
        return states;
    }

    public string? SetEnabled(string id, bool enabled)
    {
        if (!ValidId(id)) return "setWaveInterfaceEnabled: not an instance id";
        if (_stopping) return "setWaveInterfaceEnabled: the daemon is stopping";
        lock (_preferencesGate)
        {
            var attached = Volatile.Read(ref _available);
            var unit = attached.FirstOrDefault(d => d.Info.InstanceId == id);
            if (enabled && unit is null) return "setWaveInterfaceEnabled: that interface is not attached";
            // A Pro's card is switched to pro-audio by name. Two Pros that
            // the name cannot tell apart would have one unit's card switched
            // for the other, so neither becomes additional.
            if (enabled && unit!.Capabilities.OutputRouting
                && attached.Count(d => string.Equals(d.Info.NodeNameFragment, unit.Info.NodeNameFragment, StringComparison.OrdinalIgnoreCase)) > 1)
                return "setWaveInterfaceEnabled: these interfaces have no serial that tells their audio cards apart; choose one as the primary interface instead";
            var next = new HashSet<string>(_enabled, StringComparer.Ordinal);
            if (enabled) next.Add(id); else next.Remove(id);
            if (next.Count > MaximumAdditional) return "setWaveInterfaceEnabled: at most four additional interfaces can be enabled";
            // The choice applies for this run either way; a failed write is
            // reported once, in the state's warning, until a later one succeeds.
            _enabled = next;
            try
            {
                OpenXlrPaths.WriteAtomic(PathName, JsonSerializer.Serialize(next.Order(StringComparer.Ordinal)));
                Volatile.Write(ref _preferencesError, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Volatile.Write(ref _preferencesError, $"Additional interface choices apply until the daemon restarts; they could not be saved ({ex.Message}).");
            }
        }
        if (!enabled && _sessions.TryRemove(id, out var session)) { session.SuspendSession(); _ = StopSessionAsync(session); }
        Changed?.Invoke();
        return null;
    }

    /// <summary>
    /// The name fragments that find this attached unit's own capture nodes
    /// and no other unit's, the same ones the window offers its sources by:
    /// the serial fragment, and the model fragment while no other attached
    /// unit answers to it. Empty for a unit that is not attached, the
    /// primary, or a unit only a model name shared with another unit names.
    /// </summary>
    internal IReadOnlyList<string> OwnSourceFragments(string id)
    {
        IReadOnlyList<DeviceInfo> attached = [.. Volatile.Read(ref _available).Select(d => d.Info)];
        if (attached.FirstOrDefault(d => d.InstanceId == id) is not { } unit || _primary.ActiveInfo?.InstanceId == id) return [];
        bool shared = attached.Any(other => other.InstanceId != id
            && other.ModelNameFragment.Contains(unit.ModelNameFragment, StringComparison.OrdinalIgnoreCase));
        if (!shared) return unit.NodeNameFragments;
        return unit.NodeNameFragment != unit.ModelNameFragment ? [unit.NodeNameFragment] : [];
    }

    /// <summary>The capture channels whose source one of these fragments names.</summary>
    internal static IReadOnlyList<string> CaptureChannelsFrom(IEnumerable<OpenXLR.Core.Mixing.ChannelStatus> channels, IReadOnlyList<string> fragments)
        => [.. channels.Where(c => c.CaptureSource is { } source && fragments.Any(f => source.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .Select(c => c.Id)];

    public string? Apply(string id, string control, JsonElement value)
    {
        if (!ValidId(id)) return "setWaveControl: not an instance id";
        // The primary reserves its unit before opening it. A late command for
        // the unit's additional role must not reopen it or reach another unit.
        if (id == Volatile.Read(ref _reserved)) return _primary.ActiveInfo?.InstanceId == id
            ? _primary.Apply(control, value) : "setWaveControl: the primary interface is reconnecting";
        if (!_sessions.TryGetValue(id, out var session)) return "setWaveControl: enable this attached interface first";
        return session.Apply(control, value);
    }

    public Dictionary<string, DeviceState> CaptureProfile()
    {
        var result = new Dictionary<string, DeviceState>(StringComparer.Ordinal);
        foreach (var pair in _sessions.ToArray())
            if (pair.Value.Snapshot().State is { } state) result[pair.Key] = DeviceStateStore.Hardware(state);
        return result;
    }

    public string? ApplyProfile(IReadOnlyDictionary<string, DeviceState> states)
    {
        foreach (var (id, state) in states)
        {
            // A profile never enables a device or redirects a state to a
            // different unit of the same model. Unavailable units stay alone.
            if (id == Volatile.Read(ref _reserved) || !_sessions.TryGetValue(id, out var session)) continue;
            if (session.CurrentConnection is not { } connection) continue;
            string? error = null;
            if (session.WithConnection(connection, () => { error = session.ApplyProfile(state, restoring: true); }))
            {
                if (error is not null) return $"{id}: {error}";
                session.MarkRestored();
            }
        }
        return null;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                IReadOnlyList<IAudioDevice> devices;
                try { devices = _detect(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _discoveryError = "USB discovery failed: " + ex.Message;
                    Changed?.Invoke();
                    await Task.Delay(TimeSpan.FromSeconds(1), stop);
                    continue;
                }
                bool changed = _discoveryError is not null || !devices.Select(d => d.Info).SequenceEqual(Volatile.Read(ref _available).Select(d => d.Info));
                _discoveryError = null;
                Volatile.Write(ref _available, devices);
                HashSet<string> enabled;
                lock (_preferencesGate) enabled = [.. _enabled];
                string? reserved = Volatile.Read(ref _reserved) ?? _primary.ActiveInfo?.InstanceId;
                foreach (var pair in _sessions.ToArray())
                    if (pair.Key == reserved || !enabled.Contains(pair.Key) || !devices.Any(d => d.Info.InstanceId == pair.Key))
                    {
                        if (_sessions.TryRemove(pair.Key, out var old)) { old.SuspendSession(); await StopSessionAsync(old); }
                    }
                foreach (var device in devices.Where(d => d.Info.InstanceId != reserved && enabled.Contains(d.Info.InstanceId)))
                {
                    string id = device.Info.InstanceId;
                    if (_sessions.ContainsKey(id)) continue;
                    var session = new DeviceManager(_deviceLog, _configuration,
                        () => [.. Volatile.Read(ref _available).Where(d => d.Info.InstanceId == id && id != Volatile.Read(ref _reserved))]);
                    session.SetSessionStorageId(id);
                    session.StateChanged += _ => Changed?.Invoke();
                    session.DeviceArrived += arrived =>
                    {
                        _ = Task.Run(() =>
                        {
                            try
                            {
                                if (session.CurrentConnection is { } connection)
                                    session.WithConnection(connection, () => { session.RestoreLastState(); session.MarkRestored(); });
                            }
                            catch (Exception ex) { _deviceLog.LogWarning(ex, "Restoring additional interface {id} failed", id); }
                        });
                    };
                    bool added;
                    Task? starting = null;
                    lock (_sessionsGate)
                    {
                        lock (_preferencesGate)
                        {
                            added = !_stopping && id != Volatile.Read(ref _reserved) && _enabled.Contains(id) && _sessions.TryAdd(id, session);
                            if (added) starting = session.StartAsync(stop);
                        }
                    }
                    if (added) { await starting!; changed = true; } else session.Dispose();
                }
                if (changed) Changed?.Invoke();
                await Task.Delay(TimeSpan.FromSeconds(1), stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            lock (_sessionsGate) _stopping = true;
            _primary.ClaimingDevice -= ReservePrimary;
            foreach (var pair in _sessions.ToArray())
                if (_sessions.TryRemove(pair.Key, out var session)) { session.SuspendSession(); await StopSessionAsync(session); }
        }
    }
}
