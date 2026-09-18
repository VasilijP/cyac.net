using System.Globalization;

namespace CYAC.Port.Core.Schema;

/// <summary>How seriously a <see cref="SchemaDiagnostic"/> should be taken.</summary>
public enum SchemaSeverity
{
    /// <summary>A fact worth surfacing that is not a defect (e.g. an unsized-array placeholder).</summary>
    Info,

    /// <summary>Suspicious but legitimate in this project (e.g. a deliberate field overlay).</summary>
    Warning,

    /// <summary>A real inconsistency: the schema contradicts itself and consumers may mis-read it.</summary>
    Error,
}

/// <summary>One finding produced by <see cref="SchemaValidator"/>.</summary>
/// <param name="Severity">How seriously to take it.</param>
/// <param name="Code">Stable machine-readable code, e.g. <c>struct-field-overlap</c>.</param>
/// <param name="Target">What it is about — a global name, a struct name, or <c>meta</c>.</param>
/// <param name="Message">Human-readable explanation.</param>
public sealed record SchemaDiagnostic(SchemaSeverity Severity, string Code, string Target, string Message)
{
    /// <summary>Report-friendly one-liner.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Severity,-7} {Code,-26} {Target}: {Message}");
}

/// <summary>
/// Self-consistency checks over a <see cref="StateSchema"/> — the port's drift detector for exports.
/// </summary>
/// <remarks>
/// <para>
/// The severities encode project doctrine, not generic JSON hygiene:
/// </para>
/// <list type="bullet">
/// <item><description><b>Error</b> — the schema contradicts itself (duplicate offsets/names, meta
/// counts that disagree with the payload, a type spelling outside the vocabulary, fields out of
/// order or doubly-defined).</description></item>
/// <item><description><b>Warning</b> — overlapping struct fields.  Overlays are <b>legitimate</b>
/// here: <c>s_ctrl_state_block</c> is overlaid three times inside <c>s_aircraft_master</c> and the
/// master's <c>i32</c> velocity/position words are deliberately aliased at +1 by the <c>u16</c> high
/// word, so this can never be an Error.  Also: the
/// exporter's own width/count arithmetic disagreeing with <see cref="SchemaTypes"/>.</description></item>
/// <item><description><b>Info</b> — the unsized <c>u16[N]</c> placeholder, and two structs that
/// describe the same shape (a duplicate-description smell worth a human look).</description></item>
/// </list>
/// </remarks>
public static class SchemaValidator
{
    /// <summary>Runs every check and returns the findings in a stable order.</summary>
    /// <param name="schema">The schema to check.</param>
    public static IReadOnlyList<SchemaDiagnostic> Validate(StateSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        List<SchemaDiagnostic> diagnostics = new List<SchemaDiagnostic>();
        ValidateGlobals(schema, diagnostics);
        ValidateMetaCounts(schema, diagnostics);
        ValidateStructs(schema, diagnostics);
        return diagnostics;
    }

    /// <summary>True when any diagnostic is an <see cref="SchemaSeverity.Error"/>.</summary>
    public static bool HasErrors(IEnumerable<SchemaDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        return diagnostics.Any(static d => d.Severity == SchemaSeverity.Error);
    }

    private static void ValidateGlobals(StateSchema schema, List<SchemaDiagnostic> diagnostics)
    {
        Dictionary<int, string> seenOffsets = new Dictionary<int, string>();
        HashSet<string> seenNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (GlobalEntry g in schema.Globals)
        {
            if (!seenOffsets.TryAdd(g.Offset, g.Name))
            {
                diagnostics.Add(new SchemaDiagnostic(
                    SchemaSeverity.Error, "global-offset-duplicate", g.Name,
                    $"offset {g.OffsetCitation} is also used by '{seenOffsets[g.Offset]}'."));
            }

            if (!seenNames.Add(g.Name))
            {
                diagnostics.Add(new SchemaDiagnostic(
                    SchemaSeverity.Error, "global-name-duplicate", g.Name,
                    $"name repeats at {g.OffsetCitation}."));
            }

            if (!g.IsTyped)
            {
                continue;
            }

            if (!SchemaTypes.TryParse(g.Type, out TypeInfo info))
            {
                diagnostics.Add(new SchemaDiagnostic(
                    SchemaSeverity.Error, "global-type-unparsed", g.Name,
                    $"type '{g.Type}' at {g.OffsetCitation} is outside the schema type vocabulary."));
                continue;
            }

            if (info.IsUnsized)
            {
                diagnostics.Add(new SchemaDiagnostic(
                    SchemaSeverity.Info, "global-type-unsized", g.Name,
                    $"type '{g.Type}' at {g.OffsetCitation} is the scanner's unsized-array placeholder; " +
                    "the extent is not yet measured."));
                continue;
            }

            if (g.Width != info.ElementWidth || g.Count != info.Count)
            {
                diagnostics.Add(new SchemaDiagnostic(
                    SchemaSeverity.Warning, "global-width-drift", g.Name,
                    $"exporter says width={Show(g.Width)} count={Show(g.Count)} for '{g.Type}', " +
                    $"this parser says width={info.ElementWidth} count={Show(info.Count)}."));
            }
        }
    }

