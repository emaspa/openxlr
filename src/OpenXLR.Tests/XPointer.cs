using System.Runtime.InteropServices;

namespace OpenXLR.Tests;

/// <summary>
/// Pointer events from the X server through XTEST, on a connection of this
/// thread's own, so they reach whichever native window is in front.
/// </summary>
internal sealed class XPointer : IDisposable
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

    public void SetButton(bool pressed, uint button = 1)
    {
        XTestFakeButtonEvent(_display, button, pressed ? 1 : 0, 0);
        XFlush(_display);
    }

    [DllImport("libX11.so.6")] private static extern byte XKeysymToKeycode(IntPtr display, ulong key);
    [DllImport("libXtst.so.6")] private static extern int XTestFakeKeyEvent(IntPtr display, uint key, int press, ulong delay);

    public void Key(ulong keysym)
    {
        byte key = XKeysymToKeycode(_display, keysym);
        XTestFakeKeyEvent(_display, key, 1, 0);
        XTestFakeKeyEvent(_display, key, 0, 0);
        XFlush(_display);
    }

    public void Click()
    {
        SetButton(true);
        Thread.Sleep(60);
        SetButton(false);
    }

    public void Dispose()
    {
        if (_display == IntPtr.Zero) return;
        XCloseDisplay(_display);
        _display = IntPtr.Zero;
    }
}
