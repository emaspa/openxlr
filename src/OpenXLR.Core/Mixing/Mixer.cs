namespace OpenXLR.Core.Mixing;

/// <summary>
/// Builds and maintains the submix graph, entirely from PipeWire filter sinks so
/// every node is clocked by construction and audio always flows:
///
///   application -> channel (combine sink over all mixes) -> mixes (null
///   sinks) -> direct port links -> the chosen output device.
///
/// A combine sink runs one internal stream per mix it feeds, and each of those
/// streams has its own volume and mute: those streams ARE the faders, so the
/// whole matrix needs only 7 channel sinks and 3 mix sinks. Everything is
/// clocked through the output device via the direct links (an earlier
/// loopback-based design stalled because its islands had no clock driver, and
/// a remap-cell design worked but exposed 21 extra sinks, which overwhelmed
/// desktop applets and helped exhaust pipewire-pulse's file descriptors).
///
/// The graph is built once; level changes touch only stream volumes, so audio
/// is never interrupted.
/// </summary>
public sealed partial class Mixer : IDisposable, ILayoutInfo
{
    private readonly PipeWireAdapter _pw;
    private readonly Dictionary<string, double> _levels = [];   // "channel|mix" -> level
    private readonly HashSet<string> _muted = [];               // "channel|mix"
    // While one of the Pro's hardware mic paths feeds the headphone jacks,
    // the software Monitor send of the channel that carries that input (XLR 1
    // or XLR 2, or a capture channel on the unit's node while the strip is
    // bound elsewhere) is silenced in the graph, not in the saved mix, so the
    // mic is not heard twice with a few ms between them. One slot per unit
    // input, index 0 for input 1; null while that path is off.
    private readonly string?[] _hardwareMicChannels = new string?[2];
    private readonly HashSet<string> _cells = [];               // cells that exist
    private readonly Dictionary<string, double> _mixVolume = [];
    private readonly HashSet<string> _mixMuted = [];
    private readonly object _gate = new();

    // The monitor mix's routes to its output devices (direct port links; the
    // monitor can feed several at once) and the hardware interface's per-pair
    // feeds into the input channels (XLR 1, XLR 2, Line In). Routes are keyed
    // by physical link target, so several pseudo-outputs sharing one underlying
    // route (the Wave XLR Pro's jacks all ride its monitor bus) make one link.
    private readonly Dictionary<string, PortLink> _monitorRoutes = [];
    private bool _monitorMuted;
    private readonly List<string> _monitorOutputs = [];
    // Output name -> mix ids feeding it. Absent = the first
    // monitor mix, so the dictionary only holds the exceptions (issue #21).
    private readonly Dictionary<string, string> _monitorFeeds = [];
    // Selected output name -> the description it last had in the device
    // list, so clients can still name an output that is away. Only selected
    // outputs keep an entry.
    private readonly Dictionary<string, string> _monitorOutputLabels = new(StringComparer.Ordinal);
    // The device list the labels were last read against; a new list or a
    // new selection reads them again.
    private string? _labelledDeviceList;
    private bool _monitorOutputLabelsDue;
    private readonly Dictionary<string, PortLink> _inputFeeds = [];
    private string? _inputDevice;   // the capture device the feeds come from
    private long _inputChainGeneration;

    // The Aux mix's route into the device's USB Aux port (return pair), and
    // whether the user wants that port fed at all.
    private PortLink? _auxRoute;
    private string? _auxTargetSink;
    private bool _auxPortEnabled = true;

    // Cached hardware volume of the selected output device, so external
    // changes (KDE applet, hardware knobs) can be detected and pushed, and
    // what the other selected outputs still owe that volume.
    private double? _outputVolume;
    private readonly OutputVolumeSync _outputVolumeDue = new();

    /// <summary>Follow the first selected monitor output as the system playback device.</summary>
    public const string FollowMonitorOutput = "@monitor";

    // Enforced system defaults (null = not enforced).
    private string? _enforcedSink;
    private string? _enforcedSource;

    private MixerConfig _config = MixerConfig.Default();
    private bool _built;

    private MeterReader _meters = new();

    public Mixer(PipeWireAdapter? adapter = null) => _pw = adapter ?? new PipeWireAdapter();

    /// <summary>
    /// Live stereo channel and mix levels, plus one reduction value in dB per
    /// running duck stage, each microphone meter before its chain that a
    /// client asked for, the Sound Check loop's playhead, and where each
    /// playing soundboard clip is.
    /// </summary>
    public IReadOnlyDictionary<string, double[]> ReadMeters()
    {
        IReadOnlyDictionary<string, double[]> levels = _meters.Read();
        if (_duckMeterHosts.IsEmpty && _inputMeterHosts.IsEmpty && _soundCheckProbe is null && _player is null) return levels;
        var meters = new Dictionary<string, double[]>(levels);
        foreach ((string mix, NativePluginHost host) in _duckMeterHosts)
            if (host.IsHealthy) meters[DuckKey(mix)] = [host.Meters.GetValueOrDefault("reduction")];
        AddInputLevels(meters);
        AddSoundCheckPosition(meters);
        AddClipPositions(meters);
        return meters;
    }

    public MixerConfig Config => _config;

    public bool Built => _built;

    private static string Cell(string channel, string mix) => $"{channel}|{mix}";

    // channel id -> its combine module; "channel|mix" -> that leg's sink-input index
    private readonly Dictionary<string, uint> _combineModules = [];
    // mix id -> its sink module, and for virtual microphones the post sink
    // and the published capture device, so one mix can be removed alone.
    private readonly Dictionary<string, uint> _mixModules = [];
    private readonly Dictionary<string, uint> _postModules = [];
    private readonly Dictionary<string, uint> _virtualMicModules = [];
    // A virtual microphone renamed while running keeps its old PipeWire
    // description until the next build; clients show a restart hint.
    private bool _renamedSinceBuild;
    /// <summary>Every channel combine feeds every mix sink, present or future.</summary>
    private const string MixSinkPattern = "~" + MixDefinition.SinkPrefix;
    private readonly Dictionary<string, int> _legIndex = [];
    // Cells whose combine leg was not there when their fader was last pushed.
    // A leg that turns up afterwards carries whatever the session manager
    // restores onto a new stream, full level and unmuted when it remembers
    // nothing, so the sweep pushes the stored fader onto it as soon as it
    // appears. See EnsureCellLevels.
    private readonly HashSet<string> _pendingCells = [];
    /// <summary>Rounds run on consecutive sweeps before backing off; a late leg is up in one or two.</summary>
    private const int CellPromptRounds = 3;
    /// <summary>The longest the sweep waits between rounds, so a send still waiting is tried at least once a minute.</summary>
    private const int CellMaxGapSweeps = 60;
    private int _cellRounds, _cellWait;

    /// <summary>Map every combine's internal streams to their (channel, mix) cells.</summary>
    private void DiscoverLegsLocked()
    {
        _legIndex.Clear();
        _duckLegIndex.Clear();
        foreach ((string chId, uint module) in _combineModules)
        {
            IReadOnlyDictionary<string, int> legs = _pw.FindCombineLegs(module);
            foreach (MixDefinition mix in _config.Mixes)
            {
                if (legs.TryGetValue(mix.SinkName, out int idx))
                    _legIndex[Cell(chId, mix.Id)] = idx;
                if (_duckBuses.ContainsKey(mix.Id) && legs.TryGetValue(mix.DuckBusName, out int duckIdx))
                    _duckLegIndex[Cell(chId, mix.Id)] = duckIdx;
            }
        }
    }

    /// <summary>
    /// Create the whole graph. <paramref name="monitorOutputSink"/> is the sink
    /// the monitor mix feeds; <paramref name="defaultSource"/> is the capture
    /// device applications should record from by default. Both are optional and
    /// changeable at runtime, and neither affects whether audio flows.
    /// </summary>
    public void Build(MixerConfig config, string? monitorOutputSink = null, string? defaultSource = null)
    {
        lock (_gate)
        {
            if (_built) TearDownLocked();
            _config = config;
            PruneBrowserChannelsLocked();
            _duckingSetup = _duckingSetup is null ? DuckingSetup.Default(config) : DuckingSetup.Prune(_duckingSetup, config);

            // A crashed or killed daemon never runs its teardown, and loading
            // over its leftover nodes fails the whole build with a name
            // collision. Clear any stray OpenXLR modules first.
            _pw.UnloadStaleModules("OpenXLR_");

            // WirePlumber auto-switches the default capture device to newly
            // created sources (our virtual mics). Remember the user's current
            // one so it can be put back unless a choice was passed in.
            string? previousDefaultSource = _pw.GetDefaultSource();

            // Mixes first: the cells attach to them as masters.
            foreach (MixDefinition mix in config.Mixes)
            {
                _mixModules[mix.Id] = _pw.CreateNullSink(mix.SinkName, $"OpenXLR {mix.Name}");
                _mixVolume[mix.Id] = Math.Clamp(mix.Volume, 0, MixVolumeMaximumLocked(mix.Id));
                if (mix.Muted) _mixMuted.Add(mix.Id);
            }

            // One combine per channel, feeding every mix. Its internal streams
            // (one per mix) are the faders.
            foreach (ChannelDefinition ch in config.Channels)
            {
                foreach (MixDefinition mix in config.Mixes)
                {
                    string cell = Cell(ch.Id, mix.Id);
                    _levels[cell] = ch.Levels.TryGetValue(mix.Id, out double v) ? v : 0.0;
                    if (ch.MutedIn.Contains(mix.Id)) _muted.Add(cell);
                    _cells.Add(cell);
                }
                _combineModules[ch.Id] = _pw.CreateCombineSink(ch.SinkName, MixSinkPattern,
                    $"OpenXLR {ch.Name}",
                    visible: ch.IsApplication);   // hardware inputs are not playback devices
            }
            DiscoverLegsLocked();

            // Push initial fader values.
            NormalizeExclusiveGroupsLocked();
            ReapplyMixesLocked(config.Mixes);
            _duckPlan = _duckPlan?.Prune(config);
            SyncDuckBusesLocked();

            // Publish non-monitor mixes as selectable capture devices. Each
            // reads a post sink fed from the mix (directly, or through the
            // mix's insert chain), so adding inserts later never recreates
            // the capture device an app is recording from.
            foreach (MixDefinition mix in config.Mixes.Where(m => m.Kind == MixKind.VirtualMic))
            {
                _postModules[mix.Id] = _pw.CreateNullSink(mix.PostSinkName, $"OpenXLR {mix.Name} (post)");
                _virtualMicModules[mix.Id] = _pw.CreateVirtualMic(mix.VirtualMicName, $"{mix.PostSinkName}.monitor", $"OpenXLR {mix.Name}");
            }

            // Meter every channel and mix so the UI can show what is flowing.
            foreach (ChannelDefinition ch in config.Channels) _meters.Add($"ch:{ch.Id}", ch.SinkName);
            foreach (MixDefinition mix in config.Mixes) _meters.Add($"mix:{mix.Id}", mix.SinkName);

            _built = true;

            // Close microphone channels before connecting their sources on a paused start.
            SyncMicrophonePauseLocked();

            _ = previousDefaultSource;   // defaults are governed by enforcement only
            _ = defaultSource;           // the system default never overrides an explicit input binding
            // Capture before playback: the interface's capture is linked
            // before any mix is routed into its sink. Elgato's full-speed
            // units (the XLR Dock, the original Wave XLR, the Wave:3) record
            // silence when their playback stream starts first.
            WireInputFeedsLocked();
            EnsureCaptureFeedsLocked();
            if (monitorOutputSink is not null) SetMonitorOutputsLocked([monitorOutputSink]);
            WireAuxRouteLocked();
            foreach (MixDefinition mix in config.Mixes) WireMixChainLocked(mix);
            // The soundboard's player went with the last graph; it starts
            // again here and loads its clips anew.
            StartSoundboardIfWantedLocked();
        }
    }

    /// <summary>
    /// Wire each input from its own source: the driven interface's
    /// capture pair (XLR 1 = pair 0, XLR 2 = pair 1, Aux In = pair 2), or one
    /// port of the exact source the strip is bound to. The interface is found
    /// by name; a strip whose source is absent is silent and the others keep
    /// playing. Capture inputs use their binding and chain width, and enter
    /// this path only while an insert, the EQ or Sound Check needs it.
    /// Each chained input is wired from its <see cref="InputRoute"/>.
    /// <paramref name="only"/> limits the work to those inputs: the rest keep
    /// their feeds and chains untouched, so their plugins keep running. Safe
    /// to call again after a hotplug.
    /// </summary>
    private void WireInputFeedsLocked(IReadOnlyCollection<string>? only = null)
    {
        string[] strips = [.. _config.Channels.Where(c => c.InputPair is not null && !UnconfirmedMicrophonePauseLocked(c)
            && (only is null || only.Contains(c.Id))).Select(c => c.Id)];
        if (strips.Length > 0) WireInputFeedLocked(strips);
        foreach (ChannelDefinition ch in _config.Channels.Where(c => c.CaptureSource is not null).ToList())
        {
            if (only is not null && !only.Contains(ch.Id)) continue;
            if (UnconfirmedMicrophonePauseLocked(ch)) continue;
            // Each capture commits alone. A link timeout on this source must
            // not roll back another microphone or fail the mixer's build.
            try
            {
                if (CaptureRouteLocked(ch))
                {
                    WireInputFeedLocked([ch.Id]);
                    if (_soundCheckChannel == ch.Id && !_inputFeeds.ContainsKey(ch.Id))
                        throw new InvalidOperationException("Sound Check could not connect its audio path.");
                    if (_captureFeeds.Remove(ch.Id, out PortLink? old))
                        UnlinkDiscardedInputFeed(old, _inputFeeds.GetValueOrDefault(ch.Id));
                }
                else
                {
                    RestoreCaptureFeedLocked(ch);
                    _chainStructures[ch.Id] = InputRouteLocked(ch, null).Structure;
                    _insertErrors.Remove(ch.Id);
                }
            }
            catch (Exception ex)
            {
                _insertErrors[ch.Id] = ex.Message;
                _restarts.Failed(ch.Id, ex.Message);
                if (_soundCheckChannel == ch.Id)
                    StopSoundCheckLocked(restore: false, error: "Sound Check stopped because its audio path was lost.");
                try
                {
                    string? source = RestoreCaptureFeedLocked(ch);
                    if (source is not null && CaptureRouteLocked(ch) && _captureFeeds.Remove(ch.Id, out PortLink? direct))
                    {
                        _inputFeeds[ch.Id] = direct;
                        _stripSources[ch.Id] = source;
                        _inputHeads[ch.Id] = source;
                    }
                }
                catch (Exception restore) { NoteCaptureLocked(ch.Id, restore.Message); }
                // An EQ that is on and lost its stage with the route is
                // tried again by the sweep, on its own restart budget.
                if (InputRouteLocked(ch, null).Eq && !_eqStages.ContainsKey(ch.Id)) _eqErrors[ch.Id] = ex.Message;
            }
        }
    }

