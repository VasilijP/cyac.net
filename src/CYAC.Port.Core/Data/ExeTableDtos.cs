using System.Text.Json.Serialization;

namespace CYAC.Port.Core.Data;

// Wire formats for the exe-resident data documents `cyac-transform` writes under <data>/exe/ and
// CYAC.Port.Core loads at run time (the tables are EXTRACTED into the tree, never embedded
// in this source).
//
// These types are the SHARED CONTRACT between the transform (which writes them and re-encodes them
// back into the exact image bytes for --verify) and the runtime (which reads them).  They live here
// so the runtime never depends on the tool.

/// <summary>
/// Where a transformed table came from in the original executable.
/// </summary>
/// <remarks>
/// Every field is knowledge, not data: an <c>image@</c> offset into the unpacked layer-1 image, the
/// DGROUP offset when the table lives in DGROUP (base <c>image@0x3BD60</c>), the <c>seg:off</c> far
/// address when the original addresses it through a far pointer, and the extent.  It is what makes
/// the round trip checkable — <c>--verify</c> re-encodes the document and diffs it against exactly
/// this slice of <c>exe/image.l1.bin</c>.
/// </remarks>
public sealed class DataSourceDto
{
    /// <summary>Byte offset into the unpacked L1 image, as <c>"0xNNNNN"</c>.</summary>
    [JsonPropertyName("image")]
    public string? Image { get; init; }

    /// <summary>DGROUP offset as <c>"0xNNNN"</c>, when the table is DGROUP-resident.</summary>
    [JsonPropertyName("dgroup")]
    public string? Dgroup { get; init; }

    /// <summary>The original's own far address, e.g. <c>"0x4438:0005"</c>, when it uses one.</summary>
    [JsonPropertyName("farAddress")]
    public string? FarAddress { get; init; }

    /// <summary>How many bytes the table occupies.</summary>
    [JsonPropertyName("bytes")]
    public int Bytes { get; init; }

    /// <summary>How many entries it holds, when it is an array.</summary>
    [JsonPropertyName("entries")]
    public int? Entries { get; init; }

    /// <summary>The element's type in the project's vocabulary (<c>u8</c>, <c>i16</c>, a struct name).</summary>
    [JsonPropertyName("elementType")]
    public string? ElementType { get; init; }
}

/// <summary>
/// A run of bytes at a known offset — the law-L4 carrier for whatever a field model does not
/// reproduce.
/// </summary>
/// <param name="Offset">The span's offset inside the region, as <c>"0xNN"</c>.</param>
/// <param name="Hex">Its bytes, upper-case hex.</param>
public sealed record ByteSpanDto(
    [property: JsonPropertyName("offset")] string Offset,
    [property: JsonPropertyName("hex")] string Hex);

/// <summary>
/// A proven closed-form generator for a table, when one exists.
/// </summary>
/// <remarks>
/// Law L1 allows a <b>proven</b> formula to be computed rather than shipped: the arctangent octant
/// table has one (B2), the quarter-sine table provably has none.  The table is extracted either
/// way — the generator claim is recorded so a reader can check it, and
/// <c>Atan2TableTests</c> re-derives the computed table and diffs it against the extracted one.
/// </remarks>
public sealed class TableGeneratorDto
{
    /// <summary>The formula, in ordinary mathematical notation.</summary>
    [JsonPropertyName("formula")]
    public string? Formula { get; init; }

    /// <summary>True when the formula reproduces every entry exactly.</summary>
    [JsonPropertyName("exact")]
    public bool Exact { get; init; }

    /// <summary>What was measured, and against what.</summary>
    [JsonPropertyName("note")]
    public string? Note { get; init; }
}

/// <summary>
/// A one-dimensional table of 16-bit words — the trigonometric tables under <c>exe/tables/</c>.
/// </summary>
public sealed class ScalarTableDto
{
    /// <summary>Document kind and version, e.g. <c>"cyac.table.sineQuarter/1"</c>.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>What the table is and who reads it, in prose, with citations.</summary>
    [JsonPropertyName("about")]
    public string? About { get; init; }

