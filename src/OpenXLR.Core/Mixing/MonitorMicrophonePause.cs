namespace OpenXLR.Core.Mixing;

public sealed partial class Mixer
{
    private bool _monitorMuteMicrophones;
    private readonly Dictionary<string, (int Failures, long RetryAt)> _microphonePauseRepairs = new(StringComparer.Ordinal);
    private bool _microphonePausePending => _microphonePauseRepairs.Count > 0;

    /// <summary>Include microphone channels in the monitoring pause. Machine state, never a profile.</summary>
    public bool MonitorMuteMicrophones { get { lock (_gate) return _monitorMuteMicrophones; } }

    public void SetMonitorMuteMicrophones(bool enabled)
    {
        lock (_gate)
        {
            if (_monitorMuteMicrophones == enabled) return;
            _monitorMuteMicrophones = enabled;
            SyncMicrophonePauseLocked(force: true);
        }
    }

    private bool MicrophonesPausedLocked => _monitorMuteMicrophones && _monitorMuted;

    private bool UnconfirmedMicrophonePauseLocked(ChannelDefinition channel)
        => MicrophonesPausedLocked && _microphonePauseRepairs.ContainsKey(channel.SinkName);

    private void SyncMicrophonePauseLocked(bool force = false)
    {
        if (!_built || !force && !_monitorMuteMicrophones && !_microphonePausePending) return;
        _microphonePauseRepairs.Clear();
        foreach (ChannelDefinition channel in _config.Channels.Where(c => c.Microphone))
            _microphonePauseRepairs[channel.SinkName] = default;
        bool available = true;
        foreach (ChannelDefinition channel in _config.Channels.Where(c => c.Microphone))
        {
            if (available) available = ApplyMicrophonePauseLocked(channel.SinkName);
        }
        // A dry monitor send has no channel sink to mute; its legs follow the pause.
        foreach (string dry in _dryRoutes.Keys.ToList()) ReapplyDryCellsLocked(dry);
        PushDuckKeyOpenLocked();
        if (MicrophonesPausedLocked && _soundCheck is not null)
            StopSoundCheckLocked(restore: true, error: "Sound Check stopped because microphones are included in the monitoring pause.");
    }

    private bool ApplyMicrophonePauseLocked(string sink)
    {
        try
        {
            // Native names work before the registry publishes new nodes.
            bool wanted = MicrophonesPausedLocked;
            if (_pw.GetSinkMuted(sink) != wanted)
            {
                _pw.SetSinkMuted(sink, wanted);
                if (_pw.GetSinkMuted(sink) != wanted) throw new InvalidOperationException("Microphone mute was not confirmed.");
            }
            _microphonePauseRepairs.Remove(sink);
            return true;
        }
        catch (InvalidOperationException)
        {
            int failures = Math.Min(_microphonePauseRepairs.GetValueOrDefault(sink).Failures + 1, 8);
            _microphonePauseRepairs[sink] = (failures, Clock() + DesktopMuteRetryDelay(failures));
            return false;
        }
    }
}
