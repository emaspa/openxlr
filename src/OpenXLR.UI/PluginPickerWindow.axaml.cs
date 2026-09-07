using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

/// <summary>
/// Modal picker over the daemon's LV2 catalog. Closes with the chosen
/// <see cref="PluginChoice"/>, or null when cancelled.
/// </summary>
public partial class PluginPickerWindow : Window
{
    public PluginPickerWindow()
    {
        InitializeComponent();
        Filter.TextChanged += (_, _) => Refresh();
        Opened += (_, _) =>
        {
            if (DataContext is InsertsViewModel vm)
            {
                vm.EnsurePluginsLoaded();
                vm.PluginChoices.CollectionChanged += OnCatalogChanged;
            }
            Refresh();
            Filter.Focus();
        };
        Closed += (_, _) =>
        {
            if (DataContext is InsertsViewModel vm) vm.PluginChoices.CollectionChanged -= OnCatalogChanged;
        };
    }

    private void OnCatalogChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void OnFormatToggled(object? sender, RoutedEventArgs e) => Refresh();

    /// <summary>
    /// The list is what matches the search, of the formats whose buttons are
    /// pressed. No button pressed means every format, so the buttons narrow
    /// the list and never empty it by default.
    /// </summary>
    public static IEnumerable<PluginChoice> Matching(IEnumerable<PluginChoice> choices, string query,
        bool lv2, bool clap, bool vst3 = false)
    {
        string q = query.Trim();
        bool any = !lv2 && !clap && !vst3;
        return choices.Where(p =>
            (any || lv2 && p.Kind == "lv2" || clap && p.Kind == "clap" || vst3 && p.Kind == "vst3") &&
            (q.Length == 0 ||
             p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
             p.Category.Contains(q, StringComparison.OrdinalIgnoreCase) ||
             p.Format.Contains(q, StringComparison.OrdinalIgnoreCase)));
    }

    private void Refresh()
    {
        if (DataContext is not InsertsViewModel vm) return;
        List.ItemsSource = Matching(vm.PluginChoices, Filter.Text ?? "",
            OnlyLv2.IsChecked == true, OnlyClap.IsChecked == true, OnlyVst3.IsChecked == true).ToList();
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
        => AddButton.IsEnabled = List.SelectedItem is PluginChoice;

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (List.SelectedItem is PluginChoice p) Close(p);
    }

    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is PluginChoice p) Close(p);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
