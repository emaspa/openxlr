namespace OpenXLR.Core.Mixing;

public sealed partial class Mixer
{
    /// <summary>
    /// Add only the new application sink. Readers cannot observe its layout
    /// until persistence succeeds. A failed save removes only the new module.
    /// The caller serializes its save callback with other settings writers.
    /// </summary>
    public void CreateApplicationChannel(string name, Func<MixerSettings, string?> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        name = CleanLayoutName(name, "channel");
        lock (_gate)
        {
            if (!_built) throw new InvalidOperationException("mixer is not built");
            if (_config.Channels.Count(c => c.InputPair is null) >= MixerConfig.MaxApplicationChannels)
                throw new InvalidOperationException("application channel limit reached");
            string id = NewChannelId(name, _config.Channels.Select(c => c.Id));
            var channel = new ChannelDefinition(id, name)
            { Levels = _config.Mixes.ToDictionary(m => m.Id, _ => 1.0) };
            MixerConfig previous = _config;
            uint module = _pw.CreateCombineSink(channel.SinkName,
                _config.Mixes.Select(m => m.SinkName), $"OpenXLR {name}");
            try
            {
                _config = _config with { Channels = [.. _config.Channels, channel] };
                _combineModules[id] = module;
                var legs = _pw.FindCombineLegs(module);
                foreach (var mix in _config.Mixes)
                {
                    string cell = Cell(id, mix.Id);
                    _cells.Add(cell);
                    _levels[cell] = 1.0;
                    if (legs.TryGetValue(mix.SinkName, out int index)) _legIndex[cell] = index;
                    ApplyCellLocked(id, mix.Id);
                }
                if (persist(ExportSettings()) is string error) throw new IOException(error);
            }
            catch (Exception editError)
            {
                _config = previous;
                _combineModules.Remove(id);
                foreach (var mix in previous.Mixes)
                {
                    string cell = Cell(id, mix.Id);
                    _cells.Remove(cell);
                    _levels.Remove(cell);
                    _muted.Remove(cell);
                    _legIndex.Remove(cell);
                }
                try { _pw.UnloadModule(module); }
                catch (Exception cleanupError)
                { throw new AggregateException("channel creation failed and its module could not be removed", editError, cleanupError); }
                throw;
            }
            _meters.Add($"ch:{id}", channel.SinkName);
        }
    }

    /// <summary>
    /// Rename an application channel without touching PipeWire nodes. The stable
    /// id and every routing/profile/controller reference remain unchanged. The
    /// live state uses the new label immediately; the desktop node description
    /// follows on the next normal graph rebuild or daemon restart.
    /// </summary>
    public void RenameApplicationChannel(string id, string name, Func<MixerSettings, string?> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        name = CleanLayoutName(name, "channel");
        lock (_gate)
        {
            if (!_built) throw new InvalidOperationException("mixer is not built");
            int index = _config.Channels.ToList().FindIndex(c => c.Id == id && c.InputPair is null);
            if (index < 0) throw new InvalidOperationException($"'{id}' is not an editable application channel");

            MixerConfig previous = _config;
            var channels = _config.Channels.ToList();
            channels[index] = channels[index] with { Name = name };
            _config = _config with { Channels = channels };
            try
            {
                if (persist(ExportSettings()) is string error) throw new IOException(error);
            }
            catch
            {
                _config = previous;
                throw;
            }
        }
    }

    /// <summary>Rename a virtual microphone mix while preserving its stable id.</summary>
    public void RenameVirtualMix(string id, string name, Func<MixerSettings, string?> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        name = CleanLayoutName(name, "output");
        lock (_gate)
        {
            if (!_built) throw new InvalidOperationException("mixer is not built");
            int index = _config.Mixes.ToList().FindIndex(m => m.Id == id && m.Kind == MixKind.VirtualMic);
            if (index < 0) throw new InvalidOperationException($"'{id}' is not an editable virtual output");

            MixerConfig previous = _config;
            var mixes = _config.Mixes.ToList();
            mixes[index] = mixes[index] with { Name = name };
            _config = _config with { Mixes = mixes };
            try
            {
                if (persist(ExportSettings()) is string error) throw new IOException(error);
            }
            catch
            {
                _config = previous;
                throw;
            }
        }
    }

