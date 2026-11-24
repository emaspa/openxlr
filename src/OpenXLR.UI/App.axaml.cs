using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace OpenXLR.UI;

public partial class App : Application
{
    private bool _showWelcomeGuide;

    public override void Initialize()
    {
        // Read before the skin publishes the Deck palette, which every earlier
        // UI wrote on its first start.
        _showWelcomeGuide = UiSettings.PrepareWelcomeGuide();
        // The language is chosen before any markup reads its text.
        Localization.Localizer.Initialize();
        AvaloniaXamlLoader.Load(this);
        // The appearance is in the resources before anything can be built, so
        // no window is ever drawn unskinned and repainted a moment later.
        Skinning.SkinService.Initialize();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // An exception nothing caught is logged rather than ending the UI.
            UiErrors.Install();

            // An earlier version wrote a user unit with a build-tree path on
            // packaged installs; fix it before the user has to notice.
            if (!FlatpakEnvironment.IsFlatpak && UiSettings.Load().StartDaemonAtLogin)
                StartupIntegration.RepairDaemonUnit();

            // A moved installation must not leave an enabled mixer-at-login
            // preference pointing at a binary that is gone. An entry removed
            // with a desktop tool stays removed. Either way the outcome is
            // kept, and Options reports anything the user should know.
            if (!FlatpakEnvironment.IsFlatpak) StartupIntegration.RepairWindowAutostart();

            // A skin rewritten on disk, by hand or by a theme tool, is worn
            // again without pressing Reload.
            Skinning.SkinService.FollowSkinFolders();

            var window = new MainWindow(new DaemonClient(), syncDesktopVolume: true, showWelcomeGuide: _showWelcomeGuide);
            // A later launch asks for the window through the single-instance
            // socket: bring it up, from the tray or from behind other windows.
            if (Program.Instance is { } instance)
                instance.ShowRequested = () => Avalonia.Threading.Dispatcher.UIThread.Post(window.ShowMixer);
            UseLifetime(desktop, window);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Hand the window to the lifetime. The UI ends only when the window's
    /// Closed handler has finished its teardown and asks for the shutdown
    /// itself, whichever way it started: with the lifetime ending at the
    /// last window's close instead, the dispatcher stops at the handler's
    /// first await and drops the rest, push to talk's later mutes included.
    /// </summary>
    internal static void UseLifetime(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
    {
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // Starting in the tray means the window must never be mapped:
        // showing it and hiding it a moment later leaves a hollow frame on
        // some compositors (KDE on Wayland, issue #11). The lifetime shows
        // MainWindow after OnFrameworkInitializationCompleted, so the window
        // is simply not registered as MainWindow; the tray shows it on demand.
        if (!window.StartsHidden) desktop.MainWindow = window;
    }
}
