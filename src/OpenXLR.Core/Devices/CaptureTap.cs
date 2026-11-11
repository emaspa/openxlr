namespace OpenXLR.Core.Devices;

/// <summary>An input whose USB capture the unit can take before its own processing (raw capture).</summary>
public enum RawCaptureInput
{
    /// <summary>XLR 1, the first microphone input; the Wave XLR MK.2's one input.</summary>
    Xlr1 = 1,
    /// <summary>XLR 2, the Wave XLR Pro's second microphone input.</summary>
    Xlr2 = 2,
    /// <summary>The Wave XLR Pro's analog Line In.</summary>
    LineIn = 3,
}

/// <summary>
/// The 2-bit field that picks where a unit takes an input's USB capture, the
/// signal the computer records: bits 0 and 1 of one byte, 0 for the input
/// before the unit's processing and 1 to 3 for points after it. Every other
/// bit of the byte is carried as read; bit 2 is believed to bypass the
/// capture's limiter and is never written.
///
/// Desk measurements of 2026-11-11, with the unit's expander held at a
/// steady 24 dB of reduction on speech:
///   Wave XLR Pro, firmware 1.0.14, block 0x0001: byte 94 XLR 1 and byte 95
///   XLR 2 (00 the microphone before the low cut, compressor, expander and
///   Voice Tune; 01 to 03 after them), byte 97 Line In (00 as it arrives,
///   01 after the insert return, 02 after the ducker, 03 silence). Byte 96
///   showed no effect on USB Aux's capture and is not driven.
///   Wave XLR MK.2, firmware 1.0.15, block 0x0001 byte 4 (00 the raw
///   microphone, within 1 dB of the input meter; 01 and 02 after the
///   processing, 23 to 24 dB under it).
/// Every tap applies live, without the commit. The units read 02 as Wave
/// Link leaves them.
/// </summary>
public static class CaptureTap
{
    /// <summary>The bits of the byte that hold the tap.</summary>
    public const int Mask = 0b11;

    /// <summary>The input before the unit's processing.</summary>
    public const int Raw = 0;

    /// <summary>The value the units read as Wave Link leaves them, put back when nothing else was found.</summary>
    public const int Processed = 2;

    /// <summary>The tap a byte holds, 0 to 3.</summary>
    public static int Field(byte value) => value & Mask;

    /// <summary>The byte with its tap set to <paramref name="tap"/> and every other bit as given.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The tap is not 0 to 3.</exception>
    public static byte With(byte value, int tap)
        => tap is >= 0 and <= Mask ? (byte)(value & ~Mask | tap) : throw new ArgumentOutOfRangeException(nameof(tap), tap, "a tap is 0 to 3");

    /// <summary>
    /// The tap raw capture going off puts back: the one found before raw
    /// capture went on, or <see cref="Processed"/> when none was found or it
    /// was already raw.
    /// </summary>
    public static int Restored(int? found) => found is int tap and > Raw and <= Mask ? tap : Processed;
}
