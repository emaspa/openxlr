namespace OpenXLR.Core.Mixing;

/// <summary>
/// The output EQ: the microphone EQ's model (<see cref="MicEq"/>) on every
/// mix, run by the native helper's <c>eq</c> backend on two channels, which
/// runs the EQ alone. The stage sits after the mix's inserts and before
/// anything that reads the mix:
///
///   OpenXLR_mix_&lt;id&gt; -> [inserts] -> OpenXLR_mixeq_&lt;id&gt;_&lt;gen&gt; -> [latency delay] -> outputs, post sink, Aux
///
/// It is one persistent node. Every value, the switch included, goes to it
/// live through the helper's command pipe, and it keeps its filter state, so
/// a band change builds nothing. A mix whose EQ is off when its route is built
/// gets no stage; switching the EQ on starts one and points the mix's
/// readers at it once, and switching it off bypasses inside the stage, which
/// stays until the mix is next built. Only a change in the number of bands
/// starts a fresh stage.
///
/// The stage never touches the mix's insert chain: a chain rebuilt for its
/// own reasons has the stage's input linked again from the new chain, and
/// the stage keeps running. A chain the mixer gave up on stays off; the EQ
/// then takes the mix's own monitor. Nothing here moves a microphone's
/// route, so the Wave XLR Pro's direct path is left as it is.
///
/// While hardware mixing runs, the Wave XLR Pro builds the mixes its jacks
/// and its USB Aux port play, and the EQ of each of them is held off (the
/// plan's <c>eq:mix:&lt;id&gt;</c> sentence); the saved EQ is kept and
/// applies again when software mixing returns.
/// </summary>
public sealed partial class Mixer
{
    /// <summary>What an output EQ that cannot run says when this build has no native helper.</summary>
    public const string MixEqNeedsHost =
        "The output EQ needs the OpenXLR native plugin host, which this installation does not include. Install a package that ships it, or build with -p:EnableNativeLv2Host=true.";

    /// <summary>What an output EQ the restart policy has given up on says.</summary>
    internal const string MixEqGivenUp = "the EQ kept failing and is off; switch it off and on to try again";

    /// <summary>The key of a mix's EQ in the hardware mixing plan's <c>disabled</c> map.</summary>
    public static string MixEqDisabledKey(string mix) => "eq:mix:" + mix;

    private static string MixEqKey(string mix) => "mixeq:" + mix;

    // Saved values by mix id. A mix absent here has the defaults with the EQ off.
    private readonly Dictionary<string, MicEq> _mixEq = new(StringComparer.Ordinal);
    // The running stages by mix id, the link from the mix (or its chain) into
    // each, and why a stage could not start.
    private readonly Dictionary<string, SoftwareEqBackend> _mixEqStages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PortLink> _mixEqInputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _mixEqErrors = new(StringComparer.Ordinal);
    private long _mixEqGeneration;

    /// <summary>A mix's EQ: what was set or recalled, else the defaults with the EQ off.</summary>
    public MicEq MixEqFor(string mix) { lock (_gate) return MixEqLocked(mix); }

    private MicEq MixEqLocked(string mix) => _mixEq.GetValueOrDefault(mix) ?? MicEq.Default;

    /// <summary>Whether a mix's EQ runs in a stage now, for tests and diagnostics.</summary>
    internal string? MixEqNode(string mix) { lock (_gate) return _mixEqStages.GetValueOrDefault(mix)?.Node; }

    /// <summary>
    /// Why a mix's EQ is held off while hardware mixing runs, the plan's own
    /// sentence, or null while the computer builds that mix.
    /// </summary>
    public string? MixEqHeldByUnit(string mix) { lock (_gate) return MixEqUnitNoteLocked(mix); }

    private string? MixEqUnitNoteLocked(string mix)
        => RoutesOffJacksLocked && _hardwarePlan is { Active: true } plan
            && plan.Disabled.TryGetValue(MixEqDisabledKey(mix), out string? sentence) ? sentence : null;

    /// <summary>The mixes whose EQ the unit holds off now.</summary>
    private HashSet<string> MixEqsHeldLocked()
        => _config.Mixes.Where(m => MixEqUnitNoteLocked(m.Id) is not null).Select(m => m.Id).ToHashSet(StringComparer.Ordinal);

    /// <summary>Bring the stages in line after hardware mixing changed which mixes the unit builds.</summary>
    private void SyncMixEqAfterLocked(HashSet<string> heldBefore)
    {
        HashSet<string> held = MixEqsHeldLocked();
        foreach (string mix in held.Except(heldBefore).Concat(heldBefore.Except(held)).ToList())
            ApplyMixEqLocked(mix);
    }

