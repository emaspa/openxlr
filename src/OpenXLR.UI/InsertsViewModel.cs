using OpenXLR.UI.Localization;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace OpenXLR.UI;

/// <summary>A plugin the picker offers at this chain's width.</summary>
public sealed record PluginChoice(string Uri, string Name, string Category, JsonNode Params,
    bool NativeEditorAvailable = false, bool NativeEditorSupported = false, string Kind = "lv2", bool NativeUiBlocked = false)
{
    /// <summary>The format, as the picker shows it: two plugins can share a name and differ in nothing else.</summary>
    public string Format => Kind.ToUpperInvariant();

    public override string ToString() => Category.Length > 0 ? $"{Name}  ({Category}, {Format})" : $"{Name}  ({Format})";
}

/// <summary>
/// The insert chain of one channel: what the daemon reports, plus the
/// picker to add more. Edits go to the daemon as a whole new chain (order
/// matters); parameter moves go live one control at a time.
/// </summary>
public sealed partial class InsertsViewModel : ViewModelBase
{
    private readonly DaemonClient _client;
    private readonly string _channel;
    private readonly int _channels;
    private bool _applying;
    private bool _pluginsRequested;
    private int _catalogGeneration;
    internal bool IsDetached { get; private set; }

    /// <param name="channel">Insert key: "xlr1", "xlr2", a capture id or "mix:&lt;id&gt;".</param>
    /// <param name="channels">1 for a mono input, 2 for a stereo input or mix.</param>
    public InsertsViewModel(DaemonClient client, string channel, int channels = 1, string? title = null)
    {
        _client = client;
        _channel = channel;
        _channels = channels;
        _title = title ?? channel;
        MicSetup = new MicSetupViewModel(client, channel, this);
        _canMicSetup = channel is "xlr1" or "xlr2";
    }

    private string _title;
    /// <summary>What the chain belongs to, for window titles ("XLR 1", "Stream mix").</summary>
    public string Title { get => _title; internal set => Set(ref _title, value); }

    private bool _canMicSetup;
    /// <summary>Mic setup runs on channels the daemon reports as microphones.</summary>
    public bool CanMicSetup { get => _canMicSetup; internal set => Set(ref _canMicSetup, value); }
    public MicSetupViewModel MicSetup { get; }

    public Task ShowNativeEditorAsync(InsertViewModel insert)
        => insert.IsDetached ? Task.CompletedTask : _client.ShowInsertUiAsync(_channel, insert.Id);

    /// <summary>Chain window subtitle: where these plugins sit in the path.</summary>
    public string ChainHint => _channel.StartsWith("mix:", StringComparison.Ordinal)
        ? Localizer.Text("MixChainHint")
        : Localizer.Text("InputChainHint");

    /// <summary>Picker header: which plugins fit this chain.</summary>
    public string PickerHint => _channels == 1
        ? Localizer.Text("MonoPickerHint")
        : _channel.StartsWith("mix:", StringComparison.Ordinal) ? Localizer.Text("StereoPickerHint")
        : Localizer.Text("StereoInputPickerHint");

    public ObservableCollection<InsertViewModel> Items { get; } = [];
    public ObservableCollection<PluginChoice> PluginChoices { get; } = [];

    public bool HasItems => Items.Count > 0;

    /// <summary>A plugin in the chain is processing: in the path and not failed.</summary>
    public bool IsProcessing => Items.Any(i => i.IsActive);

    /// <summary>The chain holds plugins and none is processing: every one bypassed or failed.</summary>
    public bool IsOff => Items.Count > 0 && !IsProcessing;

    /// <summary>The button that opens the chain wears these; an insert raises them when its state moves.</summary>
    internal void RaiseChainState()
    {
        Raise(nameof(IsProcessing));
        Raise(nameof(IsOff));
    }

    /// <summary>One-line state for the strip: count, or a hint when empty.</summary>
    public string Summary => Items.Count switch
    {
        0 => Localizer.Text("InsertsNone"),
        1 => Localizer.Text("OnePlugin"),
        int n => Localizer.Format("PluginsInChain", n),
    };

