using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

#if OPENXLR_UI
namespace OpenXLR.UI;
#elif OPENXLR_TUI
namespace OpenXLR.Tui;
#else
namespace OpenXLR.Core;
#endif

/// <summary>Reject devices, sockets and FIFOs before an ordinary read can block.</summary>
internal static class RegularFile
{
    /// <summary>Read a regular file within one byte budget, including bytes added during the read.</summary>
    internal static byte[] ReadBytes(string path, int maximum)
    {
        Require(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > maximum) throw new IOException("File exceeds the inspection limit.");
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            if (bytes.Length + read > maximum) throw new IOException("File exceeds the inspection limit.");
            bytes.Write(buffer, 0, read);
        }
        return bytes.ToArray();
    }

    /// <summary>Keep StreamReader's legacy BOM detection while bounding the file before decoding it.</summary>
    internal static string ReadText(string path, int maximum)
    {
        // Preserve the ordinary missing-file exceptions for optional stores.
        _ = File.GetAttributes(path);
        return Decode(ReadBytes(path, maximum));
    }

    /// <summary>Bytes read with <see cref="ReadBytes"/> as text, with the same BOM detection as <see cref="ReadText"/>.</summary>
    internal static string Decode(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes));
        return reader.ReadToEnd();
    }

    internal static void Require(string path)
    {
        if (!OperatingSystem.IsLinux()) return;
        LinuxFileStatus status = Inspect(path) ?? throw new IOException($"Could not inspect the regular file {path}: file not found.");
        if ((status.Mask & 1) == 0 || (status.Mode & 0xf000) != 0x8000)
            throw new IOException($"{path} is not a regular file.");
    }

    /// <summary>
    /// True when the path, after its links, names a device, a pipe, a socket or a
    /// directory, so a caller can say so in its own words before reading. A missing
    /// path is false and left for the read to report.
    /// </summary>
    internal static bool IsSpecial(string path) =>
        OperatingSystem.IsLinux() && Inspect(path) is { } status && (status.Mode & 0xf000) != 0x8000;

    /// <summary>
    /// Open a regular file for reading, following links. The open does not
    /// block, so a pipe or a device in the file's place is refused at once
    /// instead of holding the caller, and the type is checked on the handle
    /// that was opened rather than on a path that can change after a check.
    /// </summary>
    internal static SafeFileHandle OpenHandle(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            Require(path);
            return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        int fd = Open(path, ReadOnly | NonBlocking | CloseOnExec | NoControllingTerminal);
        if (fd < 0)
        {
            int error = Marshal.GetLastPInvokeError();
            string message = $"Could not open {path}: {new Win32Exception(error).Message}";
            throw error == AccessDenied ? new UnauthorizedAccessException(message) : new IOException(message);
        }
        var handle = new SafeFileHandle(fd, ownsHandle: true);
        try
        {
            if (Inspect($"/proc/self/fd/{fd}") is not { } status || (status.Mode & 0xf000) != 0x8000)
                throw new IOException($"{path} is not a regular file.");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    // Linux's values, the same on x86-64 and AArch64.
    private const int ReadOnly = 0, NoControllingTerminal = 0x100, NonBlocking = 0x800, CloseOnExec = 0x80000;
    private const int AccessDenied = 13;

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    /// <summary>Linux's fixed metadata layout, also used to own a control socket without following links.</summary>
    internal static LinuxFileStatus? Inspect(string path, bool noFollow = false)
    {
        const uint fields = 1 | 2 | 8 | 256; // type, mode, uid, inode
        LinuxFileStatus status;
        try
        {
            if (Statx(-100, path, noFollow ? 0x100 : 0, fields, out status) != 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error == 2) return null;
                throw new IOException($"Could not inspect {path}: {new Win32Exception(error).Message}");
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        { throw new IOException($"Could not inspect the regular file {path}: statx is unavailable.", ex); }
        if ((status.Mask & fields) != fields) throw new IOException($"Incomplete metadata for {path}.");
        return status;
    }

    // Linux's fixed statx UAPI layout is the same on every supported architecture.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    internal struct LinuxFileStatus
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(20)] public uint User;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out LinuxFileStatus status);
}
