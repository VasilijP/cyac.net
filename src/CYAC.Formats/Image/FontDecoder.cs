using CYAC.Formats.EaLib;

namespace CYAC.Formats.Image;

// .fnt (DeluxeFont) decoder — C# port of.
//
// On-disk envelope: standard EALIB flag=0x01 (u32-LE declared size + LZSS).
//
// Decompressed payload layout:
//   +0x00 .. +0x0C : 13-byte ASCII marker "[DeluxeFont]\0"
//   +0x0D          : 1-byte format version (always 0x02 in shipped files)
//   +0x0E ..       : null-terminated font name ("4x6" / "Proportional" / "prop_bold")
//                    padded out to the fixed offset below
//   +0x20 : u16-LE  field_0x20      — semantic unclear (8/10/8 in samples)
//   +0x22 : u16-LE  bytes_per_row   — stride of the unified-row glyph bitmap
//   +0x24 : u16-LE  height          — scanlines per glyph
//   +0x26 : u16-LE  max_width       — informative max glyph width
//   +0x28 : u32-LE  sentinel = 0x000046FA
//   +0x2C : 256 × u16-LE pixel-column offset table
//   +0x22C: glyph data — bytes_per_row × height bytes, packed as a single
//                        (bytes_per_row*8)-pixel-wide × height-tall 1-bpp
//                        bitmap (bit 7 = leftmost pixel within byte)
//
// Glyph[c] = sub-rectangle starting at pixel column offsetTable[c] with width =
// offsetTable[c+1] - offsetTable[c], for height rows. Reverse-engineered against
// loader image@0x1F01A (fixed-width 4x6 path) and image@0x1F1F4 (proportional path);
public static class FontDecoder
{
    public const int MarkerLength = 13;                  // "[DeluxeFont]\0"
    public const byte FormatVersion = 0x02;
    public const uint FormatSentinel = 0x000046FAu;
    public const int TableOffset = 0x2C;
    public const int TableEntries = 256;
    public const int TableSize = TableEntries * 2;       // = 0x200
    public const int DataOffset = TableOffset + TableSize; // = 0x22C

    /// <summary>Offset of the NUL-terminated font name inside the payload.</summary>
    public const int NameOffset = MarkerLength + 1;      // = 0x0E

    /// <summary>Offset of the fixed header fields (<c>field_0x20</c> onwards).</summary>
    public const int FieldsOffset = 0x20;

    /// <summary>Bytes the name occupies before the fixed fields: 0x0E..0x1F.</summary>
    public const int NameFieldSize = FieldsOffset - NameOffset;   // = 18

    private static readonly byte[] s_marker =
    {
        (byte)'[', (byte)'D', (byte)'e', (byte)'l', (byte)'u', (byte)'x',
        (byte)'e', (byte)'F', (byte)'o', (byte)'n', (byte)'t', (byte)']',
        0x00
    };

    public sealed record Glyph(int Codepoint, int Width, int Height, byte[] Pixels)
    {
        /// <summary>
        /// Pixel at (row, col) — 0 or 1.  Pixels are stored row-major
        /// (length = Width × Height).
        /// </summary>
        public byte At(int row, int col) => Pixels[row * Width + col];
    }

    public sealed class Font
    {
        public required string Name { get; init; }
        public required byte FormatVersionByte { get; init; }
        public required int Field0x20 { get; init; }
        public required int BytesPerRow { get; init; }
        public required int Height { get; init; }
        public required int MaxWidth { get; init; }
        public required uint Sentinel { get; init; }
        public required ushort[] OffsetTable { get; init; }
        public required byte[] GlyphData { get; init; }
        public required IReadOnlyDictionary<int, Glyph> Glyphs { get; init; }
        public required byte[] RawPayload { get; init; }
        public required int DeclaredSize { get; init; }

        /// <summary>
        /// The bytes between the name's NUL terminator and <see cref="FieldsOffset"/>.
        /// </summary>
        /// <remarks>
        /// NOT always zero padding: <c>4x6.fnt</c> carries <c>74 6C 65 64</c> ("tled") and
        /// <c>prop3.fnt</c> carries <c>76 0E FF 14</c> there — authoring-tool buffer residue
        /// It has no known meaning and is preserved verbatim so the round trip
        /// closes (the carry-unknown-bytes rule).
        /// </remarks>
        public required byte[] NameFieldTail { get; init; }

        /// <summary>Bytes after the glyph bitmap, if any (none on the three shipping fonts).</summary>
        public required byte[] Trailing { get; init; }
    }

    /// <summary>
    /// Decompress + parse a `.fnt` file from raw on-disk bytes.
    /// </summary>
    public static Font Decode(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 4)
            throw new InvalidDataException(
                $".fnt too short ({raw.Length} bytes; need >= 4 for size header)");
        int declared = raw[0] | (raw[1] << 8) | (raw[2] << 16) | (raw[3] << 24);
        byte[] body = Lzss.Decompress(raw[4..], declared);
        if (body.Length != declared)
            throw new InvalidDataException(
                $".fnt LZSS produced {body.Length} of {declared} bytes");

