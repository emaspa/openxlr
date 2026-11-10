using System.Diagnostics;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// Sound Check as clients see it: the microphone channel a session belongs
/// to (null when idle), what the loop helper is doing, how much it holds,
/// and why the last session ended when it ended on its own.
/// </summary>
public sealed record SoundCheckState(string? Channel, string Mode, double Seconds, string? Error = null)
{
    /// <summary>The session plays the raw signal, past the channel's processing, instead of the processed one.</summary>
    public bool Raw { get; init; }

    /// <summary>The session puts the channel in the monitor mixes that play on the monitor outputs, whatever their mutes.</summary>
    public bool HearMyself { get; init; }

    /// <summary>The channel reaches a monitor output right now, through a send that is open.</summary>
    public bool Heard { get; init; }

    /// <summary>
    /// What hearing the channel through this computer adds over the Pro's
    /// direct path, in milliseconds: two graph quanta plus the latency the
    /// channel's plugins report. Null without a session or a quantum.
    /// </summary>
    public double? MonitorLatencyMs { get; init; }

    /// <summary>The number of the take the helper holds, counting from 1 in the session; 0 before the first.</summary>
    public int Take { get; init; }
}

/// <summary>One recorded take's waveform: the peak of each of its stretches, 0 to 1, in order.</summary>
public sealed record SoundCheckTake(string Channel, int Take, double Seconds, IReadOnlyList<double> Peaks);

public sealed partial class Mixer
{
    /// <summary>What a session refuses with when this build has no native helper.</summary>
    public const string SoundCheckNeedsHost =
        "Sound Check needs the OpenXLR native plugin host, which this installation does not include. Install a package that ships it, or build with -p:EnableNativeLv2Host=true.";

    /// <summary>The microphone channels Sound Check can run on.</summary>
    public bool IsSoundCheckChannel(string channel)
    {
        lock (_gate) return _config.Channels.Any(c => c.Id == channel && c.Microphone);
    }

    /// <summary>The channels that can key ducking (see <see cref="DuckingSetup.CanKey"/>).</summary>
    public bool IsDuckingKeyChannel(string channel)
    {
        lock (_gate) return _config.Channels.Any(c => c.Id == channel && DuckingSetup.CanKey(c));
    }

    /// <summary>The longest a session runs before it ends by itself.</summary>
    internal static readonly TimeSpan SoundCheckSessionLimit = TimeSpan.FromMinutes(10);

    private FilterHandle? _soundCheck;
    private PortLink? _soundCheckInput, _soundCheckTap;
    private string? _soundCheckChannel, _soundCheckDevice, _soundCheckError, _soundCheckErrorChannel;
    private int _soundCheckRate;
    private int? _soundCheckQuantum;
    private bool _soundCheckRaw, _soundCheckHearMyself;
    private long _soundCheckStarted;
    private SoundCheckState _lastSoundCheckState = new(null, "idle", 0);

    // The running session's helper and channel, for the meter push, which
    // reads them without the mixer's lock; and the last take it announced.
    private volatile SoundCheckProbe? _soundCheckProbe;
    private sealed record SoundCheckProbe(string Channel, NativePluginHost Host, int Rate);
    private int _announcedTake;

    /// <summary>The channel a running session replaces with its loop, or null.</summary>
    public string? SoundCheckChannel { get { lock (_gate) return _soundCheck is null ? null : _soundCheckChannel; } }

    /// <summary>
    /// Record, loop, go back to the live microphone, or stop. The sample is
    /// kept only in the helper's memory and enters the channel ahead of its
    /// software processing and inserts, so the chain can be tuned while the
    /// same take repeats. Nothing here is saved. Every action but stop
    /// starts a session when none runs on the channel; loop then refuses,
    /// having no take. A unit that runs the channel's EQ hands it to
    /// software before a session starts, so the take is raw and the loop
    /// hears every edit; it takes it back when the session ends.
    /// </summary>
    public void SoundCheck(string channel, string action)
    {
        if (action is not ("record" or "loop" or "live" or "stop"))
        {
            lock (_gate)
                if (!IsSoundCheckChannel(channel)) throw new ArgumentException("Sound Check needs a microphone channel.");
            throw new ArgumentException("Unknown Sound Check action.");
        }
        InSoundCheckSession(channel, action, null);
    }

