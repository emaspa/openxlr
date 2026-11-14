using OpenXLR.UI.Localization;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace OpenXLR.UI;

/// <summary>The steps of Mic setup, in the order the window offers them.</summary>
public enum MicSetupStep
{
    /// <summary>Where the microphone goes for the unit it is on, and phantom power when it needs it.</summary>
    Position,
    /// <summary>The microphone's level before any processing, against a target band, beside the interface's gain, and the room measured for the expander.</summary>
    Level,
    /// <summary>A take, its waveform, raw and processed, and hearing yourself through the chain.</summary>
    Listen,
    /// <summary>Noise removal, the expander's switch and threshold, the EQ, low cut and ClipGuard, the insert chain and the starting presets.</summary>
    Shape,
}

/// <summary>What the microphone of a Mic setup is, as far as the Position step's tips go.</summary>
public enum MicSetupUnitKind
{
    /// <summary>On no unit OpenXLR drives: a USB microphone, a headset or an interface of another make.</summary>
    None,
    /// <summary>The built-in capsule of a Wave:3 or Wave:1.</summary>
    Capsule,
    /// <summary>A microphone on an XLR jack of a Wave XLR, a Wave XLR Pro or an XLR Dock.</summary>
    XlrJack,
}

/// <summary>One entry of the step bar: its name and whether it is the step shown.</summary>
public sealed class MicSetupStepItem(MicSetupViewModel owner, MicSetupStep step, string name) : ViewModelBase
{
    public MicSetupStep Step { get; } = step;
    public string Name { get; } = name;

    /// <summary>The step shown; choosing one shows it, and the step shown cannot be unchosen.</summary>
    public bool IsSelected
    {
        get => owner.Step == Step;
        set { if (value) owner.Step = Step; Raise(nameof(IsSelected)); }
    }

    internal void Refresh() => Raise(nameof(IsSelected));
}

/// <summary>One starting preset on the Shape step: lit while the microphone's EQ holds it.</summary>
public sealed class MicSetupPresetItem(MicSetupViewModel owner, MicSetupPreset preset) : ViewModelBase
{
    public MicSetupPreset Preset { get; } = preset;
    public string Name => Preset.Name;
    public string Hint => Preset.Hint;

    /// <summary>The EQ is this preset now: applied and not edited, undone or switched off since.</summary>
    public bool IsActive => ReferenceEquals(owner.ActivePreset, Preset);

    internal void Refresh() => Raise(nameof(IsActive));
}

/// <summary>A microphone the picker offers: its channel's name and that channel's Mic setup.</summary>
public sealed record MicSetupInput(string Name, MicSetupViewModel Setup)
{
    public override string ToString() => Name;
}

/// <summary>
/// Every microphone in the mixer that Mic setup can test, in the mixer's
/// order: XLR 1 and XLR 2 where the device has them, and each mono capture
/// input, including one on an additional interface. One list for the whole
/// UI; the window's picker shows it when it holds more than one.
/// </summary>
public sealed class MicSetupInputs : ViewModelBase
{
    public ObservableCollection<MicSetupInput> Items { get; } = [];

    /// <summary>At least one microphone can be tested.</summary>
    public bool Any => Items.Count > 0;

    /// <summary>More than one, so the window offers the choice.</summary>
    public bool Many => Items.Count > 1;

    /// <summary>The microphone Mic setup opens on from the mixer's header: XLR 1 when it is listed, else the first.</summary>
    public MicSetupInput? Primary => Items.FirstOrDefault(i => i.Setup.Channel == "xlr1") ?? Items.FirstOrDefault();

    /// <summary>Take the list a state push gives; returns true when it changed.</summary>
    internal bool Update(IReadOnlyList<MicSetupInput> inputs)
    {
        if (inputs.SequenceEqual(Items)) return false;
        Items.Clear();
        foreach (MicSetupInput input in inputs) Items.Add(input);
        Raise(nameof(Any)); Raise(nameof(Many)); Raise(nameof(Primary));
        return true;
    }
}

/// <summary>
/// One microphone's Mic setup. The daemon owns the session, the take and
/// the meters; this view sends the actions and shows what the daemon
/// reports: the Sound Check state, the meter before the chain, the take's
/// waveform and the loop's playhead. The interface's gain, phantom power,
/// low cut and ClipGuard are the main window's own controls for the input
/// this channel carries, and the EQ is the channel's own editor model, all
/// set from outside as the carrier changes; the insert chain is the one this
/// setup belongs to.
/// </summary>
public sealed class MicSetupViewModel : ViewModelBase
{
    private readonly DaemonClient _client;
    private bool _busy, _active, _detached;
    private int _connectionEpoch;
    private int _requestRevision;
    private string _mode = "idle";
    private double _seconds;
    private string? _error;

    /// <param name="chain">The channel's insert chain, which the Shape step lists and edits; null shows no chain.</param>
    public MicSetupViewModel(DaemonClient client, string channel, InsertsViewModel? chain = null)
    {
        _client = client;
        Channel = channel;
        Chain = chain;
        Steps = [new(this, MicSetupStep.Position, Localizer.Text("MicSetupStepPosition")),
            new(this, MicSetupStep.Level, Localizer.Text("MicSetupStepLevel")),
            new(this, MicSetupStep.Listen, Localizer.Text("MicSetupStepListen")),
            new(this, MicSetupStep.Shape, Localizer.Text("MicSetupStepShape"))];
        Presets = [.. MicSetupPresets.All.Select(p => new MicSetupPresetItem(this, p))];
    }

    /// <summary>The microphone channel this setup belongs to.</summary>
    public string Channel { get; }

    // --- the microphone picker ---

    private MicSetupInputs? _inputs;
    /// <summary>The microphones the window can switch between, or null outside the main window's model.</summary>
    public MicSetupInputs? Inputs
    {
        get => _inputs;
        internal set { if (Set(ref _inputs, value)) RaiseInputs(); }
    }

    /// <summary>The picker is shown: the mixer has more than one microphone to test.</summary>
    public bool ShowsPicker => _inputs is { Many: true };

    /// <summary>This microphone's entry in the picker.</summary>
    public MicSetupInput? Picked => _inputs?.Items.FirstOrDefault(i => ReferenceEquals(i.Setup, this));

    internal void RaiseInputs() { Raise(nameof(ShowsPicker)); Raise(nameof(Picked)); }

    /// <summary>
    /// The window moves to another microphone: a session running here stops
    /// first, as closing the window stops it, and a measurement ends, so
    /// nothing of this microphone carries on unseen. The next one opens on the
    /// step shown here.
    /// </summary>
    public async Task<bool> HandOverAsync(MicSetupViewModel next)
    {
        if (_detached || next._detached) return false;
        if (ReferenceEquals(next, this)) return true;
        CancelMeasures();
        int epoch = _connectionEpoch;
        if (_active || _busy)
        {
            string? error = await StopOnCloseAsync();
            if (epoch != _connectionEpoch || error is not null) return false;
        }
        if (_inputs is { } inputs && !inputs.Items.Any(i => ReferenceEquals(i.Setup, next))) return false;
        next.Step = Step;
        return true;
    }

    // --- steps ---

