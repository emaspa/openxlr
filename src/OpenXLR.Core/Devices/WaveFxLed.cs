using System.Globalization;

namespace OpenXLR.Core.Devices;

/// <summary>
/// The unit's LEDs as the state reports them: the ring's brightness, the
/// moving level meter's brightness, each in percent, and the ring's colour in
/// each of the dial's three modes as <c>#rrggbb</c>.
/// </summary>
public sealed record DeviceLed
{
    /// <summary>The ring's static display, 0 to 100 %.</summary>
    public int RingBrightness { get; init; }

    /// <summary>The moving microphone level meter on the ring, 0 to 100 %.</summary>
    public int MeterBrightness { get; init; }

    /// <summary>The ring's colour while the dial sets the gain.</summary>
    public string GainColour { get; init; } = WaveFxLed.White;

    /// <summary>The ring's colour while the dial sets the headphone level.</summary>
    public string HeadphoneColour { get; init; } = WaveFxLed.White;

    /// <summary>The ring's colour while the dial sets the mix.</summary>
    public string MixColour { get; init; } = WaveFxLed.White;
}

/// <summary>
/// A change to the unit's LEDs: each field set is written, each null one is
/// left as the unit holds it. The daemon keeps the fields a user set, per
/// unit, in this shape.
/// </summary>
public sealed record DeviceLedChange
{
    public int? RingBrightness { get; init; }
    public int? MeterBrightness { get; init; }
    public string? GainColour { get; init; }
    public string? HeadphoneColour { get; init; }
    public string? MixColour { get; init; }

    /// <summary>This change with every field <paramref name="later"/> sets taken from it.</summary>
    public DeviceLedChange With(DeviceLedChange later) => new()
    {
        RingBrightness = later.RingBrightness ?? RingBrightness,
        MeterBrightness = later.MeterBrightness ?? MeterBrightness,
        GainColour = later.GainColour ?? GainColour,
        HeadphoneColour = later.HeadphoneColour ?? HeadphoneColour,
        MixColour = later.MixColour ?? MixColour,
    };

    /// <summary>True when no field is set.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => RingBrightness is null && MeterBrightness is null
        && GainColour is null && HeadphoneColour is null && MixColour is null;
}

/// <summary>
/// The LED block of the Wave XLR MK.2, block 0x0016: 61 bytes on firmware
/// 1.0.15, read and written whole at the unit's bank, applied at once and
/// read back. Measured on the desk on 2026-11-11 with the owner looking at a
/// Wave XLR MK.2 on 1.0.15 while each field was written:
///
///   byte 0      the ring's static display brightness (0x08 visibly dim,
///               0xff full); the moving level meter kept its own brightness
///   byte 2      the moving microphone level meter's brightness
///   bytes 11-14 the ring's colour in the dial's gain mode
///   bytes 19-22 the ring's colour in the headphone mode
///   bytes 27-30 the ring's colour in the mix mode
///
/// A colour is four bytes, blue, green, red and white: ff000000 showed blue,
/// 00ff0000 green, 0000ff00 red, and the factory 000000ff is white. Writing
/// the three slots showed each colour in its own mode; the mute button's
/// lights did not change. Byte 1 showed nothing at 0x08 and every other byte
/// was not tested, so each is written back as it was read. The unit read
/// 0xff in bytes 0 to 2 and white in the three slots from the factory.
/// </summary>
public static class WaveFxLed
{
    public const ushort Block = 0x0016;
    public const int Length = 61;

    public const int RingBrightnessOffset = 0;
    public const int MeterBrightnessOffset = 2;
    public const int GainColourOffset = 11;
    public const int HeadphoneColourOffset = 19;
    public const int MixColourOffset = 27;

    /// <summary>The dimmest brightness byte written: 0x08, the dimmest level seen on the desk. Lower bytes were not tried.</summary>
    public const byte LeastBrightness = 0x08;

    /// <summary>The colour of the unit's white LED alone, which the factory colours are.</summary>
    public const string White = "#ffffff";

    /// <summary>The first Wave XLR MK.2 firmware the block was measured on.</summary>
    public static Version Mk2Firmware { get; } = new(1, 0, 15);

    /// <summary>The unit's factory LEDs: full brightness and white in every mode.</summary>
    public static DeviceLedChange Factory { get; } = new()
    {
        RingBrightness = 100,
        MeterBrightness = 100,
        GainColour = White,
        HeadphoneColour = White,
        MixColour = White,
    };