    /// <summary>
    /// Play the raw signal past the channel's processing, or the processed
    /// one again. The helper crossfades between its two outputs, so the
    /// chain keeps running and the switch is heard at once. Starts a live
    /// session when none runs on the channel. Never saved.
    /// </summary>
    public void SetSoundCheckRaw(string channel, bool raw)
        => InSoundCheckSession(channel, "session", host =>
        {
            host.SetControl("raw", raw ? 1 : 0);
            _soundCheckRaw = raw;
        });

    /// <summary>
    /// Put the channel in the monitor mixes that play on the monitor outputs
    /// for the rest of the session, whatever its sends' mutes there, or let
    /// those mutes decide again. The channel's processed signal then reaches
    /// the headphones through this computer. Starts a live session when none
    /// runs on the channel. Never saved; the session's end turns it off.
    /// </summary>
    public void SetSoundCheckHearMyself(string channel, bool on)
        => InSoundCheckSession(channel, "session", _ =>
        {
            if (_soundCheckHearMyself == on) return;
            _soundCheckHearMyself = on;
            ReapplySoundCheckCellsLocked(channel);
        });

    private void InSoundCheckSession(string channel, string action, Action<NativePluginHost>? then)
    {
        lock (_gate)
            if (action != "stop" && MicrophonesPausedLocked)
                throw new InvalidOperationException("Resume monitoring before using Sound Check while microphones are included in the pause.");
        if (action != "stop") ReleaseDeviceEqForSoundCheck(channel);
        lock (_gate)
        {
            try
            {
                SoundCheckLocked(channel, action);
                if (then is not null && _soundCheck?.NativeHost is { } host) then(host);
            }
            finally
            {
                // The claim held the EQ in software while the unit let it go;
                // the session now does, or, when none started, the unit takes
                // the EQ back.
                if (_soundCheckClaim == channel)
                {
                    _soundCheckClaim = null;
                    if (_soundCheck is null) SyncDeviceEqLocked();
                }
            }
        }
    }

    /// <summary>One action on the session, or "session" to make sure one runs and leave its mode alone.</summary>
    private void SoundCheckLocked(string channel, string action)
    {
        if (!IsSoundCheckChannel(channel))
            throw new ArgumentException("Sound Check needs a microphone channel.");
        if (action == "stop")
        {
            // Stop only ends this microphone's session or clears its
            // notice; closing the other microphone's window leaves a
            // running session alone.
            string? owner = _soundCheckChannel ?? _soundCheckErrorChannel;
            if (owner is null || owner == channel) StopSoundCheckLocked(restore: true);
            return;
        }
        if (MicrophonesPausedLocked)
            throw new InvalidOperationException("Resume monitoring before using Sound Check while microphones are included in the pause.");
        if (_soundCheckChannel is not null && _soundCheckChannel != channel)
            throw new InvalidOperationException($"Stop Sound Check on {ChannelNameLocked(_config.Channels.Single(c => c.Id == _soundCheckChannel))} first.");
        if (_soundCheck is null && action == "loop")
            throw new InvalidOperationException("Record a sample first.");
        if (_soundCheck is null) StartSoundCheckLocked(channel);
        if (_soundCheck?.NativeHost is not { } host)
            throw new InvalidOperationException("Record a sample first.");
        if (action == "session") return;
        if (action == "loop" && SoundCheckSnapshotLocked().Seconds < 0.1)
            throw new InvalidOperationException("Record at least a tenth of a second before looping.");
        host.SetControl("command", action switch { "record" => 1, "loop" => 2, _ => 0 });
    }

