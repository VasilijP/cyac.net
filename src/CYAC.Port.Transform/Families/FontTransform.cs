using System.Globalization;
using System.Text.Json;
using CYAC.Formats.Image;
using CYAC.Port.Transform.Image;
using CYAC.Port.Transform.Json;
using CYAC.Port.Transform.Transform;

namespace CYAC.Port.Transform.Families;

/// <summary>
/// <c>.fnt</c> (DeluxeFont) → <c>images/fonts/&lt;name&gt;.png</c> (the 1-bit glyph strip),
/// <c>images/fonts/&lt;name&gt;.json</c> (metrics + the column table) and a
/// <c>&lt;name&gt;.sheet.png</c> view.
/// </summary>
/// <remarks>
/// <para>
/// Format (loaders <c>image@0x1F01A</c> and <c>image@0x1F1F4</c>): the glyphs are not
/// cells but <b>columns of one continuous 1-bpp strip</b> <c>bytesPerRow*8</c> pixels wide and
/// <c>height</c> tall; glyph <c>c</c> occupies pixel columns <c>columnOffsets[c]..
/// columnOffsets[c+1]</c>.
/// </para>
/// <para>
/// <b>Why the data PNG is the strip, not a 16×16 sheet.</b>  A sheet of fixed cells cannot hold the
/// columns that belong to no glyph — <c>propbold.fnt</c>'s strip is 800 px wide and the last glyph
/// ends at 796 — so a sheet round trip would silently drop bits.  The strip keeps every bit, and the
/// 16×16 sheet ships alongside it as a <i>view</i> for reading.
/// </para>
/// <para>
/// Two open fields are carried: the u16 at +0x20 (8 / 8 / 10 across the three shipping
/// fonts, no known consumer) and the bytes between the font name's NUL and +0x20, which are
/// <b>not</b> zero padding — <c>4x6.fnt</c> has <c>"tled"</c> and <c>prop3.fnt</c> has
/// <c>76 0E FF 14</c> there, authoring-tool buffer residue.
/// </para>
/// </remarks>
public sealed class FontTransform : IFamilyTransform
{
    /// <summary>The data tree folder this family writes into.</summary>
    public const string Folder = "images/fonts";

    /// <summary>Glyph cells per row in the generated sheet view.</summary>
    public const int SheetColumns = 16;

    /// <inheritdoc/>
    public string Family => "fnt";

    /// <inheritdoc/>
    public string TreeDescription =>
        "`images/fonts/<name>.png` — the font's 1-bit glyph strip exactly as the game stores it " +
        "(all glyphs side by side on one row band); `images/fonts/<name>.json` carries the metrics " +
        "and the 256-entry column table that cuts the strip into glyphs; " +
        "`images/fonts/<name>.sheet.png` is a generated 16x16 reading aid.";

    /// <inheritdoc/>
    public FidelityRule FidelityRule => FidelityRule.Exact;

    /// <inheritdoc/>
    public bool Claims(TransformSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.EntryIndex is not null && source.Extension == ".fnt";
    }

    /// <inheritdoc/>
    public IReadOnlyList<TransformOutput> Forward(TransformSource source, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);

        // The EALIB layer already undid the flag=0x01 framing, so this is the payload itself.
        FontDecoder.Font font = FontDecoder.DecodePayload(source.Content.Span);
        int stripWidth = font.BytesPerRow * 8;
        byte[] strip = Unpack(font.GlyphData, stripWidth, font.Height);

        // The name field's tail is ALWAYS carried, because it is what makes the round trip close —
        // but only its non-zero bytes are *counted* as open, since NUL padding is explained.
        UnknownBytes unknown = new UnknownBytes();
        unknown.Add(0x20, [(byte)font.Field0x20, (byte)(font.Field0x20 >> 8)]);
        unknown.AddIfNonZero(0x0E, font.NameFieldTail);
        bool trailing = unknown.AddIfNonZero(FontDecoder.DataOffset + font.GlyphData.Length, font.Trailing);

        string stem = TransformContext.SafeFileName(source.Stem);
        string stripPath = context.Allocate($"{Folder}/{stem}.png");
        string jsonPath = context.Allocate($"{Folder}/{stem}.json");
        string sheetPath = context.Allocate($"{Folder}/{stem}.sheet.png");

        List<int> offsets = font.OffsetTable.Select(v => (int)v).ToList();
        List<FontGlyphDto> glyphs = font.Glyphs.Values
            .OrderBy(g => g.Codepoint)
            .Select(g => new FontGlyphDto
            {
                Codepoint = g.Codepoint,
                X = font.OffsetTable[g.Codepoint],
                Width = g.Width,
            })
            .ToList();

        FontDto dto = new FontDto
        {
            Format = "cyac.font/1",
            About =
                "A DeluxeFont bitmap font. The .png is the font's own 1-bit glyph " +
                "STRIP: one band of `height` scanlines, `stripWidth` pixels wide, white = ink. " +
                "Glyph c occupies pixel columns columnOffsets[c] .. columnOffsets[c+1] — that " +
                "table is the authoritative metric; \"glyphs\" is derived from it for reading. " +
                "The .sheet.png is a generated 16x16 view and is never read back.",
            Source = $"{source.OriginFile}/{source.Name}",
            Name = font.Name,
            FormatVersion = font.FormatVersionByte,
            BytesPerRow = font.BytesPerRow,
            Height = font.Height,
            MaxGlyphWidth = font.MaxWidth,
            Sentinel = "0x" + font.Sentinel.ToString("X8", CultureInfo.InvariantCulture),
            Strip = stripPath,
            StripWidth = stripWidth,
            Sheet = sheetPath,
            GlyphCount = glyphs.Count,
            ColumnOffsets = offsets,
            Glyphs = glyphs,
            Unknown0x20 = Convert.ToHexString([(byte)font.Field0x20, (byte)(font.Field0x20 >> 8)]),
            Unknown0x0E = font.NameFieldTail.Length == 0 ? null : Convert.ToHexString(font.NameFieldTail),
            UnknownTrailing = trailing ? Convert.ToHexString(font.Trailing) : null,
        };

