namespace OpenXLR.Core.Mixing;

/// <summary>
/// Editing the layout while the graph runs. Every operation follows the same
/// contract: the new layout is saved before the command succeeds, a failed
/// save restores the previous layout and reports an error, and nothing that
/// carries audio for an untouched channel or mix is rebuilt. Channel combines
/// feed the mix sinks by name pattern, so a new mix grows one leg in every
/// combine on its own and a removed mix loses them; the channel nodes are
/// never reloaded for a mix change.
/// </summary>
public sealed partial class Mixer
{
    /// <summary>How long a freshly loaded node gets to grow its combine legs.</summary>
    private static readonly TimeSpan LegTimeout = TimeSpan.FromSeconds(3);

    // What one more node costs pipewire-pulse in open files, measured on
    // PipeWire 1.6: a mix with nine channels took 118, an application channel
    // with five mixes 74. The estimate runs a quarter above that, plus a
    // reserve for the pactl client connections the change itself makes.
    private const int FilesPerStream = 10;
    private const int FilesPerNode = 10;
    private const int FileReserve = 16;

    /// <summary>The state warning when pipewire-pulse runs close to its limit, or null.</summary>
    public string? PulseFileWarning()
    {
        if (_pw.PulseFileUsage() is not (int used, int limit)) return null;
        return used * 4 >= limit * 3
            ? $"pipewire-pulse has {used} of its {limit} open files in use; past the limit it drops audio nodes. Raise the limit with the pipewire-pulse drop-in OpenXLR installs and restart pipewire-pulse."
            : null;
    }

    /// <summary>Refuse a change that would push pipewire-pulse over its open-file limit.</summary>
    private void EnsurePulseHeadroomLocked(int newStreams, int newNodes)
    {
        if (_pw.PulseFileUsage() is not (int used, int limit)) return;
        int needed = newStreams * FilesPerStream + newNodes * FilesPerNode + FileReserve;
        if (used + needed <= limit) return;
        throw new InvalidOperationException(
            $"pipewire-pulse is near its open-file limit ({used} of {limit} in use, about {needed} more needed); past it the server drops audio nodes. Raise the limit with the pipewire-pulse drop-in OpenXLR installs and restart pipewire-pulse.");
    }

    /// <summary>
    /// Add one application channel: only its combine sink is loaded. It starts
    /// muted in every mix, so a new channel is silent until the user opens a
    /// send. Readers cannot observe the channel until the save succeeded.
    /// </summary>
    public void CreateApplicationChannel(string name, Func<MixerSettings, string?> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        name = CleanName(name);
        lock (_gate)
        {
            if (!_built) throw new InvalidOperationException("mixer is not built");
            if (_config.Channels.Count(c => c.InputPair is null) >= MixerConfig.MaxApplicationChannels)
                throw new InvalidOperationException("application channel limit reached");
            EnsurePulseHeadroomLocked(newStreams: _config.Mixes.Count, newNodes: 2);
            string id = NewChannelId(name, _config.Channels.Select(c => c.Id));
            var channel = new ChannelDefinition(id, name)
            {
                Levels = _config.Mixes.ToDictionary(m => m.Id, _ => 1.0),
                MutedIn = _config.Mixes.Select(m => m.Id).ToHashSet(),
            };
            MixerConfig previous = _config;
            uint module = _pw.CreateCombineSink(channel.SinkName, MixSinkPattern, $"OpenXLR {name}");
            try
            {
                _config = _config with { Channels = [.. _config.Channels, channel] };
                _combineModules[id] = module;
                foreach (MixDefinition mix in _config.Mixes)
                {
                    string cell = Cell(id, mix.Id);
                    _cells.Add(cell);
                    _levels[cell] = 1.0;
                    _muted.Add(cell);
                }
                WaitForLegsLocked([module], _config.Mixes.Select(m => m.SinkName));
                foreach (MixDefinition mix in _config.Mixes) ApplyCellLocked(id, mix.Id);
                PersistLocked(persist);
            }
            catch (Exception editError)
            {
                _config = previous;
                _combineModules.Remove(id);
                RemoveChannelCellsLocked(id, previous.Mixes);
                try { _pw.UnloadModule(module); }
                catch (Exception cleanupError)
                { throw new AggregateException("channel creation failed and its module could not be removed", editError, cleanupError); }
                throw;
            }
            _meters.Add($"ch:{id}", channel.SinkName);
        }
    }

