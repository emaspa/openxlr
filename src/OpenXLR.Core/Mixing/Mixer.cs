namespace OpenXLR.Core.Mixing;

/// <summary>
/// Builds and maintains the submix graph, entirely from PipeWire filter sinks so
/// every node is clocked by construction and audio always flows:
///
///   application -> stable public channel sink -> optional inserts -> internal
///   fan-out (combine sink over all mixes) -> mix buses -> output devices.
///
/// A combine sink runs one internal stream per mix it feeds, and each of those
/// streams has its own volume and mute: those streams ARE the faders, so the
/// whole matrix needs one sink per channel plus one per mix. Everything is
/// clocked through the output device via the direct links (an earlier
/// loopback-based design stalled because its islands had no clock driver, and
/// a remap-cell design worked but exposed 21 extra sinks, which overwhelmed
/// desktop applets and helped exhaust pipewire-pulse's file descriptors).
///
/// Level changes touch only stream volumes. New application channels are added
/// alongside the running graph, and renames change descriptions only.
/// </summary>
public sealed class Mixer : IDisposable, ILayoutInfo
{
    private readonly PipeWireAdapter _pw;
    private readonly Dictionary<string, double> _levels = [];   // "channel|mix" -> level
    private readonly HashSet<string> _muted = [];               // "channel|mix"
    // While the Pro's hardware mic path feeds the headphone jacks, the
    // software XLR 1 -> Monitor send is silenced in the graph (not in the
    // saved mix) so the mic is not heard twice with a few ms between them.
    private bool _hardwareMicMonitor;
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
    private readonly List<string> _monitorOutputs = [];
    private readonly Dictionary<string, PortLink> _inputFeeds = [];
    private string? _inputDevice;   // the capture device the feeds come from
    private long _inputChainGeneration;

    // The Aux mix's route into the device's USB Aux port (return pair), and
    // whether the user wants that port fed at all.
    private PortLink? _auxRoute;
    private string? _auxTargetSink;
    private bool _auxPortEnabled = true;

    // Cached hardware volume of the selected output device, so external
    // changes (KDE applet, hardware knobs) can be detected and pushed.
    private double? _outputVolume;

    // Enforced system defaults (null = not enforced).
    private string? _enforcedSink;
    private string? _enforcedSource;

    private MixerConfig _config = MixerConfig.Default();
    private bool _built;

    private MeterReader _meters = new();

    public Mixer(PipeWireAdapter? adapter = null) => _pw = adapter ?? new PipeWireAdapter();

    /// <summary>Live stereo levels per channel and mix, keyed by id, as [L, R].</summary>
    public IReadOnlyDictionary<string, double[]> ReadMeters() => _meters.Read();

    public MixerConfig Config => _config;
    public bool Built => _built;

    private static string Cell(string channel, string mix) => $"{channel}|{mix}";

    // channel id -> its combine module; "channel|mix" -> that leg's sink-input index
    private readonly Dictionary<string, uint> _combineModules = [];
    private readonly Dictionary<string, uint> _channelInputModules = [];
    private readonly Dictionary<string, uint> _mixModules = [];
    private readonly Dictionary<string, uint> _mixPostModules = [];
    private readonly Dictionary<string, uint> _virtualMicModules = [];
    private readonly Dictionary<string, int> _legIndex = [];

