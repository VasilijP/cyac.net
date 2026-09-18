using System.Buffers.Binary;
using System.IO.Compression;

namespace CYAC.Port.Core.Data;

/// <summary>
/// An 8-bit indexed image as the data tree carries it: one palette index per pixel plus the palette.
/// </summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Indices">One byte per pixel, row-major, top row first.</param>
/// <param name="Palette">RGB triples, 8 bits per component, as the PNG's <c>PLTE</c> chunk holds them.</param>
/// <remarks>
/// The INDICES are the data (the indexed-PNG rule: indexed PNG keeps the round trip exact and is
/// what a modder edits with palette awareness).  The palette in the PNG is the 6-bit VGA DAC value
/// expanded to 8 bits by replication; the raw 6-bit values live in the JSON side-car beside it.
/// </remarks>
public readonly record struct IndexedImage(int Width, int Height, byte[] Indices, byte[] Palette)
{
    /// <summary>The palette index at a pixel.</summary>
    /// <param name="x">Column, 0-based.</param>
    /// <param name="y">Row, 0-based.</param>
    public byte this[int x, int y] => Indices[(y * Width) + x];

    /// <summary>How many colours the palette holds.</summary>
    public int PaletteEntries => Palette.Length / 3;
}

/// <summary>
/// A dependency-free reader for the 8-bit indexed PNGs <c>cyac-transform</c> writes.
/// </summary>
/// <remarks>
/// <para>
/// Format facts are <c>platform</c> (W3C PNG specification): an 8-byte signature, then length-tagged
/// chunks with a CRC-32 each.  <c>IHDR</c> gives width, height, bit depth, colour type (3 = indexed)
/// and filter/interlace method; <c>PLTE</c> holds the palette as RGB triples; <c>IDAT</c> holds
/// zlib-compressed scanlines, each prefixed by one filter-type byte (0 none, 1 sub, 2 up, 3 average,
/// 4 Paeth).
/// </para>
/// <para>
/// The port needs a reader, not a library: it decodes exactly colour type 3 at 8 bits per pixel,
/// non-interlaced, and refuses anything else BY NAME rather than guessing.  The CRC is verified,
/// because a data tree that has been corrupted on disk should say so at load time instead of drawing
/// noise.  That one lives on the transform side (<c>CYAC.Port.Transform/Image/IndexedPng.cs</c>);
/// this is the runtime half and only reads.
/// </para>
/// </remarks>
public static class IndexedPng
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The colour type this reader handles: 3, indexed.</summary>
    public const int IndexedColorType = 3;

    /// <summary>The bit depth this reader handles: 8.</summary>
    public const int BitsPerPixel = 8;

    /// <summary>Decodes an 8-bit indexed PNG.</summary>
    /// <param name="png">The file bytes.</param>
    /// <exception cref="InvalidDataException">The file is not an 8-bit indexed, non-interlaced PNG.</exception>
    public static IndexedImage Decode(ReadOnlySpan<byte> png)
    {
        if (png.Length < Signature.Length || !png[..Signature.Length].SequenceEqual(Signature))
        {
            throw new InvalidDataException("not a PNG file (bad signature)");
        }

        int width = 0;
        int height = 0;
        byte[] palette = [];
        MemoryStream idat = new MemoryStream();
        bool haveHeader = false;
        int position = Signature.Length;

        while (position + 8 <= png.Length)
        {
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(png[position..]);
            string type = System.Text.Encoding.ASCII.GetString(png.Slice(position + 4, 4));
            int body = position + 8;
            if (length < 0 || body + length + 4 > png.Length)
            {
                throw new InvalidDataException($"PNG chunk '{type}' runs past the end of the file");
            }

            uint declared = BinaryPrimitives.ReadUInt32BigEndian(png[(body + length)..]);
            uint actual = Crc32(png.Slice(position + 4, 4 + length));
            if (declared != actual)
            {
                throw new InvalidDataException(
                    $"PNG chunk '{type}' at {position} fails its CRC (0x{actual:X8} vs 0x{declared:X8})");
            }

            switch (type)
            {
                case "IHDR":
                    if (length < 13)
                    {
                        throw new InvalidDataException("PNG IHDR is too short");
                    }

                    width = (int)BinaryPrimitives.ReadUInt32BigEndian(png[body..]);
                    height = (int)BinaryPrimitives.ReadUInt32BigEndian(png[(body + 4)..]);
                    int depth = png[body + 8];
                    int colorType = png[body + 9];
                    int interlace = png[body + 12];
                    if (depth != BitsPerPixel || colorType != IndexedColorType || interlace != 0)
                    {
                        throw new InvalidDataException(
                            $"PNG is {depth}-bit colour type {colorType}" +
                            $"{(interlace != 0 ? ", interlaced" : string.Empty)}; this reader handles " +
                            $"{BitsPerPixel}-bit colour type {IndexedColorType} (indexed), non-interlaced");
                    }

                    haveHeader = true;
                    break;

                case "PLTE":
                    if (length % 3 != 0)
                    {
                        throw new InvalidDataException($"PNG PLTE is {length} bytes, not a whole number of triples");
                    }

                    palette = png.Slice(body, length).ToArray();
                    break;

                case "IDAT":
                    idat.Write(png.Slice(body, length));
                    break;

                default:
                    break;
            }

            position = body + length + 4;
            if (type == "IEND")
            {
                break;
            }
        }

        if (!haveHeader)
        {
            throw new InvalidDataException("PNG has no IHDR chunk");
        }

        if (palette.Length == 0)
        {
            throw new InvalidDataException("PNG has no PLTE chunk, so it carries no palette");
        }

        return new IndexedImage(width, height, Unfilter(Inflate(idat.ToArray()), width, height), palette);
    }

    /// <summary>Reads an indexed PNG from a file.</summary>
    /// <param name="path">The file's path.</param>
    public static IndexedImage Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Decode(File.ReadAllBytes(path));
    }

    private static byte[] Inflate(byte[] zlib)
    {
        // A zlib stream is a 2-byte header, the raw deflate data, and a 4-byte Adler-32.
        if (zlib.Length < 6)
        {
            throw new InvalidDataException("PNG IDAT is too short to be a zlib stream");
        }

        using MemoryStream input = new MemoryStream(zlib, 2, zlib.Length - 6);
        using DeflateStream deflate = new DeflateStream(input, CompressionMode.Decompress);
        using MemoryStream output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Unfilter(byte[] raw, int width, int height)
    {
        // Colour type 3 at 8 bpp means one byte per pixel, so the Paeth/sub "previous pixel" is
        // simply the byte one to the left (bpp = 1).
        int stride = width;
        long expected = (long)(stride + 1) * height;
        if (raw.LongLength < expected)
        {
            throw new InvalidDataException(
                $"PNG scanline data is {raw.LongLength} bytes, expected {expected} for {width}x{height}");
        }

        byte[] pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            int source = (y * (stride + 1)) + 1;
            int target = y * stride;
            byte filter = raw[source - 1];
            for (int x = 0; x < stride; x++)
            {
                int a = x >= 1 ? pixels[target + x - 1] : 0;
                int b = y >= 1 ? pixels[target - stride + x] : 0;
                int c = x >= 1 && y >= 1 ? pixels[target - stride + x - 1] : 0;
                int value = raw[source + x];
                pixels[target + x] = filter switch
                {
                    0 => (byte)value,
                    1 => (byte)(value + a),
                    2 => (byte)(value + b),
                    3 => (byte)(value + ((a + b) / 2)),
                    4 => (byte)(value + Paeth(a, b, c)),
                    _ => throw new InvalidDataException($"PNG scanline {y} uses filter type {filter}"),
                };
            }
        }

        return pixels;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)(-(int)(crc & 1)));
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
