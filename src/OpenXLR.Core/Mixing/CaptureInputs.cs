namespace OpenXLR.Core.Mixing;

/// <summary>Capture bindings use stable node names, never transient registry ids.</summary>
public static class CaptureBinding
{
    /// <summary>
    /// A source takes either a stereo pair, 0 to 31, or with pair 0 one
    /// mono port, 0 to 63, that feeds both sides of the channel.
    /// </summary>
    public static bool IsValid(string? source, int pair, int? monoChannel = null)
        => source is { Length: > 0 and <= 256 } && !string.IsNullOrWhiteSpace(source)
            && !source.Any(char.IsControl) && !source.StartsWith("OpenXLR", StringComparison.Ordinal)
            && !source.EndsWith(".monitor", StringComparison.Ordinal) && pair is >= 0 and < 32
            && (monoChannel is null || pair == 0 && monoChannel is >= 0 and < 64);
}

public sealed partial class Mixer
{
    private readonly Dictionary<string, PortLink> _captureFeeds = [];

    private void RemoveCaptureFeedLocked(string id)
    {
        if (_captureFeeds.Remove(id, out PortLink? feed)) _pw.Unlink(feed);
    }

    /// <summary>
    /// Heal only missing capture routes. An absent device or pair stays silent;
    /// no other microphone takes its place. The ordinary sweep retries when
    /// the selected node or its ports return after a hotplug or profile change.
    /// </summary>
    private bool EnsureCaptureFeedsLocked()
    {
        bool changed = false;
        var channels = _config.Channels.Where(c => c.CaptureSource is not null).ToArray();
        if (channels.Length == 0) return false;
        var available = _pw.ListDevices().Where(d => d.Kind == AudioNodeKind.Source && !d.IsOwn)
            .Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        foreach (ChannelDefinition channel in channels)
        {
            if (_captureFeeds.TryGetValue(channel.Id, out PortLink? existing))
            {
                LinkHealth health = _pw.EnsureLinks(existing);
                if (health != LinkHealth.Broken) { changed |= health == LinkHealth.Relinked; continue; }
                RemoveCaptureFeedLocked(channel.Id);
                changed = true;
            }
            if (!available.Contains(channel.CaptureSource!)) continue;
            // External sources need not use the hardware driver's capture_
            // prefix. Direction and the exact node name identify their ports.
            PortLink feed = _pw.LinkNodes(channel.CaptureSource!, "", channel.SinkName, "playback",
                fromPairOffset: channel.CapturePair, fromChannel: channel.CaptureMonoChannel);
            // Capture channels have stereo sinks; mono sources also need a
            // link to each side. Do not retain a partial route as healthy.
            if (feed.Pairs.Count < 2) { _pw.Unlink(feed); continue; }
            _captureFeeds[channel.Id] = feed;
            changed = true;
        }
        return changed;
    }
}
