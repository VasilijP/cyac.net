using System.Text.Json.Serialization;

namespace CYAC.Port.Transform.Families;

// Wire format of cockpits/<aircraft>_<mode>.json.  The pack is executable code (transform-plan law
// L6), so the document holds the GENERATOR PARAMETERS the code was built from — the cockpit's opaque
// spans, their pixels (in the sibling .png) and the element count — not a translation of the code.

/// <summary>Wire format of <c>cockpits/&lt;aircraft&gt;_&lt;mode&gt;.json</c>.</summary>
internal sealed class CockpitPackDto
{
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    [JsonPropertyName("about")]
    public string? About { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    /// <summary>The aircraft this cockpit belongs to, from the file name's first half.</summary>
    [JsonPropertyName("aircraft")]
    public string? Aircraft { get; init; }

    /// <summary>The video mode: <c>Vga</c>, <c>Mcga</c>, <c>Cga</c>, <c>Tandy</c> or <c>Ega</c>.</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    /// <summary>The <c>g_cfg_sub_mode [0x15E]</c> value that selects this pack.</summary>
    [JsonPropertyName("subMode")]
    public int SubMode { get; init; }

    /// <summary>Whether this build can regenerate the pack from the parameters below.</summary>
    [JsonPropertyName("regenerated")]
    public bool Regenerated { get; init; }

    [JsonPropertyName("header")]
    public CockpitHeaderDto? Header { get; init; }

    [JsonPropertyName("regions")]
    public IReadOnlyList<CockpitRegionDto>? Regions { get; init; }

    /// <summary>How many entries the (inert) element list carries.</summary>
    [JsonPropertyName("elementCount")]
    public int ElementCount { get; init; }

    /// <summary>The element list's bytes, carried only when it is not the identity permutation.</summary>
    [JsonPropertyName("elementBytes")]
    public string? ElementBytes { get; init; }

    /// <summary>The PNG holding this cockpit's palette indices, when the pack was simulated.</summary>
    [JsonPropertyName("pixels")]
    public string? Pixels { get; init; }

    [JsonPropertyName("palette")]
    public PaletteBindingDto? Palette { get; init; }

    /// <summary>The opaque spans, <c>[row, x, length]</c> each — the cockpit's alpha channel.</summary>
    [JsonPropertyName("runs")]
    public IReadOnlyList<IReadOnlyList<int>>? Runs { get; init; }

    /// <summary>The first and last screen rows the pack paints, when it was simulated.</summary>
    [JsonPropertyName("paintedRows")]
    public IReadOnlyList<int>? PaintedRows { get; init; }

    /// <summary>Opaque pixels, when the pack was simulated.</summary>
    [JsonPropertyName("pixelCount")]
    public int PixelCount { get; init; }

    /// <summary>The files carrying regions this build does not model, for a mode it cannot regenerate.</summary>
    [JsonPropertyName("carried")]
    public IReadOnlyList<string>? Carried { get; init; }

    /// <summary>What is and is not explained about this pack, in one sentence.</summary>
    [JsonPropertyName("accounting")]
    public string? Accounting { get; init; }
}

/// <summary>The pack's header words — entry points, not counts.</summary>
internal sealed class CockpitHeaderDto
{
    /// <summary>Header word 0: the per-frame paint entry, called once per frame with the draw page's segment.</summary>
    [JsonPropertyName("paintEntry")]
    public string? PaintEntry { get; init; }

    /// <summary>Header word 1: the element list's offset.</summary>
    [JsonPropertyName("elementListOffset")]
    public string? ElementListOffset { get; init; }

    /// <summary>Header word 2 (VGA): the load-time init entry, whose real job is the +0x08 self-patch.</summary>
    [JsonPropertyName("initEntry")]
    public string? InitEntry { get; init; }

    /// <summary>Header word 3 (VGA): a fourth entry slot holding a far-nop stub.</summary>
    [JsonPropertyName("unusedEntry")]
    public string? UnusedEntry { get; init; }

    /// <summary>Header words 4/5 (VGA): the pixel-data far pointer, 0:0 on disk and self-patched at load.</summary>
    [JsonPropertyName("dataPointer")]
    public ExeFarPointerDto? DataPointer { get; init; }
}

/// <summary>One region of a pack.</summary>
internal sealed class CockpitRegionDto
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("offset")]
    public string? Offset { get; init; }

    [JsonPropertyName("bytes")]
    public int Bytes { get; init; }
}
