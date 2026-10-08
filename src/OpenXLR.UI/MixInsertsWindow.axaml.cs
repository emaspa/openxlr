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

    private EffectWorkflowWindow? _workflow;

    private void OnWorkflow(object? sender, RoutedEventArgs e)
    {
        if (Chain is null) return;
        if (_workflow is not null) { _workflow.Activate(); return; }
        _workflow = new EffectWorkflowWindow { DataContext = Chain };
        _workflow.Closed += (_, _) => _workflow = null;
        _workflow.Show(this);
    }

    private void OnCopyInsert(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is InsertViewModel insert) insert.Owner.CopyEffects(insert);
    }

    private async void OnRenameInsert(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not InsertViewModel insert) return;
        string? name = await Dialogs.NameAsync(this, $"Rename effect '{insert.Label}'", insert.Label,
            "Only the name changes. The plugin, its settings and the audio keep running.", 256);
        if (name is not null && name != insert.Label) await insert.Owner.RenameEffectAsync(insert, name);
    }

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
