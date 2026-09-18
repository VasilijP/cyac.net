namespace CYAC.Formats.Image;

/// <summary>
/// A decoded <c>.rle</c> sprite: an 8-byte header plus one row-major byte-per-pixel index plane.
/// </summary>
/// <param name="Width">Source width in pixels (header +0).</param>
/// <param name="Height">Source height in rows (header +2).</param>
/// <param name="ColorKey">The transparent colour index (low byte of header +4).</param>
/// <param name="ColorKeyHigh">The unused high byte of header +4 (0x00 on the one shipping file).</param>
/// <param name="FormatFlags">Format flags (low byte of header +6); <c>0x10</c> = nibble source.</param>
/// <param name="FormatFlagsHigh">The unused high byte of header +6 (0x01 on the one shipping file).</param>
/// <param name="Pixels">Row-major indices, <paramref name="Width"/> × <paramref name="Height"/> bytes.</param>
public sealed record RleSprite(
    int Width,
    int Height,
    byte ColorKey,
    byte ColorKeyHigh,
    byte FormatFlags,
    byte FormatFlagsHigh,
    byte[] Pixels);

/// <summary>
/// Codec for the <c>.rle</c> sprite format consumed by <c>gfx_sprite_clip_and_blit</c>
/// (<c>image@0x1D966</c>).
/// </summary>
/// <remarks>
/// <para>
/// Sources: (header read at <c>image@0x1DA30..0x1DA4F</c>) and B18, which corrected the header's
/// meaning: <c>+0</c>/<c>+2</c> are the <b>source</b> width/height in pixels (the Bresenham
/// denominators <c>[0x0D84]</c>/<c>[0x0D86]</c>), <b>not</b> a pitch/stride; the per-row byte length
/// is not in the header at all but leads every row (<c>lodsw</c> at <c>image@0x1E048</c>, row skip
/// <c>add si,[si]; add si,2</c> at <c>image@0x1DA5C</c>).
/// </para>
/// <para>
/// Layout: header <c>{u16 width, u16 height, u16 colorKey, u16 formatFlags}</c>, then
/// <c>height</c> rows of <c>{u16 rowBytes, run…}</c> where <c>rowBytes</c> counts the run data only
/// (the length word itself is not included).  A run descriptor byte <c>D</c> with bit 7 set is a
/// <b>solid</b> run of <c>D &amp; 0x7F</c> pixels of the single colour byte that follows
/// (<c>image@0x1E060</c>); with bit 7 clear it is a <b>literal</b> run of <c>D</c> pixels, each with
/// its own colour byte (<c>image@0x1E06F</c>).  Transparency is not in the stream — the consumer
/// compares each decoded pixel against the header's colour key.
/// </para>
/// <para>
/// One <c>.rle</c> ships: <c>2a.lib/EXP.RLE</c>, 115×87, key 0xE0, flags 0.  It is a <b>single
/// frame</b>, not a strip: its 87 rows are exactly the header's height and the row blocks consume
/// the payload with zero bytes left over (verified).
/// </para>
/// </remarks>
public static class RleSpriteCodec
{
    /// <summary>Bytes of fixed header before the first row block.</summary>
    public const int HeaderBytes = 8;

    /// <summary>Bit 7 of a run descriptor: set = solid run, clear = literal run.</summary>
    public const byte SolidRunFlag = 0x80;

    /// <summary>The most pixels one run descriptor can cover.</summary>
    public const int MaxRunLength = 0x7F;

    /// <summary>
    /// The shortest repeat the original encoder emits as a solid run.  Two identical pixels cost 2
    /// bytes either way, and EA always chose the run: reproducing that choice re-encodes all 87 rows
    /// of <c>EXP.RLE</c> byte-for-byte (T3).
    /// </summary>
    public const int MinSolidRun = 2;

    /// <summary>Decodes a <c>.rle</c> body.</summary>
    /// <param name="raw">The decompressed asset body.</param>
    /// <exception cref="InvalidDataException">The stream is malformed or a row is not exactly the declared width.</exception>
    public static RleSprite Decode(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < HeaderBytes)
        {
            throw new InvalidDataException($".rle is {raw.Length} B; the header alone is {HeaderBytes} B");
        }