    /// <summary>Where it came from.</summary>
    [JsonPropertyName("source")]
    public DataSourceDto? Source { get; init; }

    /// <summary>Its generator, when one is proven; absent when none exists.</summary>
    [JsonPropertyName("generator")]
    public TableGeneratorDto? Generator { get; init; }

    /// <summary>The entries, in table order.</summary>
    [JsonPropertyName("values")]
    public List<int>? Values { get; init; }
}

/// <summary>
/// The difficulty-indexed hit-probability table, <c>g_hit_probability_by_difficulty [0x45EA]</c>.
/// </summary>
public sealed class HitProbabilityTableDto
{
    /// <summary>Document kind and version.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>What the table is and who reads it.</summary>
    [JsonPropertyName("about")]
    public string? About { get; init; }

    /// <summary>Where it came from.</summary>
    [JsonPropertyName("source")]
    public DataSourceDto? Source { get; init; }

    /// <summary>One entry per difficulty level, indexed by <c>g_briefing_difficulty_idx [0xF10E]</c>.</summary>
    [JsonPropertyName("byDifficulty")]
    public List<int>? ByDifficulty { get; init; }
}

/// <summary>One entry of one aircraft's player-damage weight table.</summary>
public sealed class PlayerDamageWeightDto
{
    /// <summary>The entry's index, which is also the damage-effect index.</summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary>The effect's port name, or <c>"none"</c> for the weighted no-effect slot 24.</summary>
    [JsonPropertyName("effect")]
    public string? Effect { get; init; }

    /// <summary>The roulette weight — the low byte the walk at <c>image@0x0F811</c> accumulates.</summary>
    [JsonPropertyName("weight")]
    public int Weight { get; init; }

    /// <summary>
    /// The entry's odd byte.  The walk reads a <b>byte</b> weight and advances by <b>two</b>, so this
    /// byte is a parallel column with no identified reader — open, carried, counted.
    /// </summary>
    [JsonPropertyName("unknown_0x01")]
    public string? Unknown0x01 { get; init; }
}

/// <summary>One flyable aircraft's 25-entry player-damage weight table.</summary>
public sealed class PlayerDamageTableDto
{
    /// <summary>The aircraft's asset basename, in <c>g_active_aircraft_idx</c> order.</summary>
    [JsonPropertyName("aircraft")]
    public string? Aircraft { get; init; }

    /// <summary>The aircraft index the pointer array is indexed by.</summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary>The table's DGROUP offset.</summary>
    [JsonPropertyName("dgroup")]
    public string? Dgroup { get; init; }

    /// <summary>The 25 weight entries.</summary>
    [JsonPropertyName("weights")]
    public List<PlayerDamageWeightDto>? Weights { get; init; }
}

/// <summary>
/// The six per-aircraft player-damage weight tables plus the pointer array that selects them.
/// </summary>
public sealed class PlayerDamageTablesDto
{
    /// <summary>Document kind and version.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>What the tables are and who reads them.</summary>
    [JsonPropertyName("about")]
    public string? About { get; init; }

    /// <summary>Where the whole block came from (tables plus pointer array, contiguous).</summary>
    [JsonPropertyName("source")]
    public DataSourceDto? Source { get; init; }

    /// <summary>The exclusive upper bound of the roulette roll: <c>prng_rand_bounded(100)</c>.</summary>
    [JsonPropertyName("rollRange")]
    public int RollRange { get; init; }

    /// <summary>The six near pointers of <c>[0x4596]</c>, in aircraft order.</summary>
    [JsonPropertyName("pointerArray")]
    public List<string>? PointerArray { get; init; }

    /// <summary>The tables themselves, in aircraft order.</summary>
    [JsonPropertyName("tables")]
    public List<PlayerDamageTableDto>? Tables { get; init; }
}

