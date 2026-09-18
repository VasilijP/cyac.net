using System.Text.Json;
using CYAC.Port.Core.Model.Mission;

namespace CYAC.Port.Core.Data;

/// <summary>
/// Turns <c>&lt;data&gt;/config.json</c> into a <see cref="GameConfig"/> and back.
/// </summary>
/// <remarks>
/// <para>
/// The RUNTIME reads <c>config.json</c>, and
/// this is the same reasoning T5 used for <see cref="AircraftDataCodec"/>.  <c>ConfigTransform</c>
/// delegates to this one implementation, so the loader and the tool's inverse cannot drift.
/// </para>
/// <para>
/// The layout knowledge stays in <see cref="GameConfig"/>: the codec starts from a blob carrying
/// only the magic byte and writes every field through that type's own setters, so a field this codec
/// forgot shows up immediately as a round-trip mismatch rather than as a silently zeroed setting.
/// </para>
/// </remarks>
public static class GameConfigCodec
{
    /// <summary>Renders a config as the tree's JSON document.</summary>
    /// <param name="config">The parsed config.</param>
    public static byte[] Serialise(GameConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        (List<string> audioMask, string? audioUnknown) = ByteEnumText.WriteFlags<AudioMuteFlags>((byte)config.AudioMask);
        (List<string> overlays, string? overlaysUnknown) = ByteEnumText.WriteFlags<CockpitOverlayFlags>((byte)config.Overlays);

        ConfigDto dto = new ConfigDto
        {
            Format = "cyac.config/1",
            About =
                "yeager.cfg, 86 bytes — the game's only persistent state (there is no pilot roster). " +
                "Layout: / KNOWN_FIELDS[\"YeagerCfg\"]. " +
                "Flag bytes are 0/1 as the original stores them.",
            InitFlagD = config.InitFlagD,
            VideoSubMode = config.VideoSubMode,
            GameStateByte = config.GameStateByte,
            InputMode = ByteEnumText.Write<InputMode>((byte)config.InputMode),
            JoystickXMin = config.JoystickXMin,
            JoystickYMin = config.JoystickYMin,
            JoystickXMax = config.JoystickXMax,
            JoystickYMax = config.JoystickYMax,
            AudioMask = audioMask,
            AudioMaskUnknownBits = audioUnknown,
            AudioDriver = ByteEnumText.Write<AudioDriverType>((byte)config.AudioDriver),
            SpeechDevice = ByteEnumText.Write<SpeechDevice>((byte)config.SpeechDevice),
            GameStateFlagByte = config.GameStateFlagByte,
            DetailLevel = ByteEnumText.Write<GraphicsDetailLevel>((byte)config.DetailLevel),
            AircraftMeshDetailFlag = config.AircraftMeshDetailFlag,
            DitheredHorizonFlag = config.DitheredHorizonFlag,
            CloudsFlag = config.CloudsFlag,
            BitmapExplosionsFlag = config.BitmapExplosionsFlag,
            FlightInfoVisibleFlag = config.FlightInfoVisibleFlag,
            CheatInvincibleFlag = config.CheatInvincibleFlag,
            CheatUnlimitedAmmoFlag = config.CheatUnlimitedAmmoFlag,
            CheatEasyAimingFlag = config.CheatEasyAimingFlag,
            CheatEasyLandingsFlag = config.CheatEasyLandingsFlag,
            TargetInfoVisibleFlag = config.TargetInfoVisibleFlag,
            CheatNoBlackoutFlag = config.CheatNoBlackoutFlag,
            Overlays = overlays,
            OverlaysUnknownBits = overlaysUnknown,
            AutosaveFilmFlag = config.AutosaveFilmFlag,
            CockpitHiddenFlag = config.CockpitHiddenFlag,
            MapZoomLevel = config.MapZoomLevel,
            EraSelector = ByteEnumText.Write<MissionEra>((byte)config.EraSelector),
            Difficulty = ByteEnumText.Write<BriefingDifficulty>((byte)config.Difficulty),
            Progression =
            [
                .. Enumerable.Range(0, MissionProgression.SlotCount)
                    .Select(i => ByteEnumText.Write<MissionSlotState>(config.Progression[i])),
            ],
        };

        return JsonSerializer.SerializeToUtf8Bytes(dto, PortDataJsonContext.Readable.ConfigDto);
    }

