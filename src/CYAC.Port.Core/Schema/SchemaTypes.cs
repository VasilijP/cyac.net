using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CYAC.Port.Core.Schema;

/// <summary>
/// One element kind from the original state-schema type vocabulary.
/// </summary>
/// <remarks>
/// Source of truth: the vocabulary header of (which mirrors KNOWN_GLOBAL_TYPES</c>):
/// <c>u8/i8/bool8/flag8</c> = 1 B, <c>u16/i16/nearptr/dos_handle</c> = 2 B, <c>u32/i32/farptr</c>
/// = 4 B (<c>farptr</c> is an offset+segment pair, not a linear pointer).  These are *original*
/// widths — the port keeps them deliberately (<c>src/CYAC.Port.Core/README.md</c> § "Integer
/// semantics are deliberate").
/// </remarks>
public enum SchemaTypeKind
{
    /// <summary>Unsigned 8-bit byte (<c>u8</c>).</summary>
    U8,

    /// <summary>Signed 8-bit byte (<c>i8</c>).</summary>
    I8,

    /// <summary>Boolean stored in one byte (<c>bool8</c>).</summary>
    Bool8,

    /// <summary>Bit-flag byte (<c>flag8</c>).</summary>
    Flag8,

    /// <summary>Unsigned 16-bit word (<c>u16</c>).</summary>
    U16,

    /// <summary>Signed 16-bit word (<c>i16</c>).</summary>
    I16,

    /// <summary>DGROUP-relative near pointer, 16-bit (<c>nearptr</c>).</summary>
    NearPtr,

    /// <summary>DOS file handle, 16-bit (<c>dos_handle</c>).</summary>
    DosHandle,

    /// <summary>Unsigned 32-bit doubleword (<c>u32</c>).</summary>
    U32,

    /// <summary>Signed 32-bit doubleword (<c>i32</c>).</summary>
    I32,

    /// <summary>Far pointer stored as an offset+segment word pair, 4 B (<c>farptr</c>).</summary>
    FarPtr,
}

/// <summary>
/// The parsed form of one state-schema type spelling (e.g. <c>u16</c>, <c>u8[0x168]</c>, <c>u16[N]</c>).
/// </summary>
/// <param name="Kind">The element kind.</param>
/// <param name="ElementWidth">Width of a single element in bytes (1, 2 or 4).</param>
/// <param name="Count">
/// Element count; <see langword="null"/> for the unsized <c>u16[N]</c> placeholder that the scanner
/// uses when an array's extent is known to exist but not yet measured.
/// </param>
/// <param name="IsSigned"><see langword="true"/> for <c>i8</c>/<c>i16</c>/<c>i32</c>.</param>
/// <param name="IsArray"><see langword="true"/> when the spelling carried a <c>[...]</c> suffix.</param>
public readonly record struct TypeInfo(
    SchemaTypeKind Kind,
    int ElementWidth,
    int? Count,
    bool IsSigned,
    bool IsArray)
{
    /// <summary>True for the unsized placeholder (<c>u16[N]</c>) — extent unknown, not zero.</summary>
    public bool IsUnsized => Count is null;

    /// <summary>Total footprint in bytes, or <see langword="null"/> when the extent is unknown.</summary>
    public int? TotalBytes => Count is int c ? ElementWidth * c : null;
}

/// <remarks>
/// The parser accepts a superset of what the exporter itself parses: the exporter's array regex only
/// admits <c>u8|u16|i16|u32|i32</c> element types, this one admits every scalar in the vocabulary.
/// That is deliberate — a future scanner merge that writes <c>nearptr[8]</c> should not become an
/// unparseable type here.  <c>u16[N]</c> (literal capital N) is the scanner's *unsized* placeholder.
/// </remarks>
public static partial class SchemaTypes
{
    /// <summary>Parses a type spelling; returns <see langword="false"/> for anything outside the vocabulary.</summary>
    /// <param name="type">A type spelling such as <c>i16</c> or <c>u8[0x168]</c>.</param>
    /// <param name="info">The parsed type on success.</param>
    public static bool TryParse([NotNullWhen(true)] string? type, out TypeInfo info)
    {
        info = default;
        if (string.IsNullOrWhiteSpace(type))
        {
            return false;
        }

        string text = type.Trim();
        if (TryParseScalar(text, out SchemaTypeKind kind, out int width, out bool signed))
        {
            info = new TypeInfo(kind, width, Count: 1, signed, IsArray: false);
            return true;
        }

        Match m = ArrayRegex().Match(text);
        if (!m.Success || !TryParseScalar(m.Groups["elem"].Value, out kind, out width, out signed))
        {
            return false;
        }

        string countText = m.Groups["n"].Value;
        int? count;
        if (countText == "N")
        {
            count = null;                       // unsized placeholder — extent not yet measured
        }
        else if (TryParseCount(countText, out int parsed))
        {
            count = parsed;
        }
        else
        {
            return false;
        }

        info = new TypeInfo(kind, width, count, signed, IsArray: true);
        return true;
    }

    /// <summary>Parses a type spelling, throwing when it is outside the vocabulary.</summary>
    /// <exception cref="FormatException">The spelling is not part of the schema type vocabulary.</exception>
    public static TypeInfo Parse(string type) =>
        TryParse(type, out TypeInfo info)
            ? info
            : throw new FormatException(
                $"'{type}' is not part of the state-schema type vocabulary.");

    /// <summary>True for the scanner's unsized array placeholder (<c>u16[N]</c>).</summary>
    public static bool IsUnsizedPlaceholder(string? type) =>
        TryParse(type, out TypeInfo info) && info.IsUnsized;

    /// <summary>Parses a decimal (<c>50</c>) or hex (<c>0x168</c>) element count.</summary>
    internal static bool TryParseCount(string text, out int value)
    {
        if (text.StartsWith("0x", StringComparison.Ordinal) || text.StartsWith("0X", StringComparison.Ordinal))
        {
            return int.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Maps one scalar token of the vocabulary to (kind, width, signedness).</summary>
    internal static bool TryParseScalar(string token, out SchemaTypeKind kind, out int width, out bool isSigned)
    {
        (kind, width, isSigned) = token switch
        {
            "u8" => (SchemaTypeKind.U8, 1, false),
            "i8" => (SchemaTypeKind.I8, 1, true),
            "bool8" => (SchemaTypeKind.Bool8, 1, false),
            "flag8" => (SchemaTypeKind.Flag8, 1, false),
            "u16" => (SchemaTypeKind.U16, 2, false),
            "i16" => (SchemaTypeKind.I16, 2, true),
            "nearptr" => (SchemaTypeKind.NearPtr, 2, false),
            "dos_handle" => (SchemaTypeKind.DosHandle, 2, false),
            "u32" => (SchemaTypeKind.U32, 4, false),
            "i32" => (SchemaTypeKind.I32, 4, true),
            "farptr" => (SchemaTypeKind.FarPtr, 4, false),
            _ => (default, 0, false),
        };
        return width != 0;
    }

    [GeneratedRegex(@"^(?<elem>[a-z0-9_]+)\[(?<n>0[xX][0-9A-Fa-f]+|[0-9]+|N)\]$", RegexOptions.CultureInvariant)]
    private static partial Regex ArrayRegex();
}