    private void WireInputFeedLocked(IReadOnlyCollection<string> only)
    {
        // UCM split sources (".HiFi__Mic2__source" and friends) rank last: the
        // channels are wired by pair offset on the raw multichannel node, and
        // a split's first match could be the wrong input entirely.
        var sources = _pw.ListDevices()
            .Where(d => d.Kind == AudioNodeKind.Source && !d.IsOwn)
            .OrderBy(d => d.Name.Contains(".HiFi__", StringComparison.Ordinal) ? 1 : 0)
            .ToList();
        List<ChannelDefinition> strips = [.. _config.Channels.Where(c => c.InputPair is not null || c.CaptureSource is not null)];
        bool Rewires(ChannelDefinition ch) => only.Contains(ch.Id);
        bool unitTouched = strips.Any(ch => Rewires(ch) && ch.InputPair is not null && !_bindings.ContainsKey(ch.Id));
        string? unitNode = unitTouched ? ResolveInputLocked(sources) : _inputDevice;

        // The source node each strip will be fed from; a strip left out is silent.
        var next = new Dictionary<string, string>();
        foreach (ChannelDefinition ch in strips)
        {
            if (!Rewires(ch))
            {
                if (_stripSources.TryGetValue(ch.Id, out string? kept)) next[ch.Id] = kept;
                continue;
            }
            string? source;
            if (ch.CaptureSource is not null) source = ResolveCaptureSourceLocked(ch, sources);
            else if (_bindings.TryGetValue(ch.Id, out SourceBinding? binding)) source = ResolveBoundSourceLocked(ch.Id, binding, sources);
            else
            {
                _stripWarnings.Remove(ch.Id);
                source = unitNode;
            }
            if (source is not null) next[ch.Id] = source;
        }

        // The sample belongs to the microphone it was recorded from.
        if (_soundCheck is not null && _soundCheckChannel is { } checkedStrip && only.Contains(checkedStrip)
            && _soundCheckDevice != next.GetValueOrDefault(checkedStrip))
            StopSoundCheckLocked(restore: false, error: "Sound Check stopped because the input device changed.");

        // Console rule: a newly patched input comes up muted. Switching the
        // feed device once put a hot mic straight into the monitor outputs
        // (a feedback howl through the speakers), so a strip's monitor sends
        // start muted after its source changes and the user unmutes
        // deliberately. Only the strip whose source changed is muted: a
        // capture input or a bound strip when its own source changes, never
        // because the interface changed. A strip on the interface whose pair
        // one of the two units does not have (Aux In or XLR 2 on a one-input
        // unit) was or will be silent, so no live microphone is patched in
        // and its mutes stay as they are.
        foreach (ChannelDefinition ch in strips.Where(Rewires))
            if (_stripSources.TryGetValue(ch.Id, out string? before) && next.TryGetValue(ch.Id, out string? after) && before != after
                && (ch.InputPair is not int pair || _bindings.ContainsKey(ch.Id)
                    || CarriesPairLocked(sources, before, pair) && CarriesPairLocked(sources, after, pair)))
                MuteInputMonitorsLocked(ch.Id);

        // Build the replacement graph under unique node names while the old
        // one is still carrying audio. Only after every required port and link
        // exists do we remove the previous routes. A missing LADSPA/LV2 plugin
        // can therefore report an error without muting the microphone.
        var nextFeeds = new Dictionary<string, PortLink>();
        var nextChains = new Dictionary<string, FilterHandle>();
        var nextChainOuts = new Dictionary<string, PortLink>();
        // The EQ stages at the strips' heads, the links into them, and the
        // node each strip's feed comes from.
        var nextEqStages = new Dictionary<string, SoftwareEqBackend>();
        var nextEqInputs = new Dictionary<string, PortLink>();
        var nextEqUpstreams = new Dictionary<string, string>();
        var heads = new Dictionary<string, string>();
        var carried = new HashSet<string>();
        var routes = new Dictionary<string, InputRoute>();
        foreach (ChannelDefinition ch in strips.Where(Rewires))
            routes[ch.Id] = InputRouteLocked(ch, next.GetValueOrDefault(ch.Id));
        long generation = ++_inputChainGeneration;
        try
        {
            foreach (ChannelDefinition ch in strips)
            {
                if (!Rewires(ch))
                {
                    // Another strip's change leaves this one exactly as it is.
                    carried.Add(ch.Id);
                    if (_inputFeeds.TryGetValue(ch.Id, out PortLink? keptFeed)) nextFeeds[ch.Id] = keptFeed;
                    if (_chains.TryGetValue(ch.Id, out FilterHandle? keptChain)) nextChains[ch.Id] = keptChain;
                    if (_chainOuts.TryGetValue(ch.Id, out PortLink? keptOut)) nextChainOuts[ch.Id] = keptOut;
                    if (_eqStages.TryGetValue(ch.Id, out SoftwareEqBackend? keptStage)) nextEqStages[ch.Id] = keptStage;
                    if (_eqInputs.TryGetValue(ch.Id, out PortLink? keptInto)) nextEqInputs[ch.Id] = keptInto;
                    if (_eqUpstreams.TryGetValue(ch.Id, out string? keptUpstream)) nextEqUpstreams[ch.Id] = keptUpstream;
                    if (_inputHeads.TryGetValue(ch.Id, out string? keptHead)) heads[ch.Id] = keptHead;
                    continue;
                }
                InputRoute route = routes[ch.Id];
                if (route.Input is not string input) continue;
                bool bound = route.Bound;
                bool sameInput = _stripSources.GetValueOrDefault(ch.Id) == input
                    && _wiredBindings.GetValueOrDefault(ch.Id) == _bindings.GetValueOrDefault(ch.Id);
                string upstream = route.Source!;
                bool lc = route.LowCutHz > 0;
                bool cg = route.ClipGuard;
                List<InsertDefinition> inserts = route.Inserts;
                bool anyInsert = route.ActiveInserts;
                bool givenUp = anyInsert && _restarts.Blocked(ch.Id);
                if (givenUp) { inserts = []; anyInsert = false; _insertErrors[ch.Id] = _restarts.Message(ch.Id); }
                // A strip on the interface whose pair the device lacks (a
                // stereo interface has no XLR 2 or Aux In pair) stays silent;
                // no chain is started for it.
                if ((lc || cg || anyInsert || route.Eq) && !route.Looped && !bound && ch.InputPair is > 0
                    && !_pw.HasCaptureChannel(input, 2 * ch.InputPair.Value)) continue;

                // The EQ stage at the head: a running one is kept, so its
                // filters carry on, and only relinked when its upstream
                // changed; otherwise one is started while the EQ is on. A
                // stage that cannot run leaves the strip without it.
                SoftwareEqBackend? stage = null;
                if (route.Eq)
                {
                    if (_eqStages.TryGetValue(ch.Id, out SoftwareEqBackend? running) && running.IsAlive)
                    {
                        if (sameInput && _eqUpstreams.GetValueOrDefault(ch.Id) == upstream
                            && _eqInputs.TryGetValue(ch.Id, out PortLink? keptInto) && _pw.EnsureLinks(keptInto) != LinkHealth.Broken)
                        {
                            stage = running;
                            nextEqInputs[ch.Id] = keptInto;
                        }
                        else
                        {
                            PortLink into = RouteInputLocked(upstream, running.Node, ch, route.Looped);
                            if (into.Pairs.Count > 0) { stage = running; nextEqInputs[ch.Id] = into; }
                            else _eqErrors[ch.Id] = "The microphone could not be linked into its EQ.";
                        }
                    }
                    else if (StartEqStageLocked(ch, upstream, route.Looped, out PortLink? into) is { } started)
                    {
                        stage = started;
                        nextEqInputs[ch.Id] = into!;
                    }
                    if (stage is not null)
                    {
                        nextEqStages[ch.Id] = stage;
                        nextEqUpstreams[ch.Id] = upstream;
                    }
                }
                else _eqErrors.Remove(ch.Id);
                // What the chain or channel is fed from: the EQ stage's
                // single output, the loop helper's, or the source itself.
                string source = stage?.Node ?? upstream;
                bool looped = stage is not null || route.Looped;
                heads[ch.Id] = source;
                bool sameHead = _inputHeads.GetValueOrDefault(ch.Id) == source;
                if (lc || cg || anyInsert)
                {
                    if (!givenUp) _insertErrors.Remove(ch.Id);
                    FilterHandle chain;
                    string chainId = $"{ch.Id}_{generation}";
                    try
                    {
                        chain = _pw.CreateMicFilter(chainId, route.LowCutHz, cg,
                            ch.CaptureSource is null ? inserts : [.. inserts.Where(i => PluginCatalog.Find(i)?.Fits(route.Width) == true)],
                            route.Width);
                    }
                    catch (Exception ex) when (anyInsert)
                    {
                        // Insert failures fall back to the built-in DSP, or to
                        // a plain feed when this chain contained inserts only.
                        _insertErrors[ch.Id] = ex.Message;
                        if (!lc && !cg)
                        {
                            if (sameInput && sameHead && !_chains.ContainsKey(ch.Id)
                                && _inputFeeds.TryGetValue(ch.Id, out PortLink? existingFeed)
                                && _pw.EnsureLinks(existingFeed) != LinkHealth.Broken)
                                nextFeeds[ch.Id] = existingFeed;
                            else
                            {
                                PortLink plain = RouteInputLocked(source, ch.SinkName, ch, looped);
                                // No such capture pair on this device (see below): silent channel.
                                if (plain.Pairs.Count == 0) continue;
                                nextFeeds[ch.Id] = plain;
                            }
                            continue;
                        }
                        chain = _pw.CreateMicFilter(chainId + "_builtin", route.LowCutHz, cg);
                    }

                    // A port lookup can fail after the helper is ready too.
                    // Track the candidate before any routing so rollback owns it.
                    nextChains[ch.Id] = chain;
                    PortLink into = RouteInputLocked(source, chain.SinkName, ch, looped);
                    if (into.Pairs.Count == 0)
                    {
                        // The device has no capture pair at this offset (a
                        // stereo interface has no XLR 2 or Aux In pair): the
                        // channel stays silent, and the chain built for it is
                        // not needed.
                        _pw.StopFilter(chain);
                        nextChains.Remove(ch.Id);
                        if (ch.CaptureSource is not null)
                            throw new InvalidOperationException($"could not connect the capture source for {ch.Id}");
                        continue;
                    }
                    PortLink onward = RouteInputLocked(chain.SourceName, ch.SinkName, ch, processed: true);
                    if (onward.Pairs.Count == 0)
                    {
                        nextFeeds[ch.Id] = into;   // rolled back with the rest
                        throw new InvalidOperationException($"could not connect the filter chain for {ch.Id}");
                    }
                    nextFeeds[ch.Id] = into;
                    nextChainOuts[ch.Id] = onward;
                    continue;
                }

                // Reuse a healthy direct feed when neither the source nor the
                // DSP changed. A feed missing a pair is made again; the pair
                // that survived is kept connected (see UnlinkDiscardedInputFeed).
                if (sameInput && sameHead && !_chains.ContainsKey(ch.Id)
                    && _inputFeeds.TryGetValue(ch.Id, out PortLink? directFeed)
                    && _pw.EnsureLinks(directFeed) != LinkHealth.Broken)
                {
                    nextFeeds[ch.Id] = directFeed;
                    continue;
                }
                PortLink feed = RouteInputLocked(source, ch.SinkName, ch, looped);
                // The default config always defines XLR 1, XLR 2 and Aux In
                // (pairs 0, 1, 2); a device with fewer capture pairs has no
                // ports at the higher offsets and RouteInputToChannel makes
                // no links. That is a silent channel, not a failure: every
                // stereo interface (XLR Dock, Wave XLR, MK.2) would otherwise
                // fail the whole build here.
                if (feed.Pairs.Count == 0) continue;
                nextFeeds[ch.Id] = feed;
            }
        }
        catch
        {
            // Roll back only objects created for this candidate graph. Entries
            // reused from the old graph, and strips left alone, stay connected.
            foreach ((string key, PortLink link) in nextChainOuts)
                if (!carried.Contains(key)) _pw.Unlink(link);
            foreach ((string key, PortLink link) in nextFeeds)
                UnlinkDiscardedInputFeed(link, _inputFeeds.GetValueOrDefault(key) ?? _captureFeeds.GetValueOrDefault(key));
            foreach ((string key, FilterHandle chain) in nextChains)
                if (!carried.Contains(key)) _pw.StopFilter(chain);
            foreach ((string key, PortLink into) in nextEqInputs)
                if (!ReferenceEquals(into, _eqInputs.GetValueOrDefault(key))) _pw.Unlink(into);
            foreach ((string key, SoftwareEqBackend stage) in nextEqStages)
                if (!ReferenceEquals(stage, _eqStages.GetValueOrDefault(key))) stage.Release();
            throw;
        }

        foreach ((string key, PortLink old) in _inputFeeds)
            UnlinkDiscardedInputFeed(old, nextFeeds.GetValueOrDefault(key));
        foreach ((string key, PortLink old) in _chainOuts)
            if (!ReferenceEquals(old, nextChainOuts.GetValueOrDefault(key))) _pw.Unlink(old);
        foreach (string key in _chains.Keys.Where(k => !k.StartsWith("mix:", StringComparison.Ordinal)).ToList())
        {
            if (!ReferenceEquals(_chains[key], nextChains.GetValueOrDefault(key))) _pw.StopFilter(_chains[key]);
            _chains.Remove(key);
        }

        foreach ((string key, PortLink old) in _eqInputs)
            if (!ReferenceEquals(old, nextEqInputs.GetValueOrDefault(key))) _pw.Unlink(old);
        foreach ((string key, SoftwareEqBackend old) in _eqStages)
            if (!ReferenceEquals(old, nextEqStages.GetValueOrDefault(key))) old.Release();
        _eqInputs.Clear();
        foreach ((string key, PortLink into) in nextEqInputs) _eqInputs[key] = into;
        _eqStages.Clear();
        foreach ((string key, SoftwareEqBackend stage) in nextEqStages) _eqStages[key] = stage;
        _eqUpstreams.Clear();
        foreach ((string key, string upstream) in nextEqUpstreams) _eqUpstreams[key] = upstream;
        _inputHeads.Clear();
        foreach (string key in nextFeeds.Keys)
            if (heads.TryGetValue(key, out string? head)) _inputHeads[key] = head;

        _inputFeeds.Clear();
        foreach ((string key, PortLink feed) in nextFeeds) _inputFeeds[key] = feed;
        _chainOuts.Clear();
        foreach ((string key, PortLink link) in nextChainOuts) _chainOuts[key] = link;
        foreach ((string key, FilterHandle chain) in nextChains) _chains[key] = chain;
        foreach (ChannelDefinition ch in strips.Where(Rewires))
        {
            _chainStructures[ch.Id] = routes[ch.Id].Structure;
            if (next.TryGetValue(ch.Id, out string? source)) _stripSources[ch.Id] = source;
            else _stripSources.Remove(ch.Id);
            if (_bindings.TryGetValue(ch.Id, out SourceBinding? binding))
            {
                _wiredBindings[ch.Id] = binding;
                if (next.ContainsKey(ch.Id) && !_inputFeeds.ContainsKey(ch.Id))
                    _stripWarnings[ch.Id] = $"{binding.Source} could not be linked to the strip; retrying.";
            }
            else _wiredBindings.Remove(ch.Id);
            if (ch.CaptureSource is not null && next.ContainsKey(ch.Id))
                NoteCaptureLocked(ch.Id, _inputFeeds.ContainsKey(ch.Id) ? null
                    : $"{ch.CaptureSource} could not be linked to the channel; retrying.");
        }
        if (unitTouched)
        {
            _inputDevice = unitNode;
            // Line In is reported present only on the layout it was measured
            // in; the count comes from the same listing, not a helper call.
            if (unitNode is not null) _inputPorts = sources.FirstOrDefault(d => d.Name == unitNode)?.Channels;
        }

        // With processing or a loop in the path, a direct input-to-channel
        // link this daemon does not track (left by an earlier run, or auto-linked by
        // the session manager) would double the unfiltered signal onto the
        // filtered one. Clear such links now that the chain route is live;
        // the chain's own links are between other nodes and untouched.
        foreach (string key in nextChains.Keys.Union(nextEqStages.Keys).Union(routes.Where(r => r.Value.Looped).Select(r => r.Key)))
        {
            if (carried.Contains(key)) continue;
            ChannelDefinition? ch = _config.Channels.FirstOrDefault(c => c.Id == key);
            if (ch is not null && next.TryGetValue(key, out string? input)) _pw.UnlinkNodes(input, ch.SinkName);
        }
    }

    private void UnlinkDiscardedInputFeed(PortLink discarded, PortLink? retained)
    {
        if (ReferenceEquals(discarded, retained)) return;
        // A repaired feed can adopt surviving links. Keep those connected
        // both on commit and when rolling back a candidate.
        _pw.Unlink(retained is null ? discarded : new PortLink([.. discarded.Pairs.Except(retained.Pairs)]));
    }

    private void RemoveInputChainsLocked()
    {
        // Input chains only; mix chains are owned by WireMixChainLocked.
        foreach (PortLink link in _chainOuts.Values) _pw.Unlink(link);
        _chainOuts.Clear();
        foreach (string key in _chains.Keys.Where(k => !k.StartsWith("mix:", StringComparison.Ordinal)).ToList())
        {
            _pw.StopFilter(_chains[key]);
            _chains.Remove(key);
        }
    }

    private void RemoveMixChainsLocked()
    {
        RemoveMixDelaysLocked();
        foreach (PortLink link in _mixTaps.Values) _pw.Unlink(link);
        foreach (PortLink link in _mixPostLinks.Values) _pw.Unlink(link);
        _mixTaps.Clear();
        _mixPostLinks.Clear();
        foreach (string key in _chains.Keys.Where(k => k.StartsWith("mix:", StringComparison.Ordinal)).ToList())
        {
            _pw.StopFilter(_chains[key]);
            _chains.Remove(key);
        }
    }

    /// <summary>
    /// Count a chain's death against its restart budget, with what its helper
    /// last said. The line goes to the journal now and onto the insert if the
    /// chain is given up on; a helper that dies after it is ready is
    /// otherwise replaced without a word about why.
    /// </summary>
    private void ChainDiedLocked(string key, FilterHandle chain)
    {
        string? words = chain.LastWords;
        _restarts.Failed(key, words);
        if (words is not null) _pw.Note($"Insert chain {key} stopped. The plugin host's last message: {words}");
    }

    /// <summary>
    /// Sweep healing for built-in DSP and plugin filter chains: a dead holder
    /// process or broken link re-wires the affected path. True when something
    /// changed.
    /// </summary>
    public bool EnsureFilterRoutes()
    {
        // A process, so asked before the lock: whether the graph's rate moved
        // away from the soundboard player's.
        (SoundboardPlayer Player, int Rate)? rateAway = SoundboardRateAway();
        lock (_gate)
        {
            if (!_built) return false;
            EnsureInputMetersLocked();
            bool soundCheckChanged = EnsureSoundCheckLocked();
            soundCheckChanged |= EnsureSoundboardLocked(rateAway);
            soundCheckChanged |= HealEqStagesLocked();
            soundCheckChanged |= TrackNoiseReportsLocked();
            soundCheckChanged |= TrackVoiceReportsLocked();
            soundCheckChanged |= HealDuckBusesLocked();
            if (_chains.Count == 0 && _mixDelays.Count == 0 && _latencyReports.Count == 0)
                return EnsureMixRoutesLocked() || soundCheckChanged;
            bool changed = false;
            // Every chain heals on its own: a dead one is built again and the
            // others keep running.
            foreach (MixDefinition mix in _config.Mixes)
            {
                string key = MixKey(mix);
                if (_chains.TryGetValue(key, out FilterHandle? c) && !c.IsAlive)
                {
                    ChainDiedLocked(key, c);
                    WireMixChainLocked(mix);   // leaves the chain off once it has failed enough
                    changed = true;
                }
            }
            var broken = new List<string>();
            HashSet<string>? waitingForPause = MicrophonesPausedLocked && _microphonePausePending
                ? _config.Channels.Where(UnconfirmedMicrophonePauseLocked).Select(c => c.Id).ToHashSet(StringComparer.Ordinal) : null;
            foreach ((string key, FilterHandle chain) in _chains)
                if (!key.StartsWith("mix:", StringComparison.Ordinal) && waitingForPause?.Contains(key) != true && !chain.IsAlive)
                {
                    ChainDiedLocked(key, chain);
                    broken.Add(key);
                }
            foreach ((string key, PortLink link) in _chainOuts)
                if (waitingForPause?.Contains(key) != true && !broken.Contains(key) && _pw.EnsureLinks(link) == LinkHealth.Broken) broken.Add(key);
            if (broken.Count > 0) { WireInputFeedsLocked(broken); changed = true; }
            changed |= UpdateMixLatencyLocked();
            changed |= EnsureMixRoutesLocked();
            // A heal can retire the last bridged plugin without any command
            // being given: a chain the restart policy has given up on is left
            // off, and the helper that held Wine up goes with it. No rewire
            // follows to notice, so the same decision is taken here, against
            // the chains that now run, and carried out off this thread.
            if (changed)
            {
                IReadOnlyList<WineSession.Ending> endings = _pw.WineEndings();
                if (endings.Count > 0) Task.Run(() => _pw.EndWine(endings));
            }
            return changed || soundCheckChanged;
        }
    }

    private bool EnsureMixRoutesLocked()
    {
        bool changed = false;
        foreach (MixDefinition mix in _config.Mixes)
        {
            string key = MixKey(mix);
            if (_chains.TryGetValue(key, out FilterHandle? chain))
                changed |= EnsureMixLinkLocked(_mixTaps, key, mix.SinkName, "monitor", chain.SinkName);
            if (mix.Kind == MixKind.VirtualMic)
            {
                (string node, string prefix) = MixTapLocked(mix);
                changed |= EnsureMixLinkLocked(_mixPostLinks, key, node, prefix, mix.PostSinkName);
            }
        }
        return changed;
    }

    /// <summary>Repair a stereo mix leg without replacing its running plugin or dropping surviving pairs.</summary>
    private bool EnsureMixLinkLocked(Dictionary<string, PortLink> routes, string key, string from, string prefix, string to)
    {
        PortLink? previous = routes.GetValueOrDefault(key);
        if (previous is { Pairs.Count: >= 2 })
        {
            LinkHealth health = _pw.EnsureLinks(previous);
            if (health != LinkHealth.Broken) return health == LinkHealth.Relinked;
        }
        PortLink fresh = _pw.LinkNodes(from, prefix, to, "playback");
        routes[key] = fresh;
        if (previous is not null)
            _pw.Unlink(new PortLink([.. previous.Pairs.Except(fresh.Pairs)]));
        return fresh.Complete && fresh.Pairs.Count >= 2
            || previous is not null && (previous.Complete != fresh.Complete || !previous.Pairs.ToHashSet().SetEquals(fresh.Pairs));
    }

    /// <summary>
    /// Route the Aux mix into the device's USB Aux port (its dedicated return
    /// pair) when enabled and the device is present. The port selector and
    /// matrix cell are the daemon's job; this is only the PipeWire leg.
    /// </summary>
    private void WireAuxRouteLocked()
    {
        try
        {
            if (_auxRoute is not null) { _pw.Unlink(_auxRoute); _auxRoute = null; }
            _auxTargetSink = null;
            if (!_auxPortEnabled || !_hardwareOutputRouting) return;
            MixDefinition? aux = _config.Mixes.FirstOrDefault(m => m.Kind == MixKind.AuxPort);
            if (aux is null) return;
            string? proSink = HardwareOutputSinkLocked();
            if (proSink is null || HoldPlaybackLocked(proSink)) return;   // EnsureAuxRoute wires it later
            (string tapNode, string tapPrefix) = MixTapLocked(aux);
            PortLink route = _pw.RouteTapToOutput(tapNode, tapPrefix, proSink + "#usbaux");
            if (route.Pairs.Count > 0) { _auxRoute = route; _auxTargetSink = proSink; }
        }
        finally { SyncMonitorHardwareMutesLocked(); }
    }

    /// <summary>
    /// The Pro's raw sink. It is hidden from pickers, so it is derived from
    /// any Pro pseudo-output's bare name.
    /// </summary>
    private string? HardwareOutputSinkLocked()
        => _pw.ListDevices(true, _inputHints)
            .Where(d => d.Kind == AudioNodeKind.Sink &&
                        d.Name.Contains("Wave_XLR", StringComparison.OrdinalIgnoreCase))
            .Select(d => { int m = d.Name.IndexOf('#'); return m < 0 ? d.Name : d.Name[..m]; })
            .FirstOrDefault();

    /// <summary>
    /// Bounce the aux port's physical sink so the device re-latches its return
    /// routing. A plain suspend cycle is not enough while our port links keep
    /// the sink busy, so every link to it is dropped first, the stream is
    /// cycled on a genuinely idle sink, and the links are rebuilt (which
    /// reopens the stream with the matrix already set).
    /// </summary>
    public void BounceAuxTarget()
    {
        string? sink;
        lock (_gate)
        {
            sink = _auxTargetSink;
            if (sink is null || !_built) return;
            DisconnectMonitorRoutesLocked();
            if (_auxRoute is not null) { _pw.Unlink(_auxRoute); _auxRoute = null; }
        }
        _pw.BounceSink(sink);
        lock (_gate)
        {
            if (!_built) return;
            SetMonitorOutputsLocked([.. _monitorOutputs]);
            WireAuxRouteLocked();
        }
    }

    /// <summary>Whether the Aux mix feeds the USB Aux port.</summary>
    public bool AuxPortEnabled { get { lock (_gate) return _auxPortEnabled; } }

    public void SetAuxPortEnabled(bool on)
    {
        lock (_gate)
        {
            _auxPortEnabled = on;
            if (_built) WireAuxRouteLocked();
        }
    }

    /// <summary>
    /// Verify the aux route and repair it: relink a pair that died, and wire
    /// the route again when it came up short or its ports went away with a
    /// replugged interface. True when something was relinked or established.
    /// </summary>
    public bool EnsureAuxRoute()
    {
        lock (_gate)
        {
            if (!_built || !_auxPortEnabled) return false;
            if (_auxRoute is not null)
            {
                LinkHealth health = _pw.EnsureLinks(_auxRoute);
                if (health != LinkHealth.Broken) return health == LinkHealth.Relinked;
            }
            WireAuxRouteLocked();
            return _auxRoute is not null;
        }
    }

    private IReadOnlyList<string> _inputHints = [];   // the driven interface's name fragments, most specific first
    private bool _inputModelFallback = true;          // at most one interface attached: any Wave XLR source may feed the channels
    private string? _inputWarning;
    private string? _inputFallbackNoted;              // the source last reported as found by a later fragment

    /// <summary>Why the hardware input channels are silent although a source is present, or null.</summary>
    public string? InputWarning
    {
        get { lock (_gate) return InputWarningLocked(); }
    }

    /// <summary>Receives a line when the input source was found by a later fragment rather than the unit's serial.</summary>
    public Action<string>? InputNote { get; set; }

