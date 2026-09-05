using OpenXLR.Core.Mixing;

namespace OpenXLR.Daemon;

/// <summary>
/// Owns the submix graph for the daemon's lifetime: builds it on startup, routes
/// the monitor mix to a physical output, applies client commands, and tears the
/// graph down on shutdown so no nodes are left behind.
///
/// Building is opt-in via configuration so the daemon can run device-only (the
/// graph adds sinks to the user's audio setup, which shouldn't happen by
/// surprise). Set OPENXLR_BUILD_MIXER=1, or pass --mixer.
/// </summary>
public sealed class MixerService : IHostedService, IDisposable
{
    private readonly ILogger<MixerService> _log;
    private readonly IConfiguration _config;
    private readonly DeviceManager _devices;
    private readonly Mixer _mixer = new();
    private Timer? _streamSweep;
    private Timer? _saveDebounce;
    private Timer? _meterPush;
    // System.Threading.Timer fires a new callback every period regardless of
    // whether the previous one finished. If one tick runs long (a slow
    // PipeWire round-trip, e.g. spawning a filter-chain module), overlapping
    // ticks pile up, and since Mixer's internal lock gives no fairness
    // guarantee, a steady stream of these freshly-arriving threads can starve
    // out an unrelated waiter (e.g. a client's Snapshot() call, or
    // EnsureInputFeeds() itself) indefinitely. Skip a tick outright rather
    // than let it stack up.
    private int _sweepRunning;
    private string? _lastSweepError;
    private readonly object _layoutGate = new();

    public MixerService(ILogger<MixerService> log, IConfiguration config, DeviceManager devices)
    {
        _log = log;
        _config = config;
        _devices = devices;
    }

    /// <summary>
    /// Keep the Wave XLR Pro's physical-output selectors in step with the
    /// monitor choice: picking one of its pseudo-outputs ("...#hp1" etc.)
    /// enables exactly that output; any other monitor target disables all four
    /// (the Wave Link model: the monitor destination owns the selectors).
    /// Re-asserted on every sweep, since the device only honours them while
    /// connected.
    /// </summary>
    private bool _prevAuxDesired;

    private void SyncOutputSelectors()
    {
        var suffixes = new HashSet<string>();
        string? anyProOutput = null;
        foreach (string output in _mixer.MonitorOutputs)
        {
            int marker = output.IndexOf('#');
            if (marker < 0) continue;
            suffixes.Add(output[(marker + 1)..]);
            anyProOutput = output;
        }
        // The USB Aux port follows the Aux mix, not the monitor selection.
        bool auxDesired = _mixer.AuxPortEnabled;
        _devices.EnsureOutputSelectors(
            hp1: suffixes.Contains("hp1"), hp2: suffixes.Contains("hp2"),
            usbAux: auxDesired, lineOut: suffixes.Contains("lineout"));

        // The device latches aux-return routing at playback-stream start, so a
        // freshly enabled aux output needs the stream bounced once. The bounce
        // needs any address on the physical sink; derive one from a Pro
        // pseudo-output or fall back to the aux route's own target.
        if (auxDesired && !_prevAuxDesired && _mixer.Built)
            _mixer.BounceAuxTarget();
        _prevAuxDesired = auxDesired;
        _ = anyProOutput;

        // The Pro's headphone mix (issue #8). The jacks only hear the Monitor
        // stream while USB return pair 2/3 is a member, which Wave Link on
        // Windows may have cleared, so it is asserted whenever a jack is the
        // monitor output. The mic's zero-latency hardware path follows the
        // XLR 1 -> Monitor send's mute, but only while every monitor output is
        // a Pro jack: with another device in the set the software send has
        // to carry the mic there, and the hardware path would double it.
        bool anyJack = suffixes.Contains("hp1") || suffixes.Contains("hp2") || suffixes.Contains("lineout");
        bool jacksOnly = anyJack && _mixer.MonitorOutputs.All(o => o.Contains('#'));
        bool micDirect = jacksOnly && !_mixer.IsChannelMutedIn("xlr1", "monitor");
        _mixer.SetHardwareMicMonitor(micDirect);
        if (anyJack && _devices.EnsureHeadphoneMix(monitorReturn: true, micDirect: micDirect) && _mixer.Built)
            _mixer.BounceMonitorHardwareOutput();
    }

    /// <summary>Raised when mixer state changes, so the hub can broadcast.</summary>
    public event Action? Changed;

