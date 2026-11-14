namespace OpenXLR.Core.Mixing;

/// <summary>
/// A capture input's own clock as the state reports it (<c>ownClock</c>).
/// </summary>
/// <param name="On">The input is set to run on its own clock.</param>
/// <param name="Running">The bridge is carrying audio: the device's frames reach the mixer.</param>
/// <param name="DelayMs">What the bridge holds, in milliseconds: the delay it adds. Null while it is not running.</param>
/// <param name="TargetMs">The delay the bridge steers to, null before the device's first cycle.</param>
/// <param name="Underruns">Times the bridge ran dry and played silence until it filled again.</param>
/// <param name="Overruns">Times it held too much and dropped back to its target.</param>
/// <param name="DriftPpm">How far the device's clock runs from the mixer's, in parts per million, as the bridge measures it.</param>
/// <param name="Error">Why the bridge does not run, or null.</param>
public sealed record CaptureClockStatus(bool On, bool Running, double? DelayMs, double? TargetMs, long Underruns, long Overruns,
    double? DriftPpm, string? Error);

/// <summary>
/// The own clock of a capture input. A device that misbehaves while it
/// follows another device's clock (issue #474: a capture card whose USB
/// controller faults as a resampled follower) can keep a clock of its own:
/// the native helper's bridge puts one node in the device's graph and one in
/// the mixer's, and carries the audio between them through a ring with its
/// own drift correction (native/bridge.c). PipeWire puts linked nodes in one
/// graph, so the device is linked only to the bridge, never to the mixer.
/// </summary>
public sealed partial class Mixer
{
    internal const string OwnClockNeedsHost =
        "The own clock needs the OpenXLR native plugin host, which this installation does not include. Install a package that ships it, or build with -p:EnableNativeLv2Host=true.";

    /// <summary>How long a bridge that failed to start waits before the next attempt.</summary>
    internal TimeSpan ClockBridgeRetry { get; init; } = TimeSpan.FromSeconds(10);

    // One bridge per capture input on its own clock: the helper, its width,
    // and the device ports linked into it.
    private sealed class ClockBridge(FilterHandle handle, int width)
    {
        public FilterHandle Handle { get; } = handle;
        public int Width { get; } = width;
        public string? Device { get; set; }
        public int Pair { get; set; }
        public int? Mono { get; set; }
        public PortLink? Feed { get; set; }
    }

