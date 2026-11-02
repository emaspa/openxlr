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

    /// <summary>
    /// How far below its cell the unit plays a USB playback pair in every mix
    /// section, in dB: a fixed 7.94 on every pair measured (protocol notes,
    /// section 16), while the unit's own inputs come back at their cell.
    /// </summary>
    public const double LossDb = 7.94;
}

/// <summary>
/// What the unit held at the last read: the value each of the three fields
/// holds across all twelve entries, as the preset word when a preset writes
/// it and as the number otherwise (<c>custom</c> when the entries
/// disagree), the USB pairs whose field reads 11 in any of block 0x0001's
/// three mix sections, and the block 0x0008 entries whose flag bit 0 is
/// set. <see cref="Raw"/> is the bytes the codec reads, hex, so a check
/// that the unit holds a setting needs no transfer.
/// </summary>
public sealed record DuckingReadback(DuckingLevel Sensitivity, DuckingLevel Amount, DuckingSpeed Speed,
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
/// OpenXLR writes these bytes from the setup: a preset word's values, or a
/// custom number as is (only the presets were ever seen on a unit).
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

    // The custom numbers a setup takes, whole numbers within both the
    // software stage's controls (native/duck.c) and the unit's bytes, and
    // narrowed to what a voice key can use.
    public const int MinThreshold = -60, MaxThreshold = 0;
    public const int MinDepth = 1, MaxDepth = 40;
    public const int MinAttack = 1, MaxAttack = 1000;
    public const int MinRelease = 10, MaxRelease = 10000;

    public const string SensitivityProblem = "sensitivity must be low, medium, high or a whole number of dBFS from -60 to 0";
    public const string AmountProblem = "amount must be low, medium, high or a whole number of dB from 1 to 40";
    public const string SpeedProblem = "speed must be slow, medium, fast or {attack, release} in whole ms, attack 1 to 1000 and release 10 to 10000";

    /// <summary>The USB playback pair an output streams on.</summary>
    public static int PairOf(string output) => output == TargetAux ? ProReturnPairs.Aux : ProReturnPairs.Monitor;

    /// <summary>The key threshold in dBFS a sensitivity sets, or null for an unknown word or a number out of range.</summary>
    public static int? ThresholdOf(DuckingLevel sensitivity)
        => sensitivity.Word is { } word ? Find(Sensitivities, word) is int i and >= 0 ? Thresholds[i] : null
            : sensitivity.Db is int db and >= MinThreshold and <= MaxThreshold ? db : null;

    /// <summary>The depth in dB an amount sets, or null for an unknown word or a number out of range.</summary>
    public static int? DepthOf(DuckingLevel amount)
        => amount.Word is { } word ? Find(Amounts, word) is int i and >= 0 ? Depths[i] : null
            : amount.Db is int db and >= MinDepth and <= MaxDepth ? db : null;

    /// <summary>The attack and release in ms a speed sets, or null for an unknown word or times out of range.</summary>
    public static (int Attack, int Release)? TimesOf(DuckingSpeed speed)
        => speed.Word is { } word ? Find(Speeds, word) is int i and >= 0 ? (Times[i].Attack, Times[i].Release) : null
            : speed is { Attack: int attack and >= MinAttack and <= MaxAttack, Release: int release and >= MinRelease and <= MaxRelease }
                ? (attack, release) : null;

    private static int Find(IReadOnlyList<string> names, string name)
    {
        for (int i = 0; i < names.Count; i++) if (names[i] == name) return i;
        return -1;
    }

    /// <summary>Why the three values cannot be used, or null when each is a known word or a number in range.</summary>
    public static string? CheckValues(DuckingLevel? sensitivity, DuckingLevel? amount, DuckingSpeed? speed)
        => sensitivity is null || ThresholdOf(sensitivity) is null ? SensitivityProblem
            : amount is null || DepthOf(amount) is null ? AmountProblem
            : speed is null || TimesOf(speed) is null ? SpeedProblem : null;

    /// <summary>Why a target cannot be written, or null when every value is one the unit takes.</summary>
    public static string? Valid(ProDuckingTarget target)
    {
        if (CheckValues(target.Sensitivity, target.Amount, target.Speed) is { } problem) return problem;
        if (target.Pairs is null || target.Pairs.Any(p => p is < 0 or >= PairCount)) return $"pairs must be USB playback pairs 0 to {PairCount - 1}";
        return null;
    }

    /// <summary>The six bytes every entry takes for the three values, preset words or numbers.</summary>
    /// <exception cref="ArgumentException">A value is an unknown word or a number out of range.</exception>
    public static byte[] SettingBytes(DuckingLevel sensitivity, DuckingLevel amount, DuckingSpeed speed)
    {
        if (CheckValues(sensitivity, amount, speed) is { } problem) throw new ArgumentException(problem);
        int threshold = ThresholdOf(sensitivity)!.Value, depth = DepthOf(amount)!.Value;
        (int attack, int release) = TimesOf(speed)!.Value;
        return [unchecked((byte)(sbyte)threshold), (byte)depth, (byte)attack, (byte)(attack >> 8), (byte)release, (byte)(release >> 8)];
    }

    /// <summary>
    /// Write a target into the two blocks: its values while it is on, the
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
        byte[]? preset = target.Enabled ? SettingBytes(target.Sensitivity, target.Amount, target.Speed) : null;
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
        DuckingLevel sensitivity = Same(raw, 0, 1) is int t
            ? Array.IndexOf(Thresholds, (sbyte)t) is int ti and >= 0 ? Sensitivities[ti] : (int)(sbyte)t : Custom;
        DuckingLevel amount = Same(raw, 1, 1) is int d
            ? Array.IndexOf(Depths, (byte)d) is int di and >= 0 ? Amounts[di] : d : Custom;
        DuckingSpeed speed = Same(raw, 2, 4) is int times
            ? Array.FindIndex(Times, p => (p.Attack | p.Release << 16) == times) is int si and >= 0 ? Speeds[si]
                : DuckingSpeed.Times(times & 0xffff, (int)((uint)times >> 16))
            : Custom;
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
    /// The value a field holds in all twelve entries: its little-endian
    /// value at <paramref name="offset"/>, <paramref name="width"/> bytes
    /// wide, or null when the entries disagree.
    /// </summary>
    private static int? Same(byte[] block8, int offset, int width)
    {
        int first = 0;
        for (int e = 0; e < EntryCount; e++)
        {
            int value = 0;
            for (int b = width - 1; b >= 0; b--) value = value << 8 | block8[e * EntryLength + offset + b];
            if (e == 0) first = value;
            else if (value != first) return null;
        }
        return first;
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
