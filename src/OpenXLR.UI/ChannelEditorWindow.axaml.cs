using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OpenXLR.UI;

/// <summary>A focused editor shared by mixer cards and Flow, using the same live send models.</summary>
public partial class ChannelEditorWindow : Window
{
    public ChannelEditorWindow() => InitializeComponent();

    public ChannelEditorWindow(MainViewModel main, ChannelViewModel channel) : this()
    {
        DataContext = channel;
        void Refresh()
        {
            bool exists = main.HasMixer && main.Channels.Contains(channel);
            SendsEditor.IsEnabled = main.DaemonConnected && exists;
            ConnectionNote.Text = !main.DaemonConnected ? "Waiting for the daemon to reconnect…"
                : !exists ? "This channel was removed or the submixer was disabled." : "";
        }
        main.StateApplied += Refresh;
        Closed += (_, _) => main.StateApplied -= Refresh;
        Refresh();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
