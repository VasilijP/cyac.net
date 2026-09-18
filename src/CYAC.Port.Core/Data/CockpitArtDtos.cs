using System.Text.Json.Serialization;

namespace CYAC.Port.Core.Data;

/// <summary>
/// The runtime's read-only view of a COCKPIT COMPOSITOR PACK document
/// (<c>cockpits/&lt;aircraft&gt;_&lt;mode&gt;.json</c>) — the opaque spans the 1991 module repaints
/// over the 3-D viewport every frame.
/// </summary>
/// <remarks>
/// <para>
/// The pack itself is 8086 machine code that ships in <c>3a.lib</c> and is LCALLed once per frame
/// with the draw page's segment (<c>cockpit_sprites_post_blit @image@0x0E9BB</c>).  Round 84 solved
/// it constructively: the program is one straight-line paint of a set of opaque spans, so
/// <c>cyac-transform</c> publishes the SPANS plus the pixels and regenerates the module byte for
/// byte.  That span set is exactly the cockpit's ALPHA CHANNEL over the world — the canopy arch and
/// rails that overlap the viewport — which is all the port needs.
/// </para>
/// <para>
/// Only the fields the renderer reads are declared; the document carries more (the module's entry
/// points, its region map, the accounting line) and unknown members are ignored on read.
/// </para>
/// </remarks>
public sealed class CockpitPackDocumentDto
{
    /// <summary>The document's format tag.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>The EALIB member the pack came from.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    /// <summary>The aircraft's cockpit-asset suffix (<c>"51"</c>).</summary>
    [JsonPropertyName("aircraft")]
    public string? Aircraft { get; init; }

    /// <summary>The video mode the pack was built for (<c>"Vga"</c>).</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    /// <summary>The <c>g_cfg_sub_mode [0x015E]</c> value that selects it — 6 for VGA Mode-X.</summary>
    [JsonPropertyName("subMode")]
    public int SubMode { get; init; }

    /// <summary>The tree-relative path of the palette-index PNG the spans read their pixels from.</summary>
    [JsonPropertyName("pixels")]
    public string? Pixels { get; init; }

    /// <summary>The opaque spans, each <c>[row, x, length]</c>.</summary>
    [JsonPropertyName("runs")]
    public List<List<int>>? Runs { get; init; }

    /// <summary>The first and last screen row the pack paints.</summary>
    [JsonPropertyName("paintedRows")]
    public List<int>? PaintedRows { get; init; }

    /// <summary>How many opaque pixels the spans cover.</summary>
    [JsonPropertyName("pixelCount")]
    public int PixelCount { get; init; }
}

/// <summary>One dial slot of one aircraft's cockpit (<c>dialinit.json</c>).</summary>
/// <remarks>
/// <c>cockpit_layout_load_per_aircraft @image@0x01DAA</c> copies the flown aircraft's section into
/// the ten <c>s_dial_instrument_record</c> slots, record <c>i</c> into slot <c>i</c>.  The VALUE each
/// slot is fed names the instrument: 0 altimeter, 1 VSI, 2 airspeed, 3 compass,
/// 4 bearing pointer, 5 brake, 6 fuel, 7/8/9 percentage meters.
/// </remarks>
public sealed class DialSlotDto
{
    /// <summary>The slot index 0..9.</summary>
    [JsonPropertyName("slot")]
    public int Slot { get; init; }

    /// <summary>Whether this aircraft has the instrument at all.</summary>
    [JsonPropertyName("present")]
    public bool Present { get; init; }

    /// <summary>The instrument's rectangle <c>[x, y, w, h]</c> in 320×200 screen pixels.</summary>
    [JsonPropertyName("rect")]
    public List<int>? Rect { get; init; }

    /// <summary>Where the needle turns, <c>[x, y]</c>.</summary>
    [JsonPropertyName("pivot")]
    public List<int>? Pivot { get; init; }

    /// <summary>
    /// The style word <c>s_dial_instrument_record +0x0C</c>: <c>0x0C0C</c> analog needle,
    /// <c>0x0404</c> text, <c>0x0909</c> compass, <c>0x0F0F</c> the MiG-15's radar.
    /// </summary>
    [JsonPropertyName("kindWord")]
    public string? KindWord { get; init; }

    /// <summary>The low end of the value range (<c>+0x0E</c>).</summary>
    [JsonPropertyName("param0")]
    public int Param0 { get; init; }

    /// <summary>The high end of the value range (<c>+0x10</c>).</summary>
    [JsonPropertyName("param1")]
    public int Param1 { get; init; }

