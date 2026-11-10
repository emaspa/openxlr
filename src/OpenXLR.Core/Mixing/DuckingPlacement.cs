using OpenXLR.Core.Devices;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// A mix's consumers and audible sends, captured under the mixer lock. Open
/// channels map ids to display names; <see cref="KeysInSoftware"/> are the
/// keys heard in it through their software send.
/// </summary>
public sealed record DuckingMixView(string Id, string Name, MixKind Kind, bool OnJacks, bool OnAux,
    IReadOnlyList<string> OtherConsumers, IReadOnlyDictionary<string, string> OpenChannels, IReadOnlyList<string> KeysInSoftware)
{
    /// <summary>A published virtual microphone is a consumer even while no application records it.</summary>
    public bool Heard => OnJacks || OnAux || OtherConsumers.Count > 0 || Kind == MixKind.VirtualMic;
}

/// <summary>
/// The routing facts used by placement, with no graph or device access.
/// <see cref="JackFeed"/> is every mix the jacks play; <see cref="JackFeeds"/>
/// the feeds one by one with the USB pair each streams on, or null for one
/// feed on the Monitor pair.
/// </summary>
public sealed record DuckingView(bool MixerRunning, IReadOnlyList<DuckingMixView> Mixes,
    IReadOnlyList<string> JackFeed, bool SoftwareRunning = false, bool Failed = false)
{
    /// <summary>
    /// The channels that carry an input of the primary unit, to the input's
    /// source number (0 XLR 1, 1 XLR 2, 2 USB Aux, 3 Line In): the keys the
    /// unit hears, and the inputs it can duck.
    /// </summary>
    public IReadOnlyDictionary<string, int> UnitSources { get; init; } = new Dictionary<string, int>();

    public IReadOnlyList<DuckingJackFeed>? JackFeeds { get; init; }

    /// <summary>The unit builds the mixes its jacks play (hardware mixing).</summary>
    public bool HardwareMixing { get; init; }

    /// <summary>
    /// In hardware mixing, each mix the unit builds to the sections that hold
    /// it (a summed feed's section holds each of its mixes); empty otherwise.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<int>> UnitMixSections { get; init; } = new Dictionary<string, IReadOnlyList<int>>();

    /// <summary>In hardware mixing, the USB playback pair of every channel that streams on one; the unit's own inputs have none.</summary>
    public IReadOnlyDictionary<string, int> ChannelPairs { get; init; } = new Dictionary<string, int>();

    /// <summary>In hardware mixing, the pair a unit input software processes plays back on, which the unit's mixes take instead of the raw input.</summary>
    public IReadOnlyDictionary<string, int> ReturnPairs { get; init; } = new Dictionary<string, int>();

    /// <summary>Channel ids to display names, for notes.</summary>
    public IReadOnlyDictionary<string, string> ChannelNames { get; init; } = new Dictionary<string, string>();
}

/// <summary>One feed of the Pro's jacks: the USB pair it streams on and the mixes it sums.</summary>
public sealed record DuckingJackFeed(int Pair, IReadOnlyList<string> Mixes);

/// <summary>
/// The backend choice and the first device refusal. Outputs are derived even
/// when the unit cannot run the setup, and so are the USB pairs the unit
/// would duck: each jack feed's pair that plays a target mix, and the Aux
/// pair. A ready verdict also carries the unit's key mask and the unit
/// inputs it ducks.
/// </summary>
public sealed record DuckingVerdict(string? Backend, bool Ready, string? Reason, string? Note, IReadOnlyList<string> Outputs)
{
    public IReadOnlyList<int> Pairs { get; init; } = [.. Outputs.Select(ProDucking.PairOf).Distinct()];

    /// <summary>The sources that key the unit, bit M for source M (<see cref="ProDuckingTarget.Keys"/>); 0 when it cannot run the setup.</summary>
    public ushort Keys { get; init; }

    /// <summary>The unit inputs in <c>channels</c> the unit ducks (0 XLR 1 to 3 Line In).</summary>
    public IReadOnlyList<int> Inputs { get; init; } = [];

    /// <summary>
    /// In hardware mixing, each section of a mix the unit builds to the
    /// channels' pairs ducked in it; null as a whole-output verdict.
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlySet<int>>? Sections { get; init; }

    /// <summary>
    /// In hardware mixing, the target mixes software ducks: every one but
    /// those only the unit's copy of is heard. Null for every target, as
    /// outside the mode.
    /// </summary>
    public IReadOnlySet<string>? SoftwareMixes { get; init; }
}

