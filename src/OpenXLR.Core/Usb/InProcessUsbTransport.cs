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
        if (_handle == IntPtr.Zero) return false;
        int claim = LibUsb.libusb_claim_interface(_handle, LibUsb.VendorInterface);
        if (claim != 0)
        {
            LibUsb.libusb_close(_handle);
            _handle = IntPtr.Zero;
            // No logger here, and the helper's loop must not see an exception:
            // say why on stderr (the journal, when libusb runs in the daemon
            // itself) and report the device as not opened.
            Console.Error.WriteLine($"usb: claiming interface {LibUsb.VendorInterface} failed: {LibUsb.StrError(claim)}");
            return false;
        }
        return true;
    }

    public void Close()
    {
        if (_handle != IntPtr.Zero)
        {
            LibUsb.libusb_release_interface(_handle, LibUsb.VendorInterface);
            LibUsb.libusb_close(_handle);
            _handle = IntPtr.Zero;
        }
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