    /// <summary>
    /// The capture node that feeds the hardware input channels, or null.
    /// The driven interface is preferred, so a device switch moves the feeds
    /// with it: its serial fragment first, then its model fragment, for a
    /// model whose serial udev spells differently. While at most one
    /// supported interface is attached, any Wave XLR source comes last, so a
    /// fresh install without the udev rule, a failed claim or a unit set
    /// aside after hangs keeps the microphone working. With more than one
    /// attached, only the driven unit's own source feeds the channels, and a
    /// fragment that names the nodes of two cards feeds nothing: it would be
    /// a guess between two microphones.
    /// </summary>
    /// <summary>
    /// Whether a capture node has the ports of a strip's pair. A node no
    /// longer in the graph counts as having them: what it carried is not known.
    /// </summary>
    private bool CarriesPairLocked(IReadOnlyList<AudioNode> sources, string node, int pair)
    {
        if (sources.FirstOrDefault(d => d.Name == node) is not { } source) return true;
        int first = 2 * pair;
        return source.Channels is int ports ? ports > first : _pw.HasCaptureChannel(node, first);
    }

    private string? ResolveInputLocked(IReadOnlyList<AudioNode> sources)
    {
        IReadOnlyList<string> fragments = _inputModelFallback ? [.. _inputHints, "Wave_XLR"] : _inputHints;
        List<AudioNode> picked = InterfaceNodes.Pick(sources, fragments, out string? matched, out bool ambiguous);
        _inputWarning = ambiguous
            ? "More than one capture source matches the active interface, so the XLR channels stay silent. Pick the interface in the header, or add its source as a capture channel."
            : _inputHints.Count == 0 && !_inputModelFallback
                ? "More than one interface is attached and none is driven, so the XLR channels stay silent. Pick one in the header."
                : null;
        if (picked.Count == 0) return null;
        string source = picked[0].Name;
        bool byModel = _inputHints.Count > 0 && matched != _inputHints[0];
        if (byModel && _inputFallbackNoted != source)
            InputNote?.Invoke($"no capture source carries {_inputHints[0]}; using {source}, found by {matched}");
        _inputFallbackNoted = byModel ? source : null;
        return source;
    }
    private bool _hardwareOutputRouting;
    private int _lowCutHz;                 // 0 = off; software low cut on the first XLR channel
    // Filter-chains by insert key. Input keys hold a mono or stereo
    // chain per input that needs one (the first XLR channel's also
    // carries the soft low cut and ClipGuard); mix keys ("mix:stream") hold
    // a stereo chain spliced between the mix and its consumers.
    private readonly Dictionary<string, FilterHandle> _chains = new();
    private readonly Dictionary<string, PortLink> _chainOuts = new();   // input chains: source half into the channel sink
    private readonly Dictionary<string, PortLink> _mixTaps = new();     // mix key: mix monitor into chain or post sink
    private readonly Dictionary<string, PortLink> _mixPostLinks = new(); // mix key: chain source into the post sink

    // Plugin insert chains by key, and why a key's last build fell back to
    // running without its inserts.
    private readonly Dictionary<string, List<InsertDefinition>> _inserts = new();
    private readonly Dictionary<string, string> _insertErrors = new();
    private readonly RestartPolicy _restarts = new(() => Environment.TickCount64);

    /// <summary>Insert keys: inputs with a chain width, and "mix:&lt;id&gt;" for every mix.</summary>
    private bool IsInsertChannel(string key) => ChainWidthLocked(key) is not null;

    private int? ChainWidthLocked(string key)
        => MixForKey(key) is not null ? 2 : _config.Channels.FirstOrDefault(c => c.Id == key)?.ChainWidth;

    // ILayoutInfo, for command validation ahead of the mixer methods.
    public bool HasChannel(string id) { lock (_gate) return _config.Channels.Any(c => c.Id == id); }
    public bool HasMix(string id) { lock (_gate) return _config.Mixes.Any(m => m.Id == id); }
    public bool HasApplicationChannel(string id) { lock (_gate) return _config.Channels.Any(c => c.Id == id && c.IsApplication); }
    public bool HasEditableChannel(string id) { lock (_gate) return _config.Channels.Any(c => c.Id == id && c.InputPair is null); }
    public bool HasCaptureChannel(string id) { lock (_gate) return _config.Channels.Any(c => c.Id == id && c.CaptureSource is not null); }
    public bool HasEditableMix(string id) { lock (_gate) return _config.Mixes.Any(m => m.Id == id && m.IsEditable); }
    public bool IsMonitorFeed(string feed) { lock (_gate) return NormalizeFeedLocked(feed) is not null; }

    /// <summary>
    /// A feed as the mixer stores it: the mixes it names, in the
    /// layout's order, joined with '+'. Null when it names anything else,
    /// nothing, or the same mix twice.
    /// </summary>
    private string? NormalizeFeedLocked(string feed)
    {
        IReadOnlyList<string> parts = MonitorFeed.Parts(feed);
        if (parts.Count == 0 || parts.Distinct().Count() != parts.Count) return null;
        if (parts.Any(p => _config.Mixes.All(m => m.Id != p))) return null;
        return MonitorFeed.Join(_config.Mixes.Where(m => parts.Contains(m.Id)).Select(m => m.Id));
    }

    /// <summary>Direct mic monitoring must never remove the mic from a recorded or Aux mix.</summary>
    public bool IsMonitorOnlyFeed(string feed)
    {
        lock (_gate)
            return NormalizeFeedLocked(feed) is string normalized
                && MonitorFeed.Parts(normalized).All(id => MonitorMixLocked(id) is not null);
    }
    public bool IsMonitorOutput(string device) { lock (_gate) return MonitorOutputsForLocked(device).Any(); }

    /// <summary>
    /// The selected outputs a feed command names: the output itself, or every
    /// pseudo-output of one device when the name ends at the '#' marker.
    /// </summary>
    private IEnumerable<string> MonitorOutputsForLocked(string output)
    {
        int marker = output.IndexOf('#');
        return marker < 0 ? (_monitorOutputs.Contains(output) ? [output] : [])
            : _monitorOutputs.Where(o => o.StartsWith(output[..(marker + 1)], StringComparison.Ordinal));
    }
    public bool IsInsertKey(string key) { lock (_gate) return IsInsertChannel(key); }
    public int InsertChannels(string key) { lock (_gate) return ChainWidthLocked(key) ?? 0; }

    public InsertDefinition? InsertInChain(string key, string id) { lock (_gate) return InsertsFor(key).FirstOrDefault(i => i.Id == id); }
    public int OverrideCount { get { lock (_gate) return Matcher.Overrides.Count; } }
    public bool HasOverride(string identity)
    {
        lock (_gate) return Matcher.Overrides.ContainsKey(StreamMatcher.MigrateIdentity(identity));
    }

    /// <summary>Live assignments may update existing entries but cannot grow the registry past this count.</summary>
    public const int MaxAppOverrides = 512;

    // Called under the state lock, before either the remembered choice or its
    // live route changes. Restoring older saved assignments stays lossless.
    private void EnsureOverrideCapacity(string identity)
    {
        if (Matcher.Overrides.Count >= MaxAppOverrides && !Matcher.Overrides.ContainsKey(identity))
            throw new InvalidOperationException($"{MaxAppOverrides} remembered applications already; forget some first");
    }

    private MixDefinition? MixForKey(string key)
        => key.StartsWith("mix:", StringComparison.Ordinal) ? _config.Mixes.FirstOrDefault(m => m.Id == key[4..]) : null;

    private static string MixKey(MixDefinition mix) => $"mix:{mix.Id}";

    /// <summary>
    /// Where a mix's consumers should read from: its latency delay when it
    /// has one, else its insert chain when one runs, else its own monitor.
    /// </summary>
    private (string Node, string Prefix) MixTapLocked(MixDefinition mix)
        => _mixDelays.TryGetValue(mix.Id, out FilterHandle? delay) ? (delay.SourceName, "capture")
        : _chains.TryGetValue(MixKey(mix), out FilterHandle? chain) ? (chain.SourceName, "capture") : (mix.SinkName, "monitor");

    /// <summary>
    /// (Re)build one mix's insert chain and re-point everything that reads
    /// the mix: the post sink behind a virtual mic, the monitor routes, or
    /// the aux route. Without inserts the mix feeds them directly.
    /// </summary>
    private void WireMixChainLocked(MixDefinition mix)
    {
        string key = MixKey(mix);
        if (_mixTaps.Remove(key, out PortLink? tap)) _pw.Unlink(tap);
        if (_chains.Remove(key, out FilterHandle? old)) _pw.StopFilter(old);
        _insertErrors.Remove(key);
        _chainStructures[key] = MixChainLocked(key).Structure;

        List<InsertDefinition> inserts = InsertsFor(key);
        bool anyInsert = inserts.Any(i => !i.Bypass && PluginCatalog.Find(i) is { } p && p.Fits(2));
        if (anyInsert && _restarts.Blocked(key)) _insertErrors[key] = _restarts.Message(key);
        else if (anyInsert)
        {
            try
            {
                FilterHandle chain = _pw.CreateMixChain(mix.Id, $"OpenXLR {mix.Name} Inserts", inserts);
                _chains[key] = chain;
                _mixTaps[key] = _pw.LinkNodes(mix.SinkName, "monitor", chain.SinkName, "playback");
            }
            catch (Exception ex)
            {
                _insertErrors[key] = ex.Message;   // the mix keeps flowing without its inserts
            }
        }
        RewireMixConsumersLocked(mix);
    }

    /// <summary>
    /// Re-point what reads a mix at its current tap, rebuilding its latency
    /// delay on the way. The plugins keep running, so a delay can come and
    /// go without resetting them.
    /// </summary>
    private void RewireMixConsumersLocked(MixDefinition mix)
    {
        string key = MixKey(mix);
        if (_mixPostLinks.Remove(key, out PortLink? post)) _pw.Unlink(post);
        WireMixDelayLocked(mix);
        (string node, string prefix) = MixTapLocked(mix);
        switch (mix.Kind)
        {
            case MixKind.VirtualMic:
                // The virtual mic reads the post sink, so its identity never
                // changes when a chain comes or goes.
                _mixPostLinks[key] = _pw.LinkNodes(node, prefix, mix.PostSinkName, "playback");
                break;
            case MixKind.AuxPort:
                WireAuxRouteLocked();
                break;
        }
        // Every mix can now feed an output, including those with a virtual
        // microphone or Aux consumer. Follow its new post-insert tap too.
        if (_monitorOutputs.Any(o => MixesForOutputLocked(o).Any(m => m.Id == mix.Id)))
            SetMonitorOutputsLocked([.. _monitorOutputs]);
    }

    private List<InsertDefinition> InsertsFor(string channelId)
        => _inserts.TryGetValue(channelId, out List<InsertDefinition>? l) ? l : [];

    /// <summary>
    /// Every plugin the saved chains name, bypassed ones included: the
    /// catalogue a client is sent has to hold these whatever its size limit,
    /// or a chain the daemon loads looks like an unknown plugin in the window
    /// and on the Stream Deck.
    /// </summary>
    public IReadOnlyCollection<(string Kind, string Plugin)> InsertPlugins()
    {
        lock (_gate) return [.. _inserts.Values.SelectMany(list => list).Select(i => (i.Kind, i.Plugin))];
    }

    /// <summary>Software low cut frequency (0, 80, or 120 Hz; 0 = off).</summary>
    public int LowCutHz { get { lock (_gate) return _lowCutHz; } }

    private bool _lowCutApplicable = true;

    /// <summary>
    /// Whether the soft low cut may engage on a strip that takes the
    /// interface: false while the active device has a hardware low cut
    /// (stacking both would double-filter). A strip bound to a source has no
    /// hardware stage in front of it and keeps the soft one either way, so
    /// only an XLR 1 on the interface is rewired. The stored frequency
    /// survives, so switching back re-engages it.
    /// </summary>
    public void SetLowCutApplicable(bool applicable)
    {
        lock (_gate)
        {
            if (_lowCutApplicable == applicable) return;
            _lowCutApplicable = applicable;
            if (_built && _lowCutHz > 0) RewireSoftDspStripLocked();
        }
    }

    /// <summary>
    /// The strip the software low cut and ClipGuard sit on (pair 0), when it
    /// takes the interface: the one whose filters follow the unit's
    /// capabilities. A bound XLR 1 is left alone, so a unit coming or going
    /// never interrupts it.
    /// </summary>
    private void RewireSoftDspStripLocked()
    {
        if (_config.Channels.FirstOrDefault(c => c.InputPair == 0) is { } strip && !_bindings.ContainsKey(strip.Id))
            WireInputFeedsLocked([strip.Id]);
    }

    /// <summary>The strip the software low cut and ClipGuard sit on, bound or not; XLR 2's chain is not touched.</summary>
    private string[] SoftDspStripLocked()
        => [.. _config.Channels.Where(c => c.InputPair == 0).Select(c => c.Id)];

    // Software ClipGuard: a post-ADC hard limiter at -3 dB in the mic filter
    // chain, for devices whose ClipGuard runs host-side in the vendor app. It
    // limits the recorded signal but cannot undo clipping in the preamp/ADC.
    private bool _softClipGuard;
    private bool _clipGuardApplicable = true;

    /// <summary>Whether the software ClipGuard is enabled.</summary>
    public bool SoftClipGuard { get { lock (_gate) return _softClipGuard; } }

    public void SetSoftClipGuard(bool on)
    {
        lock (_gate)
        {
            if (_softClipGuard == on) return;
            if (on && SoftClipGuardApplicableLocked)
            {
                DspFeatureAvailability support = _pw.GetSoftwareClipGuardAvailability();
                if (!support.Available) throw new InvalidOperationException(support.Error);
            }
            bool previous = _softClipGuard;
            _softClipGuard = on;
            try
            {
                if (_built) WireInputFeedsLocked(SoftDspStripLocked());
            }
            catch
            {
                // WireInputFeedsLocked swaps only after the candidate graph is
                // complete, so restoring the requested state is enough: the
                // previous audible graph is still intact.
                _softClipGuard = previous;
                throw;
            }
        }
    }

    /// <summary>
    /// False while the active device has the hardware ClipGuard. Like the low
    /// cut, it changes nothing for an XLR 1 bound to a source, which keeps
    /// the software ClipGuard whatever unit is driven.
    /// </summary>
    public void SetClipGuardApplicable(bool applicable)
    {
        lock (_gate)
        {
            if (_clipGuardApplicable == applicable) return;
            bool before = SoftClipGuardApplicableLocked;
            _clipGuardApplicable = applicable;
            if (before == SoftClipGuardApplicableLocked) return;
            if (applicable && _softClipGuard && !_pw.GetSoftwareClipGuardAvailability().Available)
                _softClipGuard = false;
            if (_built && _softClipGuard) RewireSoftDspStripLocked();
        }
    }

    // The software low cut and ClipGuard apply to XLR 1 while nothing in front
    // of it does the same in hardware: a source binding, or a unit without
    // the control.
    private bool SoftLowCutApplicableLocked => _bindings.ContainsKey("xlr1") || _lowCutApplicable;
    private bool SoftClipGuardApplicableLocked => _bindings.ContainsKey("xlr1") || _clipGuardApplicable;

    /// <summary>
    /// Set the software low cut for the first XLR channel: a host-side
    /// high-pass for devices whose DSP lives in the vendor app (the XLR
    /// Dock), matching Wave Link's 80/120 Hz choices. Devices with a real
    /// hardware low cut never see this path; the UI gates it by capability.
    /// </summary>
    public void SetLowCutHz(int hz)
    {
        if (hz is not (0 or 80 or 120)) return;
        lock (_gate)
        {
            if (_lowCutHz == hz) return;
            int previous = _lowCutHz;
            _lowCutHz = hz;
            try
            {
                if (_built) WireInputFeedsLocked(SoftDspStripLocked());
            }
            catch
            {
                _lowCutHz = previous;
                throw;
            }
        }
    }

    // --- plugin inserts ---

    /// <summary>Replace a channel's insert chain (order matters); rewires when built.</summary>
    public void SetInserts(string channel, IReadOnlyList<InsertDefinition> inserts)
    {
        lock (_gate)
        {
            // A CLAP or VST3 plugin only ever runs in the native host, so its record
            // says so whatever a client sent; the window then shows it without a switch.
            _inserts[channel] = [.. inserts.Select(i => i with
            {
                Params = new Dictionary<string, double>(i.Params),
                NativeHost = i.RunsNatively,
            })];
            if (_built) RewireInsertKeyLocked(channel);
        }
    }

    /// <summary>Remove every occurrence of the selected plugins, then save before acknowledging.</summary>
    public (int Removed, int Chains, string? Error) RemovePluginInserts(
        IReadOnlySet<(string Kind, string Plugin)> plugins, Func<MixerSettings, string?> save)
    {
        lock (_gate)
        {
            try
            {
                return RemovePluginInserts(_inserts, plugins,
                    keys => { if (_built) RewireInsertKeysLocked(keys, endWine: false); },
                    () =>
                    {
                        if (save(ExportSettings()) is string error) throw new IOException(error);
                    });
            }
            finally { if (_built) EndUnusedWineLocked(); }
        }
    }

    internal static (int Removed, int Chains, string? Error) RemovePluginInserts(
        IDictionary<string, List<InsertDefinition>> inserts, IReadOnlySet<(string Kind, string Plugin)> plugins,
        Action<IReadOnlyList<string>> rewire, Action save)
    {
        var edits = inserts.Select(e => (e.Key, Before: e.Value,
            After: e.Value.Where(i => !plugins.Contains((i.Kind, i.Plugin))).ToList()))
            .Where(e => e.Before.Count != e.After.Count).ToArray();
        // Every chain is rebuilt on its own, inputs as well as mixes, so a
        // failure on one leaves the others changed and running.
        var groups = edits.Select(e => new[] { e }).ToArray();
        var completed = new List<string[]>();
        var errors = new List<string>();
        int removed = 0, chains = 0;
        foreach (var group in groups)
        {
            string[] keys = [.. group.Select(e => e.Key)];
            foreach (var edit in group) inserts[edit.Key] = edit.After;
            try { rewire(keys); }
            catch (Exception ex)
            {
                foreach (var edit in group) inserts[edit.Key] = edit.Before;
                errors.Add($"{string.Join(", ", keys)}: {ex.Message}");
                try { rewire(keys); }
                catch (Exception rollback) { errors.Add($"Could not restore {string.Join(", ", keys)} audio: {rollback.Message}"); }
                continue;
            }
            completed.Add(keys);
            removed += group.Sum(e => e.Before.Count - e.After.Count);
            chains += group.Length;
        }
        try { save(); }
        catch (Exception ex)
        {
            foreach (var edit in edits) inserts[edit.Key] = edit.Before;
            var rollbackErrors = new List<string>(errors);
            foreach (string[] keys in completed)
                try { rewire(keys); }
                catch (Exception rollback) { rollbackErrors.Add(rollback.Message); }
            throw new InvalidOperationException("Could not save plugin removal; previous chain settings were restored: " + ex.Message
                + (rollbackErrors.Count == 0 ? "" : " Audio recovery also reported: " + string.Join("; ", rollbackErrors)), ex);
        }
        return (removed, chains, errors.Count == 0 ? null : "Some chains could not be changed: " + string.Join("; ", errors));
    }

    /// <summary>Bypass or re-enable one insert; rewires when built.</summary>
    public void SetInsertBypass(string channel, string insertId, bool bypass)
    {
        lock (_gate)
        {
            if (!_inserts.TryGetValue(channel, out List<InsertDefinition>? list)) return;
            int idx = list.FindIndex(i => i.Id == insertId);
            if (idx < 0 || list[idx].Bypass == bypass) return;
            list[idx] = list[idx] with { Bypass = bypass };
            if (_built) RewireInsertKeyLocked(channel);
        }
    }

    /// <summary>Change one insert's label. A display edit: the running plugin and its ports are kept.</summary>
    public void RenameInsert(string channel, string insertId, string name)
    {
        lock (_gate)
        {
            if (!_inserts.TryGetValue(channel, out List<InsertDefinition>? list)) throw new ArgumentException("Unknown insert chain.");
            int idx = list.FindIndex(i => i.Id == insertId);
            if (idx < 0) throw new ArgumentException("Unknown insert.");
            list[idx] = list[idx] with { Label = name };
        }
    }

    private void RewireInsertKeyLocked(string key) => RewireInsertKeysLocked([key]);

    /// <summary>
    /// Build the chains of these keys again, and only them: another input's
    /// chain, or a mix's, keeps its running plugins.
    /// </summary>
    private void RewireInsertKeysLocked(IReadOnlyList<string> keys, bool endWine = true)
    {
        foreach (string key in keys) _restarts.Forget(key);
        var inputs = new List<string>();
        foreach (string key in keys)
        {
            if (MixForKey(key) is MixDefinition mix) WireMixChainLocked(mix);
            else if (IsInsertChannel(key)) inputs.Add(key);
        }
        if (inputs.Count > 0) WireInputFeedsLocked(inputs);
        if (endWine) EndUnusedWineLocked();
    }

    private void EndUnusedWineLocked()
    {
        // The new chain is up: if it holds no bridged plugin any more, Wine is
        // resident for nothing, and it would still be resident hours later
        // when the daemon stops. The decision is taken here, under the lock,
        // against the chain that now runs; the helper runs off this thread so
        // the command that caused the rewire answers straight away.
        IReadOnlyList<WineSession.Ending> endings = _pw.WineEndings();
        if (endings.Count > 0) Task.Run(() => _pw.EndWine(endings));
    }

