using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace OpenXLR.UI;

/// <summary>A plugin the picker offers (mono in / mono out only for the mic path).</summary>
public sealed record PluginChoice(string Uri, string Name, string Category, JsonNode Params)
{
    public override string ToString() => Category.Length > 0 ? $"{Name}  ({Category})" : Name;
}

/// <summary>
/// The insert chain of one channel: what the daemon reports, plus the
/// picker to add more. Edits go to the daemon as a whole new chain (order
/// matters); parameter moves go live one control at a time.
/// </summary>
public sealed class InsertsViewModel : ViewModelBase
{
    private readonly DaemonClient _client;
    private readonly string _channel;
    private readonly int _channels;
    private bool _applying;
    private bool _pluginsRequested;
    private bool _catalogReady;

    /// <summary>
    /// Raised only after the complete catalog has been populated. Emitting
    /// collection changes for each plugin is not a readiness signal: an insert
    /// may appear near the end of a large LV2 installation.
    /// </summary>
    internal event EventHandler? CatalogLoaded;

    /// <param name="channel">Insert key: "xlr1", "xlr2", or "mix:&lt;id&gt;".</param>
    /// <param name="channels">1 for the mono mic path, 2 for a stereo mix.</param>
    public InsertsViewModel(DaemonClient client, string channel, int channels = 1, string? title = null)
    {
        _client = client;
        _channel = channel;
        _channels = channels;
        Title = title ?? channel;
    }

    /// <summary>What the chain belongs to, for window titles ("XLR 1", "Stream mix").</summary>
    public string Title { get; }

    /// <summary>Picker header: which plugins fit this chain.</summary>
    public string PickerHint => _channels == 1
        ? "LV2 plugins that fit the mono mic path (one input, one output)"
        : "LV2 plugins that fit a stereo mix (two inputs, two outputs)";

    public ObservableCollection<InsertViewModel> Items { get; } = [];
    public ObservableCollection<PluginChoice> PluginChoices { get; } = [];

    public bool HasItems => Items.Count > 0;

    /// <summary>One-line state for the strip: count, or a hint when empty.</summary>
    public string Summary => Items.Count switch
    {
        0 => "none",
        1 => "1 plugin",
        int n => $"{n} plugins in chain",
    };

    /// <summary>Label for a compact button that opens the chain window.</summary>
    public string ButtonText => Items.Count == 0 ? "Inserts…" : $"Inserts ({Items.Count})…";

    private PluginChoice? _selectedPlugin;
    public PluginChoice? SelectedPlugin
    {
        get => _selectedPlugin;
        set { if (Set(ref _selectedPlugin, value)) Raise(nameof(CanAdd)); }
    }

    public bool CanAdd => _selectedPlugin is not null;

    private string? _note;
    /// <summary>Picker status: scanning, count, or why nothing is offered.</summary>
    public string? Note { get => _note; private set => Set(ref _note, value); }

    // One catalog fetch per daemon connection, shared by every chain (the
    // XLR strips and all the mixes), so the controls windows can build their
    // sliders from restored state without anyone opening a picker first.
    private static Task<JsonNode?>? _catalogTask;

    private static Task<JsonNode?> CatalogAsync(DaemonClient client)
        => _catalogTask ??= client.RequestPluginsAsync(TimeSpan.FromSeconds(20));

    /// <summary>Fetch the catalog once per connection (lilv's scan can take a moment).</summary>
    public async void EnsurePluginsLoaded()
    {
        if (_pluginsRequested) return;
        _pluginsRequested = true;
        Note = "Scanning LV2 plugins…";
        JsonNode? plugins = await CatalogAsync(_client);
        Dispatcher.UIThread.Post(() => ApplyCatalog(plugins));
    }