    /// <summary>Null until the graph is built.</summary>
    public MixerState? Snapshot() => _mixer.Built ? _mixer.Snapshot() : null;

    /// <summary>Selectable sinks and sources, or null when the mixer is off.</summary>
    public IReadOnlyList<AudioNode>? Devices() => _mixer.Built ? _mixer.ListDevices() : null;

    /// <summary>Live stereo levels, or null when the mixer is off.</summary>
    public IReadOnlyDictionary<string, double[]>? Meters() => _mixer.Built ? _mixer.ReadMeters() : null;

    /// <summary>Raised at the meter refresh rate so the hub can push levels.</summary>
    public event Action? MetersUpdated;

    /// <summary>Whether this run builds the submixer at all.</summary>
    public bool SubmixerEnabled { get; private set; }

    /// <summary>Whether the submix graph is up (built a few seconds after start).</summary>
    public bool Built => _mixer.Built;

    public Task StartAsync(CancellationToken ct)
    {
        // Microsoft.Extensions.Configuration treats a bare command-line
        // switch as an empty value, so GetValue<bool> alone does not honour
        // the documented `--mixer` form. Accept it explicitly as well as
        // `--mixer=true` and the packaged service environment variable.
        bool mixerSwitch = HasBareMixerSwitch(Environment.GetCommandLineArgs());
        bool launchDefault = mixerSwitch || _config.GetValue("mixer", false) ||
                             Environment.GetEnvironmentVariable("OPENXLR_BUILD_MIXER") == "1";
        bool wanted = OpenXLR.Core.DaemonSettings.SubmixerEnabled(launchDefault);
        SubmixerEnabled = wanted;
        if (!wanted)
        {
            _log.LogInformation("submixer off (daemon.json, --mixer, or OPENXLR_BUILD_MIXER=1 turn it on); hardware control only");
            return Task.CompletedTask;
        }
        OpenXLR.Core.Mixing.Lv2Catalog.Warm();   // plugin inserts: scan LV2 bundles off the startup path

        // Optional: the physical sink the monitor mix feeds. Without it the
        // monitor mix exists but isn't routed anywhere audible.
        string? output = _config.GetValue<string>("monitorOutput")
                         ?? Environment.GetEnvironmentVariable("OPENXLR_MONITOR_OUTPUT");

        // WirePlumber auto-switches the system defaults to newly created sinks
        // and sources, asynchronously, some time after they appear. Remember
        // what the user had so it can be defended after the graph settles.
        string? defaultSinkBefore = null, defaultSourceBefore = null;
        try
        {
            defaultSinkBefore = Run("pactl", "get-default-sink");
            defaultSourceBefore = Run("pactl", "get-default-source");
        }
        catch (Exception) { /* best effort */ }

        try
        {
            MixerSettings? saved = MixerSettings.Load();
            _mixer.Build(MixerConfig.FromSettings(saved), output);

            // Restore the user's saved layout, levels, mutes, device picks, and
            // per-app assignments. Env vars, when set, still win for picks.
            if (saved is not null)
            {
                _mixer.ApplySettings(saved.WithMonitorOverride(output));
                _log.LogInformation("restored settings from {path}", MixerSettings.DefaultPath);
            }
            SyncOutputSelectors();

            _log.LogInformation("submix graph built ({mixes} mixes, {channels} channels){route}",
                _mixer.Config.Mixes.Count, _mixer.Config.Channels.Count,
                output is null ? "" : $", monitor -> {output}");

            // Sweep for new application streams and route them to their channel.
            // One second is responsive enough that audio lands in the right place
            // before a user notices, without polling the graph hard.
            _streamSweep = new Timer(_ =>
            {
                if (Interlocked.CompareExchange(ref _sweepRunning, 1, 0) != 0) return;
                try
                {
                    // Channel feeds follow the actively driven interface; the
                    // node name contains the model with underscores for spaces.
                    _mixer.SetInputDeviceHint(
                        _devices.ActiveInfo?.Model.Replace(' ', '_'),
                        _devices.ActiveCapabilities?.OutputRouting ?? false);
                    // Software DSP only for devices without the hardware version.
                    _mixer.SetLowCutApplicable(!(_devices.ActiveCapabilities?.LowCut ?? false));
                    _mixer.SetClipGuardApplicable(!(_devices.ActiveCapabilities?.ClipGuard ?? false));
                    if (_mixer.SyncStreams() | _mixer.SyncDeviceVolumes() | _mixer.EnforceDefaults()
                        | _mixer.EnsureInputFeeds() | _mixer.EnsureAuxRoute()
                        | _mixer.EnsureFilterRoutes()
                        | _mixer.EnsureMonitorRoutes()) Changed?.Invoke();
                    SyncOutputSelectors();
                    if (_lastSweepError is not null)
                    {
                        _lastSweepError = null;
                        _log.LogInformation("stream sweep recovered");
                    }
                }
                catch (Exception ex)
                {
                    // A wiring failure repeats every second (a filter chain
                    // that cannot be built leaves the microphone unwired, with
                    // no other trace at the default log level). Say it once
                    // per distinct message at warning so it reaches the
                    // journal and the diagnostics archive, then keep quiet.
                    if (ex.Message != _lastSweepError)
                    {
                        _lastSweepError = ex.Message;
                        _log.LogWarning("stream sweep: {msg} (repeats logged at debug level)", ex.Message);
                    }
                    else _log.LogDebug("stream sweep: {msg}", ex.Message);
                }
                finally { Volatile.Write(ref _sweepRunning, 0); }
            }, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));

            // Meters refresh far more often than state; 15 Hz looks smooth
            // without flooding clients.
            _meterPush = new Timer(_ => MetersUpdated?.Invoke(), null,
                TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(66));

            Changed?.Invoke();

            // Defend the defaults after WirePlumber's delayed auto-switch. Two
            // passes because the switch can land seconds after node creation.
            (string? enfSink, string? enfSource) = _mixer.EnforcedDefaults;
            string? wantSink = enfSink ?? defaultSinkBefore;
            string? wantSource = enfSource ?? defaultSourceBefore;
            _ = Task.Run(async () =>
            {
                foreach (int delayMs in new[] { 2000, 5000, 10000, 20000 })
                {
                    await Task.Delay(delayMs);
                    try
                    {
                        if (wantSink is { Length: > 0 } && Run("pactl", "get-default-sink") != wantSink)
                            Run("pactl", "set-default-sink", wantSink);
                        if (wantSource is { Length: > 0 } && Run("pactl", "get-default-source") != wantSource)
                            Run("pactl", "set-default-source", wantSource);
                    }
                    catch (Exception ex) { _log.LogDebug("default defense: {msg}", ex.Message); }
                }
            });
        }
        catch (Exception ex)
        {
            _log.LogError("failed to build submix graph: {msg}", ex.Message);
            // A partial graph is worse than none: half the sinks exist but no
            // routing, and a later rebuild would double up. Remove what was made.
            try { _mixer.TearDown(); } catch (Exception) { /* best effort */ }
        }
        return Task.CompletedTask;
    }

    internal static bool HasBareMixerSwitch(IEnumerable<string> args)
        => args.Any(a => a.Equals("--mixer", StringComparison.OrdinalIgnoreCase));

    private static string Run(string exe, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe)
            { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        Task<string> outTask = p.StandardOutput.ReadToEndAsync();
        Task<string> errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(3000))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"{exe} timed out after 3 seconds");
        }
        string err = errTask.GetAwaiter().GetResult();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{exe} failed: {err.Trim()}");
        return outTask.GetAwaiter().GetResult().Trim();
    }

    public Task StopAsync(CancellationToken ct)
    {
        _streamSweep?.Dispose();
        _streamSweep = null;
        _meterPush?.Dispose();
        _meterPush = null;
        if (_mixer.Built)
        {
            try { _mixer.ExportSettings().Save(); } catch (Exception) { /* best effort */ }
            _mixer.TearDown();
            _log.LogInformation("submix graph torn down");
        }
        return Task.CompletedTask;
    }

    /// <summary>Apply a mixer command. Returns null on success, else an error.</summary>
    public string? Apply(Command cmd)
    {
        if (!_mixer.Built) return "mixer not built (start the daemon with --mixer)";
        string? invalid = CommandValidation.Check(cmd, _mixer, OpenXLR.Core.Mixing.Lv2Catalog.Find);
        if (invalid is not null) return invalid;
        bool layoutChanged = cmd.Cmd is "createChannel" or "renameChannel" or "deleteChannel"
            or "createMix" or "renameMix" or "deleteMix";
        bool layoutLockTaken = false;
        try
        {
            if (layoutChanged) Monitor.Enter(_layoutGate, ref layoutLockTaken);
            switch (cmd.Cmd)
            {
                case "setLevel":
                    if (cmd.Channel is null || cmd.Mix is null) return "setLevel: need 'channel' and 'mix'";
                    _mixer.SetLevel(cmd.Channel, cmd.Mix, cmd.Value.GetDouble());
                    break;
                case "setChannelMuted":
                    if (cmd.Channel is null || cmd.Mix is null) return "setChannelMuted: need 'channel' and 'mix'";
                    _mixer.SetChannelMuted(cmd.Channel, cmd.Mix, cmd.Value.GetBoolean());
                    SyncOutputSelectors();   // the XLR 1 mute may move the Pro's hardware mic path
                    break;
                case "setMixVolume":
                    if (cmd.Mix is null) return "setMixVolume: need 'mix'";
                    _mixer.SetMixVolume(cmd.Mix, cmd.Value.GetDouble());
                    break;
                case "setMixMuted":
                    if (cmd.Mix is null) return "setMixMuted: need 'mix'";
                    _mixer.SetMixMuted(cmd.Mix, cmd.Value.GetBoolean());
                    break;
                case "createChannel":
                    CreateChannel(cmd.Name);
                    break;
                case "renameChannel":
                    if (cmd.Channel is null) return "renameChannel: need 'channel'";
                    RenameChannel(cmd.Channel, cmd.Name);
                    break;
                case "deleteChannel":
                    if (cmd.Channel is null) return "deleteChannel: need 'channel'";
                    DeleteChannel(cmd.Channel);
                    break;
                case "createMix":
                    CreateMix(cmd.Name);
                    break;
                case "renameMix":
                    if (cmd.Mix is null) return "renameMix: need 'mix'";
                    RenameMix(cmd.Mix, cmd.Name);
                    break;
                case "deleteMix":
                    if (cmd.Mix is null) return "deleteMix: need 'mix'";
                    DeleteMix(cmd.Mix);
                    break;
                case "assignStream":
                    if (cmd.Channel is null || cmd.StreamId is null) return "assignStream: need 'channel' and 'streamId'";
                    // Also remembered per application, so it sticks next launch.
                    _mixer.AssignStream(cmd.StreamId.Value, cmd.Channel);
                    break;
                case "assignApp":
                    if (cmd.Channel is null || cmd.Identity is null) return "assignApp: need 'channel' and 'identity'";
                    _mixer.AssignApp(cmd.Identity, cmd.Channel, cmd.Label);
                    break;
                case "forgetApp":
                    if (cmd.Identity is null) return "forgetApp: need 'identity'";
                    _mixer.ForgetApp(cmd.Identity);
                    break;
                case "setMonitorOutput":
                    // A null device is meaningful here: it disconnects the route.
                    _mixer.SetMonitorOutput(cmd.Device);
                    SyncOutputSelectors();
                    break;
                case "setMonitorOutputs":
                    _mixer.SetMonitorOutputs(cmd.Devices ?? []);
                    SyncOutputSelectors();
                    break;
                case "setOutputVolume":
                    _mixer.SetOutputVolume(cmd.Value.GetDouble());
                    break;
                case "setEnforcedDefaults":
                    _mixer.SetEnforcedDefaults(cmd.Sink, cmd.Source);
                    break;
                case "setAuxPortEnabled":
                    _mixer.SetAuxPortEnabled(cmd.Value.GetBoolean());
                    SyncOutputSelectors();
                    break;
                case "setLowCutHz":
                    int hz = cmd.Value.GetInt32();
                    if (hz is not (0 or 80 or 120)) return "setLowCutHz: value must be 0, 80, or 120";
                    _mixer.SetLowCutHz(hz);
                    break;
                case "setSoftClipGuard":
                    _mixer.SetSoftClipGuard(cmd.Value.GetBoolean());
                    break;
                case "setInserts":
                    if (cmd.Channel is null || cmd.Inserts is null) return "setInserts: need 'channel' and 'inserts'";
                    foreach (InsertDefinition i in cmd.Inserts)
                        if (string.IsNullOrWhiteSpace(i.Id) || i.Kind != "lv2" || string.IsNullOrWhiteSpace(i.Plugin))
                            return "setInserts: every insert needs an id, kind 'lv2', and a plugin URI";
                    _mixer.SetInserts(cmd.Channel, cmd.Inserts);
                    break;
                case "setInsertBypass":
                    if (cmd.Channel is null || cmd.InsertId is null) return "setInsertBypass: need 'channel' and 'insertId'";
                    _mixer.SetInsertBypass(cmd.Channel, cmd.InsertId, cmd.Value.GetBoolean());
                    break;
                case "setInsertParam":
                    if (cmd.Channel is null || cmd.InsertId is null || cmd.Symbol is null)
                        return "setInsertParam: need 'channel', 'insertId', and 'symbol'";
                    _mixer.SetInsertParam(cmd.Channel, cmd.InsertId, cmd.Symbol, cmd.Value.GetDouble());
                    break;
                default:
                    return $"unknown mixer command '{cmd.Cmd}'";
            }
            // A layout command is acknowledged only after its new node list is
            // durable. Fader drags stay debounced below; editor operations are
            // rare and must not report success if mixer.json could not be saved.
            if (layoutChanged) PersistLayoutImmediately();
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            if (layoutLockTaken) Monitor.Exit(_layoutGate);
        }
        Changed?.Invoke();
        if (!layoutChanged) ScheduleSave();
        return null;
    }

    private static string LayoutName(string? name, string command)
    {
        string clean = name?.Trim() ?? "";
        if (clean.Length is 0 or > 60 || clean.Any(char.IsControl))
            throw new InvalidOperationException($"{command}: name must contain 1 to 60 printable characters");
        return clean;
    }

    private void CreateChannel(string? requestedName)
    {
        string name = LayoutName(requestedName, "createChannel");
        string id = MixerConfig.NewId(name, "channel", _mixer.Config.Channels.Select(c => c.Id));
        _mixer.AddApplicationChannel(id, name);
    }

    private void RenameChannel(string id, string? requestedName)
    {
        string name = LayoutName(requestedName, "renameChannel");
        ChannelDefinition? channel = _mixer.Config.Channels.FirstOrDefault(c => c.Id == id);
        if (channel is null) throw new InvalidOperationException($"renameChannel: unknown channel '{id}'");
        if (channel.InputPair is not null) throw new InvalidOperationException("hardware input channels cannot be renamed");

        _mixer.RenameApplicationChannel(id, name);
    }

    private void DeleteChannel(string id)
    {
        ChannelDefinition? channel = _mixer.Config.Channels.FirstOrDefault(c => c.Id == id);
        if (channel is null) throw new InvalidOperationException($"deleteChannel: unknown channel '{id}'");
        if (channel.InputPair is not null) throw new InvalidOperationException("hardware input channels cannot be deleted");
        var remaining = _mixer.Config.Channels.Where(c => c.InputPair is null && c.Id != id).ToList();
        if (remaining.Count == 0) throw new InvalidOperationException("the last application channel cannot be deleted");

        string fallback = remaining[0].Id;
        MixerSettings current = _mixer.ExportSettings();
        var overrides = current.AppOverrides.ToDictionary(e => e.Key,
            e => e.Value == id ? fallback : e.Value, StringComparer.OrdinalIgnoreCase);
        var apps = current.KnownApps.Select(a => a.ChannelId == id ? a with { ChannelId = fallback } : a).ToList();
        RebuildLayout(current with
        {
            UserChannels = [.. (current.UserChannels ?? []).Where(c => c.Id != id)],
            AppOverrides = overrides,
            KnownApps = apps,
            Levels = current.Levels.Where(e => !e.Key.StartsWith(id + "|", StringComparison.Ordinal))
                .ToDictionary(),
            ChannelMuted = [.. current.ChannelMuted.Where(c => !c.StartsWith(id + "|", StringComparison.Ordinal))],
        });
    }

    private void CreateMix(string? requestedName)
    {
        string name = LayoutName(requestedName, "createMix");
        MixerSettings current = _mixer.ExportSettings();
        var mixes = current.UserMixes?.ToList() ?? [];
        string id = MixerConfig.NewId(name, "output", _mixer.Config.Mixes.Select(m => m.Id));
        mixes.Add(new UserMixDefinition(id, name));
        RebuildLayout(current with { UserMixes = mixes });
    }

    private void RenameMix(string id, string? requestedName)
    {
        string name = LayoutName(requestedName, "renameMix");
        MixDefinition? mix = _mixer.Config.Mixes.FirstOrDefault(m => m.Id == id);
        if (mix is null) throw new InvalidOperationException($"renameMix: unknown mix '{id}'");
        if (mix.Kind != MixKind.VirtualMic)
            throw new InvalidOperationException("Monitor and hardware Aux mixes cannot be renamed");

        _mixer.RenameVirtualMix(id, name);
    }

    private void DeleteMix(string id)
    {
        MixDefinition? mix = _mixer.Config.Mixes.FirstOrDefault(m => m.Id == id);
        if (mix is null) throw new InvalidOperationException($"deleteMix: unknown mix '{id}'");
        if (mix.Kind != MixKind.VirtualMic)
            throw new InvalidOperationException("Monitor and hardware Aux mixes cannot be deleted");

        MixerSettings current = _mixer.ExportSettings();
        RebuildLayout(current with
        {
            UserMixes = [.. (current.UserMixes ?? []).Where(m => m.Id != id)],
            MixVolumes = current.MixVolumes.Where(e => e.Key != id).ToDictionary(),
            MixMuted = [.. current.MixMuted.Where(m => m != id)],
            Levels = current.Levels.Where(e => !e.Key.EndsWith("|" + id, StringComparison.Ordinal)).ToDictionary(),
            ChannelMuted = [.. current.ChannelMuted.Where(c => !c.EndsWith("|" + id, StringComparison.Ordinal))],
            Inserts = current.Inserts.Where(e => e.Key != $"mix:{id}").ToDictionary(e => e.Key, e => e.Value),
        });
    }

    /// <summary>
    /// Apply a structural edit by rebuilding PipeWire's module graph. If the
    /// new graph fails, restore the previous one so a typo or resource failure
    /// cannot leave the desktop without its OpenXLR sinks.
    /// </summary>
    private void RebuildLayout(MixerSettings desired)
    {
        MixerSettings previous = _mixer.ExportSettings();
        MixerConfig previousConfig = _mixer.Config;
        try
        {
            _mixer.Build(MixerConfig.FromSettings(desired));
            _mixer.ApplySettings(desired);
            _mixer.SyncStreams();
            SyncOutputSelectors();
        }
        catch
        {
            // Build can fail before it marks the graph as built. TearDown is
            // still required here because PipeWireAdapter may already own a
            // partial set of modules with the same names as the rollback.
            try { _mixer.TearDown(); }
            catch (Exception cleanupError)
            {
                _log.LogError("failed to clean partial mixer layout: {msg}", cleanupError.Message);
            }
            try
            {
                _mixer.Build(previousConfig);
                _mixer.ApplySettings(previous);
                _mixer.SyncStreams();
                SyncOutputSelectors();
            }
            catch (Exception restoreError)
            {
                _log.LogError("mixer layout rollback failed: {msg}", restoreError.Message);
            }
            throw;
        }
    }

    /// <summary>The current mixer scene for saving into a profile, or null.</summary>
    public OpenXLR.Core.MixerScene? ExportScene() => _mixer.Built ? _mixer.ExportScene() : null;

    /// <summary>Recall a profile's mixer scene. Returns null on success.</summary>
    public string? ApplyScene(OpenXLR.Core.MixerScene scene)
    {
        if (!_mixer.Built) return "mixer not built (start the daemon with --mixer)";
        try { _mixer.ApplyScene(scene); }
        catch (Exception ex) { return ex.Message; }
        SyncOutputSelectors();
        Changed?.Invoke();
        ScheduleSave();
        return null;
    }

    /// <summary>
    /// Persist a moment after the last change, so dragging a fader writes once
    /// instead of on every pixel of travel.
    /// </summary>
    private readonly object _saveGate = new();
    private bool _saveDirty;

    private void PersistLayoutImmediately()
    {
        lock (_saveGate)
        {
            _mixer.ExportSettings().Save();
            _saveDirty = false;
            _saveDebounce?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private void ScheduleSave()
    {
        lock (_saveGate)
        {
            _saveDirty = true;
            _saveDebounce ??= new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
            _saveDebounce.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        }
    }

    // One writer at a time, and only when something changed since the last
    // write; Dispose flushes a pending save so a change made just before
    // shutdown is not lost.
    private void SaveNow()
    {
        lock (_saveGate)
        {
            if (!_saveDirty) return;
            _saveDirty = false;
            try { _mixer.ExportSettings().Save(); }
            catch (Exception ex) { _log.LogDebug("settings save: {msg}", ex.Message); }
        }
    }

    public void Dispose()
    {
        _streamSweep?.Dispose();
        _saveDebounce?.Dispose();
        _meterPush?.Dispose();
        SaveNow();
    }
}
