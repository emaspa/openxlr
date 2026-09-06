using OpenXLR.Core;
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
    private readonly Mixer _mixer;
    private readonly ServiceProgress _progress = new();
    private volatile bool _checkingProgress;
    internal bool IsResponsive(TimeSpan limit) => !_checkingProgress || _progress.IsRecent(limit);
    private Timer? _streamSweep;
    private Timer? _saveDebounce;
    private Timer? _meterPush;
    private readonly CancellationTokenSource _stopping = new();
    private Task _defaultDefense = Task.CompletedTask;
    private int _sweepRunning;
    private string? _lastSweepError;

    public MixerService(ILogger<MixerService> log, IConfiguration config, DeviceManager devices)
    {
        _log = log;
        _config = config;
        _devices = devices;
        _mixer = new(new PipeWireAdapter(_progress.Mark));
    }

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
        bool auxDesired = _mixer.AuxPortEnabled;
        _devices.EnsureOutputSelectors(
            hp1: suffixes.Contains("hp1"), hp2: suffixes.Contains("hp2"),
            usbAux: auxDesired, lineOut: suffixes.Contains("lineout"));

        if (auxDesired && !_prevAuxDesired && _mixer.Built)
            _mixer.BounceAuxTarget();
        _prevAuxDesired = auxDesired;
        _ = anyProOutput;

        bool anyJack = suffixes.Contains("hp1") || suffixes.Contains("hp2") || suffixes.Contains("lineout");
        bool jacksOnly = anyJack && _mixer.MonitorOutputs.All(o => o.Contains('#'));
        bool micDirect = jacksOnly && OpenXLR.Core.Mixing.MonitorFeed.Parts(_mixer.JackMonitorMix ?? "monitor")
            .Any(m => !_mixer.IsChannelMutedIn("xlr1", m));
        _mixer.SetHardwareMicMonitor(micDirect);
        if (anyJack && _devices.EnsureHeadphoneMix(monitorReturn: true, micDirect: micDirect) && _mixer.Built)
            _mixer.BounceMonitorHardwareOutput();
    }

    public event Action? Changed;
    public MixerState? Snapshot() => _mixer.Built ? _mixer.Snapshot() : null;
    public IReadOnlyList<AudioNode>? Devices() => _mixer.Built ? _mixer.ListDevices() : null;
    public IReadOnlyDictionary<string, double[]>? Meters() => _mixer.Built ? _mixer.ReadMeters() : null;
    public event Action? MetersUpdated;
    public bool SubmixerEnabled { get; private set; }
    public bool Built => _mixer.Built;

    public Task StartAsync(CancellationToken ct)
    {
        bool launchDefault = _config.GetValue("mixer", false) ||
                             Environment.GetEnvironmentVariable("OPENXLR_BUILD_MIXER") == "1";
        bool wanted = OpenXLR.Core.DaemonSettings.SubmixerEnabled(launchDefault);
        SubmixerEnabled = wanted;
        if (!wanted)
        {
            _log.LogInformation("submixer off (daemon.json, --mixer, or OPENXLR_BUILD_MIXER=1 turn it on); hardware control only");
            return Task.CompletedTask;
        }
        OpenXLR.Core.Mixing.Lv2Catalog.Warm();
        _progress.Mark();
        _checkingProgress = true;

        string? output = _config.GetValue<string>("monitorOutput")
                         ?? Environment.GetEnvironmentVariable("OPENXLR_MONITOR_OUTPUT");

        string? defaultSinkBefore = null, defaultSourceBefore = null;
        try
        {
            defaultSinkBefore = Run("pactl", "get-default-sink");
            defaultSourceBefore = Run("pactl", "get-default-source");
        }
        catch (Exception) { }

        try
        {
            MixerSettings? saved = MixerSettings.Load();
            _mixer.Build(MixerConfig.FromSettings(saved), output);
            if (saved is not null)
            {
                _mixer.ApplySettings(saved.WithMonitorOverride(output));
                _log.LogInformation("restored settings from {path}", MixerSettings.DefaultPath);
            }
            SyncOutputSelectors();

            _log.LogInformation("submix graph built ({mixes} mixes, {channels} channels){route}",
                _mixer.Config.Mixes.Count, _mixer.Config.Channels.Count,
                output is null ? "" : $", monitor -> {output}");

            _streamSweep = new Timer(_ =>
            {
                if (Interlocked.CompareExchange(ref _sweepRunning, 1, 0) != 0) return;
                try
                {
                    _mixer.SetInputDeviceHint(
                        _devices.ActiveInfo?.Model.Replace(' ', '_'),
                        _devices.ActiveCapabilities?.OutputRouting ?? false);
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
                    if (ex.Message != _lastSweepError)
                    {
                        _lastSweepError = ex.Message;
                        _log.LogWarning("stream sweep: {msg} (repeats logged at debug level)", ex.Message);
                    }
                    else _log.LogDebug("stream sweep: {msg}", ex.Message);
                }
                finally
                {
                    _progress.Mark();
                    Volatile.Write(ref _sweepRunning, 0);
                }
            }, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));

            _meterPush = new Timer(_ => MetersUpdated?.Invoke(), null,
                TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(66));

            Changed?.Invoke();

            (string? enfSink, string? enfSource) = _mixer.EnforcedDefaults;
            string? wantSink = enfSink ?? defaultSinkBefore;
            string? wantSource = enfSource ?? defaultSourceBefore;
            _defaultDefense = DefaultDefense.RunAsync(wantSink, wantSource, args => Run("pactl", args),
                DefaultDefense.DelaysMs, _stopping.Token, msg => _log.LogDebug("default defense: {msg}", msg));
        }
        catch (Exception ex)
        {
            _log.LogError("failed to build submix graph: {msg}", ex.Message);
            try { _mixer.TearDown(); } catch (Exception) { }
            _checkingProgress = false;
        }
        return Task.CompletedTask;
    }

    private static string Run(string exe, params string[] args)
    {
        ProcessResult r = ProcessRunner.Run(exe, args, TimeSpan.FromSeconds(3), stdoutCap: 1024 * 1024, stderrCap: 64 * 1024);
        if (r.TimedOut) throw new TimeoutException($"{exe} timed out after 3 seconds");
        if (r.ExitCode != 0) throw new InvalidOperationException($"{exe} failed: {r.Stderr.Trim()}");
        return r.StdoutText.Trim();
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _streamSweep?.Dispose();
        _streamSweep = null;
        _meterPush?.Dispose();
        _meterPush = null;
        _stopping.Cancel();
        try { await _defaultDefense.WaitAsync(TimeSpan.FromSeconds(3), ct); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { _log.LogWarning("default defense did not stop in time"); }
        if (_mixer.Built)
        {
            if (_mixer.ExportSettings().Save() is string stopErr) _log.LogWarning("settings not saved at stop: {err}", stopErr);
            _mixer.TearDown();
            _log.LogInformation("submix graph torn down");
        }
    }

    /// <summary>Apply a mixer command. Returns null on success, else an error.</summary>
    public string? Apply(Command cmd)
    {
        if (!_mixer.Built) return "mixer not built (start the daemon with --mixer)";
        string? invalid = CommandValidation.Check(cmd, _mixer, OpenXLR.Core.Mixing.Lv2Catalog.Find);
        if (invalid is not null) return invalid;
        try
        {
            switch (cmd.Cmd)
            {
                case "createChannel":
                case "renameChannel":
                case "deleteChannel":
                case "createMix":
                case "renameMix":
                case "deleteMix":
                case "setLayoutOrder":
                    lock (_saveGate)
                    {
                        switch (cmd.Cmd)
                        {
                            case "createChannel":
                                _mixer.CreateApplicationChannel(cmd.Name!, settings => settings.Save());
                                break;
                            case "renameChannel":
                                _mixer.RenameApplicationChannel(cmd.Channel!, cmd.Name!, settings => settings.Save());
                                break;
                            case "deleteChannel":
                                _mixer.DeleteApplicationChannel(cmd.Channel!, settings => settings.Save());
                                break;
                            case "createMix":
                                _mixer.CreateVirtualMix(cmd.Name!, settings => settings.Save());
                                break;
                            case "renameMix":
                                _mixer.RenameVirtualMix(cmd.Mix!, cmd.Name!, settings => settings.Save());
                                break;
                            case "deleteMix":
                                _mixer.DeleteVirtualMix(cmd.Mix!, settings => settings.Save());
                                break;
                            default:
                                _mixer.SetLayoutOrder(cmd.Channels!, cmd.Mixes!, settings => settings.Save());
                                break;
                        }
                        _saveDirty = false;
                        _lastSaveError = null;
                        _retryDelay = SaveDelay;
                        _saveDebounce?.Change(Timeout.Infinite, Timeout.Infinite);
                    }
                    SyncOutputSelectors();
                    Changed?.Invoke();
                    return null;
                case "setLevel":
                    if (cmd.Channel is null || cmd.Mix is null) return "setLevel: need 'channel' and 'mix'";
                    _mixer.SetLevel(cmd.Channel, cmd.Mix, cmd.Value.GetDouble());
                    break;
                case "setChannelMuted":
                    if (cmd.Channel is null || cmd.Mix is null) return "setChannelMuted: need 'channel' and 'mix'";
                    _mixer.SetChannelMuted(cmd.Channel, cmd.Mix, cmd.Value.GetBoolean());
                    SyncOutputSelectors();
                    break;
                case "setMixVolume":
                    if (cmd.Mix is null) return "setMixVolume: need 'mix'";
                    _mixer.SetMixVolume(cmd.Mix, cmd.Value.GetDouble());
                    break;
                case "setMixMuted":
                    if (cmd.Mix is null) return "setMixMuted: need 'mix'";
                    _mixer.SetMixMuted(cmd.Mix, cmd.Value.GetBoolean());
                    break;
                case "assignStream":
                    if (cmd.Channel is null || cmd.StreamId is null) return "assignStream: need 'channel' and 'streamId'";
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
                    _mixer.SetMonitorOutput(cmd.Device);
                    SyncOutputSelectors();
                    break;
                case "setMonitorOutputs":
                    _mixer.SetMonitorOutputs(cmd.Devices ?? []);
                    SyncOutputSelectors();
                    break;
                case "setMonitorFeed":
                    if (cmd.Device is null || cmd.Mix is null) return "setMonitorFeed: need 'device' and 'mix'";
                    if (_mixer.SetMonitorFeed(cmd.Device, cmd.Mix) is string feedErr) return $"setMonitorFeed: {feedErr}";
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
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        Changed?.Invoke();
        ScheduleSave();
        return null;
    }

    public OpenXLR.Core.MixerScene? ExportScene() => _mixer.Built ? _mixer.ExportScene() : null;

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

    private readonly object _saveGate = new();
    private bool _saveDirty;
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);
    private TimeSpan _retryDelay = SaveDelay;
    private string? _lastSaveError;

    public string? PersistenceWarning
    {
        get { lock (_saveGate) return _lastSaveError is null ? null : $"Mixer settings are not being saved ({_lastSaveError}); retrying."; }
    }

    private void ScheduleSave()
    {
        lock (_saveGate)
        {
            _saveDirty = true;
            _saveDebounce ??= new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
            _saveDebounce.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void SaveNow()
    {
        bool changed = false;
        lock (_saveGate)
        {
            if (!_saveDirty) return;
            string? err = _mixer.ExportSettings().Save();
            if (err is null)
            {
                _saveDirty = false;
                _retryDelay = SaveDelay;
                if (_lastSaveError is not null)
                {
                    _log.LogInformation("mixer settings saved again");
                    _lastSaveError = null;
                    changed = true;
                }
            }
            else
            {
                if (err != _lastSaveError)
                {
                    _log.LogWarning("mixer settings not saved: {err}; retrying", err);
                    _lastSaveError = err;
                    changed = true;
                }
                _retryDelay = TimeSpan.FromTicks(Math.Min(_retryDelay.Ticks * 2, MaxRetryDelay.Ticks));
                _saveDebounce?.Change(_retryDelay, Timeout.InfiniteTimeSpan);
            }
        }
        if (changed) Changed?.Invoke();
    }

    public void Dispose()
    {
        _streamSweep?.Dispose();
        _saveDebounce?.Dispose();
        _meterPush?.Dispose();
        SaveNow();
    }
}
