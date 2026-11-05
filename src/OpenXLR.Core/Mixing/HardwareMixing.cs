using System.Globalization;
using OpenXLR.Core.Devices;

namespace OpenXLR.Core.Mixing;

/// <summary>Where the mixes the Wave XLR Pro's outputs play are built: <c>mixing</c> in mixer.json and <c>setMixing</c>.</summary>
public static class MixingMode
{
    /// <summary>The computer builds every mix, as before hardware mixing (the default).</summary>
    public const string Software = "software";
    /// <summary>The Wave XLR Pro builds the mixes its outputs play.</summary>
    public const string Hardware = "hardware";

    public static bool IsValid(string? value) => value is Software or Hardware;
}

/// <summary>What the daemon knows of the driven unit, for hardware mixing's eligibility.</summary>
public sealed record HardwareMixingUnit
{
    /// <summary>The driven unit is a Wave XLR Pro.</summary>
    public bool Pro { get; init; }
    public bool Connected { get; init; }
    /// <summary>The version block 0x0000 reports, major.minor.patch, or null before it is read.</summary>
    public string? Firmware { get; init; }
    /// <summary>Block 0x0001 was read and its five sections decode (aa at each section's byte 15).</summary>
    public bool SectionsDecode { get; init; }
}

/// <summary>Everything <see cref="HardwareMixingPlanner.Plan"/> reads: the mixer's model, the unit's facts and the saved maps.</summary>
public sealed record HardwareMixingSetup
{
    /// <summary><c>mixing</c> is <c>hardware</c>.</summary>
    public bool Wanted { get; init; }
    /// <summary>The software mixer runs (the graph is built).</summary>
    public bool MixerRunning { get; init; }
    public HardwareMixingUnit? Unit { get; init; }
    /// <summary>Port counts of the Pro's raw sink and source as PipeWire shows them, null when not found.</summary>
    public int? SinkPorts { get; init; }
    public int? SourcePorts { get; init; }
    /// <summary>The writer gave up on the mode this run (two reads that did not hold the plan).</summary>
    public bool Failed { get; init; }

    public IReadOnlyList<ChannelDefinition> Channels { get; init; } = [];
    public IReadOnlyList<MixDefinition> Mixes { get; init; } = [];

    /// <summary>
    /// The level each channel plays at in each mix, by <c>channel|mix</c>, in
    /// dB: the send, the channel volume and the mix's volume on the desktop's
    /// cubic scale (<c>60 log10</c> per factor), negative infinity while the
    /// send, the mix or an exclusive group mutes it. A missing entry is silent.
    /// </summary>
    public IReadOnlyDictionary<string, double> LevelsDb { get; init; } = new Dictionary<string, double>();

    /// <summary>The selected jacks with the feed each plays, in jack order (<c>hp1</c>, <c>hp2</c>, <c>lineout</c>).</summary>
    public IReadOnlyList<(string Jack, string Feed)> Jacks { get; init; } = [];

    /// <summary>The unit's own inputs the layout carries raw: source 0 XLR 1, 1 XLR 2, 2 USB Aux, 3 Line In, to the channel that carries it.</summary>
    public IReadOnlyDictionary<int, string> UnitSources { get; init; } = new Dictionary<int, string>();

    /// <summary>
    /// Channels software changes before their sends (noise removal, the voice
    /// changer, an insert, the soft low cut, a software EQ), and the one under
    /// Mic setup, which plays its take back through the channel.
    /// </summary>
    public IReadOnlySet<string> Processed { get; init; } = new HashSet<string>();

    /// <summary>
    /// Unit inputs the monitor mixes hear dry (<c>setMonitorDry</c>): the
    /// jacks' sections take them from the unit's own input even while they
    /// are processed, and only the Aux mix's section plays their return pair.
    /// </summary>
    public IReadOnlySet<string> MonitorDry { get; init; } = new HashSet<string>();

    /// <summary>
    /// Each mix's volume in dB on the desktop's cubic scale, negative infinity
    /// while it is muted: what an audition, which plays into the mixes the
    /// monitor outputs play, is heard at on the cue pair.
    /// </summary>
    public IReadOnlyDictionary<string, double> MixVolumesDb { get; init; } = new Dictionary<string, double>();

    /// <summary>Channels whose input reaches the jacks through the unit's direct path in software mode.</summary>
    public IReadOnlySet<string> DirectPath { get; init; } = new HashSet<string>();

    /// <summary>Mixes with an insert chain.</summary>
    public IReadOnlySet<string> MixChains { get; init; } = new HashSet<string>();

