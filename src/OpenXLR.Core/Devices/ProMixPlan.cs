using OpenXLR.Core.Mixing;

namespace OpenXLR.Core.Devices;

/// <summary>An XLR input's zero-latency path into the jacks' mix (section 0): its membership bit and its level cell.</summary>
public readonly record struct ProMicPath(bool Direct, double LevelDb);

/// <summary>
/// What OpenXLR wants block 0x0001 of a Wave XLR Pro to hold while the
/// computer builds the mixes (today's mode), as one target: the four output
/// selectors, the Monitor return and the two microphones' paths in section
/// 0, the USB Aux port's cells in section 36, and the ducking fields. A null
/// field is left as the unit holds it. <see cref="Apply"/> makes the byte
/// edits from a fresh read of the block, so the block is written once for
/// all of them, and every byte the plan does not own goes back as read.
///
/// Each part is written only when the unit holds something else, judged on
/// the block decoded as <see cref="Read"/> decodes it (the state the poll
/// publishes), so the sweep that posts a plan every pass writes nothing at
/// steady state:
///   a selector    when it plays something and is wanted off, or plays
///                 nothing and is wanted on; on writes 1e (20 for the USB
///                 Aux port), off 23
///   USB Aux port  also when wanted on while its Music cell (byte 45) is not
///                 at 0 dB or pair 5 (byte 49 bit 1) is out of section 36;
///                 on opens both and takes XLR 1 and Line In (byte 48 bits 0
///                 and 3) out of that section
///   the return    when it reads otherwise (on means pair 1 a member at 0 dB
///                 with Line In and every other pair out of section 0); on
///                 sets byte 12 bit 5, opens byte 5 and clears Line In and
///                 the other pairs, off clears bit 5 alone
///   a mic path    its bit (byte 12 bit 0 for XLR 1, bit 1 for XLR 2) when it
///                 differs; its level (byte 0 or 1) only while the path is
///                 wanted and the half-dB value differs
///   ducking       the fields <see cref="ProDucking.ApplyFields"/> owns
/// </summary>
public sealed record ProMixPlan
{
    public bool? Hp1 { get; init; }
    public bool? Hp2 { get; init; }
    public bool? UsbAux { get; init; }
    public bool? LineOut { get; init; }
    public bool? MonitorReturn { get; init; }
    public ProMicPath? Mic1 { get; init; }
    public ProMicPath? Mic2 { get; init; }
    public ProDuckingTarget? Ducking { get; init; }

    /// <summary>A plan that leaves every byte as the unit holds it.</summary>
    public static ProMixPlan Nothing { get; } = new();

    // Section 0, the mix the jacks play (protocol notes, section 12). Byte
    // 12 bit 5 is USB playback pair 1, where the Monitor mix streams, and
    // bit 0 XLR 1, the microphone's zero-latency path; both measured on the
    // desk unit (2026-10-30) through the mix's capture stream.
    internal const int ReturnsOffset = ProMixSections.MaskOffset;       // byte 12
    internal const int PairsOffset = ProMixSections.MaskOffset + 1;     // byte 13
    internal const byte MonitorReturnMask = 0x20;
    internal const byte Mic1Mask = 0x01;
    // XLR 1's level in the jacks' mix, int8 in half dB like every level cell
    // (desk, 2026-10-30: 00 played the mic at the return's level, f4 6 dB
    // under it, c8 28 dB under). OpenXLR used to drive this byte as a Mic/PC
    // crossfade from 0 to 200, which put the centre at the cell's +12 dB cap.
    // A level write applies at once, with no commit and no stream restart.
    internal const int Mic1LevelOffset = 0;
    // XLR 2's membership, byte 12 bit 1, at the level in byte 1 (desk,
    // 2026-10-31, a microphone on XLR 2: absent with the bit clear, its
    // capture at 0 dB with it set, 6 dB down at f4, applied at once as XLR
    // 1's level is). It is XLR 2's zero-latency path, driven like XLR 1's.
    internal const byte Mic2Mask = 0x02;
    internal const int Mic2LevelOffset = 1;
    // Pair 1's level in the jacks' mix, 00 = 0 dB (measured +6.2 dB at 0c,
    // -6.1 dB at f4). Wave Link keeps its own fader level here, so a unit
    // that dual-boots plays the Monitor mix that far down until the cell is
    // put back to unity.
    internal const int MonitorReturnLevelOffset = 5;
    // Line In's membership in the same mix, byte 12 bit 3. A unit set up
    // elsewhere can hold it set, which plays the Line In jack straight to the
    // jacks beside the Monitor mix that already carries the Line In channel
    // (an owner's archive: byte 12 e8).
    internal const byte LineInMask = 0x08;
    // The other USB playback pairs in the same mix: pair k is source 4 + k,
    // so pairs 0, 2 and 3 are byte 12 bits 4, 6 and 7, and pairs 4 to 7 are
    // byte 13 bits 0 to 3, each measured on the desk unit (2026-10-31). Pair
    // 5 is the Aux mix's stream (byte 13 bit 1): OpenXLR used to set it as
    // the microphone's direct path, which played the whole Aux mix on the
    // jacks. A unit can hold pairs 6 and 7 set (the desk unit) or pair 0 (an
    // owner's), so anything played to those channels of the Pro's sink
    // reached the jacks with no control in OpenXLR. The jacks hear the
    // Monitor mix on pair 1 only, so the other pairs are cleared with the
    // Monitor return. Bit 2, the USB Aux input (desk, 2026-10-31, a Mac on
    // the port reached the mix at 0 dB), stays as read: an owner can hear a
    // second computer there with Aux In's send muted.
    internal const byte OtherPairsLowMask = 0xd0;
    internal const byte OtherPairsHighMask = 0x0f;

