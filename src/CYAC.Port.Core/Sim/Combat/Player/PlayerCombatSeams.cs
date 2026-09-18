
using System.Reflection;

namespace CYAC.Port.Core.Sim.Combat.Player;

/// <summary>
/// The DGROUP addresses the PLAYER-side combat code (frame-ladder rows 6, 7, 9 and the render-phase
/// lock-on hook) reads and writes, each cited to the instruction that proves it.
/// </summary>
/// <remarks>
/// Same rule as C1's <see cref="CombatRegisters"/> and C5's <c>LifecycleOffsets</c>: the offsets live
/// in ONE file, the game logic addresses them through named constants only.
/// </remarks>
public static class PlayerCombatOffsets
{
    /// <summary><c>g_object_pool_segment [0x0094]</c> — <c>image@0x03929</c>.</summary>
    public const int PoolSegment = 0x0094;

    /// <summary>
    /// <c>g_init_flag_b [0x00AC]</c> — the scheduler's THIRD arming gate and a per-window
    /// down-counter (<c>image@0x03522</c>, <c>image@0x0357A..0x03584</c>).
    /// </summary>
    public const int PendingFireCount = 0x00AC;

    /// <summary><c>g_hud_byte_flag_BA [0x00BA]</c> — lock-on: "build a candidate list" (<c>image@0x02F7F</c>).</summary>
    public const int LockOnListMode = 0x00BA;

    /// <summary><c>g_hud_byte_flag_BB [0x00BB]</c> — lock-on: "free-camera proximity pick" (<c>image@0x02F9D</c>).</summary>
    public const int LockOnFreeCameraMode = 0x00BB;

    /// <summary><c>g_scene_misc_word_BC [0x00BC]</c> — the player's LOCKED target (<c>image@0x02F61</c>).</summary>
    public const int LockedTarget = 0x00BC;

    /// <summary><c>g_hud_selected_object_key [0x00BE]</c> — the last locked target (<c>image@0x02FBE</c>).</summary>
    public const int LastLockedTarget = 0x00BE;

    /// <summary><c>g_alt_object_farptr_off [0x00C0]</c> — the player's pool object.</summary>
    public const int PlayerObject = 0x00C0;

    /// <summary><c>g_alt_object_farptr_seg [0x00C2]</c>.</summary>
    public const int PlayerObjectSegment = 0x00C2;

    /// <summary><c>g_radar_track_slot [0x00C8]</c> — set to the shot's pool object on an air-to-ground launch (<c>image@0x0255E</c>).</summary>
    public const int RadarTrackSlot = 0x00C8;

    /// <summary>
    /// <c>g_weapon_fire_toggle [0x0DF0]</c> — the alternating left/right muzzle byte, complemented
    /// after every record fill (<c>not byte ptr [0xdf0]</c> @<c>image@0x0348E</c>).
    /// </summary>
    public const int WeaponFireToggle = 0x0DF0;

    /// <summary><c>g_spawn_table_last_bc [0x0DF2]</c> — the retargeter's edge latch (<c>image@0x03595</c>).</summary>
    public const int LastRetargetLock = 0x0DF2;

    /// <summary><c>g_weapon_fire_event_active [0x32ED]</c> — the scheduler's skip-init gate.</summary>
    public const int WeaponFireEventActive = 0x32ED;

    /// <summary>The AI faction's live-shot count — <c>cmp word ptr [0xb168],0xf</c> @<c>image@0x0248A</c>.</summary>
    public const int AiLiveShots = 0xB168;

    /// <summary>The PLAYER faction's live-shot count — <c>cmp word ptr [0xb494],0xf</c> @<c>image@0x0245E</c>.</summary>
    public const int PlayerLiveShots = 0xB494;

    /// <summary>
    /// <c>g_weapon_event_fired_flag [0xB496]</c> — the AIR-TO-GROUND once-per-window gate
    /// (<c>image@0x0356E</c>); air-to-air bypasses it.
    /// </summary>
    public const int WeaponEventFired = 0xB496;

    /// <summary><c>g_weapon_event_deadline_lo [0xB498]</c> — the 32-bit fire deadline's low word.</summary>
    public const int WeaponEventDeadline = 0xB498;

    /// <summary>The 30-slot spawn table's base — <c>image@0x0249D</c>.</summary>
    public const int SpawnTable = 0xB16A;

    /// <summary>The spawn table's LAST slot, where every backwards walk starts — <c>mov di,0xb479</c> @<c>image@0x02495</c>.</summary>
    public const int SpawnTableLastSlot = 0xB479;

    /// <summary><c>g_weapon_score_accum [0xBD00]</c> — the roulette's arm-2 accumulator.</summary>
    public const int WeaponScoreAccum = 0xBD00;

    /// <summary><c>g_weapon_fire_state [0xBD02]</c> — the sustain tick's burst-mode deadline.</summary>
    public const int BurstDeadlineFrame = 0xBD02;

    /// <summary><c>[0xBD04]</c> — the "cooldown warning already shown" latch (<c>image@0x0FE65</c>).</summary>
    public const int CooldownWarningShown = 0xBD04;

    /// <summary><c>g_weapon_cooldown_target_frame [0xBD06]</c> — the death deadline the roulette arms.</summary>
    public const int DeathDeadlineFrame = 0xBD06;

    /// <summary><c>[0xBD08]</c> — the sustain tick's last-seen master frame (<c>image@0x0FC6E</c>).</summary>
    public const int SustainLastFrame = 0xBD08;

    /// <summary><c>g_hit_event_severity_level [0xBD0A]</c> — the sustain tick's 0/1/2 hit-event ladder.</summary>
    public const int HitEventSeverity = 0xBD0A;

    /// <summary><c>[0xBD0C]</c> — the mission-abort deadline the sustain tick compares (<c>image@0x0FE17</c>).</summary>
    public const int AbortDeadlineFrame = 0xBD0C;

    /// <summary>
    /// <c>g_damage_effect_weight_table_ptr [0xBD0E]</c> — the roulette's 25×2-byte effect table
    /// (<c>image@0x0F841</c>); entry <c>+0</c> = weight, <c>+1</c> = the gate byte.
    /// </summary>
    public const int EffectWeightTablePtr = 0xBD0E;

    /// <summary><c>g_cannon_hit_bitmask [0xBD12]</c> — arm 3's accumulator (<c>image@0x0F8EA</c>).</summary>
    public const int CannonHitBitmask = 0xBD12;

    /// <summary><c>[0xBD14]</c> — the sustain tick's tail throttle against <c>[0xF0D0]</c> (<c>image@0x0FF7D</c>).</summary>
    public const int SustainTailGate = 0xBD14;

    /// <summary><c>g_active_aircraft_load_ack [0xC316]</c> — non-zero suppresses both player-side ticks.</summary>
    public const int AircraftLoadAck = 0xC316;

    /// <summary>
    /// <c>[0xC31C]</c> — the DERIVED IN-FLIGHT KEY GATE (<c>= ([0xEE58]==0 &amp;&amp;
    /// [0xC32F]==0 &amp;&amp; [0xC316]&lt;2)</c>, K10 §4), and the scheduler's FIRE gate at
    /// <c>image@0x0355D</c>.  Its scanner name <c>g_video_init_done_flag</c> is a struck
    /// misnomer.
    /// </summary>
    public const int InFlightKeyGate = 0xC31C;

    /// <summary><c>g_mouse_button_state_prev [0xC31D]</c> — bit0 is the scheduler's second arming gate.</summary>
    public const int MouseButtonPrev = 0xC31D;

    /// <summary><c>g_input_mode_byte [0xC32F]</c> — non-zero suppresses the roulette and the sustain tick.</summary>
    public const int InputMode = 0xC32F;

    /// <summary><c>g_cheat_invincible [0xE46B]</c> — the roulette's first gate.</summary>
    public const int CheatInvincible = 0xE46B;

    /// <summary><c>g_cheat_unlimited_ammo [0xE46C]</c>.</summary>
    public const int CheatUnlimitedAmmo = 0xE46C;

    /// <summary><c>g_view_mode_bit0_flag [0xE46F]</c> (ex-<c>g_weapon_fire_active_flag</c>, renamed after C9 §4.2) —
    /// "the selected weapon is loaded".</summary>
    public const int WeaponLoaded = 0xE46F;

    /// <summary><c>g_joystick_x_min_i16 [0xE47C]</c> — the first of the four control-authority bounds.</summary>
    public const int ControlBoundsBase = 0xE47C;