    public ObservableCollection<MicSetupStepItem> Steps { get; }

    private MicSetupStep _step = MicSetupStep.Position;
    /// <summary>The step shown. A setup starts on Position and keeps its last step while the UI runs.</summary>
    public MicSetupStep Step
    {
        get => _step;
        set
        {
            if (!Set(ref _step, value)) return;
            foreach (MicSetupStepItem item in Steps) item.Refresh();
            Raise(nameof(ShowsPosition)); Raise(nameof(ShowsLevel)); Raise(nameof(ShowsListen)); Raise(nameof(ShowsShape));
            // The editor button needs the catalogue to know which plugins have an editor of their own.
            if (value == MicSetupStep.Shape) Chain?.EnsurePluginsLoaded();
        }
    }
    public bool ShowsPosition => _step == MicSetupStep.Position;
    public bool ShowsLevel => _step == MicSetupStep.Level;
    public bool ShowsListen => _step == MicSetupStep.Listen;
    public bool ShowsShape => _step == MicSetupStep.Shape;

    // --- the session ---

    public bool Active => _active;
    public bool CanRecord => !_detached && !_busy && _unitNote is null && _mode != "recording";
    public bool CanLoop => !_detached && !_busy && _unitNote is null && _active && _seconds >= 0.1;
    public bool CanLive => !_detached && !_busy && _unitNote is null;
    public bool CanStop => !_detached && !_busy && (_active || Error is not null);
    public bool CanSwitch => !_detached && !_busy && _unitNote is null;

    private string? _unitNote;
    /// <summary>
    /// Why no session can start: the Wave XLR Pro builds the mixes its jacks
    /// play, and the daemon refuses Mic setup's checks then. Null otherwise.
    /// The Listen step says it under its buttons; Stop stays, so a session
    /// already running can still end.
    /// </summary>
    public string? UnitNote
    {
        get => _unitNote;
        internal set
        {
            if (!Set(ref _unitNote, value)) return;
            Raise(nameof(HasUnitNote));
            RaiseButtons();
        }
    }
    public bool HasUnitNote => _unitNote is not null;
    public string Status => _mode switch
    {
        "recording" => Localizer.Format("SoundCheckRecording", _seconds),
        "looping" => Localizer.Format("MicSetupLooping", _seconds),
        "live" when _active && _seconds >= 0.1 => Localizer.Format("MicSetupReady", _seconds),
        _ => Localizer.Text("MicSetupIdle"),
    };
    public string? Error { get => _error; private set => Set(ref _error, value); }

    private bool _raw;
    /// <summary>The session plays the signal past the chain.</summary>
    public bool Raw => _raw;
    /// <summary>The Processed half of the switch: choosing it sends processed.</summary>
    public bool ListenProcessed { get => !_raw; set { if (value && _raw) _ = SetRawAsync(false); else Raise(nameof(ListenProcessed)); } }
    /// <summary>The Raw half of the switch: choosing it sends raw.</summary>
    public bool ListenRaw { get => _raw; set { if (value && !_raw) _ = SetRawAsync(true); else Raise(nameof(ListenRaw)); } }

    private bool _hearMyself;
    /// <summary>Hear myself, as the daemon holds it; the check box sends the change.</summary>
    public bool HearMyself
    {
        get => _hearMyself;
        set { if (value != _hearMyself) _ = SetHearMyselfAsync(value); }
    }

    private bool _heard;
    /// <summary>A session runs and the microphone reaches no monitor output, so neither a take nor the live microphone is heard.</summary>
    public bool NotHeard => _active && !_heard;

    private double? _latencyMs;
    /// <summary>What hearing yourself through this computer adds, said in the window; empty while the daemon has no figure.</summary>
    public string LatencyText => _latencyMs is double ms
        ? Localizer.Format("MicSetupHearMyselfLatency", ms) : Localizer.Text("MicSetupHearMyselfLatencyUnknown");

    private bool _proDirect;
    /// <summary>This channel carries the Wave XLR Pro's first input, whose zero-latency path is off during a session.</summary>
    public bool ProDirect { get => _proDirect; internal set => Set(ref _proDirect, value); }

    // --- the take ---

    private int _take, _requestedTake;
    private IReadOnlyList<double>? _peaks;
    /// <summary>The take's waveform, or null before one is recorded.</summary>
    public IReadOnlyList<double>? Peaks { get => _peaks; private set { if (Set(ref _peaks, value)) Raise(nameof(HasTake)); } }
    public bool HasTake => _peaks is { Count: > 0 };

    private double _position = -1;
    /// <summary>Where the loop is across the take, 0 to 1, or -1 while it does not loop.</summary>
    public double Position { get => _position; private set => Set(ref _position, value); }

    // --- the level before the chain ---

    private double _inputLevel, _inputPeak;
    /// <summary>The microphone's RMS before the chain, on the meters' 0 to 1 scale.</summary>
    public double InputLevel { get => _inputLevel; private set => Set(ref _inputLevel, value); }
    /// <summary>The microphone's peak before the chain, on the same scale.</summary>
    public double InputPeak
    {
        get => _inputPeak;
        private set { if (Set(ref _inputPeak, value)) Raise(nameof(PeakText)); }
    }
    public string PeakText => _inputPeak <= 0 ? Localizer.Text("MicSetupPeakSilent") : Localizer.Format("MicSetupPeak", _inputPeak * 60 - 60);

    private bool _clipped;
    /// <summary>A sample reached the clip level since the light was last cleared.</summary>
    public bool Clipped { get => _clipped; private set { if (Set(ref _clipped, value)) Raise(nameof(ClipStatus)); } }
    public string ClipStatus => _clipped ? Localizer.Text("MicSetupClipLit") : Localizer.Text("MicSetupClipDark");

    public void ClearClip() => Clipped = false;

    // --- the interface's gain ---

    private UnitInputViewModel? _unit;
    /// <summary>The primary unit's input this channel carries, whose gain the Level step shows, or null.</summary>
    public UnitInputViewModel? Unit
    {
        get => _unit;
        internal set
        {
            UnitInputViewModel? old = _unit;
            if (!Set(ref _unit, value)) return;
            if (old is not null) old.PropertyChanged -= OnGainChanged;
            if (value is not null) value.PropertyChanged += OnGainChanged;
            CheckMeasuredGain();
            Raise(nameof(CarriesUnit)); Raise(nameof(HasGain)); Raise(nameof(NoGain)); RaiseCarrier();
        }
    }
    public bool CarriesUnit => _unit is not null;

    private WaveUnitInputViewModel? _extraUnit;
    /// <summary>An additional unit's input this channel carries, or null.</summary>
    public WaveUnitInputViewModel? ExtraUnit
    {
        get => _extraUnit;
        internal set
        {
            WaveUnitInputViewModel? old = _extraUnit;
            if (!Set(ref _extraUnit, value)) return;
            if (old is not null) old.PropertyChanged -= OnGainChanged;
            if (value is not null) value.PropertyChanged += OnGainChanged;
            CheckMeasuredGain();
            Raise(nameof(CarriesExtraUnit)); Raise(nameof(HasGain)); Raise(nameof(NoGain)); RaiseCarrier();
        }
    }
    public bool CarriesExtraUnit => _extraUnit is { HasGain: true };