    /// <summary>The block's tested fields; null when it is shorter than the block.</summary>
    public static DeviceLed? Decode(ReadOnlySpan<byte> block) => block.Length < Length ? null : new DeviceLed
    {
        RingBrightness = Percent(block[RingBrightnessOffset]),
        MeterBrightness = Percent(block[MeterBrightnessOffset]),
        GainColour = Hex(block.Slice(GainColourOffset, 4)),
        HeadphoneColour = Hex(block.Slice(HeadphoneColourOffset, 4)),
        MixColour = Hex(block.Slice(MixColourOffset, 4)),
    };

    /// <summary>
    /// Write the fields <paramref name="change"/> sets into a block read from
    /// the unit; every other byte keeps what was read.
    /// </summary>
    /// <exception cref="ArgumentException">The block is shorter than <see cref="Length"/>, or a colour is not <c>#rrggbb</c>.</exception>
    public static void Encode(Span<byte> block, DeviceLedChange change)
    {
        if (block.Length < Length) throw new ArgumentException($"the LED block is {block.Length} bytes, {Length} expected");
        if (change.RingBrightness is int ring) block[RingBrightnessOffset] = BrightnessByte(ring);
        if (change.MeterBrightness is int meter) block[MeterBrightnessOffset] = BrightnessByte(meter);
        if (change.GainColour is { } gain) WriteColour(block.Slice(GainColourOffset, 4), gain);
        if (change.HeadphoneColour is { } headphone) WriteColour(block.Slice(HeadphoneColourOffset, 4), headphone);
        if (change.MixColour is { } mix) WriteColour(block.Slice(MixColourOffset, 4), mix);
    }

    /// <summary>A brightness byte as a percentage of 0xff, rounded.</summary>
    public static int Percent(byte value) => (int)Math.Round(value * 100 / 255.0, MidpointRounding.AwayFromZero);

    /// <summary>
    /// A percentage as the brightness byte, rounded and held to
    /// <see cref="LeastBrightness"/> to 0xff: 3 % and below write 0x08, 100 %
    /// writes 0xff, and each percentage reads back as itself.
    /// </summary>
    public static byte BrightnessByte(int percent)
        => (byte)Math.Clamp((int)Math.Round(Math.Clamp(percent, 0, 100) * 255 / 100.0, MidpointRounding.AwayFromZero), LeastBrightness, 255);

    /// <summary>
    /// A colour slot as <c>#rrggbb</c>. The white channel adds to all three,
    /// so the factory 000000ff reads as <c>#ffffff</c>.
    /// </summary>
    public static string Hex(ReadOnlySpan<byte> slot)
    {
        int white = slot[3];
        int Channel(int value) => Math.Min(255, value + white);
        return $"#{Channel(slot[2]):x2}{Channel(slot[1]):x2}{Channel(slot[0]):x2}";
    }

    /// <summary>
    /// A colour into its slot. <c>#ffffff</c> lights the white LED alone, as
    /// the unit ships; any other colour lights the red, green and blue LEDs
    /// at its own values with the white one off.
    /// </summary>
    /// <exception cref="ArgumentException">Not <c>#rrggbb</c>.</exception>
    public static void WriteColour(Span<byte> slot, string colour)
    {
        if (!TryParse(colour, out byte red, out byte green, out byte blue))
            throw new ArgumentException($"'{colour}' is not a colour as #rrggbb");
        if (red == 0xff && green == 0xff && blue == 0xff)
        {
            slot[0] = 0; slot[1] = 0; slot[2] = 0; slot[3] = 0xff;
            return;
        }
        slot[0] = blue; slot[1] = green; slot[2] = red; slot[3] = 0;
    }

    /// <summary>True for <c>#rrggbb</c> with six hexadecimal digits, in either case.</summary>
    public static bool IsColour(string? colour) => TryParse(colour, out _, out _, out _);

    /// <summary>A valid colour in lower case, as the state reports it; null otherwise.</summary>
    public static string? Normalize(string? colour) => IsColour(colour) ? colour!.ToLowerInvariant() : null;

    private static bool TryParse(string? colour, out byte red, out byte green, out byte blue)
    {
        red = green = blue = 0;
        if (colour is not { Length: 7 } || colour[0] != '#' || !colour.Skip(1).All(char.IsAsciiHexDigit)) return false;
        red = byte.Parse(colour.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        green = byte.Parse(colour.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        blue = byte.Parse(colour.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return true;
    }
}
