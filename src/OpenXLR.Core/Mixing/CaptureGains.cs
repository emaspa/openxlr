namespace OpenXLR.Core.Mixing;

/// <summary>
/// The gain of each capture input on a source OpenXLR does not drive: the
/// source's PipeWire volume, read as the device's own gain where it has a
/// hardware capture control (see <see cref="CaptureGain"/>). The sweep reads
/// it again whenever the registry shows the source's volume moved, so a
/// change made by another program (a voice chat's automatic gain, a
/// browser, a desktop volume applet) is reported and never written back.
/// Only <see cref="SetCaptureGain"/> writes it. The source's own mute is read
/// and written the same way, beside the gain: only
/// <see cref="SetCaptureMuted"/> and <see cref="ToggleCaptureMuted"/> write it.
/// </summary>
public sealed partial class Mixer
{
    private readonly Dictionary<string, CaptureGainStatus> _captureGains = new(StringComparer.Ordinal);
    private static readonly TimeSpan HardwareMinimumRetry = TimeSpan.FromSeconds(10);
    // Each retry runs amixer under the mixer lock, up to its timeout, so a
    // card that keeps failing is given up on and keeps the fallback range.
    private const int HardwareMinimumRetries = 3;
    private readonly Dictionary<string, (double? Value, long? FailedAt, int Failures)> _hardwareMinimums = new(StringComparer.Ordinal);
    private string? _captureGainKey;
    private long? _captureGainRetrySince;
    // A recalled profile's gains and mutes, by channel, until each channel's
    // source is connected and they are written.
    private readonly Dictionary<string, CaptureGainScene> _captureRecall = new(StringComparer.Ordinal);

    /// <summary>
    /// The source a capture input's gain is read from and written to, or
    /// null when the channel has no gain of its own: it carries a Wave unit's
    /// input, whose gain is the unit's (<see cref="ChannelStatus.UnitInput"/>),
    /// it remembers a supported unit, or its source is one OpenXLR never
    /// writes (<see cref="CaptureGain.IsProtectedSource"/>).
    /// </summary>
    private static string? CaptureGainSource(ChannelDefinition channel, IReadOnlyDictionary<string, UnitInputStatus> carried)
        => channel.CaptureSource is { } source && channel.CaptureUnit is null && !carried.ContainsKey(channel.Id)
            && !CaptureGain.IsProtectedSource(source) ? source : null;

    /// <summary>The gain the state reports for a channel, or null.</summary>
    private CaptureGainStatus? CaptureGainLocked(ChannelDefinition channel, IReadOnlyDictionary<string, UnitInputStatus> carried)
        => CaptureGainSource(channel, carried) is not null ? _captureGains.GetValueOrDefault(channel.Id) : null;

    /// <summary>
    /// Read every capture input's gain again when its source's volume moved,
    /// the source came or went, or the inputs changed. True when a reported
    /// gain changed. Nothing is written.
    /// </summary>
    public bool SyncCaptureGains()
    {
        lock (_gate)
        {
            Dictionary<string, UnitInputStatus> carried = UnitInputsLocked();
            Dictionary<string, string> sources = _built
                ? _config.Channels.Select(c => (c.Id, Source: CaptureGainSource(c, carried)))
                    .Where(c => c.Source is not null).ToDictionary(c => c.Id, c => c.Source!, StringComparer.Ordinal)
                : new(StringComparer.Ordinal);
            var names = new HashSet<string>(sources.Values, StringComparer.Ordinal);
            // The registry carries every node's channel volumes, so an
            // unchanged one costs no helper unless a failed hardware-range
            // probe is due again. pactl also knows the base volume.
            string? signature = names.Count == 0 ? "" : _pw.SourceVolumeSignature(names);
            string? key = signature is null ? null
                : string.Join('\n', sources.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key}\t{s.Value}")) + "\n\n" + signature;
            if (key is not null && key == _captureGainKey
                && (_captureGainRetrySince is not { } since || Time.GetElapsedTime(since) < HardwareMinimumRetry)) return false;
            IReadOnlyDictionary<string, SourceVolume>? volumes = _pw.GetSourceVolumes(names);
            if (volumes is null) return false;   // pactl failed: keep the last reading and try again next sweep
            // A recalled profile's gains go to their sources once connected,
            // and the reading is taken again so the state shows them.
            if (_captureRecall.Count > 0 && WriteCaptureRecallLocked(volumes, carried))
                volumes = _pw.GetSourceVolumes(names) ?? volumes;
            _captureGainKey = key;
            _captureGainRetrySince = null;
            var next = new Dictionary<string, CaptureGainStatus>(StringComparer.Ordinal);
            foreach ((string channel, string source) in sources)
                if (volumes.TryGetValue(source, out SourceVolume? volume))
                {
                    double? minimum = HardwareMinimumLocked(volume, out long? failedAt);
                    if (failedAt is { } failed && (_captureGainRetrySince is null || failed < _captureGainRetrySince.Value))
                        _captureGainRetrySince = failed;
                    next[channel] = CaptureGain.Status(volume, minimum);
                }
            if (next.Count == _captureGains.Count && next.All(g => _captureGains.GetValueOrDefault(g.Key) == g.Value)) return false;
            _captureGains.Clear();
            foreach ((string channel, CaptureGainStatus gain) in next) _captureGains[channel] = gain;
            return true;
        }
    }

