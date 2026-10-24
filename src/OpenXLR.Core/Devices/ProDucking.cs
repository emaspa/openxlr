using OpenXLR.Core.Mixing;

namespace OpenXLR.Core.Devices;

/// <summary>
/// The Wave XLR Pro's USB playback pairs OpenXLR streams to. Pair n is
/// playback channels AUX(2n) and AUX(2n+1) of the unit's pro-audio sink.
/// </summary>
public static class ProReturnPairs
{
    /// <summary>The monitor feed of the jacks and Line Out (channels 2/3), a member of the mix the jacks play through block 0x0001 byte 12 bit 5.</summary>
    public const int Monitor = 1;

    /// <summary>The Aux mix for the USB Aux port: the aux mix's Music cell takes it (channels 10/11).</summary>
    public const int Aux = 5;
}

/// <summary>
/// What the unit held at the last read: the preset each of the three
/// fields matches across all twelve entries (<c>custom</c> when the entries
/// disagree or hold a value no preset writes), the USB pairs whose field
/// reads 11 in any of block 0x0001's three mix sections, and the block 0x0008 entries whose
/// flag bit 0 is set. <see cref="Raw"/> is the bytes the codec reads, hex,
/// so a check that the unit holds a setting needs no transfer.
/// </summary>
public sealed record DuckingReadback(string Sensitivity, string Amount, string Speed,
    IReadOnlyList<int> Pairs, IReadOnlyList<int> Entries)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string Raw { get; init; } = "";

    public bool Equals(DuckingReadback? other)
        => other is not null && Raw == other.Raw && Sensitivity == other.Sensitivity && Amount == other.Amount
            && Speed == other.Speed && Pairs.SequenceEqual(other.Pairs) && Entries.SequenceEqual(other.Entries);

    public override int GetHashCode() => Raw.GetHashCode(StringComparison.Ordinal);
}

/// <summary>
/// The Wave XLR Pro's ducking, as Wave Link 3.5 writes it (captures 24 to
/// 31 of 2026-10-21; the unit then ducks by itself, capture 28):
///
/// Block 0x0008, 96 bytes: twelve entries of eight bytes, entries 0 to 3 the
/// hardware inputs (XLR 1, XLR 2, USB Aux in, Line In), entries 4 to 11 the
/// USB playback pairs 0 to 7.
///   byte 0      key threshold, int8 dB: sensitivity Low -3, Medium -12, High -24
///   byte 1      depth, dB: amount Low 6, Medium 12, High 24
///   bytes 2, 3  attack, LE16 ms; bytes 4, 5 release, LE16 ms:
///               speed Slow 100/2000, Medium 80/1200, Fast 30/200
///   byte 6      flags: bit 0 this channel is ducked; bit 1 unknown (Wave
///               Link leaves 02 and 03 on some entries with no key at all)
///   byte 7      00, kept
/// Wave Link writes the three presets to all twelve entries.
///
/// Block 0x0001, in the last two bytes of its first three mix sections
/// (bytes 16 to 17, 34 to 35 and 52 to 53, after each section's aa): eight
/// 2-bit fields, LE, field k for USB pair k. The first section is the mix
/// the jacks play, the third the aux mix (desk probe of 2026-10-23). Wave
/// Link leaves every field at 01 and makes a ducked pair 11. The unit ducks
/// a pair only at 11: 10, which setting the high bit alone gives on a unit
/// whose fields read 00, does nothing. So a target pair gets both bits, and
/// any other pair loses the high bit and keeps its low bit as read.
///
/// How the key input is encoded was not found: only XLR 1 was ever the key,
/// and nothing in these two blocks names it. Every byte and bit not listed
/// as written is kept as read.
/// </summary>
public static class ProDucking
{
    public const ushort Block = 0x0008;
    public const int Length = 96;
    public const int EntryCount = 12;
    public const int EntryLength = 8;
    /// <summary>The entry of USB playback pair 0; pair k is entry k + 4.</summary>
    public const int FirstPairEntry = 4;
    public const int PairCount = 8;
    public const int FlagsByte = 6;
    public const byte DuckedBit = 0x01;
    /// <summary>Unknown; an entry that has it keeps its flags byte as read unless it is a target.</summary>
    public const byte UnknownBit = 0x02;

    /// <summary>The first byte of each mix section's eight 2-bit pair fields in block 0x0001.</summary>
    public static IReadOnlyList<int> MixFieldOffsets { get; } = [16, 34, 52];
    public const int MixBlockLength = 108;

