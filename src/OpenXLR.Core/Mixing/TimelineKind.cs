namespace OpenXLR.Core.Mixing;

/// <summary>
/// The kinds of entry in the daemon's event timeline, the short record of
/// what changed that a glitch marker is read against. The mixer reports its
/// own through <see cref="PipeWireAdapter.Events"/>; the daemon adds the rest.
/// </summary>
public static class TimelineKind
{
    /// <summary>The daemon started.</summary>
    public const string Daemon = "daemon";
    /// <summary>An interface was connected, dropped, switched or stood in for.</summary>
    public const string Device = "device";
    /// <summary>A USB transfer that never returned, a USB reset, reads that went unanswered.</summary>
    public const string Usb = "usb";
    /// <summary>The driven unit's audio card profile.</summary>
    public const string Card = "card";
    /// <summary>A capture that stalled or was recovered, and the hardware inputs' sources.</summary>
    public const string Capture = "capture";
    /// <summary>The mixer's graph was built, repaired, lost or torn down, and the mixer's own notes.</summary>
    public const string Mixer = "mixer";
    /// <summary>A plugin host or another native helper started or stopped on its own.</summary>
    public const string Plugin = "plugin";
    /// <summary>The device that drives the graph, the audio clock rule, the block size, an own clock bridge.</summary>
    public const string Clock = "clock";
    /// <summary>An application stream started or stopped on a mixer channel.</summary>
    public const string Stream = "stream";
    /// <summary>The user marked a glitch.</summary>
    public const string Marker = "marker";
}
