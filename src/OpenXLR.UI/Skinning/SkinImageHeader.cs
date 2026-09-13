using System;
using System.Buffers.Binary;
using System.IO;
using Avalonia;

namespace OpenXLR.UI.Skinning;

/// <summary>
/// The pixel size of an image file, read from its header without decoding it.
///
/// A skin is a file a user downloaded, and a four megabyte PNG can describe a
/// twenty thousand pixel square: decoding first and measuring afterwards would
/// hand the window's memory to whoever wrote the file. So the size is taken
/// from the header, checked against the budget, and only then is the image
/// decoded. Only PNG and JPEG are read, which is the whole format list a skin
/// is allowed to carry.
/// </summary>
internal static class SkinImageHeader
{
    /// <summary>The image's size, or null when the file is not a PNG or JPEG we can measure.</summary>
    public static PixelSize? Read(FileInfo file)
    {
        try
        {
            using FileStream stream = file.OpenRead();
            Span<byte> signature = stackalloc byte[8];
            if (stream.ReadAtLeast(signature, 8, throwOnEndOfStream: false) < 8) return null;
            // The PNG signature's first byte is 0x89, which is not a character:
            // in a UTF-8 literal it would encode as two bytes, so it is compared
            // as the byte it is.
            if (signature[0] == 0x89 && signature[1..4].SequenceEqual("PNG"u8)
                && signature[4] == 0x0D && signature[5] == 0x0A
                && signature[6] == 0x1A && signature[7] == 0x0A) return ReadPng(stream);
            return signature[0] == 0xFF && signature[1] == 0xD8 ? ReadJpeg(stream) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The first chunk of a PNG is IHDR, whose first eight bytes are the size.</summary>
    private static PixelSize? ReadPng(Stream stream)
    {
        Span<byte> header = stackalloc byte[16];
        if (stream.ReadAtLeast(header, 16, throwOnEndOfStream: false) < 16) return null;
        if (!header[4..8].SequenceEqual("IHDR"u8)) return null;
        return Size((int)BinaryPrimitives.ReadUInt32BigEndian(header[8..]),
            (int)BinaryPrimitives.ReadUInt32BigEndian(header[12..]));
    }

    /// <summary>
    /// A JPEG is a chain of marker segments; the frame header (SOF0 to SOF15,
    /// skipping the four markers in that range that are not frames) carries
    /// the size three bytes in.
    /// </summary>
    private static PixelSize? ReadJpeg(Stream stream)
    {
        // The signature read consumed eight bytes; the segments start after
        // the two byte start-of-image marker.
        stream.Position = 2;
        Span<byte> word = stackalloc byte[2];
        for (int segments = 0; segments < 1024; segments++)
        {
            int b = stream.ReadByte();
            if (b < 0) return null;
            if (b != 0xFF) continue;                             // resynchronize on the next marker
            int marker = stream.ReadByte();
            while (marker == 0xFF) marker = stream.ReadByte();   // fill bytes
            if (marker < 0) return null;
            if (marker is 0x00 or 0x01 or >= 0xD0 and <= 0xD9) continue;   // no payload
            if (stream.ReadAtLeast(word, 2, throwOnEndOfStream: false) < 2) return null;
            int length = BinaryPrimitives.ReadUInt16BigEndian(word);
            if (length < 2) return null;
            bool frame = marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;
            if (frame)
            {
                if (length < 7) return null;
                Span<byte> body = stackalloc byte[5];
                if (stream.ReadAtLeast(body, 5, throwOnEndOfStream: false) < 5) return null;
                return Size(BinaryPrimitives.ReadUInt16BigEndian(body[3..]),
                    BinaryPrimitives.ReadUInt16BigEndian(body[1..3]));
            }
            // The scan marker is followed by entropy-coded data, not by more
            // measurable segments; a frame header always comes before it.
            if (marker == 0xDA) return null;
            stream.Position += length - 2;
        }
        return null;
    }

    private static PixelSize? Size(int width, int height) =>
        width > 0 && height > 0 ? new PixelSize(width, height) : null;
}