    /// <summary>Reads the tree's JSON document back into a config.</summary>
    /// <param name="json">The <c>config.json</c> bytes.</param>
    /// <exception cref="InvalidDataException">The document is malformed or incomplete.</exception>
    public static GameConfig Deserialise(ReadOnlySpan<byte> json)
    {
        ConfigDto dto = JsonSerializer.Deserialize(json, PortDataJsonContext.Readable.ConfigDto)
                        ?? throw new InvalidDataException("config.json is empty");

        // Start from a blob that carries only the magic byte, then write every field through
        // GameConfig's own setters: the offsets stay in one place (Port.Core), and any byte this
        // transform forgot would show up immediately as a round-trip mismatch.
        byte[] blob = new byte[GameConfig.Bytes];
        blob[0] = GameConfig.MagicByte;
        GameConfig config = GameConfig.Parse(blob);

        config.InitFlagD = dto.InitFlagD;
        config.VideoSubMode = dto.VideoSubMode;
        config.GameStateByte = dto.GameStateByte;
        config.InputMode = (InputMode)ByteEnumText.Read<InputMode>(dto.InputMode, "inputMode");
        config.JoystickXMin = dto.JoystickXMin;
        config.JoystickYMin = dto.JoystickYMin;
        config.JoystickXMax = dto.JoystickXMax;
        config.JoystickYMax = dto.JoystickYMax;
        config.AudioMask = (AudioMuteFlags)ByteEnumText.ReadFlags<AudioMuteFlags>(
            dto.AudioMask, dto.AudioMaskUnknownBits, "audioMask");
        config.AudioDriver = (AudioDriverType)ByteEnumText.Read<AudioDriverType>(dto.AudioDriver, "audioDriver");
        config.SpeechDevice = (SpeechDevice)ByteEnumText.Read<SpeechDevice>(dto.SpeechDevice, "speechDevice");
        config.GameStateFlagByte = dto.GameStateFlagByte;
        config.DetailLevel = (GraphicsDetailLevel)ByteEnumText.Read<GraphicsDetailLevel>(dto.DetailLevel, "detailLevel");
        config.AircraftMeshDetailFlag = dto.AircraftMeshDetailFlag;
        config.DitheredHorizonFlag = dto.DitheredHorizonFlag;
        config.CloudsFlag = dto.CloudsFlag;
        config.BitmapExplosionsFlag = dto.BitmapExplosionsFlag;
        config.FlightInfoVisibleFlag = dto.FlightInfoVisibleFlag;
        config.CheatInvincibleFlag = dto.CheatInvincibleFlag;
        config.CheatUnlimitedAmmoFlag = dto.CheatUnlimitedAmmoFlag;
        config.CheatEasyAimingFlag = dto.CheatEasyAimingFlag;
        config.CheatEasyLandingsFlag = dto.CheatEasyLandingsFlag;
        config.TargetInfoVisibleFlag = dto.TargetInfoVisibleFlag;
        config.CheatNoBlackoutFlag = dto.CheatNoBlackoutFlag;
        config.Overlays = (CockpitOverlayFlags)ByteEnumText.ReadFlags<CockpitOverlayFlags>(
            dto.Overlays, dto.OverlaysUnknownBits, "overlays");
        config.AutosaveFilmFlag = dto.AutosaveFilmFlag;
        config.CockpitHiddenFlag = dto.CockpitHiddenFlag;
        config.MapZoomLevel = dto.MapZoomLevel;
        config.EraSelector = (MissionEra)ByteEnumText.Read<MissionEra>(dto.EraSelector, "eraSelector");
        config.Difficulty = (BriefingDifficulty)ByteEnumText.Read<BriefingDifficulty>(dto.Difficulty, "difficulty");

        List<string> progression = dto.Progression ?? [];
        if (progression.Count != MissionProgression.SlotCount)
        {
            throw new InvalidDataException(
                $"\"progression\" holds {progression.Count} slots; the array is " +
                $"{MissionProgression.SlotCount} bytes (yeager.cfg@0x24..0x55)");
        }

        for (int i = 0; i < progression.Count; i++)
        {
            config.Progression[i] = ByteEnumText.Read<MissionSlotState>(progression[i], $"progression[{i}]");
        }

        return config;
    }
}
