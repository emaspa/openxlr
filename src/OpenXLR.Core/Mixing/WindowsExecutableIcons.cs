using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenXLR.Core.Mixing;

/// <summary>
/// The icon a Windows program carries in its own executable, for a game that
/// runs under Wine or Proton without a desktop entry of its own. The
/// process's command line names the program the way Windows sees it, and the
/// process's Wine prefix turns that into a file here. The largest picture in
/// the program's first icon group is written once as a PNG under the user's
/// cache, and that file's path is the icon.
/// </summary>
public sealed class WindowsExecutableIcons
{
    // A process is looked at once per name it plays under; the table is
    // emptied when it grows past this, so ended processes do not pile up.
    private const int MaxProcesses = 1024;
    // Icons are small; the UI does not draw a file larger than 4 MB.
    private const int MaxIconBytes = 4 * 1024 * 1024;
    private const int MaxSide = 1024;

    private readonly string _procRoot;
    private readonly string _cacheDirectory;
    private readonly string _homePrefix;
    private readonly ConcurrentDictionary<(int Pid, string? Name), string?> _byProcess = new();

    public WindowsExecutableIcons(string? procRoot = null, string? cacheDirectory = null, string? homePrefix = null)
    {
        _procRoot = procRoot ?? "/proc";
        _cacheDirectory = cacheDirectory ?? DefaultCacheDirectory;
        _homePrefix = homePrefix ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wine");
    }

    /// <summary><c>$XDG_CACHE_HOME/openxlr/icons</c>, or under <c>~/.cache</c>.</summary>
    public static string DefaultCacheDirectory
    {
        get
        {
            string? cacheHome = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            if (string.IsNullOrWhiteSpace(cacheHome) || !Path.IsPathRooted(cacheHome))
                cacheHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
            return Path.Combine(cacheHome, "openxlr", "icons");
        }
    }

    /// <summary>
    /// The PNG of this stream's Windows program, or null when the stream is
    /// not a Wine program or its executable has no icon. The first call for
    /// a process reads its command line and its executable; later calls do
    /// not touch the disk.
    /// </summary>
    public string? For(AudioStream stream)
    {
        if (stream.ProcessId <= 0 || stream.FlatpakInstanceId is not null || !StreamMatcher.RunsUnderWine(stream)) return null;
        var key = (stream.ProcessId, stream.AppName);
        if (_byProcess.TryGetValue(key, out string? known)) return known;
        if (_byProcess.Count >= MaxProcesses) _byProcess.Clear();
        string? icon = Find(stream.ProcessId);
        _byProcess[key] = icon;
        return icon;
    }

    private string? Find(int pid)
    {
        try
        {
            string process = Path.Combine(_procRoot, pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            string? program = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(process, "cmdline")))
                .Split('\0').FirstOrDefault(StreamMatcher.IsWindowsExecutable);
            if (program is null) return null;
            string prefix = WineSession.PrefixIn(File.ReadAllBytes(Path.Combine(process, "environ")), _homePrefix);
            return UnixPath(program, prefix) is { } file ? Cached(file) : null;
        }
        // The process ended, or it is somebody else's.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>
    /// The file a program path names: a Unix path as it is, and a drive path
    /// through the prefix's <c>dosdevices</c> links, its folders matched
    /// regardless of letter case the way Windows matches them.
    /// </summary>
    internal static string? UnixPath(string program, string prefix)
    {
        if (program.StartsWith(@"\\?\", StringComparison.Ordinal)) program = program[4..];
        string current;
        string rest;
        if (program.StartsWith('/')) { current = "/"; rest = program[1..]; }
        else if (program.Length > 2 && char.IsAsciiLetter(program[0]) && program[1] == ':' && program[2] is '\\' or '/')
        {
            current = Path.Combine(prefix, "dosdevices", char.ToLowerInvariant(program[0]) + ":");
            rest = program[3..];
        }
        else return null;
        foreach (string part in rest.Split('\\', '/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is "." or "..") return null;
            string exact = Path.Combine(current, part);
            if (Path.Exists(exact)) { current = exact; continue; }
            if (!Directory.Exists(current)) return null;
            string? match = Directory.EnumerateFileSystemEntries(current)
                .FirstOrDefault(e => string.Equals(Path.GetFileName(e), part, StringComparison.OrdinalIgnoreCase));
            if (match is null) return null;
            current = match;
        }
        return File.Exists(current) ? current : null;
    }

    // One PNG per version of the executable: its path, size and time name
    // the file, so a game update draws again and a known one is not reread.
    private string? Cached(string file)
    {
        var info = new FileInfo(file);
        string stamp = $"{info.FullName}\n{info.Length}\n{info.LastWriteTimeUtc.Ticks}";
        string name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stamp)))[..32] + ".png";
        string target = Path.Combine(_cacheDirectory, name);
        if (File.Exists(target)) return target;
        if (Extract(file) is not { } png) return null;
        OpenXlrPaths.WriteAtomic(target, png);
        return target;
    }

