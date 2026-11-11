using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using OpenXLR.UI.Localization;

namespace OpenXLR.UI;

/// <summary>The commands the compressor and the expander send. The daemon client sends them; a test records them.</summary>
public interface IDynamicsCommands
{
    /// <summary>
    /// One field of a microphone's compressor or expander: <paramref name="processor"/>
    /// is <see cref="MicDynamicsViewModel.CompressorKind"/> or
    /// <see cref="MicDynamicsViewModel.ExpanderKind"/>, the other fields keep their values.
    /// </summary>
    Task SetDynamicsAsync(string processor, string channel, string field, object value);

    /// <summary>Keep the microphone's compressor and expander levels coming for another lease.</summary>
    Task WatchDynamicsAsync(string channel);

    /// <summary>Keep the microphone's meter before the chain running for another lease.</summary>
    Task WatchInputMeterAsync(string channel);

    /// <summary>Where the microphone's compressor and expander run: <c>device</c> or <c>software</c>.</summary>
    Task SetDynamicsProcessingAsync(string channel, string value);
}

/// <summary>Where a compressor's or expander's window takes its levels from.</summary>
public enum DynamicsLevelSource
{
    /// <summary>No level has come lately.</summary>
    None,
    /// <summary>The stage's own meters: the processor's input, its gain reduction and its output.</summary>
    Stage,
    /// <summary>The meter before the chain, while the stage does not run: the raw microphone, no reduction, no output.</summary>
    Raw,
}

/// <summary>
/// The input levels a processor's window draws as its history: the newest
/// last, at most <see cref="Capacity"/> of them, one per meter push.
/// </summary>
public sealed class DynamicsHistory
{
    /// <summary>About twelve seconds at the daemon's 15 meter pushes a second.</summary>
    public const int Capacity = 180;

    private readonly double[] _values = new double[Capacity];
    private int _next, _count;

    public int Count => _count;

    /// <summary>The level <paramref name="index"/> pushes after the oldest one kept, in dBFS.</summary>
    public double this[int index] => _values[(_next - _count + index + Capacity) % Capacity];

    /// <summary>Raised after a level is added or the history is cleared.</summary>
    public event Action? Changed;

    public void Add(double db)
    {
        _values[_next] = db;
        _next = (_next + 1) % Capacity;
        if (_count < Capacity) _count++;
        Changed?.Invoke();
    }

    public void Clear()
    {
        _next = _count = 0;
        Changed?.Invoke();
    }
}

/// <summary>
/// One number of a compressor or an expander: its slider, its value text,
/// and the field it sends. A report that arrives while an edit is still on
/// its way does not move the slider back. While the unit runs the processor
/// the slider keeps to the unit's limits and step, and a number the unit
/// does not have (the knee, the hold) is not shown.
/// </summary>
public sealed class DynamicsParameterViewModel : ViewModelBase
{
    private readonly MicDynamicsViewModel _owner;
    private double _value;

    internal DynamicsParameterViewModel(MicDynamicsViewModel owner, DynamicsRange range, DynamicsRange? unitRange, string label, string tip)
    {
        _owner = owner;
        SoftwareRange = range;
        UnitRange = unitRange;
        Label = label;
        Tip = tip;
        _value = range.Default;
    }

    /// <summary>The range the software stage takes.</summary>
    public DynamicsRange SoftwareRange { get; }

    /// <summary>The range a unit running the processor takes (<see cref="UnitDynamics"/>); null for a number the unit does not have.</summary>
    public DynamicsRange? UnitRange { get; }

    /// <summary>The range the slider offers now.</summary>
    public DynamicsRange Range => _owner.OnDevice && UnitRange is { } unit ? unit : SoftwareRange;

    /// <summary>Shown: software runs the processor, or the unit has this number.</summary>
    public bool Shown => !_owner.OnDevice || UnitRange is not null;

    /// <summary>The command's field: <c>threshold</c>, <c>ratio</c> and so on.</summary>
    public string Field => SoftwareRange.Name;
    public string Label { get; }
    public string Tip { get; }
    public double Minimum => Range.Minimum;
    public double Maximum => Range.Maximum;
    public double Step => Range.Step;