    /// <summary><c>g_hud_current_weapon_name_ptr [0xED1E]</c> — the SELECTED WEAPON-CLASS descriptor.</summary>
    public const int SelectedWeaponClass = 0xED1E;

    /// <summary><c>g_hud_current_weapon_slot [0xED2A]</c> — the selected slot index 0..3.</summary>
    public const int SelectedWeaponSlot = 0xED2A;

    /// <summary><c>g_hud_weapon_slot_ammo [0xED2C]</c> — <c>u16[4]</c>, indexed by the slot (<c>[bx-0x12d4]</c> @<c>image@0x03449</c>).</summary>
    public const int WeaponSlotAmmo = 0xED2C;

    /// <summary><c>[0xED24]</c> — the roulette's "damaged weapon" class pointer (<c>image@0x0FA64</c>).</summary>
    public const int RouletteWeaponClass = 0xED24;

    /// <summary><c>[0xED22]</c> — the roulette's ammo-halving mode selector (<c>image@0x0FA4B</c>).</summary>
    public const int RouletteAmmoMode = 0xED22;

    /// <summary><c>g_hud_chaff_count [0xED32]</c>.</summary>
    public const int ChaffCount = 0xED32;

    /// <summary><c>g_hud_flare_count [0xED33]</c>.</summary>
    public const int FlareCount = 0xED33;

    /// <summary><c>g_gun_rounds_fired [0xED34]</c> — the accuracy FIRED counter for air-to-air shots.</summary>
    public const int GunRoundsFired = 0xED34;

    /// <summary><c>g_missiles_fired [0xED38]</c> — the accuracy FIRED counter for air-to-ground shots.</summary>
    public const int MissilesFired = 0xED38;

    /// <summary><c>g_active_statblock_ptr [0xED54]</c> — the acquiring engagement's class prototype.</summary>
    public const int ActiveStatBlock = 0xED54;

    /// <summary><c>g_engagement_player_slot_nearptr [0xED56]</c> — the SHOOTER.</summary>
    public const int ShooterObject = 0xED56;

    /// <summary><c>g_engagement_slot_flags [0xED59]</c> — bits 0..1 = the skill tier; bit7 = the ground-attack régime.</summary>
    public const int EngagementSlotFlags = 0xED59;

    /// <summary><c>g_engagement_load_state [0xED5A]</c>.</summary>
    public const int EngagementLoadState = 0xED5A;

    /// <summary>The scratch pool-object copy's position triple — <c>[0xED42]</c>.</summary>
    public const int ScratchPosition = 0xED42;

    /// <summary><c>g_engagement_type_slot_idx [0xED64]</c> — the weapon slot inside the prototype.</summary>
    public const int EngagementTypeSlot = 0xED64;

    /// <summary><c>g_acq_state [0xED65]</c>.</summary>
    public const int AcquisitionState = 0xED65;

    /// <summary><c>g_acq_current_target [0xED6F]</c>.</summary>
    public const int AcquisitionTarget = 0xED6F;

    /// <summary>The acquisition scratch's two 24-byte target copies — <c>[0xEDCE]</c>.</summary>
    public const int AcquisitionTargetCopy = 0xEDCE;

    /// <summary><c>g_active_target_table [0xEDE6]</c> — the candidate table the scorer walks DOWNWARD.</summary>
    public const int ActiveTargetTable = 0xEDE6;

    /// <summary><c>g_active_target_count [0xEE02]</c> — its length.</summary>
    public const int ActiveTargetCount = 0xEE02;

    /// <summary><c>g_object_destroyed_flag [0xEE58]</c> — the flag behind K10's shipped key-gate bug.</summary>
    public const int ObjectDestroyedFlag = 0xEE58;

    /// <summary><c>g_player_engagement_block_farptr_off (ex-g_player_object_farptr_off) [0xEF1E]</c> — the player's ENGAGEMENT-BLOCK far pointer (C1: a misnomer).</summary>
    public const int PlayerEngagementBlock = 0xEF1E;

    /// <summary><c>g_weapon_slot_body_offset_table [0xEF3C]</c> — <c>i8[4][3]</c>, stride 3 (z, x, y).</summary>
    public const int BodyOffsetTable = 0xEF3C;

    /// <summary><c>g_aircraft_master_struct [0xEF98]</c> — the master block <c>aircraft_master_damage_apply2</c> takes.</summary>
    public const int AircraftMaster = 0xEF98;

    /// <summary>
    /// <c>g_throttle_pct [0xF035]</c> = <c>s_aircraft_master + 0x9D</c> — the THROTTLE PERCENT, and
    /// what gates the engine meter&#8217;s climb arm: the engine wears while it is making power.
    /// </summary>
    /// <remarks>
    /// Renamed, byte-verified.  It is not a flag: the HUD prints
    /// it as <c>"THR: %3d%%"</c> (<c>image@0x0C728</c>), the fuel burn scales its rate by it out of
    /// 100 (<c>mov dx,[bx+0x9d] / mov bx,0x64</c>, <c>image@0x2A63C</c>) and the engine note takes
    /// it as the throttle argument (<c>image@0x29E3D</c>).  The port publishes it flight → combat in
    /// <c>MissionSession.ThrottlePercentChannel</c>.
    /// </remarks>
    public const int ThrottlePercent = 0xF035;

    /// <summary><c>g_fuel_lo [0xF058]</c> — the 32-bit fuel quantity.</summary>
    public const int Fuel = 0xF058;

    /// <summary><c>g_fuel_capacity_lb [0xF060]</c> — master <c>+0xC8</c>, the fuel capacity the leak arm doubles.</summary>
    public const int FuelCapacity = 0xF060;

    /// <summary><c>g_player_gload_q8 [0xF06E]</c> — the player's altitude word the sight-line gate compares.</summary>
    public const int PlayerAltitudeWord = 0xF06E;

    /// <summary><c>g_player_gload_int [0xF06F]</c> — the G-load the sustain tick's blackout arms read.</summary>
    public const int PlayerGLoad = 0xF06F;

    /// <summary><c>g_target_pitch_accum_lo [0xF076]</c> — the elevator-authority pair the roulette halves.</summary>
    public const int ElevatorAuthority = 0xF076;

    /// <summary><c>g_combat_hit_score_accum [0xF084]</c> — arm 8 doubles it.</summary>
    public const int HitScoreAccum = 0xF084;

    /// <summary><c>[0xF0A0]</c> / <c>g_flight_alt_high_water [0xF0A4]</c> — the sustain tail's altitude compare.</summary>
    public const int AltitudeCompareA = 0xF0A0;

    /// <summary>See <see cref="AltitudeCompareA"/>.</summary>
    public const int AltitudeCompareB = 0xF0A4;

    /// <summary><c>g_input_state_bitfield [0xF0BC]</c> — bit2 and bit6 gate two roulette arms.</summary>
    public const int InputStateBits = 0xF0BC;

    /// <summary><c>g_master_frame_counter [0xF0C8]</c>.</summary>
    public const int MasterFrameCounter = 0xF0C8;

    /// <summary><c>g_frame_count_scaled [0xF0D0]</c> — the sustain tail's throttle source.</summary>
    public const int FrameCountScaled = 0xF0D0;

    /// <summary><c>g_frame_time_accum_lo [0xF0D2]</c> — the 32-bit frame-time accumulator.</summary>
    public const int FrameTimeAccum = 0xF0D2;

    /// <summary>
    /// <c>[0xF108]</c> — the muzzle-position PATH-A gate (<c>cmp byte ptr [0xf108],1 / jb</c>
    /// @<c>image@0x03938</c>).
    /// </summary>
    /// <remarks>
    /// Its name calls this a rendering level, and the Sim law forbids naming a presentation setting
    /// in this assembly — rightly, because here it is NOT one: the byte decides whether a NON-player
    /// launcher's shot leaves the muzzle (PATH A, a rotated body-frame offset) or the aircraft's own
    /// origin (PATH B), which is a POSITION the kernel then integrates. It is a mission-load INPUT to
    /// the integer spine, read once per shot, and the port models it as such.
    /// </remarks>
    public const int MuzzleOffsetPathGate = 0xF108;

    /// <summary><c>g_briefing_difficulty_idx [0xF10E]</c> — indexes <c>g_hit_probability_by_difficulty [0x45EA]</c>.</summary>
    public const int DifficultyIndex = 0xF10E;

    /// <summary><c>g_chaff_decoy_expiry_frame [0xF110]</c>.</summary>
    public const int ChaffDecoyExpiry = 0xF110;

    /// <summary><c>g_flare_decoy_expiry_frame [0xF112]</c>.</summary>
    public const int FlareDecoyExpiry = 0xF112;