    /// <summary>Capture the host under the mixer lock, then open its UI outside it.</summary>
    public void ShowInsertUi(string channel, string insertId, Func<InsertDefinition, string?>? editorError = null)
    {
        NativePluginHost? host;
        lock (_gate)
        {
            InsertDefinition? insert = InsertsFor(channel).FirstOrDefault(i => i.Id == insertId);
            if (insert is not null && editorError?.Invoke(insert) is string reason)
                throw new InvalidOperationException($"Native editor disabled: {reason}");
            host = _chains.GetValueOrDefault(channel)?.InsertStages
                .FirstOrDefault(stage => stage.Id == insertId).Stage?.NativeHost;
            if (host is null)
                throw new InvalidOperationException("No native editor is running for this insert. Enable it and install the optional native host.");
        }
        host.ShowUi();
    }

    /// <summary>Collect editor changes on the same path used to persist ordinary controls.</summary>
    public bool SyncPluginControls()
    {
        lock (_gate)
        {
            bool changed = false;
            foreach ((string channel, FilterHandle chain) in _chains)
                foreach ((string id, FilterHandle stage) in chain.InsertStages)
                {
                    if (stage.NativeHost is not { } host) continue;
                    foreach ((string symbol, double value) in host.DrainChanges())
                    {
                        InsertDefinition? insert = InsertsFor(channel).FirstOrDefault(i => i.Id == id);
                        PluginParam? parameter = insert is null ? null : PluginCatalog.Find(insert)?.Params.FirstOrDefault(p => p.Symbol == symbol);
                        if (parameter is null || !double.IsFinite(value)) continue;
                        insert!.Params[symbol] = Math.Clamp(value, parameter.Min, parameter.Max);
                        changed = true;
                    }
                }
            return changed;
        }
    }

    public void SetInsertParam(string channel, string insertId, string symbol, double value)
    {
        lock (_gate)
        {
            if (!_inserts.TryGetValue(channel, out List<InsertDefinition>? list)) return;
            int idx = list.FindIndex(i => i.Id == insertId);
            if (idx < 0) return;
            Dictionary<string, double> parameters = list[idx].Params;
            if (!parameters.ContainsKey(symbol) && parameters.Count >= InsertDefinition.MaxParams)
                throw new InvalidOperationException($"insert '{insertId}' already holds {InsertDefinition.MaxParams} control values");
            parameters[symbol] = value;
            // A stage that refuses the value is built again with it, alone.
            if (_built && !TryPushInsertParamLocked(channel, list, idx, symbol, value)) RewireInsertKeyLocked(channel);
        }
    }

    /// <summary>Insert chains with live status, for the state push.</summary>
    private Dictionary<string, IReadOnlyList<InsertStatus>> InsertStatusLocked()
    {
        var result = new Dictionary<string, IReadOnlyList<InsertStatus>>();
        foreach ((string channel, List<InsertDefinition> list) in _inserts)
        {
            result[channel] = [.. list.Select(i =>
            {
                FilterHandle? stage = _chains.GetValueOrDefault(channel)?.InsertStages
                    .FirstOrDefault(s => s.Id == i.Id).Stage;
                NativePluginHost? host = stage?.NativeHost;
                return new InsertStatus(i,
                    PluginCatalog.Find(i) is not { } plugin ? "plugin not installed"
                    : _config.Channels.FirstOrDefault(c => c.Id == channel && c.CaptureSource is not null)?.ChainWidth is int width
                        && !plugin.Fits(width) ? $"plugin has no {(width == 1 ? "mono" : "stereo")} layout for this chain"
                    : !i.Bypass && _insertErrors.TryGetValue(channel, out string? err) ? err
                    : host?.EditorStalled == true ? "the plugin's editor stopped answering; its controls are frozen while audio keeps playing"
                    : null, host?.Meters, host?.IsRunning == true)
                { FilterChainError = stage?.FilterChainError, LatencyMilliseconds = InsertLatencyLocked(channel, i) };
            })];
        }
        return result;
    }

    // What the active device's input side actually has. Null means no device
    // is connected, and then every channel is reported present, which is what
    // the window has always shown without one.
    private int? _deviceXlrInputs, _lastXlrInputs;
    private bool? _deviceAuxInput, _lastAuxInput, _deviceLineInput, _lastLineInput;
    // The port count of the interface's capture source as last wired, kept
    // while the unit is away like the jacks above.
    private int? _inputPorts;

    /// <summary>
    /// The active device's XLR jack count and whether it has the auxiliary
    /// input stage and the analog Line In. The channels the device cannot
    /// feed are still built and still hold their levels; they are reported as
    /// not present so clients leave them out. Returns true when the answer
    /// changed.
    /// </summary>
    public bool SetInputJacks(int? xlrInputs, bool? auxInput, bool? lineInput = null)
    {
        lock (_gate)
        {
            if (_deviceXlrInputs == xlrInputs && _deviceAuxInput == auxInput && _deviceLineInput == lineInput) return false;
            _deviceXlrInputs = xlrInputs;
            _deviceAuxInput = auxInput;
            _deviceLineInput = lineInput;
            // The last connected unit's jacks keep the strips through a replug.
            if (xlrInputs is not null) (_lastXlrInputs, _lastAuxInput, _lastLineInput) = (xlrInputs, auxInput, lineInput);
            return true;
        }
    }

    /// <summary>
    /// Name fragment of the interface whose capture should feed the input
    /// channels (the daemon's active device). A change re-wires the feeds.
    /// Any Wave XLR source feeds them when the hint finds nothing.
    /// </summary>
    public void SetInputDeviceHint(string? hint, bool hardwareOutputRouting = false)
        => SetInputDeviceHint(hint is null ? [] : [hint], hardwareOutputRouting, modelFallback: true);

    /// <summary>
    /// The driven interface's name fragments, most specific first, or none
    /// when no interface is driven. <paramref name="modelFallback"/> says
    /// whether any Wave XLR source may feed the channels when the fragments
    /// find nothing: true while at most one supported interface is attached.
    /// </summary>
    public void SetInputDeviceHint(IReadOnlyList<string> hints, bool hardwareOutputRouting, bool modelFallback)
    {
        lock (_gate)
        {
            if (_inputHints.SequenceEqual(hints, StringComparer.Ordinal) && _inputModelFallback == modelFallback
                && _hardwareOutputRouting == hardwareOutputRouting) return;
            bool routingChanged = _hardwareOutputRouting != hardwareOutputRouting;
            _inputHints = [.. hints];
            _inputModelFallback = modelFallback;
            _hardwareOutputRouting = hardwareOutputRouting;
            if (_built)
            {
                // Only the strips on the interface follow it; a strip bound to
                // a source keeps playing through a unit's switch or replug.
                WireInputFeedsLocked([.. _config.Channels.Where(c => c.InputPair is not null && !_bindings.ContainsKey(c.Id)).Select(c => c.Id)]);
                if (routingChanged) WireAuxRouteLocked();
            }
        }
    }

    /// <summary>
    /// Repair the strips' and capture channels' feeds after a disconnect or
    /// an ambiguous source name. Only the strips that need it are wired
    /// again. Returns true when routes or warnings changed.
    /// </summary>
    public bool EnsureInputFeeds()
    {
        lock (_gate)
        {
            if (!_built) return false;
            bool captureChanged = EnsureCaptureFeedsLocked();
            // A dead EQ stage is replaced first, by relinking the head, so
            // the feed it took down does not build the whole strip again.
            captureChanged |= HealEqStagesLocked();
            List<ChannelDefinition> strips = [.. _config.Channels.Where(c => c.InputPair is not null && !UnconfirmedMicrophonePauseLocked(c))];
            var due = new HashSet<string>();
            // A feed whose source node has since vanished (a card profile
            // change renames every node under it) is wired again. With an
            // EQ stage at the head, the source feeds the stage.
            foreach (ChannelDefinition ch in strips)
                if (_inputFeeds.TryGetValue(ch.Id, out PortLink? feed) && _pw.EnsureLinks(feed) == LinkHealth.Broken
                    || _eqInputs.TryGetValue(ch.Id, out PortLink? into) && _pw.EnsureLinks(into) == LinkHealth.Broken) due.Add(ch.Id);
            // A bound strip is wired again when its exact source appears, when
            // a second source acquires the name while the old links remain
            // healthy, or when the unit it is bound to publishes its node
            // under another name. Never choose a microphone by accident.
            List<ChannelDefinition> bound = [.. strips.Where(c => _bindings.ContainsKey(c.Id))];
            if (bound.Count > 0)
            {
                List<AudioNode> sources = [.. _pw.ListDevices().Where(d => d.Kind == AudioNodeKind.Source)];
                foreach (ChannelDefinition ch in bound)
                {
                    SourceBinding binding = _bindings[ch.Id];
                    bool unique = sources.Count(d => d.Name == binding.Source) == 1;
                    // A unit's renamed node: only the card's own sources are
                    // looked at, and only while the exact name is missing.
                    string? target = unique ? binding.Source
                        : binding.Unit is not null ? BoundSourceLocked(binding, sources, out _) : null;
                    bool fed = _inputFeeds.ContainsKey(ch.Id);
                    if ((target is not null) != fed || fed && _stripSources.GetValueOrDefault(ch.Id) != target) due.Add(ch.Id);
                }
            }
            // No strip on the interface has a feed (the device was absent at
            // build or went away): look for it again.
            List<ChannelDefinition> unit = [.. strips.Where(c => !_bindings.ContainsKey(c.Id))];
            if (unit.Count > 0 && !unit.Any(c => _inputFeeds.ContainsKey(c.Id))) due.UnionWith(unit.Select(c => c.Id));
            // A strip on the interface whose route failed while another one
            // came up (a replugged unit's ports can still be settling) is
            // wired again while the unit has its pair. A stereo unit has no
            // XLR 2 or Aux In pair, so those strips are left alone there;
            // the port count comes from the graph, not a helper call.
            else if (_inputDevice is { } node && unit.Any(c => !_inputFeeds.ContainsKey(c.Id))
                && _pw.ListDevices().FirstOrDefault(d => d.Kind == AudioNodeKind.Source && d.Name == node) is { } source)
            {
                foreach (ChannelDefinition ch in unit)
                {
                    int first = 2 * ch.InputPair!.Value;
                    if (!_inputFeeds.ContainsKey(ch.Id)
                        && (source.Channels is int ports ? ports > first : _pw.HasCaptureChannel(node, first)))
                        due.Add(ch.Id);
                }
            }
            if (due.Count == 0) return captureChanged;
            var before = new Dictionary<string, PortLink>(_inputFeeds);
            string? warning = InputWarningLocked();
            WireInputFeedsLocked(due);
            bool feedsChanged = before.Count != _inputFeeds.Count
                || before.Any(e => !_inputFeeds.TryGetValue(e.Key, out PortLink? now) || !ReferenceEquals(now, e.Value));
            return captureChanged || feedsChanged || warning != InputWarningLocked();
        }
    }

    /// <summary>Every sink and source the user can pick, real or virtual.</summary>
    public IReadOnlyList<AudioNode> ListDevices()
        => _pw.ListDevices(_hardwareOutputRouting, _inputHints);

    /// <summary>Close and reopen an output device's stream (see adapter).</summary>
    public void BounceOutput(string sinkName) => _pw.BounceSink(sinkName);

    /// <summary>Current user choices, for persisting.</summary>
    public MixerSettings ExportSettings()
    {
        lock (_gate)
        {
            return new MixerSettings
            {
                InputBindings = _bindings.Count == 0 ? null : new Dictionary<string, SourceBinding>(_bindings),
                ExclusiveGroups = ExclusiveGroupsModel.Copy(_config.ExclusiveGroups),
                UserChannels = [.. _config.Channels.Where(c => c.InputPair is null)
                    .Select(c => new UserChannelDefinition(c.Id, c.Name, c.CaptureSource, c.CapturePair, c.CaptureMonoChannel, c.CaptureUnit))],
                Mixes = [.. _config.Mixes.Where(m => m.IsEditable)
                    .Select(m => new UserMixDefinition(m.Id, m.Name) { Kind = KindName(m.Kind) })],
                // 0.2.0 reads only this list, with Monitor A and B fixed ahead of
                // it, and drops the canonical one on its next save. Without it a
                // downgrade would lose every added mix for good.
                UserMixes = [.. _config.Mixes.Where(m => m.IsEditable && m.Id is not ("monitor" or "monitor2"))
                    .Select(m => new UserMixDefinition(m.Id, m.Name) { Kind = KindName(m.Kind) })],
                MixVolumes = new Dictionary<string, double>(_mixVolume),
                MixMuted = [.. _mixMuted],
                Levels = new Dictionary<string, double>(_levels),
                ChannelVolumes = ExportChannelVolumesLocked(),
                ChannelMuted = [.. _muted],
                MonitorOutputs = [.. _monitorOutputs],
                MonitorMuted = _monitorMuted,
                MonitorMuteMicrophones = _monitorMuteMicrophones,
                // A release still pending for a deselected output is saved too.
                MonitorOutputMutes = new Dictionary<string, bool>(_monitorOutputMutes),
                MonitorFeeds = new Dictionary<string, string>(_monitorFeeds),
                MonitorOutputLabels = new Dictionary<string, string>(_monitorOutputLabels, StringComparer.Ordinal),
                AppOverrides = new Dictionary<string, string>(Matcher.Overrides),
                RouteAppsByDefault = Matcher.RouteByDefault,
                BlockSize = _blockSize,
                PreserveBrowserOutputs = _preserveBrowserOutputs,
                BrowserSiteChannels = _browserSiteChannels.Where(p => HasApplicationChannel(p.Value))
                    .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
                KnownApps = [.. _apps.Values.Select(a => new SavedApp(a.Identity, a.Label, a.ChannelId))],
                EnforcedDefaultSink = _enforcedSink,
                EnforcedDefaultSource = _enforcedSource,
                AuxPortEnabled = _auxPortEnabled,
                LowCutHz = _lowCutHz,
                SoftClipGuard = _softClipGuard,
                CompensateMixLatency = _compensateMixLatency,
                Inserts = CopyInsertsLocked(),
                Eq = new Dictionary<string, MicEq>(_eq, StringComparer.Ordinal),
                EqProcessing = new Dictionary<string, string>(_eqProcessing, StringComparer.Ordinal),
                NoiseRemoval = new Dictionary<string, MicNoise>(_noise, StringComparer.Ordinal),
                VoiceChanger = new Dictionary<string, MicVoice>(_voice, StringComparer.Ordinal),
                Ducking = DuckingSetupLocked().Copy(),
                Appearance = ExportAppearanceLocked(),
            };
        }
    }

    /// <summary>
    /// The insert chains as a snapshot nothing else can change. An insert's
    /// Params dictionary is written in place (a control move, an editor edit)
    /// under this lock, while an export is serialized outside it, so handing
    /// the live dictionary out would let a save enumerate a dictionary that
    /// gains a key mid-write. That throws inside a timer callback, which ends
    /// the daemon.
    /// </summary>
    private Dictionary<string, List<InsertDefinition>> CopyInsertsLocked() => CopyInserts(_inserts);

    /// <inheritdoc cref="CopyInsertsLocked"/>
    internal static Dictionary<string, List<InsertDefinition>> CopyInserts(
        IReadOnlyDictionary<string, List<InsertDefinition>> inserts)
        => inserts.ToDictionary(e => e.Key,
            e => e.Value.Select(i => i with { Params = new Dictionary<string, double>(i.Params) }).ToList());

    /// <summary>
    /// Apply the mutes a saved scene or settings file carries, leaving alone
    /// every entry it does not mention. A channel or a mix added after the
    /// file was written is not in it, and those start muted with their sends
    /// at unity, so clearing the whole set and re-adding would open a send
    /// the file never described: the microphone into a mix a profile predates.
    /// An entry the file describes (it carries a level for it, or lists it as
    /// muted) is set exactly as the file says.
    /// </summary>
    internal static void RecallMutes(HashSet<string> muted, IEnumerable<string> entries,
        IEnumerable<string> described, IEnumerable<string> savedMuted)
    {
        var wanted = new HashSet<string>(savedMuted, StringComparer.Ordinal);
        var known = new HashSet<string>(wanted, StringComparer.Ordinal);
        known.UnionWith(described);
        foreach (string entry in entries)
        {
            if (wanted.Contains(entry)) muted.Add(entry);
            else if (known.Contains(entry)) muted.Remove(entry);
        }
    }

    /// <summary>Apply saved choices onto the built graph.</summary>
    public void ApplySettings(MixerSettings s)
    {
        lock (_gate)
        {
            SetRouteAppsByDefault(s.RouteAppsByDefault);
            SetBlockSize(s.BlockSize);
            RestoreBrowserSettingsLocked(s);
            SetMonitorMuteMicrophones(s.MonitorMuteMicrophones);
            _duckingSetup = DuckingSetup.Prune(s.Ducking ?? DuckingSetup.Default(_config), _config);
            if (!_built) { SyncDuckingLocked(); return; }
            if (_config.RepairedMonitorMix is { } repaired)
            {
                s = s with
                {
                    MixVolumes = s.MixVolumes.Where(p => p.Key != repaired).ToDictionary(),
                    MixMuted = [.. s.MixMuted.Where(id => id != repaired)],
                    Levels = s.Levels.Where(p => !p.Key.EndsWith("|" + repaired, StringComparison.Ordinal)).ToDictionary(),
                    ChannelMuted = [.. s.ChannelMuted.Where(cell => !cell.EndsWith("|" + repaired, StringComparison.Ordinal))],
                };
                _config = _config with { RepairedMonitorMix = null };
            }
            if (s.MonitorMuted) SetMonitorMuted(true);
            SetInputBindings(s.InputBindings);
            RestoreAppearanceLocked(s.Appearance);

            foreach ((string mixId, double vol) in s.MixVolumes)
                if (_mixVolume.ContainsKey(mixId)) _mixVolume[mixId] = Math.Clamp(vol, 0, MixVolumeMaximumLocked(mixId));
            RecallMutes(_mixMuted, _config.Mixes.Select(m => m.Id), s.MixVolumes.Keys, s.MixMuted);

            foreach ((string cell, double lvl) in s.Levels)
                if (_cells.Contains(cell)) _levels[cell] = Math.Clamp(lvl, 0, 1);
            RecallChannelVolumesLocked(s.ChannelVolumes, s.Levels.Keys);
            RecallMutes(_muted, _cells, s.Levels.Keys, s.ChannelMuted);
            NormalizeExclusiveGroupsLocked();

            foreach ((string identity, string channelId) in StreamMatcher.MigrateOverrides(s.AppOverrides))
                Matcher.SetOverride(identity, _config.ResolveApplicationChannel(channelId));

            // Remembered apps come back inactive until a stream appears.
            // Identities saved before the "(deleted)" fix are migrated here so
            // an app does not appear twice after its binary was updated.
            foreach (SavedApp app in s.KnownApps)
            {
                string identity = StreamMatcher.MigrateIdentity(Sanitize(app.Identity));
                if (PipeWireAdapter.IsPlumbingIdentity(identity)) continue;   // pre-filter leftovers
                // A label saved from a helper stream's identifier reads as
                // the application's own name, as a live stream's now does.
                string label = Sanitize(app.Label);
                if (AudioStream.IsTechnicalName(label)) label = new AudioStream(0, label, identity, null).Label;
                if (!_apps.ContainsKey(identity))
                    _apps[identity] = new StreamAssignment(0, 0, label, identity,
                        Matcher.Overrides.TryGetValue(identity, out string? pinned)
                            ? pinned
                            : Matcher.RouteByDefault ? _config.ResolveApplicationChannel(app.ChannelId) : StreamMatcher.Ignore) { Active = false, Running = false };
            }

            static string Sanitize(string v) => v.EndsWith(" (deleted)", StringComparison.Ordinal) ? v[..^10] : v;

            ReapplyMixesLocked(_config.Mixes);

            IReadOnlyList<string> savedOutputs = s.MonitorOutputs is { Count: > 0 }
                ? s.MonitorOutputs
                : s.MonitorOutput is not null ? [s.MonitorOutput] : [];
            _monitorFeeds.Clear();
            foreach ((string output, string mixId) in s.MonitorFeeds) _monitorFeeds[output] = mixId;
            // Selecting the saved outputs below drops the labels of any other.
            _monitorOutputLabels.Clear();
            foreach ((string output, string label) in s.MonitorOutputLabels ?? [])
                if (!string.IsNullOrWhiteSpace(label)) _monitorOutputLabels[output] = label;
            HashSet<string> selectedHardware = savedOutputs.Select(StripMarker).ToHashSet(StringComparer.Ordinal);
            var savedOwners = (s.MonitorOutputMutes ?? []).Where(pair => IsExactOutputName(pair.Key)
                && !pair.Key.StartsWith("OpenXLR_", StringComparison.Ordinal)).ToArray();
            // A saved owner outside this selection, left by a release still
            // pending at the last stop or by an override replacing the saved
            // selection, is released with the old selection below. An owner
            // this mixer already holds keeps its live flag.
            foreach ((string output, bool muted) in savedOwners)
                if (!selectedHardware.Contains(output) && !_monitorOutputMutes.ContainsKey(output))
                    RememberMonitorOutputMuteLocked(output, muted);
            // Release the old selection while its ownership is still known.
            // Legacy settings for the same output keep the current baseline.
            ForgetUnselectedDesktopOutputsLocked(selectedHardware);
            foreach ((string output, bool muted) in savedOwners)
                if (selectedHardware.Contains(output)) RememberMonitorOutputMuteLocked(output, muted);
            SetMonitorOutputsLocked(savedOutputs);
            // Resume at the saved selection, not the output it replaced.
            SetMonitorMuted(s.MonitorMuted);
            _enforcedSink = s.EnforcedDefaultSink;
            _enforcedSource = s.EnforcedDefaultSource;

            // Migration: before the Aux mix existed, "USB Aux Out" was a
            // monitor destination; carry that intent over once.
            _auxPortEnabled = s.AuxPortEnabled
                ?? (s.MonitorOutputs.Any(o => o.EndsWith("#usbaux", StringComparison.Ordinal)) ||
                    (s.MonitorOutput?.EndsWith("#usbaux", StringComparison.Ordinal) ?? false));
            WireAuxRouteLocked();

            bool chainsChanged = false;
            bool rebuildAll = false;
            if (s.LowCutHz is 80 or 120 && _lowCutHz != s.LowCutHz)
            {
                _lowCutHz = s.LowCutHz;
                chainsChanged = true;
            }
            if (s.SoftClipGuard && !_softClipGuard)
            {
                // A stale saved preference must not prevent the entire mixer
                // from starting on a machine where the optional LADSPA bundle
                // is absent. Keep the plain/low-cut route and expose the
                // dependency error in MixerState instead.
                if (_pw.GetSoftwareClipGuardAvailability().Available)
                {
                    _softClipGuard = true;
                    chainsChanged = true;
                }
            }
            if (s.CompensateMixLatency != _compensateMixLatency)
            {
                // The same chains the live switch rebuilds, so a plugin runs
                // in the same host after a restart as before it.
                _compensateMixLatency = s.CompensateMixLatency;
                _pw.MeasurePluginLatency = s.CompensateMixLatency;
                rebuildAll = true;
            }
            // The EQs first, so a strip built below starts with them. A
            // microphone the file does not name keeps its EQ. Where each one
            // runs is machine wiring, read only from this file.
            RecallEqLocked(s.Eq);
            RecallNoiseLocked(s.NoiseRemoval);
            RecallVoiceLocked(s.VoiceChanger);
            _eqProcessing.Clear();
            foreach ((string channel, string processing) in s.EqProcessing)
                if (processing == MicEqProcessing.Software) _eqProcessing[channel] = processing;
            // A chain the file does not name keeps what it has, and only a
            // chain whose structure changed is built again.
            if (chainsChanged || rebuildAll || s.Inserts.Count > 0)
                ApplyChainsLocked(s.Inserts.Count > 0 ? s.Inserts : null, absentMixesEmpty: false, rebuildAll);
            SyncEqStagesLocked();
            UpdateMixLatencyLocked();
            SyncDuckingLocked();
        }
    }

