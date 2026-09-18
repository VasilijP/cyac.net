using System.Text.Json.Serialization;

namespace CYAC.Port.Core.Data;

// Wire format of <data>/config.json.  Field names are the CYAC.Port.Core GameConfig property names
// in camelCase (T1); enums are strings; the 50-slot progression is an array of named states.
// Order here is the order in the file — declaration order is what the serialiser emits.

/// <summary>Wire format of <c>&lt;data&gt;/config.json</c> — <c>yeager.cfg</c> as a document.</summary>
#pragma warning disable CS1591
public sealed class ConfigDto
{
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    [JsonPropertyName("about")]
    public string? About { get; init; }

    [JsonPropertyName("initFlagD")]
    public byte InitFlagD { get; init; }

    [JsonPropertyName("videoSubMode")]
    public byte VideoSubMode { get; init; }

    [JsonPropertyName("gameStateByte")]
    public byte GameStateByte { get; init; }

    [JsonPropertyName("inputMode")]
    public string? InputMode { get; init; }

    [JsonPropertyName("joystickXMin")]
    public short JoystickXMin { get; init; }

    [JsonPropertyName("joystickYMin")]
    public short JoystickYMin { get; init; }

    [JsonPropertyName("joystickXMax")]
    public short JoystickXMax { get; init; }

    [JsonPropertyName("joystickYMax")]
    public short JoystickYMax { get; init; }

    [JsonPropertyName("audioMask")]
    public List<string>? AudioMask { get; init; }

    [JsonPropertyName("audioMaskUnknownBits")]
    public string? AudioMaskUnknownBits { get; init; }

    [JsonPropertyName("audioDriver")]
    public string? AudioDriver { get; init; }

    [JsonPropertyName("speechDevice")]
    public string? SpeechDevice { get; init; }

    [JsonPropertyName("gameStateFlagByte")]
    public byte GameStateFlagByte { get; init; }

    [JsonPropertyName("detailLevel")]
    public string? DetailLevel { get; init; }

    [JsonPropertyName("aircraftMeshDetailFlag")]
    public byte AircraftMeshDetailFlag { get; init; }

    [JsonPropertyName("ditheredHorizonFlag")]
    public byte DitheredHorizonFlag { get; init; }

    [JsonPropertyName("cloudsFlag")]
    public byte CloudsFlag { get; init; }

    [JsonPropertyName("bitmapExplosionsFlag")]
    public byte BitmapExplosionsFlag { get; init; }

    [JsonPropertyName("flightInfoVisibleFlag")]
    public byte FlightInfoVisibleFlag { get; init; }

    [JsonPropertyName("cheatInvincibleFlag")]
    public byte CheatInvincibleFlag { get; init; }

    [JsonPropertyName("cheatUnlimitedAmmoFlag")]
    public byte CheatUnlimitedAmmoFlag { get; init; }

    [JsonPropertyName("cheatEasyAimingFlag")]
    public byte CheatEasyAimingFlag { get; init; }

    [JsonPropertyName("cheatEasyLandingsFlag")]
    public byte CheatEasyLandingsFlag { get; init; }

    [JsonPropertyName("targetInfoVisibleFlag")]
    public byte TargetInfoVisibleFlag { get; init; }

    [JsonPropertyName("cheatNoBlackoutFlag")]
    public byte CheatNoBlackoutFlag { get; init; }

    [JsonPropertyName("overlays")]
    public List<string>? Overlays { get; init; }

    [JsonPropertyName("overlaysUnknownBits")]
    public string? OverlaysUnknownBits { get; init; }

    [JsonPropertyName("autosaveFilmFlag")]
    public byte AutosaveFilmFlag { get; init; }

    [JsonPropertyName("cockpitHiddenFlag")]
    public byte CockpitHiddenFlag { get; init; }

    [JsonPropertyName("mapZoomLevel")]
    public ushort MapZoomLevel { get; init; }

    [JsonPropertyName("eraSelector")]
    public string? EraSelector { get; init; }

    [JsonPropertyName("difficulty")]
    public string? Difficulty { get; init; }

    [JsonPropertyName("progression")]
    public List<string>? Progression { get; init; }
}
#pragma warning restore CS1591
