using System.Text.RegularExpressions;

namespace CYAC.Port.Core.Schema;

/// <summary>
/// The best-effort parse of one struct-field *descriptor* string from the state schema
/// (KNOWN_FIELDS</c> → <c>Schema/state_schema.json</c>).
/// </summary>
/// <remarks>
/// <para>
/// Descriptors are curated RE evidence, not a formal type language: most are
/// <c>&lt;name&gt;_&lt;width&gt;</c> (<c>current_lo_i16</c>), some carry an inline array extent
/// (<c>date_str[9]</c>, <c>mission_unlock[50]</c>, <c>init_params_u8x4</c>), and some are bare notes
/// with no width at all (<c>flag_filter_word</c>, <c>field_tbd_23B</c>).  The exporter emits them RAW
/// on purpose (docstring: "the C# side parses the suffix conventions best-effort and must treat the
/// raw string as the authority of record").
/// </para>
/// <para>
/// Therefore <see cref="Parse"/> <b>never throws</b> and <see cref="Raw"/> always round-trips the
/// input byte-for-byte.  A <see langword="null"/> <see cref="WidthHint"/> means "the descriptor does
/// not encode a width" — never "the field is zero bytes".
/// </para>
/// </remarks>
/// <param name="Raw">The descriptor exactly as it appears in the schema (authority of record).</param>
/// <param name="Name">The field name: the descriptor minus its width/array suffixes, or — for prose
/// descriptors — the first identifier-like token found in it.</param>
/// <param name="WidthHint">The element type parsed from a trailing <c>_u8</c>/<c>_i16</c>/… suffix,
/// or <see langword="null"/> when the descriptor does not encode one.</param>
/// <param name="ArrayLength">The element count parsed from a trailing <c>[N]</c> or <c>xN</c> suffix,
/// or <see langword="null"/> for a scalar / unknown extent.</param>
public readonly partial record struct FieldDescriptor(
    string Raw,
    string Name,
    TypeInfo? WidthHint,
    int? ArrayLength)
{
    /// <summary>
    /// Footprint in bytes when the descriptor encodes an element width, else <see langword="null"/>.
    /// A descriptor with an array extent but no width (e.g. <c>date_str[9]</c>) yields
    /// <see langword="null"/> — the element width is not stated and this parser does not guess.
    /// </summary>
    public int? TotalBytes => WidthHint is { } hint ? hint.ElementWidth * (ArrayLength ?? 1) : null;

    /// <summary>True when the descriptor carried an explicit element count.</summary>
    public bool IsArray => ArrayLength is not null;

    /// <summary>Parses a descriptor. Never throws; unknown shapes degrade to name-only.</summary>
    /// <param name="raw">The raw descriptor string from the schema.</param>
    public static FieldDescriptor Parse(string? raw)
    {
        string text = raw ?? string.Empty;
        string trimmed = text.Trim();

        Match m = StructuredRegex().Match(trimmed);
        if (m.Success)
        {
            TypeInfo? hint = null;
            if (m.Groups["t"].Success && SchemaTypes.TryParse(m.Groups["t"].Value, out TypeInfo info))
            {
                hint = info;
            }

            int? length = null;
            if (m.Groups["xn"].Success && SchemaTypes.TryParseCount(m.Groups["xn"].Value, out int inlineCount))
            {
                length = inlineCount;
            }

            if (m.Groups["n"].Success && SchemaTypes.TryParseCount(m.Groups["n"].Value, out int bracketCount))
            {
                length = bracketCount;
            }

            return new FieldDescriptor(text, m.Groups["name"].Value, hint, length);
        }

        // Prose descriptor (spaces, citations, …): keep the raw string and take the first
        // identifier-like token as the name; claim no width.
        Match token = TokenRegex().Match(trimmed);
        return new FieldDescriptor(text, token.Success ? token.Value : trimmed, WidthHint: null, ArrayLength: null);
    }

    /// <summary>
    /// One whole descriptor of the regular shape: an identifier, optionally followed by a vocabulary
    /// width suffix (<c>_i16</c>, optionally with an inline count <c>_u8x4</c>), optionally followed
    /// by an array extent (<c>[9]</c>, <c>[0x40]</c>).  The name group is lazy so the suffix wins.
    /// </summary>
    [GeneratedRegex(
        @"^(?<name>[A-Za-z_][A-Za-z0-9_]*?)" +
        @"(?:_(?<t>u8|i8|bool8|flag8|u16|i16|u32|i32|nearptr|dos_handle|farptr)(?:x(?<xn>[0-9]+))?)?" +
        @"(?:\[(?<n>0[xX][0-9A-Fa-f]+|[0-9]+)\])?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex StructuredRegex();

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();
}