    private void StartSoundCheckLocked(string channel)
    {
        // Each input records from its own source, before the loop helper or
        // any processing: the interface's pair, the port a strip is bound
        // to, or a capture input's pair or mono port. A capture input with no
        // insert has no wired route yet, so its source is looked up here. The
        // refusal is the input's own bindingWarning when it has one.
        ChannelDefinition input = _config.Channels.Single(c => c.Id == channel);
        string? device = MicrophoneSourceLocked(input);
        if (device is null)
            throw new InvalidOperationException(BindingWarningLocked(input) is { } why
                ? $"The microphone is not connected. {ChannelNameLocked(input)}: {why}"
                : $"The microphone is not connected. {ChannelNameLocked(input)}.");
        // The wait for the unit is over, confirmed or not.
        if (_soundCheckClaim == channel && (_eqReleasing.Remove(channel) | _dynamicsReleasing.Remove(channel))) ApplySoftwareEqLocked(channel);
        FilterHandle filter = _pw.CreateSoundCheck(channel, out int rate, out int? quantum);
        PortLink? feed = null;
        try
        {
            feed = RouteInputLocked(device, filter.SinkName, input);
            if (feed.Pairs.Count != 1) throw new InvalidOperationException("The microphone's capture port is unavailable.");
            _soundCheck = filter;
            _soundCheckInput = feed;
            _soundCheckChannel = channel;
            _soundCheckDevice = device;
            _soundCheckRate = rate;
            _soundCheckQuantum = quantum;
            _soundCheckRaw = _soundCheckHearMyself = false;
            _soundCheckStarted = Stopwatch.GetTimestamp();
            _soundCheckError = _soundCheckErrorChannel = null;
            _announcedTake = 0;
            // The raw path: the helper's tap straight into the channel, past
            // the chain. It carries silence until the session asks for raw.
            // Linked first, so the channel stays joined to the microphone
            // through the helper while its feed moves: a channel cut off for
            // a moment is a graph of its own, PipeWire picks a quantum for
            // each half and changes it again when they join, and on
            // PipeWire 1.0 that change left a loopback source silent.
            _soundCheckTap = _pw.LinkNodes(filter.SourceName, "tap", input.SinkName, "playback");
            if (_soundCheckTap.Pairs.Count == 0)
                throw new InvalidOperationException("Sound Check could not connect its raw path.");
            // A reused direct feed would bypass the loop, so the
            // channel's feed is rebuilt from the helper's output.
            if (_inputFeeds.Remove(channel, out PortLink? previous)) _pw.Unlink(previous);
            WireInputFeedsLocked([channel]);
            if (_soundCheck != filter)
                throw new InvalidOperationException(_soundCheckError ?? "Sound Check could not connect its audio path.");
            _soundCheckProbe = new SoundCheckProbe(channel, filter.NativeHost!, rate);
            // The monitor hears the take through the effects, not the live microphone dry.
            SyncDryRoutesLocked();
        }
        catch (Exception failure)
        {
            try
            {
                if (_soundCheck is not null) StopSoundCheckLocked(restore: true);
                else { if (feed is not null) _pw.Unlink(feed); _pw.StopFilter(filter); }
            }
            catch (Exception rollback)
            {
                throw new AggregateException("Sound Check could not start, and restoring the microphone also failed.", failure, rollback);
            }
            throw;
        }
    }

    /// <summary>
    /// The node a microphone channel takes its audio from, before the loop
    /// helper or any processing, or null while it has none: the interface,
    /// the source a strip is bound to, or a capture input's source.
    /// </summary>
    private string? MicrophoneSourceLocked(ChannelDefinition input)
        => !_built ? null
            : input.CaptureSource is not null ? ResolveCaptureSourceLocked(input, _pw.ListDevices())
            : BindingConnectedLocked(input) ? _stripSources.GetValueOrDefault(input.Id) : null;

    private SoundCheckState SoundCheckSnapshotLocked()
    {
        if (_soundCheck?.NativeHost is not { } host) return new(_soundCheckErrorChannel, "idle", 0, _soundCheckError);
        var meters = host.Meters;
        double seconds = _soundCheckRate > 0 ? Math.Clamp(meters.GetValueOrDefault("frames") / _soundCheckRate, 0, 10) : 0;
        string mode = meters.GetValueOrDefault("mode") switch { 1 => "recording", 2 => "looping", _ => "live" };
        string channel = _soundCheckChannel!;
        return new(channel, mode, seconds, _soundCheckError)
        {
            Raw = _soundCheckRaw,
            HearMyself = _soundCheckHearMyself,
            Heard = HeardLocked(channel),
            MonitorLatencyMs = _soundCheckQuantum is int quantum && _soundCheckRate > 0
                ? Math.Round(2000.0 * quantum / _soundCheckRate
                    + MixLatency.Total(InsertsFor(channel).Select(i => InsertLatencyLocked(channel, i))), 1)
                : null,
            Take = (int)Math.Clamp(meters.GetValueOrDefault("take"), 0, int.MaxValue),
        };
    }