    private static void ValidateMetaCounts(StateSchema schema, List<SchemaDiagnostic> diagnostics)
    {
        SchemaCounts counts = schema.Meta.Counts;
        Compare("globals", counts.Globals, schema.Globals.Count);
        Compare("typed_globals", counts.TypedGlobals, schema.Globals.Count(static g => g.IsTyped));
        Compare("structs", counts.Structs, schema.Structs.Count);
        Compare("struct_fields", counts.StructFields, schema.Structs.Values.Sum(static s => s.Fields.Count));

        void Compare(string what, int declared, int actual)
        {
            if (declared != actual)
            {
                diagnostics.Add(new SchemaDiagnostic(
                    SchemaSeverity.Error, "meta-count-mismatch", "meta",
                    $"meta.counts.{what} = {declared} but the payload holds {actual} — " +
                    "re-run `python3`."));
            }
        }
    }

    private static void ValidateStructs(StateSchema schema, List<SchemaDiagnostic> diagnostics)
    {
        KeyValuePair<string, StructDef>[] ordered = schema.Structs
            .OrderBy(static kv => kv.Key, StringComparer.Ordinal)
            .ToArray();

        foreach ((string name, StructDef def) in ordered)
        {
            HashSet<int> seenOffsets = new HashSet<int>();
            StructField? previous = null;

            foreach (StructField field in def.Fields)
            {
                if (previous is not null && field.Offset < previous.Offset)
                {
                    diagnostics.Add(new SchemaDiagnostic(
                        SchemaSeverity.Error, "struct-fields-unsorted", name,
                        $"field '{field.Descriptor}' at +0x{field.Offset:X} follows " +
                        $"'{previous.Descriptor}' at +0x{previous.Offset:X}."));
                }

                if (!seenOffsets.Add(field.Offset))
                {
                    diagnostics.Add(new SchemaDiagnostic(
                        SchemaSeverity.Error, "struct-field-offset-duplicate", name,
                        $"offset +0x{field.Offset:X} is defined more than once ('{field.Descriptor}')."));
                }

                if (previous?.TotalBytes is int width && previous.Offset + width > field.Offset)
                {
                    diagnostics.Add(new SchemaDiagnostic(
                        SchemaSeverity.Warning, "struct-field-overlap", name,
                        $"'{previous.Descriptor}' at +0x{previous.Offset:X} spans {width} B and reaches " +
                        $"into '{field.Descriptor}' at +0x{field.Offset:X}."));
                }

                previous = field;
            }
        }

        ReportDuplicateDescriptions(ordered, diagnostics);
    }

    /// <summary>
    /// Flags two structs that describe the <b>same original record twice</b>: identical field offsets
    /// AND at least half the descriptors identical.  Matching offsets alone prove nothing (any two
    /// all-word records of the same length collide), so the descriptor agreement is what makes this a
    /// signal instead of noise.
    /// </summary>
    private static void ReportDuplicateDescriptions(
        KeyValuePair<string, StructDef>[] ordered,
        List<SchemaDiagnostic> diagnostics)
    {
        for (int i = 0; i < ordered.Length; i++)
        {
            for (int j = i + 1; j < ordered.Length; j++)
            {
                IReadOnlyList<StructField> a = ordered[i].Value.Fields;
                IReadOnlyList<StructField> b = ordered[j].Value.Fields;
                if (a.Count < 2 || a.Count != b.Count)
                {
                    continue;
                }

                int agree = 0;
                for (int k = 0; k < a.Count; k++)
                {
                    if (a[k].Offset != b[k].Offset)
                    {
                        agree = -1;
                        break;
                    }

                    if (string.Equals(a[k].Descriptor, b[k].Descriptor, StringComparison.Ordinal))
                    {
                        agree++;
                    }
                }

                if (agree * 2 >= a.Count)
                {
                    diagnostics.Add(new SchemaDiagnostic(
                        SchemaSeverity.Info, "struct-duplicate-description", ordered[j].Key,
                        $"describes the same record as '{ordered[i].Key}' ({agree}/{a.Count} descriptors " +
                        "identical, offsets identical) — the two disagree on the remaining field names."));
                }
            }
        }
    }

    private static string Show(int? value) =>
        value is int v ? v.ToString(CultureInfo.InvariantCulture) : "null";
}