    /// <summary>Populate one chain's view of the shared daemon catalog atomically.</summary>
    internal void ApplyCatalog(JsonNode? plugins)
    {
        _catalogReady = false;
        Raise(nameof(CatalogReady));
        PluginChoices.Clear();
        if (plugins is not JsonArray arr)
        {
            Note = "Plugin list unavailable";
            _pluginsRequested = false;
            _catalogTask = null;
            return;
        }
        foreach (JsonNode? p in arr)
        {
            if (p is null) continue;
            // Mono chains take mono in / mono out plugins; stereo chains take
            // plugins with at least two ins and two outs (extra ports stay unlinked).
            int ins = p["audioIns"]?.GetValue<int>() ?? 0, outs = p["audioOuts"]?.GetValue<int>() ?? 0;
            bool fits = _channels == 1 ? ins == 1 && outs == 1 : ins >= 2 && outs >= 2;
            if (!fits) continue;
            PluginChoices.Add(new PluginChoice(
                p["plugin"]!.GetValue<string>(),
                p["name"]?.GetValue<string>() ?? p["plugin"]!.GetValue<string>(),
                p["category"]?.GetValue<string>() ?? "",
                p["params"] ?? new JsonArray()));
        }
        string width = _channels == 1 ? "mono" : "stereo";
        Note = PluginChoices.Count == 0
            ? $"No {width} LV2 plugins found (install e.g. lsp-plugins-lv2 or x42-plugins)"
            : $"{PluginChoices.Count} {width} LV2 plugins available";
        _catalogReady = true;
        Raise(nameof(CatalogReady));
        CatalogLoaded?.Invoke(this, EventArgs.Empty);
    }

    public void ResetForNewConnection()
    {
        _pluginsRequested = false;
        _catalogReady = false;
        Raise(nameof(CatalogReady));
        _catalogTask = null;
    }

    /// <summary>Whether the catalog has arrived for this chain.</summary>
    public bool CatalogReady => _catalogReady;

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
                if (!byId.TryGetValue(id, out InsertViewModel? vm))
                    vm = new InsertViewModel(this, id, ins["plugin"]!.GetValue<string>(), ins["label"]?.GetValue<string>() ?? id);
                vm.ApplyFromDaemon(ins, entry?["error"]?.GetValue<string>());
                next.Add(vm);
            }
            if (!next.SequenceEqual(Items))
            {
                Items.Clear();
                foreach (InsertViewModel vm in next) Items.Add(vm);
                Raise(nameof(HasItems));
                Raise(nameof(Summary));
                Raise(nameof(ButtonText));
            }
        }
        finally { _applying = false; }
    }

    // --- edits, all expressed as a new whole chain ---

    public void Add() { if (_selectedPlugin is not null) Add(_selectedPlugin); }

    public void Add(PluginChoice plugin)
    {
        var chain = Snapshot();
        chain.Add(new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid().ToString("N")[..8],
            ["kind"] = "lv2",
            ["plugin"] = plugin.Uri,
            ["label"] = plugin.Name,
            ["bypass"] = false,
            ["params"] = new Dictionary<string, double>(),
        });
        _ = _client.SetInsertsAsync(_channel, chain);
    }

    public void Remove(InsertViewModel item)
        => _ = _client.SetInsertsAsync(_channel, Snapshot(skip: item.Id));

    public void Move(InsertViewModel item, int delta)
    {
        int i = Items.IndexOf(item);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= Items.Count) return;
        var order = Items.ToList();
        (order[i], order[j]) = (order[j], order[i]);
        _ = _client.SetInsertsAsync(_channel, Snapshot(order));
    }

    internal void SendBypass(InsertViewModel item, bool bypass)
    {
        if (!_applying) _ = _client.SetInsertBypassAsync(_channel, item.Id, bypass);
    }

    internal void SendParam(InsertViewModel item, string symbol, double value)
    {
        if (_applying) return;
        string key = $"ins:{item.Id}:{symbol}";
        SliderSync.Touch(key);
        SliderSync.Send(key, () => _ = _client.SetInsertParamAsync(_channel, item.Id, symbol, value));
    }

    internal bool Applying => _applying;

    /// <summary>The current chain as the daemon wants it, minus an optional id.</summary>
    private List<object> Snapshot(IEnumerable<InsertViewModel>? order = null, string? skip = null)
        => [.. (order ?? Items).Where(i => i.Id != skip).Select(i => (object)i.ToPayload())];

    /// <summary>Parameter metadata for a plugin uri, from the catalog.</summary>
    internal JsonNode? ParamsFor(string uri) => PluginChoices.FirstOrDefault(p => p.Uri == uri)?.Params;
}

