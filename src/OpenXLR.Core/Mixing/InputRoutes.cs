namespace OpenXLR.Core.Mixing;

/// <summary>
/// What decides how an insert chain is built: its plugins in order, with each
/// one's bypass, host and own block, and on XLR 1 the software low cut and ClipGuard.
/// Control values and labels are not part of it. Two chains with the same
/// structure are made of the same nodes, so a running chain takes new control
/// values live instead of being built again.
/// </summary>
internal sealed record ChainStructure(int LowCutHz, bool ClipGuard, IReadOnlyList<ChainStructure.Slot> Slots)
{
    /// <summary>
    /// One insert as the builder sees it. <paramref name="Installed"/> is
    /// whether the catalogue knows the plugin, since a plugin installed after
    /// the chain was built is only loaded by building the chain again.
    /// </summary>
    internal readonly record struct Slot(string Id, string Kind, string Plugin, bool Bypass, bool Native, bool Installed,
        bool OwnBlock = false);

    internal static ChainStructure Of(IEnumerable<InsertDefinition> inserts, int lowCutHz = 0, bool clipGuard = false)
        => new(lowCutHz, clipGuard, [.. inserts.Select(i =>
            new Slot(i.Id, i.Kind, i.Plugin, i.Bypass, i.RunsNatively, PluginCatalog.Find(i) is not null, i.OwnBlock))]);

    public bool Equals(ChainStructure? other)
        => other is not null && LowCutHz == other.LowCutHz && ClipGuard == other.ClipGuard && Slots.SequenceEqual(other.Slots);

    public override int GetHashCode() => HashCode.Combine(LowCutHz, ClipGuard, Slots.Count);
}

/// <summary>One control value a recall changes on a chain whose structure stays the same.</summary>
internal readonly record struct ParamChange(string InsertId, string Symbol, double? Value);

/// <summary>
/// How one input is wired, decided before anything is built: the
/// node it is fed from (the loop helper while Sound Check runs on it), the
/// microphone EQ at its head, and the software low cut, ClipGuard and inserts
/// its chain carries. A route with none of those feeds the channel directly.
/// </summary>
internal sealed record InputRoute(ChannelDefinition Channel, string? Input, bool Bound, bool Looped,
    int LowCutHz, bool ClipGuard, List<InsertDefinition> Inserts, bool ActiveInserts)
{
    public int Width => Channel.ChainWidth ?? 2;

    /// <summary>The node the channel's audio enters from: the loop helper while looped, else the input.</summary>
    public string? Source { get; init; }

    /// <summary>
    /// Whether the route wants the microphone stage at its head, between the
    /// source and the chain: a microphone whose noise removal is on, or whose
    /// EQ is on and runs in software, so an EQ is never run behind a unit that
    /// already equalises. It is not part of the chain's structure: the stage
    /// is spliced in and out by relinking, so it never builds a chain again.
    /// </summary>
    public bool Eq { get; init; }

    /// <summary>Whether the route runs through a chain of its own.</summary>
    public bool Chained => LowCutHz > 0 || ClipGuard || ActiveInserts;

    public ChainStructure Structure => ChainStructure.Of(Inserts, LowCutHz, ClipGuard);
}

public sealed partial class Mixer
{
    // The structure each chain key was last built for, inputs and mixes. A
    // recall compares the structure it asks for with this one and builds
    // again only the keys that differ.
    private readonly Dictionary<string, ChainStructure> _chainStructures = new(StringComparer.Ordinal);

