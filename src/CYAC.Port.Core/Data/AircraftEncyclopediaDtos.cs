using System.Text.Json.Serialization;

namespace CYAC.Port.Core.Data;

// The RUNTIME's view of two documents the hangar and the tactics screen read:
//
//   pi.json                     the aircraft encyclopedia (14 pages) and the 15 matchup hints
//   exe/tables/planes_atlas.json  which sheet row each aircraft's side view is
//
// Law L2: CYAC.Port.Core opens the transformed documents, never a .lib or the executable.  These are
// READER shapes and deliberately not cyac-transform's own PiDto: that one carries the round-trip's
// bookkeeping — every string's body offset, the residue spans, the pointer table — which is exactly
// what the runtime must not depend on.  What the game needs is the fourteen pages with their text
// resolved, and that is what AircraftEncyclopedia builds out of these.

/// <summary><c>pi.json</c>: the hangar's aircraft pages and the tactics screen's hints.</summary>
public sealed class AircraftEncyclopediaDto
{
    /// <summary>The document's schema tag.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>The fourteen encyclopedia pages, in <c>pi.bin</c> directory order.</summary>
    [JsonPropertyName("planes")]
    public List<EncyclopediaPlaneDto>? Planes { get; init; }

    /// <summary>The fifteen (you, enemy) matchup hints the tactics screen shows.</summary>
    [JsonPropertyName("hints")]
    public List<EncyclopediaHintDto>? Hints { get; init; }
}

/// <summary>One encyclopedia page, as the document stores it.</summary>
public sealed class EncyclopediaPlaneDto
{
    /// <summary>Its position in the directory, 0..13 — the order <c>←</c>/<c>→</c> cycle in.</summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary>Field <c>+0x00</c>: the aircraft-class id the class table is indexed by.</summary>
    [JsonPropertyName("aircraftClassId")]
    public int AircraftClassId { get; init; }

    /// <summary>Derived in the document: which of the six flyable slots this is, or −1.</summary>
    [JsonPropertyName("_flyablePlayerSlot")]
    public int FlyablePlayerSlot { get; init; }

    /// <summary>Field <c>+0x0D</c>: the tactics screen's armament rating.</summary>
    [JsonPropertyName("armamentRating")]
    public int ArmamentRating { get; init; }

    /// <summary>Field <c>+0x10</c>: <c>WEIGHT</c>, pounds.</summary>
    [JsonPropertyName("weightLb")]
    public int WeightLb { get; init; }

    /// <summary>Field <c>+0x12</c>: <c>MAX SPEED</c>, mph.</summary>
    [JsonPropertyName("maxSpeedMph")]
    public int MaxSpeedMph { get; init; }

    /// <summary>Field <c>+0x14</c>: <c>MAX ALT</c>, feet.</summary>
    [JsonPropertyName("maxAltitudeFt")]
    public int MaxAltitudeFt { get; init; }

    /// <summary>Field <c>+0x16</c>: thrust-to-weight as u8.8 (the tactics screen's row).</summary>
    [JsonPropertyName("thrustToWeightQ8")]
    public int ThrustToWeightQ8 { get; init; }

    /// <summary>Field <c>+0x18</c>: wing loading, pounds per square foot.</summary>
    [JsonPropertyName("wingLoadingPsf")]
    public int WingLoadingPsf { get; init; }

    /// <summary>Field <c>+0x1C</c>: the hangar view's X, world units (<c>image@0x26655</c>).</summary>
    [JsonPropertyName("hangarCameraX")]
    public int HangarCameraX { get; init; }

    /// <summary>Field <c>+0x1E</c>: its Y (<c>image@0x2666A</c>).</summary>
    [JsonPropertyName("hangarCameraY")]
    public int HangarCameraY { get; init; }

    /// <summary>Field <c>+0x20</c>: its Z, the viewing distance (<c>image@0x2667F</c>).</summary>
    [JsonPropertyName("hangarCameraZ")]
    public int HangarCameraZ { get; init; }

    /// <summary>
    /// Field <c>+0x22</c>: how far the side view's vertical centre is moved
    /// (<c>image@0x26A87</c>).
    /// </summary>
    [JsonPropertyName("silhouetteYOffset")]
    public int SilhouetteYOffset { get; init; }

    /// <summary>Field <c>+0x24</c>: the length callout's feet.</summary>
    [JsonPropertyName("lengthFt")]
    public int LengthFt { get; init; }

    /// <summary>Field <c>+0x26</c>: its inches.</summary>
    [JsonPropertyName("lengthIn")]
    public int LengthIn { get; init; }

