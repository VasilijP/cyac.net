using System.Globalization;
using System.Text.Json;
using CYAC.Formats.Image;
using CYAC.Port.Transform.Image;
using CYAC.Port.Transform.Json;
using CYAC.Port.Transform.Transform;

namespace CYAC.Port.Transform.Families;

/// <summary>
/// <c>.msk</c> overlay masks → <c>images/masks/&lt;name&gt;.png</c> (1 bit per pixel, as an 8-bit
/// indexed PNG of 0/1) plus <c>images/masks/&lt;name&gt;.json</c> (the geometry and where it was
/// proven from).
/// </summary>
/// <remarks>
/// <para>
/// The file has no header of any kind: it is <c>ceil(width/8) · height</c> bytes
/// of MSB-first 1-bpp bitmap (bit set = opaque) — see <see cref="MaskBitmap"/> for the bit-order proof.
/// The dimensions come from outside the file, in one of two ways, and every one of the 19 shipping
/// masks is covered by one of them, with the stored length matching <c>ceil(w/8)·h</c> exactly and no
/// byte left over:
/// </para>
/// <list type="number">
///   <item><b>Per-aircraft cockpit masks</b> (13 members, incl. the duplicated <c>90_horiz.msk</c>):
///   the region rectangle in the program image, <see cref="CockpitMaskGeometry"/>.</item>
///   <item><b>Companion masks</b> (6 members): the same-named <c>.pic</c> in the same archive, with
///   an optional trailing <c>m</c> on the mask's stem — <c>cursorm.msk</c> ↔ <c>cursor.pic</c>
///   (128×24), <c>cursor2m.msk</c> ↔ <c>cursor2.pic</c> (32×24), <c>insigm.msk</c> ↔
///   <c>INSIG.PIC</c> (128×11), <c>bulletm.msk</c> ↔ <c>bullet.pic</c> (48×38),
///   <c>PLANES0.MSK</c>/<c>PLANES1.MSK</c> ↔ <c>PLANES0.PIC</c>/<c>PLANES1.PIC</c> (112×200).  The
///   pairing is how the game uses them: the mask says which pixels of its <c>.pic</c> are drawn.</item>
/// </list>
/// <para>
/// A mask that fits neither rule, or whose length contradicts the geometry, is <b>declined</b> — the
/// runner then carries it as a counted <c>.raw</c> rather than letting a guess into the tree.
/// </para>
/// </remarks>
public sealed class MaskTransform : IFamilyTransform
{
    /// <summary>The data tree folder this family writes into.</summary>
    public const string Folder = "images/masks";

    /// <inheritdoc/>
    public string Family => "msk";

    /// <inheritdoc/>
    public string TreeDescription =>
        "`images/masks/<name>.png` — a `.msk` overlay mask as a 1-bit image (white = opaque, black " +
        "= transparent); `images/masks/<name>.json` records the width/height the file itself does " +
        "not carry and cites where they were read from (the cockpit region table in the program " +
        "image, or the companion `.pic`).";

    /// <inheritdoc/>
    public FidelityRule FidelityRule => FidelityRule.Exact;

    /// <inheritdoc/>
    public bool Claims(TransformSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.EntryIndex is not null && source.Extension == ".msk";
    }

    /// <inheritdoc/>
    public IReadOnlyList<TransformOutput> Forward(TransformSource source, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);

        ReadOnlySpan<byte> body = source.Content.Span;
        (int width, int height, MaskGeometryDto geometry) = ResolveGeometry(source, context, body.Length);
        byte[] pixels = MaskBitmap.Decode(body, width, height);
        byte[] pad = MaskBitmap.PadBits(body, width, height);

        UnknownBytes unknown = new UnknownBytes();
        bool oddPad = false;
        foreach (byte bit in pad)
        {
            oddPad |= bit != 0;
        }

        if (oddPad)
        {
            unknown.Add(0, pad);
        }

        string stem = TransformContext.SafeFileName(source.Stem);
        string pngPath = context.Allocate($"{Folder}/{stem}.png");
        string jsonPath = context.Allocate(Path.ChangeExtension(pngPath, ".json"));

        MaskDto dto = new MaskDto
        {
            Format = "cyac.image.mask/1",
            About =
                "A .msk overlay mask: 1 bit per pixel, most-significant bit leftmost, row pitch " +
                "ceil(width/8), NO header. A set bit selects the pixel's VGA plane in " +
                "gfx_masked_blit (image@0x1D162), i.e. 1 = opaque, 0 = see-through. The width and " +
                "height are not in the file — see \"geometry\" for where they were read from.",
            Source = $"{source.OriginFile}/{source.Name}",
            Width = width,
            Height = height,
            BitsPerPixel = MaskBitmap.BitsPerPixel,
            PitchBytes = MaskBitmap.PitchFor(width),
            Pixels = pngPath,
            Geometry = geometry,
            UnknownPadBits = oddPad ? Convert.ToHexString(pad) : null,
        };

