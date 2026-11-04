using System.Globalization;
using System.Runtime.InteropServices;

namespace OpenXLR.Core.Devices;

/// <summary>The card controls the Wave backends read back.</summary>
[Flags]
internal enum AlsaControls
{
    None = 0,
    Mute = 1,
    Headphones = 2,
}

internal interface IAlsaControlEvents : IDisposable
{
    bool Available { get; }
    /// <summary>The read-back controls whose value changed since the last call.</summary>
    AlsaControls Changed();
}

/// <summary>A nonblocking ALSA control subscription, drained by the device poll.</summary>
internal sealed class AlsaControlEvents : IAlsaControlEvents
{
    private const int NonBlocking = 0x0001, ReadOnly = 0x0004;
    private const int Again = -11, Interrupted = -4;
    private const int ElementEvent = 0;
    private const uint ValueChanged = 1, ElementRemoved = uint.MaxValue;
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

    public AlsaControls Changed()
    {
        AlsaControls changed = AlsaControls.None;
        // Bound work during a burst. Remaining events are drained next poll.
        for (int i = 0; Available && i < 64; i++)
        {
            int result = snd_ctl_read(_control, _event);
            if (result > 0)
            {
                if (snd_ctl_event_get_type(_event) == ElementEvent)
                    changed |= ReadbackControl(snd_ctl_event_elem_get_mask(_event),
                        Marshal.PtrToStringAnsi(snd_ctl_event_elem_get_name(_event)));
                continue;
            }
            if (result == Interrupted) continue; // EINTR: retry within the same bounded drain
            if (result != Again) Dispose(); // EAGAIN means no event; EOF or error ends the watch
            break;
        }
        return changed;
    }

    internal static AlsaControls ReadbackControl(uint mask, string? name)
        => mask == ElementRemoved || (mask & ValueChanged) == 0 ? AlsaControls.None
            : name switch
            {
                Wave3Device.MuteCtl => AlsaControls.Mute,
                Wave3Device.HpCtl => AlsaControls.Headphones,
                _ => AlsaControls.None,
            };

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
    private static extern int snd_ctl_event_get_type(IntPtr ev);
    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint snd_ctl_event_elem_get_mask(IntPtr ev);
    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr snd_ctl_event_elem_get_name(IntPtr ev);
    [DllImport("libasound.so.2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int snd_ctl_close(IntPtr control);
}
