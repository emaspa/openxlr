using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using OpenXLR.Core.Mixing;

namespace OpenXLR.Tests;

/// <summary>
/// A Windows game's icon from its own executable: the resources of a PE
/// image built here, its bitmaps as PNG pixels, the program path a Wine
/// process names turned into a file, and a stream's process found through a
/// fake <c>/proc</c>.
/// </summary>
public sealed class WindowsExecutableIconsTests : IDisposable
{
    private const uint Icon = 3, GroupIcon = 14;
    private readonly string _root = Directory.CreateTempSubdirectory("openxlr-exe-icons-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void TheLargestIconOfTheGroupComesOutAsPng()
    {
        byte[] exe = Executable(
            (Icon, 1, Dib32(16, 16, (x, y) => (255, 0, 0, 255))),
            (Icon, 2, Dib32(48, 48, (x, y) => ((byte)x, (byte)y, 7, x < 24 ? (byte)0 : (byte)200))),
            (Icon, 3, Dib32(32, 32, (x, y) => (0, 0, 255, 255))),
            (GroupIcon, 101, Group((16, 32, 1), (48, 32, 2), (32, 32, 3))));
        string file = Path.Combine(_root, "Game.exe");
        File.WriteAllBytes(file, exe);

        var (w, h, rgba) = ReadPng(WindowsExecutableIcons.Extract(file)!);
        Assert.Equal((48, 48), (w, h));
        // Top-left first, though the bitmap's rows run bottom-up.
        Assert.Equal(new byte[] { 0, 0, 7, 0 }, rgba[..4]);
        int last = (47 * 48 + 47) * 4;
        Assert.Equal(new byte[] { 47, 47, 7, 200 }, rgba[last..(last + 4)]);
    }

    [Fact]
    public void AStoredPngIsKeptAsItIs()
    {
        byte[] png = WindowsExecutableIcons.EncodePng(2, 2, new byte[16]);
        string file = Path.Combine(_root, "Png.exe");
        File.WriteAllBytes(file, Executable((Icon, 9, png), (GroupIcon, 1, Group((0, 32, 9)))));
        Assert.Equal(png, WindowsExecutableIcons.Extract(file));
    }

    [Fact]
    public void APalettedIconTakesItsTransparencyFromTheMask()
    {
        // 4 bits a pixel, two colours: the left half colour 1, the right
        // half masked out.
        var (w, h, rgba) = WindowsExecutableIcons.Bitmap(Dib4(8, 2))!.Value;
        Assert.Equal((8, 2), (w, h));
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, rgba[..4]);
        Assert.Equal(0, rgba[4 * 4 + 3]);
        // A 32-bit icon whose alpha is all zero is an old one: the mask decides.
        byte[] old = Dib32(2, 1, (x, y) => (1, 2, 3, 0), maskRow: 0x40);
        var (_, _, pixels) = WindowsExecutableIcons.Bitmap(old)!.Value;
        Assert.Equal(new byte[] { 1, 2, 3, 255, 1, 2, 3, 0 }, pixels);
    }

    [Fact]
    public void AnythingElseIsNoIcon()
    {
        string text = Path.Combine(_root, "notes.exe");
        File.WriteAllText(text, "MZ but nothing after it");
        Assert.Null(WindowsExecutableIcons.Extract(text));
        string bare = Path.Combine(_root, "bare.exe");
        File.WriteAllBytes(bare, Executable((Icon, 1, Dib32(4, 4, (x, y) => (0, 0, 0, 255)))));
        Assert.Null(WindowsExecutableIcons.Extract(bare));
        Assert.Null(WindowsExecutableIcons.Bitmap(new byte[12]));
        Assert.Null(WindowsExecutableIcons.ToPng([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]));
    }