    /// <summary>The monitoring pause is on.</summary>
    public bool MonitorMuted { get; init; }

    /// <summary>The USB Aux port plays the Aux mix (<c>setAuxPortEnabled</c>), so the unit builds it in section 36.</summary>
    public bool AuxPort { get; init; }

    /// <summary>Section 0's level as the unit holds it in byte 98 (-21 dB on every unit read so far).</summary>
    public double Section0LevelDb { get; init; } = HardwareMixingPlanner.Section0LevelDb;

    /// <summary><c>hardwarePairs</c> as saved: channel id to USB playback pair.</summary>
    public IReadOnlyDictionary<string, int> SavedPairs { get; init; } = new Dictionary<string, int>();

    /// <summary><c>hardwareSections</c> as saved: feed to section start.</summary>
    public IReadOnlyDictionary<string, int> SavedSections { get; init; } = new Dictionary<string, int>();
}

/// <summary>A level the unit cannot play as the faders set it: the cell holds it at the ceiling.</summary>
public sealed record HardwareMixingClamp(string Channel, string Feed, double WantedDb, double HeldDb);

/// <summary>
/// One section the unit builds: the twelve level cells and the membership
/// mask (bytes 0 to 13 of the section), in source order (0 XLR 1, 1 XLR 2,
/// 2 USB Aux, 3 Line In, 4 + k pair k).
/// </summary>
public sealed record HardwareSectionContent(string Feed, int Section, IReadOnlyList<byte> Cells, ushort Members)
{
    /// <summary>Bytes 0 to 13 of the section as hex, cells then the mask low byte first.</summary>
    public string Hex => Convert.ToHexString([.. Cells, (byte)Members, (byte)(Members >> 8)]);
}

/// <summary>
/// The outcome of <see cref="HardwareMixingPlanner.Plan"/>: whether the mode
/// can run and why not, the pair of every channel that needs one, the section
/// of every feed a jack plays with its bytes, the levels held at the ceiling,
/// and what the mode turns off, as the sentence each client shows.
/// </summary>
public sealed record HardwareMixingPlan
{
    public bool Wanted { get; init; }
    /// <summary>Wanted and nothing in the way.</summary>
    public bool Active => Wanted && Reason is null;
    /// <summary>
    /// Why the mode cannot run, whether or not it is wanted: <c>mixerOff</c>,
    /// <c>notSupported</c>, <c>unitAway</c>, <c>firmware</c>,
    /// <c>unsupportedBlock</c>, <c>captureLayout</c>, <c>budget</c> or
    /// <c>failed</c>; null when it can.
    /// </summary>
    public string? Reason { get; init; }
    /// <summary>One sentence for the reason, or null.</summary>
    public string? Note { get; init; }

    /// <summary>Channel id to USB playback pair, for every channel that streams on one.</summary>
    public IReadOnlyDictionary<string, int> Pairs { get; init; } = new Dictionary<string, int>();
    /// <summary>
    /// A unit input software processes, to the pair its processed audio plays
    /// back to the unit on (a return pair): the unit's mixes take it from
    /// there instead of the raw input. An input absent here while processed
    /// reaches them raw, with a <c>processedMic:&lt;id&gt;</c> note.
    /// </summary>
    public IReadOnlyDictionary<string, int> ReturnPairs { get; init; } = new Dictionary<string, int>();
    /// <summary>The pair soundboard auditions play on, a member of the jacks' sections only; null when none is free.</summary>
    public int? CuePair { get; init; }
    /// <summary>
    /// Feed to section start, for every feed a selected jack plays, and the
    /// Aux mix to section 36 while the USB Aux port plays it.
    /// </summary>
    public IReadOnlyDictionary<string, int> Sections { get; init; } = new Dictionary<string, int>();
    /// <summary>Jack (<c>hp1</c>, <c>hp2</c>, <c>lineout</c>) to the section it plays.</summary>
    public IReadOnlyDictionary<string, int> Jacks { get; init; } = new Dictionary<string, int>();
    /// <summary>The bytes of each section the unit builds.</summary>
    public IReadOnlyList<HardwareSectionContent> Content { get; init; } = [];
    public IReadOnlyList<HardwareMixingClamp> Clamped { get; init; } = [];
    /// <summary>What the mode turns off, by fixed key, as the sentence a client shows.</summary>
    public IReadOnlyDictionary<string, string> Disabled { get; init; } = new Dictionary<string, string>();
    /// <summary>
    /// How far each jack's level (headphone volume or Line Out level) is
    /// lowered on entering the mode, in dB, by jack (see
    /// <see cref="HardwareMixingPlanner.EntryOffsetDb"/>).
    /// </summary>
    public IReadOnlyDictionary<string, double> EntryOffsetsDb { get; init; } = new Dictionary<string, double>();
}