    /// <summary>
    /// The route of one input fed from <paramref name="input"/>, or
    /// with no input when only its chain is asked about.
    /// </summary>
    private InputRoute InputRouteLocked(ChannelDefinition ch, string? input)
    {
        bool bound = _bindings.ContainsKey(ch.Id);
        // A channel under Sound Check is fed from the loop helper's single
        // output instead of its capture pair; everything downstream, the
        // software DSP and the inserts, stays live.
        bool looped = _soundCheck is not null && _soundCheckChannel == ch.Id;
        // The soft low cut and ClipGuard belong to the first XLR channel
        // only; inserts can sit on either mono XLR channel.
        bool lc = ch.InputPair == 0 && _lowCutHz > 0 && (bound || _lowCutApplicable);
        bool cg = ch.InputPair == 0 && _softClipGuard && (bound || _clipGuardApplicable);
        List<InsertDefinition> inserts = IsInsertChannel(ch.Id) ? InsertsFor(ch.Id) : [];
        bool anyInsert = inserts.Any(i => !i.Bypass && PluginCatalog.Find(i) is { } plugin
            && (ch.CaptureSource is null || plugin.Fits(ch.ChainWidth!.Value)));
        return new InputRoute(ch, input, bound, looped, lc ? _lowCutHz : 0, cg, inserts, anyInsert)
        {
            Source = looped ? _soundCheck!.SourceName : input,
            Eq = CarriesEq(ch) && StageWantedLocked(ch.Id),
        };
    }

    /// <summary>The structure a mix's chain is built for, and whether it runs a chain at all.</summary>
    private (ChainStructure Structure, bool Chained) MixChainLocked(string key)
    {
        List<InsertDefinition> inserts = InsertsFor(key);
        return (ChainStructure.Of(inserts), inserts.Any(i => !i.Bypass && PluginCatalog.Find(i) is { } p && p.Fits(2)));
    }

    /// <summary>
    /// Whether a recall has to build this key's chain again: its structure
    /// differs from the one it was built for, or the chain it should run is
    /// missing, dead or fell back after an error. A chain the restart policy
    /// has given up on stays off until its structure changes, as it did when
    /// every recall built it again.
    /// </summary>
    private bool ChainNeedsRebuildLocked(string key, ChainStructure wanted, bool chained)
    {
        if (!Equals(_chainStructures.GetValueOrDefault(key), wanted)) return true;
        if (_restarts.Blocked(key)) return false;
        if (_insertErrors.ContainsKey(key)) return true;
        return chained && !(_chains.TryGetValue(key, out FilterHandle? chain) && chain.IsAlive);
    }

    /// <summary>
    /// Apply recalled chains and the low cut and ClipGuard already set,
    /// building only the chains whose structure changed. A chain whose
    /// structure stays takes its new control values live. With
    /// <paramref name="recalled"/> null only the low cut and ClipGuard are
    /// compared. <paramref name="absentMixesEmpty"/> keeps a profile's old
    /// rule for mixes: a mix it does not name has no inserts. An input it does
    /// not name keeps its chain. <paramref name="rebuildAll"/> builds every
    /// chain, for a change of host such as latency compensation.
    /// </summary>
    private void ApplyChainsLocked(IReadOnlyDictionary<string, List<InsertDefinition>>? recalled,
        bool absentMixesEmpty, bool rebuildAll)
    {
        var before = new Dictionary<string, List<InsertDefinition>>(_inserts, StringComparer.Ordinal);
        if (recalled is not null)
        {
            Dictionary<string, List<InsertDefinition>> next = RecallChains(_inserts, recalled,
                key => absentMixesEmpty && key.StartsWith("mix:", StringComparison.Ordinal));
            _inserts.Clear();
            foreach ((string key, List<InsertDefinition> list) in next) _inserts[key] = list;
        }

        var inputs = new List<string>();
        foreach (ChannelDefinition ch in _config.Channels.Where(c => c.InputPair is not null || c.ChainWidth is not null))
        {
            InputRoute route = InputRouteLocked(ch, null);
            if (rebuildAll || ChainNeedsRebuildLocked(ch.Id, route.Structure, route.Chained)
                || !PushChainParamsLocked(ch.Id, before.GetValueOrDefault(ch.Id)))
                inputs.Add(ch.Id);
        }
        if (inputs.Count > 0) WireInputFeedsLocked(inputs);

        foreach (MixDefinition mix in _config.Mixes)
        {
            string key = MixKey(mix);
            (ChainStructure structure, bool chained) = MixChainLocked(key);
            if (rebuildAll || ChainNeedsRebuildLocked(key, structure, chained)
                || !PushChainParamsLocked(key, before.GetValueOrDefault(key)))
                WireMixChainLocked(mix);
        }
    }

