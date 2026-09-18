using CYAC.Formats.EaLib;

namespace CYAC.Formats.Image;

/// <summary>
/// The inverse of <see cref="PicDecoder"/>: packs pixel indices back into a stored <c>.pic</c>
/// (PXPK) asset — the encoder that was never written.
/// </summary>
/// <remarks>
/// <para>
/// Layout (see <see cref="PicDecoder"/>'s own header comment, closed against
/// <c>pic_codec_decompress @ image@0x2FBD6</c>): a 16-byte PXPK header (<c>"PXPK"</c>, <c>mode</c>,
/// <c>width</c>, <c>word_pitch</c>, <c>height</c>, 4 reserved bytes) immediately followed by the
/// <b>standard EALIB flag=0x01 asset framing</b> — u32-LE decompressed size + LZSS stream.  That is
/// why the encoder can simply delegate to <see cref="LzssCompressor.CompressAsset"/>: the inner body
/// is the same codec, with the same framing, as every other compressed member (<c>LzssCompressor</c>
/// reproduces EA's own stream byte-for-byte on all 138 compressed assets).
/// </para>
/// <para>
/// The <c>.pic</c> members carry encoding flag <c>0x03</c>, i.e. the archive stores them verbatim,
/// so this really does have to reproduce every byte — including the compressed stream.  T3 verified
/// 36/36 shipping <c>.pic</c> assets re-encode byte-identically.
/// </para>
/// </remarks>
public static class PicEncoder
{
    /// <summary>The 4-byte magic every <c>.pic</c> starts with.</summary>
    public static ReadOnlySpan<byte> Magic => "PXPK"u8;

    /// <summary>Bytes of PXPK header before the u32-LE size that starts the LZSS asset framing.</summary>
    public const int MagicHeaderBytes = 16;

    /// <summary>
    /// Encodes a decoded body back into a stored <c>.pic</c> asset.
    /// </summary>
    /// <param name="mode">The image mode (which also fixes the bit depth).</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="wordPitch">The header's word pitch (<c>width * bpp / 16</c> on every shipping file).</param>
    /// <param name="height">Height in scanlines.</param>
    /// <param name="body">The decompressed body: <c>width * height * bpp / 8</c> packed bytes.</param>
    /// <param name="reserved">The 4 reserved header bytes at +12 (zero on every shipping file).</param>
    /// <exception cref="ArgumentException">A dimension or the body length is inconsistent.</exception>
    public static byte[] Encode(
        PicMode mode,
        int width,
        int wordPitch,
        int height,
        ReadOnlySpan<byte> body,
        ReadOnlySpan<byte> reserved = default)
    {
        int bpp = mode switch
        {
            PicMode.Cga2Bpp => 2,
            PicMode.Ega4Bpp => 4,
            PicMode.Vga8Bpp => 8,
            _ => throw new ArgumentException($"unknown PIC mode 0x{(int)mode:X4}", nameof(mode)),
        };

        int expected = width * height * bpp / 8;
        if (body.Length != expected)
        {
            throw new ArgumentException(
                $"body is {body.Length} B; a {width}x{height} {bpp}bpp image is {expected} B", nameof(body));
        }

        if (reserved.Length is not (0 or 4))
        {
            throw new ArgumentException("the reserved header field is 4 bytes", nameof(reserved));
        }

        byte[] framed = LzssCompressor.CompressAsset(body);
        byte[] outBuf = new byte[MagicHeaderBytes + framed.Length];
        Magic.CopyTo(outBuf);
        WriteU16(outBuf, 4, (ushort)mode);
        WriteU16(outBuf, 6, checked((ushort)width));
        WriteU16(outBuf, 8, checked((ushort)wordPitch));
        WriteU16(outBuf, 10, checked((ushort)height));
        if (reserved.Length == 4)
        {
            reserved.CopyTo(outBuf.AsSpan(12));
        }

        framed.CopyTo(outBuf.AsSpan(MagicHeaderBytes));
        return outBuf;
    }

    /// <summary>
    /// Packs one row-major byte-per-pixel index array into the mode's packed scanline format — the
    /// inverse of <see cref="PicFile.PixelIndices"/>.
    /// </summary>
    /// <param name="mode">The target mode.</param>
    /// <param name="width">Width in pixels; must be a whole number of packed bytes.</param>
    /// <param name="height">Height in scanlines.</param>
    /// <param name="indices">Row-major indices, <paramref name="width"/> × <paramref name="height"/> bytes.</param>
    /// <exception cref="ArgumentException">The index count or an index value does not fit the mode.</exception>
    public static byte[] Pack(PicMode mode, int width, int height, ReadOnlySpan<byte> indices)
    {
        if (indices.Length != width * height)
        {
            throw new ArgumentException(
                $"{indices.Length} indices for a {width}x{height} image", nameof(indices));
        }

        switch (mode)
        {
            case PicMode.Vga8Bpp:
                return indices.ToArray();

            case PicMode.Ega4Bpp:
            {
                if (width % 2 != 0)
                {
                    throw new ArgumentException($"a 4bpp row needs an even width, got {width}", nameof(width));
                }

                byte[] packed = new byte[width * height / 2];
                for (int i = 0, o = 0; i < indices.Length; i += 2, o++)
                {
                    packed[o] = (byte)((Nibble(indices[i]) << 4) | Nibble(indices[i + 1]));
                }

                return packed;
            }

            case PicMode.Cga2Bpp:
            {
                if (width % 4 != 0)
                {
                    throw new ArgumentException(
                        $"a 2bpp row needs a width that is a multiple of 4, got {width}", nameof(width));
                }

                byte[] packed = new byte[width * height / 4];
                for (int i = 0, o = 0; i < indices.Length; i += 4, o++)
                {
                    packed[o] = (byte)((Pair(indices[i]) << 6) | (Pair(indices[i + 1]) << 4) |
                                       (Pair(indices[i + 2]) << 2) | Pair(indices[i + 3]));
                }

                return packed;
            }

            default:
                throw new ArgumentException($"unknown PIC mode 0x{(int)mode:X4}", nameof(mode));
        }
    }

    private static int Nibble(byte index) => index <= 0xF
        ? index
        : throw new ArgumentException($"index {index} does not fit 4 bpp", nameof(index));

    private static int Pair(byte index) => index <= 0x3
        ? index
        : throw new ArgumentException($"index {index} does not fit 2 bpp", nameof(index));

    private static void WriteU16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }
}
