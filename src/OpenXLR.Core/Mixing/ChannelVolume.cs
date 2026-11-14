namespace OpenXLR.Core.Mixing;

/// <summary>
/// A channel's own volume: one level for any channel, hardware inputs
/// included, that scales every send it makes, the way a console's channel
/// fader sits before its auxiliary sends. It adds no node to the graph. Each
/// send is already one internal stream of the channel's combine sink with a
/// volume of its own, so the channel volume is folded into that number: the
/// stream carries the send level times the channel volume. The mix master
/// is the mix sink's own volume, for every kind of mix. Audio takes the
/// same path it always did, with no added latency.
///
/// On XLR 1, XLR 2 and Aux In it comes after the interface's gain or Aux In
/// level and after the input's plugin chain, which it leaves alone; it is a
/// level for the mixes, whatever source the strip is bound to. The Wave XLR
/// Pro's direct microphone path to its jacks is hardware and does not pass
/// through a send, so it does not follow the channel volume. The channel's
/// sink is never used for this either. It is held at full desktop volume,
/// and the sweep puts it back when anything else moves it.
/// </summary>
public sealed partial class Mixer
{
    // channel id -> volume; an absent channel is at unity
    private readonly Dictionary<string, double> _channelVolume = [];

    /// <summary>
    /// A send level scaled by the channel volume and by
    /// <paramref name="mixVolume"/>. A send's internal stream is given it
    /// with the mix volume at unity, since every mix's master is its sink's
    /// own volume. The stream volume is on the desktop's
    /// cubic scale, where multiplying two values multiplies their gains, so
    /// halving the channel volume takes the same number of decibels off every
    /// send whatever its level.
    /// </summary>
    internal static double SendGain(double level, double channelVolume, double mixVolume)
        => Math.Clamp(level, 0, 1) * Math.Clamp(channelVolume, 0, 1) * Math.Clamp(mixVolume, 0, 1);

    /// <summary>Whether a channel takes a channel volume: every channel in the layout does.</summary>
    private bool HasChannelVolumeLocked(string channelId) => _config.Channels.Any(c => c.Id == channelId);

    private double ChannelVolumeLocked(string channelId) => _channelVolume.GetValueOrDefault(channelId, 1.0);

    /// <summary>
    /// The volume of a channel (0..1), applied to each of its sends at once.
    /// Mute stays per send, and a hardware input's gain is not touched. An
    /// unknown channel is left alone.
    /// </summary>
    public void SetChannelVolume(string channelId, double volume)
    {
        lock (_gate)
        {
            if (!HasChannelVolumeLocked(channelId)) return;
            _channelVolume[channelId] = Math.Clamp(volume, 0, 1);
            foreach (MixDefinition mix in _config.Mixes) ApplyCellLocked(channelId, mix.Id);
        }
    }

    /// <summary>Every channel's volume, for the settings file and a profile.</summary>
    private Dictionary<string, double> ExportChannelVolumesLocked()
        => _config.Channels.ToDictionary(c => c.Id, c => ChannelVolumeLocked(c.Id));

    /// <summary>
    /// Take the channel volumes a settings file or a profile carries. See
    /// <see cref="RecallChannelVolumes"/>. The caller pushes the sends.
    /// </summary>
    private void RecallChannelVolumesLocked(IReadOnlyDictionary<string, double>? saved, IEnumerable<string> describedCells)
        => RecallChannelVolumes(_channelVolume, _config.Channels.Select(c => c.Id), saved, describedCells);

    /// <summary>
    /// Set each channel's volume from saved data. A channel the data names
    /// takes its value, clamped to 0..1. A channel it does not name but whose
    /// sends it describes was saved before that channel had a volume (before
    /// channel volumes existed, or for XLR 1, XLR 2 and Aux In before they
    /// took one), when it played at full volume, so it goes back to unity. A
    /// channel the data says nothing about, one added after it was written,
    /// keeps its volume. Entries for channels that do not exist are ignored.
    /// </summary>
    internal static void RecallChannelVolumes(Dictionary<string, double> volumes, IEnumerable<string> channels,
        IReadOnlyDictionary<string, double>? saved, IEnumerable<string> describedCells)
    {
        var described = new HashSet<string>(StringComparer.Ordinal);
        foreach (string cell in describedCells)
        {
            int bar = cell.IndexOf('|', StringComparison.Ordinal);
            if (bar > 0) described.Add(cell[..bar]);
        }
        foreach (string channel in channels)
        {
            if (saved is not null && saved.TryGetValue(channel, out double value) && double.IsFinite(value))
                volumes[channel] = Math.Clamp(value, 0, 1);
            else if (described.Contains(channel))
                volumes.Remove(channel);
        }
    }
}
