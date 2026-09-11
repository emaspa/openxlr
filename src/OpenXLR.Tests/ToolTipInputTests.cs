using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;

namespace OpenXLR.Tests;

/// <summary>
/// A tooltip must never take the click meant for the control it describes.
/// A tooltip is a native popup window of its own, so the only way to see this
/// is to let the X server deliver the pointer events: a press posted straight
/// into the window would miss the popup and pass whatever the popup does. The
/// test therefore moves and clicks a real pointer through XTEST and asks the
/// production window what happened.
///
/// The window is walked to the bottom of the screen because that is where the
/// popup has to be pushed back over the pointer to fit, which is what used to
/// eat the press. Set AVALONIA_GLOBAL_SCALE_FACTOR to run the same cases at a
/// different desktop scale.
/// </summary>
[Collection("xdg-config")]
public sealed class ToolTipInputTests
{
    /// <summary>A port nothing serves, so a running daemon is not part of the test.</summary>
    private const string DeadDaemon = "ws://127.0.0.1:37899/ws";

    [ToolTipFact]
    public void ATooltipAtAScreenEdgeNeverTakesTheClickFromItsControl()
    {
        string config = Path.Combine(Path.GetTempPath(), "openxlr-tooltip-" + Guid.NewGuid());
        string? oldConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string? oldRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        string? oldBus = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");

        MainWindow? main = null;
        Button? cog = null;
        ToggleButton? bypass = null;
        DropDownButton? profiles = null;
        int cogClicks = 0, bypassClicks = 0, profileClicks = 0;
        var ready = new TaskCompletionSource();
        var quit = new CancellationTokenSource();
        Exception? failure = null;

        var ui = new Thread(() =>
        {
            try
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
                Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", config);
                Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", "unix:path=/nonexistent");
                AppBuilder.Configure<App>().UseSkia().UseHarfBuzz().UseX11().SetupWithoutStarting();
                Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;

                main = new MainWindow(new DaemonClient(DeadDaemon));
                var vm = new MainViewModel(new DaemonClient(DeadDaemon));
                main.DataContext = vm;
                typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(vm, [JsonNode.Parse("""{"mixes":[],"channels":[],"inserts":{}}""")]);
                // One plugin in the XLR 1 chain, hosted in the mixer, so its cog
                // opens the generated controls instead of reaching for a helper.
                vm.Inserts.Apply(JsonNode.Parse("""
                    [{"insert":{"id":"eq","plugin":"http://lsp-plug.in/plugins/lv2/graphic_equalizer_x16_mono",
                    "label":"LSP Graphic Equalizer x16 Mono"}}]
                    """));
                main.WindowStartupLocation = WindowStartupLocation.Manual;
                main.Position = new PixelPoint(40, 40);
                main.Width = 900;
                main.Height = 700;
                main.Show();
                ready.SetResult();
                Dispatcher.UIThread.MainLoop(quit.Token);
            }
            catch (Exception ex) { failure = ex; ready.TrySetResult(); }
            finally
            {
                Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", oldConfig);
                Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", oldRuntime);
                Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", oldBus);
            }
        }) { IsBackground = true };
        ui.Start();
        Assert.True(ready.Task.Wait(TimeSpan.FromSeconds(60)), "The window never opened.");
        if (failure is not null) throw failure;

