using System.Text.Json.Serialization;
using CYAC.Port.Core.Data;

namespace CYAC.Port.Transform.Families;

// Wire formats of the six mission-adjacent catalogs T4 transforms:
//   scenarios.json  pi.json  strings.json  strings2.json  remap.json  dialinit.json
//
// Every one of them is the ROUND-TRIP SOURCE OF TRUTH for its asset: the family's Inverse reads
// exactly these fields back and rebuilds the decompressed body.  Names are the port's domain names
// (CYAC.Port.Core.Model.Mission.MissionEntry for the scenario record); `_`-prefixed fields are
// derived annotations for readers and are ignored on the way back in.

// scenarios.json's own DTOs moved to CYAC.Port.Core/Data/MissionDataDtos.cs in T7 (the
// runtime reads that document); they are used from there.

// ----------------------------------------------------------------------------------------- pi.json

internal sealed class PiDto
{
    [JsonPropertyName("format")] public string? Format { get; init; }

    [JsonPropertyName("about")] public string? About { get; init; }

    [JsonPropertyName("source")] public string? Source { get; init; }

    [JsonPropertyName("bodyBytes")] public int BodyBytes { get; init; }

    [JsonPropertyName("planeDirectory")] public List<int>? PlaneDirectory { get; init; }

    [JsonPropertyName("hintDirectory")] public List<int>? HintDirectory { get; init; }

    [JsonPropertyName("planes")] public List<PiPlaneDto>? Planes { get; init; }

    [JsonPropertyName("hints")] public List<PiHintDto>? Hints { get; init; }

    [JsonPropertyName("unknownResidue")] public List<ResidueSpanDto>? UnknownResidue { get; init; }
}

internal sealed class PiPlaneDto
{
    [JsonPropertyName("index")] public int Index { get; init; }

    [JsonPropertyName("bodyOffset")] public int BodyOffset { get; init; }

    [JsonPropertyName("aircraftClassId")] public int AircraftClassId { get; init; }

    [JsonPropertyName("_className")] public string? ClassName { get; init; }

    [JsonPropertyName("_flyablePlayerSlot")] public int FlyablePlayerSlot { get; init; }

    [JsonPropertyName("armamentRating")] public int ArmamentRating { get; init; }

    [JsonPropertyName("weightLb")] public int WeightLb { get; init; }

    [JsonPropertyName("maxSpeedMph")] public int MaxSpeedMph { get; init; }

    [JsonPropertyName("maxAltitudeFt")] public int MaxAltitudeFt { get; init; }

    [JsonPropertyName("thrustToWeightQ8")] public int ThrustToWeightQ8 { get; init; }

    [JsonPropertyName("_thrustToWeightText")] public string? ThrustToWeightText { get; init; }

    [JsonPropertyName("wingLoadingPsf")] public int WingLoadingPsf { get; init; }

    [JsonPropertyName("hangarCameraX")] public int HangarCameraX { get; init; }

    [JsonPropertyName("hangarCameraY")] public int HangarCameraY { get; init; }

    [JsonPropertyName("hangarCameraZ")] public int HangarCameraZ { get; init; }

    [JsonPropertyName("silhouetteYOffset")] public int SilhouetteYOffset { get; init; }

    [JsonPropertyName("lengthFt")] public int LengthFt { get; init; }

    [JsonPropertyName("lengthIn")] public int LengthIn { get; init; }

    [JsonPropertyName("heightFt")] public int HeightFt { get; init; }

    [JsonPropertyName("heightIn")] public int HeightIn { get; init; }

    [JsonPropertyName("lengthCalloutX1")] public int LengthCalloutX1 { get; init; }

    [JsonPropertyName("lengthCalloutX2")] public int LengthCalloutX2 { get; init; }

    [JsonPropertyName("heightCalloutY1")] public int HeightCalloutY1 { get; init; }

    [JsonPropertyName("heightCalloutY2")] public int HeightCalloutY2 { get; init; }

    /// <summary>Body-absolute offsets of the record's strings, keyed by the field that points at them.</summary>
    [JsonPropertyName("textPointers")] public Dictionary<string, int>? TextPointers { get; init; }

    /// <summary>The record's own string pool, in stored order — where the text lives.</summary>
    [JsonPropertyName("strings")] public List<PiStringDto>? Strings { get; init; }
}

internal sealed class PiStringDto
{
    [JsonPropertyName("offset")] public int Offset { get; init; }

    [JsonPropertyName("text")] public string? Text { get; init; }

    /// <summary>Non-ASCII runs keep their bytes instead of text, so nothing is lost.</summary>
    [JsonPropertyName("hex")] public string? Hex { get; init; }

    [JsonPropertyName("_usedBy")] public List<string>? UsedBy { get; init; }
}

internal sealed class PiHintDto
{
    [JsonPropertyName("index")] public int Index { get; init; }

    [JsonPropertyName("bodyOffset")] public int BodyOffset { get; init; }

    [JsonPropertyName("playerClassId")] public int PlayerClassId { get; init; }

