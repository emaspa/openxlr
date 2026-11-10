using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using OpenXLR.UI.Localization;

namespace OpenXLR.UI;

/// <summary>The two commands the Ducking window sends. The daemon client sends them; a test records them.</summary>
public interface IDuckingCommands
{
    /// <summary><c>setDucking</c> with the fields that change; resolves to null, or the daemon's refusal.</summary>
    Task<string?> SetDuckingAsync(IReadOnlyDictionary<string, object> fields);

    /// <summary><c>setDuckingProcessing</c>: <c>device</c> or <c>software</c>; resolves to null, or the daemon's refusal.</summary>
    Task<string?> SetDuckingProcessingAsync(string value);
}

/// <summary>A key, a channel or a mix in one of the Ducking window's checklists.</summary>
public sealed class DuckingChoice : ViewModelBase
{
    private readonly Action<DuckingChoice> _toggled;
    private bool _isChecked;

    internal DuckingChoice(string id, string name, bool isChecked, Action<DuckingChoice> toggled)
    {
        Id = id;
        Name = name;
        _isChecked = isChecked;
        _toggled = toggled;
    }

    public string Id { get; }

    public string Name { get; }

    /// <summary>Ticked by the user: the list goes to the daemon.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set { if (Set(ref _isChecked, value)) _toggled(this); }
    }

    /// <summary>Show what the daemon holds, sending nothing.</summary>
    internal void Show(bool value)
    {
        if (_isChecked == value) return;
        _isChecked = value;
        Raise(nameof(IsChecked));
    }
}

/// <summary>
/// The ducking setup as the Ducking window edits it: the switch, the keys
/// (any of them loud lowers the targets, the loudest counting), the
/// channels lowered and the mixes they are lowered in, the sensitivity,
/// amount and speed on sliders that mark the Wave XLR Pro's presets and send
/// numbers, where it runs, and what runs it now. Every value is the daemon's,
/// or one the user just set; a state that differs from an edit not yet
/// reported back is taken as older than the edit, for a second at most,
/// as the EQ editor does. A refused edit puts the daemon's state back and
/// shows why.
/// </summary>
public sealed class DuckingViewModel : ViewModelBase
{
    // The Wave XLR Pro's presets, which a state may name by word and the
    // sliders mark, and the bounds the daemon takes for a number.
    internal static readonly (string Word, int Db)[] SensitivityPresets = [("low", -3), ("medium", -12), ("high", -24)];
    internal static readonly (string Word, int Db)[] AmountPresets = [("low", 6), ("medium", 12), ("high", 24)];
    internal static readonly (string Word, int Attack, int Release)[] SpeedPresets = [("slow", 100, 2000), ("medium", 80, 1200), ("fast", 30, 200)];
    public const int MinThreshold = -60, MaxThreshold = 0, MinDepth = 1, MaxDepth = 40;
    public const int MinAttack = 1, MaxAttack = 1000, MinRelease = 10, MaxRelease = 10000;

    /// <summary>The deepest preset, in dB: the reduction bar is full at this depth, or at a deeper amount.</summary>
    public const double FullScaleDb = 24;

    /// <summary>How long an edit waits for the daemon to report it back before a different report wins.</summary>
    internal static readonly TimeSpan EchoWait = TimeSpan.FromSeconds(1);

    private readonly IDuckingCommands _commands;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, (string Json, long Since)> _awaiting = new(StringComparer.Ordinal);
    private JsonNode? _reportedNode;
    private bool _applying;

    // The layout the lists are drawn from, as the last state named it.
    // A channel can be a key unless it is a capture input on a stereo pair.
    private sealed record LayoutChannel(string Id, string Name, bool CanKey, bool Application, bool Present);
    private List<LayoutChannel> _layoutChannels = [];
    private List<(string Id, string Name)> _layoutMixes = [];

    // The setup, as reported or as just edited.
    private bool _reported, _mixerRunning, _targetsReported, _enabled;
    private List<string> _keys = ["xlr1"];
    private List<string> _channels = [], _mixes = [];
    private string _processing = "device";

