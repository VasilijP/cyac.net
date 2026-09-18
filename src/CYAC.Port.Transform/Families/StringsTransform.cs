using System.Text.Json;
using CYAC.Formats.EaLib;
using CYAC.Port.Transform.Json;
using CYAC.Port.Transform.Transform;

namespace CYAC.Port.Transform.Families;

/// <summary>
/// <c>strings.bin</c> → <c>strings.json</c>: the UI string catalog, with each string's call sites
/// beside it so a translator can see where it appears.
/// </summary>
/// <remarks>
/// <para>
/// The container is <see cref="StringsBinCodec"/> (NUL-separated body, NUL padding, a trailing u16
/// offset table the runtime never reads).  The table is regenerated on the way
/// back, so nothing about the file is left unexplained.
/// </para>
/// <para>
/// The "used by" annotations come from <see cref="StringsBinCallerCatalog"/> (the 29-call-site
/// cross-reference).  They are derived, so they carry the <c>_</c> prefix and are
/// ignored when the file is read back — edit <c>text</c>, not <c>_usedBy</c>.
/// </para>
/// </remarks>
public sealed class StringsTransform : IFamilyTransform
{
    /// <summary>The data-tree path this family writes.</summary>
    public const string OutputPath = "strings.json";

    /// <summary>The asset name this family claims.</summary>
    public const string AssetName = "strings.bin";

    /// <inheritdoc/>
    public string Family => "strings";

    /// <inheritdoc/>
    public string TreeDescription =>
        "`strings.json` — every UI string the game prints from `strings.bin`, in index order, each " +
        "with the screens that show it. This is the file to translate.";

    /// <inheritdoc/>
    public FidelityRule FidelityRule => FidelityRule.Exact;

    /// <inheritdoc/>
    public bool Claims(TransformSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.EntryIndex is not null
            && string.Equals(source.Name, AssetName, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public IReadOnlyList<TransformOutput> Forward(TransformSource source, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);

        StringsBinFile table = StringsBinCodec.Parse(source.Content.Span);
        StringsDto dto = new StringsDto
        {
            Format = "cyac.strings/1",
            About =
                "The UI string catalog. Index order is what the code passes in AX to " +
                "strings_bin_lookup_and_copy @image@0x23FFC, so the ORDER is the interface: adding " +
                "or removing a string renumbers everything after it. Text is latin-1 and may carry " +
                "a tab; the trailing offset table the original file ends with is regenerated on " +
                "export. Fields prefixed `_` are derived and ignored on import.",
            Source = $"{source.OriginFile}/{source.Name}",
            PadNuls = table.PadNulCount,
            Strings = [.. table.Strings.Select((text, index) => new UiStringDto
            {
                Index = index,
                Text = text,
                UsedBy = Callers(index),
            })],
        };

        string path = context.Allocate(OutputPath);
        return
        [
            new TransformOutput(
                path,
                JsonSerializer.SerializeToUtf8Bytes(dto, TransformJsonContext.Readable.StringsDto),
                OutputRole.Data,
                OutputFidelity.Exact,
                $"{table.Strings.Count} strings"),
        ];
    }

    /// <inheritdoc/>
    public byte[] Inverse(IReadOnlyList<LoadedOutput> outputs, TransformContext context)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        LoadedOutput json = outputs.SingleOrDefault(o => o.Role == OutputRole.Data)
                            ?? throw new InvalidDataException("strings expects exactly one data output");
        return ToBytes(json.Bytes);
    }

    /// <summary>Rebuilds <c>strings.bin</c>'s decompressed body from the tree's JSON.</summary>
    /// <param name="json">The <c>strings.json</c> bytes.</param>
    /// <exception cref="InvalidDataException">The document is malformed.</exception>
    public static byte[] ToBytes(ReadOnlySpan<byte> json)
    {
        StringsDto dto = JsonSerializer.Deserialize(json, TransformJsonContext.Readable.StringsDto)
                         ?? throw new InvalidDataException("strings.json is empty");
        List<UiStringDto> strings = dto.Strings ?? throw new InvalidDataException("strings.json has no \"strings\"");
        return StringsBinCodec.ToBytes(new StringsBinFile(
            [.. strings.Select(s => s.Text ?? string.Empty)], dto.PadNuls, 0, 0));
    }

    private static List<string>? Callers(int index)
    {
        List<string> sites = StringsBinCallerCatalog.Lookup(index)
            .Select(c => $"{c.EnclosingFunctionLabel} (image@0x{c.CallerImageOffset:X5}, {c.AxDescription})")
            .ToList();
        return sites.Count == 0 ? null : sites;
    }
}