    public const string KeyXlr1 = "xlr1";
    public const string Device = "device";
    /// <summary>The outputs a unit can duck: the jacks' feed on pair 1 and the USB Aux port on pair 5.</summary>
    public const string TargetMonitor = "monitor";
    public const string TargetAux = "aux";
    public const string Low = "low", Medium = "medium", High = "high", Slow = "slow", Fast = "fast", Custom = "custom";

    public static IReadOnlyList<string> Sensitivities { get; } = [Low, Medium, High];
    public static IReadOnlyList<string> Amounts { get; } = [Low, Medium, High];
    public static IReadOnlyList<string> Speeds { get; } = [Slow, Medium, Fast];

    private static readonly sbyte[] Thresholds = [-3, -12, -24];
    private static readonly byte[] Depths = [6, 12, 24];
    private static readonly (ushort Attack, ushort Release)[] Times = [(100, 2000), (80, 1200), (30, 200)];

    /// <summary>The USB playback pair an output streams on.</summary>
    public static int PairOf(string output) => output == TargetAux ? ProReturnPairs.Aux : ProReturnPairs.Monitor;

    /// <summary>Why a target cannot be written, or null when every value is one the unit takes.</summary>
    public static string? Valid(ProDuckingTarget target)
    {
        if (!Sensitivities.Contains(target.Sensitivity)) return "sensitivity must be low, medium or high";
        if (!Amounts.Contains(target.Amount)) return "amount must be low, medium or high";
        if (!Speeds.Contains(target.Speed)) return "speed must be slow, medium or fast";
        if (target.Pairs is null || target.Pairs.Any(p => p is < 0 or >= PairCount)) return $"pairs must be USB playback pairs 0 to {PairCount - 1}";
        return null;
    }

    /// <summary>The six preset bytes every entry takes for the three preset words.</summary>
    public static byte[] PresetBytes(string sensitivity, string amount, string speed)
    {
        sbyte threshold = Thresholds[IndexOf(Sensitivities, sensitivity)];
        byte depth = Depths[IndexOf(Amounts, amount)];
        (ushort attack, ushort release) = Times[IndexOf(Speeds, speed)];
        return [(byte)threshold, depth, (byte)attack, (byte)(attack >> 8), (byte)release, (byte)(release >> 8)];
    }

    private static int IndexOf(IReadOnlyList<string> names, string name)
    {
        for (int i = 0; i < names.Count; i++) if (names[i] == name) return i;
        throw new ArgumentException($"'{name}' is not one of {string.Join(", ", names)}");
    }

    /// <summary>
    /// Write a target into the two blocks: the presets while it is on, the
    /// ducked flag of every entry, and the pair fields of the three mix
    /// sections, both bits set for the target's pairs while it is on and the
    /// high bit cleared otherwise. Everything else is left as it was.
    /// </summary>
    /// <exception cref="ArgumentException">A block has the wrong length or the target a value the unit does not take.</exception>
    public static void Apply(Span<byte> block8, Span<byte> block1, ProDuckingTarget target)
    {
        if (block8.Length != Length) throw new ArgumentException($"block 0x0008 is {Length} bytes", nameof(block8));
        if (block1.Length != MixBlockLength) throw new ArgumentException($"block 0x0001 is {MixBlockLength} bytes", nameof(block1));
        if (Valid(target) is { } problem) throw new ArgumentException(problem, nameof(target));
        int pairs = WantedPairs(target);
        byte[]? preset = target.Enabled ? PresetBytes(target.Sensitivity, target.Amount, target.Speed) : null;
        for (int e = 0; e < EntryCount; e++)
        {
            Span<byte> entry = block8.Slice(e * EntryLength, EntryLength);
            preset?.CopyTo(entry);
            bool wanted = e >= FirstPairEntry && (pairs & 1 << (e - FirstPairEntry)) != 0;
            if (wanted) entry[FlagsByte] |= DuckedBit;
            else if ((entry[FlagsByte] & UnknownBit) == 0) entry[FlagsByte] &= unchecked((byte)~DuckedBit);
        }
        foreach (int at in MixFieldOffsets)
        {
            int fields = block1[at] | block1[at + 1] << 8;
            for (int k = 0; k < PairCount; k++)
            {
                int both = 3 << (2 * k), high = 1 << (2 * k + 1);
                fields = (pairs & 1 << k) != 0 ? fields | both : fields & ~high;
            }
            block1[at] = (byte)fields;
            block1[at + 1] = (byte)(fields >> 8);
        }
    }

    /// <summary>The pairs a target ducks, as a mask with bit k for pair k: none while it is off.</summary>
    public static int WantedPairs(ProDuckingTarget target)
        => target.Enabled ? target.Pairs.Aggregate(0, (mask, pair) => mask | 1 << pair) : 0;

