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
/// What the unit held at the last read: the value each of the three fields
/// holds across all twelve entries, as the preset word when a preset writes
/// it and as the number otherwise (<c>custom</c> when the entries
/// disagree), the USB pairs whose field reads 11 in any of block 0x0001's
/// five mix sections, the block 0x0008 entries any source keys (a bit of
/// the entry's key mask set beside the entry's own), the unit inputs whose
/// field reads 11 in any section (<see cref="Inputs"/>) and the sources
/// that key any entry (<see cref="Keys"/>, in the entries' numbering).
/// <see cref="Raw"/> is the bytes the codec reads, hex, so a check that the
/// unit holds a setting needs no transfer; it compares every byte, so a key
/// left beside the ones written makes the unit not hold OpenXLR's setting.
/// </summary>
public sealed record DuckingReadback(DuckingLevel Sensitivity, DuckingLevel Amount, DuckingSpeed Speed,
    IReadOnlyList<int> Pairs, IReadOnlyList<int> Entries)
{
    /// <summary>The unit inputs ducked in any section: 0 XLR 1, 1 XLR 2, 2 USB Aux, 3 Line In.</summary>
    public IReadOnlyList<int> Inputs { get; init; } = [];

    /// <summary>The sources set in any entry's key mask beside the entry's own: 0 to 3 the inputs, 4 + k pair k.</summary>
    public IReadOnlyList<int> Keys { get; init; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public string Raw { get; init; } = "";

    public bool Equals(DuckingReadback? other)
        => other is not null && Raw == other.Raw && Sensitivity == other.Sensitivity && Amount == other.Amount
            && Speed == other.Speed && Pairs.SequenceEqual(other.Pairs) && Entries.SequenceEqual(other.Entries)
            && Inputs.SequenceEqual(other.Inputs) && Keys.SequenceEqual(other.Keys);

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
///   bytes 6, 7  the key mask, LE16, bit M for source M in the entries'
///               order (0 XLR 1, 1 XLR 2, 2 USB Aux in, 3 Line In, 4 + k
///               pair k): every source whose bit is set keys this entry,
///               and the unit's key level is the loudest of them. An
///               entry's own bit in its own mask is ignored.
/// Wave Link writes the three presets to all twelve entries, sets a key's
/// bit when it is made one and never clears a bit, so units carry leftover
/// masks (02 and 03 seen with no key chosen at all). OpenXLR writes the
/// union of the target's keys (<see cref="ProDuckingTarget.Keys"/>), less
/// the entry's own bit, on each entry it ducks, and 0000 on every other,
/// leftovers included, so no source it did not choose ducks what it set up.
///
/// Block 0x0001, in the last two bytes of each of its five mix sections
/// (bytes 16 to 17, 34 to 35, 52 to 53, 70 to 71 and 88 to 89): eight
/// 2-bit tap fields, LE, field k for USB pair k. The first
/// section is the mix the jacks play, the third the aux mix (desk probe of
/// 2026-10-23), the second and fourth the jacks' second and third feeds; the
/// fields work in the fourth and fifth too (protocol notes, section 16,
/// probe P5). Wave Link leaves every field at 01 and makes a ducked pair 11.
/// The unit ducks a pair only at 11: 10, which setting the high bit alone
/// gives on a unit whose fields read 00, does nothing. So a target pair gets
/// both bits, and any other pair loses the high bit and keeps its low bit as
/// read. The depth is per block 0x0008 entry, so a pair ducked in two
/// sections dips by the same amount in both.
///
/// A target without <see cref="ProDuckingTarget.Sections"/> ducks its pairs
/// in the first four sections, as a whole output (today's mode, the fifth
/// left as read). One with sections (hardware mixing, where the unit builds
/// each jack's mix from the channels' pairs) ducks each pair only in the
/// sections it names, and covers all five.
///
/// The byte before them in each section (15, 33, 51, 69 and 87) holds the
/// four inputs' tap fields; field 3 there puts an input after the ducker,
/// which ducks it in that section's mix. A target's
/// <see cref="ProDuckingTarget.Inputs"/> get 3 in the sections it ducks
/// (<see cref="InputSections"/>), and every other input field read at 3 goes
/// back to 2. Every byte and bit not listed as written is kept as read.
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
    /// <summary>Bytes 6 and 7 of an entry: its key mask, LE16, bit M set when source M keys the entry.</summary>
    public const int KeyMaskOffset = 6;
    /// <summary>The key mask of XLR 1 alone, a target's keys unless it names others.</summary>
    public const ushort KeyXlr1Mask = 1 << ProMixSections.Xlr1;
    /// <summary>Every source a key mask can name: the four inputs and the eight pairs.</summary>
    public const ushort AllSourcesMask = (1 << ProMixSections.SourceCount) - 1;

    /// <summary>
    /// The first byte of the eight 2-bit pair fields of the four mix sections
    /// a whole-output target covers in block 0x0001 (sections 0 to 54).
    /// </summary>
    public static IReadOnlyList<int> MixFieldOffsets { get; } = [16, 34, 52, 70];

    /// <summary>The same for all five sections (0 to 72), which a target with sections covers and the readback decodes.</summary>
    public static IReadOnlyList<int> SectionFieldOffsets { get; } = [16, 34, 52, 70, 88];
    public const int MixBlockLength = 108;

    /// <summary>The bytes of each section the readback carries: the inputs' tap fields, then the two of pair fields.</summary>
    private const int SectionRawLength = 3;
    private const int RawLength = Length + SectionRawLength * ProMixSections.SectionCount;

    public const string KeyXlr1 = "xlr1";
    public const string Device = "device";
    /// <summary>The outputs a unit can duck: the jacks' feeds on pairs 1 to 3 and the USB Aux port on pair 5.</summary>
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
        if (target.Sections is { } sections && sections.Any(s => !ProMixSections.Starts.Contains(s.Key) || s.Value is null || !s.Value.IsSubsetOf(target.Pairs)))
            return "sections must be block 0x0001's section starts, each with pairs the target ducks";
        if ((target.Keys & ~AllSourcesMask) != 0) return $"keys must be sources 0 to {ProMixSections.SourceCount - 1}";
        if (target.Inputs is null || target.Inputs.Any(i => i is < 0 or >= ProMixSections.InputCount))
            return $"inputs must be unit inputs 0 to {ProMixSections.InputCount - 1}";
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
    /// key mask of every entry, and the tap fields of the mix sections: both
    /// bits set for the target's pairs while it is on and the high bit
    /// cleared otherwise, and its inputs after the ducker in the sections it
    /// ducks with no other input left there. Everything else is left as it
    /// was.
    /// </summary>
    /// <exception cref="ArgumentException">A block has the wrong length or the target a value the unit does not take.</exception>
    public static void Apply(Span<byte> block8, Span<byte> block1, ProDuckingTarget target)
    {
        if (block1.Length != MixBlockLength) throw new ArgumentException($"block 0x0001 is {MixBlockLength} bytes", nameof(block1));
        ApplyEntries(block8, target);
        ApplyFields(block1, target);
    }

    /// <summary>
    /// Block 0x0008's part of <see cref="Apply"/>: the target's values in
    /// every entry while it is on, and each entry's key mask: on each entry
    /// the target ducks while it is on (its pairs' and, when it ducks them
    /// in any section, its inputs'), the target's keys less the entry's own
    /// bit, and no key on every other entry.
    /// </summary>
    /// <exception cref="ArgumentException">The block has the wrong length or the target a value the unit does not take.</exception>
    public static void ApplyEntries(Span<byte> block8, ProDuckingTarget target)
    {
        if (block8.Length != Length) throw new ArgumentException($"block 0x0008 is {Length} bytes", nameof(block8));
        if (Valid(target) is { } problem) throw new ArgumentException(problem, nameof(target));
        int ducked = WantedPairs(target) << FirstPairEntry | (InputSections(target).Count > 0 ? Mask(target.Inputs) : 0);
        byte[]? preset = target.Enabled ? SettingBytes(target.Sensitivity, target.Amount, target.Speed) : null;
        for (int e = 0; e < EntryCount; e++)
        {
            Span<byte> entry = block8.Slice(e * EntryLength, EntryLength);
            preset?.CopyTo(entry);
            ushort keys = (ducked & 1 << e) != 0 ? (ushort)(target.Keys & ~(1 << e)) : (ushort)0;
            entry[KeyMaskOffset] = (byte)keys;
            entry[KeyMaskOffset + 1] = (byte)(keys >> 8);
        }
    }

    /// <summary>
    /// Block 0x0001's part of <see cref="Apply"/>, which
    /// <see cref="ProMixPlan"/> writes with the rest of that block: the pair
    /// fields of the four mix sections, or of all five, each with its own
    /// pairs, for a target with <see cref="ProDuckingTarget.Sections"/>; and
    /// the inputs' tap fields of all five, the target's inputs at 3 in the
    /// sections <see cref="InputSections"/> names and no other input there.
    /// </summary>
    /// <exception cref="ArgumentException">The block has the wrong length or the target a value the unit does not take.</exception>
    public static void ApplyFields(Span<byte> block1, ProDuckingTarget target)
    {
        if (block1.Length != MixBlockLength) throw new ArgumentException($"block 0x0001 is {MixBlockLength} bytes", nameof(block1));
        if (Valid(target) is { } problem) throw new ArgumentException(problem, nameof(target));
        if (target.Sections is { } sections)
        {
            foreach (int section in ProMixSections.Starts)
                SetFields(block1, section + ProMixSections.DuckingOffset,
                    target.Enabled && sections.TryGetValue(section, out IReadOnlySet<int>? ducked) ? Mask(ducked) : 0);
        }
        else
        {
            int pairs = WantedPairs(target);
            foreach (int at in MixFieldOffsets) SetFields(block1, at, pairs);
        }
        IReadOnlyDictionary<int, int> inputs = InputSections(target);
        foreach (int section in ProMixSections.Starts)
        {
            int at = section + ProMixSections.InputTapsOffset;
            block1[at] = ProMixSections.WithInputDucking(block1[at], inputs.GetValueOrDefault(section));
        }
    }

    /// <summary>
    /// The sections a target ducks its inputs in, to the inputs as a mask
    /// (bit M for input M); empty while it is off or has no input. A target
    /// with <see cref="ProDuckingTarget.Sections"/> ducks them in every
    /// section it names. A whole-output target ducks them in the section
    /// each of its pairs plays as a whole output: a jack feed's (pair 1 on
    /// section 0, 2 on 18, 3 on 54) and the Aux mix's (pair 5 on 36).
    /// </summary>
    public static IReadOnlyDictionary<int, int> InputSections(ProDuckingTarget target)
    {
        var sections = new SortedDictionary<int, int>();
        if (!target.Enabled || target.Inputs is not { Count: > 0 } inputs) return sections;
        int mask = Mask(inputs);
        if (target.Sections is { } named)
            foreach (int section in named.Keys) sections[section] = mask;
        else
            foreach (int pair in target.Pairs)
                if (SectionOfPair(pair) is int section) sections[section] = mask;
        return sections;
    }

    /// <summary>The section a USB pair plays as a whole output, or null for a pair no output plays that way.</summary>
    private static int? SectionOfPair(int pair)
    {
        if (pair == ProReturnPairs.Aux) return ProMixPlan.AuxSection;
        for (int slot = 0; slot < ProJackFeeds.Slots; slot++)
            if (ProJackFeeds.Pairs[slot] == pair) return ProJackFeeds.Sections[slot];
        return null;
    }

    private static int Mask(IEnumerable<int> pairs) => pairs.Aggregate(0, (mask, pair) => mask | 1 << pair);

    /// <summary>One section's fields: both bits for each pair in <paramref name="pairs"/>, the high bit cleared for the others.</summary>
    private static void SetFields(Span<byte> block1, int at, int pairs)
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

    /// <summary>The pairs a target ducks, as a mask with bit k for pair k: none while it is off.</summary>
    public static int WantedPairs(ProDuckingTarget target)
        => target.Enabled ? Mask(target.Pairs) : 0;

    /// <summary>What the two blocks hold, decoded; the 96 bytes of 0x0008 and the 108 of 0x0001.</summary>
    public static DuckingReadback Decode(ReadOnlySpan<byte> block8, ReadOnlySpan<byte> block1)
    {
        if (block8.Length != Length || block1.Length != MixBlockLength)
            throw new ArgumentException("block 0x0008 is 96 bytes and block 0x0001 108");
        var raw = new byte[RawLength];
        block8.CopyTo(raw);
        for (int i = 0; i < ProMixSections.SectionCount; i++)
            block1.Slice(ProMixSections.Starts[i] + ProMixSections.InputTapsOffset, SectionRawLength)
                .CopyTo(raw.AsSpan(Length + SectionRawLength * i));
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
        var inputs = new List<int>();
        for (int k = 0; k < PairCount; k++)
            for (int i = 0; i < ProMixSections.SectionCount; i++)
            {
                int at = Length + SectionRawLength * i + 1;
                int fields = raw[at] | raw[at + 1] << 8;
                if ((fields >> (2 * k) & 3) == 3) { pairs.Add(k); break; }
            }
        for (int input = 0; input < ProMixSections.InputCount; input++)
            for (int i = 0; i < ProMixSections.SectionCount; i++)
                if ((raw[Length + SectionRawLength * i] >> (2 * input) & 3) == 3) { inputs.Add(input); break; }
        var entries = new List<int>();
        int keys = 0;
        for (int e = 0; e < EntryCount; e++)
        {
            int mask = (raw[e * EntryLength + KeyMaskOffset] | raw[e * EntryLength + KeyMaskOffset + 1] << 8) & ~(1 << e);
            if (mask != 0) entries.Add(e);
            keys |= mask;
        }
        return new DuckingReadback(sensitivity, amount, speed, pairs, entries)
        {
            Inputs = inputs,
            Keys = [.. Enumerable.Range(0, 16).Where(m => (keys & 1 << m) != 0)],
            Raw = Convert.ToHexString(raw),
        };
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
    /// change no byte, so a key mask with any bit beside the one written, or
    /// an input left after the ducker, is not held. False when the readback
    /// carries no raw bytes.
    /// </summary>
    public static bool Holds(DuckingReadback unit, ProDuckingTarget target)
    {
        if (unit.Raw.Length != 2 * RawLength || Valid(target) is not null) return false;
        byte[] raw = Convert.FromHexString(unit.Raw);
        byte[] block8 = raw[..Length];
        var block1 = new byte[MixBlockLength];
        for (int i = 0; i < ProMixSections.SectionCount; i++)
            raw.AsSpan(Length + SectionRawLength * i, SectionRawLength)
                .CopyTo(block1.AsSpan(ProMixSections.Starts[i] + ProMixSections.InputTapsOffset));
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