    /// <summary>
    /// Set a capture input's gain in dB, clamped to its range: the device's
    /// capture control on a source that has one, its software level on any
    /// other. Refused on a channel whose gain belongs to a Wave unit, on one
    /// whose source OpenXLR never writes, and while the source is missing.
    /// </summary>
    public void SetCaptureGain(string channelId, double gainDb)
    {
        if (!double.IsFinite(gainDb)) throw new ArgumentException("setCaptureGain: the gain must be a finite number of dB.");
        lock (_gate)
        {
            (string source, SourceVolume volume) = CaptureSourceLocked(channelId, "gain");
            double? minimum = HardwareMinimumLocked(volume, out _);
            long raw = CaptureGain.RawFor(volume, CaptureGain.Status(volume, minimum), gainDb);
            _pw.SetSourceVolume(source, raw);
            _captureRecall.Remove(channelId);
            // Report what was written at once; the next sweep reads it back.
            _captureGains[channelId] = CaptureGain.Status(volume with { Volume = raw }, minimum);
            _captureGainKey = null;
        }
    }

    /// <summary>
    /// Mute or unmute a capture input's source, for every program that
    /// records it, as a desktop panel would. Refused where
    /// <see cref="SetCaptureGain"/> is: on a channel whose input belongs to a
    /// Wave unit, on a source OpenXLR never writes, and while the source is
    /// missing.
    /// </summary>
    public void SetCaptureMuted(string channelId, bool muted)
    {
        lock (_gate) SetCaptureMutedLocked(CaptureSourceLocked(channelId, "mute"), muted);
    }

    /// <summary>
    /// Flip a capture input's source mute from what the audio server reports
    /// now, under the mixer lock, so each accepted command flips it once
    /// however fast a key repeats it.
    /// </summary>
    public void ToggleCaptureMuted(string channelId)
    {
        lock (_gate)
        {
            (string Source, SourceVolume Volume) found = CaptureSourceLocked(channelId, "mute");
            SetCaptureMutedLocked(found, !found.Volume.Muted);
        }
    }

    private void SetCaptureMutedLocked((string Source, SourceVolume Volume) found, bool muted)
    {
        _pw.SetSourceMute(found.Source, muted);
        // A recall still waiting for this source must not undo the change.
        Dictionary<string, UnitInputStatus> onSource = UnitInputsLocked();
        foreach (ChannelDefinition channel in _config.Channels)
            if (CaptureGainSource(channel, onSource) == found.Source) _captureRecall.Remove(channel.Id);
        // Report it at once on every input on the source, which shares the
        // one mute; the next sweep reads the source again.
        CaptureGainStatus status = CaptureGain.Status(found.Volume with { Muted = muted }, HardwareMinimumLocked(found.Volume, out _));
        Dictionary<string, UnitInputStatus> carried = UnitInputsLocked();
        foreach (ChannelDefinition channel in _config.Channels)
            if (CaptureGainSource(channel, carried) == found.Source) _captureGains[channel.Id] = status;
        _captureGainKey = null;
    }

    /// <summary>
    /// The source a capture input's gain and mute are written to, read now.
    /// <paramref name="what"/> names the control in the refusal.
    /// </summary>
    private (string Source, SourceVolume Volume) CaptureSourceLocked(string channelId, string what)
    {
        ChannelDefinition channel = _config.Channels.FirstOrDefault(c => c.Id == channelId && c.CaptureSource is not null)
            ?? throw new ArgumentException($"'{channelId}' is not a capture input.");
        string source = CaptureGainSource(channel, UnitInputsLocked())
            ?? throw new InvalidOperationException(
                $"{ChannelNameLocked(channel)} takes its sound from a Wave unit, whose {what} is set through the unit's controls.");
        IReadOnlyDictionary<string, SourceVolume> volumes = _pw.GetSourceVolumes(new HashSet<string>([source], StringComparer.Ordinal))
            ?? throw new InvalidOperationException($"The volume of {source} could not be read.");
        return volumes.TryGetValue(source, out SourceVolume? volume) ? (source, volume)
            : throw new InvalidOperationException($"{source} is not connected.");
    }

