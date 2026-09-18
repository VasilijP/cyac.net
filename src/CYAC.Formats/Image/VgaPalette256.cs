namespace CYAC.Formats.Image;

// VGA 256-color palette helpers for .PIC files in mode 0x100.
//
// This library no longer finds a palette for the caller.  The game's own 256-colour palette is
// data: the `palette` member of 1a.lib, or the transformed tree's palettes/palette.json, which the
// caller reads and hands over, e.g. through FromRawBytes.  The development tools keep their own
// walk-up.
//
// Components are 6-bit (range 0..63, max 0x3F) — the canonical VGA DAC
// resolution. Multiply by 4 (or 255/63) to project into 8-bit RGB.
//
// Per-screen overrides (title0v.pal etc.) use a different on-disk format
// that the decoder does not handle.
//
// The canonical EGA and CGA palettes below are platform facts, not game data.
public static class VgaPalette256
{
    public const int EntryCount = 256;

    /// <summary>
    /// Load a 768-byte VGA palette file (6-bit components) into an array of
    /// 8-bit RGB tuples.
    /// </summary>
    public static (byte R, byte G, byte B)[] FromRawBytes(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < EntryCount * 3)
            throw new ArgumentException(
                $"VGA palette needs {EntryCount * 3} bytes, got {raw.Length}");
        (byte R, byte G, byte B)[] pal = new (byte R, byte G, byte B)[EntryCount];
        for (int i = 0; i < EntryCount; i++)
        {
            byte r6 = raw[i * 3 + 0];
            byte g6 = raw[i * 3 + 1];
            byte b6 = raw[i * 3 + 2];
            // Scale 6-bit (0..63) → 8-bit (0..255). Use *255/63 for accuracy.
            pal[i] = (
                (byte)(r6 * 255 / 63),
                (byte)(g6 * 255 / 63),
                (byte)(b6 * 255 / 63));
        }
        return pal;
    }

    /// <summary>Canonical CGA hi-intensity cyan/magenta palette (mode 5).</summary>
    public static readonly (byte R, byte G, byte B)[] CgaCyanMagenta =
    {
        (0x00, 0x00, 0x00), // 00 background (actually programmable)
        (0x55, 0xFF, 0xFF), // 01 cyan
        (0xFF, 0x55, 0xFF), // 02 magenta
        (0xFF, 0xFF, 0xFF), // 03 white
    };

    /// <summary>Canonical EGA 16-color palette.</summary>
    public static readonly (byte R, byte G, byte B)[] EgaDefault =
    {
        (0x00, 0x00, 0x00), (0x00, 0x00, 0xAA), (0x00, 0xAA, 0x00), (0x00, 0xAA, 0xAA),
        (0xAA, 0x00, 0x00), (0xAA, 0x00, 0xAA), (0xAA, 0x55, 0x00), (0xAA, 0xAA, 0xAA),
        (0x55, 0x55, 0x55), (0x55, 0x55, 0xFF), (0x55, 0xFF, 0x55), (0x55, 0xFF, 0xFF),
        (0xFF, 0x55, 0x55), (0xFF, 0x55, 0xFF), (0xFF, 0xFF, 0x55), (0xFF, 0xFF, 0xFF),
    };
}