    // Section 36, the Aux mix the USB Aux port plays: pair 5's cell (byte 45)
    // and membership (byte 49 bit 1), decoded from capture 4 where music was
    // confirmed flowing to the port, and the unit's inputs in that mix (byte
    // 48 bit 0 XLR 1, bit 3 Line In), cleared when OpenXLR drives the port:
    // all audio then travels through the software Aux mix, so nothing arrives
    // twice.
    internal const int AuxMusicLevelOffset = 45;
    internal const int AuxInputsOffset = 48;
    internal const int AuxPairsOffset = 49;
    internal const byte AuxPair5Mask = 0x02;
    internal const byte AuxInputsMask = 0x09;

    // Output selectors (captures 2 to 4 and a live jack test): 1e plays
    // section 0, 20 section 36, 23 nothing. A selector change needs the
    // commit.
    internal const byte SelectorMonitor = ProMixSections.FirstSelector;
    internal const byte SelectorAuxMix = 0x20;
    internal const byte SelectorOff = ProMixSections.SelectorNothing;

    internal const int LevelFloor = -120;    // 88, -60 dB: Wave Link's floor
    internal const int LevelCeiling = 24;    // 18, +12 dB: the firmware holds anything above

    /// <summary>What block 0x0001 holds of the parts a plan owns, as the state reads them.</summary>
    public static ProMixHeld Read(ReadOnlySpan<byte> block)
    {
        if (block.Length != ProMixSections.Length) throw new ArgumentException($"block 0x0001 is {ProMixSections.Length} bytes", nameof(block));
        byte returns = block[ReturnsOffset], pairs = block[PairsOffset];
        return new ProMixHeld(
            Hp1: block[ProMixSections.SelectorHp1] != SelectorOff,
            Hp2: block[ProMixSections.SelectorHp2] != SelectorOff,
            UsbAux: block[ProMixSections.SelectorUsbAux] != SelectorOff,
            LineOut: block[ProMixSections.SelectorLineOut] != SelectorOff,
            MonitorReturn: (returns & (MonitorReturnMask | LineInMask | OtherPairsLowMask)) == MonitorReturnMask
                && (pairs & OtherPairsHighMask) == 0 && block[MonitorReturnLevelOffset] == 0x00,
            Mic1Direct: (returns & Mic1Mask) != 0,
            Mic1LevelDb: HeldLevelDb(block[Mic1LevelOffset]),
            Mic2Direct: (returns & Mic2Mask) != 0,
            Mic2LevelDb: HeldLevelDb(block[Mic2LevelOffset]),
            AuxReturnEnabled: block[AuxMusicLevelOffset] == 0x00 && (block[AuxPairsOffset] & AuxPair5Mask) != 0);
    }

    /// <summary>A mic level cell as the firmware plays it: half dB, held at +12 dB.</summary>
    private static double HeldLevelDb(byte cell) => Math.Min((int)(sbyte)cell, LevelCeiling) / 2.0;

