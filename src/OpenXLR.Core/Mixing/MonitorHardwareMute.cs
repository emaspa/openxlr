namespace OpenXLR.Core.Mixing;

public sealed partial class Mixer
{
    // The native flags held before OpenXLR muted an output. This is machine
    // wiring, saved with selected outputs rather than with profile scenes.
    private readonly Dictionary<string, bool> _monitorOutputMutes = new(StringComparer.Ordinal);
    private bool _readingDesktopMutes;
    private bool _monitorOutputMutesChanged;

    private void RememberMonitorOutputMuteLocked(string name, bool muted)
    {
        if (_monitorOutputMutes.TryGetValue(name, out bool previous) && previous == muted) return;
        _monitorOutputMutes[name] = muted;
        _monitorOutputMutesChanged = true;
    }

    private void ForgetMonitorOutputMuteLocked(string name)
    {
        if (_monitorOutputMutes.Remove(name)) _monitorOutputMutesChanged = true;
    }

    /// <summary>Whether one selected output has an open mix in its assigned feed.</summary>
    public bool IsMonitorOutputAudible(string output)
    {
        lock (_gate) return !_monitorMuted && _monitorOutputs.Contains(output)
            && MixesForOutputLocked(output).Any(m => !_mixMuted.Contains(m.Id));
    }

    private void SyncMonitorHardwareMutesLocked(string? unmutedMix = null, bool explicitChange = false)
    {
        if (!_built) return;
        // A new explicit command reclaims hardware released by an applet,
        // even while a grouped desktop observation defers its native writes.
        if (explicitChange)
            foreach (string name in PhysicalMonitorOutputsLocked())
                if (_outputMutes.TryGetValue(name, out var previous) && (previous.Released || previous.UnmuteObserved))
                {
                    _outputMutes[name] = previous with
                    {
                        Released = false, UnmuteObserved = false,
                        WritePending = previous.WritePending || previous.UnmuteObserved,
                    };
                    if (previous.UnmuteObserved)
                    {
                        // Supersede the unverified edit through the normal
                        // identity-checked repair instead of a blind toggle.
                        _desktopMuteResets[name] = (previous.Muted, 0, 0);
                        _desktopMuteOffsets.Remove(name);
                        _desktopMutePending.Remove(name);
                        _desktopMuteRetries.Remove(name);
                    }
                }
        if (_readingDesktopMutes) return;
        bool nativeAvailable = true;
        foreach (string name in PhysicalMonitorOutputsLocked())
        {
            if (!_outputMutes.TryGetValue(name, out var baseline) || baseline.Identity is null) continue;
            if (!explicitChange && _desktopMuteRetries.ContainsKey(name) && !_desktopMuteOffsets.Contains(name)) continue;
            var outputs = _monitorOutputs.Where(o => StripMarker(o) == name).ToArray();
            bool audible = !_monitorMuted && outputs.Any(o => MixesForOutputLocked(o).Any(m => !_mixMuted.Contains(m.Id)));
            // The Pro's Aux pair and monitor bus live in one PipeWire sink.
            // Its jack selectors stop monitoring independently of the Aux port.
            bool independentAux = _auxPortEnabled && _auxTargetSink == name
                && _config.Mixes.Any(m => m.Kind == MixKind.AuxPort && !_mixMuted.Contains(m.Id));
            bool closed = !audible && !independentAux;
            bool released = closed && baseline.Released && !explicitChange;
            bool explicitOpen = unmutedMix is not null && outputs.Any(o => MixesForOutputLocked(o).Any(m => m.Id == unmutedMix));
            bool wanted, release = false;
            if (released) wanted = false;
            else if (closed)
            {
                if (!_monitorOutputMutes.ContainsKey(name)) RememberMonitorOutputMuteLocked(name, baseline.Muted);
                wanted = true;
            }
            else if (explicitOpen)
            {
                RememberMonitorOutputMuteLocked(name, false);
                wanted = false;
                release = true;
            }
            else if (_monitorOutputMutes.TryGetValue(name, out bool previous)) { wanted = previous; release = true; }
            else continue;
            if (baseline.Muted == wanted)
            {
                if (release && !baseline.WritePending && !_desktopMuteResets.ContainsKey(name)) ForgetMonitorOutputMuteLocked(name);
                continue;
            }
            _outputMutes[name] = baseline with { Muted = wanted, UnmuteObserved = false, Released = released, WritePending = true };
            bool credited = _desktopMuteOffsets.Remove(name);
            _desktopMutePending.Remove(name);
            _desktopMuteRetries.Remove(name);
            if (!nativeAvailable && !credited)
            {
                _desktopMuteResets[name] = (wanted, 0, 0);
                continue;
            }
            try
            {
                // Change the expected flag by a toggle too. A native key that
                // raced the command stays visible as a difference next sweep.
                if (_desktopMuteResets.ContainsKey(name)) _pw.SetSinkMuted(name, wanted);
                else if (!credited) _pw.ToggleSinkMuted(name);
                _desktopMuteResets.Remove(name);
            }
            catch (InvalidOperationException)
            {
                // Software links stay closed while the usual bounded native
                // restoration path retries the current intent after failure.
                _desktopMuteResets[name] = (wanted, 0, 0);
                nativeAvailable = false;
            }
        }
    }