    /// <summary>
    /// The take the running session's helper holds, once it is complete, or
    /// null without one. Read without the mixer's lock: the meter push asks
    /// at its own rate.
    /// </summary>
    public SoundCheckTake? CurrentSoundCheckTake()
    {
        if (_soundCheckProbe is not { } probe) return null;
        IReadOnlyDictionary<string, double> meters = probe.Host.Meters;
        // Kept once read, so its length is the one it ended with, not that of
        // a recording made since.
        if (_takeCache is { } cached && ReferenceEquals(cached.Probe, probe) && cached.Take.Take == (int)meters.GetValueOrDefault("take"))
            return cached.Take;
        if (TakeFrom(probe.Channel, meters, probe.Rate) is not { } take) return null;
        _takeCache = new TakeEntry(probe, take);
        return take;
    }

    private sealed record TakeEntry(SoundCheckProbe Probe, SoundCheckTake Take);
    private volatile TakeEntry? _takeCache;

    /// <summary>
    /// The take, when the helper has finished one that has not been handed
    /// out yet; null otherwise. Each take is handed out once.
    /// </summary>
    public SoundCheckTake? NewSoundCheckTake()
    {
        if (CurrentSoundCheckTake() is not { } take) return null;
        int previous = Volatile.Read(ref _announcedTake);
        if (take.Take == previous || Interlocked.CompareExchange(ref _announcedTake, take.Take, previous) != previous) return null;
        return take;
    }

    /// <summary>
    /// A take out of a helper's meters, or null before its first. The helper
    /// prints the peaks before the count, so a count read here comes with its
    /// peaks already stored.
    /// </summary>
    internal static SoundCheckTake? TakeFrom(string channel, IReadOnlyDictionary<string, double> meters, int rate)
    {
        int take = (int)Math.Clamp(meters.GetValueOrDefault("take"), 0, int.MaxValue);
        if (take == 0 || rate <= 0) return null;
        var peaks = new double[PipeWireAdapter.TakePeakCount];
        for (int i = 0; i < peaks.Length; i++)
            peaks[i] = Math.Round(Math.Clamp(meters.GetValueOrDefault($"p{i}"), 0, 1), 3);
        return new SoundCheckTake(channel, take, Math.Clamp(meters.GetValueOrDefault("takeFrames", meters.GetValueOrDefault("frames")) / rate, 0, 10), peaks);
    }

    /// <summary>
    /// Where the loop is, as a share of the take from 0 to 1, for the meter
    /// push: the key is <c>take:</c> and the channel. Nothing while the
    /// session is not looping.
    /// </summary>
    private void AddSoundCheckPosition(Dictionary<string, double[]> meters)
    {
        if (_soundCheckProbe is not { } probe || !probe.Host.IsRunning) return;
        IReadOnlyDictionary<string, double> values = probe.Host.Meters;
        double frames = values.GetValueOrDefault("frames");
        if (values.GetValueOrDefault("mode") != 2 || frames <= 0) return;
        meters[$"take:{probe.Channel}"] = [Math.Round(Math.Clamp(values.GetValueOrDefault("cursor") / frames, 0, 1), 3)];
    }

    /// <summary>
    /// The monitor mixes the monitor outputs play: each selected output's
    /// feed, or the default monitor mix while none is selected.
    /// </summary>
    private HashSet<string> HeadphoneMixesLocked()
    {
        var mixes = new HashSet<string>(StringComparer.Ordinal);
        if (_monitorOutputs.Count == 0)
        {
            if (PrimaryMonitorLocked() is { } primary) mixes.Add(primary.Id);
        }
        else foreach (string output in _monitorOutputs)
            foreach (MixDefinition mix in MixesForOutputLocked(output)) mixes.Add(mix.Id);
        mixes.RemoveWhere(id => MonitorMixLocked(id) is null);
        return mixes;
    }

    /// <summary>Hear myself holds this send open: the session runs on the channel with it on, and the mix plays on the monitor outputs.</summary>
    private bool HearsMyselfLocked(string channelId, string mixId)
        => _soundCheckHearMyself && _soundCheck is not null && _soundCheckChannel == channelId
            && MonitorMixLocked(mixId) is not null && HeadphoneMixesLocked().Contains(mixId);