    /// <summary>A level in dB as its cell compares: half-dB steps between -60 and +12, the floor for anything not finite.</summary>
    public static int HalfDb(double db)
        => double.IsFinite(db) ? (int)Math.Round(Math.Clamp(db, -60, 12) * 2, MidpointRounding.AwayFromZero) : LevelFloor;

    /// <summary>The level cell a mic path is written with: half dB, held to -60 to +12 dB, the floor for anything not finite.</summary>
    public static byte LevelCell(double db)
        => unchecked((byte)(sbyte)(double.IsFinite(db)
            ? Math.Clamp((int)Math.Round(db * 2, MidpointRounding.AwayFromZero), LevelFloor, LevelCeiling)
            : LevelFloor));

    /// <summary>
    /// The parts of the plan, other than ducking, that a unit holding
    /// <paramref name="held"/> would be written for. None means the plan,
    /// without its ducking, is already held.
    /// </summary>
    public ProMixChanges Needs(ProMixHeld held)
    {
        ProMixChanges changes = ProMixChanges.None;
        if (Hp1 is { } hp1 && held.Hp1 != hp1) changes |= ProMixChanges.Selectors;
        if (Hp2 is { } hp2 && held.Hp2 != hp2) changes |= ProMixChanges.Selectors;
        if (LineOut is { } lineOut && held.LineOut != lineOut) changes |= ProMixChanges.Selectors;
        if (UsbAuxWritten(held)) changes |= ProMixChanges.Selectors;
        if (MonitorReturn is { } ret && held.MonitorReturn != ret) changes |= ProMixChanges.MonitorReturn;
        if (Mic1 is { } mic1)
        {
            if (mic1.Direct && HalfDb(held.Mic1LevelDb) != HalfDb(mic1.LevelDb)) changes |= ProMixChanges.Levels;
            if (held.Mic1Direct != mic1.Direct) changes |= ProMixChanges.MicPaths;
        }
        if (Mic2 is { } mic2)
        {
            if (mic2.Direct && HalfDb(held.Mic2LevelDb) != HalfDb(mic2.LevelDb)) changes |= ProMixChanges.Levels;
            if (held.Mic2Direct != mic2.Direct) changes |= ProMixChanges.MicPaths;
        }
        return changes;
    }

    private bool UsbAuxWritten(ProMixHeld held)
        => UsbAux is { } aux && (held.UsbAux != aux || (aux && !held.AuxReturnEnabled));

    /// <summary>
    /// Make the plan's edits: <paramref name="into"/> gets
    /// <paramref name="held"/> with every part the plan wants changed, and
    /// the rest as read. Returns the parts that changed a byte.
    /// </summary>
    /// <exception cref="ArgumentException">A block is not 108 bytes, or the ducking target is one the unit does not take.</exception>
    public ProMixChanges Apply(ReadOnlySpan<byte> held, Span<byte> into)
    {
        if (into.Length != ProMixSections.Length) throw new ArgumentException($"block 0x0001 is {ProMixSections.Length} bytes", nameof(into));
        ProMixHeld was = Read(held);
        held.CopyTo(into);
        ProMixChanges changes = Needs(was);

        if (Hp1 is { } hp1 && was.Hp1 != hp1) into[ProMixSections.SelectorHp1] = hp1 ? SelectorMonitor : SelectorOff;
        if (Hp2 is { } hp2 && was.Hp2 != hp2) into[ProMixSections.SelectorHp2] = hp2 ? SelectorMonitor : SelectorOff;
        if (LineOut is { } lineOut && was.LineOut != lineOut) into[ProMixSections.SelectorLineOut] = lineOut ? SelectorMonitor : SelectorOff;
        // The USB Aux port cannot play the jacks' mix (no USB playback pair
        // reaches it under 1e); it plays the Aux mix with pair 5's cell open,
        // as Wave Link delivers audio there, and without the unit's own
        // inputs, which the Aux mix on the computer already carries.
        if (UsbAuxWritten(was))
        {
            bool aux = UsbAux!.Value;
            into[ProMixSections.SelectorUsbAux] = aux ? SelectorAuxMix : SelectorOff;
            if (aux)
            {
                into[AuxMusicLevelOffset] = 0x00;
                into[AuxPairsOffset] |= AuxPair5Mask;
                into[AuxInputsOffset] &= unchecked((byte)~AuxInputsMask);
            }
        }
        // The return on takes the Line In jack and every other USB playback
        // pair, the Aux mix's stream among them, out of the jacks' mix: the
        // jacks hear those only through the Monitor mix. The microphones'
        // bits and USB Aux's bit 2 stay as read. Off clears bit 5 alone.
        if (MonitorReturn is { } ret && was.MonitorReturn != ret)
        {
            if (ret)
            {
                into[ReturnsOffset] = (byte)((into[ReturnsOffset] | MonitorReturnMask) & ~(LineInMask | OtherPairsLowMask));
                into[PairsOffset] &= unchecked((byte)~OtherPairsHighMask);
                into[MonitorReturnLevelOffset] = 0x00;
            }
            else into[ReturnsOffset] &= unchecked((byte)~MonitorReturnMask);
        }
        ApplyMic(Mic1, was.Mic1Direct, was.Mic1LevelDb, Mic1LevelOffset, Mic1Mask, into);
        ApplyMic(Mic2, was.Mic2Direct, was.Mic2LevelDb, Mic2LevelOffset, Mic2Mask, into);

        if (Ducking is { } ducking)
        {
            Span<byte> before = stackalloc byte[ProMixSections.Length];
            into.CopyTo(before);
            ProDucking.ApplyFields(into, ducking);
            if (!into.SequenceEqual(before)) changes |= ProMixChanges.DuckingFields;
        }
        return changes;
    }