/// <summary>
/// Hardware mixing on the Wave XLR Pro, as a pure plan from the mixer's model
/// (design notes: the hardware mixing plan, sections 3.2, 3.3, 3.7, 3.8 and
/// 3.11). Nothing here touches the unit or the graph.
///
/// The unit builds only the mixes its outputs play: the jacks' feeds, and
/// the Aux mix in section 36 while the USB Aux port is on. Every channel the
/// unit cannot take raw streams on a USB playback pair of its own: pair 0
/// System, 1 Game, 2 Music, 3 Browser, 4 Voice Chat, 6 SFX, then 7 and 5 for
/// the channels beyond those. Pair 5 carried the Aux mix's stream before the
/// unit built the Aux mix; in the mode nothing of the computer's plays there.
/// The unit's own inputs (XLR 1, XLR 2, USB Aux, Line In) are sources of
/// their own, and Aux In never joins the Aux mix's section, which would send
/// the second computer its own audio back. A channel keeps its pair across
/// restarts and layout edits (<c>hardwarePairs</c>), a jack feed its section
/// (<c>hardwareSections</c>).
///
/// Each level cell is the fader's: the send, the channel volume and the mix
/// volume, in half-dB steps. A pair plays 7.94 dB below its cell on the unit
/// and a cell above 0 dB does not make that up (protocol notes, section 16),
/// so pairs and unit inputs keep the balance Wave Link gives them, a pair cell
/// never goes above 0 dB, and a unit input's cell stops at +12 dB.
/// </summary>
public static class HardwareMixingPlanner
{
    /// <summary>The pair the Aux mix streams on outside the mode; in it, a channel pair like the others.</summary>
    public const int AuxPair = ProReturnPairs.Aux;

    /// <summary>The section starts a jack feed can take, in the order a new feed takes them.</summary>
    public static IReadOnlyList<int> JackSections { get; } = [0, 18, 54];

    /// <summary>The section the USB Aux port plays (selector 20), which the unit builds the Aux mix in.</summary>
    public const int AuxSection = 36;

    /// <summary>Ports of the Pro's raw sink and source in the layout the mode was measured in.</summary>
    public const int SinkPortsNeeded = 17;
    public const int SourcePortsNeeded = 18;

    /// <summary>The oldest firmware the mode was measured on.</summary>
    public static readonly Version MinimumFirmware = new(1, 0, 14);

    /// <summary>A USB playback pair plays this far below its cell (protocol notes, section 16).</summary>
    public const double PairLossDb = 7.94;

    /// <summary>Section 0's level, byte 98, on every unit read so far; the mode writes it to 0 dB.</summary>
    public const double Section0LevelDb = -21;

    private static readonly Dictionary<string, int> DefaultPairs = new(StringComparer.Ordinal)
    {
        ["system"] = 0, ["game"] = 1, ["music"] = 2, ["browser"] = 3, ["voicechat"] = 4, ["sfx"] = 6,
    };

    /// <summary>The pairs a channel can take, in the order a channel without a default takes them.</summary>
    public static IReadOnlyList<int> ChannelPairs { get; } = [7, 5, 0, 1, 2, 3, 4, 6];

    public static string BudgetNote(int needed)
        => $"Hardware mixing on the Wave XLR Pro streams at most {ChannelPairs.Count} channels; this layout has {needed} that need a USB pair.";