    /// <summary>Label for a compact button that opens the chain window.</summary>
    public string ButtonText => Items.Count == 0 ? Localizer.Text("Inserts") : Localizer.Format("InsertsCount", Items.Count);

    private string? _unitNote;
    /// <summary>
    /// Why no plugin can be added to this mix's chain: the Wave XLR Pro
    /// builds the mix for its jacks and runs no insert there. Null otherwise.
    /// The chain window says it under its title and keeps Add plugin off.
    /// </summary>
    public string? UnitNote
    {
        get => _unitNote;
        internal set
        {
            if (!Set(ref _unitNote, value)) return;
            Raise(nameof(HasUnitNote));
            Raise(nameof(CanAddPlugins));
            Raise(nameof(ButtonTip));
        }
    }
    public bool HasUnitNote => _unitNote is not null;

    /// <summary>Add plugin and Presets and A/B: off while the unit builds the mix.</summary>
    public bool CanAddPlugins => _unitNote is null;

    /// <summary>The tooltip of the button that opens the chain: why it does nothing on the unit, or what a chain is.</summary>
    public string ButtonTip => _unitNote ?? Localizer.Text("StereoPluginsProcessingThisMixBeforeItReaches");

    private PluginChoice? _selectedPlugin;
    public PluginChoice? SelectedPlugin
    {
        get => _selectedPlugin;
        set { if (Set(ref _selectedPlugin, value)) Raise(nameof(CanAdd)); }
    }

    public bool CanAdd => !IsDetached && _selectedPlugin is not null;

    private string? _note;
    /// <summary>Picker status: scanning, count, or why nothing is offered.</summary>
    public string? Note { get => _note; private set => Set(ref _note, value); }

    // One catalog fetch per daemon connection, shared by every chain (the
    // XLR strips, capture inputs and mixes), so the controls windows can build their
    // sliders from restored state without anyone opening a picker first.
    private static Task<JsonNode?>? _catalogTask;

    private static Task<JsonNode?> CatalogAsync(DaemonClient client)
        => _catalogTask ??= client.RequestPluginsAsync(TimeSpan.FromSeconds(20));

    /// <summary>Fetch the catalog once per connection (lilv's scan can take a moment).</summary>
    public void EnsurePluginsLoaded() => _ = LoadPluginsAsync();

