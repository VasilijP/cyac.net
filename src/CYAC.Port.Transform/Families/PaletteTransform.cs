using System.Text.Json;
using CYAC.Port.Transform.Image;
using CYAC.Port.Transform.Json;
using CYAC.Port.Transform.Transform;

namespace CYAC.Port.Transform.Families;

/// <summary>
/// VGA palettes → <c>palettes/&lt;name&gt;.json</c> (the raw 6-bit DAC values, editable) plus a
/// <c>.png</c> swatch for humans.
/// </summary>
/// <remarks>
/// <para>
/// Claimed assets (<c>resources/ASSETS.md</c>): the two <c>.pal</c> members of <c>1b.lib</c> —
/// <c>title0v.pal</c> and <c>title1v.pal</c>, LZSS-compressed, declared decompressed size 0x300 —
/// and the extension-less raw member <c>1a.lib/palette</c>, "VGA 6-bit DAC palette (256*3=768
/// bytes)".  The plan's family table counts the same three ("`.pal` (+ `1a/palette` raw), 2+1").
/// </para>
/// <para>
/// The body is a flat run of RGB triples, one per palette index, each component a <b>6-bit</b> DAC
/// value 0..63 — the width the VGA DAC accepts (platform: IBM VGA, DAC data register 0x3C9 takes 6
/// significant bits).  The game loads them through the DOS/BIOS path at <c>image@0x24F6A</c>
/// (<c>MOV AX,0x3408</c>) / <c>image@0x24F82</c> (<c>MOV AX,0x3414</c>) for the two title palettes
/// and <c>image@0x2409E</c> for <c>1a/palette</c>.
/// </para>
/// <para>
/// The JSON keeps the raw stored bytes, so the round trip is exact even for a component above 63
/// (the DAC would ignore the high bits; the transform must not).  The PNG is 8-bit indexed with the
/// palette expanded to 8 bits as <c>v&lt;&lt;2 | v&gt;&gt;4</c> — the standard 6→8 bit replication,
/// which is injective, so the swatch shows true colour without becoming a second source of truth.
/// </para>
/// </remarks>
public sealed class PaletteTransform : IFamilyTransform
{
    /// <summary>The data tree folder this family writes into.</summary>
    public const string Folder = "palettes";

    /// <summary>Bytes per palette entry: R, G, B.</summary>
    public const int ComponentsPerColor = 3;

    /// <summary>Significant bits per component in the stored data (VGA DAC width).</summary>
    public const int ComponentBits = 6;

    /// <summary>Columns in the generated swatch.</summary>
    public const int SwatchColumns = 16;

    /// <summary>The extension-less raw palette member of <c>1a.lib</c>.</summary>
    public const string RawPaletteEntryName = "palette";

    /// <inheritdoc/>
    public string Family => "palette";

    /// <inheritdoc/>
    public string TreeDescription =>
        "`palettes/<name>.json` — VGA palettes as raw 6-bit DAC triples (0..63), the editable source " +
        "of truth; `palettes/<name>.png` is a generated 16-wide swatch (6-bit values expanded to 8 " +
        "as v<<2|v>>4) and is never read back.";

    /// <inheritdoc/>
    public FidelityRule FidelityRule => FidelityRule.Exact;

    /// <inheritdoc/>
    public bool Claims(TransformSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.EntryIndex is null)
        {
            return false;
        }

        return source.Extension == ".pal"
            || string.Equals(source.Name, RawPaletteEntryName, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public IReadOnlyList<TransformOutput> Forward(TransformSource source, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);

        ReadOnlySpan<byte> body = source.Content.Span;
        int colorCount = body.Length / ComponentsPerColor;
        int trailing = body.Length % ComponentsPerColor;
        if (colorCount == 0)
        {
            throw new InvalidDataException(
                $"{source}: {body.Length} bytes is not a palette (expected a multiple of " +
                $"{ComponentsPerColor}, {IndexedPng.MaxPaletteEntries * ComponentsPerColor} for a full VGA set)");
        }

        List<int[]> colors = new List<int[]>(colorCount);
        for (int i = 0; i < colorCount; i++)
        {
            int o = i * ComponentsPerColor;
            colors.Add([body[o], body[o + 1], body[o + 2]]);
        }

        UnknownBytes unknown = new UnknownBytes();
        if (trailing != 0)
        {
            unknown.Add(colorCount * ComponentsPerColor, body[(colorCount * ComponentsPerColor)..]);
        }

        PaletteDto dto = new PaletteDto
        {
            Format = "cyac.palette/1",
            About =
                $"VGA palette, {ComponentBits}-bit DAC components (0..63) exactly as stored. " +
                "Row order is palette index order; the sibling .png is a generated swatch.",
            Source = $"{source.OriginFile}/{source.Name}",
            ComponentBits = ComponentBits,
            ColorCount = colorCount,
            Colors = colors,
            UnknownTrailing = trailing == 0
                ? null
                : Convert.ToHexString(body[(colorCount * ComponentsPerColor)..]),
        };

        string jsonPath = context.Allocate($"{Folder}/{TransformContext.SafeFileName(source.Stem)}.json");
        string pngPath = context.Allocate(Path.ChangeExtension(jsonPath, ".png"));

        return
        [
            new TransformOutput(
                jsonPath,
                JsonSerializer.SerializeToUtf8Bytes(dto, TransformJsonContext.Readable.PaletteDto),
                OutputRole.Data,
                OutputFidelity.Exact,
                null,
                unknown.Count),
            TransformOutput.View(pngPath, RenderSwatch(colors), jsonPath),
        ];
    }

