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

    internal static bool RelaunchRequested { get; set; }

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
        if (RelaunchRequested) Relaunch(args);
    }

    private static void Relaunch(string[] args)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("The UI executable is unavailable.");
        string[] launchArgs = System.IO.Path.GetFileNameWithoutExtension(executable) == "dotnet"
            ? [typeof(Program).Assembly.Location, .. args] : args;
        // RunInteractiveAsync starts the user-owned child before its first
        // await. The old UI returns only after releasing the instance socket;
        // the child is not tied to this process's cancellation or lifetime.
        var launch = ProcessRunner.RunInteractiveAsync(executable, launchArgs);
        if (launch.IsCompleted) launch.GetAwaiter().GetResult();
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
