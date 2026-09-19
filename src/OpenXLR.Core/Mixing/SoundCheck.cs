using System.Diagnostics;

namespace OpenXLR.Core.Mixing;

public sealed record SoundCheckState(string? Channel, string Mode, double Seconds, string? Error = null);

public sealed partial class Mixer
{
    private FilterHandle? _soundCheck;
    private PortLink? _soundCheckInput;
    private string? _soundCheckChannel, _soundCheckDevice, _soundCheckError;
    private int _soundCheckRate;
    private long _soundCheckStarted;
    private SoundCheckState? _lastSoundCheckState;

    /// <summary>A short dry recording, kept only in the isolated helper's memory.</summary>
    public void SoundCheck(string channel, string action)
    {
        lock (_gate)
        {
            if (channel is not ("xlr1" or "xlr2") || !HasChannel(channel))
                throw new ArgumentException("Sound Check needs an XLR microphone channel.");
            if (action is not ("record" or "loop" or "live" or "stop"))
                throw new ArgumentException("Unknown Sound Check action.");
            if (_soundCheckChannel is not null && _soundCheckChannel != channel)
                throw new InvalidOperationException("Stop Sound Check on the other microphone first.");
            if (action == "stop") { StopSoundCheckLocked(restore: true); return; }
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
                    _soundCheckError = null;
                    // A reused direct feed would bypass the loop. Rewire from
                    // the helper before recording; built-in safety DSP remains downstream.
                    if (_inputFeeds.Remove(channel, out PortLink? previous)) _pw.Unlink(previous);
                    WireInputFeedsLocked();
                }
                catch
                {
                    if (_soundCheck is not null) StopSoundCheckLocked(restore: true);
                    else { if (feed is not null) _pw.Unlink(feed); _pw.StopFilter(filter); }
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
        if (_soundCheck?.NativeHost is not { } host) return new(null, "idle", 0, _soundCheckError);
        var meters = host.Meters;
        double seconds = _soundCheckRate > 0 ? Math.Clamp(meters.GetValueOrDefault("frames") / _soundCheckRate, 0, 10) : 0;
        string mode = meters.GetValueOrDefault("mode") switch { 1 => "recording", 2 => "looping", _ => "live" };
        return new(_soundCheckChannel, mode, seconds, _soundCheckError);
    }

    private void StopSoundCheckLocked(bool restore)
    {
        string? channel = _soundCheckChannel;
        if (_soundCheckInput is not null) _pw.Unlink(_soundCheckInput);
        if (_soundCheck is not null) _pw.StopFilter(_soundCheck);
        _soundCheck = null;
        _soundCheckInput = null;
        _soundCheckChannel = _soundCheckDevice = null;
        if (channel is not null && _inputFeeds.Remove(channel, out PortLink? feed)) _pw.Unlink(feed);
        if (restore && channel is not null && _built) WireInputFeedsLocked();
    }

    private bool EnsureSoundCheckLocked()
    {
        SoundCheckState state = SoundCheckSnapshotLocked();
        bool changed = state != _lastSoundCheckState;
        _lastSoundCheckState = state;
        if (_soundCheck is null) return changed;
        bool failed = !_soundCheck.IsAlive || _soundCheckInput is null || _pw.EnsureLinks(_soundCheckInput) == LinkHealth.Broken;
        bool expired = Stopwatch.GetElapsedTime(_soundCheckStarted) >= TimeSpan.FromMinutes(10);
        if (!failed && !expired) return changed;
        _soundCheckError = failed ? "Sound Check stopped because its audio path was lost." : "Sound Check ended after ten minutes.";
        StopSoundCheckLocked(restore: true);
        return true;
    }
}
