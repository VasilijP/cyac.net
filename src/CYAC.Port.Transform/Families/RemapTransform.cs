using System.Text.Json;
using CYAC.Formats.EaLib;
using CYAC.Port.Transform.Json;
using CYAC.Port.Transform.Transform;

namespace CYAC.Port.Transform.Families;

/// <summary>
/// <c>remap.bin</c> → <c>remap.json</c>: the 256-entry colour-translation table that maps the
/// 256-colour authoring palette onto a 16-colour video mode.
/// </summary>
/// <remarks>
/// Loaded by <c>palette_and_remap_load @image@0x2409E</c> into
/// <c>g_color_xlat_table_farptr [0x478A]</c> and applied by
/// <c>gfx_or_palette_color_select @image@0x24112</c>, which returns <c>table[index]</c> whenever
/// <c>g_render_state_mask [0xE64E]</c> is below 0x100 (i.e. any non-VGA mode) and passes the index
/// through unchanged at 0x100.  The asset is 157 bytes <i>stored</i> and 256 bytes decompressed;
/// the 157 quoted across the project's docs is the compressed size (see <see cref="RemapBinDecoder"/>).
/// </remarks>
public sealed class RemapTransform : IFamilyTransform
{
    /// <summary>The data-tree path this family writes.</summary>
    public const string OutputPath = "remap.json";

    /// <inheritdoc/>
    public string Family => "remap";

    /// <inheritdoc/>
    public string TreeDescription =>
        "`remap.json` — one entry per palette index: the colour to draw it as when the game is not " +
        "in 256-colour mode. Entries 0..15 are the identity and no entry exceeds 15, so this is the " +
        "256→16 colour reduction the EGA/CGA paths use.";

    /// <inheritdoc/>
    public FidelityRule FidelityRule => FidelityRule.Exact;

    /// <inheritdoc/>
    public bool Claims(TransformSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.EntryIndex is not null && RemapBinDecoder.IsRemapTable(source.Name);
    }

    /// <inheritdoc/>
    public IReadOnlyList<TransformOutput> Forward(TransformSource source, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);

        byte[] entries = RemapBinDecoder.Parse(source.Content.Span);
        RemapDto dto = new RemapDto
        {
            Format = "cyac.color-remap/1",
            About =
                "Colour-translation table. gfx_or_palette_color_select @image@0x24112 returns " +
                "translatedColor[i] for authored colour i while the video mode is not 256-colour " +
                "(g_render_state_mask [0xE64E] < 0x100), and returns i unchanged in VGA. The row " +
                "index is the authored colour; the value is what the 16-colour mode draws.",
            Source = $"{source.OriginFile}/{source.Name}",
            EntryCount = entries.Length,
            MaxTranslatedIndex = entries.Max(),
            TranslatedColor = [.. entries.Select(b => (int)b)],
        };

        string path = context.Allocate(OutputPath);
        return
        [
            new TransformOutput(
                path,
                JsonSerializer.SerializeToUtf8Bytes(dto, TransformJsonContext.Readable.RemapDto),
                OutputRole.Data,
                OutputFidelity.Exact,
                $"{entries.Length} entries, max translated index {entries.Max()}"),
        ];
    }

    /// <inheritdoc/>
    public byte[] Inverse(IReadOnlyList<LoadedOutput> outputs, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        LoadedOutput json = outputs.SingleOrDefault(o => o.Role == OutputRole.Data)
                            ?? throw new InvalidDataException("remap expects exactly one data output");
        return ToBytes(json.Bytes);
    }

    /// <summary>Rebuilds <c>remap.bin</c>'s decompressed body from the tree's JSON.</summary>
    /// <param name="json">The <c>remap.json</c> bytes.</param>
    /// <exception cref="InvalidDataException">The document is malformed.</exception>
    public static byte[] ToBytes(ReadOnlySpan<byte> json)
    {
        RemapDto dto = JsonSerializer.Deserialize(json, TransformJsonContext.Readable.RemapDto)
                       ?? throw new InvalidDataException("remap.json is empty");
        List<int> entries = dto.TranslatedColor
                            ?? throw new InvalidDataException("remap.json has no \"translatedColor\"");

        byte[] bytes = new byte[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i] is < 0 or > 0xFF)
            {
                throw new InvalidDataException(
                    $"\"translatedColor\"[{i}] = {entries[i]} is not a palette index");
            }

            bytes[i] = (byte)entries[i];
        }

        return RemapBinDecoder.ToBytes(bytes);
    }
}