    /// <summary>The current mixer scene, for saving into a profile.</summary>
    public MixerScene ExportScene()
    {
        lock (_gate)
        {
            return new MixerScene
            {
                MixVolumes = new Dictionary<string, double>(_mixVolume),
                MixMuted = [.. _mixMuted],
                Levels = new Dictionary<string, double>(_levels),
                ChannelVolumes = ExportChannelVolumesLocked(),
                ChannelMuted = [.. _muted],
                MonitorOutputs = [.. _monitorOutputs],
                MonitorFeeds = SceneMonitorFeedsLocked(),
                AuxPortEnabled = _auxPortEnabled,
                OutputVolume = _outputVolume,
                LowCutHz = _lowCutHz,
                SoftClipGuard = _softClipGuard,
                Inserts = SceneInsertsLocked(),
                Eq = SceneEqLocked(),
                NoiseRemoval = SceneNoiseLocked(),
                VoiceChanger = SceneVoiceLocked(),
                Ducking = DuckingSetupLocked().ToScene(),
            };
        }
    }

    /// <summary>
    /// The feed of every selected output, written out even where it is only
    /// the default monitor. Which monitor is the default can change between
    /// saving a profile and recalling it (a monitor whose id sorts first is
    /// added after Monitor A was deleted), and recall replaces the whole feed
    /// table, so an output left implicit would follow a mix it never played.
    /// </summary>
    private Dictionary<string, string> SceneMonitorFeedsLocked()
    {
        var feeds = new Dictionary<string, string>(_monitorFeeds);
        foreach (string output in _monitorOutputs)
            if (FeedOfLocked(output) is string feed) feeds[output] = feed;
        return feeds;
    }

    /// <summary>
    /// The chains a profile keeps: every one the mixer holds, and an empty
    /// list for an input that has none. A recall leaves an input the profile
    /// does not name as it is, so a new profile names every input.
    /// </summary>
    private Dictionary<string, List<InsertDefinition>> SceneInsertsLocked()
    {
        Dictionary<string, List<InsertDefinition>> chains = CopyInsertsLocked();
        foreach (ChannelDefinition ch in _config.Channels)
            if (ch.ChainWidth is not null) chains.TryAdd(ch.Id, []);
        return chains;
    }

    /// <summary>
    /// Recall a profile's mixer scene. Unlike <see cref="ApplySettings"/> this
    /// touches only scene state: app routing, the registry, and the enforced
    /// system defaults stay exactly as they are.
    /// </summary>
    public void ApplyScene(MixerScene s)
    {
        lock (_gate)
        {
            SavedMixerValidation.Validate(s);
            if (s.Ducking is { } ducking)
            {
                DuckingSetup previous = DuckingSetupLocked();
                _duckingSetup = DuckingSetup.Prune(DuckingSetup.FromScene(ducking, previous.Processing), _config);
                if (_duckingSetup.Enabled && !previous.Enabled) _duckingFailed.Clear();
            }
            if (!_built) { SyncDuckingLocked(); return; }

            foreach ((string mixId, double vol) in s.MixVolumes)
                if (_mixVolume.ContainsKey(mixId)) _mixVolume[mixId] = Math.Clamp(vol, 0, MixVolumeMaximumLocked(mixId));
            // A profile saved before a channel or a mix existed says nothing
            // about its sends, and those sends sit at unity behind a mute.
            // Recalling it must not open them.
            RecallMutes(_mixMuted, _config.Mixes.Select(m => m.Id), s.MixVolumes.Keys, s.MixMuted);

            foreach ((string cell, double lvl) in s.Levels)
                if (_cells.Contains(cell)) _levels[cell] = Math.Clamp(lvl, 0, 1);
            RecallChannelVolumesLocked(s.ChannelVolumes, s.Levels.Keys);
            RecallMutes(_muted, _cells, s.Levels.Keys, s.ChannelMuted);
            // A profile carries mutes, not groups: one that opens two members
            // of a group closes that group.
            NormalizeExclusiveGroupsLocked();

            ReapplyMixesLocked(_config.Mixes);

            // An empty list is a real scene choice: disconnect every monitor
            // route. Null belongs to an older profile that did not store this
            // field, so preserve the current route for backwards compatibility.
            if (s.MonitorFeeds is not null)
            {
                _monitorFeeds.Clear();
                foreach ((string output, string mixId) in s.MonitorFeeds) _monitorFeeds[output] = mixId;
            }
            if (s.MonitorOutputs is not null)
                SetMonitorOutputsLocked(s.MonitorOutputs);
            else if (s.MonitorFeeds is not null)
                SetMonitorOutputsLocked([.. _monitorOutputs]);   // relink with the recalled feeds
            _auxPortEnabled = s.AuxPortEnabled;
            WireAuxRouteLocked();

            bool chainsChanged = false;
            if (s.LowCutHz is int hz && hz is 0 or 80 or 120 && _lowCutHz != hz)
            {
                _lowCutHz = hz;
                chainsChanged = true;
            }
            if (s.SoftClipGuard is bool scg && _softClipGuard != scg)
            {
                if (!scg || _pw.GetSoftwareClipGuardAvailability().Available)
                {
                    _softClipGuard = scg;
                    chainsChanged = true;
                }
            }
            // The profile replaces the chains it names. An input it does not
            // name keeps its chain; a mix it does not name has no inserts, as
            // profiles always had it. Only a chain whose plugins, order,
            // bypass or host changed is built again; the others take the
            // profile's control values live.
            // A profile without EQs leaves every EQ alone; one with them
            // replaces the EQ of each microphone it names. Values reach a
            // running stage live; an EQ switched on is spliced in.
            if (s.Eq is not null) RecallEqLocked(s.Eq);
            // Noise removal and the voice changer the same way: a profile
            // without one leaves it alone.
            if (s.NoiseRemoval is not null) RecallNoiseLocked(s.NoiseRemoval);
            if (s.VoiceChanger is not null) RecallVoiceLocked(s.VoiceChanger);
            if (chainsChanged || s.Inserts is not null)
                ApplyChainsLocked(s.Inserts, absentMixesEmpty: true, rebuildAll: false);
            if (s.Eq is not null || s.NoiseRemoval is not null || s.VoiceChanger is not null) SyncEqStagesLocked();
            SyncDuckingLocked();
        }
        if (s.OutputVolume is double v) SetOutputVolume(v);
    }

    /// <summary>
    /// Volume of the selected output devices, applied to each. The range is
    /// the devices' own (0 to <see cref="PipeWireAdapter.MaxSinkVolume"/>),
    /// so a level the desktop set above unity can be held and written back
    /// unchanged; the cached value is the one that reached the devices.
    /// </summary>
    public void SetOutputVolume(double volume)
    {
        lock (_gate)
        {
            string[] sinks = [.. _monitorOutputs.Select(StripMarker).Distinct()];
            if (sinks.Length == 0) return;
            volume = Math.Clamp(volume, 0, PipeWireAdapter.MaxSinkVolume);
            // The first output is the one the desktop drives and the one
            // every sweep reads back, so a failed write on it corrects
            // itself; only what the others owe is worth chasing.
            try { _pw.SetSinkVolume(sinks[0], volume); }
            catch (InvalidOperationException) { /* device gone */ }
            foreach (string sink in sinks.Skip(1)) WriteFollowerVolumeLocked(sink, volume);
            _outputVolume = volume;
        }
    }

    /// <summary>The selected outputs the first one's volume is copied to.</summary>
    private IEnumerable<string> FollowerOutputsLocked()
        => _monitorOutputs.Select(StripMarker).Distinct().Skip(1);

    /// <summary>Write one follower's volume, remembering it when the device refuses.</summary>
    private void WriteFollowerVolumeLocked(string sink, double volume)
    {
        try
        {
            _pw.SetSinkVolume(sink, volume);
            _outputVolumeDue.Delivered(sink);
        }
        catch (InvalidOperationException) { _outputVolumeDue.Failed(sink, volume); }
    }

    internal static string StripMarker(string name)
    {
        int marker = name.IndexOf('#');
        return marker >= 0 ? name[..marker] : name;
    }

    /// <summary>Enforced system default devices (sink, source); null = off.</summary>
    public (string? Sink, string? Source) EnforcedDefaults
    {
        get { lock (_gate) return (_enforcedSink, _enforcedSource); }
    }

    /// <summary>
    /// Choose the devices to hold as system defaults. Applied immediately and
    /// then re-asserted on every sweep.
    /// </summary>
    public void SetEnforcedDefaults(string? sink, string? source)
    {
        lock (_gate)
        {
            _enforcedSink = string.IsNullOrEmpty(sink) ? null : sink;
            _enforcedSource = string.IsNullOrEmpty(source) ? null : source;
        }
        EnforceDefaults();
    }

    internal static string? ResolveDefaultSink(string? setting, IReadOnlyList<string> outputs)
        => setting == FollowMonitorOutput
            ? outputs.FirstOrDefault() is string first ? StripMarker(first) : null
            : setting;

    /// <summary>
    /// Re-assert the enforced defaults. WirePlumber auto-switches defaults to
    /// new devices and replays remembered preferences, so a one-time set is not
    /// enough: this runs every sweep, exactly like Wave Link holds its devices.
    /// </summary>
    public bool EnforceDefaults(CancellationToken stop = default)
    {
        string? sink, source;
        lock (_gate)
        {
            if (!_built) return false;
            sink = ResolveDefaultSink(_enforcedSink, _monitorOutputs);
            source = _enforcedSource;
        }
        // Reading the current defaults costs a helper call each, every
        // second, so it runs without the mixer lock.
        stop.ThrowIfCancellationRequested();
        bool fixSink = sink is not null && _pw.GetDefaultSink() != sink;
        stop.ThrowIfCancellationRequested();
        bool fixSource = source is not null && _pw.GetDefaultSource() != source;
        if (!fixSink && !fixSource) return false;
        // A correction is written under the lock, after checking again that
        // the graph is built and still wants that device. Teardown therefore
        // either waits for the write or has already happened and the write is
        // skipped: no default-device write lands after the graph is gone,
        // including from calls made outside the daemon's regular sweep.
        lock (_gate)
        {
            if (!_built) return false;
            bool corrected = false;
            try
            {
                if (fixSink && ResolveDefaultSink(_enforcedSink, _monitorOutputs) == sink)
                {
                    stop.ThrowIfCancellationRequested();
                    _pw.SetDefaultSink(sink!);
                    corrected = true;
                }
                if (fixSource && _enforcedSource == source)
                {
                    stop.ThrowIfCancellationRequested();
                    _pw.SetDefaultSource(source!);
                    corrected = true;
                }
            }
            catch (InvalidOperationException) { /* device currently absent */ }
            return corrected;
        }
    }

    /// <summary>
    /// Refresh the cached device volumes; true when either moved (externally or
    /// through us), so the daemon knows to push fresh state.
    /// </summary>
    public bool SyncDeviceVolumes()
    {
        lock (_gate)
        {
            _readingDesktopMutes = true;
            bool changed;
            try
            {
                string? first = _monitorOutputs.FirstOrDefault();
                double? outV = first is null ? null : _pw.GetSinkVolume(first);
                changed = Differs(outV, _outputVolume);
                // In follow mode the desktop changes the first output directly.
                // Carry that change to the other selected outputs just as the
                // MONITOR slider does. A new output selection establishes a fresh
                // baseline rather than overwriting its remembered device volume.
                if (_enforcedSink == FollowMonitorOutput && changed && _outputVolume is not null && outV is double volume)
                    foreach (string sink in FollowerOutputsLocked()) WriteFollowerVolumeLocked(sink, volume);
                // Nothing moved, so a device that refused an earlier copy is the
                // only thing left out of step; it gets that value again.
                else
                    foreach ((string sink, double owed) in _outputVolumeDue.Due())
                        WriteFollowerVolumeLocked(sink, owed);
                _outputVolume = outV;
                changed |= ReadOutputMonitorMuteLocked();
            }
            finally
            {
                _readingDesktopMutes = false;
                _desktopMuteConsumed = false;
                SyncMonitorHardwareMutesLocked();
            }
            // Hotplug and native readback can change the restart baseline
            // without moving a volume or the software pause.
            changed |= _monitorOutputMutesChanged;
            _monitorOutputMutesChanged = false;
            return changed;
        }

        static bool Differs(double? a, double? b)
            => a.HasValue != b.HasValue || (a.HasValue && Math.Abs(a.Value - b!.Value) > 0.005);
    }

    /// <summary>
    /// True when a selectable source/output changed, or an output's desktop
    /// volume or mute moved. Pickers and keys need a state push for these;
    /// they are not mixer settings, so nothing is saved for them.
    /// </summary>
    public bool SyncDeviceList()
    {
        lock (_gate)
        {
            if (!_built) return false;
            List<AudioNode> devices = [.. _pw.ListDevices()];
            string levels = string.Join('\n', devices
                .Select(d => $"{d.Kind}\t{d.Name}\t{d.Description}\t{d.IsPhysical}\t{d.Volume}\t{d.Muted}"));
            if (levels == _deviceList) return false;
            _deviceList = levels;
            _outputNames = devices.Where(d => d.Kind == AudioNodeKind.Sink && !string.IsNullOrWhiteSpace(d.Description))
                .GroupBy(d => d.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Description, StringComparer.Ordinal);
            return true;
        }
    }
    private string? _deviceList;

    /// <summary>
    /// Remember the description of every selected output the device list
    /// shows, the Wave XLR Pro's own outputs included, so a client can name
    /// an output that is away by what it was called. Reads the list again
    /// only after the selection or the device list changed. True when a
    /// label changed, which is a mixer setting to save.
    /// </summary>
    public bool SyncMonitorOutputLabels()
    {
        lock (_gate)
        {
            if (!_built || !_monitorOutputLabelsDue && ReferenceEquals(_deviceList, _labelledDeviceList)) return false;
            _monitorOutputLabelsDue = false;
            _labelledDeviceList = _deviceList;
            if (_monitorOutputs.Count == 0) return false;
            bool changed = false;
            foreach (AudioNode node in _pw.ListDevices(_hardwareOutputRouting, _inputHints))
            {
                // A node without a description lists its name, which says nothing new.
                if (node.Kind != AudioNodeKind.Sink || node.IsOwn || !_monitorOutputs.Contains(node.Name)
                    || string.IsNullOrWhiteSpace(node.Description) || node.Description == node.Name
                    || _monitorOutputLabels.GetValueOrDefault(node.Name) == node.Description) continue;
                _monitorOutputLabels[node.Name] = node.Description;
                changed = true;
            }
            return changed;
        }
    }

    // Output descriptions from the last device sync, so notes name an output the way the pickers do.
    private Dictionary<string, string> _outputNames = new(StringComparer.Ordinal);

    /// <summary>Repair internal sinks and read monitor masters from one dump under the mixer lock.</summary>
    public bool SyncOwnSinkLevels(out IReadOnlyList<string> restored)
    {
        lock (_gate)
        {
            restored = [];
            if (!_built) return false;
            _readingDesktopMutes = true;
            try
            {
                (IReadOnlyList<OwnSinkLevel> levels, IReadOnlyList<OwnStageStream> stages) = _pw.OwnLevels();
                bool changed = ReadDesktopMonitorMuteLocked(ref levels);
                bool pending = _microphonePausePending;
                restored = EnsureOwnSinkLevelsLocked(levels, stages);
                return SyncMonitorVolumesLocked(levels) || changed || pending != _microphonePausePending;
            }
            finally
            {
                _readingDesktopMutes = false;
                SyncMonitorHardwareMutesLocked();
            }
        }
    }

    /// <summary>
    /// Hold internal channel, DSP and non-monitor mix sinks, and the DSP
    /// stage streams, at unity and unmuted. Monitor sinks carry their own
    /// masters and belong to the desktop controls too. Returns the internal
    /// sinks and streams put back.
    /// </summary>
    public IReadOnlyList<string> EnsureOwnSinkLevels()
    {
        lock (_gate)
        {
            if (!_built) return [];
            (IReadOnlyList<OwnSinkLevel> levels, IReadOnlyList<OwnStageStream> stages) = _pw.OwnLevels();
            return EnsureOwnSinkLevelsLocked(levels, stages);
        }
    }

    private IReadOnlyList<string> EnsureOwnSinkLevelsLocked(IReadOnlyList<OwnSinkLevel> levels, IReadOnlyList<OwnStageStream> stages)
    {
        var restored = new List<string>();
        var microphones = _config.Channels.Where(c => c.Microphone).Select(c => c.SinkName).ToHashSet(StringComparer.Ordinal);
        foreach (string stale in _microphonePauseRepairs.Keys.Where(n => !microphones.Contains(n)).ToArray())
            _microphonePauseRepairs.Remove(stale);
        bool microphoneHelpersAvailable = true;
        foreach (OwnSinkLevel sink in levels)
        {
            if (MonitorMixForSinkLocked(sink.Name) is not null) continue;
            bool microphone = microphones.Remove(sink.Name);
            bool managed = microphone && (_monitorMuteMicrophones || _microphonePausePending);
            bool muted = microphone && MicrophonesPausedLocked;
            bool off = Math.Abs(sink.Volume - 1.0) > 0.01;
            if (managed && (sink.Muted != muted || _microphonePauseRepairs.ContainsKey(sink.Name)))
            {
                _microphonePauseRepairs.TryAdd(sink.Name, default);
                if (!microphoneHelpersAvailable || Clock() < _microphonePauseRepairs[sink.Name].RetryAt) continue;
                microphoneHelpersAvailable = ApplyMicrophonePauseLocked(sink.Name);
                if (!microphoneHelpersAvailable) continue;
                restored.Add(sink.Name);
            }
            if (!off && (managed || sink.Muted == muted)) continue;
            try
            {
                if (off) _pw.SetSinkVolume(sink.Name, 1.0);
                if (!managed && sink.Muted != muted) _pw.SetSinkMuted(sink.Name, muted);
                restored.Add(sink.Name);
            }
            catch (InvalidOperationException) { /* the sink went away; the next sweep sees the rest */ }
        }
        foreach (OwnStageStream stage in stages)
        {
            if (Math.Abs(stage.Volume - 1.0) <= 0.01 && !stage.Muted) continue;
            try
            {
                _pw.HoldStageStream(stage);
                restored.Add(stage.Name);
            }
            catch (InvalidOperationException) { /* the stage was rebuilt; the next sweep reads the new one */ }
        }
        return restored;
    }

