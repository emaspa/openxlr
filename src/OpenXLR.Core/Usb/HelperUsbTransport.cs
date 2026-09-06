using System.Buffers.Binary;
using System.Diagnostics;

namespace OpenXLR.Core;

/// <summary>
/// libusb in a helper process. Each request goes down the helper's stdin
/// and the reply comes back on its stdout; a reply that does not arrive
/// within the transfer's timeout plus a guard means the native call is
/// stuck, so the whole helper is killed and the operating system reclaims
/// the parked thread and the device handle. The next open starts a fresh
/// helper. The daemon itself never holds a libusb handle this way.
/// </summary>
public sealed class HelperUsbTransport : IUsbTransport
{
    /// <summary>Extra time past libusb's own timeout before a transfer counts as stuck.</summary>
    public static readonly TimeSpan Guard = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan OpenDeadline = TimeSpan.FromSeconds(8);

    private readonly string _exe;
    private readonly IReadOnlyList<string> _args;
    private readonly object _gate = new();
    private Process? _helper;
    private bool _open;

    /// <summary>The daemon binary in helper mode.</summary>
    public HelperUsbTransport()
        : this(Environment.ProcessPath ?? throw new InvalidOperationException("no process path for the USB helper"), ["--usb-helper"]) { }

    /// <summary>Any program that speaks <see cref="UsbHelperProtocol"/>; tests use this.</summary>
    public HelperUsbTransport(string exe, IReadOnlyList<string> args)
    {
        _exe = exe;
        _args = args;
    }

    public bool IsOpen { get { lock (_gate) return _open && _helper is { HasExited: false }; } }

    public bool Open(ushort vendorId, ushort productId)
    {
        lock (_gate)
        {
            EnsureHelper();
            byte[] reply = Exchange(UsbHelperProtocol.Open(vendorId, productId), OpenDeadline,
                () => new InvalidOperationException("the USB helper did not answer an open request in time"));
            _open = BinaryPrimitives.ReadInt32LittleEndian(reply) == 0;
            return _open;
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            if (_helper is { HasExited: false } && _open)
            {
                try { Exchange(UsbHelperProtocol.Close(), OpenDeadline, () => new IOException("close timed out")); }
                catch (Exception) { Kill(); }
            }
            _open = false;
        }
    }

    public int ControlTransfer(byte bmRequestType, byte bRequest, ushort wValue, ushort wIndex,
        byte[] data, ushort wLength, uint timeoutMs)
    {
        lock (_gate)
        {
            if (!IsOpen) throw new InvalidOperationException("USB device not open");
            bool isRead = (bmRequestType & 0x80) != 0;
            byte[] req = UsbHelperProtocol.Transfer(bmRequestType, bRequest, wValue, wIndex, wLength, timeoutMs,
                isRead ? default : data.AsSpan(0, Math.Min(wLength, data.Length)));
            var started = Stopwatch.StartNew();
            byte[] reply = Exchange(req, TimeSpan.FromMilliseconds(timeoutMs) + Guard, () =>
            {
                _open = false;
                return new UsbHungException(
                    $"USB control transfer did not return after {started.ElapsedMilliseconds} ms " +
                    $"(libusb timeout {timeoutMs} ms, helper process killed): bmRequestType {bmRequestType:X2} " +
                    $"bRequest {bRequest:X2} wValue {wValue:X4} wIndex {wIndex:X4} wLength {wLength}" +
                    (isRead ? "" : $" data {Convert.ToHexString(data.AsSpan(0, Math.Min(wLength, data.Length)))}"));
            });
            int code = BinaryPrimitives.ReadInt32LittleEndian(reply);
            if (isRead && code > 0) reply.AsSpan(4, Math.Min(reply.Length - 4, data.Length)).CopyTo(data);
            return code;
        }
    }

    private void EnsureHelper()
    {
        if (_helper is { HasExited: false }) return;
        _helper?.Dispose();
        var psi = new ProcessStartInfo(_exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string a in _args) psi.ArgumentList.Add(a);
        _helper = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start the USB helper {_exe}");
        _ = _helper.StandardError.BaseStream.CopyToAsync(Stream.Null);   // nothing useful comes back this way
        _open = false;
    }

    /// <summary>
    /// One request, one reply, within the deadline; past it the helper is
    /// killed and <paramref name="onTimeout"/> supplies the exception. A
    /// helper that died is reported as an IOException, and the caller sees
    /// IsOpen false either way.
    /// </summary>
    private byte[] Exchange(byte[] request, TimeSpan deadline, Func<Exception> onTimeout)
    {
        Process p = _helper ?? throw new InvalidOperationException("USB helper not running");
        try
        {
            UsbHelperProtocol.WriteFrame(p.StandardInput.BaseStream, request);
        }
        catch (IOException ex)
        {
            Kill();
            throw new IOException($"USB helper is gone: {ex.Message}", ex);
        }
        Task<byte[]?> read = Task.Run(() => UsbHelperProtocol.ReadFrame(p.StandardOutput.BaseStream));
        if (!read.Wait(deadline))
        {
            Kill();
            _ = read.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw onTimeout();
        }
        byte[]? reply;
        try { reply = read.Result; }
        catch (AggregateException ex) { Kill(); throw new IOException($"USB helper read failed: {ex.InnerException?.Message}", ex.InnerException); }
        if (reply is null || reply.Length < 4)
        {
            Kill();
            throw new IOException("USB helper exited");
        }
        return reply;
    }

    private void Kill()
    {
        _open = false;
        if (_helper is null) return;
        try { if (!_helper.HasExited) _helper.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        try { _helper.WaitForExit(2000); } catch (Exception) { }
        _helper.Dispose();
        _helper = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Close();
            try { _helper?.StandardInput.Close(); } catch (Exception) { }
            if (_helper is { HasExited: false } && !_helper.WaitForExit(1000)) Kill();
            _helper?.Dispose();
            _helper = null;
        }
    }
}