    /// <summary>Where the processor runs changed: the slider's range, step and visibility follow.</summary>
    internal void RangeChanged()
    {
        Raise(nameof(Range));
        Raise(nameof(Minimum));
        Raise(nameof(Maximum));
        Raise(nameof(Step));
        Raise(nameof(Shown));
        Raise(nameof(Text));
    }

    private string Key => $"{_owner.Kind}:{_owner.Channel}:{Field}";

    public double Value
    {
        get => _value;
        set
        {
            if (_owner.Detached || !double.IsFinite(value)) return;
            double snapped = Math.Clamp(Range.Snap(value), Range.Minimum, Range.Maximum);
            if (!Set(ref _value, snapped)) return;
            SliderSync.Touch(Key);
            SliderSync.Send(Key, () => _ = _owner.Commands.SetDynamicsAsync(_owner.Kind, _owner.Channel, Field, snapped));
            Raise(nameof(Text));
            _owner.ParameterChanged(this);
        }
    }

    /// <summary>The value with its unit: "-18 dB", "3:1", "5 ms".</summary>
    public string Text => Format(Range, _value);

    internal static string Format(DynamicsRange range, double value)
    {
        string number = value.ToString(range.Step < 1 ? "0.#" : "0", CultureInfo.CurrentCulture);
        return range.Unit switch
        {
            "dB" => Localizer.Format("DynamicsDecibels", number),
            "ms" => Localizer.Format("DynamicsMilliseconds", number),
            _ => Localizer.Format("DynamicsRatioValue", number),
        };
    }

    /// <summary>Take the value from a state push, unless the user moved it a moment ago.</summary>
    internal void Apply(JsonNode? value)
    {
        if (value is not JsonValue v || !v.TryGetValue(out double reported) || reported == _value || SliderSync.RecentlyTouched(Key)) return;
        _value = reported;
        Raise(nameof(Value));
        Raise(nameof(Text));
        _owner.ParameterChanged(this);
    }

    internal void Forget() => SliderSync.Forget(Key);
}

/// <summary>
/// A microphone's compressor or expander on this computer, as its window and
/// the strip's Dynamics flyout show it: the switch, the threshold, the
/// make-up gain (the compressor's), the advanced settings, what the daemon
/// says about it, and the levels the stage reports while a window watches.
/// </summary>
public sealed class MicDynamicsViewModel : ViewModelBase
{
    public const string CompressorKind = "compressor", ExpanderKind = "expander";

    /// <summary>How long a recent switch press wins over state reports that may still precede it.</summary>
    internal static readonly TimeSpan EchoWait = TimeSpan.FromSeconds(1);

    /// <summary>How long the last levels count as current; after that the window says they stopped.</summary>
    internal static readonly TimeSpan LevelsWait = TimeSpan.FromSeconds(1.5);

    private readonly TimeProvider _time;
    private string _channelName;
    private bool _enabled, _available, _showsAdvanced;
    private string? _error;
    private long _enabledAt = long.MinValue, _levelsAt = long.MinValue, _rawAt = long.MinValue;
    private double _inputDb = -120, _reductionDb, _outputDb = -120;

