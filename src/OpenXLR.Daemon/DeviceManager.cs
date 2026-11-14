using System.Text.Json;
using OpenXLR.Core;
using OpenXLR.Core.Devices;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Daemon;

/// <summary>
/// Owns the connected audio interface. A background loop connects (and
/// reconnects) the first supported device, polls its state, and raises
/// <see cref="StateChanged"/> when anything moves, including changes made by
/// other clients, so every UI/plugin stays in sync. Commands from clients are
/// applied here; the device's own lock serialises the USB transfers.
/// </summary>
public sealed partial class DeviceManager : BackgroundService
{
    private readonly ILogger<DeviceManager> _log;
    private readonly object _gate = new();
    private IAudioDevice? _device;
    // The clock for phantom settling and the EQ writer's spacing; tests drive it by hand.
    private readonly TimeProvider _time;
    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;
    private long NowMs => (long)((Int128)_time.GetTimestamp() * 1000 / _time.TimestampFrequency);
    private DeviceState? _last;
    // Replaced, never changed in place, so the client-facing readers below
    // can take it without the device lock.
    private volatile IReadOnlyList<DeviceInfo> _detected = [];

    /// <summary>
    /// The device half of the state as clients see it, written under the
    /// device lock each time it changes and read without it. The lock is held
    /// for every USB transfer, so a reader that took it would wait out a
    /// transfer that does not come back, and a reader that read the device
    /// itself would turn a wedged unit into an exception per state broadcast.
    /// </summary>
    private sealed record Published(StateMessage Message, DeviceInfo? Info, UnitEqSupport? Eq = null, UnitDuckingSupport? Ducking = null);
    private static readonly Published NothingDriven = new(new StateMessage { Connected = false }, null);
    private volatile Published _published = NothingDriven;

    // The last state read from a unit in this run, kept past a drop so a
    // unit that reconnects but answers no read still shows its values.
    private DeviceState? _lastGood;
    private string? _lastGoodId;
    // The unit whose reads fail, until one succeeds, it leaves the bus or
    // another unit is driven; and what clients are told meanwhile.
    private string? _unansweredId;
    private volatile string? _unansweredWarning;
    private readonly Func<IReadOnlyList<IAudioDevice>> _detect;
    // What the primary drives: the model OPENXLR_DEVICE names, or the unit
    // and model the last setActiveDevice chose. Only these two choose; a
    // unit taken while the chosen one is away is a stand-in.
    private ushort? _preferredPid;
    private string? _preferredInstance;   // the unit chosen in the picker, by instance id
    // The model OPENXLR_DEVICE names (or a test passes), which picks a
    // stand-in while a choice restored from the last run is not attached.
    private readonly ushort? _startupModel;
    // The primary keeps setActiveDevice's choice in active-device.json and
    // starts from it; an additional interface's manager does neither.
    private readonly bool _keepsChoice;
    // A choice read back at start whose unit has not been driven in this run
    // yet; until _choiceWaitUntil no other unit is taken in its place.
    private bool _choiceRestored;
    private long _choiceWaitUntil;
    private bool _choiceAbsentSaid;

    /// <summary>
    /// How long a restored choice is waited for at start before another unit
    /// stands in for it, so a chosen unit that enumerates after the others
    /// is driven from the outset instead of after a stand-in.
    /// </summary>
    internal static readonly TimeSpan RestoredChoiceWait = TimeSpan.FromSeconds(10);
    // Units enabled as additional interfaces: never taken over as a stand-in.
    private Func<string, bool> _additional = _ => false;
    // Set on an additional interface's manager: its last settings, defaults
    // and gain lock are kept under the unit's instance id, not the model's.
    private string? _stateStorageId;
    private volatile bool _suspended;

    /// <summary>
    /// Raised under the device lock just before a unit is opened, with its
    /// instance id, so an additional interface's manager lets go of it first.
    /// </summary>
    public event Action<string>? ClaimingDevice;

    internal void SetSessionStorageId(string id) => _stateStorageId = id;

    /// <summary>
    /// Which attached units are enabled as additional interfaces. The primary
    /// takes one of them only when setActiveDevice names it, never as a
    /// stand-in while its chosen unit is away. Read under the device lock, so
    /// it must not wait for anything.
    /// </summary>
    internal void SetAdditionalUnits(Func<string, bool> enabled) => _additional = enabled;

    /// <summary>Stop driving for good: save pending settings, release the unit and restore its card profile.</summary>
    internal void SuspendSession()
    {
        lock (_gate) { _suspended = true; FlushLastState(force: true); Drop(); RestoreCardProfile(); }
    }
    internal ServiceProgress Progress { get; } = new();

    // Whether this run builds the submixer (same decision MixerService
    // makes). Only then does the card need the pro-audio profile; in
    // hardware-control mode the stock layout (UCM split) stays in place.
    private readonly bool _submixer;

    public DeviceManager(ILogger<DeviceManager> log, IConfiguration config, IHostApplicationLifetime? lifetime = null)
        : this(log, config, DeviceRegistry.DetectAll, lifetime, keepsChoice: true) { }

    /// <summary>
    /// The daemon's event timeline. The primary's manager owns it; the mixer,
    /// the hub and the additional interfaces' managers write to the same one.
    /// </summary>
    public EventTimeline Timeline { get; }

    // Set when the host begins to stop. The loop runs on until this service's
    // own turn to stop, after the mixer's teardown; opening a unit meanwhile
    // (an eight second deadline, then reads that time out on a wedged one)
    // only spends the stop timeout.
    private volatile bool _noNewConnections;

    /// <summary>Keep polling a connected unit, but open none from now on.</summary>
    internal void StopConnecting() => _noNewConnections = true;

    /// <summary>The detection source is injectable so the reconnect and set-aside logic can be driven by a fake device.</summary>
    /// <param name="preferredModel">The product id OPENXLR_DEVICE names; read from the environment when null. Tests pass it.</param>
    /// <param name="keepsChoice">
    /// Save setActiveDevice's choice and start from the saved one, which wins
    /// over <paramref name="preferredModel"/>: the primary's manager only.
    /// </param>
    /// <param name="timeline">The timeline to write to; a new one when null, as for the primary.</param>
    internal DeviceManager(ILogger<DeviceManager> log, IConfiguration config, Func<IReadOnlyList<IAudioDevice>> detect,
        IHostApplicationLifetime? lifetime = null, TimeProvider? time = null, ushort? preferredModel = null, bool keepsChoice = false,
        EventTimeline? timeline = null)
    {
        _time = time ?? TimeProvider.System;
        Timeline = timeline ?? new EventTimeline(_time);
        lifetime?.ApplicationStopping.Register(StopConnecting);
        _detect = detect;
        _log = log;
        string? want = Environment.GetEnvironmentVariable("OPENXLR_DEVICE");
        if (preferredModel is not null) _preferredPid = preferredModel;
        else if (want is not null && ushort.TryParse(want, System.Globalization.NumberStyles.HexNumber, null, out ushort pid))
            _preferredPid = pid;
        _startupModel = _preferredPid;
        _keepsChoice = keepsChoice;
        if (keepsChoice)
        {
            if (ActiveDeviceChoice.Load(out string? choiceError) is { } chosen)
            {
                _preferredInstance = chosen;
                _preferredPid = ActiveDeviceChoice.ProductId(chosen);
                _choiceRestored = true;
                _choiceWaitUntil = NowMs + (long)RestoredChoiceWait.TotalMilliseconds;
                _log.LogInformation("{device}, the interface setActiveDevice chose last, is driven again once attached", chosen);
            }
            else if (choiceError is not null)
                _log.LogWarning("the interface chosen last could not be read from {path} ({error}); choosing as if none was", ActiveDeviceChoice.PathName, choiceError);
        }
        bool launchDefault = config.GetValue("mixer", false) ||
                             Environment.GetEnvironmentVariable("OPENXLR_BUILD_MIXER") == "1";
        _submixer = OpenXLR.Core.DaemonSettings.SubmixerEnabled(launchDefault);
    }

    /// <summary>Every supported interface currently attached, for client pickers.</summary>
    public IReadOnlyList<(string UsbId, string Name, bool Active)> Detected()
    {
        // Part of every state message, so it does not wait for the device lock.
        IReadOnlyList<DeviceInfo> detected = _detected;
        string? active = _published.Info?.InstanceId;
        return [.. detected.Select(d => (
            d.InstanceId,
            d.DisplayName + (detected.Count(x => x.ProductId == d.ProductId) > 1 ? $" ({d.Location?.Port ?? d.InstanceId})" : ""),
            active == d.InstanceId))];
    }

    /// <summary>
    /// Switch to another attached supported device: one unit by its instance
    /// id ("vvvv:pppp@hash"), or the first unit of a model ("vvvv:pppp" or "pppp").
    /// The connect loop picks it up within a poll tick. Null on success.
    /// </summary>
    public string? SetActiveDevice(string usbId)
    {
        string model = usbId.Split('@')[0];
        string pidPart = model.Contains(':') ? model.Split(':')[1] : model;
        if (!ushort.TryParse(pidPart, System.Globalization.NumberStyles.HexNumber, null, out ushort pid))
            return $"setActiveDevice: bad device id '{usbId}'";
        lock (_gate)
        {
            var target = _detected.FirstOrDefault(d => usbId.Contains('@') ? d.InstanceId == usbId
                : d.ProductId == pid && (!model.Contains(':') || model.Equals($"{d.VendorId:x4}:{d.ProductId:x4}", StringComparison.OrdinalIgnoreCase)));
            if (target is null)
                return $"setActiveDevice: no attached supported device '{usbId}'";
            _preferredPid = pid;
            _preferredInstance = target.InstanceId;
            _choiceRestored = false;
            // Kept for the next start; a failed write leaves this run's choice in place.
            if (_keepsChoice && ActiveDeviceChoice.Save(target.InstanceId) is { } saveError)
                _log.LogWarning("setActiveDevice: {target} is driven, but the choice was not saved for the next start ({error})", target.DisplayName, saveError);
            if (_device is not null && _device.Info.InstanceId != target.InstanceId)
            {
                // The one way the primary moves on purpose, said in the journal.
                _log.LogInformation("setActiveDevice: driving {target} instead of {current}", target.DisplayName, _device.Info.DisplayName);
                Timeline.Record(TimelineKind.Device, $"switching to {target.DisplayName} from {_device.Info.DisplayName}");
                // Settings still waiting for the debounce belong to the old
                // device; write them before its snapshot goes. Drop disposes
                // the transport even when disconnect fails, so its USB
                // helper does not outlive the switch, and reports the handoff.
                FlushLastState(force: true);
                Drop();
                RestoreCardProfile();   // the parked UCM split comes back with the device released
            }
        }
        return null;
    }

    /// <summary>
    /// The attached supported interface whose card publishes a PipeWire node,
    /// as its instance id and name, or null (see <see cref="OpenXLR.Core.Mixing.InterfaceNodes.Owner"/>).
    /// Every attached unit counts, driven or not.
    /// </summary>
    public (string Id, string Name)? SupportedUnitFor(string nodeName)
        => OpenXLR.Core.Mixing.InterfaceNodes.Owner(nodeName, _detected);   // read for every state message, without the device lock

    /// <summary>How many supported interfaces the last detection found, driven or not.</summary>
    public int AttachedCount { get { lock (_gate) return _detected.Count; } }

    /// <summary>
    /// The connected unit's node-name fragments, most specific first, or none.
    /// The model fragment is there only while no other attached unit could
    /// answer to it; an additional unit, which runs only next to another
    /// one, always goes by its own serial.
    /// </summary>
    public IReadOnlyList<string> ActiveNodeNameFragments
    {
        get { lock (_gate) return _device is { Connected: true } ? FragmentsLocked(_device.Info) : []; }
    }

    private IReadOnlyList<string> FragmentsLocked(DeviceInfo info)
        => _stateStorageId is not null ? [info.NodeNameFragment] : info.FragmentsAmong(_detected);

    /// <summary>Identity of the connected device, or null; cheap to poll.</summary>
    public DeviceInfo? ActiveInfo
    {
        get { lock (_gate) return _device is { Connected: true } ? _device.Info : null; }
    }

    /// <summary>Capabilities of the connected device, or null; cheap to poll.</summary>
    public DeviceCapabilities? ActiveCapabilities
    {
        get { lock (_gate) return _device is { Connected: true } ? _device.Capabilities : null; }
    }

    // Software gain lock (Wave Link's Gain Lock, app-side on these devices):
    // per-device usbIds persisted so the lock survives restarts. The daemon
    // stamps the flag onto every state snapshot and rejects gain writes while
    // set, so every client honors it without needing its own logic.
    private readonly HashSet<string> _gainLocked = LoadGainLocks();

    private static string GainLockPath => OpenXlrPaths.ConfigFile("gainlock.json");