        return ParsePayload(body, declared);
    }

    public static Font DecodeFromFile(string path)
        => Decode(File.ReadAllBytes(path));

    /// <summary>
    /// Parse an ALREADY-DECOMPRESSED `.fnt` payload — what a caller has when the
    /// EALIB container layer has already undone the flag=0x01 framing.
    /// </summary>
    /// <param name="payload">The decompressed payload.</param>
    public static Font DecodePayload(ReadOnlySpan<byte> payload)
    {
        byte[] body = payload.ToArray();
        return ParsePayload(body, body.Length);
    }

    /// <summary>
    /// Cheap probe: does the buffer start with the LZSS envelope of a
    /// `.fnt` file?  We can't verify the marker without decompressing,
    /// but we sanity-check the declared size is plausible.
    /// </summary>
    public static bool LooksLikeFnt(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 8) return false;
        int declared = raw[0] | (raw[1] << 8) | (raw[2] << 16) | (raw[3] << 24);
        // Shipped fonts: 952 / 1164 / 1356.  Be lenient.
        return declared > 0x40 && declared < 0x10000;
    }

    private static Font ParsePayload(byte[] body, int declared)
    {
        for (int i = 0; i < MarkerLength; i++)
        {
            if (body[i] != s_marker[i])
                throw new InvalidDataException(
                    $".fnt: missing [DeluxeFont] marker at +0x{i:X2} " +
                    $"(got 0x{body[i]:X2}, expected 0x{s_marker[i]:X2})");
        }
        byte ver = body[MarkerLength];
        if (ver != FormatVersion)
            throw new InvalidDataException(
                $".fnt: unexpected format byte 0x{ver:X2} " +
                $"(expected 0x{FormatVersion:X2})");

        int nameStart = NameOffset;
        int nameEnd = nameStart;
        while (nameEnd < body.Length && nameEnd < FieldsOffset && body[nameEnd] != 0) nameEnd++;
        if (nameEnd >= FieldsOffset)
            throw new InvalidDataException(".fnt: name field not null-terminated before +0x20");
        string name = System.Text.Encoding.ASCII.GetString(body, nameStart, nameEnd - nameStart);
        byte[] nameTail = new byte[FieldsOffset - (nameEnd + 1)];
        Array.Copy(body, nameEnd + 1, nameTail, 0, nameTail.Length);

        if (body.Length < TableOffset)
            throw new InvalidDataException(
                $".fnt: payload too short for header (got {body.Length} B)");
        int field_0x20 = body[0x20] | (body[0x21] << 8);
        int bytesPerRow = body[0x22] | (body[0x23] << 8);
        int height = body[0x24] | (body[0x25] << 8);
        int maxWidth = body[0x26] | (body[0x27] << 8);
        uint sentinel = (uint)(body[0x28] | (body[0x29] << 8) |
                               (body[0x2A] << 16) | (body[0x2B] << 24));
        if (sentinel != FormatSentinel)
            throw new InvalidDataException(
                $".fnt: bad sentinel 0x{sentinel:X8} (expected 0x{FormatSentinel:X8})");

        if (body.Length < DataOffset)
            throw new InvalidDataException(
                $".fnt: payload short for offset table " +
                $"(got {body.Length}, need {DataOffset})");
        ushort[] table = new ushort[TableEntries];
        for (int i = 0; i < TableEntries; i++)
        {
            int o = TableOffset + i * 2;
            table[i] = (ushort)(body[o] | (body[o + 1] << 8));
        }

        int dataSize = bytesPerRow * height;
        if (body.Length < DataOffset + dataSize)
            throw new InvalidDataException(
                $".fnt: payload short for {bytesPerRow}×{height}={dataSize} B glyph data");
        byte[] glyphData = new byte[dataSize];
        Array.Copy(body, DataOffset, glyphData, 0, dataSize);
        byte[] trailing = new byte[body.Length - (DataOffset + dataSize)];
        Array.Copy(body, DataOffset + dataSize, trailing, 0, trailing.Length);

        IReadOnlyDictionary<int, Glyph> glyphs = ExtractGlyphs(table, glyphData, bytesPerRow, height);

        return new Font
        {
            NameFieldTail = nameTail,
            Trailing = trailing,
            Name = name,
            FormatVersionByte = ver,
            Field0x20 = field_0x20,
            BytesPerRow = bytesPerRow,
            Height = height,
            MaxWidth = maxWidth,
            Sentinel = sentinel,
            OffsetTable = table,
            GlyphData = glyphData,
            Glyphs = glyphs,
            RawPayload = body,
            DeclaredSize = declared,
        };
    }

    private static IReadOnlyDictionary<int, Glyph> ExtractGlyphs(
        ushort[] table, byte[] glyphData, int bytesPerRow, int height)
    {
        ushort sentinelOffset = table[TableEntries - 1];
        int totalPixelCols = bytesPerRow * 8;
        Dictionary<int, Glyph> glyphs = new Dictionary<int, Glyph>();

        for (int cp = 0; cp < TableEntries - 1; cp++)
        {
            ushort colStart = table[cp];
            ushort colEnd = table[cp + 1];
            if (colStart == sentinelOffset) continue;
            int width = colEnd - colStart;
            if (width <= 0) continue;
            if (colStart + width > totalPixelCols) continue;

            byte[] pixels = new byte[width * height];
            for (int row = 0; row < height; row++)
            {
                int rowBase = row * bytesPerRow;
                int outBase = row * width;
                for (int c = 0; c < width; c++)
                {
                    int pcol = colStart + c;
                    byte b = glyphData[rowBase + (pcol >> 3)];
                    int bit = (b >> (7 - (pcol & 7))) & 1;
                    pixels[outBase + c] = (byte)bit;
                }
            }

            glyphs[cp] = new Glyph(cp, width, height, pixels);
        }

        return glyphs;
    }
}