    /// <summary>
    /// Add a virtual microphone mix. A new matrix column changes every combine
    /// sink, so this deliberately rebuilds the owned graph and restores the old
    /// graph if either construction or the durable save fails.
    /// </summary>
    public void CreateVirtualMix(string name, Func<MixerSettings, string?> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        name = CleanLayoutName(name, "output");
        lock (_gate)
        {
            if (!_built) throw new InvalidOperationException("mixer is not built");
            if (_config.Mixes.Count(m => m.Kind == MixKind.VirtualMic) >= MixerConfig.MaxVirtualMixes)
                throw new InvalidOperationException("virtual output limit reached");

            MixerSettings current = ExportSettings();
            string id = NewLayoutId(name, "output", _config.Mixes.Select(m => m.Id));
            var mixes = current.UserMixes?.ToList()
                ?? _config.Mixes.Where(m => m.Kind == MixKind.VirtualMic)
                    .Select(m => new UserMixDefinition(m.Id, m.Name)).ToList();
            mixes.Add(new UserMixDefinition(id, name));
            RebuildEditableLayoutLocked(current with { UserMixes = mixes }, persist);
        }
    }

    /// <summary>
    /// Delete an application channel. Assigned applications are moved to the
    /// first remaining application channel. The matrix row change rebuilds the
    /// owned graph transactionally.
    /// </summary>
    public void DeleteApplicationChannel(string id, Func<MixerSettings, string?> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        lock (_gate)
        {
            if (!_built) throw new InvalidOperationException("mixer is not built");
            ChannelDefinition? channel = _config.Channels.FirstOrDefault(c => c.Id == id && c.InputPair is null);
            if (channel is null) throw new InvalidOperationException($"'{id}' is not an editable application channel");
            var remaining = _config.Channels.Where(c => c.InputPair is null && c.Id != id).ToList();
            if (remaining.Count == 0) throw new InvalidOperationException("the last application channel cannot be deleted");

            string fallback = remaining[0].Id;
            MixerSettings current = ExportSettings();
            var overrides = current.AppOverrides.ToDictionary(
                entry => entry.Key,
                entry => entry.Value == id ? fallback : entry.Value,
                StringComparer.OrdinalIgnoreCase);
            var apps = current.KnownApps
                .Select(app => app.ChannelId == id ? app with { ChannelId = fallback } : app)
                .ToList();

            RebuildEditableLayoutLocked(current with
            {
                UserChannels = [.. (current.UserChannels ?? []).Where(c => c.Id != id)],
                AppOverrides = overrides,
                KnownApps = apps,
                Levels = current.Levels
                    .Where(entry => !entry.Key.StartsWith(id + "|", StringComparison.Ordinal))
                    .ToDictionary(),
                ChannelMuted = [.. current.ChannelMuted
                    .Where(cell => !cell.StartsWith(id + "|", StringComparison.Ordinal))],
            }, persist);
        }
    }

