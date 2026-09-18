using System.Text.Json.Serialization;

namespace CYAC.Port.Core.Data;

/// <summary>
/// <c>exe/tables/advisor.json</c>: where each action code of the in-flight advisor points its two text
/// lines, as the transform reads them out of the message dispatcher's handlers.
/// </summary>
/// <remarks>
/// <para>
/// <c>ai_advisor_message_dispatch @image@0x0EFF1</c> selects a handler through a CS-relative jump
/// table, and each handler loads far pointers into the line-1 pair <c>[0xBCBA]/[0xBCBC]</c> and the
/// line-2 pair <c>[0xBCBE]/[0xBCC0]</c>.  The document records every such pointer as its offset
/// inside the advisory string table and as the image offset it resolves to, together with the
/// instructions that load its two halves.
/// </para>
/// <para>
/// It carries no text.  <c>exe/strings.json</c> does, and a reader joins the two by image offset.
/// </para>
/// </remarks>
public sealed class AdvisorTableDto
{
    /// <summary>Where the document lives in the data tree.</summary>
    public const string DataPath = "exe/tables/advisor.json";

    /// <summary>The document's schema tag.</summary>
    public const string FormatTag = "cyac.table.advisor/1";

    /// <summary>The document's schema tag, <see cref="FormatTag"/>.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>What the document is, in prose.</summary>
    [JsonPropertyName("about")]
    public string? About { get; init; }

    /// <summary>The message dispatcher's entry, as an image offset.</summary>
    [JsonPropertyName("dispatcher")]
    public string? Dispatcher { get; init; }

    /// <summary>The dispatcher's range check and indirect jump, as an image offset.</summary>
    [JsonPropertyName("switch")]
    public string? Switch { get; init; }

    /// <summary>The jump table the switch indexes, as an image offset.</summary>
    [JsonPropertyName("jumpTable")]
    public string? JumpTable { get; init; }

    /// <summary>The common tail every handler that shows a message ends in, as an image offset.</summary>
    [JsonPropertyName("tail")]
    public string? Tail { get; init; }

    /// <summary>
    /// The image offset of the string table: the base of the segment every handler pushes.
    /// </summary>
    [JsonPropertyName("tableImageBase")]
    public string? TableImageBase { get; init; }

    /// <summary>One entry per action code, in code order.</summary>
    [JsonPropertyName("codes")]
    public List<AdvisorCodeDto>? Codes { get; init; }
}

/// <summary>What one action code's handler loads.</summary>
/// <remarks>
/// A handler either loads one first line (<see cref="Line1"/>) or chooses between several on
/// run-time state (<see cref="Variants"/>, in the order its branches appear).  Its second line is
/// either a pointer (<see cref="Line2"/>) or explicitly cleared (<see cref="Line2ClearedAt"/>).
/// </remarks>
public sealed class AdvisorCodeDto
{
    /// <summary>The action code, which is also the entry's index.</summary>
    [JsonPropertyName("code")]
    public int Code { get; init; }

    /// <summary>The handler the jump table sends the code to, as an image offset.</summary>
    [JsonPropertyName("handler")]
    public string? Handler { get; init; }

    /// <summary>The first line, when the handler loads exactly one.</summary>
    [JsonPropertyName("line1")]
    public AdvisorPointerDto? Line1 { get; init; }

    /// <summary>The first lines the handler chooses between, in branch order.</summary>
    [JsonPropertyName("variants")]
    public List<AdvisorPointerDto>? Variants { get; init; }

    /// <summary>The second line, when the handler loads one.</summary>
    [JsonPropertyName("line2")]
    public AdvisorPointerDto? Line2 { get; init; }

    /// <summary>
    /// Where the handler zeroes both halves of the line-2 pointer instead, as an image offset: the
    /// message has no second line.
    /// </summary>
    [JsonPropertyName("line2ClearedAt")]
    public string? Line2ClearedAt { get; init; }

    /// <summary>A string the handler also passes to a far call, when it does.</summary>
    [JsonPropertyName("banner")]
    public AdvisorPointerDto? Banner { get; init; }
}

/// <summary>One far pointer into the advisory string table.</summary>
public sealed class AdvisorPointerDto
{
    /// <summary>The pointer's offset half: where the string starts inside the table.</summary>
    [JsonPropertyName("offset")]
    public string? Offset { get; init; }

    /// <summary>The image offset the pointer resolves to: the table's base plus <see cref="Offset"/>.</summary>
    [JsonPropertyName("image")]
    public string? Image { get; init; }

    /// <summary>The instruction that loads the offset half, as an image offset.</summary>
    [JsonPropertyName("offsetAt")]
    public string? OffsetAt { get; init; }

    /// <summary>The instruction that loads the segment half, as an image offset.</summary>
    [JsonPropertyName("segmentAt")]
    public string? SegmentAt { get; init; }

    /// <summary>For a pushed pointer: the far call it is passed to, as an image offset.</summary>
    [JsonPropertyName("pushedTo")]
    public string? PushedTo { get; init; }
}

/// <summary>
/// The rules of <c>exe/tables/advisor.json</c>, checked when the document is read, so a damaged table
/// fails with a message that names the field instead of producing an empty advisor.
/// </summary>
public static class AdvisorTableRules
{
    /// <summary>The largest value a 16-bit pointer half can hold.</summary>
    private const int WordMax = 0xFFFF;

