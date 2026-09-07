using System.Runtime.InteropServices;

namespace OpenXLR.Core;

/// <summary>
/// Giving native memory back to the system. Scanning the LV2 world builds
/// and frees a large model in C, and the C library keeps what it frees for
/// its own next use, so a daemon that rescans a few times holds hundreds of
/// megabytes it will never ask for again. One call after a scan hands the
/// free pages back.
/// </summary>
public static class NativeHeap
{
    [DllImport("libc", EntryPoint = "malloc_trim", SetLastError = false)]
    private static extern int MallocTrim(nuint pad);

    private static bool _unavailable;

    /// <summary>Return what the C library is holding free. Does nothing where there is no such call.</summary>
    public static void Trim()
    {
        if (_unavailable || !OperatingSystem.IsLinux()) return;
        try { MallocTrim(0); }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            _unavailable = true;   // not glibc: nothing to do here
        }
    }
}