public sealed class InsertViewModel : ViewModelBase
{
    private readonly InsertsViewModel _owner;
    private bool _paramsBuilt;
    private bool _waitingForCatalog;

    public InsertViewModel(InsertsViewModel owner, string id, string plugin, string label)
    {
        _owner = owner;
        Id = id;
        Plugin = plugin;
        Label = label;
    }

    public string Id { get; }
    public string Plugin { get; }
    public string Label { get; }

    /// <summary>The channel chain this insert belongs to (row buttons route through it).</summary>
    public InsertsViewModel Owner => _owner;

    private readonly Dictionary<string, double> _params = [];

    private bool _bypass;
    public bool Bypass
    {
        get => _bypass;
        set { if (Set(ref _bypass, value)) { Raise(nameof(StateText)); Raise(nameof(IsActive)); _owner.SendBypass(this, value); } }
    }

    private string? _error;
    public string? Error
    {
        get => _error;
        private set { if (Set(ref _error, value)) { Raise(nameof(HasError)); Raise(nameof(StateText)); Raise(nameof(IsActive)); } }
    }
    public bool HasError => _error is not null;

    public string StateText => HasError ? "problem" : Bypass ? "bypassed" : "active";

    /// <summary>Green LED: in the chain and processing. Red otherwise (bypassed or failed).</summary>
    public bool IsActive => !Bypass && !HasError;

    public ObservableCollection<InsertParamViewModel> Params { get; } = [];
    public bool HasParams => Params.Count > 0;
    public string ControlsNote => !_paramsBuilt ? "Reading LV2 control metadata…"
        : HasParams ? $"{Params.Count} configurable controls"
        : "This plugin declares no configurable input control ports.";

    /// <summary>
    /// Controls grouped for the window. Many plugins, including LSP, group
    /// their audio ports but leave control ports ungrouped, so the portable
    /// fallback is a name heuristic; a plugin with few controls stays flat.
    /// </summary>
    public ObservableCollection<InsertParamGroup> Groups { get; } = [];

    /// <summary>
    /// Build the control view models on first use (the controls window
    /// opening). If the catalog is not here yet, ask for it and build as
    /// soon as it lands.
    /// </summary>
    public void EnsureParams()
    {
        if (_paramsBuilt) return;
        if (_owner.CatalogReady) { BuildParams(); return; }
        if (_waitingForCatalog) return;
        _waitingForCatalog = true;
        _owner.CatalogLoaded += OnCatalogLoaded;
        _owner.EnsurePluginsLoaded();
    }

    private void OnCatalogLoaded(object? sender, EventArgs e)
    {
        if (!_owner.CatalogReady || _paramsBuilt) return;
        _owner.CatalogLoaded -= OnCatalogLoaded;
        _waitingForCatalog = false;
        BuildParams();
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
                Groups.Add(new InsertParamGroup(g, true, l));
    }