    /// <inheritdoc/>
    public byte[] Inverse(IReadOnlyList<LoadedOutput> outputs, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        LoadedOutput json = outputs.SingleOrDefault(o => o.Role == OutputRole.Data)
                            ?? throw new InvalidDataException("palette expects exactly one data output");
        return ToBytes(json.Bytes);
    }

    /// <summary>Rebuilds the stored palette bytes from the tree's JSON.</summary>
    /// <param name="json">The <c>palettes/&lt;name&gt;.json</c> bytes.</param>
    /// <exception cref="InvalidDataException">The document is malformed.</exception>
    public static byte[] ToBytes(ReadOnlySpan<byte> json)
    {
        PaletteDto dto = JsonSerializer.Deserialize(json, TransformJsonContext.Readable.PaletteDto)
                         ?? throw new InvalidDataException("palette JSON is empty");
        List<int[]> colors = dto.Colors ?? throw new InvalidDataException("palette JSON has no \"colors\"");
        if (dto.ColorCount != colors.Count)
        {
            throw new InvalidDataException(
                $"\"colorCount\" is {dto.ColorCount} but \"colors\" holds {colors.Count} rows");
        }

        byte[] trailing = string.IsNullOrEmpty(dto.UnknownTrailing)
            ? []
            : Convert.FromHexString(dto.UnknownTrailing);

        byte[] bytes = new byte[(colors.Count * ComponentsPerColor) + trailing.Length];
        for (int i = 0; i < colors.Count; i++)
        {
            for (int c = 0; c < ComponentsPerColor; c++)
            {
                int component = colors[i][c];
                if (component is < 0 or > 0xFF)
                {
                    throw new InvalidDataException(
                        $"\"colors\"[{i}][{c}] = {component} does not fit in a byte");
                }

                bytes[(i * ComponentsPerColor) + c] = (byte)component;
            }
        }

        trailing.CopyTo(bytes.AsSpan(colors.Count * ComponentsPerColor));
        return bytes;
    }

    /// <summary>The 6→8 bit component expansion used for the swatch: <c>v&lt;&lt;2 | v&gt;&gt;4</c>.</summary>
    /// <param name="component">A stored component; only its low 6 bits reach the DAC.</param>
    public static byte ToEightBit(int component)
    {
        int v = component & 0x3F;
        return (byte)((v << 2) | (v >> 4));
    }

    private static byte[] RenderSwatch(IReadOnlyList<int[]> colors)
    {
        // A PNG palette holds at most 256 entries and an 8-bit index can address no more; a longer
        // palette (none ships) is shown truncated — the JSON keeps every entry.
        int shown = Math.Min(colors.Count, IndexedPng.MaxPaletteEntries);
        int width = Math.Min(SwatchColumns, shown);
        int height = (shown + width - 1) / width;

        byte[] palette = new byte[shown * ComponentsPerColor];
        for (int i = 0; i < shown; i++)
        {
            palette[(i * ComponentsPerColor) + 0] = ToEightBit(colors[i][0]);
            palette[(i * ComponentsPerColor) + 1] = ToEightBit(colors[i][1]);
            palette[(i * ComponentsPerColor) + 2] = ToEightBit(colors[i][2]);
        }

        byte[] indices = new byte[width * height];
        for (int i = 0; i < indices.Length; i++)
        {
            // Pad an incomplete last row with index 0 — a swatch is a view, not data.
            indices[i] = (byte)(i < shown ? i : 0);
        }

        return IndexedPng.Encode(width, height, indices, palette);
    }
}