        return
        [
            new TransformOutput(
                jsonPath,
                JsonSerializer.SerializeToUtf8Bytes(dto, TransformJsonContext.Readable.FontDto),
                OutputRole.Data,
                OutputFidelity.Exact,
                null,
                unknown.Count),
            new TransformOutput(
                stripPath,
                IndexedPng.Encode(stripWidth, font.Height, strip, ImagePalettes.Monochrome.Rgb),
                OutputRole.Data,
                OutputFidelity.Exact),
            TransformOutput.View(sheetPath, RenderSheet(font), jsonPath),
        ];
    }

    /// <inheritdoc/>
    public byte[] Inverse(IReadOnlyList<LoadedOutput> outputs, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        LoadedOutput json = outputs.FirstOrDefault(o => o.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidDataException("fnt expects a .json side-car among its outputs");
        LoadedOutput strip = outputs.FirstOrDefault(
                                 o => o.Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                                      !o.Path.EndsWith(".sheet.png", StringComparison.OrdinalIgnoreCase))
                             ?? throw new InvalidDataException("fnt expects a glyph-strip .png among its outputs");
        return ToBytes(json.Bytes, strip.Bytes);
    }

    /// <summary>Rebuilds the decompressed <c>.fnt</c> payload from the tree's side-car and strip.</summary>
    /// <param name="json">The side-car bytes.</param>
    /// <param name="strip">The glyph-strip PNG's bytes.</param>
    /// <exception cref="InvalidDataException">The pair is inconsistent or malformed.</exception>
    public static byte[] ToBytes(ReadOnlySpan<byte> json, ReadOnlySpan<byte> strip)
    {
        FontDto dto = JsonSerializer.Deserialize(json, TransformJsonContext.Readable.FontDto)
                      ?? throw new InvalidDataException("font JSON is empty");
        List<int> offsets = dto.ColumnOffsets
                            ?? throw new InvalidDataException("font JSON has no \"columnOffsets\"");
        (int width, int height, byte[] pixels, _) = IndexedPng.Decode(strip);
        if (width != dto.StripWidth || height != dto.Height)
        {
            throw new InvalidDataException(
                $"the strip is {width}x{height} but the side-car says {dto.StripWidth}x{dto.Height}");
        }

        return FontEncoder.Encode(
            (byte)dto.FormatVersion,
            dto.Name ?? string.Empty,
            string.IsNullOrEmpty(dto.Unknown0x0E) ? [] : Convert.FromHexString(dto.Unknown0x0E),
            Word(dto.Unknown0x20),
            dto.BytesPerRow,
            height,
            dto.MaxGlyphWidth,
            uint.Parse((dto.Sentinel ?? "0x0")[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            [.. offsets.Select(v => (ushort)v)],
            Pack(pixels, width, height),
            string.IsNullOrEmpty(dto.UnknownTrailing) ? [] : Convert.FromHexString(dto.UnknownTrailing));
    }

    private static int Word(string? hex)
    {
        if (string.IsNullOrEmpty(hex))
        {
            return 0;
        }

        byte[] bytes = Convert.FromHexString(hex);
        return bytes.Length == 2
            ? bytes[0] | (bytes[1] << 8)
            : throw new InvalidDataException($"expected two hex bytes, got \"{hex}\"");
    }

    private static byte[] Unpack(ReadOnlySpan<byte> packed, int width, int height)
    {
        byte[] pixels = new byte[width * height];
        int pitch = width / 8;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                pixels[(y * width) + x] = (byte)((packed[(y * pitch) + (x >> 3)] >> (7 - (x & 7))) & 1);
            }
        }

        return pixels;
    }

    private static byte[] Pack(ReadOnlySpan<byte> pixels, int width, int height)
    {
        int pitch = width / 8;
        byte[] packed = new byte[pitch * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (pixels[(y * width) + x] != 0)
                {
                    packed[(y * pitch) + (x >> 3)] |= (byte)(1 << (7 - (x & 7)));
                }
            }
        }

        return packed;
    }

    // The reading aid: every glyph drawn into a fixed cell, 16 per row, in codepoint order.
    private static byte[] RenderSheet(FontDecoder.Font font)
    {
        int cellWidth = Math.Max(1, font.MaxWidth) + 1;
        int cellHeight = font.Height + 1;
        int width = SheetColumns * cellWidth;
        int height = 16 * cellHeight;
        byte[] sheet = new byte[width * height];

        foreach (FontDecoder.Glyph glyph in font.Glyphs.Values)
        {
            int cellX = (glyph.Codepoint % SheetColumns) * cellWidth;
            int cellY = (glyph.Codepoint / SheetColumns) * cellHeight;
            if (cellY + font.Height > height)
            {
                continue;
            }

            for (int row = 0; row < glyph.Height; row++)
            {
                for (int col = 0; col < Math.Min(glyph.Width, cellWidth); col++)
                {
                    sheet[((cellY + row) * width) + cellX + col] = glyph.At(row, col);
                }
            }
        }

        return IndexedPng.Encode(width, height, sheet, ImagePalettes.Monochrome.Rgb);
    }
}
