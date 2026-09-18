namespace CYAC.Formats.Image;

/// <summary>
/// Codec for the <c>.msk</c> overlay masks — a <b>headerless</b> 1-bit-per-pixel bitmap,
/// most-significant-bit first, row pitch <c>ceil(width / 8)</c>.
/// </summary>
/// <remarks>
/// <para><b>What the format is.</b> The file carries no dimensions at all:
/// <c>cockpit_mask_load_via_concat</c> (*.c</c>) loads it by name through <c>cockpit_sprite_blit</c>
/// (<c>image@0x0D77C</c>) and the geometry comes from elsewhere — see
/// <see cref="CockpitMaskGeometry"/>.  The proof that the layout is <c>ceil(w/8)·h</c> at 1 bpp is
/// arithmetic and exhaustive: all 19 shipping masks' decoded lengths equal <c>ceil(w/8)·h</c> for
/// the width/height their consumer uses, with no byte left over.</para>
///
/// <para><b>Bit order.</b>  <c>gfx_masked_blit</c> (<c>image@0x1D162</c>) reads one mask byte per 8
/// pixels at <c>maskSeg:[si&gt;&gt;1]</c> and feeds its <b>high</b> nibble to the VGA Map Mask for
/// the first Mode-X byte (pixels 0..3, plane <c>p</c> ← bit <c>p</c> of the nibble) and its low
/// nibble for the second (pixels 4..7).  In between sits <c>cockpit_sprite_blit</c>'s VGA-path
/// remap through <c>g_font_alt_palette [0x222]</c>, which the L1 image shows is a per-nibble
/// <b>bit reversal</b> (<c>0,8,4,C,2,A,6,E,1,9,5,D,3,B,7,F</c> at <c>image@0x3BF82</c>).  Composing
/// the two: the on-disk pixel <c>j</c> of a byte is bit <c>7-j</c> — i.e. plain MSB-first.  A set
/// bit selects the plane, so <b>1 = opaque</b> (the pixel is copied) and <b>0 = transparent</b>.
/// Rendering the shipping masks under this rule produces coherent artwork (four national insignia in
/// <c>insigm.msk</c>, a rounded radar bezel in <c>4_radar.msk</c>), which any wrong bit order
/// destroys.</para>
/// </remarks>
public static class MaskBitmap
{
    /// <summary>Bits per pixel: a mask is one bit per pixel.</summary>
    public const int BitsPerPixel = 1;

    /// <summary>Bytes per scanline for a mask of a given width.</summary>
    /// <param name="width">Width in pixels.</param>
    public static int PitchFor(int width) => (width + 7) / 8;

    /// <summary>The stored length of a mask of a given size.</summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in scanlines.</param>
    public static int SizeFor(int width, int height) => PitchFor(width) * height;

    /// <summary>Expands a mask into one byte per pixel (0 = transparent, 1 = opaque).</summary>
    /// <param name="raw">The stored mask bytes.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in scanlines.</param>
    /// <exception cref="InvalidDataException">The body is not <c>ceil(width/8) * height</c> bytes.</exception>
    public static byte[] Decode(ReadOnlySpan<byte> raw, int width, int height)
    {
        int expected = SizeFor(width, height);
        if (raw.Length != expected)
        {
            throw new InvalidDataException(
                $".msk is {raw.Length} B; a {width}x{height} 1bpp mask is {expected} B " +
                $"(pitch {PitchFor(width)})");
        }

        int pitch = PitchFor(width);
        byte[] pixels = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            int rowBase = y * pitch;
            int outBase = y * width;
            for (int x = 0; x < width; x++)
            {
                pixels[outBase + x] = (byte)((raw[rowBase + (x >> 3)] >> (7 - (x & 7))) & 1);
            }
        }

        return pixels;
    }

    /// <summary>Packs one byte per pixel back into the stored mask.</summary>
    /// <param name="pixels">Row-major pixels; any non-zero value is opaque.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in scanlines.</param>
    /// <param name="padBits">
    /// The bits of the last byte of each row that lie past <paramref name="width"/>.  They are not
    /// pixels, so they are supplied rather than derived; every shipping mask has a width that is a
    /// multiple of 8, so none of them has any.
    /// </param>
    /// <exception cref="ArgumentException">The pixel count does not match the dimensions.</exception>
    public static byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height, ReadOnlySpan<byte> padBits = default)
    {
        if (pixels.Length != width * height)
        {
            throw new ArgumentException(
                $"{pixels.Length} pixels for a {width}x{height} mask", nameof(pixels));
        }

        int pitch = PitchFor(width);
        int padPerRow = (pitch * 8) - width;
        if (padBits.Length != 0 && padBits.Length != padPerRow * height)
        {
            throw new ArgumentException(
                $"{padBits.Length} pad bits for {padPerRow} per row x {height} rows", nameof(padBits));
        }

        byte[] raw = new byte[pitch * height];
        for (int y = 0; y < height; y++)
        {
            int rowBase = y * pitch;
            int inBase = y * width;
            for (int x = 0; x < width; x++)
            {
                if (pixels[inBase + x] != 0)
                {
                    raw[rowBase + (x >> 3)] |= (byte)(1 << (7 - (x & 7)));
                }
            }

            for (int i = 0; i < padPerRow; i++)
            {
                int bit = width + i;
                if (padBits.Length != 0 && padBits[(y * padPerRow) + i] != 0)
                {
                    raw[rowBase + (bit >> 3)] |= (byte)(1 << (7 - (bit & 7)));
                }
            }
        }

        return raw;
    }

    /// <summary>The pad bits of a stored mask — the bits of each row past <paramref name="width"/>.</summary>
    /// <param name="raw">The stored mask bytes.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in scanlines.</param>
    public static byte[] PadBits(ReadOnlySpan<byte> raw, int width, int height)
    {
        int pitch = PitchFor(width);
        int padPerRow = (pitch * 8) - width;
        if (padPerRow == 0)
        {
            return [];
        }

        byte[] bits = new byte[padPerRow * height];
        for (int y = 0; y < height; y++)
        {
            for (int i = 0; i < padPerRow; i++)
            {
                int bit = width + i;
                bits[(y * padPerRow) + i] =
                    (byte)((raw[(y * pitch) + (bit >> 3)] >> (7 - (bit & 7))) & 1);
            }
        }

        return bits;
    }
}