    public MicDynamicsViewModel(IDynamicsCommands commands, string kind, string channel, string channelName, TimeProvider? time = null)
    {
        if (kind is not (CompressorKind or ExpanderKind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Commands = commands;
        Kind = kind;
        Channel = channel;
        _channelName = channelName;
        _time = time ?? TimeProvider.System;
        // Each number with the range the unit takes for it, null where the unit has none.
        DynamicsParameterViewModel Compressor(DynamicsRange range, string label, string tip)
            => new(this, range, UnitDynamics.CompressorControl(range), label, tip);
        DynamicsParameterViewModel Expander(DynamicsRange range, string label, string tip)
            => new(this, range, UnitDynamics.ExpanderControl(range), label, tip);
        if (IsCompressor)
        {
            Threshold = Compressor(MicCompressor.ThresholdRange, Localizer.Text("DynamicsThreshold"), Localizer.Text("CompressorThresholdTip"));
            Makeup = Compressor(MicCompressor.MakeupRange, Localizer.Text("DynamicsMakeup"), Localizer.Text("DynamicsMakeupTip"));
            Advanced =
            [
                Compressor(MicCompressor.RatioRange, Localizer.Text("DynamicsRatio"), Localizer.Text("CompressorRatioTip")),
                Compressor(MicCompressor.AttackRange, Localizer.Text("DynamicsAttack"), Localizer.Text("CompressorAttackTip")),
                Compressor(MicCompressor.ReleaseRange, Localizer.Text("DynamicsRelease"), Localizer.Text("CompressorReleaseTip")),
                Compressor(MicCompressor.KneeRange, Localizer.Text("DynamicsKnee"), Localizer.Text("DynamicsKneeTip")),
            ];
        }
        else
        {
            Threshold = Expander(MicExpander.ThresholdRange, Localizer.Text("DynamicsThreshold"), Localizer.Text("ExpanderThresholdTip"));
            Advanced =
            [
                Expander(MicExpander.RatioRange, Localizer.Text("DynamicsRatio"), Localizer.Text("ExpanderRatioTip")),
                Expander(MicExpander.RangeRange, Localizer.Text("DynamicsRange"), Localizer.Text("DynamicsRangeTip")),
                Expander(MicExpander.AttackRange, Localizer.Text("DynamicsAttack"), Localizer.Text("ExpanderAttackTip")),
                Expander(MicExpander.HoldRange, Localizer.Text("DynamicsHold"), Localizer.Text("DynamicsHoldTip")),
                Expander(MicExpander.ReleaseRange, Localizer.Text("DynamicsRelease"), Localizer.Text("ExpanderReleaseTip")),
            ];
        }
    }

    internal IDynamicsCommands Commands { get; }

    /// <summary><see cref="CompressorKind"/> or <see cref="ExpanderKind"/>.</summary>
    public string Kind { get; }
    public bool IsCompressor => Kind == CompressorKind;

    /// <summary>The channel id the commands name.</summary>
    public string Channel { get; }

    internal bool Detached { get; private set; }

    public string ChannelName
    {
        get => _channelName;
        internal set
        {
            if (!Set(ref _channelName, value)) return;
            Raise(nameof(Title));
            Raise(nameof(Subtitle));
            Raise(nameof(SwitchName));
        }
    }

    /// <summary>"Compressor" or "Expander", the window's heading.</summary>
    public string Name => IsCompressor ? Localizer.Text("Compressor") : Localizer.Text("Expander");

    /// <summary>The window's title: whose processor it is.</summary>
    public string Title => IsCompressor ? Localizer.Format("CompressorWindowTitle", _channelName)
        : Localizer.Format("ExpanderWindowTitle", _channelName);

    /// <summary>Under the heading: the microphone, and where the processor runs.</summary>
    public string Subtitle => OnDevice ? Localizer.Format("DynamicsSubtitleDevice", _channelName, UnitName)
        : Localizer.Format("DynamicsSubtitle", _channelName);

    /// <summary>The window's note: where the processor sits and how it relates to the unit.</summary>
    public string Note => OnDevice
        ? IsCompressor ? Localizer.Format("CompressorNoteDevice", UnitName) : Localizer.Format("ExpanderNoteDevice", UnitName)
        : IsCompressor ? Localizer.Text("CompressorNote") : Localizer.Text("ExpanderNote");

    // --- where it runs ---

    private string? _backend, _deviceName, _deviceFirmware, _deviceReason, _deviceNote, _minimumFirmware;
    private string _processing = UnitDynamics.Device;
    private bool _processingReported, _hasDevice, _deviceReady, _deviceLevels;
    private string? _awaitingProcessing;
    private long _awaitingProcessingSince = long.MinValue;

    /// <summary>Which side runs the processor: <c>device</c> on the unit, <c>software</c>, or null while neither does.</summary>
    public string? Backend => _backend;

    /// <summary>The unit's own processor runs it, on or off: its window shows the unit's limits and no stage levels.</summary>
    public bool OnDevice => _backend == UnitDynamics.Device;

    /// <summary>The unit the channel carries, by its model name; "the interface" when the daemon names none.</summary>
    public string UnitName => _deviceName ?? Localizer.Text("EqTheInterface");

    /// <summary>The channel's choice for its compressor and expander: <c>device</c> or <c>software</c>.</summary>
    public string Processing => _processing;

    /// <summary>
    /// The window offers the choice: the channel carries a unit whose model
    /// runs a compressor and an expander, and the daemon reports the choice.
    /// The unit may not be ready; the choice is kept until it is.
    /// </summary>
    public bool HasProcessingChoice => _hasDevice && _processingReported;

    /// <summary>The "On the unit" side is lit: the unit is chosen and can take them.</summary>
    public bool ProcessOnDevice => _processing == UnitDynamics.Device && _deviceReady;

    /// <summary>The "In software" side is lit: by choice, or because the unit cannot take them.</summary>
    public bool ProcessInSoftware => _processing == UnitDynamics.Software || !_deviceReady;

    /// <summary>The "On the unit" side can be pressed.</summary>
    public bool CanProcessOnDevice => _deviceReady;

    /// <summary>The "On the unit" side's tooltip: the unit named, or why it cannot take them.</summary>
    public string ProcessOnDeviceTip => (_deviceReady ? null : DeviceNote) ?? Localizer.Format("DynamicsProcessingDeviceTip", UnitName);

    /// <summary>
    /// A side of the switch was pressed: <c>device</c> or <c>software</c>.
    /// The side already chosen sends nothing and stays lit.
    /// </summary>
    public void ChooseProcessing(string value)
    {
        if (Detached) return;
        if (!UnitDynamics.IsValid(value) || value == _processing || !HasProcessingChoice || value == UnitDynamics.Device && !_deviceReady)
        {
            Raise(nameof(ProcessOnDevice));
            Raise(nameof(ProcessInSoftware));
            return;
        }
        _processing = value;
        _awaitingProcessing = value;
        _awaitingProcessingSince = _time.GetTimestamp();
        _ = Commands.SetDynamicsProcessingAsync(Channel, value);
        RaisePlace();
    }

    /// <summary>
    /// Why a unit the channel carries does not run the processor while its
    /// model can: older firmware, settings it cannot read or a write it did
    /// not keep. Null while it runs them, while software runs them by choice
    /// (the switch shows that), or when the channel carries no such unit.
    /// </summary>
    public string? DeviceNote
    {
        get
        {
            if (!_hasDevice || OnDevice || _deviceReady) return null;
            return _deviceReason switch
            {
                "firmwareOld" => Localizer.Format("DynamicsDeviceFirmwareOld", UnitName, _deviceFirmware ?? "?", _minimumFirmware ?? "?"),
                "firmwareUnknown" => Localizer.Format("DynamicsDeviceFirmwareUnknown", UnitName, _minimumFirmware ?? "?"),
                "unsupportedBlock" => Localizer.Format("DynamicsDeviceUnsupported", UnitName),
                "failed" => Localizer.Format("DynamicsDeviceFailed", UnitName),
                _ => _deviceNote,
            };
        }
    }

    public bool HasDeviceNote => DeviceNote is not null;

    /// <summary>
    /// The unit runs it and reports its levels (the device's <c>levels</c>,
    /// the Wave XLR MK.2 from firmware 1.0.15): they come as the stage's do.
    /// </summary>
    public bool UnitReportsLevels => OnDevice && _deviceLevels;

    /// <summary>The input and gain reduction meters show: software runs it, or a unit that reports them. Other units report neither.</summary>
    public bool ShowsStageMeters => !OnDevice || _deviceLevels;

    private void RaisePlace()
    {
        foreach (string name in new[]
        {
            nameof(Backend), nameof(OnDevice), nameof(UnitName), nameof(Processing), nameof(HasProcessingChoice), nameof(ProcessOnDevice),
            nameof(ProcessInSoftware), nameof(CanProcessOnDevice), nameof(ProcessOnDeviceTip), nameof(DeviceNote), nameof(HasDeviceNote),
            nameof(ShowsStageMeters), nameof(UnitReportsLevels), nameof(Subtitle), nameof(Note), nameof(FloorDb),
        })
            Raise(name);
        Threshold.RangeChanged();
        Makeup?.RangeChanged();
        foreach (DynamicsParameterViewModel parameter in Advanced) parameter.RangeChanged();
        RaiseStatus();
    }

    /// <summary>The switch's accessible name, with the processor and the channel in it.</summary>
    public string SwitchName => Localizer.Format("DynamicsSwitchName", Name, _channelName);

    /// <summary>The button that opens the window, for a screen reader.</summary>
    public string OpenName => Localizer.Format("DynamicsOpenName", Name, _channelName);

    public DynamicsParameterViewModel Threshold { get; }

    /// <summary>The compressor's make-up gain; null on the expander.</summary>
    public DynamicsParameterViewModel? Makeup { get; }

    public bool HasMakeup => Makeup is not null;

    /// <summary>What the settings button reveals, in the order the signal meets them.</summary>
    public IReadOnlyList<DynamicsParameterViewModel> Advanced { get; }

    public bool ShowsAdvanced { get => _showsAdvanced; set => Set(ref _showsAdvanced, value); }

    /// <summary>The processor's switch.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (Detached || !Set(ref _enabled, value)) return;
            _enabledAt = _time.GetTimestamp();
            _ = Commands.SetDynamicsAsync(Kind, Channel, "enabled", value);
            RaiseStatus();
        }
    }