    internal async Task LoadPluginsAsync()
    {
        if (IsDetached || _pluginsRequested) return;
        _pluginsRequested = true;
        Note = Localizer.Text("ScanningPlugins");
        int generation = ++_catalogGeneration;
        Task<JsonNode?> request = CatalogAsync(_client);
        JsonNode? plugins = await request.ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (generation != _catalogGeneration) return;
            PluginChoices.Clear();
            SelectedPlugin = null;
            if (plugins is not JsonArray arr)
            {
                Note = Localizer.Text("PluginListUnavailableShort");
                _pluginsRequested = false;
                if (ReferenceEquals(_catalogTask, request)) _catalogTask = null;
                return;
            }
            foreach (JsonNode? p in arr)
            {
                if (p is null) continue;
                if (p["supported"]?.GetValue<bool>() == false) continue;   // needs a host feature the chain lacks
                if (!Fits(p, _channels)) continue;
                PluginChoices.Add(new PluginChoice(
                    p["plugin"]!.GetValue<string>(),
                    p["name"]?.GetValue<string>() ?? p["plugin"]!.GetValue<string>(),
                    p["category"]?.GetValue<string>() ?? "",
                    p["params"] ?? new JsonArray(),
                    p["nativeEditorAvailable"]?.GetValue<bool>() == true,
                    p["nativeEditorSupported"]?.GetValue<bool>() == true,
                    p["kind"]?.GetValue<string>() ?? "lv2",
                    p["nativeUiBlocked"]?.GetValue<bool>() == true));
            }
            foreach (InsertViewModel insert in Items) insert.RefreshCatalogue();
            bool mono = _channels == 1;
            Note = PluginChoices.Count == 0
                ? (mono ? Localizer.Text("NoMonoPlugins") : Localizer.Text("NoStereoPlugins"))
                : mono ? Localizer.Format("MonoPluginsAvailable", PluginChoices.Count)
                : Localizer.Format("StereoPluginsAvailable", PluginChoices.Count);
        });
    }

    /// <summary>
    /// Whether a catalogue entry can sit in a chain of this width. A daemon
    /// that asked the plugin says so in <c>widths</c>, which is how a VST3
    /// plugin that reports stereo but runs mono reaches the mic path; an
    /// entry without it goes by its ports, as it always did: one each way
    /// for the mono chains, two or more for a stereo mix (extra ports stay
    /// unlinked).
    /// </summary>
    internal static bool Fits(JsonNode plugin, int channels)
    {
        if (plugin["widths"] is JsonArray widths)
            return widths.Any(w => w is JsonValue value && value.TryGetValue(out int width) && width == channels);
        int ins = plugin["audioIns"]?.GetValue<int>() ?? 0, outs = plugin["audioOuts"]?.GetValue<int>() ?? 0;
        return channels == 1 ? ins == 1 && outs == 1 : ins >= 2 && outs >= 2;
    }

    public void ResetForNewConnection()
    {
        MicSetup.Reset();
        _catalogGeneration++;
        _pluginsRequested = false;
        _catalogTask = null;
        PluginChoices.Clear();
        SelectedPlugin = null;
        Note = null;
        foreach (var insert in Items) insert.ForgetPendingParameters();
        ResetEffectWorkflow();
    }

    /// <summary>
    /// After a plugin was installed: every chain fetches the catalogue
    /// again, sharing one request. The main view model wires this to all
    /// of its chains; the picker and Options call it.
    /// </summary>
    public static Action? ReloadAll { get; set; }

    internal static void ForgetCatalogue() => _catalogTask = null;

    /// <summary>Fetch the catalogue again for this chain.</summary>
    public void Refetch() { _pluginsRequested = false; EnsurePluginsLoaded(); }

    internal DaemonClient Client => _client;

    /// <summary>Retire a deleted mix's chain, including pending catalogue and workflow replies.</summary>
    internal void Detach()
    {
        if (IsDetached) return;
        IsDetached = true;
        MicSetup.Detach();
        MicSetupWindow.CloseRemovedInput(MicSetup);
        _catalogGeneration++;
        ResetEffectWorkflow();
        Apply(null);
        PluginChoices.Clear();
        SelectedPlugin = null;
        Raise(nameof(CanAdd));
    }

    /// <summary>Apply the daemon's view of this channel's chain.</summary>
    public void Apply(JsonNode? chain)
    {
        _applying = true;
        try
        {
            var incoming = chain as JsonArray ?? [];
            // Rebuild in place, keeping view models whose id survives so
            // expanded panels and slider state do not flicker.
            var byId = Items.ToDictionary(i => i.Id);
            var next = new List<InsertViewModel>();
            foreach (JsonNode? entry in incoming)
            {
                JsonNode? ins = entry?["insert"];
                if (ins is null) continue;
                string id = ins["id"]!.GetValue<string>();
                // A slot that now holds another plugin gets a new view model,
                // so its controls and open windows never show the old one.
                if (!byId.TryGetValue(id, out InsertViewModel? vm)
                    || vm.Plugin != ins["plugin"]!.GetValue<string>()
                    || vm.Kind != (ins["kind"]?.GetValue<string>() ?? "lv2"))
                    vm = new InsertViewModel(this, id, ins["plugin"]!.GetValue<string>(), ins["label"]?.GetValue<string>() ?? id,
                        ins["kind"]?.GetValue<string>() ?? "lv2");
                vm.ApplyFromDaemon(ins, entry?["error"]?.GetValue<string>(),
                    entry?["nativeHostRunning"]?.GetValue<bool>() == true,
                    entry?["nativeUiBlocked"]?.GetValue<bool>() == true,
                    entry?["nativeUiBlockReason"]?.GetValue<string>());
                vm.LatencyMilliseconds = entry?["latencyMilliseconds"]?.GetValue<double>();
                vm.MeasuredLatencyMilliseconds = entry?["measuredLatencyMilliseconds"]?.GetValue<double>();
                next.Add(vm);
            }
            if (!next.SequenceEqual(Items))
            {
                foreach (var removed in Items.Except(next)) removed.Detach();
                Items.Clear();
                foreach (InsertViewModel vm in next) Items.Add(vm);
                Raise(nameof(HasItems));
                Raise(nameof(Summary));
                Raise(nameof(ButtonText));
            }
            RaiseChainState();
        }
        finally { _applying = false; }
        CheckComparison();
    }

    // --- edits, all expressed as a new whole chain ---

    public void Add() { if (_selectedPlugin is not null) Add(_selectedPlugin); }

    public void Add(PluginChoice plugin)
    {
        if (IsDetached) return;
        EffectEdited();
        var chain = Snapshot();
        chain.Add(new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid().ToString("N")[..8],
            ["kind"] = plugin.Kind,
            ["plugin"] = plugin.Uri,
            ["label"] = plugin.Name,
            ["bypass"] = false,
            ["params"] = new Dictionary<string, double>(),
        });
        _ = _client.SetInsertsAsync(_channel, chain);
    }

    public void Remove(InsertViewModel item)
    {
        if (item.IsDetached) return;
        EffectEdited();
        _ = _client.SetInsertsAsync(_channel, Snapshot(skip: item.Id));
    }

    public void Move(InsertViewModel item, int delta)
    {
        int i = Items.IndexOf(item);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= Items.Count) return;
        EffectEdited();
        var order = Items.ToList();
        (order[i], order[j]) = (order[j], order[i]);
        _ = _client.SetInsertsAsync(_channel, Snapshot(order));
    }

    internal void SendBypass(InsertViewModel item, bool bypass)
    {
        if (!_applying && !IsDetached) { EffectEdited(); _ = _client.SetInsertBypassAsync(_channel, item.Id, bypass); }
    }

    internal void SendOwnBlock(InsertViewModel item, bool ownBlock)
    {
        if (!_applying && !IsDetached) { EffectEdited(); _ = _client.SetInsertOwnBlockAsync(_channel, item.Id, ownBlock); }
    }

    internal void SendParam(InsertViewModel item, string symbol, double value)
    {
        if (_applying || IsDetached) return;
        EffectEdited();
        string key = ParameterKey(item.Id, symbol);
        SliderSync.Touch(key);
        SliderSync.Send(key, () => _ = _client.SetInsertParamAsync(_channel, item.Id, symbol, value));
    }

    // Channel and insert ids cannot contain '/', so each control has one key
    // even when two chains hold an insert under the same id.
    internal string ParameterKey(string id, string symbol) => $"ins:{_channel}/{id}/{symbol}";

    internal bool Applying => _applying;

    internal void SendHostChoice()
    {
        if (!_applying && !IsDetached) { EffectEdited(); _ = _client.SetInsertsAsync(_channel, Snapshot()); }
    }

    /// <summary>The current chain as the daemon wants it, minus an optional id.</summary>
    private List<object> Snapshot(IEnumerable<InsertViewModel>? order = null, string? skip = null)
        => [.. (order ?? Items).Where(i => i.Id != skip).Select(i => (object)i.ToPayload())];

    /// <summary>Parameter metadata for a plugin uri, from the catalog.</summary>
    internal JsonNode? ParamsFor(string kind, string uri) => PluginChoices.FirstOrDefault(p => p.Kind == kind && p.Uri == uri)?.Params;
}

