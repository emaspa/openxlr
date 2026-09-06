using System.Runtime.InteropServices;
using System.Threading;

namespace OpenXLR.Core;

/// <summary>
/// Minimal P/Invoke over libusb-1.0, just enough for the Wave XLR Pro's vendor
/// control transfers. Mirrors the validated Python/ctypes prototype rather than
/// depending on a higher-level wrapper for the critical path. The vendor
/// interface (3) is unclaimed by any kernel driver, so no detach is needed.
/// Only <see cref="InProcessUsbTransport"/> calls into it; in the daemon that
/// transport runs inside the USB helper process (see HelperUsbTransport).
/// </summary>
internal static class LibUsb
{
    private const string Lib = "libusb-1.0.so.0";

    [DllImport(Lib)] internal static extern int libusb_init(out IntPtr ctx);
    [DllImport(Lib)] internal static extern void libusb_exit(IntPtr ctx);

    [DllImport(Lib)]
    internal static extern IntPtr libusb_open_device_with_vid_pid(
        IntPtr ctx, ushort vendorId, ushort productId);

    [DllImport(Lib)] internal static extern void libusb_close(IntPtr devHandle);

    [DllImport(Lib)]
    internal static extern int libusb_control_transfer(
        IntPtr devHandle, byte bmRequestType, byte bRequest,
        ushort wValue, ushort wIndex,
        byte[] data, ushort wLength, uint timeout);

    [DllImport(Lib)] internal static extern IntPtr libusb_strerror(int errcode);
    [DllImport(Lib)] private static extern IntPtr libusb_get_version();

    /// <summary>"1.0.30" style, or "unknown" if the library refuses to say.</summary>
    internal static string Version
    {
        get
        {
            try
            {
                IntPtr v = libusb_get_version();
                if (v == IntPtr.Zero) return "unknown";
                return $"{Marshal.ReadInt16(v, 0)}.{Marshal.ReadInt16(v, 2)}.{Marshal.ReadInt16(v, 4)}";
            }
            catch (Exception) { return "unknown"; }
        }
    }

    /// <summary>
    /// Extra time allowed beyond libusb's own timeout before a transfer is
    /// declared stuck. libusb enforces its timeout by cancelling the URB; on
    /// a device that never completes the cancel, the synchronous call never
    /// returns (issue #6, Wave XLR MK.1 writes).
    /// </summary>
    internal const int GuardMs = 3000;

    /// <summary>
    /// libusb_control_transfer that cannot hang its caller. The transfer runs
    /// on its own thread and the caller waits for the libusb timeout plus
    /// <see cref="GuardMs"/>. Past that a <see cref="UsbHungException"/> is
    /// thrown while the worker stays parked in libusb; the handle it holds
    /// must then be abandoned, never closed, because the native call may
    /// still touch it.
    /// </summary>
    internal static int ControlTransfer(IntPtr devHandle, byte bmRequestType, byte bRequest,
        ushort wValue, ushort wIndex, byte[] data, ushort wLength, uint timeoutMs)
        => Guarded(() => libusb_control_transfer(devHandle, bmRequestType, bRequest, wValue, wIndex, data, wLength, timeoutMs),
            timeoutMs,
            $"bmRequestType {bmRequestType:X2} bRequest {bRequest:X2} wValue {wValue:X4} wIndex {wIndex:X4} wLength {wLength}"
            + ((bmRequestType & 0x80) == 0 ? $" data {Convert.ToHexString(data.AsSpan(0, Math.Min(wLength, data.Length)))}" : ""));

    internal static int Guarded(Func<int> transfer, uint timeoutMs, string what)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        int result = 0;
        Exception? failure = null;
        var done = new ManualResetEventSlim(false);
        var worker = new Thread(() =>
        {
            try { result = transfer(); }
            catch (Exception ex) { failure = ex; }
            finally { try { done.Set(); } catch (ObjectDisposedException) { } }
        }) { IsBackground = true, Name = "libusb-control" };
        worker.Start();
        int budget = checked((int)timeoutMs + GuardMs);
        if (!done.Wait(budget))
            throw new UsbHungException(
                $"USB control transfer did not return after {started.ElapsedMilliseconds} ms " +
                $"(libusb timeout {timeoutMs} ms, libusb {Version}): {what}");
        if (failure is not null) throw failure;
        return result;
    }

    internal static string StrError(int code)
    {
        IntPtr p = libusb_strerror(code);
        return Marshal.PtrToStringAnsi(p) ?? $"libusb error {code}";
    }
}

/// <summary>A control transfer that outlived libusb's timeout and its cancel.</summary>
public sealed class UsbHungException(string message) : IOException(message);