    public static HardwareMixingPlan Plan(HardwareMixingSetup setup)
    {
        (string? reason, string? note) = Eligibility(setup);
        List<ChannelDefinition> needing = [.. setup.Channels.Where(c => !setup.UnitSources.Values.Contains(c.Id))];
        Dictionary<string, int> pairs = [];
        if (needing.Count > ChannelPairs.Count)
        {
            reason ??= "budget";
            note ??= BudgetNote(needing.Count);
        }
        else pairs = AllocatePairs(needing.Select(c => c.Id), setup.SavedPairs);
        (Dictionary<string, int> returns, int? cue) = pairs.Count == needing.Count ? Claim(setup, pairs) : ([], null);

        Dictionary<string, int> sections = AllocateSections(setup.Jacks.Select(j => j.Feed).Distinct(), setup.SavedSections);
        var jacks = setup.Jacks.ToDictionary(j => j.Jack, j => sections[j.Feed], StringComparer.Ordinal);
        if (AuxMixOf(setup) is { } aux) sections[aux.Id] = AuxSection;
        var clamped = new List<HardwareMixingClamp>();
        var content = new List<HardwareSectionContent>();
        if (pairs.Count == needing.Count)
            foreach ((string feed, int section) in sections.OrderBy(p => p.Value))
                content.Add(Build(setup, feed, section, pairs, returns, cue, clamped));

        return new HardwareMixingPlan
        {
            Wanted = setup.Wanted,
            Reason = reason,
            Note = note,
            Pairs = pairs,
            ReturnPairs = returns,
            CuePair = cue,
            Sections = sections,
            Jacks = jacks,
            Content = content,
            Clamped = clamped,
            Disabled = Disabled(setup, sections.Keys, pairs, returns, cue),
            // A dry input plays raw on the jacks, so it gains what any unit input does.
            EntryOffsetsDb = setup.Jacks.ToDictionary(j => j.Jack,
                j => EntryOffsetDb(setup, j.Feed, returns.Keys.Where(c => !setup.MonitorDry.Contains(c))), StringComparer.Ordinal),
        };
    }

    /// <summary>The first thing in the way of the mode, with its sentence; nulls when nothing is.</summary>
    private static (string? Reason, string? Note) Eligibility(HardwareMixingSetup setup)
    {
        if (!setup.MixerRunning) return ("mixerOff", "Hardware mixing needs the software mixer running.");
        if (setup.Unit is not { Pro: true } unit) return ("notSupported", "Hardware mixing needs a Wave XLR Pro as the driven unit.");
        if (!unit.Connected) return ("unitAway", "The Wave XLR Pro is not connected; hardware mixing starts when it is back.");
        if (!FirmwareSupported(unit.Firmware))
            return ("firmware", $"Hardware mixing needs Wave XLR Pro firmware {MinimumFirmware} or later; this unit reports {unit.Firmware ?? "no version"}.");
        if (!unit.SectionsDecode) return ("unsupportedBlock", "The Wave XLR Pro's mixes are not in the layout hardware mixing knows.");
        if (setup.SinkPorts != SinkPortsNeeded || setup.SourcePorts != SourcePortsNeeded)
            return ("captureLayout", $"Hardware mixing needs the Wave XLR Pro's audio in its {SinkPortsNeeded}-channel playback and {SourcePortsNeeded}-channel capture layout.");
        if (setup.Failed) return ("failed", "The Wave XLR Pro did not keep the mixes OpenXLR wrote, so hardware mixing stopped for this run.");
        return (null, null);
    }

    /// <summary>The Aux mix the unit builds in section 36: the layout's Aux mix while the USB Aux port is on, else null.</summary>
    private static MixDefinition? AuxMixOf(HardwareMixingSetup setup)
        => setup.AuxPort ? setup.Mixes.FirstOrDefault(m => m.Kind == MixKind.AuxPort) : null;

    internal static bool FirmwareSupported(string? firmware)
        => firmware is not null && Version.TryParse(firmware, out Version? version) && version >= MinimumFirmware;

    /// <summary>
    /// A pair for every channel that needs one, in layout order: the saved
    /// pair when it is a channel pair and still free, then the channel's
    /// default, then the first free pair in <see cref="ChannelPairs"/> order.
    /// The caller has checked the budget.
    /// </summary>
    internal static Dictionary<string, int> AllocatePairs(IEnumerable<string> channels, IReadOnlyDictionary<string, int> saved)
    {
        List<string> order = [.. channels];
        var pairs = new Dictionary<string, int>(StringComparer.Ordinal);
        var taken = new HashSet<int>();
        foreach (string channel in order)
            if (saved.TryGetValue(channel, out int pair) && ChannelPairs.Contains(pair) && taken.Add(pair)) pairs[channel] = pair;
        foreach (string channel in order.Where(c => !pairs.ContainsKey(c)))
            if (DefaultPairs.TryGetValue(channel, out int pair) && taken.Add(pair)) pairs[channel] = pair;
        foreach (string channel in order.Where(c => !pairs.ContainsKey(c)))
        {
            int free = ChannelPairs.First(p => !taken.Contains(p));
            taken.Add(free);
            pairs[channel] = free;
        }
        return pairs;
    }