    /// <summary>
    /// Push the control values a recall changed into the running chain of
    /// <paramref name="key"/>, whose structure is unchanged. False when one
    /// cannot be pushed and the chain has to be built again with them.
    /// </summary>
    private bool PushChainParamsLocked(string key, List<InsertDefinition>? before)
    {
        List<InsertDefinition> after = InsertsFor(key);
        if (ReferenceEquals(before, after)) return true;
        foreach (ParamChange change in ParamChanges(before ?? [], after))
        {
            int idx = after.FindIndex(i => i.Id == change.InsertId);
            // A control the recalled chain no longer names goes back to the
            // plugin's default, as a chain built from it would have it.
            double? value = change.Value
                ?? PluginCatalog.Find(after[idx])?.Params.FirstOrDefault(p => p.Symbol == change.Symbol)?.Default;
            if (value is not double v || !TryPushInsertParamLocked(key, after, idx, change.Symbol, v)) return false;
        }
        return true;
    }

    /// <summary>
    /// Set one control on the running stage of an insert. True when that is
    /// done, or when no running stage holds the insert and the saved value is
    /// all there is to change; false when the stage refused it.
    /// </summary>
    private bool TryPushInsertParamLocked(string key, List<InsertDefinition> list, int idx, string symbol, double value)
    {
        if (list[idx].Bypass || PluginCatalog.Find(list[idx]) is null || _insertErrors.ContainsKey(key)
            || !_chains.TryGetValue(key, out FilterHandle? chain)) return true;
        // The chain names LV2 stages i0, i1, ... in the order of the
        // non-bypassed, loadable inserts, so find this insert's stage index.
        int k = 0;
        ChannelDefinition? capture = _config.Channels.FirstOrDefault(c => c.Id == key && c.CaptureSource is not null);
        if (capture is not null && PluginCatalog.Find(list[idx])?.Fits(capture.ChainWidth!.Value) != true) return true;
        for (int j = 0; j < idx; j++)
            if (!list[j].Bypass && PluginCatalog.Find(list[j]) is { } plugin
                && (capture is null || plugin.Fits(capture.ChainWidth!.Value))) k++;
        try { _pw.SetFilterControl(chain, $"i{k}:{symbol}", value); }
        catch (InvalidOperationException) { return false; }
        return true;
    }

    /// <summary>
    /// The chains after a recall. A key the recalled data names is replaced
    /// by a copy of its list. A key it does not name is kept as it is, unless
    /// <paramref name="absentMeansEmpty"/> says that key's absence clears it.
    /// </summary>
    internal static Dictionary<string, List<InsertDefinition>> RecallChains(
        IReadOnlyDictionary<string, List<InsertDefinition>> current,
        IReadOnlyDictionary<string, List<InsertDefinition>> recalled,
        Func<string, bool> absentMeansEmpty)
    {
        var next = new Dictionary<string, List<InsertDefinition>>(StringComparer.Ordinal);
        foreach ((string key, List<InsertDefinition> list) in current)
            if (!recalled.ContainsKey(key) && !absentMeansEmpty(key)) next[key] = list;
        foreach ((string key, List<InsertDefinition> list) in CopyInserts(recalled)) next[key] = list;
        return next;
    }

    /// <summary>
    /// The control values that differ between two chains of the same
    /// structure, insert by insert: a new or changed value, or a null value
    /// for one <paramref name="after"/> no longer holds.
    /// </summary>
    internal static List<ParamChange> ParamChanges(IReadOnlyList<InsertDefinition> before, IReadOnlyList<InsertDefinition> after)
    {
        var changes = new List<ParamChange>();
        foreach (InsertDefinition insert in after)
        {
            Dictionary<string, double> old = before.FirstOrDefault(i => i.Id == insert.Id)?.Params ?? [];
            foreach ((string symbol, double value) in insert.Params)
                if (!old.TryGetValue(symbol, out double was) || was != value) changes.Add(new(insert.Id, symbol, value));
            foreach (string symbol in old.Keys)
                if (!insert.Params.ContainsKey(symbol)) changes.Add(new(insert.Id, symbol, null));
        }
        return changes;
    }
}