    /// <summary>Read desktop monitor masters without touching the other mixes or channel sends.</summary>
    public bool SyncMonitorVolumes()
    {
        lock (_gate)
            return _built ? SyncMonitorVolumesLocked(_pw.OwnSinkLevels()) : false;
    }

    private bool SyncMonitorVolumesLocked(IReadOnlyList<OwnSinkLevel> levels)
    {
        bool changed = false, canRestore = true;
        foreach (OwnSinkLevel sink in levels)
        {
            MixDefinition? mix = MonitorMixForSinkLocked(sink.Name);
            if (mix is null) continue;
            double volume = Math.Min(sink.DesktopVolume, PipeWireAdapter.MaxSinkVolume);
            if (sink.DesktopVolume > PipeWireAdapter.MaxSinkVolume)
                _pw.SetSinkVolume(sink.Name, volume);
            if (Math.Round(volume * 100) != Math.Round(_mixVolume.GetValueOrDefault(mix.Id, 1) * 100))
            {
                _mixVolume[mix.Id] = volume;
                changed = true;
            }
            if (_desktopMixMuteResets.ContainsKey(sink.Name))
            {
                if (canRestore) canRestore = RestoreDesktopMixMuteLocked(sink.Name, mix.Id);
            }
            else changed |= sink.Muted ? _mixMuted.Add(mix.Id) : _mixMuted.Remove(mix.Id);
        }
        if (changed)
        {
            SyncMonitorHardwareMutesLocked();
            PushDuckKeyOpenLocked();
        }
        return changed;
    }

    /// <summary>First selected monitor output, or null (legacy single view).</summary>
    public string? MonitorOutput { get { lock (_gate) return _monitorOutputs.FirstOrDefault(); } }

    /// <summary>All selected monitor outputs, in selection order.</summary>
    public IReadOnlyList<string> MonitorOutputs { get { lock (_gate) return [.. _monitorOutputs]; } }

    /// <summary>Pause listening and, when opted in, microphone sends without replacing individual mutes.</summary>
    public bool MonitorMuted { get { lock (_gate) return _monitorMuted; } }

    public void SetMonitorMuted(bool muted)
    {
        lock (_gate)
        {
            _desktopMonitorIntent = null;
            if (_monitorMuted == muted) return;
            if (!muted && _built && !RemovePausedMonitorRoutesLocked())
                throw new InvalidOperationException("Monitor routes could not be disconnected; listening stays paused until they can be removed.");
            _monitorMuted = muted;
            if (!_built) return;
            if (muted)
            {
                try
                {
                    RemovePausedMonitorRoutesLocked();
                    SyncMonitorHardwareMutesLocked(explicitChange: true);
                }
                finally
                {
                    SyncMicrophonePauseLocked();
                    // Nothing would hear an audition while listening is paused.
                    SyncAuditionLinksLocked();
                }
                return;
            }
            try
            {
                try { SetMonitorOutputsLocked([.. _monitorOutputs]); }
                catch
                {
                    _monitorMuted = true;
                    RemovePausedMonitorRoutesLocked();
                    SyncMonitorHardwareMutesLocked(explicitChange: true);
                    throw;
                }
                SyncMonitorHardwareMutesLocked(explicitChange: true);
            }
            finally
            {
                SyncMicrophonePauseLocked();
                SyncAuditionLinksLocked();
            }
        }
    }

    private bool RemovePausedMonitorRoutesLocked()
    {
        foreach ((string key, PortLink route) in _monitorRoutes.ToArray())
            if (_pw.EnsureUnlinked(route)) _monitorRoutes.Remove(key);
        _incompleteMonitorRoutes.Clear();
        return _monitorRoutes.Count == 0;
    }

    private void DisconnectMonitorRoutesLocked()
    {
        if (_monitorMuted) RemovePausedMonitorRoutesLocked();
        else
        {
            foreach (PortLink route in _monitorRoutes.Values) _pw.Unlink(route);
            _monitorRoutes.Clear();
        }
    }

    public bool IsChannelMutedIn(string channelId, string mixId)
    {
        lock (_gate) return _muted.Contains(Cell(channelId, mixId));
    }

    /// <summary>
    /// The channel the Pro's hardware mic path follows: the one that carries
    /// the unit's input 1 (see <see cref="UnitInputChannel"/>), or null when
    /// no channel does and the capsule is not in the mixer.
    /// </summary>
    public string? HardwareMicChannel { get { lock (_gate) return DirectPathChannelLocked(1); } }

    private string? DirectPathChannelLocked(int input) => CarrierOfLocked(input);

    private bool IsHardwareMicLocked(string channel) => Array.IndexOf(_hardwareMicChannels, channel) >= 0;

    /// <summary>
    /// The Pro's hardware mic path for unit <paramref name="input"/> 1 (XLR 1)
    /// or 2 (XLR 2) is carrying it to the headphone jacks (or not); the
    /// software monitor send of the channel that carries
    /// that input is muted in the graph while it does, and restored when it
    /// stops. With <paramref name="whenHeardIn"/>, the path is used only while
    /// that channel's send is unmuted in one of the feed's mixes, read under
    /// the same lock. Returns whether the path is used: never while no
    /// channel carries the input, never while that channel is in an
    /// exclusive group, because the group mutes its members through their
    /// sends, and the device's direct path would keep the microphone in the
    /// jacks after the group closed it. Never while it is under Sound Check
    /// either: the direct path carries the live microphone, and the jacks
    /// must hear the loop the send carries. Never while software changes the
    /// channel's audio (see <see cref="ProcessedInSoftwareLocked"/>): the
    /// direct path would play the voice unchanged, so the send carries the
    /// processed one instead, a few milliseconds later. Input 2 never takes
    /// the channel that already carries input 1.
    /// </summary>
    public bool SetHardwareMicMonitor(bool on, string? whenHeardIn = null, int input = 1)
    {
        if (input is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(input));
        lock (_gate)
        {
            string? channel = DirectPathChannelLocked(input);
            on &= !_monitorMuted && channel is not null && GroupForChannelLocked(channel) is null && _soundCheckChannel != channel
                && !ProcessedInSoftwareLocked(channel)
                && (input == 1 || channel != DirectPathChannelLocked(1))
                && (whenHeardIn is null || MonitorFeed.Parts(whenHeardIn).Any(m => !_mixMuted.Contains(m) && !_muted.Contains(Cell(channel, m))));
            SetHardwareMicChannelLocked(input, on ? channel : null);
            return on;
        }
    }

    /// <summary>
    /// Whether software changes the channel's audio before its sends: a
    /// chain of its own (the soft low cut, ClipGuard or an insert that runs)
    /// or the microphone stage (the EQ in software, noise removal or the
    /// voice changer). An EQ the unit runs does not count, nor does a
    /// bypassed insert.
    /// </summary>
    private bool ProcessedInSoftwareLocked(string channel)
        => _config.Channels.FirstOrDefault(c => c.Id == channel) is { } ch
            && InputRouteLocked(ch, null) is { } route && (route.Chained || route.Eq);

    /// <summary>
    /// The level, in dB, the jacks' feed would play the hardware mic path's
    /// channel at through its software send: the send level and the channel
    /// volume, then the feeding mix's volume, all on the desktop's cubic
    /// scale. For a summed feed the loudest unmuted part counts. Negative
    /// infinity while no part carries it; the device holds that to its floor.
    /// This is input 1's; <see cref="HardwareMicLevelDbOf"/> takes either.
    /// </summary>
    public double HardwareMicLevelDb => HardwareMicLevelDbOf(1);

    /// <inheritdoc cref="HardwareMicLevelDb"/>
    public double HardwareMicLevelDbOf(int input)
    {
        lock (_gate)
        {
            if (_hardwareMicChannels[input - 1] is not { } channel || JackFeedLocked() is not { } feed) return double.NegativeInfinity;
            double gain = MonitorFeed.Parts(feed)
                .Where(m => !_mixMuted.Contains(m) && !_muted.Contains(Cell(channel, m)))
                .Select(m => SendGain(_levels.GetValueOrDefault(Cell(channel, m), 0.0), ChannelVolumeLocked(channel), 1)
                    * Math.Clamp(_mixVolume.GetValueOrDefault(m, 1.0), 0, MixVolumeMaximumLocked(m)))
                .DefaultIfEmpty(0).Max();
            return gain > 0 ? 60 * Math.Log10(gain) : double.NegativeInfinity;
        }
    }

    private void SetHardwareMicChannelLocked(int input, string? channel)
    {
        string? previous = _hardwareMicChannels[input - 1];
        if (previous == channel) return;
        _hardwareMicChannels[input - 1] = channel;
        RefreshHardwareMicCellsLocked(previous);
        RefreshHardwareMicCellsLocked(channel);
    }

    /// <summary>Turn off each path whose input another channel, or none, now carries.</summary>
    private void DropMovedHardwareMicsLocked()
    {
        for (int input = 1; input <= _hardwareMicChannels.Length; input++)
            if (_hardwareMicChannels[input - 1] is { } channel && channel != DirectPathChannelLocked(input))
                SetHardwareMicChannelLocked(input, null);
    }

    /// <summary>Put both paths back as a refused change found them.</summary>
    private void RestoreHardwareMicsLocked(string?[] previous)
    {
        for (int input = 1; input <= previous.Length; input++) SetHardwareMicChannelLocked(input, previous[input - 1]);
    }

    private void RefreshHardwareMicCellsLocked(string? channel)
    {
        if (!_built || channel is null) return;
        foreach (MixDefinition mix in _config.Mixes.Where(m => m.Kind == MixKind.Monitor))
            if (_cells.Contains(Cell(channel, mix.Id))) ApplyCellLocked(channel, mix.Id);
    }

    /// <summary>
    /// Restart the Pro sink's playback stream after a headphone-mix change,
    /// which is when the device latches its matrix (same as the aux return).
    /// The monitor routes are dropped for the bounce and rebuilt after it.
    /// With no jack a monitor output (a direct path another program left on
    /// being switched off), the stream restarted is the Aux route's when it
    /// plays there, and otherwise the sink's own, which no route of ours holds.
    /// </summary>
    public void BounceMonitorHardwareOutput()
    {
        string? sink;
        bool rewire = false, aux = false;
        lock (_gate)
        {
            if (!_built) return;
            string? pseudo = _monitorOutputs.FirstOrDefault(o => o.Contains('#'));
            if (pseudo is not null)
            {
                sink = pseudo[..pseudo.IndexOf('#')];
                DisconnectMonitorRoutesLocked();
                rewire = true;
            }
            else if (_auxTargetSink is not null) { sink = null; aux = true; }
            else sink = HardwareOutputSinkLocked();
        }
        if (aux) { BounceAuxTarget(); return; }
        if (sink is null) return;
        _pw.BounceSink(sink);
        if (!rewire) return;
        lock (_gate)
        {
            if (_built) SetMonitorOutputsLocked([.. _monitorOutputs]);
        }
    }

    /// <summary>
    /// Send the monitor mix to one output device (or none). Kept for clients
    /// that think in a single monitor destination.
    /// </summary>
    public void SetMonitorOutput(string? sinkName)
        => SetMonitorOutputs(sinkName is null ? [] : [sinkName]);

    /// <summary>
    /// Send the monitor mix to any set of output devices at once. Any sink
    /// works, virtual ones included; an empty list disconnects the monitor.
    /// </summary>
    public void SetMonitorOutputs(IReadOnlyList<string> sinkNames)
    {
        lock (_gate)
        {
            if (!_built) return;
            SetMonitorOutputsLocked(sinkNames);
        }
    }

    private void SetMonitorOutputsLocked(IReadOnlyList<string> sinkNames)
    {
        PrepareMonitorHardwareSelectionLocked(sinkNames);
        string? previousSink = ResolveDefaultSink(FollowMonitorOutput, _monitorOutputs);
        DisconnectMonitorRoutesLocked();
        _monitorOutputs.Clear();
        foreach (string name in sinkNames.Where(n => !string.IsNullOrEmpty(n)).Distinct())
        {
            // The aux port is owned by the Aux mix now; old saved selections
            // that still carry it as a monitor destination are dropped.
            if (name.EndsWith("#usbaux", StringComparison.Ordinal)) continue;
            _monitorOutputs.Add(name);
        }
        // A different device needs its own volume baseline, and with it no
        // output is owed the volume of the selection that is gone. Relinking
        // the same device must keep pending desktop volume changes detectable,
        // and only drops what is owed to outputs no longer selected.
        if (previousSink != ResolveDefaultSink(FollowMonitorOutput, _monitorOutputs))
        {
            _outputVolume = null;
            _outputVolumeDue.Clear();
        }
        else _outputVolumeDue.Keep(FollowerOutputsLocked());
        // Feeds only make sense for selected outputs; drop the rest so a
        // stale choice never resurfaces when the output is ticked again.
        foreach (string stale in _monitorFeeds.Keys.Where(o => !_monitorOutputs.Contains(o)).ToList())
            _monitorFeeds.Remove(stale);
        // A label is kept only while its output is selected.
        foreach (string stale in _monitorOutputLabels.Keys.Where(o => !_monitorOutputs.Contains(o)).ToList())
            _monitorOutputLabels.Remove(stale);
        _monitorOutputLabelsDue = true;
        // Several physical jacks on the same return pair cannot carry distinct
        // feeds. Keep every published member consistent with the audible one.
        foreach (var group in _monitorOutputs.Where(output => output.Contains('#')).GroupBy(OutputRouteKey))
        {
            string? feed = FeedOfLocked(group.First());
            if (feed is null) continue;
            foreach (string output in group) _monitorFeeds[output] = feed;
        }
        _incompleteMonitorRoutes.Clear();
        foreach (string? channel in _hardwareMicChannels) RefreshHardwareMicCellsLocked(channel);
        if (!_monitorMuted)
            foreach ((string key, string target) in MonitorRouteTargetsLocked())
            {
                PortLink? route = RouteFeedLocked(target);
                _monitorRoutes[key] = route ?? new PortLink([]);
            }
        CaptureMonitorMuteBaselinesLocked();
        SyncMonitorHardwareMutesLocked();
        // The soundboard's audition follows the mixes the outputs now play.
        SyncAuditionLinksLocked();
    }

    /// <summary>
    /// How long a route into the driven interface's own sink waits for the
    /// interface's capture to be linked first. A capture that does not come
    /// within this time (its source is missing, or its chain cannot start)
    /// no longer keeps the output silent.
    /// </summary>
    internal static readonly TimeSpan CaptureFirstGrace = TimeSpan.FromSeconds(5);
    /// <summary>Milliseconds for <see cref="CaptureFirstGrace"/>; tests move it on.</summary>
    internal Func<long> Clock { get; init; } = () => Environment.TickCount64;
    // When a route into each card first waited, and the output the late
    // capture was last noted for, by the card part of the node name.
    private readonly Dictionary<string, long> _playbackHeldSince = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _playbackHoldNoted = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether a route into <paramref name="target"/> waits for now: the
    /// target is a sink of an Elgato unit whose capture a strip takes, and
    /// none of the strips that take that unit has its capture linked and
    /// started yet (<see cref="CaptureRun.Started"/>: the kernel's capture
    /// stream running, where the card says). That is the driven interface's
    /// own sink while strips are on it, and the sink of another unit a strip
    /// is bound to. Elgato's full-speed units record silence for as long as
    /// capture stays open when their playback started first (the XLR Dock,
    /// the original Wave XLR and the Wave:3 on some ports), and a Wave:3 MK.2
    /// left its capture stopped in the kernel while PipeWire showed it
    /// running, so the mixer never starts playback first. The strips' feeds
    /// are wired before the monitor routes in every sweep, so the route
    /// follows as soon as the capture runs, or after
    /// <see cref="CaptureFirstGrace"/> without it. While the unit recovers
    /// from a USB reset (<see cref="BeginCaptureRecovery"/>) the route waits
    /// for the capture of the re-enumerated card instead, up to
    /// <see cref="CaptureRecoveryLimit"/>.
    /// </summary>
    private bool HoldPlaybackLocked(string target)
    {
        string bare = target.IndexOf('#') is int marker and >= 0 ? target[..marker] : target;
        if (!bare.StartsWith("alsa_output.", StringComparison.Ordinal)) return false;
        string card = InterfaceNodes.CardKey(bare);
        List<string> takers = [];
        if (IsDrivenUnitSinkLocked(target))
            takers.AddRange(_config.Channels.Where(c => c.InputPair is not null && !_bindings.ContainsKey(c.Id)).Select(c => c.Id));
        // A strip bound to a unit's node takes that unit's capture, under the
        // node's present name or, until it is found, the bound one.
        foreach ((string strip, SourceBinding binding) in _bindings)
            if (binding.Unit is not null && InterfaceNodes.CardKey(_stripSources.GetValueOrDefault(strip) ?? binding.Source) == card
                && _config.Channels.Any(c => c.Id == strip && c.InputPair is not null))
                takers.Add(strip);
        if (takers.Count == 0)
        {
            ReleasePlaybackHoldLocked(card);
            return false;
        }
        List<CaptureRun> captures = [.. takers.Where(_inputFeeds.ContainsKey).Select(CaptureSourceLocked).OfType<string>()
            .Distinct(StringComparer.Ordinal).Select(ProbeCaptureLocked)];
        if (_captureRecovery is { } recovery && recovery.Card == card)
        {
            // The card the reset brought back publishes its source under a new id.
            if (captures.Any(run => run.Started && run.NodeId != recovery.Node))
            {
                _captureRecovery = null;
                ReleasePlaybackHoldLocked(card);
                InputNote?.Invoke($"the interface's capture runs again after its USB reset; routing into {target}");
                return false;
            }
            if (Clock() - recovery.Since < (long)CaptureRecoveryLimit.TotalMilliseconds) return true;
            _captureRecovery = null;
            ReleasePlaybackHoldLocked(card);
            InputNote?.Invoke($"routing into {target} although the interface's capture did not run again within {CaptureRecoveryLimit.TotalSeconds:0} seconds of its USB reset");
            return false;
        }
        if (captures.Any(run => run.Started))
        {
            ReleasePlaybackHoldLocked(card);
            return false;
        }
        // The wait counts only while the sink is there: a unit that is
        // unplugged has nothing to start, and when it comes back its capture
        // gets the whole grace again.
        if (!_pw.ListDevices(true, _inputHints).Any(d => d.Kind == AudioNodeKind.Sink
                && (d.Name == bare || d.Name.StartsWith(bare + "#", StringComparison.Ordinal))))
        {
            _playbackHeldSince.Remove(card);
            return false;
        }
        long now = Clock();
        if (!_playbackHeldSince.TryGetValue(card, out long since)) _playbackHeldSince[card] = since = now;
        if (now - since < (long)CaptureFirstGrace.TotalMilliseconds) return true;
        if (_playbackHoldNoted.GetValueOrDefault(card) != target)
        {
            _playbackHoldNoted[card] = target;
            InputNote?.Invoke(captures.Count > 0
                ? $"routing into {target} before the interface's capture runs; it was linked but did not start within {CaptureFirstGrace.TotalSeconds:0} seconds"
                : $"routing into {target} before the interface's capture is linked; it did not come within {CaptureFirstGrace.TotalSeconds:0} seconds");
        }
        return false;
    }

    private void ReleasePlaybackHoldLocked(string card)
    {
        _playbackHeldSince.Remove(card);
        _playbackHoldNoted.Remove(card);
    }

