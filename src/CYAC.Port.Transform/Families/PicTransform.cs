using System.Globalization;
using System.Text.Json;
using CYAC.Formats.Image;
using CYAC.Port.Transform.Image;
using CYAC.Port.Transform.Json;
using CYAC.Port.Transform.Transform;

namespace CYAC.Port.Transform.Families;

/// <summary>
/// <c>.pic</c> (PXPK) screens → <c>images/&lt;name&gt;.png</c> (8-bit indexed, the pixel indices)
/// plus <c>images/&lt;name&gt;.json</c> (mode, geometry, palette binding).
/// </summary>
/// <remarks>
/// <para>
/// Format closed against <c>pic_codec_decompress @ image@0x2FBD6</c>: a 16-byte PXPK
/// header followed by the ordinary EALIB flag=0x01 asset framing (u32-LE size + LZSS).  The archive
/// stores <c>.pic</c> members verbatim (encoding flag 0x03), so the inverse has to reproduce the
/// compressed stream too — <see cref="PicEncoder"/> does, on all 36 shipping files.
/// </para>
/// <para>
/// The PNG keeps <b>indices</b>, not colours, so the round trip is exact and a modder can repaint
/// the palette without touching the art.  Which palette the PNG is <i>shown</i> with is program
/// state, recorded in the side-car and in the manifest note rather than baked in
/// (<see cref="ImagePalettes"/>).
/// </para>
/// </remarks>
public sealed class PicTransform : IFamilyTransform
{
    /// <summary>The data tree folder this family writes into.</summary>
    public const string Folder = "images";

    /// <inheritdoc/>
    public string Family => "pic";

    /// <inheritdoc/>
    public string TreeDescription =>
        "`images/<name>.png` — a `.pic` screen as an 8-bit indexed PNG whose pixel values are the " +
        "original palette indices; `images/<name>.json` carries the mode, the geometry and which " +
        "palette the PNG was drawn with (companion `.pal`, the global `1a.lib/palette`, or the " +
        "mode's platform default). Both files together rebuild the stored PXPK asset exactly.";

    /// <inheritdoc/>
    public FidelityRule FidelityRule => FidelityRule.Exact;

    /// <inheritdoc/>
    public bool Claims(TransformSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.EntryIndex is not null
            && source.Extension == ".pic"
            && PicDecoder.Looks(source.Content.ToArray());
    }

    /// <inheritdoc/>
    public IReadOnlyList<TransformOutput> Forward(TransformSource source, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);

        PicFile pic = PicDecoder.Parse(source.Content.ToArray());
        byte[] indices = pic.PixelIndices();
        ImagePalette palette = ImagePalettes.Resolve(context, source.OriginFile, source.Stem, pic.Bpp);

        UnknownBytes unknown = new UnknownBytes();
        ReadOnlySpan<byte> reserved = source.Content.Span.Slice(12, 4);
        bool oddReserved = unknown.AddIfNonZero(0x0C, reserved);

        string stem = TransformContext.SafeFileName(source.Stem);
        string pngPath = context.Allocate($"{Folder}/{stem}.png");
        string jsonPath = context.Allocate(Path.ChangeExtension(pngPath, ".json"));

        PicDto dto = new PicDto
        {
            Format = "cyac.image.pic/1",
            About =
                "A .pic (PXPK) screen. The .png holds the ORIGINAL PALETTE INDICES, one byte per " +
                "pixel, so editing it edits the art and editing the palette recolours it. " +
                "`wordPitch` is the header's own bytes-per-row/2; the codec is at image@0x2FBD6.",
            Source = $"{source.OriginFile}/{source.Name}",
            Mode = pic.ModeName,
            ModeValue = (int)pic.Mode,
            BitsPerPixel = pic.Bpp,
            Width = pic.Width,
            Height = pic.Height,
            WordPitch = pic.WordPitch,
            DeclaredSize = pic.DeclaredSize,
            Pixels = pngPath,
            Palette = new PaletteBindingDto { Source = palette.Source, Binding = palette.Binding },
            Unknown0x0C = oddReserved ? Convert.ToHexString(reserved) : null,
        };

        return
        [
            new TransformOutput(
                jsonPath,
                JsonSerializer.SerializeToUtf8Bytes(dto, TransformJsonContext.Readable.PicDto),
                OutputRole.Data,
                OutputFidelity.Exact,
                $"palette: {palette.Source} ({palette.Binding})",
                unknown.Count),
            new TransformOutput(
                pngPath,
                IndexedPng.Encode(pic.Width, pic.Height, indices, PaletteFor(palette, pic.Bpp)),
                OutputRole.Data,
                OutputFidelity.Exact,
                $"palette: {palette.Source} ({palette.Binding})"),
        ];
    }

    /// <inheritdoc/>
    public byte[] Inverse(IReadOnlyList<LoadedOutput> outputs, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        LoadedOutput json = outputs.FirstOrDefault(o => o.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidDataException("pic expects a .json side-car among its outputs");
        LoadedOutput png = outputs.FirstOrDefault(o => o.Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                           ?? throw new InvalidDataException("pic expects a .png among its outputs");
        return ToBytes(json.Bytes, png.Bytes);
    }

    /// <summary>Rebuilds the stored PXPK asset from the tree's side-car and PNG.</summary>
    /// <param name="json">The <c>images/&lt;name&gt;.json</c> bytes.</param>
    /// <param name="png">The <c>images/&lt;name&gt;.png</c> bytes.</param>
    /// <exception cref="InvalidDataException">The pair is inconsistent or malformed.</exception>
    public static byte[] ToBytes(ReadOnlySpan<byte> json, ReadOnlySpan<byte> png)
    {
        PicDto dto = JsonSerializer.Deserialize(json, TransformJsonContext.Readable.PicDto)
                     ?? throw new InvalidDataException("pic JSON is empty");
        (int width, int height, byte[] indices, _) = IndexedPng.Decode(png);
        if (width != dto.Width || height != dto.Height)
        {
            throw new InvalidDataException(
                $"the PNG is {width}x{height} but the side-car says {dto.Width}x{dto.Height}");
        }

        PicMode mode = (PicMode)dto.ModeValue;
        byte[] body = PicEncoder.Pack(mode, width, height, indices);
        if (body.Length != dto.DeclaredSize)
        {
            throw new InvalidDataException(
                $"the packed body is {body.Length} B but \"declaredSize\" is {dto.DeclaredSize}");
        }

        byte[] reserved = string.IsNullOrEmpty(dto.Unknown0x0C) ? [] : Convert.FromHexString(dto.Unknown0x0C);
        return PicEncoder.Encode(mode, width, dto.WordPitch, height, body, reserved);
    }

    // A PNG palette must cover every index the image uses; the low-colour modes' defaults are 16 and
    // 4 entries, which is exactly their index range.
    private static byte[] PaletteFor(ImagePalette palette, int bitsPerPixel)
    {
        int needed = 1 << bitsPerPixel;
        if (palette.Rgb.Length >= needed * 3)
        {
            return palette.Rgb;
        }

        throw new InvalidDataException(
            $"palette {palette.Source} has {palette.Rgb.Length / 3} entries; a {bitsPerPixel}bpp " +
            $"image needs {needed.ToString(CultureInfo.InvariantCulture)}");
    }
}