    /// <summary>Field <c>+0x28</c>: the height callout's feet.</summary>
    [JsonPropertyName("heightFt")]
    public int HeightFt { get; init; }

    /// <summary>Field <c>+0x2A</c>: its inches.</summary>
    [JsonPropertyName("heightIn")]
    public int HeightIn { get; init; }

    /// <summary>Field <c>+0x2C</c>: the length callout line's left end (<c>image@0x26B23</c>).</summary>
    [JsonPropertyName("lengthCalloutX1")]
    public int LengthCalloutX1 { get; init; }

    /// <summary>Field <c>+0x2E</c>: its right end.</summary>
    [JsonPropertyName("lengthCalloutX2")]
    public int LengthCalloutX2 { get; init; }

    /// <summary>Field <c>+0x30</c>: the height callout line's top (<c>image@0x26AAE</c>).</summary>
    [JsonPropertyName("heightCalloutY1")]
    public int HeightCalloutY1 { get; init; }

    /// <summary>Field <c>+0x32</c>: its bottom.</summary>
    [JsonPropertyName("heightCalloutY2")]
    public int HeightCalloutY2 { get; init; }

    /// <summary>
    /// The record's pointer fields by name: <c>nameFull</c>, <c>nameShort</c>, <c>armament1..3</c>,
    /// <c>armamentSummary</c>, <c>engine</c>, <c>description</c>, each a body offset into
    /// <see cref="Strings"/>.
    /// </summary>
    [JsonPropertyName("textPointers")]
    public Dictionary<string, int>? TextPointers { get; init; }

    /// <summary>The record's own string pool, in stored order.</summary>
    [JsonPropertyName("strings")]
    public List<EncyclopediaStringDto>? Strings { get; init; }
}

/// <summary>One string in a page's pool.</summary>
public sealed class EncyclopediaStringDto
{
    /// <summary>Its body-absolute offset — what a <c>textPointers</c> entry names.</summary>
    [JsonPropertyName("offset")]
    public int Offset { get; init; }

    /// <summary>The text, when every byte is printable ASCII.</summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary>
    /// Its bytes, when they are not: the Me-109E's title carries the font's own opening-quote glyph
    /// <c>0x0C</c> (<c>Messerschmitt Me-109E \x0C Emil"</c>), which is a GLYPH and not a control
    /// character — the original draws it as a left double quote.
    /// </summary>
    [JsonPropertyName("hex")]
    public string? Hex { get; init; }
}

/// <summary>One matchup hint: what to do when THIS class meets THAT one.</summary>
public sealed class EncyclopediaHintDto
{
    /// <summary>Its position in the hint directory, 0..14.</summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary>The player's aircraft-class id.</summary>
    [JsonPropertyName("playerClassId")]
    public int PlayerClassId { get; init; }

    /// <summary>The enemy's.</summary>
    [JsonPropertyName("enemyClassId")]
    public int EnemyClassId { get; init; }

    /// <summary>The advice, as shipped.</summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }
}

/// <summary><c>exe/tables/planes_atlas.json</c>: the hangar's fourteen side-view rows.</summary>
public sealed class PlanesAtlasTableDto
{
    /// <summary>The document's schema tag.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>Where the record table starts in the unpacked image.</summary>
    [JsonPropertyName("tableImageOffset")]
    public string? TableImageOffset { get; init; }

    /// <summary>The width every silhouette is blitted at: the full 112-pixel sheet.</summary>
    [JsonPropertyName("sheetWidth")]
    public int SheetWidth { get; init; }

    /// <summary>The rows, in SHEET order — which is not <c>pi.json</c> order.</summary>
    [JsonPropertyName("records")]
    public List<PlanesAtlasRowDto>? Records { get; init; }
}

/// <summary>One silhouette row.</summary>
public sealed class PlanesAtlasRowDto
{
    /// <summary>Its position in the table, 0..13.</summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary>0 = <c>planes0.pic</c>, 1 = <c>planes1.pic</c>.</summary>
    [JsonPropertyName("variant")]
    public int Variant { get; init; }

    /// <summary>The row's first line in the 112 × 200 sheet.</summary>
    [JsonPropertyName("topY")]
    public int TopY { get; init; }

    /// <summary>How many lines the row is.</summary>
    [JsonPropertyName("height")]
    public int Height { get; init; }

    /// <summary>The aircraft's engagement-prototype DGROUP pointer — the match key.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }
}