    /// <summary>The native helper is installed, so the switch does something.</summary>
    public bool Available => _available;

    /// <summary>On and running: the window's switch and the strip's Dynamics button light.</summary>
    public bool IsLit => _enabled && _available && _error is not { Length: > 0 };

    /// <summary>On and unable to run: the Dynamics button takes the alert colour.</summary>
    public bool IsFaulty => _enabled && (!_available || _error is { Length: > 0 });

    /// <summary>
    /// One line under the heading: why the setting does not run, else where
    /// the Input meter reads, since that differs between the two sources and
    /// the two processors, else that no level has come yet.
    /// </summary>
    public string Status
    {
        get
        {
            if (_enabled && _error is { Length: > 0 } error) return error;
            if (!_available) return Localizer.Text("DynamicsNoHost");
            if (OnDevice)
                return UnitReportsLevels && Source == DynamicsLevelSource.Stage
                    ? IsCompressor ? Localizer.Format("CompressorUnitInputPoint", UnitName) : Localizer.Format("ExpanderUnitInputPoint", UnitName)
                    : UnitReportsLevels && Source == DynamicsLevelSource.None ? Localizer.Text("DynamicsNoLevels")
                    : Localizer.Format("DynamicsUnitLevels", UnitName);
            return Source switch
            {
                DynamicsLevelSource.Raw => Localizer.Text("DynamicsRawInput"),
                DynamicsLevelSource.Stage => IsCompressor ? Localizer.Text("CompressorInputPoint") : Localizer.Text("ExpanderInputPoint"),
                _ => Localizer.Text("DynamicsNoLevels"),
            };
        }
    }

