using CYAC.Port.Core.Data;

namespace CYAC.Port.Core.Model.Mission;

/// <summary>
/// The authoring vocabulary the mission documents are written in, as loaded from
/// <c>missions/_vocabulary.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// A host installs it once at startup, the way it installs the trig and class-record tables
/// (the data-tree rule — the runtime carries the KNOWLEDGE of what these tables mean but reads
/// their contents from the tree, so the tree stays the one place a modder edits and the two halves
/// of the transform cannot drift apart).
/// </para>
/// <para>
/// Everything here is derived from the image: the tag names and the class tables
/// (the scenery names ARE the mesh descriptor names the 46-entry class table @<c>image@0x34F90</c>
/// points at).
/// </para>
/// </remarks>
public static class MissionVocabulary
{
    /// <summary>Where the document lives inside the data tree.</summary>
    public const string DataPath = "missions/_vocabulary.json";

    private static MissionVocabularyDto? _loaded;
    private static Dictionary<int, string> _aircraft = [];
    private static Dictionary<int, string> _scenery = [];

    /// <summary>Whether a host has installed the vocabulary.</summary>
    public static bool IsLoaded => _loaded is not null;

    /// <summary>Installs the vocabulary from its document.</summary>
    /// <param name="document">The parsed <c>missions/_vocabulary.json</c>.</param>
    /// <exception cref="InvalidDataException">The document carries no class tables.</exception>
    public static void Load(MissionVocabularyDto document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.AircraftClasses is null || document.SceneryClasses is null)
        {
            throw new InvalidDataException($"{DataPath} carries no class tables");
        }

        _aircraft = document.AircraftClasses.ToDictionary(e => e.Id, e => e.Name ?? string.Empty);
        _scenery = document.SceneryClasses.ToDictionary(e => e.Id, e => e.Name ?? string.Empty);
        _loaded = document;
    }

    /// <summary>Forgets the installed vocabulary (tests).</summary>
    public static void Unload()
    {
        _loaded = null;
        _aircraft = [];
        _scenery = [];
    }

    /// <summary>Class id → name, for the aircraft and vehicles a mission may place.</summary>
    public static IReadOnlyDictionary<int, string> AircraftClassNames
    {
        get
        {
            _ = Required();
            return _aircraft;
        }
    }

    /// <summary>Class id → name, for the scenery prototypes the theater catalogs place.</summary>
    public static IReadOnlyDictionary<int, string> SceneryClassNames
    {
        get
        {
            _ = Required();
            return _scenery;
        }
    }

    /// <summary>The theater catalog each era loads, indexed by era.</summary>
    public static IReadOnlyList<string> TheaterAssetForEra => Required().TheaterAssetForEra ?? [];

    /// <summary>The highest actor slot an author may use: 12.</summary>
    public static int MaxActorSlot => Required().MaxActorSlot;

    /// <summary>The highest nav-waypoint slot: 2.</summary>
    public static int MaxNavSlot => Required().MaxNavSlot;

    /// <summary>The PLAYER pseudo-class id.</summary>
    public static int PlayerClassId => Required().PlayerClassId;

    /// <summary>The HOME-BASE-POS pseudo-class id.</summary>
    public static int HomeBasePositionClassId => Required().HomeBasePositionClassId;

    /// <summary>The lowest class id that spawns a real object.</summary>
    public static int FirstSpawningClassId => Required().FirstSpawningClassId;

    /// <summary>The class id a <c>prim_4d00</c> opener denotes.</summary>
    public static int AirportClassId => Required().AirportClassId;

    /// <summary>The tag byte a header-level directive name encodes to.</summary>
    /// <param name="name">An authoring name such as <c>player_aircraft</c>.</param>
    /// <exception cref="InvalidDataException">The vocabulary does not name that directive.</exception>
    public static int DirectiveTag(string name) => Tag(Required().Directives, name, "directive");

    /// <summary>The tag byte an object attribute name encodes to.</summary>
    /// <param name="name">An authoring name such as <c>actor_slot</c>.</param>
    /// <exception cref="InvalidDataException">The vocabulary does not name that attribute.</exception>
    public static int AttributeTag(string name) => Tag(Required().Attributes, name, "attribute");

    /// <summary>The tag a placement name encodes to — also the <c>MissionPlacementKind</c> value.</summary>
    /// <param name="name">An authoring name such as <c>at_site</c>.</param>
    /// <exception cref="InvalidDataException">The vocabulary does not name that placement.</exception>
    public static int PlacementTag(string name) => Tag(Required().Placements, name, "placement");

    private static int Tag(List<MissionVocabularyEntryDto>? table, string name, string what)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (MissionVocabularyEntryDto entry in table ?? [])
        {
            if (string.Equals(entry.Name, name, StringComparison.Ordinal))
            {
                return entry.Id;
            }
        }

        throw new InvalidDataException($"{DataPath} names no {what} \"{name}\"");
    }

    private static MissionVocabularyDto Required() =>
        _loaded ?? throw new InvalidOperationException(
            $"the mission vocabulary is not loaded: call {nameof(MissionVocabulary)}.{nameof(Load)} " +
            $"with \"{DataPath}\" from the data tree (run cyac-transform to produce it).");
}