    /// <summary>Map every combine's internal streams to their (channel, mix) cells.</summary>
    private void DiscoverLegsLocked()
    {
        _legIndex.Clear();
        foreach ((string chId, uint module) in _combineModules)
        {
            IReadOnlyDictionary<string, int> legs = _pw.FindCombineLegs(module);
            foreach (MixDefinition mix in _config.Mixes)
                if (legs.TryGetValue(mix.SinkName, out int idx))
                    _legIndex[Cell(chId, mix.Id)] = idx;
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
                _mixModules[mix.Id] = _pw.CreateNullSink(
                    mix.SinkName, $"OpenXLR {mix.Name} (internal mix bus)", isInternal: true);
                _mixVolume[mix.Id] = mix.Volume;
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
                if (ch.InputPair is null)
                    _channelInputModules[ch.Id] = _pw.CreateNullSink(ch.SinkName, $"OpenXLR {ch.Name}");
                _combineModules[ch.Id] = _pw.CreateCombineSink(ch.FanOutSinkName,
                    config.Mixes.Select(m => m.SinkName),
                    $"OpenXLR {ch.Name} (internal distribution)",
                    needsMonitor: ch.InputPair is not null);
            }
            DiscoverLegsLocked();

            // Push initial fader values.
            foreach (MixDefinition mix in config.Mixes) ReapplyMixLocked(mix.Id);

            // Publish non-monitor mixes as selectable capture devices. Each
            // reads a post sink fed from the mix (directly, or through the
            // mix's insert chain), so adding inserts later never recreates
            // the capture device an app is recording from.
            foreach (MixDefinition mix in config.Mixes.Where(m => m.Kind == MixKind.VirtualMic))
            {
                _mixPostModules[mix.Id] = _pw.CreateNullSink(
                    mix.PostSinkName, $"OpenXLR {mix.Name} (internal capture tap)", isInternal: true);
                _virtualMicModules[mix.Id] = _pw.CreateVirtualMic(
                    mix.VirtualMicName, $"{mix.PostSinkName}.monitor", $"OpenXLR {mix.Name}");
            }

            // Meter every channel and mix so the UI can show what is flowing.
            foreach (ChannelDefinition ch in config.Channels) _meters.Add($"ch:{ch.Id}", ch.SinkName);
            foreach (MixDefinition mix in config.Mixes) _meters.Add($"mix:{mix.Id}", mix.SinkName);

            _built = true;

            if (monitorOutputSink is not null) SetMonitorOutputsLocked([monitorOutputSink]);
            _ = previousDefaultSource;   // defaults are governed by enforcement only
            _ = defaultSource;           // input channels are hardware-wired, not selectable
            WireInputFeedsLocked();
            WireAuxRouteLocked();
            foreach (MixDefinition mix in config.Mixes) WireMixChainLocked(mix);
            foreach (ChannelDefinition channel in config.Channels.Where(c => c.InputPair is null))
                WireAppChainLocked(channel);
        }
    }

    /// <summary>
    /// Add one application channel without rebuilding the existing graph.
    /// Existing application streams stay attached to their public sinks and
    /// every virtual microphone remains the same PipeWire node.
    /// </summary>
    public void AddApplicationChannel(string id, string name)
    {
        lock (_gate)
        {
            if (!_built) throw new InvalidOperationException("mixer is not built");
            if (_config.Channels.Any(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"channel '{id}' already exists");

            var levels = _config.Mixes.ToDictionary(m => m.Id, _ => 1.0);
            var channel = new ChannelDefinition(id, name) { Levels = levels };
            uint inputModule = 0, fanOutModule = 0;
            MixerConfig previous = _config;
            try
            {
                inputModule = _pw.CreateNullSink(channel.SinkName, $"OpenXLR {channel.Name}");
                fanOutModule = _pw.CreateCombineSink(channel.FanOutSinkName,
                    _config.Mixes.Select(m => m.SinkName),
                    $"OpenXLR {channel.Name} (internal distribution)", needsMonitor: false);

                _config = _config with { Channels = [.. _config.Channels, channel] };
                _channelInputModules[id] = inputModule;
                _combineModules[id] = fanOutModule;
                foreach (MixDefinition mix in _config.Mixes)
                {
                    string cell = Cell(id, mix.Id);
                    _levels[cell] = 1.0;
                    _cells.Add(cell);
                }
                DiscoverLegsLocked();
                foreach (MixDefinition mix in _config.Mixes) ApplyCellLocked(id, mix.Id);
                WireAppChainLocked(channel);
                _meters.Add($"ch:{id}", channel.SinkName);
            }
            catch
            {
                if (_appFeeds.Remove(id, out PortLink? feed)) _pw.Unlink(feed);
                if (_appOutputs.Remove(id, out PortLink? output)) _pw.Unlink(output);
                if (_chains.Remove(id, out FilterHandle? chain)) _pw.StopFilter(chain);
                _meters.Remove($"ch:{id}");
                _channelInputModules.Remove(id);
                _combineModules.Remove(id);
                foreach (string cell in _cells.Where(c => c.StartsWith(id + "|", StringComparison.Ordinal)).ToList())
                {
                    _cells.Remove(cell);
                    _levels.Remove(cell);
                    _muted.Remove(cell);
                    _legIndex.Remove(cell);
                }
                _config = previous;
                if (fanOutModule != 0) _pw.UnloadModule(fanOutModule);
                if (inputModule != 0) _pw.UnloadModule(inputModule);
                DiscoverLegsLocked();
                throw;
            }
        }
    }

    /// <summary>Rename an application channel by changing descriptions only.</summary>
    public void RenameApplicationChannel(string id, string name)
    {
        lock (_gate)
        {
            int index = _config.Channels.ToList().FindIndex(c => c.Id == id);
            if (index < 0) throw new InvalidOperationException($"unknown channel '{id}'");
            ChannelDefinition channel = _config.Channels[index];
            if (channel.InputPair is not null)
                throw new InvalidOperationException("hardware input channels cannot be renamed");

            _pw.SetSinkDescription(channel.SinkName, $"OpenXLR {name}");
            _pw.SetSinkDescription(channel.FanOutSinkName,
                $"OpenXLR {name} (internal distribution)");
            var channels = _config.Channels.ToList();
            channels[index] = channel with { Name = name };
            _config = _config with { Channels = channels };
        }
    }

    /// <summary>Rename a user output by updating the existing node descriptions.</summary>
    public void RenameVirtualMix(string id, string name)
    {
        lock (_gate)
        {
            int index = _config.Mixes.ToList().FindIndex(m => m.Id == id);
            if (index < 0) throw new InvalidOperationException($"unknown mix '{id}'");
            MixDefinition mix = _config.Mixes[index];
            if (mix.Kind != MixKind.VirtualMic)
                throw new InvalidOperationException("Monitor and hardware Aux mixes cannot be renamed");

            _pw.SetSinkDescription(mix.SinkName, $"OpenXLR {name} (internal mix bus)");
            _pw.SetSinkDescription(mix.PostSinkName, $"OpenXLR {name} (internal capture tap)");
            _pw.SetSourceDescription(mix.VirtualMicName, $"OpenXLR {name}");
            var mixes = _config.Mixes.ToList();
            mixes[index] = mix with { Name = name };
            _config = _config with { Mixes = mixes };
        }
    }

    /// <summary>
    /// Wire the hardware interface's capture pairs into their input channels
    /// (XLR 1 = pair 0, XLR 2 = pair 1, Line In = pair 2). The interface is
    /// found by name; when absent the input channels are silent and everything
    /// else still works. Safe to call again after a hotplug.
    /// </summary>
    private void WireInputFeedsLocked()
    {
        // UCM split sources (".HiFi__Mic2__source" and friends) rank last: the
        // channels are wired by pair offset on the raw multichannel node, and
        // a split's first match could be the wrong input entirely.
        var sources = _pw.ListDevices()
            .Where(d => d.Kind == AudioNodeKind.Source && !d.IsOwn)
            .OrderBy(d => d.Name.Contains(".HiFi__", StringComparison.Ordinal) ? 1 : 0)
            .ToList();
        // Prefer the interface the daemon actively drives (the hint), so a
        // device switch moves the channel feeds with it; fall back to any
        // Wave XLR so the mixer still works when no device is connected.
        string? previousInput = _inputDevice;
        string? nextInput = (_inputHint is null ? null : sources.FirstOrDefault(
                d => d.Name.Contains(_inputHint, StringComparison.OrdinalIgnoreCase))?.Name)
            ?? sources.FirstOrDefault(
                d => d.Name.Contains("Wave_XLR", StringComparison.OrdinalIgnoreCase))?.Name;
        if (nextInput is null)
        {
            foreach (PortLink feed in _inputFeeds.Values) _pw.Unlink(feed);
            _inputFeeds.Clear();
            RemoveInputChainsLocked();
            _inputDevice = null;
            return;
        }

        // Console rule: a newly patched input comes up muted. Switching the
        // feed device once put a hot mic straight into the monitor outputs
        // (a feedback howl through the speakers), so the hardware channels'
        // monitor sends start muted after a device change and the user
        // unmutes deliberately.
        if (previousInput is not null && previousInput != nextInput)
        {
            MixDefinition? mon = _config.Mixes.FirstOrDefault(m => m.Kind == MixKind.Monitor);
            if (mon is not null)
                foreach (ChannelDefinition hw in _config.Channels.Where(c => c.InputPair is not null))
                {
                    _muted.Add(Cell(hw.Id, mon.Id));
                    ApplyCellLocked(hw.Id, mon.Id);
                }
        }
        // Build the replacement graph under unique node names while the old
        // one is still carrying audio. Only after every required port and link
        // exists do we remove the previous routes. A missing LADSPA/LV2 plugin
        // can therefore report an error without muting the microphone.
        var nextFeeds = new Dictionary<string, PortLink>();
        var nextChains = new Dictionary<string, FilterHandle>();
        var nextChainOuts = new Dictionary<string, PortLink>();
        long generation = ++_inputChainGeneration;
        try
        {
            foreach (ChannelDefinition ch in _config.Channels.Where(c => c.InputPair is not null))
            {
                // The soft low cut and ClipGuard belong to the first XLR
                // channel only; inserts can sit on either mono XLR channel.
                bool lc = ch.InputPair == 0 && _lowCutHz > 0 && _lowCutApplicable;
                bool cg = ch.InputPair == 0 && _softClipGuard && _clipGuardApplicable;
                List<InsertDefinition> inserts = IsInsertChannel(ch.Id) ? InsertsFor(ch.Id) : [];
                bool anyInsert = inserts.Any(i => !i.Bypass && Lv2Catalog.Find(i.Plugin) is not null);
                if (lc || cg || anyInsert)
                {
                    _insertErrors.Remove(ch.Id);
                    FilterHandle chain;
                    string chainId = $"{ch.Id}_{generation}";
                    try
                    {
                        chain = _pw.CreateMicFilter(chainId, lc ? _lowCutHz : 0, cg, inserts);
                    }
                    catch (Exception ex) when (anyInsert)
                    {
                        // Insert failures fall back to the built-in DSP, or to
                        // a plain feed when this chain contained inserts only.
                        _insertErrors[ch.Id] = ex.Message;
                        if (!lc && !cg)
                        {
                            if (previousInput == nextInput && !_chains.ContainsKey(ch.Id)
                                && _inputFeeds.TryGetValue(ch.Id, out PortLink? existingFeed)
                                && _pw.EnsureLinks(existingFeed) != LinkHealth.Broken)
                                nextFeeds[ch.Id] = existingFeed;
                            else
                            {
                                PortLink plain = _pw.RouteInputToChannel(nextInput, ch.SinkName, ch.InputPair!.Value);
                                // No such capture pair on this device (see below): silent channel.
                                if (plain.Pairs.Count == 0) continue;
                                nextFeeds[ch.Id] = plain;
                            }
                            continue;
                        }
                        chain = _pw.CreateMicFilter(chainId + "_builtin", lc ? _lowCutHz : 0, cg);
                    }

                    PortLink into = _pw.RouteInputToChannel(nextInput, chain.SinkName, ch.InputPair!.Value);
                    if (into.Pairs.Count == 0)
                    {
                        // The device has no capture pair at this offset (a
                        // stereo interface has no XLR 2 or Aux In pair): the
                        // channel stays silent, and the chain built for it is
                        // not needed.
                        _pw.StopFilter(chain);
                        continue;
                    }
                    nextChains[ch.Id] = chain;
                    PortLink onward = _pw.LinkNodes(chain.SourceName, "capture", ch.SinkName, "playback");
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
                // DSP changed. Trying to create the same pw-link again returns
                // EEXIST and would look like a failed route.
                if (previousInput == nextInput && !_chains.ContainsKey(ch.Id)
                    && _inputFeeds.TryGetValue(ch.Id, out PortLink? directFeed)
                    && _pw.EnsureLinks(directFeed) != LinkHealth.Broken)
                {
                    nextFeeds[ch.Id] = directFeed;
                    continue;
                }
                PortLink feed = _pw.RouteInputToChannel(nextInput, ch.SinkName, ch.InputPair!.Value);
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
            // reused from the old direct graph must stay connected.
            foreach (PortLink link in nextChainOuts.Values) _pw.Unlink(link);
            foreach ((string key, PortLink link) in nextFeeds)
                if (!_inputFeeds.TryGetValue(key, out PortLink? old) || !ReferenceEquals(old, link))
                    _pw.Unlink(link);
            foreach (FilterHandle chain in nextChains.Values) _pw.StopFilter(chain);
            throw;
        }

        foreach ((string key, PortLink old) in _inputFeeds)
            if (!nextFeeds.TryGetValue(key, out PortLink? keep) || !ReferenceEquals(old, keep))
                _pw.Unlink(old);
        foreach (PortLink old in _chainOuts.Values) _pw.Unlink(old);
        foreach (string key in _chains.Keys.Where(k =>
                     _config.Channels.Any(c => c.Id == k && c.InputPair is not null)).ToList())
        {
            _pw.StopFilter(_chains[key]);
            _chains.Remove(key);
        }

        _inputFeeds.Clear();
        foreach ((string key, PortLink feed) in nextFeeds) _inputFeeds[key] = feed;
        _chainOuts.Clear();
        foreach ((string key, PortLink link) in nextChainOuts) _chainOuts[key] = link;
        foreach ((string key, FilterHandle chain) in nextChains) _chains[key] = chain;
        _inputDevice = nextInput;

        // With a chain in the path, a direct input-to-channel link that this
        // daemon does not track (left by an earlier run, or auto-linked by
        // the session manager) would double the unfiltered signal onto the
        // filtered one. Clear such links now that the chain route is live;
        // the chain's own links are between other nodes and untouched.
        foreach (string key in nextChains.Keys)
        {
            ChannelDefinition? ch = _config.Channels.FirstOrDefault(c => c.Id == key);
            if (ch is not null) _pw.UnlinkNodes(nextInput, ch.SinkName);
        }
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
    /// Sweep healing for built-in DSP and plugin filter chains: a dead holder
    /// process or broken link re-wires the affected path. True when something
    /// changed.
    /// </summary>
    public bool EnsureFilterRoutes()
    {
        lock (_gate)
        {
            if (!_built) return false;
            bool changed = false;
            // Mix chains heal individually; input chains re-wire the whole input path.
            foreach (MixDefinition mix in _config.Mixes)
            {
                string key = MixKey(mix);
                if (_chains.TryGetValue(key, out FilterHandle? c) && c.Process.HasExited)
                {
                    WireMixChainLocked(mix);
                    changed = true;
                }
            }
            foreach (ChannelDefinition channel in _config.Channels.Where(c => c.InputPair is null))
            {
                bool wantsChain = InsertsFor(channel.Id).Any(i => !i.Bypass);
                bool shouldRunChain = wantsChain && !_insertErrors.ContainsKey(channel.Id);
                bool feedBroken = !_appFeeds.TryGetValue(channel.Id, out PortLink? feed)
                    || _pw.EnsureLinks(feed) == LinkHealth.Broken;
                bool chainBroken = shouldRunChain &&
                    (!_chains.TryGetValue(channel.Id, out FilterHandle? appChain)
                     || appChain.Process.HasExited
                     || !_appOutputs.TryGetValue(channel.Id, out PortLink? output)
                     || _pw.EnsureLinks(output) == LinkHealth.Broken);
                if (feedBroken || chainBroken)
                {
                    WireAppChainLocked(channel);
                    changed = true;
                }
            }
            bool inputBroken = _chains.Where(e => _config.Channels.Any(c => c.Id == e.Key && c.InputPair is not null))
                    .Any(e => e.Value.Process.HasExited)
                || _chainOuts.Values.Any(l => _pw.EnsureLinks(l) == LinkHealth.Broken);
            if (inputBroken) { WireInputFeedsLocked(); changed = true; }
            return changed;
        }
    }

    /// <summary>
    /// Route the Aux mix into the device's USB Aux port (its dedicated return
    /// pair) when enabled and the device is present. The port selector and
    /// matrix cell are the daemon's job; this is only the PipeWire leg.
    /// </summary>
    private void WireAuxRouteLocked()
    {
        if (_auxRoute is not null) { _pw.Unlink(_auxRoute); _auxRoute = null; }
        _auxTargetSink = null;
        if (!_auxPortEnabled || !_hardwareOutputRouting) return;
        MixDefinition? aux = _config.Mixes.FirstOrDefault(m => m.Kind == MixKind.AuxPort);
        if (aux is null) return;
        // The raw sink is hidden from pickers, so derive it from any Pro
        // pseudo-output's bare name.
        string? proSink = _pw.ListDevices(
                exposeHardwareMonitorOutputs: true, hardwareSinkHint: _inputHint)
            .Where(d => d.Kind == AudioNodeKind.Sink &&
                        d.Name.Contains("Wave_XLR", StringComparison.OrdinalIgnoreCase))
            .Select(d => { int m = d.Name.IndexOf('#'); return m < 0 ? d.Name : d.Name[..m]; })
            .FirstOrDefault();
        if (proSink is null) return;
        (string tapNode, string tapPrefix) = MixTapLocked(aux);
        PortLink route = _pw.RouteTapToOutput(tapNode, tapPrefix, proSink + "#usbaux");
        if (route.Pairs.Count > 0) { _auxRoute = route; _auxTargetSink = proSink; }
    }

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
        IReadOnlyList<string> monitorSelection;
        lock (_gate)
        {
            sink = _auxTargetSink;
            if (sink is null || !_built) return;
            monitorSelection = [.. _monitorOutputs];
            foreach (PortLink route in _monitorRoutes.Values) _pw.Unlink(route);
            _monitorRoutes.Clear();
            if (_auxRoute is not null) { _pw.Unlink(_auxRoute); _auxRoute = null; }
        }
        _pw.BounceSink(sink);
        lock (_gate)
        {
            SetMonitorOutputsLocked(monitorSelection);
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

    /// <summary>Re-wire the aux route after a hotplug; true when established.</summary>
    public bool EnsureAuxRoute()
    {
        lock (_gate)
        {
            if (!_built || !_auxPortEnabled || _auxRoute is not null) return false;
            WireAuxRouteLocked();
            return _auxRoute is not null;
        }
    }

    private string? _inputHint;
    private bool _hardwareOutputRouting;
    private int _lowCutHz;                 // 0 = off; software low cut on the first XLR channel
    // Filter-chains by insert key. Input keys ("xlr1", "xlr2") hold a mono
    // chain per hardware input that needs one (the first XLR channel's also
    // carries the soft low cut and ClipGuard); mix keys ("mix:stream") hold
    // a stereo chain spliced between the mix and its consumers.
    private readonly Dictionary<string, FilterHandle> _chains = new();
    private readonly Dictionary<string, PortLink> _chainOuts = new();   // input chains: source half into the channel sink
    private readonly Dictionary<string, PortLink> _mixTaps = new();     // mix key: mix monitor into chain or post sink
    private readonly Dictionary<string, PortLink> _mixPostLinks = new(); // mix key: chain source into the post sink
    private readonly Dictionary<string, PortLink> _appFeeds = new();     // public app sink into chain/fan-out
    private readonly Dictionary<string, PortLink> _appOutputs = new();   // app chain into fan-out

    // Plugin insert chains by key, and why a key's last build fell back to
    // running without its inserts.
    private readonly Dictionary<string, List<InsertDefinition>> _inserts = new();
    private readonly Dictionary<string, string> _insertErrors = new();

    /// <summary>Insert keys: every channel ID and "mix:&lt;id&gt;" for every mix.</summary>
    private bool IsInsertChannel(string key) => _config.Channels.Any(c => c.Id == key) || MixForKey(key) is not null;

    // ILayoutInfo, for command validation ahead of the mixer methods.
    public bool HasChannel(string id) { lock (_gate) return _config.Channels.Any(c => c.Id == id); }
    public bool HasMix(string id) { lock (_gate) return _config.Mixes.Any(m => m.Id == id); }
    public bool IsInsertKey(string key) { lock (_gate) return IsInsertChannel(key); }
    public int OverrideCount { get { lock (_gate) return Matcher.Overrides.Count; } }

    private MixDefinition? MixForKey(string key)
        => key.StartsWith("mix:", StringComparison.Ordinal) ? _config.Mixes.FirstOrDefault(m => m.Id == key[4..]) : null;

    private static string MixKey(MixDefinition mix) => $"mix:{mix.Id}";

    /// <summary>
    /// Keep the public application sink stable, process it once, then feed the
    /// internal combine whose legs are the per-mix send faders.
    /// </summary>
    private void WireAppChainLocked(ChannelDefinition channel)
    {
        string key = channel.Id;
        if (_appFeeds.Remove(key, out PortLink? feed)) _pw.Unlink(feed);
        if (_appOutputs.Remove(key, out PortLink? output)) _pw.Unlink(output);
        if (_chains.Remove(key, out FilterHandle? old)) _pw.StopFilter(old);
        _insertErrors.Remove(key);
        if (InsertsFor(key).Any(i => !i.Bypass))
        {
            try
            {
                FilterHandle chain = _pw.CreateMixChain($"ch_{key}", $"OpenXLR {channel.Name} Inserts", InsertsFor(key));
                _chains[key] = chain;
                _appFeeds[key] = _pw.LinkNodes(channel.SinkName, "monitor", chain.SinkName, "playback");
                _appOutputs[key] = _pw.LinkNodes(chain.SourceName, "capture", channel.FanOutSinkName, "input");
                if (_appFeeds[key].Pairs.Count < 2 || _appOutputs[key].Pairs.Count < 2)
                    throw new InvalidOperationException("Could not connect the application channel's stereo insert chain.");
                return;
            }
            catch (Exception ex)
            {
                if (_appFeeds.Remove(key, out PortLink? failedFeed)) _pw.Unlink(failedFeed);
                if (_appOutputs.Remove(key, out PortLink? failedOutput)) _pw.Unlink(failedOutput);
                if (_chains.Remove(key, out FilterHandle? failed)) _pw.StopFilter(failed);
                _insertErrors[key] = ex.Message;
            }
        }
        _appFeeds[key] = _pw.LinkNodes(channel.SinkName, "monitor", channel.FanOutSinkName, "input");
        if (_appFeeds[key].Pairs.Count < 2)
            throw new InvalidOperationException($"Could not connect channel '{key}' to its sends.");
    }

    /// <summary>Where a mix's consumers should read from: its insert chain when one runs, else its own monitor.</summary>
    private (string Node, string Prefix) MixTapLocked(MixDefinition mix)
        => _chains.TryGetValue(MixKey(mix), out FilterHandle? chain) ? (chain.SourceName, "capture") : (mix.SinkName, "monitor");

    /// <summary>
    /// (Re)build one mix's insert chain and re-point everything that reads
    /// the mix: the post sink behind a virtual mic, the monitor routes, or
    /// the aux route. Without inserts the mix feeds them directly.
    /// </summary>
    private void WireMixChainLocked(MixDefinition mix)
    {
        string key = MixKey(mix);
        if (_mixTaps.Remove(key, out PortLink? tap)) _pw.Unlink(tap);
        if (_mixPostLinks.Remove(key, out PortLink? post)) _pw.Unlink(post);
        if (_chains.Remove(key, out FilterHandle? old)) _pw.StopFilter(old);
        _insertErrors.Remove(key);

        List<InsertDefinition> inserts = InsertsFor(key);
        bool anyInsert = inserts.Any(i => !i.Bypass && Lv2Catalog.Find(i.Plugin) is { } p && p.AudioIns >= 2 && p.AudioOuts >= 2);
        if (anyInsert)
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
        (string node, string prefix) = MixTapLocked(mix);
        switch (mix.Kind)
        {
            case MixKind.VirtualMic:
                // The virtual mic reads the post sink, so its identity never
                // changes when a chain comes or goes.
                _mixPostLinks[key] = _pw.LinkNodes(node, prefix, mix.PostSinkName, "playback");
                break;
            case MixKind.Monitor:
                SetMonitorOutputsLocked([.. _monitorOutputs]);
                break;
            case MixKind.AuxPort:
                WireAuxRouteLocked();
                break;
        }
    }

    private List<InsertDefinition> InsertsFor(string channelId)
        => _inserts.TryGetValue(channelId, out List<InsertDefinition>? l) ? l : [];

    /// <summary>Software low cut frequency (0, 80, or 120 Hz; 0 = off).</summary>
    public int LowCutHz { get { lock (_gate) return _lowCutHz; } }

    private bool _lowCutApplicable = true;

    /// <summary>
    /// Whether the soft low cut may engage: false while the active device has
    /// a hardware low cut (stacking both would double-filter). The stored
    /// frequency survives, so switching back re-engages it.
    /// </summary>
    public void SetLowCutApplicable(bool applicable)
    {
        lock (_gate)
        {
            if (_lowCutApplicable == applicable) return;
            _lowCutApplicable = applicable;
            if (_built && _lowCutHz > 0) WireInputFeedsLocked();
        }
    }

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
            if (on && _clipGuardApplicable)
            {
                DspFeatureAvailability support = _pw.GetSoftwareClipGuardAvailability();
                if (!support.Available) throw new InvalidOperationException(support.Error);
            }
            bool previous = _softClipGuard;
            _softClipGuard = on;
            try
            {
                if (_built) WireInputFeedsLocked();
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

    /// <summary>False while the active device has the hardware ClipGuard.</summary>
    public void SetClipGuardApplicable(bool applicable)
    {
        lock (_gate)
        {
            if (_clipGuardApplicable == applicable) return;
            _clipGuardApplicable = applicable;
            if (applicable && _softClipGuard && !_pw.GetSoftwareClipGuardAvailability().Available)
                _softClipGuard = false;
            if (_built && _softClipGuard) WireInputFeedsLocked();
        }
    }

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
            _lowCutHz = hz;
            if (_built) WireInputFeedsLocked();
        }
    }

    // --- plugin inserts ---

    /// <summary>Replace a channel's insert chain (order matters); rewires when built.</summary>
    public void SetInserts(string channel, IReadOnlyList<InsertDefinition> inserts)
    {
        lock (_gate)
        {
            _inserts[channel] = [.. inserts.Select(i => i with { Params = new Dictionary<string, double>(i.Params) })];
            if (_built) RewireInsertKeyLocked(channel);
        }
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

    private void RewireInsertKeyLocked(string key)
    {
        if (MixForKey(key) is MixDefinition mix) WireMixChainLocked(mix);
        else if (_config.Channels.FirstOrDefault(c => c.Id == key && c.InputPair is null) is { } appChannel)
            WireAppChainLocked(appChannel);
        else if (IsInsertChannel(key)) WireInputFeedsLocked();
    }

    /// <summary>
    /// Set one control of an insert. Applied live to the running chain when
    /// possible (no dropout); a chain that cannot take it is rebuilt.
    /// </summary>
    public void SetInsertParam(string channel, string insertId, string symbol, double value)
    {
        lock (_gate)
        {
            if (!_inserts.TryGetValue(channel, out List<InsertDefinition>? list)) return;
            int idx = list.FindIndex(i => i.Id == insertId);
            if (idx < 0) return;
            list[idx].Params[symbol] = value;
            if (!_built || list[idx].Bypass || _insertErrors.ContainsKey(channel)
                || !_chains.TryGetValue(channel, out FilterHandle? chain)) return;
            // The chain names LV2 stages i0, i1, ... in the order of the
            // non-bypassed, loadable inserts, so find this insert's stage index.
            int k = 0;
            for (int j = 0; j < idx; j++)
                if (!list[j].Bypass && list[j].Kind == "lv2" && Lv2Catalog.Find(list[j].Plugin) is not null) k++;
            try { _pw.SetFilterControl(chain, $"i{k}:{symbol}", value); }
            catch (InvalidOperationException) { RewireInsertKeyLocked(channel); }
        }
    }

    /// <summary>Insert chains with live status, for the state push.</summary>
    private Dictionary<string, IReadOnlyList<InsertStatus>> InsertStatusLocked()
    {
        var result = new Dictionary<string, IReadOnlyList<InsertStatus>>();
        foreach ((string channel, List<InsertDefinition> list) in _inserts)
        {
            result[channel] = [.. list.Select(i => new InsertStatus(i,
                Lv2Catalog.Find(i.Plugin) is null ? "plugin not installed"
                : !i.Bypass && _insertErrors.TryGetValue(channel, out string? err) ? err
                : null))];
        }
        return result;
    }

    /// <summary>
    /// Name fragment of the interface whose capture should feed the input
    /// channels (the daemon's active device). A change re-wires the feeds.
    /// </summary>
    public void SetInputDeviceHint(string? hint, bool hardwareOutputRouting = false)
    {
        lock (_gate)
        {
            if (_inputHint == hint && _hardwareOutputRouting == hardwareOutputRouting) return;
            bool routingChanged = _hardwareOutputRouting != hardwareOutputRouting;
            _inputHint = hint;
            _hardwareOutputRouting = hardwareOutputRouting;
            if (_built)
            {
                WireInputFeedsLocked();
                if (routingChanged) WireAuxRouteLocked();
            }
        }
    }

    /// <summary>
    /// Re-wire the hardware input feeds if none are connected (the interface
    /// was absent at build time or was replugged). Returns true when feeds
    /// were (re)established.
    /// </summary>
    public bool EnsureInputFeeds()
    {
        lock (_gate)
        {
            if (!_built) return false;
            // No feeds at all (device absent at build), or feeds whose source
            // node has since vanished (a card profile change renames every
            // node under it): both mean re-resolve the input and re-wire.
            bool broken = _inputFeeds.Count == 0
                || _inputFeeds.Values.Any(f => _pw.EnsureLinks(f) == LinkHealth.Broken);
            if (!broken) return false;
            WireInputFeedsLocked();
            return _inputFeeds.Count > 0;
        }
    }

    /// <summary>Every sink and source the user can pick, real or virtual.</summary>
    public IReadOnlyList<AudioNode> ListDevices()
        => _pw.ListDevices(_hardwareOutputRouting, _inputHint);

    /// <summary>Close and reopen an output device's stream (see adapter).</summary>
    public void BounceOutput(string sinkName) => _pw.BounceSink(sinkName);

    /// <summary>Current user choices, for persisting.</summary>
    public MixerSettings ExportSettings()
    {
        lock (_gate)
        {
            return new MixerSettings
            {
                UserChannels = [.. _config.Channels.Where(c => c.InputPair is null)
                    .Select(c => new UserChannelDefinition(c.Id, c.Name))],
                UserMixes = [.. _config.Mixes.Where(m => m.Kind == MixKind.VirtualMic)
                    .Select(m => new UserMixDefinition(m.Id, m.Name))],
                MixVolumes = new Dictionary<string, double>(_mixVolume),
                MixMuted = [.. _mixMuted],
                Levels = new Dictionary<string, double>(_levels),
                ChannelMuted = [.. _muted],
                MonitorOutputs = [.. _monitorOutputs],
                AppOverrides = new Dictionary<string, string>(Matcher.Overrides),
                KnownApps = [.. _apps.Values.Select(a => new SavedApp(a.Identity, a.Label, a.ChannelId))],
                EnforcedDefaultSink = _enforcedSink,
                EnforcedDefaultSource = _enforcedSource,
                AuxPortEnabled = _auxPortEnabled,
                LowCutHz = _lowCutHz,
                SoftClipGuard = _softClipGuard,
                Inserts = _inserts.ToDictionary(e => e.Key, e => e.Value.ToList()),
            };
        }
    }

    /// <summary>Apply saved choices onto the built graph.</summary>
    public void ApplySettings(MixerSettings s)
    {
        lock (_gate)
        {
            if (!_built) return;

            foreach ((string mixId, double vol) in s.MixVolumes)
                if (_mixVolume.ContainsKey(mixId)) _mixVolume[mixId] = Math.Clamp(vol, 0, 1);
            _mixMuted.Clear();
            foreach (string mixId in s.MixMuted)
                if (_mixVolume.ContainsKey(mixId)) _mixMuted.Add(mixId);

            foreach ((string cell, double lvl) in s.Levels)
                if (_cells.Contains(cell)) _levels[cell] = Math.Clamp(lvl, 0, 1);
            _muted.Clear();
            foreach (string cell in s.ChannelMuted)
                if (_cells.Contains(cell)) _muted.Add(cell);

            var validAppChannels = _config.Channels.Where(c => c.InputPair is null).Select(c => c.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            string fallbackChannel = validAppChannels.FirstOrDefault()
                ?? _config.Channels.FirstOrDefault()?.Id ?? "system";
            Matcher.ClearOverrides();
            foreach ((string identity, string channelId) in s.AppOverrides)
                Matcher.SetOverride(StreamMatcher.MigrateIdentity(Sanitize(identity)),
                    validAppChannels.Contains(channelId) ? channelId : fallbackChannel);

            // A live reconfiguration keeps the registry in memory. Move any
            // app whose channel was removed onto the safe application channel.
            foreach ((string identity, StreamAssignment app) in _apps.ToList())
            {
                string channel = Matcher.Overrides.TryGetValue(identity, out string? assigned)
                    ? assigned
                    : validAppChannels.Contains(app.ChannelId) ? app.ChannelId : fallbackChannel;
                _apps[identity] = app with { ChannelId = channel };
            }

            // Remembered apps come back inactive until a stream appears.
            // Identities saved before the "(deleted)" fix are migrated here so
            // an app does not appear twice after its binary was updated.
            foreach (SavedApp app in s.KnownApps)
            {
                string identity = StreamMatcher.MigrateIdentity(Sanitize(app.Identity));
                if (PipeWireAdapter.IsPlumbingIdentity(identity)) continue;   // pre-filter leftovers
                string channel = validAppChannels.Contains(app.ChannelId) ? app.ChannelId : fallbackChannel;
                if (!_apps.ContainsKey(identity))
                    _apps[identity] = new StreamAssignment(0, 0, Sanitize(app.Label), identity, channel) { Active = false, Running = false };
                else
                    _apps[identity] = _apps[identity] with { ChannelId = channel };
            }

            static string Sanitize(string v) => v.EndsWith(" (deleted)", StringComparison.Ordinal) ? v[..^10] : v;

            foreach (MixDefinition mix in _config.Mixes) ReapplyMixLocked(mix.Id);

            IReadOnlyList<string> savedOutputs = s.MonitorOutputs is { Count: > 0 }
                ? s.MonitorOutputs
                : s.MonitorOutput is not null ? [s.MonitorOutput] : [];
            SetMonitorOutputsLocked(savedOutputs);
            _enforcedSink = s.EnforcedDefaultSink;
            _enforcedSource = s.EnforcedDefaultSource;

            // Migration: before the Aux mix existed, "USB Aux Out" was a
            // monitor destination; carry that intent over once.
            _auxPortEnabled = s.AuxPortEnabled
                ?? (s.MonitorOutputs.Any(o => o.EndsWith("#usbaux", StringComparison.Ordinal)) ||
                    (s.MonitorOutput?.EndsWith("#usbaux", StringComparison.Ordinal) ?? false));
            WireAuxRouteLocked();

            bool rewireInputs = false;
            bool rewireMixes = false;
            if (s.LowCutHz is 80 or 120 && _lowCutHz != s.LowCutHz)
            {
                _lowCutHz = s.LowCutHz;
                rewireInputs = true;
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
                    rewireInputs = true;
                }
            }
            _inserts.Clear();
            foreach ((string channel, List<InsertDefinition> list) in s.Inserts)
                if (IsInsertChannel(channel)) _inserts[channel] = [.. list];
            rewireInputs = true;
            rewireMixes = true;
            if (rewireInputs) WireInputFeedsLocked();
            if (rewireMixes)
                foreach (MixDefinition mix in _config.Mixes) WireMixChainLocked(mix);
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
                ChannelMuted = [.. _muted],
                MonitorOutputs = [.. _monitorOutputs],
                AuxPortEnabled = _auxPortEnabled,
                OutputVolume = _outputVolume,
                LowCutHz = _lowCutHz,
                SoftClipGuard = _softClipGuard,
                Inserts = _inserts.ToDictionary(e => e.Key, e => e.Value.ToList()),
            };
        }
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
            if (!_built) return;

            foreach ((string mixId, double vol) in s.MixVolumes)
                if (_mixVolume.ContainsKey(mixId)) _mixVolume[mixId] = Math.Clamp(vol, 0, 1);
            _mixMuted.Clear();
            foreach (string mixId in s.MixMuted)
                if (_mixVolume.ContainsKey(mixId)) _mixMuted.Add(mixId);

            foreach ((string cell, double lvl) in s.Levels)
                if (_cells.Contains(cell)) _levels[cell] = Math.Clamp(lvl, 0, 1);
            _muted.Clear();
            foreach (string cell in s.ChannelMuted)
                if (_cells.Contains(cell)) _muted.Add(cell);

            foreach (MixDefinition mix in _config.Mixes) ReapplyMixLocked(mix.Id);

            // An empty list is a real scene choice: disconnect every monitor
            // route. Null belongs to an older profile that did not store this
            // field, so preserve the current route for backwards compatibility.
            if (s.MonitorOutputs is not null)
                SetMonitorOutputsLocked(s.MonitorOutputs);
            _auxPortEnabled = s.AuxPortEnabled;
            WireAuxRouteLocked();

            bool rewireInputs = false;
            bool rewireMixes = false;
            if (s.LowCutHz is int hz && hz is 0 or 80 or 120 && _lowCutHz != hz)
            {
                _lowCutHz = hz;
                rewireInputs = true;
            }
            if (s.SoftClipGuard is bool scg && _softClipGuard != scg)
            {
                if (!scg || _pw.GetSoftwareClipGuardAvailability().Available)
                {
                    _softClipGuard = scg;
                    rewireInputs = true;
                }
            }
            if (s.Inserts is not null)
            {
                _inserts.Clear();
                foreach ((string channel, List<InsertDefinition> list) in s.Inserts)
                    if (IsInsertChannel(channel)) _inserts[channel] = [.. list];
                rewireInputs = true;
                rewireMixes = true;
            }
            if (rewireInputs) WireInputFeedsLocked();
            if (rewireMixes)
                foreach (MixDefinition mix in _config.Mixes) WireMixChainLocked(mix);
        }
        if (s.OutputVolume is double v) SetOutputVolume(v);
    }

    /// <summary>Volume of the selected output devices (0..1), applied to each.</summary>
    public void SetOutputVolume(double volume)
    {
        lock (_gate)
        {
            if (_monitorOutputs.Count == 0) return;
            foreach (string sink in _monitorOutputs.Select(StripMarker).Distinct())
            {
                try { _pw.SetSinkVolume(sink, volume); }
                catch (InvalidOperationException) { /* device gone */ }
            }
            _outputVolume = volume;
        }
    }

    private static string StripMarker(string name)
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

    /// <summary>
    /// Re-assert the enforced defaults. WirePlumber auto-switches defaults to
    /// new devices and replays remembered preferences, so a one-time set is not
    /// enough: this runs every sweep, exactly like Wave Link holds its devices.
    /// </summary>
    public bool EnforceDefaults()
    {
        string? sink, source;
        lock (_gate) { sink = _enforcedSink; source = _enforcedSource; }
        bool corrected = false;
        try
        {
            if (sink is not null && _pw.GetDefaultSink() != sink)
            {
                _pw.SetDefaultSink(sink);
                corrected = true;
            }
            if (source is not null && _pw.GetDefaultSource() != source)
            {
                _pw.SetDefaultSource(source);
                corrected = true;
            }
        }
        catch (InvalidOperationException) { /* device currently absent */ }
        return corrected;
    }

    /// <summary>
    /// Refresh the cached device volumes; true when either moved (externally or
    /// through us), so the daemon knows to push fresh state.
    /// </summary>
    public bool SyncDeviceVolumes()
    {
        lock (_gate)
        {
            string? first = _monitorOutputs.FirstOrDefault();
            double? outV = first is null ? null : _pw.GetSinkVolume(first);
            bool changed = Differs(outV, _outputVolume);
            _outputVolume = outV;
            return changed;
        }

        static bool Differs(double? a, double? b)
            => a.HasValue != b.HasValue || (a.HasValue && Math.Abs(a.Value - b!.Value) > 0.005);
    }

    /// <summary>First selected monitor output, or null (legacy single view).</summary>
    public string? MonitorOutput { get { lock (_gate) return _monitorOutputs.FirstOrDefault(); } }

    /// <summary>All selected monitor outputs, in selection order.</summary>
    public IReadOnlyList<string> MonitorOutputs { get { lock (_gate) return [.. _monitorOutputs]; } }

    public bool IsChannelMutedIn(string channelId, string mixId)
    {
        lock (_gate) return _muted.Contains(Cell(channelId, mixId));
    }

    /// <summary>
    /// The Pro's hardware mic path is carrying XLR 1 to the headphone jacks
    /// (or not); the software send for that cell is muted in the graph
    /// while it does, and restored when it stops.
    /// </summary>
    public void SetHardwareMicMonitor(bool on)
    {
        lock (_gate)
        {
            if (_hardwareMicMonitor == on) return;
            _hardwareMicMonitor = on;
            MixDefinition? monitor = _config.Mixes.FirstOrDefault(m => m.Kind == MixKind.Monitor);
            if (_built && monitor is not null) ApplyCellLocked("xlr1", monitor.Id);
        }
    }

    /// <summary>
    /// Restart the Pro sink's playback stream after a headphone-mix change,
    /// which is when the device latches its matrix (same as the aux return).
    /// The monitor routes are dropped for the bounce and rebuilt after it.
    /// </summary>
    public void BounceMonitorHardwareOutput()
    {
        string? sink;
        IReadOnlyList<string> monitorSelection;
        lock (_gate)
        {
            if (!_built) return;
            string? pseudo = _monitorOutputs.FirstOrDefault(o => o.Contains('#'));
            if (pseudo is null) return;
            sink = pseudo[..pseudo.IndexOf('#')];
            monitorSelection = [.. _monitorOutputs];
            foreach (PortLink route in _monitorRoutes.Values) _pw.Unlink(route);
            _monitorRoutes.Clear();
        }
        _pw.BounceSink(sink);
        lock (_gate) SetMonitorOutputsLocked(monitorSelection);
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
        lock (_gate) { if (_built) SetMonitorOutputsLocked(sinkNames); }
    }

    private void SetMonitorOutputsLocked(IReadOnlyList<string> sinkNames)
    {
        foreach (PortLink route in _monitorRoutes.Values) _pw.Unlink(route);
        _monitorRoutes.Clear();
        _monitorOutputs.Clear();
        foreach (string name in sinkNames.Where(n => !string.IsNullOrEmpty(n)).Distinct())
        {
            // The aux port is owned by the Aux mix now; old saved selections
            // that still carry it as a monitor destination are dropped.
            if (name.EndsWith("#usbaux", StringComparison.Ordinal)) continue;
            _monitorOutputs.Add(name);
        }
        MixDefinition? monitor = _config.Mixes.FirstOrDefault(m => m.Kind == MixKind.Monitor);
        if (monitor is null) return;
        (string tapNode, string tapPrefix) = MixTapLocked(monitor);
        foreach ((string key, string target) in MonitorRouteTargetsLocked())
            _monitorRoutes[key] = _pw.RouteTapToOutput(tapNode, tapPrefix, target);
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
            int marker = name.IndexOf('#');
            string key = marker < 0 ? name : name[..marker] + "#bus";
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
            if (!_built || _monitorOutputs.Count == 0) return false;
            MixDefinition? monitor = _config.Mixes.FirstOrDefault(m => m.Kind == MixKind.Monitor);
            if (monitor is null) return false;
            bool changed = false;
            foreach ((string key, string target) in MonitorRouteTargetsLocked())
            {
                PortLink? route = _monitorRoutes.GetValueOrDefault(key);
                if (route is { Pairs.Count: > 0 })
                {
                    LinkHealth health = _pw.EnsureLinks(route);
                    if (health == LinkHealth.Healthy) continue;
                    if (health == LinkHealth.Relinked) { changed = true; continue; }
                    _pw.Unlink(route);   // Broken: the port names themselves are stale
                }
                (string tapNode, string tapPrefix) = MixTapLocked(monitor);
                _monitorRoutes[key] = _pw.RouteTapToOutput(tapNode, tapPrefix, target);
                changed |= _monitorRoutes[key].Pairs.Count > 0;
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

    /// <summary>Mute/unmute one channel within one mix only.</summary>
    public void SetChannelMuted(string channelId, string mixId, bool muted)
    {
        lock (_gate)
        {
            string cell = Cell(channelId, mixId);
            if (!_cells.Contains(cell)) return;
            if (muted) _muted.Add(cell); else _muted.Remove(cell);
            ApplyCellLocked(channelId, mixId);
        }
    }

    /// <summary>Master level for a mix, scaling every channel feeding it.</summary>
    public void SetMixVolume(string mixId, double volume)
    {
        lock (_gate)
        {
            _mixVolume[mixId] = Math.Clamp(volume, 0.0, 1.0);
            ReapplyMixLocked(mixId);
        }
    }

    public void SetMixMuted(string mixId, bool muted)
    {
        lock (_gate)
        {
            if (muted) _mixMuted.Add(mixId); else _mixMuted.Remove(mixId);
            ReapplyMixLocked(mixId);
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

    /// <summary>
    /// Look for application streams and route new ones to their channel. Called
    /// on a timer by the daemon. Returns true when anything changed. Streams the
    /// mixer already placed are left alone so manual overrides survive.
    /// </summary>
    public bool SyncStreams()
    {
        if (!_built) return false;
        IReadOnlyList<AudioStream> live = _pw.ListStreams();
        bool changed = false;

        lock (_gate)
        {
            var seen = new HashSet<int>();
            var liveIdentities = new HashSet<string>();
            foreach (AudioStream s in live)
            {
                seen.Add(s.Id);
                liveIdentities.Add(s.Identity);
                if (_streams.ContainsKey(s.Id)) continue;

                string channelId = Matcher.Match(s);
                ChannelDefinition? ch = _config.Channels.FirstOrDefault(c => c.Id == channelId)
                                        ?? _config.Channels.FirstOrDefault(c => c.InputPair is null)
                                        ?? _config.Channels.FirstOrDefault();
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

                var placed = new StreamAssignment(s.Id, s.Serial, s.Label, s.Identity, ch.Id);
                _streams[s.Id] = placed;
                // Transient plumbing (Wine's probe streams, bare runtime
                // binaries) is routed but never remembered as an app.
                if (!PipeWireAdapter.IsPlumbingIdentity(s.Identity)) _apps[s.Identity] = placed;
                changed = true;
            }

            foreach (int gone in _streams.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                _streams.Remove(gone);
                changed = true;
            }

            // Every running audio-capable app is listed even before it plays:
            // PipeWire clients cover "running", streams cover "playing".
            var runningIdentities = new HashSet<string>();
            foreach (AudioStream client in _pw.ListClients())
            {
                string identity = client.Identity;
                if (PipeWireAdapter.IsPlumbingIdentity(identity)) continue;
                runningIdentities.Add(identity);
                if (!_apps.ContainsKey(identity))
                {
                    string matched = Matcher.Match(client);
                    string channel = _config.Channels.Any(c => c.Id == matched && c.InputPair is null)
                        ? matched
                        : _config.Channels.FirstOrDefault(c => c.InputPair is null)?.Id
                          ?? _config.Channels.FirstOrDefault()?.Id ?? "system";
                    _apps[identity] = new StreamAssignment(0, 0, client.Label, identity,
                        channel)
                    { Active = false, Running = true };
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
            foreach ((string identity, StreamAssignment app) in _apps.ToList())
            {
                bool activeNow = liveIdentities.Contains(identity);
                bool runningNow = activeNow || runningIdentities.Contains(identity);
                if (app.Active != activeNow || app.Running != runningNow)
                {
                    _apps[identity] = app with
                    {
                        Active = activeNow,
                        Running = runningNow,
                        Id = activeNow ? app.Id : 0,
                        Serial = activeNow ? app.Serial : 0,
                    };
                    changed = true;
                }
            }
        }
        return changed;
    }

    /// <summary>
    /// Drop an application from the registry and forget its channel override.
    /// A still-running app simply re-registers on the next sweep.
    /// </summary>
    public void ForgetApp(string identity)
    {
        lock (_gate)
        {
            _apps.Remove(identity);
            Matcher.RemoveOverride(identity);
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
            ChannelDefinition? ch = _config.Channels.FirstOrDefault(c => c.Id == channelId && c.InputPair is null);
            if (ch is null || string.IsNullOrWhiteSpace(identity)) return;
            Matcher.SetOverride(identity, channelId);

            foreach ((int id, StreamAssignment placed) in _streams.ToList())
                if (placed.Identity == identity)
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
                _apps[identity] = new StreamAssignment(0, 0, label ?? identity, identity, channelId) { Active = false, Running = false };
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
            ChannelDefinition? ch = _config.Channels.FirstOrDefault(c => c.Id == channelId);
            if (ch is null) return;

            if (_streams.TryGetValue(streamId, out StreamAssignment? existing))
            {
                _pw.MoveStreamToSink(existing.Serial, ch.SinkName);
                Matcher.SetOverride(existing.Identity, channelId);
                _streams.Remove(streamId); // the next sweep confirms the destination
                return;
            }
            _pw.MoveStreamToSink(streamId, ch.SinkName);
        }
    }

    public MixerState Snapshot()
    {
        lock (_gate)
        {
            DspFeatureAvailability clipGuard = _pw.GetSoftwareClipGuardAvailability();
            return new MixerState
            {
                Mixes = [.. _config.Mixes.Select(m => new MixStatus(
                    m.Id, m.Name,
                    _mixVolume.GetValueOrDefault(m.Id, 1.0),
                    _mixMuted.Contains(m.Id),
                    m.Kind == MixKind.Monitor,
                    m.Kind == MixKind.VirtualMic,
                    m.Kind == MixKind.AuxPort,
                    m.Kind == MixKind.VirtualMic))],
                Channels = [.. _config.Channels.Select(c => new ChannelStatus(
                    c.Id, c.Name,
                    _config.Mixes.ToDictionary(m => m.Id, m => _levels.GetValueOrDefault(Cell(c.Id, m.Id), 0.0)),
                    [.. _config.Mixes.Where(m => _muted.Contains(Cell(c.Id, m.Id))).Select(m => m.Id)],
                    c.InputPair is not null,
                    c.InputPair is null,
                    c.InputPair is null))],
                MonitorOutput = _monitorOutputs.FirstOrDefault(),
                MonitorOutputs = [.. _monitorOutputs],
                OutputVolume = _outputVolume,
                LowCutHz = _lowCutHz,
                SoftClipGuard = _softClipGuard,
                SoftClipGuardAvailable = clipGuard.Available,
                SoftClipGuardError = clipGuard.Error,
                Inserts = InsertStatusLocked(),
                EnforcedDefaultSink = _enforcedSink,
                EnforcedDefaultSource = _enforcedSource,
                AuxPortEnabled = _auxPortEnabled,
                Streams = [.. _apps.Values
                    .OrderByDescending(a => a.Active).ThenBy(a => a.Label, StringComparer.OrdinalIgnoreCase)],
            };
        }
    }

    /// <summary>Cell level x mix master, applied to the combine leg's stream.</summary>
    private void ApplyCellLocked(string channelId, string mixId)
    {
        string cell = Cell(channelId, mixId);
        double level = _levels.GetValueOrDefault(cell, 0.0) * _mixVolume.GetValueOrDefault(mixId, 1.0);
        bool muted = _muted.Contains(cell) || _mixMuted.Contains(mixId);
        if (_hardwareMicMonitor && channelId == "xlr1"
            && _config.Mixes.FirstOrDefault(m => m.Kind == MixKind.Monitor)?.Id == mixId)
            muted = true;   // the hardware direct path carries it to the jacks

        if (!_legIndex.TryGetValue(cell, out int idx)) { DiscoverLegsLocked(); if (!_legIndex.TryGetValue(cell, out idx)) return; }
        try
        {
            _pw.SetSinkInputVolume(idx, level);
            _pw.SetSinkInputMuted(idx, muted);
        }
        catch (InvalidOperationException)
        {
            // The leg's index changed (combine reconnected); rediscover once.
            DiscoverLegsLocked();
            if (_legIndex.TryGetValue(cell, out idx))
            {
                try { _pw.SetSinkInputVolume(idx, level); _pw.SetSinkInputMuted(idx, muted); }
                catch (InvalidOperationException) { /* give up until next change */ }
            }
        }
    }

    private void ReapplyMixLocked(string mixId)
    {
        foreach (ChannelDefinition ch in _config.Channels)
            ApplyCellLocked(ch.Id, mixId);
    }

    private void TearDownLocked()
    {
        _meters.Dispose();
        _meters = new MeterReader();   // Dispose is terminal; a rebuild needs a fresh reader
        foreach (PortLink route in _monitorRoutes.Values) _pw.Unlink(route);
        _monitorRoutes.Clear();
        _monitorOutputs.Clear();
        if (_auxRoute is not null) { _pw.Unlink(_auxRoute); _auxRoute = null; }
        foreach (PortLink feed in _inputFeeds.Values) _pw.Unlink(feed);
        _inputFeeds.Clear();
        RemoveInputChainsLocked();
        RemoveMixChainsLocked();
        foreach (PortLink link in _appFeeds.Values.Concat(_appOutputs.Values)) _pw.Unlink(link);
        _appFeeds.Clear();
        _appOutputs.Clear();
        foreach (FilterHandle chain in _chains.Values) _pw.StopFilter(chain);
        _chains.Clear();
        _inputDevice = null;
        _pw.TearDown();     // unloads modules in reverse order: combines, then mixes
        _combineModules.Clear();
        _channelInputModules.Clear();
        _mixModules.Clear();
        _mixPostModules.Clear();
        _virtualMicModules.Clear();
        _legIndex.Clear();
        _streams.Clear();
        _cells.Clear();
        _levels.Clear();
        _muted.Clear();
        _mixVolume.Clear();
        _mixMuted.Clear();
        _built = false;
    }

    public void TearDown()
    {
        lock (_gate) TearDownLocked();
    }

    public void Dispose()
    {
        TearDown();
        _meters.Dispose();
    }
}
