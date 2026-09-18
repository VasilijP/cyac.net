using System.Text.Json.Serialization;

namespace CYAC.Port.Core.Data;

// The runtime's READ views of the tree's palette, image and mesh documents.
//
// They are deliberately leaner than the writer's models in CYAC.Port.Transform: the tool's job is to
// account for every byte of the original, so its DTOs carry the layout fields that make a document
// invertible (stored pitches, declared sizes, residue spans).  The port needs the CONTENT — the
// colours, the pixels and their palette binding, the vertices — so these declare that, and
// System.Text.Json ignores the rest.  The tests read the tool's real output through these, which is
// what keeps the two halves honest.
//
// (Consolidating the two sides into one set of wire formats, as T5 did for the exe tables and T7 did
// for scenarios and audio, is a cheap follow-up)

/// <summary>Read view of <c>palettes/&lt;name&gt;.json</c>.</summary>
public sealed class PaletteDocumentDto
{
    /// <summary>Document format tag.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>Where the bytes came from.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    /// <summary>Significant bits per component as stored: 6, the VGA DAC width.</summary>
    [JsonPropertyName("componentBits")]
    public int ComponentBits { get; init; }

    /// <summary>How many colours the palette holds.</summary>
    [JsonPropertyName("colorCount")]
    public int ColorCount { get; init; }

    /// <summary>The colours, as raw stored <c>[r, g, b]</c> triples in palette-index order.</summary>
    [JsonPropertyName("colors")]
    public List<List<int>>? Colors { get; init; }

    /// <summary>The 8-bit RGB the DAC produces from a stored component: <c>v&lt;&lt;2 | v&gt;&gt;4</c>.</summary>
    /// <param name="component">A stored component; only its low 6 bits reach the DAC.</param>
    public static byte ToEightBit(int component)
    {
        int v = component & 0x3F;
        return (byte)((v << 2) | (v >> 4));
    }
}

/// <summary>Which palette an image's indices are meant to be read against.</summary>
public sealed class PaletteBindingDto
{
    /// <summary>Where the palette comes from — an asset name or a <c>platform</c> citation.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    /// <summary>How firm the binding is: a companion <c>.pal</c>, the global palette, or a default.</summary>
    [JsonPropertyName("binding")]
    public string? Binding { get; init; }
}

/// <summary>
/// Read view of the tree's image documents — <c>.pic</c>, <c>.msk</c>, <c>.rle</c> and <c>.fnt</c>.
/// </summary>
/// <remarks>
/// All four families write the same shape at this level: the pixels live in a sibling 8-bit indexed
/// PNG (the indices ARE the data) and this document says how to read them.  A family that
/// has no palette of its own — a mask is 1 bit per pixel — leaves <see cref="Palette"/> null.
/// </remarks>
public sealed class ImageDocumentDto
{
    /// <summary>Document format tag, e.g. <c>cyac.image.pic/1</c>.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>Where the bytes came from.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    /// <summary>Width in pixels.</summary>
    [JsonPropertyName("width")]
    public int Width { get; init; }

    /// <summary>Height in pixels.</summary>
    [JsonPropertyName("height")]
    public int Height { get; init; }

    /// <summary>The original's storage mode, where the family has one.</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    /// <summary>Bits per pixel in the ORIGINAL, which may be fewer than the PNG's eight.</summary>
    [JsonPropertyName("bitsPerPixel")]
    public int BitsPerPixel { get; init; }

    /// <summary>The sibling PNG holding the palette indices, tree-relative.</summary>
    [JsonPropertyName("pixels")]
    public string? Pixels { get; init; }

    /// <summary>Which palette the indices are meant to be read against.</summary>
    [JsonPropertyName("palette")]
    public PaletteBindingDto? Palette { get; init; }

    /// <summary>
    /// The palette index that is SEE-THROUGH, where the family has one (the <c>rle</c>
    /// documents' <c>"colorKey"</c>; <c>data/images/exp.json</c> says 224).
    /// </summary>
    /// <remarks>
    /// The field was already in the documents the transform writes (<c>RleTransform</c>); it was
    /// simply not read back, so a consumer had to hardcode the number.  Additive and read-only.
    /// </remarks>
    [JsonPropertyName("colorKey")]
    public int? ColorKey { get; init; }
}

/// <summary>One level of detail of a <c>.PNT</c> mesh.</summary>
public sealed class PntLodDocumentDto
{
    /// <summary>
    /// The LOD's index, 0..2.  Index 0 is the FARTHEST / COARSEST:
    /// <c>CYAC.Formats/Mesh/SceneryFootprint.cs</c> §2 "WHICH LOD — the COARSEST, not the densest …
    /// index 0 is the FARTHEST", and the shipped data says the same (<c>p51</c> LOD0 = 8 vertices /
    /// 3 records, LOD2 = 106 vertices / 76 records).
    /// </summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary>The LOD's directory flag, as hex.</summary>
    [JsonPropertyName("flag")]
    public string? Flag { get; init; }

    /// <summary>The vertices, as <c>[x, y, z]</c> integer triples.</summary>
    [JsonPropertyName("vertices")]
    public List<List<int>>? Vertices { get; init; }

    /// <summary>One byte per vertex: that vertex's parent in the wireframe edge tree (255 = root).</summary>
    [JsonPropertyName("edgeParents")]
    public List<int>? EdgeParents { get; init; }

    /// <summary>One colour byte per shape record of the matching executable-resident LOD.</summary>
    [JsonPropertyName("faceColors")]
    public List<int>? FaceColors { get; init; }
}

/// <summary>Read view of <c>meshes/&lt;name&gt;.json</c> — a <c>.PNT</c> mesh's geometry.</summary>
public sealed class PntMeshDocumentDto
{
    /// <summary>Document format tag.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>Where the bytes came from.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    /// <summary>The mesh's basename, e.g. <c>bridge</c>.</summary>
    [JsonPropertyName("basename")]
    public string? Basename { get; init; }

    /// <summary>The sections in the order the file lays them out.</summary>
    [JsonPropertyName("layout")]
    public List<string>? Layout { get; init; }

    /// <summary>
    /// The levels of detail, see <see cref="PntLodDocumentDto.Index"/>: coarsest
    /// first.
    /// </summary>
    [JsonPropertyName("lods")]
    public List<PntLodDocumentDto>? Lods { get; init; }
}