    /// <summary>
    /// The pairs no channel takes, claimed on demand in order (plan, section
    /// 3.2): first each unit input software processes, in source order, for
    /// its processed audio; then the cue, for auditions. Channels come first
    /// and keep their pairs, so a channel added later takes a pair from these
    /// claims, and the claim it takes falls back (the input raw, auditions
    /// refused) with a note. An input the monitor mixes hear dry needs its
    /// return only for the Aux mix's section, so it claims none while the
    /// unit builds no Aux mix.
    /// </summary>
    internal static (Dictionary<string, int> Returns, int? Cue) Claim(HardwareMixingSetup setup, IReadOnlyDictionary<string, int> pairs)
    {
        Queue<int> free = new(FreePairs(pairs.Values));
        var returns = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach ((int _, string channel) in setup.UnitSources.OrderBy(s => s.Key))
            if (NeedsReturn(setup, channel) && !returns.ContainsKey(channel) && free.TryDequeue(out int pair)) returns[channel] = pair;
        return (returns, free.TryDequeue(out int cue) ? cue : null);
    }

    /// <summary>Whether a unit input's processed audio has a section to play in: it is processed, and heard processed on the jacks or in the Aux mix.</summary>
    private static bool NeedsReturn(HardwareMixingSetup setup, string channel)
        => setup.Processed.Contains(channel) && (!setup.MonitorDry.Contains(channel) || AuxMixOf(setup) is not null);

    /// <summary>The channel pairs not in <paramref name="taken"/>, in <see cref="ChannelPairs"/> order.</summary>
    internal static IEnumerable<int> FreePairs(IEnumerable<int> taken)
    {
        HashSet<int> used = [.. taken];
        return ChannelPairs.Where(p => !used.Contains(p));
    }

    /// <summary>A section for every feed: the saved one when still free, then the first free in <see cref="JackSections"/> order.</summary>
    internal static Dictionary<string, int> AllocateSections(IEnumerable<string> feeds, IReadOnlyDictionary<string, int> saved)
    {
        List<string> order = [.. feeds];
        var sections = new Dictionary<string, int>(StringComparer.Ordinal);
        var taken = new HashSet<int>();
        foreach (string feed in order)
            if (saved.TryGetValue(feed, out int section) && JackSections.Contains(section) && taken.Add(section)) sections[feed] = section;
        foreach (string feed in order.Where(f => !sections.ContainsKey(f)))
        {
            int free = JackSections.First(s => !taken.Contains(s));
            taken.Add(free);
            sections[feed] = free;
        }
        return sections;
    }

    /// <summary>
    /// A section's cells and mask for one feed. A source plays at the sum of
    /// its levels in the feed's mixes (amplitudes add); it is a member while
    /// that is at or above the lowest cell that plays and, on a jack, the
    /// monitoring pause is off (the pause is the monitor outputs', so the
    /// Aux mix keeps playing). A source no channel carries is out, and so is
    /// Aux In in the Aux mix's section. A unit input with a return pair plays
    /// from that pair, processed, and its own source stays out, so the voice
    /// is never heard twice; on a jack's section a dry input plays from its
    /// own source instead, and its pair stays out. The cue pair is in the jacks' sections only, at
    /// the volume of the feed's mixes, as an audition into those mixes is.
    /// </summary>
    private static HardwareSectionContent Build(HardwareMixingSetup setup, string feed, int section,
        IReadOnlyDictionary<string, int> pairs, IReadOnlyDictionary<string, int> returns, int? cue, List<HardwareMixingClamp> clamped)
    {
        var cells = Enumerable.Repeat(ProMixSections.CellOff, ProMixSections.SourceCount).ToArray();
        ushort members = 0;
        IReadOnlyList<string> parts = MonitorFeed.Parts(feed);
        bool aux = section == AuxSection;
        bool paused = setup.MonitorMuted && !aux;
        void Source(int source, string channel, double ceilingDb) => Level(source, channel, FeedLevelDb(setup, channel, parts), ceilingDb);
        void Level(int source, string channel, double db, double ceilingDb)
        {
            if (!double.IsFinite(db)) return;
            double held = Math.Min(db, ceilingDb);
            if (Math.Round(db * 2, MidpointRounding.AwayFromZero) > Math.Round(ceilingDb * 2))
                clamped.Add(new HardwareMixingClamp(channel, feed, Round(db), held));
            cells[source] = ProMixSections.CellOf(held);
            if (!paused && cells[source] != ProMixSections.CellOff) members |= (ushort)(1 << source);
        }
        foreach ((int source, string channel) in setup.UnitSources)
        {
            if (source is < 0 or >= ProMixSections.FirstPair || aux && source == ProMixSections.UsbAux) continue;
            if (returns.TryGetValue(channel, out int back) && (aux || !setup.MonitorDry.Contains(channel)))
                Source(ProMixSections.SourceOfPair(back), channel, 0);
            else Source(source, channel, ProMixSections.CellCeiling / 2.0);
        }
        foreach ((string channel, int pair) in pairs)
            Source(ProMixSections.SourceOfPair(pair), channel, 0);
        if (cue is int cuePair && !aux) Level(ProMixSections.SourceOfPair(cuePair), CueName, FeedVolumeDb(setup, parts), 0);
        return new HardwareSectionContent(feed, section, cells, members);
    }