        return
        [
            new TransformOutput(
                jsonPath,
                JsonSerializer.SerializeToUtf8Bytes(dto, TransformJsonContext.Readable.MaskDto),
                OutputRole.Data,
                OutputFidelity.Exact,
                $"geometry: {geometry.Citation}",
                unknown.Count),
            new TransformOutput(
                pngPath,
                IndexedPng.Encode(width, height, pixels, ImagePalettes.Monochrome.Rgb),
                OutputRole.Data,
                OutputFidelity.Exact,
                $"geometry: {geometry.Citation}"),
        ];
    }

    /// <inheritdoc/>
    public byte[] Inverse(IReadOnlyList<LoadedOutput> outputs, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        LoadedOutput json = outputs.FirstOrDefault(o => o.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidDataException("msk expects a .json side-car among its outputs");
        LoadedOutput png = outputs.FirstOrDefault(o => o.Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                           ?? throw new InvalidDataException("msk expects a .png among its outputs");
        return ToBytes(json.Bytes, png.Bytes);
    }

    /// <summary>Rebuilds the stored mask from the tree's side-car and PNG.</summary>
    /// <param name="json">The side-car bytes.</param>
    /// <param name="png">The 1-bit image's bytes.</param>
    /// <exception cref="InvalidDataException">The pair is inconsistent or malformed.</exception>
    public static byte[] ToBytes(ReadOnlySpan<byte> json, ReadOnlySpan<byte> png)
    {
        MaskDto dto = JsonSerializer.Deserialize(json, TransformJsonContext.Readable.MaskDto)
                      ?? throw new InvalidDataException("mask JSON is empty");
        (int width, int height, byte[] indices, _) = IndexedPng.Decode(png);
        if (width != dto.Width || height != dto.Height)
        {
            throw new InvalidDataException(
                $"the PNG is {width}x{height} but the side-car says {dto.Width}x{dto.Height}");
        }

        byte[] pad = string.IsNullOrEmpty(dto.UnknownPadBits) ? [] : Convert.FromHexString(dto.UnknownPadBits);
        return MaskBitmap.Encode(indices, width, height, pad);
    }

    private static (int Width, int Height, MaskGeometryDto Geometry) ResolveGeometry(
        TransformSource source, TransformContext context, int storedLength)
    {
        IOriginalData originals = context.Originals
                                  ?? throw new InvalidDataException(
                                      "a .msk carries no dimensions, so it can only be transformed with the other " +
                                      "originals available");

        byte[]? image = originals.TryGetProgramImage();
        if (image is not null &&
            CockpitMaskGeometry.TryResolve(image, source.Name) is { } rect &&
            rect.Width > 0 && rect.Height > 0 &&
            MaskBitmap.SizeFor(rect.Width, rect.Height) == storedLength)
        {
            string citation =
                $"image DGROUP+0x{rect.TableDgroupOffset.ToString("X4", CultureInfo.InvariantCulture)} " +
                $"(region \"{rect.Region}\" table + aircraft index {rect.AircraftIndex} x 8)";
            return (rect.Width, rect.Height, new MaskGeometryDto
            {
                From = "program-image-cockpit-region-table",
                Citation = citation,
                Aircraft = rect.Aircraft,
                AircraftIndex = rect.AircraftIndex,
                Region = rect.Region,
                ScreenX = rect.X,
                ScreenY = rect.Y,
            });
        }

        foreach (string candidate in CompanionStems(source.Stem))
        {
            byte[]? companion = originals.TryGetArchiveMember(source.OriginFile, candidate + ".pic");
            if (companion is null || !PicDecoder.Looks(companion))
            {
                continue;
            }

            PicFile pic = PicDecoder.Parse(companion);
            if (MaskBitmap.SizeFor(pic.Width, pic.Height) != storedLength)
            {
                continue;
            }

            return (pic.Width, pic.Height, new MaskGeometryDto
            {
                From = "companion-pic",
                Citation = $"{source.OriginFile}/{candidate}.pic is {pic.Width}x{pic.Height}",
                Companion = $"{source.OriginFile}/{candidate}.pic",
            });
        }

        throw new InvalidDataException(
            $"{source}: {storedLength} B of headerless mask and nothing proves its geometry — no " +
            "cockpit region rectangle and no companion .pic of a matching size");
    }

    // `<stem>m.msk` pairs with `<stem>.pic` (cursorm/cursor, insigm/INSIG, bulletm/bullet); the two
    // aircraft-preview masks pair name-for-name (PLANES0.MSK/PLANES0.PIC).  Both spellings are tried
    // and the stored length decides, so the rule can never mis-pair silently.
    private static IEnumerable<string> CompanionStems(string stem)
    {
        yield return stem;
        if (stem.EndsWith('m') && stem.Length > 1)
        {
            yield return stem[..^1];
        }
    }
}