    public bool HasStatus => Status.Length > 0;

    /// <summary>True while the line is an error rather than a hint.</summary>
    public bool HasError => _enabled && _error is { Length: > 0 } || !_available;

    // --- the levels ---

    /// <summary>The input level over time, in dBFS, for the graph.</summary>
    public DynamicsHistory History { get; } = new();

    /// <summary>The compressor draws its input as an envelope, the expander as a line.</summary>
    public DynamicsGraphStyle GraphStyle => IsCompressor ? DynamicsGraphStyle.Envelope : DynamicsGraphStyle.Line;

    /// <summary>The graph's and the input and output meters' floor: the threshold's lowest setting.</summary>
    public double FloorDb => Threshold.Minimum;

    private bool Live(long at) => at != long.MinValue && _time.GetElapsedTime(at) < LevelsWait;

    /// <summary>
    /// Where the levels come from now: the stage's own meters while it runs,
    /// else the meter before the chain, which reads the raw microphone.
    /// </summary>
    public DynamicsLevelSource Source => Live(_levelsAt) ? DynamicsLevelSource.Stage
        : Live(_rawAt) ? DynamicsLevelSource.Raw : DynamicsLevelSource.None;

    /// <summary>Levels arrived a moment ago, so the meters and the graph are live.</summary>
    public bool HasLevels => Source != DynamicsLevelSource.None;

    public double InputDb => _inputDb;
    public double ReductionDb => _reductionDb;
    public double OutputDb => _outputDb;

    /// <summary>The input on the meter's 0 to 1 scale, from <see cref="FloorDb"/> to 0 dBFS.</summary>
    public double InputLevel => Scale(_inputDb);
    /// <summary>The output on the same scale; nothing while the raw microphone stands in for a stage that does not run.</summary>
    public double OutputLevel => Source == DynamicsLevelSource.Raw && !OnDevice ? 0 : Scale(_outputDb);