    /// <summary><c>g_view_flag_byte_cached [0xF12A]</c> — bit2 in the record fill's PATH-A gate.</summary>
    public const int ViewFlagsCached = 0xF12A;

    /// <summary>
    /// <c>g_freecam_screen_x [0xF17A]</c> — the free-camera pick's reference point, screen X.
    /// </summary>
    /// <remarks>
    /// SWAPPED by C10, from the same evidence as <see cref="TargetScreenX"/>:
    /// <c>target_select_by_freecam_proximity</c> subtracts <c>[0xF17C]</c> from the word the
    /// projector wrote through its OUT-Y pointer <c>[bp-0x0A]</c> and <c>[0xF17A]</c> from the
    /// OUT-X word <c>[bp-8]</c> (<c>image@0x032A3..0x032BD</c>; the call two instructions earlier
    /// pushes <c>&amp;[bp-8]</c> as P26's <c>arg1</c> = out-X and <c>&amp;[bp-0x0A]</c> as
    /// <c>arg0</c> = out-Y, <c>image@0x03298..0x032A0</c>).  Only the NAMES move.
    /// </remarks>
    public const int FreeCameraScreenX = 0xF17A;

    /// <summary><c>g_freecam_screen_y [0xF17C]</c>.  See <see cref="FreeCameraScreenX"/>.</summary>
    public const int FreeCameraScreenY = 0xF17C;

    /// <summary><c>[0xF17E]</c> — the roulette's arm-23 "pilot hit" latch (<c>image@0x0FBAB</c>).</summary>
    public const int PilotHitFlag = 0xF17E;

    /// <summary><c>g_target_screen_x [0xF1B2]</c> / <c>g_target_screen_y [0xF1B4]</c> (scanner names SWAPPED after C9
    /// §4.1; the trace WIRE window keeps the old name <c>g_target_screen_y</c>) — the lock-on reticle.</summary>
    /// <remarks>
    /// SWAPPED by C10 — the two constants carried the <c>g_target_screen_y</c> misnomer C9 §4.1 disproved and
    /// corrected on.  The lock-on's tail pushes <c>0xF1B2</c> as the projector's <c>[bp+6]</c> and <c>0xF1B4</c> as
    /// its <c>[bp+4]</c> (<c>image@0x030AF</c>/<c>0x030B3</c>), and the trampoline's slots are <c>[bp+6]=*out_y</c>,
    /// <c>[bp+8]=*out_x</c>, so out_x lands in <c>[0xF1B2]</c> and out_y in <c>[0xF1B4]</c>.  Independently:
    /// <c>engagement_object_frustum_qualify</c> clips the word written through the same slot as <c>[0xF1B2]</c>
    /// against <c>g_gfx_clip_x_min [0xE628]</c>/<c>[0xE62A]</c> (<c>image@0x03151..0x0315E</c>).  Only the NAMES
    /// move; every store and compare keeps its address.  The trace's WIRE window is still called
    /// <c>g_target_screen_y</c> — an on-disk identifier consumers key on,.
    /// </remarks>
    public const int TargetScreenX = 0xF1B2;

    /// <summary><c>g_target_screen_y [0xF1B4]</c>.  See <see cref="TargetScreenX"/>.</summary>
    public const int TargetScreenY = 0xF1B4;

    /// <summary><c>g_player_damage_accum [0xF1CC]</c> — the running player damage total.</summary>
    public const int PlayerDamageAccum = 0xF1CC;

    /// <summary>
    /// <c>g_fuel_leak_rate_lo [0xF1CE]</c> (ex-<c>g_combat_score_lo</c>, which was a MISNOMER;
    /// RENAMED R1) — the 32-bit FUEL-LEAK RATE arm 1 raises and <c>engagement_per_frame_tick</c>
    /// drains (<c>image@0x0F921</c>, <c>image@0x0FC9F</c>).
    /// </summary>
    public const int FuelLeakRate = 0xF1CE;

    /// <summary><c>g_target_view_active_flag [0xF1D2]</c> — the "out of fuel" message latch.</summary>
    public const int OutOfFuelShown = 0xF1D2;

    /// <summary><c>g_player_xy_vel_x_per_frame [0xF1D4]</c> / <c>_y [0xF1D6]</c> — the burst-mode shake pair.</summary>
    public const int BurstShakeX = 0xF1D4;

    /// <summary>See <see cref="BurstShakeX"/>.</summary>
    public const int BurstShakeY = 0xF1D6;

    /// <summary><c>g_engagement_pct_meter_a [0xF1D8]</c> — the AIRFRAME meter (control-authority degradation).</summary>
    public const int MeterAirframe = 0xF1D8;

    /// <summary><c>g_engagement_pct_meter_b [0xF1D9]</c> — the second meter, drained by <c>[0xBD00]</c>.</summary>
    public const int MeterB = 0xF1D9;

    /// <summary><c>g_engagement_pct_meter_c [0xF1DA]</c> — the ENGINE meter, climbed by <c>[0xF1DB]</c>.</summary>
    public const int MeterEngine = 0xF1DA;

    /// <summary><c>g_weapon_burst_state_a [0xF1DB]</c> — the engine meter's per-tick climb rate.</summary>
    public const int EngineClimbRate = 0xF1DB;

    /// <summary><c>g_weapon_burst_state_b [0xF1DC]</c>.</summary>
    public const int BurstStateB = 0xF1DC;

    /// <summary><c>g_engagement_ceiling [0xF1DE]</c> — the player's hit-point ceiling.</summary>
    public const int DamageCeiling = 0xF1DE;

    /// <summary><c>g_damage_effect_hit_counts [0xF1E0]</c> — <c>u8[24]</c>, one per roulette arm.</summary>
    public const int EffectHitCounts = 0xF1E0;

    /// <summary><c>[0xF1E1]</c> — the FUEL-LEAK flag the sustain tick gates on (<c>image@0x0FC91</c>).</summary>
    public const int FuelLeakFlag = 0xF1E1;

    /// <summary><c>[0xF1E4]</c> — the burst-mode selector 0/1/other (<c>image@0x0FEEF</c>).</summary>
    public const int BurstMode = 0xF1E4;

    /// <summary><c>[0xF1E7]</c> — the blackout-enable flag (<c>image@0x0FEC9</c>).</summary>
    public const int BlackoutEnabled = 0xF1E7;

    /// <summary><c>g_damage_effect_hit_count_22_alias (ex-g_kill_confirmed_flag) [0xF1F6]</c> — arm 22 branches on it.</summary>
    public const int DamageEffectHitCount22 = 0xF1F6;

    /// <summary><c>[0xF1F7]</c> — the sustain tail's first gate (<c>image@0x0FF87</c>).</summary>
    public const int SustainTailDisable = 0xF1F7;

    /// <summary><c>g_hit_probability_by_difficulty [0x45EA]</c> — a CONSTANT table (<see cref="ICombatStaticData"/>).</summary>
    public const int HitProbabilityTable = 0x45EA;

    /// <summary><c>g_render_arena_mode_flag [0xEA02]</c> — the lock-on's on-screen arm clears it.</summary>
    public const int RenderArenaModeFlag = 0xEA02;
}

/// <summary>
/// The K9 <c>Sim/Flight/AircraftDamage</c> channel, as a SEAM.
/// </summary>
/// <remarks>
/// <para>
/// The rule: <c>Sim/Flight/AircraftDamage</c> already reproduces every MASTER write the
/// roulette and the sustain tick make (its own doc comment names the 7 + 4 absolute call sites and
/// the 4 + 4 register-argument ones inside <c>weapon_fire_combat_loop @image@0x0F748</c> and
/// <c>engagement_per_frame_tick @image@0x0FC51</c>).  C6 therefore CONSUMES it through this
/// interface and never re-ports it — the combat kernel owns the DGROUP half of those arms, the
/// flight kernel owns the master half, and the seam is where they meet.
/// </para>
/// <para>
/// A verification implementation counts the calls and asserts their arguments; the port's real
/// runtime implementation forwards to <c>AircraftDamage.Apply</c> / <c>ApplyHitPointDamage</c> /
/// <c>HalveElevatorBounds</c> / <c>HalveAileronAuthority</c> / <c>DoubleInducedDrag</c>.
/// </para>
/// </remarks>
public interface IAircraftDamageChannel
{
    /// <summary>
    /// <c>aircraft_master_damage_apply2 @image@0x2A4F8</c> — <c>AL</c> = the damage-type bit,
    /// <c>DX</c> = the percentage, <c>BX</c> = <c>g_aircraft_master_struct [0xEF98]</c>.
    /// </summary>
    /// <param name="typeBits">The original's <c>AL</c>.</param>
    /// <param name="amountPercent">The original's <c>DX</c>.</param>
    void Apply(byte typeBits, short amountPercent);

