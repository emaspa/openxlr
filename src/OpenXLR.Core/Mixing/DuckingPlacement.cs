using OpenXLR.Core.Devices;

namespace OpenXLR.Core.Mixing;

/// <summary>A mix's consumers and audible sends, captured under the mixer lock. Open channels map ids to display names.</summary>
public sealed record DuckingMixView(string Id, string Name, MixKind Kind, bool OnJacks, bool OnAux,
    IReadOnlyList<string> OtherConsumers, IReadOnlyDictionary<string, string> OpenChannels, bool KeyInSoftware)
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
public sealed record DuckingView(bool MixerRunning, bool KeyIsUnitInput1, IReadOnlyList<DuckingMixView> Mixes,
    IReadOnlyList<string> JackFeed, bool SoftwareRunning = false, bool Failed = false)
{
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

    /// <summary>Channel ids to display names, for notes.</summary>
    public IReadOnlyDictionary<string, string> ChannelNames { get; init; } = new Dictionary<string, string>();
}

/// <summary>One feed of the Pro's jacks: the USB pair it streams on and the mixes it sums.</summary>
public sealed record DuckingJackFeed(int Pair, IReadOnlyList<string> Mixes);

/// <summary>
/// The backend choice and the first device refusal. Outputs are derived even
/// when the unit cannot run the setup, and so are the USB pairs the unit
/// would duck: each jack feed's pair that plays a target mix, and the Aux
/// pair.
/// </summary>
public sealed record DuckingVerdict(string? Backend, bool Ready, string? Reason, string? Note, IReadOnlyList<string> Outputs)
{
    public IReadOnlyList<int> Pairs { get; init; } = [.. Outputs.Select(ProDucking.PairOf).Distinct()];

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
        if (view.HardwareMixing && setup.Mixes.Any(view.UnitMixSections.ContainsKey))
            return InHardwareMixing(setup, view, support, software);
        if (!view.KeyIsUnitInput1) return No("keyNotSupported", $"The {support.Name} ducks only with XLR 1 as the key.");
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
                if (id != setup.Key && !setup.Channels.Contains(id))
                    return No("partialMix", $"{name} plays in {mix.Name} and is not ducked; the {support.Name} lowers a whole output.");
        foreach (DuckingMixView mix in targets)
            if (mix.KeyInSoftware)
            {
                string name = mix.OpenChannels.GetValueOrDefault(setup.Key) ?? setup.Key;
                return No("keyInMix", $"{name} is heard in {mix.Name} through its software send, so the unit would lower the voice too.");
            }
        if (view.Failed || support.Reason == "failed")
            return No("failed", $"The {support.Name} did not keep the ducking written to it. Switch ducking off and on to try again.");
        if (!support.Ready) return No(support.Reason ?? "unsupportedBlock", support.Note
            ?? $"The {support.Name}'s ducking block holds values OpenXLR does not know.");
        return new(setup.Processing == ProDucking.Device ? ProDucking.Device : software, true, null, null, outputs) { Pairs = pairs };

        DuckingVerdict No(string reason, string note) => new(software, false, reason, note, outputs) { Pairs = pairs };
        string Name(string id) => view.Mixes.FirstOrDefault(m => m.Id == id)?.Name ?? id;
    }

    /// <summary>
    /// Hardware mixing with a target mix the unit builds. The unit ducks each
    /// target channel's pair in each section that holds a target mix, per
    /// channel, so the whole-output rules do not apply: a channel heard there
    /// and not in <c>channels</c> keeps its level, and so does the key, which
    /// is the unit's own input. A summed feed's section is ducked when any of
    /// its mixes is a target. The unit's own inputs in <c>channels</c> are
    /// skipped with a note, since it does not duck them. Every other target
    /// mix, and the computer's copy of a unit-built mix another output hears,
    /// is ducked in software at the same time; the two never meet, since a
    /// channel's pair streams what reaches the channel before its sends. The
    /// unit ducks whatever <c>processing</c> says, since nothing on this
    /// computer can lower a mix the unit builds.
    /// </summary>
    private static DuckingVerdict InHardwareMixing(DuckingSetup setup, DuckingView view, UnitDuckingSupport support, string? software)
    {
        List<string> unitMixes = [.. setup.Mixes.Where(view.UnitMixSections.ContainsKey)];
        HashSet<string> unitOnly = [.. unitMixes.Where(id => view.Mixes.FirstOrDefault(m => m.Id == id) is not { OtherConsumers.Count: > 0 })];
        IReadOnlySet<string> softwareMixes = setup.Mixes.Where(id => !unitOnly.Contains(id)).ToHashSet(StringComparer.Ordinal);
        if (!view.KeyIsUnitInput1)
            return Verdict(software, false, "keyNotSupported",
                $"The {support.Name} ducks only with XLR 1 as the key, so the mixes it builds for its outputs are not ducked.", null, []);
        if (view.Failed || support.Reason == "failed")
            return Verdict(software, false, "failed", $"The {support.Name} did not keep the ducking written to it. Switch ducking off and on to try again.", null, []);
        if (!support.Ready)
            return Verdict(software, false, support.Reason ?? "unsupportedBlock", support.Note
                ?? $"The {support.Name}'s ducking block holds values OpenXLR does not know.", null, []);

        // Pair 5 is a channel's in the mode like any other: the unit builds
        // the Aux mix in section 36 from the channels' pairs.
        IReadOnlySet<int> ducked = setup.Channels.Where(view.ChannelPairs.ContainsKey).Select(c => view.ChannelPairs[c]).ToHashSet();
        var sections = new SortedDictionary<int, IReadOnlySet<int>>();
        foreach (string mix in unitMixes)
            foreach (int section in view.UnitMixSections[mix])
                sections[section] = ducked;
        List<string> skipped = [.. setup.Channels.Where(c => !view.ChannelPairs.ContainsKey(c))
            .Select(c => view.ChannelNames.GetValueOrDefault(c) ?? c)];
        string? note = skipped.Count switch
        {
            0 => null,
            1 => $"{skipped[0]} is the {support.Name}'s own input, which it does not duck.",
            _ => $"{string.Join(", ", skipped[..^1])} and {skipped[^1]} are the {support.Name}'s own inputs, which it does not duck.",
        };
        return Verdict(ProDucking.Device, true, null, note, new Dictionary<int, IReadOnlySet<int>>(sections), [.. ducked.Order()]);

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