    private ChannelViewModel? _captureInput;
    public ChannelViewModel? CaptureInput
    {
        get => _captureInput;
        private set
        {
            var old = _captureInput;
            if (!Set(ref _captureInput, value)) return;
            if (old is not null) old.PropertyChanged -= OnCaptureGainChanged;
            if (value is not null) value.PropertyChanged += OnCaptureGainChanged;
            Raise(nameof(HasGain)); Raise(nameof(NoGain));
        }
    }

    private void OnCaptureGainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChannelViewModel.CaptureGainDb)) CheckRoom();
        if (e.PropertyName != nameof(ChannelViewModel.ShowsCaptureGain)) return;
        Raise(nameof(HasGain)); Raise(nameof(NoGain));
        CheckRoom();
    }

    public bool HasGain => CarriesUnit || CarriesExtraUnit || _captureInput?.ShowsCaptureGain == true;
    public bool NoGain => !HasGain;

    // --- Position: the unit the microphone is on, tips for it, phantom power ---

    private MicSetupUnitKind _unitKind;
    private string _unitName = "";
    private int _unitInput = 1;

    /// <summary>What the microphone is, for the tips: a unit's capsule, on a unit's XLR jack, or on no unit OpenXLR drives.</summary>
    public MicSetupUnitKind UnitKind => _unitKind;

    /// <summary>
    /// The unit this channel's microphone is on, its name and the input; set
    /// after every state from the same carrier as the gain.
    /// </summary>
    internal void Place(MicSetupUnitKind kind, string? name, int input)
    {
        name ??= "";
        if (kind == _unitKind && name == _unitName && input == _unitInput) return;
        _unitKind = kind; _unitName = name; _unitInput = input;
        Raise(nameof(UnitKind)); Raise(nameof(PositionIntro)); Raise(nameof(PositionTips)); RaiseCarrier();
    }

    /// <summary>The Position step's first line: which unit the microphone is on, or that OpenXLR drives none.</summary>
    public string PositionIntro => _unitKind switch
    {
        MicSetupUnitKind.Capsule => Localizer.Format("MicSetupPositionCapsule", UnitLabel),
        MicSetupUnitKind.XlrJack => Localizer.Format("MicSetupPositionXlr", UnitLabel, _unitInput),
        _ => Localizer.Text("MicSetupPositionNone"),
    };

    private string UnitLabel => _unitName.Length > 0 ? _unitName : Localizer.Text("MicSetupYourInterface");

    /// <summary>
    /// Distance and angle tips for this microphone: first what its kind of
    /// unit asks for, then what holds for any speech microphone. General
    /// practice, with no distances in centimetres, since none were measured.
    /// </summary>
    public IReadOnlyList<string> PositionTips
    {
        get
        {
            var tips = new List<string>();
            switch (_unitKind)
            {
                case MicSetupUnitKind.Capsule:
                    tips.Add(Localizer.Text("MicSetupTipCapsuleFront"));
                    tips.Add(Localizer.Text("MicSetupTipCapsuleRoom"));
                    break;
                case MicSetupUnitKind.XlrJack:
                    tips.Add(Localizer.Text("MicSetupTipXlrAddress"));
                    tips.Add(Localizer.Text("MicSetupTipXlrDistance"));
                    break;
                default:
                    tips.Add(Localizer.Text("MicSetupTipOtherDevice"));
                    break;
            }
            tips.Add(Localizer.Text("MicSetupTipDistance"));
            tips.Add(Localizer.Text("MicSetupTipAngle"));
            tips.Add(Localizer.Text("MicSetupTipNoise"));
            return tips;
        }
    }

    /// <summary>The unit behind the microphone has phantom power OpenXLR switches.</summary>
    public bool HasPhantom => _unit is { CapPhantom: true } || _extraUnit is { HasPhantom: true };

    /// <summary>A built-in capsule needs no phantom power, so the step leaves it out; any other microphone may need it.</summary>
    public bool ShowsPhantom => _unitKind != MicSetupUnitKind.Capsule;

    /// <summary>Phantom power is said, but OpenXLR cannot switch it: set it on the microphone's own interface.</summary>
    public bool PhantomElsewhere => ShowsPhantom && !HasPhantom;

    // --- Shape: the EQ, low cut and ClipGuard, the chain and the presets ---

    /// <summary>The channel's insert chain, or null.</summary>
    public InsertsViewModel? Chain { get; }

    private MainViewModel? _software;
    /// <summary>
    /// The main window's model while this is XLR 1's setup, whose software
    /// low cut and ClipGuard sit after the EQ; null on every other channel.
    /// </summary>
    public MainViewModel? Software
    {
        get => _software;
        internal set { if (Set(ref _software, value)) RaiseCarrier(); }
    }

    /// <summary>The unit's own low cut or ClipGuard is offered for this microphone; they act before the take.</summary>
    public bool HasUnitFilters => _unit is { CapLowCut: true } or { CapClipGuard: true }
        || _extraUnit is { HasLowCut: true } or { HasClipGuard: true };

    /// <summary>XLR 1's software low cut or ClipGuard applies; they act after the EQ, in the loop.</summary>
    public bool HasSoftwareFilters => _software is { ShowSoftwareControls: true };

    /// <summary>This microphone has neither a low cut nor ClipGuard outside the EQ.</summary>
    public bool NoFilters => !HasUnitFilters && !HasSoftwareFilters;

    /// <summary>
    /// What the carrier offers changed: called after every state, since the
    /// capabilities behind these come with it.
    /// </summary>
    internal void RaiseCarrier()
    {
        Raise(nameof(HasPhantom)); Raise(nameof(ShowsPhantom)); Raise(nameof(PhantomElsewhere));
        Raise(nameof(HasUnitFilters)); Raise(nameof(HasSoftwareFilters)); Raise(nameof(NoFilters));
        Raise(nameof(CanAcceptGain)); Raise(nameof(SuggestBlocked));
    }

    // --- Suggest gain ---

    /// <summary>The clock a measurement runs on; tests replace it.</summary>
    internal TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary>The gain a measurement is judged against: the input's own control, its range and whether OpenXLR may write it now.</summary>
    private readonly record struct GainControl(object Owner, int GainDb, int MaxDb, bool Writable, bool Locked);

    private GainControl? CurrentGain => _unit is { } unit
            ? new GainControl(unit, unit.GainDb, unit.GainMaxDb, !unit.GainLocked, unit.GainLocked)
        : _extraUnit is { HasGain: true } extra
            ? new GainControl(extra, extra.GainDb, extra.GainMaxDb, extra.CanAdjustGain, extra.Unit.GainLocked)
        : null;

    private List<LevelReading>? _measure;
    private long _measureStarted;
    private GainControl? _measuredGain;
    private GainSuggestion? _suggestion;
    private string? _gainNote;
    private (ChannelBinding? Binding, UnitInputInfo? Unit, string? Source, int Pair, int? Port, bool Connected) _input;

    /// <summary>A source or unit changed, even when the same gain control and value are reused.</summary>
    internal void ObserveInput(ChannelViewModel? input)
    {
        CaptureInput = input?.CaptureSource is not null ? input : null;
        var next = (input?.Binding, input?.UnitInput, input?.CaptureSource, input?.CapturePair ?? 0,
            input?.CaptureMonoChannel, input?.CaptureSource is not null ? input.CaptureConnected : input?.BindingConnected == true);
        if (_input == next) return;
        _input = next;
        InvalidateGain();
        // A voice measured on the old input says nothing about the new one, nor does the room.
        _voice = null;
        InvalidateRoom();
    }

    private void OnGainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is nameof(UnitInputViewModel.GainDb) or nameof(UnitInputViewModel.GainMaxDb))
            CheckMeasuredGain();
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(UnitInputViewModel.GainLocked))
        {
            Raise(nameof(CanAcceptGain));
            Raise(nameof(SuggestBlocked));
        }
    }

    private void CheckMeasuredGain()
    {
        if (!SameGain(_measuredGain, CurrentGain)) InvalidateGain();
        CheckRoom();
    }

    private void InvalidateGain()
    {
        if (_measure is null && _suggestion is null) return;
        _measure = null;
        _suggestion = null;
        _measuredGain = null;
        _gainNote = Localizer.Text("MicSetupSuggestStale");
        RaiseSuggestion();
    }

    /// <summary>Suggest gain is listening to the microphone.</summary>
    public bool Measuring => _measure is not null;
    public bool CanSuggestGain => !_detached && _measure is null && _room is null;

    /// <summary>The last measurement's suggestion, until it is accepted, discarded or measured again.</summary>
    public GainSuggestion? Suggestion => _suggestion;

    /// <summary>The suggestion changes a gain OpenXLR may write now.</summary>
    public bool CanAcceptGain => _suggestion is { ChangesGain: true } && CurrentGain is { Writable: true };

    /// <summary>A suggestion or a note to clear.</summary>
    public bool CanDiscardGain => _suggestion is not null || _gainNote is not null;

    /// <summary>What the measurement read, or null before one.</summary>
    public string? SuggestMeasured => _suggestion is { Outcome: not GainOutcome.TooFewReadings } s
        ? Localizer.Format("MicSetupSuggestMeasured", s.SpeechDb, s.PeakDb) : null;

    /// <summary>The countdown while listening, then what to do with the gain, or a note after an accept.</summary>
    public string? SuggestAdvice
    {
        get
        {
            if (_measure is not null)
            {
                double left = Math.Max(0, (MicSetupLevels.MeasureTime - Time.GetElapsedTime(_measureStarted)).TotalSeconds);
                return Localizer.Format("MicSetupSuggestListening", Math.Ceiling(left));
            }
            if (_suggestion is not { } s) return _gainNote;
            string advice = s.Outcome switch
            {
                GainOutcome.TooFewReadings => Localizer.Text("MicSetupSuggestTooFew"),
                GainOutcome.NoVoice => Localizer.Text("MicSetupSuggestNoVoice"),
                GainOutcome.Keep when s.FromDb is int from => Localizer.Format("MicSetupSuggestKeep", from),
                GainOutcome.Keep => Localizer.Text("MicSetupSuggestKeepElsewhere"),
                GainOutcome.Elsewhere => Localizer.Format("MicSetupSuggestElsewhere", s.ChangeDb),
                GainOutcome.AtMaximum => Localizer.Format("MicSetupSuggestAtMaximum", s.ToDb ?? 0, s.ShortfallDb),
                GainOutcome.AtMinimum => Localizer.Format("MicSetupSuggestAtMinimum", s.ShortfallDb),
                _ => Localizer.Format("MicSetupSuggestChange", s.FromDb ?? 0, s.ToDb ?? 0),
            };
            if (s.ChangesGain && s.Outcome != GainOutcome.Change) advice += " " + Localizer.Format("MicSetupSuggestChange", s.FromDb ?? 0, s.ToDb ?? 0);
            return s.Clipped ? Localizer.Text("MicSetupSuggestClipped") + " " + advice : advice;
        }
    }

    /// <summary>Why a suggested change cannot be accepted now, or null.</summary>
    public string? SuggestBlocked => _suggestion is { ChangesGain: true } && CurrentGain is { Writable: false } gain
        ? gain.Locked ? Localizer.Text("MicSetupSuggestLocked") : Localizer.Text("MicSetupSuggestUnreachable") : null;

    /// <summary>
    /// Listen for <see cref="MicSetupLevels.MeasureTime"/> to the meter
    /// before the chain. Nothing is written to the unit; the suggestion waits
    /// for <see cref="AcceptGain"/>.
    /// </summary>
    public void StartGainMeasure()
    {
        if (!CanSuggestGain) return;
        _measure = [];
        _measureStarted = Time.GetTimestamp();
        _measuredGain = CurrentGain;
        _suggestion = null;
        _gainNote = null;
        RaiseSuggestion();
    }

    /// <summary>The window's clock while listening, to the voice or to the room: the countdown, and the end when no reading brings it.</summary>
    public void MeasureTick()
    {
        if (_measure is not null)
        {
            if (Time.GetElapsedTime(_measureStarted) >= MicSetupLevels.MeasureTime) FinishGainMeasure();
            else Raise(nameof(SuggestAdvice));
        }
        if (_room is { } room)
        {
            if (Time.GetElapsedTime(room.Started) >= MicSetupLevels.MeasureTime) FinishRoomMeasure();
            else Raise(nameof(RoomAdvice));
        }
    }

    private void FinishGainMeasure()
    {
        if (_measure is not { } readings) return;
        _measure = null;
        GainControl? gain = CurrentGain;
        if (!SameGain(_measuredGain, gain))
        {
            // The gain or the input it belongs to moved while listening.
            _gainNote = Localizer.Text("MicSetupSuggestStale");
            _suggestion = null;
        }
        else
        {
            _suggestion = GainSuggestion.From(readings, gain?.GainDb, gain?.MaxDb ?? 0);
            // The voice's level, for the room measurement to keep the expander under.
            if (_suggestion.Outcome is not (GainOutcome.TooFewReadings or GainOutcome.NoVoice))
                _voice = new VoiceLevel(gain?.Owner, gain?.GainDb, CaptureGain, _suggestion.SpeechDb);
        }
        RaiseSuggestion();
        RaiseRoom();
    }

    private static bool SameGain(GainControl? a, GainControl? b)
        => a is null ? b is null : b is { } other && ReferenceEquals(a.Value.Owner, other.Owner)
            && a.Value.GainDb == other.GainDb && a.Value.MaxDb == other.MaxDb;

    /// <summary>
    /// Write the suggested gain through the input's own control, the one the
    /// Level step's slider moves, so the gain lock and the unit's range
    /// apply. A gain that moved since the measurement is not overwritten.
    /// </summary>
    public void AcceptGain()
    {
        if (_suggestion is not { ChangesGain: true, ToDb: int to } s || CurrentGain is not { Writable: true } gain) return;
        _suggestion = null;
        if (!SameGain(_measuredGain, gain) || gain.GainDb != s.FromDb) _gainNote = Localizer.Text("MicSetupSuggestStale");
        else
        {
            if (gain.Owner is UnitInputViewModel unit) unit.GainDb = to;
            else if (gain.Owner is WaveUnitInputViewModel extra) extra.GainDb = to;
            _gainNote = Localizer.Format("MicSetupSuggestSet", to);
        }
        RaiseSuggestion();
    }

    /// <summary>The window closed while listening: the measurement is dropped, since its meter stops with the window.</summary>
    public void CancelGainMeasure()
    {
        if (_measure is null) return;
        _measure = null;
        RaiseSuggestion();
    }

    /// <summary>Forget the suggestion; the unit's gain stays as it is.</summary>
    public void DiscardGain()
    {
        _suggestion = null;
        _gainNote = null;
        RaiseSuggestion();
    }

    /// <summary>The window closed or moved to another microphone: both measurements are dropped, since their meters stop with it.</summary>
    public void CancelMeasures()
    {
        CancelGainMeasure();
        CancelRoomMeasure();
    }

    private void RaiseSuggestion()
    {
        Raise(nameof(Measuring)); Raise(nameof(CanSuggestGain)); Raise(nameof(CanMeasureRoom)); Raise(nameof(Suggestion)); Raise(nameof(CanAcceptGain));
        Raise(nameof(CanDiscardGain)); Raise(nameof(SuggestMeasured)); Raise(nameof(SuggestAdvice)); Raise(nameof(SuggestBlocked));
    }

    private MicEqViewModel? _eq;
    private MicEq? _beforePreset;
    private string? _presetStatus;

    /// <summary>The channel's EQ editor model, the one the EQ window edits; null while the daemon reports no EQ for it.</summary>
    public MicEqViewModel? Eq
    {
        get => _eq;
        internal set
        {
            MicEqViewModel? old = _eq;
            if (!Set(ref _eq, value)) return;
            if (old is not null) old.Changed -= RefreshActivePreset;
            if (value is not null) value.Changed += RefreshActivePreset;
            // An undo belongs to the EQ it was taken from.
            _beforePreset = null;
            _presetStatus = null;
            Raise(nameof(HasEq)); Raise(nameof(NoEq)); RaisePresets();
        }
    }
    public bool HasEq => _eq is not null;
    public bool NoEq => _eq is null;

    private MicNoiseViewModel? _noise;
    /// <summary>The channel's noise removal; null while the daemon reports none for it.</summary>
    public MicNoiseViewModel? Noise
    {
        get => _noise;
        internal set
        {
            MicNoiseViewModel? old = _noise;
            if (!Set(ref _noise, value)) return;
            // Noise removal runs before the expander, so a change to it changes what a room measurement read.
            if (old is not null) old.PropertyChanged -= OnRoomInputChanged;
            if (value is not null) value.PropertyChanged += OnRoomInputChanged;
            Raise(nameof(HasNoise));
            Raise(nameof(NoNoise));
            CheckRoom();
        }
    }
    public bool HasNoise => _noise is not null;
    public bool NoNoise => _noise is null;

    // --- The expander, and Measure room noise for its threshold ---
    //
    // The measurement sits on Level, beside Suggest gain: it reads the same
    // meter the step shows, so the room is seen on the big bar while the user
    // stays quiet, the gain it depends on is set there, and the voice level
    // Suggest gain measures is what the threshold is kept under. Shape, where
    // the window would run past a 1440-pixel screen with it, keeps the
    // expander's switch and threshold in signal order after noise removal.

    private ChannelDynamicsViewModel? _dynamics;
    /// <summary>
    /// The channel's compressor and expander, the ones their windows edit;
    /// null while the daemon reports none. Level and Shape show the expander's
    /// switch and threshold, and the room measurement sets them through it, so a
    /// threshold goes out as the window's own <c>setExpander</c> does, to
    /// software or to the unit, whichever runs it.
    /// </summary>
    public ChannelDynamicsViewModel? Dynamics
    {
        get => _dynamics;
        internal set
        {
            ChannelDynamicsViewModel? old = _dynamics;
            if (!Set(ref _dynamics, value)) return;
            if (old is not null)
            {
                old.Expander.PropertyChanged -= OnRoomInputChanged;
                old.Compressor.PropertyChanged -= OnRoomInputChanged;
                if (old.Compressor.Makeup is { } makeup) makeup.PropertyChanged -= OnRoomInputChanged;
            }
            if (value is not null)
            {
                value.Expander.PropertyChanged += OnRoomInputChanged;
                value.Compressor.PropertyChanged += OnRoomInputChanged;
                if (value.Compressor.Makeup is { } makeup) makeup.PropertyChanged += OnRoomInputChanged;
            }
            // A measurement belongs to the processor it was made for.
            InvalidateRoom();
            Raise(nameof(Expander)); Raise(nameof(HasExpander)); Raise(nameof(NoExpander));
            RaiseRoom();
        }
    }

    /// <summary>The channel's expander, or null.</summary>
    public MicDynamicsViewModel? Expander => _dynamics?.Expander;
    public bool HasExpander => _dynamics is not null;
    public bool NoExpander => _dynamics is null;

    /// <summary>One room measurement under way: when it began, and the two meters' peak readings in dBFS.</summary>
    private sealed class RoomMeasure(long started, bool stageCounts)
    {
        public long Started { get; } = started;
        /// <summary>The expander's own input level reads where it listens; false while a unit runs it and reports no level.</summary>
        public bool StageCounts { get; } = stageCounts;
        /// <summary>The <c>exp:</c> input level: the expander's input, after noise removal, or the unit's own expander key.</summary>
        public List<double> Stage { get; } = [];
        /// <summary>The held peak of the meter before the chain: the microphone as it reaches this computer.</summary>
        public List<double> Raw { get; } = [];
    }

    /// <summary>
    /// What a room measurement depends on: the gain and the input it belongs
    /// to, a capture input's own gain, noise removal ahead of the expander,
    /// where the expander runs, and the unit compressor's make-up gain the
    /// meter is corrected by. Any of them changing makes the room measure again.
    /// </summary>
    private readonly record struct RoomPoint(object? GainOwner, int? GainDb, double? CaptureGainDb, bool Noise, string? Engine,
        double Strength, bool OnDevice, double MakeupDb);

    /// <summary>The voice Suggest gain measured, at the gain it was measured at.</summary>
    private readonly record struct VoiceLevel(object? GainOwner, int? GainDb, double? CaptureGainDb, double SpeechDb);

    private RoomMeasure? _room;
    private RoomPoint? _roomPoint;
    private RoomSuggestion? _roomSuggestion;
    private string? _roomNote;
    private bool _roomBeforeNoise, _sessionAnywhere;
    private VoiceLevel? _voice;

    private double? CaptureGain => _captureInput is { ShowsCaptureGain: true } capture ? capture.CaptureGainDb : null;

    /// <summary>
    /// The unit compressor's make-up gain that the meter before the chain
    /// reads and the expander does not hear, in dB: on a unit that runs the
    /// compressor and the expander, the meter is after both, and at the
    /// room's level the compressor is under its threshold and only adds its
    /// make-up gain. 0 while software runs them, since the meter is then
    /// ahead of the whole stage.
    /// </summary>
    private double UnitMakeupDb => _dynamics is { Expander.OnDevice: true, Compressor: { Enabled: true, Makeup: { } makeup } } ? makeup.Value : 0;

    private RoomPoint CurrentRoomPoint
    {
        get
        {
            GainControl? gain = CurrentGain;
            return new(gain?.Owner, gain?.GainDb, CaptureGain, _noise?.Enabled ?? false, _noise?.Engine, _noise?.Strength ?? 0,
                Expander?.OnDevice ?? false, UnitMakeupDb);
        }
    }

    /// <summary>
    /// The voice's level now, in dBFS on the meter before the chain: what
    /// Suggest gain measured, moved by any gain change since, on the same
    /// input. Null before a voice was measured, or once the gain belongs to
    /// another input.
    /// </summary>
    private double? VoiceDb
    {
        get
        {
            if (_voice is not { } voice) return null;
            GainControl? gain = CurrentGain;
            if (!ReferenceEquals(voice.GainOwner, gain?.Owner) || voice.CaptureGainDb.HasValue != CaptureGain.HasValue) return null;
            return voice.SpeechDb + ((gain?.GainDb ?? 0) - (voice.GainDb ?? 0)) + ((CaptureGain ?? 0) - (voice.CaptureGainDb ?? 0));
        }
    }

    private void OnRoomInputChanged(object? sender, PropertyChangedEventArgs e)
    {
        CheckRoom();
        if (sender is MicDynamicsViewModel && e.PropertyName is null or nameof(MicDynamicsViewModel.Enabled)
                or nameof(MicDynamicsViewModel.OnDevice) or nameof(MicDynamicsViewModel.UnitReportsLevels) or nameof(MicDynamicsViewModel.Available))
            RaiseRoom();
    }

    /// <summary>A measurement, or the threshold applied from one, no longer matches what the expander hears: say so.</summary>
    private void CheckRoom()
    {
        if (_roomPoint is { } point && point != CurrentRoomPoint) InvalidateRoom();
    }

    private void InvalidateRoom()
    {
        if (_room is null && _roomPoint is null && _roomSuggestion is null) return;
        _room = null;
        _roomPoint = null;
        _roomSuggestion = null;
        _roomNote = Localizer.Text("MicSetupRoomStale");
        RaiseRoom();
    }

    /// <summary>
    /// The meter before the chain reads after the unit's own expander while
    /// the unit runs it and reports no level of its own, so with that
    /// expander on it would read the room already turned down.
    /// </summary>
    private bool RoomGated => Expander is { OnDevice: true, UnitReportsLevels: false, Enabled: true };

    /// <summary>Why the room cannot be measured now, or null.</summary>
    public string? RoomBlocked => _detached || Expander is not { } expander ? null
        : _sessionAnywhere ? Localizer.Text("MicSetupRoomSession")
        : RoomGated ? Localizer.Format("MicSetupRoomGated", expander.UnitName)
        : null;

    /// <summary>Measure room noise is listening.</summary>
    public bool MeasuringRoom => _room is not null;

    public bool CanMeasureRoom => !_detached && _room is null && _measure is null && Expander is { Available: true } && RoomBlocked is null;

    /// <summary>The last room measurement's suggestion, until it is applied, discarded or out of date.</summary>
    public RoomSuggestion? RoomSuggestion => _roomSuggestion;

    public bool CanApplyRoom => !_detached && _roomSuggestion is { HasThreshold: true } && Expander is { Available: true };

    public bool CanDiscardRoom => _roomSuggestion is not null || _roomNote is not null;

    /// <summary>What the room measurement read, or null before one.</summary>
    public string? RoomMeasured => _roomSuggestion switch
    {
        // Talk is no room's noise, so its level is not said as one.
        null or { Outcome: RoomOutcome.TooFewReadings or RoomOutcome.NotHeard or RoomOutcome.Talking } => null,
        { AtFloor: true } s => Localizer.Format("MicSetupRoomMeasuredFloor", s.NoiseDb),
        var s when _roomBeforeNoise => Localizer.Format("MicSetupRoomMeasuredRaw", s.NoiseDb),
        var s => Localizer.Format("MicSetupRoomMeasured", s.NoiseDb),
    };

    /// <summary>The countdown while listening, then the threshold and what it means, or a note.</summary>
    public string? RoomAdvice
    {
        get
        {
            if (_room is { } room)
            {
                double left = Math.Max(0, (MicSetupLevels.MeasureTime - Time.GetElapsedTime(room.Started)).TotalSeconds);
                return Localizer.Format("MicSetupRoomListening", Math.Ceiling(left));
            }
            if (_roomSuggestion is not { } s) return _roomNote;
            string advice = s.Outcome switch
            {
                RoomOutcome.TooFewReadings => Localizer.Text("MicSetupSuggestTooFew"),
                RoomOutcome.NotHeard => s.AtFloor ? Localizer.Text("MicSetupRoomFloor") : Localizer.Text("MicSetupRoomNotHeard"),
                RoomOutcome.Talking => Localizer.Text("MicSetupRoomTalking"),
                RoomOutcome.QuietRoom => Localizer.Format("MicSetupRoomQuiet", s.ThresholdDb ?? 0),
                RoomOutcome.LoudRoom => Localizer.Format("MicSetupRoomLoud", s.ThresholdDb ?? 0),
                RoomOutcome.VoiceTooQuiet => Localizer.Text("MicSetupRoomVoiceTooQuiet"),
                _ => Localizer.Format("MicSetupRoomSet", s.ThresholdDb ?? 0),
            };
            return s.HasThreshold && s.SpeechDb is null ? advice + " " + Localizer.Text("MicSetupRoomNoVoice") : advice;
        }
    }

    /// <summary>
    /// Listen to the room for <see cref="MicSetupLevels.MeasureTime"/> while
    /// the user stays quiet. Nothing is written; the threshold waits for
    /// <see cref="ApplyRoom"/>.
    /// </summary>
    /// <remarks>
    /// Where it reads. The microphone stage runs noise removal, the expander,
    /// the EQ, the compressor and the voice changer in that order
    /// (native/eq.c), after the interface's gain, and the expander judges
    /// each sample's peak. The meter before the chain (native/level.c, linked
    /// beside the route from the channel's capture port, Mixer.WatchInputMeter
    /// in src/OpenXLR.Core/Mixing/InputMeters.cs) reads after the gain and
    /// before noise removal, so it is the expander's input only while noise
    /// removal is off. The stage's own expander meter, <c>exp:</c> with the
    /// channel (MicDynamicsRoutes.cs, AddDynamicsLevels), is the expander's
    /// input itself, after noise removal, and the daemon sends it while a
    /// client watches the dynamics and the stage runs, which it does whenever
    /// noise removal is on. So the measurement watches both and judges the
    /// expander's meter when it came, and the meter before the chain
    /// otherwise, which is then the same point. Noise removal stays as it is
    /// set: the expander hears the cleaned room, so that is the room it is
    /// set against, and changing noise removal afterwards asks for another
    /// measurement. While a unit runs the expander, the unit's own key level
    /// stands in where the unit reports one (a Wave XLR MK.2 from firmware
    /// 1.0.15); elsewhere the meter before the chain reads after the unit's
    /// expander and compressor, so the expander has to be off and the
    /// compressor's make-up gain is taken back out.
    /// </remarks>
    public void StartRoomMeasure()
    {
        if (!CanMeasureRoom || Expander is not { } expander) return;
        bool stageCounts = !expander.OnDevice || expander.UnitReportsLevels;
        _room = new RoomMeasure(Time.GetTimestamp(), stageCounts);
        _roomPoint = CurrentRoomPoint;
        _roomSuggestion = null;
        _roomNote = null;
        if (stageCounts) _ = _client.WatchDynamicsAsync(Channel);
        _ = KeepMeterAsync();
        RaiseRoom();
    }

    /// <summary>The meters' <c>exp:</c> entry for this channel, <c>[input, reduction, output]</c> in dB, while the room is measured.</summary>
    public void ApplyExpanderLevel(JsonArray levels)
    {
        if (_detached || _room is not { StageCounts: true } room || levels.Count < 1 || Number(levels[0]) is not double input) return;
        room.Stage.Add(Math.Clamp(input, -120, 24));
        MeasureTick();
    }

    private void FinishRoomMeasure()
    {
        if (_room is not { } room || Expander is not { } expander) return;
        _room = null;
        bool stage = room.StageCounts && room.Stage.Count >= MicSetupLevels.MinReadings;
        if (_roomPoint != CurrentRoomPoint || !stage && expander is { OnDevice: true, Enabled: true })
        {
            // What the expander hears moved while listening, or the unit's
            // expander came on in front of the only meter that reads.
            _roomPoint = null;
            _roomSuggestion = null;
            _roomNote = Localizer.Text("MicSetupRoomStale");
            RaiseRoom();
            return;
        }
        // The voice was read on the meter before the chain, so on a unit
        // running the compressor it too carries the make-up gain.
        double makeup = UnitMakeupDb, correction = stage ? 0 : -makeup;
        IReadOnlyList<double> readings = stage ? room.Stage : [.. room.Raw.Select(r => r + correction)];
        // The expander's meter reaches down to -120 dBFS; the meter before the chain stops at -60.
        double floor = stage ? -120 : MicSetupLevels.MeterFloorDb + correction;
        _roomBeforeNoise = !stage && !expander.OnDevice && _noise is { Enabled: true };
        _roomSuggestion = RoomSuggestion.From(readings, floor, VoiceDb - makeup, expander.Threshold.Minimum, expander.Threshold.Maximum);
        RaiseRoom();
    }

    /// <summary>
    /// Switch the expander on at the suggested threshold through its own
    /// model, as its window would: the threshold goes out first, then the
    /// switch, each as one <c>setExpander</c>, to software or the unit.
    /// </summary>
    public void ApplyRoom()
    {
        if (!CanApplyRoom || _roomSuggestion is not { ThresholdDb: int threshold } || Expander is not { } expander) return;
        _roomSuggestion = null;
        if (_roomPoint != CurrentRoomPoint)
        {
            _roomPoint = null;
            _roomNote = Localizer.Text("MicSetupRoomStale");
        }
        else
        {
            expander.Threshold.SendNow(threshold);
            expander.Enabled = true;
            // The point stays, so a gain change afterwards says the threshold is out of date.
            _roomNote = Localizer.Format("MicSetupRoomApplied", threshold);
        }
        RaiseRoom();
    }

    /// <summary>Forget the room measurement; the expander stays as it is.</summary>
    public void DiscardRoom()
    {
        _roomPoint = null;
        _roomSuggestion = null;
        _roomNote = null;
        RaiseRoom();
    }

    /// <summary>The window closed while listening to the room: the measurement is dropped.</summary>
    public void CancelRoomMeasure()
    {
        if (_room is null) return;
        _room = null;
        _roomPoint = null;
        RaiseRoom();
    }

    private void RaiseRoom()
    {
        Raise(nameof(MeasuringRoom)); Raise(nameof(CanMeasureRoom)); Raise(nameof(CanSuggestGain)); Raise(nameof(RoomSuggestion));
        Raise(nameof(CanApplyRoom)); Raise(nameof(CanDiscardRoom)); Raise(nameof(RoomMeasured)); Raise(nameof(RoomAdvice));
        Raise(nameof(RoomBlocked));
    }

    /// <summary>The starting presets, in order.</summary>
    public IReadOnlyList<MicSetupPresetItem> Presets { get; }

    private MicSetupPreset? _activePreset;
    /// <summary>
    /// The preset the EQ holds now, whoever put it there, or null. It follows
    /// the EQ as shown: a band edit, the switch, an undo or another client's
    /// change that leaves the preset's values clears it.
    /// </summary>
    public MicSetupPreset? ActivePreset => _activePreset;

    private void RefreshActivePreset()
    {
        MicSetupPreset? active = _eq is { } eq ? MicSetupPresets.All.FirstOrDefault(p => p.Holds(eq.Current, eq.Limits)) : null;
        if (ReferenceEquals(active, _activePreset)) return;
        _activePreset = active;
        Raise(nameof(ActivePreset));
        foreach (MicSetupPresetItem item in Presets) item.Refresh();
    }

    public bool CanApplyPreset => _eq is not null;

    /// <summary>A preset was applied and its EQ can be put back.</summary>
    public bool CanUndoPreset => _eq is not null && _beforePreset is not null;

    /// <summary>What the last preset press did, or how the presets work before one.</summary>
    public string PresetStatus => _presetStatus ?? Localizer.Text("MicSetupPresetHint");

    /// <summary>
    /// Put a preset on the EQ as one <c>setEq</c>, so it is heard at once,
    /// on the live microphone or the looped take, and keep the EQ it
    /// replaces for one undo. Edits made after it are not kept by the undo.
    /// </summary>
    public void ApplyPreset(MicSetupPreset preset)
    {
        if (_eq is not { } eq) return;
        _beforePreset = eq.Current;
        eq.Replace(preset.For(eq.Limits));
        _presetStatus = Localizer.Format("MicSetupPresetApplied", preset.Name);
        RaisePresets();
    }

    /// <summary>
    /// A preset's button was pressed. The preset the EQ already holds sends
    /// nothing and keeps the undo, and every button is told again, since a
    /// press flips a toggle that only the EQ may light.
    /// </summary>
    public void ChoosePreset(MicSetupPresetItem item)
    {
        if (!item.IsActive) ApplyPreset(item.Preset);
        foreach (MicSetupPresetItem preset in Presets) preset.Refresh();
    }

    /// <summary>Put back the EQ the last preset replaced, again as one <c>setEq</c>.</summary>
    public void UndoPreset()
    {
        if (_eq is not { } eq || _beforePreset is not { } before) return;
        _beforePreset = null;
        eq.Replace(before);
        _presetStatus = Localizer.Text("MicSetupPresetUndone");
        RaisePresets();
    }

    private void RaisePresets()
    {
        Raise(nameof(CanApplyPreset)); Raise(nameof(CanUndoPreset)); Raise(nameof(PresetStatus));
        RefreshActivePreset();
    }

    /// <summary>The daemon's <c>mixer.soundCheck</c>; null or another channel's session reads as idle.</summary>
    public void Apply(JsonNode? state)
    {
        if (_detached) return;
        // One session runs at a time, on any microphone; while one runs, the
        // room is not left alone: a take or a voice plays in the monitors.
        bool session = (state?["mode"]?.GetValue<string>() ?? "idle") != "idle";
        if (session != _sessionAnywhere)
        {
            _sessionAnywhere = session;
            if (session && _room is not null)
            {
                _room = null;
                _roomPoint = null;
                _roomNote = Localizer.Text("MicSetupRoomInterrupted");
            }
            RaiseRoom();
        }
        bool own = state?["channel"]?.GetValue<string>() == Channel;
        _mode = own ? state?["mode"]?.GetValue<string>() ?? "idle" : "idle";
        _active = own && _mode != "idle";
        _seconds = _active ? state?["seconds"]?.GetValue<double>() ?? 0 : 0;
        Error = own ? state?["error"]?.GetValue<string>() : null;
        _raw = _active && (state?["raw"]?.GetValue<bool>() ?? false);
        _hearMyself = _active && (state?["hearMyself"]?.GetValue<bool>() ?? false);
        _heard = _active && (state?["heard"]?.GetValue<bool>() ?? false);
        _latencyMs = _active ? state?["monitorLatencyMs"]?.GetValue<double>() : null;
        // The waveform arrives once, when the daemon finishes the take; a
        // state that names a take this view never received asks for it.
        int take = _active ? state?["take"]?.GetValue<int>() ?? 0 : 0;
        if (take == 0) { _take = _requestedTake = 0; Peaks = null; }
        else if (take > Math.Max(_take, _requestedTake))
        {
            _requestedTake = take;
            _ = _client.RequestSoundCheckTakeAsync();
        }
        if (_mode != "looping") Position = -1;
        Raise(nameof(Active)); Raise(nameof(Status)); Raise(nameof(Raw)); Raise(nameof(ListenProcessed)); Raise(nameof(ListenRaw));
        Raise(nameof(HearMyself)); Raise(nameof(NotHeard)); Raise(nameof(LatencyText));
        RaiseButtons();
    }

    /// <summary>A <c>soundCheckTake</c> message: this channel's take replaces the one shown.</summary>
    public void ApplyTake(JsonNode? message)
    {
        if (message?["channel"]?.GetValue<string>() != Channel || !_active) return;
        int take = message["take"]?.GetValue<int>() ?? 0;
        if (take <= _take || take < _requestedTake || message["peaks"] is not JsonArray { Count: > 0 } peaks) return;
        _take = take;
        Peaks = [.. peaks.Select(p => Number(p) is double d ? Math.Clamp(d, 0, 1) : 0)];
    }

    /// <summary>The meters' <c>in:</c> entry for this channel: rms, peak and the clip flag.</summary>
    public void ApplyInputLevel(JsonArray level)
    {
        if (_detached) return;
        double Read(int i) => level.Count > i && Number(level[i]) is double d ? Math.Clamp(d, 0, 1) : 0;
        InputLevel = Read(0);
        InputPeak = Read(1);
        if (Read(2) >= 1) Clipped = true;
        if (_measure is { } readings)
        {
            readings.Add(new LevelReading(MicSetupLevels.FromMeter(Read(0)), MicSetupLevels.FromMeter(Read(1)), Read(2) >= 1));
            MeasureTick();
        }
        if (_room is { } room)
        {
            room.Raw.Add(MicSetupLevels.FromMeter(Read(1)));
            MeasureTick();
        }
    }

    /// <summary>The meters' <c>take:</c> entry for this channel, while the take loops.</summary>
    public void ApplyPosition(JsonArray position)
    {
        if (_mode == "looping" && position.Count > 0 && Number(position[0]) is double at)
            Position = Math.Clamp(at, 0, 1);
    }

    /// <summary>A finite number in a JSON value, written as an integer or not; null for anything else.</summary>
    private static double? Number(JsonNode? node)
        => node is JsonValue v && (v.TryGetValue(out double d) || v.TryGetValue(out int i) && (d = i) == i) && double.IsFinite(d) ? d : null;

    /// <summary>
    /// The connection dropped: a reply still on its way belongs to the old
    /// connection and must not change what this one shows.
    /// </summary>
    public void Reset()
    {
        _connectionEpoch++;
        _busy = false;
        Apply(null);
        InputLevel = InputPeak = 0;
        Clipped = false;
        // A measurement or a suggestion belongs to the connection it was made on.
        _measure = null;
        _measuredGain = null;
        _suggestion = null;
        _gainNote = null;
        _voice = null;
        RaiseSuggestion();
        _room = null;
        _roomPoint = null;
        _roomSuggestion = null;
        _roomNote = null;
        RaiseRoom();
        // The daemon may hold another EQ by the time it is back.
        _beforePreset = null;
        _presetStatus = null;
        RaisePresets();
    }

    /// <summary>The input left the mixer; release subscriptions to controls it no longer carries.</summary>
    internal void Detach()
    {
        if (_detached) return;
        Reset();
        _detached = true;
        Unit = null;
        ExtraUnit = null;
        CaptureInput = null;
        Eq = null;
        Noise = null;
        Dynamics = null;
        Software = null;
        Inputs = null;
        RaiseButtons();
        RaiseSuggestion();
    }

    private void RaiseButtons()
    {
        Raise(nameof(CanRecord)); Raise(nameof(CanLoop)); Raise(nameof(CanLive)); Raise(nameof(CanStop)); Raise(nameof(CanSwitch));
    }

    /// <summary>One request owns the controls and error. Stop may queue behind it and take ownership.</summary>
    private async Task<string?> SendAsync(Func<Task<string?>> request, bool supersede = false)
    {
        if (_detached || _busy && !supersede) return null;
        int epoch = _connectionEpoch;
        int revision = ++_requestRevision;
        _busy = true; RaiseButtons();
        try
        {
            string? error = await request();
            if (epoch == _connectionEpoch && revision == _requestRevision) Error = error;
            return error;
        }
        finally
        {
            if (epoch == _connectionEpoch && revision == _requestRevision) { _busy = false; RaiseButtons(); }
            // A refused switch snaps back to what the daemon holds.
            Raise(nameof(ListenProcessed)); Raise(nameof(ListenRaw)); Raise(nameof(HearMyself));
        }
    }

    public Task RunAsync(string action) => SendAsync(() => _client.SoundCheckAsync(Channel, action));

    public Task SetRawAsync(bool raw) => SendAsync(() => _client.SetSoundCheckRawAsync(Channel, raw));

    public Task SetHearMyselfAsync(bool on) => SendAsync(() => _client.SetSoundCheckHearMyselfAsync(Channel, on));

    /// <summary>Keep the meter before the chain running while the window shows it.</summary>
    public Task KeepMeterAsync() => _detached ? Task.CompletedTask : _client.WatchInputMeterAsync(Channel);


    /// <summary>
    /// Closing can follow a pending record command; the stop is sent behind
    /// it rather than dropped because the buttons are disabled for a moment.
    /// </summary>
    public Task<string?> StopOnCloseAsync()
        => SendAsync(() => _client.SoundCheckAsync(Channel, "stop"), supersede: true);
}
