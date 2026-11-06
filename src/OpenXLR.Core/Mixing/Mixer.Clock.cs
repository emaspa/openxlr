using System.Text.Json;

namespace OpenXLR.Core.Mixing;

public sealed partial record MixerState
{
    /// <summary>The node that drives the mixer's graph and the mixer's devices that follow it; null until a read shows one.</summary>
    public MixerClock? Clock { get; init; }

    /// <summary>
    /// The audio clock the user picked and whether it waits for WirePlumber
    /// to restart; set by the daemon, which owns the rule, and absent where
    /// it cannot write one (the Flatpak).
    /// </summary>
    public AudioClockChoice? ClockChoice { get; init; }
}

public sealed partial class Mixer
{
    // The clock as the last read found it, the registry revision and the
    // device list it was asked for, and when the next read is due.
    private MixerClock? _clock;
    private long? _clockRevision;
    private string _clockDevices = "";
    private long? _clockDueAt;
    private long _clockReadAt = long.MinValue / 2;

    /// <summary>How long the graph has to stay as it is before the clock is read again.</summary>
    internal TimeSpan ClockSettle { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>The shortest time between two reads while the graph keeps changing.</summary>
    internal TimeSpan ClockMinGap { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Read the clock of the mixer's graph again once the graph has settled
    /// after a change; true when what the state reports changed.
    ///
    /// The registry subscription says when something that can move the
    /// clock changed (a node, its state, a link) but not which driver a node
    /// follows now, which PipeWire does not send to subscribers. So a change
    /// schedules one pw-dump, <see cref="ClockSettle"/> after the last change
    /// and at most once per <see cref="ClockMinGap"/>. A graph that does not
    /// change starts no helper: each sweep compares one revision and one
    /// short string. Without a subscription there is no clock.
    /// </summary>
    public bool SyncClock()
    {
        IReadOnlyList<ClockCandidate> devices;
        string signature;
        long now = Environment.TickCount64;
        lock (_gate)
        {
            if (!_built) return false;
            devices = ClockCandidatesLocked();
            signature = string.Join("\n", devices.Select(d => $"{d.Node}|{d.Role}|{d.Microphone}|{string.Join(',', d.Channels)}"));
            long? revision = _pw.ClockRevision;
            if (revision is null) return false;
            if (revision != _clockRevision || signature != _clockDevices)
            {
                _clockRevision = revision;
                _clockDevices = signature;
                _clockDueAt = now + (long)ClockSettle.TotalMilliseconds;
            }
            if (_clockDueAt is not long due || now < due || now - _clockReadAt < (long)ClockMinGap.TotalMilliseconds)
                return false;
            _clockDueAt = null;
            _clockReadAt = now;
        }
        JsonElement[] objects;
        try { objects = _pw.ReadGraphOnce(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // pw-dump failed or timed out: try again after the gap.
            lock (_gate) _clockDueAt ??= now;
            return false;
        }
        if (objects.Length == 0) return false;
        MixerClock? clock = MixerClock.Read(objects, devices);
        lock (_gate)
        {
            if (Equals(clock, _clock)) return false;
            _clock = clock;
            return true;
        }
    }

    /// <summary>The driven unit's capture node, once the strips found it.</summary>
    public string? UnitInputNode { get { lock (_gate) return _inputDevice; } }

    /// <summary>The clock as the last read found it.</summary>
    public MixerClock? GraphClock { get { lock (_gate) return _clock; } }

    /// <summary>
    /// The mixer's devices, by node name: each strip's source while it is
    /// linked (the driven unit's capture, a capture input's source, a bound
    /// strip's source), each selected output and the Aux port's sink.
    /// </summary>
    private List<ClockCandidate> ClockCandidatesLocked()
    {
        var channels = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var microphone = new HashSet<string>(StringComparer.Ordinal);
        foreach (ChannelDefinition channel in _config.Channels)
        {
            // A hardware or bound strip's source, or the node a capture
            // input's feed comes from, directly or into its chain.
            string? source = _stripSources.GetValueOrDefault(channel.Id);
            if (source is null && channel.CaptureSource is not null
                && (_captureFeeds.GetValueOrDefault(channel.Id) ?? _inputFeeds.GetValueOrDefault(channel.Id)) is { Pairs: [var first, ..] }
                && first.From.LastIndexOf(':') is int colon and > 0)
                source = first.From[..colon];
            if (source is null) continue;
            if (!channels.TryGetValue(source, out List<string>? strips)) channels[source] = strips = [];
            strips.Add(channel.Id);
            // Only the microphone strips warn: an extra mic on a capture input
            // follows the unit's clock in a normal setup and is shown, not flagged.
            if (channel.Id is "xlr1" or "xlr2" && CarriesEq(channel)) microphone.Add(source);
        }
        var devices = new List<ClockCandidate>();
        foreach ((string source, List<string> strips) in channels)
            devices.Add(new ClockCandidate(source, source == _inputDevice ? "unitInput" : "capture", strips, microphone.Contains(source)));
        var outputs = new HashSet<string>(StringComparer.Ordinal);
        foreach (string output in _monitorOutputs.Append(_auxTargetSink ?? ""))
        {
            int marker = output.IndexOf('#');
            string node = marker < 0 ? output : output[..marker];
            if (node.Length == 0 || channels.ContainsKey(node) || !outputs.Add(node)) continue;
            devices.Add(new ClockCandidate(node, IsDrivenUnitSinkLocked(node) ? "unitOutput" : "output", [], false));
        }
        return devices;
    }
}
