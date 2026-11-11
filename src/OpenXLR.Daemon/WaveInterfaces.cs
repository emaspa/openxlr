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
/// A client uses both verbatim. <paramref name="CardProfile"/> is the unit's
/// card as its manager last checked it, null while it is not driven.
/// </summary>
public sealed record WaveInterfaceState(string Id, string Name, bool Active, bool Enabled,
    bool Connected, string CaptureHint, string CaptureModelHint, DeviceCapabilities Capabilities,
    DeviceState? State, string? Warning, OpenXLR.Core.Mixing.CardProfileStatus? CardProfile = null);

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

    /// <summary>An additional unit's EQ status settled; see <see cref="DeviceManager.EqStatusChanged"/>.</summary>
    public event Action? EqStatusChanged;

    /// <summary>A preferences file that could not be read or written, or a failed USB scan; null otherwise.</summary>
    public string? Warning => Volatile.Read(ref _preferencesError) ?? Volatile.Read(ref _discoveryError);

    public WaveInterfaces(DeviceManager primary, ILogger<DeviceManager> deviceLog, IConfiguration configuration)
        : this(primary, deviceLog, configuration, DeviceRegistry.DetectAll) { }

    internal WaveInterfaces(DeviceManager primary, ILogger<DeviceManager> deviceLog, IConfiguration configuration, Func<IReadOnlyList<IAudioDevice>> detect)
    {
        _detect = detect;
        _primary = primary; _deviceLog = deviceLog; _configuration = configuration;
        _primary.ClaimingDevice += ReservePrimary;
        // Enabling a unit as additional never hands it the primary role: the
        // primary takes it only when setActiveDevice names it. The set is
        // replaced, never changed in place, so this reads it without a lock.
        _primary.SetAdditionalUnits(id => Volatile.Read(ref _enabled).Contains(id));
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

    // Enabled, attached units that show no state: since when, and whether it was said.
    private readonly Dictionary<string, (long Since, bool Said)> _stateGaps = new(StringComparer.Ordinal);

    /// <summary>How long an enabled, attached unit shows no state before the journal says so. Tests shorten it.</summary>
    internal TimeSpan StateGapWarning { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Once a second, from the scan: an enabled unit on the bus whose entry
    /// has shown no state for <see cref="StateGapWarning"/> is said once at
    /// warning, with what its manager shows, and once more when its state is
    /// back. A unit that is not attached is listed as such and not said here.
    /// </summary>
    private void NoteStateGaps(IReadOnlyList<IAudioDevice> devices, HashSet<string> enabled, string? primary)
    {
        long now = Environment.TickCount64;
        HashSet<string> watched = new(StringComparer.Ordinal);
        foreach (IAudioDevice device in devices)
        {
            string id = device.Info.InstanceId;
            if (id == primary || !enabled.Contains(id)) continue;
            watched.Add(id);
            DeviceManager? session = _sessions.GetValueOrDefault(id);
            StateMessage? message = session?.Snapshot();
            string? gap = session is null ? "no manager drives it"
                : message is not { Connected: true } ? "its manager has no connection to it"
                : message.State is null ? "it is connected, but no state has been read from it"
                : null;
            if (gap is null)
            {
                if (_stateGaps.Remove(id, out var ended) && ended.Said)
                    _deviceLog.LogInformation("{dev} ({id}) shows its state again after {s} s", device.Info.DisplayName, id, (now - ended.Since) / 1000);
                continue;
            }
            if (!_stateGaps.TryGetValue(id, out var open)) _stateGaps[id] = open = (now, false);
            if (open.Said || now - open.Since < (long)StateGapWarning.TotalMilliseconds) continue;
            _stateGaps[id] = open with { Said = true };
            _deviceLog.LogWarning("{dev} ({id}) is enabled as an additional interface and attached, but has shown no state for {s} s: {gap}. {warning}",
                device.Info.DisplayName, id, (now - open.Since) / 1000, gap, session?.Warning ?? "No warning is set.");
        }
        foreach (string id in _stateGaps.Keys.Where(id => !watched.Contains(id)).ToList()) _stateGaps.Remove(id);
    }

    public IReadOnlyList<WaveInterfaceState> Snapshot()
    {
        var available = Volatile.Read(ref _available);
        // Part of every state message: nothing here waits for a device lock.
        string? primary = _primary.PublishedInfo?.InstanceId;
        HashSet<string> enabled;
        lock (_preferencesGate) enabled = [.. _enabled];
        var states = available.Select(device =>
        {
            string id = device.Info.InstanceId;
            bool active = id == primary;
            StateMessage? state = active ? _primary.Snapshot() : _sessions.TryGetValue(id, out var session) ? session.Snapshot() : null;
            // A driven unit's capabilities are the ones its connect decided,
            // the hardware EQ among them; a unit not driven shows its model's.
            return new WaveInterfaceState(id, device.Info.DisplayName + (device.Info.Location is { } location ? $" ({location.Port})" : ""),
                active, enabled.Contains(id), state?.Connected ?? false, device.Info.NodeNameFragment,
                device.Info.FragmentsAmong(available.Select(d => d.Info)) is [_, string model] ? model : "",
                state is { Connected: true, Capabilities: { } driven } ? driven : device.Capabilities,
                state?.State, active ? _primary.Warning : _sessions.GetValueOrDefault(id)?.Warning, state?.CardProfile);
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
        if (attached.FirstOrDefault(d => d.InstanceId == id) is not { } unit || _primary.PublishedInfo?.InstanceId == id) return [];
        bool shared = attached.Any(other => other.InstanceId != id
            && other.ModelNameFragment.Contains(unit.ModelNameFragment, StringComparison.OrdinalIgnoreCase));
        if (!shared) return unit.NodeNameFragments;
        return unit.NodeNameFragment != unit.ModelNameFragment ? [unit.NodeNameFragment] : [];
    }

    /// <summary>
    /// Every attached unit, for the mixer: whether it is driven as an
    /// additional interface, the fragments that find its own nodes while it
    /// is, and what its inputs are. The primary is listed for its inputs only;
    /// the mixer knows it as the driven unit.
    /// </summary>
    internal IReadOnlyList<OpenXLR.Core.Mixing.AttachedUnit> AttachedUnits()
    {
        string? primary = _primary.PublishedInfo?.InstanceId;
        return [.. Volatile.Read(ref _available).Select(device =>
        {
            string id = device.Info.InstanceId;
            bool driven = id != primary && _sessions.TryGetValue(id, out DeviceManager? session) && session.PublishedInfo is not null;
            return new OpenXLR.Core.Mixing.AttachedUnit(id, driven ? OwnSourceFragments(id) : [],
                device.Capabilities.XlrInputs, device.Capabilities.XlrJacks, driven);
        })];
    }

    /// <summary>The manager driving an additional unit, or null; never the primary's.</summary>
    internal DeviceManager? Session(string id) => id == Volatile.Read(ref _reserved) ? null : _sessions.GetValueOrDefault(id);

    /// <summary>Each driven additional unit whose model has a hardware EQ, by instance id.</summary>
    internal IEnumerable<(string Id, OpenXLR.Core.Mixing.UnitEqSupport Support)> EqSupport()
    {
        string? primary = _primary.PublishedInfo?.InstanceId;
        foreach ((string id, DeviceManager session) in _sessions.ToArray())
            if (id != primary && session.PublishedEqSupport is { } support) yield return (id, support);
    }

    /// <summary>The capture channels removed on disable, by saved unit or, for older entries, source fragment.</summary>
    internal static IReadOnlyList<string> CaptureChannelsFrom(IEnumerable<OpenXLR.Core.Mixing.ChannelStatus> channels, IReadOnlyList<string> fragments,
        string? unit = null, bool keepChannels = false)
        => [.. channels.Where(c => !keepChannels && c.CaptureSource is { } source
            && (c.Binding.Unit is { } id ? id == unit : fragments.Any(f => source.Contains(f, StringComparison.OrdinalIgnoreCase))))
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
                {
                    bool scanned = devices.Any(d => d.Info.InstanceId == pair.Key);
                    if (pair.Key != reserved && enabled.Contains(pair.Key) && scanned) continue;
                    if (!_sessions.TryRemove(pair.Key, out var old)) continue;
                    // A unit disabled was the user's doing; the other two
                    // stop the unit's state without a failed read, so they
                    // are said.
                    if (pair.Key == reserved)
                        _deviceLog.LogInformation("{id} is the primary interface now; its additional interface stops", pair.Key);
                    else if (enabled.Contains(pair.Key))
                        _deviceLog.LogWarning("{id} is not on the USB scan any more; it is not driven as an additional interface until it is back", pair.Key);
                    old.SuspendSession();
                    await StopSessionAsync(old);
                }
                foreach (var device in devices.Where(d => d.Info.InstanceId != reserved && enabled.Contains(d.Info.InstanceId)))
                {
                    string id = device.Info.InstanceId;
                    if (_sessions.ContainsKey(id)) continue;
                    var session = new DeviceManager(_deviceLog, _configuration,
                        () => [.. Volatile.Read(ref _available).Where(d => d.Info.InstanceId == id && id != Volatile.Read(ref _reserved))]);
                    session.SetSessionStorageId(id);
                    session.StateChanged += _ => Changed?.Invoke();
                    session.EqStatusChanged += () => EqStatusChanged?.Invoke();
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
                NoteStateGaps(devices, enabled, reserved);
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
