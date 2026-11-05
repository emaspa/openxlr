using System;
using Avalonia.Controls;
using Avalonia.Threading;

namespace OpenXLR.UI;

public partial class MainWindow
{
    private WelcomeGuideWindow? _welcomeGuide;

    internal WelcomeGuideWindow OpenWelcomeGuide(bool firstLaunch = false)
    {
        if (_welcomeGuide is { } open)
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return open;
        }

        var guide = new WelcomeGuideWindow(this);
        _welcomeGuide = guide;
        guide.Closing += (_, e) =>
        {
            if (firstLaunch && !_reallyExit && e.CloseReason == WindowCloseReason.WindowClosing
                && (UiSettings.Load() with { WelcomeGuideCompleted = true }).Save() is { } error)
                Console.Error.WriteLine(error);
        };
        guide.Closed += (_, _) => _welcomeGuide = null;
        guide.Show(this);
        return guide;
    }

    internal void ShowGuideMonitor()
    {
        ShowMixer();
        RevealGuideSection(MonitorTile);
        Dispatcher.UIThread.Post(() =>
        {
            if (MonitorDestination.IsEffectivelyVisible) MonitorDestination.Focus();
        }, DispatcherPriority.Loaded);
    }

    internal void ShowGuideMixer()
    {
        ShowMixer();
        if (DetachedMixer is { } detached)
        {
            if (detached.WindowState == WindowState.Minimized) detached.WindowState = WindowState.Normal;
            detached.Activate();
        }
        else RevealGuideSection(SubmixerTile);
    }

    private static void RevealGuideSection(Expander section)
    {
        if (!section.IsEffectivelyVisible) return;
        section.IsExpanded = true;
        section.BringIntoView();
    }
}