        int width = raw[0] | (raw[1] << 8);
        int height = raw[2] | (raw[3] << 8);
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException($".rle header declares {width}x{height}");
        }

        byte[] pixels = new byte[width * height];
        int p = HeaderBytes;
        for (int y = 0; y < height; y++)
        {
            if (p + 2 > raw.Length)
            {
                throw new InvalidDataException($".rle row {y}: the stream ends before its length word");
            }

            int rowBytes = raw[p] | (raw[p + 1] << 8);
            p += 2;
            int end = p + rowBytes;
            if (end > raw.Length)
            {
                throw new InvalidDataException(
                    $".rle row {y}: declares {rowBytes} B but only {raw.Length - p} B remain");
            }

            int x = 0;
            while (p < end)
            {
                byte descriptor = raw[p++];
                int count = descriptor & MaxRunLength;
                if ((descriptor & SolidRunFlag) != 0)
                {
                    if (p >= end)
                    {
                        throw new InvalidDataException($".rle row {y}: solid run without a colour byte");
                    }

                    byte colour = raw[p++];
                    Fill(pixels, y, width, ref x, count, colour);
                }
                else
                {
                    if (p + count > end)
                    {
                        throw new InvalidDataException($".rle row {y}: literal run runs past the row");
                    }

                    for (int i = 0; i < count; i++)
                    {
                        Fill(pixels, y, width, ref x, 1, raw[p + i]);
                    }

                    p += count;
                }
            }

            if (x != width)
            {
                throw new InvalidDataException($".rle row {y}: decoded {x} pixels, the header declares {width}");
            }

            p = end;
        }

        if (p != raw.Length)
        {
            throw new InvalidDataException(
                $".rle: {raw.Length - p} B left over after {height} rows");
        }

        return new RleSprite(
            width, height, raw[4], raw[5], raw[6], raw[7], pixels);
    }

    /// <summary>Re-encodes a sprite into its stored form.</summary>
    /// <param name="sprite">The sprite to encode.</param>
    /// <exception cref="ArgumentException">The pixel count does not match the dimensions.</exception>
    public static byte[] Encode(RleSprite sprite)
    {
        ArgumentNullException.ThrowIfNull(sprite);
        if (sprite.Pixels.Length != sprite.Width * sprite.Height)
        {
            throw new ArgumentException(
                $"{sprite.Pixels.Length} pixels for a {sprite.Width}x{sprite.Height} sprite", nameof(sprite));
        }

        List<byte> outBuf = new List<byte>(HeaderBytes + sprite.Pixels.Length)
        {
            (byte)sprite.Width, (byte)(sprite.Width >> 8),
            (byte)sprite.Height, (byte)(sprite.Height >> 8),
            sprite.ColorKey, sprite.ColorKeyHigh,
            sprite.FormatFlags, sprite.FormatFlagsHigh,
        };

        List<byte> row = new List<byte>(sprite.Width * 2);
        for (int y = 0; y < sprite.Height; y++)
        {
            row.Clear();
            EncodeRow(sprite.Pixels.AsSpan(y * sprite.Width, sprite.Width), row);
            outBuf.Add((byte)row.Count);
            outBuf.Add((byte)(row.Count >> 8));
            outBuf.AddRange(row);
        }

        return [.. outBuf];
    }

    private static void EncodeRow(ReadOnlySpan<byte> pixels, List<byte> row)
    {
        List<byte> literals = new List<byte>(MaxRunLength);
        int i = 0;
        while (i < pixels.Length)
        {
            int j = i;
            while (j < pixels.Length && pixels[j] == pixels[i] && j - i < MaxRunLength)
            {
                j++;
            }

            int run = j - i;
            if (run >= MinSolidRun)
            {
                FlushLiterals(literals, row);
                row.Add((byte)(SolidRunFlag | run));
                row.Add(pixels[i]);
                i = j;
            }
            else
            {
                literals.Add(pixels[i]);
                if (literals.Count == MaxRunLength)
                {
                    FlushLiterals(literals, row);
                }

                i++;
            }
        }

        FlushLiterals(literals, row);
    }

    private static void FlushLiterals(List<byte> literals, List<byte> row)
    {
        if (literals.Count == 0)
        {
            return;
        }

        row.Add((byte)literals.Count);
        row.AddRange(literals);
        literals.Clear();
    }

    private static void Fill(byte[] pixels, int y, int width, ref int x, int count, byte colour)
    {
        if (x + count > width)
        {
            throw new InvalidDataException($".rle row {y}: a run overruns the {width}-pixel row");
        }

        pixels.AsSpan((y * width) + x, count).Fill(colour);
        x += count;
    }
}
