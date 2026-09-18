using System.Text.Json.Serialization;

namespace CYAC.Port.Core.Data;

/// <summary>
/// <c>exe/tables/create_mission.json</c>: the constant tables the CREATE MISSION builder
/// (<c>custom_mission_build_from_picks @image@0x27E76</c>) places the picked enemies with.
/// </summary>
/// <remarks>
/// <para>
/// It carries two tables, each found by reading the instructions that index it and checked against the
/// layout <see cref="Model.Mission.CustomMissionVocabulary"/> names:
/// </para>
/// <list type="bullet">
/// <item>the formation offsets that <c>enemy_slot_fill_position @image@0x283A3</c> adds to a clause's
/// origin, one triple per slot;</item>
/// <item>the altitude table the builder's type-1 arm reads the player's altitude from, one word per
/// ALTITUDE picker row.</item>
/// </list>
/// <para>
/// The document is shaped as sections, so the form's other tables can join it. The altitude table
/// joined it.
/// </para>
/// </remarks>
public sealed class CreateMissionTableDto
{
    /// <summary>Where the document lives in the data tree.</summary>
    public const string DataPath = "exe/tables/create_mission.json";

    /// <summary>The document's schema tag.</summary>
    public const string FormatTag = "cyac.table.createMission/1";

    /// <summary>The document's schema tag, <see cref="FormatTag"/>.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>What the document is, in prose.</summary>
    [JsonPropertyName("about")]
    public string? About { get; init; }

    /// <summary>The formation-offset table.</summary>
    [JsonPropertyName("formationOffsets")]
    public FormationOffsetTableDto? FormationOffsets { get; init; }

    /// <summary>The altitude table: the feet each ALTITUDE picker row stands for.</summary>
    [JsonPropertyName("altitudeFeet")]
    public AltitudeFeetTableDto? AltitudeFeet { get; init; }
}

/// <summary>
/// The CREATE MISSION altitude table: one <c>u16</c> per ALTITUDE picker row, the altitude in feet the
/// builder spawns the player at.
/// </summary>
public sealed class AltitudeFeetTableDto
{
    /// <summary>What the table is and who reads it.</summary>
    [JsonPropertyName("about")]
    public string? About { get; init; }

    /// <summary>Where the table lives and what it holds.</summary>
    [JsonPropertyName("source")]
    public DataSourceDto? Source { get; init; }

    /// <summary>
    /// The instructions that read the table, as the image offset of the first of them; the table's DGROUP
    /// offset is the displacement of the word read that follows.
    /// </summary>
    [JsonPropertyName("indexedAt")]
    public string? IndexedAt { get; init; }

    /// <summary>How many rows the table holds: one per ALTITUDE picker row.</summary>
    [JsonPropertyName("rows")]
    public int Rows { get; init; }

    /// <summary>The altitude of each row, in feet, in row order.</summary>
    [JsonPropertyName("feet")]
    public List<int>? Feet { get; init; }
}

/// <summary>The formation-offset table: one <c>(x, y, z)</c> triple of <c>i16</c> per row and slot.</summary>
public sealed class FormationOffsetTableDto
{
    /// <summary>Where the table lives and what it holds.</summary>
    [JsonPropertyName("source")]
    public DataSourceDto? Source { get; init; }

    /// <summary>
    /// The instructions that index the table, as the image offset of the <c>mov ax,imm16</c> that loads
    /// the slots-per-row multiplier; the record size and the table's DGROUP offset follow it.
    /// </summary>
    [JsonPropertyName("indexedAt")]
    public string? IndexedAt { get; init; }

    /// <summary>How many rows the table holds.</summary>
    [JsonPropertyName("rows")]
    public int Rows { get; init; }

    /// <summary>How many slots one row holds.</summary>
    [JsonPropertyName("slotsPerRow")]
    public int SlotsPerRow { get; init; }

    /// <summary>Every slot, row by row and slot by slot, in table order.</summary>
    [JsonPropertyName("offsets")]
    public List<FormationOffsetDto>? Offsets { get; init; }
}

/// <summary>One formation slot's offset from its clause's origin, in world feet.</summary>
public sealed class FormationOffsetDto
{
    /// <summary>The formation row.</summary>
    [JsonPropertyName("row")]
    public int Row { get; init; }

    /// <summary>The slot within the row.</summary>
    [JsonPropertyName("slot")]
    public int Slot { get; init; }

    /// <summary>The X offset.</summary>
    [JsonPropertyName("x")]
    public int X { get; init; }

    /// <summary>The Y offset, the altitude axis.</summary>
    [JsonPropertyName("y")]
    public int Y { get; init; }

    /// <summary>The Z offset.</summary>
    [JsonPropertyName("z")]
    public int Z { get; init; }
}