    private static void ApplyMic(ProMicPath? path, bool wasDirect, double wasLevelDb, int levelOffset, byte mask, Span<byte> into)
    {
        if (path is not { } mic) return;
        // The level goes in with the path, so the jacks never play the
        // microphone at a level another program left.
        if (mic.Direct && HalfDb(wasLevelDb) != HalfDb(mic.LevelDb)) into[levelOffset] = LevelCell(mic.LevelDb);
        if (wasDirect != mic.Direct)
            into[ReturnsOffset] = (byte)(mic.Direct ? into[ReturnsOffset] | mask : into[ReturnsOffset] & ~mask);
    }
}

/// <summary>What block 0x0001 holds of the parts a <see cref="ProMixPlan"/> owns, decoded as the state reads them.</summary>
public sealed record ProMixHeld(bool Hp1, bool Hp2, bool UsbAux, bool LineOut, bool MonitorReturn,
    bool Mic1Direct, double Mic1LevelDb, bool Mic2Direct, double Mic2LevelDb, bool AuxReturnEnabled)
{
    /// <summary>The same parts from a polled state.</summary>
    public static ProMixHeld Of(DeviceState state) => new(state.OutHp1, state.OutHp2, state.OutUsbAux, state.OutLineOut,
        state.HpMixMonitorReturn, state.HpMixMicDirect, state.HpMixMicLevelDb, state.HpMixMic2Direct, state.HpMixMic2LevelDb,
        state.AuxReturnEnabled);
}

/// <summary>The parts of block 0x0001 a plan changed.</summary>
[Flags]
public enum ProMixChanges
{
    None = 0,
    /// <summary>An output selector, with the USB Aux port's cells when it goes on.</summary>
    Selectors = 1,
    /// <summary>The Monitor return's membership in the jacks' mix.</summary>
    MonitorReturn = 2,
    /// <summary>A microphone's path into the jacks' mix.</summary>
    MicPaths = 4,
    /// <summary>A microphone's level cell; applies at once, no commit.</summary>
    Levels = 8,
    /// <summary>The ducking fields; no commit, as Wave Link sends none.</summary>
    DuckingFields = 16,
}

public static class ProMixChangesExtensions
{
    /// <summary>A selector or membership changed: the commit Wave Link sends follows, which the selectors need.</summary>
    public static bool Commits(this ProMixChanges changes)
        => (changes & (ProMixChanges.Selectors | ProMixChanges.MonitorReturn | ProMixChanges.MicPaths)) != 0;

    /// <summary>The jacks' mix membership changed: the caller restarts the Monitor playback stream, as before.</summary>
    public static bool Membership(this ProMixChanges changes)
        => (changes & (ProMixChanges.MonitorReturn | ProMixChanges.MicPaths)) != 0;
}

/// <summary>
/// What one write of a <see cref="ProMixPlan"/> did: the parts that changed,
/// for each block whether it was written and the bytes it changed (block
/// 0x0008 only when the plan carried ducking), and whether the commit
/// followed. Nothing written when the unit already held the plan.
/// </summary>
public sealed record ProMixWrite(ProMixChanges Changes, bool Wrote8, string Change8, bool Wrote1, string Change1, bool Committed)
{
    public bool Wrote => Wrote8 || Wrote1;
}
