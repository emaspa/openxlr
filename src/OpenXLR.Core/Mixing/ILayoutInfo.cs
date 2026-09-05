namespace OpenXLR.Core.Mixing;

/// <summary>
/// What a command validator needs to know about the live layout, kept
/// small so it can be faked in tests.
/// </summary>
public interface ILayoutInfo
{
    bool HasChannel(string id);
    bool HasMix(string id);
    /// <summary>A mix an output can be fed from (Monitor A, Monitor B).</summary>
    bool IsMonitorMix(string id);
    /// <summary>A currently selected monitor output, or the shared key of a device's pseudo-outputs.</summary>
    bool IsMonitorOutput(string device);
    /// <summary>An insert chain key: an XLR input id or "mix:&lt;id&gt;".</summary>
    bool IsInsertKey(string key);
    /// <summary>Remembered application identities (pinned assignments).</summary>
    int OverrideCount { get; }
}
