namespace OpenXLR.Core.Mixing;

/// <summary>
/// What a command validator needs to know about the live layout, kept
/// small so it can be faked in tests.
/// </summary>
public interface ILayoutInfo
{
    bool HasChannel(string id);
    bool HasMix(string id);
    /// <summary>An editable application channel (not a hardware input).</summary>
    bool HasApplicationChannel(string id) => HasChannel(id);
    /// <summary>An editable virtual microphone (not a monitor or Aux mix).</summary>
    bool HasVirtualMix(string id) => HasMix(id);
    /// <summary>A valid feed for an output: one monitor mix, or several distinct ones joined with '+' (see <see cref="MonitorFeed"/>).</summary>
    bool IsMonitorFeed(string feed);
    /// <summary>A currently selected monitor output, or the shared key of a device's pseudo-outputs.</summary>
    bool IsMonitorOutput(string device);
    /// <summary>An insert chain key: an XLR input id or "mix:&lt;id&gt;".</summary>
    bool IsInsertKey(string key);
    /// <summary>The channels an insert chain carries: one on an XLR input, two on a mix.</summary>
    int InsertChannels(string key) => key.StartsWith("mix:", StringComparison.Ordinal) ? 2 : 1;
    /// <summary>
    /// The insert the chain already holds under this id, or null. A rule
    /// that refuses a plugin at the chain's width applies to what is being
    /// added, never to what is already there, or a chain with one such
    /// insert could not be edited to remove it; the caller compares the
    /// plugin too, since an id kept while its plugin changes is an addition.
    /// </summary>
    InsertDefinition? InsertInChain(string key, string id) => null;
    /// <summary>Remembered application identities (pinned assignments).</summary>
    int OverrideCount { get; }
}
