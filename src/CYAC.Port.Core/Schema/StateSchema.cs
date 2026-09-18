using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace CYAC.Port.Core.Schema;

/// <summary>
/// The project's curated state model of the original game — every named global and every
/// <c>KNOWN_FIELDS</c> struct — as an immutable, queryable object.
/// </summary>
/// <remarks>
/// <para>
/// Source of truth: <c>src/CYAC.Port.Core/Schema/state_schema.json</c>, generated from by <c>python3
/// and embedded in this assembly as <see cref="EmbeddedResourceName"/>.  Never hand-edit the JSON;
/// regenerate it after a scanner merge.
/// </para>
/// <para>
/// This type carries the original data model as <b>metadata</b>.  Port domain types link back to it
/// through <see cref="OriginalStructAttribute"/> / <see cref="OriginalFieldAttribute"/> /
/// <see cref="OriginalGlobalAttribute"/> so comparators and the effect-verification rig can map port
/// state ↔ original memory, while the port's own model stays clean
/// (<c>src/CYAC.Port.Core/README.md</c> § "The schema is the ground truth of the data model").
/// </para>
/// <para>
/// Loading is deliberately tolerant: duplicate names or offsets do not throw, because reporting them
/// is <see cref="SchemaValidator"/>'s job.  On a duplicate key the first entry wins the lookup and the
/// full list still contains every entry.
/// </para>
/// </remarks>
public sealed class StateSchema
{
    /// <summary>Manifest-resource name of the embedded schema (see <c>CYAC.Port.Core.csproj</c>).</summary>
    public const string EmbeddedResourceName = "CYAC.Port.Core.Schema.state_schema.json";

    private static readonly Lazy<StateSchema> LazyEmbedded =
        new(LoadEmbedded, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Dictionary<string, GlobalEntry> _globalsByName;
    private readonly Dictionary<int, GlobalEntry> _globalsByOffset;
    private readonly Dictionary<string, StructDef> _structsByName;

    /// <summary>Creates a schema from already-materialised parts (used by the loader and by tests).</summary>
    /// <param name="meta">The provenance/counts header.</param>
    /// <param name="globals">The globals, in export order (ascending offset).</param>
    /// <param name="structs">The struct definitions.</param>
    public StateSchema(SchemaMeta meta, IEnumerable<GlobalEntry> globals, IEnumerable<StructDef> structs)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(globals);
        ArgumentNullException.ThrowIfNull(structs);

        Meta = meta;
        GlobalEntry[] globalList = globals.ToArray();
        Globals = globalList;

        _globalsByName = new Dictionary<string, GlobalEntry>(globalList.Length, StringComparer.Ordinal);
        _globalsByOffset = new Dictionary<int, GlobalEntry>(globalList.Length);
        foreach (GlobalEntry entry in globalList)
        {
            _globalsByName.TryAdd(entry.Name, entry);
            _globalsByOffset.TryAdd(entry.Offset, entry);
        }

        _structsByName = new Dictionary<string, StructDef>(StringComparer.Ordinal);
        foreach (StructDef def in structs)
        {
            _structsByName.TryAdd(def.Name, def);
        }

        Structs = _structsByName;
    }

    /// <summary>Provenance and the counts recorded when the schema was generated.</summary>
    public SchemaMeta Meta { get; }

    /// <summary>All named globals, in export order (ascending DGROUP offset).</summary>
    public IReadOnlyList<GlobalEntry> Globals { get; }

    /// <summary>All structs, keyed by scanner struct name.</summary>
    public IReadOnlyDictionary<string, StructDef> Structs { get; }

    /// <summary>The embedded schema, parsed once per process.</summary>
    public static StateSchema Embedded => LazyEmbedded.Value;