    /// <summary>The name the cue's held level carries in <see cref="HardwareMixingPlan.Clamped"/>.</summary>
    public const string CueName = "cue";

    /// <summary>A feed's mixes' volumes summed as amplitudes, in dB: what an audition into each of them is heard at together.</summary>
    internal static double FeedVolumeDb(HardwareMixingSetup setup, IReadOnlyList<string> parts)
    {
        double amplitude = 0;
        foreach (string mix in parts)
            if (setup.MixVolumesDb.TryGetValue(mix, out double db) && double.IsFinite(db))
                amplitude += Math.Pow(10, db / 20);
        return amplitude > 0 ? 20 * Math.Log10(amplitude) : double.NegativeInfinity;
    }

    private static double Round(double db) => Math.Round(db, 2, MidpointRounding.AwayFromZero);

    /// <summary>A channel's level in a feed: the amplitudes of its levels in the feed's mixes summed, in dB.</summary>
    internal static double FeedLevelDb(HardwareMixingSetup setup, string channel, IReadOnlyList<string> parts)
    {
        double amplitude = 0;
        foreach (string mix in parts)
            if (setup.LevelsDb.TryGetValue($"{channel}|{mix}", out double db) && double.IsFinite(db))
                amplitude += Math.Pow(10, db / 20);
        return amplitude > 0 ? 20 * Math.Log10(amplitude) : double.NegativeInfinity;
    }

    /// <summary>
    /// How far a jack's level (its headphone volume, or Line Out's level) is
    /// lowered on entering the mode, so no source gets louder on it. Byte 98
    /// goes from -21 dB (or what the unit holds there, if lower) to 0 dB,
    /// which every source on section 0 gains, and the trim on sections 18 and
    /// 54 gave the other feeds 21 dB: the larger of the two. A unit
    /// input the jack heard through the Monitor mix on a USB pair (Line In,
    /// USB Aux, a microphone the direct path did not carry) now plays as a
    /// source of its own, without the pair's 7.94 dB: 7.94 dB more while one
    /// is in the feed. A processed input on a return pair (<paramref name="onPairs"/>)
    /// still comes through a pair, so it gains nothing. Leaving the mode gives
    /// the whole offset back (see <see cref="LeaveLevelDb"/>).
    /// </summary>
    public static double EntryOffsetDb(HardwareMixingSetup setup, string feed, IEnumerable<string>? onPairs = null)
    {
        IReadOnlyList<string> parts = MonitorFeed.Parts(feed);
        HashSet<string> returned = [.. onPairs ?? []];
        bool fromPair = setup.UnitSources.Values.Any(channel => !setup.DirectPath.Contains(channel) && !returned.Contains(channel)
            && double.IsFinite(FeedLevelDb(setup, channel, parts)));
        return Math.Max(-Section0LevelDb, -setup.Section0LevelDb) + (fromPair ? PairLossDb : 0);
    }

    /// <summary>
    /// A jack's level in the mode: its base (the level it had, lowered by the
    /// entry offset, and every change the owner makes after) with the output
    /// volume's attenuation, as the Pro sink gave it to the pairs before. Never
    /// above the base: above 100% the output volume does nothing on the jacks.
    /// </summary>
    public static double JackLevelDb(double baseDb, double? outputVolume)
        => baseDb + Mixer.JackOutputVolumeDb(outputVolume);

    /// <summary>
    /// A jack's level after leaving the mode: its base with the entry offset
    /// back, so the jack is where the owner had it before the mode, moved by
    /// whatever they changed in it, less what the Pro sink's volume above
    /// 100% will add to the pairs again. A source the mode heard without the
    /// pair's 7.94 dB goes back through a pair, so nothing is louder than
    /// before the mode.
    /// </summary>
    public static double LeaveLevelDb(double baseDb, double offsetDb, double? outputVolume)
        => baseDb + offsetDb - AboveFullDb(outputVolume);