    /// <summary>The EQ a mix's stage runs: the saved one, switched off while the unit builds the mix.</summary>
    private MicEq MixEqRunLocked(string mix)
    {
        MicEq eq = MixEqLocked(mix);
        return eq.Enabled && MixEqUnitNoteLocked(mix) is not null ? eq with { Enabled = false } : eq;
    }

    private MixDefinition RequireMixLocked(string cmd, string mix)
        => _config.Mixes.FirstOrDefault(m => m.Id == mix) ?? throw new ArgumentException($"{cmd}: '{mix}' is not a mix");

    /// <summary>Refuse a change while the unit builds the mix, with the plan's sentence.</summary>
    private void RequireMixEqEditableLocked(string cmd, string mix)
    {
        if (MixEqUnitNoteLocked(mix) is { } sentence) throw new InvalidOperationException($"{cmd}: {sentence}");
    }

    /// <summary>Replace a mix's whole EQ: its switch and bands.</summary>
    public void SetMixEq(string mix, MicEq eq)
    {
        if (MicEq.Check(eq) is string problem) throw new ArgumentException($"setEq: {problem}");
        lock (_gate)
        {
            RequireMixLocked("setEq", mix);
            RequireMixEqEditableLocked("setEq", mix);
            if (eq.Enabled && !MixEqLocked(mix).Enabled) _restarts.Forget(MixEqKey(mix));
            _mixEq[mix] = MicEq.Snap(eq);
            ApplyMixEqLocked(mix);
        }
    }

    /// <summary>A mix EQ's own switch.</summary>
    public void SetMixEqEnabled(string mix, bool on)
    {
        lock (_gate)
        {
            RequireMixLocked("setEqEnabled", mix);
            RequireMixEqEditableLocked("setEqEnabled", mix);
            if (on) _restarts.Forget(MixEqKey(mix));
            _mixEq[mix] = MixEqLocked(mix) with { Enabled = on };
            ApplyMixEqLocked(mix);
        }
    }

    /// <summary>Part of one existing band of a mix's EQ: the path a drag takes.</summary>
    public void SetMixEqBand(string mix, int band, EqBandChange change)
    {
        if (band is < 1 or > MicEq.MaximumBands) throw new ArgumentException($"setEqBand: band must be 1 to {MicEq.MaximumBands}");
        if (change.IsEmpty) throw new ArgumentException("setEqBand: need at least one of 'enabled', 'type', 'frequency', 'gain' and 'q'");
        lock (_gate)
        {
            RequireMixLocked("setEqBand", mix);
            RequireMixEqEditableLocked("setEqBand", mix);
            MicEq eq = MixEqLocked(mix);
            if (band > eq.Bands.Count) throw new ArgumentException("setEqBand: the band does not exist");
            EqBand next = change.ApplyTo(eq.Bands[band - 1]);
            if (MicEq.Check(next) is string problem) throw new ArgumentException($"setEqBand: {problem}");
            _mixEq[mix] = eq.WithBand(band, MicEq.Snap(next));
            ApplyMixEqLocked(mix);
        }
    }

    /// <summary>
    /// Bring a mix's stage to its values: written live to a running stage,
    /// which builds nothing; started and spliced in when the EQ is on and no
    /// stage runs. A stage that refuses a value, died, or runs another number
    /// of bands than an EQ that is on is replaced. One whose EQ went off is
    /// bypassed inside and kept.
    /// </summary>
    private void ApplyMixEqLocked(string mixId)
    {
        if (!_built || _config.Mixes.FirstOrDefault(m => m.Id == mixId) is not { } mix) return;
        MicEq eq = MixEqRunLocked(mixId);
        if (_mixEqStages.TryGetValue(mixId, out SoftwareEqBackend? stage))
        {
            if (stage.IsAlive && (!eq.Enabled || stage.BandCount == eq.Bands.Count))
            {
                try
                {
                    if (stage.BandCount == eq.Bands.Count) stage.Apply(eq);
                    else stage.SetEnabled(false);
                    return;
                }
                catch (InvalidOperationException ex)
                {
                    _pw.Note($"The EQ on {mix.Name} did not take a change and is started again: {ex.Message}");
                }
            }
            ReplaceMixEqLocked(mix);
            return;
        }
        if (eq.Enabled) ReplaceMixEqLocked(mix);
    }

    /// <summary>Every mix's stage brought to its saved values, after a recall.</summary>
    private void SyncMixEqStagesLocked()
    {
        foreach (MixDefinition mix in _config.Mixes.ToList()) ApplyMixEqLocked(mix.Id);
    }

    /// <summary>Where a mix's EQ takes its input: the mix's insert chain while one runs, else the mix's own monitor.</summary>
    private (string Node, string Prefix) MixEqUpstreamLocked(MixDefinition mix)
        => _chains.TryGetValue(MixKey(mix), out FilterHandle? chain) ? (chain.SourceName, "capture") : (mix.SinkName, "monitor");

