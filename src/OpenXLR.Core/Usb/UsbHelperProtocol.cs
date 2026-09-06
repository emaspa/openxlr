using System.Buffers.Binary;

namespace OpenXLR.Core;

/// <summary>
/// The wire between the daemon and its USB helper process: length-prefixed
/// little-endian frames on the helper's stdin and stdout. One request, one
/// reply, in order. Kept tiny on purpose; the helper must have nothing to
/// do but open a device and move control transfers.
/// </summary>
public static class UsbHelperProtocol
{
    public const byte OpPing = 1, OpOpen = 2, OpClose = 3, OpTransfer = 4;
    /// <summary>Reply code when the device was not found or could not be opened.</summary>
    public const int NotOpened = -1000;
    /// <summary>Reply code for a transfer attempted with no device open.</summary>
    public const int NoDevice = -1001;
    /// <summary>The largest frame either side accepts; a control transfer moves at most 64 KiB.</summary>
    public const int MaxFrame = 80 * 1024;

    public static byte[] Ping() => [OpPing];

    public static byte[] Open(ushort vid, ushort pid)
    {
        var f = new byte[5];
        f[0] = OpOpen;
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(1), vid);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(3), pid);
        return f;
    }

    public static byte[] Close() => [OpClose];

    public static byte[] Transfer(byte bmRequestType, byte bRequest, ushort wValue, ushort wIndex,
        ushort wLength, uint timeoutMs, ReadOnlySpan<byte> outData)
    {
        var f = new byte[13 + outData.Length];
        f[0] = OpTransfer; f[1] = bmRequestType; f[2] = bRequest;
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(3), wValue);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(5), wIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(7), wLength);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(9), timeoutMs);
        outData.CopyTo(f.AsSpan(13));
        return f;
    }

    /// <summary>A reply: the code, then any bytes read from the device.</summary>
    public static byte[] Reply(int code, ReadOnlySpan<byte> data = default)
    {
        var f = new byte[4 + data.Length];
        BinaryPrimitives.WriteInt32LittleEndian(f, code);
        data.CopyTo(f.AsSpan(4));
        return f;
    }

    public static void WriteFrame(Stream s, ReadOnlySpan<byte> frame)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)frame.Length);
        s.Write(len);
        s.Write(frame);
        s.Flush();
    }

    /// <summary>The next frame, or null at end of stream.</summary>
    public static byte[]? ReadFrame(Stream s)
    {
        var len = new byte[4];
        if (!Fill(s, len)) return null;
        uint n = BinaryPrimitives.ReadUInt32LittleEndian(len);
        if (n > MaxFrame) throw new IOException($"USB helper frame of {n} bytes exceeds {MaxFrame}");
        var frame = new byte[n];
        if (!Fill(s, frame)) return null;
        return frame;
    }

    private static bool Fill(Stream s, byte[] buf)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = s.Read(buf, got, buf.Length - got);
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }

    /// <summary>
    /// The helper's loop: answer requests on <paramref name="backend"/>
    /// until the request stream ends. Runs in the helper process; tests run
    /// it over in-memory streams with a fake backend.
    /// </summary>
    public static void Serve(Stream requests, Stream replies, IUsbTransport backend)
    {
        while (ReadFrame(requests) is byte[] req && req.Length > 0)
        {
            byte[] reply;
            try
            {
                switch (req[0])
                {
                    case OpPing:
                        reply = Reply(0);
                        break;
                    case OpOpen:
                        reply = Reply(backend.Open(BinaryPrimitives.ReadUInt16LittleEndian(req.AsSpan(1)),
                            BinaryPrimitives.ReadUInt16LittleEndian(req.AsSpan(3))) ? 0 : NotOpened);
                        break;
                    case OpClose:
                        backend.Close();
                        reply = Reply(0);
                        break;
                    case OpTransfer:
                    {
                        if (!backend.IsOpen) { reply = Reply(NoDevice); break; }
                        byte bmRequestType = req[1], bRequest = req[2];
                        ushort wValue = BinaryPrimitives.ReadUInt16LittleEndian(req.AsSpan(3));
                        ushort wIndex = BinaryPrimitives.ReadUInt16LittleEndian(req.AsSpan(5));
                        ushort wLength = BinaryPrimitives.ReadUInt16LittleEndian(req.AsSpan(7));
                        uint timeout = BinaryPrimitives.ReadUInt32LittleEndian(req.AsSpan(9));
                        var data = new byte[Math.Max(wLength, req.Length - 13)];
                        req.AsSpan(13).CopyTo(data);
                        int code = backend.ControlTransfer(bmRequestType, bRequest, wValue, wIndex, data, wLength, timeout);
                        bool isRead = (bmRequestType & 0x80) != 0;
                        reply = Reply(code, isRead && code > 0 ? data.AsSpan(0, Math.Min(code, data.Length)) : default);
                        break;
                    }
                    default:
                        reply = Reply(-1002);
                        break;
                }
            }
            catch (UsbHungException)
            {
                // Nothing to answer with; the daemon has given up on this
                // process already and is about to kill it.
                return;
            }
            WriteFrame(replies, reply);
        }
    }
}
