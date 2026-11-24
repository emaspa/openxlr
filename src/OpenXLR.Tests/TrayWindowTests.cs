using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using OpenXLR.UI;
using OpenXLR.UI.Localization;

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
                WindowLayoutTests.PinMaterial(new UiSettings { MinimizeToTray = true });
                AppBuilder.Configure<App>().UseSkia().UseHarfBuzz().UseX11().SetupWithoutStarting();
                WelcomeGuideWindowTests.Check();
                TrayRegistrationSurvivesHidingAndIsRemovedOnQuit();
                MeterBurstsApplyOnlyTheNewestFrame();
                window = NewWindow();
                Assert.Single(TrayIcon.GetIcons(Application.Current!)!);
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
                window.Quit();
                window = null;

                // Last: the lifetime this starts ends the dispatcher.
                QuitDuringPushToTalkMutesEveryMicrophoneBeforeTheProcessEnds();
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

    private static void TrayRegistrationSurvivesHidingAndIsRemovedOnQuit()
    {
        var app = Application.Current!;
        using var unrelated = new TrayIcon();
        var icons = new TrayIcons { unrelated };
        TrayIcon.SetIcons(app, icons);
        try
        {
            new UiSettings { StartMinimized = true, MinimizeToTray = true }.Save();
            for (int i = 0; i < 2; i++)
            {
                var window = NewWindow();
                try
                {
                    Assert.True(window.StartsHidden);
                    Assert.False(window.IsVisible);
                    Assert.Equal(2, icons.Count);
                    var icon = Assert.Single(icons, icon => icon != unrelated);
                    Assert.True(icon.IsVisible);
                    Assert.NotNull(icon.Icon);
                    Assert.Equal("OpenXLR", icon.ToolTipText);
                    Assert.NotNull(icon.Menu);
                    Assert.Equal(4, icon.Menu.Items.Count);
                    Assert.Equal(Localizer.Text("Soundboard"), Assert.IsType<NativeMenuItem>(icon.Menu.Items[1]).Header);
                    window.ShowMixer();
                    window.Close();
                    Dispatcher.UIThread.RunJobs();
                    Assert.False(window.IsVisible);
                    Assert.Contains(icon, icons);
                    window.ShowMixer();
                    Assert.True(window.IsVisible);
                    Assert.Equal(2, icons.Count);
                }
                finally { window.Quit(); }
                Assert.Same(unrelated, Assert.Single(icons));
            }
        }
        finally
        {
            TrayIcon.SetIcons(app, null);
            new UiSettings { MinimizeToTray = true }.Save();
        }
    }

    /// <summary>
    /// The UI started visible is closed while push to talk holds two
    /// microphones open, under the lifetime App gives it and the real
    /// lifetime class running the dispatcher. Both mutes reach the daemon and
    /// the client is gone before the lifetime is asked to end, which is what
    /// lets the process exit. The daemon is a fake and the desktop's
    /// shortcut portal sits on a private session bus.
    /// </summary>
    private static void QuitDuringPushToTalkMutesEveryMicrophoneBeforeTheProcessEnds()
    {
        if (DesktopBusSkip.Reason is not null) return;
        // The windows closed above finish their teardown first, so none of
        // them asks the lifetime below to end.
        Pump(TimeSpan.FromSeconds(1));
        // Set up off this thread: its awaits must not wait for a dispatcher
        // that nothing is pumping.
        var (bus, desktop, fake, daemon) = Task.Run(async () =>
        {
            var bus = await DesktopKeysTests.PrivateBus.Start();
            var desktop = new Tmds.DBus.Protocol.DBusConnection(bus.Address);
            await desktop.ConnectAsync();
            await desktop.RequestNameAsync(DesktopBus.Portal);
            var fake = new DesktopKeysTests.DesktopBackend(new DesktopBus(desktop));
            desktop.AddMethodHandler(fake);
            return (bus, desktop, fake, await DesktopKeysTests.MicDaemon.Start());
        }).GetAwaiter().GetResult();
        try
        {
            WindowLayoutTests.PinMaterial(new UiSettings { MinimizeToTray = false });
            new DesktopKeySettings { Enabled = true, PushToTalk = true, Microphones = ["xlr1", "xlr2"] }.Save();
            var client = new DaemonClient(daemon.Server.Url);
            var window = new MainWindow(client);
            Assert.False(window.StartsHidden);
            string[] Mutes() => [.. daemon.Commands.Where(c => c.StartsWith("set mute", StringComparison.Ordinal))];

            PumpUntil(() => Mutes().Length == 2 && fake.ShortcutIds.Contains(DesktopKeys.TalkKey), "Push to talk was not set up.");
            Assert.Equal(["set mute true", "set mute2 true"], Mutes());
            fake.Activate(DesktopKeys.TalkKey);
            PumpUntil(() => Mutes().Length == 4, "The key did not open the microphones.");
            Assert.Equal(["set mute false", "set mute2 false"], Mutes()[2..]);

            var lifetime = new Avalonia.Controls.ApplicationLifetimes.ClassicDesktopStyleApplicationLifetime();
            typeof(Application).GetField("_applicationLifetime", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(Application.Current, lifetime);
            App.UseLifetime(lifetime, window);
            string[]? mutesAtExit = null;
            bool clientGoneAtExit = false;
            lifetime.Exit += (_, _) =>
            {
                mutesAtExit = Mutes();
                clientGoneAtExit = client.DisposeAsync().IsCompleted;
            };
            bool stuck = false;
            DispatcherTimer.RunOnce(() => { stuck = true; lifetime.Shutdown(); }, TimeSpan.FromSeconds(15));
            Dispatcher.UIThread.Post(window.Close);
            // Shows the window, then runs the dispatcher until the lifetime ends.
            lifetime.Start([]);

            Assert.False(stuck, "Closing the window never ended the lifetime.");
            Assert.NotNull(mutesAtExit);
            Assert.Equal(["set mute true", "set mute2 true", "set mute false", "set mute2 false", "set mute true", "set mute2 true"], mutesAtExit);
            Assert.True(clientGoneAtExit, "The lifetime ended before the daemon client was disposed.");
            Assert.True(daemon.Muted["xlr1"]);
            Assert.True(daemon.Muted["xlr2"]);
        }
        finally
        {
            Task.Run(async () =>
            {
                await daemon.DisposeAsync();
                desktop.Dispose();
                await bus.DisposeAsync();
            }).GetAwaiter().GetResult();
        }
    }

    private static void PumpUntil(Func<bool> condition, string message)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) Pump(TimeSpan.FromMilliseconds(25));
        Assert.True(condition(), message);
    }

    /// <summary>
    /// A window whose daemon link points at a port nothing serves, so the
    /// developer's running daemon never takes part in the test.
    /// </summary>
    private static MainWindow NewWindow() => new(new DaemonClient("ws://127.0.0.1:1/ws"));

    private static void MeterBurstsApplyOnlyTheNewestFrame()
    {
        var client = new DaemonClient("ws://127.0.0.1:1/ws");
        try
        {
            var model = new MainViewModel(client);
            var channel = new ChannelViewModel(client, "music", "Music", []);
            model.Channels.Add(channel);
            int applied = 0;
            channel.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(channel.MeterL)) applied++; };
            var receive = (Action<System.Text.Json.Nodes.JsonNode>)typeof(DaemonClient)
                .GetField("MetersReceived", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(client)!;
            // The dispatcher is deliberately not pumped while frames arrive,
            // as when a native dialog or a slow layout occupies the UI thread.
            for (int i = 0; i < 1000; i++)
                receive(System.Text.Json.Nodes.JsonNode.Parse(i % 2 == 0
                    ? "{\"ch:music\":[0.25,0.25]}" : "{\"ch:music\":[0.75,0.75]}")!);
            Assert.Equal(0, applied);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, applied);
            Assert.Equal(0.75, channel.MeterL);
            Assert.Equal(0.75, channel.MeterR);

            receive(System.Text.Json.Nodes.JsonNode.Parse("{\"ch:music\":[0.5,0.5]}")!);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, applied);
            Assert.Equal(0.5, channel.MeterL);
        }
        finally { client.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

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
