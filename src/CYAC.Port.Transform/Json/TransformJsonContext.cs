using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CYAC.Port.Core.Data;
using CYAC.Port.Transform.Ealib;
using CYAC.Port.Transform.Families;
using CYAC.Port.Transform.Families.ExeTables;
using CYAC.Port.Transform.Manifest;

namespace CYAC.Port.Transform.Json;

// The one source-generated serialisation context for everything cyac-transform writes.  Same
// doctrine as CYAC.Port.Core/Schema/SchemaJson.cs: no reflection-based serialisation anywhere, so
// the tool stays trimming-friendly and every document's shape is visible in one place.
//
// WriteIndented: the data tree is meant to be read and hand-edited (transform-plan L5).
// WhenWritingNull: an absent optional field (a fidelity note, an unknown-byte tail) is omitted
// rather than emitted as null, which keeps the common case clean; the readers all treat a missing
// field as "none".

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ManifestDto))]
[JsonSerializable(typeof(ExeUnpackDto))]
[JsonSerializable(typeof(PaletteDto))]
[JsonSerializable(typeof(ArchiveDirectoryDto))]
[JsonSerializable(typeof(PicDto))]
[JsonSerializable(typeof(MaskDto))]
[JsonSerializable(typeof(RleDto))]
[JsonSerializable(typeof(FontDto))]
[JsonSerializable(typeof(ScenarioCatalogDto))]
[JsonSerializable(typeof(PiDto))]
[JsonSerializable(typeof(StringsDto))]
[JsonSerializable(typeof(CpAnswerTableDto))]
[JsonSerializable(typeof(RemapDto))]
[JsonSerializable(typeof(DialInitDto))]
[JsonSerializable(typeof(PntMeshDto))]
[JsonSerializable(typeof(ExeMeshDto))]
[JsonSerializable(typeof(MeshIndexDto))]
[JsonSerializable(typeof(MeshCensusDto))]
[JsonSerializable(typeof(CockpitPackDto))]
[JsonSerializable(typeof(MissionVocabularyDto))]
[JsonSerializable(typeof(FlightMenusDto))]
[JsonSerializable(typeof(PlanesAtlasDto))]
internal sealed partial class TransformJsonContext : JsonSerializerContext
{
    // The tree is read by people.  The default encoder escapes every non-ASCII character and both
    // quote forms (a section sign becomes §, an apostrophe '), which turns a documentation
    // string into line noise.  UnsafeRelaxedJsonEscaping still escapes what JSON requires — the
    // "unsafe" in the name is about embedding output in HTML, which nothing here does.
    private static readonly JsonSerializerOptions ReadableOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The context every data-tree document is written through.</summary>
    public static TransformJsonContext Readable { get; } = new(ReadableOptions);
}