    /// <summary>Delete a virtual microphone mix and all state owned by that column.</summary>
    public void DeleteVirtualMix(string id, Func<MixerSettings, string?> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        lock (_gate)
        {
            if (!_built) throw new InvalidOperationException("mixer is not built");
            if (!_config.Mixes.Any(m => m.Id == id && m.Kind == MixKind.VirtualMic))
                throw new InvalidOperationException($"'{id}' is not an editable virtual output");

            MixerSettings current = ExportSettings();
            RebuildEditableLayoutLocked(current with
            {
                UserMixes = [.. (current.UserMixes ?? []).Where(m => m.Id != id)],
                MixVolumes = current.MixVolumes.Where(entry => entry.Key != id).ToDictionary(),
                MixMuted = [.. current.MixMuted.Where(mixId => mixId != id)],
                Levels = current.Levels
                    .Where(entry => !entry.Key.EndsWith("|" + id, StringComparison.Ordinal))
                    .ToDictionary(),
                ChannelMuted = [.. current.ChannelMuted
                    .Where(cell => !cell.EndsWith("|" + id, StringComparison.Ordinal))],
                Inserts = current.Inserts
                    .Where(entry => entry.Key != $"mix:{id}")
                    .ToDictionary(entry => entry.Key, entry => entry.Value),
            }, persist);
        }
    }

    /// <summary>
    /// Rebuild an edited row/column and keep the previous graph alive as the
    /// logical rollback target. The new state is reported as successful only
    /// after mixer.json has been written atomically.
    /// </summary>
    private void RebuildEditableLayoutLocked(MixerSettings desired, Func<MixerSettings, string?> persist)
    {
        MixerSettings previous = ExportSettings();
        MixerConfig previousConfig = _config;
        try
        {
            Build(MixerConfig.FromSettings(desired));
            ApplySettings(desired);
            SanitizeRuntimeLayoutLocked();
            SyncStreams();
            if (persist(ExportSettings()) is string error) throw new IOException(error);
        }
        catch (Exception editError)
        {
            Exception? cleanupError = null;
            Exception? restoreError = null;
            try { TearDownLocked(); }
            catch (Exception ex) { cleanupError = ex; }
            try
            {
                Build(previousConfig);
                ApplySettings(previous);
                SanitizeRuntimeLayoutLocked();
                SyncStreams();
            }
            catch (Exception ex) { restoreError = ex; }

            if (restoreError is not null)
                throw new AggregateException("layout edit failed and the previous mixer graph could not be restored",
                    editError, restoreError);
            if (cleanupError is not null)
                throw new AggregateException("layout edit failed; cleanup also reported an error",
                    editError, cleanupError);
            throw;
        }
    }

    /// <summary>Remove stale in-memory references after a structural rebuild.</summary>
    private void SanitizeRuntimeLayoutLocked()
    {
        foreach (string key in _inserts.Keys.Where(key => !IsInsertChannel(key)).ToList())
        {
            _inserts.Remove(key);
            _insertErrors.Remove(key);
        }

        foreach ((string identity, StreamAssignment app) in _apps.ToList())
        {
            string target = app.ChannelId == StreamMatcher.Ignore
                ? StreamMatcher.Ignore
                : Matcher.Overrides.TryGetValue(identity, out string? pinned)
                    ? _config.ResolveApplicationChannel(pinned)
                    : _config.ResolveApplicationChannel(app.ChannelId);
            if (target != app.ChannelId) _apps[identity] = app with { ChannelId = target };
        }
    }

    private static string CleanLayoutName(string name, string kind)
    {
        name = name.Trim();
        if (name.Length is 0 or > 60 || name.Any(char.IsControl))
            throw new InvalidOperationException($"{kind} name must contain 1 to 60 printable characters");
        return name;
    }

    internal static string NewChannelId(string name, IEnumerable<string> existing)
        => NewLayoutId(name, "channel", existing.Append(StreamMatcher.Ignore));

    internal static string NewLayoutId(string name, string prefix, IEnumerable<string> existing)
    {
        string slug = new(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        slug = string.Join('-', slug.Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (slug.Length == 0) slug = prefix;
        if (!char.IsAsciiLetter(slug[0])) slug = prefix + "-" + slug;
        if (slug.Length > 28) slug = slug[..28].TrimEnd('-');
        var used = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        string id = slug;
        for (int n = 2; used.Contains(id); n++) id = $"{slug}-{n}";
        return id;
    }
}
