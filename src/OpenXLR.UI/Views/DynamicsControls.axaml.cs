using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace OpenXLR.UI.Views;

/// <summary>A microphone's expander and compressor switches. Its data context is a <see cref="ChannelDynamicsViewModel"/>.</summary>
public partial class DynamicsControls : UserControl
{
    public DynamicsControls()
    {
        InitializeComponent();
    }

    private void OnOpenExpander(object? sender, RoutedEventArgs e) => Open((DataContext as ChannelDynamicsViewModel)?.Expander);

    private void OnOpenCompressor(object? sender, RoutedEventArgs e) => Open((DataContext as ChannelDynamicsViewModel)?.Compressor);

    private void Open(MicDynamicsViewModel? processor)
    {
        if (processor is null || OwnerWindow() is not { } owner) return;
        DynamicsWindows.Show(owner, processor);
    }

    /// <summary>
    /// The window the controls are part of. In a flyout they sit in a popup,
    /// whose logical parent is the button that opened it.
    /// </summary>
    private Window? OwnerWindow()
        => TopLevel.GetTopLevel(this) as Window ?? this.FindLogicalAncestorOfType<Window>();
}
