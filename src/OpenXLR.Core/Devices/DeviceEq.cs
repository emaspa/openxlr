namespace OpenXLR.Core.Devices;

/// <summary>
/// One band of a unit's own EQ in the unit's raw form: what its settings
/// block holds, byte for byte. <see cref="Type"/> is 0 high pass, 1 low
/// shelf, 2 bell, 3 notch, 4 high shelf, 5 low pass, the order of
/// <c>MicEq.Types</c>. <see cref="Gain"/> is in quarter decibels and
/// <see cref="Q"/> is round(64 log10 Q).
/// </summary>
public sealed record DeviceEqBand(bool Enabled, int Type, int Gain, int Frequency, int Q);

/// <summary>
/// A unit's EQ in its raw form: the on switch and four bands. With
/// <see cref="Bands"/> null it names the switch alone, which is what
/// releasing a unit's EQ writes: the bands stay as they are.
/// </summary>
public sealed record DeviceEq(bool Enabled, IReadOnlyList<DeviceEqBand>? Bands)
{
    /// <summary>The switch off and nothing else: the bands the unit holds are kept.</summary>
    public static DeviceEq Off { get; } = new(false, null);

    /// <summary>Whether this names the switch alone.</summary>
    public bool SwitchOnly => Bands is null;

    public bool Equals(DeviceEq? other)
        => other is not null && Enabled == other.Enabled
            && (ReferenceEquals(Bands, other.Bands) || Bands is not null && other.Bands is not null && Bands.SequenceEqual(other.Bands));

    public override int GetHashCode() => HashCode.Combine(Enabled, Bands?.Count ?? -1);

    /// <summary>
    /// Whether the fields this value names match what a unit holds: the
    /// switch, and the bands when it names them. Compared in the raw form,
    /// so the one Q byte no grid point reaches cannot hide a difference.
    /// </summary>
    public bool HeldBy(DeviceEq held)
        => Enabled == held.Enabled && (Bands is null || held.Bands is not null && Bands.SequenceEqual(held.Bands));

    public override string ToString() => Bands is null ? $"switch {(Enabled ? "on" : "off")}"
        : $"{(Enabled ? "on" : "off")}; " + string.Join("; ", Bands.Select((b, i) =>
            $"band {i + 1} {(b.Enabled ? "on" : "off")} type {b.Type} gain {b.Gain / 4.0:0.##} dB {b.Frequency} Hz q {b.Q}"));
}

/// <summary>
/// Whether a unit runs the microphone EQ on its own DSP, and when it does
/// not though its model can, why: <see cref="Reason"/> is a word a client
/// can explain (<c>firmwareOld</c>, <c>firmwareUnknown</c>,
/// <c>unsupportedBlock</c>) and <see cref="Note"/> the sentence the daemon
/// gives. Null on a model without a hardware EQ.
/// </summary>
public sealed record DeviceEqSupport(bool Ready, string? Reason = null, string? Note = null)
{
    public const string FirmwareOld = "firmwareOld";
    public const string FirmwareUnknown = "firmwareUnknown";
    public const string UnsupportedBlock = "unsupportedBlock";

    /// <summary>The first firmware the model runs the EQ on, as major.minor.patch; null where the gate did not run.</summary>
    public string? MinimumFirmware { get; init; }

    /// <summary>
    /// The widest band gain, in dB either way, the unit is written: 20 on a
    /// model measured holding and applying it, 12 on one measured to 12 only.
    /// A microphone whose EQ holds a band beyond it runs the EQ in software.
    /// </summary>
    public double MaxGain { get; init; } = WaveFxEq.DefaultMaxGainDb;
}

