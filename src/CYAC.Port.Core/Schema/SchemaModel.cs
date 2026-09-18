using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace CYAC.Port.Core.Schema;

/// <summary>
/// The element counts recorded in the schema's <c>meta.counts</c> block — the drift detector the
/// exporter designed in ("the port's schema-validation tests detect drift by comparing counts against
/// this file's meta block").
/// </summary>
/// <param name="Globals">Number of named original globals.</param>
/// <param name="TypedGlobals">Number of globals that also carry a declared type.</param>
/// <param name="Structs">Number of <c>KNOWN_FIELDS</c> structs.</param>
/// <param name="StructFields">Total number of struct fields across all structs.</param>
public sealed record SchemaCounts(int Globals, int TypedGlobals, int Structs, int StructFields);

/// <summary>Provenance header of the exported state schema (<c>meta</c> block).</summary>
/// <param name="Source">Where the data came from (tables).</param>
/// <param name="GeneratedBy">The generator that must be re-run after a scanner merge.</param>
/// <param name="Counts">The counts recorded at generation time.</param>
public sealed record SchemaMeta(string Source, string GeneratedBy, SchemaCounts Counts);

/// <summary>
/// One named global of the original game, at a DGROUP-relative offset.
/// </summary>
/// <remarks>
/// Source of truth: KNOWN_GLOBALS</c> / <c>KNOWN_GLOBAL_TYPES</c>, exported.  <see cref="Offset"/> is the
/// DGROUP offset the project cites as <c>[0xNNNN]</c>; it is <b>metadata for the port</b>, never an address
/// the port dereferences (<c>src/CYAC.Port.Core/README.md</c>: "no DGROUP offsets in game logic").
/// </remarks>
/// <param name="Offset">DGROUP-relative offset of the global.</param>
/// <param name="Name">The scanner's identifier for the global (e.g. <c>g_master_frame_counter</c>).</param>
/// <param name="Type">The declared type spelling, or <see langword="null"/> when untyped.</param>
/// <param name="Width">Element width in bytes as computed by the exporter, or <see langword="null"/>.</param>
/// <param name="Count">Element count as computed by the exporter, or <see langword="null"/>.</param>
public sealed record GlobalEntry(int Offset, string Name, string? Type, int? Width, int? Count)
{
    /// <summary>True when the scanner declares a type for this global.</summary>
    [MemberNotNullWhen(true, nameof(Type))]
    public bool IsTyped => !string.IsNullOrEmpty(Type);

    /// <summary>Total footprint in bytes per the exporter's own arithmetic, when known.</summary>
    public int? TotalBytes => Width is int w && Count is int c ? w * c : null;

    /// <summary>The offset in the project's citation form, e.g. <c>[0xF0C8]</c>.</summary>
    public string OffsetCitation => string.Create(
        CultureInfo.InvariantCulture, $"[0x{Offset:X4}]");

    /// <summary>Parses <see cref="Type"/> with <see cref="SchemaTypes"/>; <see langword="null"/> when untyped or unparseable.</summary>
    public TypeInfo? ParsedType => SchemaTypes.TryParse(Type, out TypeInfo info) ? info : null;
}

/// <summary>
/// One field of an original struct: its offset plus the scanner's raw descriptor string and the
/// best-effort parse of that string.
/// </summary>
/// <remarks>
/// Source of truth: KNOWN_FIELDS["s_..."]</c>.  <see cref="Descriptor"/> is the authority of
/// record — <see cref="Name"/>/<see cref="WidthHint"/> are conveniences derived by
/// <see cref="FieldDescriptor"/> and may be absent for prose descriptors.
/// </remarks>
public sealed record StructField
{
    /// <summary>Creates a field from its offset and raw descriptor.</summary>
    /// <param name="offset">Byte offset of the field inside the struct.</param>
    /// <param name="descriptor">The raw descriptor string, kept verbatim.</param>
    public StructField(int offset, string descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Offset = offset;
        Descriptor = descriptor;
        Parsed = FieldDescriptor.Parse(descriptor);
    }

    /// <summary>Byte offset of the field inside its struct.</summary>
    public int Offset { get; }

    /// <summary>The raw descriptor string, exactly as exported.</summary>
    public string Descriptor { get; }

    /// <summary>The best-effort parse of <see cref="Descriptor"/>.</summary>
    public FieldDescriptor Parsed { get; }

    /// <summary>Field name derived from the descriptor.</summary>
    public string Name => Parsed.Name;

    /// <summary>Element type derived from the descriptor's width suffix, if any.</summary>
    public TypeInfo? WidthHint => Parsed.WidthHint;

    /// <summary>Element count derived from the descriptor's array suffix, if any.</summary>
    public int? ArrayLength => Parsed.ArrayLength;

    /// <summary>Footprint in bytes when the descriptor encodes a width, else <see langword="null"/>.</summary>
    public int? TotalBytes => Parsed.TotalBytes;
}

/// <summary>
/// One original struct: an ordered field map keyed by byte offset.
/// </summary>
/// <remarks>
/// Source of truth: KNOWN_FIELDS</c>.  Fields are kept in the exported order (the exporter sorts by
/// offset); <see cref="SchemaValidator"/> checks that ordering rather than this type silently
/// re-sorting it.  Overlapping fields are <b>legitimate</b> here — several structs are deliberate
/// overlays.
/// </remarks>
/// <param name="Name">The scanner's struct name, e.g. <c>s_ctrl_state_block</c>.</param>
/// <param name="Fields">The struct's fields, in exported (offset) order.</param>
public sealed record StructDef(string Name, IReadOnlyList<StructField> Fields)
{
    /// <summary>Finds a field by exact offset.</summary>
    public bool TryGetField(int offset, [NotNullWhen(true)] out StructField? field)
    {
        foreach (StructField candidate in Fields)
        {
            if (candidate.Offset == offset)
            {
                field = candidate;
                return true;
            }
        }

        field = null;
        return false;
    }

    /// <summary>Finds a field by exact offset, or <see langword="null"/>.</summary>
    public StructField? FindField(int offset) => TryGetField(offset, out StructField? field) ? field : null;

    /// <summary>Finds the first field whose parsed <see cref="StructField.Name"/> matches (ordinal).</summary>
    public StructField? FindField(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (StructField candidate in Fields)
        {
            if (string.Equals(candidate.Name, name, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Known extent = last field offset + that field's footprint, or <see langword="null"/> when the
    /// last descriptor does not encode a width (the struct's size is then a documentation fact, not a
    /// schema fact).
    /// </summary>
    public int? KnownExtent =>
        Fields.Count == 0 ? 0
        : Fields[^1].TotalBytes is int last ? Fields[^1].Offset + last
        : null;
}
