using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

/// <summary>A mix's insert chain in its own window: add, reorder, bypass, remove, open controls.</summary>
public partial class MixInsertsWindow : Window
{
    public MixInsertsWindow()
    {
        InitializeComponent();
    }

    private SoundCheckWindow? _soundCheck;
    private void OnSoundCheck(object? sender, RoutedEventArgs e)
    {
        if (Chain is not { CanSoundCheck: true } chain) return;
        if (_soundCheck is not null) { _soundCheck.Activate(); return; }
        _soundCheck = new SoundCheckWindow { DataContext = chain.SoundCheck };
        _soundCheck.Closed += (_, _) => _soundCheck = null;
        _soundCheck.Show(this);
    }

    private InsertsViewModel? Chain => DataContext as InsertsViewModel;

    private async void OnAddInsert(object? sender, RoutedEventArgs e)
    {
        if (Chain is null) return;
        var picker = new PluginPickerWindow { DataContext = Chain };
        PluginChoice? choice = await picker.ShowDialog<PluginChoice?>(this);
        if (choice is not null) Chain.Add(choice);
    }

    private void OnInsertControls(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is InsertViewModel ins) InsertWindows.OpenControls(this, ins);
    }

    private void OnInsertUp(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is InsertViewModel ins) ins.Owner.Move(ins, -1);
    }

    private void OnInsertDown(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is InsertViewModel ins) ins.Owner.Move(ins, +1);
    }

    private void OnInsertRemove(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is InsertViewModel ins) ins.Owner.Remove(ins);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