    /// <summary>The channel reaches a selected monitor output through an open send.</summary>
    private bool HeardLocked(string channel)
    {
        if (_monitorMuted || _monitorOutputs.Count == 0) return false;
        foreach (string mix in HeadphoneMixesLocked())
        {
            if (!_cells.Contains(Cell(channel, mix))) continue;
            (double level, bool muted) = CellTargetLocked(channel, mix, out _);
            if (!muted && level > 0) return true;
        }
        return false;
    }

    /// <summary>Push the session channel's monitor sends again after Hear myself changed.</summary>
    private void ReapplySoundCheckCellsLocked(string channel)
    {
        if (!_built) return;
        foreach (MixDefinition mix in _config.Mixes.Where(m => m.Kind == MixKind.Monitor))
            if (_cells.Contains(Cell(channel, mix.Id))) ApplyCellLocked(channel, mix.Id);
    }

    /// <summary>
    /// End the session and drop the sample. An error stays on the channel the
    /// session ran on until an explicit stop or a new recording clears it.
    /// </summary>
    private void StopSoundCheckLocked(bool restore, string? error = null)
    {
        string? channel = _soundCheckChannel;
        bool heard = _soundCheckHearMyself;
        _soundCheckError = error;
        _soundCheckErrorChannel = error is null ? null : channel;
        _soundCheckProbe = null;
        PortLink? tap = _soundCheckTap, input = _soundCheckInput;
        FilterHandle? helper = _soundCheck;
        _soundCheck = null;
        _soundCheckInput = _soundCheckTap = null;
        _soundCheckChannel = _soundCheckDevice = null;
        _soundCheckRaw = _soundCheckHearMyself = false;
        _soundCheckQuantum = null;
        // Hear myself held the channel's monitor sends open; they follow
        // their own mutes again.
        if (heard && channel is not null) ReapplySoundCheckCellsLocked(channel);
        // The unit takes the EQ back before the strip is wired again, so the
        // route is built without a software stage.
        if (restore) SyncDeviceEqLocked();
        if (channel is not null && _inputFeeds.Remove(channel, out PortLink? feed)) _pw.Unlink(feed);
        // The direct feed comes back while the helper, its input and its tap
        // still join the channel to the microphone, so the graph never
        // splits (see the start).
        try { if (restore && channel is not null && _built) WireInputFeedsLocked([channel]); }
        finally
        {
            if (tap is not null) _pw.Unlink(tap);
            if (input is not null) _pw.Unlink(input);
            if (helper is not null) _pw.StopFilter(helper);
        }
        // A microphone heard dry gets its route back; the sweep does it after a stop without a restore.
        if (restore && channel is not null && _built) SyncDryRoutesLocked();
    }

    /// <summary>
    /// The sweep's part: report progress, and end a session whose helper or
    /// input link is gone, or that has run past its limit. A raw path that
    /// broke is linked again. True when what clients see changed.
    /// </summary>
    private bool EnsureSoundCheckLocked()
    {
        SoundCheckState state = SoundCheckSnapshotLocked();
        bool changed = state != _lastSoundCheckState;
        _lastSoundCheckState = state;
        if (_soundCheck is null) return changed;
        bool failed = !_soundCheck.IsAlive || _soundCheckInput is null || _pw.EnsureLinks(_soundCheckInput) == LinkHealth.Broken;
        bool expired = Stopwatch.GetElapsedTime(_soundCheckStarted) >= SoundCheckSessionLimit;
        if (!failed && !expired)
        {
            if (_soundCheckTap is null || _pw.EnsureLinks(_soundCheckTap) == LinkHealth.Broken)
            {
                ChannelDefinition? input = _config.Channels.FirstOrDefault(c => c.Id == _soundCheckChannel);
                if (_soundCheckTap is not null) _pw.Unlink(_soundCheckTap);
                _soundCheckTap = input is null ? null : _pw.LinkNodes(_soundCheck.SourceName, "tap", input.SinkName, "playback");
            }
            return changed;
        }
        string? words = failed ? _soundCheck.LastWords : null;
        string error = failed ? "Sound Check stopped because its audio path was lost." : "Sound Check ended after ten minutes.";
        if (words is not null) error += $" The plugin host's last message: {words}";
        if (failed) _pw.Note($"Sound Check on {_soundCheckChannel} stopped{(words is null ? "." : $". The plugin host's last message: {words}")}");
        StopSoundCheckLocked(restore: true, error: error);
        return true;
    }
}