        using var pointer = new XPointer();
        try
        {
            Thread.Sleep(500);
            Ui(() =>
            {
                cog = Named<Button>(main!, "Open this plugin's controls");
                bypass = Named<ToggleButton>(main!, "Bypass this plugin");
                profiles = main!.GetVisualDescendants().OfType<DropDownButton>()
                    .First(d => d.Content as string == "Profiles");
                cog.AddHandler(Button.ClickEvent, (_, _) => cogClicks++, handledEventsToo: true);
                bypass.AddHandler(Button.ClickEvent, (_, _) => bypassClicks++, handledEventsToo: true);
                profiles.AddHandler(Button.ClickEvent, (_, _) => profileClicks++, handledEventsToo: true);
                return true;
            });

            // The cog at the bottom of the screen, where a tooltip anchored to
            // the pointer has nowhere to go but back over the cursor.
            Place(main!, cog!, right: false);
            HoverAndClick(pointer, main!, cog!, "the insert cog at the bottom edge", () => cogClicks);
            Assert.True(Ui(() => main!.OwnedWindows.OfType<InsertControlsWindow>().Any()),
                "The cog was clicked but no controls window opened.");
            CloseChildren(main!);

            // Another control, and the tightest placement there is.
            bool checkedBefore = Ui(() => bypass!.IsChecked == true);
            Place(main!, bypass!, right: true);
            HoverAndClick(pointer, main!, bypass!, "the bypass toggle in the bottom corner", () => bypassClicks);
            Assert.NotEqual(checkedBefore, Ui(() => bypass!.IsChecked == true));

            // A tooltip in front of a menu button must still let the menu open.
            Place(main!, profiles!, right: false);
            HoverAndClick(pointer, main!, profiles!, "the profiles menu at the bottom edge", () => profileClicks);
            Assert.True(Wait(() => Ui(() => profiles!.Flyout!.IsOpen)), "The profiles menu did not open.");
            Ui(() => { profiles!.Flyout!.Hide(); return true; });

            // What made all of that work, so a revert says which setting went.
            // The application's own styles decide these, not anything set here.
            Assert.Equal(PlacementMode.Bottom, Ui(() => ToolTip.GetPlacement(cog!)));
            Assert.Equal(0d, Ui(() => ToolTip.GetVerticalOffset(cog!)));
        }
        finally
        {
            Ui(() =>
            {
                foreach (Window w in main!.OwnedWindows.ToArray()) w.Close();
                main.Close();
                quit.Cancel();
                return true;
            });
            ui.Join(TimeSpan.FromSeconds(15));
            if (Directory.Exists(config)) Directory.Delete(config, true);
        }
    }

    /// <summary>
    /// Closes the windows the clicks opened and puts the mixer back in front,
    /// so the next hover starts from the same state as the first one.
    /// </summary>
    private static void CloseChildren(MainWindow main)
    {
        Ui(() =>
        {
            foreach (Window w in main.OwnedWindows.ToArray()) w.Close();
            main.Activate();
            return true;
        });
        Thread.Sleep(300);
    }

    /// <summary>Moves the window until the control sits against the bottom of the screen.</summary>
    private static void Place(MainWindow main, Control target, bool right)
    {
        Ui(() =>
        {
            PixelRect screen = Desktop(main);
            PixelRect box = ScreenRect(main, target);
            PixelPoint window = main.Position;
            int y = screen.Y + screen.Height - box.Height - 2 - (box.Y - window.Y);
            int x = right
                ? screen.X + screen.Width - box.Width - 2 - (box.X - window.X)
                : window.X;
            main.Position = new PixelPoint(x, y);
            return true;
        });
        Thread.Sleep(400);
    }

    private static void HoverAndClick(XPointer pointer, MainWindow main, Control target, string what, Func<int> clicks)
    {
        // Park the pointer clear of the window first, so entering the control
        // is a fresh hover and the tooltip arms again.
        PixelRect screen = Ui(() => Desktop(main));
        pointer.MoveTo(screen.X + screen.Width - 1, screen.Y);
        Thread.Sleep(250);

        PixelRect box = Ui(() => ScreenRect(main, target));
        pointer.MoveTo(box.X + box.Width / 2, box.Y + box.Height / 2);
        Assert.True(Wait(() => Ui(() => ToolTip.GetIsOpen(target))),
            $"No tooltip appeared over {what}. box={box} window={Ui(() => main.Position)} "
            + $"over={Ui(() => target.IsPointerOver)} screen={screen}");

        int before = clicks();
        pointer.Click();
        Assert.True(Wait(() => clicks() > before), $"The tooltip over {what} swallowed the click.");
        Thread.Sleep(400);
        Assert.Equal(before + 1, clicks());
    }

    /// <summary>The screen the window is on, or the first one when nothing is marked primary.</summary>
    private static PixelRect Desktop(MainWindow main) =>
        (main.Screens.ScreenFromWindow(main) ?? main.Screens.Primary ?? main.Screens.All[0]).Bounds;

    private static PixelRect ScreenRect(MainWindow main, Control target) =>
        new(target.PointToScreen(default), PixelSize.FromSize(target.Bounds.Size, main.RenderScaling));

    private static T Named<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => AutomationProperties.GetName(c) == name);

    private static T Ui<T>(Func<T> work) => Dispatcher.UIThread.Invoke(work);

    private static bool Wait(Func<bool> done)
    {
        for (int i = 0; i < 80; i++)
        {
            if (done()) return true;
            Thread.Sleep(50);
        }
        return false;
    }

    /// <summary>
    /// Pointer events from the X server through XTEST, on a connection of this
    /// thread's own, so they reach whichever native window is in front.
    /// </summary>
    private sealed class XPointer : IDisposable
    {
        [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
        [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
        [DllImport("libX11.so.6")] private static extern int XFlush(IntPtr display);
        [DllImport("libXtst.so.6")] private static extern int XTestFakeMotionEvent(
            IntPtr display, int screen, int x, int y, ulong delay);
        [DllImport("libXtst.so.6")] private static extern int XTestFakeButtonEvent(
            IntPtr display, uint button, int press, ulong delay);

        private IntPtr _display;

        public XPointer()
        {
            _display = XOpenDisplay(IntPtr.Zero);
            Assert.True(_display != IntPtr.Zero, "The test needs an X display to move a pointer on.");
        }

        public void MoveTo(int x, int y)
        {
            XTestFakeMotionEvent(_display, -1, x, y, 0);
            XFlush(_display);
        }

        public void Click()
        {
            XTestFakeButtonEvent(_display, 1, 1, 0);
            XFlush(_display);
            Thread.Sleep(60);
            XTestFakeButtonEvent(_display, 1, 0, 0);
            XFlush(_display);
        }

        public void Dispose()
        {
            if (_display == IntPtr.Zero) return;
            XCloseDisplay(_display);
            _display = IntPtr.Zero;
        }
    }
}

public sealed class ToolTipFactAttribute : FactAttribute
{
    public ToolTipFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENXLR_TEST_TOOLTIP") != "1")
            Skip = "Run separately with OPENXLR_TEST_TOOLTIP=1 under xvfb-run, filtered to ToolTipInputTests.";
    }
}
