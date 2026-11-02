using OpenXLR.Core.Devices;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// Hardware mixing as clients see it in the state: whether it is wanted and
/// running, why not, and while it runs the pairs, sections, held levels and
/// what it turns off (see <see cref="HardwareMixingPlan"/>).
/// </summary>
public sealed record HardwareMixingState
{
    public bool Wanted { get; init; }
    public bool Active { get; init; }
    public string? Reason { get; init; }
    public string? Note { get; init; }
    public IReadOnlyDictionary<string, int> Pairs { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> Sections { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> Jacks { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<HardwareMixingClamp> Clamped { get; init; } = [];
    public IReadOnlyDictionary<string, string> Disabled { get; init; } = new Dictionary<string, string>();

    /// <summary>The state of a plan: the maps only while the mode runs.</summary>
    public static HardwareMixingState Of(HardwareMixingPlan plan) => plan.Active
        ? new()
        {
            Wanted = true, Active = true, Pairs = plan.Pairs, Sections = plan.Sections, Jacks = plan.Jacks,
            Clamped = plan.Clamped, Disabled = plan.Disabled,
        }
        : new() { Wanted = plan.Wanted, Reason = plan.Reason, Note = plan.Note };
}

public sealed partial record MixerState
{
    /// <summary>Hardware mixing on the Wave XLR Pro; null until the daemon has planned it once.</summary>
    public HardwareMixingState? HardwareMixing { get; init; }
}

public sealed partial record ChannelStatus
{
    /// <summary>The USB playback pair the channel streams on while hardware mixing runs; null otherwise.</summary>
    public int? HardwarePair { get; init; }
}

public sealed partial record MixStatus
{
    /// <summary>The section of the Wave XLR Pro that builds the mix while hardware mixing runs and a jack plays it; null otherwise.</summary>
    public int? HardwareSection { get; init; }
}

public sealed partial class Mixer
{
    // mixer.json "mixing", "hardwarePairs" and "hardwareSections" (machine
    // wiring, never in a profile), and the plan the last sweep made.
    private string _mixing = MixingMode.Software;
    private readonly Dictionary<string, int> _hardwarePairs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _hardwareSections = new(StringComparer.Ordinal);
    private HardwareMixingPlan? _hardwarePlan;

    /// <summary>Where the mixes the Pro's outputs play are wanted: <see cref="MixingMode"/>.</summary>
    public string Mixing { get { lock (_gate) return _mixing; } }

    /// <summary>The plan the last sweep made, or null before the first.</summary>
    public HardwareMixingPlan? HardwarePlan { get { lock (_gate) return _hardwarePlan; } }

    /// <summary>Keep the plan a sweep made, for the state. True when what the state shows of it changed.</summary>
    public bool NoteHardwarePlan(HardwareMixingPlan plan)
    {
        lock (_gate)
        {
            bool changed = _hardwarePlan is null || !SameState(HardwareMixingState.Of(_hardwarePlan), HardwareMixingState.Of(plan));
            _hardwarePlan = plan;
            return changed;
        }
    }

    private static bool SameState(HardwareMixingState a, HardwareMixingState b)
        => a.Wanted == b.Wanted && a.Active == b.Active && a.Reason == b.Reason && a.Note == b.Note
            && a.Pairs.Count == b.Pairs.Count && !a.Pairs.Except(b.Pairs).Any()
            && a.Sections.Count == b.Sections.Count && !a.Sections.Except(b.Sections).Any()
            && a.Jacks.Count == b.Jacks.Count && !a.Jacks.Except(b.Jacks).Any()
            && a.Clamped.SequenceEqual(b.Clamped)
            && a.Disabled.Count == b.Disabled.Count && !a.Disabled.Except(b.Disabled).Any();

    /// <summary>
    /// The plan for hardware mixing from the model as it stands, for the
    /// unit the daemon describes; <paramref name="mixing"/> overrides the
    /// saved choice, as a dry run asks.
    /// </summary>
    public HardwareMixingPlan PlanHardwareMixing(HardwareMixingUnit? unit, bool failed = false, string? mixing = null,
        double section0LevelDb = HardwareMixingPlanner.Section0LevelDb)
    {
        lock (_gate) return HardwareMixingPlanner.Plan(HardwareSetupLocked(unit, failed, mixing ?? _mixing, section0LevelDb));
    }

    private HardwareMixingSetup HardwareSetupLocked(HardwareMixingUnit? unit, bool failed, string mixing, double section0LevelDb)
    {
        var levels = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (ChannelDefinition channel in _config.Channels)
            foreach (MixDefinition mix in _config.Mixes)
                levels[Cell(channel.Id, mix.Id)] = HardwareLevelDbLocked(channel.Id, mix.Id);
        var unitSources = new Dictionary<int, string>();
        if (_drivenUnit is not null)
        {
            if (CarrierOfLocked(1) is { } xlr1) unitSources[ProMixSections.Xlr1] = xlr1;
            if (CarrierOfLocked(2) is { } xlr2 && xlr2 != unitSources.GetValueOrDefault(ProMixSections.Xlr1)) unitSources[ProMixSections.Xlr2] = xlr2;
            foreach (ChannelDefinition c in _config.Channels)
                if (c.InputPair is int pair && pair is ProMixSections.UsbAux or ProMixSections.LineIn
                    && !_bindings.ContainsKey(c.Id) && ChannelPresentLocked(c))
                    unitSources.TryAdd(pair, c.Id);
        }
        string? proSink = _hardwareOutputRouting ? HardwareOutputSinkLocked() : null;
        return new HardwareMixingSetup
        {
            Wanted = mixing == MixingMode.Hardware,
            MixerRunning = _built,
            Unit = unit,
            SinkPorts = proSink is null ? null : _pw.NodeChannels(proSink),
            SourcePorts = _inputPorts,
            Failed = failed,
            Channels = [.. _config.Channels],
            Mixes = [.. _config.Mixes],
            LevelsDb = levels,
            Jacks = [.. _monitorOutputs.Where(o => o.Contains('#') && !o.EndsWith("#usbaux", StringComparison.Ordinal)).OrderBy(JackOrder)
                .Select(o => (Jack: o[(o.IndexOf('#') + 1)..], Feed: FeedOfLocked(o)))
                .Where(j => JackSuffixes.Contains(j.Jack) && j.Feed is not null)
                .Select(j => (j.Jack, j.Feed!))],
            UnitSources = unitSources,
            Processed = unitSources.Values.Where(ProcessedInSoftwareLocked).ToHashSet(StringComparer.Ordinal),
            DirectPath = _hardwareMicChannels.OfType<string>().ToHashSet(StringComparer.Ordinal),
            MixChains = _config.Mixes.Where(m => InsertsFor(MixKey(m)).Count > 0).Select(m => m.Id).ToHashSet(StringComparer.Ordinal),
            MonitorMuted = _monitorMuted,
            SavedPairs = new Dictionary<string, int>(_hardwarePairs, StringComparer.Ordinal),
            SavedSections = new Dictionary<string, int>(_hardwareSections, StringComparer.Ordinal),
            Section0LevelDb = section0LevelDb,
        };
    }

    /// <summary>
    /// A channel's level in a mix for the unit's cells, in dB: the send, the
    /// channel volume and the mix volume, each on the desktop's cubic scale;
    /// negative infinity while the send or the mix is muted or an exclusive
    /// peer's mute is still waiting.
    /// </summary>
    private double HardwareLevelDbLocked(string channel, string mix)
    {
        string cell = Cell(channel, mix);
        if (_muted.Contains(cell) || _mixMuted.Contains(mix) || ExclusivePeerPendingLocked(channel)) return double.NegativeInfinity;
        double send = SendGain(_levels.GetValueOrDefault(cell, 0.0), ChannelVolumeLocked(channel), 1);
        double master = Math.Clamp(_mixVolume.GetValueOrDefault(mix, 1.0), 0, MixVolumeMaximumLocked(mix));
        return send > 0 && master > 0 ? 60 * Math.Log10(send) + 60 * Math.Log10(master) : double.NegativeInfinity;
    }

    /// <summary>The state's view of hardware mixing, and each channel's pair and each mix's section while it runs.</summary>
    private MixerState WithHardwareMixingLocked(MixerState state)
    {
        if (_hardwarePlan is not { } plan) return state;
        HardwareMixingState view = HardwareMixingState.Of(plan);
        if (!view.Active) return state with { HardwareMixing = view };
        return state with
        {
            HardwareMixing = view,
            Channels = [.. state.Channels.Select(c => view.Pairs.TryGetValue(c.Id, out int pair) ? c with { HardwarePair = pair } : c)],
            Mixes = [.. state.Mixes.Select(m => view.Sections.FirstOrDefault(s => MonitorFeed.Parts(s.Key).Contains(m.Id)) is { Key: not null } s
                ? m with { HardwareSection = s.Value } : m)],
        };
    }

    private void RestoreHardwareMixingLocked(MixerSettings s)
    {
        _mixing = MixingMode.IsValid(s.Mixing) ? s.Mixing : MixingMode.Software;
        _hardwarePairs.Clear();
        foreach ((string channel, int pair) in s.HardwarePairs) _hardwarePairs[channel] = pair;
        _hardwareSections.Clear();
        foreach ((string feed, int section) in s.HardwareSections) _hardwareSections[feed] = section;
    }
}