/// <summary>All-or-nothing placement of a setup on the primary unit or in software.</summary>
public static class DuckingPlacement
{
    /// <summary>Apply the fit rules in order. An unheard target mix imposes no restriction on the unit.</summary>
    public static DuckingVerdict Evaluate(DuckingSetup setup, DuckingView view, UnitDuckingSupport? support)
    {
        List<DuckingMixView> targets = [.. view.Mixes.Where(m => setup.Mixes.Contains(m.Id) && m.Heard)];
        var outputs = new List<string>();
        if (targets.Any(m => m.OnJacks)) outputs.Add(ProDucking.TargetMonitor);
        if (targets.Any(m => m.OnAux)) outputs.Add(ProDucking.TargetAux);
        // Each jack feed streams on its own pair, and the unit ducks a pair
        // in every section it plays in: a feed is ducked whole, or not.
        IReadOnlyList<DuckingJackFeed> feeds = view.JackFeeds ?? [new(ProReturnPairs.Monitor, view.JackFeed)];
        List<DuckingJackFeed> ducked = [.. feeds.Where(f => f.Mixes.Any(id => targets.Any(m => m.Id == id && m.OnJacks)))];
        var pairs = new List<int>(ducked.Select(f => f.Pair));
        if (targets.Any(m => m.OnAux)) pairs.Add(ProReturnPairs.Aux);
        string? software = view.MixerRunning && view.SoftwareRunning ? MicEqProcessing.Software : null;
        if (support is null) return new(software, false, null, null, outputs) { Pairs = pairs };
        if (!view.MixerRunning) return No("mixerOff", "The software mixer is off, so ducking does not run.");
        IReadOnlyList<string> keys = DuckingScene.KeysOf(setup) ?? [];
        if (view.HardwareMixing && setup.Mixes.Any(view.UnitMixSections.ContainsKey))
            return InHardwareMixing(setup, keys, view, support, software);
        // Outside hardware mixing no channel streams on a pair of its own, so
        // the unit hears only its inputs.
        if (keys.FirstOrDefault(k => !view.UnitSources.ContainsKey(k)) is { } unheard)
            return No("keyNotSupported", $"{ChannelName(view, unheard)} is not one of the {support.Name}'s inputs, so the unit cannot use it as a key.");
        if (!support.ReachesJacks && targets.Any(m => m.OnJacks))
            return No("monitorReturn", $"The jacks' feed is not in the {support.Name}'s headphone mix, where it ducks, so this computer ducks.");
        foreach (DuckingMixView mix in targets)
            if (mix.OtherConsumers.Count > 0 || mix.Kind == MixKind.VirtualMic
                || mix.OnJacks && mix.Kind != MixKind.Monitor || mix.OnAux && mix.Kind != MixKind.AuxPort)
            {
                string where = mix.OtherConsumers.FirstOrDefault() ?? (mix.Kind == MixKind.VirtualMic ? "a virtual microphone" : "another feed");
                return No("mixElsewhere", $"{mix.Name} also plays on {where}, which the {support.Name} cannot duck.");
            }
        foreach (DuckingJackFeed feed in ducked)
            if (feed.Mixes.Any(id => !setup.Mixes.Contains(id)))
            {
                string all = string.Join(" and ", feed.Mixes.Select(Name));
                string selected = string.Join(" and ", feed.Mixes.Where(setup.Mixes.Contains).Select(Name));
                return No("sharedOutput", $"The jacks play {all} together, and only {selected} is ducked.");
            }
        foreach (DuckingMixView mix in targets)
            foreach ((string id, string name) in mix.OpenChannels)
                if (!keys.Contains(id) && !setup.Channels.Contains(id))
                    return No("partialMix", $"{name} plays in {mix.Name} and is not ducked; the {support.Name} lowers a whole output.");
        foreach (DuckingMixView mix in targets)
            if (mix.KeysInSoftware.FirstOrDefault() is { } heard)
            {
                string name = mix.OpenChannels.GetValueOrDefault(heard) ?? ChannelName(view, heard);
                return No("keyInMix", $"{name} is heard in {mix.Name} through its software send, so the unit would lower the key too.");
            }
        if (view.Failed || support.Reason == "failed")
            return No("failed", $"The {support.Name} did not keep the ducking written to it. Switch ducking off and on to try again.");
        if (!support.Ready) return No(support.Reason ?? "unsupportedBlock", support.Note
            ?? $"The {support.Name}'s ducking block holds values OpenXLR does not know.");
        return new(setup.Processing == ProDucking.Device ? ProDucking.Device : software, true, null, null, outputs)
        {
            Pairs = pairs,
            Keys = KeyMask(keys.Select(k => view.UnitSources[k])),
            Inputs = InputsOf(setup, view),
        };

        DuckingVerdict No(string reason, string note) => new(software, false, reason, note, outputs) { Pairs = pairs };
        string Name(string id) => view.Mixes.FirstOrDefault(m => m.Id == id)?.Name ?? id;
    }

    private static string ChannelName(DuckingView view, string id) => view.ChannelNames.GetValueOrDefault(id) ?? id;

    private static ushort KeyMask(IEnumerable<int> sources) => (ushort)sources.Aggregate(0, (mask, source) => mask | 1 << source);