    /// <summary>
    /// The "ELEVATORS DAMAGED" arm's two <c>sar word ptr [0xF076/0xF078],1</c>
    /// (<c>image@0x0F96C</c>, <c>image@0x0F970</c>) — <c>AircraftDamage.HalveElevatorBounds</c>.
    /// </summary>
    void HalveElevatorBounds();

    /// <summary>
    /// The "AILERONS DAMAGED" arm's four <c>sar word ptr [0xEFD0..0xEFD6],1</c>
    /// (<c>image@0x0F984..0x0F990</c>) — <c>AircraftDamage.HalveAileronAuthority</c>.
    /// </summary>
    void HalveAileronAuthority();

    /// <summary>
    /// The "WING DAMAGED" arm's <c>shl word ptr [0xF084],1</c> (<c>image@0x0F9C9</c>) —
    /// <c>AircraftDamage.DoubleInducedDrag</c>.
    /// </summary>
    void DoubleInducedDrag();
}

/// <summary>An <see cref="IAircraftDamageChannel"/> that records and does nothing else.</summary>
public sealed class CountingAircraftDamageChannel : IAircraftDamageChannel
{
    /// <summary>Every <see cref="Apply"/> call, in order, as <c>(typeBits, amountPercent)</c>.</summary>
    public List<(byte TypeBits, short AmountPercent)> Applies { get; } = [];

    /// <summary>How many times <see cref="HalveElevatorBounds"/> fired.</summary>
    public int ElevatorHalvings { get; private set; }

    /// <summary>How many times <see cref="HalveAileronAuthority"/> fired.</summary>
    public int AileronHalvings { get; private set; }

    /// <summary>How many times <see cref="DoubleInducedDrag"/> fired.</summary>
    public int DragDoublings { get; private set; }

    /// <inheritdoc/>
    public void Apply(byte typeBits, short amountPercent) => Applies.Add((typeBits, amountPercent));

    /// <inheritdoc/>
    public void HalveElevatorBounds() => ElevatorHalvings++;

    /// <inheritdoc/>
    public void HalveAileronAuthority() => AileronHalvings++;

    /// <inheritdoc/>
    public void DoubleInducedDrag() => DragDoublings++;
}

/// <summary>
/// The eight words <c>subsystem4x19_row_attach (ex-projectile_spawn) @image@0x0B467</c> receives, named in PUSH order (the callee
/// reads them in reverse).
/// </summary>
/// <param name="Selector">
/// <c>AL</c> at the first push: 4 from the slot allocator (<c>image@0x02567</c>), 1 from roulette
/// arm 2 (<c>image@0x0F8BE</c>), 2 from arms 3 and 1 (<c>image@0x0F8F4</c>, <c>image@0x0F929</c>).
/// </param>
/// <param name="Flags">The second push — 0 at every C6 site.</param>
/// <param name="Lifetime">The third — <c>0xFFFF</c> from the allocator, <c>0x14</c> from the roulette.</param>
/// <param name="Reserved">The fourth — 2 from the allocator, 0 from the roulette.</param>
/// <param name="Kind">The fifth — 1 from the allocator, 2 from the roulette.</param>
/// <param name="ObjectRef">The sixth — the shot's pool object, or the player's.</param>
/// <param name="ExtraA">The seventh — 0 at every C6 site.</param>
/// <param name="ExtraB">The eighth — 0 at every C6 site.</param>
public readonly record struct TrailSpawnRequest(
    ushort Selector,
    ushort Flags,
    ushort Lifetime,
    ushort Reserved,
    ushort Kind,
    ushort ObjectRef,
    ushort ExtraA,
    ushort ExtraB);

/// <summary>
/// The out-of-subsystem callees the PLAYER side makes that are not combat state: geometry helpers
/// the renderer owns, the film recorder, the sprite and effect queues, sound, cockpit text and the
/// advisor.
/// </summary>
/// <remarks>
/// None of them feeds a combat decision back in EXCEPT the three that return a value, which are
/// declared separately so a verification can oracle-feed exactly those
/// (<see cref="ProjectPositionToScreen"/>, <see cref="RotateBodyOffset"/>,
/// <see cref="SpawnCountermeasureCloud"/>).
/// </remarks>
public interface IPlayerCombatEvents
{
    /// <summary>
    /// <c>spawn_record_position_fill @image@0x2367F</c> (<c>image@0x039A5</c>) — rotate the three
    /// body-frame muzzle offsets by the launcher's attitude and add its world position.
    /// </summary>
    /// <param name="launcherObject">The original's <c>[bp-0x0A]</c> — the launcher's pool object.</param>
    /// <param name="offsetZ">The first pushed offset (<c>±table[0] &lt;&lt; 2</c>).</param>
    /// <param name="offsetX">The second (<c>table[1] &lt;&lt; 2</c>).</param>
    /// <param name="offsetY">The third (<c>table[2] &lt;&lt; 2</c>).</param>
    /// <returns>The muzzle position the routine writes into the caller's 12-byte block.</returns>
    CombatPosition RotateBodyOffset(
        ushort launcherObject, short offsetZ, short offsetX, short offsetY);

    /// <summary>
    /// <c>object_range_from_view_anchor @image@0x24448</c> (<c>image@0x0395B</c>) — the range test
    /// that decides PATH A vs PATH B in the record fill.
    /// </summary>
    /// <param name="positionRef">The pushed <c>si + 6</c> far pointer's offset half.</param>
    /// <returns>The original's <c>AX</c>, compared against <c>0x07D0</c>.</returns>
    ushort RangeFromViewAnchor(ushort positionRef);

    /// <summary>
    /// <c>object_screen_pos_project @image@0x036EA</c> — the lock-on's projection of one RENDER-LIST
    /// NODE to screen coordinates (three near-pointer arguments in push order: node,
    /// <c>&amp;primary</c>, <c>&amp;secondary</c>).
    /// </summary>
    /// <param name="objectRef">The RENDER-LIST NODE's DGROUP near offset.</param>
    /// <returns>
    /// The projected pair, in the machine's own out-pointer order: <c>Y</c> is the word written
    /// through <c>[bp+6]</c> (the FIRST out-pointer, the one the lock-on's tail sends to
    /// <c>[0xF1B2]</c>) and <c>X</c> the word written through <c>[bp+4]</c> (<c>[0xF1B4]</c>).
    /// </returns>
    /// <remarks>
    /// Two corrections to the record, both byte-derived:
    /// <list type="number">
    ///   <item><description>
    ///     The argument is a RENDER-LIST NODE, not a pool object: <c>engagement_object_frustum_qualify</c>
    ///     pushes <c>DI</c> (<c>image@0x03145</c>) and the lock-on's tail pushes <c>SI</c>
    ///     (<c>image@0x030AE</c>), both DGROUP node pointers, and the routine reads <c>[bx+6]</c> /
    ///     <c>[bx+8]</c> / <c>[bx+0xA]</c> with the DEFAULT segment (<c>image@0x036F3</c>) — DS, not
    ///     the pool segment.  It also promotes each word into the HIGH half of an <c>i32</c>
    ///     (<c>mov word [bp-0xC],0 / mov [bp-0xA],ax</c>), i.e. <c>value &lt;&lt; 16</c>, not a sign
    ///     extension.
    ///   </description></item>
    ///   <item><description>
    ///     The pair is <c>(screen X, screen Y)</c> in that order.  The tuple's components were RENAMED
    ///     to <c>(ScreenX, ScreenY)</c> by C10 — the order and every comparison are
    ///     unchanged, only the inherited misnomer is gone.  Chain: the projector pushes
    ///     <c>(&amp;vec, [bp+6], [bp+4])</c> @<c>image@0x03714..0x0371B</c> into
    ///     <c>gfx_perspective_project_trampoline</c> whose slots are
    ///     <c>([bp+6]=*out_y, [bp+8]=*out_x, [bp+0xA]=slot)</c>, which forwards to
    ///     <c>gfx_trig_lookup_wrapper @image@0x1934E</c> (<c>*[bp+6]=AX</c>, <c>*[bp+8]=CX</c>).
    ///     Independent confirmation: <c>engagement_object_frustum_qualify</c> compares the word from
    ///     <c>[bp+6]</c> against <c>g_gfx_clip_x_min [0xE628]</c>/<c>[0xE62A]</c> and the word from
    ///     <c>[bp+4]</c> against <c>[0xE62C]</c>/<c>[0xE62E]</c> (<c>image@0x03151..0x0316D</c>).
    ///     The port's ordering is unchanged and remains behaviourally correct — the selectors compare
    ///     this member's <c>Y</c> FIRST, which is the machine's primary key (screen X) — only the
    ///     NAMES are inherited from the <c>g_target_screen_y [0xF1B2]</c> misnomer.
    ///   </description></item>
    /// </list>
    /// </remarks>
    (short ScreenX, short ScreenY) ProjectPositionToScreen(ushort objectRef);

