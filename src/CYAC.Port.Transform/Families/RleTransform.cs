using System.Text.Json;
using CYAC.Formats.Image;
using CYAC.Port.Transform.Image;
using CYAC.Port.Transform.Json;
using CYAC.Port.Transform.Transform;

namespace CYAC.Port.Transform.Families;

/// <summary>
/// <c>.rle</c> sprites → <c>images/&lt;name&gt;.png</c> (8-bit indexed, colour-key index preserved)
/// plus <c>images/&lt;name&gt;.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// One <c>.rle</c> ships: <c>2a.lib/EXP.RLE</c>.  Format and consumer: <see cref="RleSpriteCodec"/>
/// — <c>gfx_sprite_clip_and_blit @ image@0x1D966</c>, corrected by B18.
/// </para>
/// <para>
/// <b>One frame, not a strip</b> (T3): the header declares 115×87, the payload holds exactly 87 row
/// blocks and ends flush with the last one.  Nothing in the file or in the consumer divides it
/// further — the blitter scales the whole rectangle through its two Bresenham axes.
/// </para>
/// <para>
/// The re-encode is <b>exact</b>, not merely valid: the original encoder emits a solid run for any
/// repeat of two or more pixels and literals otherwise, which reproduces all 87 rows byte for byte
/// (<see cref="RleSpriteCodec.MinSolidRun"/>).
/// </para>
/// </remarks>
public sealed class RleTransform : IFamilyTransform
{
    /// <summary>The data tree folder this family writes into.</summary>
    public const string Folder = "images";

    /// <inheritdoc/>
    public string Family => "rle";

    /// <inheritdoc/>
    public string TreeDescription =>
        "`images/<name>.png` — a `.rle` sprite as an 8-bit indexed PNG of palette indices, with the " +
        "transparent colour left in place at its own index; `images/<name>.json` records the " +
        "colour key, the format flags and the palette the PNG was drawn with.";

    /// <inheritdoc/>
    public FidelityRule FidelityRule => FidelityRule.Exact;

    /// <inheritdoc/>
    public bool Claims(TransformSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.EntryIndex is not null && source.Extension == ".rle";
    }

    /// <inheritdoc/>
    public IReadOnlyList<TransformOutput> Forward(TransformSource source, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);

        RleSprite sprite = RleSpriteCodec.Decode(source.Content.Span);
        ImagePalette palette = ImagePalettes.Resolve(context, source.OriginFile, source.Stem, 8);

        UnknownBytes unknown = new UnknownBytes();
        bool keyHigh = unknown.AddIfNonZero(0x05, [sprite.ColorKeyHigh]);
        bool flagsHigh = unknown.AddIfNonZero(0x07, [sprite.FormatFlagsHigh]);

        string stem = TransformContext.SafeFileName(source.Stem);
        string pngPath = context.Allocate($"{Folder}/{stem}.png");
        string jsonPath = context.Allocate(Path.ChangeExtension(pngPath, ".json"));

        RleDto dto = new RleDto
        {
            Format = "cyac.image.rle/1",
            About =
                "A run-length-encoded sprite. The .png holds palette indices, one byte per pixel, " +
                "including the transparent ones: a pixel is see-through when its index equals " +
                "\"colorKey\". Row format: u16 byte-length, then runs — descriptor bit 7 set = " +
                "solid run of (d & 0x7F) pixels of the next byte, clear = d literal pixels. " +
                "Consumer: gfx_sprite_clip_and_blit @ image@0x1D966.",
            Source = $"{source.OriginFile}/{source.Name}",
            Width = sprite.Width,
            Height = sprite.Height,
            Frames = 1,
            ColorKey = sprite.ColorKey,
            FormatFlags = sprite.FormatFlags,
            Pixels = pngPath,
            Palette = new PaletteBindingDto { Source = palette.Source, Binding = palette.Binding },
            Unknown0x05 = keyHigh ? Convert.ToHexString([sprite.ColorKeyHigh]) : null,
            Unknown0x07 = flagsHigh ? Convert.ToHexString([sprite.FormatFlagsHigh]) : null,
        };

        return
        [
            new TransformOutput(
                jsonPath,
                JsonSerializer.SerializeToUtf8Bytes(dto, TransformJsonContext.Readable.RleDto),
                OutputRole.Data,
                OutputFidelity.Exact,
                $"palette: {palette.Source} ({palette.Binding})",
                unknown.Count),
            new TransformOutput(
                pngPath,
                IndexedPng.Encode(sprite.Width, sprite.Height, sprite.Pixels, palette.Rgb),
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
                            ?? throw new InvalidDataException("rle expects a .json side-car among its outputs");
        LoadedOutput png = outputs.FirstOrDefault(o => o.Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                           ?? throw new InvalidDataException("rle expects a .png among its outputs");
        return ToBytes(json.Bytes, png.Bytes);
    }

    /// <summary>Rebuilds the stored sprite from the tree's side-car and PNG.</summary>
    /// <param name="json">The side-car bytes.</param>
    /// <param name="png">The image's bytes.</param>
    /// <exception cref="InvalidDataException">The pair is inconsistent or malformed.</exception>
    public static byte[] ToBytes(ReadOnlySpan<byte> json, ReadOnlySpan<byte> png)
    {
        RleDto dto = JsonSerializer.Deserialize(json, TransformJsonContext.Readable.RleDto)
                     ?? throw new InvalidDataException("rle JSON is empty");
        (int width, int height, byte[] indices, _) = IndexedPng.Decode(png);
        if (width != dto.Width || height != dto.Height)
        {
            throw new InvalidDataException(
                $"the PNG is {width}x{height} but the side-car says {dto.Width}x{dto.Height}");
        }

        return RleSpriteCodec.Encode(new RleSprite(
            width,
            height,
            (byte)dto.ColorKey,
            Single(dto.Unknown0x05),
            (byte)dto.FormatFlags,
            Single(dto.Unknown0x07),
            indices));
    }

    private static byte Single(string? hex)
    {
        if (string.IsNullOrEmpty(hex))
        {
            return 0;
        }

        byte[] bytes = Convert.FromHexString(hex);
        return bytes.Length == 1
            ? bytes[0]
            : throw new InvalidDataException($"expected one hex byte, got \"{hex}\"");
    }
}
