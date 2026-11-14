using OpenXLR.Core.Mixing;

namespace OpenXLR.Daemon;

/// <summary>
/// The undo history's side of the hub: recording around the mixer commands
/// and the hardware controls, the barriers, and <c>undo</c> and <c>redo</c>. A failure while recording
/// is logged and never fails the command it wraps.
/// </summary>
public sealed partial class WebSocketHub
{
    /// <summary>The one history every client shares.</summary>
    internal MixHistory History { get; } = new();

    /// <summary>
    /// Run a mixer command through <see cref="MixerService.Apply"/>. A
    /// recorded command reads the values it may change before and after and
    /// records what moved; a barrier clears the history once it succeeded; any
    /// other command costs nothing more. <paramref name="client"/> is the
    /// WebSocket the command came on, null over HTTP.
    /// </summary>
    private string? ApplyMixer(Command cmd, Guid? client)
    {
        switch (HistoryPolicy.ClassOf(cmd.Cmd))
        {
            case HistoryClass.Recorded:
                return ApplyRecorded(cmd, client);
            case HistoryClass.Barrier:
            {
                string? error = _mixer.Apply(cmd, client);
                if (error is null) ClearHistory();
                return error;
            }
            default:
                return _mixer.Apply(cmd, client);
        }
    }

    private string? ApplyRecorded(Command cmd, Guid? client)
    {
        HistoryTarget? target = null;
        Dictionary<SceneKey, object>? before = null;
        try
        {
            target = HistoryPolicy.TargetOf(cmd, _mixer.ExclusivePeersOf, _mixer.ExclusiveGroupMembers);
            if (target is not null) before = _mixer.ReadSceneValues(target.Fields, target.Keep);
        }
        catch (Exception ex) { _log.LogWarning(ex, "history: could not read the values before {command}", cmd.Cmd); }

        string? error = _mixer.Apply(cmd, client);
        if (error is not null || target is null || before is null) return error;
        try
        {
            if (_mixer.ReadSceneValues(target.Fields, target.Keep) is not { } after) return null;
            var changes = new List<SceneChange>();
            foreach ((SceneKey key, object was) in before)
                if (after.TryGetValue(key, out object? now) && !SceneValues.Same(was, now)) changes.Add(new(key, was, now));
            if (History.Record(client?.ToString("N") ?? "http", target, HistoryPolicy.LabelOf(target, _mixer.LayoutConfig, _mixer.InsertName), changes))
                _stateBroadcasts.Signal();
        }
        catch (Exception ex) { _log.LogWarning(ex, "history: could not record {command}", cmd.Cmd); }
        return null;
    }

    /// <summary>Empty the history after a barrier, and tell the clients.</summary>
    private void ClearHistory()
    {
        if (History.Clear()) _stateBroadcasts.Signal();
    }

    /// <summary>
    /// Run a <c>set</c> or <c>setWaveControl</c> through the device manager's
    /// recording write. A control on <see cref="DeviceHistory"/>'s list that
    /// the unit reads back changed becomes a step bound to that unit; any
    /// other control, phantom power, the mute and the locks among them, costs
    /// nothing more. <paramref name="client"/> is the WebSocket the command
    /// came on, null over HTTP.
    /// </summary>
    private string? ApplyDevice(string control, Guid? client, Func<DeviceWrite> write)
    {
        DeviceWrite done = write();
        if (done.Error is not null) return done.Error;
        try
        {
            if (DeviceHistory.ChangeOf(done, control) is { } change)
            {
                HistoryTarget target = DeviceHistory.TargetOf(change, done.Additional);
                string label = DeviceHistory.LabelOf(target, _mixer.LayoutConfig, done.Additional ? change.UnitName : null) ?? target.Kind;
                if (History.Record(client?.ToString("N") ?? "http", target, label, [], [change])) _stateBroadcasts.Signal();
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "history: could not record set {control}", control); }
        return null;
    }

