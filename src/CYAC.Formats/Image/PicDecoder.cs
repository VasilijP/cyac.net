using CYAC.Formats.EaLib;

namespace CYAC.Formats.Image;

// .PIC (PXPK) file decoder — C# port of.
//
// On-disk layout:
//   +0..3   "PXPK" magic
//   +4..5   mode u16    (0x0004=CGA, 0x0010=EGA/MCGA, 0x0100=VGA-256)
//   +6..7   width u16   in pixels
//   +8..9   word_pitch u16  (= bytes_per_row / 2 = width * bpp / 16)
//   +10..11 height u16  in scanlines
//   +12..15 reserved (always 0)
//   +16..19 u32-LE declared decompressed body size (= width*height*bpp/8)
//   +20..   LZSS bitstream (bit-exact to ealib_decompress at image@0x2FA64)
//
// Mode -> bpp:
//   0x004  CGA       2 bpp packed 4 px/byte, MSB-first
//   0x010  EGA/MCGA  4 bpp packed 2 px/byte, MSB-first (NOT planar)
//   0x100  VGA-256   8 bpp chunky (mode 13h linear)
//
// Reference: pic_codec_decompress @ image@0x2FBD6 (renamed from
// adlib_init_and_driver).
public enum PicMode : ushort
{
    Cga2Bpp = 0x0004,
    Ega4Bpp = 0x0010,
    Vga8Bpp = 0x0100,
}

public sealed class PicFile
{
    public required PicMode Mode { get; init; }
    public required int Width { get; init; }
    public required int WordPitch { get; init; }
    public required int Height { get; init; }
    public required int DeclaredSize { get; init; }
    public required byte[] CompressedBody { get; init; }
    public byte[]? RawSource { get; init; }

    public int Bpp => Mode switch
    {
        PicMode.Cga2Bpp => 2,
        PicMode.Ega4Bpp => 4,
        PicMode.Vga8Bpp => 8,
        _ => 0,
    };

    public string ModeName => Mode switch
    {
        PicMode.Cga2Bpp => "CGA 2bpp",
        PicMode.Ega4Bpp => "EGA/MCGA 4bpp",
        PicMode.Vga8Bpp => "VGA-256 8bpp",
        _ => $"unknown(0x{(int)Mode:X4})",
    };

    private byte[]? _decoded;

    public byte[] DecodedBytes()
    {
        if (_decoded is not null) return _decoded;
        byte[] pixels = Lzss.Decompress(CompressedBody, DeclaredSize);
        if (pixels.Length != DeclaredSize)
            throw new InvalidDataException(
                $"PIC LZSS produced {pixels.Length} of {DeclaredSize} bytes");
        _decoded = pixels;
        return _decoded;
    }

    /// <summary>
    /// Return the decoded image as a flat row-major byte array of palette
    /// indices (8-bit, regardless of mode).
    /// </summary>
    public byte[] PixelIndices()
    {
        byte[] data = DecodedBytes();
        int w = Width, h = Height;
        byte[] pixels = new byte[w * h];
        switch (Mode)
        {
            case PicMode.Vga8Bpp:
                // Chunky 8 bpp, row-major.
                Buffer.BlockCopy(data, 0, pixels, 0, w * h);
                break;
            case PicMode.Ega4Bpp:
            {
                int bpr = w / 2; // bytes per row
                for (int y = 0; y < h; y++)
                {
                    int src = y * bpr;
                    int dst = y * w;
                    for (int i = 0; i < bpr; i++)
                    {
                        byte b = data[src + i];
                        pixels[dst++] = (byte)((b >> 4) & 0xF);
                        pixels[dst++] = (byte)(b & 0xF);
                    }
                }
                break;
            }
            case PicMode.Cga2Bpp:
            {
                int bpr = w / 4;
                for (int y = 0; y < h; y++)
                {
                    int src = y * bpr;
                    int dst = y * w;
                    for (int i = 0; i < bpr; i++)
                    {
                        byte b = data[src + i];
                        pixels[dst++] = (byte)((b >> 6) & 3);
                        pixels[dst++] = (byte)((b >> 4) & 3);
                        pixels[dst++] = (byte)((b >> 2) & 3);
                        pixels[dst++] = (byte)(b & 3);
                    }
                }
                break;
            }
            default:
                throw new InvalidDataException($"unknown mode 0x{(int)Mode:X4}");
        }
        return pixels;
    }

    /// <summary>Bytes per scanline of the decoded body: <c>width * bpp / 8</c>.</summary>
    /// <remarks>
    /// Equal to <c>WordPitch * 2</c> on every shipping .pic (36/36, verified T3) — the header carries
    /// the same number twice, once in pixels and once in words.  <see cref="PicDecoder.Parse"/>
    /// rejects a file where <c>DeclaredSize != width*height*bpp/8</c>, so this is always consistent
    /// with the body length.
    /// </remarks>
    public int BytesPerRow => Width * Bpp / 8;