    /// <summary>
    /// <c>image@0x0307F..0x03091</c> / <c>image@0x02FD5..0x02FED</c> — walk the RENDER LIST for the
    /// node whose <c>+0x14</c> holds <paramref name="objectRef"/>.
    /// </summary>
    /// <param name="renderListHead">The pushed <c>[bp+6]</c>, i.e. <c>g_engagement_object_list_head [0xE90A]</c>.</param>
    /// <param name="objectRef">The pool near offset to look for (<c>g_scene_misc_word_BC [0x00BC]</c>).</param>
    /// <returns>The node's DGROUP near offset, or 0 when the list does not carry the object.</returns>
    /// <remarks>
    /// The walk is over DGROUP, NOT the pool arena — <c>mov si,[bp+6] / cmp word [si+0x14],ax / mov si,[si+2]</c>
    /// carry no segment prefix, so they run on DS (<c>platform</c>: the 8086 ModRM default segment is DS for a SI
    /// base).  Only the OBJECT the node names lives in the pool segment (<c>mov cx,[0x94] / mov es,cx / test byte
    /// es:[bx+2],1</c> @<c>image@0x02F6F</c>). The nodes are the renderer's sorted display-list records
    /// (<c>render_slot_sorted_list_merge @image@0x144BE</c> writes the head), 22 bytes apart and in NO combat-trace
    /// window — which is why this is a SEAM and not ported state.
    /// </remarks>
    ushort FindInRenderList(ushort renderListHead, ushort objectRef);

    /// <summary>
    /// <c>node[+0x14]</c> — the pool object a render-list node names (<c>image@0x03001</c>,
    /// <c>image@0x0325A</c>, <c>image@0x0335F</c>).
    /// </summary>
    /// <param name="node">The node's DGROUP near offset.</param>
    /// <returns>The object's pool near offset.</returns>
    ushort RenderListNodeObject(ushort node);

    /// <summary>
    /// <c>or byte es:[bx+2],8</c> @<c>image@0x14AE5</c> — the RENDER's own write into the pool: every
    /// object that gets a display-list node this frame has its flag word's bit 3 SET.
    /// </summary>
    /// <param name="renderListHead">The list head, <c>g_engagement_object_list_head [0xE90A]</c>.</param>
    /// <param name="objectRef">The pool near offset to ask about.</param>
    /// <returns><see langword="true"/> when the render list carries a node for the object.</returns>
    /// <remarks>
    /// <para>
    /// Bit 3 is a PER-FRAME RENDER FLAG, not a property of the object: an image-wide operand sweep
    /// finds exactly two writers that SET it — <c>image@0x14AE5</c> (the node-append arm of the
    /// render-slot filler) and <c>image@0x15981</c> — and three that CLEAR it
    /// (<c>and byte es:[bx+2],0xF7</c> at <c>image@0x14992</c>, <c>image@0x14C1B</c> and
    /// <c>image@0x15936</c>), all five inside <c>polygon_fill_mesh_render_setup</c>'s subtree.
    /// </para>
    /// <para>
    /// Its ONE combat reader is the lock-on's RE-ADOPT arm (<c>test byte es:[bx+2],8 / jne</c>
    /// @<c>image@0x03070</c>), which runs from inside that same render call — AFTER the flag has been
    /// refreshed.  A port seeded at CS8 holds the PREVIOUS frame's value, which is what made
    /// <c>session_20260830_130115_det</c> step 63,168 a declared residual for C9 (the object
    /// <c>0x4CAE</c> read <c>0x6F01</c> at CS8 and <c>0x6F09</c> at CS9 — it had gained a node).  With
    /// the render-slot block on the stage record (trace format v1.6 ask G1) the port can answer the
    /// question instead of declaring it.
    /// </para>
    /// <para>
    /// MEASURED over the six C6 pool windows: <b>130,856 of 130,856</b> node objects carry bit 3 at CS9,
    /// and 129,127 of them already carried it at CS8 — so the flag is exactly "this object was drawn this
    /// frame".  The port does NOT write the arena byte: the same render pass also moves bit 7 on 75,429
    /// of those objects and bit 6 on 21, and the port has no way to know those, so it answers the
    /// QUESTION and leaves the byte to the external channel.
    /// </para>
    /// </remarks>
    bool RenderPhaseFlaggedObject(ushort renderListHead, ushort objectRef);

    /// <summary>
    /// <c>qualifying_object_list_build @image@0x030C0</c> (<c>image@0x02F90</c>) — fills the
    /// lock-on's 32-entry near-pointer candidate list.
    /// </summary>
    /// <param name="listArgument">The caller's <c>[bp+6]</c> — the render list head.</param>
    /// <param name="candidates">The 32-word buffer the routine fills.</param>
    /// <returns>How many entries it wrote (the original's <c>AX</c>).</returns>
    int BuildQualifyingObjectList(ushort listArgument, Span<ushort> candidates);

    /// <summary>
    /// <c>engagement_object_frustum_qualify @image@0x03105</c> (<c>image@0x03094</c>) — the
    /// still-visible test that keeps an existing lock.
    /// </summary>
    /// <param name="listEntry">The pushed list node.</param>
    /// <returns>The original's <c>AL</c>.</returns>
    bool ObjectStillQualifies(ushort listEntry);

    /// <summary>
    /// <c>radar_closest_approach_compute @image@0x0A4D3</c> (<c>image@0x02FC1</c>) — the radar
    /// recompute the lock-on runs after a lock changes.
    /// </summary>
    void RecomputeRadarClosestApproach();

    /// <summary>
    /// <c>countermeasure_cloud_spawn @image@0x0ABC3</c> (<c>image@0x0AF97</c> / <c>image@0x0AFFB</c>)
    /// — allocates the chaff (kind 1) or flare (kind 2) cloud.
    /// </summary>
    /// <param name="kind">1 = chaff, 2 = flare.</param>
    /// <param name="heading">The wrapped launch heading.</param>
    /// <param name="origin">The pushed far pointer's offset half — the player object <c>+6</c>.</param>
    /// <returns>The cloud's pool near offset, or 0 when the pool is full.</returns>
    ushort SpawnCountermeasureCloud(byte kind, short heading, ushort origin);

    /// <summary>
    /// <c>world_grid_frustum_query_and_select @image@0x28540</c> as the SCORER calls it
    /// (<c>image@0x07C2E</c>, an eleven-word line-of-sight probe along the shot vector).
    /// </summary>
    /// <param name="fromPosition">The pushed <c>&amp;[0xED42]</c> — the shooter's scratch position.</param>
    /// <param name="toPositionRef">The pushed candidate <c>+6</c>.</param>
    /// <param name="shooter">The pushed <c>[0xED56]</c>.</param>
    /// <returns>The original's <c>AX</c>: non-zero = the line is BLOCKED.</returns>
    ushort ScorerLineOfSightQuery(CombatPosition fromPosition, ushort toPositionRef, ushort shooter);

    // `angle_3d_orientation_combine @image@0x1C122`, the fourth case of
    // `target_angle_wrap_and_combine @image@0x07E96`, WAS a seam here. *(REMOVED by C8 — the
    // routine and its `angle_mat3_pair_build @image@0x1BFA4` helper are ported at `Sim/Geometry/`,
    // so `TargetSelectionCluster.WeaponAimAngles` computes all four cases itself and there is
    // nothing left to ask an outside actor for.  The driver's 1,097-byte attribution went with it;
    // `PlayerCombatCensus.GuidanceMatrixCases` still counts how often the case fires.

    /// <summary>
    /// <c>shot_trajectory_proximity_accum @image@0x08510</c> — the proximity-fuze accumulator the
    /// guidance solution runs before its lead step (<c>image@0x08483</c>).
    /// </summary>
    /// <param name="mode">The original's <c>AL</c> — always 1 at this site.</param>
    void AccumulateShotTrajectory(byte mode);