    /// <summary>
    /// The headphone levels of a saved state (a profile, the last settings,
    /// a reset's baseline) as the unit plays them in the mode. A saved level
    /// is the jack as software mixing plays it, with section 0 21 dB down;
    /// the mode runs section 0 at 0 dB, so a saved level written as it is
    /// would play the jack louder by the entry offset. This is the level that
    /// <see cref="LeaveLevelDb"/> turns back into the saved one.
    /// </summary>
    public static DeviceState ToUnitJackLevels(DeviceState saved, HardwareEntry entry) => saved with
    {
        HpVolumeDb = UnitLevelDb(saved.HpVolumeDb, entry, "hp1"),
        Hp2VolumeDb = UnitLevelDb(saved.Hp2VolumeDb, entry, "hp2"),
    };

    /// <summary>
    /// The headphone levels to save from a unit in the mode: where leaving
    /// would put them, so a profile saved in the mode recalls the same
    /// loudness in either mode (see <see cref="ToUnitJackLevels"/>).
    /// </summary>
    public static DeviceState ToSavedJackLevels(DeviceState unit, HardwareEntry entry) => unit with
    {
        HpVolumeDb = SavedLevelDb(unit.HpVolumeDb, entry, "hp1"),
        Hp2VolumeDb = SavedLevelDb(unit.Hp2VolumeDb, entry, "hp2"),
    };

    private static double EntryOffsetDbOf(HardwareEntry entry, string jack)
        => entry.Offsets?.GetValueOrDefault(jack, -Section0LevelDb) ?? -Section0LevelDb;

    // What the Pro sink's volume above 100% adds to the pairs, in dB.
    private static double AboveFullDb(double? outputVolume) => outputVolume is > 1 and var v ? 60 * Math.Log10(v) : 0;

    private static double UnitLevelDb(double savedDb, HardwareEntry entry, string jack)
        => ProJackLevels.DbOf(ProJackLevels.ByteOf(
            JackLevelDb(savedDb - EntryOffsetDbOf(entry, jack) + AboveFullDb(entry.OutputVolume), entry.OutputVolume)));

    private static double SavedLevelDb(double unitDb, HardwareEntry entry, string jack)
    {
        double output = Mixer.JackOutputVolumeDb(entry.OutputVolume);
        // With the output volume at 0 the unit sits at its floor, and the base says where the jack is.
        double baseDb = double.IsFinite(output) ? unitDb - output : entry.Jacks.GetValueOrDefault(jack, unitDb);
        return ProJackLevels.DbOf(ProJackLevels.ByteOf(LeaveLevelDb(baseDb, EntryOffsetDbOf(entry, jack), entry.OutputVolume)));
    }

    /// <summary>The fixed keys of what the mode turns off for this layout, with each sentence.</summary>
    private static Dictionary<string, string> Disabled(HardwareMixingSetup setup, IEnumerable<string> feeds,
        IReadOnlyDictionary<string, int> pairs, IReadOnlyDictionary<string, int> returns, int? cue)
    {
        var disabled = new SortedDictionary<string, string>(StringComparer.Ordinal);
        HashSet<string> unitMixes = [.. feeds.SelectMany(MonitorFeed.Parts)];
        foreach (string mix in unitMixes)
        {
            MixDefinition? definition = setup.Mixes.FirstOrDefault(m => m.Id == mix);
            string name = definition?.Name ?? mix;
            string where = definition?.Kind == MixKind.AuxPort ? "its USB Aux port" : "its jacks";
            disabled[$"inserts:mix:{mix}"] = $"The Wave XLR Pro builds {name} for {where}, so no insert runs on it there.";
        }
        if (unitMixes.Count > 0)
        {
            disabled["latencyCompensation"] = "Latency compensation leaves out the mixes the Wave XLR Pro builds.";
            // The unit ducks the mixes it builds itself, keyed by XLR 1 only,
            // and never lowers its own inputs.
            disabled["ducking:key"] = "The Wave XLR Pro ducks the mixes it builds only with XLR 1 as the key.";
            foreach (string channel in setup.UnitSources.Where(s => s.Key != ProMixSections.Xlr1).Select(s => s.Value))
            {
                string name = setup.Channels.FirstOrDefault(c => c.Id == channel)?.Name ?? channel;
                disabled[$"ducking:channel:{channel}"] = $"The Wave XLR Pro does not duck {name}, its own input, in the mixes it builds.";
            }
        }
        string NameOf(string channel) => setup.Channels.FirstOrDefault(c => c.Id == channel)?.Name ?? channel;
        foreach (string channel in setup.UnitSources.Values.Where(c => NeedsReturn(setup, c) && !returns.ContainsKey(c)))
            disabled[$"processedMic:{channel}"] =
                $"The Wave XLR Pro's mixes take {NameOf(channel)} as it comes in, without the processing on this computer: no USB pair is free to play it back.";
        // Mic setup plays the take back through the channel, so a unit
        // microphone under it needs a pair: its return pair, a free one, or
        // the cue's, which a processed input claims before the cue.
        bool spare = cue is not null || FreePairs(pairs.Values.Concat(returns.Values)).Any();
        List<string> microphones = [.. setup.UnitSources.Where(s => s.Key is ProMixSections.Xlr1 or ProMixSections.Xlr2).Select(s => s.Value)];
        List<string> blocked = [.. microphones.Where(c => !returns.ContainsKey(c) && !spare)];
        foreach (string channel in blocked)
            disabled[$"soundCheck:{channel}"] = $"Mic setup needs a free USB pair on the Wave XLR Pro to play {NameOf(channel)}'s take back, and every pair is in use.";
        if (blocked.Count > 0 && blocked.Count == microphones.Count)
            disabled["soundCheck"] = "Mic setup needs a free USB pair on the Wave XLR Pro to play a take back, and every pair is in use.";
        if (cue is null)
            disabled["audition"] = "Auditions need a free USB pair on the Wave XLR Pro, and every pair is in use.";
        return new Dictionary<string, string>(disabled, StringComparer.Ordinal);
    }

