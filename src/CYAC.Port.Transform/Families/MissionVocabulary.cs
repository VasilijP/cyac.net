using System.Text.Json;
using CYAC.Formats.EaLib;
using CYAC.Port.Core.Data;
using CYAC.Port.Transform.Json;

namespace CYAC.Port.Transform.Families;

/// <summary>
/// Writes <c>missions/_vocabulary.json</c> — the id and tag NAME tables the mission and theater
/// documents are written in.
/// </summary>
/// <remarks>
/// <para>
/// The tag names come from <see cref="SDataModel"/>, which derived them from the image (for
/// the directive / attribute / placement tags).  The class names are read from the originals at
/// transform time (<see cref="OriginalNames"/>: the class table's engagement prototypes and, for the
/// scenery, the mesh descriptors it points at); the two engine pseudo-classes carry the
/// port's own words (<see cref="ReadingAidLabels"/>).
/// </para>
/// <para>
/// It is emitted as a DOCUMENT rather than compiled into the runtime for two reasons: the tree has
/// to be self-describing for a modder (transform-), and a name table that exists on both sides of
/// the transform is a name table that drifts.  <c>CYAC.Port.Core</c> loads this file into
/// <c>MissionClassCatalog</c> at startup.
/// </para>
/// </remarks>
public static class MissionVocabulary
{
    /// <summary>Builds the document's bytes.</summary>
    /// <param name="names">The class names read from the originals.</param>
    public static byte[] Document(OriginalNames names)
    {
        ArgumentNullException.ThrowIfNull(names);
        Dictionary<int, string> aircraft = names.AircraftClasses.ToDictionary();
        aircraft[ReadingAidLabels.PlayerClassId] = ReadingAidLabels.PlayerClass;
        aircraft[ReadingAidLabels.HomeBaseClassId] = ReadingAidLabels.HomeBaseClass;

        MissionVocabularyDto dto = new MissionVocabularyDto
        {
            Format = "cyac.mission-vocabulary/1",
            About =
                "The authoring vocabulary of missions/*.json and world/*.json: the class-table ids " +
                "and their names, the theater each era loads, and the directive / attribute / " +
                "placement tag names. Ids 0 and 1 are the two engine pseudo-classes (the player's " +
                "start and the made-it-home reference); 6..24 are the aircraft and vehicles a " +
                "mission may place; 26..45 (no 35) are the scenery prototypes only the theater " +
                "catalogs place, and their names are the mesh descriptors the class table points at.",
            MaxActorSlot = SDataModel.MaxActorSlot,
            MaxNavSlot = SDataModel.MaxNavSlot,
            PlayerClassId = ReadingAidLabels.PlayerClassId,
            HomeBasePositionClassId = ReadingAidLabels.HomeBaseClassId,
            FirstSpawningClassId = 6,
            AirportClassId = SDataModel.Prim4D00ClassId,
            TheaterAssetForEra = [.. SDataModel.TheaterAssetForEra],
            AircraftClasses = Entries(aircraft),
            SceneryClasses = Entries(names.SceneryClasses),
            Directives = Entries(SDataModel.DirectiveNames),
            Attributes = Entries(SDataModel.AttrNames),
            Placements = Entries(SDataModel.PosNames),
        };

        return JsonSerializer.SerializeToUtf8Bytes(dto, TransformJsonContext.Readable.MissionVocabularyDto);
    }

    private static List<MissionVocabularyEntryDto> Entries(IReadOnlyDictionary<int, string> table) =>
        [.. table.OrderBy(kv => kv.Key).Select(kv => new MissionVocabularyEntryDto
        {
            Id = kv.Key,
            Name = kv.Value,
        })];

    private static List<MissionVocabularyEntryDto> Entries(IReadOnlyDictionary<byte, string> table) =>
        [.. table.OrderBy(kv => kv.Key).Select(kv => new MissionVocabularyEntryDto
        {
            Id = kv.Key,
            Name = kv.Value,
        })];
}
