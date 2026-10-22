using System.Text.Json;

namespace OpenXLR.Core.Mixing;

public sealed partial class Mixer
{
    private bool _desktopMuteConsumed;
    private bool? _desktopMonitorIntent;
    // A grouped native action can be acknowledged by one sink before another.
    // Remember the latter's native parity, not a master state to restore later.
    private readonly HashSet<string> _desktopMuteOffsets = new(StringComparer.Ordinal);
    private readonly HashSet<string> _desktopMutePending = new(StringComparer.Ordinal);
    private sealed record DesktopOutputMute(string? Identity, string? RegistryIdentity, bool Muted, bool UnmuteObserved = false, bool Released = false, bool WritePending = false);
    private readonly Dictionary<string, DesktopOutputMute> _outputMutes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (bool Muted, int Failures, long RetryAt)> _desktopMuteResets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Failures, long RetryAt)> _desktopMixMuteResets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Failures, long RetryAt)> _desktopMuteRetries = new(StringComparer.Ordinal);
    private (int Failures, long RetryAt) _desktopIdentityRetry;

    /// <summary>
    /// Consume a native key with an atomic toggle. Unlike setting a fixed
    /// flag, this leaves a second press during acknowledgement pending.
    /// A refused consume changes no monitor intent; two unconsumed presses
    /// cancel each other even while a helper is unavailable.
    /// </summary>
    private bool ConsumeDesktopMuteLocked(string name)
    {
        (int failures, long retryAt) = _desktopMuteRetries.GetValueOrDefault(name);
        if (Clock() < retryAt) return false;
        try
        {
            _pw.ToggleSinkMuted(name);
            _desktopMuteRetries.Remove(name);
            return true;
        }
        catch (InvalidOperationException)
        {
            int count = Math.Min(failures + 1, 8);
            _desktopMuteRetries[name] = (count, Clock() + DesktopMuteRetryDelay(count));
            return false;
        }
    }

    private bool ReadDesktopMutePulseLocked(string name, bool moved)
    {
        if (!moved)
        {
            _desktopMuteRetries.Remove(name);
            _desktopMutePending.Remove(name);
            // The first action was counted elsewhere. Returning its unconsumed
            // flag to baseline is another user key, rather than two lost keys.
            return _desktopMuteOffsets.Remove(name);
        }
        _desktopMutePending.Add(name);
        if (!ConsumeDesktopMuteLocked(name)) return false;
        _desktopMutePending.Remove(name);
        return !_desktopMuteOffsets.Remove(name);
    }

    private bool ObservePendingDesktopMuteLocked(string name)
    {
        bool fresh = !_desktopMuteOffsets.Contains(name) && !_desktopMuteRetries.ContainsKey(name);
        _desktopMutePending.Add(name);
        _desktopMuteRetries.TryAdd(name, (0, 0));
        return fresh;
    }

    private bool CountDesktopMutePulseLocked(bool pulse)
    {
        bool changed = pulse && !_desktopMuteConsumed && ToggleDesktopMonitorIntentLocked();
        _desktopMuteConsumed |= pulse;
        if (_desktopMuteConsumed) _desktopMuteOffsets.UnionWith(_desktopMutePending);
        return changed;
    }

    private static long DesktopMuteRetryDelay(int count)
        => count < 3 ? 0 : Math.Min(30_000, 1_000L << (count - 3));

    private bool ToggleDesktopMonitorIntentLocked()
    {
        _desktopMonitorIntent = !(_desktopMonitorIntent ?? _monitorMuted);
        return ApplyDesktopMonitorIntentLocked();
    }

    private bool ApplyDesktopMonitorIntentLocked()
    {
        if (_desktopMonitorIntent is not bool wanted) return false;
        bool previous = _monitorMuted;
        try { SetMonitorMuted(wanted); }
        catch (InvalidOperationException) { _desktopMonitorIntent = wanted; }
        return previous != _monitorMuted;
    }

    /// <summary>Application sinks stay open to recording; the default sink's native mute is monitoring intent.</summary>
    private bool ReadDesktopMonitorMuteLocked(ref IReadOnlyList<OwnSinkLevel> levels)
    {
        _desktopMuteConsumed = false;
        _desktopMutePending.Clear();
        var applications = _config.Channels.Where(c => c.IsApplication).Select(c => c.SinkName).ToHashSet(StringComparer.Ordinal);
        // Idle application sinks do not need a helper to read the default.
        string? defaultSink = levels.Any(s => applications.Contains(s.Name) && (s.Muted || _desktopMuteRetries.ContainsKey(s.Name)))
            ? _pw.GetDefaultSink() : null;
        var present = levels.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        foreach (string missing in _desktopMuteRetries.Keys.Where(n => n.StartsWith("OpenXLR_", StringComparison.Ordinal)
                     && (!present.Contains(n) || n != defaultSink)).ToArray()) _desktopMuteRetries.Remove(missing);
        bool pulse = false, applicationAction = false, skipRepair = false;
        foreach (OwnSinkLevel sink in levels.Where(s => s.Name == defaultSink && applications.Contains(s.Name)
                     && (s.Muted || _desktopMuteRetries.ContainsKey(s.Name))))
        {
            try
            {
                bool muted = _pw.GetSinkMuted(sink.Name);
                applicationAction = muted;
                if (!muted) { _desktopMuteRetries.Remove(sink.Name); skipRepair = true; continue; }
                pulse = ConsumeDesktopMuteLocked(sink.Name);
                skipRepair = pulse;
            }
            catch (InvalidOperationException)
            {
                if (ReadNativeMuteStatesLocked(new HashSet<string>([sink.Name], StringComparer.Ordinal)) is { } native
                    && native.TryGetValue(sink.Name, out var state))
                {
                    applicationAction = state.Muted;
                    skipRepair = !state.Muted;
                }
            }
        }
        bool changed = CountDesktopMutePulseLocked(pulse);
        bool restoreMonitorMutes = pulse || applicationAction;
        if (restoreMonitorMutes || levels.Any(s => s.Muted && s.Name != defaultSink && applications.Contains(s.Name)))
        {
            // A desktop blanket action can target a physical default instead.
            // Read it before adopting monitor flags from that same action.
            changed |= ReadOutputMonitorMuteLocked(confirmNative: true, out bool physicalAction);
            restoreMonitorMutes |= physicalAction;
        }
        if (restoreMonitorMutes)
            foreach (OwnSinkLevel sink in levels)
                if (MonitorMixForSinkLocked(sink.Name) is { } mix && sink.Muted != _mixMuted.Contains(mix.Id))
                    _desktopMixMuteResets.TryAdd(sink.Name, (0, 0));
        // A refused consume leaves the default sink to ordinary repair.
        // An accepted consume must not overwrite a later native key.
        if (skipRepair) levels = [.. levels.Select(s => s.Name == defaultSink ? s with { Muted = false } : s)];
        return changed;
    }

    private bool RestoreDesktopMixMuteLocked(string name, string mixId)
    {
        (int failures, long retryAt) = _desktopMixMuteResets[name];
        if (Clock() < retryAt) return true;
        bool wanted = _mixMuted.Contains(mixId);
        try
        {
            // Keep the saved intent through failures and stale registry echoes.
            // Only a later native read confirms a successful restoration.
            if (_pw.GetSinkMuted(name) == wanted) _desktopMixMuteResets.Remove(name);
            else
            {
                _pw.SetSinkMuted(name, wanted);
                _desktopMixMuteResets[name] = (0, 0);
            }
            return true;
        }
        catch (InvalidOperationException)
        {
            int count = Math.Min(failures + 1, 8);
            _desktopMixMuteResets[name] = (count, Clock() + DesktopMuteRetryDelay(count));
            return false;
        }
    }

    private MixDefinition? MonitorMixForSinkLocked(string name)
        => _config.Mixes.FirstOrDefault(m => m.Kind == MixKind.Monitor && m.SinkName == name);

    private HashSet<string> PhysicalMonitorOutputsLocked()
        => _monitorOutputs.Select(StripMarker)
            .Where(n => IsExactOutputName(n) && !n.StartsWith("OpenXLR_", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);

    private void ForgetUnselectedDesktopOutputsLocked(IReadOnlySet<string> selected)
    {
        string[] removedNames = [.. _outputMutes.Keys.Where(n => !selected.Contains(n))];
        HashSet<string> pending = RestoreUnselectedMonitorHardwareMutesLocked(removedNames);
        foreach (string removed in removedNames)
        {
            if (pending.Contains(removed)) continue;
            _outputMutes.Remove(removed);
            _desktopMuteResets.Remove(removed);
            _desktopMuteRetries.Remove(removed);
            _desktopMuteOffsets.Remove(removed);
            _desktopMutePending.Remove(removed);
        }
    }

    private IReadOnlyDictionary<string, (string Identity, bool Muted)>? ReadNativeMuteStatesLocked(IReadOnlySet<string> names, bool immediate = false)
    {
        if (!immediate && Clock() < _desktopIdentityRetry.RetryAt) return null;
        try
        {
            var states = _pw.NativeSinkMutes(names);
            _desktopIdentityRetry = default;
            return states;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or JsonException)
        {
            int failures = Math.Min(_desktopIdentityRetry.Failures + 1, 8);
            _desktopIdentityRetry = (failures, Clock() + DesktopMuteRetryDelay(failures));
            return null;
        }
    }

    /// <summary>Remember new outputs before a key can arrive after selection returns.</summary>
    private void CaptureMonitorMuteBaselinesLocked()
    {
        HashSet<string> selected = PhysicalMonitorOutputsLocked();
        ForgetUnselectedDesktopOutputsLocked(selected);
        HashSet<string> added = selected.Where(n => !_outputMutes.ContainsKey(n)).ToHashSet(StringComparer.Ordinal);
        if (added.Count == 0) return;
        IReadOnlyDictionary<string, (string Identity, bool Muted)>? nodes = null;
        try { nodes = _pw.ObservedSinkMutes(selected); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { /* Native commands may already reach the selected sink. */ }
        var native = ReadNativeMuteStatesLocked(added, immediate: true);
        foreach (string name in added)
        {
            if (native is not null && native.TryGetValue(name, out var state))
            {
                string? registryIdentity = nodes is not null && nodes.TryGetValue(name, out var node)
                    && !DesktopIdentitiesDisagree(state.Identity, node.Identity) ? node.Identity : null;
                _outputMutes[name] = new(state.Identity, registryIdentity, state.Muted);
            }
            else if (Clock() >= _desktopIdentityRetry.RetryAt)
            {
                try { _outputMutes[name] = new(null, null, _pw.GetSinkMuted(name)); }
                catch (InvalidOperationException) { /* An absent output is discovered by the next sweep. */ }
            }
        }
    }

    private static bool DesktopIdentitiesDisagree(string native, string registry)
        => native != registry && (native.StartsWith("serial:", StringComparison.Ordinal) && registry.StartsWith("serial:", StringComparison.Ordinal)
            || native.StartsWith("node:", StringComparison.Ordinal) && registry.StartsWith("node:", StringComparison.Ordinal));

    /// <summary>External output mute changes control OpenXLR listening, never its recording mixes.</summary>
    private bool ReadOutputMonitorMuteLocked() => ReadOutputMonitorMuteLocked(confirmNative: false, out _);

    private bool ReadOutputMonitorMuteLocked(bool confirmNative, out bool observedAction)
    {
        try { return ReadOutputMonitorMuteBatchLocked(confirmNative, out observedAction); }
        finally { _desktopMutePending.Clear(); }
    }

    private bool ReadOutputMonitorMuteBatchLocked(bool confirmNative, out bool observedAction)
    {
        observedAction = false;
        bool changed = ApplyDesktopMonitorIntentLocked();
        HashSet<string> selected = PhysicalMonitorOutputsLocked();
        ForgetUnselectedDesktopOutputsLocked(selected);
        IReadOnlyDictionary<string, (string Identity, bool Muted)> nodes;
        try { nodes = _pw.ObservedSinkMutes(selected); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { return changed; }
        // Native and registry connections can describe different generations
        // of the same name. Verify only a changed or first registry identity,
        // in one batch, against the native identity sampled with its mute.
        var verify = selected.Where(name =>
        {
            bool present = nodes.TryGetValue(name, out var node);
            return _outputMutes.TryGetValue(name, out var previous)
                ? present && (previous.Identity is null || previous.UnmuteObserved || previous.RegistryIdentity != node.Identity)
                    || !present && previous.RegistryIdentity is not null
                : present;
        }).ToHashSet(StringComparer.Ordinal);
        var nativeNames = selected.Where(name => verify.Contains(name)
            || _outputMutes.TryGetValue(name, out var previous) && previous.WritePending
                && (nodes.ContainsKey(name) || previous.RegistryIdentity is not null)).ToHashSet(StringComparer.Ordinal);
        var native = nativeNames.Count > 0 ? ReadNativeMuteStatesLocked(nativeNames) : null;
        var observed = new Dictionary<string, bool>(StringComparer.Ordinal);
        var observedNative = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in selected)
        {
            if (verify.Contains(name))
            {
                if (native is null)
                {
                    if (_outputMutes.TryGetValue(name, out var pendingWrite) && pendingWrite.WritePending) continue;
                    // Registry mute can be late too. Confirm the native flag
                    // before crediting a source whose identity read failed.
                    if (Clock() >= _desktopIdentityRetry.RetryAt
                        && _outputMutes.TryGetValue(name, out var baseline) && !_desktopMuteResets.ContainsKey(name))
                    {
                        try
                        {
                            bool muted = _pw.GetSinkMuted(name);
                            if (!muted && baseline.Muted && baseline.Identity is not null && !(_monitorMuted && _monitorOutputMutes.ContainsKey(name)))
                                _outputMutes[name] = baseline with { UnmuteObserved = true };
                            else if (muted != baseline.Muted) observedAction |= ObservePendingDesktopMuteLocked(name);
                        }
                        catch (InvalidOperationException) { /* No observed action to count until a read succeeds. */ }
                    }
                    continue;
                }
                if (!native.TryGetValue(name, out var state))
                {
                    if (_outputMutes.TryGetValue(name, out var missing))
                    {
                        if (missing.Identity is not null)
                        {
                            _desktopMuteResets.TryAdd(name, (missing.Muted, 0, 0));
                            _desktopMuteOffsets.Remove(name);
                            _desktopMutePending.Remove(name);
                        }
                        _outputMutes[name] = missing with { RegistryIdentity = null, UnmuteObserved = false };
                    }
                    continue;
                }
                bool known = _outputMutes.TryGetValue(name, out var previous);
                string? registryIdentity = nodes.TryGetValue(name, out var registry) ? registry.Identity : null;
                if (registryIdentity is not null && DesktopIdentitiesDisagree(state.Identity, registryIdentity))
                {
                    // Neither generation's flag is a key until both views agree.
                    _outputMutes[name] = known ? previous! with { RegistryIdentity = null, UnmuteObserved = false }
                        : new(state.Identity, null, state.Muted);
                    continue;
                }
                bool replaced = known && previous!.Identity is not null && previous.Identity != state.Identity;
                if (replaced)
                {
                    _desktopMuteResets[name] = (previous!.Muted, 0, 0);
                    _desktopMuteRetries.Remove(name);
                    _desktopMuteOffsets.Remove(name);
                    _desktopMutePending.Remove(name);
                }
                // An unmute seen during a failed identity read belongs to
                // the original baseline only after that instance is verified.
                if (known && !replaced && previous!.UnmuteObserved && _monitorOutputMutes.ContainsKey(name))
                    RememberMonitorOutputMuteLocked(name, false);
                _outputMutes[name] = new(state.Identity, registryIdentity,
                    known ? previous!.Muted && (replaced || !previous.UnmuteObserved) : state.Muted,
                    Released: known && !replaced && (previous!.Released || previous.UnmuteObserved && !_monitorMuted && _monitorOutputMutes.ContainsKey(name)),
                    WritePending: known && !replaced && previous!.WritePending && state.Muted != previous.Muted);
                observed[name] = state.Muted;
                observedNative.Add(name);
                continue;
            }
            if (_outputMutes.TryGetValue(name, out var pending) && pending.WritePending)
            {
                if (native is null || !native.TryGetValue(name, out var state) || state.Identity != pending.Identity) continue;
                observed[name] = state.Muted;
                observedNative.Add(name);
            }
            else if (nodes.TryGetValue(name, out var node)) observed[name] = node.Muted;
        }
        var failed = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in observed.Keys.ToArray())
        {
            var previous = _outputMutes[name];
            if (!observedNative.Contains(name) && !_desktopMuteResets.ContainsKey(name) && (confirmNative || previous.Muted != observed[name] || _desktopMuteConsumed || _desktopMuteRetries.ContainsKey(name) || _desktopMuteOffsets.Contains(name)))
            {
                try { observed[name] = _pw.GetSinkMuted(name); }
                catch (InvalidOperationException) { observed.Remove(name); failed.Add(name); }
            }
        }
        bool pulse = false;
        if (failed.Count > 0 && ReadNativeMuteStatesLocked(failed) is { } fallback)
            foreach (string name in failed)
                if (!_desktopMuteResets.ContainsKey(name) && fallback.TryGetValue(name, out var state) && state.Identity == _outputMutes[name].Identity)
                {
                    if (AcceptDesktopOutputUnmuteLocked(name, state.Muted)) continue;
                    if (state.Muted != _outputMutes[name].Muted) observedAction |= ObservePendingDesktopMuteLocked(name);
                    else
                    {
                        bool cancellation = ReadDesktopMutePulseLocked(name, false);
                        pulse |= cancellation;
                        observedAction |= cancellation;
                    }
                }
        var resumedOutputs = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string name, bool muted) in observed)
        {
            bool ownedUnmute = !muted && _monitorMuted && _monitorOutputMutes.ContainsKey(name) && _outputMutes[name].Muted;
            if (_desktopMuteResets.ContainsKey(name) || AcceptDesktopOutputUnmuteLocked(name, muted)) continue;
            if (_outputMutes[name].WritePending && _outputMutes[name].Muted == muted)
                _outputMutes[name] = _outputMutes[name] with { WritePending = false };
            bool moved = _outputMutes[name].Muted != muted;
            bool credited = _desktopMuteOffsets.Contains(name);
            // An old acknowledgement must not replace a newer native mix edit.
            observedAction |= moved ? !credited && !_desktopMuteRetries.ContainsKey(name) : credited;
            bool accepted = ReadDesktopMutePulseLocked(name, moved);
            pulse |= accepted;
            if (ownedUnmute && accepted) resumedOutputs.Add(name);
        }
        changed |= CountDesktopMutePulseLocked(pulse);
        // Resuming through a native unmute releases that hardware flag even
        // when every assigned mix remains individually closed. It opens no mix.
        if (!_monitorMuted)
            foreach (string name in resumedOutputs)
                _outputMutes[name] = _outputMutes[name] with { Released = true };
        bool canReset = true;
        foreach ((string name, (bool muted, int failures, long retryAt)) in _desktopMuteResets.ToArray())
        {
            if (!canReset || !selected.Contains(name) && _monitorOutputMutes.ContainsKey(name)) continue;
            if (!observed.TryGetValue(name, out bool current)) continue;
            if (current == muted)
            {
                if (!observedNative.Contains(name))
                {
                    if (Clock() < retryAt) continue;
                    try { current = _pw.GetSinkMuted(name); }
                    catch (InvalidOperationException)
                    {
                        int count = Math.Min(failures + 1, 8);
                        _desktopMuteResets[name] = (muted, count, Clock() + DesktopMuteRetryDelay(count));
                        canReset = false;
                        continue;
                    }
                }
                if (current == muted)
                {
                    _desktopMuteResets.Remove(name);
                    _outputMutes[name] = _outputMutes[name] with { WritePending = false };
                    continue;
                }
            }
            if (Clock() < retryAt || !canReset) continue;
            try
            {
                _pw.SetSinkMuted(name, muted);
                _desktopMuteResets.Remove(name);
            }
            catch (InvalidOperationException)
            {
                int count = Math.Min(failures + 1, 8);
                _desktopMuteResets[name] = (muted, count, Clock() + DesktopMuteRetryDelay(count));
                canReset = false;
            }
        }
        return changed;
    }

    private bool AcceptDesktopOutputUnmuteLocked(string name, bool muted)
    {
        if (muted || !_outputMutes[name].Muted || _desktopMuteResets.ContainsKey(name)) return false;
        bool owned = _monitorOutputMutes.ContainsKey(name);
        if (owned)
        {
            // A verified native unmute supersedes the original native mute,
            // whether monitoring was paused or its feed was individually closed.
            RememberMonitorOutputMuteLocked(name, false);
            if (_monitorMuted) return false; // An authored pause's unmute is a resume key.
        }
        _outputMutes[name] = _outputMutes[name] with { Muted = false, UnmuteObserved = false, Released = owned, WritePending = false };
        _desktopMuteRetries.Remove(name);
        _desktopMuteOffsets.Remove(name);
        _desktopMutePending.Remove(name);
        return true;
    }

    private void ResetDesktopMonitorMuteLocked()
    {
        _desktopMuteConsumed = false;
        _desktopMonitorIntent = null;
        _desktopMuteOffsets.Clear();
        _desktopMutePending.Clear();
        _outputMutes.Clear();
        _monitorOutputMutes.Clear();
        _monitorOutputMutesChanged = false;
        _desktopMuteResets.Clear();
        _desktopMixMuteResets.Clear();
        _desktopMuteRetries.Clear();
        _desktopIdentityRetry = default;
    }
}