public sealed class InsertViewModel : ViewModelBase
{
    private readonly InsertsViewModel _owner;
    internal bool IsDetached { get; private set; }

    public InsertViewModel(InsertsViewModel owner, string id, string plugin, string label, string kind = "lv2")
    {
        _owner = owner;
        Id = id;
        Plugin = plugin;
        Label = label;
        Kind = kind;
    }

    public string Id { get; }
    public string Plugin { get; }
    private string _label = "";
    public string Label { get => _label; private set => Set(ref _label, value); }
    public string Kind { get; }
    public string Format => Kind.ToUpperInvariant();

    private double? _latencyMilliseconds;
    /// <summary>The plugin's processing latency as the daemon reads it, or null when it gives no figure.</summary>
    public double? LatencyMilliseconds
    {
        get => _latencyMilliseconds;
        internal set { if (Set(ref _latencyMilliseconds, value)) Raise(nameof(LatencyText)); }
    }

    private double? _measuredLatencyMilliseconds;
    /// <summary>The delay the native host measured on the insert's node, or null until it has a figure.</summary>
    public double? MeasuredLatencyMilliseconds
    {
        get => _measuredLatencyMilliseconds;
        internal set { if (Set(ref _measuredLatencyMilliseconds, value)) Raise(nameof(LatencyText)); }
    }

