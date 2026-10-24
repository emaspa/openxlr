using OpenXLR.Core.Devices;

namespace OpenXLR.Core.Mixing;

/// <summary>A mix's consumers and audible sends, captured under the mixer lock. Open channels map ids to display names.</summary>
public sealed record DuckingMixView(string Id, string Name, MixKind Kind, bool OnJacks, bool OnAux,
    IReadOnlyList<string> OtherConsumers, IReadOnlyDictionary<string, string> OpenChannels, bool KeyInSoftware)
{
    /// <summary>A published virtual microphone is a consumer even while no application records it.</summary>
    public bool Heard => OnJacks || OnAux || OtherConsumers.Count > 0 || Kind == MixKind.VirtualMic;
}

/// <summary>The routing facts used by placement, with no graph or device access.</summary>
public sealed record DuckingView(bool MixerRunning, bool KeyIsUnitInput1, IReadOnlyList<DuckingMixView> Mixes,
    IReadOnlyList<string> JackFeed, bool SoftwareRunning = false, bool Failed = false);

/// <summary>The backend choice and the first device refusal. Outputs are derived even when the unit cannot run the setup.</summary>
public sealed record DuckingVerdict(string? Backend, bool Ready, string? Reason, string? Note, IReadOnlyList<string> Outputs);

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
        string? software = view.MixerRunning && view.SoftwareRunning ? MicEqProcessing.Software : null;
        if (support is null) return new(software, false, null, null, outputs);
        if (!view.MixerRunning) return No("mixerOff", "The software mixer is off, so ducking does not run.");
        if (!view.KeyIsUnitInput1) return No("keyNotSupported", $"The {support.Name} ducks only with XLR 1 as the key.");
        if (!support.ReachesJacks && targets.Any(m => m.OnJacks))
            return No("monitorReturn", $"The jacks' feed is not in the {support.Name}'s headphone mix, where it ducks, so this computer ducks.");
        if (!support.ReachesAux && targets.Any(m => m.OnAux))
            return No("auxNotChecked", $"Ducking on the {support.Name}'s USB Aux port has not been checked by ear yet, so this computer ducks.");
        foreach (DuckingMixView mix in targets)
            if (mix.OtherConsumers.Count > 0 || mix.Kind == MixKind.VirtualMic
                || mix.OnJacks && mix.Kind != MixKind.Monitor || mix.OnAux && mix.Kind != MixKind.AuxPort)
            {
                string where = mix.OtherConsumers.FirstOrDefault() ?? (mix.Kind == MixKind.VirtualMic ? "a virtual microphone" : "another feed");
                return No("mixElsewhere", $"{mix.Name} also plays on {where}, which the {support.Name} cannot duck.");
            }
        if (targets.Any(m => m.OnJacks) && view.JackFeed.Any(id => !setup.Mixes.Contains(id)))
        {
            string all = string.Join(" and ", view.JackFeed.Select(Name));
            string selected = string.Join(" and ", view.JackFeed.Where(setup.Mixes.Contains).Select(Name));
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
        return new(setup.Processing == ProDucking.Device ? ProDucking.Device : software, true, null, null, outputs);

        DuckingVerdict No(string reason, string note) => new(software, false, reason, note, outputs);
        string Name(string id) => view.Mixes.FirstOrDefault(m => m.Id == id)?.Name ?? id;
    }
}