    /// <summary>
    /// Start a mix's EQ stage and link the mix into it. Null when it cannot
    /// run, with the reason kept for the state; nothing is left behind then.
    /// </summary>
    private SoftwareEqBackend? StartMixEqStageLocked(MixDefinition mix, out PortLink? into)
    {
        into = null;
        string key = MixEqKey(mix.Id);
        if (_restarts.Blocked(key)) return null;
        MicEq eq = MixEqRunLocked(mix.Id);
        FilterHandle? filter = null;
        try
        {
            filter = _pw.CreateMixEq($"{mix.Id}_{++_mixEqGeneration}", SoftwareEqBackend.Controls(eq), eq.Bands.Count);
            var stage = new SoftwareEqBackend(_pw, filter, eq);
            (string node, string prefix) = MixEqUpstreamLocked(mix);
            into = _pw.LinkNodes(node, prefix, stage.Node, "playback");
            if (into.Pairs.Count != 2) throw new InvalidOperationException("The mix could not be linked into its EQ.");
            _mixEqErrors.Remove(mix.Id);
            return stage;
        }
        catch (InvalidOperationException ex)
        {
            if (into is not null) _pw.Unlink(into);
            into = null;
            if (filter is not null) _pw.StopFilter(filter);
            _mixEqErrors[mix.Id] = ex.Message;
            // A missing helper is not a failure to count: nothing retries it.
            if (_pw.EqHostInstalled) _restarts.Failed(key, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Put a mix's EQ stage right without touching its chain: start a fresh
    /// stage when the EQ is on, point everything that reads the mix at it (or
    /// at the chain or the mix again without one), then stop the old stage.
    /// False when nothing changed.
    /// </summary>
    private bool ReplaceMixEqLocked(MixDefinition mix)
    {
        SoftwareEqBackend? old = _mixEqStages.GetValueOrDefault(mix.Id);
        PortLink? into = null;
        SoftwareEqBackend? stage = MixEqRunLocked(mix.Id).Enabled ? StartMixEqStageLocked(mix, out into) : null;
        if (stage is null && old is null) return false;
        _mixEqStages.Remove(mix.Id);
        if (_mixEqInputs.Remove(mix.Id, out PortLink? oldInto)) _pw.Unlink(oldInto);
        if (stage is not null)
        {
            _mixEqStages[mix.Id] = stage;
            _mixEqInputs[mix.Id] = into!;
        }
        RewireMixConsumersLocked(mix);
        old?.Release();
        return true;
    }

    /// <summary>
    /// The mix's EQ as its chain is (re)built: a running stage has its input
    /// linked again from the mix's new upstream and keeps running; a stage
    /// that died goes; an EQ that is on with no stage gets one. Called by
    /// <see cref="WireMixChainLocked"/> before it points the mix's readers at
    /// the mix's tap, so they read the stage.
    /// </summary>
    private void WireMixEqInputLocked(MixDefinition mix)
    {
        if (_mixEqStages.TryGetValue(mix.Id, out SoftwareEqBackend? stage) && !stage.IsAlive)
        {
            _mixEqStages.Remove(mix.Id);
            if (_mixEqInputs.Remove(mix.Id, out PortLink? dead)) _pw.Unlink(dead);
            _restarts.Failed(MixEqKey(mix.Id), stage.Stage.LastWords);
            stage.Release();
            stage = null;
        }
        if (stage is null)
        {
            if (MixEqRunLocked(mix.Id).Enabled && StartMixEqStageLocked(mix, out PortLink? into) is { } started)
            {
                _mixEqStages[mix.Id] = started;
                _mixEqInputs[mix.Id] = into!;
            }
            return;
        }
        if (_mixEqInputs.Remove(mix.Id, out PortLink? old)) _pw.Unlink(old);
        (string node, string prefix) = MixEqUpstreamLocked(mix);
        PortLink link = _pw.LinkNodes(node, prefix, stage.Node, "playback");
        _mixEqInputs[mix.Id] = link;
        if (link.Pairs.Count != 2) _mixEqErrors[mix.Id] = "The mix could not be linked into its EQ.";
        else _mixEqErrors.Remove(mix.Id);
    }

    /// <summary>The link into a running stage, repaired by the sweep like the mix's other legs. True when it changed.</summary>
    private bool EnsureMixEqLinkLocked(MixDefinition mix)
    {
        if (!_mixEqStages.TryGetValue(mix.Id, out SoftwareEqBackend? stage) || !stage.IsAlive) return false;
        (string node, string prefix) = MixEqUpstreamLocked(mix);
        return EnsureMixLinkLocked(_mixEqInputs, mix.Id, node, prefix, stage.Node);
    }

    /// <summary>
    /// The sweep's part: a stage whose helper died counts against the restart
    /// policy keyed <c>mixeq:&lt;mix&gt;</c> and is started again; one that
    /// cannot start leaves the mix playing without its EQ, never silent. An EQ
    /// that is on but has no stage, because its start failed, is tried again
    /// on the same budget. True when a route changed.
    /// </summary>
    private bool HealMixEqStagesLocked()
    {
        if (!_built) return false;
        bool changed = false;
        foreach ((string id, SoftwareEqBackend stage) in _mixEqStages.ToList())
        {
            if (stage.IsAlive) continue;
            string? words = stage.Stage.LastWords;
            _restarts.Failed(MixEqKey(id), words);
            _pw.Note($"The EQ on mix {id} stopped{(words is null ? "." : $". The plugin host's last message: {words}")}");
            if (_config.Mixes.FirstOrDefault(m => m.Id == id) is { } mix) changed |= ReplaceMixEqLocked(mix);
            else
            {
                _mixEqStages.Remove(id);
                if (_mixEqInputs.Remove(id, out PortLink? into)) _pw.Unlink(into);
                stage.Release();
                changed = true;
            }
        }
        if (!_pw.EqHostInstalled) return changed;
        foreach (MixDefinition mix in _config.Mixes.ToList())
            if (!_mixEqStages.ContainsKey(mix.Id) && MixEqRunLocked(mix.Id).Enabled && _mixEqErrors.ContainsKey(mix.Id)
                && !_restarts.Blocked(MixEqKey(mix.Id)))
                changed |= ReplaceMixEqLocked(mix);
        return changed;
    }

    /// <summary>Stop one mix's stage and forget it, as the mix goes.</summary>
    private void RemoveMixEqLocked(string id)
    {
        if (_mixEqInputs.Remove(id, out PortLink? into)) _pw.Unlink(into);
        if (_mixEqStages.Remove(id, out SoftwareEqBackend? stage)) stage.Release();
        _mixEqErrors.Remove(id);
        _restarts.Forget(MixEqKey(id));
    }

    /// <summary>Stop every mix's stage, with the rest of the mix routes.</summary>
    private void RemoveMixEqStagesLocked()
    {
        foreach (PortLink into in _mixEqInputs.Values) _pw.Unlink(into);
        foreach (SoftwareEqBackend stage in _mixEqStages.Values) stage.Release();
        _mixEqInputs.Clear();
        _mixEqStages.Clear();
    }

    /// <summary>
    /// A mix's EQ for the state, as a microphone's is reported: the switch and
    /// bands as saved, <c>software</c> while a stage runs, and why it cannot
    /// run. Its processing is always <c>software</c>.
    /// </summary>
    private MicEqState MixEqStateLocked(string mix)
    {
        MicEq eq = MixEqLocked(mix);
        string key = MixEqKey(mix);
        bool installed = _pw.EqHostInstalled;
        bool blocked = _restarts.Blocked(key);
        string? error = !installed ? MixEqNeedsHost
            : blocked ? _restarts.Message(key, MixEqGivenUp)
            : eq.Enabled ? _mixEqErrors.GetValueOrDefault(mix) : null;
        string? backend = _mixEqStages.ContainsKey(mix) ? "software" : null;
        return new MicEqState(eq.Enabled, eq.Bands, backend, installed && !blocked, error) { Processing = MicEqProcessing.Software };
    }

    /// <summary>The saved EQs as the settings file keeps them: every mix of the layout that has one.</summary>
    private Dictionary<string, MicEq> ExportMixEqLocked()
        => _mixEq.Where(e => _config.Mixes.Any(m => m.Id == e.Key)).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);

    /// <summary>Every mix's EQ, for a profile: a recall then names every mix.</summary>
    private Dictionary<string, MicEq> SceneMixEqLocked()
        => _config.Mixes.ToDictionary(m => m.Id, m => MixEqLocked(m.Id), StringComparer.Ordinal);

    /// <summary>Store recalled EQs, snapped, for the mixes of this layout the recall names; an id that is no mix is ignored.</summary>
    private void RecallMixEqLocked(IReadOnlyDictionary<string, MicEq>? recalled)
    {
        foreach ((string mix, MicEq eq) in RecallEq(_mixEq, recalled))
        {
            if (_config.Mixes.All(m => m.Id != mix)) { _mixEq.Remove(mix); continue; }
            if (eq.Enabled && !MixEqLocked(mix).Enabled) _restarts.Forget(MixEqKey(mix));
            _mixEq[mix] = eq;
        }
    }
}
