using Avalonia;
using System;
using OpenXLR.UI.Skinning;

namespace OpenXLR.UI;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    /// <summary>The listening single-instance guard, or null in a process that handed off and exits.</summary>
    internal static SingleInstance? Instance { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        if (BrowserNativeHost.TryRun(args)) return;
        if (InstallationCommands.TryRun(args)) return;
        // A window is already open: it shows itself, this process is done.
        Instance = SingleInstance.TryBecomePrimary();
        if (Instance is null) return;
        FlatpakSession.Start();
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally { FlatpakSession.Current?.Dispose(); Instance.Dispose(); }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .ConfigureFonts(BundledFonts.Register)
            .LogToTrace();
}