    /// <summary>The executable's largest icon as PNG bytes, or null when it has none this can draw.</summary>
    internal static byte[]? Extract(string file)
    {
        try
        {
            using SafeFileHandle handle = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var image = new PortableExecutable(handle, RandomAccess.GetLength(handle));
            if (image.Resource(ResourceType.GroupIcon, null) is not { } group) return null;
            foreach (ushort id in IconsBySize(group))
                if (image.Resource(ResourceType.Icon, id) is { } icon && ToPng(icon) is { } png) return png;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    // GRPICONDIR: a 6-byte header and 14-byte entries, each naming an icon
    // resource by id. Larger first, then more colours.
    internal static IEnumerable<ushort> IconsBySize(byte[] group)
    {
        if (group.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(2)) != 1) return [];
        int count = Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(4)), (group.Length - 6) / 14);
        var entries = new List<(int Side, int Bits, ushort Id)>();
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> e = group.AsSpan(6 + i * 14, 14);
            int side = e[0] == 0 ? 256 : e[0];
            entries.Add((side, BinaryPrimitives.ReadUInt16LittleEndian(e[6..]), BinaryPrimitives.ReadUInt16LittleEndian(e[12..])));
        }
        return entries.OrderByDescending(e => e.Side).ThenByDescending(e => e.Bits).Select(e => e.Id);
    }

    /// <summary>One icon resource as PNG: a PNG stored as it is, a Windows bitmap converted; null for anything else.</summary>
    internal static byte[]? ToPng(byte[] icon)
    {
        ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        if (icon.AsSpan().StartsWith(signature))
        {
            if (icon.Length < 24) return null;
            uint w = BinaryPrimitives.ReadUInt32BigEndian(icon.AsSpan(16)), h = BinaryPrimitives.ReadUInt32BigEndian(icon.AsSpan(20));
            return w is > 0 and <= MaxSide && h is > 0 and <= MaxSide ? icon : null;
        }
        return Bitmap(icon) is var (width, height, rgba) ? EncodePng(width, height, rgba) : null;
    }

    // A BITMAPINFOHEADER, its palette, the colour rows and the 1-bit
    // transparency mask, both bottom-up with rows padded to four bytes. The
    // header's height counts both halves.
    internal static (int Width, int Height, byte[] Rgba)? Bitmap(byte[] dib)
    {
        if (dib.Length < 40) return null;
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        int width = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(4));
        int height = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8)) / 2;
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14));
        uint compression = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(16));
        uint used = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(32));
        if (headerSize < 40 || headerSize > dib.Length || width is <= 0 or > 256 || height is <= 0 or > 256) return null;
        if (bits is not (1 or 4 or 8 or 24 or 32) || !(compression == 0 || (compression == 3 && bits == 32))) return null;
        int colours = bits <= 8 ? (used is > 0 and <= 256 ? (int)used : 1 << bits) : 0;
        int palette = headerSize + (compression == 3 ? 12 : 0);
        int pixels = palette + colours * 4;
        int stride = (width * bits + 31) / 32 * 4, maskStride = (width + 31) / 32 * 4;
        int mask = pixels + stride * height;
        if (mask > dib.Length) return null;
        bool hasMask = mask + maskStride * height <= dib.Length;

        var rgba = new byte[width * height * 4];
        bool anyAlpha = false;
        for (int y = 0; y < height; y++)
        {
            int row = pixels + (height - 1 - y) * stride;
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                byte b, g, r, a = 255;
                switch (bits)
                {
                    case 32:
                        b = dib[row + x * 4]; g = dib[row + x * 4 + 1]; r = dib[row + x * 4 + 2]; a = dib[row + x * 4 + 3];
                        anyAlpha |= a != 0;
                        break;
                    case 24:
                        b = dib[row + x * 3]; g = dib[row + x * 3 + 1]; r = dib[row + x * 3 + 2];
                        break;
                    default:
                        int perByte = 8 / bits;
                        int index = (dib[row + x / perByte] >> ((perByte - 1 - x % perByte) * bits)) & ((1 << bits) - 1);
                        if (index >= colours) return null;
                        int c = palette + index * 4;
                        b = dib[c]; g = dib[c + 1]; r = dib[c + 2];
                        break;
                }
                rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = b; rgba[o + 3] = a;
            }
        }
        // A 32-bit icon carries its own transparency unless every pixel says
        // 0, which older tools wrote; the mask is the transparency otherwise.
        if (bits != 32 || !anyAlpha)
            for (int y = 0; y < height; y++)
            {
                int row = mask + (height - 1 - y) * maskStride;
                for (int x = 0; x < width; x++)
                    rgba[(y * width + x) * 4 + 3] = hasMask && (dib[row + x / 8] & (0x80 >> (x % 8))) != 0 ? (byte)0 : (byte)255;
            }
        return (width, height, rgba);
    }

    internal static byte[] EncodePng(int width, int height, byte[] rgba)
    {
        var raw = new byte[height * (1 + width * 4)];
        for (int y = 0; y < height; y++)
            Buffer.BlockCopy(rgba, y * width * 4, raw, y * (1 + width * 4) + 1, width * 4);   // filter 0 per row
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;   // bits per channel
        header[9] = 6;   // RGBA
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        byte[] name = Encoding.ASCII.GetBytes(type);
        png.Write(name);
        png.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(number, SoundboardArchive.Crc32(SoundboardArchive.Crc32(0, name), data));
        png.Write(number);
    }

    private enum ResourceType : uint { Icon = 3, GroupIcon = 14 }

    /// <summary>
    /// Just enough of a PE image to find resources: the section table maps
    /// addresses to file offsets, and the resource tree runs type, name,
    /// language. Every read is bounded by the file; anything out of place is
    /// no resource.
    /// </summary>
    private sealed class PortableExecutable
    {
        private readonly SafeFileHandle _file;
        private readonly long _length;
        private readonly List<(uint Address, uint Size, uint Offset, uint RawSize)> _sections = [];
        private readonly long _resources = -1;

        public PortableExecutable(SafeFileHandle file, long length)
        {
            _file = file;
            _length = length;
            if (Read(0, 64) is not { } dos || dos[0] != 'M' || dos[1] != 'Z') return;
            long pe = BinaryPrimitives.ReadUInt32LittleEndian(dos.AsSpan(0x3C));
            if (Read(pe, 24) is not { } coff || BinaryPrimitives.ReadUInt32LittleEndian(coff) != 0x00004550) return;
            int sections = BinaryPrimitives.ReadUInt16LittleEndian(coff.AsSpan(6));
            int optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(coff.AsSpan(20));
            if (sections > 96 || Read(pe + 24, optionalSize) is not { } optional || optionalSize < 2) return;
            int directories = BinaryPrimitives.ReadUInt16LittleEndian(optional) switch { 0x10B => 96, 0x20B => 112, _ => -1 };
            // The resource table is the third data directory.
            if (directories < 0 || optionalSize < directories + 3 * 8
                || BinaryPrimitives.ReadUInt32LittleEndian(optional.AsSpan(directories - 4)) < 3) return;
            uint resourceAddress = BinaryPrimitives.ReadUInt32LittleEndian(optional.AsSpan(directories + 16));
            if (Read(pe + 24 + optionalSize, sections * 40) is not { } table) return;
            for (int i = 0; i < sections; i++)
            {
                ReadOnlySpan<byte> s = table.AsSpan(i * 40, 40);
                _sections.Add((BinaryPrimitives.ReadUInt32LittleEndian(s[12..]), BinaryPrimitives.ReadUInt32LittleEndian(s[8..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(s[20..]), BinaryPrimitives.ReadUInt32LittleEndian(s[16..])));
            }
            if (resourceAddress != 0) _resources = Offset(resourceAddress);
        }

        /// <summary>The first language's data of this type's resource with this id, or its first one when the id is null.</summary>
        public byte[]? Resource(ResourceType type, ushort? id)
        {
            if (_resources < 0) return null;
            if (Child(_resources, (uint)type) is not { Directory: true } names) return null;
            if (Child(names.At, id) is not { Directory: true } languages) return null;
            if (Child(languages.At, null) is not { Directory: false } leaf || Read(leaf.At, 16) is not { } data) return null;
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
            long offset = Offset(BinaryPrimitives.ReadUInt32LittleEndian(data));
            return offset < 0 || size == 0 || size > MaxIconBytes ? null : Read(offset, (int)size);
        }

        // One directory's entry with this numeric id, or its first entry.
        private (bool Directory, long At)? Child(long directory, uint? id)
        {
            if (Read(directory, 16) is not { } header) return null;
            int named = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(12));
            int count = Math.Min(named + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14)), 4096);
            if (count == 0 || Read(directory + 16, count * 8) is not { } entries) return null;
            for (int i = 0; i < count; i++)
            {
                uint name = BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan(i * 8));
                uint target = BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan(i * 8 + 4));
                if (id is { } wanted && ((name & 0x8000_0000) != 0 || name != wanted)) continue;
                return ((target & 0x8000_0000) != 0, _resources + (target & 0x7FFF_FFFF));
            }
            return null;
        }

        private long Offset(uint address)
        {
            foreach (var (start, size, offset, raw) in _sections)
                if (address >= start && address - start < Math.Max(size, raw))
                    return address - start < raw ? offset + (long)(address - start) : -1;
            return -1;
        }

        private byte[]? Read(long offset, int count)
        {
            if (offset < 0 || count < 0 || offset + count > _length) return null;
            var buffer = new byte[count];
            return RandomAccess.Read(_file, buffer, offset) == count ? buffer : null;
        }
    }
}
