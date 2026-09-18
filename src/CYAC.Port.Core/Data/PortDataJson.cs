using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CYAC.Port.Core.Data;

// The one source-generated serialisation context for the transformed data tree's documents.  Same
// doctrine as Schema/SchemaJson.cs: no reflection-based serialisation, so the assembly stays
// trimming-friendly and every document's shape is visible in one place.
//
// It lives in CYAC.Port.Core, not in the tool, because the RUNTIME reads these documents
// (transform-plan L2) and must not depend on cyac-transform.  The tool writes them through the
// same context, so a shape change cannot make the two halves disagree.

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ScalarTableDto))]
[JsonSerializable(typeof(HitProbabilityTableDto))]
[JsonSerializable(typeof(PlayerDamageTablesDto))]
[JsonSerializable(typeof(ScancodeTableDto))]
[JsonSerializable(typeof(UiWidgetTableDto))]
[JsonSerializable(typeof(ExeStringCatalogDto))]
[JsonSerializable(typeof(UiStringCatalogDto))]
[JsonSerializable(typeof(CombatConstantsDto))]
[JsonSerializable(typeof(FlightTuningDto))]
[JsonSerializable(typeof(CockpitLayoutDto))]
[JsonSerializable(typeof(DialInitDocumentDto))]
[JsonSerializable(typeof(CockpitPackDocumentDto))]
[JsonSerializable(typeof(FontDocumentDto))]
[JsonSerializable(typeof(AircraftClassTableDto))]
[JsonSerializable(typeof(EngagementDocumentDto))]
[JsonSerializable(typeof(ClassRegistryDocumentDto))]
[JsonSerializable(typeof(WeaponTablesDocumentDto))]
[JsonSerializable(typeof(AircraftDefinitionDto))]
[JsonSerializable(typeof(DataTreeManifestDto))]
[JsonSerializable(typeof(ConfigDto))]
[JsonSerializable(typeof(ScenarioCatalogDto))]
[JsonSerializable(typeof(MissionDocumentDto))]
[JsonSerializable(typeof(MissionVocabularyDto))]
[JsonSerializable(typeof(PaletteDocumentDto))]
[JsonSerializable(typeof(ImageDocumentDto))]
[JsonSerializable(typeof(PntMeshDocumentDto))]
[JsonSerializable(typeof(ExeMeshDocumentDto))]
[JsonSerializable(typeof(SpeechClipDto))]
[JsonSerializable(typeof(MusicStreamDto))]
[JsonSerializable(typeof(AudioDriverDto))]
[JsonSerializable(typeof(AircraftEncyclopediaDto))]
[JsonSerializable(typeof(PlanesAtlasTableDto))]
[JsonSerializable(typeof(AdvisorTableDto))]
[JsonSerializable(typeof(CreateMissionTableDto))]
[JsonSerializable(typeof(EffectLookTableDto))]
[JsonSerializable(typeof(WorldTableDto))]
public sealed partial class PortDataJsonContext : JsonSerializerContext
{
    // The tree is read by people.  The default encoder escapes every non-ASCII character and both
    // quote forms, which turns a documentation string into line noise; UnsafeRelaxedJsonEscaping
    // still escapes everything JSON requires (the "unsafe" is about embedding output in HTML).
    private static readonly JsonSerializerOptions ReadableOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The context every data-tree document is written and read through.</summary>
    public static PortDataJsonContext Readable { get; } = new(ReadableOptions);
}
