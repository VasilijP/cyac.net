using System.Buffers.Binary;
using CYAC.Port.Core.Schema;

namespace CYAC.Port.Core.Model.Mission;

/// <summary>
/// <c>yeager.cfg</c> — the game's entire persistent state in 86 bytes: video/input/audio selection,
/// joystick calibration, the display and cheat toggles, and the 50-byte campaign progression.
/// </summary>
/// <remarks>
/// <para>
/// Canonical layout: KNOWN_FIELDS["YeagerCfg"]</c> (as exported into <c>Schema/state_schema.json</c>,
/// which <c>MissionSchemaLinkTests</c> checks this type against).  The two ends of the round trip in
/// the original are <c>cfg_read_or_init_persistent_blob @image@0x2D2C7</c> (open → magic → scatter
/// into DGROUP → default-init → argv override → hardware autodetect) and <c>cfg_write_persistent_blob
/// @image@0x2D6DF</c> (gather 86 globals → <c>file_write_helper</c>).
/// </para>
/// <para>
/// <b>Why this type holds the raw blob.</b>  A config file is a persistence format, so
/// <see cref="ToBytes"/> must be able to give back exactly what <see cref="Parse"/> was given —
/// including values outside a documented enum, and including the three bytes above the five
/// documented <see cref="AudioMuteFlags"/> bits that the shipped file happens to set.  The typed
/// properties are therefore a <i>view</i> over the persisted bytes rather than a decoded copy; the
/// port's runtime state will be separate objects that this seeds.
/// </para>
/// <para>
/// <b>Dual-use slots</b> (project memory "CYAC dual-use globals"): <c>cfg@0x0D</c>
/// <c>[0xE483]</c>, <c>cfg@0x11</c> <c>[0xF108]</c> and <c>cfg@0x1D</c> <c>[0xF1CB]</c> were named in
/// from their cfg-write context and renamed once their runtime roles were found; the
/// persisted byte and the live global are one storage location.  The port splits them: the persisted
/// meaning lives here, the runtime meaning will live in the subsystem that owns it.  <c>cfg@0x0E</c>
/// is the sharpest case — the same byte is the audio driver index <i>and</i>
/// <c>g_joystick_mid_axis_hi</c>, and the scanner says outright "Port should split joystick-calib vs
/// audio-driver-type".
/// </para>
/// <para>INT-only: every byte here selects content or behaviour.</para>
/// </remarks>
[OriginalStruct("YeagerCfg")]
[OriginalGlobal("g_cfg_sub_mode")]
[OriginalGlobal("g_input_mode")]
[OriginalGlobal("g_scenario_era_selector")]
[OriginalGlobal("g_mission_unlock_array")]
public sealed class GameConfig
{
    /// <summary>The file's length: 86 bytes = 0x24 header + the 50-byte progression.</summary>
    public const int Bytes = 0x24 + MissionProgression.SlotCount;

    /// <summary>
    /// <c>cfg@0x00</c> — the magic byte, <c>0xAC</c>.  A mismatch makes the reader discard the file
    /// and default-init (<c>image@0x2D2C7..0x2D32D</c>).
    /// </summary>
    public const byte MagicByte = 0xAC;

    /// <summary>Offset of the 50-byte mission-unlock array: <c>cfg@0x24</c> (<c>memcpy</c> @<c>image@0x2D3EA</c>).</summary>
    public const int ProgressionOffset = 0x24;

    /// <summary>The joystick calibration extreme the reset default writes: ±105 (<c>cfg_joystick_active_calibration_defaults @0x2D2B4</c>).</summary>
    public const short DefaultJoystickCalibration = 105;

    private readonly byte[] _blob;

    private GameConfig(byte[] blob, MissionProgression progression)
    {
        _blob = blob;
        Progression = progression;
    }

    /// <summary>
    /// Reads an 86-byte config blob.
    /// </summary>
    /// <param name="blob">The file contents, exactly <see cref="Bytes"/> bytes.</param>
    /// <exception cref="ArgumentException">Wrong length, or the magic byte is not <see cref="MagicByte"/>.</exception>
    public static GameConfig Parse(ReadOnlySpan<byte> blob)
    {
        if (blob.Length != Bytes)
        {
            throw new ArgumentException($"yeager.cfg is {Bytes} bytes; got {blob.Length}.", nameof(blob));
        }

        if (blob[0] != MagicByte)
        {
            throw new ArgumentException(
                $"bad cfg magic 0x{blob[0]:X2}: the reader at image@0x2D32D requires 0x{MagicByte:X2} " +
                "and default-inits otherwise.",
                nameof(blob));
        }

        return new GameConfig(
            blob.ToArray(),
            MissionProgression.FromBytes(blob.Slice(ProgressionOffset, MissionProgression.SlotCount)));
    }

