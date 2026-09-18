using System.Text.Json.Serialization;

namespace CYAC.Port.Core.Schema;

// Wire-format DTOs for Schema/state_schema.json.  They exist only so the public model
// (SchemaModel.cs) can stay clean and immutable: the JSON has snake_case meta keys and models
// structs as an object-of-arrays with the struct name in the key, neither of which we want in the
// port's API surface.  Deserialisation goes through the source-generated context below — no
// reflection-based serialisation, so the assembly stays trimming-friendly.

internal sealed class SchemaDocumentDto
{
    [JsonPropertyName("meta")]
    public SchemaMetaDto? Meta { get; init; }

    [JsonPropertyName("globals")]
    public List<GlobalEntryDto>? Globals { get; init; }

    [JsonPropertyName("structs")]
    public Dictionary<string, List<StructFieldDto>>? Structs { get; init; }
}

internal sealed class SchemaMetaDto
{
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("generated_by")]
    public string? GeneratedBy { get; init; }

    [JsonPropertyName("counts")]
    public SchemaCountsDto? Counts { get; init; }
}

internal sealed class SchemaCountsDto
{
    [JsonPropertyName("globals")]
    public int Globals { get; init; }

    [JsonPropertyName("typed_globals")]
    public int TypedGlobals { get; init; }

    [JsonPropertyName("structs")]
    public int Structs { get; init; }

    [JsonPropertyName("struct_fields")]
    public int StructFields { get; init; }
}

internal sealed class GlobalEntryDto
{
    [JsonPropertyName("offset")]
    public int Offset { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("width")]
    public int? Width { get; init; }

    [JsonPropertyName("count")]
    public int? Count { get; init; }
}

internal sealed class StructFieldDto
{
    [JsonPropertyName("offset")]
    public int Offset { get; init; }

    [JsonPropertyName("descriptor")]
    public string? Descriptor { get; init; }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(SchemaDocumentDto))]
internal sealed partial class SchemaJsonContext : JsonSerializerContext;
