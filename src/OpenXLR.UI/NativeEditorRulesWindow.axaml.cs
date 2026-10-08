using OpenXLR.UI.Localization;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace OpenXLR.UI;

public sealed record NativeEditorRuleRow(string Kind, string Plugin, string Name, string Reason,
    bool DefaultBlocked, bool? Override, bool Blocked)
{
    public string Label => $"{Name} ({Kind.ToUpperInvariant()})";
    public string Summary => (Blocked, Override is null) switch
    {
        (true, true) => Localizer.Text("RuleControlsReleaseDefault"),
        (true, false) => Localizer.Text("RuleControlsYourOverride"),
        (false, true) => Localizer.Text("RuleEditorReleaseDefault"),
        (false, false) => Localizer.Text("RuleEditorYourOverride"),
    };
}

public sealed record NativeEditorPluginChoice(string Kind, string Plugin, string Name)
{
    public override string ToString() => $"{Name} ({Kind.ToUpperInvariant()})";
}

public partial class NativeEditorRulesWindow : Window
{
    private readonly ObservableCollection<NativeEditorRuleRow> _rules = [];
    private readonly ObservableCollection<NativeEditorPluginChoice> _plugins = [];
    private readonly DaemonClient? _client;
    private readonly (string Kind, string Plugin)? _initialSelection;
    private bool _busy, _closed, _hasError, _reloadPending;
    private int _connectionGeneration;
    private NativeEditorRuleRow? Selected => RuleList.SelectedItem as NativeEditorRuleRow;

    public NativeEditorRulesWindow()
    {
        InitializeComponent();
        RuleList.ItemsSource = _rules;
        PluginPicker.ItemsSource = _plugins;
        Opened += (_, _) =>
        {
            var screen = Screens.ScreenFromWindow(this);
            if (screen is null) return;
            double height = screen.WorkingArea.Height / screen.Scaling - 60;
            if (height > 300) { MinHeight = Math.Min(MinHeight, height); MaxHeight = height; }
        };
    }