    /// <summary>
    /// The latency the plugin reports and, for an insert in the native host,
    /// the delay measured on its node, or "measuring" until there is a
    /// figure. An insert in PipeWire's filter chain is not measured, so it
    /// shows the reported figure alone.
    /// </summary>
    public string LatencyText => (NativeHostRunning, LatencyMilliseconds, MeasuredLatencyMilliseconds) switch
    {
        (true, double reported, double measured) => Localizer.Format("LatencyReportedMeasured", reported, measured),
        (true, double reported, null) => Localizer.Format("LatencyReportedMeasuring", reported),
        (true, null, double measured) => Localizer.Format("LatencyUnreportedMeasured", measured),
        (true, null, null) => Localizer.Text("LatencyUnreportedMeasuring"),
        (false, double reported, _) => Localizer.Format("LatencyMilliseconds", reported),
        _ => Localizer.Text("LatencyUnknown"),
    };

    /// <summary>The channel chain this insert belongs to (row buttons route through it).</summary>
    public InsertsViewModel Owner => _owner;
    /// <summary>
    /// The plugin could be hosted, whether or not the helper is here. A daemon
    /// that only reports availability is older than this window, and anything
    /// it calls available is by definition supported.
    /// </summary>
    public bool NativeEditorSupported => _owner.PluginChoices.Any(
        p => p.Kind == Kind && p.Uri == Plugin && (p.NativeEditorSupported || p.NativeEditorAvailable));

    /// <summary>The helper is here too, so turning the host on can work.</summary>
    public bool NativeHostInstalled => _owner.PluginChoices.Any(p => p.Kind == Kind && p.Uri == Plugin && p.NativeEditorAvailable);

    private bool _nativeUiBlocked;
    private string? _nativeUiBlockReason;
    public bool NativeUiBlocked => _nativeUiBlocked || _owner.PluginChoices.Any(p => p.Kind == Kind && p.Uri == Plugin && p.NativeUiBlocked);
    public string? NativeUiBlockReason => NativeUiBlocked
        ? _nativeUiBlockReason ?? Localizer.Text("NativeEditorDisabled")
        : null;

    /// <summary>
    /// The editor follows the host that runs the insert, which is the native
    /// host for a saved choice and for an LV2 insert PipeWire's filter chain
    /// refused.
    /// </summary>
    public bool NativeEditorAvailable => !IsDetached && !NativeUiBlocked && NativeHostInstalled && !Bypass && !HasError && NativeHostRunning;

    /// <summary>
    /// The switch can be turned on only where the helper is installed. It
    /// stays usable while it is on, so a chain can always be moved back.
    /// </summary>
    public bool CanTurnNativeHostOn => NativeHostInstalled || NativeHost;

    /// <summary>What the cog will open, which depends on what is running.</summary>
    public string ControlsHint => NativeEditorAvailable
        ? Localizer.Text("OpenPluginOwnEditor")
        : Localizer.Text("OpenThisPluginSControls");

    /// <summary>Everything the row and the controls window derive from the host state.</summary>
    internal void RefreshCatalogue()
    {
        RaiseNativeFlags();
        if (_paramsRequested) BuildParams();
    }

    private void RaiseNativeFlags()
    {
        Raise(nameof(NativeUiBlocked));
        Raise(nameof(NativeUiBlockReason));
        Raise(nameof(NativeEditorSupported));
        Raise(nameof(NativeHostInstalled));
        Raise(nameof(NativeEditorAvailable));
        Raise(nameof(CanChooseNativeHost));
        Raise(nameof(CanTurnNativeHostOn));
        Raise(nameof(CanChooseOwnBlock));
        Raise(nameof(ControlsHint));
    }

