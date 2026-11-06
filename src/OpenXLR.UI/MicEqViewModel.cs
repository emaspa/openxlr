using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using OpenXLR.UI.Localization;

namespace OpenXLR.UI;

/// <summary>The four commands the EQ editor sends. The daemon client sends them; a test records them.</summary>
public interface IEqCommands
{
    Task SetEqAsync(string channel, MicEq eq);
    Task SetEqEnabledAsync(string channel, bool enabled);
    Task SetEqBandAsync(string channel, int band, EqBandChange change);
    /// <summary>Where the channel's EQ runs: <c>device</c> on the unit's own processor, or <c>software</c>.</summary>
    Task SetEqProcessingAsync(string channel, string value);
}

/// <summary>
/// A microphone's EQ as the window edits it: the switch, the bands, the
/// backend that runs it and why it cannot. Every value it shows is one the
/// daemon reported, or one the user just set, snapped with the daemon's own
/// rules (<see cref="MicEq"/>), so the two agree; a report that comes back
/// with something else wins once it is not an echo of an older edit.
/// </summary>
public sealed class MicEqViewModel : ViewModelBase, IDisposable
{
    /// <summary>How often one band's live changes go to the daemon while it is dragged.</summary>
    public static readonly TimeSpan SendInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>How long a recent edit wins over state reports that may still precede it.</summary>
    internal static readonly TimeSpan EchoWait = TimeSpan.FromSeconds(1);

    private readonly IEqCommands _commands;
    private readonly EqBandSender _sender;
    private readonly TimeProvider _time;
    private bool _enabled, _comparing, _disposed, _available = true;
    private bool? _awaitingEnabled;
    private long _awaitingEnabledSince;
    private string? _backend, _error;
    // The unit the channel carries, while its model has a hardware EQ.
    private bool _hasDevice, _deviceReady;
    private string? _deviceName, _deviceFirmware, _deviceReason, _deviceNote, _minimumFirmware;
    private string _processing = "device";
    // The state names the choice: a daemon from before the hardware EQ does not.
    private bool _processingReported;
    private string? _awaitingProcessing;
    private long _awaitingProcessingSince;
    private string _channelName;
    private EqBandViewModel? _selected;
    private MicEq? _awaitingShape;
    private long _awaitingShapeSince;

    /// <param name="time">The clock for the send interval and the echo wait; a test passes one it moves by hand.</param>
    public MicEqViewModel(IEqCommands commands, string channel, string channelName, TimeSpan? sendInterval = null, TimeProvider? time = null)
    {
        _commands = commands;
        Channel = channel;
        _channelName = channelName;
        _time = time ?? TimeProvider.System;
        _sender = new EqBandSender(sendInterval ?? SendInterval, (band, change) => _ = _commands.SetEqBandAsync(channel, band, change), _time);
        Bands = [.. Enumerable.Range(1, MicEq.BandCount).Select(n => new EqBandViewModel(this, n, MicEq.Default.Bands[n - 1]))];
        EqClipboard.Changed += OnClipboardChanged;
    }

    /// <summary>
    /// What the commands name: <c>xlr1</c>, <c>xlr2</c> or a capture input's
    /// id, or <c>mix:</c> and a mix's id for an output EQ, which the daemon
    /// client sends as the command's <c>mix</c>.
    /// </summary>
    public string Channel { get; }

    /// <summary>The prefix of <see cref="Channel"/> for a mix's output EQ.</summary>
    public const string MixPrefix = "mix:";

    /// <summary>An output mix's EQ: no unit, no choice of where, before the mix's plugins.</summary>
    public bool IsMix => Channel.StartsWith(MixPrefix, StringComparison.Ordinal);

    private string? _unitNote;
    /// <summary>
    /// Why the EQ is held off while hardware mixing runs, the daemon's
    /// sentence for a mix the Wave XLR Pro builds; null while the computer
    /// builds it. The button is greyed with it as its tooltip, and the saved
    /// EQ applies again when software mixing returns.
    /// </summary>
    public string? UnitNote
    {
        get => _unitNote;
        set
        {
            if (_unitNote == value) return;
            _unitNote = value;
            Raise(nameof(HasUnitNote));
            Raise(nameof(CanOpen));
            RaiseState();
        }
    }

    public bool HasUnitNote => _unitNote is not null;

    /// <summary>The EQ button opens the editor: always, but while the unit builds the mix.</summary>
    public bool CanOpen => _unitNote is null;

    /// <summary>The EQ profiles the editor lists and loads, the daemon's; null shows no profile row.</summary>
    public EqProfileLibrary? ProfileLibrary { get; init; }

    /// <summary>
    /// A capture input's EQ, or a mix's, which always runs on this computer:
    /// the editor offers no unit, no choice of where and no hardware badge,
    /// and names no low cut or ClipGuard, which neither has.
    /// </summary>
    public bool IsCaptureInput => Channel is not ("xlr1" or "xlr2");

    public string ChannelName
    {
        get => _channelName;
        internal set
        {
            if (!Set(ref _channelName, value)) return;
            Raise(nameof(Title));
            Raise(nameof(ButtonName));
        }
    }

    /// <summary>The editor's title.</summary>
    public string Title => Localizer.Format("EqWindowTitle", _channelName);