    /// <summary>A plan's sections as the dry run shows them: section start to bytes 0 to 13 as hex.</summary>
    public static IReadOnlyDictionary<string, string> ContentHex(HardwareMixingPlan plan)
        => plan.Content.ToDictionary(c => c.Section.ToString(CultureInfo.InvariantCulture), c => c.Hex, StringComparer.Ordinal);
}

/// <summary>
/// What the Wave XLR Pro held when hardware mixing began, and the mode's own
/// levels while it runs: saved as <c>hardwareEntry</c> in mixer.json before
/// anything is written to the unit, so a daemon stopped in the mode comes
/// back to it, and one stopped while leaving finishes leaving.
/// </summary>
public sealed record HardwareEntry
{
    /// <summary>Section 0's first 16 bytes before the mode, hex.</summary>
    public string Section0 { get; init; } = "";
    /// <summary>Section 36's first 16 bytes before the mode, hex.</summary>
    public string Section36 { get; init; } = "";
    /// <summary>Byte 98 before the mode.</summary>
    public int Section0Level { get; init; } = 0x54;
    /// <summary>
    /// Each jack's base level in the mode, in dB, by jack (<c>hp1</c>,
    /// <c>hp2</c>, <c>lineout</c>): its level before the mode less the entry
    /// offset, and every change the owner makes on the unit after. May be
    /// below the -60 dB the unit takes.
    /// </summary>
    public Dictionary<string, double> Jacks { get; init; } = [];
    /// <summary>
    /// How far each jack was lowered on entry, in dB, by jack (see
    /// <see cref="HardwareMixingPlanner.EntryOffsetDb"/>); leaving gives it
    /// back. A jack absent here gets the 21 dB of section 0's level.
    /// </summary>
    public Dictionary<string, double> Offsets { get; init; } = [];
    /// <summary>The jacks' output volume in the mode (desktop scale), which the Pro's sink had before it.</summary>
    public double? OutputVolume { get; init; }
    /// <summary>The graph has left the mode and the unit is being put back.</summary>
    public bool Leaving { get; init; }

    public static IReadOnlyList<string> JackNames { get; } = ["hp1", "hp2", "lineout"];

    /// <summary>Whether a saved entry can be used: both sections 16 bytes of hex, byte 98 a byte, known jacks with finite levels.</summary>
    public bool Valid => ProMixPlan.IsRestore(Section0) && ProMixPlan.IsRestore(Section36) && Section0Level is >= 0 and <= 255
        && Jacks.All(j => JackNames.Contains(j.Key) && double.IsFinite(j.Value) && j.Value is >= -200 and <= 0)
        && (Offsets ?? []).All(o => JackNames.Contains(o.Key) && double.IsFinite(o.Value) && o.Value is >= 0 and <= 60)
        && OutputVolume is null or (>= 0 and <= PipeWireAdapter.MaxSinkVolume);
}
