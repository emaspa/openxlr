using Avalonia;
using System;

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
        // A window is already open: it shows itself, this process is done.
        Instance = SingleInstance.TryBecomePrimary();
        if (Instance is null) return;
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally { Instance.Dispose(); }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
