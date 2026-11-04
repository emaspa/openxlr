namespace OpenXLR.Core.Devices;

/// <summary>
/// The Elgato Wave:1 (0fd9:006e), the smaller USB condenser microphone of
/// the Wave:3's generation, not an XLR interface. Nobody on this project owns
/// one and no public source has probed one, so this is the Wave:3's backend
/// under the Wave:1's product id and name. An owner's diagnostics on 0.1.60
/// showed the unit answering both blocks in the Wave:3's layout, bytes 2
/// and 3 reading 0x00EC as a Wave:3's do, and a second archive
/// showed the gain, mute and headphone volume written from the UI taking
/// effect; the firmware kept a gain word written with the dial on
/// headphones. ClipGuard and the mix writes are not yet confirmed
/// (docs/hardware-support.md).
///
/// What is known of the microphone comes from a published review
/// (TechPowerUp, Elgato Wave:1 Review): a 3.5 mm jack for direct monitoring,
/// one dial that sets the headphone volume by default or the gain when Wave
/// Link is told so, the dial pressed as the mute button, and in Wave Link the
/// gain, headphone volume, mic/PC mix, ClipGuard, low cut and gain lock, the
/// same controls Wave Link shows for the Wave:3. None of that says the
/// config block is laid out the same way. It says the firmware keeps the
/// same settings, and the two microphones shipped together on one software
/// generation, so the Wave:3's class-request dialect and 16-byte block are
/// the reading to try. A unit answering a different length is refused
/// before anything is written, as on the Wave:3.
///
/// Where the review says the Wave:1 differs: its dial has two targets, the
/// headphones and the gain, with no monitor-mix mode, so byte 12 should
/// only ever read 1 or 2; the mute is a press of the dial rather than a pad.
/// The gain range is the Wave:3's 0 to 40 dB until an owner's kernel control
/// range says otherwise. The diagnostics dump shows both blocks as answered
/// and the dial's target, which is what an owner's report needs.
/// </summary>
public sealed class Wave1Device : Wave3Device
{
    public new const ushort ProductId = 0x006E;

    public Wave1Device() : this(UsbTransport.Create(), AlsaCards.RunAmixer, null, events: AlsaControlEvents.Open) { }

    /// <summary>Tests substitute the transport; without amixer the card is left alone.</summary>
    internal Wave1Device(IUsbTransport usb) : this(usb, null, null) { }

    /// <summary>Tests substitute the transport, amixer, the card lookup and the clock.</summary>
    internal Wave1Device(IUsbTransport usb, Func<IReadOnlyList<string>, ProcessResult>? amixer, Func<int>? findCard, Func<DateTime>? now = null, Func<int, IAlsaControlEvents?>? events = null)
        : base(ProductId, "Wave:1", usb, amixer, findCard, now, events) { }
}
