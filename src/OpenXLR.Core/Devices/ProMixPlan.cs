using OpenXLR.Core.Mixing;

namespace OpenXLR.Core.Devices;

/// <summary>An XLR input's zero-latency path into the jacks' mix (section 0): its membership bit and its level cell.</summary>
public readonly record struct ProMicPath(bool Direct, double LevelDb);

/// <summary>
/// A jack feed beyond the first, on a section of its own: the USB playback
/// pair it streams on and the two microphones' paths into it, at the levels
/// the feed's sends would play them at.
/// </summary>
public readonly record struct ProFeedSection(int Pair, ProMicPath Mic1, ProMicPath Mic2);

/// <summary>
/// The jack feeds on the Wave XLR Pro's own sections. Each distinct feed the
/// jacks play takes a slot, in the jack order Headphones 1, Headphones 2,
/// Line Out: slot 0 is pair 1 on section 0 (the jacks' mix as before), slot
/// 1 pair 2 on section 18 and slot 2 pair 3 on section 54. The Aux mix keeps
/// pair 5 on section 36. Sections 18 and 54 have no level byte of their own,
/// so a feed there takes <see cref="TrimDb"/> into its cells, the level byte
/// 98 gives section 0 (protocol notes, sections 16 to 18): two jacks at
/// equal faders and equal headphone volumes are equally loud.
/// </summary>
public static class ProJackFeeds
{
    public const int Slots = 3;
    public static IReadOnlyList<int> Pairs { get; } = [ProReturnPairs.Monitor, 2, 3];
    public static IReadOnlyList<int> Sections { get; } = [0, 18, 54];
    public static IReadOnlyList<byte> Selectors { get; } = [0x1E, 0x1F, 0x21];

    /// <summary>Section 0's level as byte 98 holds it on every unit read so far, which the other jack sections take into their cells.</summary>
    public const double TrimDb = -21;

    /// <summary>The slot of a section, or -1 for a section no jack feed uses.</summary>
    public static int SlotOfSection(int section) => section switch { 0 => 0, 18 => 1, 54 => 2, _ => -1 };