    public IReadOnlyList<EqBandViewModel> Bands { get; private set; }

    public bool CanAddBand => !_disposed && Bands.Count < Limits.MaxBands;
    public bool CanRemoveBand => !_disposed && Bands.Count > Limits.MinBands;
    public bool VariableBands => Limits.MaxBands > Limits.MinBands;

    /// <summary>The ranges and steps the daemon reports, or the model's own from a daemon that sends none.</summary>
    public EqLimits Limits { get; private set; } = new();

    /// <summary>The band the graph and the keyboard work on.</summary>
    public EqBandViewModel? SelectedBand
    {
        get => _selected;
        set
        {
            if (value is not null && !Bands.Contains(value)) return;
            if (!Set(ref _selected, value)) return;
            foreach (EqBandViewModel band in Bands) band.RaiseSelected();
        }
    }

    /// <summary>
    /// The EQ's switch. While a comparison bypasses it, the switch stays on:
    /// it says what the EQ will be when the comparison ends.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_disposed) return;
            if (_enabled == value && !_comparing) return;
            SetComparing(false, send: false);
            _enabled = value;
            Expect(value);
            _ = _commands.SetEqEnabledAsync(Channel, value);
            RaiseState();
        }
    }

    /// <summary>
    /// Bypass to compare: the daemon switches the EQ off while this is on and
    /// back on when it ends, the switch staying on throughout. Only an EQ that
    /// is on can be compared.
    /// </summary>
    public bool Comparing
    {
        get => _comparing;
        set
        {
            if (value && !_enabled) value = false;
            SetComparing(value, send: true);
        }
    }

    private void SetComparing(bool value, bool send)
    {
        if (_disposed) return;
        if (_comparing == value) { Raise(nameof(Comparing)); return; }
        _comparing = value;
        if (send)
        {
            Expect(!value);
            _ = _commands.SetEqEnabledAsync(Channel, !value);
        }
        RaiseState();
    }

    private void Expect(bool enabled)
    {
        _awaitingEnabled = enabled;
        _awaitingEnabledSince = _time.GetTimestamp();
    }

    /// <summary>An edit made at <paramref name="since"/> still waits for its echo: a different report is older than it.</summary>
    internal bool Waiting(long since) => _time.GetElapsedTime(since) < EchoWait;

    /// <summary>Which backend runs the EQ: <c>device</c> on the unit's own DSP, <c>software</c>, null while nothing runs it.</summary>
    public string? Backend => _backend;

    /// <summary>The unit's own DSP runs the EQ, on or off. Never on a capture input.</summary>
    public bool OnDevice => _backend == "device" && !IsCaptureInput;

    /// <summary>The unit that runs it, or the one the channel carries; null when the daemon names none.</summary>
    public string? DeviceName => _deviceName;

    /// <summary>The channel's choice of where its EQ runs: <c>device</c> or <c>software</c>.</summary>
    public string Processing => _processing;

    /// <summary>
    /// The footer offers the choice: the channel carries a unit whose model
    /// runs the EQ itself, and the daemon reports the choice. The unit may
    /// not be ready; the choice is kept and takes effect once it is.
    /// </summary>
    public bool HasProcessingChoice => _hasDevice && _processingReported;

    /// <summary>
    /// The unit can take the EQ now: it is ready and holds the bands. While it
    /// cannot (older firmware, settings it cannot read, a write it did not
    /// keep, more bands than it holds), the EQ runs in software whatever the
    /// choice, and the choice is kept for when it can.
    /// </summary>
    private bool UnitTakesEq => _deviceReady && Bands.Count <= MicEq.BandCount;

    /// <summary>The switch's "On the unit" side is lit: the unit is chosen and can take the EQ.</summary>
    public bool ProcessOnDevice => _processing == "device" && UnitTakesEq;

    /// <summary>The switch's "In software" side is lit: by choice, or because the unit cannot take the EQ.</summary>
    public bool ProcessInSoftware => _processing == "software" || !UnitTakesEq;

    /// <summary>The "On the unit" side can be pressed: the unit can take the EQ.</summary>
    public bool CanProcessOnDevice => UnitTakesEq;

    /// <summary>The "On the unit" side's tooltip: the unit named, or why it cannot take the EQ.</summary>
    public string ProcessOnDeviceTip => (UnitTakesEq ? null : DeviceNote)
        ?? Localizer.Format("EqProcessingDeviceTip", _deviceName ?? Localizer.Text("EqTheInterface"));

    /// <summary>
    /// A side of the switch was pressed: <c>device</c> or <c>software</c>.
    /// The side already chosen sends nothing and stays lit, so both sides are
    /// told again either way: a toggle that a press switched off turns back on.
    /// </summary>
    public void ChooseProcessing(string value)
    {
        if (_disposed) return;
        if (value is not ("device" or "software") || value == _processing || !HasProcessingChoice
            || (value == "device" && !UnitTakesEq))
        {
            Raise(nameof(ProcessOnDevice));
            Raise(nameof(ProcessInSoftware));
            return;
        }
        _processing = value;
        _awaitingProcessing = value;
        _awaitingProcessingSince = _time.GetTimestamp();
        _ = _commands.SetEqProcessingAsync(Channel, value);
        RaiseState();
    }

    /// <summary>The badge's tooltip: which unit runs the EQ.</summary>
    public string DeviceBadgeTip => _deviceName is { } name ? Localizer.Format("EqHardwareBadgeTip", name) : Localizer.Text("EqHardwareBadgeTipUnnamed");

    /// <summary>
    /// Why a unit that the channel carries does not run the EQ, while its
    /// model can: older firmware, a version or block it cannot read, a write
    /// it did not keep, or the user's choice. Null while the unit runs it or
    /// the channel carries no such unit.
    /// </summary>
    public string? DeviceNote
    {
        get
        {
            if (!_hasDevice || OnDevice) return null;
            string name = _deviceName ?? Localizer.Text("EqTheInterface");
            if (Bands.Count > MicEq.BandCount) return Localizer.Format("EqDeviceBandLimit", name, MicEq.BandCount);
            if (_deviceReady) return _processing == "software" ? Localizer.Format("EqDeviceBySoftware", name) : null;
            return _deviceReason switch
            {
                "firmwareOld" => Localizer.Format("EqDeviceFirmwareOld", name, _deviceFirmware ?? "?", _minimumFirmware ?? "1.0.15"),
                "firmwareUnknown" => Localizer.Format("EqDeviceFirmwareUnknown", name, _minimumFirmware ?? "1.0.15"),
                "unsupportedBlock" => Localizer.Format("EqDeviceUnsupported", name),
                "failed" => Localizer.Format("EqDeviceFailed", name),
                _ => _deviceNote,
            };
        }
    }

    public bool HasDeviceNote => DeviceNote is not null;

    /// <summary>
    /// The note under the editor's switch: why the unit does not run the EQ.
    /// Software by choice needs no sentence there, since the switch shows it.
    /// </summary>
    public string? EditorNote => HasProcessingChoice && _deviceReady && _processing == "software" ? null : DeviceNote;

    public bool HasEditorNote => EditorNote is not null;

    /// <summary>The EQ button's tooltip, which says where the EQ runs.</summary>
    public string ButtonTip => _unitNote ?? (IsMix ? Localizer.Text("EqButtonTipMix") : null) ?? (OnDevice
        ? Localizer.Format("EqButtonTipDevice", _deviceName ?? Localizer.Text("EqTheInterface"))
        : IsCaptureInput ? Localizer.Text("EqButtonTipCapture")
        : HasDeviceNote ? Localizer.Text("EqButtonTip") + " " + DeviceNote : Localizer.Text("EqButtonTip"));

    /// <summary>False when the EQ cannot run, and <see cref="Error"/> says why.</summary>
    public bool Available => _available;

    public string? Error => _error;

    public bool HasError => _error is not null || !_available;

    /// <summary>The EQ is on and its stage runs: the button is lit.</summary>
    public bool IsLit => _enabled && !_comparing && _available && _error is null && _backend is not null && _unitNote is null;

    /// <summary>The EQ cannot run: the button takes the alert colour.</summary>
    public bool IsFaulty => !_available || _error is not null;

    /// <summary>The EQ button's name for a screen reader: whose EQ, and its state.</summary>
    public string ButtonName => IsFaulty ? Localizer.Format("EqButtonNameUnavailable", _channelName)
        : _enabled ? Localizer.Format("EqButtonNameOn", _channelName) : Localizer.Format("EqButtonNameOff", _channelName);

    /// <summary>The editor's footer: what runs the EQ, or why nothing does.</summary>
    public string Status => _error is { } error ? error
        : _unitNote is { } held ? held
        : !_available ? Localizer.Text("EqUnavailable")
        : _comparing ? Localizer.Text("EqStatusComparing")
        : !_enabled ? Localizer.Text("EqStatusOff")
        : _backend switch
        {
            "software" => IsMix ? Localizer.Text("EqStatusSoftwareMix")
                : IsCaptureInput ? Localizer.Text("EqStatusSoftwareCapture") : Localizer.Text("EqStatusSoftware"),
            "device" => _deviceName is { } name ? Localizer.Format("EqStatusDeviceNamed", name) : Localizer.Text("EqStatusDevice"),
            _ => Localizer.Text("EqStatusStarting"),
        };

    private void RaiseState()
    {
        Raise(nameof(Enabled));
        Raise(nameof(Comparing));
        Raise(nameof(IsLit));
        Raise(nameof(IsFaulty));
        Raise(nameof(HasError));
        Raise(nameof(ButtonName));
        Raise(nameof(Status));
        Raise(nameof(Backend));
        Raise(nameof(Available));
        Raise(nameof(Error));
        Raise(nameof(OnDevice));
        Raise(nameof(DeviceName));
        Raise(nameof(Processing));
        Raise(nameof(HasProcessingChoice));
        Raise(nameof(ProcessOnDevice));
        Raise(nameof(ProcessInSoftware));
        Raise(nameof(CanProcessOnDevice));
        Raise(nameof(ProcessOnDeviceTip));
        Raise(nameof(DeviceBadgeTip));
        Raise(nameof(DeviceNote));
        Raise(nameof(HasDeviceNote));
        Raise(nameof(EditorNote));
        Raise(nameof(HasEditorNote));
        Raise(nameof(ButtonTip));
        Raise(nameof(CanAddBand));
        Raise(nameof(CanRemoveBand));
        Raise(nameof(VariableBands));
        Raise(nameof(CanPaste));
        Changed?.Invoke();
    }

    /// <summary>Something the graph draws changed: a band, the switch or a comparison.</summary>
    public event Action? Changed;

    internal void RaiseChanged() => Changed?.Invoke();

    internal TimeProvider Time => _time;

    /// <summary>
    /// Take a channel's <c>eq</c> from a state push, and the mixer's
    /// <c>eqLimits</c>. A band being dragged keeps what the user is doing, and
    /// a recent edit holds over state reports for a second at most, including
    /// an older report arriving after a matching echo.
    /// </summary>
    public void Apply(JsonNode eq, JsonNode? limits)
    {
        if (_disposed) return;
        EqLimits read = ReadLimits(limits) ?? new EqLimits();
        if (read != Limits)
        {
            Limits = read;
            foreach (EqBandViewModel band in Bands) band.RaiseLimits();
        }
        bool enabled = eq["enabled"]?.GetValue<bool>() ?? false;
        _backend = eq["backend"]?.GetValue<string>();
        _available = eq["available"]?.GetValue<bool>() ?? true;
        _error = eq["error"]?.GetValue<string>();
        // An older daemon sends neither: no unit, the EQ where it always ran.
        string? processing = eq["processing"]?.GetValue<string>();
        _processingReported = processing is "device" or "software";
        processing = _processingReported ? processing! : "device";
        // Even a matching echo can precede an older one. Keep the choice for the whole hold window.
        if (_awaitingProcessing is { } chosen && Waiting(_awaitingProcessingSince)) _processing = chosen;
        else
        {
            _awaitingProcessing = null;
            _processing = processing;
        }
        JsonNode? device = eq["device"];
        _hasDevice = device is not null && !IsCaptureInput;
        _deviceReady = device?["ready"]?.GetValue<bool>() ?? false;
        _deviceName = device?["name"]?.GetValue<string>();
        _deviceFirmware = device?["firmware"]?.GetValue<string>();
        _deviceReason = device?["reason"]?.GetValue<string>();
        _deviceNote = device?["note"]?.GetValue<string>();
        _minimumFirmware = device?["minimumFirmware"]?.GetValue<string>();
        if (_awaitingEnabled is bool expected && Waiting(_awaitingEnabledSince)) enabled = expected;
        else _awaitingEnabled = null;
        if (_comparing)
        {
            // Switched on from elsewhere while compared: the comparison is over.
            if (enabled) { _comparing = false; _enabled = true; }
        }
        else
        {
            _enabled = enabled;
        }
        if (eq["bands"] is JsonArray bands && bands.Count is >= MicEq.MinimumBands and <= MicEq.MaximumBands)
        {
            EqBand?[] values = [.. bands.Select(ReadBand)];
            bool waiting = _awaitingShape is { } expectedShape && bands.Count != expectedShape.Bands.Count && Waiting(_awaitingShapeSince);
            if (!waiting && values.All(b => MicEq.Check(b) is null))
            {
                if (!Waiting(_awaitingShapeSince)) _awaitingShape = null;
                ResizeBands([.. values.Select(b => b!)]);
                for (int i = 0; i < Bands.Count; i++) Bands[i].ApplyReported(values[i]!);
            }
        }
        RaiseState();
    }

    private static EqBand? ReadBand(JsonNode? node)
    {
        if (node is null) return null;
        try
        {
            return new EqBand
            {
                Enabled = node["enabled"]?.GetValue<bool>() ?? false,
                Type = node["type"]?.GetValue<string>() ?? MicEq.Peak,
                Frequency = node["frequency"]?.GetValue<double>() ?? 1000,
                Gain = node["gain"]?.GetValue<double>() ?? 0,
                Q = node["q"]?.GetValue<double>() ?? MicEq.DefaultQ,
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return null; }
    }

    private static EqLimits? ReadLimits(JsonNode? node)
    {
        if (node is null) return null;
        try
        {
            EqLimits.EqRange Range(JsonNode? range, EqLimits.EqRange fallback) => range is null ? fallback
                : new(range["min"]?.GetValue<double>() ?? fallback.Min, range["max"]?.GetValue<double>() ?? fallback.Max,
                    range["step"]?.GetValue<double>() ?? fallback.Step);
            EqLimits defaults = EqLimits.Instance;
            JsonNode? q = node["q"];
            return defaults with
            {
                MinBands = node["minBands"]?.GetValue<int>() is int min && min >= MicEq.MinimumBands && min <= MicEq.MaximumBands ? min : MicEq.BandCount,
                MaxBands = node["maxBands"]?.GetValue<int>() is int max && max >= MicEq.BandCount && max <= MicEq.MaximumBands ? max : MicEq.BandCount,
                Frequency = Range(node["frequency"], defaults.Frequency),
                Gain = Range(node["gain"], defaults.Gain),
                Q = q is null ? defaults.Q : new(q["min"]?.GetValue<double>() ?? defaults.Q.Min,
                    q["max"]?.GetValue<double>() ?? defaults.Q.Max, q["log10Step"]?.GetValue<double>() ?? defaults.Q.Log10Step),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>The EQ as shown: the switch as the user set it and its bands.</summary>
    public MicEq Current => new() { Enabled = _enabled, Bands = [.. Bands.Select(b => b.Band)] };

    /// <summary>Put an EQ on this microphone: one <c>setEq</c>, in place of any band change still waiting.</summary>
    public void Replace(MicEq eq)
    {
        if (ShowLocal(eq) is { } shown) _ = _commands.SetEqAsync(Channel, shown);
    }

    /// <summary>
    /// Show an EQ a loaded profile puts on this microphone, which the caller
    /// asks the daemon for its own way: it wins over older reports as an
    /// edit made here does.
    /// </summary>
    public void ShowLoaded(MicEq eq) => ShowLocal(eq);

    private MicEq? ShowLocal(MicEq eq)
    {
        if (_disposed || MicEq.Check(eq) is not null) return null;
        eq = MicEq.Snap(eq);
        _sender.Clear();
        if (eq.Bands.Count != Bands.Count) { _awaitingShape = eq; _awaitingShapeSince = _time.GetTimestamp(); }
        ResizeBands(eq.Bands);
        SetComparing(false, send: false);
        _enabled = eq.Enabled;
        Expect(eq.Enabled);
        for (int i = 0; i < Bands.Count; i++) Bands[i].ShowLocal(eq.Bands[i]);
        RaiseState();
        return eq;
    }

    private void ResizeBands(IReadOnlyList<EqBand> values)
    {
        if (values.Count == Bands.Count) return;
        _sender.Clear();
        int selected = _selected?.Number ?? 1;
        Bands = [.. values.Select((band, index) => new EqBandViewModel(this, index + 1, band))];
        SelectedBand = Bands[Math.Min(selected, Bands.Count) - 1];
        Raise(nameof(Bands));
    }

    public void AddBand() { if (CanAddBand) Replace(Current.WithAddedBand()); }

    public void RemoveBand(EqBandViewModel band)
    {
        if (CanRemoveBand && Bands.Contains(band)) Replace(Current.WithoutBand(band.Number));
    }

    /// <summary>Wave Link's four bands again, the switch left as it is.</summary>
    public void Reset() => Replace(MicEq.Default with { Enabled = _enabled });

    public void Copy() { if (!_disposed) EqClipboard.Value = Current; }

    public bool CanPaste => !_disposed && EqClipboard.Value is { } eq && eq.Bands.Count >= Limits.MinBands && eq.Bands.Count <= Limits.MaxBands;

    public void Paste()
    {
        if (CanPaste && EqClipboard.Value is { } copied) Replace(copied);
    }

    private void OnClipboardChanged() => Raise(nameof(CanPaste));

    /// <summary>Queue part of a band for the daemon, at most once a send interval per band.</summary>
    internal void Send(EqBandViewModel band, EqBandChange change)
    {
        if (!_disposed && Bands.Contains(band)) _sender.Queue(band.Number, change);
    }

    /// <summary>The window closes: a comparison ends and the EQ comes back on.</summary>
    public void EndComparison() => Comparing = false;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _comparing = false;
        EqClipboard.Changed -= OnClipboardChanged;
        // This EQ left the mixer. Pending edits must not reach a replacement with the same channel id.
        _sender.Clear();
        _sender.Dispose();
    }
}

/// <summary>
/// One band of the editor. Its values are snapped and held inside the ranges
/// here, with the daemon's rules, so what is shown is what the daemon will
/// keep, and every change goes out through the EQ's rate limited sender.
/// </summary>
public sealed class EqBandViewModel : ViewModelBase
{
    private EqBand _band;
    private EqBand? _awaiting;
    private long _awaitingSince;
    private EqBand? _reported;
    private bool _dragging;

    internal EqBandViewModel(MicEqViewModel owner, int number, EqBand band)
    {
        Owner = owner;
        Number = number;
        _band = band;
    }

    public MicEqViewModel Owner { get; }

    /// <summary>1 to 16, as the commands and the handles number the bands.</summary>
    public int Number { get; }

    public EqBand Band => _band;

    public bool Enabled
    {
        get => _band.Enabled;
        set { if (value != _band.Enabled) Edit(_band with { Enabled = value }, new EqBandChange(Enabled: value)); }
    }

    public string Type
    {
        get => _band.Type;
        set { if (value != _band.Type && MicEq.TypeIndex(value) >= 0) Edit(_band with { Type = value }, new EqBandChange(Type: value)); }
    }

    /// <summary>The type as the picker lists it.</summary>
    public EqTypeChoice TypeChoice
    {
        get => EqTypeChoice.Of(_band.Type);
        set { if (value is not null) Type = value.Type; }
    }

    public double Frequency
    {
        get => _band.Frequency;
        set => SetFrequency(value);
    }

    public double Gain
    {
        get => _band.Gain;
        set => SetGain(value);
    }

    public double Q
    {
        get => _band.Q;
        set => SetQ(value);
    }

    /// <summary>The frequency field: whole hertz.</summary>
    public decimal? FrequencyValue
    {
        get => (decimal)_band.Frequency;
        set { if (value is decimal v) SetFrequency((double)v); else Raise(); }
    }

    /// <summary>The gain field: quarter decibels.</summary>
    public decimal? GainValue
    {
        get => (decimal)_band.Gain;
        set { if (value is decimal v) SetGain((double)v); else Raise(); }
    }

    /// <summary>
    /// The Q field. Its spinner steps 0.01, finer than the grid near 1, so a
    /// value that snaps back onto the Q it came from moves one grid step its
    /// way instead, and the spinner always moves.
    /// </summary>
    public decimal? QValue
    {
        get => Math.Round((decimal)_band.Q, 2);
        set
        {
            if (value is not decimal v) { Raise(); return; }
            double wanted = (double)v, snapped = MicEq.SnapQ(Math.Clamp(wanted, Owner.Limits.Q.Min, Owner.Limits.Q.Max));
            if (snapped == _band.Q && Math.Abs(wanted - _band.Q) > 1e-9) StepQ(wanted > _band.Q ? 1 : -1);
            else SetQ(wanted);
        }
    }

    public decimal FrequencyMinimum => (decimal)Owner.Limits.Frequency.Min;
    public decimal FrequencyMaximum => (decimal)Owner.Limits.Frequency.Max;
    public decimal GainMinimum => (decimal)Owner.Limits.Gain.Min;
    public decimal GainMaximum => (decimal)Owner.Limits.Gain.Max;
    public decimal GainIncrement => (decimal)Owner.Limits.Gain.Step;
    public decimal QMinimum => (decimal)Owner.Limits.Q.Min;
    public decimal QMaximum => (decimal)Owner.Limits.Q.Max;

    /// <summary>The fields' names for a screen reader, each with the band's number.</summary>
    public string RemoveName => Localizer.Format("EqRemoveBandName", Number);

    public string SwitchName => Localizer.Format("EqBandSwitchName", Number);
    public string TypeFieldName => Localizer.Format("EqBandTypeName", Number);
    public string FrequencyName => Localizer.Format("EqBandFrequencyName", Number);
    public string GainName => Localizer.Format("EqBandGainName", Number);
    public string QName => Localizer.Format("EqBandQName", Number);

    internal void RaiseLimits()
    {
        foreach (string name in new[] { nameof(FrequencyMinimum), nameof(FrequencyMaximum), nameof(GainMinimum), nameof(GainMaximum),
                     nameof(GainIncrement), nameof(QMinimum), nameof(QMaximum) })
            Raise(name);
    }

    /// <summary>The type takes a gain: low shelf, bell and high shelf. The others ignore theirs and keep it.</summary>
    public bool UsesGain => MicEq.UsesGain(_band.Type);

    public bool IsSelected => ReferenceEquals(Owner.SelectedBand, this);

    internal void RaiseSelected() => Raise(nameof(IsSelected));

    public string TypeName => EqTypeChoice.Of(_band.Type).Name;

    public string FrequencyText => EqText.Frequency(_band.Frequency);

    public string GainText => EqText.Gain(_band.Gain);

    public string QText => EqText.Q(_band.Q);

    /// <summary>A handle's name for a screen reader: "Band 2, Bell, 310 Hz, 0 dB, Q 0.70", and off when it is.</summary>
    public string Description
    {
        get
        {
            string values = UsesGain
                ? Localizer.Format("EqBandValues", TypeName, FrequencyText, GainText, QText)
                : Localizer.Format("EqBandValuesNoGain", TypeName, FrequencyText, QText);
            return _band.Enabled ? Localizer.Format("EqBandName", Number, values) : Localizer.Format("EqBandNameOff", Number, values);
        }
    }

    /// <summary>The handle's tooltip: its values, then what the pointer does with it.</summary>
    public string Tip => Description + "\n" + (UsesGain ? Localizer.Text("EqHandleTip") : Localizer.Text("EqHandleTipNoGain"));

    /// <summary>A drag is under way: reports keep their hands off the values until it ends.</summary>
    public bool Dragging => _dragging;

    public void BeginDrag()
    {
        _dragging = true;
        Owner.SelectedBand = this;
    }

    public void EndDrag()
    {
        _dragging = false;
        // A report that came in meanwhile and differs from the last edit is
        // older than it; the echo of the last edit settles the values.
        if (_reported is { } reported && _awaiting is null) Show(reported);
    }

    public void SetFrequency(double hz)
    {
        EqLimits.EqRange range = Owner.Limits.Frequency;
        double value = Math.Clamp(MicEq.SnapFrequency(hz), range.Min, range.Max);
        if (value != _band.Frequency) Edit(_band with { Frequency = value }, new EqBandChange(Frequency: value));
        else Raise(nameof(FrequencyValue));
    }

    public void SetGain(double db)
    {
        EqLimits.EqRange range = Owner.Limits.Gain;
        double value = Math.Clamp(MicEq.SnapGain(db), range.Min, range.Max);
        if (value != _band.Gain) Edit(_band with { Gain = value }, new EqBandChange(Gain: value));
        else Raise(nameof(GainValue));
    }

    public void SetQ(double q)
    {
        double value = MicEq.SnapQ(Math.Clamp(q, Owner.Limits.Q.Min, Owner.Limits.Q.Max));
        if (value != _band.Q) Edit(_band with { Q = value }, new EqBandChange(Q: value));
        else Raise(nameof(QValue));
    }

    /// <summary>A drag's position: frequency and gain together, in one change.</summary>
    public void DragTo(double hz, double db)
    {
        EqLimits limits = Owner.Limits;
        double frequency = Math.Clamp(MicEq.SnapFrequency(hz), limits.Frequency.Min, limits.Frequency.Max);
        double gain = Math.Clamp(MicEq.SnapGain(db), limits.Gain.Min, limits.Gain.Max);
        if (frequency == _band.Frequency && gain == _band.Gain) return;
        Edit(_band with { Frequency = frequency, Gain = gain },
            new EqBandChange(Frequency: frequency != _band.Frequency ? frequency : null, Gain: gain != _band.Gain ? gain : null));
    }

    /// <summary>A gainless band's drag: frequency sideways, Q up and down, in one change.</summary>
    public void DragToWithQ(double hz, double q)
    {
        EqLimits limits = Owner.Limits;
        double frequency = Math.Clamp(MicEq.SnapFrequency(hz), limits.Frequency.Min, limits.Frequency.Max);
        double snapped = MicEq.SnapQ(Math.Clamp(q, limits.Q.Min, limits.Q.Max));
        if (frequency == _band.Frequency && snapped == _band.Q) return;
        Edit(_band with { Frequency = frequency, Q = snapped },
            new EqBandChange(Frequency: frequency != _band.Frequency ? frequency : null, Q: snapped != _band.Q ? snapped : null));
    }

    /// <summary>Move Q by whole steps of the daemon's grid: one per wheel notch or key press.</summary>
    public void StepQ(int steps)
    {
        if (steps == 0) return;
        EqLimits.EqQRange range = Owner.Limits.Q;
        double log = MicEq.QLog10(_band.Q) + steps * range.Log10Step;
        SetQ(Math.Clamp(Math.Pow(10, log), range.Min, range.Max));
    }

    /// <summary>Move the frequency by a fraction of an octave, at least one hertz.</summary>
    public void StepFrequency(double octaves)
    {
        if (octaves == 0) return;
        double target = MicEq.SnapFrequency(_band.Frequency * Math.Pow(2, octaves));
        if (target == _band.Frequency) target += Math.Sign(octaves) * MicEq.FrequencyStep;
        SetFrequency(target);
    }

    public void StepGain(double db) => SetGain(_band.Gain + db);

    /// <summary>The daemon reported this band.</summary>
    internal void ApplyReported(EqBand band)
    {
        _reported = band;
        if (_dragging) return;
        if (_awaiting is not null && Owner.Waiting(_awaitingSince)) return;
        _awaiting = null;
        Show(band);
    }

    /// <summary>Show values set here without sending them; the caller sends them its own way.</summary>
    internal void ShowLocal(EqBand band)
    {
        _awaiting = band;
        _awaitingSince = Owner.Time.GetTimestamp();
        Show(band);
    }

    private void Edit(EqBand band, EqBandChange change)
    {
        ShowLocal(band);
        Owner.Send(this, change);
    }

    private void Show(EqBand band)
    {
        if (band == _band) return;
        EqBand old = _band;
        _band = band;
        if (old.Enabled != band.Enabled) Raise(nameof(Enabled));
        if (old.Type != band.Type)
        {
            Raise(nameof(Type));
            Raise(nameof(TypeChoice));
            Raise(nameof(TypeName));
            Raise(nameof(UsesGain));
        }
        if (old.Frequency != band.Frequency) { Raise(nameof(Frequency)); Raise(nameof(FrequencyValue)); Raise(nameof(FrequencyText)); }
        if (old.Gain != band.Gain) { Raise(nameof(Gain)); Raise(nameof(GainValue)); Raise(nameof(GainText)); }
        if (old.Q != band.Q) { Raise(nameof(Q)); Raise(nameof(QValue)); Raise(nameof(QText)); }
        Raise(nameof(Band));
        Raise(nameof(Description));
        Raise(nameof(Tip));
        Owner.RaiseChanged();
    }
}

/// <summary>
/// The band changes on their way to the daemon. Each band sends at most once
/// an interval and always its last value: a change inside the interval waits
/// and merges with the ones after it, so one <c>setEqBand</c> carries every
/// field that moved, and the last change of a drag is never lost. A change
/// that finds its band's interval over goes at once, and what follows it
/// inside the next interval goes when that interval ends, so the last value
/// of each field always reaches the daemon, though not always in one command.
/// </summary>
internal sealed class EqBandSender : IDisposable
{
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly long _interval;
    private readonly Action<int, EqBandChange> _send;
    private readonly EqBandChange?[] _pending = new EqBandChange?[MicEq.MaximumBands];
    private readonly long[] _last = new long[MicEq.MaximumBands];
    private readonly ITimer _timer;
    private bool _disposed;

    public EqBandSender(TimeSpan interval, Action<int, EqBandChange> send, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _interval = (long)(interval.TotalSeconds * _time.TimestampFrequency);
        _send = send;
        Array.Fill(_last, long.MinValue / 2);
        _timer = _time.CreateTimer(_ => Due(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Queue(int band, EqBandChange change)
    {
        lock (_gate)
        {
            if (_disposed) return;
            int i = band - 1;
            _pending[i] = Merge(_pending[i], change);
            long now = _time.GetTimestamp();
            // Sent under the lock, so two sends of one band keep their order
            // on their way into the client's send queue.
            if (now - _last[i] >= _interval) Take(i, now);
            else Arm(now);
        }
    }

    /// <summary>Forget what waits: a whole EQ is about to replace it.</summary>
    public void Clear()
    {
        lock (_gate) Array.Clear(_pending);
    }

    private void Due()
    {
        lock (_gate)
        {
            if (_disposed) return;
            long now = _time.GetTimestamp();
            for (int i = 0; i < _pending.Length; i++)
                if (_pending[i] is not null && now - _last[i] >= _interval) Take(i, now);
            Arm(now);
        }
    }

    private void Take(int i, long now)
    {
        EqBandChange change = _pending[i]!;
        _pending[i] = null;
        _last[i] = now;
        _send(i + 1, change);
    }

    private void Arm(long now)
    {
        long next = long.MaxValue;
        for (int i = 0; i < _pending.Length; i++)
            if (_pending[i] is not null) next = Math.Min(next, _last[i] + _interval);
        if (next == long.MaxValue) return;
        double milliseconds = Math.Max(0, next - now) * 1000.0 / _time.TimestampFrequency;
        _timer.Change(TimeSpan.FromMilliseconds(Math.Ceiling(milliseconds)), Timeout.InfiniteTimeSpan);
    }

    private static EqBandChange Merge(EqBandChange? earlier, EqBandChange later) => earlier is null ? later : new(
        later.Enabled ?? earlier.Enabled, later.Type ?? earlier.Type, later.Frequency ?? earlier.Frequency,
        later.Gain ?? earlier.Gain, later.Q ?? earlier.Q);

    public void Dispose()
    {
        lock (_gate)
        {
            // What waits still goes: the last value of a drag is never lost.
            for (int i = 0; i < _pending.Length; i++)
                if (_pending[i] is not null) Take(i, _time.GetTimestamp());
            _disposed = true;
        }
        _timer.Dispose();
    }
}

/// <summary>One EQ held between microphones: Copy on one, Paste on another, for this run of the window.</summary>
public static class EqClipboard
{
    private static MicEq? _value;

    public static MicEq? Value
    {
        get => _value;
        set { _value = value; Changed?.Invoke(); }
    }

    public static event Action? Changed;
}

/// <summary>A band type as the editor names and draws it.</summary>
public sealed record EqTypeChoice(string Type, string Icon)
{
    /// <summary>
    /// The six types in the order of the daemon's list. The icons are our own
    /// line drawings of each curve in a 24 by 24 box, drawn with a stroke.
    /// </summary>
    public static IReadOnlyList<EqTypeChoice> All { get; } =
    [
        new(MicEq.LowCut, "M2,20 C6,20 8,7 12,7 L22,7"),
        new(MicEq.LowShelf, "M2,7 L6,7 C10,7 10,15 14,15 L22,15"),
        new(MicEq.Peak, "M2,16 L6,16 C9,16 10,5 12,5 C14,5 15,16 18,16 L22,16"),
        new(MicEq.Notch, "M2,7 L8,7 C10,7 11,21 12,21 C13,21 14,7 16,7 L22,7"),
        new(MicEq.HighShelf, "M2,15 L10,15 C14,15 14,7 18,7 L22,7"),
        new(MicEq.HighCut, "M2,7 L12,7 C16,7 18,20 22,20"),
    ];

    public static EqTypeChoice Of(string type) => All.FirstOrDefault(c => c.Type == type) ?? All[2];

    /// <summary>The name in the window's language. The bell is a bell here, whatever other editors call it.</summary>
    public string Name => Type switch
    {
        MicEq.LowCut => Localizer.Text("EqTypeLowCut"),
        MicEq.LowShelf => Localizer.Text("EqTypeLowShelf"),
        MicEq.Notch => Localizer.Text("EqTypeNotch"),
        MicEq.HighShelf => Localizer.Text("EqTypeHighShelf"),
        MicEq.HighCut => Localizer.Text("EqTypeHighCut"),
        _ => Localizer.Text("EqTypePeak"),
    };

    /// <summary>What the type does, for its tooltip.</summary>
    public string Hint => Type switch
    {
        MicEq.LowCut => Localizer.Text("EqTypeLowCutTip"),
        MicEq.LowShelf => Localizer.Text("EqTypeLowShelfTip"),
        MicEq.Notch => Localizer.Text("EqTypeNotchTip"),
        MicEq.HighShelf => Localizer.Text("EqTypeHighShelfTip"),
        MicEq.HighCut => Localizer.Text("EqTypeHighCutTip"),
        _ => Localizer.Text("EqTypePeakTip"),
    };

    public override string ToString() => Name;
}

/// <summary>How the editor writes the EQ's numbers.</summary>
public static class EqText
{
    public static string Frequency(double hz) => hz >= 1000
        ? Localizer.Format("EqKilohertz", (hz / 1000).ToString(hz >= 10000 ? "0.#" : "0.##", System.Globalization.CultureInfo.CurrentCulture))
        : Localizer.Format("EqHertz", hz.ToString("0", System.Globalization.CultureInfo.CurrentCulture));

    public static string Gain(double db) =>
        Localizer.Format("EqDecibels", db.ToString("+0.##;-0.##;0", System.Globalization.CultureInfo.CurrentCulture));

    public static string Q(double q) => q.ToString("0.00", System.Globalization.CultureInfo.CurrentCulture);
}