    /// <summary>Parses a fresh copy of the schema embedded in this assembly.</summary>
    /// <exception cref="InvalidOperationException">The embedded resource is missing.</exception>
    /// <exception cref="InvalidDataException">The resource is not a well-formed state schema.</exception>
    public static StateSchema LoadEmbedded()
    {
        using Stream stream = typeof(StateSchema).Assembly.GetManifestResourceStream(EmbeddedResourceName)
                              ?? throw new InvalidOperationException(
                                  $"Embedded resource '{EmbeddedResourceName}' not found; check the EmbeddedResource " +
                                  "LogicalName in CYAC.Port.Core.csproj.");
        return Load(stream);
    }

    /// <summary>Parses a state schema from a UTF-8 JSON stream.</summary>
    /// <param name="stream">A stream over a <c>state_schema.json</c> document.</param>
    /// <exception cref="InvalidDataException">The document is null, empty or malformed.</exception>
    public static StateSchema Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        SchemaDocumentDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(stream, SchemaJsonContext.Default.SchemaDocumentDto);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("state_schema.json is not valid JSON.", ex);
        }

        if (dto is null)
        {
            throw new InvalidDataException("state_schema.json deserialised to null.");
        }

        SchemaCountsDto counts = dto.Meta?.Counts
                                 ?? throw new InvalidDataException("state_schema.json is missing its meta.counts block.");

        SchemaMeta meta = new SchemaMeta(
            dto.Meta?.Source ?? string.Empty,
            dto.Meta?.GeneratedBy ?? string.Empty,
            new SchemaCounts(counts.Globals, counts.TypedGlobals, counts.Structs, counts.StructFields));

        IEnumerable<GlobalEntry> globals = (dto.Globals ?? []).Select(static g => new GlobalEntry(
            g.Offset,
            g.Name ?? throw new InvalidDataException($"global at offset 0x{g.Offset:X4} has no name."),
            g.Type,
            g.Width,
            g.Count));

        IEnumerable<StructDef> structs = (dto.Structs ?? []).Select(static kv => new StructDef(
            kv.Key,
            kv.Value.Select(f => new StructField(
                f.Offset,
                f.Descriptor ?? throw new InvalidDataException(
                    $"struct '{kv.Key}' field at +0x{f.Offset:X} has no descriptor."))).ToArray()));

        return new StateSchema(meta, globals, structs);
    }

    /// <summary>Looks a global up by its scanner name (ordinal).</summary>
    public bool TryGetGlobal(string name, [NotNullWhen(true)] out GlobalEntry? entry)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _globalsByName.TryGetValue(name, out entry);
    }

    /// <summary>Looks a global up by its scanner name, or <see langword="null"/>.</summary>
    public GlobalEntry? FindGlobal(string name) => TryGetGlobal(name, out GlobalEntry? entry) ? entry : null;

    /// <summary>Looks a global up by its DGROUP offset.</summary>
    public bool TryGetGlobalAt(int offset, [NotNullWhen(true)] out GlobalEntry? entry) =>
        _globalsByOffset.TryGetValue(offset, out entry);

    /// <summary>Looks a global up by its DGROUP offset, or <see langword="null"/>.</summary>
    public GlobalEntry? FindGlobalAt(int offset) => TryGetGlobalAt(offset, out GlobalEntry? entry) ? entry : null;

    /// <summary>Looks a struct up by its scanner name (ordinal).</summary>
    public bool TryGetStruct(string name, [NotNullWhen(true)] out StructDef? def)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _structsByName.TryGetValue(name, out def);
    }

    /// <summary>Looks a struct up by its scanner name, or <see langword="null"/>.</summary>
    public StructDef? FindStruct(string name) => TryGetStruct(name, out StructDef? def) ? def : null;

    /// <summary>Gets a global by name; throws when it is not in the schema.</summary>
    /// <exception cref="KeyNotFoundException">No global with that name exists.</exception>
    public GlobalEntry Global(string name) =>
        FindGlobal(name) ?? throw new KeyNotFoundException($"no global named '{name}' in the state schema.");

    /// <summary>Gets a struct by name; throws when it is not in the schema.</summary>
    /// <exception cref="KeyNotFoundException">No struct with that name exists.</exception>
    public StructDef Struct(string name) =>
        FindStruct(name) ?? throw new KeyNotFoundException($"no struct named '{name}' in the state schema.");
}