    private bool _nativeHost;
    public bool NativeHost
    {
        get => _nativeHost;
        set
        {
            if (IsDetached) return;
            if (Set(ref _nativeHost, value))
            {
                RaiseNativeFlags();
                _owner.SendHostChoice();
            }
        }
    }
    /// <summary>Only an LV2 plugin has a filter chain to choose over the host.</summary>
    public bool CanChooseNativeHost => Kind == "lv2" && (NativeEditorSupported || NativeHost);

    private readonly Dictionary<string, double> _params = [];

    private bool _ownBlock;
    /// <summary>
    /// The plugin runs on a block of its own in the native host, for one that
    /// cannot keep up with a short block. Switching starts its host again.
    /// </summary>
    public bool OwnBlock
    {
        get => _ownBlock;
        set
        {
            if (IsDetached) return;
            if (Set(ref _ownBlock, value))
            {
                Raise(nameof(CanChooseOwnBlock));
                _owner.SendOwnBlock(this, value);
            }
        }
    }

    /// <summary>
    /// Offered where the native host can run the plugin, and always while it
    /// is on, so it can be turned off again.
    /// </summary>
    public bool CanChooseOwnBlock => OwnBlock || NativeHostRunning || NativeHostInstalled;

    private bool _bypass;
    public bool Bypass
    {
        get => _bypass;
        set { if (!IsDetached && Set(ref _bypass, value)) { Raise(nameof(StateText)); Raise(nameof(IsActive)); RaiseNativeFlags(); _owner.RaiseChainState(); _owner.SendBypass(this, value); } }
    }

    private string? _error;
    public string? Error
    {
        get => _error;
        private set { if (Set(ref _error, value)) { Raise(nameof(HasError)); Raise(nameof(StateText)); Raise(nameof(IsActive)); RaiseNativeFlags(); _owner.RaiseChainState(); } }
    }
    public bool HasError => _error is not null;

    private bool _nativeHostRunning;
    public bool NativeHostRunning
    {
        get => _nativeHostRunning;
        private set
        {
            if (!Set(ref _nativeHostRunning, value)) return;
            RaiseNativeFlags();
            Raise(nameof(LatencyText));
        }
    }

    public string StateText => HasError ? Localizer.Text("InsertStateProblem") : Bypass ? Localizer.Text("InsertStateBypassed") : Localizer.Text("InsertStateActive");

    /// <summary>Green LED: in the chain and processing. Red otherwise (bypassed or failed).</summary>
    public bool IsActive => !Bypass && !HasError;

    public ObservableCollection<InsertParamViewModel> Params { get; } = [];

    /// <summary>
    /// Controls grouped for the window. LV2 has no control-port grouping in
    /// practice (LSP groups only its audio ports), so groups come from a
    /// name heuristic; a plugin with few controls stays one flat list.
    /// </summary>
    public ObservableCollection<InsertParamGroup> Groups { get; } = [];

    /// <summary>
    /// Build the control view models on first use (the controls window
    /// opening). If the catalog is not here yet, ask for it and build as
    /// soon as it lands.
    /// </summary>
    private bool _paramsRequested;
    public void EnsureParams()
    {
        if (IsDetached) return;
        if (!_paramsRequested)
        {
            _paramsRequested = true;
            BuildParams();
        }
        _owner.EnsurePluginsLoaded();
    }

    private static readonly (string Group, string[] Keys)[] GroupRules =
    [
        ("Display",   ["show", "overlay", "visib", "meter", "graph", "pause", "clear", "zoom", " ui", "display"]),
        ("Sidechain", ["sidechain", "link", "listen"]),
        ("Filter",    ["filter", "frequency", "-pass", "cutoff", "eq ", "equaliz", "band"]),
        ("Dynamics",  ["attack", "release", "threshold", "ratio", "knee", "hold", "hysteresis", "curve", "zone",
                       "reduction", "boost", "compress", "expan", "gate", "limit", "envelope", "lookahead"]),
        ("Levels",    ["gain", "level", "makeup", "dry", "wet", "balance", "mix", "volume", "preamp", "trim", "pan"]),
    ];

    private static readonly string[] GroupOrder = ["General", "Levels", "Dynamics", "Sidechain", "Filter", "Display"];

