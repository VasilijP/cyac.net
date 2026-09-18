using System.Text.Json.Serialization;

namespace CYAC.Port.Core.Data;

// Wire format of <data>/scenarios.json — the mission picker's catalog (2a.lib::scenario.bin).
//
// The RUNTIME reads scenarios.json, so its
// wire format belongs beside the runtime, next to T5's exe-table and aircraft documents.  The tool
// still writes them through the same declarations, so the two halves cannot disagree.

/// <summary>Bytes a model does not explain, kept so the round trip still closes.</summary>
public sealed class ResidueSpanDto
{
    /// <summary>Where the span starts in the re-emitted body.</summary>
    [JsonPropertyName("offset")]
    public int Offset { get; init; }

    /// <summary>Its bytes, upper-case hex.</summary>
    [JsonPropertyName("hex")]
    public string? Hex { get; init; }
}

/// <summary>
/// One <c>scenario.bin</c> record: a line of the mission picker.
/// </summary>
/// <remarks>
/// Field map: KNOWN_FIELDS["s_scenario_record"]</c> (stride <c>0xA4</c>).  <c>_</c>-prefixed fields are derived names for readers and are ignored
/// when the document is read back.
/// </remarks>
public sealed class ScenarioEntryDto
{
    /// <summary>The record's position in the catalog as loaded.</summary>
    [JsonPropertyName("slot")]
    public int Slot { get; init; }

    /// <summary><c>+0x00</c> — the record's own index, the key into the 50-slot unlock array.</summary>
    [JsonPropertyName("recordIndex")]
    public int RecordIndex { get; init; }

    /// <summary><c>+0x01</c> — the era, which is also the theater.</summary>
    [JsonPropertyName("era")]
    public int Era { get; init; }

    /// <summary>The era's name, derived.</summary>
    [JsonPropertyName("_eraName")]
    public string? EraName { get; init; }

    /// <summary>The theater asset the era loads, derived.</summary>
    [JsonPropertyName("_theaterAsset")]
    public string? TheaterAsset { get; init; }

    /// <summary><c>+0x02</c> — the player side's national marking, a frame of <c>INSIG.PIC</c>.</summary>
    [JsonPropertyName("insigniaIndex")]
    public int InsigniaIndex { get; init; }

    /// <summary>The insignia's name, derived.</summary>
    [JsonPropertyName("_insigniaName")]
    public string? InsigniaName { get; init; }

    /// <summary><c>+0x03</c> — the player's aircraft, 0..5.</summary>
    [JsonPropertyName("playerAircraftIndex")]
    public int PlayerAircraftIndex { get; init; }

    /// <summary>The player aircraft's name, derived.</summary>
    [JsonPropertyName("_playerAircraftName")]
    public string? PlayerAircraftName { get; init; }

    /// <summary><c>+0x04</c> — the featured opponent's class id (0 = none).</summary>
    [JsonPropertyName("opponentClassId")]
    public int OpponentClassId { get; init; }

    /// <summary>The opponent's name, derived.</summary>
    [JsonPropertyName("_opponentName")]
    public string? OpponentName { get; init; }

    /// <summary><c>+0x05</c> — the authored mission rating 1..3.</summary>
    [JsonPropertyName("difficultyRating")]
    public int DifficultyRating { get; init; }

    /// <summary>The rating's label, derived.</summary>
    [JsonPropertyName("_difficultyName")]
    public string? DifficultyName { get; init; }

    /// <summary><c>+0x06</c> — the mission date, "M-D-YY".</summary>
    [JsonPropertyName("date")]
    public string? Date { get; init; }

    /// <summary><c>+0x0F</c> — the picker's title line.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary><c>+0x2D</c> — the <c>.S</c> module's asset name, as authored.</summary>
    [JsonPropertyName("moduleAssetName")]
    public string? ModuleAssetName { get; init; }

    /// <summary><c>+0x3B</c> — the briefing blurb the detail panel word-wraps.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

/// <summary>Wire format of <c>&lt;data&gt;/scenarios.json</c>.</summary>
public sealed class ScenarioCatalogDto
{
    /// <summary>Document format tag.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>What the document is and how it is edited.</summary>
    [JsonPropertyName("about")]
    public string? About { get; init; }

    /// <summary>Where the bytes came from: <c>2a.lib/scenario.bin</c>.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    /// <summary>Bytes per record: 164.</summary>
    [JsonPropertyName("recordBytes")]
    public int RecordBytes { get; init; }

    /// <summary>The records, in file order.</summary>
    [JsonPropertyName("missions")]
    public List<ScenarioEntryDto>? Missions { get; init; }

    /// <summary>
    /// Law L4: bytes the record model does not explain — a string field's padding that is not zero,
    /// for instance.  Empty on the shipping catalog (a padding audit found all 200 string
    /// fields NUL-padded), and applied over the re-emitted body on the way back.
    /// </summary>
    [JsonPropertyName("unknownResidue")]
    public List<ResidueSpanDto>? UnknownResidue { get; init; }
}