    /// <summary>
    /// Whether an output is a sink of the interface the strips take: the
    /// card of the capture source they are fed from, or before that source
    /// is found, the card the driven unit's most specific fragment names.
    /// </summary>
    private bool IsDrivenUnitSinkLocked(string target)
    {
        int marker = target.IndexOf('#');
        string bare = marker < 0 ? target : target[..marker];
        if (!bare.StartsWith("alsa_output.", StringComparison.Ordinal)) return false;
        string card = InterfaceNodes.CardKey(bare);
        if (_inputDevice is { } source) return InterfaceNodes.CardKey(source) == card;
        return _inputHints.Count > 0 && card.Contains(_inputHints[0], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Outputs whose feed came up short on links; the sweep relinks them.</summary>
    private readonly HashSet<string> _incompleteMonitorRoutes = [];

    /// <summary>
    /// One identity for the outputs that share a hardware return bus: the
    /// Pro's jacks are "device#hp1", "device#hp2" and "device#lineout" and
    /// carry one feed, so they are routed and grouped as "device#bus".
    /// </summary>
    internal static string OutputRouteKey(string output)
    {
        int marker = output.IndexOf('#');
        return marker < 0 ? output : output[..marker] + "#bus";
    }

    /// <summary>
    /// Link every mix of an output's feed into that output. PipeWire sums
    /// what arrives on one input port, so "A+B" is simply both taps linked
    /// to the same playback ports, at unity: a blend at other levels is a
    /// mix of its own. Null without a usable mix at all.
    /// </summary>
    private PortLink? RouteFeedLocked(string target)
    {
        if (_monitorMuted) return null;
        List<MixDefinition> mixes = MixesForOutputLocked(target);
        string key = OutputRouteKey(target);
        _incompleteMonitorRoutes.Remove(key);
        if (mixes.Count == 0) return null;
        // An empty route is tried again by the next sweep (EnsureMonitorRoutes).
        if (HoldPlaybackLocked(target)) return new PortLink([]);
        var pairs = new List<(string From, string To)>();
        try
        {
            foreach (MixDefinition mix in mixes)
            {
                (string tap, string prefix) = MixTapLocked(mix);
                PortLink route = _pw.RouteTapToOutput(tap, prefix, target);
                if (route.Pairs.Count == 0 || !route.Complete) _incompleteMonitorRoutes.Add(key);
                pairs.AddRange(route.Pairs);
            }
        }
        catch { _pw.Unlink(new PortLink(pairs)); throw; }
        return new PortLink(pairs);
    }

    /// <summary>The stable default monitor, unaffected by display ordering.</summary>
    private MixDefinition? PrimaryMonitorLocked() => _config.DefaultMonitor;

    /// <summary>The mixes feeding an output, falling back to the stable default monitor.</summary>
    private List<MixDefinition> MixesForOutputLocked(string output)
    {
        if (_monitorFeeds.TryGetValue(output, out string? feed))
        {
            IReadOnlyList<string> parts = MonitorFeed.Parts(feed);
            List<MixDefinition> chosen = [.. _config.Mixes.Where(m => parts.Contains(m.Id))];
            if (chosen.Count > 0) return chosen;
        }
        return PrimaryMonitorLocked() is MixDefinition primary ? [primary] : [];
    }

    /// <summary>An output's feed as a string (see <see cref="MonitorFeed"/>), or null without a monitor mix.</summary>
    private string? FeedOfLocked(string output)
    {
        List<MixDefinition> mixes = MixesForOutputLocked(output);
        return MonitorFeed.Join(mixes.Select(m => m.Id));
    }

    /// <summary>
    /// Mix ids that reach the Wave XLR Pro's own jacks (its
    /// pseudo-outputs share one return bus, so they follow one feed), or the
    /// default monitor mix when no jack is selected.
    /// </summary>
    private string? JackFeedLocked()
    {
        string? jack = _monitorOutputs.FirstOrDefault(o => o.Contains('#'));
        return jack is null ? PrimaryMonitorLocked()?.Id : FeedOfLocked(jack);
    }

    /// <summary>The feed of the interface's own jacks (see <see cref="JackFeedLocked"/> and <see cref="MonitorFeed"/>).</summary>
    public string? JackMonitorMix { get { lock (_gate) return JackFeedLocked(); } }

    /// <summary>The feed of one selected output (see <see cref="MonitorFeed"/>).</summary>
    public string? MonitorFeedOf(string output) { lock (_gate) return FeedOfLocked(output); }

    /// <summary>
    /// Choose which mix feeds a selected output. The Pro's
    /// pseudo-outputs ride one return bus, so a choice on one applies to
    /// every jack of that device. An unknown mix or an unselected output is
    /// ignored; the first monitor mix is stored as "no exception".
    /// Choose what feeds one selected output: a mix, or several
    /// summed ("monitor+monitor2", see <see cref="MonitorFeed"/>). Null on
    /// success, else why nothing changed.
    /// </summary>
    public string? SetMonitorFeed(string output, string feed)
    {
        lock (_gate)
        {
            if (!_built) return "mixer not built";
            List<string> affected = [.. MonitorOutputsForLocked(output)];
            if (affected.Count == 0) return $"'{output}' is not a selected monitor output";
            if (NormalizeFeedLocked(feed) is not string normalized) return $"'{feed}' is not a mix or a sum of distinct mixes";
            bool primary = PrimaryMonitorLocked()?.Id == normalized;
            foreach (string o in affected)
            {
                if (primary) _monitorFeeds.Remove(o);
                else _monitorFeeds[o] = normalized;
            }
            SetMonitorOutputsLocked([.. _monitorOutputs]);
            return null;
        }
    }

    /// <summary>
    /// One route per selected monitor destination, keyed so pseudo-outputs of
    /// one device share a route when they ride the same USB return pair: the
    /// analog outputs (hp1/hp2/lineout) all use the monitor-bus pair.
    /// </summary>
    private IEnumerable<(string Key, string Target)> MonitorRouteTargetsLocked()
    {
        var seen = new HashSet<string>();
        foreach (string name in _monitorOutputs)
        {
            string key = OutputRouteKey(name);
            if (seen.Add(key)) yield return (key, name);
        }
    }

    /// <summary>
    /// Verify the monitor mix's device links and repair what died: a USB
    /// output that re-enumerates (suspend, reset, profile change) takes its
    /// node and every link on it along, and a device absent at wiring time
    /// got no links at all. Runs every sweep; true when something changed.
    /// </summary>
    public bool EnsureMonitorRoutes()
    {
        lock (_gate)
        {
            if (!_built) return false;
            if (_monitorMuted)
            {
                int pending = _monitorRoutes.Count;
                RemovePausedMonitorRoutesLocked();
                return _monitorRoutes.Count != pending;
            }
            if (_monitorOutputs.Count == 0) return false;
            bool changed = false;
            foreach ((string key, string target) in MonitorRouteTargetsLocked())
            {
                PortLink? route = _monitorRoutes.GetValueOrDefault(key);
                if (route is { Pairs.Count: > 0 })
                {
                    // A unit that re-enumerates brings its sink back with the
                    // port names the route remembers, often before its capture.
                    // Relinking then would start its playback first, so a
                    // route that lost links waits as a new route does.
                    if (!_pw.LinksPresent(route) && HoldPlaybackLocked(target))
                    {
                        _pw.Unlink(route);
                        _monitorRoutes[key] = new PortLink([]);
                        changed = true;
                        continue;
                    }
                    LinkHealth health = _pw.EnsureLinks(route);
                    if (health == LinkHealth.Healthy && !_incompleteMonitorRoutes.Contains(key)) continue;
                    // A route that had to be made again is a device that went
                    // away and returned, so a volume it refused while it was
                    // gone is worth writing once more.
                    _outputVolumeDue.Rearm(StripMarker(target));
                    if (health == LinkHealth.Relinked && !_incompleteMonitorRoutes.Contains(key)) { changed = true; continue; }
                    _pw.Unlink(route);   // Broken: the port names themselves are stale
                }
                PortLink? fresh = RouteFeedLocked(target);
                if (fresh is null) { _monitorRoutes[key] = new PortLink([]); continue; }
                _monitorRoutes[key] = fresh;
                if (fresh.Pairs.Count > 0)
                {
                    _outputVolumeDue.Rearm(StripMarker(target));
                    changed = true;
                }
            }
            return changed;
        }
    }

    /// <summary>Level of one channel in one mix (0..1).</summary>
    public void SetLevel(string channelId, string mixId, double level)
    {
        lock (_gate)
        {
            string cell = Cell(channelId, mixId);
            if (!_cells.Contains(cell)) return;
            _levels[cell] = Math.Clamp(level, 0.0, 1.0);
            ApplyCellLocked(channelId, mixId);
        }
    }

    /// <summary>
    /// Mute/unmute one channel within one mix only. Unmuting a member of an
    /// exclusive group first mutes the other members in every mix.
    /// </summary>
    public void SetChannelMuted(string channelId, string mixId, bool muted)
    {
        lock (_gate)
        {
            string cell = Cell(channelId, mixId);
            if (!_cells.Contains(cell)) return;
            if (!muted) CloseExclusivePeersLocked(channelId);
            if (muted) _muted.Add(cell); else _muted.Remove(cell);
            ApplyCellLocked(channelId, mixId);
        }
    }

    /// <summary>Master level for a mix, scaling every channel feeding it.</summary>
    public void SetMixVolume(string mixId, double volume)
    {
        lock (_gate)
        {
            MixDefinition? monitor = MonitorMixLocked(mixId);
            volume = Math.Clamp(volume, 0, monitor is not null ? PipeWireAdapter.MaxSinkVolume : 1);
            if (monitor is not null) _pw.SetSinkVolume(monitor.SinkName, volume);
            _mixVolume[mixId] = volume;
            if (monitor is null) ReapplyMixLocked(mixId);
            else PushDuckKeyOpenLocked();
        }
    }

    public void SetMixMuted(string mixId, bool muted)
    {
        lock (_gate)
        {
            MixDefinition? monitor = MonitorMixLocked(mixId);
            if (monitor is null)
            {
                if (muted) _mixMuted.Add(mixId); else _mixMuted.Remove(mixId);
                ReapplyMixLocked(mixId);
                SyncMonitorHardwareMutesLocked();
                return;
            }
            bool replaceSelection = !muted && _monitorMuted;
            var affected = replaceSelection ? _config.Mixes.Where(m => m.Kind == MixKind.Monitor).ToArray() : [monitor];
            var before = affected.ToDictionary(m => m.Id, m => _mixMuted.Contains(m.Id));
            var attempted = new List<MixDefinition>();
            try
            {
                // Commit intent only after every virtual monitor accepted it.
                // A refused resume keeps its software routes closed.
                foreach (MixDefinition mix in affected)
                {
                    attempted.Add(mix); // A timeout can leave even the refused write applied.
                    _pw.SetSinkMuted(mix.SinkName, mix.Id == mixId ? muted : true);
                }
                foreach (MixDefinition mix in affected)
                    if (mix.Id == mixId && !muted) _mixMuted.Remove(mix.Id); else _mixMuted.Add(mix.Id);
                if (replaceSelection) SetMonitorMuted(false);
            }
            catch
            {
                foreach (MixDefinition mix in affected)
                {
                    if (before[mix.Id]) _mixMuted.Add(mix.Id); else _mixMuted.Remove(mix.Id);
                }
                // Queue every attempted sink before a helper can fail again.
                foreach (MixDefinition mix in attempted) _desktopMixMuteResets[mix.SinkName] = (0, 0);
                foreach (MixDefinition mix in attempted)
                    if (!RestoreDesktopMixMuteLocked(mix.SinkName, mix.Id)) break;
                throw;
            }
            foreach (MixDefinition mix in affected) _desktopMixMuteResets.Remove(mix.SinkName);
            SyncMonitorHardwareMutesLocked(unmutedMix: muted ? null : mixId, explicitChange: true);
            PushDuckKeyOpenLocked();
        }
    }

    /// <summary>The matcher deciding which channel a new app stream joins.</summary>
    public StreamMatcher Matcher { get; } = new();

    /// <summary>Streams seen on the last sweep, with the channel each landed in.</summary>
    public IReadOnlyList<StreamAssignment> Streams
    {
        get { lock (_gate) return [.. _streams.Values]; }
    }

    // Known applications keyed by identity: active ones have a live stream,
    // inactive ones are remembered so their routing stays editable and the
    // list is stable. _streams tracks the live stream ids already placed.
    private readonly Dictionary<string, StreamAssignment> _apps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, StreamAssignment> _streams = [];

    // Each application's icon. Its desktop entries are read outside the
    // mixer lock; inside it, lookups only read what was already read.
    private readonly ApplicationIconResolver _icons = new();

    /// <summary>
    /// Look for application streams and route new ones to their channel. Called
    /// on a timer by the daemon. Returns true when anything changed. Streams the
    /// mixer already placed are left alone so manual overrides survive.
    /// </summary>
    public bool SyncStreams()
    {
        if (!_built) return false;
        var (live, clients) = _pw.ListApplications();
        bool changed = false;
        _icons.Refresh();
        Dictionary<string, string> icons = LiveIcons(live, clients);

        lock (_gate)
        {
            var seen = new HashSet<int>();
            _browserIdentities.Clear();
            foreach (AudioStream source in live.Concat(clients))
                if (BrowserTabRouting.IsChromium(source.Binary)) _browserIdentities.Add(source.Identity);
            foreach (AudioStream s in live)
            {
                string identity = s.Identity;
                seen.Add(s.Id);
                bool browser = BrowserTabRouting.IsChromium(s.Binary);
                if (_streams.TryGetValue(s.Id, out StreamAssignment? previous))
                {
                    // Registry ids are reused, and an app can publish its
                    // identity after playback starts. Only keep the placement
                    // while both the stream serial and app still match.
                    if (previous.Serial == s.Serial &&
                        string.Equals(previous.Identity, identity, StringComparison.OrdinalIgnoreCase)
                        && previous.BrowserHeld == (_preserveBrowserOutputs && browser))
                    {
                        if (previous.BrowserHeld)
                        {
                            string? sink;
                            try { sink = _pw.StreamSinkName(s.Serial); }
                            catch (InvalidOperationException) { continue; }
                            string? output = _config.Channels.FirstOrDefault(c => c.IsApplication && c.SinkName == sink)?.Id;
                            if (previous.OutputChannelId != output)
                            {
                                _streams[s.Id] = previous with { OutputChannelId = output };
                                if (_apps.TryGetValue(identity, out StreamAssignment? app) && app.Id == s.Id)
                                    _apps[identity] = app with { OutputChannelId = output };
                                changed = true;
                            }
                        }
                        continue;
                    }
                    _streams.Remove(s.Id);
                    changed = true;
                }

                string channelId = _config.ResolveApplicationChannel(Matcher.Match(s));
                if (_preserveBrowserOutputs && browser)
                {
                    // Browser output selection: the stream stays where the
                    // browser or desktop put it, which can be an OpenXLR
                    // channel the session manager restored it to. The app
                    // keeps its assignment, so mixer.json does not change and
                    // the assignment applies again when the option is off;
                    // the entry says where the stream plays meanwhile.
                    string? sink;
                    try { sink = _pw.StreamSinkName(s.Serial); }
                    catch (InvalidOperationException) { continue; }
                    string assigned = channelId == StreamMatcher.Ignore ? channelId
                        : (_config.Channels.FirstOrDefault(c => c.Id == channelId && c.IsApplication)
                           ?? _config.FallbackApplicationChannel)?.Id ?? channelId;
                    var held = new StreamAssignment(s.Id, s.Serial, s.Label, identity, assigned)
                    {
                        BrowserHeld = true,
                        OutputChannelId = sink is null ? null
                            : _config.Channels.FirstOrDefault(c => c.IsApplication && c.SinkName == sink)?.Id,
                    };
                    _streams[s.Id] = held;
                    if (!PipeWireAdapter.IsPlumbingIdentity(identity)) _apps[identity] = held;
                    changed = true;
                    continue;
                }
                if (channelId == StreamMatcher.Ignore)
                {
                    // Not managed: the stream stays where the desktop put it.
                    // The one exception is a stream the session manager
                    // restored onto an OpenXLR channel from the time the app
                    // was managed; that one goes back to the default output.
                    try { if (!ReleaseStreamLocked(s.Serial)) continue; }
                    catch (InvalidOperationException) { continue; }
                    var left = new StreamAssignment(s.Id, s.Serial, s.Label, identity, StreamMatcher.Ignore);
                    _streams[s.Id] = left;
                    if (!PipeWireAdapter.IsPlumbingIdentity(identity)) _apps[identity] = left;
                    changed = true;
                    continue;
                }
                ChannelDefinition? ch = _config.Channels.FirstOrDefault(c => c.Id == channelId && c.IsApplication)
                                        ?? _config.FallbackApplicationChannel;
                if (ch is null) continue;

                try
                {
                    if (!_pw.IsStreamOnSink(s.Serial, ch.SinkName))
                    {
                        _pw.MoveStreamToSink(s.Serial, ch.SinkName);
                        // Leave it unplaced so the next sweep retries. No wait
                        // or sleep under the mixer lock.
                        if (!_pw.IsStreamOnSink(s.Serial, ch.SinkName)) continue;
                    }
                    // The mixer owns muting (sends, masters) from here on; a
                    // per-stream mute remembered by stream-restore has no
                    // control anywhere in OpenXLR and just reads as silence.
                    _pw.SetSinkInputMuted(s.Serial, false);
                }
                catch (InvalidOperationException) { continue; }

                var placed = new StreamAssignment(s.Id, s.Serial, s.Label, identity, ch.Id);
                _streams[s.Id] = placed;
                // Transient plumbing (Wine's probe streams, bare runtime
                // binaries) is routed but never remembered as an app.
                if (!PipeWireAdapter.IsPlumbingIdentity(identity)) _apps[identity] = placed;
                changed = true;
            }

            foreach (int gone in _streams.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                _streams.Remove(gone);
                changed = true;
            }

            // Every running audio-capable app is listed even before it plays:
            // PipeWire clients cover "running", streams cover "playing".
            var runningIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AudioStream client in clients)
            {
                string identity = client.Identity;
                if (PipeWireAdapter.IsPlumbingIdentity(identity)) continue;
                runningIdentities.Add(identity);
                if (!_apps.ContainsKey(identity))
                {
                    _apps[identity] = new StreamAssignment(0, 0, client.Label, identity,
                        _config.ResolveApplicationChannel(Matcher.Match(client)))
                        { Active = false, Running = true, BrowserHeld = BrowserHeldLocked(identity) };
                    changed = true;
                }
                else if (!_apps[identity].Active && _apps[identity].Label != client.Label &&
                         (client.Label.Length < _apps[identity].Label.Length ||
                          string.Equals(_apps[identity].Label, identity, StringComparison.OrdinalIgnoreCase)))
                {
                    // Heal stale or placeholder labels from a live client, but
                    // prefer the shortest name: apps register helper clients
                    // like "Google Chrome input" alongside the real one.
                    _apps[identity] = _apps[identity] with { Label = client.Label };
                    changed = true;
                }
            }

            // Reconcile flags; remembered apps stay listed when gone entirely.
            // An app keeps the icon it was last seen with once it stops.
            foreach ((string identity, StreamAssignment app) in _apps.ToList())
            {
                AudioStream? stream = live.FirstOrDefault(s => s.Id == app.Id && s.Serial == app.Serial
                    && string.Equals(s.Identity, identity, StringComparison.OrdinalIgnoreCase))
                    ?? live.LastOrDefault(s => string.Equals(s.Identity, identity, StringComparison.OrdinalIgnoreCase));
                bool activeNow = stream is not null;
                bool runningNow = activeNow || runningIdentities.Contains(identity);
                string? icon = icons.GetValueOrDefault(identity) ?? app.Icon
                    ?? _icons.Resolve(ApplicationIconHints.Remembered(identity, app.Label))?.Icon;
                // A replacement can still be waiting for placement. Its
                // browser flags must not come from the previous stream.
                bool held = stream is null ? BrowserHeldLocked(identity)
                    : _preserveBrowserOutputs && BrowserTabRouting.IsChromium(stream.Binary);
                string? output = held && stream is not null && _streams.TryGetValue(stream.Id, out StreamAssignment? placed)
                    && placed.Serial == stream.Serial ? placed.OutputChannelId : null;
                int id = stream?.Id ?? 0, serial = stream?.Serial ?? 0;
                if (app.Active != activeNow || app.Running != runningNow || app.Icon != icon || app.OutputChannelId != output
                    || app.BrowserHeld != held || app.Id != id || app.Serial != serial)
                {
                    _apps[identity] = app with
                    {
                        Active = activeNow, Running = runningNow,
                        Id = id, Serial = serial,
                        Icon = icon, OutputChannelId = output, BrowserHeld = held,
                    };
                    changed = true;
                }
            }
        }
        return changed;
    }

    /// <summary>
    /// The best icon each live identity's streams and clients lead to: an
    /// application can register several clients, and one of them may carry a
    /// published icon name or an application id the others lack.
    /// </summary>
    private Dictionary<string, string> LiveIcons(IReadOnlyList<AudioStream> live, IReadOnlyList<AudioStream> clients)
    {
        var best = new Dictionary<string, IconMatch>(StringComparer.OrdinalIgnoreCase);
        foreach (AudioStream s in live.Concat(clients))
        {
            if (_icons.Resolve(ApplicationIconHints.From(s)) is not { } match) continue;
            string identity = s.Identity;
            if (!best.TryGetValue(identity, out IconMatch? held) || match.Source < held.Source) best[identity] = match;
        }
        return best.ToDictionary(p => p.Key, p => p.Value.Icon, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Hand a stream back to the desktop: if it sits on one of the mixer's
    /// channel sinks, move it to the system default output. A stream already
    /// elsewhere is left alone, and so is everything when the default output
    /// is unknown.
    /// </summary>
    private bool ReleaseStreamLocked(int serial)
    {
        string? current = _pw.StreamSinkName(serial);
        if (current is null) return false;
        if (!_config.Channels.Any(c => c.SinkName == current)) return true;
        string? fallback = _pw.GetDefaultSink();
        if (string.IsNullOrEmpty(fallback)) return false;
        if (fallback == current) return true;
        _pw.MoveStreamToSink(serial, fallback);
        // A refused or not yet applied move stays unplaced for the next sweep.
        return _pw.IsStreamOnSink(serial, fallback);
    }

    /// <summary>
    /// Drop an application from the registry and forget its channel override.
    /// A still-running app simply re-registers on the next sweep.
    /// </summary>
    public void ForgetApp(string identity)
    {
        lock (_gate)
        {
            identity = StreamMatcher.MigrateIdentity(identity);
            _apps.Remove(identity);
            Matcher.RemoveOverride(identity);
            foreach ((int id, StreamAssignment placed) in _streams.ToList())
                if (string.Equals(placed.Identity, identity, StringComparison.OrdinalIgnoreCase))
                    _streams.Remove(id);
        }
    }

    /// <summary>
    /// Route an application (by identity) to a channel: remembered for every
    /// future stream, and applied to its live streams right away if any.
    /// </summary>
    public void AssignApp(string identity, string channelId, string? label = null)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(identity)) return;
            identity = StreamMatcher.MigrateIdentity(identity);
            if (channelId == StreamMatcher.Ignore)
            {
                // Stop managing the app: remember the choice, hand its live
                // streams back to the desktop, keep it listed as ignored.
                EnsureOverrideCapacity(identity);
                Matcher.SetOverride(identity, StreamMatcher.Ignore);
                foreach ((int id, StreamAssignment placed) in _streams.ToList())
                    if (string.Equals(placed.Identity, identity, StringComparison.OrdinalIgnoreCase)
                        && !placed.BrowserHeld)
                    {
                        try { ReleaseStreamLocked(placed.Serial); }
                        catch (InvalidOperationException) { /* the sweep retries */ }
                        _streams.Remove(id);   // the next sweep lists it as unmanaged
                    }
                if (_apps.TryGetValue(identity, out StreamAssignment? ignored))
                    _apps[identity] = ignored with { ChannelId = StreamMatcher.Ignore };
                else
                    _apps[identity] = new StreamAssignment(0, 0, label ?? identity, identity, StreamMatcher.Ignore)
                        { Active = false, Running = false, BrowserHeld = BrowserHeldLocked(identity) };
                return;
            }
            ChannelDefinition? ch = _config.Channels.FirstOrDefault(c => c.Id == channelId && c.IsApplication);
            if (ch is null) return;
            EnsureOverrideCapacity(identity);
            Matcher.SetOverride(identity, channelId);

            foreach ((int id, StreamAssignment placed) in _streams.ToList())
                if (string.Equals(placed.Identity, identity, StringComparison.OrdinalIgnoreCase)
                    && !placed.BrowserHeld)
                {
                    try { _pw.MoveStreamToSink(placed.Serial, ch.SinkName); }
                    catch (InvalidOperationException) { /* the sweep retries */ }
                    _streams.Remove(id);
                }

            if (_apps.TryGetValue(identity, out StreamAssignment? app))
                _apps[identity] = app with { ChannelId = channelId };
            else
                // Pre-registered by hand (e.g. from the installed-apps picker):
                // listed silent until its first stream shows up and confirms.
                _apps[identity] = new StreamAssignment(0, 0, label ?? identity, identity, channelId)
                    { Active = false, Running = false, BrowserHeld = BrowserHeldLocked(identity) };
        }
    }