    /// <summary>
    /// The gain reduction on the meter's 0 to 1 scale: 30 dB fills the
    /// compressor's, and the expander's range fills its own, so a full meter
    /// means fully closed.
    /// </summary>
    public double ReductionLevel => Math.Clamp(_reductionDb / ReductionScale, 0, 1);

    private double ReductionScale => IsCompressor ? 30 : Math.Max(Advanced[1].Value, 6);

    /// <summary>The input, or a dash while the unit runs the processor and no level of its own has come.</summary>
    public string InputText => OnDevice && !Live(_levelsAt) ? Localizer.Text("DynamicsNoReading") : LevelText(_inputDb);
    /// <summary>
    /// The output, or a dash while the levels come from before a stage that
    /// does not run. While the unit runs the processor the output is the
    /// microphone as it leaves the unit.
    /// </summary>
    public string OutputText => Source == DynamicsLevelSource.Raw && !OnDevice ? Localizer.Text("DynamicsNoReading") : LevelText(_outputDb);
    public string ReductionText => DynamicsParameterViewModel.Format(MicCompressor.MakeupRange, Math.Round(_reductionDb, 0) + 0);

    public string InputName => Localizer.Format("DynamicsInputName", InputText);
    public string ReductionName => Localizer.Format("DynamicsReductionName", ReductionText);
    public string OutputName => Localizer.Format("DynamicsOutputName", OutputText);

    private double Scale(double db) => Math.Clamp((db - FloorDb) / -FloorDb, 0, 1);

    private string LevelText(double db) => HasLevels && db > FloorDb
        ? DynamicsParameterViewModel.Format(MicCompressor.ThresholdRange, Math.Round(db, 0) + 0)
        : Localizer.Text("DynamicsSilent");

    /// <summary>Take this processor's <c>[input, reduction, output]</c> from a meter push.</summary>
    internal void ApplyLevels(JsonArray levels)
    {
        // The unit runs it: a stage that runs for something else has this
        // processor off and nothing to report, unless the unit reports its own.
        if (Detached || OnDevice && !_deviceLevels || levels.Count < 3) return;
        double Read(int i) => levels[i] is JsonValue v && v.TryGetValue(out double d) && double.IsFinite(d) ? d : double.NaN;
        double input = Read(0), reduction = Read(1), output = Read(2);
        if (double.IsNaN(input) || double.IsNaN(reduction) || double.IsNaN(output)) return;
        DynamicsLevelSource was = Source;
        // The two sources read at different points of the chain: the history starts again.
        if (was == DynamicsLevelSource.Raw) History.Clear();
        _levelsAt = _time.GetTimestamp();
        _inputDb = input;
        _reductionDb = Math.Max(0, reduction);
        _outputDb = output;
        History.Add(Math.Max(input, FloorDb));
        RaiseLevels();
        if (was != DynamicsLevelSource.Stage) RaiseStatus();
    }