    /// <summary>
    /// <c>subsystem4x19_row_attach @image@0x0B467</c> — the 4×19 trail-row allocator the slot allocator
    /// (<c>image@0x02580</c>) and three roulette arms (<c>image@0x0F8D7</c>, <c>image@0x0F90D</c>,
    /// <c>image@0x0F942</c>) call.
    /// </summary>
    /// <param name="request">The eight pushed words, in push order.</param>
    void SpawnTrail(in TrailSpawnRequest request);

    /// <summary>
    /// <c>pending_sprite_slot_enqueue @image@0x14484</c> (<c>image@0x025E4</c>) then
    /// <c>pixel_obj_register_all_records @image@0x0D518</c> (<c>image@0x025EE</c>).
    /// </summary>
    /// <param name="objectRef">The spawned shot's pool object.</param>
    void RegisterShotSprite(ushort objectRef);

    /// <summary>
    /// <c>film_obj_state_capture @image@0x30AF8</c> — the tag-0x81 ENTER record every spawn writes
    /// unconditionally (<c>image@0x025F9</c>).
    /// </summary>
    /// <param name="objectRef">The spawned shot's pool object.</param>
    /// <param name="airToGround">The original's <c>AL</c> — the <c>[bp-4]</c> air-to-ground flag.</param>
    void FilmSpawnRecord(ushort objectRef, bool airToGround);

    /// <summary>
    /// <c>sfx_weapon_type_tone_dispatch @image@0x29A79</c> (<c>image@0x0350A</c>) — the fire tone.
    /// </summary>
    /// <param name="weaponClassRef">The original's <c>BX</c>.</param>
    void PlayFireTone(ushort weaponClassRef);

    /// <summary>
    /// <c>sfx_countermeasure_deploy_sound @image@0x29B29</c> (<c>image@0x0AFC5</c> / <c>image@0x0B029</c>).
    /// </summary>
    void PlayCountermeasureSound();

    /// <summary>
    /// <c>show_cockpit_text_string @image@0x0CC5B</c> — one cockpit message, given as the far
    /// pointer's OFFSET half (the segment is always <c>0x453D</c> at every C6 site).
    /// </summary>
    /// <param name="stringOffset">The pushed offset.</param>
    void ShowCockpitText(ushort stringOffset);

    /// <summary>
    /// <c>ai_advisor_message_dispatch @image@0x0EFF1</c> — <c>AL</c> = the advisory code
    /// (3 from the blocked air-to-ground sight line, 2 from the sustain tick's abort warning).
    /// </summary>
    /// <param name="code">The original's <c>AL</c>.</param>
    void AdvisorMessage(byte code);

    /// <summary>
    /// <c>combat_event_notify @image@0x0FFCA</c> — <c>AL</c> = the event code
    /// (0/1/2/3 across the roulette and the sustain tick).
    /// </summary>
    /// <param name="code">The original's <c>AL</c>.</param>
    void CombatEventNotify(byte code);

    /// <summary>
    /// <c>schedule_weapon_timer @image@0x1003C</c> — <c>AX</c> = a frame count
    /// (<c>image@0x0F8AA</c>, <c>image@0x0FBC2</c>, <c>image@0x0FDB7</c>).
    /// </summary>
    /// <param name="frames">The original's <c>AX</c>.</param>
    void ScheduleWeaponTimer(short frames);

    /// <summary>
    /// <c>damage_indicator_pos_enqueue @image@0x0CD26</c> (<c>image@0x0FB35</c>, arm 19).
    /// </summary>
    void EnqueueDamageIndicator();

    /// <summary>
    /// <c>radar_mode_toggle_with_sweep_reset @image@0x0E1C5</c> (<c>image@0x0FB35</c>, arm 19's
    /// second call).
    /// </summary>
    void ToggleRadarMode();

    /// <summary>
    /// <c>weapon_slot_name_refresh @image@0x033F8</c> (<c>image@0x0FA91</c>, arms 12..14).
    /// </summary>
    void RefreshWeaponSlotName();

    /// <summary>
    /// <c>timer_blink_b_thresh_advance @image@0x2D212</c> (<c>image@0x0FFBE</c>, the sustain tail).
    /// </summary>
    /// <param name="threshold">The pushed word — always <c>0x80</c> at this site.</param>
    void AdvanceBlinkTimer(short threshold);

    /// <summary>
    /// <c>scene_setup_or_camera_reset @image@0x22643</c> — C5's door, reached from the sustain tick
    /// with <c>AL</c> = 0, 1 or 2 (<c>image@0x0FE24</c>, <c>image@0x0FEEA</c>, <c>image@0x0FE4C</c>).
    /// </summary>
    /// <param name="mode">The original's <c>AL</c>.</param>
    void SceneReset(byte mode);

    /// <summary>
    /// <c>destroyed_flag_set_and_frame_deadline_arm @image@0x1004A</c> (<c>image@0x0FE1D</c>).
    /// </summary>
    void ArmDestroyedFlagDeadline();

    /// <summary>
    /// <c>engagement_slot_fsm_advance @image@0x04F64</c> with <c>AL = 7</c> — the slot allocator's
    /// tail (<c>image@0x0261B</c>), which an earlier pass measured as door <c>0x0261B</c> ×2.
    /// </summary>
    /// <param name="blockRef">The original's <c>BX</c> — the target's engagement block.</param>
    void VmAdvanceMode7(ushort blockRef);
}

/// <summary>An <see cref="IPlayerCombatEvents"/> that records call counts and answers zero.</summary>
public class CountingPlayerCombatEvents : IPlayerCombatEvents
{
    /// <summary>Every cockpit-text offset, in order.</summary>
    public List<ushort> CockpitText { get; } = [];

    /// <summary>Every advisor code, in order.</summary>
    public List<byte> Advisories { get; } = [];

    /// <summary>Every combat-event code, in order.</summary>
    public List<byte> CombatEvents { get; } = [];

    /// <summary>Every scene-reset mode, in order.</summary>
    public List<byte> SceneResets { get; } = [];

    /// <summary>Every weapon-timer schedule, in order.</summary>
    public List<short> WeaponTimers { get; } = [];

    /// <summary>Every trail spawn, in order.</summary>
    public List<TrailSpawnRequest> Trails { get; } = [];

    /// <summary>Every film spawn record, in order.</summary>
    public List<(ushort ObjectRef, bool AirToGround)> FilmRecords { get; } = [];

    /// <summary>Every fire tone, in order.</summary>
    public List<ushort> FireTones { get; } = [];

    /// <summary>Every mode-7 VM advance, in order.</summary>
    public List<ushort> VmMode7 { get; } = [];

    /// <summary>Every countermeasure cloud request, in order.</summary>
    public List<(byte Kind, short Heading, ushort Origin)> CountermeasureClouds { get; } = [];

    /// <summary>Every registered shot sprite, in order.</summary>
    public List<ushort> ShotSprites { get; } = [];

    /// <summary>How many times each of the remaining void hooks fired.</summary>
    public int Radars { get; private set; }

    /// <summary>How many countermeasure sounds played.</summary>
    public int CountermeasureSounds { get; private set; }

    /// <summary>How many damage indicators were enqueued.</summary>
    public int DamageIndicators { get; private set; }

    /// <summary>How many radar-mode toggles fired.</summary>
    public int RadarToggles { get; private set; }

    /// <summary>How many weapon-slot-name refreshes fired.</summary>
    public int SlotNameRefreshes { get; private set; }

    /// <summary>How many blink-timer advances fired.</summary>
    public int BlinkTimers { get; private set; }

    /// <summary>How many destroyed-flag deadline arms fired.</summary>
    public int DestroyedFlagArms { get; private set; }

    /// <summary>How many line-of-sight probes the scorer made.</summary>
    public int LineOfSightQueries { get; private set; }

    /// <summary>How many trajectory accumulations the guidance solution ran.</summary>
    public int TrajectoryAccumulations { get; private set; }

    /// <inheritdoc/>
    public virtual CombatPosition RotateBodyOffset(
        ushort launcherObject, short offsetZ, short offsetX, short offsetY) => default;

    /// <inheritdoc/>
    public virtual ushort RangeFromViewAnchor(ushort positionRef) => 0;

    /// <inheritdoc/>
    public virtual (short ScreenX, short ScreenY) ProjectPositionToScreen(ushort objectRef) => (0, 0);

    /// <inheritdoc/>
    public virtual ushort FindInRenderList(ushort renderListHead, ushort objectRef) => 0;

    /// <inheritdoc/>
    public virtual ushort RenderListNodeObject(ushort node) => 0;

    /// <inheritdoc/>
    public virtual bool RenderPhaseFlaggedObject(ushort renderListHead, ushort objectRef) => false;

