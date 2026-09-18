namespace CYAC.Formats.EaLib;

// ---------------------------------------------------------------------------
// remap.bin — the COLOUR-TRANSLATION table (1a.lib idx 69, LZSS flag 0x01:
// 157 B stored -> 256 B decompressed).
//
// The figure quoted throughout the project is the COMPRESSED size.  The asset
// the engine sees is 256 bytes: one byte per VGA palette index.
//
// WHAT IT IS.  Entry i is the colour to use for authored index i when the
// current video mode is NOT 256-colour: gfx_or_palette_color_select
// @image@0x24112 returns `xlat_table[al]` while g_render_state_mask [0xE64E]
// < 0x100 and passes `al` through unchanged at 0x100 (VGA).  The table is
// loaded by palette_and_remap_load @image@0x2409E (`lea bx,[0x2e88]` =
// "remap.bin" @image@0x3EBE8) into g_color_xlat_table_farptr [0x478A]/[0x478C].
//
// Shipping content: entries 0..15 are the identity and every entry is <= 15,
// i.e. it maps the 256-colour authoring palette down onto the 16-colour
// EGA/CGA index space.  That is a property of the shipped data, not of the
// format, so this codec neither assumes nor enforces it.
// ---------------------------------------------------------------------------

/// <summary>
/// The 256-entry colour-translation LUT of <c>remap.bin</c> (see the file header).
/// </summary>
public static class RemapBinDecoder
{
    /// <summary>Entries in the table: one per VGA palette index.</summary>
    public const int EntryCount = 256;

    /// <summary>The asset's name in <c>1a.lib</c>.</summary>
    public const string AssetName = "remap.bin";

    /// <summary>Whether an asset name is the colour-translation table.</summary>
    /// <param name="name">An EALIB member name.</param>
    public static bool IsRemapTable(string name) =>
        string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the LUT from a decompressed body.</summary>
    /// <param name="body">The decompressed asset body; must be exactly 256 bytes.</param>
    /// <exception cref="InvalidDataException">The body is not 256 bytes.</exception>
    public static byte[] Parse(ReadOnlySpan<byte> body)
    {
        if (body.Length != EntryCount)
        {
            throw new InvalidDataException(
                $"remap.bin is {body.Length} B; the colour-translation table indexed by " +
                $"gfx_or_palette_color_select @image@0x24112 is {EntryCount} B (one byte per palette index)");
        }

        return body.ToArray();
    }

    /// <summary>Writes the LUT back out.</summary>
    /// <param name="entries">The 256 translated colour indices.</param>
    /// <exception cref="InvalidDataException">The table does not hold 256 entries.</exception>
    public static byte[] ToBytes(IReadOnlyList<byte> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count != EntryCount)
        {
            throw new InvalidDataException(
                $"a colour-translation table has {EntryCount} entries, not {entries.Count}");
        }

        return [.. entries];
    }
}
