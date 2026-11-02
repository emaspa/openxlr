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
}