/// <summary>
/// The four-band EQ in one 38-byte input struct of the Wave FX settings
/// block (block <c>0x0004</c>), as the Wave XLR MK.2 and the XLR Dock MK.2
/// hold it from firmware 1.0.15 and the Wave:3 MK.2 from its firmware 0.2.3,
/// and as Wave Link 3.5 writes it:
///
///   byte 2, bit 0          the EQ's on switch
///   bytes 18, 23, 28, 33   flags: bit 0 band on, bits 1 to 3 the type, bits 4 to 7 kept
///   bytes 19, 24, 29, 34   gain, int8, quarter dB
///   bytes 20 and 21, ...   frequency, uint16 little endian, Hz
///   bytes 22, 27, 32, 37   Q, int8, round(64 log10 Q)
///
/// Every other byte and bit belongs to another control (gain, the flag
/// byte at 1, ClipGuard at byte 2 bit 2, voice tune strength, unmapped live
/// parameters) and is never touched here. The Wave XLR Pro's block holds
/// two structs of the same shape, XLR 1's at 0 and XLR 2's at 38, from
/// firmware 1.0.14, so the codec takes a span and <see cref="InputBase"/>
/// says where an input's struct starts.
/// </summary>
public static class WaveFxEq
{
    /// <summary>The length of one input struct.</summary>
    public const int StructLength = 38;
    public const int SwitchOffset = 2;
    public const byte SwitchMask = 0x01;
    public const int FirstBand = 18;
    public const int BandStride = 5;
    public const int Bands = 4;
    private const byte BandOnMask = 0x01;
    private const byte TypeMask = 0x0E;
    private const byte OwnedFlagBits = BandOnMask | TypeMask;

    /// <summary>Where input 1 or 2's struct starts in a settings block that holds one per input.</summary>
    public static int InputBase(int input) => (input - 1) * StructLength;

    /// <summary>
    /// The ranges a band must decode into, the ranges the EQ itself takes.
    /// Gain is -80 to 80 quarter decibels, -20 to +20 dB, read from every
    /// model; what each model is written is held to its
    /// <see cref="DeviceEqSupport.MaxGain"/>.
    /// </summary>
    public const int MaxType = 5, MinGain = -80, MaxGain = 80, MinFrequency = 20, MaxFrequency = 20000, MinQ = -64, MaxQ = 64;

    /// <summary>
    /// The widest band gain, in dB either way, written to a model whose EQ was
    /// measured to that range: the Wave XLR MK.2 on firmware 1.0.15, where a
    /// bell at 703 Hz read +21 dB at +20 (byte <c>50</c>) and -19 dB at -20
    /// (byte <c>b0</c>) on the USB capture, and both bytes read back as
    /// written (desk measurement, 2026-11-11).
    /// </summary>
    public const double MeasuredMaxGainDb = 20;

    /// <summary>
    /// The widest band gain, in dB either way, written to a model whose EQ was
    /// measured only from -12 to +12 dB: the Wave XLR Pro, the XLR Dock MK.2
    /// and the Wave:3 MK.2.
    /// </summary>
    public const double DefaultMaxGainDb = 12;

    /// <summary>Read the EQ from an input struct, raw, whatever the values. Null when the struct is too short.</summary>
    public static DeviceEq? Decode(ReadOnlySpan<byte> input)
    {
        if (input.Length < StructLength) return null;
        var bands = new DeviceEqBand[Bands];
        for (int i = 0; i < Bands; i++)
        {
            int at = FirstBand + i * BandStride;
            byte flags = input[at];
            bands[i] = new DeviceEqBand((flags & BandOnMask) != 0, (flags & TypeMask) >> 1, (sbyte)input[at + 1],
                input[at + 2] | input[at + 3] << 8, (sbyte)input[at + 4]);
        }
        return new DeviceEq((input[SwitchOffset] & SwitchMask) != 0, bands);
    }

    /// <summary>
    /// Whether every band holds a value the EQ takes: a known type, a
    /// frequency from 20 to 20000 Hz, a gain from -20 to +20 dB and a Q
    /// from 0.1 to 10. A unit whose block does not is not driven. Any model
    /// is read across the whole gain range, so a band another program
    /// stored beyond what OpenXLR writes to that model still decodes.
    /// </summary>
    public static bool Decodes(DeviceEq? eq)
        => eq?.Bands is { Count: Bands } bands && bands.All(b => b.Type is >= 0 and <= MaxType
            && b.Frequency is >= MinFrequency and <= MaxFrequency && b.Gain is >= MinGain and <= MaxGain && b.Q is >= MinQ and <= MaxQ);

    /// <summary>
    /// Whether every band of <paramref name="eq"/> stays within
    /// <paramref name="maxGainDb"/> either way; a value that names the switch
    /// alone always does.
    /// </summary>
    public static bool FitsGain(DeviceEq eq, double maxGainDb)
        => eq.Bands is not { } bands || bands.All(b => Math.Abs(b.Gain) <= maxGainDb * 4);

