namespace OpenXLR.Core;

/// <summary>
/// libusb in this process. The helper process runs on it; the daemon only
/// does when asked to (see <see cref="UsbTransport.Create"/>). A transfer
/// that never returns leaves its thread parked in libusb and the handle
/// abandoned, which is exactly what the helper process exists to contain.
/// </summary>
public sealed class InProcessUsbTransport : IUsbTransport
{
    private IntPtr _ctx;
    private IntPtr _handle;

    public bool IsOpen => _handle != IntPtr.Zero;

    public bool Open(ushort vendorId, ushort productId)
    {
        if (_ctx == IntPtr.Zero)
        {
            int rc = LibUsb.libusb_init(out _ctx);
            if (rc != 0) throw new InvalidOperationException($"libusb_init failed: {LibUsb.StrError(rc)}");
        }
        Close();
        _handle = LibUsb.libusb_open_device_with_vid_pid(_ctx, vendorId, productId);
        return _handle != IntPtr.Zero;
    }

    public void Close()
    {
        if (_handle != IntPtr.Zero) { LibUsb.libusb_close(_handle); _handle = IntPtr.Zero; }
    }

    public int ControlTransfer(byte bmRequestType, byte bRequest, ushort wValue, ushort wIndex,
        byte[] data, ushort wLength, uint timeoutMs)
    {
        if (_handle == IntPtr.Zero) throw new InvalidOperationException("USB device not open");
        try
        {
            return LibUsb.ControlTransfer(_handle, bmRequestType, bRequest, wValue, wIndex, data, wLength, timeoutMs);
        }
        catch (UsbHungException)
        {
            _handle = IntPtr.Zero;   // never closed: the stuck native call may still touch it
            throw;
        }
    }

    public void Dispose()
    {
        Close();
        if (_ctx != IntPtr.Zero) { LibUsb.libusb_exit(_ctx); _ctx = IntPtr.Zero; }
    }
}
