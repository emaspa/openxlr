using System.Buffers.Binary;

namespace OpenXLR.Core.Devices;

/// <summary>
/// Block 0x0006 of the Wave XLR MK.2 decoded: whether a microphone and
/// headphones are plugged in, the value the microphone detection reads,
/// whether the unit is storing its settings, and the sample rate.
/// </summary>
public sealed record Mk2StatusReading(bool MicConnected, bool HeadphonesConnected, int MicDetection, bool Storing, uint SampleRate);

/// <summary>
/// The Wave XLR MK.2's status, block 0x0006: 17 bytes, read only. Measured
/// on the desk on 2026-11-10 (firmware 1.0.15):
///
///   0        bit 0 a microphone is plugged in, bit 1 headphones are
///   10 to 13 the sample rate, little endian: 48000
///   14       bit 0 set by any write to the unit and cleared about 10.5 s
///            later, while the unit stores its settings
///   15, 16   the microphone detection, little endian: about 55 to 70 with
///            a microphone plugged in, 373 without
///
/// Bytes 1 and 2 stayed 0 at a 0 dBFS input with ClipGuard on; what they
/// count is not known, and nothing is decoded from them or from the rest.
/// </summary>
public static class Mk2Status
{
    public const ushort Block = 0x0006;
    public const int Length = 17;

    public const int ConnectionOffset = 0;
    public const byte MicMask = 0x01;
    public const byte HeadphonesMask = 0x02;
    public const int SampleRateOffset = 10;
    public const int StoringOffset = 14;
    public const byte StoringMask = 0x01;
    public const int DetectionOffset = 15;

    public static string? Check(ReadOnlySpan<byte> block)
        => block.Length != Length ? $"block 0x0006 is {block.Length} bytes, not {Length}" : null;

    /// <summary>The block decoded; null when it is not 17 bytes.</summary>
    public static Mk2StatusReading? Decode(ReadOnlySpan<byte> block)
    {
        if (Check(block) is not null) return null;
        byte connection = block[ConnectionOffset];
        return new((connection & MicMask) != 0, (connection & HeadphonesMask) != 0,
            BinaryPrimitives.ReadUInt16LittleEndian(block[DetectionOffset..]),
            (block[StoringOffset] & StoringMask) != 0,
            BinaryPrimitives.ReadUInt32LittleEndian(block[SampleRateOffset..]));
    }

    /// <summary>
    /// The block decoded for tester diagnostics as one <c>status</c> line; a
    /// block of another length says why it was not decoded.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Describe(ReadOnlySpan<byte> block)
    {
        if (Check(block) is { } problem) return new Dictionary<string, string> { ["status"] = $"not decoded: {problem}" };
        Mk2StatusReading s = Decode(block)!;
        return new Dictionary<string, string>
        {
            ["status"] = $"microphone {(s.MicConnected ? "connected" : "not connected")} (detection {s.MicDetection}), "
                + $"headphones {(s.HeadphonesConnected ? "connected" : "not connected")}, "
                + $"{(s.Storing ? "storing settings" : "not storing")}, {s.SampleRate} Hz",
        };
    }
}