    public NativeEditorRulesWindow(DaemonClient client, string? kind = null, string? plugin = null) : this()
    {
        _client = client;
        if (kind is not null && plugin is not null) _initialSelection = (kind, plugin);
        Opened += async (_, _) => await LoadAsync();
        client.NativeEditorRulesChanged += OnRulesChanged;
        client.ConnectionChanged += OnConnectionChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            client.NativeEditorRulesChanged -= OnRulesChanged;
            client.ConnectionChanged -= OnConnectionChanged;
        };
    }

    private void OnRulesChanged() => Dispatcher.UIThread.Post(async () =>
    {
        await LoadAsync();
    });

    private void OnConnectionChanged(bool connected) => Dispatcher.UIThread.Post(() =>
    {
        if (_closed) return;
        _connectionGeneration++;
        if (connected) OnRulesChanged();
        else
        {
            _hasError = true;
            Status.Text = Localizer.Text("RulesDisconnected");
            UpdateButtons();
        }
    });

    private void FinishOperation()
    {
        _busy = false;
        if (_closed) return;
        UpdateButtons();
        if (_reloadPending) OnRulesChanged();
    }

    private async Task LoadAsync()
    {
        if (_client is null || _closed) return;
        if (_busy) { _reloadPending = true; return; }
        _reloadPending = false;
        int generation = _connectionGeneration;
        _busy = true;
        UpdateButtons();
        try
        {
            JsonNode? reply = await _client.RequestNativeEditorRulesAsync(TimeSpan.FromSeconds(20));
            if (_closed || generation != _connectionGeneration) return;
            ApplyRules(reply);
            JsonNode? catalogue = await _client.RequestPluginsAsync(TimeSpan.FromSeconds(20));
            if (_closed || generation != _connectionGeneration) return;
            _plugins.Clear();
            foreach (JsonNode p in (catalogue as JsonArray ?? []).OfType<JsonNode>()
                .Where(p => p["nativeEditorSupported"]?.GetValue<bool>() == true || p["nativeEditorAvailable"]?.GetValue<bool>() == true)
                .OrderBy(p => p["name"]?.GetValue<string>(), StringComparer.OrdinalIgnoreCase))
                _plugins.Add(new(p["kind"]?.GetValue<string>() ?? "lv2", p["plugin"]!.GetValue<string>(),
                    p["name"]?.GetValue<string>() ?? p["plugin"]!.GetValue<string>()));
            if (catalogue is null && !_hasError) Status.Text = Localizer.Text("RulesPluginListUnavailable");
        }
        catch (Exception ex)
        {
            if (!_closed && generation == _connectionGeneration) { _hasError = true; Status.Text = ex.Message; }
        }
        finally { FinishOperation(); }
    }

    internal void ApplyRules(JsonNode? reply)
    {
        var selected = Selected;
        _rules.Clear();
        _hasError = reply is null || reply["error"]?.GetValue<string>() is { Length: > 0 };
        Status.Text = reply is null ? Localizer.Text("RulesNoAnswer")
            : reply["error"]?.GetValue<string>() ?? "";
        foreach (JsonNode row in (reply?["rules"] as JsonArray ?? []).OfType<JsonNode>())
            _rules.Add(new(row["kind"]!.GetValue<string>(), row["plugin"]!.GetValue<string>(),
                row["name"]!.GetValue<string>(), row["reason"]?.GetValue<string>() ?? "",
                row["defaultBlocked"]!.GetValue<bool>(), row["override"]?.GetValue<bool>(), row["blocked"]!.GetValue<bool>()));
        string? kind = selected?.Kind ?? _initialSelection?.Kind;
        string? plugin = selected?.Plugin ?? _initialSelection?.Plugin;
        RuleList.SelectedItem = _rules.FirstOrDefault(r => r.Kind == kind && r.Plugin == plugin);
        EmptyRules.IsVisible = _rules.Count == 0;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool ready = !_busy && !_hasError;
        RuleList.IsEnabled = PluginPicker.IsEnabled = ready;
        BlockSelected.IsEnabled = ready && Selected is { Blocked: false };
        AllowSelected.IsEnabled = ready && Selected is { Blocked: true };
        DefaultSelected.IsEnabled = ready && Selected?.Override is not null;
        AddRule.IsEnabled = ready && PluginPicker.SelectedItem is NativeEditorPluginChoice choice
            && !_rules.Any(r => r.Kind == choice.Kind && r.Plugin == choice.Plugin && r.Blocked);
        RuleDetail.Text = Selected is { } rule
            ? rule.Reason + "\n" + rule.Kind.ToUpperInvariant() + " · " + rule.Plugin
                + "\n" + (rule.DefaultBlocked ? Localizer.Text("ReleaseDefaultControls") : Localizer.Text("ReleaseDefaultEditor"))
            : Localizer.Text("SelectARule");
    }

    private void OnRuleSelected(object? sender, SelectionChangedEventArgs e) => UpdateButtons();
    private void OnPluginSelected(object? sender, SelectionChangedEventArgs e) => UpdateButtons();

    private async Task SetRuleAsync(string kind, string plugin, string name, bool? blocked)
    {
        if (_client is null || _closed || _busy) return;
        int generation = _connectionGeneration;
        _busy = true;
        UpdateButtons();
        try
        {
            JsonNode? reply = await _client.SetNativeEditorRuleAsync(kind, plugin, name, blocked, TimeSpan.FromSeconds(20));
            if (_closed || generation != _connectionGeneration) return;
            ApplyRules(reply);
            RuleList.SelectedItem = _rules.FirstOrDefault(r => r.Kind == kind && r.Plugin == plugin);
        }
        catch (Exception ex)
        {
            if (!_closed && generation == _connectionGeneration) Status.Text = ex.Message;
        }
        finally { FinishOperation(); }
    }

    private async void OnAddRule(object? sender, RoutedEventArgs e)
    {
        if (PluginPicker.SelectedItem is NativeEditorPluginChoice plugin)
            await SetRuleAsync(plugin.Kind, plugin.Plugin, plugin.Name, true);
    }

    private async void OnBlockSelected(object? sender, RoutedEventArgs e)
    {
        if (Selected is { } rule) await SetRuleAsync(rule.Kind, rule.Plugin, rule.Name, true);
    }

    private async void OnAllowSelected(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } rule) return;
        if (!await Dialogs.ConfirmAsync(this, Localizer.Text("AllowNativeEditorTitle"),
            Localizer.Format("AllowNativeEditorDetail", rule.Name), Localizer.Text("AllowEditor"))) return;
        await SetRuleAsync(rule.Kind, rule.Plugin, rule.Name, false);
    }

    private async void OnDefaultSelected(object? sender, RoutedEventArgs e)
    {
        if (Selected is { } rule) await SetRuleAsync(rule.Kind, rule.Plugin, rule.Name, null);
    }

    private async void OnRefresh(object? sender, RoutedEventArgs e)
    {
        if (!_busy) await LoadAsync();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
