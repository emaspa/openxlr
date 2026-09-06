namespace OpenXLR.Core.Devices;

/// <summary>
/// The Elgato Wave XLR Dock MK.2 (0fd9:00c7), the Stream Deck+ module built
/// on the same Wave FX platform as the Wave XLR MK.2. Its USB descriptor is
/// interface-for-interface identical to the MK.2's (reported in issue #1) and
/// its blocks have the MK.2 layout (0x0001 crossfade, 0x0004 input settings,
/// 0x0005 headphones), but the firmware serves them at wIndex 0x0103, the
/// Pro's bank, and stalls the MK.2's 0x0203. No commit block: block 0x0003
/// does not exist and writes take effect at once. Like the first XLR Dock it
/// has no physical controls of its own; the Stream Deck+ dials drive it
/// through software. Every control verified on hardware: gain, mute and
/// headphone volume cross-checked against the kernel's ALSA mirror of the
/// feature units, phantom power with a condenser microphone, the rest by ear.
/// Unlike the first dock it keeps its settings across a power cycle (verified
/// by replugging: the gain came back as set), so the daemon treats it like
/// the Pro and does not restore anything on connect.
/// </summary>
public sealed class XlrDockMk2Device : WaveXlrMk2Device
{
    public new const ushort ProductId = 0x00C7;

    public XlrDockMk2Device() : base(ProductId, "Wave XLR Dock MK.2", physicalControls: false, vIndex: 0x0103) { }
}