    /// <summary>
    /// <c>undo</c> or <c>redo</c>, after <see cref="CommandValidation.CheckHistory"/>
    /// and under the install gate. Each value is written only where it is
    /// still what the step left; one moved since is skipped and named in
    /// <c>history.note</c>. A hardware value goes only to the unit it was
    /// taken from, and not at all while that unit is absent or a lock holds
    /// it. A step that wrote nothing is dropped with an error. Only a mixer
    /// that cannot act at all keeps a step that holds mixer values. Null on
    /// success.
    /// </summary>
    private string? ApplyHistory(Command cmd)
    {
        bool redo = cmd.Cmd == "redo";
        string verb = cmd.Cmd;
        if (History.Begin(redo) is not { } begun) return $"{verb}: nothing to {verb}";
        (HistoryStep step, SceneChange[] changes, string label) = begun;
        if (CommandValidation.HistoryStep(cmd) is long id && id != step.Id) return $"{verb}: the history changed";
        DeviceChange[] device = History.DeviceChangesOf(step);

        IReadOnlyList<ScenePatchOutcome> outcomes = [];
        if (changes.Length > 0)
        {
            ScenePatchEntry[] entries = [.. changes.Select(c => redo
                ? new ScenePatchEntry(c.Key, c.Before, c.After)
                : new ScenePatchEntry(c.Key, c.After, c.Before))];
            if (_mixer.ApplyScenePatch(entries) is not { } written) return $"{verb}: {_mixer.NotBuiltError}";
            outcomes = written;
        }

        var done = new List<SceneChange>();
        int changed = 0, gone = 0, failed = 0, locked = 0;
        string? absent = null;
        for (int i = 0; i < outcomes.Count; i++)
        {
            switch (outcomes[i])
            {
                case ScenePatchOutcome.Applied or ScenePatchOutcome.AlreadyThere: done.Add(changes[i]); break;
                case ScenePatchOutcome.Changed: changed++; break;
                case ScenePatchOutcome.Gone: gone++; break;
                default: failed++; break;
            }
        }
        var doneDevice = new List<DeviceChange>();
        IReadOnlyList<DevicePatchOutcome> deviceOutcomes = ApplyDevicePatch(device, redo);
        for (int i = 0; i < device.Length; i++)
        {
            switch (deviceOutcomes[i])
            {
                case DevicePatchOutcome.Applied or DevicePatchOutcome.AlreadyThere: doneDevice.Add(device[i]); break;
                case DevicePatchOutcome.Changed: changed++; break;
                case DevicePatchOutcome.Locked: locked++; break;
                case DevicePatchOutcome.Gone: absent ??= device[i].UnitName; break;
                default: failed++; break;
            }
        }
        string? reason = changed > 0 ? "it changed since"
            : locked > 0 ? "it is locked"
            : absent is not null ? $"{absent} is not connected"
            : gone > 0 ? "its channel or mix no longer exists"
            : failed > 0 ? "it could not be written" : null;
        bool any = done.Count > 0 || doneDevice.Count > 0;
        string? note = reason is null ? null : $"{char.ToUpperInvariant(verb[0])}{verb[1..]} skipped part of {label}: {reason}.";
        History.Complete(step, redo, done, any ? note : null, doneDevice);
        _stateBroadcasts.Signal();
        return any ? null : $"{verb}: {label} was skipped: {reason}";
    }

    /// <summary>
    /// Write a step's hardware values, each group to the manager that drives
    /// its unit now: the primary's, or an additional interface's. A unit no
    /// manager drives gets nothing, and the manager checks again under its
    /// own lock, so a unit switched or replugged meanwhile cannot receive
    /// another unit's values.
    /// </summary>
    private DevicePatchOutcome[] ApplyDevicePatch(DeviceChange[] changes, bool redo)
    {
        var outcomes = new DevicePatchOutcome[changes.Length];
        foreach (IGrouping<string, int> unit in Enumerable.Range(0, changes.Length).GroupBy(i => changes[i].Unit, StringComparer.Ordinal))
        {
            int[] indices = [.. unit];
            DevicePatchEntry[] entries = [.. indices.Select(i => redo
                ? new DevicePatchEntry(changes[i].Control, changes[i].Before, changes[i].After)
                : new DevicePatchEntry(changes[i].Control, changes[i].After, changes[i].Before))];
            DeviceManager? manager = _devices.ActiveInfo?.InstanceId == unit.Key ? _devices : _waveInterfaces?.Session(unit.Key);
            IReadOnlyList<DevicePatchOutcome>? written = manager?.ApplyPatch(unit.Key, entries);
            for (int j = 0; j < indices.Length; j++) outcomes[indices[j]] = written?[j] ?? DevicePatchOutcome.Gone;
        }
        return outcomes;
    }
}