    /// <summary>
    /// The bytes of a section a restore puts back: its cells, membership,
    /// byte 14 and the inputs' tap fields, any input after the ducker put
    /// back to 2 (see <see cref="ProMixSections.ClearInputDucking"/>). The
    /// pairs' ducking fields stay with ducking.
    /// </summary>
    public const int RestoredLength = ProMixSections.DuckingOffset;
}

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
/// With <see cref="JackSections"/>, each jack's selector is written whenever
/// it is not the exact byte for its feed's section, and
/// <see cref="Feeds"/> and <see cref="Restore"/> give sections 18 and 54
/// to the jacks' second and third feeds and back (see <see cref="ProJackFeeds"/>);
/// <see cref="Needs(ReadOnlySpan{byte})"/> judges those on the block itself.
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

    /// <summary>
    /// The section each jack plays while it is on (0, 18 or 54, see
    /// <see cref="ProJackFeeds"/>). Null keeps the rule from before per-jack
    /// feeds: on is any selector but 23, and a jack turned on gets 1e.
    /// </summary>
    public ProJackSections? JackSections { get; init; }

    /// <summary>The jack feeds beyond the first, by section (18 or 54). A section absent here is left as held, or restored.</summary>
    public IReadOnlyDictionary<int, ProFeedSection>? Feeds { get; init; }

    /// <summary>
    /// Sections to put back as they were before a jack feed first used them,
    /// by section, as the hex of their first <see cref="ProJackFeeds.RestoredLength"/>
    /// bytes. Ignored for a section <see cref="Feeds"/> names.
    /// </summary>
    public IReadOnlyDictionary<int, string>? Restore { get; init; }

    /// <summary>
    /// Hardware mixing: the sections the unit builds, by section start (a
    /// jack's 0, 18 or 54, and 36 for the Aux mix), as their first 14 bytes
    /// (the twelve level cells and the membership mask), written exactly.
    /// Byte 14 and the tap fields stay as read, except that the plan puts
    /// any input after the ducker back to 2 in every section, as a ducking
    /// write does. A restore for
    /// a section named here is skipped. With section 36 here,
    /// <see cref="UsbAux"/> sets the port's selector alone: the section's
    /// cells are the unit's Aux mix, not pair 5's stream.
    /// </summary>
    public IReadOnlyDictionary<int, byte[]>? UnitSections { get; init; }

    /// <summary>Byte 98, section 0's level in quarter-dB attenuation, as written; null leaves it.</summary>
    public byte? Section0Level { get; init; }

    /// <summary>
    /// Sections 0 and 36 as they were before hardware mixing, by section
    /// start, as the hex of their first <see cref="ProJackFeeds.RestoredLength"/>
    /// bytes: put back first when leaving, so every other part is judged on
    /// the bytes as they were.
    /// </summary>
    public IReadOnlyDictionary<int, string>? EntryRestore { get; init; }

    /// <summary>The jacks' own levels in block 0x0005, written with the block by the driver (see <see cref="ProJackLevels"/>).</summary>
    public ProJackLevels? JackLevels { get; init; }

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
    internal const byte UsbAuxMask = 0x04;
    internal const int AuxSection = 36;
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

    /// <summary>
    /// <see cref="Needs(ProMixHeld)"/> on the whole block, with the parts only
    /// the bytes show: the section each jack's selector names, the feed
    /// sections and the restores.
    /// </summary>
    public ProMixChanges Needs(ReadOnlySpan<byte> block)
    {
        if (block.Length != ProMixSections.Length) throw new ArgumentException($"block 0x0001 is {ProMixSections.Length} bytes", nameof(block));
        if (EntryRestore is null) return NeedsCore(block);
        // Putting sections 0 and 36 back and asserting today's parts over
        // them gives the same bytes every time, so the plan is held when a
        // write would change nothing.
        Span<byte> after = stackalloc byte[ProMixSections.Length];
        ProMixChanges changes = Apply(block, after);
        return after.SequenceEqual(block) ? ProMixChanges.None : changes;
    }

    /// <summary><see cref="Needs(ReadOnlySpan{byte})"/> without <see cref="EntryRestore"/>.</summary>
    private ProMixChanges NeedsCore(ReadOnlySpan<byte> block)
    {
        ProMixHeld held = Read(block);
        ProMixChanges changes = Needs(held) & ~ProMixChanges.Selectors;
        if (UsbAuxWritten(held) || SelectorsWritten(block, held)) changes |= ProMixChanges.Selectors;
        Span<byte> copy = stackalloc byte[ProMixSections.Length];
        block.CopyTo(copy);
        if (ApplySections(block, copy)) changes |= ProMixChanges.FeedSections;
        changes |= ApplyUnit(copy);
        return changes;
    }

    /// <summary>The jacks' selector bytes the plan wants, for each jack it sets; only with <see cref="JackSections"/>.</summary>
    private IEnumerable<(int Offset, byte Wanted)> JackSelectors()
    {
        if (JackSections is not { } sections) yield break;
        if (Hp1 is { } hp1) yield return (ProMixSections.SelectorHp1, hp1 ? SelectorOf(sections.Hp1) : SelectorOff);
        if (Hp2 is { } hp2) yield return (ProMixSections.SelectorHp2, hp2 ? SelectorOf(sections.Hp2) : SelectorOff);
        if (LineOut is { } lineOut) yield return (ProMixSections.SelectorLineOut, lineOut ? SelectorOf(sections.LineOut) : SelectorOff);
    }

    private static byte SelectorOf(int section)
        => ProJackFeeds.SlotOfSection(section) is int slot and >= 0 ? ProJackFeeds.Selectors[slot]
            : throw new ArgumentException($"no jack feed plays section {section}", nameof(section));

    /// <summary>Whether a jack's selector is written: with <see cref="JackSections"/> when it is not the exact byte, without by the on and off rule.</summary>
    private bool SelectorsWritten(ReadOnlySpan<byte> block, ProMixHeld held)
    {
        if (JackSections is null)
            return Hp1 is { } hp1 && held.Hp1 != hp1 || Hp2 is { } hp2 && held.Hp2 != hp2 || LineOut is { } lineOut && held.LineOut != lineOut;
        foreach ((int offset, byte wanted) in JackSelectors())
            if (block[offset] != wanted) return true;
        return false;
    }

    /// <summary>
    /// The feed sections' and restores' edits on <paramref name="into"/>, a
    /// copy of <paramref name="held"/>. True when a byte changed.
    ///
    /// A feed section holds its pair at <see cref="ProJackFeeds.TrimDb"/>, each
    /// microphone's path when the feed's send carries it (the bit, and the
    /// level trimmed the same way while on), USB Aux's bit 2 as section 0
    /// holds it with its level trimmed, and no other source: Line In and every
    /// other pair out. The pair a feed streams on also leaves section 36, so a
    /// jack's feed never reaches the USB Aux port. Byte 14 and the tap fields
    /// stay as read.
    /// </summary>
    private bool ApplySections(ReadOnlySpan<byte> held, Span<byte> into)
    {
        bool changed = false;
        if (Restore is { } restore)
            foreach ((int section, string hex) in restore)
            {
                if (Feeds?.ContainsKey(section) == true || UnitSections?.ContainsKey(section) == true) continue;
                byte[] bytes = RestoreBytes(section, hex);
                if (held.Slice(section, bytes.Length).SequenceEqual(bytes)) continue;
                bytes.CopyTo(into[section..]);
                changed = true;
            }
        if (Feeds is not { } feeds) return changed;
        foreach ((int section, ProFeedSection feed) in feeds)
        {
            if (ProJackFeeds.SlotOfSection(section) is not (1 or 2))
                throw new ArgumentException($"section {section} carries no jack feed beyond the first", nameof(Feeds));
            if (feed.Pair is < 0 or >= ProMixSections.PairCount || feed.Pair is ProReturnPairs.Aux or ProReturnPairs.Monitor)
                throw new ArgumentException($"pair {feed.Pair} cannot carry a jack feed of its own", nameof(Feeds));
            int source = ProMixSections.SourceOfPair(feed.Pair);
            changed |= Set(into, section + source, LevelCell(ProJackFeeds.TrimDb));
            changed |= SetMicLevel(into, section + Mic1LevelOffset, feed.Mic1);
            changed |= SetMicLevel(into, section + Mic2LevelOffset, feed.Mic2);
            int usbAux = held[ReturnsOffset] & UsbAuxMask;
            if (usbAux != 0)
                changed |= Set(into, section + ProMixSections.UsbAux, LevelCell(HeldLevelDb(held[ProMixSections.UsbAux]) + ProJackFeeds.TrimDb));
            int mask = (feed.Mic1.Direct ? Mic1Mask : 0) | (feed.Mic2.Direct ? Mic2Mask : 0) | usbAux | 1 << source;
            changed |= Set(into, section + ReturnsOffset, (byte)mask);
            changed |= Set(into, section + PairsOffset, (byte)(held[section + PairsOffset] & ~OtherPairsHighMask | mask >> 8));
            int auxMask = AuxSection + ReturnsOffset + source / 8;
            changed |= Set(into, auxMask, (byte)(into[auxMask] & ~(1 << source % 8)));
        }
        return changed;
    }

    /// <summary>Sections 0 and 36 put back as <see cref="EntryRestore"/> holds them. True when a byte changed.</summary>
    private bool ApplyEntryRestore(Span<byte> into)
    {
        bool changed = false;
        if (EntryRestore is not { } restore) return false;
        foreach ((int section, string hex) in restore)
        {
            if (section is not (0 or AuxSection)) throw new ArgumentException($"section {section} is not put back on leaving", nameof(EntryRestore));
            if (!IsRestore(hex)) throw new ArgumentException($"a restore is {ProJackFeeds.RestoredLength} bytes of hex", nameof(EntryRestore));
            byte[] bytes = Restored(hex);
            if (into.Slice(section, bytes.Length).SequenceEqual(bytes)) continue;
            bytes.CopyTo(into[section..]);
            changed = true;
        }
        return changed;
    }

    /// <summary>The bytes of a section the unit builds that a plan writes: the cells and the mask.</summary>
    public const int UnitSectionLength = ProMixSections.MaskOffset + 2;

    /// <summary>
    /// Hardware mixing's edits on <paramref name="into"/>: the unit-built
    /// sections, no input after the ducker in any section, and byte 98.
    /// Returns the parts that changed a byte.
    /// </summary>
    private ProMixChanges ApplyUnit(Span<byte> into)
    {
        ProMixChanges changes = ProMixChanges.None;
        if (UnitSections is { } sections)
            foreach ((int section, byte[] bytes) in sections)
            {
                if (ProJackFeeds.SlotOfSection(section) < 0 && section != AuxSection)
                    throw new ArgumentException($"section {section} is not one a jack or the USB Aux port plays", nameof(UnitSections));
                if (bytes.Length != UnitSectionLength) throw new ArgumentException($"a unit section is {UnitSectionLength} bytes", nameof(UnitSections));
                if (into.Slice(section, UnitSectionLength).SequenceEqual(bytes)) continue;
                bytes.CopyTo(into[section..]);
                changes |= ProMixChanges.UnitSections;
            }
        if (UnitSections is not null && ProMixSections.ClearInputDucking(into)) changes |= ProMixChanges.UnitSections;
        if (Section0Level is byte level && into[ProMixSections.Section0LevelOffset] != level)
        {
            into[ProMixSections.Section0LevelOffset] = level;
            changes |= ProMixChanges.Section0Level;
        }
        return changes;
    }

    private static bool Set(Span<byte> into, int offset, byte value)
    {
        if (into[offset] == value) return false;
        into[offset] = value;
        return true;
    }

    private static bool SetMicLevel(Span<byte> into, int offset, ProMicPath mic)
    {
        double wanted = mic.LevelDb + ProJackFeeds.TrimDb;
        return mic.Direct && HalfDb(HeldLevelDb(into[offset])) != HalfDb(wanted) && Set(into, offset, LevelCell(wanted));
    }

    /// <summary>A restore's bytes, checked: hex of <see cref="ProJackFeeds.RestoredLength"/> bytes for section 18 or 54, no input after the ducker.</summary>
    /// <exception cref="ArgumentException">Another section, or hex of another length.</exception>
    public static byte[] RestoreBytes(int section, string hex)
    {
        if (ProJackFeeds.SlotOfSection(section) is not (1 or 2)) throw new ArgumentException($"section {section} is not restored", nameof(section));
        if (!IsRestore(hex)) throw new ArgumentException($"a restore is {ProJackFeeds.RestoredLength} bytes of hex", nameof(hex));
        return Restored(hex);
    }

    /// <summary>A checked restore's bytes with any input tap field at 3 put back to 2, as every write of the block leaves them.</summary>
    private static byte[] Restored(string hex) => Restored(Convert.FromHexString(hex));

    private static byte[] Restored(byte[] bytes)
    {
        bytes[ProMixSections.InputTapsOffset] = ProMixSections.WithoutInputDucking(bytes[ProMixSections.InputTapsOffset]);
        return bytes;
    }

    /// <summary>Whether a saved restore is <see cref="ProJackFeeds.RestoredLength"/> bytes of hex.</summary>
    public static bool IsRestore(string? hex)
        => hex is { Length: 2 * ProJackFeeds.RestoredLength } && hex.All(Uri.IsHexDigit);

    /// <summary>The bytes of a section a restore would put back, as hex, from a whole block: no input after the ducker.</summary>
    public static string RestoreOf(ReadOnlySpan<byte> block, int section)
        => Convert.ToHexString(Restored(block.Slice(section, ProJackFeeds.RestoredLength).ToArray()));

    /// <summary>
    /// Whether a section of the block holds what a kept restore puts back,
    /// as the restore writes it: a restore kept with an input after the
    /// ducker is held once that input reads 2.
    /// </summary>
    public static bool HoldsRestore(ReadOnlySpan<byte> block, int section, string hex)
        => IsRestore(hex) && block.Slice(section, ProJackFeeds.RestoredLength).SequenceEqual(Restored(hex));

    /// <summary>The unit builds the Aux mix in section 36 (hardware mixing): the port's selector is all <see cref="UsbAux"/> sets.</summary>
    private bool AuxInUnit => UnitSections?.ContainsKey(AuxSection) == true;

    private bool UsbAuxWritten(ProMixHeld held)
        => UsbAux is { } aux && (held.UsbAux != aux || (aux && !AuxInUnit && !held.AuxReturnEnabled));

    /// <summary>
    /// Make the plan's edits: <paramref name="into"/> gets
    /// <paramref name="held"/> with every part the plan wants changed, and
    /// the rest as read. Returns the parts that changed a byte.
    /// </summary>
    /// <exception cref="ArgumentException">A block is not 108 bytes, or the ducking target is one the unit does not take.</exception>
    public ProMixChanges Apply(ReadOnlySpan<byte> held, Span<byte> into)
    {
        if (into.Length != ProMixSections.Length) throw new ArgumentException($"block 0x0001 is {ProMixSections.Length} bytes", nameof(into));
        held.CopyTo(into);
        // Leaving hardware mixing puts sections 0 and 36 back first; every
        // other part is judged on the bytes as they were before it.
        bool unwound = ApplyEntryRestore(into);
        Span<byte> restored = stackalloc byte[ProMixSections.Length];
        into.CopyTo(restored);
        ProMixHeld was = Read(restored);
        ProMixChanges changes = NeedsCore(restored) & ~(ProMixChanges.FeedSections | ProMixChanges.UnitSections | ProMixChanges.Section0Level);
        if (unwound) changes |= ProMixChanges.FeedSections;

        if (JackSections is null)
        {
            if (Hp1 is { } hp1 && was.Hp1 != hp1) into[ProMixSections.SelectorHp1] = hp1 ? SelectorMonitor : SelectorOff;
            if (Hp2 is { } hp2 && was.Hp2 != hp2) into[ProMixSections.SelectorHp2] = hp2 ? SelectorMonitor : SelectorOff;
            if (LineOut is { } lineOut && was.LineOut != lineOut) into[ProMixSections.SelectorLineOut] = lineOut ? SelectorMonitor : SelectorOff;
        }
        else
            foreach ((int offset, byte wanted) in JackSelectors()) into[offset] = wanted;
        // The USB Aux port cannot play the jacks' mix (no USB playback pair
        // reaches it under 1e); it plays the Aux mix with pair 5's cell open,
        // as Wave Link delivers audio there, and without the unit's own
        // inputs, which the Aux mix on the computer already carries. In
        // hardware mixing the unit builds the Aux mix in section 36 itself,
        // so only the selector moves.
        if (UsbAuxWritten(was))
        {
            bool aux = UsbAux!.Value;
            into[ProMixSections.SelectorUsbAux] = aux ? SelectorAuxMix : SelectorOff;
            if (aux && !AuxInUnit)
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
        // After section 0, whose USB Aux bit the feed sections copy.
        Span<byte> before = stackalloc byte[ProMixSections.Length];
        into.CopyTo(before);
        if (ApplySections(before, into)) changes |= ProMixChanges.FeedSections;
        changes |= ApplyUnit(into);

        if (Ducking is { } ducking)
        {
            Span<byte> unducked = stackalloc byte[ProMixSections.Length];
            into.CopyTo(unducked);
            ProDucking.ApplyFields(into, ducking);
            if (!into.SequenceEqual(unducked)) changes |= ProMixChanges.DuckingFields;
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
    /// <summary>A jack feed's own section, or a section put back as it was; applies live, no commit (protocol notes, section 16).</summary>
    FeedSections = 32,
    /// <summary>A section the unit builds in hardware mixing, or section 36's members; applies live, no commit.</summary>
    UnitSections = 64,
    /// <summary>Byte 98, section 0's level; committed, as a selector is.</summary>
    Section0Level = 128,
}

/// <summary>
/// The levels of the Wave XLR Pro's jacks in block 0x0005, in dB: the
/// headphone volumes (bytes 0 and 2) and Line Out's level (byte 4), each
/// -4 x dB, 0 to -60 dB. A null leaves that level as the unit holds it. The
/// driver lowers before it writes block 0x0001 and raises after it, so no
/// transition is louder for a moment.
/// </summary>
public sealed record ProJackLevels(double? Hp1Db, double? Hp2Db, double? LineOutDb)
{
    public const int Hp1Offset = 0;
    public const int Hp2Offset = 2;
    public const int LineOutOffset = 4;

    /// <summary>The byte for a level: -4 x dB, held to 0 (0 dB) and 240 (-60 dB).</summary>
    public static byte ByteOf(double db)
        => double.IsFinite(db) ? (byte)Math.Clamp((int)Math.Round(-db * 4, MidpointRounding.AwayFromZero), 0, 240) : (byte)240;

    /// <summary>The level a byte gives.</summary>
    public static double DbOf(byte value) => -value / 4.0;

    private IEnumerable<(int Offset, double Db)> Wanted()
    {
        if (Hp1Db is double hp1) yield return (Hp1Offset, hp1);
        if (Hp2Db is double hp2) yield return (Hp2Offset, hp2);
        if (LineOutDb is double lineOut) yield return (LineOutOffset, lineOut);
    }

    /// <summary>Block 0x0005 with every level that goes down set, and the rest as held.</summary>
    public byte[] Lowered(ReadOnlySpan<byte> held)
    {
        byte[] block = held.ToArray();
        foreach ((int offset, double db) in Wanted())
            if (ByteOf(db) > block[offset]) block[offset] = ByteOf(db);
        return block;
    }

    /// <summary>Block 0x0005 with every level set.</summary>
    public byte[] All(ReadOnlySpan<byte> held)
    {
        byte[] block = held.ToArray();
        foreach ((int offset, double db) in Wanted()) block[offset] = ByteOf(db);
        return block;
    }

    /// <summary>Whether a unit holding <paramref name="state"/> holds these levels.</summary>
    public bool HeldBy(DeviceState state)
        => (Hp1Db is not double hp1 || ByteOf(hp1) == ByteOf(state.HpVolumeDb))
        && (Hp2Db is not double hp2 || ByteOf(hp2) == ByteOf(state.Hp2VolumeDb))
        && (LineOutDb is not double lineOut || ByteOf(lineOut) == ByteOf(state.LineOutLevelDb));
}

/// <summary>The section each of the Pro's jacks plays while it is on: 0, 18 or 54 (see <see cref="ProJackFeeds"/>).</summary>
public readonly record struct ProJackSections(int Hp1, int Hp2, int LineOut)
{
    /// <summary>Every jack on section 0, as before per-jack feeds.</summary>
    public static ProJackSections Shared { get; } = new(0, 0, 0);
}

public static class ProMixChangesExtensions
{
    /// <summary>A selector or membership changed: the commit Wave Link sends follows, which the selectors need.</summary>
    public static bool Commits(this ProMixChanges changes)
        => (changes & (ProMixChanges.Selectors | ProMixChanges.MonitorReturn | ProMixChanges.MicPaths | ProMixChanges.Section0Level)) != 0;

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
    /// <summary>The bytes of block 0x0005 the jack levels changed, empty when none.</summary>
    public string Change5 { get; init; } = "";

    public bool Wrote => Wrote8 || Wrote1 || Change5.Length > 0;
}