    /// <summary>
    /// The state the reader default-inits to when there is no readable <c>yeager.cfg</c>
    /// (<c>image@0x2D3F6..0x2D496</c>, plus <c>mission_unlock_memset_defaults @0x2D809</c> on the
    /// same path).
    /// </summary>
    /// <remarks>
    /// <c>GameConfigTests</c> checks that claim against <c>sources/yeager.cfg</c> byte for byte.  The
    /// 50 progression bytes deliberately differ: a fresh install unlocks three missions, the shipped
    /// file is a completed save.
    /// </remarks>
    public static GameConfig Defaults()
    {
        byte[] blob = new byte[Bytes];
        blob[0x00] = MagicByte;
        blob[0x02] = 6;      // sub_mode: Mode-X
        blob[0x04] = (byte)InputMode.Keyboard;
        BinaryPrimitives.WriteInt16LittleEndian(blob.AsSpan(0x05), -DefaultJoystickCalibration);
        BinaryPrimitives.WriteInt16LittleEndian(blob.AsSpan(0x07), -DefaultJoystickCalibration);
        BinaryPrimitives.WriteInt16LittleEndian(blob.AsSpan(0x09), DefaultJoystickCalibration);
        BinaryPrimitives.WriteInt16LittleEndian(blob.AsSpan(0x0B), DefaultJoystickCalibration);
        blob[0x0D] = 0xFE;   // audio mask
        blob[0x0E] = (byte)AudioDriverType.PcSpeaker;
        blob[0x0F] = (byte)SpeechDevice.PcSpeaker;
        blob[0x11] = (byte)GraphicsDetailLevel.Medium;
        blob[0x12] = 1;      // detailed aircraft meshes
        blob[0x13] = 1;      // dithered horizon
        blob[0x14] = 1;      // clouds
        blob[0x15] = 1;      // bitmap explosions
        blob[0x16] = 1;      // flight info visible
        blob[0x17] = 1;      // invincible
        blob[0x18] = 1;      // unlimited ammo
        blob[0x19] = 1;      // easy aiming
        blob[0x1A] = 1;      // easy landings
        blob[0x1B] = 1;      // target info visible
        blob[0x1D] = (byte)(CockpitOverlayFlags.Target | CockpitOverlayFlags.Map);
        blob[0x1F] = 1;      // cockpit hidden
        BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(0x20), 7);   // map zoom 1×
        blob[0x22] = (byte)MissionEra.Korea;
        blob[0x23] = (byte)BriefingDifficulty.Normal;