    /// <summary>Segments are 16 bytes apart, so a segment base is a multiple of this.</summary>
    private const int SegmentGranularity = 16;

    /// <summary>Checks a document and returns it unchanged.</summary>
    /// <param name="table">The document as read.</param>
    /// <param name="strings">
    /// The tree's string catalogue, to check that every pointer lands on the start of a string; null
    /// checks the document on its own.
    /// </param>
    /// <returns><paramref name="table"/>.</returns>
    /// <exception cref="InvalidDataException">A rule is broken; the message says which, and where.</exception>
    public static AdvisorTableDto Validate(AdvisorTableDto table, ExeStringCatalogDto? strings = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        HashSet<int>? starts = strings is null ? null : StringStarts(strings);
        if (!string.Equals(table.Format, AdvisorTableDto.FormatTag, StringComparison.Ordinal))
        {
            throw Malformed($"its format is \"{table.Format}\", expected \"{AdvisorTableDto.FormatTag}\"");
        }

        int tableBase = Hex(table.TableImageBase, "tableImageBase");
        if (tableBase % SegmentGranularity != 0)
        {
            throw Malformed(
                $"tableImageBase {table.TableImageBase} is not a segment base (a multiple of 0x{SegmentGranularity:X})");
        }

        List<AdvisorCodeDto>? codes = table.Codes;
        if (codes is null || codes.Count == 0)
        {
            throw Malformed("it lists no codes");
        }

        for (int i = 0; i < codes.Count; i++)
        {
            AdvisorCodeDto code = codes[i] ?? throw Malformed($"codes[{i}] is null");
            if (code.Code != i)
            {
                throw Malformed($"codes[{i}] is code {code.Code}; the codes must run 0, 1, 2, ... in order");
            }

            string where = $"code {i}";
            _ = Hex(code.Handler, $"{where} handler");
            if ((code.Line1 is null) == (code.Variants is null))
            {
                throw Malformed($"{where} must have exactly one of line1 and variants");
            }

            if (code.Line1 is { } line1)
            {
                Pointer(line1, tableBase, starts, $"{where} line1");
            }

            if (code.Variants is { } variants)
            {
                if (variants.Count < 2)
                {
                    throw Malformed($"{where} lists {variants.Count} variant(s); a choice needs at least two");
                }

                for (int v = 0; v < variants.Count; v++)
                {
                    Pointer(variants[v], tableBase, starts, $"{where} variants[{v}]");
                }
            }

            if ((code.Line2 is null) == (code.Line2ClearedAt is null))
            {
                throw Malformed($"{where} must have exactly one of line2 and line2ClearedAt");
            }

            if (code.Line2 is { } line2)
            {
                Pointer(line2, tableBase, starts, $"{where} line2");
            }
            else
            {
                _ = Hex(code.Line2ClearedAt, $"{where} line2ClearedAt");
            }

            if (code.Banner is { } banner)
            {
                Pointer(banner, tableBase, starts, $"{where} banner");
                _ = Hex(banner.PushedTo, $"{where} banner pushedTo");
            }
        }

        return table;
    }

    private static void Pointer(AdvisorPointerDto? pointer, int tableBase, HashSet<int>? starts, string where)
    {
        if (pointer is null)
        {
            throw Malformed($"{where} is null");
        }

        int offset = Hex(pointer.Offset, $"{where} offset");
        int image = Hex(pointer.Image, $"{where} image");
        _ = Hex(pointer.OffsetAt, $"{where} offsetAt");
        _ = Hex(pointer.SegmentAt, $"{where} segmentAt");
        if (offset > WordMax)
        {
            throw Malformed($"{where} offset {pointer.Offset} does not fit a 16-bit pointer half");
        }

        if (image != tableBase + offset)
        {
            throw Malformed(
                $"{where} image {pointer.Image} is not tableImageBase + offset "
                    + $"(0x{tableBase:X5} + {pointer.Offset} = 0x{tableBase + offset:X5})");
        }

        if (starts is not null && !starts.Contains(image))
        {
            throw Malformed($"{where} points at image@0x{image:X5}, where exe/strings.json has no string");
        }
    }

    private static HashSet<int> StringStarts(ExeStringCatalogDto strings)
    {
        HashSet<int> starts = new HashSet<int>();
        foreach (ExeStringZoneDto zone in strings.Zones ?? [])
        {
            foreach (ExeStringDto literal in zone.Strings ?? [])
            {
                if (literal.Image is { } at && literal.Text is not null)
                {
                    starts.Add(PortHex.Parse(at));
                }
            }
        }

        return starts;
    }

    private static int Hex(string? text, string field)
    {
        int value;
        try
        {
            value = PortHex.Parse(text);
        }
        catch (InvalidDataException ex)
        {
            throw Malformed($"{field}: {ex.Message}", ex);
        }

        return value >= 0 ? value : throw Malformed($"{field} is negative");
    }

    private static InvalidDataException Malformed(string problem, Exception? inner = null) =>
        new($"{AdvisorTableDto.DataPath} is malformed: {problem}", inner);
}
