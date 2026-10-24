using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI.Views;

/// <summary>The browser tab routing option and tab rows shown in the Browser tabs window.</summary>
public partial class BrowserTabsView : UserControl
{
    public BrowserTabsView() => InitializeComponent();

    private void OnForgetWebsite(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is BrowserTabViewModel tab) tab.ForgetWebsite();
    }

    private void OnActivate(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is BrowserTabViewModel tab) tab.Activate();
    }
}