/// <summary>One entry of the game's own scancode-to-ASCII translation table.</summary>
public sealed class ScancodeEntryDto
{
    /// <summary>The table index: <c>0x00..0x7F</c> unshifted, <c>0x80..0xFF</c> shifted.</summary>
    [JsonPropertyName("index")]
    public string? Index { get; init; }

    /// <summary>The byte the translation yields.</summary>
    [JsonPropertyName("ascii")]
    public int Ascii { get; init; }

    /// <summary>The printable character, when the byte is one.</summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }
}

/// <summary>
/// The 256-byte scancode-to-ASCII table at <c>image@0x3501A</c>.
/// </summary>
public sealed class ScancodeTableDto
{
    /// <summary>Document kind and version.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>What the table is and who reads it.</summary>
    [JsonPropertyName("about")]
    public string? About { get; init; }

    /// <summary>Where it came from.</summary>
    [JsonPropertyName("source")]
    public DataSourceDto? Source { get; init; }

    /// <summary>The 256 entries, in index order.</summary>
    [JsonPropertyName("entries")]
    public List<ScancodeEntryDto>? Entries { get; init; }
}

/// <summary>One record of <c>g_film_review_widget_table</c> — the original <c>s_ui_widget</c>.</summary>
public sealed class UiWidgetDto
{
    /// <summary>The record's index in the table.</summary>
    [JsonPropertyName("record")]
    public int Record { get; init; }

    /// <summary>The record's DGROUP offset.</summary>
    [JsonPropertyName("dgroup")]
    public string? Dgroup { get; init; }

    /// <summary><c>+0x00</c> — widget id / the character returned to the caller on a click.</summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary><c>+0x01</c> — accelerator key.</summary>
    [JsonPropertyName("keyShortcut")]
    public string? KeyShortcut { get; init; }

    /// <summary><c>+0x02/+0x04</c> — the custom-draw far pointer; custom draw fires iff its segment is non-zero.</summary>
    [JsonPropertyName("customDrawFn")]
    public FarPointerDto? CustomDrawFn { get; init; }

    /// <summary><c>+0x06</c> — negative = a <c>strings.bin</c> index, non-negative = a literal near pointer.</summary>
    [JsonPropertyName("labelStringRef")]
    public int LabelStringRef { get; init; }

    /// <summary><c>+0x08..+0x0E</c> — the bounding rectangle passed to <c>point_in_rect</c>.</summary>
    [JsonPropertyName("rect")]
    public List<int>? Rect { get; init; }

    /// <summary><c>+0x10</c> — the second accelerator's ASCII half.</summary>
    [JsonPropertyName("keyShortcutAltAscii")]
    public string? KeyShortcutAltAscii { get; init; }

    /// <summary><c>+0x11</c> — the second accelerator's extended-scancode half.</summary>
    [JsonPropertyName("keyShortcutAltScancode")]
    public string? KeyShortcutAltScancode { get; init; }

    /// <summary><c>+0x12</c> — flags; bit0 clickable, bit2 suppresses the custom draw.</summary>
    [JsonPropertyName("flags")]
    public string? Flags { get; init; }

    /// <summary><c>+0x13</c> — page-indexed hover dirty flag (runtime state; zero on disk).</summary>
    [JsonPropertyName("hoverPrevious")]
    public int HoverPrevious { get; init; }

    /// <summary><c>+0x14</c> — current hover flag (runtime state; zero on disk).</summary>
    [JsonPropertyName("hoverCurrent")]
    public int HoverCurrent { get; init; }

    /// <summary><c>+0x15</c> — render state: 0 idle, 1 hover, 2 pressed (runtime state; zero on disk).</summary>
    [JsonPropertyName("renderState")]
    public int RenderState { get; init; }
}

/// <summary>A far pointer as the image stores it: an offset and a segment word.</summary>
/// <remarks>
/// Both halves are carried because the resolved <c>image@</c> offset does not determine them — many
/// <c>seg:off</c> pairs address the same byte, and the segment words are MZ-relocation-patched, so
/// only the stored pair reproduces the original bytes.
/// </remarks>
public sealed class FarPointerDto
{
    /// <summary>The stored offset half.</summary>
    [JsonPropertyName("offset")]
    public string? Offset { get; init; }