    [JsonPropertyName("_playerName")] public string? PlayerName { get; init; }

    [JsonPropertyName("enemyClassId")] public int EnemyClassId { get; init; }

    [JsonPropertyName("_enemyName")] public string? EnemyName { get; init; }

    [JsonPropertyName("text")] public string? Text { get; init; }

    /// <summary>Bytes after the hint's NUL, when the record's span is longer than its text.</summary>
    [JsonPropertyName("trailingHex")] public string? TrailingHex { get; init; }
}

// ------------------------------------------------------------------------------------ strings.json

internal sealed class StringsDto
{
    [JsonPropertyName("format")] public string? Format { get; init; }

    [JsonPropertyName("about")] public string? About { get; init; }

    [JsonPropertyName("source")] public string? Source { get; init; }

    [JsonPropertyName("padNuls")] public int PadNuls { get; init; }

    [JsonPropertyName("strings")] public List<UiStringDto>? Strings { get; init; }
}

internal sealed class UiStringDto
{
    [JsonPropertyName("index")] public int Index { get; init; }

    [JsonPropertyName("text")] public string? Text { get; init; }

    /// <summary>Where the string is shown, from <c>StringsBinCallerCatalog</c> — a translator's map.</summary>
    [JsonPropertyName("_usedBy")] public List<string>? UsedBy { get; init; }
}

// ----------------------------------------------------------------------------------- strings2.json

internal sealed class CpAnswerTableDto
{
    [JsonPropertyName("format")] public string? Format { get; init; }

    [JsonPropertyName("about")] public string? About { get; init; }

    [JsonPropertyName("source")] public string? Source { get; init; }

    [JsonPropertyName("role")] public string? Role { get; init; }

    [JsonPropertyName("padNuls")] public int PadNuls { get; init; }

    [JsonPropertyName("failureMessage")] public string? FailureMessage { get; init; }

    [JsonPropertyName("challenges")] public List<CpChallengeDto>? Challenges { get; init; }
}

internal sealed class CpChallengeDto
{
    [JsonPropertyName("index")] public int Index { get; init; }

    [JsonPropertyName("question")] public string? Question { get; init; }

    [JsonPropertyName("answer")] public string? Answer { get; init; }
}

// -------------------------------------------------------------------------------------- remap.json

internal sealed class RemapDto
{
    [JsonPropertyName("format")] public string? Format { get; init; }

    [JsonPropertyName("about")] public string? About { get; init; }

    [JsonPropertyName("source")] public string? Source { get; init; }

    [JsonPropertyName("entryCount")] public int EntryCount { get; init; }

    [JsonPropertyName("_maxTranslatedIndex")] public int MaxTranslatedIndex { get; init; }

    [JsonPropertyName("translatedColor")] public List<int>? TranslatedColor { get; init; }
}

// ----------------------------------------------------------------------------------- dialinit.json

internal sealed class DialInitDto
{
    [JsonPropertyName("format")] public string? Format { get; init; }

    [JsonPropertyName("about")] public string? About { get; init; }

    [JsonPropertyName("source")] public string? Source { get; init; }

    [JsonPropertyName("_slotNames")] public List<string>? SlotNames { get; init; }

    [JsonPropertyName("cockpits")] public List<DialCockpitDto>? Cockpits { get; init; }
}

internal sealed class DialCockpitDto
{
    [JsonPropertyName("aircraftIndex")] public int AircraftIndex { get; init; }

    [JsonPropertyName("_aircraftName")] public string? AircraftName { get; init; }

    [JsonPropertyName("instruments")] public List<DialInstrumentDto>? Instruments { get; init; }
}

internal sealed class DialInstrumentDto
{
    [JsonPropertyName("slot")] public int Slot { get; init; }

    [JsonPropertyName("_slotName")] public string? SlotName { get; init; }

    [JsonPropertyName("_dgroupAddress")] public string? DgroupAddress { get; init; }

    [JsonPropertyName("present")] public bool Present { get; init; }

    [JsonPropertyName("rect")] public List<int>? Rect { get; init; }

    [JsonPropertyName("pivot")] public List<int>? Pivot { get; init; }

    [JsonPropertyName("kindWord")] public string? KindWord { get; init; }

    [JsonPropertyName("_kindName")] public string? KindName { get; init; }

    [JsonPropertyName("param0")] public int Param0 { get; init; }

    [JsonPropertyName("param1")] public int Param1 { get; init; }

    [JsonPropertyName("needleAngleOffset")] public int NeedleAngleOffset { get; init; }

    [JsonPropertyName("keyframeCount")] public int KeyframeCount { get; init; }

    [JsonPropertyName("amplitude")] public int Amplitude { get; init; }

    [JsonPropertyName("sweepStep")] public int SweepStep { get; init; }

    [JsonPropertyName("directionInvert")] public int DirectionInvert { get; init; }

    /// <summary>
    /// The engine-filled half of the record, present only when it is not all zero (it is zero in
    /// every shipped record).
    /// </summary>
    [JsonPropertyName("runtimeStateHex")] public string? RuntimeStateHex { get; init; }
}