    // What runs it, from the daemon only.
    private string? _backend, _error;
    private bool _available = true;
    private bool _hasDevice, _deviceReady, _held = true;
    private string? _deviceName, _reason, _note;

    private double _reduction;
    private string? _refusal;

    /// <param name="time">The clock for the echo wait; a test passes one it moves by hand.</param>
    public DuckingViewModel(IDuckingCommands commands, TimeProvider? time = null)
    {
        _commands = commands;
        _time = time ?? TimeProvider.System;
        string Db(int value) => Localizer.Format("DynamicsDecibels", DuckingSliderViewModel.Number(value));
        string Ms(int value) => Localizer.Format("DynamicsMilliseconds", DuckingSliderViewModel.Number(value));
        string low = Localizer.Text("DuckingLow"), medium = Localizer.Text("DuckingMedium"), high = Localizer.Text("DuckingHigh");
        string slow = Localizer.Text("DuckingSlow"), fast = Localizer.Text("DuckingFast");
        // Sensitivity rises to the right, so its threshold falls.
        Sensitivity = new(Localizer.Text("DuckingSensitivity"), Localizer.Text("DuckingKeysSensitivityTip"), MinThreshold, MaxThreshold,
            DuckingScale.Reversed, value => Localizer.Format("DuckingDbfs", DuckingSliderViewModel.Number(value)), -12,
            [(-3, low), (-12, medium), (-24, high)], SliderMoved);
        Amount = new(Localizer.Text("DuckingAmount"), Localizer.Text("DuckingKeysAmountTip"), MinDepth, MaxDepth,
            DuckingScale.Linear, Db, 12, [(6, low), (12, medium), (24, high)], SliderMoved);
        Attack = new(Localizer.Text("DynamicsAttack"), Localizer.Text("DuckingKeysAttackTip"), MinAttack, MaxAttack,
            DuckingScale.Log, Ms, 80, [(100, slow), (80, medium), (30, fast)], SliderMoved, isPart: true);
        Release = new(Localizer.Text("DynamicsRelease"), Localizer.Text("DuckingKeysReleaseTip"), MinRelease, MaxRelease,
            DuckingScale.Log, Ms, 1200, [(2000, slow), (1200, medium), (200, fast)], SliderMoved, isPart: true);
    }

    /// <summary>The daemon reports ducking: the buttons that open the window show.</summary>
    public bool IsReported => _reported;

    /// <summary>The setup can be changed: the software mixer runs. In hardware-control mode the daemon refuses both commands.</summary>
    public bool CanEdit => _reported && _mixerRunning;