    /// <summary>
    /// Rename an application channel. The name is saved first; then the
    /// channel's sink is reloaded under the new description so desktop
    /// applets show it right away. Streams playing into the channel are put
    /// back on it afterwards (PipeWire parks them on the default output while
    /// the sink is away), so apps on that channel hear a short gap and every
    /// other channel is untouched.
    /// </summary>
    public void RenameApplicationChannel(string id, string name, Func<MixerSettings, string?> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        name = CleanName(name);
        lock (_gate)
        {
            if (!_built) throw new InvalidOperationException("mixer is not built");
            ChannelDefinition channel = _config.Channels.FirstOrDefault(c => c.Id == id && c.InputPair is null)
                ?? throw new InvalidOperationException($"'{id}' is not an application channel");
            if (channel.Name == name) return;
            EnsurePulseHeadroomLocked(newStreams: 0, newNodes: 1);   // the reload frees the old sink first
            MixerConfig previous = _config;
            _config = _config.WithChannelName(id, name);
            try { PersistLocked(persist); }
            catch { _config = previous; throw; }

            ReloadChannelSinkLocked(channel with { Name = name }, channel.Name);
        }
    }

    /// <summary>
    /// Remove an application channel. Apps routed to it move to the first
    /// remaining application channel, remembered assignments included; the
    /// removal is saved, then the channel's sink is unloaded. The last
    /// application channel cannot go: new streams need a destination.
    /// </summary>
    public void DeleteApplicationChannel(string id, Func<MixerSettings, string?> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        lock (_gate)
        {
            if (!_built) throw new InvalidOperationException("mixer is not built");
            MixerConfig previous = _config;
            MixerConfig next = _config.WithoutChannel(id);
            ChannelDefinition fallback = next.Channels.First(c => c.InputPair is null);

            var movedOverrides = Matcher.Overrides.Where(kv => kv.Value == id).Select(kv => kv.Key).ToList();
            var movedApps = _apps.Where(kv => kv.Value.ChannelId == id).ToDictionary(kv => kv.Key, kv => kv.Value);
            CellSnapshot cells = TakeChannelCellsLocked(id, previous.Mixes);
            _config = next;
            foreach (string identity in movedOverrides) Matcher.SetOverride(identity, fallback.Id);
            foreach ((string identity, StreamAssignment app) in movedApps) _apps[identity] = app with { ChannelId = fallback.Id };
            try { PersistLocked(persist); }
            catch
            {
                _config = previous;
                cells.Restore(this);
                foreach (string identity in movedOverrides) Matcher.SetOverride(identity, id);
                foreach ((string identity, StreamAssignment app) in movedApps) _apps[identity] = app;
                throw;
            }

            // Whatever plays into the sink right now moves to the fallback:
            // the registry's view and PipeWire's own (the session manager
            // may have moved a stream since the last sweep).
            ChannelDefinition gone = previous.Channels.First(c => c.Id == id);
            var serials = new HashSet<int>(_pw.StreamSerialsOnSink(gone.SinkName));
            foreach ((int streamId, StreamAssignment placed) in _streams.Where(kv => kv.Value.ChannelId == id).ToList())
            {
                serials.Add(placed.Serial);
                _streams.Remove(streamId);   // the next sweep confirms the destination
            }
            foreach (int serial in serials)
            {
                try { _pw.MoveStreamToSink(serial, fallback.SinkName); }
                catch (InvalidOperationException) { /* the stream ended meanwhile */ }
            }
            _meters.Remove($"ch:{id}");
            _inserts.Remove(id);
            _insertErrors.Remove(id);
            if (_combineModules.Remove(id, out uint module))
            {
                try { _pw.UnloadModule(module); }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"the channel was removed, but its PipeWire sink could not be unloaded ({ex.Message}); it disappears when the daemon restarts");
                }
            }
        }
    }

    /// <summary>
    /// Add a virtual microphone. Its mix sink is loaded first and every channel
    /// combine grows a leg into it by itself; those legs are muted before the
    /// capture device is published, so nothing reaches the new microphone until
    /// the user opens a send. The layout is saved before the command succeeds.
    /// </summary>
    public void CreateVirtualMix(string name, Func<MixerSettings, string?> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        name = CleanName(name);
        lock (_gate)
        {
            if (!_built) throw new InvalidOperationException("mixer is not built");
            EnsurePulseHeadroomLocked(newStreams: _config.Channels.Count, newNodes: 4);
            string id = MixerConfig.NewId(name, "mix", _config.Mixes.Select(m => m.Id));
            var mix = new MixDefinition(id, name, MixKind.VirtualMic);
            MixerConfig previous = _config;
            MixerConfig next = _config.WithMix(mix);   // validates the limit before any node is loaded
            string key = MixKey(mix);
            try
            {
                _mixModules[id] = _pw.CreateNullSink(mix.SinkName, $"OpenXLR {name}");
                _config = next;   // leg discovery and the cell faders go by the mix list
                _mixVolume[id] = 1.0;
                foreach (ChannelDefinition ch in _config.Channels)
                {
                    string cell = Cell(ch.Id, id);
                    _cells.Add(cell);
                    _levels[cell] = 1.0;
                    _muted.Add(cell);
                }
                WaitForLegsLocked([.. _combineModules.Values], [mix.SinkName]);
                foreach (ChannelDefinition ch in _config.Channels) ApplyCellLocked(ch.Id, id);
                _postModules[id] = _pw.CreateNullSink(mix.PostSinkName, $"OpenXLR {name} (post)");
                _virtualMicModules[id] = _pw.CreateVirtualMic(mix.VirtualMicName, $"{mix.PostSinkName}.monitor", $"OpenXLR {name}");
                WireMixChainLocked(mix);
                PersistLocked(persist);
            }
            catch (Exception editError)
            {
                _config = previous;
                RemoveMixChainLocked(key);
                RemoveMixCellsLocked(id);
                var cleanupErrors = new List<Exception>();
                foreach (var modules in new[] { _virtualMicModules, _postModules, _mixModules })
                    if (modules.Remove(id, out uint module))
                    {
                        try { _pw.UnloadModule(module); }
                        catch (Exception ex) { cleanupErrors.Add(ex); }
                    }
                if (cleanupErrors.Count > 0)
                    throw new AggregateException("mix creation failed and its nodes could not all be removed", [editError, .. cleanupErrors]);
                throw;
            }
            _meters.Add($"mix:{id}", mix.SinkName);
        }
    }

    /// <summary>
    /// Rename a virtual microphone. Only the layout changes: reloading the
    /// capture device would throw every app recording from it onto another
    /// source (verified: a recorder does not come back to a reloaded device
    /// of the same name), so the PipeWire description keeps the old name
    /// until the daemon restarts and the state says so.
    /// </summary>
    public void RenameVirtualMix(string id, string name, Func<MixerSettings, string?> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        name = CleanName(name);
        lock (_gate)
        {
            if (!_built) throw new InvalidOperationException("mixer is not built");
            MixDefinition mix = _config.Mixes.FirstOrDefault(m => m.Id == id && m.Kind == MixKind.VirtualMic)
                ?? throw new InvalidOperationException($"'{id}' is not a virtual microphone");
            if (mix.Name == name) return;
            MixerConfig previous = _config;
            _config = _config.WithMixName(id, name);
            try { PersistLocked(persist); }
            catch { _config = previous; throw; }
            _renamedSinceBuild = true;
        }
    }

    /// <summary>
    /// Remove a virtual microphone: its insert chain, capture device, post
    /// sink and mix sink go, and the channel combines drop their legs into it
    /// on their own. Saved before the command succeeds.
    /// </summary>
    public void DeleteVirtualMix(string id, Func<MixerSettings, string?> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        lock (_gate)
        {
            if (!_built) throw new InvalidOperationException("mixer is not built");
            MixDefinition mix = _config.Mixes.FirstOrDefault(m => m.Id == id && m.Kind == MixKind.VirtualMic)
                ?? throw new InvalidOperationException($"'{id}' is not a virtual microphone");
            string key = MixKey(mix);
            MixerConfig previous = _config;
            _config = _config.WithoutMix(id);
            _inserts.Remove(key, out List<InsertDefinition>? savedInserts);
            string? previousSource = _enforcedSource;
            if (_enforcedSource == mix.VirtualMicName) _enforcedSource = null;
            CellSnapshot cells = TakeMixCellsLocked(id);
            try { PersistLocked(persist); }
            catch
            {
                _config = previous;
                cells.Restore(this);
                if (savedInserts is not null) _inserts[key] = savedInserts;
                _enforcedSource = previousSource;
                throw;
            }

            RemoveMixChainLocked(key);
            _meters.Remove($"mix:{id}");
            var errors = new List<string>();
            foreach (var modules in new[] { _virtualMicModules, _postModules, _mixModules })
                if (modules.Remove(id, out uint module))
                {
                    try { _pw.UnloadModule(module); }
                    catch (Exception ex) { errors.Add(ex.Message); }
                }
            if (errors.Count > 0)
                throw new InvalidOperationException(
                    $"the mix was removed, but a PipeWire node could not be unloaded ({string.Join("; ", errors)}); it disappears when the daemon restarts");
        }
    }

    /// <summary>Save an order change without touching any PipeWire node or link.</summary>
    public void SetLayoutOrder(IReadOnlyList<string> channels, IReadOnlyList<string> mixes,
        Func<MixerSettings, string?> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        lock (_gate)
        {
            if (!_built) throw new InvalidOperationException("mixer is not built");
            MixerConfig previous = _config;
            _config = _config.WithOrder(channels, mixes);
            try { PersistLocked(persist); }
            catch { _config = previous; throw; }
        }
    }

    internal static string NewChannelId(string name, IEnumerable<string> existing)
        => MixerConfig.NewId(name, "channel", existing.Append(StreamMatcher.Ignore));

    private static string CleanName(string name)
    {
        name = name?.Trim() ?? "";
        if (name.Length is 0 or > 60 || name.Any(char.IsControl))
            throw new InvalidOperationException("name must contain 1 to 60 printable characters");
        return name;
    }

    private void PersistLocked(Func<MixerSettings, string?> persist)
    {
        if (persist(ExportSettings()) is string error) throw new IOException(error);
    }

    /// <summary>
    /// Reload one channel's combine sink under its current name and put the
    /// streams that played into it back. If the reload fails the sink comes
    /// back under its old description, so audio keeps flowing either way.
    /// </summary>
    private void ReloadChannelSinkLocked(ChannelDefinition channel, string previousName)
    {
        string id = channel.Id;
        var onIt = new HashSet<int>(_pw.StreamSerialsOnSink(channel.SinkName));
        foreach ((int streamId, StreamAssignment placed) in _streams.Where(kv => kv.Value.ChannelId == id).ToList())
        {
            onIt.Add(placed.Serial);
            _streams.Remove(streamId);   // the next sweep confirms the destination
        }
        string oldDescription = $"OpenXLR {previousName}";
        _meters.Remove($"ch:{id}");
        if (_combineModules.Remove(id, out uint old)) _pw.UnloadModule(old);
        uint fresh;
        try { fresh = _pw.CreateCombineSink(channel.SinkName, MixSinkPattern, $"OpenXLR {channel.Name}"); }
        catch (Exception renameError)
        {
            try { fresh = _pw.CreateCombineSink(channel.SinkName, MixSinkPattern, oldDescription); }
            catch (Exception restoreError)
            { throw new AggregateException("the channel's sink could not be reloaded or restored", renameError, restoreError); }
            FinishReloadLocked(channel, fresh, onIt);
            throw new InvalidOperationException(
                $"the name was saved, but the PipeWire sink could not be reloaded ({renameError.Message}); other apps see the new name after a daemon restart");
        }
        FinishReloadLocked(channel, fresh, onIt);
    }

    private void FinishReloadLocked(ChannelDefinition channel, uint module, IEnumerable<int> streamSerials)
    {
        _combineModules[channel.Id] = module;
        WaitForLegsLocked([module], _config.Mixes.Select(m => m.SinkName));
        foreach (MixDefinition mix in _config.Mixes) ApplyCellLocked(channel.Id, mix.Id);
        foreach (int serial in streamSerials)
        {
            try { _pw.MoveStreamToSink(serial, channel.SinkName); }
            catch (InvalidOperationException) { /* the stream ended meanwhile */ }
        }
        _meters.Add($"ch:{channel.Id}", channel.SinkName);
    }

    /// <summary>
    /// A combine loaded with a name pattern grows its legs as the registry
    /// scan reports the matching sinks, a moment after the load returns. Wait
    /// for the named ones, then refresh the leg table.
    /// </summary>
    private void WaitForLegsLocked(IReadOnlyList<uint> modules, IEnumerable<string> sinkNames)
    {
        var wanted = sinkNames.ToHashSet(StringComparer.Ordinal);
        var pending = modules.ToHashSet();
        var deadline = DateTime.UtcNow + LegTimeout;
        while (pending.Count > 0 && DateTime.UtcNow < deadline)
        {
            foreach (uint module in pending.ToList())
                if (_pw.TryFindCombineLegs(module) is { } legs && wanted.All(legs.ContainsKey)) pending.Remove(module);
            if (pending.Count > 0) Thread.Sleep(100);
        }
        DiscoverLegsLocked();
    }

    /// <summary>The fader state of removed cells, so a failed save can put them back.</summary>
    private sealed record CellSnapshot(Dictionary<string, double> Levels, HashSet<string> Muted, Dictionary<string, int> Legs,
        double? MixVolume, bool MixMuted, string? MixId)
    {
        public void Restore(Mixer m)
        {
            foreach ((string cell, double level) in Levels) { m._cells.Add(cell); m._levels[cell] = level; }
            foreach (string cell in Muted) m._muted.Add(cell);
            foreach ((string cell, int index) in Legs) m._legIndex[cell] = index;
            if (MixId is not null && MixVolume is double volume) m._mixVolume[MixId] = volume;
            if (MixId is not null && MixMuted) m._mixMuted.Add(MixId);
        }
    }

    private CellSnapshot TakeChannelCellsLocked(string channelId, IEnumerable<MixDefinition> mixes)
        => TakeCellsLocked(mixes.Select(mix => Cell(channelId, mix.Id)).ToList(), null);

    private CellSnapshot TakeMixCellsLocked(string mixId)
        => TakeCellsLocked(_cells.Where(c => c.EndsWith("|" + mixId, StringComparison.Ordinal)).ToList(), mixId);

    private CellSnapshot TakeCellsLocked(List<string> cells, string? mixId)
    {
        var snapshot = new CellSnapshot([], [], [],
            mixId is not null && _mixVolume.TryGetValue(mixId, out double volume) ? volume : null,
            mixId is not null && _mixMuted.Contains(mixId), mixId);
        foreach (string cell in cells)
        {
            if (_cells.Remove(cell)) snapshot.Levels[cell] = _levels.GetValueOrDefault(cell, 0.0);
            _levels.Remove(cell);
            if (_muted.Remove(cell)) snapshot.Muted.Add(cell);
            if (_legIndex.Remove(cell, out int index)) snapshot.Legs[cell] = index;
        }
        if (mixId is not null) { _mixVolume.Remove(mixId); _mixMuted.Remove(mixId); }
        return snapshot;
    }

    private void RemoveChannelCellsLocked(string channelId, IEnumerable<MixDefinition> mixes)
    {
        foreach (MixDefinition mix in mixes)
        {
            string cell = Cell(channelId, mix.Id);
            _cells.Remove(cell);
            _levels.Remove(cell);
            _muted.Remove(cell);
            _legIndex.Remove(cell);
        }
    }

    private void RemoveMixCellsLocked(string mixId)
    {
        foreach (string cell in _cells.Where(c => c.EndsWith("|" + mixId, StringComparison.Ordinal)).ToList())
        {
            _cells.Remove(cell);
            _levels.Remove(cell);
            _muted.Remove(cell);
            _legIndex.Remove(cell);
        }
        _mixVolume.Remove(mixId);
        _mixMuted.Remove(mixId);
    }

    /// <summary>Take down one mix's insert chain and the links that read the mix.</summary>
    private void RemoveMixChainLocked(string key)
    {
        if (_mixTaps.Remove(key, out PortLink? tap)) _pw.Unlink(tap);
        if (_mixPostLinks.Remove(key, out PortLink? post)) _pw.Unlink(post);
        if (_chains.Remove(key, out FilterHandle? chain)) _pw.StopFilter(chain);
        _insertErrors.Remove(key);
    }
}
