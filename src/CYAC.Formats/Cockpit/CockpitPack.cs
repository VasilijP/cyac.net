namespace CYAC.Formats.Cockpit;

/// <summary>
/// The video mode a cockpit compositor pack paints in — the <c>_&lt;mode&gt;</c> half of its
/// <c>3a.lib/&lt;aircraft&gt;_&lt;mode&gt;.BIN</c> name.
/// </summary>
/// <remarks>
/// Source of truth: — the loader <c>cockpit_weapon_icon_table_load @image@0x0E7FB</c> builds
/// the file name from <c>g_aircraft_suffix_table [0x4226]</c> and
/// <c>g_weapon_icon_mode_suffix_table [0x4372]</c> (<c>{0:"_cga", 1:"_ega", 2:NULL, 3:NULL,
/// 4:"_mcga", 5:"_tandy", 6:"_vga"}</c>, indexed by <c>g_cfg_sub_mode [0x15E]</c>).  Sub-modes
/// 2 and 3 load no pack at all.
/// </remarks>
public enum CockpitPackMode
{
    /// <summary>Mode-X 320×200×256, four planes, 80 bytes of plane per row (sub-mode 6).</summary>
    Vga,

    /// <summary>Linear 320×200×256, 320 bytes per row (sub-mode 4).</summary>
    Mcga,

    /// <summary>CGA 4-colour (sub-mode 0) — a dormant mode; the pack ships but the mode does not work.</summary>
    Cga,

    /// <summary>Tandy 16-colour (sub-mode 5) — a dormant mode.</summary>
    Tandy,

    /// <summary>EGA (sub-mode 1) — a dormant mode carrying the <c>mov ds,[bp+6]</c> anomaly.</summary>
    Ega,
}

/// <summary>
/// One horizontal run of opaque cockpit pixels on one screen row — the pack's semantic content.
/// </summary>
/// <remarks>
/// The pack is a straight-line paint program with no control flow, so what it *is* is a set of
/// opaque spans and their colour indices: the cockpit bitmap plus its alpha channel, which is
/// exactly what the port's cockpit renderer consumes.  A pixel outside every run is transparent —
/// the 3-D viewport shows through it.
/// </remarks>
/// <param name="Row">The screen row, 0-based from the top.</param>
/// <param name="X">The run's first column, 0-based from the left.</param>
/// <param name="Pixels">The run's palette indices, left to right.</param>
public sealed record CockpitPaintRun(int Row, int X, byte[] Pixels)
{
    /// <summary>The column just past the run's last pixel.</summary>
    public int EndX => X + Pixels.Length;
}

/// <summary>
/// A cockpit pack's region map: where its header, code, pixel data and element list live.
/// </summary>
/// <remarks>
/// <para>
/// Two shapes exist.  A VGA pack is <c>hdr(12) | init stub | far-nop stub | compositor
/// code | pixel data | element list</c>; every other mode is <c>hdr(4) | pixel data | element list |
/// code</c>.  The header words are ENTRY POINTS, not counts — the P41 "3-word header" reading was a
/// misnomer family.
/// </para>
/// <para>
/// The element list is a <c>0xFF</c>-terminated byte array that is provably inert: its offset occurs
/// exactly once in the whole file (in the header) and nothing in the pack or the image reads it.
/// Only its LENGTH carries information, and in every shipping pack it is the identity permutation
/// <c>0,1,2,…</c> — an authoring-tool fingerprint.
/// </para>
/// </remarks>
public sealed class CockpitPackLayout
{
    /// <summary>The mode this pack paints in.</summary>
    public required CockpitPackMode Mode { get; init; }

    /// <summary>The pack's total decompressed length in bytes.</summary>
    public required int Length { get; init; }

    /// <summary>Bytes of header: 12 for VGA, 4 otherwise.</summary>
    public required int HeaderLength { get; init; }

    /// <summary>Header word 0 — the per-frame PAINT entry point (<c>g_cockpit_pack_paint_entry_off [0xBCAE]</c>).</summary>
    public required int PaintEntry { get; init; }

    /// <summary>Header word 1 — the offset of the element list (<c>g_cockpit_pack_elemlist_off [0xBC40]</c>).</summary>
    public required int ElementListOffset { get; init; }

    /// <summary>Header word 2 — the load-time INIT entry (VGA only; always <c>0x000C</c>).</summary>
    public int? InitEntry { get; init; }

    /// <summary>Header word 3 — a fourth entry slot holding a far-nop stub (VGA only).</summary>
    public int? UnusedEntry { get; init; }

    /// <summary>
    /// Header words 4/5 — the far pointer to the pixel data, <c>0:0</c> on disk and self-patched by
    /// the init stub (VGA only).  The runtime blob differs from the shipped asset in exactly these
    /// four bytes.
    /// </summary>
    public int? DataPointerOffset { get; init; }

    /// <summary>The segment half of <see cref="DataPointerOffset"/>.</summary>
    public int? DataPointerSegment { get; init; }

    /// <summary>Where the pixel-data region starts.</summary>
    public required int DataStart { get; init; }

    /// <summary>Where the pixel-data region ends (exclusive).</summary>
    public required int DataEnd { get; init; }

    /// <summary>Where the code region starts.</summary>
    public required int CodeStart { get; init; }

    /// <summary>Where the code region ends (exclusive).</summary>
    public required int CodeEnd { get; init; }

    /// <summary>The element list's bytes, without its <c>0xFF</c> terminator.</summary>
    public required byte[] Elements { get; init; }

    /// <summary>Whether the element list is the identity permutation <c>0,1,2,…</c>.</summary>
    public bool ElementsAreIdentity
    {
        get
        {
            for (int i = 0; i < Elements.Length; i++)
            {
                if (Elements[i] != (byte)i)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Bytes of pixel data.</summary>
    public int DataBytes => DataEnd - DataStart;

    /// <summary>Bytes of machine code.</summary>
    public int CodeBytes => CodeEnd - CodeStart;

    /// <summary>Bytes of element list, including the terminator.</summary>
    public int ElementListBytes => Elements.Length + 1;
}

/// <summary>
/// What simulating a pack's paint program produced: the cockpit bitmap and how it was painted.
/// </summary>
/// <param name="Runs">The opaque spans, in the order the generator re-emits them (row, then column).</param>
/// <param name="CopyBytes">Bytes moved by string ops — the pixels that stream from the data region.</param>
/// <param name="LiteralPixels">Pixels emitted as immediate stores — the unaligned run edges.</param>
/// <param name="DataCursorEnd">Where the string-op source cursor finished; it must equal <c>DataEnd</c>.</param>
public sealed record CockpitPaintProgram(
    IReadOnlyList<CockpitPaintRun> Runs, int CopyBytes, int LiteralPixels, int DataCursorEnd)
{
    /// <summary>Total opaque pixels.</summary>
    public int PixelCount
    {
        get
        {
            int n = 0;
            foreach (CockpitPaintRun run in Runs)
            {
                n += run.Pixels.Length;
            }

            return n;
        }
    }

    /// <summary>The first row any pixel is painted on, or -1 when nothing is painted.</summary>
    public int FirstRow => Runs.Count == 0 ? -1 : Runs.Min(r => r.Row);

    /// <summary>The last row any pixel is painted on, or -1 when nothing is painted.</summary>
    public int LastRow => Runs.Count == 0 ? -1 : Runs.Max(r => r.Row);
}