    private HashSet<string> RestoreUnselectedMonitorHardwareMutesLocked(IEnumerable<string> removed)
    {
        var pending = removed.Where(n => _monitorOutputMutes.ContainsKey(n)).ToHashSet(StringComparer.Ordinal);
        // A refused disconnect can outlive the selection. Keep the native
        // mute until the last owned route into this output is gone.
        var due = pending.Where(n => Clock() >= _desktopMuteResets.GetValueOrDefault(n).RetryAt
            && !_monitorRoutes.Any(route => route.Value.Pairs.Count > 0
                && (route.Key == n || route.Key.StartsWith(n + "#", StringComparison.Ordinal))))
            .ToHashSet(StringComparer.Ordinal);
        if (due.Count == 0) return pending;
        IReadOnlyDictionary<string, (string Identity, bool Muted)> native;
        try { native = _pw.NativeSinkMutes(due); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException)
        {
            foreach (string name in due) Failed(name);
            return pending;
        }
        bool canWrite = true;
        foreach (string name in due)
        {
            bool previous = _monitorOutputMutes[name];
            var baseline = _outputMutes[name];
            // An owner with no verified instance waits for its device to appear.
            if (baseline.Identity is null && !native.ContainsKey(name)) { Failed(name); continue; }
            // A release only owns the instance that was actually selected.
            if (!native.TryGetValue(name, out var state) || baseline.Identity is not null && state.Identity != baseline.Identity
                || state.Muted == previous)
            {
                ForgetMonitorOutputMuteLocked(name);
                pending.Remove(name);
                continue;
            }
            if (!canWrite) { _desktopMuteResets.TryAdd(name, (previous, 0, 0)); continue; }
            try
            {
                _pw.SetSinkMuted(name, previous);
                // The readback that completes the release checks this instance.
                _outputMutes[name] = baseline with { Identity = state.Identity, Muted = previous, WritePending = true };
                // Native readback, rather than a successful exit, completes release.
                _desktopMuteResets[name] = (previous, 0, 0);
            }
            catch (InvalidOperationException) { Failed(name); canWrite = false; }
        }
        return pending;

        void Failed(string name)
        {
            int failures = Math.Min(_desktopMuteResets.GetValueOrDefault(name).Failures + 1, 8);
            _desktopMuteResets[name] = (_monitorOutputMutes[name], failures, Clock() + DesktopMuteRetryDelay(failures));
        }
    }

    /// <summary>
    /// Put every native flag OpenXLR owns back to its original, for a stop
    /// after which no graph is built to restore it. An ordinary stop leaves
    /// the mutes in place and the next start takes them over from the saved
    /// settings. An output that is absent or refuses the write stays owned,
    /// so the final save still carries it.
    /// </summary>
    public void ReleaseMonitorHardwareMutes()
    {
        lock (_gate)
        {
            if (_monitorOutputMutes.Count == 0) return;
            var native = ReadNativeMuteStatesLocked(_monitorOutputMutes.Keys.ToHashSet(StringComparer.Ordinal), immediate: true);
            foreach ((string name, bool previous) in _monitorOutputMutes.ToArray())
            {
                string? identity = _outputMutes.GetValueOrDefault(name)?.Identity;
                try
                {
                    bool muted;
                    if (native is null) muted = _pw.GetSinkMuted(name);
                    else if (!native.TryGetValue(name, out var state)) continue;
                    else if (identity is not null && state.Identity != identity)
                    {
                        // Another instance under the same name was never muted here.
                        ForgetMonitorOutputMuteLocked(name);
                        continue;
                    }
                    else muted = state.Muted;
                    if (muted != previous) _pw.SetSinkMuted(name, previous);
                    ForgetMonitorOutputMuteLocked(name);
                }
                catch (InvalidOperationException) { /* Saved for the next start that builds the graph. */ }
            }
        }
    }

    private void PrepareMonitorHardwareSelectionLocked(IReadOnlyList<string> outputs)
    {
        HashSet<string> previous = PhysicalMonitorOutputsLocked();
        var returning = outputs.Select(StripMarker).Where(n => IsExactOutputName(n) && !previous.Contains(n)
            && _monitorOutputMutes.ContainsKey(n) && _outputMutes.ContainsKey(n)).ToHashSet(StringComparer.Ordinal);
        if (returning.Count == 0) return;
        var native = ReadNativeMuteStatesLocked(returning, immediate: true);
        foreach (string name in returning)
        {
            if (native is null || !native.TryGetValue(name, out var state))
            {
                // An unverified old release cannot become a fresh mute key.
                _desktopMuteResets[name] = (_outputMutes[name].Muted, 0, 0);
                continue;
            }
            // A saved owner was never tied to an instance and is kept.
            if (_outputMutes[name].Identity is { } identity && state.Identity != identity) ForgetMonitorOutputMuteLocked(name);
            _outputMutes[name] = new(state.Identity, null, state.Muted);
            _desktopMuteResets.Remove(name);
            _desktopMuteRetries.Remove(name);
            _desktopMuteOffsets.Remove(name);
            _desktopMutePending.Remove(name);
        }
    }
}
