using System.Text.Json.Serialization;

namespace CYAC.Port.Core.Data;

// Wire format of <data>/missions/_vocabulary.json — the authoring vocabulary the mission documents
// are written in: which class ids exist and what they are called, which theater each era loads, and
// the directive / attribute / placement tag names.
//
// It is a document rather than a table compiled into the runtime because the tree has to be
// self-describing for a modder (transform-: "the game must be readable, editable, moddable") and
// because duplicating the names on both sides of the transform is how two vocabularies drift apart.
// Every value in it is KNOWLEDGE — names, ids and tag numbers derived from the image
// — never game data.

/// <summary>One entry of a name table: an id and what it is called.</summary>
public sealed class MissionVocabularyEntryDto
{
    /// <summary>The numeric id or tag.</summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>The authoring name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

/// <summary>Wire format of <c>missions/_vocabulary.json</c>.</summary>
public sealed class MissionVocabularyDto
{
    /// <summary>Document format tag.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>What the document is.</summary>
    [JsonPropertyName("about")]
    public string? About { get; init; }

    /// <summary>The highest actor slot an author may use: 12 (<c>[0xEE5A]</c> is u16[13]).</summary>
    [JsonPropertyName("maxActorSlot")]
    public int MaxActorSlot { get; init; }

    /// <summary>The highest nav-waypoint slot: 2 (<c>g_nav_slot_record_array [0xB564]</c>).</summary>
    [JsonPropertyName("maxNavSlot")]
    public int MaxNavSlot { get; init; }

    /// <summary>The PLAYER pseudo-class id: 0.</summary>
    [JsonPropertyName("playerClassId")]
    public int PlayerClassId { get; init; }

    /// <summary>The HOME-BASE-POS pseudo-class id: 1.</summary>
    [JsonPropertyName("homeBasePositionClassId")]
    public int HomeBasePositionClassId { get; init; }

    /// <summary>The lowest class id that spawns a real object: 6.</summary>
    [JsonPropertyName("firstSpawningClassId")]
    public int FirstSpawningClassId { get; init; }

    /// <summary>The class id a <c>prim_4d00</c> opener denotes: 26 (the airport).</summary>
    [JsonPropertyName("airportClassId")]
    public int AirportClassId { get; init; }

    /// <summary>The theater catalog each era loads, indexed by era.</summary>
    [JsonPropertyName("theaterAssetForEra")]
    public List<string>? TheaterAssetForEra { get; init; }

    /// <summary>The aircraft / vehicle class ids a <c>.S</c> mission may place.</summary>
    [JsonPropertyName("aircraftClasses")]
    public List<MissionVocabularyEntryDto>? AircraftClasses { get; init; }

    /// <summary>The scenery prototype ids only the <c>.W</c> catalogs place.</summary>
    [JsonPropertyName("sceneryClasses")]
    public List<MissionVocabularyEntryDto>? SceneryClasses { get; init; }

    /// <summary>Header-level directive tags and their authoring names.</summary>
    [JsonPropertyName("directives")]
    public List<MissionVocabularyEntryDto>? Directives { get; init; }

    /// <summary>Object attribute tags and their authoring names.</summary>
    [JsonPropertyName("attributes")]
    public List<MissionVocabularyEntryDto>? Attributes { get; init; }

    /// <summary>Placement tags and their authoring names.</summary>
    [JsonPropertyName("placements")]
    public List<MissionVocabularyEntryDto>? Placements { get; init; }
}
