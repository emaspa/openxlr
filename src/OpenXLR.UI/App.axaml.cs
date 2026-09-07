using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace OpenXLR.UI;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // An earlier version wrote a user unit with a build-tree path on
            // packaged installs; fix it before the user has to notice.
            if (UiSettings.Load().StartDaemonAtLogin)
                StartupIntegration.RepairDaemonUnit();

            var window = new MainWindow();
            // A later launch asks for the window through the single-instance
            // socket: bring it up, from the tray or from behind other windows.
            if (Program.Instance is { } instance)
                instance.ShowRequested = () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    window.Show();
                    if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                    window.Activate();
                });
            if (window.StartsHidden)
            {
                // Starting in the tray means the window must never be
                // mapped: showing it and hiding it a moment later leaves a
                // hollow frame on some compositors (KDE on Wayland, issue
                // #11). The lifetime shows MainWindow after this method, so
                // the window is simply not registered as MainWindow; the
                // tray shows it on demand and Quit shuts the app down.
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            }
            else
            {
                desktop.MainWindow = window;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
