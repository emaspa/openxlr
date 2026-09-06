using System.Buffers.Binary;
using System.Diagnostics;
using OpenXLR.Core;

namespace OpenXLR.Tests;

public sealed class UsbHelperTests
{
    /// <summary>A backend that records calls and answers a read with a pattern.</summary>
    private sealed class FakeUsb : IUsbTransport
    {
        public List<string> Calls { get; } = [];
        public bool IsOpen { get; private set; }
        public bool Open(ushort vid, ushort pid) { Calls.Add($"open {vid:x4}:{pid:x4}"); IsOpen = pid == 0x00b4; return IsOpen; }
        public void Close() { Calls.Add("close"); IsOpen = false; }
        public int ControlTransfer(byte t, byte r, ushort v, ushort i, byte[] data, ushort len, uint timeout)
        {
            Calls.Add($"xfer {t:x2} {r:x2} {v:x4} {i:x4} len {len} timeout {timeout}" + ((t & 0x80) == 0 ? " out " + Convert.ToHexString(data.AsSpan(0, len)) : ""));
            if ((t & 0x80) != 0) for (int k = 0; k < len; k++) data[k] = (byte)(k + 1);
            return len;
        }
        public void Dispose() { }
    }

    [Fact]
    public void TheProtocolCarriesOpenTransfersAndRepliesWhole()
    {
        var requests = new MemoryStream();
        UsbHelperProtocol.WriteFrame(requests, UsbHelperProtocol.Ping());
        UsbHelperProtocol.WriteFrame(requests, UsbHelperProtocol.Open(0x0fd9, 0x0001));   // not this one
        UsbHelperProtocol.WriteFrame(requests, UsbHelperProtocol.Transfer(0xC1, 0x02, 4, 0x0103, 8, 1000, default));   // before an open
        UsbHelperProtocol.WriteFrame(requests, UsbHelperProtocol.Open(0x0fd9, 0x00b4));
        UsbHelperProtocol.WriteFrame(requests, UsbHelperProtocol.Transfer(0xC1, 0x02, 4, 0x0103, 8, 1000, default));   // read
        UsbHelperProtocol.WriteFrame(requests, UsbHelperProtocol.Transfer(0x41, 0x01, 4, 0x0103, 3, 500, new byte[] { 9, 8, 7 }));   // write
        UsbHelperProtocol.WriteFrame(requests, UsbHelperProtocol.Close());
        requests.Position = 0;
        var replies = new MemoryStream();
        var fake = new FakeUsb();
        UsbHelperProtocol.Serve(requests, replies, fake);

        replies.Position = 0;
        int Code(byte[] f) => BinaryPrimitives.ReadInt32LittleEndian(f);
        byte[] r;
        Assert.Equal(0, Code(r = UsbHelperProtocol.ReadFrame(replies)!));                       // ping
        Assert.Equal(UsbHelperProtocol.NotOpened, Code(UsbHelperProtocol.ReadFrame(replies)!)); // wrong device
        Assert.Equal(UsbHelperProtocol.NoDevice, Code(UsbHelperProtocol.ReadFrame(replies)!));  // transfer with nothing open
        Assert.Equal(0, Code(UsbHelperProtocol.ReadFrame(replies)!));                           // open
        r = UsbHelperProtocol.ReadFrame(replies)!;
        Assert.Equal(8, Code(r));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, r.AsSpan(4).ToArray());              // read data came back
        r = UsbHelperProtocol.ReadFrame(replies)!;
        Assert.Equal(3, Code(r));
        Assert.Equal(4, r.Length);                                                               // a write carries no data back
        Assert.Equal(0, Code(UsbHelperProtocol.ReadFrame(replies)!));                           // close
        Assert.Null(UsbHelperProtocol.ReadFrame(replies));
        Assert.Contains("xfer 41 01 0004 0103 len 3 timeout 500 out 090807", fake.Calls);
        Assert.Equal("close", fake.Calls[^1]);
    }

    private static (string Exe, string[] Args) RealHelper()
    {
        string dll = Path.Combine(AppContext.BaseDirectory, "OpenXLR.Daemon.dll");
        Assert.True(File.Exists(dll), "the daemon assembly sits next to the tests");
        return ("dotnet", [dll, "--usb-helper"]);
    }

    [Fact]
    public void TheRealHelperAnswersAndReportsAnAbsentDevice()
    {
        (string exe, string[] args) = RealHelper();
        using var usb = new HelperUsbTransport(exe, args);
        Assert.False(usb.IsOpen);
        Assert.False(usb.Open(0xffff, 0xfffe));   // no such device on any machine
        Assert.False(usb.IsOpen);
        Assert.Throws<InvalidOperationException>(() => usb.ControlTransfer(0xC1, 2, 0, 0, new byte[4], 4, 100));
    }

    [Fact]
    public void AHelperThatStopsAnsweringIsKilledAndTheTransferReportsAHang()
    {
        // A helper that answers the open, then never again: it swallows the
        // frame lengths and sleeps. What the daemon sees is a hung transfer.
        const string script = """
            import sys, struct, time
            def rd(n):
                b = b''
                while len(b) < n:
                    c = sys.stdin.buffer.read(n - len(b))
                    if not c: sys.exit(0)
                    b += c
                return b
            n = struct.unpack('<I', rd(4))[0]; rd(n)
            sys.stdout.buffer.write(struct.pack('<I', 4) + struct.pack('<i', 0)); sys.stdout.buffer.flush()
            time.sleep(60)
            """;
        using var usb = new HelperUsbTransport("python3", ["-c", script]);
        Assert.True(usb.Open(0x0fd9, 0x00b4));
        var sw = Stopwatch.StartNew();
        var ex = Assert.Throws<UsbHungException>(() => usb.ControlTransfer(0xC1, 2, 4, 0x0103, new byte[8], 8, 300));
        Assert.Contains("helper process killed", ex.Message);
        Assert.InRange(sw.Elapsed, TimeSpan.FromMilliseconds(250), HelperUsbTransport.Guard + TimeSpan.FromSeconds(3));
        Assert.False(usb.IsOpen);
        // A fresh open starts a fresh helper.
        Assert.True(usb.Open(0x0fd9, 0x00b4));
    }
}
