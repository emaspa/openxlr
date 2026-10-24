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
    private readonly StartupDefaults? _startupDefaults;
    private readonly Mixer _mixer;
    private readonly PipeWireAdapter _pipeWire;
    private readonly DeviceEqPort _eqPort;
    private int _duckingPortHooked;
    private IDisposable? _graphWatch;
    private readonly ServiceProgress _progress = new();
    private volatile bool _checkingProgress;
    internal bool IsResponsive(TimeSpan limit) => !_checkingProgress || _progress.IsRecent(limit);
    private Timer? _streamSweep;
    private readonly ITimer _browserTitles;
    private Timer? _meterPush;
    // The default-device defense after a build: stored and cancellable, so a
    // stop never leaves a pass behind that would override a default the
    // user chose after the daemon was gone.
    private readonly CancellationTokenSource _stopping = new();
    private Task _defaultDefense = Task.CompletedTask;
    // Cancelled once the stop has run for StopBudget: the adapter then kills
    // a helper still running and starts no other, so a stalled audio server
    // ends the stop early instead of each pactl call waiting out its own
    // deadline (a graph has dozens of modules, five seconds each).
    private readonly CancellationTokenSource _stopDeadline = new();
    private long _stopStarted;   // monotonic ms when the host began to stop; 0 before

    /// <summary>
    /// How long the stop may spend on the audio server, counted from the
    /// moment the host begins to stop. The unit allows 45 s
    /// (TimeoutStopSec) for the whole daemon; the device manager's card
    /// profile restore (two helpers, five seconds each), a Wine session
    /// ending (up to seven) and the process exit come after or beside this.
    /// What is left loaded is removed by the next start. Tests shorten it.
    /// </summary>
    internal TimeSpan StopBudget { get; init; } = TimeSpan.FromSeconds(15);
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

    private readonly IHostApplicationLifetime? _lifetime;
    private int _graphMissing;

    // A failed startup build is retried in the background until the audio
    // server answers (see RetryBuildAsync). Meanwhile a recalled profile's
    // mixer half waits in _pendingScene and the state carries a warning.
    private Task _buildRetry = Task.CompletedTask;
    internal Task BuildRetry => _buildRetry;
    private readonly object _sceneGate = new();
    private bool _waitingForAudio;
    private OpenXLR.Core.MixerScene? _pendingScene;

    /// <summary>
    /// The debounced writer for the mixer settings. It stops writing as soon
    /// as the graph is on its way out, so nothing can export a torn-down
    /// mixer over the user's file.
    /// </summary>
    private readonly SettingsSaver _saves;
    public NativeEditorPolicy EditorPolicy { get; }

    public MixerService(ILogger<MixerService> log, IConfiguration config, DeviceManager devices,
        IHostApplicationLifetime? lifetime = null, NativeEditorPolicy? editorPolicy = null,
        StartupDefaults? startupDefaults = null, WaveInterfaces? waveInterfaces = null, TimeProvider? time = null)
    {
        _log = log;
        _config = config;
        bool launchDefault = config.GetValue("mixer", false) ||
                             Environment.GetEnvironmentVariable("OPENXLR_BUILD_MIXER") == "1";
        _launchDefault = launchDefault;
        SubmixerEnabled = DaemonSettings.SubmixerEnabled(launchDefault);
        _devices = devices;
        _startupDefaults = startupDefaults;
        _lifetime = lifetime;
        // The budget runs from the start of the host's stop, not from this
        // service's turn: the services stopped before it spend the same 45 s.
        lifetime?.ApplicationStopping.Register(() => Interlocked.CompareExchange(ref _stopStarted, Environment.TickCount64, 0));
        EditorPolicy = editorPolicy ?? new NativeEditorPolicy();
        _pipeWire = new PipeWireAdapter(_progress.Mark, note => _log.LogInformation("{msg}", note));
        _mixer = new(_pipeWire) { InputNote = note => _log.LogWarning("hardware input: {note}", note) };
        _eqPort = new DeviceEqPort(devices, waveInterfaces);
        // A unit confirming or refusing an EQ is followed at once, off the
        // device lock: a hand-over from the unit waits for its confirmation.
        _eqPort.StatusChanged += () => ThreadPool.QueueUserWorkItem(_ =>
        {
            try { if (_mixer.Built && _mixer.SyncDeviceEq()) Changed?.Invoke(); }
            catch (Exception ex) { _log.LogDebug("EQ hand-over: {msg}", ex.Message); }
        });
        DuckingPort = new DeviceDuckingPort(devices);
        _offlineDucking = new(LoadOfflineDucking);
        _saves = new SettingsSaver(
            () => _mixer.ExportSettings().Save(),
            error =>
            {
                if (error is null) _log.LogInformation("mixer settings saved again");
                else _log.LogWarning("mixer settings not saved: {err}; retrying", error);
            },
            () => Changed?.Invoke());
        _browserTitles = (time ?? TimeProvider.System).CreateTimer(_ => PublishBrowserTabTitles(), null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// pipewire-pulse restarted underneath the daemon: every module it hosted
    /// is gone and the module ids are being reused by other clients. Nothing
    /// can be unloaded or healed piecemeal, so the graph is forgotten and the
    /// daemon asks to be started afresh; systemd brings it back in seconds
    /// with the saved layout. Two consecutive sightings, so a momentary pactl
    /// hiccup does not trigger it.
    /// </summary>
    private bool GraphLost()
    {
        if (_mixer.GraphPresent() is not false) { _graphMissing = 0; return false; }
        if (++_graphMissing < 2) return false;
        _log.LogWarning("the submixer graph is gone (pipewire-pulse restarted?); restarting the daemon to rebuild it");
        // ForgetGraph empties the mixer, so this is the last chance to write
        // and the end of writing, exactly as at a clean stop.
        if (_saves.Close(write: _saves.Pending) is string err) _log.LogWarning("settings not saved before the restart: {err}", err);
        _mixer.ForgetGraph();
        RestartRequest.Ask(RestartRequest.TemporaryFailure);
        _lifetime?.StopApplication();
        return true;
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
        if (_devices.ActiveCapabilities?.OutputRouting is not true)
        {
            // A saved Pro jack is not evidence of a live hardware mic path.
            // Keep software monitoring while no Pro is driven, and re-arm the
            // Aux stream when one is again.
            _mixer.SetHardwareMicMonitor(false);
            _prevAuxDesired = false;
            return;
        }
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
        bool monitorMuted = _mixer.MonitorMuted;
        _devices.EnsureOutputSelectors(
            hp1: AudibleJack("hp1"), hp2: AudibleJack("hp2"),
            usbAux: auxDesired, lineOut: AudibleJack("lineout"));

        bool AudibleJack(string suffix) => _mixer.MonitorOutputs.Any(output =>
            output.EndsWith("#" + suffix, StringComparison.Ordinal) && _mixer.IsMonitorOutputAudible(output));

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
        // Monitor send's mute of the channel that carries the Pro's input 1:
        // XLR 1 on the interface, or the capture channel kept for the Pro's
        // microphone while XLR 1 is bound to another source, and off while
        // no channel carries it. Only while every monitor output is a Pro
        // jack: with another device in the set the software send has to
        // carry the mic there, and the hardware path would double it.
        bool anyJack = suffixes.Contains("hp1") || suffixes.Contains("hp2") || suffixes.Contains("lineout");
        bool jacksOnly = !monitorMuted && anyJack && _mixer.MonitorOutputs.All(o => o.Contains('#'));
        // With a summed feed (A+B) the mic rides the hardware path as soon as
        // any of the summed mixes carries it.
        string jackFeed = _mixer.JackMonitorMix ?? "monitor";
        // The mixer refuses the path while that channel is in an exclusive
        // group or under Sound Check, and reads its mutes under its own lock.
        bool micDirect = _mixer.SetHardwareMicMonitor(jacksOnly && _mixer.IsMonitorOnlyFeed(jackFeed), whenHeardIn: jackFeed);
        // The direct path is driven whether or not a jack is in use: another
        // program (Wave Link in a virtual machine) can leave it on, and the
        // jacks would then carry the microphone with nothing here showing it.
        // The return pair is only asserted for a jack; with none in use it
        // stays as the unit holds it, which nothing here depends on.
        if (_devices.EnsureHeadphoneMix(monitorReturn: anyJack ? true : null, micDirect: micDirect) && _mixer.Built)
            _mixer.BounceMonitorHardwareOutput();
    }

    /// <summary>Raised when mixer state changes, so the hub can broadcast.</summary>
    public event Action? Changed;

    /// <summary>Null until the graph is built.</summary>
    public MixerState? Snapshot()
    {
        if (!_mixer.Built) return null;
        MixerState state = _mixer.Snapshot();
        return state with
        {
            LayoutWarning = ResourceWarning,
            Inserts = state.Inserts.ToDictionary(e => e.Key, e => (IReadOnlyList<InsertStatus>)e.Value.Select(insert =>
            {
                string? reason = EditorPolicy.BlockReason(insert.Insert.Kind, insert.Insert.Plugin);
                return insert with { NativeUiBlocked = reason is not null, NativeUiBlockReason = reason };
            }).ToArray()),
        };
    }

    /// <summary>Selectable sinks and sources, or null when the mixer is off.</summary>
    /// <summary>
    /// Selectable sinks and sources, or null when the mixer is off. Each
    /// node of an attached supported interface names that unit in
    /// <see cref="AudioNode.DrivenUnit"/>.
    /// </summary>
    public IReadOnlyList<AudioNode>? Devices()
        => _mixer.Built ? [.. _mixer.ListDevices().Select(node => _devices.SupportedUnitFor(node.Name) is { } unit ? node with { DrivenUnit = unit.Id } : node)] : null;

    /// <summary>
    /// The attached units next to the primary one: a strip or capture channel
    /// on a driven additional unit carries that unit's controls, and a strip
    /// bound to one of its XLR jacks keeps the jack's name.
    /// </summary>
    public void SetAttachedUnits(IReadOnlyList<AttachedUnit> units)
    {
        if (_mixer.SetAttachedUnits(units)) Changed?.Invoke();
    }

    /// <summary>Live stereo levels, or null when the mixer is off.</summary>
    public IReadOnlyDictionary<string, double[]>? Meters() => _mixer.Built ? _mixer.ReadMeters() : null;

    /// <summary>
    /// Every plugin the saved chains name, for the catalogue a client is sent.
    /// The socket accepts clients before this service has restored the chains,
    /// and a client that reconnects in that window asks straight away, so the
    /// saved file answers until the mixer holds them.
    /// </summary>
    public IReadOnlyCollection<(string Kind, string Plugin)> InsertPlugins()
    {
        var used = new HashSet<(string Kind, string Plugin)>(_mixer.InsertPlugins());
        if (used.Count == 0) used.UnionWith(OpenXLR.Core.Mixing.MixerSettings.Load()?.InsertPlugins() ?? []);
        return used;
    }

    /// <summary>Raised at the meter refresh rate so the hub can push levels.</summary>
    public event Action? MetersUpdated;

    /// <summary>Whether this run builds the submixer at all.</summary>
    public bool SubmixerEnabled { get; }

    private readonly bool _launchDefault;

    /// <summary>
    /// Whether the next start with the same command line builds the submixer,
    /// read again from daemon.json: the window turns the submixer off by
    /// saving that file and restarting the daemon.
    /// </summary>
    private bool SubmixerEnabledAtNextStart => DaemonSettings.SubmixerEnabled(_launchDefault);

    // A built graph is not ready for profile recall until ApplySettings and
    // output routing have finished. Device arrival can precede StartAsync.
    private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Initialized => _initialized.Task;

    /// <summary>Whether the submix graph is up (built a few seconds after start).</summary>
    public bool Built => _mixer.Built;

    /// <summary>The primary unit's ducking, as the mixer's port reaches it. Tests replace it before the start.</summary>
    internal IDaemonDuckingPort DuckingPort { get; init; }

    // The saved setup, shown while no graph is built: in hardware-control
    // mode, or while the mixer waits for the audio server. Read once.
    private readonly Lazy<DuckingSetup> _offlineDucking;

    private static DuckingSetup LoadOfflineDucking()
    {
        try
        {
            MixerSettings? saved = MixerSettings.Load(MixerSettings.DefaultPath, out _);
            MixerConfig layout = MixerConfig.FromSettings(saved);
            return saved?.Ducking is { } setup ? DuckingSetup.Prune(setup, layout) : DuckingSetup.Default(layout);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return DuckingSetup.Default(MixerConfig.Default());
        }
    }

    /// <summary>
    /// The ducking state, present whenever the daemon runs. Without a graph
    /// it is the saved setup, nothing running, and the primary unit's
    /// verdict that the software mixer is off.
    /// </summary>
    public DuckingState Ducking()
    {
        if (_mixer.Built) return DuckingState.From(_mixer.DuckingSnapshot());
        DuckingSetup setup = _offlineDucking.Value;
        DuckingDeviceState? device = null;
        if (DuckingPort.Primary() is { } primary)
        {
            DuckingVerdict verdict = DuckingPlacement.Evaluate(setup, new DuckingView(false, false, [], []), primary.Support);
            DeviceDuckingStatus status = DuckingPort.Status(primary.Unit);
            device = new(primary.Unit, primary.Support.Name, primary.Support.Firmware, verdict.Ready, verdict.Reason, verdict.Note,
                [.. verdict.Outputs], status.Phase == DeviceDuckingPhase.Verified, status.Readback);
        }
        bool installed = _pipeWire.DuckHostInstalled;
        return DuckingState.From(new DuckingStateSnapshot(setup.Copy(), null, installed, installed ? null : Mixer.DuckingNeedsHost, device));
    }

    /// <summary>The primary unit's ducking support handed to the mixer; true when it changed what the mixer knows.</summary>
    private bool SyncUnitDucking()
    {
        (string Unit, UnitDuckingSupport Support)? primary = DuckingPort.Primary();
        return _mixer.SetUnitDuckingSupport(primary?.Unit, primary?.Support);
    }

    /// <summary>A <c>setDucking</c> the validation already passed, as the change it carries.</summary>
    private static DuckingChange DuckingChangeOf(Command cmd)
    {
        CommandValidation.CheckDucking(cmd, null, out DuckingChange? change);
        return change ?? throw new ArgumentException("setDucking: value must be an object");
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (!SubmixerEnabled)
        {
            _initialized.TrySetResult();
            _log.LogInformation("submixer off (daemon.json, --mixer, or OPENXLR_BUILD_MIXER=1 turn it on); hardware control only");
            return Task.CompletedTask;
        }
        // plugin inserts: scan the LV2, CLAP and VST3 bundles off the startup
        // path, and say in the log what the scan could not read.
        OpenXLR.Core.Mixing.PluginCatalog.Warm(() => PluginScanLog.Write(_log));
        _progress.Mark();
        _checkingProgress = true;

        // Optional: the physical sink the monitor mix feeds. Without it the
        // monitor mix exists but isn't routed anywhere audible.
        string? output = _config.GetValue<string>("monitorOutput")
                         ?? Environment.GetEnvironmentVariable("OPENXLR_MONITOR_OUTPUT");

        // WirePlumber auto-switches the system defaults to newly created sinks
        // and sources, asynchronously, some time after they appear. Defend what
        // the user had before this process touched anything. Program.cs takes
        // that snapshot before any hosted service starts: DeviceManager parks
        // the card in pro-audio as soon as it connects, and WirePlumber may
        // move the defaults to the card's new nodes before this service runs
        // (see StartupDefaults).
        StartupDefaults before = _startupDefaults ?? StartupDefaults.Capture();

        try
        {
            BuildGraph(output, before);
        }
        catch (Exception ex)
        {
            _log.LogError("failed to build submix graph: {msg}", ex.Message);
            DiscardPartialGraph();
            // No audio server is a degraded operating mode, not a deadlock.
            _checkingProgress = false;
            // A pipewire-pulse that stalls for a while at boot (a wedged USB
            // device can hold it) answers again later, and nothing else would
            // ever build the graph: GraphLost only watches a built one. Keep
            // trying in the background until it answers or the daemon stops.
            lock (_sceneGate) _waitingForAudio = true;
            _log.LogWarning("the mixer is waiting for the audio server; the graph build is retried with backoff, up to every {max} s (repeated failures are logged at debug level)",
                (int)MaxBuildRetryDelay.TotalSeconds);
            Changed?.Invoke();
            string firstError = ex.Message;
            _buildRetry = Task.Run(() => RetryBuildAsync(output, before, firstError));
        }
        finally { _initialized.TrySetResult(); }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Build the graph, restore the saved settings and start the sweeps. A
    /// failure anywhere throws with the graph possibly half made; the caller
    /// removes it with <see cref="DiscardPartialGraph"/>.
    /// </summary>
    private void BuildGraph(string? output, StartupDefaults before)
    {
        _graphWatch ??= _pipeWire.WatchGraph(note => _log.LogInformation("{msg}", note));
        MixerSettings? saved = MixerSettings.Load(MixerSettings.DefaultPath, out string? settingsWarning);
        if (settingsWarning is not null)
            _log.LogWarning("mixer settings: {warning}{fallback}", settingsWarning, saved is null ? "; starting with defaults" : "");
        _mixer.SetInputBindings(saved?.InputBindings);
        _mixer.SetMonitorMuteMicrophones(saved?.MonitorMuteMicrophones ?? false);
        _mixer.SetMonitorMuted(saved?.MonitorMuted ?? false);
        UpdateInputDeviceHint();
        _mixer.Build(MixerConfig.FromSettings(saved), output);
        // Units run their microphone's EQ only while the submixer runs: in
        // hardware-control mode no channel carries an input and nothing is
        // written to any unit.
        _mixer.SetDeviceEqPort(_eqPort);
        _mixer.SetUnitEqSupport(_eqPort.Units());
        // Restore the user's saved levels, mutes, device picks, and per-app
        // assignments. Env vars, when set, still win for the device picks.
        if (saved is not null)
        {
            _mixer.ApplySettings(saved.WithMonitorOverride(output));
            _log.LogInformation("restored settings from {path}", MixerSettings.DefaultPath);
        }
        // A profile recalled while the graph was waiting for the audio
        // server lands on top of the saved settings, as it would have at a
        // normal start. Applied under the gate that queued it, so a recall
        // arriving meanwhile is either queued here or applied after this.
        lock (_sceneGate)
        {
            if (_pendingScene is { } scene)
            {
                _pendingScene = null;
                try
                {
                    _mixer.ApplyScene(scene);
                    ScheduleSave();
                    _log.LogInformation("applied the mixer half of the profile recalled while the mixer was waiting");
                }
                catch (Exception ex) { _log.LogWarning("the profile recalled while the mixer was waiting could not be applied: {msg}", ex.Message); }
            }
            _waitingForAudio = false;
        }
        // Ducking, like the EQ, reaches a unit only in software mixer mode,
        // so hardware-control mode writes nothing to it. The port is attached
        // once the saved setup and a waiting profile are in place, so the
        // unit's first post is the setup it should hold. A unit
        // confirming or refusing a target is followed at once, off the device
        // lock: a hand-over from the unit waits for its confirmation.
        if (Interlocked.Exchange(ref _duckingPortHooked, 1) == 0)
            DuckingPort.StatusChanged += () => ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (!_stopping.IsCancellationRequested && _mixer.Built && (SyncUnitDucking() | _mixer.SyncDucking()))
                        Changed?.Invoke();
                }
                catch (Exception ex) { _log.LogDebug("ducking hand-over: {msg}", ex.Message); }
            });
        _mixer.SetDeviceDuckingPort(DuckingPort);
        SyncUnitDucking();
        SyncOutputSelectors();

        if (_mixer.MigrateCaptureInputs(source => _devices.SupportedUnitFor(source)?.Id)) ScheduleSave();
        _log.LogInformation("submix graph built ({mixes} mixes, {channels} channels){route}",
            _mixer.Config.Mixes.Count, _mixer.Config.Channels.Count,
            output is null ? "" : $", monitor -> {output}");

        // Sweep for new application streams and route them to their channel.
        // One second is responsive enough that audio lands in the right place
        // before a user notices, without polling the graph hard.
        _streamSweep = new Timer(_ => SweepOnce(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));

        // Meters refresh far more often than state; 15 Hz looks smooth
        // without flooding clients.
        _meterPush = new Timer(_ => MetersUpdated?.Invoke(), null,
            TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(66));

        Changed?.Invoke();

        // Defend the defaults after WirePlumber's delayed auto-switch. Two
        // passes because the switch can land seconds after node creation.
        (string? enfSink, string? enfSource) = _mixer.EnforcedDefaults;
        // Follow mode is resolved by the regular sweep from the current
        // monitor selection, never pinned to a startup-time device.
        string? wantSink = enfSink == Mixer.FollowMonitorOutput ? null : enfSink ?? before.Sink;
        string? wantSource = enfSource ?? before.Source;
        _defaultDefense = DefaultDefense.RunAsync(wantSink, wantSource, args => Run("pactl", args),
            DefaultDefense.DelaysMs, _stopping.Token, msg => _log.LogDebug("default defense: {msg}", msg));
    }

    /// <summary>
    /// A partial graph is worse than none: half the sinks exist but no
    /// routing, and a later rebuild would double up. Remove what was made.
    /// </summary>
    private void DiscardPartialGraph()
    {
        // A failure after the sweeps started must not leave them running
        // against the removed graph, or doubled by the next attempt.
        Interlocked.Exchange(ref _streamSweep, null)?.Dispose();
        Interlocked.Exchange(ref _meterPush, null)?.Dispose();
        try { _mixer.TearDown(); } catch (Exception) { /* best effort; the next build clears stale modules */ }
    }

    /// <summary>The wait before the next build attempt after <paramref name="failures"/> failed ones.</summary>
    internal static TimeSpan BuildRetryDelay(int failures)
        => TimeSpan.FromSeconds(Math.Min(MaxBuildRetryDelay.TotalSeconds, 2 << Math.Clamp(failures - 1, 0, 4)));
    internal static readonly TimeSpan MaxBuildRetryDelay = TimeSpan.FromSeconds(30);
    /// <summary>Tests shorten the backoff; the daemon uses <see cref="BuildRetryDelay"/>.</summary>
    internal Func<int, TimeSpan> RetryDelay { get; init; } = BuildRetryDelay;

    private async Task RetryBuildAsync(string? output, StartupDefaults before, string lastError)
    {
        CancellationToken stop = _stopping.Token;
        for (int failures = 1; ; failures++)
        {
            try { await Task.Delay(RetryDelay(failures), stop); }
            catch (OperationCanceledException) { return; }
            if (stop.IsCancellationRequested) return;
            // Each attempt is watched like the first one: every helper it
            // runs has a deadline and marks progress.
            _progress.Mark();
            _checkingProgress = true;
            try
            {
                BuildGraph(output, before);
                _log.LogInformation("the audio server answers again; submix graph built after {n} failed attempt(s)", failures);
                return;
            }
            catch (Exception ex)
            {
                _checkingProgress = false;
                DiscardPartialGraph();
                lock (_sceneGate) _waitingForAudio = true;
                if (stop.IsCancellationRequested) return;
                if (ex.Message != lastError)
                {
                    lastError = ex.Message;
                    _log.LogWarning("submix graph still not built: {msg}; retrying", ex.Message);
                }
                else _log.LogDebug("submix graph still not built: {msg}; retrying", ex.Message);
            }
        }
    }

    // Channel feeds follow the actively driven interface; the node name
    // contains the model, and the serial, as udev spells them. True when
    // the driven or attached units changed what clients see.
    private bool UpdateInputDeviceHint()
    {
        _mixer.SetInputDeviceHint(
            _devices.ActiveNodeNameFragments,
            _devices.ActiveCapabilities?.OutputRouting ?? false,
            modelFallback: _devices.AttachedCount <= 1);
        return _mixer.SetDrivenUnit(_devices.ActiveInfo?.InstanceId) | _mixer.SetAttached(_devices.AttachedCount > 0)
            | _mixer.SetUnitXlrJacks(_devices.ActiveCapabilities?.XlrJacks);
    }

    /// <summary>
    /// One pass of the sweep. False when it did not run because the daemon is
    /// stopping or another pass (the timer's or a caller's) is still running.
    /// </summary>
    internal bool SweepOnce()
    {
        if (_stopping.IsCancellationRequested || Interlocked.CompareExchange(ref _sweepRunning, 1, 0) != 0) return false;
        try
        {
            CancellationToken stop = _stopping.Token;
            stop.ThrowIfCancellationRequested();
            if (GraphLost()) return true;
            stop.ThrowIfCancellationRequested();
            // Which input jacks the device actually has, so a client does
            // not offer a strip the hardware cannot feed. Before the driven
            // unit is handed on below, so the strips it carries are counted
            // by its own jacks, never by the unit before it.
            OpenXLR.Core.Devices.DeviceCapabilities? jacks = _devices.ActiveCapabilities;
            if (_mixer.SetInputJacks(jacks?.XlrInputs, jacks?.AuxInput)) Changed?.Invoke();
            stop.ThrowIfCancellationRequested();
            if (UpdateInputDeviceHint()) Changed?.Invoke();
            stop.ThrowIfCancellationRequested();
            // Which driven units run the microphone EQ, and each microphone's
            // EQ put where it runs: a unit that connected or left, a firmware
            // update, a binding that moved.
            if (_mixer.SetUnitEqSupport(_eqPort.Units()) | _mixer.SyncDeviceEq()) Changed?.Invoke();
            // Software DSP only for devices without the hardware version.
            _mixer.SetLowCutApplicable(!(_devices.ActiveCapabilities?.LowCut ?? false));
            stop.ThrowIfCancellationRequested();
            _mixer.SetClipGuardApplicable(!(_devices.ActiveCapabilities?.ClipGuard ?? false));
            stop.ThrowIfCancellationRequested();
            bool monitorChanged = _mixer.SyncOwnSinkLevels(out IReadOnlyList<string> restoredSinks);
            if (restoredSinks.Count > 0)
                _log.LogWarning("restored the expected level and mute of {n} OpenXLR sink(s) ({names}); something outside OpenXLR had changed them",
                    restoredSinks.Count, string.Join(", ", restoredSinks));
            // Collect what the plugins' own editors changed before the
            // healing pass below, so a chain that is about to be rebuilt
            // comes back with the values its editor last showed.
            // Desktop volume and mute changes are user settings too.
            stop.ThrowIfCancellationRequested();
            if (_mixer.SyncPluginControls() | monitorChanged | _mixer.SyncDeviceVolumes())
            {
                ScheduleSave();
                Changed?.Invoke();
            }
            // A combine leg that appeared after its cell was applied, or
            // that the session manager gave a remembered level after
            // OpenXLR set it, plays at the wrong level until the stored
            // fader is pushed onto it.
            stop.ThrowIfCancellationRequested();
            _mixer.EnsureCellLevels(out IReadOnlyList<string> heldSends);
            if (heldSends.Count > 0)
                _log.LogInformation("put {n} send(s) back on their faders ({cells}); something outside OpenXLR had changed them",
                    heldSends.Count, string.Join(", ", heldSends));
            stop.ThrowIfCancellationRequested();
            bool changed = _mixer.SyncStreams();
            if (_mixer.MigrateCaptureInputs(source => _devices.SupportedUnitFor(source)?.Id))
            {
                ScheduleSave();
                changed = true;
            }
            changed |= _mixer.EnforceDefaults(stop);
            stop.ThrowIfCancellationRequested();
            changed |= _mixer.EnsureInputFeeds() | _mixer.EnsureAuxRoute();
            stop.ThrowIfCancellationRequested();
            changed |= _mixer.EnsureFilterRoutes();
            stop.ThrowIfCancellationRequested();
            changed |= _mixer.EnsureMonitorRoutes() | _mixer.SyncDeviceList();
            stop.ThrowIfCancellationRequested();
            // Where ducking runs, after routing and the device status are
            // current: the unit when the setup fits it, software otherwise.
            // EnsureFilterRoutes above keeps repairing its buses and stages.
            changed |= SyncUnitDucking() | _mixer.SyncDucking();
            if (changed) Changed?.Invoke();
            stop.ThrowIfCancellationRequested();
            SyncOutputSelectors();
            // Once a minute: is the PulseAudio server close to its
            // open-file limit? Cheap (one /proc directory listing).
            if (++_sweepCount % 60 == 1)
            {
                string? warning = _mixer.PulseFileWarning();
                if (warning != Volatile.Read(ref _resourceWarning))
                {
                    if (warning is not null) _log.LogWarning("{msg}", warning);
                    Volatile.Write(ref _resourceWarning, warning);
                    Changed?.Invoke();
                }
            }
            if (_lastSweepError is not null)
            {
                _lastSweepError = null;
                _log.LogInformation("stream sweep recovered");
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
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
        finally
        {
            _progress.Mark();
            Volatile.Write(ref _sweepRunning, 0);
        }
        return true;
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
        // No default-device write may land after the graph is gone.
        _stopping.Cancel();
        Interlocked.CompareExchange(ref _stopStarted, Environment.TickCount64, 0);
        long spent = Environment.TickCount64 - Interlocked.Read(ref _stopStarted);
        double left = StopBudget.TotalMilliseconds - spent;
        if (left > 0) _stopDeadline.CancelAfter(TimeSpan.FromMilliseconds(left));
        else _stopDeadline.Cancel();
        _pipeWire.EndHelpersAt(_stopDeadline.Token);
        // A build attempt in progress finishes or fails on its helpers'
        // deadlines, which the stop limit above bounds; join it so the
        // timers and the graph it may have made are the ones stopped and
        // torn down below.
        await _buildRetry;
        ValueTask sweep = Interlocked.Exchange(ref _streamSweep, null)?.DisposeAsync() ?? ValueTask.CompletedTask;
        ValueTask meters = Interlocked.Exchange(ref _meterPush, null)?.DisposeAsync() ?? ValueTask.CompletedTask;
        // Timer.Dispose does not join a callback already in progress. Drain
        // it before saving or tearing down, even if the host's stop deadline
        // expired. Helpers keep their deadlines and the sweep stops between
        // phases instead of starting the rest of its repair work.
        await sweep;
        await meters;
        await _browserTitles.DisposeAsync();
        // Nothing is written to a unit at stop: it keeps the ducking it holds.
        _mixer.SetDeviceDuckingPort(null);
        try { await _defaultDefense.WaitAsync(TimeSpan.FromSeconds(4)); }   // one in-flight pactl call at most, 3 s
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { _log.LogWarning("default defense did not stop in time"); }
        if (_mixer.Built)
        {
            // Output mutes taken over by the next start stay in place, so a
            // restart while monitoring is paused lets no desktop sound out.
            // With the submixer turned off no start restores them; put them
            // back now, before the final save records what is still owned.
            if (!SubmixerEnabledAtNextStart) _mixer.ReleaseMonitorHardwareMutes();
            // The final save and the end of saving are one step. Tearing the
            // graph down empties the levels, mutes and monitor routing, so a
            // debounced save still pending here, or the flush in Dispose,
            // would write that empty state over the user's settings.
            if (_saves.Close(write: true) is string stopErr) _log.LogWarning("settings not saved at stop: {err}", stopErr);
            _mixer.TearDown();
            _log.LogInformation("submix graph torn down");
        }
        else _saves.Close(write: false);
        // One line for whatever the limit cut short.
        if (_stopDeadline.IsCancellationRequested)
            _log.LogWarning("stop: the audio server took longer than {s} s, so the helpers still running were ended{left}",
                (int)StopBudget.TotalSeconds, _pipeWire.ModulesLeft == 0 ? ""
                    : $" and {_pipeWire.ModulesLeft} OpenXLR module(s) stay loaded until the next start removes them");
        Interlocked.Exchange(ref _graphWatch, null)?.Dispose();
    }

    public string OpenBrowserSession(Guid owner) => _mixer.OpenBrowserSession(owner);

    private void PublishBrowserTabTitles()
    {
        if (_mixer.FlushBrowserTabTitles()) Changed?.Invoke();
    }

    public void RemoveBrowserSession(Guid owner)
    {
        if (_mixer.RemoveBrowserSession(owner)) Changed?.Invoke();
    }

    public string? SyncBrowserTabs(Guid owner, Command command)
    {
        string? invalid = CommandValidation.Check(command, _mixer, _ => null);
        if (invalid is not null) return invalid;
        try
        {
            if (_mixer.SyncBrowserTabs(owner, command.Session!, command.Tabs!)) Changed?.Invoke();
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return ex.Message; }
    }

    /// <summary>
    /// Whether activateBrowserTab may be passed to the tab's companion. It
    /// changes nothing here: the browser reports what it does in its tab list.
    /// </summary>
    public string? CheckBrowserActivation(Command cmd)
    {
        // Like syncBrowserTabs, this needs no audio graph: the companion
        // reports an output it cannot find as the tab's error.
        string? invalid = CommandValidation.Check(cmd, _mixer, _ => null);
        if (invalid is not null) return invalid;
        try
        {
            _mixer.CheckBrowserActivation(cmd.Session!, cmd.TabId!.Value, cmd.Site!);
            return null;
        }
        catch (InvalidOperationException ex) { return ex.Message; }
    }

    /// <summary>Apply a mixer command. Returns null on success, else an error.</summary>
    public string? Apply(Command cmd)
    {
        if (!_mixer.Built) return WaitingForAudio ? WaitingWarning : "mixer not built (start the daemon with --mixer)";
        string? invalid = CommandValidation.Check(cmd, _mixer, OpenXLR.Core.Mixing.PluginCatalog.Find);
        if (invalid is not null) return invalid;
        try
        {
            switch (cmd.Cmd)
            {
                case "assignBrowserTab":
                    if (cmd.Remember.GetBoolean())
                        _saves.RunSaved(() => _mixer.AssignBrowserTab(cmd.Session!, cmd.TabId!.Value, cmd.Site!, cmd.Channel, true, s => s.Save()));
                    else _mixer.AssignBrowserTab(cmd.Session!, cmd.TabId!.Value, cmd.Site!, cmd.Channel, false, _ => null);
                    Changed?.Invoke();
                    return null;
                case "setPreserveBrowserOutputs":
                    _saves.RunSaved(() => _mixer.SetPreserveBrowserOutputs(cmd.Value.GetBoolean(), s => s.Save()));
                    Changed?.Invoke();
                    return null;
                case "setInputBinding":
                {
                    string strip = cmd.Channel!;
                    // A supported unit's node is a source like any other, and
                    // the binding remembers the unit: its node is renamed when
                    // the unit is driven or its card profile switches, and the
                    // strip follows the unit's card to the new name. A strip
                    // bound to a driven additional unit carries its controls.
                    _saves.RunSaved(() => _mixer.SetInputBinding(strip, cmd.Source is null ? null
                        : new SourceBinding(cmd.Source, 0, cmd.CaptureMonoChannel ?? 0) { Unit = _devices.SupportedUnitFor(cmd.Source)?.Id },
                        settings => settings.Save()));
                    // The interface stays driven whatever the strips take; the
                    // Pro's direct path follows the channel carrying input 1.
                    SyncOutputSelectors();
                    Changed?.Invoke();
                    return null;
                }
                case "setCaptureSource":
                case "createCaptureChannel":
                case "createChannel":
                case "renameChannel":
                case "deleteChannel":
                case "createMix":
                case "renameMix":
                case "deleteMix":
                case "setLayoutOrder":
                case "setExclusiveGroup":
                case "deleteExclusiveGroup":
                case "setLayoutAppearance":
                    // Layout commands save synchronously, under the same gate
                    // as the debounced fader saves, and succeed only once the
                    // new layout is on disk.
                    _saves.RunSaved(() =>
                    {
                        Func<MixerSettings, string?> save = settings => settings.Save();
                        switch (cmd.Cmd)
                        {
                            case "setCaptureSource": _mixer.SetCaptureSource(cmd.Channel!, cmd.Source!, cmd.CapturePair, save, cmd.CaptureMonoChannel, _devices.SupportedUnitFor(cmd.Source!)?.Id); break;
                            case "createCaptureChannel": _mixer.CreateCaptureChannel(cmd.Name!, cmd.Source!, cmd.CapturePair, save, cmd.CaptureMonoChannel, _devices.SupportedUnitFor(cmd.Source!)?.Id); break;
                            case "createChannel": _mixer.CreateApplicationChannel(cmd.Name!, save, cmd.Icon); break;
                            case "renameChannel": _mixer.RenameApplicationChannel(cmd.Channel!, cmd.Name!, save); break;
                            case "deleteChannel": _mixer.DeleteApplicationChannel(cmd.Channel!, save); break;
                            case "createMix": _mixer.CreateMix(cmd.Name!, save, cmd.Kind == "monitor" ? MixKind.Monitor : MixKind.VirtualMic, cmd.Icon); break;
                            case "renameMix": _mixer.RenameMix(cmd.Mix!, cmd.Name!, save); break;
                            case "deleteMix": _mixer.DeleteMix(cmd.Mix!, save); break;
                            case "setExclusiveGroup": _mixer.SetExclusiveGroup(cmd.Group, cmd.Name!, cmd.Channels!, save); break;
                            case "deleteExclusiveGroup": _mixer.DeleteExclusiveGroup(cmd.Group!, save); break;
                            case "setLayoutAppearance":
                                _mixer.SetLayoutAppearance(cmd.Output is not null ? "output:" + cmd.Output
                                    : cmd.Channel is null ? "mix:" + cmd.Mix : "channel:" + cmd.Channel, cmd.Appearance!, save);
                                break;
                            default: _mixer.SetLayoutOrder(cmd.Channels!, cmd.Mixes!, save); break;
                        }
                    });
                    // Grouping the channel that carries the Pro's input 1 takes
                    // it off the hardware mic path; a capture channel made,
                    // removed or moved can change which channel carries it. A deleted
                    // mix can change the jacks' feed and direct microphone path.
                    if (cmd.Cmd is "setExclusiveGroup" or "deleteExclusiveGroup" or "createCaptureChannel" or "setCaptureSource" or "deleteChannel" or "deleteMix" or "setLayoutOrder")
                        SyncOutputSelectors();
                    Changed?.Invoke();
                    return null;
                case "cycleExclusiveGroup":
                    _mixer.CycleExclusiveGroup(cmd.Group!);
                    SyncOutputSelectors();   // the input 1 channel's mute may move the Pro's hardware mic path
                    break;
                case "setLevel":
                    if (cmd.Channel is null || cmd.Mix is null) return "setLevel: need 'channel' and 'mix'";
                    _mixer.SetLevel(cmd.Channel, cmd.Mix, cmd.Value.GetDouble());
                    break;
                case "setChannelMuted":
                    if (cmd.Channel is null || cmd.Mix is null) return "setChannelMuted: need 'channel' and 'mix'";
                    _mixer.SetChannelMuted(cmd.Channel, cmd.Mix, cmd.Value.GetBoolean());
                    SyncOutputSelectors();   // the input 1 channel's mute may move the Pro's hardware mic path
                    break;
                case "setChannelVolume":
                    if (cmd.Channel is null) return "setChannelVolume: need 'channel'";
                    _mixer.SetChannelVolume(cmd.Channel, cmd.Value.GetDouble());
                    break;
                case "setMixVolume":
                    if (cmd.Mix is null) return "setMixVolume: need 'mix'";
                    _mixer.SetMixVolume(cmd.Mix, cmd.Value.GetDouble());
                    break;
                case "setMixMuted":
                    if (cmd.Mix is null) return "setMixMuted: need 'mix'";
                    _mixer.SetMixMuted(cmd.Mix, cmd.Value.GetBoolean());
                    SyncOutputSelectors();
                    break;
                case "adjustOutputVolume": _mixer.AdjustOutputVolume(cmd.Device, cmd.Value.GetDouble()); break;
                case "setOutputDeviceVolume": _mixer.SetOutputDeviceVolume(cmd.Device, cmd.Value.GetDouble()); break;
                case "toggleOutputMute":
                    _mixer.ToggleOutputMute(cmd.Device);
                    SyncOutputSelectors();
                    break;
                case "setMainOutput": _mixer.SetMainOutput(cmd.Device!); break;
                case "routeFocusedApp":
                    _mixer.RouteFocusedApplication(DesktopFocusQuery.Read(), cmd.Channel!);
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
                case "setMonitorMuted":
                    _mixer.SetMonitorMuted(cmd.Value.GetBoolean());
                    SyncOutputSelectors();
                    break;
                case "setMonitorMuteMicrophones":
                    _mixer.SetMonitorMuteMicrophones(cmd.Value.GetBoolean());
                    SyncOutputSelectors();
                    break;
                case "toggleMonitorMuted":
                    _mixer.ToggleMonitorMuted();
                    SyncOutputSelectors();
                    break;
                case "setMonitorFeed":
                    if (cmd.Device is null || cmd.Mix is null) return "setMonitorFeed: need 'device' and 'mix'";
                    if (_mixer.SetMonitorFeed(cmd.Device, cmd.Mix) is string feedErr) return $"setMonitorFeed: {feedErr}";
                    SyncOutputSelectors();   // the jacks may now follow another mix's XLR 1 send
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
                case "renameInsert":
                    _mixer.RenameInsert(cmd.Channel!, cmd.InsertId!, cmd.Name!);   // checked by CommandValidation
                    break;
                case "setMixLatencyCompensation":
                    _mixer.SetMixLatencyCompensation(cmd.Value.GetBoolean());
                    break;
                case "soundCheck":
                    _mixer.SoundCheck(cmd.Channel!, cmd.Action!);   // both checked by CommandValidation
                    SyncOutputSelectors();   // a loop on the input 1 channel takes the Pro's hardware mic path off
                    Changed?.Invoke();
                    return null;   // a session is never saved
                case "setInserts":
                    _mixer.SetInserts(cmd.Channel!, cmd.Inserts!);   // both checked by CommandValidation
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
                case "setEq":
                    _mixer.SetEq(cmd.Channel!, MicEq.Read(cmd.Eq, out _)!);   // checked by CommandValidation
                    break;
                case "setEqEnabled":
                    _mixer.SetEqEnabled(cmd.Channel!, cmd.Value.GetBoolean());
                    break;
                case "setEqBand":
                    _mixer.SetEqBand(cmd.Channel!, cmd.Band!.Value, new EqBandChange(cmd.Enabled, cmd.Type, cmd.Frequency, cmd.Gain, cmd.Q));
                    break;
                case "setEqProcessing":
                    _mixer.SetEqProcessing(cmd.Channel!, cmd.Value.GetString()!);   // checked by CommandValidation
                    break;
                case "setDucking":
                case "setDuckingProcessing":
                    // Saved in mixer.json before it applies; a failed save
                    // leaves the setup and both backends as they were.
                    _saves.RunSaved(() =>
                    {
                        DuckingSetup current = _mixer.DuckingSnapshot().Setup;
                        DuckingSetup next = cmd.Cmd == "setDuckingProcessing"
                            ? current with { Processing = cmd.Value.GetString()! }
                            : DuckingChangeOf(cmd).ApplyTo(current);
                        _mixer.SetDuckingSetup(next, settings => settings.Save());
                    });
                    Changed?.Invoke();
                    return null;
                case "showInsertUi":
                    if (cmd.Channel is null || cmd.InsertId is null) return "showInsertUi: need 'channel' and 'insertId'";
                    _mixer.ShowInsertUi(cmd.Channel, cmd.InsertId, insert => EditorPolicy.BlockReason(insert.Kind, insert.Plugin));
                    return null;
                default:
                    return $"unknown mixer command '{cmd.Cmd}'";
            }
        }
        catch (Exception ex)
        {
            // Sound Check hand-back can fail after the pause has changed.
            // Keep that intent durable and visible even when cleanup reports an error.
            if (cmd.Cmd is "setMonitorMuted" or "toggleMonitorMuted" or "setMonitorMuteMicrophones" or "toggleOutputMute")
            {
                ScheduleSave();
                Changed?.Invoke();
            }
            return ex.Message;
        }
        Changed?.Invoke();
        ScheduleSave();
        return null;
    }

    /// <summary>Remove current uses and persist them before file-management actions become available.</summary>
    public (int Removed, int Chains, string? Error) RemovePluginInserts(IReadOnlySet<(string Kind, string Plugin)> plugins)
    {
        if (!_mixer.Built) return (0, 0, "The audio mixer must be running to remove plugins from its current chains.");
        (int Removed, int Chains, string? Error) result = (0, 0, null);
        try
        {
            _saves.RunSaved(() =>
            {
                if (_stopping.IsCancellationRequested)
                    throw new InvalidOperationException("The mixer is stopping.");
                result = _mixer.RemovePluginInserts(plugins, settings => settings.Save());
            });
        }
        catch (Exception ex)
        {
            Changed?.Invoke();
            return (0, 0, ex.Message);
        }
        Changed?.Invoke();
        return result;
    }

    /// <summary>The current mixer scene for saving into a profile, or null.</summary>
    public OpenXLR.Core.MixerScene? ExportScene() => _mixer.Built ? _mixer.ExportScene() : null;

    /// <summary>Recall a profile's mixer scene. Returns null on success.</summary>
    public string? ApplyScene(OpenXLR.Core.MixerScene scene)
    {
        lock (_sceneGate)
        {
            if (_waitingForAudio)
            {
                // Applied as soon as the graph is built; the latest recall wins.
                _pendingScene = scene;
                return null;
            }
        }
        if (!_mixer.Built) return "mixer not built (start the daemon with --mixer)";
        try { _mixer.ApplyScene(scene); }
        catch (Exception ex) { return ex.Message; }
        SyncOutputSelectors();
        Changed?.Invoke();
        ScheduleSave();
        return null;
    }

    private int _sweepCount;
    private string? _resourceWarning;

    /// <summary>Why the hardware input channels are silent although a source is present, or null.</summary>
    public string? InputWarning => _mixer.InputWarning;

    /// <summary>Whether a failed startup build is being retried.</summary>
    public bool WaitingForAudio { get { lock (_sceneGate) return _waitingForAudio; } }

    private const string WaitingWarning = "The mixer is waiting for the audio server (pipewire-pulse), which did not answer when the daemon started; it is built as soon as the server answers.";

    /// <summary>Why the mixer is not built although the submixer is on, or null.</summary>
    public string? GraphWarning => WaitingForAudio ? WaitingWarning : null;

    /// <summary>pipewire-pulse close to its open-file limit, or null.</summary>
    public string? ResourceWarning => Volatile.Read(ref _resourceWarning);

    /// <summary>
    /// Why the mixer settings are not on disk, or null while they are. A
    /// failed write (a full disk, a read-only home) keeps the change pending
    /// and retries with backoff; meanwhile every client sees this in the
    /// state so the user knows the window's changes would not survive a
    /// restart yet.
    /// </summary>
    public string? PersistenceWarning
        => _saves.Error is string err ? $"Mixer settings are not being saved ({err}); retrying." : null;

    private void ScheduleSave() => _saves.Schedule();

    public void Dispose()
    {
        _browserTitles.Dispose();
        _streamSweep?.Dispose();
        _meterPush?.Dispose();
        _saves.Dispose();
        Interlocked.Exchange(ref _graphWatch, null)?.Dispose();
    }
}