    /// <summary>
    /// Render the decoded PIC as 32 bpp BGRA pixels (Avalonia Bgra8888 / WriteableBitmap-ready).
    /// </summary>
    /// <param name="palette">
    /// The palette to draw with.  Required for a VGA-256 picture, whose colours are the game's own
    /// palette (data the caller reads); optional for EGA and CGA, which default to the canonical
    /// hardware palettes.
    /// </param>
    /// <exception cref="InvalidOperationException">A VGA-256 picture was given no palette.</exception>
    /// <remarks>
    /// That default looked for a palette file in the checkout; a codec library takes its inputs from
    /// the caller.
    /// </remarks>
    public byte[] ToBgra32(
        (byte R, byte G, byte B)[]? palette = null)
    {
        byte[] idx = PixelIndices();
        palette ??= Mode switch
        {
            PicMode.Vga8Bpp => throw new InvalidOperationException(
                "a VGA-256 picture has no built-in palette: pass the one to draw it with (the game's " +
                "palette member, the tree's palettes/palette.json, or a companion .pal)"),
            PicMode.Ega4Bpp => VgaPalette256.EgaDefault,
            PicMode.Cga2Bpp => VgaPalette256.CgaCyanMagenta,
            _ => throw new InvalidDataException(),
        };
        int mask = Mode switch
        {
            PicMode.Vga8Bpp => 0xFF,
            PicMode.Ega4Bpp => 0xF,
            PicMode.Cga2Bpp => 0x3,
            _ => 0xFF,
        };
        byte[] bgra = new byte[idx.Length * 4];
        for (int i = 0; i < idx.Length; i++)
        {
            (byte r, byte g, byte b) = palette[idx[i] & mask];
            int o = i * 4;
            bgra[o + 0] = b;
            bgra[o + 1] = g;
            bgra[o + 2] = r;
            bgra[o + 3] = 0xFF;
        }
        return bgra;
    }
}

public static class PicDecoder
{
    public const int HeaderBytes = 20;

    public static bool Looks(byte[] raw)
        => raw.Length >= HeaderBytes
           && raw[0] == 'P' && raw[1] == 'X' && raw[2] == 'P' && raw[3] == 'K';

    public static PicFile Parse(byte[] raw)
    {
        if (!Looks(raw))
            throw new InvalidDataException("missing PXPK magic");
        ushort mode = (ushort)(raw[4] | (raw[5] << 8));
        ushort width = (ushort)(raw[6] | (raw[7] << 8));
        ushort wpitch = (ushort)(raw[8] | (raw[9] << 8));
        ushort height = (ushort)(raw[10] | (raw[11] << 8));
        int declared = raw[16] | (raw[17] << 8) | (raw[18] << 16) | (raw[19] << 24);

        PicMode picMode = (PicMode)mode;
        int bpp = picMode switch
        {
            PicMode.Cga2Bpp => 2,
            PicMode.Ega4Bpp => 4,
            PicMode.Vga8Bpp => 8,
            _ => throw new InvalidDataException($"unknown PIC mode 0x{mode:X4}"),
        };
        int expected = width * height * bpp / 8;
        if (expected != declared)
            throw new InvalidDataException(
                $"PIC declared_size={declared} but w*h*bpp/8={expected}");

        byte[] body = new byte[raw.Length - HeaderBytes];
        Buffer.BlockCopy(raw, HeaderBytes, body, 0, body.Length);
        return new PicFile
        {
            Mode = picMode,
            Width = width,
            WordPitch = wpitch,
            Height = height,
            DeclaredSize = declared,
            CompressedBody = body,
            RawSource = raw,
        };
    }

    public static PicFile ParseFromFile(string path)
        => Parse(File.ReadAllBytes(path));

    /// <summary>
    /// Locate a `.pal` companion file alongside a `.pic` file on disk,
    /// using the convention established by the script-driven asset table
    /// at image@0x3F140: `&lt;basename&gt;.pic` ↔ `&lt;basename&gt;.pal`
    /// (case-insensitive). Returns null if no companion exists.
    /// </summary>
    public static string? FindCompanionPalPath(string picPath)
    {
        string? dir = Path.GetDirectoryName(picPath);
        if (string.IsNullOrEmpty(dir)) return null;
        string stem = Path.GetFileNameWithoutExtension(picPath);
        // Try several extension casings.
        foreach (string ext in new[] { ".pal", ".PAL" })
        {
            string candidate = Path.Combine(dir, stem + ext);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Locate a `.pal` companion buffer alongside a `.pic` archive entry
    /// when the .pic was extracted from an EALIB archive. Returns null
    /// if `archiveDir` is null or no companion is found.
    /// </summary>
    public static byte[]? TryLoadCompanionPalBytes(string picPath)
    {
        string? palPath = FindCompanionPalPath(picPath);
        return palPath is null ? null : File.ReadAllBytes(palPath);
    }
}