    /// <summary>
    /// The device's lowest hardware gain, read once per source from the ALSA
    /// capture control whose highest gain the base volume stands for; null
    /// for a software level, without a card or a matching control. A failed
    /// helper call is retried after a delay, even at unchanged volume, up to
    /// <see cref="HardwareMinimumRetries"/> times; a missing amixer is an
    /// answer like a missing control and is not retried.
    /// </summary>
    private double? HardwareMinimumLocked(SourceVolume volume, out long? failedAt)
    {
        failedAt = null;
        if (!CaptureGain.IsHardware(volume) || volume.AlsaCard is not { } card) return null;
        string key = $"{volume.Name}\t{card}\t{volume.BaseVolume}";
        if (_hardwareMinimums.TryGetValue(key, out var known)
            && (known.FailedAt is not { } previousFailure || Time.GetElapsedTime(previousFailure) < HardwareMinimumRetry))
        {
            failedAt = known.FailedAt;
            return known.Value;
        }
        string? controls = PipeWireAdapter.AlsaControls(card, out bool noAmixer);
        double? found = controls is not null ? CaptureGain.HardwareMinimum(controls, -CaptureGain.VolumeDb(volume.BaseVolume)) : null;
        int failures = controls is null && !noAmixer ? known.Failures + 1 : 0;
        if (failures is > 0 and <= HardwareMinimumRetries) failedAt = Time.GetTimestamp();
        if (_hardwareMinimums.Count >= 64) _hardwareMinimums.Clear();
        _hardwareMinimums[key] = (found, failedAt, failures);
        return found;
    }

    /// <summary>
    /// The gains and mutes a profile keeps: every capture input with a gain
    /// of its own, as last read, or as a recall still waiting for its source
    /// asked. An input whose source has not been read is left out, so a
    /// recall leaves it as it is.
    /// </summary>
    private Dictionary<string, CaptureGainScene> SceneCaptureGainsLocked()
    {
        Dictionary<string, UnitInputStatus> carried = UnitInputsLocked();
        var gains = new Dictionary<string, CaptureGainScene>(StringComparer.Ordinal);
        foreach (ChannelDefinition channel in _config.Channels)
        {
            if (CaptureGainSource(channel, carried) is null) continue;
            if (_captureRecall.TryGetValue(channel.Id, out CaptureGainScene? waiting)) gains[channel.Id] = waiting;
            else if (_captureGains.TryGetValue(channel.Id, out CaptureGainStatus? gain)) gains[channel.Id] = new(gain.GainDb, gain.Muted);
        }
        return gains;
    }

    /// <summary>
    /// Recall a profile's gains and mutes. Each is written by the next sweep
    /// that finds its source connected; one for a channel the layout no
    /// longer has, or whose gain belongs to a Wave unit, is dropped then.
    /// </summary>
    private void RecallCaptureGainsLocked(IReadOnlyDictionary<string, CaptureGainScene>? recalled)
    {
        if (recalled is null) return;
        foreach ((string channel, CaptureGainScene gain) in recalled)
            if (_config.Channels.Any(c => c.Id == channel && c.CaptureSource is not null)) _captureRecall[channel] = gain;
        // The next sweep reads the sources again, connected or not.
        _captureGainKey = null;
    }

    /// <summary>Write each waiting recall whose source is connected. True when anything was written.</summary>
    private bool WriteCaptureRecallLocked(IReadOnlyDictionary<string, SourceVolume> volumes, IReadOnlyDictionary<string, UnitInputStatus> carried)
    {
        bool wrote = false;
        foreach ((string channelId, CaptureGainScene wanted) in _captureRecall.ToArray())
        {
            ChannelDefinition? channel = _config.Channels.FirstOrDefault(c => c.Id == channelId);
            if (channel is null || CaptureGainSource(channel, carried) is not { } source)
            {
                _captureRecall.Remove(channelId);
                continue;
            }
            if (!volumes.TryGetValue(source, out SourceVolume? volume)) continue;   // not connected yet
            long raw = CaptureGain.RawFor(volume, CaptureGain.Status(volume, HardwareMinimumLocked(volume, out _)), wanted.GainDb);
            if (raw != volume.Volume) _pw.SetSourceVolume(source, raw);
            if (wanted.Muted != volume.Muted) _pw.SetSourceMute(source, wanted.Muted);
            _captureRecall.Remove(channelId);
            wrote = true;
        }
        return wrote;
    }
}