    /// <summary>
    /// Take the microphone's meter before the chain, <c>[rms, peak, clip]</c>
    /// on the meters' 0 to 1 scale (-60 to 0 dBFS), while the stage does not
    /// run: the input and the history show the raw microphone, the gain
    /// reduction stays at 0 and the output shows a dash. The stage's own
    /// levels win while they come. While the unit runs the processor the
    /// same meter reads the microphone as it leaves the unit, after the
    /// unit's compressor and expander: it is the output, and the history
    /// draws it, until the unit's own levels come, on a unit that reports
    /// them.
    /// </summary>
    internal void ApplyInputLevel(JsonArray level)
    {
        if (Detached || level.Count < 2 || Live(_levelsAt)) return;
        // Any number form: the socket's text, or a value built in code.
        if (level[1] is not JsonValue v || v.GetValueKind() != System.Text.Json.JsonValueKind.Number
            || !double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double peak)
            || !double.IsFinite(peak)) return;
        DynamicsLevelSource was = Source;
        if (was == DynamicsLevelSource.None && _levelsAt != long.MinValue) History.Clear();
        _levelsAt = long.MinValue;
        _rawAt = _time.GetTimestamp();
        double db = peak <= 0 ? -120 : Math.Clamp(peak, 0, 1) * 60 - 60;
        _inputDb = OnDevice ? -120 : db;
        _reductionDb = 0;
        _outputDb = OnDevice ? db : -120;
        History.Add(Math.Max(db, FloorDb));
        RaiseLevels();
        if (was != DynamicsLevelSource.Raw) RaiseStatus();
    }

    /// <summary>A second passed: levels that stopped coming stop showing.</summary>
    internal void CheckLevels()
    {
        if (_levelsAt == long.MinValue && _rawAt == long.MinValue || HasLevels) return;
        _levelsAt = _rawAt = long.MinValue;
        _inputDb = _outputDb = -120;
        _reductionDb = 0;
        History.Clear();
        RaiseLevels();
        RaiseStatus();
    }

    /// <summary>
    /// Ask for the levels for another lease, while a window shows them: the
    /// stage's, or the unit's when it runs the processor and reports them,
    /// and while those do not come, the meter before the chain, which runs
    /// beside the route and starts no stage. A unit that reports no levels
    /// gets the meter before the chain alone.
    /// </summary>
    internal async Task KeepLevelsAsync()
    {
        if (Detached) return;
        if (OnDevice && !_deviceLevels)
        {
            await Commands.WatchInputMeterAsync(Channel);
            return;
        }
        await Commands.WatchDynamicsAsync(Channel);
        if (!Live(_levelsAt)) await Commands.WatchInputMeterAsync(Channel);
    }

    private void RaiseLevels()
    {
        foreach (string name in new[]
        {
            nameof(Source), nameof(HasLevels), nameof(InputDb), nameof(ReductionDb), nameof(OutputDb), nameof(InputLevel), nameof(OutputLevel),
            nameof(ReductionLevel), nameof(InputText), nameof(OutputText), nameof(ReductionText),
            nameof(InputName), nameof(ReductionName), nameof(OutputName),
        })
            Raise(name);
    }

    internal void ParameterChanged(DynamicsParameterViewModel parameter)
    {
        if (!IsCompressor && ReferenceEquals(parameter, Advanced[1])) Raise(nameof(ReductionLevel));
    }

    private bool Settled(long at) => at == long.MinValue || _time.GetElapsedTime(at) >= EchoWait;

    /// <summary>Take the channel's <c>compressor</c> or <c>expander</c> from a state push.</summary>
    internal void Apply(JsonNode? state)
    {
        if (Detached || state is not JsonObject s) return;
        _available = s["available"]?.GetValue<bool>() ?? false;
        _error = s["error"]?.GetValue<string>();
        if (Settled(_enabledAt) && s["enabled"]?.GetValue<bool>() is bool enabled && enabled != _enabled)
        {
            _enabled = enabled;
            Raise(nameof(Enabled));
        }
        ApplyPlace(s);
        Threshold.Apply(s["threshold"]);
        Makeup?.Apply(s["makeup"]);
        foreach (DynamicsParameterViewModel parameter in Advanced) parameter.Apply(s[parameter.Field]);
        Raise(nameof(Available));
        RaiseStatus();
    }

    /// <summary>
    /// Where it runs, from a state push: the backend, the choice (a press a
    /// moment ago wins over a report that may precede it) and the unit. A
    /// move between the unit and software starts the levels again, since the
    /// two read at different points.
    /// </summary>
    private void ApplyPlace(JsonObject s)
    {
        bool wasOnDevice = OnDevice;
        _backend = s["backend"]?.GetValue<string>();
        string? processing = s["processing"]?.GetValue<string>();
        _processingReported = UnitDynamics.IsValid(processing);
        processing = _processingReported ? processing! : UnitDynamics.Device;
        if (_awaitingProcessing is { } chosen && _awaitingProcessingSince != long.MinValue
            && _time.GetElapsedTime(_awaitingProcessingSince) < EchoWait && processing != chosen)
            _processing = chosen;
        else
        {
            _awaitingProcessing = null;
            _processing = processing;
        }
        JsonObject? device = s["device"] as JsonObject;
        _hasDevice = device is not null;
        _deviceReady = device?["ready"]?.GetValue<bool>() ?? false;
        bool hadLevels = _deviceLevels;
        _deviceLevels = device?["levels"]?.GetValue<bool>() ?? false;
        _deviceName = device?["name"]?.GetValue<string>();
        _deviceFirmware = device?["firmware"]?.GetValue<string>();
        _deviceReason = device?["reason"]?.GetValue<string>();
        _deviceNote = device?["note"]?.GetValue<string>();
        _minimumFirmware = device?["minimumFirmware"]?.GetValue<string>();
        if (wasOnDevice != OnDevice || OnDevice && hadLevels != _deviceLevels)
        {
            _levelsAt = _rawAt = long.MinValue;
            _inputDb = _outputDb = -120;
            _reductionDb = 0;
            History.Clear();
            RaiseLevels();
        }
        RaisePlace();
    }

    private void RaiseStatus()
    {
        Raise(nameof(Status));
        Raise(nameof(HasStatus));
        Raise(nameof(HasError));
        Raise(nameof(IsLit));
        Raise(nameof(IsFaulty));
    }

    /// <summary>The channel left the mixer or stopped reporting this: nothing more is sent.</summary>
    internal void Detach()
    {
        Detached = true;
        Threshold.Forget();
        Makeup?.Forget();
        foreach (DynamicsParameterViewModel parameter in Advanced) parameter.Forget();
    }
}