    private static string GroupFor(string name)
    {
        string n = " " + name.ToLowerInvariant();
        foreach ((string group, string[] keys) in GroupRules)
            if (keys.Any(k => n.Contains(k, StringComparison.Ordinal))) return group;
        return "General";
    }

    private void RebuildGroups()
    {
        Groups.Clear();
        if (Params.Count <= 12)
        {
            Groups.Add(new InsertParamGroup("", false, [.. Params]));
            return;
        }
        var buckets = new Dictionary<string, List<InsertParamViewModel>>();
        foreach (InsertParamViewModel p in Params)
        {
            string g = GroupFor(p.Name);
            if (!buckets.TryGetValue(g, out List<InsertParamViewModel>? l)) buckets[g] = l = [];
            l.Add(p);
        }
        foreach (string g in GroupOrder)
            if (buckets.TryGetValue(g, out List<InsertParamViewModel>? l))
                Groups.Add(new InsertParamGroup(GroupLabel(g), true, l));
    }

    private static string GroupLabel(string group) => group switch
    {
        "Levels" => Localizer.Text("ParameterGroupLevels"),
        "Dynamics" => Localizer.Text("ParameterGroupDynamics"),
        "Sidechain" => Localizer.Text("ParameterGroupSidechain"),
        "Filter" => Localizer.Text("ParameterGroupFilter"),
        "Display" => Localizer.Text("ParameterGroupDisplay"),
        _ => Localizer.Text("ParameterGroupGeneral"),
    };

    public void ApplyFromDaemon(JsonNode ins, string? error, bool nativeHostRunning,
        bool nativeUiBlocked = false, string? nativeUiBlockReason = null)
    {
        Label = ins["label"]?.GetValue<string>() ?? Plugin;
        _nativeUiBlocked = nativeUiBlocked;
        _nativeUiBlockReason = nativeUiBlockReason;
        _bypass = ins["bypass"]?.GetValue<bool>() ?? false;
        _nativeHost = ins["nativeHost"]?.GetValue<bool>() ?? false;
        _ownBlock = ins["ownBlock"]?.GetValue<bool>() ?? false;
        Raise(nameof(NativeHost));
        Raise(nameof(OwnBlock));
        Raise(nameof(Bypass));
        Raise(nameof(StateText));
        Raise(nameof(IsActive));
        RaiseNativeFlags();
        Error = error;
        NativeHostRunning = nativeHostRunning;
        // A late echo must not replace a value still waiting to be sent, or a
        // snapshot taken now would store the old one. Removing entries while
        // enumerating the keys is allowed for Dictionary on .NET.
        foreach (string symbol in _params.Keys)
            if (!SliderSync.RecentlyTouched(_owner.ParameterKey(Id, symbol))) _params.Remove(symbol);
        if (ins["params"] is JsonObject po)
            foreach ((string k, JsonNode? v) in po)
                if (v is not null && !_params.ContainsKey(k)) _params[k] = v.GetValue<double>();
        foreach (InsertParamViewModel p in Params)
        {
            // While a control is being dragged the daemon's echo lags the
            // slider; applying it would make the thumb jitter (the mixer's
            // faders use the same guard).
            if (SliderSync.RecentlyTouched(_owner.ParameterKey(Id, p.Symbol))) continue;
            p.ApplyFromDaemon(_params.GetValueOrDefault(p.Symbol, p.Default));
        }
    }

    /// <summary>Put every control back to the plugin's declared default, live.</summary>
    public void ResetToDefaults()
    {
        EnsureParams();
        foreach (InsertParamViewModel p in Params) p.Value = p.Default;
    }

    private void BuildParams()
    {
        RaiseNativeFlags();
        Params.Clear();
        Groups.Clear();
        if (_owner.ParamsFor(Kind, Plugin) is not JsonArray arr) return;
        foreach (JsonNode? p in arr)
        {
            if (p is null) continue;
            string sym = p["symbol"]!.GetValue<string>();
            var vm = new InsertParamViewModel(this, sym, p["name"]?.GetValue<string>() ?? sym,
                p["min"]?.GetValue<double>() ?? 0, p["max"]?.GetValue<double>() ?? 1,
                p["default"]?.GetValue<double>() ?? 0,
                p["toggled"]?.GetValue<bool>() ?? false, p["integer"]?.GetValue<bool>() ?? false,
                p["logarithmic"]?.GetValue<bool>() ?? false);
            if (_params.TryGetValue(sym, out double cur)) vm.ApplyFromDaemon(cur);
            Params.Add(vm);
        }
        RebuildGroups();
    }

