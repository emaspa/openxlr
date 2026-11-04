using System.Globalization;
using System.Runtime.InteropServices;

namespace OpenXLR.Core.Devices;

internal interface IAlsaControlEvents : IDisposable
{
    bool Available { get; }
    bool Changed();
}

/// <summary>A nonblocking ALSA control subscription, drained by the device poll.</summary>
internal sealed class AlsaControlEvents : IAlsaControlEvents
{
    private const int NonBlocking = 0x0001, ReadOnly = 0x0004;
    private const int Again = -11, Interrupted = -4;
    private IntPtr _control, _event;
    public bool Available => _control != IntPtr.Zero;

    internal static IAlsaControlEvents? Open(int card)
    {
        if (!OperatingSystem.IsLinux() || card < 0) return null;
        var watch = new AlsaControlEvents();
        try
        {
            if (snd_ctl_open(out watch._control, "hw:" + card.ToString(CultureInfo.InvariantCulture), NonBlocking | ReadOnly) >= 0
                && snd_ctl_subscribe_events(watch._control, 1) >= 0 && snd_ctl_event_malloc(out watch._event) >= 0)
                return watch;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { }
        watch.Dispose();
        return null;
    }

    public bool Changed()
    {
        bool changed = false;
        // Bound work during a burst. Remaining events are drained next poll.
        for (int i = 0; Available && i < 64; i++)
        {
            int result = snd_ctl_read(_control, _event);
            if (result > 0) { changed = true; continue; }
            if (result == Interrupted) continue; // EINTR: retry within the same bounded drain
            if (result != Again) Dispose(); // EAGAIN means no event; EOF or error ends the watch
            break;
        }
        return changed;
    }

    public void Dispose()
    {
        if (_event != IntPtr.Zero) { snd_ctl_event_free(_event); _event = IntPtr.Zero; }
        if (_control != IntPtr.Zero) { snd_ctl_close(_control); _control = IntPtr.Zero; }
    }

    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int snd_ctl_open(out IntPtr control, string name, int mode);
    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_ctl_subscribe_events(IntPtr control, int subscribe);
    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_ctl_event_malloc(out IntPtr ev);
    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)]
    private static extern void snd_ctl_event_free(IntPtr ev);
    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_ctl_read(IntPtr control, IntPtr ev);
    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_ctl_close(IntPtr control);
}