        MissionProgression progression = MissionProgression.FreshInstall();
        progression.CopyTo(blob.AsSpan(ProgressionOffset));
        return new GameConfig(blob, progression);
    }

    /// <summary>
    /// The 86 bytes to write back — byte-identical to what <see cref="Parse"/> read, with
    /// <see cref="Progression"/>'s current state folded in.
    /// </summary>
    public byte[] ToBytes()
    {
        byte[] blob = _blob.ToArray();
        Progression.CopyTo(blob.AsSpan(ProgressionOffset));
        return blob;
    }

    /// <summary>The campaign save state at <c>cfg@0x24..0x55</c>.</summary>
    [OriginalField("+0x24", "mission_unlock[50]")]
    public MissionProgression Progression { get; }

    /// <summary><c>cfg@0x01</c> — <c>g_cfg_init_flag_d [0xB3]</c>; role still open (0 in the shipped file).</summary>
    [OriginalField("+0x01", "init_flag_d_u8")]
    public byte InitFlagD
    {
        get => _blob[0x01];
        set => _blob[0x01] = value;
    }

    /// <summary>
    /// <c>cfg@0x02</c> — the video sub-mode, 0..6; <c>g_cfg_sub_mode [0x15E]</c>, "THE master
    /// display-path selector".  Stock is 6 (Mode-X).
    /// </summary>
    /// <remarks>
    /// The DGROUP global is a u16 but only its low byte persists — the writer's <c>MOV AL,[0x15E]</c>
    /// @<c>image@0x2D6F2</c>.  Mode dispatch:'s canonical sub_mode→INT10h table @<c>[0x015E]</c>.
    /// Modes 2 and 3 ship broken (project memory "CYAC is 320×200-only"), which is a data/defect fact
    /// about the game, not about this byte.
    /// </remarks>
    [OriginalField("+0x02", "sub_mode_u8")]
    public byte VideoSubMode
    {
        get => _blob[0x02];
        set => _blob[0x02] = value;
    }

    /// <summary><c>cfg@0x03</c> — <c>g_cfg_game_state_byte [0xC313]</c>; 0 in the shipped file.</summary>
    [OriginalField("+0x03", "game_state_byte_u8")]
    public byte GameStateByte
    {
        get => _blob[0x03];
        set => _blob[0x03] = value;
    }

    /// <summary><c>cfg@0x04</c> — the active input device, <c>g_input_mode [0xE45C]</c>.</summary>
    [OriginalField("+0x04", "input_mode_u8")]
    public InputMode InputMode
    {
        get => (InputMode)_blob[0x04];
        set => _blob[0x04] = (byte)value;
    }

    /// <summary><c>cfg@0x05</c> — joystick X minimum, <c>g_joystick_x_min_i16 [0xE47C]</c>.</summary>
    [OriginalField("+0x05", "joystick_x_min_i16")]
    public short JoystickXMin
    {
        get => ReadI16(0x05);
        set => WriteI16(0x05, value);
    }

    /// <summary><c>cfg@0x07</c> — joystick Y minimum, <c>g_joystick_y_min_i16 [0xE480]</c>.</summary>
    [OriginalField("+0x07", "joystick_y_min_i16")]
    public short JoystickYMin
    {
        get => ReadI16(0x07);
        set => WriteI16(0x07, value);
    }

    /// <summary><c>cfg@0x09</c> — joystick X maximum, <c>g_joystick_x_max_i16 [0xE47E]</c>.</summary>
    [OriginalField("+0x09", "joystick_x_max_i16")]
    public short JoystickXMax
    {
        get => ReadI16(0x09);
        set => WriteI16(0x09, value);
    }

    /// <summary><c>cfg@0x0B</c> — joystick Y maximum, <c>g_joystick_y_max_i16 [0xE484]</c>.</summary>
    [OriginalField("+0x0B", "joystick_y_max_i16")]
    public short JoystickYMax
    {
        get => ReadI16(0x0B);
        set => WriteI16(0x0B, value);
    }

    /// <summary><c>cfg@0x0D</c> — the audio bit mask, <c>g_audio_mute_mask [0xE483]</c> (dual-use slot).</summary>
    [OriginalField("+0x0D", "audio_mute_mask_u8")]
    public AudioMuteFlags AudioMask
    {
        get => (AudioMuteFlags)_blob[0x0D];
        set => _blob[0x0D] = (byte)value;
    }

    /// <summary>
    /// <c>cfg@0x0E</c> — the audio driver family.  The same byte is <c>g_joystick_mid_axis_hi
    /// [0xE482]</c>; the port splits the two roles (see the type remarks).
    /// </summary>
    [OriginalField("+0x0E", "audio_driver_type_u8")]
    public AudioDriverType AudioDriver
    {
        get => (AudioDriverType)_blob[0x0E];
        set => _blob[0x0E] = (byte)value;
    }

    /// <summary><c>cfg@0x0F</c> — the speech device, <c>g_speech_device_index [0xC30E]</c>.</summary>
    [OriginalField("+0x0F", "speech_device_u8")]
    public SpeechDevice SpeechDevice
    {
        get => (SpeechDevice)_blob[0x0F];
        set => _blob[0x0F] = (byte)value;
    }

    /// <summary><c>cfg@0x10</c> — <c>g_game_state_flag_byte [0xC312]</c>; 0 in the shipped file.</summary>
    [OriginalField("+0x10", "game_state_flag_byte_u8")]
    public byte GameStateFlagByte
    {
        get => _blob[0x10];
        set => _blob[0x10] = value;
    }

    /// <summary><c>cfg@0x11</c> — the Graphics-menu detail level (dual-use slot <c>[0xF108]</c>).</summary>
    [OriginalField("+0x11", "graphics_detail_level_u8")]
    public GraphicsDetailLevel DetailLevel
    {
        get => (GraphicsDetailLevel)_blob[0x11];
        set => _blob[0x11] = (byte)value;
    }

    /// <summary>
    /// <c>cfg@0x12</c> — <c>g_simple_planes_flag [0xF0FE]</c>: <b>1 = Detailed, 0 = Simple</b>
    /// (the name reads backwards; the scanner spells the polarity out).  Selects the low-poly
    /// aircraft mesh variant.
    /// </summary>
    [OriginalField("+0x12", "simple_planes_flag_u8")]
    public byte AircraftMeshDetailFlag
    {
        get => _blob[0x12];
        set => _blob[0x12] = value;
    }

    /// <summary>True when <see cref="AircraftMeshDetailFlag"/> selects the detailed meshes.</summary>
    public bool UsesDetailedAircraftMeshes => AircraftMeshDetailFlag != 0;

    /// <summary><c>cfg@0x13</c> — <c>g_dithered_horizon_flag [0x781]</c>.</summary>
    [OriginalField("+0x13", "dithered_horizon_flag_u8")]
    public byte DitheredHorizonFlag
    {
        get => _blob[0x13];
        set => _blob[0x13] = value;
    }

    /// <summary><c>cfg@0x14</c> — <c>g_clouds_flag [0xB6]</c> (Graphics.Clouds).</summary>
    [OriginalField("+0x14", "clouds_flag_u8")]
    public byte CloudsFlag
    {
        get => _blob[0x14];
        set => _blob[0x14] = value;
    }

    /// <summary><c>cfg@0x15</c> — <c>g_bitmap_explosions_flag [0xC31E]</c>.</summary>
    [OriginalField("+0x15", "bitmap_explosions_flag_u8")]
    public byte BitmapExplosionsFlag
    {
        get => _blob[0x15];
        set => _blob[0x15] = value;
    }

    /// <summary><c>cfg@0x16</c> — <c>g_flight_info_visible [0xB0]</c> (Ctrl-F).</summary>
    [OriginalField("+0x16", "flight_info_visible_u8")]
    public byte FlightInfoVisibleFlag
    {
        get => _blob[0x16];
        set => _blob[0x16] = value;
    }

    /// <summary><c>cfg@0x17</c> — <c>g_cheat_invincible [0xE46B]</c> (Ctrl-I).</summary>
    [OriginalField("+0x17", "cheat_invincible_u8")]
    public byte CheatInvincibleFlag
    {
        get => _blob[0x17];
        set => _blob[0x17] = value;
    }

    /// <summary><c>cfg@0x18</c> — <c>g_cheat_unlimited_ammo [0xE46C]</c> (Ctrl-U).</summary>
    [OriginalField("+0x18", "cheat_unlimited_ammo_u8")]
    public byte CheatUnlimitedAmmoFlag
    {
        get => _blob[0x18];
        set => _blob[0x18] = value;
    }

    /// <summary><c>cfg@0x19</c> — <c>g_cheat_easy_aiming [0xE46D]</c> (Ctrl-E).</summary>
    [OriginalField("+0x19", "cheat_easy_aiming_u8")]
    public byte CheatEasyAimingFlag
    {
        get => _blob[0x19];
        set => _blob[0x19] = value;
    }

    /// <summary><c>cfg@0x1A</c> — <c>g_cheat_easy_landings [0xE46A]</c> (Ctrl-L).</summary>
    [OriginalField("+0x1A", "cheat_easy_landings_u8")]
    public byte CheatEasyLandingsFlag
    {
        get => _blob[0x1A];
        set => _blob[0x1A] = value;
    }

    /// <summary><c>cfg@0x1B</c> — <c>g_target_info_visible [0xB1]</c> (Ctrl-T).</summary>
    [OriginalField("+0x1B", "target_info_visible_u8")]
    public byte TargetInfoVisibleFlag
    {
        get => _blob[0x1B];
        set => _blob[0x1B] = value;
    }

    /// <summary><c>cfg@0x1C</c> — <c>g_cheat_no_blackout [0xB2]</c> (Ctrl-B); 0 in the shipped file.</summary>
    [OriginalField("+0x1C", "cheat_no_blackout_u8")]
    public byte CheatNoBlackoutFlag
    {
        get => _blob[0x1C];
        set => _blob[0x1C] = value;
    }

    /// <summary>
    /// The four Ctrl-key cheat bytes of <c>cfg@0x17..0x1A</c> plus <c>cfg@0x1C</c> — true when any is
    /// non-zero.
    /// </summary>
    /// <remarks>
    /// The shipped <c>sources/yeager.cfg</c> has all four of <c>0x17..0x1A</c> set to 1, which is why
    /// A recording taken with the cheats ON shows the same byte.  Whether 1 means "cheat armed"
    /// for every one of them is a runtime question the port should settle before wiring behaviour;
    /// this property only reports the bytes.
    /// </remarks>
    public bool AnyCheatByteSet =>
        CheatInvincibleFlag != 0 || CheatUnlimitedAmmoFlag != 0 || CheatEasyAimingFlag != 0
        || CheatEasyLandingsFlag != 0 || CheatNoBlackoutFlag != 0;

    /// <summary><c>cfg@0x1D</c> — visible in-flight overlays (dual-use slot <c>[0xF1CB]</c>).</summary>
    [OriginalField("+0x1D", "inflight_overlay_visibility_u8")]
    public CockpitOverlayFlags Overlays
    {
        get => (CockpitOverlayFlags)_blob[0x1D];
        set => _blob[0x1D] = (byte)value;
    }

    /// <summary><c>cfg@0x1E</c> — <c>g_autosave_film_flag [0xB4]</c>, read by <c>flight_session_end @0x30328</c>.</summary>
    [OriginalField("+0x1E", "autosave_film_flag_u8")]
    public byte AutosaveFilmFlag
    {
        get => _blob[0x1E];
        set => _blob[0x1E] = value;
    }

    /// <summary><c>cfg@0x1F</c> — <c>g_cockpit_hidden_flag [0xE471]</c> (Backspace; 1 = hidden).</summary>
    [OriginalField("+0x1F", "cockpit_hidden_flag_u8")]
    public byte CockpitHiddenFlag
    {
        get => _blob[0x1F];
        set => _blob[0x1F] = value;
    }

    /// <summary>
    /// <c>cfg@0x20</c> — the map/radar zoom index, <c>g_map_zoom_level [0xD8A0]</c>: 7..12 meaning
    /// 1×..32× (<c>1 &lt;&lt; (N − 7)</c>).  The only u16 field in the blob.
    /// </summary>
    [OriginalField("+0x20", "map_zoom_level_u16")]
    public ushort MapZoomLevel
    {
        get => BinaryPrimitives.ReadUInt16LittleEndian(_blob.AsSpan(0x20));
        set => BinaryPrimitives.WriteUInt16LittleEndian(_blob.AsSpan(0x20), value);
    }

    /// <summary>The zoom factor <see cref="MapZoomLevel"/> denotes: <c>1 &lt;&lt; (level − 7)</c>.</summary>
    public int MapZoomFactor => MapZoomLevel >= 7 ? 1 << (MapZoomLevel - 7) : 0;

    /// <summary>
    /// <c>cfg@0x22</c> — the era the mission picker filters by, <c>g_scenario_era_selector [0x2A0E]</c>.
    /// Stock is <see cref="MissionEra.Korea"/>.
    /// </summary>
    [OriginalField("+0x22", "scenario_era_selector_u8")]
    public MissionEra EraSelector
    {
        get => (MissionEra)_blob[0x22];
        set => _blob[0x22] = (byte)value;
    }

    /// <summary><c>cfg@0x23</c> — the difficulty level, <c>g_briefing_difficulty_idx [0xF10E]</c>.</summary>
    [OriginalField("+0x23", "briefing_difficulty_idx_u8")]
    public BriefingDifficulty Difficulty
    {
        get => (BriefingDifficulty)_blob[0x23];
        set => _blob[0x23] = (byte)value;
    }

    private short ReadI16(int offset) => BinaryPrimitives.ReadInt16LittleEndian(_blob.AsSpan(offset));

    private void WriteI16(int offset, short value) =>
        BinaryPrimitives.WriteInt16LittleEndian(_blob.AsSpan(offset), value);
}