/// <summary>
/// A microphone's compressor and expander together, as its strip's Dynamics
/// button shows them: lit while either runs, in the alert colour while
/// either cannot.
/// </summary>
public sealed class ChannelDynamicsViewModel : ViewModelBase
{
    public ChannelDynamicsViewModel(IDynamicsCommands commands, string channel, string channelName, TimeProvider? time = null)
    {
        Expander = new MicDynamicsViewModel(commands, MicDynamicsViewModel.ExpanderKind, channel, channelName, time);
        Compressor = new MicDynamicsViewModel(commands, MicDynamicsViewModel.CompressorKind, channel, channelName, time);
        Expander.PropertyChanged += OnPartChanged;
        Compressor.PropertyChanged += OnPartChanged;
        Channel = channel;
        _channelName = channelName;
    }

    private string _channelName;

    public string Channel { get; }

    /// <summary>In the order the signal meets them.</summary>
    public MicDynamicsViewModel Expander { get; }
    public MicDynamicsViewModel Compressor { get; }

    public string ChannelName
    {
        get => _channelName;
        internal set
        {
            if (!Set(ref _channelName, value)) return;
            Expander.ChannelName = value;
            Compressor.ChannelName = value;
            Raise(nameof(ButtonName));
        }
    }

    public bool IsLit => Expander.IsLit || Compressor.IsLit;
    public bool IsFaulty => Expander.IsFaulty || Compressor.IsFaulty;

    /// <summary>The flyout's heading: where the two run.</summary>
    public string Heading => Compressor.OnDevice ? Localizer.Format("DynamicsHeadingDevice", Compressor.UnitName) : Localizer.Text("DynamicsHeading");

    /// <summary>The flyout's hint: what the two do, and where.</summary>
    public string Hint => Compressor.OnDevice ? Localizer.Format("DynamicsHintDevice", Compressor.UnitName) : Localizer.Text("DynamicsHint");

    /// <summary>The Dynamics button's name for a screen reader: whose, and whether something in it is on.</summary>
    public string ButtonName => IsFaulty ? Localizer.Format("DynamicsButtonNameUnavailable", _channelName)
        : IsLit ? Localizer.Format("DynamicsButtonNameOn", _channelName) : Localizer.Format("DynamicsButtonNameOff", _channelName);

    private void OnPartChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MicDynamicsViewModel.OnDevice) or nameof(MicDynamicsViewModel.UnitName))
        {
            Raise(nameof(Heading));
            Raise(nameof(Hint));
            return;
        }
        if (e.PropertyName is not (null or nameof(MicDynamicsViewModel.IsLit) or nameof(MicDynamicsViewModel.IsFaulty))) return;
        Raise(nameof(IsLit));
        Raise(nameof(IsFaulty));
        Raise(nameof(ButtonName));
    }

    /// <summary>Take the channel's <c>compressor</c> and <c>expander</c> from a state push.</summary>
    internal void Apply(JsonNode? compressor, JsonNode? expander)
    {
        Compressor.Apply(compressor);
        Expander.Apply(expander);
    }

    internal void Detach()
    {
        Expander.Detach();
        Compressor.Detach();
    }
}