    /// <inheritdoc/>
    public virtual int BuildQualifyingObjectList(ushort listArgument, Span<ushort> candidates) => 0;

    /// <inheritdoc/>
    public virtual bool ObjectStillQualifies(ushort listEntry) => false;

    /// <inheritdoc/>
    public void RecomputeRadarClosestApproach() => Radars++;

    /// <inheritdoc/>
    public virtual ushort SpawnCountermeasureCloud(byte kind, short heading, ushort origin)
    {
        CountermeasureClouds.Add((kind, heading, origin));
        return 0;
    }

    /// <inheritdoc/>
    public virtual ushort ScorerLineOfSightQuery(
        CombatPosition fromPosition, ushort toPositionRef, ushort shooter)
    {
        LineOfSightQueries++;
        return 0;
    }

    /// <inheritdoc/>
    public virtual void AccumulateShotTrajectory(byte mode) => TrajectoryAccumulations++;

    /// <inheritdoc/>
    public void SpawnTrail(in TrailSpawnRequest request) => Trails.Add(request);

    /// <inheritdoc/>
    public void RegisterShotSprite(ushort objectRef) => ShotSprites.Add(objectRef);

    /// <inheritdoc/>
    public void FilmSpawnRecord(ushort objectRef, bool airToGround) =>
        FilmRecords.Add((objectRef, airToGround));

    /// <inheritdoc/>
    public void PlayFireTone(ushort weaponClassRef) => FireTones.Add(weaponClassRef);

    /// <inheritdoc/>
    public void PlayCountermeasureSound() => CountermeasureSounds++;

    /// <inheritdoc/>
    public void ShowCockpitText(ushort stringOffset) => CockpitText.Add(stringOffset);

    /// <inheritdoc/>
    public void AdvisorMessage(byte code) => Advisories.Add(code);

    /// <inheritdoc/>
    public void CombatEventNotify(byte code) => CombatEvents.Add(code);

    /// <inheritdoc/>
    public void ScheduleWeaponTimer(short frames) => WeaponTimers.Add(frames);

    /// <inheritdoc/>
    public void EnqueueDamageIndicator() => DamageIndicators++;

    /// <inheritdoc/>
    public void ToggleRadarMode() => RadarToggles++;

    /// <inheritdoc/>
    public void RefreshWeaponSlotName() => SlotNameRefreshes++;

    /// <inheritdoc/>
    public void AdvanceBlinkTimer(short threshold) => BlinkTimers++;

    /// <inheritdoc/>
    public void SceneReset(byte mode) => SceneResets.Add(mode);

    /// <inheritdoc/>
    public void ArmDestroyedFlagDeadline() => DestroyedFlagArms++;

    /// <inheritdoc/>
    public void VmAdvanceMode7(ushort blockRef) => VmMode7.Add(blockRef);
}

/// <summary>
/// Everything the PLAYER-side kernel needs: the mutable combat state, the constant tables, the RNG
/// and the four seams.
/// </summary>
public sealed class PlayerCombatContext
{
    /// <summary>The DGROUP combat register file.</summary>
    public required CombatRegisters Registers { get; init; }

    /// <summary>The pool arena.</summary>
    public required PoolArena Arena { get; init; }

    /// <summary>The constant DGROUP tables.</summary>
    public required ICombatStaticData StaticData { get; init; }

    /// <summary>The Sim LFSR — every draw advances <c>[0x07A8]</c> inside <see cref="Registers"/>.</summary>
    public required ICombatRandom Random { get; init; }

    /// <summary>The K9 flight-kernel damage channel.</summary>
    public required IAircraftDamageChannel Damage { get; init; }

    /// <summary>The out-of-subsystem callees.</summary>
    public required IPlayerCombatEvents Events { get; init; }

    /// <summary>The per-run arm census — instrumentation only, never a decision input.</summary>
    public PlayerCombatCensus Census { get; } = new();
}

/// <summary>Which arms the PLAYER-side kernel reached.  Instrumentation only.</summary>
public sealed class PlayerCombatCensus
{
    /// <summary>How many times the scheduler ran.</summary>
    public int SchedulerCalls { get; internal set; }

    /// <summary>Scheduler calls that took the SKIP-INIT arm (<c>image@0x03529</c>).</summary>
    public int SchedulerSkipInit { get; internal set; }

    /// <summary>Fire windows the scheduler opened (<c>image@0x03553</c>).</summary>
    public int SchedulerWindows { get; internal set; }

    /// <summary>Windows that reached <c>weapon_fire_check_and_spawn</c>.</summary>
    public int SchedulerFires { get; internal set; }

    /// <summary>Windows the <c>[0xC31C]</c> in-flight key gate suppressed (K10's shipped-bug path).</summary>
    public int SchedulerKeyGateBlocked { get; internal set; }

    /// <summary>Windows the air-to-ground once-per-window gate suppressed.</summary>
    public int SchedulerA2gGateBlocked { get; internal set; }

    /// <summary>Calls into <c>weapon_fire_check_and_spawn</c>.</summary>
    public int FireChecks { get; internal set; }

    /// <summary>Fire checks that returned early with no weapon selected.</summary>
    public int FireChecksNoWeapon { get; internal set; }

    /// <summary>Fire checks that returned early because the ammunition had run out.</summary>
    public int FireChecksNoAmmo { get; internal set; }

    /// <summary>Fire checks that cleared the lock because the weapon was not loaded.</summary>
    public int FireChecksLockCleared { get; internal set; }

    /// <summary>Fire checks that ran the air-to-ground sight-line test.</summary>
    public int SightLineChecks { get; internal set; }

    /// <summary>Sight-line tests that FAILED (target dropped + advisory 3).</summary>
    public int SightLineBlocked { get; internal set; }

    /// <summary>Record fills that took PATH A (the rotated muzzle offset).</summary>
    public int RecordFillPathA { get; internal set; }

    /// <summary>Record fills that took PATH B (the launcher's own position).</summary>
    public int RecordFillPathB { get; internal set; }

    /// <summary>Record fills that negated the z offset.</summary>
    public int RecordFillNegatedZ { get; internal set; }

    /// <summary>Calls into <c>combat_spawn_slot_alloc_and_film_record</c>.</summary>
    public int SlotAllocations { get; internal set; }

    /// <summary>Allocations from the PLAYER door.</summary>
    public int SlotAllocationsPlayer { get; internal set; }

    /// <summary>Allocations from the AI door.</summary>
    public int SlotAllocationsAi { get; internal set; }

    /// <summary>Allocations refused by the 15-per-faction gate.</summary>
    public int SlotAllocationsFactionFull { get; internal set; }

    /// <summary>Allocations refused because the table had no free slot.</summary>
    public int SlotAllocationsTableFull { get; internal set; }

    /// <summary>Allocations that took the air-to-ground arm.</summary>
    public int SlotAllocationsAirToGround { get; internal set; }

    /// <summary>Allocations whose class had the guided bit (<c>+0x24 &amp; 0x10</c>).</summary>
    public int SlotAllocationsGuided { get; internal set; }

    /// <summary>Allocations that took the "fire flag 0" arm (<c>image@0x02547</c>).</summary>
    public int SlotAllocationsUnfired { get; internal set; }

    /// <summary>Allocations that bumped <c>[0xF0C2]</c> (a shot AT the player from an aircraft class).</summary>
    public int SlotAllocationsAtPlayer { get; internal set; }

    /// <summary>Allocations that ran the mode-7 VM tail.</summary>
    public int SlotAllocationsVmTail { get; internal set; }

    /// <summary>Calls into <c>weapon_fire_combat_loop</c> (the roulette).</summary>
    public int RouletteCalls { get; internal set; }

    /// <summary>Roulette calls suppressed by the three entry gates.</summary>
    public int RouletteSuppressed { get; internal set; }

    /// <summary>Roulette calls that reached the fatal arm (23).</summary>
    public int RouletteFatal { get; internal set; }

    /// <summary>Roulette calls that dispatched an effect arm.</summary>
    public int RouletteEffects { get; internal set; }

    /// <summary>Roulette calls that fell out before any effect.</summary>
    public int RouletteNoEffect { get; internal set; }

    /// <summary>Roulette calls whose weighted draw loop (up to 10 tries) found an entry.</summary>
    public int RouletteWeightedHits { get; internal set; }

    /// <summary>Roulette calls that fell through to the linear scan (up to 25 entries).</summary>
    public int RouletteLinearScans { get; internal set; }

    /// <summary>Per-arm dispatch counts, indexed by arm 0..23.</summary>
    public int[] RouletteArms { get; } = new int[24];

