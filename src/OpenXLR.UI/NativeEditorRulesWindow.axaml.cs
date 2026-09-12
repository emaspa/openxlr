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
    public string Summary => (Blocked ? "OpenXLR controls" : "Native editor allowed")
        + (Override is null ? " · Release default" : " · Your override");
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
    private bool _busy, _closed, _hasError;
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
        Closed += (_, _) => { _closed = true; client.NativeEditorRulesChanged -= OnRulesChanged; };
    }

    private void OnRulesChanged() => Dispatcher.UIThread.Post(async () =>
    {
        if (!_closed && !_busy) await LoadAsync();
    });

    private async Task LoadAsync()
    {
        if (_client is null) return;
        _busy = true;
        UpdateButtons();
        try
        {
            ApplyRules(await _client.RequestNativeEditorRulesAsync(TimeSpan.FromSeconds(20)));
            JsonNode? catalogue = await _client.RequestPluginsAsync(TimeSpan.FromSeconds(20));
            _plugins.Clear();
            foreach (JsonNode p in (catalogue as JsonArray ?? []).OfType<JsonNode>()
                .Where(p => p["nativeEditorSupported"]?.GetValue<bool>() == true || p["nativeEditorAvailable"]?.GetValue<bool>() == true)
                .OrderBy(p => p["name"]?.GetValue<string>(), StringComparer.OrdinalIgnoreCase))
                _plugins.Add(new(p["kind"]?.GetValue<string>() ?? "lv2", p["plugin"]!.GetValue<string>(),
                    p["name"]?.GetValue<string>() ?? p["plugin"]!.GetValue<string>()));
            if (catalogue is null && !_hasError) Status.Text = "The plugin list is unavailable. Existing rules can still be edited.";
        }
        catch (Exception ex) { _hasError = true; Status.Text = ex.Message; }
        finally { _busy = false; UpdateButtons(); }
    }

    internal void ApplyRules(JsonNode? reply)
    {
        var selected = Selected;
        _rules.Clear();
        _hasError = reply is null || reply["error"]?.GetValue<string>() is { Length: > 0 };
        Status.Text = reply is null ? "The daemon did not return editor rules. Update or restart it if needed."
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
                + "\nRelease default: " + (rule.DefaultBlocked ? "OpenXLR controls" : "native editor allowed")
            : "Select a rule to change it, or find a plugin below to add one.";
    }

    private void OnRuleSelected(object? sender, SelectionChangedEventArgs e) => UpdateButtons();
    private void OnPluginSelected(object? sender, SelectionChangedEventArgs e) => UpdateButtons();

    private async Task SetRuleAsync(string kind, string plugin, string name, bool? blocked)
    {
        if (_client is null || _busy) return;
        _busy = true;
        UpdateButtons();
        try
        {
            ApplyRules(await _client.SetNativeEditorRuleAsync(kind, plugin, name, blocked, TimeSpan.FromSeconds(20)));
            RuleList.SelectedItem = _rules.FirstOrDefault(r => r.Kind == kind && r.Plugin == plugin);
        }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { _busy = false; UpdateButtons(); }
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
        if (!await Dialogs.ConfirmAsync(this, "Allow this native editor?",
            $"Allow {rule.Name} to open its own editor again? If the compatibility issue remains, its window may freeze or its plugin process may crash. Your choice will override release defaults.", "Allow editor")) return;
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
