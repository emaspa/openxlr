using System.Diagnostics;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// Sound Check as clients see it: the microphone channel a session belongs
/// to (null when idle), what the loop helper is doing, how much it holds,
/// and why the last session ended when it ended on its own.
/// </summary>
public sealed record SoundCheckState(string? Channel, string Mode, double Seconds, string? Error = null);

public sealed partial class Mixer
{
    /// <summary>What a session refuses with when this build has no native helper.</summary>
    public const string SoundCheckNeedsHost =
        "Sound Check needs the OpenXLR native plugin host, which this installation does not include. Install a package that ships it, or build with -p:EnableNativeLv2Host=true.";

    /// <summary>The microphone channels Sound Check can run on.</summary>
    public static bool IsSoundCheckChannel(string? channel) => channel is "xlr1" or "xlr2";

    /// <summary>The longest a session runs before it ends by itself.</summary>
    internal static readonly TimeSpan SoundCheckSessionLimit = TimeSpan.FromMinutes(10);

    private FilterHandle? _soundCheck;
    private PortLink? _soundCheckInput;
    private string? _soundCheckChannel, _soundCheckDevice, _soundCheckError, _soundCheckErrorChannel;
    private int _soundCheckRate;
    private long _soundCheckStarted;
    private SoundCheckState _lastSoundCheckState = new(null, "idle", 0);

    /// <summary>The channel a running session replaces with its loop, or null.</summary>
    public string? SoundCheckChannel { get { lock (_gate) return _soundCheck is null ? null : _soundCheckChannel; } }

    /// <summary>
    /// Record, loop, go back to the live microphone, or stop. The sample is
    /// kept only in the helper's memory and enters the channel ahead of its
    /// software processing and inserts, so the chain can be tuned while the
    /// same take repeats. Nothing here is saved.
    /// </summary>
    public void SoundCheck(string channel, string action)
    {
        lock (_gate)
        {
            if (!IsSoundCheckChannel(channel) || !HasChannel(channel))
                throw new ArgumentException("Sound Check needs the XLR 1 or XLR 2 channel.");
            if (action is not ("record" or "loop" or "live" or "stop"))
                throw new ArgumentException("Unknown Sound Check action.");
            if (action == "stop")
            {
                // Stop only ends this microphone's session or clears its
                // notice; closing the other microphone's window leaves a
                // running session alone.
                string? owner = _soundCheckChannel ?? _soundCheckErrorChannel;
                if (owner is null || owner == channel) StopSoundCheckLocked(restore: true);
                return;
            }
            if (_soundCheckChannel is not null && _soundCheckChannel != channel)
                throw new InvalidOperationException("Stop Sound Check on the other microphone first.");
            if (!_built || _inputDevice is null)
                throw new InvalidOperationException("The microphone is not connected.");
            if (action == "record" && _soundCheck is null)
            {
                ChannelDefinition input = _config.Channels.Single(c => c.Id == channel);
                FilterHandle filter = _pw.CreateSoundCheck(channel, out int rate);
                PortLink? feed = null;
                try
                {
                    feed = _pw.RouteInputToChannel(_inputDevice, filter.SinkName, input.InputPair!.Value);
                    if (feed.Pairs.Count != 1) throw new InvalidOperationException("The microphone's capture port is unavailable.");
                    _soundCheck = filter;
                    _soundCheckInput = feed;
                    _soundCheckChannel = channel;
                    _soundCheckDevice = _inputDevice;
                    _soundCheckRate = rate;
                    _soundCheckStarted = Stopwatch.GetTimestamp();
                    _soundCheckError = _soundCheckErrorChannel = null;
                    // A reused direct feed would bypass the loop, so the
                    // channel's feed is rebuilt from the helper's output.
                    if (_inputFeeds.Remove(channel, out PortLink? previous)) _pw.Unlink(previous);
                    WireInputFeedsLocked();
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
            if (_soundCheck?.NativeHost is not { } host)
                throw new InvalidOperationException("Record a sample first.");
            if (action == "loop" && SoundCheckSnapshotLocked().Seconds < 0.1)
                throw new InvalidOperationException("Record at least a tenth of a second before looping.");
            host.SetControl("command", action switch { "record" => 1, "loop" => 2, _ => 0 });
        }
    }

    private SoundCheckState SoundCheckSnapshotLocked()
    {
        if (_soundCheck?.NativeHost is not { } host) return new(_soundCheckErrorChannel, "idle", 0, _soundCheckError);
        var meters = host.Meters;
        double seconds = _soundCheckRate > 0 ? Math.Clamp(meters.GetValueOrDefault("frames") / _soundCheckRate, 0, 10) : 0;
        string mode = meters.GetValueOrDefault("mode") switch { 1 => "recording", 2 => "looping", _ => "live" };
        return new(_soundCheckChannel, mode, seconds, _soundCheckError);
    }

    /// <summary>
    /// End the session and drop the sample. An error stays on the channel the
    /// session ran on until an explicit stop or a new recording clears it.
    /// </summary>
    private void StopSoundCheckLocked(bool restore, string? error = null)
    {
        string? channel = _soundCheckChannel;
        _soundCheckError = error;
        _soundCheckErrorChannel = error is null ? null : channel;
        if (_soundCheckInput is not null) _pw.Unlink(_soundCheckInput);
        if (_soundCheck is not null) _pw.StopFilter(_soundCheck);
        _soundCheck = null;
        _soundCheckInput = null;
        _soundCheckChannel = _soundCheckDevice = null;
        if (channel is not null && _inputFeeds.Remove(channel, out PortLink? feed)) _pw.Unlink(feed);
        if (restore && channel is not null && _built) WireInputFeedsLocked();
    }

    /// <summary>
    /// The sweep's part: report progress, and end a session whose helper or
    /// input link is gone, or that has run past its limit. True when what
    /// clients see changed.
    /// </summary>
    private bool EnsureSoundCheckLocked()
    {
        SoundCheckState state = SoundCheckSnapshotLocked();
        bool changed = state != _lastSoundCheckState;
        _lastSoundCheckState = state;
        if (_soundCheck is null) return changed;
        bool failed = !_soundCheck.IsAlive || _soundCheckInput is null || _pw.EnsureLinks(_soundCheckInput) == LinkHealth.Broken;
        bool expired = Stopwatch.GetElapsedTime(_soundCheckStarted) >= SoundCheckSessionLimit;
        if (!failed && !expired) return changed;
        StopSoundCheckLocked(restore: true, error: failed ? "Sound Check stopped because its audio path was lost." : "Sound Check ended after ten minutes.");
        return true;
    }
}
