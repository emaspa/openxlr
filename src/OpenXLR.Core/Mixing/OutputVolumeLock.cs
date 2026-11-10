namespace OpenXLR.Core.Mixing;

/// <summary>The exact PipeWire node name and the desktop level it should keep.</summary>
public sealed record OutputVolumeLock(string Device, double Volume = double.NaN)
{
    public double[]? ChannelVolumes { get; init; }
}

public sealed partial class Mixer
{
    private OutputVolumeLock? _outputVolumeLock;
    private long _outputVolumeLockRetryAt;

    internal static bool IsLockableOutputName(string name)
        => IsExactOutputName(name) && !name.StartsWith("OpenXLR", StringComparison.Ordinal);

    private bool IsVolumeControlledByUnitLocked(string sink) => UnitBuildsJacksLocked && JackSinkLocked() == sink;

    private bool IsOutputVolumeLocked(string sink) => _outputVolumeLock?.Device == sink && !IsVolumeControlledByUnitLocked(sink);

    private void RequireEditableOutputVolume(string sink)
    {
        if (IsOutputVolumeLocked(sink))
            throw new InvalidOperationException("output volume is locked in Options, Audio");
    }

    /// <summary>
    /// The shared output volume as a client sets it. Refused while the first
    /// output is locked, since that is the output the volume belongs to and the
    /// one the state reports; a profile recall goes through
    /// <see cref="SetOutputVolume"/>, which leaves a locked output alone.
    /// </summary>
    public void SetSharedOutputVolume(double volume)
    {
        lock (_gate)
        {
            if (_monitorOutputs.FirstOrDefault() is { } first) RequireEditableOutputVolume(StripMarker(first));
            SetOutputVolume(volume);
        }
    }

    /// <summary>Hold the current output's volume, or release the existing lock, after saving the choice.</summary>
    public void SetOutputVolumeLock(bool enabled, Func<MixerSettings, string?> save)
    {
        lock (_gate)
        {
            if (enabled == (_outputVolumeLock is not null)) return;
            OutputVolumeLock? next = null;
            if (enabled)
            {
                if (!_built) throw new InvalidOperationException("mixer is not built");
                string? sink = ResolveDefaultSink(_enforcedSink, _monitorOutputs) ?? _pw.GetDefaultSink();
                if (sink is null || !IsLockableOutputName(sink))
                    sink = _monitorOutputs.FirstOrDefault() is { } first ? StripMarker(first) : null;
                if (sink is null || !IsLockableOutputName(sink) || IsVolumeControlledByUnitLocked(sink))
                    throw new InvalidOperationException("select a PipeWire output with software-controlled volume");
                sink = ResolveOutputKeyLocked(sink);
                double[] volumes = _pw.GetSinkChannelVolumes(sink)
                    ?? throw new InvalidOperationException("output volume is unavailable");
                if (volumes.Any(volume => volume > PipeWireAdapter.MaxSinkVolume))
                    throw new InvalidOperationException("lower the output volume to 150% or less before locking it");
                next = new(sink, volumes[0]) { ChannelVolumes = volumes.All(volume => volume == volumes[0]) ? null : volumes };
            }

            if (save(ExportSettings() with { OutputVolumeLock = next }) is { } error) throw new IOException(error);
            _outputVolumeLock = next;
            _outputVolumeLockRetryAt = 0;
            if (next is null) return;
            _outputVolumeDue.Delivered(next.Device);
            if (_monitorOutputs.FirstOrDefault() is { } selected && StripMarker(selected) == next.Device)
                _outputVolume = next.Volume;
        }
    }

    private double? SyncOutputVolumeLockLocked()
    {
        if (_outputVolumeLock is not { } held || IsVolumeControlledByUnitLocked(held.Device)) return null;
        if (Clock() >= _outputVolumeLockRetryAt &&
            _pw.GetSinkChannelVolumes(held.Device, skipMissing: true) is { } current)
        {
            try
            {
                if (held.ChannelVolumes is { } saved && saved.Length == current.Length)
                {
                    if (!saved.SequenceEqual(current)) _pw.SetSinkChannelVolumes(held.Device, saved);
                }
                else if (current.Any(volume => volume != held.Volume))
                    _pw.SetSinkVolume(held.Device, held.Volume);
            }
            catch (InvalidOperationException) { _outputVolumeLockRetryAt = Clock() + 3000; }
        }
        return held.Volume;
    }
}