    /// <summary>
    /// Move one stream to a channel by hand and remember the choice for the next
    /// time the same application starts.
    /// </summary>
    public void AssignStream(int streamId, string channelId)
    {
        lock (_gate)
        {
            // The command carries a registry id, not the PulseAudio serial
            // used by pactl. An unknown id must never address another stream.
            if (!_streams.TryGetValue(streamId, out StreamAssignment? existing))
                throw new InvalidOperationException($"No tracked playback stream with PipeWire id {streamId}.");
            if (channelId == StreamMatcher.Ignore)
            {
                EnsureOverrideCapacity(existing.Identity);
                if (!existing.BrowserHeld) ReleaseStreamLocked(existing.Serial);
                Matcher.SetOverride(existing.Identity, StreamMatcher.Ignore);
                _streams.Remove(streamId);   // the next sweep lists it as unmanaged
                return;
            }
            ChannelDefinition? ch = _config.Channels.FirstOrDefault(c => c.Id == channelId && c.IsApplication);
            if (ch is null) return;

            EnsureOverrideCapacity(existing.Identity);
            if (!existing.BrowserHeld) _pw.MoveStreamToSink(existing.Serial, ch.SinkName);
            Matcher.SetOverride(existing.Identity, channelId);
            _streams.Remove(streamId); // the next sweep confirms the destination
        }
    }

    public MixerState Snapshot()
    {
        lock (_gate)
        {
            DspFeatureAvailability clipGuard = _pw.GetSoftwareClipGuardAvailability();
            Dictionary<string, UnitInputStatus> unitInputs = UnitInputsLocked();
            int monitorCount = _config.Mixes.Count(m => m.Kind == MixKind.Monitor);
            return WithAppearanceLocked(new MixerState
            {
                SoftLowCutApplicable = SoftLowCutApplicableLocked,
                SoftClipGuardApplicable = SoftClipGuardApplicableLocked,
                ExclusiveGroups = ExclusiveGroupsModel.Copy(_config.ExclusiveGroups),
                Mixes = [.. _config.Mixes.Select(m => new MixStatus(
                    m.Id, m.Name,
                    _mixVolume.GetValueOrDefault(m.Id, 1.0),
                    _mixMuted.Contains(m.Id), KindName(m.Kind), m.IsEditable)
                    { Deletable = m.IsEditable && (m.Kind != MixKind.Monitor || monitorCount > 1) })],
                Channels = [.. _config.Channels.Select(c => new ChannelStatus(
                    c.Id, ChannelNameLocked(c),
                    _config.Mixes.ToDictionary(m => m.Id, m => _levels.GetValueOrDefault(Cell(c.Id, m.Id), 0.0)),
                    [.. _config.Mixes.Where(m => _muted.Contains(Cell(c.Id, m.Id))).Select(m => m.Id)],
                    c.InputPair is not null, c.CaptureSource, c.CapturePair, c.CaptureSource is not null && BindingConnectedLocked(c),
                    ChannelPresentLocked(c), GroupForChannelLocked(c.Id)?.Id)
                {
                    CaptureMonoChannel = c.CaptureMonoChannel,
                    Microphone = c.Microphone,
                    ChainWidth = c.ChainWidth,
                    Binding = BindingStateLocked(c),
                    BindingConnected = BindingConnectedLocked(c),
                    BindingWarning = BindingWarningLocked(c),
                    UnitInput = unitInputs.GetValueOrDefault(c.Id),
                    Volume = ChannelVolumeLocked(c.Id),
                    CaptureGain = CaptureGainLocked(c, unitInputs),
                    Eq = CarriesEq(c) ? EqStateLocked(c.Id, unitInputs) : null,
                    Noise = CarriesEq(c) ? NoiseStateLocked(c.Id) : null,
                    Voice = CarriesEq(c) ? VoiceStateLocked(c.Id) : null,
                })],
                RenamedSinceStart = _renamedSinceBuild,
                DefaultMonitorMix = PrimaryMonitorLocked()?.Id,
                MonitorOutput = _monitorOutputs.FirstOrDefault(),
                MonitorOutputs = [.. _monitorOutputs],
                MonitorMuted = _monitorMuted,
                MonitorMuteMicrophones = _monitorMuteMicrophones,
                MicrophonePausePending = _microphonePausePending,
                MonitorFeeds = new Dictionary<string, string>(_monitorFeeds),
                MonitorOutputLabels = new Dictionary<string, string>(_monitorOutputLabels, StringComparer.Ordinal),
                OutputVolume = _outputVolume,
                LowCutHz = _lowCutHz,
                SoftClipGuard = _softClipGuard,
                SoftClipGuardAvailable = clipGuard.Available,
                SoftClipGuardError = clipGuard.Error,
                CompensateMixLatency = _compensateMixLatency,
                MixDelayMilliseconds = new Dictionary<string, double>(_mixDelayValues),
                MixLatencyError = _mixLatencyError,
                SoundCheck = SoundCheckSnapshotLocked(),
                Soundboard = SoundboardSnapshotLocked(),
                Inserts = InsertStatusLocked(),
                EnforcedDefaultSink = _enforcedSink,
                EnforcedDefaultSource = _enforcedSource,
                AuxPortEnabled = _auxPortEnabled,
                Streams = [.. _apps.Values
                    .OrderByDescending(a => a.Active).ThenBy(a => a.Label, StringComparer.OrdinalIgnoreCase)],
                PreserveBrowserOutputs = _preserveBrowserOutputs,
                RouteAppsByDefault = Matcher.RouteByDefault,
                BlockSize = _blockSize,
                BrowserTabs = BrowserTabsLocked(),
            });
        }
    }

    private static string KindName(MixKind kind) => kind switch
    {
        MixKind.Monitor => "monitor",
        MixKind.VirtualMic => "virtualMic",
        _ => "auxPort",
    };

    /// <summary>
    /// What a send's leg should carry: the send level with the channel
    /// volume, and the master only when the mix has no desktop master; and
    /// whether it is muted, including while an exclusive peer's mute is
    /// still waiting (<paramref name="waiting"/>).
    /// </summary>
    private (double Level, bool Muted) CellTargetLocked(string channelId, string mixId, out bool waiting)
    {
        string cell = Cell(channelId, mixId);
        bool monitor = MonitorMixLocked(mixId) is not null;
        double level = SendGain(_levels.GetValueOrDefault(cell, 0.0), ChannelVolumeLocked(channelId),
            monitor ? 1 : _mixVolume.GetValueOrDefault(mixId, 1.0));
        waiting = !_muted.Contains(cell) && ExclusivePeerPendingLocked(channelId);
        bool muted = waiting || _muted.Contains(cell) || (!monitor && _mixMuted.Contains(mixId));
        if (IsHardwareMicLocked(channelId) && monitor && MonitorFeed.Includes(JackFeedLocked(), mixId))
            muted = true;   // the hardware direct path carries it to the jacks
        else if (monitor && HearsMyselfLocked(channelId, mixId))
        {
            // Mic setup's Hear myself, for the session only: the send opens
            // at its own level, or at unity when it was pulled all the way
            // down, and an exclusive group still holds it while it waits.
            muted = waiting;
            if (_levels.GetValueOrDefault(cell, 0.0) <= 0) level = SendGain(1, ChannelVolumeLocked(channelId), 1);
        }
        return (level, muted);
    }

    /// <summary>Channel send, with the channel volume, and the master only when the mix has no desktop master.</summary>
    private void ApplyCellLocked(string channelId, string mixId)
    {
        string cell = Cell(channelId, mixId);
        var targets = CellLegTargetsLocked(channelId, mixId, out bool waiting);
        bool bus = _duckBuses.ContainsKey(mixId);
        if (!_legIndex.ContainsKey(cell) || bus && !_duckLegIndex.ContainsKey(cell)) DiscoverLegsLocked();
        try
        {
            Write();
        }
        catch (InvalidOperationException)
        {
            // The leg's index changed (combine reconnected); rediscover once.
            DiscoverLegsLocked();
            try { Write(); }
            catch (InvalidOperationException) { MarkCellPendingLocked(cell); }
        }
        PushDuckKeyOpenLocked();

        void Write()
        {
            if (!_legIndex.TryGetValue(cell, out int direct) || bus && !_duckLegIndex.ContainsKey(cell))
            {
                MarkCellPendingLocked(cell);
                return;
            }
            // Close the unused leg first. A failed close must never be
            // followed by opening the other path onto the same audio.
            if (bus && UsesDuckBusLocked(cell, mixId))
            {
                Leg(direct, targets.Direct);
                Leg(_duckLegIndex[cell], targets.Bus);
            }
            else
            {
                if (bus) Leg(_duckLegIndex[cell], targets.Bus);
                Leg(direct, targets.Direct);
            }
            if (waiting) MarkCellPendingLocked(cell); else _pendingCells.Remove(cell);
        }

        void Leg(int index, (double Level, bool Muted) target)
        {
            _pw.SetSinkInputVolume(index, target.Level);
            _pw.SetSinkInputMuted(index, target.Muted);
        }
    }

    /// <summary>A cell that could not be pushed, and a fresh round of tries for it.</summary>
    private void MarkCellPendingLocked(string cell)
    {
        if (_pendingCells.Add(cell)) _cellRounds = _cellWait = 0;
    }

    /// <summary>
    /// Drop from the pending set every cell that no longer exists. A deleted
    /// channel or mix is not waiting for a leg, and its cells must not keep
    /// the reconciliation running on its own account.
    /// </summary>
    internal static void ForgetRemovedCells(HashSet<string> pending, IReadOnlySet<string> cells)
    {
        if (pending.Count > 0) pending.RemoveWhere(cell => !cells.Contains(cell));
    }

    /// <summary>
    /// Sweeps to wait before reconciliation round <paramref name="rounds"/>.
    /// The first rounds go on consecutive sweeps, because a leg that is
    /// merely late is up within a second or two. After that the gap doubles
    /// and settles at <see cref="CellMaxGapSweeps"/>, and there it stays: a
    /// send whose leg has still not appeared is a send sitting at full level
    /// and unmuted, so it is tried again for as long as the cell exists.
    /// The cost in the steady state is one leg discovery a minute, and only
    /// while something is actually waiting.
    /// </summary>
    internal static int CellRoundGap(int rounds)
    {
        if (rounds <= 0) return 0;                     // the first round runs at once
        if (rounds < CellPromptRounds) return 1;       // then the next sweep, twice
        return Math.Min(2 << Math.Min(rounds - CellPromptRounds, 16), CellMaxGapSweeps);
    }

    /// <summary>
    /// Keep every send's leg on its stored fader. Called from the sweep.
    /// <para>
    /// A leg that moved away from its fader is put back. The session manager
    /// restores a remembered volume and mute onto every new stream, and every
    /// leg of one channel shares the key it remembers them by, so a new leg
    /// can take another send's level, or full level, and that write can land
    /// after OpenXLR's own. WirePlumber 0.4 does this to every new leg, with
    /// full level when it remembers nothing. The check reads the registry
    /// the sweep already holds, so it costs no helper call while every leg
    /// is where it belongs.
    /// </para>
    /// <para>
    /// The stored fader is also pushed onto every send whose combine leg was
    /// missing when it was last set. A combine grows its legs a moment after
    /// its module loads, and the build does not wait for them, so a leg can
    /// turn up after its cell was applied. A cell stays on this list until
    /// its fader is on the leg or the cell itself is gone, on the backoff
    /// <see cref="CellRoundGap"/> sets.
    /// </para>
    /// </summary>
    /// <param name="held">The sends whose leg was put back on its fader.</param>
    public bool EnsureCellLevels(out IReadOnlyList<string> held)
    {
        lock (_gate)
        {
            held = [];
            ForgetRemovedCells(_pendingCells, _cells);
            if (!_built) { _cellRounds = _cellWait = 0; return false; }
            held = HoldCellLevelsLocked();
            if (_pendingCells.Count == 0) { _cellRounds = _cellWait = 0; return held.Count > 0; }
            if (_cellWait > 0) { _cellWait--; return held.Count > 0; }
            DiscoverLegsLocked();
            bool applied = false;
            foreach (string cell in _pendingCells.ToList())
            {
                int bar = cell.IndexOf('|', StringComparison.Ordinal);
                if (bar <= 0) { _pendingCells.Remove(cell); continue; }
                if (!_legIndex.ContainsKey(cell)) continue;   // still not there; try again next round
                ApplyCellLocked(cell[..bar], cell[(bar + 1)..]);
                applied = true;
            }
            _cellRounds++;
            // The gap is a distance between rounds; the wait counts the
            // sweeps skipped in between, which is one fewer.
            _cellWait = Math.Max(0, CellRoundGap(_cellRounds) - 1);
            return applied || held.Count > 0;
        }
    }

    /// <inheritdoc cref="EnsureCellLevels(out IReadOnlyList{string})"/>
    public bool EnsureCellLevels() => EnsureCellLevels(out _);

    /// <summary>
    /// Push the fader again onto every known leg whose level or mute differs
    /// from it. A leg the registry does not show yet is left for the next
    /// sweep, and a waiting cell is the pending round's business.
    /// </summary>
    private List<string> HoldCellLevelsLocked()
    {
        var held = new List<string>();
        if (_legIndex.Count == 0) return held;
        IReadOnlyDictionary<int, (int Percent, bool Muted)> legs = _pw.CombineLegLevels();
        foreach ((string cell, int index) in _legIndex.ToList())
        {
            if (_pendingCells.Contains(cell) || !_cells.Contains(cell)) continue;
            int bar = cell.IndexOf('|', StringComparison.Ordinal);
            if (bar <= 0) continue;
            string channel = cell[..bar], mix = cell[(bar + 1)..];
            if (!legs.TryGetValue(index, out (int Percent, bool Muted) leg))
            {
                if (_duckBuses.ContainsKey(mix)) MarkCellPendingLocked(cell);
                continue;
            }
            var targets = CellLegTargetsLocked(channel, mix, out _);
            bool directHeld = Matches(leg, targets.Direct);
            bool busHeld = !_duckBuses.ContainsKey(mix) || _duckLegIndex.TryGetValue(cell, out int busIndex)
                && legs.TryGetValue(busIndex, out var busLeg) && Matches(busLeg, targets.Bus);
            if (directHeld && busHeld) continue;
            ApplyCellLocked(channel, mix);
            held.Add(cell);
        }
        return held;

        static bool Matches((int Percent, bool Muted) leg, (double Level, bool Muted) target)
            => leg.Percent == PipeWireAdapter.SinkInputPercent(target.Level) && leg.Muted == target.Muted;
    }

    private MixDefinition? MonitorMixLocked(string mixId)
        => _config.Mixes.FirstOrDefault(m => m.Id == mixId && m.Kind == MixKind.Monitor);

    private double MixVolumeMaximumLocked(string mixId)
        => MonitorMixLocked(mixId) is not null ? PipeWireAdapter.MaxSinkVolume : 1;

    private void ReapplyMixLocked(string mixId) => ReapplyCellsLocked([mixId], masters: true);

    private void ReapplyMixesLocked(IEnumerable<MixDefinition> mixes)
        => ReapplyCellsLocked([.. mixes.Select(m => m.Id)], masters: true);

    private void TearDownLocked()
    {
        ResetDesktopMonitorMuteLocked();
        // The sends go with the graph, so Hear myself has nothing to put back.
        _soundCheckHearMyself = false;
        if (_soundCheck is not null)
            StopSoundCheckLocked(restore: false, error: "Sound Check stopped because the audio graph was rebuilt.");
        RemoveInputMetersLocked();
        _meters.Dispose();
        _meters = new MeterReader();   // Dispose is terminal; a rebuild needs a fresh reader
        _meters.SetForceQuantum(_blockSize);
        foreach (PortLink route in _monitorRoutes.Values) _pw.Unlink(route);
        _monitorRoutes.Clear();
        _monitorMuted = false;
        _microphonePauseRepairs.Clear();
        _monitorOutputs.Clear();
        _monitorFeeds.Clear();
        _monitorOutputLabels.Clear();
        _incompleteMonitorRoutes.Clear();
        if (_auxRoute is not null) { _pw.Unlink(_auxRoute); _auxRoute = null; }
        foreach (PortLink feed in _inputFeeds.Values) _pw.Unlink(feed);
        _inputFeeds.Clear();
        foreach (string id in _captureFeeds.Keys.ToArray()) RemoveCaptureFeedLocked(id);
        StopPlayerLocked();
        RemoveEqStagesLocked();
        RemoveDuckBusesLocked();
        RemoveInputChainsLocked();
        RemoveMixChainsLocked();
        _chainStructures.Clear();
        _inputDevice = null;
        _inputWarning = null;
        _stripSources.Clear();
        _stripWarnings.Clear();
        _captureWarnings.Clear();
        _wiredBindings.Clear();
        _pw.TearDown();     // unloads modules in reverse order: combines, then mixes
        _combineModules.Clear();
        _mixModules.Clear();
        _postModules.Clear();
        _virtualMicModules.Clear();
        _renamedSinceBuild = false;
        _legIndex.Clear();
        _pendingCells.Clear();
        _cellRounds = _cellWait = 0;
        _streams.Clear();
        _cells.Clear();
        _levels.Clear();
        _channelVolume.Clear();
        _muted.Clear();
        _mixVolume.Clear();
        _mixMuted.Clear();
        _built = false;
    }

    public void TearDown()
    {
        lock (_gate) TearDownLocked();
    }

    /// <summary>
    /// Whether the built graph is still published: false once pipewire-pulse
    /// restarted underneath the daemon and took every module with it. Null
    /// when the question cannot be answered right now.
    /// </summary>
    public bool? GraphPresent()
    {
        lock (_gate)
        {
            if (!_built) return null;
            MixDefinition? probe = _config.Mixes.FirstOrDefault();
            return probe is null ? null : _pw.SinkExists(probe.SinkName);
        }
    }

    /// <summary>
    /// Forget a graph that is already gone. The holder processes are stopped
    /// and the bookkeeping cleared, but no module is unloaded: their ids now
    /// belong to whatever loaded after the restart.
    /// </summary>
    public void ForgetGraph()
    {
        lock (_gate)
        {
            _pw.ForgetModules();
            TearDownLocked();
        }
    }

    public void Dispose()
    {
        TearDown();
        _meters.Dispose();
    }
}
