using System.Text.Json.Serialization;

namespace CYAC.Port.Core.Data;

// Wire format of <data>/aircraft/<name>.json — one flyable aircraft's `.fmd` flight model and
// `.fme` flight envelope, merged into one editable document.
//
// The two assets are separate members of 2b.lib and the transform's inverse re-emits each of them
// byte-exactly from this one document; the runtime reads it through
// CYAC.Port.Core.Model.Flight.AircraftDefinition.Load.

/// <summary>One of the nine 16-byte integrator blocks at the head of a <c>.fmd</c>.</summary>
public sealed class AircraftInitBlockDto
{
    /// <summary>The block index; the block's offset in the file is <c>index * 0x10</c>.</summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary>What the block drives, from <c>FlightModelDecoder.BlockRoles</c>.</summary>
    [JsonPropertyName("role")]
    public string? Role { get; init; }

    /// <summary>How well that role is established: <c>verified</c> / partly / hypothesis.</summary>
    [JsonPropertyName("confidence")]
    public string? Confidence { get; init; }

    /// <summary><c>+0x00</c> — the integration accumulator; zero in every shipped file.</summary>
    [JsonPropertyName("value")]
    public int Value { get; init; }

    /// <summary><c>+0x04</c> — scratch; zero in every shipped file.</summary>
    [JsonPropertyName("working")]
    public int Working { get; init; }

    /// <summary><c>+0x08</c> — the positive-axis limit.</summary>
    [JsonPropertyName("hiBound")]
    public int HiBound { get; init; }

    /// <summary><c>+0x0A</c> — the negative-axis limit.</summary>
    [JsonPropertyName("loBound")]
    public int LoBound { get; init; }

    /// <summary><c>+0x0C</c> — the directional base step.</summary>
    [JsonPropertyName("baseDir")]
    public int BaseDir { get; init; }

    /// <summary><c>+0x0E</c> — the sign-flip kick / zero-input decay target.</summary>
    [JsonPropertyName("dirStep")]
    public int DirStep { get; init; }
}

/// <summary>One named scalar of the <c>.fmd</c>'s 154-byte tail.</summary>
public sealed class AircraftTailFieldDto
{
    /// <summary>The field's offset in the file, which is also its offset in <c>s_aircraft_master</c>.</summary>
    [JsonPropertyName("offset")]
    public string? Offset { get; init; }

    /// <summary>The field's name in <c>FlightModelDecoder.TailFields</c>.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Its width and signedness: <c>u8</c>, <c>i16</c>, <c>u16</c> or <c>i32</c>.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>The value.</summary>
    [JsonPropertyName("value")]
    public int Value { get; init; }

    [JsonPropertyName("scannerName")]
    public string? ScannerName { get; init; }

    /// <summary>What is known about the field across the six shipped files.</summary>
    [JsonPropertyName("note")]
    public string? Note { get; init; }
}

/// <summary>One point of a V-n envelope curve.</summary>
public sealed class EnvelopePointDto
{
    /// <summary>Airspeed in feet per second.</summary>
    [JsonPropertyName("airspeedFps")]
    public int AirspeedFps { get; init; }

    /// <summary>Altitude in units of <c>feet / 8</c>, as the consumer compares it.</summary>
    [JsonPropertyName("altitudeUnits")]
    public int AltitudeUnits { get; init; }
}

/// <summary>One curve of the flight envelope: the reachable altitudes at one load factor.</summary>
public sealed class EnvelopeCurveDto
{
    /// <summary>The curve's record index, 0..13.</summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary><c>+0x00</c> — the signed load factor in G that keys the lookup (-4..+9).</summary>
    [JsonPropertyName("loadFactorG")]
    public int LoadFactorG { get; init; }

    /// <summary><c>+0x01</c> — how many of the eight point slots are authored.</summary>
    [JsonPropertyName("pointCount")]
    public int PointCount { get; init; }

    /// <summary><c>+0x02</c> — the index of the peak-altitude point.</summary>
    [JsonPropertyName("peakIndex")]
    public int PeakIndex { get; init; }

    /// <summary><c>+0x03</c> — the index of the high-speed boundary point.</summary>
    [JsonPropertyName("highSpeedIndex")]
    public int HighSpeedIndex { get; init; }

    /// <summary>
    /// All eight point slots.  Slots at or past <see cref="PointCount"/> are authoring-tool filler,
    /// not curve data — they are carried verbatim because one consumer scans past the count
    /// (<c>fme_low_speed_limit_check @image@0x2A9BE</c>).
    /// </summary>
    [JsonPropertyName("points")]
    public List<EnvelopePointDto>? Points { get; init; }
}

/// <summary>One flyable aircraft's merged flight model and flight envelope.</summary>
public sealed class AircraftDefinitionDto
{
    /// <summary>Document kind and version.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>What the document is, which two assets it merges and how the inverse splits it.</summary>
    [JsonPropertyName("about")]
    public string? About { get; init; }

    /// <summary>The asset basename, e.g. <c>"f4"</c>.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>The aircraft's display name.</summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    /// <summary>The index <c>g_active_aircraft_idx [0xC31A]</c> selects this aircraft with.</summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary>The archive member the flight model came from, e.g. <c>"2b.lib/P51.FMD"</c>.</summary>
    [JsonPropertyName("flightModelSource")]
    public string? FlightModelSource { get; init; }

    /// <summary>The archive member the envelope came from, e.g. <c>"2b.lib/P51.FME"</c>.</summary>
    [JsonPropertyName("envelopeSource")]
    public string? EnvelopeSource { get; init; }

    /// <summary>The nine integrator blocks at <c>.fmd</c> <c>0x00..0x8F</c>.</summary>
    [JsonPropertyName("initBlocks")]
    public List<AircraftInitBlockDto>? InitBlocks { get; init; }

    /// <summary>The <c>.fmd</c>'s 154-byte scalar tail, field by field; every byte is covered.</summary>
    [JsonPropertyName("tail")]
    public List<AircraftTailFieldDto>? Tail { get; init; }

    /// <summary>The <c>.fme</c>'s 14 V-n curves.</summary>
    [JsonPropertyName("envelope")]
    public List<EnvelopeCurveDto>? Envelope { get; init; }
}
