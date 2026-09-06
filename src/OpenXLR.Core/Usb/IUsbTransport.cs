namespace OpenXLR.Core;

/// <summary>
/// The few USB operations a device class needs, behind an interface so the
/// native library can live in a separate process. Every device class keeps
/// its own request ids, block sizes and commit rules; the transport only
/// opens the device and moves control transfers.
/// </summary>
public interface IUsbTransport : IDisposable
{
    /// <summary>A device handle is open.</summary>
    bool IsOpen { get; }

    /// <summary>Open the device with these ids; false when absent or not permitted.</summary>
    bool Open(ushort vendorId, ushort productId);

    void Close();

    /// <summary>
    /// libusb_control_transfer: the number of bytes moved, or a negative
    /// libusb error. For a read the data comes back in <paramref name="data"/>.
    /// Throws <see cref="UsbHungException"/> when the transfer never
    /// returns; the transport is closed by then.
    /// </summary>
    int ControlTransfer(byte bmRequestType, byte bRequest, ushort wValue, ushort wIndex,
        byte[] data, ushort wLength, uint timeoutMs);
}

/// <summary>Picks the transport every device class uses.</summary>
public static class UsbTransport
{
    /// <summary>
    /// A helper process by default: a transfer that never returns is then
    /// ended by killing the helper, and the operating system reclaims the
    /// stuck thread and the device handle with it. OPENXLR_USB_INPROCESS=1
    /// keeps libusb in the daemon, for debugging.
    /// </summary>
    public static IUsbTransport Create()
        => Environment.GetEnvironmentVariable("OPENXLR_USB_INPROCESS") is "1" or "true"
            ? new InProcessUsbTransport()
            : new HelperUsbTransport();
}