    private readonly HashSet<string> _editedParameters = [];

    /// <summary>Send this insert's queued control values now, ahead of a whole-chain replacement.</summary>
    internal void FlushPendingParameters()
    {
        foreach (string symbol in _editedParameters) SliderSync.Flush(_owner.ParameterKey(Id, symbol));
        ForgetPendingParameters();
    }

    /// <summary>Drop queued values and echo guards, for an insert that left the chain or the connection.</summary>
    internal void ForgetPendingParameters()
    {
        foreach (string symbol in _editedParameters) SliderSync.Forget(_owner.ParameterKey(Id, symbol));
        _editedParameters.Clear();
    }

    /// <summary>Raised when the insert leaves its chain; its preset window closes.</summary>
    internal event Action? Detached;

    internal void Detach()
    {
        if (IsDetached) return;
        IsDetached = true;
        ForgetPendingParameters();
        InsertWindows.CloseControls(this);
        RaiseNativeFlags();
        Detached?.Invoke();
    }

    internal void SendParam(string symbol, double value)
    {
        if (IsDetached) return;
        _editedParameters.Add(symbol);
        _params[symbol] = value;
        _owner.SendParam(this, symbol, value);
    }

    internal object ToPayload()
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = Id,
            ["kind"] = Kind,
            ["plugin"] = Plugin,
            ["label"] = Label,
            ["bypass"] = _bypass,
            ["nativeHost"] = _nativeHost,
            ["params"] = new Dictionary<string, double>(_params),
        };
        // Written only while on, so a chain or a preset of inserts in
        // PipeWire's cycle reads exactly as it did before the switch existed.
        if (_ownBlock) payload["ownBlock"] = true;
        return payload;
    }
}

/// <summary>A titled run of controls in the controls window.</summary>
public sealed record InsertParamGroup(string Name, bool ShowHeader, IReadOnlyList<InsertParamViewModel> Params);

/// <summary>One control port as a slider or switch.</summary>
public sealed class InsertParamViewModel : ViewModelBase
{
    private readonly InsertViewModel _owner;
    private bool _applying;

    public InsertParamViewModel(InsertViewModel owner, string symbol, string name,
        double min, double max, double def, bool toggled, bool integer, bool logarithmic)
    {
        _owner = owner;
        Symbol = symbol;
        Name = name;
        Min = min;
        Max = max;
        Toggled = toggled;
        Integer = integer;
        Logarithmic = logarithmic;
        Default = def;
        _value = def;
    }

    public string Symbol { get; }
    public string Name { get; }
    public double Min { get; }
    public double Max { get; }
    public double Default { get; }
    public bool Toggled { get; }
    public bool Integer { get; }
    public bool Logarithmic { get; }
    public bool IsSlider => !Toggled;
    public double Step => Integer ? 1 : 0;

    private double _value;
    public double Value
    {
        get => _value;
        set
        {
            if (!Set(ref _value, value)) return;
            Raise(nameof(ValueText));
            Raise(nameof(On));
            if (!_applying) _owner.SendParam(Symbol, value);
        }
    }

    /// <summary>Toggle view of the value for switch ports.</summary>
    public bool On
    {
        get => _value >= 0.5;
        set => Value = value ? 1 : 0;
    }

    public string ValueText => Toggled ? (On ? Localizer.Text("ParamOn") : Localizer.Text("ParamOff"))
        : Integer ? ((int)Math.Round(_value)).ToString()
        : Math.Abs(_value) >= 100 ? _value.ToString("0")
        : Math.Abs(_value) >= 10 ? _value.ToString("0.0")
        : _value.ToString("0.000");

    public void ApplyFromDaemon(double v)
    {
        _applying = true;
        try { Value = v; }
        finally { _applying = false; }
    }
}