    public void ApplyFromDaemon(JsonNode ins, string? error)
    {
        _bypass = ins["bypass"]?.GetValue<bool>() ?? false;
        Raise(nameof(Bypass));
        Raise(nameof(StateText));
        Raise(nameof(IsActive));
        Error = error;
        _params.Clear();
        if (ins["params"] is JsonObject po)
            foreach ((string k, JsonNode? v) in po)
                if (v is not null) _params[k] = v.GetValue<double>();
        foreach (InsertParamViewModel p in Params)
        {
            // While a control is being dragged the daemon's echo lags the
            // slider; applying it would make the thumb jitter (the mixer's
            // faders use the same guard).
            if (SliderSync.RecentlyTouched($"ins:{Id}:{p.Symbol}")) continue;
            if (_params.TryGetValue(p.Symbol, out double v)) p.ApplyFromDaemon(v);
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
        if (_paramsBuilt) return;
        _paramsBuilt = true;
        if (_owner.ParamsFor(Plugin) is not JsonArray arr)
        {
            Raise(nameof(HasParams));
            Raise(nameof(ControlsNote));
            return;
        }
        foreach (JsonNode? p in arr)
        {
            if (p is null) continue;
            string sym = p["symbol"]!.GetValue<string>();
            var options = new List<PluginParamOption>();
            if (p["scalePoints"] is JsonArray points)
                foreach (JsonNode? point in points)
                    if (point is not null)
                        options.Add(new PluginParamOption(
                            point["label"]?.GetValue<string>() ?? "",
                            point["value"]?.GetValue<double>() ?? 0));
            var vm = new InsertParamViewModel(this, sym, p["name"]?.GetValue<string>() ?? sym,
                p["min"]?.GetValue<double>() ?? 0, p["max"]?.GetValue<double>() ?? 1,
                p["default"]?.GetValue<double>() ?? 0,
                p["toggled"]?.GetValue<bool>() ?? false, p["integer"]?.GetValue<bool>() ?? false,
                p["logarithmic"]?.GetValue<bool>() ?? false,
                p["enumeration"]?.GetValue<bool>() ?? false, options,
                p["unitSymbol"]?.GetValue<string>());
            if (_params.TryGetValue(sym, out double cur)) vm.ApplyFromDaemon(cur);
            Params.Add(vm);
        }
        RebuildGroups();
        Raise(nameof(HasParams));
        Raise(nameof(ControlsNote));
    }

    internal void SendParam(string symbol, double value)
    {
        _params[symbol] = value;
        _owner.SendParam(this, symbol, value);
    }

    internal object ToPayload() => new Dictionary<string, object?>
    {
        ["id"] = Id,
        ["kind"] = "lv2",
        ["plugin"] = Plugin,
        ["label"] = Label,
        ["bypass"] = _bypass,
        ["params"] = new Dictionary<string, double>(_params),
    };
}

/// <summary>A titled run of controls in the controls window.</summary>
public sealed record InsertParamGroup(string Name, bool ShowHeader, IReadOnlyList<InsertParamViewModel> Params);

/// <summary>One named value of an LV2 enumeration control.</summary>
public sealed record PluginParamOption(string Label, double Value)
{
    public override string ToString() => Label;
}

/// <summary>One LV2 control port represented as a slider, switch, or selector.</summary>
public sealed class InsertParamViewModel : ViewModelBase
{
    private readonly InsertViewModel _owner;
    private bool _applying;

    public InsertParamViewModel(InsertViewModel owner, string symbol, string name,
        double min, double max, double def, bool toggled, bool integer, bool logarithmic,
        bool enumeration, IReadOnlyList<PluginParamOption> options, string? unitSymbol)
    {
        _owner = owner;
        Symbol = symbol;
        Name = name;
        Min = min;
        Max = max;
        Toggled = toggled;
        Integer = integer;
        Logarithmic = logarithmic;
        Enumeration = enumeration;
        Options = [.. options.OrderBy(o => o.Value)];
        UnitSymbol = unitSymbol;
        Default = Normalize(def);
        _value = Default;
    }

    public string Symbol { get; }
    public string Name { get; }
    public double Min { get; }
    public double Max { get; }
    public double Default { get; }
    public bool Toggled { get; }
    public bool Integer { get; }
    public bool Logarithmic { get; }
    public bool Enumeration { get; }
    public string? UnitSymbol { get; }
    public IReadOnlyList<PluginParamOption> Options { get; }
    public bool IsEnumeration => !Toggled && Enumeration && Options.Count > 0;
    public bool IsSlider => !Toggled && !IsEnumeration;
    public bool UsesLogarithmicScale => IsSlider && Logarithmic && Max > Min;
    public double SliderMinimum => UsesLogarithmicScale ? 0 : Min;
    public double SliderMaximum => UsesLogarithmicScale ? 1 : Max;
    public double SliderStep => Integer && !UsesLogarithmicScale ? 1 : 0;