    private readonly Dictionary<string, ClockBridge> _clockBridges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Message, long At)> _clockBridgeErrors = new(StringComparer.Ordinal);
    private long _clockBridgeGeneration;
    private string _clockBridgeSummary = "";

    /// <summary>
    /// Put a capture input on its own clock, or back on the mixer's. Saved
    /// before the graph changes; a refused save changes nothing. The input's
    /// feed is rewired at once: on, the device is linked to its bridge and
    /// the channel to the bridge's output; off, the device is linked to the
    /// channel again and the bridge stops.
    /// </summary>
    public void SetCaptureOwnClock(string channel, bool on, Func<MixerSettings, string?> save)
    {
        ArgumentNullException.ThrowIfNull(save);
        lock (_gate)
        {
            ChannelDefinition previous = _config.Channels.FirstOrDefault(c => c.Id == channel && c.CaptureSource is not null)
                ?? throw new ArgumentException($"'{channel}' is not a capture input.");
            if (previous.CaptureOwnClock == on) { PersistLocked(save); return; }
            ChannelDefinition next = previous with { CaptureOwnClock = on };
            MixerConfig before = _config;
            _config = _config with { Channels = [.. _config.Channels.Select(c => c.Id == channel ? next : c)] };
            try { PersistLocked(save); }
            catch { _config = before; throw; }
            RewireOwnClockLocked(next);
        }
    }

    /// <summary>
    /// Rewire a capture input whose own clock just changed in the layout:
    /// the device to its bridge or back to the channel.
    /// </summary>
    private void RewireOwnClockLocked(ChannelDefinition next)
    {
        _clockBridgeErrors.Remove(next.Id);
        if (!_built) return;
        // Sound Check holds the old path; it ends rather than play from a
        // node that is about to change.
        if (_soundCheckChannel == next.Id)
            StopSoundCheckLocked(restore: false, error: "Sound Check stopped because the capture input's clock changed.");
        RebindCaptureFeedLocked(next);
        if (!next.CaptureOwnClock) RemoveClockBridgeLocked(next.Id);
    }

    /// <summary>The own clock a profile keeps: every capture input, on or off.</summary>
    private Dictionary<string, bool> SceneCaptureOwnClockLocked()
        => _config.Channels.Where(c => c.CaptureSource is not null)
            .ToDictionary(c => c.Id, c => c.CaptureOwnClock, StringComparer.Ordinal);

    /// <summary>
    /// Recall a profile's own clocks. Each capture input the profile names
    /// takes its value and is rewired when it changes; an input it does not
    /// name, or a channel that is no longer a capture input, is left alone.
    /// The daemon saves the layout after a recall, so the value stays.
    /// </summary>
    private void RecallCaptureOwnClockLocked(IReadOnlyDictionary<string, bool>? recalled)
    {
        if (recalled is null) return;
        foreach ((string channel, bool on) in recalled)
        {
            ChannelDefinition? previous = _config.Channels.FirstOrDefault(c => c.Id == channel && c.CaptureSource is not null);
            if (previous is null || previous.CaptureOwnClock == on) continue;
            ChannelDefinition next = previous with { CaptureOwnClock = on };
            _config = _config with { Channels = [.. _config.Channels.Select(c => c.Id == channel ? next : c)] };
            RewireOwnClockLocked(next);
        }
    }

    /// <summary>Whether <paramref name="node"/> is the output of this channel's bridge.</summary>
    private bool IsClockBridgeLocked(string channel, string? node)
        => node is not null && _clockBridges.TryGetValue(channel, out ClockBridge? bridge) && bridge.Handle.SourceName == node;

    /// <summary>
    /// The bridge's output for a capture input on its own clock, with the
    /// bridge started and <paramref name="device"/> linked into it, or null
    /// with the reason noted. Never the device itself: linking the device
    /// to the mixer would put it back on the mixer's clock.
    /// </summary>
    private string? BridgedSourceLocked(ChannelDefinition channel, string? device)
    {
        string id = channel.Id;
        _clockBridges.TryGetValue(id, out ClockBridge? bridge);
        if (device is null)
        {
            // The device is gone; the bridge waits for it with silence.
            if (bridge?.Feed is { } stale) _pw.Unlink(stale);
            if (bridge is not null) { bridge.Feed = null; bridge.Device = null; }
            return null;
        }
        int width = channel.ChainWidth ?? 2;
        if (bridge is null || !bridge.Handle.IsAlive || bridge.Width != width)
        {
            if (bridge is not null) RemoveClockBridgeLocked(id);
            if (_clockBridgeErrors.TryGetValue(id, out var failed) && Clock() - failed.At < (long)ClockBridgeRetry.TotalMilliseconds)
            {
                NoteCaptureLocked(id, failed.Message);
                return null;
            }
            try
            {
                FilterHandle handle = _pw.CreateClockBridge($"{id}_{++_clockBridgeGeneration}", width);
                bridge = _clockBridges[id] = new ClockBridge(handle, width);
                _clockBridgeErrors.Remove(id);
            }
            catch (InvalidOperationException ex)
            {
                string message = $"The own clock could not start, so the channel stays silent: {ex.Message}";
                _clockBridgeErrors[id] = (message, Clock());
                _pw.Note($"Capture input {id}: {message}");
                NoteCaptureLocked(id, message);
                return null;
            }
        }
        if (bridge.Feed is null || bridge.Device != device || bridge.Pair != channel.CapturePair
            || bridge.Mono != channel.CaptureMonoChannel || _pw.EnsureLinks(bridge.Feed) == LinkHealth.Broken)
        {
            if (bridge.Feed is { } old) _pw.Unlink(old);
            bridge.Feed = null;
            PortLink feed = _pw.LinkNodes(device, "", bridge.Handle.SinkName, "playback",
                fromPairOffset: channel.CapturePair, fromChannel: channel.CaptureMonoChannel);
            if (!feed.Complete || feed.Pairs.Count < width)
            {
                _pw.Unlink(feed);
                NoteCaptureLocked(id, $"{channel.CaptureSource} could not be linked to its own clock; retrying.");
                return null;
            }
            bridge.Feed = feed;
            bridge.Device = device;
            bridge.Pair = channel.CapturePair;
            bridge.Mono = channel.CaptureMonoChannel;
        }
        return bridge.Handle.SourceName;
    }

    private void RemoveClockBridgeLocked(string id)
    {
        if (!_clockBridges.Remove(id, out ClockBridge? bridge)) return;
        if (bridge.Feed is { } feed) _pw.Unlink(feed);
        _pw.StopFilter(bridge.Handle);
    }

    private void RemoveClockBridgesLocked()
    {
        foreach (string id in _clockBridges.Keys.ToArray()) RemoveClockBridgeLocked(id);
        _clockBridgeSummary = "";
    }

    /// <summary>The device each running bridge takes, by channel, for the clock readout.</summary>
    private IEnumerable<(string Channel, string Device)> BridgedDevicesLocked()
        => _clockBridges.Where(b => b.Value.Device is not null && b.Value.Feed is not null).Select(b => (b.Key, b.Value.Device!));

    /// <summary>What a capture input's own clock reports in the state.</summary>
    private CaptureClockStatus CaptureClockStateLocked(ChannelDefinition channel)
    {
        string? error = _clockBridgeErrors.TryGetValue(channel.Id, out var failed) ? failed.Message : null;
        if (!channel.CaptureOwnClock || !_clockBridges.TryGetValue(channel.Id, out ClockBridge? bridge)
            || bridge.Handle.NativeHost is not { } host)
            return new(channel.CaptureOwnClock, false, null, null, 0, 0, null, channel.CaptureOwnClock ? error : null);
        IReadOnlyDictionary<string, double> meters = host.Meters;
        int rate = host.SampleRate;
        bool running = meters.GetValueOrDefault("running") == 1 && bridge.Feed is not null;
        double? Milliseconds(string meter) => meters.TryGetValue(meter, out double frames) && frames > 0 && rate > 0
            ? Math.Round(frames * 1000.0 / rate, 1) : null;
        return new(true, running, running ? Milliseconds("delay") : null, Milliseconds("target"),
            (long)meters.GetValueOrDefault("underruns"), (long)meters.GetValueOrDefault("overruns"),
            running && meters.TryGetValue("drift", out double drift) ? drift : null, error);
    }

    /// <summary>
    /// Whether a bridge's state changed enough to tell the clients: it
    /// started or stopped carrying audio, counted an underrun or an overrun,
    /// or its delay moved by a whole millisecond. Its drift alone is not
    /// news; it goes out with the next state.
    /// </summary>
    public bool SyncClockBridges()
    {
        lock (_gate)
        {
            if (!_built) return false;
            // A bridge whose input went away or left its own clock stops.
            foreach (string id in _clockBridges.Keys.Where(id => !_config.Channels.Any(c => c.Id == id && c.CaptureSource is not null
                && c.CaptureOwnClock)).ToArray())
                RemoveClockBridgeLocked(id);
            var states = _config.Channels.Where(c => c.CaptureSource is not null && c.CaptureOwnClock)
                .Select(c => (Channel: c, State: CaptureClockStateLocked(c))).ToList();
            string summary = string.Join("\n", states
                .Select(s => $"{s.Channel.Id}|{s.State.Running}|{s.State.Underruns}|{s.State.Overruns}|{Math.Round(s.State.DelayMs ?? -1)}|{s.State.Error}"));
            if (summary == _clockBridgeSummary) return false;
            NoteBridgeRunsLocked(states);
            _clockBridgeSummary = summary;
            return true;
        }
    }

    // The underruns and overruns each bridge had counted when last looked at.
    private readonly Dictionary<string, (long Underruns, long Overruns)> _clockBridgeRuns = new(StringComparer.Ordinal);

    /// <summary>
    /// A bridge that counted another underrun or overrun since the last look
    /// goes on the daemon's timeline: it is the one place the mixer itself
    /// sees audio dropped. A bridge started afresh counts from zero again.
    /// </summary>
    private void NoteBridgeRunsLocked(IReadOnlyList<(ChannelDefinition Channel, CaptureClockStatus State)> states)
    {
        foreach (string id in _clockBridgeRuns.Keys.Where(id => !states.Any(s => s.Channel.Id == id)).ToArray())
            _clockBridgeRuns.Remove(id);
        foreach ((ChannelDefinition channel, CaptureClockStatus state) in states)
        {
            (long Underruns, long Overruns) now = (state.Underruns, state.Overruns);
            if (_clockBridgeRuns.TryGetValue(channel.Id, out var before)
                && (now.Underruns > before.Underruns || now.Overruns > before.Overruns)
                && now.Underruns >= before.Underruns && now.Overruns >= before.Overruns)
                _pw.Event(TimelineKind.Clock, $"{channel.Name}'s own clock bridge counted "
                    + $"{now.Underruns - before.Underruns} underrun(s) and {now.Overruns - before.Overruns} overrun(s)");
            _clockBridgeRuns[channel.Id] = now;
        }
    }
}