    [Fact]
    public void AWindowsPathFindsItsFileThroughThePrefix()
    {
        string prefix = Prefix();
        string game = Path.Combine(prefix, "drive_c/Program Files/Game/Game.exe");
        Assert.Equal(game, WindowsExecutableIcons.UnixPath(@"C:\Program Files\Game\Game.exe", prefix)?.Replace("dosdevices/c:", "drive_c"));
        // Windows does not mind letter case, and neither does the lookup.
        Assert.NotNull(WindowsExecutableIcons.UnixPath(@"c:\PROGRAM FILES\game\game.EXE", prefix));
        Assert.NotNull(WindowsExecutableIcons.UnixPath(@"\\?\C:\Program Files\Game\Game.exe", prefix));
        Assert.Equal(game, WindowsExecutableIcons.UnixPath(game, prefix));
        Assert.Null(WindowsExecutableIcons.UnixPath(@"C:\Program Files\..\Program Files\Game\Game.exe", prefix));
        Assert.Null(WindowsExecutableIcons.UnixPath(@"D:\Game.exe", prefix));
        Assert.Null(WindowsExecutableIcons.UnixPath("Game.exe", prefix));
    }

    [Fact]
    public void AWineStreamGetsItsProgramsIconOnceAndKeepsIt()
    {
        string prefix = Prefix();
        string proc = Path.Combine(_root, "proc");
        Process(proc, 4242, @"C:\Program Files\Game\Game.exe" + "\0-launch\0", $"HOME=/x\0WINEPREFIX={prefix}\0");
        Process(proc, 4343, "/usr/bin/firefox\0", "");
        string cache = Path.Combine(_root, "cache");
        var icons = new WindowsExecutableIcons(proc, cache, Path.Combine(_root, "no-wine"));

        var game = new AudioStream(1, "Game.exe", "wine-preloader", null) { ProcessId = 4242 };
        string? png = icons.For(game);
        Assert.NotNull(png);
        Assert.Equal(cache, Path.GetDirectoryName(png));
        var (w, h, _) = ReadPng(File.ReadAllBytes(png!));
        Assert.Equal((32, 32), (w, h));

        // Kept: the process is not read again.
        Directory.Delete(Path.Combine(proc, "4242"), recursive: true);
        Assert.Equal(png, icons.For(game));
        // A second reader finds the file already drawn.
        Process(proc, 4242, @"C:\Program Files\Game\Game.exe" + "\0", $"WINEPREFIX={prefix}\0");
        Assert.Equal(png, new WindowsExecutableIcons(proc, cache).For(game));

        Assert.Null(icons.For(new AudioStream(2, "Firefox", "firefox", null) { ProcessId = 4343 }));
        Assert.Null(icons.For(game with { ProcessId = 0 }));
        Assert.Null(icons.For(game with { ProcessId = 4242, FlatpakInstanceId = "1" }));
    }

    [Fact]
    public void WithoutTheEnvironmentThePrefixIsFoundOnDisk()
    {
        // The daemon's service cannot read another process's environment:
        // its command line is all there is.
        string prefix = Prefix();
        string proc = Path.Combine(_root, "proc");
        Process(proc, 7, @"C:\Program Files\Game\Game.exe" + "\0", environ: null);
        string unix = Path.Combine(prefix, "drive_c/Program Files/Game/Game.exe").Replace('/', '\\');
        Process(proc, 8, "Z:" + unix + "\0", environ: null);
        Process(proc, 9, @"C:\Elsewhere\Game.exe" + "\0", environ: null);
        var icons = new WindowsExecutableIcons(proc, Path.Combine(_root, "cache"), Path.Combine(_root, "no-wine"),
            [(Path.Combine(_root, "missing"), 4), (_root, 1)]);

        Assert.NotNull(icons.For(new AudioStream(1, "Game.exe", "wine64-preloader", null) { ProcessId = 7 }));
        Assert.NotNull(icons.For(new AudioStream(2, "Game.exe", "wine64-preloader", null) { ProcessId = 8 }));
        Assert.Null(icons.For(new AudioStream(3, "Game.exe", "wine64-preloader", null) { ProcessId = 9 }));
    }

