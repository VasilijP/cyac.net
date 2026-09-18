using CYAC.Formats.EaLib;

namespace CYAC.Formats.Image;

// .pal (companion-palette) decoder — C# port of.
//
// Format:
//   +0..3 : u32-LE  declared decompressed size (always 0x300 = 768 bytes)
//   +4..  : LZSS-compressed stream (identical to every other flag=0x01
//           EALIB asset — same codec at image@0x2FA64; see EaLib/Lzss.cs)
//
// After decompression the 768-byte body is 256 RGB triplets at 6-bit
// VGA-DAC resolution (each component 0..63). Multiply by 255/63 to
// project into 8-bit RGB.
//
// Observed shipped pairs (`title?v.pic` + `title?v.pal` in `1b.lib`)
// populate only entries 0..15 (the 16 indices the EGA/MCGA .pic refers
// to), with entries 16..255 left zero. The decoder still loads the
// full 256 entries — VGA-256 mode (`.pic` mode 0x100) could legitimately
// use the entire palette even if no shipped pair currently does.
public static class PalDecoder
{
    public const int ExpectedDeclaredSize = 0x300;   // 768 bytes
    public const int PaletteEntries = 256;
    public const int MaxDacComponent = 0x3F;         // 6-bit VGA-DAC limit

    /// <summary>
    /// Decode raw on-disk `.pal` bytes into a 256-entry palette at the
    /// native 6-bit VGA-DAC range (0..63 per channel).
    /// </summary>
    public static (byte R, byte G, byte B)[] Decode6Bit(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 4)
            throw new InvalidDataException(
                $".pal too short ({raw.Length} bytes; need >= 4 for size header)");
        int declared = raw[0] | (raw[1] << 8) | (raw[2] << 16) | (raw[3] << 24);
        if (declared != ExpectedDeclaredSize)
            throw new InvalidDataException(
                $".pal declared size = {declared} (expected {ExpectedDeclaredSize} " +
                "= 256 RGB triplets)");
        byte[] body = Lzss.Decompress(raw[4..], declared);
        if (body.Length != declared)
            throw new InvalidDataException(
                $".pal LZSS produced {body.Length} of {declared} bytes");
        (byte R, byte G, byte B)[] pal = new (byte R, byte G, byte B)[PaletteEntries];
        for (int i = 0; i < PaletteEntries; i++)
        {
            byte r = body[i * 3 + 0];
            byte g = body[i * 3 + 1];
            byte b = body[i * 3 + 2];
            if (r > MaxDacComponent || g > MaxDacComponent || b > MaxDacComponent)
                throw new InvalidDataException(
                    $".pal entry {i} component out of 6-bit range " +
                    $"(0x{r:X2}, 0x{g:X2}, 0x{b:X2})");
            pal[i] = (r, g, b);
        }
        return pal;
    }

    /// <summary>
    /// Decode raw on-disk `.pal` bytes into a 256-entry palette projected
    /// into 8-bit RGB (component scaled by 255/63).
    /// </summary>
    public static (byte R, byte G, byte B)[] Decode8Bit(ReadOnlySpan<byte> raw)
    {
        (byte R, byte G, byte B)[] src = Decode6Bit(raw);
        (byte R, byte G, byte B)[] dst = new (byte R, byte G, byte B)[PaletteEntries];
        for (int i = 0; i < PaletteEntries; i++)
        {
            (byte r, byte g, byte b) = src[i];
            dst[i] = (
                (byte)(r * 255 / 63),
                (byte)(g * 255 / 63),
                (byte)(b * 255 / 63));
        }
        return dst;
    }

    public static (byte R, byte G, byte B)[] Decode8BitFromFile(string path)
        => Decode8Bit(File.ReadAllBytes(path));

    /// <summary>
    /// Detect whether a buffer is plausibly a `.pal` file (LZSS-declared
    /// size header = 0x300). Cheap (4-byte read) — does NOT verify the
    /// LZSS body.
    /// </summary>
    public static bool LooksLikePal(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 8) return false;
        int declared = raw[0] | (raw[1] << 8) | (raw[2] << 16) | (raw[3] << 24);
        return declared == ExpectedDeclaredSize;
    }
}
