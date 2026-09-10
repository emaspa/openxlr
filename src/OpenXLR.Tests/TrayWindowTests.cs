using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class TrayWindowTests
{
    private static bool NativeClose(Window window, WindowCloseReason reason)
        => (bool)typeof(Window).GetMethod("HandleClosing",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, [reason])!;

    // Run in a separate test process under Xvfb: Avalonia owns one UI thread.
    [DesktopFact]
    public void CloseDefersHidingAndMixerCanBeRestoredOrQuit()
    {
        string config = Path.Combine(Path.GetTempPath(), "openxlr-tray-" + Guid.NewGuid());
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string? previousRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        string? previousBus = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
                // Exercise the window lifecycle without registering icons in
                // the developer's real desktop tray.
                Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", "unix:path=/nonexistent");
                Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", config);
                new UiSettings { MinimizeToTray = true }.Save();
                AppBuilder.Configure<App>().UseSkia().UseHarfBuzz().UseX11().SetupWithoutStarting();
                window = new MainWindow();
                window.ShowMixer();
                bool closed = false;
                window.Closed += (_, _) => closed = true;

                for (int i = 0; i < 3; i++)
                {
                    // Exercise the platform's close callback, as the title-bar X does.
                    Assert.True(NativeClose(window, WindowCloseReason.WindowClosing));
                    Assert.True(window.IsVisible);
                    Assert.False(closed);
                    Dispatcher.UIThread.RunJobs();
                    Assert.False(window.IsVisible);
                    Assert.False(closed);
                    window.ShowMixer();
                    Assert.True(window.IsVisible);
                }

                window.WindowState = WindowState.Minimized;
                window.ShowMixer();
                Assert.Equal(WindowState.Normal, window.WindowState);

                // A new launch arriving before the deferred hide keeps the mixer open.
                window.Close();
                window.ShowMixer();
                Dispatcher.UIThread.RunJobs();
                Assert.True(window.IsVisible);

                // Quit wins even when a hide is still queued.
                window.Close();
                window.Quit();
                Dispatcher.UIThread.RunJobs();
                Assert.True(closed);
                Assert.False(window.IsVisible);

                window = new MainWindow();
                window.ShowMixer();
                Assert.False(NativeClose(window, WindowCloseReason.ApplicationShutdown));
                window.ShowMixer();
                Assert.False(window.IsVisible);

                new UiSettings { MinimizeToTray = false }.Save();
                window = new MainWindow();
                window.ShowMixer();
                window.Close();
                Assert.False(window.IsVisible);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                window?.Quit();
                Dispatcher.UIThread.RunJobs();
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
                Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previousRuntime);
                Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", previousBus);
                if (Directory.Exists(config)) Directory.Delete(config, recursive: true);
            }
        }) { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The window lifecycle hung.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

public sealed class DesktopFactAttribute : FactAttribute
{
    public DesktopFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENXLR_TEST_DESKTOP") != "1")
            Skip = "Run separately with OPENXLR_TEST_DESKTOP=1 under xvfb-run, filtered to TrayWindowTests.";
    }
}
