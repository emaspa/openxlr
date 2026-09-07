using System;
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

    private void Refresh()
    {
        if (DataContext is not InsertsViewModel vm) return;
        string q = (Filter.Text ?? "").Trim();
        List.ItemsSource = string.IsNullOrEmpty(q)
            ? vm.PluginChoices.ToList()
            : vm.PluginChoices.Where(p =>
                p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                p.Category.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                p.Format.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
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