    private static HashSet<string> LoadGainLocks()
    {
        try { return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(GainLockPath)) ?? []; }
        catch (Exception) { return []; }
    }

    // Every device manager writes the one gain-lock file; each changes only
    // its own unit's entry in what is on disk.
    private static readonly object GainLockWriteGate = new();
    private void SaveGainLocks()
    {
        try
        {
            lock (GainLockWriteGate)
            {
                var saved = LoadGainLocks();
                if (_device is { } device)
                {
                    string id = DevId(device);
                    if (_gainLocked.Contains(id)) saved.Add(id); else saved.Remove(id);
                }
                OpenXlrPaths.WriteAtomic(GainLockPath, JsonSerializer.Serialize(saved));
            }
        }
        catch (Exception) { /* best effort */ }
    }

    private string DevId(IAudioDevice d) => _stateStorageId ?? $"{d.Info.VendorId:x4}:{d.Info.ProductId:x4}";

    private bool GainIsLocked => _device is not null && _gainLocked.Contains(DevId(_device));

    // The LEDs a user set through setLed, per unit by the same id as the
    // gain lock, and only the fields set: a connect writes them back, so a
    // unit set elsewhere meanwhile comes back as OpenXLR left it. A unit
    // never set from OpenXLR, or reset to its factory LEDs, has no entry and
    // is left as it is.
    private static string LedPath => OpenXlrPaths.ConfigFile("leds.json");
    private static readonly object LedWriteGate = new();
    private static readonly JsonSerializerOptions LedJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    internal static Dictionary<string, DeviceLedChange> LoadLeds()
    {
        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, DeviceLedChange>>(File.ReadAllText(LedPath), LedJson) ?? [];
            // A hand edit outside the command's ranges is dropped field by field.
            return saved.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => Valid(pair.Value), StringComparer.Ordinal);
        }
        catch (Exception) { return []; }

        static DeviceLedChange Valid(DeviceLedChange led) => new()
        {
            RingBrightness = led.RingBrightness is >= 0 and <= 100 ? led.RingBrightness : null,
            MeterBrightness = led.MeterBrightness is >= 0 and <= 100 ? led.MeterBrightness : null,
            GainColour = WaveFxLed.Normalize(led.GainColour),
            HeadphoneColour = WaveFxLed.Normalize(led.HeadphoneColour),
            MixColour = WaveFxLed.Normalize(led.MixColour),
        };
    }

    // Every device manager writes the one file; each changes only its own unit's entry.
    private void SaveLed(string id, DeviceLedChange? led)
    {
        try
        {
            lock (LedWriteGate)
            {
                Dictionary<string, DeviceLedChange> saved = LoadLeds();
                if (led is null || led.IsEmpty) saved.Remove(id); else saved[id] = led;
                OpenXlrPaths.WriteAtomic(LedPath, JsonSerializer.Serialize(saved, LedJson));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("the LED settings were not saved: {msg}", ex.Message);
        }
    }

    // The Pro's firmware mutes an XLR input for ~13 s around every 48V
    // transition (anti-thump) and unmutes it itself, ignoring host unmutes
    // meanwhile. Stamp a settling flag for 15 s after each phantom write so
    // clients can present that hold instead of a stuck mute button.
    private static readonly TimeSpan PhantomSettleWindow = TimeSpan.FromSeconds(15);
    private DateTime _phantomWroteAt = DateTime.MinValue;
    private DateTime _phantomWroteAt2 = DateTime.MinValue;

    // Whole seconds left in a settle window (0 outside it). The 100 ms poll
    // notices each one-second step, so clients get a ticking countdown from
    // the ordinary state broadcasts.
    private int SettleSecondsLeft(DateTime wroteAt)
    {
        double left = (PhantomSettleWindow - (UtcNow - wroteAt)).TotalSeconds;
        return left > 0 ? (int)Math.Ceiling(left) : 0;
    }

    // The hold ends when the firmware's own unmute shows up in the readback;
    // the window is only a cap (and the grace covers the moment right after
    // the write, before the firmware's mute is first observed).
    private static readonly TimeSpan PhantomMuteGrace = TimeSpan.FromSeconds(2);

    private DeviceState Stamp(DeviceState s)
    {
        DateTime now = UtcNow;
        bool held1 = SettleSecondsLeft(_phantomWroteAt) > 0
                     && (s.Mute || now - _phantomWroteAt < PhantomMuteGrace);
        bool held2 = SettleSecondsLeft(_phantomWroteAt2) > 0
                     && (s.Mute2 || now - _phantomWroteAt2 < PhantomMuteGrace);
        return s with
        {
            GainLocked = GainIsLocked,
            PhantomSettling = held1,
            PhantomSettling2 = held2,
            PhantomSettleSeconds = held1 ? SettleSecondsLeft(_phantomWroteAt) : 0,
            PhantomSettleSeconds2 = held2 ? SettleSecondsLeft(_phantomWroteAt2) : 0,
        };
    }

    /// <summary>Raised (off the poll loop) whenever the pushed state should change.</summary>
    public event Action<StateMessage>? StateChanged;

    /// <summary>
    /// Raised (with the vvvv:pppp id) when a device connects fresh: at daemon
    /// start, after it was off the bus (a replug or a power cycle, when its
    /// firmware may have reset), or after a switch to a different model.
    /// Not raised for the reconnect that follows a transient USB error,
    /// so a recall-on-connect profile does not undo later manual changes.
    /// Handlers run under the manager's lock and must only hand off work.
    /// </summary>
    public event Action<string>? DeviceArrived;
    internal readonly record struct Connection(string DeviceId, long Generation);
    private long _connectionGeneration;

    internal Connection? CurrentConnection
    {
        get
        {
            lock (_gate) return _device is { Connected: true }
                ? new(DevId(_device), _connectionGeneration) : null;
        }
    }

    // A queued restoration belongs to one arrival, including when the same
    // model leaves and returns. Keep the identity check and the restoration
    // together so a device switch cannot redirect the hardware writes.
    // Callers must do slow mixer graph work outside this device lock.
    internal bool WithConnection(Connection connection, Action restore)
    {
        lock (_gate)
        {
            if (CurrentConnection != connection) return false;
            restore();
            return true;
        }
    }

    private bool _everConnected;
    private string? _lastInstance;
    // Every unit this run has driven, where it was last seen, and which have
    // since been off the bus: a return after that, or at another USB address
    // (an unplug that fell between two scans), is a power cycle for the unit,
    // even when the daemon drove another interface meanwhile.
    private readonly Dictionary<string, UsbLocation> _drivenLocations = [];
    private readonly HashSet<string> _drivenInstances = [];
    private readonly HashSet<string> _absentInstances = [];
    private bool AddressChanged(DeviceInfo info) => info.Location is { } now
        && _drivenLocations.TryGetValue(info.InstanceId, out var before)
        && (before.Bus != now.Bus || before.Address != now.Address);

    /// <summary>
    /// The device half of the state, as last read by the poll loop or after a
    /// command. Never touches USB and never waits for a transfer in progress.
    /// </summary>
    public StateMessage Snapshot() => _published.Message;

    /// <summary>The driven unit as clients were last shown it; read without the device lock.</summary>
    internal DeviceInfo? PublishedInfo => _published.Info;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            SweepOnce();
            // An EQ posted while no write is due wakes the loop at once.
            if (!_unitMetersWatched)
            {
                await NapAsync(PollIntervalMs, stop);
                continue;
            }
            // While a client watches the unit's meters, they are read once
            // more halfway to the next poll: about 20 reads a second of block
            // 0x0002 alone instead of 10, and only for as long as asked.
            if (await NapAsync(PollIntervalMs / 2, stop)) continue;
            ReadUnitMetersBetweenPolls();
            await NapAsync(PollIntervalMs / 2, stop);
        }
        FlushLastState(force: true);
        Drop();
        RestoreCardProfile();
    }

    /// <summary>The device loop's wait between polls, in milliseconds, unless something wakes it.</summary>
    private const int PollIntervalMs = 100;

    /// <summary>Wait up to <paramref name="ms"/> for a wake; true when woken or stopping, so the loop polls (or ends) at once.</summary>
    private async Task<bool> NapAsync(int ms, CancellationToken stop)
    {
        try { return await _wake.WaitAsync(ms, stop); }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { return true; }
    }

    /// <summary>One pass of the device loop: connect, poll, park the card profile, persist. Tests drive it directly.</summary>
    internal void SweepOnce()
    {
        if (_suspended) { Progress.Mark(); return; }
        try
        {
            EnsureConnected();
            Progress.Mark();
            WriteEq();
            WriteDynamics();
            WriteDucking();
            PollOnce();
            Progress.Mark();
            TryParkCardProfile();
            FlushLastState(force: false);
        }
        catch (UsbHungException ex)
        {
            NoteHung(ex);
        }
        catch (Exception ex)
        {
            // Once per distinct message at warning; the retries that follow
            // every few seconds go to debug.
            if (ex.Message != _lastLoopError)
            {
                _log.LogWarning("device loop: {msg}", ex.Message);
                Timeline.Record(TimelineKind.Device, $"device loop: {ex.Message}");
            }
            else _log.LogDebug("device loop: {msg}", ex.Message);
            _lastLoopError = ex.Message;
            // The last good read is what an unplugged unit was left at.
            FlushLastState(force: true);
            Drop();
            // A device that opens but answers every read badly would otherwise
            // be dropped and reopened on the next tick, ten times a second,
            // a USB helper process forked and killed each time. Wait, and
            // wait longer while the failures go on.
            _pollFailures = Math.Min(_pollFailures + 1, PollRetryDoublings + 1);
            long delay = (long)PollRetryDelay.TotalMilliseconds << (_pollFailures - 1);
            _reconnectNotBefore = Environment.TickCount64 + delay;
        }
        Progress.Mark(); // a completed failed poll is responsive too
    }

    /// <summary>After a failed poll: the first wait before the device is reopened, doubled on each failure that follows. Tests shorten it.</summary>
    internal static TimeSpan PollRetryDelay = TimeSpan.FromSeconds(2);
    private const int PollRetryDoublings = 4;   // 2, 4, 8, 16, 32 s
    private int _pollFailures;

    // After a transfer that never returned the helper process was killed;
    // a device that hangs at once again is not worth a tight loop, so wait
    // before retrying. Tests shorten it.
    internal static TimeSpan HungReconnectDelay = TimeSpan.FromSeconds(10);
    /// <summary>After an ordinary open failure (permissions, a busy device): shorter, still not a tight loop.</summary>
    internal static TimeSpan OpenRetryDelay = TimeSpan.FromSeconds(2);
    private long _reconnectNotBefore;   // monotonic ms
    private string? _lastLoopError;

    /// <summary>
    /// The last transfer that never returned, kept for the diagnostics
    /// archive (Options, SUPPORT, Collect diagnostics) so a tester can hand
    /// over the exact setup packet, payload, timing and versions without
    /// digging through the journal.
    /// </summary>
    private string? _lastUsbFault;

    private readonly HungTransferPolicy _hung = new();
    private volatile string? _setAsideWarning;

    /// <summary>
    /// A condition about the hardware the user should see, or null: a device
    /// set aside after repeated hung transfers, one that does not answer
    /// reads, or a driven card whose profile has no microphone input. Read
    /// without the device lock, like <see cref="Snapshot"/>.
    /// </summary>
    public string? Warning
        => string.Join(" ", new[] { _setAsideWarning, _unansweredWarning, CardProblem }.Where(w => w is not null)) is { Length: > 0 } w ? w : null;

    private void NoteHung(UsbHungException ex)
    {
        IAudioDevice? dev = _device;
        string device = dev is null ? "no device"
            : $"{dev.Info.DisplayName} {dev.Info.VendorId:x4}:{dev.Info.ProductId:x4}";
        _lastUsbFault = $"{DateTime.UtcNow:O} {device}, kernel {KernelRelease()}: {ex.Message}";
        bool setAside = dev is not null && _hung.NoteHung(dev.Info.InstanceId);
        // The name and the count only: the transfer's payload stays in the
        // journal and the vendor block dump, where the archive masks it.
        Timeline.Record(TimelineKind.Usb, dev is null ? "a USB transfer never returned"
            : $"a USB transfer to {dev.Info.DisplayName} never returned (hung transfer {_hung.HungCount(dev.Info.InstanceId)} of {HungTransferPolicy.Limit}"
              + (setAside ? "; it is set aside)" : "; it is reconnected)"));
        if (setAside)
        {
            // A device that hangs on every transfer is not worth a reconnect
            // every ten seconds; everything else (the mixer, another
            // interface) keeps running.
            _setAsideWarning = $"{dev!.Info.DisplayName} hung {HungTransferPolicy.Limit} USB transfers in this run and is no longer driven; " +
                               "unplug it and plug it back in, or restart the daemon, to try again.";
            _log.LogError("{fault}. That is hung transfer {n} of {limit}: the device is set aside until it is replugged or the daemon restarts. " +
                          "Please collect diagnostics (Options, SUPPORT) and attach the archive to an issue.",
                _lastUsbFault, HungTransferPolicy.Limit, HungTransferPolicy.Limit);
        }
        else
        {
            _log.LogError("{fault}. The device is dropped and reconnected in {s} s (hung transfer {n} of {limit} before it is set aside). " +
                          "Please collect diagnostics (Options, SUPPORT) and attach the archive to an issue.",
                _lastUsbFault, (int)HungReconnectDelay.TotalSeconds,
                dev is null ? 0 : _hung.HungCount(dev.Info.InstanceId), HungTransferPolicy.Limit);
        }
        _reconnectNotBefore = Environment.TickCount64 + (long)HungReconnectDelay.TotalMilliseconds;
        Drop();
    }

    /// <summary>How long after a USB reset no other unit stands in for the one reset. Tests shorten it.</summary>
    internal static TimeSpan ResetReturnWindow = TimeSpan.FromSeconds(20);
    /// <summary>How long after a USB reset the unit is first reopened. Tests shorten it.</summary>
    internal static TimeSpan ResetReopenDelay = TimeSpan.FromSeconds(2);
    // The unit reset on purpose, until it is back on the bus or the window ends.
    private (string Instance, long Until)? _resetReturn;

    /// <summary>
    /// Reset the driven unit on the USB bus, for a model that brings its
    /// capture back this way (<see cref="DeviceCapabilities.CaptureRecovery"/>),
    /// and drop it: its card re-enumerates and the loop reconnects it as
    /// after a replug, with no other unit standing in meanwhile. Only the
    /// unit named by <paramref name="instance"/>, so a switch that came in
    /// between is never reset. Null on success, else why not.
    /// </summary>
    public string? ResetUnitUsb(string instance)
    {
        lock (_gate)
        {
            if (_device is not { Connected: true } device || device.Info.InstanceId != instance)
                return "the interface is no longer the one driven";
            if (!device.Capabilities.CaptureRecovery) return $"{device.Info.DisplayName} is not reset on USB";
            int code;
            try { code = device.ResetUsb(); }
            catch (UsbHungException ex)
            {
                NoteHung(ex);
                return ex.Message;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                return ex.Message;
            }
            // -5, LIBUSB_ERROR_NOT_FOUND: the unit re-enumerated, which is the point.
            if (code is not (0 or -5)) return $"the USB reset of {device.Info.DisplayName} failed (libusb error {code})";
            _log.LogInformation("{dev} was reset on USB (libusb {code}); reconnecting it", device.Info.DisplayName, code);
            Timeline.Record(TimelineKind.Usb, $"{device.Info.DisplayName} was reset on USB");
            _resetReturn = (instance, NowMs + (long)ResetReturnWindow.TotalMilliseconds);
            // Give the kernel the moment it takes to let go of the unit and
            // bind it again, so the reopen does not catch the old device.
            _reconnectNotBefore = Environment.TickCount64 + (long)ResetReopenDelay.TotalMilliseconds;
            FlushLastState(force: true);
            Drop();
            return null;
        }
    }

    private bool _scanned;

    /// <summary>The units a scan found that the one before did not, and those it no longer found.</summary>
    private void NoteAttachedLocked(IReadOnlyList<DeviceInfo> before, IReadOnlyList<DeviceInfo> now)
    {
        // The first scan is what was attached at start, not a change.
        if (!_scanned) { _scanned = true; return; }
        foreach (DeviceInfo unit in now.Where(d => !before.Any(b => b.InstanceId == d.InstanceId)))
            Timeline.Record(TimelineKind.Device, $"{unit.DisplayName} came onto the USB bus");
        foreach (DeviceInfo unit in before.Where(b => !now.Any(d => d.InstanceId == b.InstanceId)))
            Timeline.Record(TimelineKind.Device, $"{unit.DisplayName} left the USB bus");
    }

    private static string KernelRelease()
    {
        try { return File.ReadAllText("/proc/sys/kernel/osrelease").Trim(); }
        catch (Exception) { return "unknown"; }
    }

    private void EnsureConnected()
    {
        lock (_gate)
        {
            if (_suspended) return;
            IReadOnlyList<IAudioDevice> all = _detect();
            IReadOnlyList<DeviceInfo> before = _detected;
            _detected = [.. all.Select(d => d.Info)];
            // The primary's scan sees every supported unit: one that came onto
            // the bus or left it goes on the timeline, driven or not.
            if (_keepsChoice) NoteAttachedLocked(before, _detected);
            // A unit that left the bus is no longer the one not answering.
            if (_unansweredId is { } silent && !all.Any(d => d.Info.InstanceId == silent)) ClearUnansweredLocked();
            // The unit went away under its transport without a drop: clients
            // stop seeing it as driven now, not when the next state is read.
            if (_device is { Connected: false } gone && _published.Message.Connected)
            {
                // No failed transfer says this anywhere else.
                _log.LogWarning("{dev} closed without a failed USB transfer (its USB helper exited); its state is gone until it reconnects",
                    gone.Info.DisplayName);
                Timeline.Record(TimelineKind.Device, $"{gone.Info.DisplayName} closed: its USB helper exited");
                RaiseFromLocked();
                ForgetEq();
                ForgetDynamics();
                ForgetDucking();
            }
            foreach (string drivenInstance in _drivenInstances)
                if (!all.Any(d => d.Info.InstanceId == drivenInstance)) _absentInstances.Add(drivenInstance);
            // A device set aside for hanging gets a fresh start once it has
            // been off the bus and back (its firmware restarted with it).
            foreach (string aside in _hung.SetAside.ToList())
            {
                if (all.FirstOrDefault(d => d.Info.InstanceId == aside) is { } returned
                    && (_absentInstances.Contains(aside) || AddressChanged(returned.Info)))
                {
                    _hung.Returned(aside);
                    _setAsideWarning = null;
                    _log.LogInformation("{instance} is back on the bus after being set aside; driving it again", aside);
                    Timeline.Record(TimelineKind.Usb, $"{returned.Info.DisplayName} is back on the bus after being set aside");
                }
            }
            if (_device is { Connected: true } && !StandInGivesWayLocked(all)) return;
            if (_noNewConnections || Environment.TickCount64 < _reconnectNotBefore) return;
            // A unit reset on purpose is off the bus for a moment: no other
            // unit stands in for it meanwhile, so its strips are not handed
            // to another interface and back. It is the one opened next.
            if (_resetReturn is { } reset && NowMs >= reset.Until) _resetReturn = null;
            string? awaited = _resetReturn?.Instance;
            List<IAudioDevice> usable = [.. all.Where(d => !_hung.IsSetAside(d.Info.InstanceId))];
            // A stand-in is never a unit enabled as an additional interface:
            // enabling one leaves the primary where it was.
            List<IAudioDevice> free = [.. usable.Where(d => !_additional(d.Info.InstanceId))];
            // The chosen unit first. While it is away and other units are
            // attached, none of them is taken over: one may be driven as an
            // additional interface. A lone unit is driven whatever its id,
            // so a unit without a serial still connects after a port change.
            IAudioDevice? dev = awaited is not null ? usable.FirstOrDefault(d => d.Info.InstanceId == awaited)
                : _preferredInstance is { } instance && usable.FirstOrDefault(d => d.Info.InstanceId == instance) is { } chosen
                ? chosen
                : _choiceRestored ? RestoredStandInLocked(all, free)
                : _preferredInstance is not null && all.Count > 1
                    ? null
                    : _preferredPid is ushort pid
                        ? free.FirstOrDefault(d => d.Info.ProductId == pid) ?? free.FirstOrDefault()
                        : free.FirstOrDefault();
            if (dev is null) return;                    // nothing usable attached; try again next tick
            if (dev.Info.InstanceId == _preferredInstance) _choiceRestored = false;
            ClaimingDevice?.Invoke(dev.Info.InstanceId);
            try { dev.Connect(); }
            catch (Exception ex)
            {
                // The candidate owns a transport, and a helper process once it
                // tried to open: release it, and do not hammer a device that
                // cannot be opened (a udev rule not yet applied) ten times a
                // second.
                dev.Dispose();
                if (ex is not UsbHungException) _reconnectNotBefore = Environment.TickCount64 + (long)OpenRetryDelay.TotalMilliseconds;
                throw;
            }
            if (dev.Info.InstanceId == awaited) _resetReturn = null;
            _device = dev;
            _last = null;
            ForgetCardArrivalsLocked();
            ForgetEq();
            ForgetDynamics();
            ForgetDucking();
            ConnectDuckingLocked(dev);
            ConnectUnitGainLockLocked(dev);
            ConnectLedLocked(dev);
            ConnectRawCaptureLocked(dev);
            if (dev.EqSupport is { Ready: false } eqSupport && eqSupport.Note is { } eqNote)
                _log.LogInformation("{dev}: {note}", dev.Info.DisplayName, eqNote);
            if (_unansweredId is not null && _unansweredId != dev.Info.InstanceId) ClearUnansweredLocked();
            _log.LogInformation("connected {dev}", dev.Info.DisplayName);
            Timeline.Record(TimelineKind.Device, $"connected {dev.Info.DisplayName}");
            if (dev.ConnectionNote is string note) _log.LogWarning("{dev}: {note}", dev.Info.DisplayName, note);
            _lastLoopError = null;
            bool powerCycled = _absentInstances.Remove(dev.Info.InstanceId) || AddressChanged(dev.Info);
            bool fresh = !_everConnected || powerCycled || _lastInstance != dev.Info.InstanceId;
            // The UCM split profile that hides the raw multichannel nodes exists
            // for the Wave XLR Pro only, so only its card is parked; every
            // driven card is checked for a capture node.
            EnsureCardProfile(dev.Info, parksUcm: dev.Capabilities.OutputRouting, fresh);
            if (fresh && !dev.Capabilities.RetainsSettings)
            {
                // A device without settings memory: what it answers now is
                // either what it booted with (firmware defaults, worth
                // recording when we know the bus lost it) or what it was
                // left at. Either way nothing is persisted until the
                // arrival handler has restored the last settings, so the
                // boot values never overwrite them.
                _restorePending = true;
                _dirtyAt = null;
                try
                {
                    _last = Stamp(dev.ReadState());
                    if (powerCycled) DeviceStateStore.SaveDefaults(DevId(dev), _last);
                }
                catch (Exception ex) when (ex is not UsbHungException)
                {
                    _log.LogWarning("initial read of {dev}: {msg}", dev.Info.DisplayName, ex.Message);
                }
            }
            RaiseFromLocked();                          // push the initial state
            _everConnected = true;
            _lastInstance = dev.Info.InstanceId;
            if (dev.Info.Location is { } location) _drivenLocations[dev.Info.InstanceId] = location;
            _drivenInstances.Add(dev.Info.InstanceId);
            if (fresh)
            {
                _connectionGeneration++;
                DeviceArrived?.Invoke(DevId(dev));
            }
        }
    }

    /// <summary>
    /// What to drive while the choice restored from the last run is not
    /// attached. For <see cref="RestoredChoiceWait"/> after the start nothing,
    /// unless the one attached unit of the chosen model is there under
    /// another id (a unit without a serial moved to another port). After the
    /// wait a stand-in by the rules a start without a choice uses: the model
    /// OPENXLR_DEVICE names, else the first unit. The stand-in gives way once
    /// the chosen unit is attached (<see cref="StandInGivesWayLocked"/>).
    /// </summary>
    private IAudioDevice? RestoredStandInLocked(IReadOnlyList<IAudioDevice> all, List<IAudioDevice> free)
    {
        List<IAudioDevice> sameModel = [.. free.Where(d => d.Info.ProductId == _preferredPid)];
        if (NowMs < _choiceWaitUntil) return sameModel.Count == 1 && all.Count(d => d.Info.ProductId == _preferredPid) == 1 ? sameModel[0] : null;
        IAudioDevice? standIn = (_startupModel is ushort model ? free.FirstOrDefault(d => d.Info.ProductId == model) : null) ?? free.FirstOrDefault();
        if (standIn is not null && !_choiceAbsentSaid)
        {
            _choiceAbsentSaid = true;
            _log.LogWarning("{chosen}, the interface chosen last, is not attached; driving {standIn} until it is", _preferredInstance, standIn.Info.DisplayName);
        }
        return standIn;
    }

    /// <summary>
    /// Whether the primary is the unit its choice names: the one the last
    /// setActiveDevice chose, else a unit of the model OPENXLR_DEVICE names,
    /// else whichever it drives.
    /// </summary>
    private bool IsChosenLocked(DeviceInfo info)
        => _preferredInstance is { } instance ? info.InstanceId == instance
            : _preferredPid is not ushort pid || info.ProductId == pid;

    /// <summary>
    /// A connected stand-in, driven while the chosen unit was away or set
    /// aside, is let go once the chosen unit is attached and usable again, so
    /// a moment off the bus never leaves the primary on another unit. True
    /// when it was let go and the connect below takes the chosen one.
    /// </summary>
    private bool StandInGivesWayLocked(IReadOnlyList<IAudioDevice> all)
    {
        IAudioDevice standIn = _device!;
        if (IsChosenLocked(standIn.Info) || _noNewConnections || Environment.TickCount64 < _reconnectNotBefore) return false;
        IAudioDevice? chosen = all.FirstOrDefault(d => IsChosenLocked(d.Info) && !_hung.IsSetAside(d.Info.InstanceId)
            && (_preferredInstance is not null || !_additional(d.Info.InstanceId)));
        if (chosen is null) return false;
        _log.LogInformation("{chosen} is attached again: driving it instead of {standIn}", chosen.Info.DisplayName, standIn.Info.DisplayName);
        FlushLastState(force: true);
        Drop();
        RestoreCardProfile();
        return true;
    }

    // Last-settings memory for devices that have none of their own (see
    // DeviceCapabilities.RetainsSettings) and for those the desktop writes
    // to (see HoldDesktopRestoreLocked): every change marks the snapshot
    // dirty and the poll loop writes it a second later, so a slider drag
    // costs one file write, not hundreds.
    private bool _restorePending;
    private DateTime? _dirtyAt;
    private static readonly TimeSpan PersistDelay = TimeSpan.FromSeconds(1);

    private void NoteChangedLocked()
    {
        if (_device is null || _restorePending) return;
        if (_device.Capabilities.RetainsSettings && !_device.DesktopRestoresControls) return;
        _dirtyAt ??= DateTime.UtcNow;
    }

    // A unit the desktop writes saved values to when its card appears
    // (IAudioDevice.DesktopRestoresControls). For a few seconds after each
    // arrival, a mute or headphone level the unit changes on its own is the
    // desktop's saved value, and the one OpenXLR had is written back. Its
    // last settings are kept on disk for this, so the values held after a
    // daemon start are the ones from before it: WirePlumber also writes them
    // when it starts, and an audio service restart restarts the daemon.
    internal static readonly TimeSpan DesktopRestoreHold = TimeSpan.FromSeconds(5);
    private long _cardArrivalsSeen, _desktopHoldUntil;
    private DeviceState? _beforeCardLoss;
    private bool _desktopHoldSaid;

    private void ForgetCardArrivalsLocked()
    {
        _cardArrivalsSeen = 0;
        _desktopHoldUntil = 0;
        _beforeCardLoss = null;
    }

    /// <summary>
    /// The state just read, or the one read after writing back the mute and
    /// headphone level the desktop replaced. The values held are those from
    /// before the card went away, else the last read, else the last ones
    /// saved for the unit; a change made through OpenXLR during the hold
    /// is in the last read, so it stays.
    /// </summary>
    private DeviceState HoldDesktopRestoreLocked(IAudioDevice dev, DeviceState now)
    {
        if (!dev.DesktopRestoresControls) return now;
        // Nothing can write to an absent card; what the unit holds until it
        // returns is what to keep.
        if (!dev.HasCard)
        {
            _beforeCardLoss ??= _last;
            return now;
        }
        DeviceState? held = _last;
        long arrivals = dev.CardArrivals;
        if (arrivals != _cardArrivalsSeen)
        {
            _cardArrivalsSeen = arrivals;
            _desktopHoldUntil = NowMs + (long)DesktopRestoreHold.TotalMilliseconds;
            _desktopHoldSaid = false;
            held = _beforeCardLoss ?? _last;
            _beforeCardLoss = null;
            if (held is null)
            {
                try { held = DeviceStateStore.LoadLast(DevId(dev)); }
                catch (Exception ex) { _log.LogWarning("loading last settings of {dev}: {msg}", DevId(dev), ex.Message); }
            }
        }
        else if (NowMs >= _desktopHoldUntil) return now;
        if (held is null) return now;
        bool mute = held.Mute != now.Mute;
        bool hp = Math.Abs(held.HpVolumeDb - now.HpVolumeDb) >= 0.25;   // half-dB steps
        if (!mute && !hp) return now;
        try
        {
            if (mute) dev.SetMute(held.Mute);
            if (hp) dev.SetHpVolumeDb(held.HpVolumeDb);
            if (!_desktopHoldSaid)
            {
                _desktopHoldSaid = true;
                _log.LogInformation("{dev}: the desktop restored older values for the card, wrote back mute {mute} and headphones {hp} dB",
                    dev.Info.DisplayName, held.Mute ? "on" : "off", held.HpVolumeDb.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            return Stamp(dev.ReadState());
        }
        catch (Exception ex) when (ex is not UsbHungException)
        {
            _log.LogWarning("{dev}: writing back mute and headphones after the desktop restored older values: {msg}", dev.Info.DisplayName, ex.Message);
            return now;
        }
    }

    private void FlushLastState(bool force)
    {
        lock (_gate)
        {
            if (_dirtyAt is not DateTime since) return;
            if (!force && DateTime.UtcNow - since < PersistDelay) return;
            _dirtyAt = null;
            if (_device is null || _last is null) return;
            try { DeviceStateStore.SaveLast(DevId(_device), SavedStateLocked(_device, _last)); }
            catch (Exception ex) { _log.LogWarning("saving last settings: {msg}", ex.Message); }
        }
    }

    /// <summary>
    /// For a device without settings memory that just connected fresh and
    /// has no recall-on-connect profile: write back the last settings the
    /// daemon saw on it. Returns null when there was nothing to restore
    /// (a device that keeps its own settings, or one never seen before),
    /// otherwise a status line for the log. Persistence resumes either way.
    /// </summary>
    public string? RestoreLastState()
    {
        lock (_gate)
        {
            if (_device is null || !_device.Connected) { _restorePending = false; return null; }
            if (_device.Capabilities.RetainsSettings) { _restorePending = false; return null; }
            string id = DevId(_device);
            DeviceState? last;
            try { last = DeviceStateStore.LoadLast(id); }
            catch (Exception ex) { last = null; _log.LogWarning("loading last settings of {dev}: {msg}", id, ex.Message); }
            _restorePending = false;
            if (last is null) return null;
            string? err = ApplyProfile(last, restoring: true);
            NoteChangedLocked();
            return err is null ? $"restored the last settings of {id}" : $"restoring the last settings of {id}: {err}";
        }
    }

    /// <summary>
    /// Called after a recall-on-connect profile was applied instead of the
    /// last settings: persistence resumes and the recalled state is what
    /// gets remembered.
    /// </summary>
    public void MarkRestored()
    {
        lock (_gate)
        {
            _restorePending = false;
            NoteChangedLocked();
        }
    }

    /// <summary>
    /// Write the firmware defaults recorded after the device's last power
    /// cycle and forget the last settings, for a device without memory of
    /// its own. Null on success, else the reason.
    /// </summary>
    public string? ResetToDefaults()
    {
        lock (_gate)
        {
            if (_device is null || !_device.Connected) return "no device connected";
            if (_device.Capabilities.RetainsSettings)
            {
                // Nothing to record on a device that keeps its settings:
                // OpenXLR's own baseline is written instead, when the model has one.
                DeviceState current;
                try { current = _last ?? Stamp(_device.ReadState()); }
                catch (Exception ex) { return ex.Message; }
                DeviceState? baseline = DeviceDefaults.Baseline(_device.Info, current);
                if (baseline is null) return "resetDevice: this interface keeps its own settings and OpenXLR has no baseline for it";
                if (GainIsLocked) return "resetDevice: release the gain lock first";
                return ApplyProfile(baseline);
            }
            string id = DevId(_device);
            DeviceState? defaults;
            try { defaults = DeviceStateStore.LoadDefaults(id); }
            catch (Exception ex) { return ex.Message; }
            if (defaults is null)
                return "no defaults recorded for this interface yet: unplug it and plug it back in once, then try again";
            string? err = ApplyProfile(defaults);
            if (err is not null) return err;
            _dirtyAt = null;
            try { DeviceStateStore.ClearLast(id); }
            catch (Exception ex) { _log.LogWarning("clearing last settings of {dev}: {msg}", id, ex.Message); }
            return null;
        }
    }

    // UCM coexistence: a card with a UCM split profile hides the raw
    // multichannel nodes the mixer links against, so drive it in pro-audio
    // while connected and restore the split on graceful shutdown. Capture:
    // a card on a profile that only plays is switched once per connection
    // to the same output with an input, and is not switched back when the
    // daemon stops, since a microphone interface never wants the profile
    // without its microphone; what cannot be fixed is reported.
    private string? _profileRestore;
    private string? _profileCardFragment;   // the fragment that found the card, used to restore it
    private volatile CardProfileWatch? _cardWatch;

    private void EnsureCardProfile(DeviceInfo info, bool parksUcm, bool fresh)
    {
        if (!_submixer) return;   // hardware-control mode leaves the card's layout alone
        // A unit without a USB location is a backend with no card of its own
        // (a test's); only the Pro's park ever looked for one.
        if (!parksUcm && info.Location is null) { _cardWatch = null; return; }
        // The USB device is visible to libusb before WirePlumber has created
        // (and profiled) the card, so a single check at connect misses the
        // boot case entirely: the watch keeps checking until the card settles.
        // A reconnect after a USB error is the same connection, and a switch
        // made in it is not made again.
        (string From, string To)? switched = fresh ? null : _cardWatch?.Switched;
        _profileCardFragment = info.NodeNameFragment;
        _cardWatch = new CardProfileWatch(info.DisplayName, FragmentsLocked(info), parksUcm, switched, (info.VendorId, info.ProductId));
        TryParkCardProfile();
    }

    private void TryParkCardProfile()
    {
        if (_cardWatch is not { } watch) return;
        CardProfileStatus? before = watch.Status;
        foreach (CardProfileNote note in watch.Check())
        {
            if (note.Warning) _log.LogWarning("{note}", note.Text);
            else _log.LogInformation("{note}", note.Text);
            Timeline.Record(TimelineKind.Card, note.Text);
        }
        // A switch of unit or a restore that came in meanwhile has replaced
        // the watch; what this one found no longer applies.
        lock (_gate)
        {
            if (_cardWatch != watch) return;
            if (watch.Matched is { } matched) _profileCardFragment = matched;
            if (watch.Parked is { } parked) _profileRestore = parked;
            if (!Equals(before, watch.Status) && _device is { Connected: true }) RaiseFromLocked();
        }
    }

    /// <summary>The driven card's capture problem, or null; read without the device lock.</summary>
    private string? CardProblem => _published.Info is not null ? _cardWatch?.Status?.Problem : null;

    // Restore at the START of shutdown: the teardown of the rest of the
    // daemon can take many seconds, and the profile flip must not depend
    // on it completing. RestoreCardProfile is idempotent, so the post-loop
    // call in ExecuteAsync stays as a fallback for non-host exits.
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        RestoreCardProfile();
        await base.StopAsync(cancellationToken);
    }

    private void RestoreCardProfile()
    {
        _cardWatch = null;
        if (_profileRestore is null || _profileCardFragment is null) return;
        try
        {
            OpenXLR.Core.Mixing.CardProfile.SetProfile(_profileCardFragment, _profileRestore);
            _log.LogInformation("card {frag}: restored UCM profile {prev}", _profileCardFragment, _profileRestore);
        }
        catch (Exception ex)
        {
            _log.LogWarning("card profile restore: {msg}", ex.Message);
        }
        _profileRestore = null;
        _profileCardFragment = null;
    }

    private void PollOnce()
    {
        lock (_gate)
        {
            if (_device is null || !_device.Connected)
            {
                _unitMetersWatched = false;
                return;
            }
            DeviceState now;
            try { now = Stamp(_device.ReadState()); }
            catch (Exception ex) when (ex is not UsbHungException)
            {
                NoteUnansweredLocked(_device.Info, ex.Message);
                throw;   // dropped and reopened after a wait, as any failed poll
            }
            _pollFailures = 0;
            now = HoldDesktopRestoreLocked(_device, now);
            bool recovered = false;
            if (_unansweredId == _device.Info.InstanceId)
            {
                ClearUnansweredLocked();
                recovered = true;
                _log.LogInformation("{dev} answers USB reads again", _device.Info.DisplayName);
                Timeline.Record(TimelineKind.Usb, $"{_device.Info.DisplayName} answers USB reads again");
            }
            bool changed = _last is null || now != _last;
            if (changed)
            {
                _last = now;
                NoteChangedLocked();
            }
            // A readback that changed can change whether the unit runs ducking at all.
            changed |= VerifyDuckingLocked(_device);
            ReadUnitMetersLocked(_device);
            VerifyEqLocked(_device);
            VerifyDynamicsLocked(_device);
            if (changed || recovered) RaiseFromLocked();
        }
    }

    /// <summary>
    /// A poll read failed. Said once per unit until it answers again, in a
    /// sentence, and in the state's warning; the reconnects that follow every
    /// few seconds while the unit stays wedged add nothing to the journal.
    /// </summary>
    private void NoteUnansweredLocked(DeviceInfo unit, string error)
    {
        if (_unansweredId != unit.InstanceId)
        {
            _unansweredId = unit.InstanceId;
            _log.LogWarning("{dev} does not answer USB reads ({error}). OpenXLR keeps reconnecting it and shows the last values it read; " +
                            "if it stays this way, unplug the device and plug it back in.", unit.DisplayName, error);
            Timeline.Record(TimelineKind.Usb, $"{unit.DisplayName} does not answer USB reads ({error})");
        }
        _unansweredWarning = $"{unit.DisplayName} does not answer USB reads ({error}). The values shown are the last ones read from it " +
                             "and OpenXLR keeps reconnecting it; if this does not clear, unplug the device and plug it back in.";
        _lastLoopError = error;   // the device loop's own line would say the same again
    }

    private void ClearUnansweredLocked()
    {
        _unansweredId = null;
        _unansweredWarning = null;
    }

    /// <summary>Apply a client "set" command. Returns null on success, else an error string.</summary>
    public string? Apply(string control, JsonElement value)
    {
        lock (_gate)
        {
            if (_device is null || !_device.Connected) return "no device connected";
            string? unsupported = UnsupportedControlReason(EffectiveCapabilitiesLocked(_device), control);
            if (unsupported is not null) return unsupported;
            int? lineInWritten = null;
            try
            {
                switch (control)
                {
                    case ControlNames.Gain:
                        if (GainIsLocked) return "gain is locked";
                        _device.SetGainDb(value.GetInt32());
                        break;
                    case ControlNames.Mute: _device.SetMute(value.GetBoolean()); break;
                    case ControlNames.LowCut: _device.SetLowCut(value.GetBoolean()); break;
                    case ControlNames.Expander: _device.SetExpander(value.GetBoolean()); break;
                    case ControlNames.VoiceTune: _device.SetVoiceTune(value.GetBoolean()); break;
                    case ControlNames.VoiceTuneStrength: _device.SetVoiceTuneStrength(value.GetInt32()); break;
                    case ControlNames.HpVolumeDb: _device.SetHpVolumeDb(value.GetDouble()); break;
                    case ControlNames.LowImpedance: _device.SetLowImpedance(value.GetBoolean()); break;
                    case ControlNames.Crossfade: _device.SetCrossfade(value.GetInt32()); break;
                    case ControlNames.Phantom:
                        _device.SetPhantom(value.GetBoolean());
                        _phantomWroteAt = UtcNow;
                        break;
                    case ControlNames.ClipGuard: _device.SetClipGuard(value.GetBoolean()); break;
                    case ControlNames.Compressor: _device.SetCompressor(value.GetBoolean()); break;
                    case ControlNames.OutHp1: WriteMixLocked(_device, new ProMixPlan { Hp1 = value.GetBoolean() }, "set"); break;
                    case ControlNames.OutHp2: WriteMixLocked(_device, new ProMixPlan { Hp2 = value.GetBoolean() }, "set"); break;
                    case ControlNames.OutUsbAux: WriteMixLocked(_device, new ProMixPlan { UsbAux = value.GetBoolean() }, "set"); break;
                    case ControlNames.OutLineOut: WriteMixLocked(_device, new ProMixPlan { LineOut = value.GetBoolean() }, "set"); break;
                    case ControlNames.AuxLevelDb: _device.SetAuxLevelDb(value.GetDouble()); break;
                    case ControlNames.AuxLevelLock: _device.SetAuxLevelLock(value.GetBoolean()); break;
                    case ControlNames.LineInGain: lineInWritten = _device.SetLineInGainDb(value.GetInt32()); break;
                    case ControlNames.LineInLevelLock: _device.SetLineInLevelLock(value.GetBoolean()); break;
                    case ControlNames.RawCapture or ControlNames.RawCapture2 or ControlNames.LineInRawCapture:
                        SetRawCaptureLocked(_device, RawCaptureControl(control)!.Value, value.GetBoolean());
                        break;
                    case "hp2VolumeDb": _device.SetHp2VolumeDb(value.GetDouble()); break;
                    case "gain2":
                        if (GainIsLocked) return "gain is locked";
                        _device.SetGain2Db(value.GetInt32());
                        break;
                    case "gainLock":
                        bool locking = value.GetBoolean();
                        // The unit's own lock first: a failed write leaves
                        // both locks as they were.
                        if (_device.Capabilities.HardwareGainLock) _device.SetUnitGainLock(locking);
                        if (locking) _gainLocked.Add(DevId(_device));
                        else _gainLocked.Remove(DevId(_device));
                        SaveGainLocks();
                        break;
                    case "mute2": _device.SetMute2(value.GetBoolean()); break;
                    case "lowCut2": _device.SetLowCut2(value.GetBoolean()); break;
                    case "expander2": _device.SetExpander2(value.GetBoolean()); break;
                    case "voiceTune2": _device.SetVoiceTune2(value.GetBoolean()); break;
                    case "voiceTuneStrength2": _device.SetVoiceTuneStrength2(value.GetInt32()); break;
                    case "phantom2":
                        _device.SetPhantom2(value.GetBoolean());
                        _phantomWroteAt2 = UtcNow;
                        break;
                    case "clipGuard2": _device.SetClipGuard2(value.GetBoolean()); break;
                    case "compressor2": _device.SetCompressor2(value.GetBoolean()); break;
                    default: return $"unknown control '{control}'";
                }
            }
            catch (UsbHungException ex)
            {
                NoteHung(ex);
                return ex.Message;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
            // Reflect immediately; the poll loop will also catch it, but this
            // makes the client's own change feel instant.
            _last = Stamp(_device.ReadState());
            // Each Line In gain write is logged with what the unit reads back.
            if (lineInWritten is int written)
                _log.LogInformation("{dev}: Line In gain written as {db} dB (block 0x0004 byte 76), read back as {readback} dB",
                    _device.Info.DisplayName, written, _last.LineInGainDb);
            NoteChangedLocked();
            RaiseFromLocked();
            return null;
        }
    }

    /// <summary>
    /// Reject controls a device does not advertise. The device interface keeps
    /// no-op defaults for source compatibility, but treating a no-op as success
    /// is unsafe for remote clients: a Stream Deck key must not appear to have
    /// enabled 48 V or muted an input when the hardware ignored the command.
    /// </summary>
    internal static string? UnsupportedControlReason(DeviceCapabilities c, string control)
    {
        bool supported = control switch
        {
            ControlNames.Gain => c.Gain,
            ControlNames.Mute => c.Mute,
            ControlNames.LowCut => c.LowCut,
            ControlNames.Expander => c.Expander,
            ControlNames.VoiceTune or ControlNames.VoiceTuneStrength => c.VoiceTune,
            ControlNames.HpVolumeDb => c.HpVolume,
            ControlNames.LowImpedance => c.LowImpedance,
            ControlNames.Crossfade => c.Crossfade,
            ControlNames.Phantom => c.Phantom,
            ControlNames.ClipGuard => c.ClipGuard,
            ControlNames.Compressor => c.Compressor,
            ControlNames.OutHp1 or ControlNames.OutHp2 or ControlNames.OutUsbAux or
                ControlNames.OutLineOut => c.OutputRouting,
            ControlNames.AuxLevelDb or ControlNames.AuxLevelLock => c.AuxInput,
            ControlNames.LineInGain or ControlNames.LineInLevelLock => c.LineInput,
            ControlNames.RawCapture or ControlNames.RawCapture2 or ControlNames.LineInRawCapture
                => RawCaptureSupported(c, RawCaptureControl(control)!.Value),
            "gain2" => c.Gain && c.XlrInputs > 1,
            "mute2" => c.Mute && c.XlrInputs > 1,
            "lowCut2" => c.LowCut && c.XlrInputs > 1,
            "expander2" => c.Expander && c.XlrInputs > 1,
            "voiceTune2" or "voiceTuneStrength2" => c.VoiceTune && c.XlrInputs > 1,
            "phantom2" => c.Phantom && c.XlrInputs > 1,
            "clipGuard2" => c.ClipGuard && c.XlrInputs > 1,
            "compressor2" => c.Compressor && c.XlrInputs > 1,
            "hp2VolumeDb" => c.HpVolume && c.HpOutputs > 1,
            // Software gain lock is daemon-owned and deliberately available
            // even when the hardware also has physical controls.
            "gainLock" => true,
            _ => true, // the command switch reports unknown names precisely
        };
        return supported ? null : $"control '{control}' is not supported by the active device";
    }

    /// <summary>
    /// Recall a profile's hardware snapshot, writing only the fields that
    /// differ from the live state. The physical-output selectors are skipped
    /// on purpose: they follow the mixer's monitor-output selection (synced
    /// every sweep), which the profile's mixer half restores instead.
    /// </summary>
    /// <summary>
    /// Write a saved state to the device. <paramref name="restoring"/> marks
    /// the settings a device that forgets its own is being given back on
    /// connect, which includes its gain even when the gain lock is set: the
    /// lock is there to keep the gain where the user put it, and refusing to
    /// restore it leaves the hardware at whatever its firmware powers up
    /// with, which is the one outcome the lock exists to prevent. Named profile
    /// recall also restores gain; ordinary control commands still respect the lock.
    /// </summary>
    public string? ApplyProfile(DeviceState p, bool restoring = false)
    {
        lock (_gate)
        {
            if (_device is null || !_device.Connected) return "no device connected";
            DeviceState s = _last ?? Stamp(_device.ReadState());
            DeviceCapabilities c = EffectiveCapabilitiesLocked(_device);
            // A saved state holds the Pro's jacks as software mixing plays them,
            // or above 0 dB when saved in the mode, where only the mode plays them.
            p = c.OutputRouting && HardwareMixingEntry?.Invoke() is { } entry
                ? HardwareMixingPlanner.ToUnitJackLevels(p, entry) : HardwareMixingPlanner.ToSoftwareJackLevels(p);
            bool lockApplies = GainIsLocked && !restoring;
            bool gainBlocked = lockApplies &&
                (c.Gain && s.GainDb != p.GainDb ||
                 c.Gain && c.XlrInputs > 1 && s.Gain2Db != p.Gain2Db);
            bool gainRestored = GainIsLocked && restoring && c.Gain && s.GainDb != p.GainDb;
            // An MK.1 state saved before its ClipGuard and mix were read holds placeholders for both.
            bool placeholders = s.ClipGuardAndMixRead == true && p.ClipGuardAndMixRead is null;
            try
            {
                if (c.Gain && !lockApplies && s.GainDb != p.GainDb) _device.SetGainDb(p.GainDb);
                if (c.Mute && s.Mute != p.Mute) _device.SetMute(p.Mute);
                if (c.LowCut && s.LowCut != p.LowCut) _device.SetLowCut(p.LowCut);
                if (c.Expander && s.Expander != p.Expander) _device.SetExpander(p.Expander);
                if (c.VoiceTune && s.VoiceTune != p.VoiceTune) _device.SetVoiceTune(p.VoiceTune);
                if (c.VoiceTune && s.VoiceTuneStrength != p.VoiceTuneStrength) _device.SetVoiceTuneStrength(p.VoiceTuneStrength);
                if (c.Phantom && s.Phantom != p.Phantom) { _device.SetPhantom(p.Phantom); _phantomWroteAt = UtcNow; }
                if (c.ClipGuard && !placeholders && s.ClipGuard != p.ClipGuard) _device.SetClipGuard(p.ClipGuard);
                if (c.Compressor && s.Compressor != p.Compressor) _device.SetCompressor(p.Compressor);
                if (c.Gain && c.XlrInputs > 1 && !lockApplies && s.Gain2Db != p.Gain2Db) _device.SetGain2Db(p.Gain2Db);
                if (c.Mute && c.XlrInputs > 1 && s.Mute2 != p.Mute2) _device.SetMute2(p.Mute2);
                if (c.LowCut && c.XlrInputs > 1 && s.LowCut2 != p.LowCut2) _device.SetLowCut2(p.LowCut2);
                if (c.Expander && c.XlrInputs > 1 && s.Expander2 != p.Expander2) _device.SetExpander2(p.Expander2);
                if (c.VoiceTune && c.XlrInputs > 1 && s.VoiceTune2 != p.VoiceTune2) _device.SetVoiceTune2(p.VoiceTune2);
                if (c.VoiceTune && c.XlrInputs > 1 && s.VoiceTuneStrength2 != p.VoiceTuneStrength2) _device.SetVoiceTuneStrength2(p.VoiceTuneStrength2);
                if (c.Phantom && c.XlrInputs > 1 && s.Phantom2 != p.Phantom2) { _device.SetPhantom2(p.Phantom2); _phantomWroteAt2 = UtcNow; }
                if (c.ClipGuard && c.XlrInputs > 1 && s.ClipGuard2 != p.ClipGuard2) _device.SetClipGuard2(p.ClipGuard2);
                if (c.Compressor && c.XlrInputs > 1 && s.Compressor2 != p.Compressor2) _device.SetCompressor2(p.Compressor2);
                if (c.HpVolume && s.HpVolumeDb != p.HpVolumeDb) _device.SetHpVolumeDb(p.HpVolumeDb);
                if (c.HpVolume && c.HpOutputs > 1 && s.Hp2VolumeDb != p.Hp2VolumeDb) _device.SetHp2VolumeDb(p.Hp2VolumeDb);
                if (c.LowImpedance && s.LowImpedance != p.LowImpedance) _device.SetLowImpedance(p.LowImpedance);
                if (c.Crossfade && !placeholders && s.Crossfade != p.Crossfade) _device.SetCrossfade(p.Crossfade);
                if (c.AuxInput && s.AuxLevelDb != p.AuxLevelDb) _device.SetAuxLevelDb(p.AuxLevelDb);
                if (c.AuxInput && s.AuxLevelLock != p.AuxLevelLock) _device.SetAuxLevelLock(p.AuxLevelLock);
                // A snapshot from before the Line In existed holds none and leaves the unit's gain alone.
                // One saved above the cap is written as the cap, once: the unit then reads it back.
                if (c.LineInput && p.LineInGainDb is int saved && Math.Clamp(saved, 0, c.LineInGainMaxDb) is int lineIn
                    && s.LineInGainDb != lineIn)
                {
                    int written = _device.SetLineInGainDb(lineIn);
                    _log.LogInformation("{dev}: Line In gain written as {db} dB from a saved state", _device.Info.DisplayName, written);
                }
                // Likewise the Line In lock: a snapshot without it leaves the unit's lock as it is.
                if (c.LineInput && p.LineInLevelLock is bool lineLock && s.LineInLevelLock != lineLock)
                    _device.SetLineInLevelLock(lineLock);
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
            _last = Stamp(_device.ReadState());
            NoteChangedLocked();
            RaiseFromLocked();
            // Not an error: the profile loaded and the state was broadcast.
            // The lock is visible to every client in the state itself.
            if (gainBlocked) _log.LogInformation("profile loaded; gain left unchanged because the gain lock is active");
            if (gainRestored) _log.LogInformation("gain restored to {db} dB; the lock keeps it there rather than at the firmware's own", p.GainDb);
            return null;
        }
    }

    /// <summary>
    /// Bring the Pro's block 0x0001 toward the mixer's plan for this sweep:
    /// the output selectors, the headphone mix's Monitor return (issue #8)
    /// and the microphones' zero-latency paths at the levels the software
    /// sends would play them at. Called every mixer sweep, so it is a no-op
    /// at steady state: the plan is checked against the last poll and no
    /// transfer happens while the unit holds it and no ducking target waits.
    /// A ducking target posted and not written yet goes into the same write,
    /// so the block is written once for both. Returns true when a membership
    /// bit of the jacks' mix was written, so the caller can restart the
    /// playback stream as before; selectors and levels apply without it.
    /// Quietly does nothing without a connected device that has output
    /// routing.
    /// </summary>
    public bool EnsureMix(ProMixPlan plan) => EnsureMix(plan, out _);

    /// <summary>
    /// <see cref="EnsureMix(ProMixPlan)"/>, saying whether the unit holds the
    /// plan afterwards, as the last read shows it: block 0x0001 needs nothing
    /// more and the jacks' levels are the plan's. Hardware mixing's entry and
    /// leave steps go on only when it does.
    /// </summary>
    public bool EnsureMix(ProMixPlan plan, out bool held)
    {
        held = false;
        lock (_gate)
        {
            if (_device is null || !_device.Connected || !_device.Capabilities.OutputRouting) return false;
            DeviceState? s = _last;
            if (s is null) return false;
            plan = KeepingDuckedInputs(_device, plan);
            bool duckWaiting;
            lock (_duckGate) duckWaiting = _device.Capabilities.Ducking && _duckPending is not null;
            if (!duckWaiting && Holds(s, plan)) { held = true; return false; }
            try
            {
                ProMixWrite? write = WriteMixLocked(_device, plan, "update");
                if (write is null) return false;
                bool membership = write.Changes.Membership();
                if (membership)
                    _log.LogInformation("headphone mix: monitor return {ret}, mic direct {mic}, XLR 2 direct {mic2}",
                        plan.MonitorReturn ?? s.HpMixMonitorReturn, plan.Mic1?.Direct ?? s.HpMixMicDirect, plan.Mic2?.Direct ?? s.HpMixMic2Direct);
                if (write.Change5.Length > 0) _log.LogInformation("jack levels: block 0x0005 {change}", write.Change5);
                if (write.Wrote1 || write.Change5.Length > 0)
                {
                    _last = Stamp(_device.ReadState());
                    RaiseFromLocked();
                }
                held = _last is { } now && Holds(now, plan);
                return membership;
            }
            catch (UsbHungException ex)
            {
                NoteHung(ex);
                return false;
            }
            catch (Exception ex)
            {
                _log.LogDebug("block 0x0001 sync: {msg}", ex.Message);
                return false;
            }
        }

        static bool Holds(DeviceState state, ProMixPlan plan)
            => (state.ProMixBlock is { Length: 2 * ProMixSections.Length } hex
                    ? plan.Needs(Convert.FromHexString(hex)) : plan.Needs(ProMixHeld.Of(state))) == ProMixChanges.None
                && plan.JackLevels?.HeldBy(state) != false;
    }

    /// <summary>
    /// The Pro's block 0x0001 as the last poll read it, hex; null without a
    /// connected unit that has output routing or before its first poll.
    /// </summary>
    public string? ProMixBlock
    {
        get
        {
            lock (_gate)
                return _device is { Connected: true } dev && dev.Capabilities.OutputRouting ? _last?.ProMixBlock : null;
        }
    }

    /// <summary>
    /// What hardware mixing needs to know of the driven unit: whether it is a
    /// model that can build its outputs' mixes, connected, its firmware, and
    /// whether the last poll's block 0x0001 decodes; null without a unit.
    /// </summary>
    public HardwareMixingUnit? HardwareMixingUnit
    {
        get
        {
            lock (_gate)
            {
                if (_device is not { } dev) return null;
                byte[]? block = _last?.ProMixBlock is { Length: 2 * ProMixSections.Length } hex ? Convert.FromHexString(hex) : null;
                return new HardwareMixingUnit
                {
                    Pro = dev.Capabilities.HardwareMixing,
                    Connected = dev.Connected,
                    Firmware = dev.Firmware,
                    SectionsDecode = dev.Connected && block is not null && ProMixSections.Decodes(block),
                };
            }
        }
    }

    /// <summary>What the last poll read from a connected unit, or null.</summary>
    public DeviceState? LastState
    {
        get { lock (_gate) return _device is { Connected: true } ? _last : null; }
    }

    /// <summary>
    /// While the Wave XLR Pro builds its jacks' mixes, what it held before the
    /// mode (the mixer service sets this; it must not take the mixer's lock).
    /// A saved state then reaches the jacks lowered by the mode's entry
    /// offset, and one saved from the unit carries the levels leaving would
    /// give, so a profile means the same loudness in either mode.
    /// </summary>
    public Func<HardwareEntry?>? HardwareMixingEntry { get; set; }

    /// <summary>The connected unit's state as a profile saves it, or null.</summary>
    public DeviceState? SavedState()
    {
        lock (_gate) return _device is { Connected: true } && _last is { } last ? SavedStateLocked(_device, last) : null;
    }

    private DeviceState SavedStateLocked(IAudioDevice device, DeviceState state)
    {
        // What is plugged into the unit is read, never written back, and the
        // LEDs are kept per unit in leds.json.
        state = state with { MicConnected = null, HeadphonesConnected = null, Led = null };
        return EffectiveCapabilitiesLocked(device).OutputRouting && HardwareMixingEntry?.Invoke() is { } entry
            ? HardwareMixingPlanner.ToSavedJackLevels(state, entry) : state;
    }

    /// <summary>Section 0's level as the Pro's last poll read it in byte 98, or null without one.</summary>
    public double? ProSection0LevelDb
        => ProMixBlock is { Length: 2 * ProMixSections.Length } hex
            ? ProMixSections.Section0LevelDb(Convert.FromHexString(hex)[ProMixSections.Section0LevelOffset]) : null;

    /// <summary>Vendor block dumps for diagnostics; empty without a device.</summary>
    public IReadOnlyDictionary<string, string> DumpBlocks()
    {
        lock (_gate)
        {
            var blocks = new Dictionary<string, string>();
            if (_lastUsbFault is not null) blocks["usbFault"] = _lastUsbFault;
            if (_device is null || !_device.Connected) return blocks;
            try
            {
                foreach ((string k, string v) in _device.DumpBlocks()) blocks[k] = v;
            }
            catch (UsbHungException ex) { NoteHung(ex); blocks["error"] = ex.Message; }
            catch (Exception ex) { blocks["error"] = ex.Message; }
            return blocks;
        }
    }

    private void RaiseFromLocked()
    {
        // Fire outside the caller's expectations but we're already under _gate;
        // handlers only enqueue/serialize, they don't call back into the manager.
        // Published first: with no handler, ?.Invoke would skip its argument.
        StateMessage msg = PublishLocked();
        StateChanged?.Invoke(msg);
    }

    /// <summary>Make the current device state what <see cref="Snapshot"/> serves.</summary>
    private StateMessage PublishLocked()
    {
        if (_device is not { Connected: true } dev)
        {
            _published = NothingDriven;
            return NothingDriven.Message;
        }
        string id = dev.Info.InstanceId;
        if (_last is not null) { _lastGood = _last; _lastGoodId = id; }
        var msg = new StateMessage
        {
            Connected = true,
            Device = new DeviceDescriptor(dev.Info.Vendor, dev.Info.Model,
                $"{dev.Info.VendorId:x4}:{dev.Info.ProductId:x4}", dev.ConnectionNote, dev.Firmware),
            Capabilities = EffectiveCapabilitiesLocked(dev),
            // Reopened but not answering yet: the values it last gave, which
            // the warning marks as such.
            State = _last ?? (_unansweredId == id && _lastGoodId == id ? _lastGood : null),
            CardProfile = _cardWatch?.Status,
        };
        KeepHeldDynamicsLocked(dev);
        UnitEqSupport? eq = dev.EqSupport is { } support
            ? new UnitEqSupport(dev.Info.Model, dev.Firmware, support.Ready, support.Reason, support.Note)
            {
                Inputs = dev.Capabilities.XlrInputs,
                MinimumFirmware = support.MinimumFirmware ?? WaveFxEq.Format(WaveFxEq.Mk2EqFirmware),
                MaxGain = support.MaxGain,
                Dynamics = dev.DynamicsSupport,
                DynamicsLevels = dev.Capabilities.DynamicsMeters,
                RawCaptureInputs = RawCaptureMicsLocked(),
            }
            : null;
        // A unit that answered a poll but not the ducking block cannot run it.
        bool duckingRefused = _last is not null && dev.ReadDucking() is null;
        // The unit ducks inside the mix the jacks play, which holds the
        // jacks' feed while its membership bit reads set (desk probe,
        // 2026-10-23), so the jacks are reached only once a poll has read
        // it. The USB Aux port dips with its mix (desk probe, 2026-11-02).
        UnitDuckingSupport? ducking = dev.Capabilities.Ducking
            ? new UnitDuckingSupport(dev.Info.Model, dev.Firmware, !duckingRefused,
                duckingRefused ? "unsupportedBlock" : null,
                duckingRefused ? $"The {dev.Info.Model} did not answer a read of its ducking block." : null,
                ReachesJacks: _last?.HpMixMonitorReturn == true)
            : null;
        _published = new Published(msg, dev.Info, eq, ducking);
        return msg;
    }

    private void Drop()
    {
        lock (_gate)
        {
            try { _device?.Disconnect(); } catch { /* ignore */ }
            try { _device?.Dispose(); } catch { /* the helper is gone either way */ }
            bool was = _device is not null;
            if (_device is { } dropped) Timeline.Record(TimelineKind.Device, $"{dropped.Info.DisplayName} is no longer driven");
            _device = null;
            _last = null;
            _published = NothingDriven;
            // Nothing is written to a unit that went away: what was posted
            // to it is forgotten, and the mixer posts again when it returns.
            ForgetEq();
            ForgetDynamics();
            ForgetDucking();
            if (was) StateChanged?.Invoke(NothingDriven.Message);
        }
    }

    // Hardware ducking (the Wave XLR Pro). The mixer owns the setup and posts
    // what the primary unit should hold through DeviceDuckingPort; the device
    // loop writes it at the start of a sweep and the poll in the same sweep
    // reads it back. Nothing is written before the first post, so a daemon in
    // hardware-control mode leaves the unit as it is. After that every connect
    // writes the last target posted for that unit, or off for another one,
    // which clears ducking Wave Link left, and a target another program
    // changed is written back no more often than DuckingRewriteSpacing. Every
    // write is said in the journal with the bytes it changed, so a hardware
    // check can follow it. Posts and statuses take their own small lock,
    // never the device lock, as the EQ's do.
    private readonly object _duckGate = new();
    private bool _duckManaged;                 // a target was posted in this run
    private string? _duckUnit;                 // the unit the last target is for
    private ProDuckingTarget? _duckWanted;     // the last target posted, kept across a drop
    private string _duckOwner = "";
    private ProDuckingTarget? _duckPending;    // not written yet
    private ProDuckingTarget? _duckWritten;    // written, or found already held; the next poll reads it back
    private ProDuckingTarget? _duckVerified;   // read back held
    private bool _duckDrifted;                 // the unit no longer holds the verified target; a rewrite is due
    private string? _duckFailed;
    private int _duckMisses;
    private DuckingReadback? _duckReadback;
    private long _duckRewriteNotBefore;
    private bool _duckElsewhereLogged;

    /// <summary>The least time between two rewrites of ducking another program changed.</summary>
    internal static readonly TimeSpan DuckingRewriteSpacing = TimeSpan.FromSeconds(5);

    // The unit's own reduction, from its meters (block 0x0002): read by the
    // poll only while the unit holds ducking on and someone asked for it
    // within UnitMetersHold, so an idle daemon adds no USB read.
    private long? _duckMetersAskedAt;
    private double? _duckReduction;
    private long _duckReductionAt;

    // The same block for the mixes the unit builds in hardware mixing: read
    // only while the mixer holds the mode and asked within UnitMetersHold.
    // One read serves both. Guarded by _duckGate, as the reduction is.
    private long? _unitMetersAskedAt;
    private ProMeterReading? _unitMeters;
    private long _unitMetersAt;
    // The Wave XLR MK.2's meters (its own block 0x0002) for the levels of the
    // compressor and expander it runs: read only while the mixer asked within
    // UnitMetersHold, which it does while a client watches them. Guarded by
    // _duckGate, as the others are.
    private long? _dynamicsMetersAskedAt;
    private Mk2MeterReading? _dynamicsMeters;
    private long _dynamicsMetersAt;
    // The last poll read the unit's meters for someone, so the device loop
    // reads them once more between polls.
    private volatile bool _unitMetersWatched;

    /// <summary>How long one ask for the unit's reduction or meters keeps its meters read.</summary>
    internal static readonly TimeSpan UnitMetersHold = TimeSpan.FromSeconds(1);

    /// <summary>What a unit that did not keep a written ducking target reports, after the unit's name.</summary>
    internal const string DuckingNotKept = "did not keep the ducking written to it";

    /// <summary>
    /// Raised when the ducking status settles (verified, failed, or forgotten
    /// with the unit) or the unit's readback changes. Raised under the device
    /// lock, so handlers only hand off.
    /// </summary>
    public event Action? DuckingStatusChanged;

    /// <summary>The driven unit and its ducking support; null when none is driven or its model has no hardware ducking.</summary>
    internal (string Unit, UnitDuckingSupport Support)? PublishedDuckingSupport
        => _published is { Info: { } info, Ducking: { } support } ? (info.InstanceId, support) : null;

    /// <summary>
    /// The target the driven unit should hold, replacing one not written yet.
    /// A target equal to the one the unit was read holding is not written
    /// again. Dropped for a unit that is not the driven one or has no
    /// hardware ducking.
    /// </summary>
    internal void PostDucking(string unit, ProDuckingTarget target, string owner)
    {
        if (_published.Info?.InstanceId != unit || _published.Ducking is null || ProDucking.Valid(target) is not null) return;
        lock (_duckGate)
        {
            _duckManaged = true;
            _duckUnit = unit;
            _duckWanted = target;
            _duckOwner = owner;
            if (_duckFailed is null && _duckPending is null && _duckWritten is null && !_duckDrifted && target.Equals(_duckVerified)) return;
            _duckPending = target;
            _duckFailed = null;
            _duckMisses = 0;
        }
        Wake();
    }

    /// <summary>
    /// Where the last target posted to the driven unit stands, with its last
    /// readback. None for a unit that is not driven, and before anything was
    /// posted in this run. Never waits for the device lock.
    /// </summary>
    internal DeviceDuckingStatus DuckingStatus(string unit)
    {
        if (_published.Info?.InstanceId != unit || _published.Ducking is null) return DeviceDuckingStatus.None;
        lock (_duckGate)
        {
            DeviceDuckingStatus status = _duckFailed is { } reason ? new(DeviceDuckingPhase.Failed, _duckWanted, reason)
                : (_duckPending ?? _duckWritten) is { } pending ? new(DeviceDuckingPhase.Pending, pending)
                : _duckVerified is { } held ? new(_duckDrifted ? DeviceDuckingPhase.Pending : DeviceDuckingPhase.Verified, held)
                : DeviceDuckingStatus.None;
            return status with { Readback = _duckReadback };
        }
    }

    /// <summary>
    /// A unit just opened that has a gain lock of its own gets it as
    /// OpenXLR's gain lock stands: set while that lock is on, cleared while
    /// it is off, whoever set it (Wave Link, or the ClipGuard switch of an
    /// earlier release). A failed write leaves the software lock in force
    /// and is tried again at the next connect.
    /// </summary>
    private void ConnectUnitGainLockLocked(IAudioDevice dev)
    {
        if (!dev.Capabilities.HardwareGainLock) return;
        bool locked = _gainLocked.Contains(DevId(dev));
        try
        {
            if (dev.SetUnitGainLock(locked))
                _log.LogInformation("{dev}: the unit's gain lock {action} to follow OpenXLR's gain lock",
                    dev.Info.DisplayName, locked ? "set" : "cleared");
        }
        catch (UsbHungException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning("{dev}: the unit's gain lock was not written: {msg}", dev.Info.DisplayName, ex.Message);
        }
    }

    /// <summary>
    /// A unit just opened with LEDs OpenXLR sets gets the ones a user set
    /// for it, the fields it does not hold already. A failed write is said
    /// once and tried again at the next connect.
    /// </summary>
    private void ConnectLedLocked(IAudioDevice dev)
    {
        if (!dev.Capabilities.LedRing || !LoadLeds().TryGetValue(DevId(dev), out DeviceLedChange? saved) || saved.IsEmpty) return;
        try
        {
            if (dev.SetLed(saved)) _log.LogInformation("{dev}: the LEDs set in OpenXLR written back", dev.Info.DisplayName);
        }
        catch (UsbHungException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning("{dev}: the LEDs were not written back: {msg}", dev.Info.DisplayName, ex.Message);
        }
    }

    /// <summary>
    /// The setLed command, after <see cref="CommandValidation.CheckLed"/>:
    /// write <paramref name="change"/> to the active unit and keep it for
    /// that unit, or with null write the unit's factory LEDs and forget what
    /// was kept. Null on success, else the reason.
    /// </summary>
    public string? ApplyLed(DeviceLedChange? change)
    {
        lock (_gate)
        {
            if (_device is null || !_device.Connected) return "no device connected";
            if (!_device.Capabilities.LedRing) return "setLed: the active device has no LEDs OpenXLR can set";
            string id = DevId(_device);
            try
            {
                _device.SetLed(change ?? WaveFxLed.Factory);
            }
            catch (UsbHungException ex)
            {
                NoteHung(ex);
                return ex.Message;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
            SaveLed(id, change is null ? null : (LoadLeds().GetValueOrDefault(id) ?? new DeviceLedChange()).With(change));
            try { _last = Stamp(_device.ReadState()); }
            catch (UsbHungException ex)
            {
                NoteHung(ex);
                return ex.Message;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
            RaiseFromLocked();
            return null;
        }
    }

    /// <summary>A unit just opened: once ducking is managed, it gets the last target posted for it, or off.</summary>
    private void ConnectDuckingLocked(IAudioDevice dev)
    {
        _duckElsewhereLogged = false;
        _duckRewriteNotBefore = 0;
        lock (_duckGate)
        {
            ForgetDuckingSlotLocked();
            if (!dev.Capabilities.Ducking || !_duckManaged) return;
            if (_duckUnit != dev.Info.InstanceId)
            {
                _duckUnit = dev.Info.InstanceId;
                _duckWanted = ProDuckingTarget.Off;
                _duckOwner = "";
            }
            _duckPending = _duckWanted ?? ProDuckingTarget.Off;
        }
        WriteDuckingLocked(dev, "connect");
    }

    private void ForgetDuckingSlotLocked()
    {
        _duckPending = _duckWritten = _duckVerified = null;
        _duckDrifted = false;
        _duckFailed = null;
        _duckMisses = 0;
        _duckReadback = null;
        _duckReduction = null;
    }

    /// <summary>
    /// The driven unit's ducking reduction now, in dB, the largest across the
    /// entries it ducks, as the poll last read its meters. Each ask keeps the
    /// poll reading them for <see cref="UnitMetersHold"/>; null for a unit
    /// that is not driven, before the first read, and once the last read is
    /// older than the hold. Never waits for the device lock.
    /// </summary>
    internal double? UnitDuckReduction(string unit)
    {
        if (_published.Info?.InstanceId != unit || _published.Ducking is null) return null;
        long now = NowMs;
        lock (_duckGate)
        {
            _duckMetersAskedAt = now;
            return _duckReduction is { } db && now - _duckReductionAt < (long)UnitMetersHold.TotalMilliseconds ? db : null;
        }
    }

    /// <summary>
    /// The driven unit's own meters as the poll last read them for the mixes
    /// it builds in hardware mixing. Each ask keeps the poll reading them for
    /// <see cref="UnitMetersHold"/> while the mixer holds the mode; null for a
    /// unit that is not driven, before the first read, after a read that
    /// failed, and once the last read is older than the hold. Never waits
    /// for the device lock.
    /// </summary>
    internal ProMeterReading? UnitMeters(string unit)
    {
        if (_published.Info?.InstanceId != unit) return null;
        long now = NowMs;
        lock (_duckGate)
        {
            _unitMetersAskedAt = now;
            return _unitMeters is { } meters && now - _unitMetersAt < (long)UnitMetersHold.TotalMilliseconds ? meters : null;
        }
    }

    /// <summary>
    /// The driven unit's meters as the poll last read them for the levels of
    /// the compressor and expander it runs on <paramref name="input"/>. Each
    /// ask keeps the poll reading them for <see cref="UnitMetersHold"/>; null
    /// on a unit that does not report them
    /// (<see cref="DeviceCapabilities.DynamicsMeters"/>), for an input it
    /// does not have, before the first read, after a read that failed, and
    /// once the last read is older than the hold. Never waits for the device
    /// lock.
    /// </summary>
    internal Mk2MeterReading? UnitDynamicsMeters(int input)
    {
        if (input != 1 || _published.Eq is not { DynamicsLevels: true }) return null;
        long now = NowMs;
        lock (_duckGate)
        {
            _dynamicsMetersAskedAt = now;
            return _dynamicsMeters is { } meters && now - _dynamicsMetersAt < (long)UnitMetersHold.TotalMilliseconds ? meters : null;
        }
    }

    /// <summary>
    /// After the ducking readback: at most one read of the unit's meters,
    /// for whoever wants it. The ducking reduction wants it while the unit
    /// holds an enabled target, verified and not moved since, and the
    /// reduction was asked for within the hold. The mix meters want it while
    /// the mixer holds hardware mixing and asked within the hold. On a Wave
    /// XLR MK.2 the levels of its compressor and expander want its own meter
    /// block while the mixer asked within the hold. Anything else reads
    /// nothing and forgets the last reading; a read that fails leaves no
    /// reading, so the mixer meters its own copy of a mix instead and sends
    /// no compressor or expander levels.
    /// </summary>
    private void ReadUnitMetersLocked(IAudioDevice dev)
    {
        long now = NowMs;
        long hold = (long)UnitMetersHold.TotalMilliseconds;
        bool inMode = HardwareMixingEntry?.Invoke() is not null;
        IReadOnlyList<int> entries = [];
        bool forMixes, forDynamics;
        lock (_duckGate)
        {
            if (dev.Capabilities.Ducking)
            {
                bool asked = _duckMetersAskedAt is { } at && now - at < hold;
                bool held = _duckPending is null && _duckWritten is null && _duckFailed is null && !_duckDrifted;
                entries = asked && held && _duckVerified is { } verified ? ProMeters.DuckedEntries(verified) : [];
            }
            forMixes = inMode && _unitMetersAskedAt is { } mixesAt && now - mixesAt < hold;
            forDynamics = dev.Capabilities.DynamicsMeters && _dynamicsMetersAskedAt is { } dynamicsAt && now - dynamicsAt < hold;
            if (entries.Count == 0) _duckReduction = null;
            if (!forMixes) _unitMeters = null;
            if (!forDynamics) _dynamicsMeters = null;
        }
        _unitMetersWatched = entries.Count > 0 || forMixes || forDynamics;
        if (forDynamics)
        {
            // The Wave XLR MK.2's own block; no model reports both kinds.
            Mk2MeterReading? levels = dev.ReadInputMeters();
            lock (_duckGate)
            {
                _dynamicsMeters = levels;
                _dynamicsMetersAt = now;
            }
        }
        if (entries.Count == 0 && !forMixes) return;
        ProMeterReading? meters = dev.ReadMeters();
        lock (_duckGate)
        {
            if (entries.Count > 0)
            {
                _duckReduction = meters?.DuckReductionDb(entries);
                _duckReductionAt = now;
            }
            if (forMixes)
            {
                _unitMeters = meters;
                _unitMetersAt = now;
            }
        }
    }

    /// <summary>
    /// Between two polls, while the last one read the unit's meters for
    /// someone: one more read of them alone, so they move at about twice the
    /// poll rate. Nothing else is read. Tests drive it directly.
    /// </summary>
    internal void ReadUnitMetersBetweenPolls()
    {
        if (_suspended || !_unitMetersWatched) return;
        try
        {
            lock (_gate)
                if (_device is { Connected: true } dev) ReadUnitMetersLocked(dev);
        }
        catch (UsbHungException ex) { NoteHung(ex); }
        catch (Exception ex) { _log.LogDebug("unit meters: {msg}", ex.Message); }
    }

    private void ForgetDucking()
    {
        bool had;
        lock (_duckGate)
        {
            had = _duckPending is not null || _duckWritten is not null || _duckVerified is not null || _duckFailed is not null || _duckReadback is not null;
            ForgetDuckingSlotLocked();
        }
        if (had) DuckingStatusChanged?.Invoke();
    }

    /// <summary>The target posted since the last write, written by the device loop.</summary>
    private void WriteDucking()
    {
        lock (_gate)
        {
            if (_device is { Connected: true } dev && dev.Capabilities.Ducking) WriteDuckingLocked(dev, "update");
        }
    }

    /// <summary>
    /// Write the pending ducking target, if any, through the one writer of
    /// block 0x0001. A write that fails counts as a miss, as one the unit
    /// does not keep; the second fails the target.
    /// </summary>
    private void WriteDuckingLocked(IAudioDevice dev, string reason)
    {
        lock (_duckGate)
            if (_duckPending is null) return;
        try { WriteMixLocked(dev, ProMixPlan.Nothing, reason); }
        catch (UsbHungException) { throw; }
        catch (Exception) { }   // counted and logged by WriteMixLocked
    }

    /// <summary>
    /// The one way the device manager writes the Pro's block 0x0001: the
    /// caller's plan with the ducking target posted and not written yet, if
    /// the unit runs ducking, in one <see cref="IAudioDevice.WriteMix"/>.
    /// The ducking's pending, written and failed bookkeeping is the same
    /// whichever caller carried it. A failed write counts a miss for the
    /// ducking it carried and is rethrown.
    /// </summary>
    private ProMixWrite? WriteMixLocked(IAudioDevice dev, ProMixPlan plan, string reason)
    {
        plan = KeepingDuckedInputs(dev, plan);
        ProDuckingTarget? target = null;
        string owner = "";
        if (dev.Capabilities.Ducking)
            lock (_duckGate)
            {
                target = _duckPending;
                owner = _duckOwner;
            }
        ProMixWrite? write;
        try { write = dev.WriteMix(target is null ? plan : plan with { Ducking = target }); }
        catch (UsbHungException) { throw; }
        catch (Exception ex)
        {
            if (target is null) throw;
            _log.LogDebug("ducking ({reason}) on {dev}: {msg}", reason, dev.Info.DisplayName, ex.Message);
            bool failed = false;
            lock (_duckGate)
            {
                if (ReferenceEquals(_duckPending, target) && ++_duckMisses >= 2)
                {
                    _duckPending = null;
                    _duckFailed = $"{DuckingNotKept} ({ex.Message})";
                    failed = true;
                }
            }
            if (failed)
            {
                _log.LogWarning("{dev} {reason}: {msg}", dev.Info.DisplayName, DuckingNotKept, ex.Message);
                DuckingStatusChanged?.Invoke();
            }
            throw;
        }
        if (target is null || write is null) return write;
        lock (_duckGate)
        {
            if (ReferenceEquals(_duckPending, target)) _duckPending = null;
            _duckWritten = target;
        }
        string key = target.Enabled && owner.Length > 0 ? $", keyed by {owner}" : "";
        if (!write.Wrote)
        {
            _log.LogDebug("ducking ({reason}) on {dev}: already held, {target}{key}", reason, dev.Info.DisplayName, target, key);
            return write;
        }
        _log.LogInformation("ducking ({reason}) on {dev}: {target}{key}. Block 0x0008 {b8}. Block 0x0001 {b1}.",
            reason, dev.Info.DisplayName, target, key,
            write.Wrote8 ? "written: " + write.Change8 : "unchanged", write.Wrote1 ? "written: " + write.Change1 : "unchanged");
        return write;
    }

    /// <summary>
    /// The plan with the inputs the last ducking target posted puts after
    /// the ducker, so a unit-section write or a restore leaves them there
    /// instead of putting them back to 2 (see <see cref="ProMixPlan.DuckedInputs"/>).
    /// </summary>
    private ProMixPlan KeepingDuckedInputs(IAudioDevice dev, ProMixPlan plan)
    {
        if (!dev.Capabilities.Ducking) return plan;
        ProDuckingTarget? wanted;
        lock (_duckGate) wanted = _duckUnit == dev.Info.InstanceId ? _duckWanted : null;
        return wanted is { Enabled: true } && ProDucking.InputSections(wanted) is { Count: > 0 } inputs
            ? plan with { DuckedInputs = inputs } : plan;
    }

    /// <summary>
    /// After a poll read the blocks: what was written must read back held. A
    /// mismatch is written once more and a second one fails the target. A
    /// verified target that changed with nothing of ours to explain it was
    /// changed by another program: ours is written back, at most once every
    /// <see cref="DuckingRewriteSpacing"/>, and the journal says so once per
    /// connection. True when the readback changed.
    /// </summary>
    private bool VerifyDuckingLocked(IAudioDevice dev)
    {
        if (!dev.Capabilities.Ducking) return false;
        DuckingReadback? held = dev.ReadDucking();
        bool changed = false, settled = false, rewrite = false;
        long now = NowMs;
        lock (_duckGate)
        {
            if (held != _duckReadback)
            {
                _duckReadback = held;
                changed = settled = true;
            }
            if (_duckWritten is { } written)
            {
                _duckWritten = null;
                if (held is not null && ProDucking.Holds(held, written))
                {
                    _duckVerified = written;
                    _duckDrifted = false;
                    _duckMisses = 0;
                    settled = true;
                }
                else if (++_duckMisses >= 2)
                {
                    _duckPending = null;
                    _duckFailed = DuckingNotKept;
                    settled = true;
                    _log.LogWarning("{dev} {reason}: it read back {held} after {wanted}.",
                        dev.Info.DisplayName, DuckingNotKept, held is null ? "nothing" : $"pairs {string.Join(" ", held.Pairs)}", written);
                }
                else _duckPending ??= written;
            }
            else if (_duckPending is null && _duckFailed is null && _duckVerified is { } verified && held is not null)
            {
                // Not held until the rewrite reads back, which may wait out the spacing.
                bool moved = !ProDucking.Holds(held, verified);
                if (moved != _duckDrifted) settled = true;
                _duckDrifted = moved;
            }
            if (_duckDrifted && _duckPending is null && _duckWritten is null && _duckFailed is null && _duckVerified is { } kept
                && now >= _duckRewriteNotBefore)
            {
                _duckPending = _duckWanted ?? kept;
                _duckRewriteNotBefore = now + (long)DuckingRewriteSpacing.TotalMilliseconds;
                rewrite = true;
                if (!_duckElsewhereLogged)
                {
                    _duckElsewhereLogged = true;
                    _log.LogInformation("The {model}'s ducking no longer matches OpenXLR's ({target}); OpenXLR writes its own back, at most every {s} s.",
                        dev.Info.Model, _duckPending, (int)DuckingRewriteSpacing.TotalSeconds);
                }
            }
        }
        if (rewrite) WriteDuckingLocked(dev, "restore");
        if (settled) DuckingStatusChanged?.Invoke();
        return changed;
    }

    // The microphone EQ on the unit's own DSP. The mixer posts the EQ each
    // input should hold; the device loop writes it at the start of a sweep,
    // at most once per EqWriteSpacing, and the poll in the same sweep reads
    // it back. Posts and statuses take their own small lock, never the
    // device lock, so a post does not wait out a USB transfer.
    private sealed class EqSlot
    {
        public DeviceEq? Pending;   // the latest post, not written yet
        public DeviceEq? Wanted;    // what the input should hold: the last post
        public DeviceEq? Written;   // written, or found already held; the next poll reads it back
        public DeviceEq? Verified;  // read back equal
        public string? Failed;
        public string Owner = "";
        public int Misses;
        public bool Settled;        // the first write of this connection is done
        public long RewriteNotBefore;
    }

    private readonly object _eqGate = new();
    private readonly Dictionary<int, EqSlot> _eqSlots = [];
    private long _eqLastWrite = long.MinValue / 2;
    private readonly HashSet<string> _eqElsewhereLogged = [];
    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>The least time between two EQ writes to one unit: Wave Link's rate, ten a second.</summary>
    internal static readonly TimeSpan EqWriteSpacing = TimeSpan.FromMilliseconds(100);

    /// <summary>The least time between two rewrites of an EQ another program changed.</summary>
    internal static readonly TimeSpan EqRewriteSpacing = TimeSpan.FromSeconds(5);

    /// <summary>What a unit that did not keep a written EQ reports, after the unit's name.</summary>
    internal const string EqNotKept = "did not keep the EQ written to it";

    /// <summary>
    /// Raised under the device lock when an input's EQ status settles:
    /// verified, failed, or forgotten with the unit. Handlers only hand off.
    /// </summary>
    public event Action? EqStatusChanged;

    /// <summary>Whether the driven unit runs the EQ, with its name and firmware; null when none is driven or its model has none.</summary>
    internal UnitEqSupport? PublishedEqSupport => _published.Eq;

    /// <summary>
    /// The EQ one input of the driven unit should hold, replacing a value not
    /// written yet. A value equal to the one the unit was last read holding is
    /// not written again. Dropped while no unit is driven.
    /// </summary>
    internal void PostEq(int input, DeviceEq eq, string owner)
    {
        // An input the unit does not have holds no EQ: nothing to write and
        // nothing that failed.
        if (_published.Info is null || input < 1 || input > (_published.Message.Capabilities?.XlrInputs ?? 1)) return;
        bool wake;
        lock (_eqGate)
        {
            if (!_eqSlots.TryGetValue(input, out EqSlot? slot)) _eqSlots[input] = slot = new EqSlot();
            slot.Owner = owner;
            slot.Wanted = eq;
            if (slot.Failed is null && slot.Pending is null && slot.Written is null && eq.Equals(slot.Verified)) return;
            slot.Pending = eq;
            slot.Failed = null;
            slot.Misses = 0;
            wake = NowMs - _eqLastWrite >= (long)EqWriteSpacing.TotalMilliseconds;
        }
        if (wake) Wake();
    }

    /// <summary>Where the last post to one input stands.</summary>
    internal DeviceEqStatus EqStatus(int input)
    {
        if (_published.Info is null) return DeviceEqStatus.None;
        lock (_eqGate)
        {
            if (!_eqSlots.TryGetValue(input, out EqSlot? slot)) return DeviceEqStatus.None;
            if (slot.Failed is { } reason) return new(DeviceEqPhase.Failed, null, reason);
            if (slot.Pending is not null || slot.Written is not null) return new(DeviceEqPhase.Pending);
            return slot.Verified is { } held ? new(DeviceEqPhase.Verified, held) : DeviceEqStatus.None;
        }
    }

    private void Wake()
    {
        try { if (_wake.CurrentCount == 0) _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    /// <summary>
    /// No microphone holds this input's EQ any more (<see cref="IDeviceEqPort.ForgetEq"/>):
    /// its post is dropped, so a change made elsewhere is not written back over.
    /// </summary>
    internal void ForgetEq(int input)
    {
        lock (_eqGate) _eqSlots.Remove(input);
    }

    private void ForgetEq()
    {
        bool had;
        lock (_eqGate)
        {
            had = _eqSlots.Count > 0;
            _eqSlots.Clear();
        }
        if (had) EqStatusChanged?.Invoke();
    }

    /// <summary>
    /// Write the EQ posted since the last write: once the spacing has passed,
    /// and not while phantom power settles, when the firmware writes the same
    /// block itself; the latest value goes out once that ends. A write that
    /// fails counts as a miss, as one the unit does not keep.
    /// </summary>
    private void WriteEq()
    {
        lock (_gate)
        {
            if (_device is not { Connected: true } dev || dev.EqSupport?.Ready != true) return;
            long now = NowMs;
            if (now - _eqLastWrite < (long)EqWriteSpacing.TotalMilliseconds) return;
            if (_last is { PhantomSettling: true } or { PhantomSettling2: true }) return;
            List<(int Input, EqSlot Slot, DeviceEq Eq)> due = [];
            lock (_eqGate)
                foreach ((int input, EqSlot slot) in _eqSlots)
                    if (slot.Pending is { } eq) due.Add((input, slot, eq));
            if (due.Count == 0) return;
            bool failed = false;
            foreach ((int input, EqSlot slot, DeviceEq eq) in due)
            {
                if (input > dev.Capabilities.XlrInputs)
                {
                    // Posted while the unit before this one was driven.
                    lock (_eqGate) _eqSlots.Remove(input);
                    continue;
                }
                bool wrote;
                try { wrote = dev.SetEq(input, eq); }
                catch (UsbHungException) { throw; }
                catch (Exception ex)
                {
                    _log.LogDebug("EQ write to {dev} input {input}: {msg}", dev.Info.DisplayName, input, ex.Message);
                    lock (_eqGate)
                    {
                        if (!ReferenceEquals(slot.Pending, eq)) continue;
                        if (++slot.Misses >= 2) { slot.Pending = null; slot.Failed = $"{EqNotKept} ({ex.Message})"; failed = true; }
                    }
                    continue;
                }
                _eqLastWrite = now;
                bool first;
                lock (_eqGate)
                {
                    if (ReferenceEquals(slot.Pending, eq)) slot.Pending = null;
                    slot.Written = eq;
                    first = !slot.Settled;
                    slot.Settled = true;
                }
                if (!wrote) continue;
                if (first)
                    _log.LogInformation(eq.SwitchOnly
                        ? "{owner}'s EQ runs in software, so the EQ the {model} held is switched off."
                        : "{owner}'s EQ was written to the {model}, replacing the one it held.", slot.Owner, dev.Info.Model);
                else _log.LogDebug("EQ written to {dev} input {input}: {eq}", dev.Info.DisplayName, input, eq);
            }
            _eqLastWrite = now;
            if (failed) EqStatusChanged?.Invoke();
        }
    }

    /// <summary>
    /// After a poll read the block: what was written must read back equal,
    /// compared in the raw form. A mismatch is written once more and a second
    /// one fails the input. A verified EQ that changed with nothing of ours
    /// to explain it was changed by another program: ours is written back,
    /// at most once every <see cref="EqRewriteSpacing"/>, and the journal
    /// says so once per unit.
    /// </summary>
    private void VerifyEqLocked(IAudioDevice dev)
    {
        if (dev.EqSupport?.Ready != true) return;
        bool settled = false, rewrite = false;
        long now = NowMs;
        lock (_eqGate)
        {
            foreach ((int input, EqSlot slot) in _eqSlots)
            {
                if (dev.ReadEq(input) is not { } held) continue;
                if (slot.Written is { } written)
                {
                    slot.Written = null;
                    if (written.HeldBy(held))
                    {
                        slot.Verified = written;
                        slot.Misses = 0;
                        settled = true;
                    }
                    else if (++slot.Misses >= 2)
                    {
                        slot.Pending = null;
                        slot.Failed = EqNotKept;
                        settled = true;
                        _log.LogWarning("{dev} {reason}: input {input} read back {held} after {wanted}; {owner}'s EQ runs in software.",
                            dev.Info.DisplayName, EqNotKept, input, held, written, slot.Owner);
                    }
                    else slot.Pending ??= written;
                }
                else if (slot.Pending is null && slot.Failed is null && slot.Verified is { } verified && !verified.HeldBy(held)
                         && now >= slot.RewriteNotBefore)
                {
                    slot.Pending = slot.Wanted ?? verified;
                    slot.RewriteNotBefore = now + (long)EqRewriteSpacing.TotalMilliseconds;
                    rewrite = true;
                    if (_eqElsewhereLogged.Add(dev.Info.InstanceId))
                        _log.LogInformation("Another program changed the EQ {owner} runs on the {model}; OpenXLR writes its own back, at most every {s} s.",
                            slot.Owner, dev.Info.Model, (int)EqRewriteSpacing.TotalSeconds);
                }
            }
        }
        if (settled) EqStatusChanged?.Invoke();
        if (rewrite) Wake();
    }
}
