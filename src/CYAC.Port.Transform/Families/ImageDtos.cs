using System.Text.Json.Serialization;

namespace CYAC.Port.Transform.Families;

// Wire formats of the image side-cars.  In every one of them the sibling .png holds the PIXEL
// INDICES (transform-plan L5) and this document holds everything else the inverse needs, so the two
// files together reproduce the stored asset byte for byte.

/// <summary>Where an image's palette came from (see <see cref="ImagePalettes"/>).</summary>
internal sealed class PaletteBindingDto
{
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("binding")]
    public string? Binding { get; init; }
}

/// <summary>Wire format of <c>images/&lt;name&gt;.json</c> for a <c>.pic</c>.</summary>
internal sealed class PicDto
{
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    [JsonPropertyName("about")]
    public string? About { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    [JsonPropertyName("modeValue")]
    public int ModeValue { get; init; }

    [JsonPropertyName("bitsPerPixel")]
    public int BitsPerPixel { get; init; }

    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }

    [JsonPropertyName("wordPitch")]
    public int WordPitch { get; init; }

    [JsonPropertyName("declaredSize")]
    public int DeclaredSize { get; init; }

    [JsonPropertyName("pixels")]
    public string? Pixels { get; init; }

    [JsonPropertyName("palette")]
    public PaletteBindingDto? Palette { get; init; }

    /// <summary>The 4 reserved header bytes at +0x0C, carried only when they are not zero.</summary>
    [JsonPropertyName("unknown_0x0C")]
    public string? Unknown0x0C { get; init; }
}

/// <summary>Where a headerless <c>.msk</c> got its width and height.</summary>
internal sealed class MaskGeometryDto
{
    [JsonPropertyName("from")]
    public string? From { get; init; }

    [JsonPropertyName("citation")]
    public string? Citation { get; init; }

    [JsonPropertyName("aircraft")]
    public string? Aircraft { get; init; }

    [JsonPropertyName("aircraftIndex")]
    public int? AircraftIndex { get; init; }

    [JsonPropertyName("region")]
    public string? Region { get; init; }

    [JsonPropertyName("screenX")]
    public int? ScreenX { get; init; }

    [JsonPropertyName("screenY")]
    public int? ScreenY { get; init; }

    [JsonPropertyName("companion")]
    public string? Companion { get; init; }
}

/// <summary>Wire format of <c>images/masks/&lt;name&gt;.json</c>.</summary>
internal sealed class MaskDto
{
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    [JsonPropertyName("about")]
    public string? About { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }

    [JsonPropertyName("bitsPerPixel")]
    public int BitsPerPixel { get; init; }

    [JsonPropertyName("pitchBytes")]
    public int PitchBytes { get; init; }

    [JsonPropertyName("pixels")]
    public string? Pixels { get; init; }

    [JsonPropertyName("geometry")]
    public MaskGeometryDto? Geometry { get; init; }

    /// <summary>The row-padding bits past the last pixel column, when the width is not a multiple of 8.</summary>
    [JsonPropertyName("unknown_padBits")]
    public string? UnknownPadBits { get; init; }
}

/// <summary>Wire format of <c>images/&lt;name&gt;.json</c> for a <c>.rle</c>.</summary>
internal sealed class RleDto
{
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    [JsonPropertyName("about")]
    public string? About { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }

    [JsonPropertyName("frames")]
    public int Frames { get; init; }

    [JsonPropertyName("colorKey")]
    public int ColorKey { get; init; }

    [JsonPropertyName("formatFlags")]
    public int FormatFlags { get; init; }

    [JsonPropertyName("pixels")]
    public string? Pixels { get; init; }

    [JsonPropertyName("palette")]
    public PaletteBindingDto? Palette { get; init; }

    /// <summary>The unused high byte of the header's colour-key word (+0x04).</summary>
    [JsonPropertyName("unknown_0x05")]
    public string? Unknown0x05 { get; init; }

    /// <summary>The unused high byte of the header's format-flags word (+0x06).</summary>
    [JsonPropertyName("unknown_0x07")]
    public string? Unknown0x07 { get; init; }
}

/// <summary>One glyph's box inside the font's unified strip — derived from <c>columnOffsets</c>.</summary>
internal sealed class FontGlyphDto
{
    [JsonPropertyName("codepoint")]
    public int Codepoint { get; init; }

    [JsonPropertyName("x")]
    public int X { get; init; }

    [JsonPropertyName("width")]
    public int Width { get; init; }
}

/// <summary>Wire format of <c>images/fonts/&lt;name&gt;.json</c>.</summary>
internal sealed class FontDto
{
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    [JsonPropertyName("about")]
    public string? About { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; init; }

    [JsonPropertyName("bytesPerRow")]
    public int BytesPerRow { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }

    [JsonPropertyName("maxGlyphWidth")]
    public int MaxGlyphWidth { get; init; }

    [JsonPropertyName("sentinel")]
    public string? Sentinel { get; init; }

    [JsonPropertyName("strip")]
    public string? Strip { get; init; }

    [JsonPropertyName("stripWidth")]
    public int StripWidth { get; init; }

    [JsonPropertyName("sheet")]
    public string? Sheet { get; init; }

    [JsonPropertyName("glyphCount")]
    public int GlyphCount { get; init; }

    /// <summary>The authoritative 256-entry pixel-column offset table.</summary>
    [JsonPropertyName("columnOffsets")]
    public List<int>? ColumnOffsets { get; init; }

    /// <summary>Derived from <see cref="ColumnOffsets"/> for readability; the inverse ignores it.</summary>
    [JsonPropertyName("glyphs")]
    public List<FontGlyphDto>? Glyphs { get; init; }

    /// <summary>The u16 at +0x20 whose meaning is still open (8 / 8 / 10 in the shipping fonts).</summary>
    [JsonPropertyName("unknown_0x20")]
    public string? Unknown0x20 { get; init; }

    /// <summary>Authoring-tool residue between the name's NUL and +0x20; not always zero.</summary>
    [JsonPropertyName("unknown_0x0E")]
    public string? Unknown0x0E { get; init; }

    /// <summary>Bytes after the glyph bitmap, when a font has any.</summary>
    [JsonPropertyName("unknown_trailing")]
    public string? UnknownTrailing { get; init; }
}
