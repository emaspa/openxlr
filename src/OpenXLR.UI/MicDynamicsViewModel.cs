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
/// its way does not move the slider back.
/// </summary>
public sealed class DynamicsParameterViewModel : ViewModelBase
{
    private readonly MicDynamicsViewModel _owner;
    private double _value;

    internal DynamicsParameterViewModel(MicDynamicsViewModel owner, DynamicsRange range, string label, string tip)
    {
        _owner = owner;
        Range = range;
        Label = label;
        Tip = tip;
        _value = range.Default;
    }

    public DynamicsRange Range { get; }

    /// <summary>The command's field: <c>threshold</c>, <c>ratio</c> and so on.</summary>
    public string Field => Range.Name;
    public string Label { get; }
    public string Tip { get; }
    public double Minimum => Range.Minimum;
    public double Maximum => Range.Maximum;
    public double Step => Range.Step;

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
    private long _enabledAt = long.MinValue, _levelsAt = long.MinValue;
    private double _inputDb = -120, _reductionDb, _outputDb = -120;

    public MicDynamicsViewModel(IDynamicsCommands commands, string kind, string channel, string channelName, TimeProvider? time = null)
    {
        if (kind is not (CompressorKind or ExpanderKind)) throw new ArgumentOutOfRangeException(nameof(kind));
        Commands = commands;
        Kind = kind;
        Channel = channel;
        _channelName = channelName;
        _time = time ?? TimeProvider.System;
        if (IsCompressor)
        {
            Threshold = new(this, MicCompressor.ThresholdRange, Localizer.Text("DynamicsThreshold"), Localizer.Text("CompressorThresholdTip"));
            Makeup = new(this, MicCompressor.MakeupRange, Localizer.Text("DynamicsMakeup"), Localizer.Text("DynamicsMakeupTip"));
            Advanced =
            [
                new(this, MicCompressor.RatioRange, Localizer.Text("DynamicsRatio"), Localizer.Text("CompressorRatioTip")),
                new(this, MicCompressor.AttackRange, Localizer.Text("DynamicsAttack"), Localizer.Text("CompressorAttackTip")),
                new(this, MicCompressor.ReleaseRange, Localizer.Text("DynamicsRelease"), Localizer.Text("CompressorReleaseTip")),
                new(this, MicCompressor.KneeRange, Localizer.Text("DynamicsKnee"), Localizer.Text("DynamicsKneeTip")),
            ];
        }
        else
        {
            Threshold = new(this, MicExpander.ThresholdRange, Localizer.Text("DynamicsThreshold"), Localizer.Text("ExpanderThresholdTip"));
            Advanced =
            [
                new(this, MicExpander.RatioRange, Localizer.Text("DynamicsRatio"), Localizer.Text("ExpanderRatioTip")),
                new(this, MicExpander.RangeRange, Localizer.Text("DynamicsRange"), Localizer.Text("DynamicsRangeTip")),
                new(this, MicExpander.AttackRange, Localizer.Text("DynamicsAttack"), Localizer.Text("ExpanderAttackTip")),
                new(this, MicExpander.HoldRange, Localizer.Text("DynamicsHold"), Localizer.Text("DynamicsHoldTip")),
                new(this, MicExpander.ReleaseRange, Localizer.Text("DynamicsRelease"), Localizer.Text("ExpanderReleaseTip")),
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

    /// <summary>Under the heading: the microphone, and that this runs on the computer, not on the unit.</summary>
    public string Subtitle => Localizer.Format("DynamicsSubtitle", _channelName);

    /// <summary>The window's note on how it differs from the unit's own switch.</summary>
    public string Note => IsCompressor ? Localizer.Text("CompressorNote") : Localizer.Text("ExpanderNote");

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
    /// One line under the heading: why the setting does not run, else that the
    /// levels show only while the stage runs, else nothing.
    /// </summary>
    public string Status
    {
        get
        {
            if (_enabled && _error is { Length: > 0 } error) return error;
            if (!_available) return Localizer.Text("DynamicsNoHost");
            return HasLevels ? "" : Localizer.Text("DynamicsNoLevels");
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

    /// <summary>Levels arrived a moment ago, so the meters and the graph are live.</summary>
    public bool HasLevels => _levelsAt != long.MinValue && _time.GetElapsedTime(_levelsAt) < LevelsWait;

    public double InputDb => _inputDb;
    public double ReductionDb => _reductionDb;
    public double OutputDb => _outputDb;

    /// <summary>The input on the meter's 0 to 1 scale, from <see cref="FloorDb"/> to 0 dBFS.</summary>
    public double InputLevel => Scale(_inputDb);
    public double OutputLevel => Scale(_outputDb);

    /// <summary>
    /// The gain reduction on the meter's 0 to 1 scale: 30 dB fills the
    /// compressor's, and the expander's range fills its own, so a full meter
    /// means fully closed.
    /// </summary>
    public double ReductionLevel => Math.Clamp(_reductionDb / ReductionScale, 0, 1);

    private double ReductionScale => IsCompressor ? 30 : Math.Max(Advanced[1].Value, 6);

    public string InputText => LevelText(_inputDb);
    public string OutputText => LevelText(_outputDb);
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
        if (Detached || levels.Count < 3) return;
        double Read(int i) => levels[i] is JsonValue v && v.TryGetValue(out double d) && double.IsFinite(d) ? d : double.NaN;
        double input = Read(0), reduction = Read(1), output = Read(2);
        if (double.IsNaN(input) || double.IsNaN(reduction) || double.IsNaN(output)) return;
        bool had = HasLevels;
        _levelsAt = _time.GetTimestamp();
        _inputDb = input;
        _reductionDb = Math.Max(0, reduction);
        _outputDb = output;
        History.Add(Math.Max(input, FloorDb));
        RaiseLevels();
        if (!had) RaiseStatus();
    }

    /// <summary>A second passed: levels that stopped coming stop showing.</summary>
    internal void CheckLevels()
    {
        if (_levelsAt == long.MinValue || HasLevels) return;
        _levelsAt = long.MinValue;
        _inputDb = _outputDb = -120;
        _reductionDb = 0;
        History.Clear();
        RaiseLevels();
        RaiseStatus();
    }

    /// <summary>Ask for the levels for another lease, while a window shows them.</summary>
    internal Task KeepLevelsAsync() => Detached ? Task.CompletedTask : Commands.WatchDynamicsAsync(Channel);

    private void RaiseLevels()
    {
        foreach (string name in new[]
        {
            nameof(HasLevels), nameof(InputDb), nameof(ReductionDb), nameof(OutputDb), nameof(InputLevel), nameof(OutputLevel),
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
        Threshold.Apply(s["threshold"]);
        Makeup?.Apply(s["makeup"]);
        foreach (DynamicsParameterViewModel parameter in Advanced) parameter.Apply(s[parameter.Field]);
        Raise(nameof(Available));
        RaiseStatus();
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

    /// <summary>The Dynamics button's name for a screen reader: whose, and whether something in it is on.</summary>
    public string ButtonName => IsFaulty ? Localizer.Format("DynamicsButtonNameUnavailable", _channelName)
        : IsLit ? Localizer.Format("DynamicsButtonNameOn", _channelName) : Localizer.Format("DynamicsButtonNameOff", _channelName);

    private void OnPartChanged(object? sender, PropertyChangedEventArgs e)
    {
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