    /// <summary>The unit inputs among the channels to lower, as source numbers in order.</summary>
    private static IReadOnlyList<int> InputsOf(DuckingSetup setup, DuckingView view)
        => [.. setup.Channels.Where(view.UnitSources.ContainsKey).Select(c => view.UnitSources[c]).Distinct().Order()];

    /// <summary>
    /// Hardware mixing with a target mix the unit builds. The unit ducks each
    /// target channel in each section that holds a target mix, per channel:
    /// a channel on a pair (or a unit input software processes, on its
    /// return pair) at its pair's field, and a unit input at its input's
    /// field. So the whole-output rules do not apply: a channel heard there
    /// and not in <c>channels</c> keeps its level, and so does every key. A
    /// summed feed's section is ducked when any of its mixes is a target.
    /// Each key must be one the unit hears: a unit input, or a channel on a
    /// pair. Every other target mix, and the computer's copy of a unit-built
    /// mix another output hears, is ducked in software at the same time; the
    /// two never meet, since a channel's pair streams what reaches the
    /// channel before its sends. The unit ducks whatever <c>processing</c>
    /// says, since nothing on this computer can lower a mix it builds.
    /// </summary>
    private static DuckingVerdict InHardwareMixing(DuckingSetup setup, IReadOnlyList<string> keys, DuckingView view,
        UnitDuckingSupport support, string? software)
    {
        List<string> unitMixes = [.. setup.Mixes.Where(view.UnitMixSections.ContainsKey)];
        HashSet<string> unitOnly = [.. unitMixes.Where(id => view.Mixes.FirstOrDefault(m => m.Id == id) is not { OtherConsumers.Count: > 0 })];
        IReadOnlySet<string> softwareMixes = setup.Mixes.Where(id => !unitOnly.Contains(id)).ToHashSet(StringComparer.Ordinal);
        if (keys.FirstOrDefault(k => SourceOf(k) is null) is { } unheard)
            return Verdict(software, false, "keyNotSupported",
                $"{ChannelName(view, unheard)} is neither one of the {support.Name}'s inputs nor on one of its USB pairs, so the unit cannot use it as a key and the mixes it builds for its outputs are not ducked.",
                null, []);
        if (view.Failed || support.Reason == "failed")
            return Verdict(software, false, "failed", $"The {support.Name} did not keep the ducking written to it. Switch ducking off and on to try again.", null, []);
        if (!support.Ready)
            return Verdict(software, false, support.Reason ?? "unsupportedBlock", support.Note
                ?? $"The {support.Name}'s ducking block holds values OpenXLR does not know.", null, []);

        // Pair 5 is a channel's in the mode like any other: the unit builds
        // the Aux mix in section 36 from the channels' pairs.
        IReadOnlySet<int> ducked = setup.Channels.SelectMany(PairsOf).ToHashSet();
        var sections = new SortedDictionary<int, IReadOnlySet<int>>();
        foreach (string mix in unitMixes)
            foreach (int section in view.UnitMixSections[mix])
                sections[section] = ducked;
        return Verdict(ProDucking.Device, true, null, null, new Dictionary<int, IReadOnlySet<int>>(sections), [.. ducked.Order()]) with
        {
            Keys = KeyMask(keys.Select(k => SourceOf(k)!.Value)),
            Inputs = InputsOf(setup, view),
        };

        // A key's source: its unit input, or its pair (source 4 + pair).
        int? SourceOf(string channel)
            => view.UnitSources.TryGetValue(channel, out int input) ? input
                : view.ChannelPairs.TryGetValue(channel, out int pair) ? ProMixSections.SourceOfPair(pair) : null;

        IEnumerable<int> PairsOf(string channel)
        {
            if (view.ChannelPairs.TryGetValue(channel, out int pair)) yield return pair;
            if (view.ReturnPairs.TryGetValue(channel, out int processed)) yield return processed;
        }

        // The outputs the unit's ducking is heard on: the jacks for a jack
        // feed's section, the USB Aux port for the Aux mix's.
        List<string> Outputs()
        {
            IEnumerable<int> held = unitMixes.SelectMany(id => view.UnitMixSections[id]).ToList();
            var outputs = new List<string>();
            if (held.Any(s => s != HardwareMixingPlanner.AuxSection)) outputs.Add(ProDucking.TargetMonitor);
            if (held.Contains(HardwareMixingPlanner.AuxSection)) outputs.Add(ProDucking.TargetAux);
            return outputs;
        }

        DuckingVerdict Verdict(string? backend, bool ready, string? reason, string? note,
            IReadOnlyDictionary<int, IReadOnlySet<int>>? sections, IReadOnlyList<int> pairs)
            => new(backend, ready, reason, note, Outputs()) { Pairs = pairs, Sections = sections, SoftwareMixes = softwareMixes };
    }
}
