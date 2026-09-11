using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class TrayWindowTests
{
    /// <summary>
    /// The entry point the X11 backend calls from its event loop when the
    /// window manager asks the window to close. Used for the reasons no
    /// window manager can send, such as a session shutdown.
    /// </summary>
    private static bool NativeClose(Window window, WindowCloseReason reason)
        => (bool)typeof(Window).GetMethod("HandleClosing",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, [reason])!;

    /// <summary>Run the platform loop briefly, so X11 events and posted jobs are delivered.</summary>
    private static void Pump(TimeSpan duration)
    {
        using var stop = new CancellationTokenSource();
        DispatcherTimer.RunOnce(stop.Cancel, duration);
        Dispatcher.UIThread.MainLoop(stop.Token);
    }

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
                window = NewWindow();
                window.ShowMixer();
                bool closed = false;
                window.Closed += (_, _) => closed = true;

                // The window's own Closing handler runs first, so this one
                // sees what it left behind when the native callback returns.
                bool? visibleWhenClosingReturned = null;
                window.Closing += (_, _) => visibleWhenClosingReturned = window.IsVisible;

                // The window manager's close request, as the title bar X sends
                // it: a real WM_DELETE_WINDOW client message through the X11
                // event loop, not a direct call into the window.
                Pump(TimeSpan.FromMilliseconds(200));
                SendDeleteWindow(window);
                Pump(TimeSpan.FromMilliseconds(300));
                Assert.True(visibleWhenClosingReturned,
                    "The window was unmapped inside the native close callback instead of after it.");
                Assert.False(window.IsVisible);
                Assert.False(closed);

                for (int i = 0; i < 3; i++)
                {
                    window.ShowMixer();
                    Assert.True(window.IsVisible);
                    visibleWhenClosingReturned = null;
                    Assert.True(NativeClose(window, WindowCloseReason.WindowClosing));
                    Assert.True(visibleWhenClosingReturned);
                    Assert.True(window.IsVisible);
                    Assert.False(closed);
                    Dispatcher.UIThread.RunJobs();
                    Assert.False(window.IsVisible);
                    Assert.False(closed);
                }

                // A new launch arriving before the deferred hide keeps the
                // mixer open: the queued hide must find itself cancelled.
                window.ShowMixer();
                window.Close();
                Assert.True(window.IsVisible, "The hide ran inside the close callback.");
                window.ShowMixer();
                Dispatcher.UIThread.RunJobs();
                Assert.True(window.IsVisible);

                // Quit wins even when a hide is still queued.
                window.Close();
                Assert.True(window.IsVisible, "The hide ran inside the close callback.");
                window.Quit();
                Dispatcher.UIThread.RunJobs();
                Assert.True(closed);
                Assert.False(window.IsVisible);

                // A session shutdown is never intercepted: cancelling it would
                // stop the machine from logging out.
                window = NewWindow();
                window.ShowMixer();
                Assert.False(NativeClose(window, WindowCloseReason.ApplicationShutdown));
                window.ShowMixer();
                Assert.False(window.IsVisible);

                // With close-to-tray off the close button really closes, and
                // it does so with no tray host on this session bus: Avalonia
                // reports none either way, so the option is taken at its word.
                new UiSettings { MinimizeToTray = false }.Save();
                window = NewWindow();
                window.ShowMixer();
                window.Close();
                Assert.False(window.IsVisible);

                // Starting minimized is honoured whether or not a tray is there.
                new UiSettings { StartMinimized = true }.Save();
                window = NewWindow();
                Assert.True(window.StartsHidden);
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

    /// <summary>
    /// A window whose daemon link points at a port nothing serves, so the
    /// developer's running daemon never takes part in the test.
    /// </summary>
    private static MainWindow NewWindow() => new(new DaemonClient("ws://127.0.0.1:1/ws"));

    // WindowState is not asserted here: with no window manager under Xvfb the
    // property reads back Normal whatever it is set to, so a minimize and a
    // restore cannot be told apart. That one belongs on a real desktop.

    private const int ClientMessage = 33;
    private const long NoEventMask = 0;

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(string? name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] private static extern IntPtr XInternAtom(IntPtr display, string name, bool onlyIfExists);
    [DllImport("libX11.so.6")] private static extern int XSendEvent(IntPtr display, IntPtr window, bool propagate, IntPtr mask, ref XClientMessageEvent e);
    [DllImport("libX11.so.6")] private static extern int XFlush(IntPtr display);

    /// <summary>
    /// An XEvent carrying a ClientMessage. Padded to the size of the XEvent
    /// union (24 machine words) because Xlib reads that much.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct XClientMessageEvent
    {
        public int Type;
        public IntPtr Serial;
        public int SendEvent;
        public IntPtr Display;
        public IntPtr Window;
        public IntPtr MessageType;
        public int Format;
        public IntPtr Data0;
        public IntPtr Data1;
        public IntPtr Data2;
        public IntPtr Data3;
        public IntPtr Data4;
        private readonly IntPtr _pad0, _pad1, _pad2, _pad3, _pad4, _pad5, _pad6, _pad7, _pad8, _pad9, _pad10, _pad11;
    }

    /// <summary>
    /// Send the window manager's close request to a mapped window, the way a
    /// title bar X does. Xvfb runs no window manager, so the message comes
    /// from here; the X11 backend cannot tell the difference.
    /// </summary>
    private static void SendDeleteWindow(Window window)
    {
        IntPtr handle = window.TryGetPlatformHandle()!.Handle;
        IntPtr display = XOpenDisplay(null);
        Assert.NotEqual(IntPtr.Zero, display);
        try
        {
            var message = new XClientMessageEvent
            {
                Type = ClientMessage,
                Format = 32,
                Display = display,
                Window = handle,
                MessageType = XInternAtom(display, "WM_PROTOCOLS", false),
                Data0 = XInternAtom(display, "WM_DELETE_WINDOW", false),
            };
            Assert.NotEqual(0, XSendEvent(display, handle, false, (IntPtr)NoEventMask, ref message));
            XFlush(display);
        }
        finally { XCloseDisplay(display); }
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
