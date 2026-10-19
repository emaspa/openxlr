namespace OpenXLR.Core.Mixing;

/// <summary>A stable capture node and one zero-based mono port feeding XLR 1.</summary>
public sealed record PrimaryInputBinding(string Source, int Channel = 0)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => CaptureBinding.IsValid(Source, 0, Channel);
}

public sealed partial class Mixer
{
    private PrimaryInputBinding? _primaryInput, _wiredPrimaryInput;

    /// <summary>Null uses the supported interface; an explicit binding enables generic microphone mode.</summary>
    public PrimaryInputBinding? PrimaryInput { get { lock (_gate) return _primaryInput; } }

    /// <summary>
    /// Replace the primary microphone without rebuilding channels or mixes.
    /// A missing source stays selected and silent. Switching sources mutes
    /// microphone monitoring, so plugging in a hot microphone cannot feed back.
    /// </summary>
    public void SetPrimaryInput(PrimaryInputBinding? input, Func<MixerSettings, string?>? save = null)
    {
        if (input is not null && !input.IsValid) throw new ArgumentException("Select an external capture source and a mono port from 0 to 63.");
        lock (_gate)
        {
            if (_primaryInput == input)
            {
                if (save is not null) PersistLocked(save);
                return;
            }
            PrimaryInputBinding? previous = _primaryInput;
            var previousMutes = new HashSet<string>(_muted);
            bool previousHardwareMonitor = _hardwareMicMonitor;
            if (!_built) { _primaryInput = input; return; }
            try
            {
                StopSoundCheckLocked(restore: false);
                _primaryInput = input;
                SetHardwareMicMonitor(false);
                MuteInputMonitorsLocked();
                WireInputFeedsLocked();
                WireAuxRouteLocked();
                if (save is not null) PersistLocked(save);
            }
            catch (Exception change)
            {
                _primaryInput = previous;
                _muted.Clear();
                _muted.UnionWith(previousMutes);
                try
                {
                    SetHardwareMicMonitor(previousHardwareMonitor);
                    ReapplyMixesLocked(_config.Mixes);
                    WireInputFeedsLocked();
                    WireAuxRouteLocked();
                }
                catch (Exception rollback)
                {
                    throw new AggregateException("The primary microphone could not be changed or restored.", change, rollback);
                }
                throw;
            }
        }
    }

    private void MuteInputMonitorsLocked()
    {
        foreach (MixDefinition mix in _config.Mixes.Where(m => m.Kind == MixKind.Monitor))
            foreach (ChannelDefinition channel in _config.Channels.Where(c => c.InputPair is not null))
            {
                _muted.Add(Cell(channel.Id, mix.Id));
                ApplyCellLocked(channel.Id, mix.Id);
            }
    }

    private PortLink RouteInputLocked(string source, string target, ChannelDefinition channel, bool processed = false)
    {
        PortLink route = _primaryInput is { } input && !processed
            ? _pw.LinkNodes(source, "", target, "playback", fromChannel: input.Channel)
            : _pw.RouteInputToChannel(source, target, processed ? 0 : channel.InputPair!.Value);
        if (_primaryInput is not null && (!route.Complete || route.Pairs.Count < (target == channel.SinkName ? 2 : 1)))
        {
            // A half-connected microphone must retry instead of being kept
            // as a healthy route that carries audio on one side only.
            _pw.Unlink(route);
            return new PortLink([]);
        }
        return route;
    }
}