    /// <summary>The angle added after the range map, in the engine's ⅛-degree BAM (<c>+0x12</c>).</summary>
    [JsonPropertyName("needleAngleOffset")]
    public int NeedleAngleOffset { get; init; }

    /// <summary>How many keyframe endpoints the needle has (<c>+0x14</c>).</summary>
    [JsonPropertyName("keyframeCount")]
    public int KeyframeCount { get; init; }

    /// <summary>The needle's length in pixels (<c>+0x1E</c>).</summary>
    [JsonPropertyName("amplitude")]
    public int Amplitude { get; init; }

    /// <summary>The per-tick animation step (<c>+0x3E</c>).</summary>
    [JsonPropertyName("sweepStep")]
    public int SweepStep { get; init; }

    /// <summary>Non-zero negates the mapped angle before the offset (<c>+0x46</c>).</summary>
    [JsonPropertyName("directionInvert")]
    public int DirectionInvert { get; init; }
}

/// <summary>One aircraft's ten dial slots.</summary>
public sealed class DialCockpitDto
{
    /// <summary>The aircraft's index in <c>g_active_aircraft_idx</c> order.</summary>
    [JsonPropertyName("aircraftIndex")]
    public int AircraftIndex { get; init; }

    /// <summary>Its ten slots, in slot order.</summary>
    [JsonPropertyName("instruments")]
    public List<DialSlotDto>? Instruments { get; init; }
}

/// <summary>The cockpit instrument layout of all six flyable aircraft (<c>dialinit.json</c>).</summary>
public sealed class DialInitDocumentDto
{
    /// <summary>The document's format tag.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>The EALIB member it came from.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    /// <summary>The ten slot names, in slot order.</summary>
    [JsonPropertyName("_slotNames")]
    public List<string>? SlotNames { get; init; }

    /// <summary>One section per aircraft.</summary>
    [JsonPropertyName("cockpits")]
    public List<DialCockpitDto>? Cockpits { get; init; }
}

/// <summary>One glyph of a bitmap font: where it sits in the strip and how wide it is.</summary>
public sealed class FontGlyphDto
{
    /// <summary>The character it draws.</summary>
    [JsonPropertyName("codepoint")]
    public int Codepoint { get; init; }

    /// <summary>Its first column in the strip.</summary>
    [JsonPropertyName("x")]
    public int X { get; init; }

    /// <summary>Its width in pixels, which is also its advance.</summary>
    [JsonPropertyName("width")]
    public int Width { get; init; }
}

/// <summary>
/// One of the game's DeluxeFont bitmap fonts (<c>images/fonts/&lt;name&gt;.json</c>).
/// </summary>
/// <remarks>
/// The <c>.png</c> beside it is the font's own 1-bit glyph STRIP: one band of <see cref="Height"/>
/// scanlines, <see cref="StripWidth"/> pixels wide, ink where the pixel is non-zero.
/// </remarks>
public sealed class FontDocumentDto
{
    /// <summary>The document's format tag.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>The font's name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>The glyph height in scanlines.</summary>
    [JsonPropertyName("height")]
    public int Height { get; init; }

    /// <summary>The strip's width in pixels.</summary>
    [JsonPropertyName("stripWidth")]
    public int StripWidth { get; init; }

    /// <summary>The tree-relative path of the strip image.</summary>
    [JsonPropertyName("strip")]
    public string? Strip { get; init; }

    /// <summary>Every glyph the font carries.</summary>
    /// <remarks>
    /// The <c>codepoint</c> labels here are ONE TOO HIGH: rendering the entry labelled <c>0x41</c>
    /// draws a <c>B</c>, <c>0x30</c> draws a <c>1</c> and <c>0x2D</c> draws a <c>.</c>. The
    /// <c>.fnt</c>'s column table is a running list of glyph END columns, so character <c>c</c>
    /// occupies <c>columnOffsets[c − 1].. columnOffsets[c]</c> and not <c>columnOffsets[c]..
    /// columnOffsets[c + 1]</c>.  Read <see cref="ColumnOffsets"/> instead — see
    /// <c>CYAC.Port.Render.Cockpit.CockpitFont</c>.
    /// </remarks>
    [JsonPropertyName("glyphs")]
    public List<FontGlyphDto>? Glyphs { get; init; }

    /// <summary>
    /// The font's own 256-entry column table — the END column of each character's glyph in the strip.
    /// </summary>
    [JsonPropertyName("columnOffsets")]
    public List<int>? ColumnOffsets { get; init; }
}