    /// <summary>
    /// Slider-facing value. Logarithmic ports use a normalized position while
    /// Value always remains the real number sent to PipeWire.
    /// </summary>
    public double SliderValue
    {
        get => ToSliderPosition(_value, Min, Max, UsesLogarithmicScale);
        set => Value = FromSliderPosition(value, Min, Max, UsesLogarithmicScale);
    }

    private double _value;
    public double Value
    {
        get => _value;
        set
        {
            value = Normalize(value);
            if (!Set(ref _value, value)) return;
            Raise(nameof(ValueText));
            Raise(nameof(On));
            Raise(nameof(SelectedOption));
            Raise(nameof(SliderValue));
            if (!_applying) _owner.SendParam(Symbol, value);
        }
    }

    /// <summary>Toggle view of the value for switch ports.</summary>
    public bool On
    {
        get => _value >= 0.5;
        set => Value = value ? 1 : 0;
    }

    public PluginParamOption? SelectedOption
    {
        get => Options.Count == 0 ? null : Options.MinBy(o => Math.Abs(o.Value - _value));
        set { if (value is not null) Value = value.Value; }
    }

    public string ValueText
    {
        get
        {
            if (Toggled) return On ? "on" : "off";
            if (IsEnumeration) return SelectedOption?.Label ?? FormatNumber(_value);
            string value = FormatNumber(_value);
            return string.IsNullOrWhiteSpace(UnitSymbol) ? value : $"{value} {UnitSymbol}";
        }
    }

    public string Description => $"{Symbol} • range {FormatNumber(Min)} to {FormatNumber(Max)}" +
        $" • default {FormatNumber(Default)}" +
        (string.IsNullOrWhiteSpace(UnitSymbol) ? "" : $" {UnitSymbol}");

    private string FormatNumber(double value) => Integer ? ((int)Math.Round(value)).ToString(CultureInfo.CurrentCulture)
        : Math.Abs(value) >= 100 ? value.ToString("0", CultureInfo.CurrentCulture)
        : Math.Abs(value) >= 10 ? value.ToString("0.0", CultureInfo.CurrentCulture)
        : value.ToString("0.###", CultureInfo.CurrentCulture);

    private double Normalize(double value)
    {
        if (!double.IsFinite(value)) return Default;
        double low = Math.Min(Min, Max), high = Math.Max(Min, Max);
        value = Math.Clamp(value, low, high);
        if (Toggled) return value >= 0.5 ? 1 : 0;
        if (IsEnumeration) return Options.MinBy(o => Math.Abs(o.Value - value))!.Value;
        if (Integer) value = Math.Round(value);
        return Math.Clamp(value, low, high);
    }

    internal static double ToSliderPosition(double value, double min, double max, bool logarithmic)
    {
        if (!logarithmic || max <= min) return value;
        value = Math.Clamp(value, min, max);
        if (min > 0)
            return Math.Log(value / min) / Math.Log(max / min);
        double normalized = (value - min) / (max - min);
        return Math.Cbrt(normalized);
    }

    internal static double FromSliderPosition(double position, double min, double max, bool logarithmic)
    {
        if (!logarithmic || max <= min) return Math.Clamp(position, Math.Min(min, max), Math.Max(min, max));
        position = Math.Clamp(position, 0, 1);
        if (min > 0) return min * Math.Pow(max / min, position);
        return min + (max - min) * position * position * position;
    }

    public void ApplyFromDaemon(double v)
    {
        _applying = true;
        try { Value = v; }
        finally { _applying = false; }
    }
}