    /// <summary>What the two blocks hold, decoded; the 96 bytes of 0x0008 and the 108 of 0x0001.</summary>
    public static DuckingReadback Decode(ReadOnlySpan<byte> block8, ReadOnlySpan<byte> block1)
    {
        if (block8.Length != Length || block1.Length != MixBlockLength)
            throw new ArgumentException("block 0x0008 is 96 bytes and block 0x0001 108");
        var raw = new byte[Length + 2 * MixFieldOffsets.Count];
        block8.CopyTo(raw);
        for (int i = 0; i < MixFieldOffsets.Count; i++)
        {
            raw[Length + 2 * i] = block1[MixFieldOffsets[i]];
            raw[Length + 2 * i + 1] = block1[MixFieldOffsets[i] + 1];
        }
        return FromRaw(raw);
    }

    private static DuckingReadback FromRaw(byte[] raw)
    {
        string sensitivity = Same(raw, 0, 1, v => Array.IndexOf(Thresholds, (sbyte)v), Sensitivities);
        string amount = Same(raw, 1, 1, v => Array.IndexOf(Depths, (byte)v), Amounts);
        string speed = Same(raw, 2, 4, v => Array.FindIndex(Times, t => (t.Attack | t.Release << 16) == v), Speeds);
        var pairs = new List<int>();
        for (int k = 0; k < PairCount; k++)
            for (int i = 0; i < MixFieldOffsets.Count; i++)
            {
                int fields = raw[Length + 2 * i] | raw[Length + 2 * i + 1] << 8;
                if ((fields >> (2 * k) & 3) == 3) { pairs.Add(k); break; }
            }
        var entries = new List<int>();
        for (int e = 0; e < EntryCount; e++)
            if ((raw[e * EntryLength + FlagsByte] & DuckedBit) != 0) entries.Add(e);
        return new DuckingReadback(sensitivity, amount, speed, pairs, entries) { Raw = Convert.ToHexString(raw) };
    }

    /// <summary>
    /// The preset a field holds in all twelve entries: the field's
    /// little-endian value at <paramref name="offset"/>, <paramref name="width"/>
    /// bytes wide, looked up by <paramref name="index"/>; <c>custom</c> when
    /// the entries disagree or no preset writes the value.
    /// </summary>
    private static string Same(byte[] block8, int offset, int width, Func<int, int> index, IReadOnlyList<string> names)
    {
        int first = 0;
        for (int e = 0; e < EntryCount; e++)
        {
            int value = 0;
            for (int b = width - 1; b >= 0; b--) value = value << 8 | block8[e * EntryLength + offset + b];
            if (e == 0) first = value;
            else if (value != first) return Custom;
        }
        return index(first) is int i and >= 0 ? names[i] : Custom;
    }

    /// <summary>
    /// Whether the unit, as last read, holds a target: writing it would
    /// change no byte. False when the readback carries no raw bytes.
    /// </summary>
    public static bool Holds(DuckingReadback unit, ProDuckingTarget target)
    {
        if (unit.Raw.Length != 2 * (Length + 2 * MixFieldOffsets.Count) || Valid(target) is not null) return false;
        byte[] raw = Convert.FromHexString(unit.Raw);
        byte[] block8 = raw[..Length];
        var block1 = new byte[MixBlockLength];
        for (int i = 0; i < MixFieldOffsets.Count; i++)
        {
            block1[MixFieldOffsets[i]] = raw[Length + 2 * i];
            block1[MixFieldOffsets[i] + 1] = raw[Length + 2 * i + 1];
        }
        byte[] want8 = (byte[])block8.Clone(), want1 = (byte[])block1.Clone();
        Apply(want8, want1, target);
        return want8.AsSpan().SequenceEqual(block8) && want1.AsSpan().SequenceEqual(block1);
    }

    /// <summary>The bytes that differ between two copies of a block, as "offset old->new" pairs, for the journal.</summary>
    public static string Diff(ReadOnlySpan<byte> before, ReadOnlySpan<byte> after)
    {
        var parts = new List<string>();
        for (int i = 0; i < Math.Min(before.Length, after.Length); i++)
            if (before[i] != after[i]) parts.Add($"{i} {before[i]:x2}->{after[i]:x2}");
        return parts.Count == 0 ? "no change" : string.Join(", ", parts);
    }
}

/// <summary>
/// What one ducking write did: for each block whether it was written, and
/// the bytes it changed. Neither written when the unit already held it.
/// </summary>
public sealed record DuckingWrite(bool Wrote8, string Change8, bool Wrote1, string Change1)
{
    public bool Wrote => Wrote8 || Wrote1;
}