    /// <summary>
    /// Write an EQ into an input struct: the switch, and the bands when the
    /// value names them. Flags bits 4 to 7 and every byte the EQ does not
    /// own keep what the struct held.
    /// </summary>
    /// <exception cref="ArgumentException">The struct is short or a value is outside the EQ's ranges.</exception>
    public static void Encode(Span<byte> input, DeviceEq eq)
    {
        if (input.Length < StructLength) throw new ArgumentException($"an input struct is {StructLength} bytes", nameof(input));
        if (eq.Bands is { } bands && (bands.Count != Bands || !Decodes(eq)))
            throw new ArgumentException("the EQ holds a value outside the unit's ranges", nameof(eq));
        input[SwitchOffset] = eq.Enabled ? (byte)(input[SwitchOffset] | SwitchMask) : (byte)(input[SwitchOffset] & ~SwitchMask);
        if (eq.Bands is null) return;
        for (int i = 0; i < Bands; i++)
        {
            DeviceEqBand band = eq.Bands[i];
            int at = FirstBand + i * BandStride;
            input[at] = (byte)(input[at] & ~OwnedFlagBits | band.Type << 1 | (band.Enabled ? BandOnMask : 0));
            input[at + 1] = (byte)(sbyte)band.Gain;
            input[at + 2] = (byte)(band.Frequency & 0xFF);
            input[at + 3] = (byte)(band.Frequency >> 8);
            input[at + 4] = (byte)(sbyte)band.Q;
        }
    }

    /// <summary>
    /// A firmware version in block <c>0x0000</c> bytes 5 to 7 (major, minor,
    /// patch), or null when the block is too short to hold one.
    /// </summary>
    public static Version? FirmwareVersion(ReadOnlySpan<byte> identity)
        => identity.Length >= 8 ? new Version(identity[5], identity[6], identity[7]) : null;

    /// <summary>The first firmware the Wave XLR MK.2 and the XLR Dock MK.2 run the EQ on: 1.0.15, measured on both models.</summary>
    public static Version Mk2EqFirmware { get; } = new(1, 0, 15);

    /// <summary>
    /// The first firmware the Wave XLR Pro is driven on: 1.0.14, the version
    /// Wave Link was captured writing its EQ to. Whether an older one runs it
    /// is not known.
    /// </summary>
    public static Version ProEqFirmware { get; } = new(1, 0, 14);

    /// <summary>A version as the state reports it, major.minor.patch.</summary>
    public static string Format(Version version) => $"{version.Major}.{version.Minor}.{version.Build}";

    /// <summary>
    /// The firmware gate both families share: the EQ runs on the unit from
    /// <paramref name="minimum"/>, a newer version included, when the EQ
    /// fields the unit holds decode. An unknown or older version leaves it to
    /// software, and so does a block that does not decode; each says why.
    /// <paramref name="blockDecodes"/> is asked only once the version passes.
    /// </summary>
    public static DeviceEqSupport Gate(string model, Version? version, Version minimum, Func<bool> blockDecodes)
    {
        string least = Format(minimum);
        DeviceEqSupport support;
        if (version is null)
            support = new(false, DeviceEqSupport.FirmwareUnknown,
                $"The firmware version of this {model} could not be read, so its EQ runs in software.");
        else if (version < minimum)
            support = new(false, DeviceEqSupport.FirmwareOld,
                $"This {model} has firmware {Format(version)}. From {least} the unit runs the EQ itself; Elgato's updater is in Wave Link on Windows or macOS.");
        else if (!blockDecodes())
            support = new(false, DeviceEqSupport.UnsupportedBlock,
                $"This {model} holds EQ settings OpenXLR does not recognise, so its EQ runs in software. Collect diagnostics for a device report.");
        else support = new(true);
        return support with { MinimumFirmware = least };
    }

    /// <summary>What a connection note adds when the EQ fields do not decode.</summary>
    public const string UnsupportedBlockNote =
        "the EQ fields of the settings block do not decode, so the microphone EQ runs in software. Collect diagnostics for a device report.";
}