    /// <summary>The switch.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (!_applying) Send(new() { ["enabled"] = value });
            RaiseState();
        }
    }

    /// <summary>
    /// The channels that can be keys, by name: microphones, the unit's own
    /// inputs and application channels, never a capture input on a stereo
    /// pair. Ticked rows are the keys.
    /// </summary>
    public ObservableCollection<DuckingChoice> Keys { get; } = [];

    /// <summary>The key ids, in the order they were chosen; the first is the one an older client reads as <c>key</c>.</summary>
    public IReadOnlyList<string> KeyIds => _keys;

    /// <summary>
    /// A key ticked or unticked: the keys go to the daemon in the order they
    /// were chosen, a new key leaves the channels in the same command, and the
    /// last key stays, since ducking needs one.
    /// </summary>
    private void KeyToggled(DuckingChoice choice)
    {
        // A list being refilled can write its old rows back as it lets them go: that is not a choice.
        if (_applying || !Keys.Contains(choice)) return;
        List<string> keys = choice.IsChecked
            ? [.. _keys.Where(k => k != choice.Id), choice.Id]
            : [.. _keys.Where(k => k != choice.Id)];
        if (keys.Count == 0)
        {
            Refusal = Localizer.Text("DuckingKeysLast");
            choice.Show(true);
            return;
        }
        if (keys.SequenceEqual(_keys)) return;
        _keys = keys;
        var fields = new Dictionary<string, object> { ["keys"] = _keys.ToArray() };
        if (_channels.Contains(choice.Id))
        {
            _channels = [.. _channels.Where(c => c != choice.Id)];
            fields["channels"] = _channels.ToArray();
        }
        Send(fields);
        RefreshLists();
        RaiseState();
    }

    /// <summary>The channels that can be lowered, the keys left out.</summary>
    public ObservableCollection<DuckingChoice> Channels { get; } = [];

    /// <summary>The mixes they can be lowered in.</summary>
    public ObservableCollection<DuckingChoice> Mixes { get; } = [];

    /// <summary>The daemon names the channels and mixes; one from before the per-channel setup does not, and the lists stay hidden.</summary>
    public bool HasTargets => _targetsReported;

    /// <summary>The key threshold in dBFS, which falls as the slider moves right.</summary>
    public DuckingSliderViewModel Sensitivity { get; }

    /// <summary>How far the channels go down, in dB.</summary>
    public DuckingSliderViewModel Amount { get; }

    /// <summary>The speed's attack, in ms.</summary>
    public DuckingSliderViewModel Attack { get; }

    /// <summary>The speed's release, in ms.</summary>
    public DuckingSliderViewModel Release { get; }

    /// <summary>
    /// A slider settled on a new value: it goes to the daemon as a number,
    /// and the speed as both its times, since the command takes them together.
    /// </summary>
    private void SliderMoved(DuckingSliderViewModel slider)
    {
        if (_applying) return;
        if (slider == Sensitivity) Send(new() { ["sensitivity"] = slider.Value });
        else if (slider == Amount) Send(new() { ["amount"] = slider.Value });
        else Send(new() { ["speed"] = new Dictionary<string, int> { ["attack"] = Attack.Value, ["release"] = Release.Value } });
        Raise(nameof(ReductionLevel));
    }

    /// <summary>Lower every application channel and nothing else.</summary>
    public void ChooseAllApplications()
    {
        if (!CanEdit) return;
        SetChannels([.. _layoutChannels.Where(c => c.Application && !_keys.Contains(c.Id)).Select(c => c.Id)]);
    }

    /// <summary>Lower no channel: ducking then lowers nothing until one is ticked.</summary>
    public void ChooseNoChannels()
    {
        if (!CanEdit) return;
        SetChannels([]);
    }

    private void SetChannels(List<string> channels)
    {
        if (channels.SequenceEqual(_channels)) return;
        _channels = channels;
        Send(new() { ["channels"] = _channels.ToArray() });
        RefreshLists();
        RaiseState();
    }

    private void ChannelToggled(DuckingChoice choice)
    {
        // A list being refilled can write its old rows back as it lets them go: that is not a choice.
        if (_applying || !Channels.Contains(choice)) return;
        // Ticks in layout order, plus any channel the list does not show (one whose jack the unit lacks).
        string[] shown = [.. Channels.Select(c => c.Id)];
        SetChannels([.. Channels.Where(c => c.IsChecked).Select(c => c.Id), .. _channels.Where(c => !shown.Contains(c))]);
    }

    private void MixToggled(DuckingChoice choice)
    {
        if (_applying || !Mixes.Contains(choice)) return;
        string[] shown = [.. Mixes.Select(m => m.Id)];
        List<string> mixes = [.. Mixes.Where(m => m.IsChecked).Select(m => m.Id), .. _mixes.Where(m => !shown.Contains(m))];
        if (mixes.SequenceEqual(_mixes)) return;
        _mixes = mixes;
        Send(new() { ["mixes"] = _mixes.ToArray() });
        RaiseState();
    }

    /// <summary>The unit that can run ducking itself is attached: the "Runs on" choice shows.</summary>
    public bool HasProcessingChoice => _hasDevice;

    /// <summary>The user's choice of where it runs: <c>device</c> or <c>software</c>.</summary>
    public string Processing => _processing;

    /// <summary>
    /// The "This unit" side is lit: the unit is chosen and can run the setup.
    /// While it cannot, this computer runs it whatever the choice, and the
    /// choice is kept for when the unit can.
    /// </summary>
    public bool ProcessOnDevice => _processing == "device" && _deviceReady;

    /// <summary>The "This computer" side is lit: by choice, or because the unit cannot run the setup.</summary>
    public bool ProcessInSoftware => _processing == "software" || !_deviceReady;

    /// <summary>The "This unit" side can be pressed: the unit can run the setup.</summary>
    public bool CanProcessOnDevice => _deviceReady;

    /// <summary>The "This unit" side's tooltip: where the unit listens and what it lowers, or why it cannot run the setup.</summary>
    public string ProcessOnDeviceTip => (_deviceReady ? null : UnitReason)
        ?? Localizer.Format("DuckingKeysProcessingDeviceTip", DeviceName);

    /// <summary>
    /// A side of the switch was pressed: <c>device</c> or <c>software</c>.
    /// The side already chosen sends nothing and stays lit, so both sides are
    /// told again either way: a toggle that a press switched off turns back on.
    /// </summary>
    public void ChooseProcessing(string value)
    {
        if (value is not ("device" or "software") || value == _processing || !HasProcessingChoice || !CanEdit
            || (value == "device" && !_deviceReady))
        {
            Raise(nameof(ProcessOnDevice));
            Raise(nameof(ProcessInSoftware));
            return;
        }
        _processing = value;
        Expect("processing", value);
        Refusal = null;
        _ = Run(_commands.SetDuckingProcessingAsync(value), new() { ["processing"] = value });
        RaiseState();
    }

    /// <summary>Which backend runs the setup: <c>device</c>, <c>software</c>, or null while nothing does.</summary>
    public string? Backend => _backend;

    /// <summary>The unit's own processor runs it: the EQ editor's badge shows.</summary>
    public bool OnDevice => _backend == "device";

    public string BadgeTip => Localizer.Format("DuckingHardwareBadgeTip", DeviceName);

    private string DeviceName => _deviceName ?? Localizer.Text("EqTheInterface");

    /// <summary>The keys by name, joined as a list: "XLR 1", "XLR 1 and Voice Chat", "XLR 1, XLR 2 and Voice Chat".</summary>
    private string KeyNames
    {
        get
        {
            string[] names = [.. _keys.Select(k => _layoutChannels.FirstOrDefault(c => c.Id == k)?.Name ?? k)];
            if (names.Length == 0) return "";
            string list = names[0];
            for (int i = 1; i < names.Length; i++)
                list = i == names.Length - 1 ? Localizer.Format("DuckingKeysListLast", list, names[i])
                    : Localizer.Format("DuckingKeysListMore", list, names[i]);
            return list;
        }
    }

    /// <summary>
    /// Why the unit cannot run the setup, chosen or not, shown under the
    /// switch. The reasons that name no channel or mix read from the
    /// catalogue; the others name them (a key the unit cannot hear among
    /// them), so the daemon's own sentence is shown, as it is for a reason
    /// this window does not know. A mixer that is off is said by the status
    /// instead.
    /// </summary>
    public string? Note => !_hasDevice || !CanEdit || _deviceReady || _reason == "mixerOff" ? null : UnitReason;

    private string UnitReason => _reason switch
    {
        "mixerOff" => Localizer.Text("DuckingMixerOff"),
        "failed" => Localizer.Format("DuckingDeviceFailed", DeviceName),
        "unsupportedBlock" => Localizer.Format("DuckingDeviceUnsupported", DeviceName),
        _ => _note ?? Localizer.Format("DuckingDeviceNotReady", DeviceName),
    };

    public bool HasNote => Note is not null;

    /// <summary>Switched on with no channel or no mix chosen: nothing is lowered.</summary>
    public bool NothingChosen => _targetsReported && (_channels.Count == 0 || _mixes.Count == 0);

    // The software side cannot run: no helper, or a stage that kept failing.
    private bool SoftwareBroken => _backend != "device" && (!_available || _error is not null);

    /// <summary>What runs ducking and where it sits in the signal, or why nothing runs it.</summary>
    public string Status =>
        !_mixerRunning || _reason == "mixerOff" ? Localizer.Text("DuckingMixerOff")
        : !_enabled ? Localizer.Text("DuckingStatusOff")
        : NothingChosen ? Localizer.Text("DuckingNothingChosen")
        : SoftwareBroken ? _error ?? Localizer.Text("DuckingNotRunning")
        : _backend == "device" ? _held ? Localizer.Format("DuckingKeysStatusOnUnit", DeviceName) : Localizer.Text("DuckingNotHeld")
        : _backend == "software" ? Localizer.Format("DuckingKeysStatusOnComputer", KeyNames)
        : Localizer.Text("DuckingNotRunning");

    /// <summary>The status says something is wrong: nothing runs a setup that is on, or the unit does not hold it.</summary>
    public bool Warns => _mixerRunning && _reason != "mixerOff" && _enabled && !NothingChosen
        && (SoftwareBroken || (_backend == "device" ? !_held : _backend != "software"));

    /// <summary>The reduction bar shows while this computer runs it, or while the unit runs it and holds it, which reports its own.</summary>
    public bool ShowReduction => CanEdit && _enabled && (_backend == "software" || _backend == "device" && _held);

    /// <summary>The largest reduction across the ducked mixes now, in dB.</summary>
    public double Reduction => _reduction;

    /// <summary>The bar's fill, 0 to 1 of <see cref="FullScaleDb"/>.</summary>
    public double ReductionLevel => Math.Clamp(_reduction / Math.Max(FullScaleDb, Amount.Value), 0, 1);

    public string ReductionText => Localizer.Format("DuckingReductionValue", Math.Round(_reduction));

    /// <summary>The daemon refused the last change, and why; the controls show its state again.</summary>
    public string? Refusal
    {
        get => _refusal;
        private set { if (Set(ref _refusal, value)) Raise(nameof(HasRefusal)); }
    }

    public bool HasRefusal => _refusal is not null;

    /// <summary>
    /// Take the meters frame's <c>duck:&lt;mix&gt;</c> readings: the largest
    /// across the mixes, or 0 when the frame has none.
    /// </summary>
    public void ApplyMeters(JsonObject levels)
    {
        double largest = 0;
        foreach ((string key, JsonNode? value) in levels)
        {
            if (!key.StartsWith("duck:", StringComparison.Ordinal)) continue;
            double reading = value is JsonArray { Count: > 0 } array ? Number(array[0]) : Number(value);
            if (double.IsFinite(reading)) largest = Math.Max(largest, reading);
        }
        if (Math.Abs(largest - _reduction) < 0.01) return;
        _reduction = largest;
        Raise(nameof(Reduction));
        Raise(nameof(ReductionLevel));
        Raise(nameof(ReductionText));
    }

    private static double Number(JsonNode? node)
    {
        try { return node?.GetValue<double>() ?? 0; }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return 0; }
    }

    /// <summary>Take a state push: its <c>ducking</c> and the mixer the lists are drawn from.</summary>
    public void Apply(JsonNode? ducking, JsonNode? mixer)
    {
        _mixerRunning = mixer is JsonObject;
        ReadLayout(mixer);
        if (ducking is not JsonObject)
        {
            _reported = false;
            _reportedNode = null;
            _awaiting.Clear();
            RefreshLists();
            RaiseState();
            return;
        }
        _reported = true;
        _reportedNode = ducking.DeepClone();
        Take(ducking);
        RefreshLists();
        RaiseState();
    }

    private void ReadLayout(JsonNode? mixer)
    {
        if (mixer?["channels"] is JsonArray channels)
            _layoutChannels = [.. channels.OfType<JsonObject>().Where(c => Text(c["id"]) is not null).Select(c =>
            {
                string id = Text(c["id"])!;
                bool hardware = Flag(c["hardware"]) ?? false;
                bool capture = Text(c["captureSource"]) is not null;
                // A daemon from before the field: XLR 1 and XLR 2 are the microphones.
                bool microphone = Flag(c["microphone"]) ?? id is "xlr1" or "xlr2";
                // Microphones, the unit's own inputs and application channels can key; a stereo capture input cannot.
                return new LayoutChannel(id, Text(c["name"]) ?? id, microphone || hardware || !capture, !hardware && !capture,
                    Flag(c["present"]) ?? true);
            })];
        if (mixer?["mixes"] is JsonArray mixes)
            _layoutMixes = [.. mixes.OfType<JsonObject>().Where(m => Text(m["id"]) is not null)
                .Select(m => (Text(m["id"])!, Text(m["name"]) ?? Text(m["id"])!))];
    }

    private void Take(JsonNode ducking)
    {
        _applying = true;
        try
        {
            if (Fresh("enabled", ducking["enabled"])) _enabled = Flag(ducking["enabled"]) ?? false;
            // A daemon from before several keys reports one key alone.
            JsonArray keys = ducking["keys"] is JsonArray reported ? reported : new JsonArray(Text(ducking["key"]) ?? "xlr1");
            if (Fresh("keys", keys) && Words(keys) is { Count: > 0 } chosen) _keys = chosen;
            _targetsReported = ducking["channels"] is JsonArray && ducking["mixes"] is JsonArray;
            if (Fresh("channels", ducking["channels"])) _channels = Words(ducking["channels"]);
            if (Fresh("mixes", ducking["mixes"])) _mixes = Words(ducking["mixes"]);
            // A word or a number; a word the window does not know leaves the slider where it is.
            if (Fresh("sensitivity", ducking["sensitivity"]) && Level(ducking["sensitivity"], SensitivityPresets) is { } threshold)
                Sensitivity.Show(threshold);
            if (Fresh("amount", ducking["amount"]) && Level(ducking["amount"], AmountPresets) is { } depth) Amount.Show(depth);
            if (Fresh("speed", ducking["speed"]) && Times(ducking["speed"]) is { } times)
            {
                Attack.Show(times.Attack);
                Release.Show(times.Release);
            }
            if (Fresh("processing", ducking["processing"])) _processing = Text(ducking["processing"]) is "software" ? "software" : "device";
            _backend = Text(ducking["backend"]);
            _available = Flag(ducking["available"]) ?? true;
            _error = Text(ducking["error"]);
            JsonNode? device = ducking["device"];
            _hasDevice = device is JsonObject;
            _deviceReady = Flag(device?["ready"]) ?? false;
            _held = Flag(device?["held"]) ?? true;
            _deviceName = Text(device?["name"]);
            _reason = Text(device?["reason"]);
            _note = Text(device?["note"]);
        }
        finally { _applying = false; }
    }

    /// <summary>
    /// The report for a field wins unless the user changed that field less
    /// than <see cref="EchoWait"/> ago and the report still says otherwise.
    /// </summary>
    private bool Fresh(string field, JsonNode? reported)
    {
        if (!_awaiting.TryGetValue(field, out (string Json, long Since) awaited)) return true;
        if (awaited.Json != Json(reported) && _time.GetElapsedTime(awaited.Since) < EchoWait) return false;
        _awaiting.Remove(field);
        return true;
    }

    private void Expect(string field, object value) => _awaiting[field] = (Json(JsonSerializer.SerializeToNode(value)), _time.GetTimestamp());

    private static string Json(JsonNode? node) => node?.ToJsonString() ?? "null";

    private void Send(Dictionary<string, object> fields)
    {
        foreach ((string field, object value) in fields) Expect(field, value);
        Refusal = null;
        _ = Run(_commands.SetDuckingAsync(fields), fields);
    }

    /// <summary>
    /// Send one command. A refusal gives the fields it carried back to the
    /// daemon's last report; an edit to another field still waiting for its
    /// echo keeps its wait, so a report older than that edit cannot undo it.
    /// </summary>
    private async Task Run(Task<string?> command, Dictionary<string, object> fields)
    {
        string? error;
        try { error = await command; }
        catch (Exception ex) when (ex is InvalidOperationException or System.Net.WebSockets.WebSocketException or TimeoutException) { error = ex.Message; }
        if (error is null) return;
        // The daemon kept its setup for these fields: show it again, and why.
        foreach ((string field, object value) in fields)
            if (_awaiting.TryGetValue(field, out (string Json, long Since) awaited) && awaited.Json == Json(JsonSerializer.SerializeToNode(value)))
                _awaiting.Remove(field);
        Refusal = error;
        if (_reportedNode is { } reported) Take(reported);
        RefreshLists();
        RaiseState();
    }

    /// <summary>Redraw the key picker and both checklists from the layout and the setup.</summary>
    private void RefreshLists()
    {
        _applying = true;
        try
        {
            // Every channel that can key and is present, and a key whose channel is not shown by its id.
            List<(string Id, string Name)> keys = [.. _layoutChannels.Where(c => c.CanKey && (c.Present || _keys.Contains(c.Id)))
                .Select(c => (c.Id, c.Name))];
            if (_reported) keys.AddRange(_keys.Where(k => keys.All(c => c.Id != k)).Select(k => (k, k)));
            Sync(Keys, keys, _keys, KeyToggled);

            Sync(Channels, [.. _layoutChannels.Where(c => !_keys.Contains(c.Id) && (c.Present || _channels.Contains(c.Id))).Select(c => (c.Id, c.Name))],
                _channels, ChannelToggled);
            Sync(Mixes, _layoutMixes, _mixes, MixToggled);
        }
        finally { _applying = false; }
    }

    private static void Sync(ObservableCollection<DuckingChoice> list, IReadOnlyList<(string Id, string Name)> items,
        IReadOnlyList<string> chosen, Action<DuckingChoice> toggled)
    {
        if (!items.SequenceEqual(list.Select(c => (c.Id, c.Name))))
        {
            list.Clear();
            foreach ((string id, string name) in items) list.Add(new DuckingChoice(id, name, chosen.Contains(id), toggled));
            return;
        }
        foreach (DuckingChoice choice in list) choice.Show(chosen.Contains(choice.Id));
    }

    private void RaiseState()
    {
        Raise(nameof(IsReported));
        Raise(nameof(CanEdit));
        Raise(nameof(Enabled));
        Raise(nameof(KeyIds));
        Raise(nameof(HasTargets));
        Raise(nameof(ReductionLevel));
        Raise(nameof(HasProcessingChoice));
        Raise(nameof(Processing));
        Raise(nameof(ProcessOnDevice));
        Raise(nameof(ProcessInSoftware));
        Raise(nameof(CanProcessOnDevice));
        Raise(nameof(ProcessOnDeviceTip));
        Raise(nameof(Backend));
        Raise(nameof(OnDevice));
        Raise(nameof(BadgeTip));
        Raise(nameof(Note));
        Raise(nameof(HasNote));
        Raise(nameof(NothingChosen));
        Raise(nameof(Status));
        Raise(nameof(Warns));
        Raise(nameof(ShowReduction));
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static bool? Flag(JsonNode? node) => node is JsonValue value && value.TryGetValue(out bool flag) ? flag : null;

    private static List<string> Words(JsonNode? node) => node is JsonArray array ? [.. array.Select(Text).OfType<string>()] : [];

    private static int? Whole(JsonNode? node)
        => node is JsonValue value && value.GetValueKind() == JsonValueKind.Number
            && double.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double number)
            && double.IsFinite(number) && number == Math.Floor(number)
            && number is >= int.MinValue and <= int.MaxValue ? (int)number : null;

    /// <summary>A sensitivity's or an amount's number: its preset's for a word, or the number the state holds.</summary>
    private static int? Level(JsonNode? node, (string Word, int Db)[] presets)
        => Text(node) is { } word ? Array.FindIndex(presets, p => p.Word == word) is int at and >= 0 ? presets[at].Db : null : Whole(node);

    /// <summary>A speed's attack and release: its preset's for a word, or the object's.</summary>
    private static (int Attack, int Release)? Times(JsonNode? node)
    {
        if (Text(node) is { } word)
            return Array.FindIndex(SpeedPresets, p => p.Word == word) is int at and >= 0 ? (SpeedPresets[at].Attack, SpeedPresets[at].Release) : null;
        return node is JsonObject times && Whole(times["attack"]) is { } attack && Whole(times["release"]) is { } release ? (attack, release) : null;
    }
}