    /// <summary>The stored segment half, as relocated for load segment <c>0x1000</c>.</summary>
    [JsonPropertyName("segment")]
    public string? Segment { get; init; }

    /// <summary>The byte the pair addresses in the L1 image, or absent for a NULL pointer.</summary>
    [JsonPropertyName("imageOffset")]
    public string? ImageOffset { get; init; }
}

/// <summary>The 20-record film-review widget table, <c>g_film_review_widget_table [0x49D8]</c>.</summary>
public sealed class UiWidgetTableDto
{
    /// <summary>Document kind and version.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>What the table is and who registers it.</summary>
    [JsonPropertyName("about")]
    public string? About { get; init; }

    /// <summary>Where it came from.</summary>
    [JsonPropertyName("source")]
    public DataSourceDto? Source { get; init; }

    /// <summary>The 20 widget records, in table order.</summary>
    [JsonPropertyName("widgets")]
    public List<UiWidgetDto>? Widgets { get; init; }
}

/// <summary>One NUL-terminated literal found in an executable string zone.</summary>
public sealed class ExeStringDto
{
    /// <summary>The literal's <c>image@</c> offset.</summary>
    [JsonPropertyName("image")]
    public string? Image { get; init; }

    /// <summary>Its DGROUP offset, when it lies inside DGROUP.</summary>
    [JsonPropertyName("dgroup")]
    public string? Dgroup { get; init; }

    /// <summary>The text, without the terminating NUL.</summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary>
    /// Present and true when the literal is NOT followed by a NUL — it runs into a control byte, a
    /// table, or the end of its zone (the MS C run-time copyright fills its zone exactly).  The
    /// inverse writes the terminator only when this is absent.
    /// </summary>
    [JsonPropertyName("unterminated")]
    public bool? Unterminated { get; init; }
}

public sealed class ExeStringZoneDto
{
    [JsonPropertyName("zone")]
    public string? Zone { get; init; }

    /// <summary>The region's <c>image@</c> start.</summary>
    [JsonPropertyName("image")]
    public string? Image { get; init; }

    /// <summary>The region's length in bytes.</summary>
    [JsonPropertyName("bytes")]
    public int Bytes { get; init; }

    /// <summary>The region's confidence level: verified / partial / hypothesis.</summary>
    [JsonPropertyName("zoneConfidence")]
    public string? ZoneConfidence { get; init; }

    /// <summary>How many of the region's bytes are inside a recovered literal.</summary>
    [JsonPropertyName("textBytes")]
    public int TextBytes { get; init; }

    /// <summary>The literals recovered from the region, in address order.</summary>
    [JsonPropertyName("strings")]
    public List<ExeStringDto>? Strings { get; init; }

    /// <summary>
    /// Everything in the region that is not a printable NUL-terminated literal: padding, embedded
    /// tables and binary blobs, each span at its offset inside the region.  Law L4 — carried and
    /// counted, never dropped; this is the burn-down that says how much of the "strings" fog is not
    /// actually strings.
    /// </summary>
    [JsonPropertyName("unknown_spans")]
    public List<ByteSpanDto>? UnknownSpans { get; init; }
}

/// <summary>Every literal the executable's catalogued string zones hold.</summary>
public sealed class ExeStringCatalogDto
{
    /// <summary>Document kind and version.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>What the catalogue is, where the zone list comes from, and what it does not claim.</summary>
    [JsonPropertyName("about")]
    public string? About { get; init; }

    /// <summary>The DGROUP base every <c>dgroup</c> field is relative to.</summary>
    [JsonPropertyName("dgroupImageBase")]
    public string? DgroupImageBase { get; init; }

    /// <summary>Total literals across all zones.</summary>
    [JsonPropertyName("stringCount")]
    public int StringCount { get; init; }

    /// <summary>The zones, in address order.</summary>
    [JsonPropertyName("zones")]
    public List<ExeStringZoneDto>? Zones { get; init; }
}
