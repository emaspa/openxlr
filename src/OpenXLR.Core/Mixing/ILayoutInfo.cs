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
    /// <summary>An editable application or capture channel.</summary>
    bool HasEditableChannel(string id) => HasApplicationChannel(id);
    /// <summary>An editable input fed by an external capture source.</summary>
    bool HasCaptureChannel(string id) => false;
    /// <summary>A user mix (not the structural Monitor A/B or Aux mixes).</summary>
    bool HasEditableMix(string id) => HasMix(id);
    /// <summary>A valid feed for an output: any mix, or several distinct ones joined with '+' (see <see cref="MonitorFeed"/>).</summary>
    bool IsMonitorFeed(string feed);
    /// <summary>A currently selected monitor output, or the shared key of a device's pseudo-outputs.</summary>
    bool IsMonitorOutput(string device);
    /// <summary>A hardware strip that takes an input binding: XLR 1 or XLR 2, never Aux In.</summary>
    bool HasBindableStrip(string id) => Mixer.IsBindableStrip(id) && HasChannel(id);
    /// <summary>A microphone input that can record a mono Sound Check take.</summary>
    bool IsSoundCheckChannel(string id) => id is "xlr1" or "xlr2" && HasChannel(id);
    /// <summary>A microphone input, which carries the microphone EQ: XLR 1, XLR 2 or a capture input on one mono port.</summary>
    bool IsMicrophoneChannel(string id) => Mixer.IsMicrophoneId(id) && HasChannel(id);
    /// <summary>A microphone whose EQ runs on this computer only, with no choice of where: a capture input.</summary>
    bool EqRunsInSoftwareOnly(string id) => false;
    /// <summary>Whether a microphone's EQ runs on its unit now, so switching it on needs no native helper.</summary>
    bool EqRunsOnDevice(string id) => false;
    /// <summary>Whether the unit can take an incoming table, before that table replaces the current one.</summary>
    bool CanRunEqOnDevice(string id, int bands) => bands is >= MicEq.MinimumBands and <= MicEq.BandCount && EqRunsOnDevice(id);
    /// <summary>Whether a microphone's compressor and expander run on its unit now, so switching either on needs no native helper.</summary>
    bool DynamicsRunOnDevice(string id) => false;
    /// <summary>An insert chain key: an XLR or capture input id, or "mix:&lt;id&gt;".</summary>
    bool IsInsertKey(string key);
    /// <summary>The chain's binding width: one or two. The default is for legacy layouts.</summary>
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
    /// <summary>Whether this application already has a remembered assignment, including legacy aliases.</summary>
    bool HasOverride(string identity) => false;
}
