using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace CYAC.Port.Transform.Image;

/// <summary>
/// A minimal writer for 8-bit <b>indexed</b> PNG — the open image format the data tree uses.
/// </summary>
/// <remarks>
/// <para>
/// Indexed (colour type 3) is deliberate: CYAC's images are palette indices, and keeping the indices
/// is what makes the image round trip exactly and lets a modder repaint the palette without touching
/// the art.  Format: PNG spec (platform, RFC 2083) — signature, <c>IHDR</c>, <c>PLTE</c>,
/// <c>IDAT</c> (zlib, platform RFC 1950/1951), <c>IEND</c>; each scanline prefixed by filter type 0.
/// </para>
/// <para>
/// Written by hand rather than pulled from a package so <c>CYAC.Port.Transform</c> stays
/// dependency-free and the output is byte-deterministic.
/// </para>
/// </remarks>
public static class IndexedPng
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The most palette entries colour type 3 can carry.</summary>
    public const int MaxPaletteEntries = 256;

    /// <summary>
    /// Encodes an indexed image.
    /// </summary>
    /// <param name="width">Image width in pixels; must be positive.</param>
    /// <param name="height">Image height in pixels; must be positive.</param>
    /// <param name="indices">Row-major palette indices, <paramref name="width"/> × <paramref name="height"/> bytes.</param>
    /// <param name="paletteRgb">The palette as 8-bit RGB triples, 3 bytes per entry, at most 256 entries.</param>
    /// <exception cref="ArgumentException">A dimension, the index count or the palette size is wrong.</exception>
    public static byte[] Encode(int width, int height, ReadOnlySpan<byte> indices, ReadOnlySpan<byte> paletteRgb)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException($"image is {width}x{height}; both dimensions must be positive");
        }

        if (indices.Length != width * height)
        {
            throw new ArgumentException(
                $"{indices.Length} indices for a {width}x{height} image (expected {width * height})",
                nameof(indices));
        }

        if (paletteRgb.Length == 0 || paletteRgb.Length % 3 != 0 || paletteRgb.Length / 3 > MaxPaletteEntries)
        {
            throw new ArgumentException(
                $"palette is {paletteRgb.Length} bytes; expected 3 x 1..{MaxPaletteEntries}", nameof(paletteRgb));
        }

        int entries = paletteRgb.Length / 3;
        foreach (byte index in indices)
        {
            if (index >= entries)
            {
                throw new ArgumentException(
                    $"pixel index {index} is outside the {entries}-entry palette", nameof(indices));
            }
        }

        MemoryStream png = new MemoryStream();
        png.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 3;    // colour type: indexed
        ihdr[10] = 0;   // compression: deflate
        ihdr[11] = 0;   // filter method 0
        ihdr[12] = 0;   // no interlace
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "PLTE", paletteRgb);

        byte[] raw = new byte[height * (width + 1)];
        for (int y = 0; y < height; y++)
        {
            raw[y * (width + 1)] = 0;   // filter type 0 (None)
            indices.Slice(y * width, width).CopyTo(raw.AsSpan(y * (width + 1) + 1));
        }

        MemoryStream deflated = new MemoryStream();
        using (ZLibStream zlib = new ZLibStream(deflated, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        WriteChunk(png, "IDAT", deflated.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>
    /// Reads an 8-bit indexed PNG back: the indices are the data, which is what makes the image families'
    /// inverse possible.
    /// </summary>
    /// <param name="png">The PNG bytes, as this class writes them.</param>
    /// <returns>The dimensions, the row-major indices and the palette as RGB triples.</returns>
    /// <exception cref="InvalidDataException">The file is not an 8-bit indexed, non-interlaced PNG.</exception>
    public static (int Width, int Height, byte[] Indices, byte[] PaletteRgb) Decode(ReadOnlySpan<byte> png)
    {
        if (png.Length < Signature.Length || !png[..Signature.Length].SequenceEqual(Signature))
        {
            throw new InvalidDataException("not a PNG (bad signature)");
        }

        int width = 0, height = 0;
        byte[] palette = [];
        MemoryStream idat = new MemoryStream();
        bool sawHeader = false;
        int p = Signature.Length;

        while (p + 8 <= png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png[p..]);
            if (length < 0 || p + 12 + length > png.Length)
            {
                throw new InvalidDataException($"chunk at +0x{p:X} declares {length} bytes, which does not fit");
            }

            string type = Encoding.ASCII.GetString(png.Slice(p + 4, 4));
            ReadOnlySpan<byte> data = png.Slice(p + 8, length);
            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(data);
                    height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                    if (data[8] != 8 || data[9] != 3)
                    {
                        throw new InvalidDataException(
                            $"expected an 8-bit indexed PNG, got bit depth {data[8]} colour type {data[9]}");
                    }

                    if (data[12] != 0)
                    {
                        throw new InvalidDataException("interlaced PNGs are not supported");
                    }

                    sawHeader = true;
                    break;

                case "PLTE":
                    palette = data.ToArray();
                    break;

                case "IDAT":
                    idat.Write(data);
                    break;

                default:
                    break;
            }

            p += 12 + length;
            if (type == "IEND")
            {
                break;
            }
        }

        if (!sawHeader || width <= 0 || height <= 0)
        {
            throw new InvalidDataException("the PNG has no usable IHDR");
        }

        idat.Position = 0;
        MemoryStream raw = new MemoryStream();
        using (ZLibStream zlib = new ZLibStream(idat, CompressionMode.Decompress, leaveOpen: true))
        {
            zlib.CopyTo(raw);
        }

        byte[] rows = raw.ToArray();
        if (rows.Length != height * (width + 1))
        {
            throw new InvalidDataException(
                $"the image data is {rows.Length} B; a {width}x{height} indexed image is {height * (width + 1)} B");
        }

        byte[] indices = new byte[width * height];
        byte[] previous = new byte[width];
        for (int y = 0; y < height; y++)
        {
            int at = y * (width + 1);
            byte filter = rows[at];
            Span<byte> line = rows.AsSpan(at + 1, width);
            Unfilter(filter, line, previous);
            line.CopyTo(indices.AsSpan(y * width));
            line.CopyTo(previous);
        }

        return (width, height, indices, palette);
    }

    // PNG filter types 0..4 (platform: RFC 2083 §6).  This writer only ever emits 0, but a modder's
    // editor will happily rewrite a tree image with any of them, and the inverse has to read that.
    private static void Unfilter(byte filter, Span<byte> line, ReadOnlySpan<byte> previous)
    {
        switch (filter)
        {
            case 0:
                return;
            case 1:
                for (int x = 1; x < line.Length; x++)
                {
                    line[x] = (byte)(line[x] + line[x - 1]);
                }

                return;
            case 2:
                for (int x = 0; x < line.Length; x++)
                {
                    line[x] = (byte)(line[x] + previous[x]);
                }

                return;
            case 3:
                for (int x = 0; x < line.Length; x++)
                {
                    int left = x == 0 ? 0 : line[x - 1];
                    line[x] = (byte)(line[x] + ((left + previous[x]) / 2));
                }

                return;
            case 4:
                for (int x = 0; x < line.Length; x++)
                {
                    int a = x == 0 ? 0 : line[x - 1];
                    int b = previous[x];
                    int c = x == 0 ? 0 : previous[x - 1];
                    int q = a + b - c;
                    int pa = Math.Abs(q - a), pb = Math.Abs(q - b), pc = Math.Abs(q - c);
                    int pred = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                    line[x] = (byte)(line[x] + pred);
                }

                return;
            default:
                throw new InvalidDataException($"unknown PNG filter type {filter}");
        }
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        Span<byte> typeBytes = stackalloc byte[4];
        Encoding.ASCII.GetBytes(type, typeBytes);
        stream.Write(typeBytes);
        stream.Write(data);

        uint crc = Crc32.Compute(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }
}

/// <summary>CRC-32 as PNG defines it (platform: RFC 2083 §15, the ISO 3309 / ITU-T V.42 polynomial).</summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    /// <summary>The CRC of a chunk's type field followed by its data.</summary>
    public static uint Compute(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in type)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (byte b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
