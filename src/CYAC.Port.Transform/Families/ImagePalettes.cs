using CYAC.Formats.Image;
using CYAC.Port.Transform.Transform;

namespace CYAC.Port.Transform.Families;

/// <summary>
/// Which palette a data-tree PNG is drawn with, and where that palette came from.
/// </summary>
/// <param name="Rgb">The palette as 8-bit RGB triples, ready for a PNG <c>PLTE</c> chunk.</param>
/// <param name="Source">A citation: an archive member, or the platform default that was used.</param>
/// <param name="Binding">
/// <c>companion</c> (the screen's own <c>.pal</c>), <c>default-vga</c> (the global
/// <c>1a.lib/palette</c>), <c>default-ega</c> / <c>default-cga</c> (a platform default, because the
/// game programs those modes' palettes from code rather than from the asset), or <c>fixed</c> (an
/// image whose indices are not game palette indices at all — a 1-bit mask or font).
/// </param>
public sealed record ImagePalette(byte[] Rgb, string Source, string Binding);

/// <summary>
/// Resolves the palette a <c>.pic</c> or <c>.rle</c> PNG is rendered with.
/// </summary>
/// <remarks>
/// <para>
/// A palette binding is <b>program state</b>, not a property of the image file: the game loads a
/// screen's <c>.pal</c> next to its <c>.pic</c> when it has one (<c>image@0x24F6A</c> /
/// <c>image@0x24F82</c> for the two title screens) and otherwise runs on whatever the mode was last
/// programmed with.  So the binding is recorded next to every image instead of being pretended away.
/// </para>
/// <para>
/// <b>The one thing this must never do</b> is invent colours the way
/// <c>ResourceBrowser/Mesh/VgaPalette.cs:113</c> does when <c>1a/palette</c> is missing (it
/// fabricates an HSL ramp).  Here a missing palette is either a named platform default — a
/// documented fact, cited — or an error.
/// </para>
/// </remarks>
public static class ImagePalettes
{
    /// <summary>The archive holding the global VGA palette.</summary>
    public const string GlobalPaletteArchive = "1a.lib";

    /// <summary>The member name of the global VGA palette (<c>resources/ASSETS.md</c> #68).</summary>
    public const string GlobalPaletteMember = "palette";

    /// <summary>Bytes per palette entry: R, G, B.</summary>
    public const int ComponentsPerColor = 3;

    /// <summary>Expands a 6-bit VGA DAC component to 8 bits: <c>v&lt;&lt;2 | v&gt;&gt;4</c>.</summary>
    /// <param name="component">The stored component; only its low 6 bits reach the DAC.</param>
    public static byte ToEightBit(int component)
    {
        int v = component & 0x3F;
        return (byte)((v << 2) | (v >> 4));
    }

    /// <summary>Expands a 768-byte 6-bit DAC palette body into 8-bit RGB triples.</summary>
    /// <param name="dac">The stored palette body.</param>
    public static byte[] FromDac(ReadOnlySpan<byte> dac)
    {
        byte[] rgb = new byte[dac.Length / 3 * 3];
        for (int i = 0; i < rgb.Length; i++)
        {
            rgb[i] = ToEightBit(dac[i]);
        }

        return rgb;
    }

    /// <summary>A two-entry palette for 1-bit images: index 0 black, index 1 white.</summary>
    public static ImagePalette Monochrome { get; } =
        new([0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF], "1-bit image (not a game palette)", "fixed");

    /// <summary>
    /// Picks the palette for an image, given the mode's bit depth and where it came from.
    /// </summary>
    /// <param name="context">The run's context; its originals provide the companion and global palettes.</param>
    /// <param name="archiveFileName">The archive the image is in, for the companion lookup.</param>
    /// <param name="stem">The image's file-name stem, whose <c>.pal</c> sibling is the companion.</param>
    /// <param name="bitsPerPixel">2, 4 or 8.</param>
    /// <exception cref="InvalidDataException">Nothing could supply a palette for an 8-bpp image.</exception>
    public static ImagePalette Resolve(
        TransformContext context, string archiveFileName, string stem, int bitsPerPixel)
    {
        ArgumentNullException.ThrowIfNull(context);
        IOriginalData? originals = context.Originals;

        // 1. The screen's own .pal, when it ships one (title0v.pic + title0v.pal, 1b.lib).
        // NOTE: what the container layer hands over is the DECODED body — 768 raw 6-bit DAC bytes,
        // the flag=0x01 framing already undone — not the stored asset PalDecoder.Decode6Bit parses.
        byte[]? companion = originals?.TryGetArchiveMember(archiveFileName, stem + ".pal");
        if (companion is not null && companion.Length >= 256 * ComponentsPerColor)
        {
            return new ImagePalette(
                FromDac(companion.AsSpan(0, 256 * ComponentsPerColor)),
                $"{archiveFileName}/{stem}.pal",
                "companion");
        }

        // 2. The global VGA palette for 8-bpp screens.
        if (bitsPerPixel == 8)
        {
            byte[]? global = originals?.TryGetArchiveMember(GlobalPaletteArchive, GlobalPaletteMember);
            if (global is null || global.Length < 256 * ComponentsPerColor)
            {
                throw new InvalidDataException(
                    $"an 8-bpp image needs a 256-colour palette and {GlobalPaletteArchive}/" +
                    $"{GlobalPaletteMember} is not available — refusing to invent one");
            }

            return new ImagePalette(
                FromDac(global.AsSpan(0, 256 * ComponentsPerColor)),
                $"{GlobalPaletteArchive}/{GlobalPaletteMember}",
                "default-vga");
        }

        // 3. The mode's documented default for the low-colour variants, which the game programs
        //    from code rather than from an asset.
        return bitsPerPixel switch
        {
            4 => new ImagePalette(
                Flatten(VgaPalette256.EgaDefault),
                "platform: IBM EGA default 16-colour palette",
                "default-ega"),
            2 => new ImagePalette(
                Flatten(VgaPalette256.CgaCyanMagenta),
                "platform: IBM CGA mode 5 high-intensity cyan/magenta palette",
                "default-cga"),
            _ => throw new InvalidDataException($"no palette rule for {bitsPerPixel} bpp"),
        };
    }

    private static byte[] Flatten((byte R, byte G, byte B)[] entries)
    {
        byte[] rgb = new byte[entries.Length * 3];
        for (int i = 0; i < entries.Length; i++)
        {
            rgb[(i * 3) + 0] = entries[i].R;
            rgb[(i * 3) + 1] = entries[i].G;
            rgb[(i * 3) + 2] = entries[i].B;
        }

        return rgb;
    }
}