    /// <summary>Calls into <c>engagement_per_frame_tick</c>.</summary>
    public int SustainCalls { get; internal set; }

    /// <summary>Sustain calls suppressed by the two entry gates.</summary>
    public int SustainSuppressed { get; internal set; }

    /// <summary>Sustain calls that took the ODD-FRAME body.</summary>
    public int SustainOddFrames { get; internal set; }

    /// <summary>Odd frames that drained leaking fuel.</summary>
    public int SustainFuelDrains { get; internal set; }

    /// <summary>Odd frames that hit the "out of fuel" message.</summary>
    public int SustainOutOfFuel { get; internal set; }

    /// <summary>Odd frames that ran the second meter's decay.</summary>
    public int SustainMeterBDecays { get; internal set; }

    /// <summary>Odd frames that ran the airframe meter's decay to zero (the 3× repair).</summary>
    public int SustainAirframeRepairs { get; internal set; }

    /// <summary>Odd frames that stepped the engine meter.</summary>
    public int SustainEngineSteps { get; internal set; }

    /// <summary>Odd frames that saturated the engine meter at 100.</summary>
    public int SustainEngineFailures { get; internal set; }

    /// <summary>Odd frames that degraded the four control-authority bounds.</summary>
    public int SustainControlDegradations { get; internal set; }

    /// <summary>Odd frames that armed the mission-abort path.</summary>
    public int SustainAbortArms { get; internal set; }

    /// <summary>Odd frames that reached the death deadline.</summary>
    public int SustainDeaths { get; internal set; }

    /// <summary>Odd frames that showed the cooldown warning.</summary>
    public int SustainCooldownWarnings { get; internal set; }

    /// <summary>Odd frames that ran a hit-event severity arm.</summary>
    public int SustainHitEvents { get; internal set; }

    /// <summary>Odd frames that ran a blackout roll.</summary>
    public int SustainBlackouts { get; internal set; }

    /// <summary>Frames that ran the burst-mode shake.</summary>
    public int SustainBurstShakes { get; internal set; }

    /// <summary>Frames that reached the tail's blink-timer advance.</summary>
    public int SustainTailAdvances { get; internal set; }

    /// <summary>Calls into <c>target_acquisition_state_machine_step</c>.</summary>
    public int LockOnCalls { get; internal set; }

    /// <summary>Lock-on calls that kept an existing lock.</summary>
    public int LockOnKept { get; internal set; }

    /// <summary>Lock-on calls that cleared the lock.</summary>
    public int LockOnCleared { get; internal set; }

    /// <summary>Lock-on calls that ran the first-valid selector.</summary>
    public int LockOnFirstValid { get; internal set; }

    /// <summary>Lock-on calls that ran the free-camera selector.</summary>
    public int LockOnFreeCamera { get; internal set; }

    /// <summary>
    /// Reticle stores the host's register file had no home for — the projection's SECOND result at
    /// <c>[0xF1B4]</c> (<c>image@0x030B7</c>), which the combat trace's two-byte
    /// <c>g_target_screen_y [0xF1B2]</c> window does not reach.  A full-DGROUP host never increments
    /// this; a verification host counts every skip so the format gap stays visible.
    /// </summary>
    public int ReticleSecondaryUnwindowed { get; internal set; }

    /// <summary>Lock-on calls that ran the reselect-from-list selector.</summary>
    public int LockOnReselect { get; internal set; }

    /// <summary>Lock-on calls that reached the on-screen reticle arm.</summary>
    public int LockOnReticle { get; internal set; }

    /// <summary>Calls into <c>spawn_table_retarget_on_lock_change</c>.</summary>
    public int RetargetCalls { get; internal set; }

    /// <summary>Retarget calls that actually walked the table (the lock CHANGED).</summary>
    public int RetargetWalks { get; internal set; }

    /// <summary>Spawn slots the retargeter re-pointed at the new lock.</summary>
    public int RetargetRetargeted { get; internal set; }

    /// <summary>Spawn slots the retargeter cleared.</summary>
    public int RetargetCleared { get; internal set; }

    /// <summary>
    /// Free spawn slots whose null weapon-class pointer the retargeter dereferenced (<c>test
    /// byte ptr [di+0x24],0x10</c> with <c>di = 0</c> — the shipped null-deref quirk).
    /// </summary>
    public int RetargetNullClassReads { get; internal set; }

    /// <summary>Calls into <c>combat_target_score_and_fire</c>.</summary>
    public int ScorerCalls { get; internal set; }

    /// <summary>Scorer calls that kept the existing target (<c>image@0x0796F</c>).</summary>
    public int ScorerKeptTarget { get; internal set; }

    /// <summary>Scorer calls that took the single-candidate arm (<c>[0xEE02] == 1</c>).</summary>
    public int ScorerSingleCandidate { get; internal set; }

    /// <summary>Scorer calls that walked the candidate table.</summary>
    public int ScorerTableWalks { get; internal set; }

    /// <summary>Candidates the scorer scored.</summary>
    public int ScorerCandidatesScored { get; internal set; }

    /// <summary>Candidates the scorer penalised with the <c>0x1B58</c> term.</summary>
    public int ScorerPenalised { get; internal set; }

    /// <summary>Scorer calls that returned a target.</summary>
    public int ScorerSelected { get; internal set; }

    /// <summary>Scorer calls that drew a re-scan cooldown (the "nothing found" arm).</summary>
    public int ScorerCooldownDraws { get; internal set; }

    /// <summary>Scorer calls that ran the sight-line gate.</summary>
    public int ScorerSightLines { get; internal set; }

    /// <summary>Scorer calls that ran the line-of-sight grid probe.</summary>
    public int ScorerGridProbes { get; internal set; }

    /// <summary>Calls into <c>combat_target_range_and_angle_qualify</c>.</summary>
    public int QualifyCalls { get; internal set; }

    /// <summary>Qualify calls that passed.</summary>
    public int QualifyPassed { get; internal set; }

    /// <summary>
    /// Diagnostic only: the image address of the gate that rejected the last
    /// <c>combat_target_range_and_angle_qualify</c> call, so a verification can name the arm rather
    /// than only report a divergence.
    /// </summary>
    public string? LastQualifyReject { get; internal set; }

    /// <summary>Calls into <c>target_sight_line_check</c>.</summary>
    public int SightLineCalls { get; internal set; }

    /// <summary>Sight-line calls short-circuited by the <c>0x5A</c> class tag.</summary>
    public int SightLineTagShortCircuits { get; internal set; }

    /// <summary>Calls into <c>weapon_guidance_angle_track</c>.</summary>
    public int GuidanceCalls { get; internal set; }

    /// <summary>Guidance calls that ran the LEAD arm (class <c>+0x24 &amp; 0x20</c>).</summary>
    public int GuidanceLeadArms { get; internal set; }

    /// <summary>Aim-angle computations that needed the renderer's Euler-matrix seam.</summary>
    public int GuidanceMatrixCases { get; internal set; }

    /// <summary>Calls into <c>chaff_fire</c>.</summary>
    public int ChaffFires { get; internal set; }

    /// <summary>Calls into <c>flare_fire</c>.</summary>
    public int FlareFires { get; internal set; }

    /// <summary>Countermeasure deployments the pool refused.</summary>
    public int CountermeasurePoolFull { get; internal set; }

    /// <summary>Countermeasure deployments refused for lack of stock.</summary>
    public int CountermeasureEmpty { get; internal set; }

    /// <summary>Calls into <c>countermeasure_engagement_slot_update</c>.</summary>
    public int DecoyUpdates { get; internal set; }

    /// <summary>Spawn slots the decoy update considered.</summary>
    public int DecoyCandidates { get; internal set; }

    /// <summary>Spawn slots the decoy update actually seduced.</summary>
    public int DecoySeductions { get; internal set; }

    /// <summary>Calls into <c>engagement_hit_pct_compute</c>.</summary>
    public int HitPctCalls { get; internal set; }

    /// <summary>Hit-percentage calls that took the range-gated zero arm.</summary>
    public int HitPctOutOfRange { get; internal set; }

    /// <summary>Merges another census into this one.</summary>
    /// <param name="other">The census to fold in.</param>
    public void Add(PlayerCombatCensus other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (PropertyInfo property in typeof(PlayerCombatCensus).GetProperties())
        {
            if (property.PropertyType == typeof(int) && property.CanWrite)
            {
                property.SetValue(
                    this, (int)property.GetValue(this)! + (int)property.GetValue(other)!);
            }
        }

        for (int i = 0; i < RouletteArms.Length; i++)
        {
            RouletteArms[i] += other.RouletteArms[i];
        }
    }
}
