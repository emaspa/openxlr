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
    private readonly Action<string>? _errorLog;
    private readonly object _gate = new();
    private Process? _helper;
    private bool _open;

    /// <summary>The daemon binary in helper mode.</summary>
    public HelperUsbTransport(UsbLocation? location = null)
        : this(Environment.ProcessPath ?? throw new InvalidOperationException("no process path for the USB helper"), ["--usb-helper"]) { _location = location; }

    /// <summary>
    /// Any program that speaks <see cref="UsbHelperProtocol"/>; tests use
    /// this. Its standard error goes to <paramref name="errorLog"/>, or to
    /// <see cref="ErrorLog"/> without one.
    /// </summary>
    public HelperUsbTransport(string exe, IReadOnlyList<string> args, Action<string>? errorLog = null)
    {
        _exe = exe;
        _args = args;
        _errorLog = errorLog;
    }

    /// <summary>
    /// Where every helper's standard error goes, a line at a time: why a claim
    /// failed, or the exception that ended the helper. The daemon points it at
    /// its log at start; until then the lines are read and dropped.
    /// </summary>
    public static Action<string>? ErrorLog { get; set; }

    /// <summary>Lines taken from one helper's standard error; the rest is read and dropped.</summary>
    internal const int MaxErrorLines = 100;
    /// <summary>The longest line passed on; a longer one is cut there.</summary>
    internal const int MaxErrorLineLength = 1000;

    public bool IsOpen { get { lock (_gate) return _open && _helper is { HasExited: false }; } }

    private readonly UsbLocation? _location;

    public bool Open(ushort vendorId, ushort productId)
    {
        lock (_gate)
        {
            EnsureHelper();
            byte[] reply = Exchange(UsbHelperProtocol.Open(vendorId, productId, _location), OpenDeadline,
                () => new InvalidOperationException("the USB helper did not answer an open request in time"));
            _open = BinaryPrimitives.ReadInt32LittleEndian(reply) == 0;
            // A helper with nothing open is a process for nothing; the device
            // loop retries every few seconds and each retry would otherwise
            // leave one more behind.
            if (!_open) Kill();
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
            int payloadLength = isRead && code > 0 ? code : 0;
            // Device decoders trust the returned count when accepting a
            // complete settings block. Never report bytes we did not receive
            // or change the caller's buffer before validating the whole reply.
            if (code > wLength || payloadLength > data.Length || reply.Length - 4 != payloadLength)
            {
                Kill();
                throw new IOException("USB helper transfer count does not match its reply payload or requested length");
            }
            if (payloadLength > 0) reply.AsSpan(4, payloadLength).CopyTo(data);
            return code;
        }
    }

    /// <summary>The reset's own deadline in the helper; the reply may take this plus <see cref="Guard"/>.</summary>
    private static readonly TimeSpan ResetDeadline = TimeSpan.FromSeconds(5);

    public int Reset()
    {
        lock (_gate)
        {
            if (!IsOpen) throw new InvalidOperationException("USB device not open");
            var started = Stopwatch.StartNew();
            byte[] reply = Exchange(UsbHelperProtocol.Reset(), ResetDeadline + Guard, () =>
            {
                _open = false;
                return new UsbHungException($"USB reset did not return after {started.ElapsedMilliseconds} ms (helper process killed)");
            });
            if (reply.Length != 4)
            {
                Kill();
                throw new IOException("USB helper reset reply has a payload");
            }
            return BinaryPrimitives.ReadInt32LittleEndian(reply);
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
        // Off this thread: reads that complete at once must not hold the open.
        StreamReader stderr = _helper.StandardError;
        Action<string>? errorLog = _errorLog;
        _ = Task.Run(() => ForwardErrorsAsync(stderr, errorLog));
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
        Task<byte[]?> exchange = Task.Run(() =>
        {
            // A helper stuck before reading can fill stdin too. One deadline
            // covers sending the request as well as receiving the reply.
            UsbHelperProtocol.WriteFrame(p.StandardInput.BaseStream, request);
            return UsbHelperProtocol.ReadFrame(p.StandardOutput.BaseStream);
        });
        byte[]? reply;
        try
        {
            if (!exchange.Wait(deadline))
            {
                Kill();
                _ = exchange.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                throw onTimeout();
            }
            reply = exchange.Result;
        }
        catch (AggregateException ex)
        {
            // Wait itself propagates a failed read or write, before Result.
            Kill();
            throw new IOException($"USB helper exchange failed: {ex.InnerException?.Message}", ex.InnerException);
        }
        if (reply is null || reply.Length < 4)
        {
            Kill();
            throw new IOException("USB helper exited");
        }
        return reply;
    }

    /// <summary>
    /// The helper's standard error into the log, for the helper's whole life,
    /// so a helper that writes a lot never waits on a full pipe and one that
    /// writes nothing holds no thread. Past <see cref="MaxErrorLines"/> the
    /// output is still read, and dropped.
    /// </summary>
    private static async Task ForwardErrorsAsync(StreamReader stderr, Action<string>? errorLog)
    {
        var line = new System.Text.StringBuilder();
        var buffer = new char[4096];
        int lines = 0;
        void Emit()
        {
            if (++lines <= MaxErrorLines)
            {
                string text = line.ToString().TrimEnd('\r');
                if (lines == MaxErrorLines) text += " (further output from this USB helper is dropped)";
                try { (errorLog ?? ErrorLog)?.Invoke(text); }
                catch (Exception) { /* a failing log must not stop the pipe being read */ }
            }
            line.Clear();
        }
        try
        {
            int n;
            while ((n = await stderr.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
                foreach (char c in buffer.AsSpan(0, n))
                {
                    if (c == '\n') Emit();
                    else if (line.Length < MaxErrorLineLength) line.Append(c);
                }
            if (line.Length > 0) Emit();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        // The process leaves a stream it handed out open; this is its only reader.
        finally { stderr.Dispose(); }
    }

    /// <summary>For tests: whether a helper process is alive right now.</summary>
    internal bool HelperAlive { get { lock (_gate) return _helper is { HasExited: false }; } }

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