    [Fact]
    public void PrefixesAreFoundToTheirDepthAndNotSearchedInside()
    {
        string prefix = Prefix();
        Directory.CreateDirectory(Path.Combine(prefix, "drive_c/nested/dosdevices"));
        Directory.CreateDirectory(Path.Combine(prefix, "drive_c/nested/drive_c"));
        Directory.CreateDirectory(Path.Combine(_root, "a/b/c/deep/dosdevices"));
        Directory.CreateDirectory(Path.Combine(_root, "a/b/c/deep/drive_c"));
        Directory.CreateDirectory(Path.Combine(_root, ".hidden/dosdevices"));
        Directory.CreateDirectory(Path.Combine(_root, ".hidden/drive_c"));

        Assert.Equal([prefix], WindowsExecutableIcons.PrefixesUnder(_root, 3));
        Assert.Equal([Path.Combine(_root, "a/b/c/deep"), prefix], WindowsExecutableIcons.PrefixesUnder(_root, 4));
        Assert.Equal([prefix], WindowsExecutableIcons.PrefixesUnder(prefix, 0));
        Assert.Empty(WindowsExecutableIcons.PrefixesUnder(Path.Combine(_root, "missing"), 4));
    }

    // A prefix whose drive C holds one game with a 32-pixel icon.
    private string Prefix()
    {
        string prefix = Path.Combine(_root, "prefix");
        if (Directory.Exists(prefix)) return prefix;
        string folder = Path.Combine(prefix, "drive_c/Program Files/Game");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(prefix, "dosdevices"));
        File.CreateSymbolicLink(Path.Combine(prefix, "dosdevices/c:"), "../drive_c");
        File.WriteAllBytes(Path.Combine(folder, "Game.exe"), Executable(
            (Icon, 1, Dib32(32, 32, (x, y) => (9, 9, 9, 255))), (GroupIcon, 1, Group((32, 32, 1)))));
        return prefix;
    }

    // A null environment is one that cannot be read.
    private static void Process(string proc, int pid, string cmdline, string? environ)
    {
        string dir = Path.Combine(proc, pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "cmdline"), cmdline);
        if (environ is not null) File.WriteAllText(Path.Combine(dir, "environ"), environ);
    }

    private static byte[] Group(params (int Side, int Bits, ushort Id)[] icons)
    {
        var group = new byte[6 + icons.Length * 14];
        BinaryPrimitives.WriteUInt16LittleEndian(group.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(group.AsSpan(4), (ushort)icons.Length);
        for (int i = 0; i < icons.Length; i++)
        {
            Span<byte> e = group.AsSpan(6 + i * 14, 14);
            e[0] = e[1] = (byte)(icons[i].Side & 0xFF);
            BinaryPrimitives.WriteUInt16LittleEndian(e[4..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(e[6..], (ushort)icons[i].Bits);
            BinaryPrimitives.WriteUInt16LittleEndian(e[12..], icons[i].Id);
        }
        return group;
    }

    private static byte[] Header(int width, int height, int bits, int colours)
    {
        var header = new byte[40];
        BinaryPrimitives.WriteInt32LittleEndian(header, 40);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), height * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), (ushort)bits);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(32), colours);
        return header;
    }

    // Rows bottom-up, each pixel B G R A, then a mask row per row.
    private static byte[] Dib32(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> pixel, byte maskRow = 0)
    {
        var dib = new List<byte>(Header(width, height, 32, 0));
        for (int y = height - 1; y >= 0; y--)
            for (int x = 0; x < width; x++)
            {
                var (r, g, b, a) = pixel(x, y);
                dib.AddRange([b, g, r, a]);
            }
        int maskStride = (width + 31) / 32 * 4;
        for (int y = 0; y < height; y++)
            for (int i = 0; i < maskStride; i++) dib.Add(i == 0 ? maskRow : (byte)0);
        return [.. dib];
    }

    private static byte[] Dib4(int width, int height)
    {
        var dib = new List<byte>(Header(width, height, 4, 2));
        dib.AddRange([0, 0, 0, 0, 30, 20, 10, 0]);   // black, then colour 1 as B G R x
        for (int y = 0; y < height; y++) dib.AddRange([0x11, 0x11, 0x11, 0x11]);   // every pixel colour 1
        for (int y = 0; y < height; y++) dib.AddRange([0x0F, 0, 0, 0]);   // right half masked
        return [.. dib];
    }

    /// <summary>A PE32+ image with one section that holds this resource tree.</summary>
    private static byte[] Executable(params (uint Type, ushort Id, byte[] Data)[] resources)
    {
        const uint Rva = 0x1000;
        var types = resources.GroupBy(r => r.Type).OrderBy(g => g.Key).ToList();
        int typeAt = 16 + types.Count * 8;
        int nameAt = typeAt + types.Sum(g => 16 + g.Count() * 8);
        int dataAt = nameAt + resources.Length * 24;
        int blobAt = dataAt + resources.Length * 16;
        var rsrc = new byte[blobAt + resources.Sum(r => (r.Data.Length + 3) & ~3)];
        Dir(rsrc, 0, types.Count);
        for (int t = 0; t < types.Count; t++)
        {
            Entry(rsrc, 16 + t * 8, types[t].Key, 0x8000_0000u | (uint)typeAt);
            var items = types[t].ToList();
            Dir(rsrc, typeAt, items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                Entry(rsrc, typeAt + 16 + i * 8, items[i].Id, 0x8000_0000u | (uint)nameAt);
                Dir(rsrc, nameAt, 1);
                Entry(rsrc, nameAt + 16, 0x409, (uint)dataAt);
                BinaryPrimitives.WriteUInt32LittleEndian(rsrc.AsSpan(dataAt), Rva + (uint)blobAt);
                BinaryPrimitives.WriteUInt32LittleEndian(rsrc.AsSpan(dataAt + 4), (uint)items[i].Data.Length);
                items[i].Data.CopyTo(rsrc, blobAt);
                blobAt += (items[i].Data.Length + 3) & ~3;
                nameAt += 24;
                dataAt += 16;
            }
            typeAt += 16 + items.Count * 8;
        }

        var image = new byte[0x200 + rsrc.Length];
        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x3C), 0x40);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x40), 0x4550);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x44), 0x8664);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x46), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x54), 240);
        const int optional = 0x58;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optional), 0x20B);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 108), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 112 + 16), Rva);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 112 + 20), (uint)rsrc.Length);
        const int section = optional + 240;
        Encoding.ASCII.GetBytes(".rsrc").CopyTo(image, section);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 8), (uint)rsrc.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 12), Rva);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 16), (uint)rsrc.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(section + 20), 0x200);
        rsrc.CopyTo(image, 0x200);
        return image;

        static void Dir(byte[] b, int at, int ids) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at + 14), (ushort)ids);
        static void Entry(byte[] b, int at, uint name, uint target)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), name);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 4), target);
        }
    }

    /// <summary>An RGBA PNG read back with its chunk checksums checked.</summary>
    private static (int Width, int Height, byte[] Rgba) ReadPng(byte[] png)
    {
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        int width = 0, height = 0, at = 8;
        using var idat = new MemoryStream();
        while (true)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            byte[] typed = png[(at + 4)..(at + 8 + length)];
            Assert.Equal(BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at + 8 + length)), SoundboardArchive.Crc32(0, typed));
            string type = Encoding.ASCII.GetString(typed, 0, 4);
            byte[] data = typed[4..];
            at += 12 + length;
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4));
                Assert.Equal(new byte[] { 8, 6, 0, 0, 0 }, data[8..]);
            }
            else if (type == "IDAT") idat.Write(data);
            else if (type == "IEND") break;
        }
        idat.Position = 0;
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionMode.Decompress)) z.CopyTo(raw);
        byte[] rows = raw.ToArray();
        Assert.Equal(height * (1 + width * 4), rows.Length);
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            Assert.Equal(0, rows[y * (1 + width * 4)]);
            Buffer.BlockCopy(rows, y * (1 + width * 4) + 1, rgba, y * width * 4, width * 4);
        }
        return (width, height, rgba);
    }
}
