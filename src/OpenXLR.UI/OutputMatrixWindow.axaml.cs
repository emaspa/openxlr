using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

public partial class OutputMatrixWindow : Window
{
    public OutputMatrixWindow() => InitializeComponent();

    private const string Manual =
        "https://github.com/emaspa/openxlr/blob/main/docs/manual.md#output-matrix";

    private void OnManual(object? sender, RoutedEventArgs e) => ExternalLink.Open(Manual);
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
