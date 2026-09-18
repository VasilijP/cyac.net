using System.Buffers.Binary;
using CYAC.Port.Core.Schema;

namespace CYAC.Port.Core.Model.Flight;

/// <summary>
/// The player aircraft's live flight state — the original <c>s_aircraft_master</c>
/// (<c>g_aircraft_master_struct</c> [0xEF98], ≥ <c>0x12A</c> bytes).
/// </summary>
/// <remarks>
/// <para>
/// Field classes: the kinematics — position, the three <see cref="VelocityAxisState"/> blocks, the control
/// axes and the attitude accumulators — are the <b>integer half of DUAL</b> pairs and keep the original's
/// widths and wrap semantics.  Everything else here (flags, timers, hit points, fuel, the AoA scan
/// results) is <b>INT-only</b> spine.
/// </para>
/// <para>
/// This type is data shapes only: no stepping, no control integration, no physics.  What the type
/// does model is the <b>load effect</b> — <see cref="Load"/> reproduces
/// <c>flight_model_load_for_aircraft @0x2A112</c>, i.e. the 298-byte bulk copy plus the loader's
/// explicit overrides.
/// </para>
/// <para>
/// <b>Dual-use offsets are split.</b> Where one offset holds a <c>.fmd</c> constant at load and runtime
/// state in flight, the static half lives on <see cref="AircraftDefinition"/> and the runtime half here,
/// both carrying the same original offset in their attributes: <c>+0xBC</c>
/// (<see cref="AircraftDefinition.GrossWeightLb"/> ↔ <see cref="MassAccumulator"/>) and the pitch bounds
/// <c>+0xDE..+0xE4</c>.  both are corrected here.
/// </para>
/// <para>
/// <b>Unaligned aliases are computed, never stored.</b>  Several "fields" of the original are word
/// reads at odd offsets inside a wider field: <see cref="CurrentAirspeedFps"/> (<c>+0x01</c> inside
/// <c>vel_forward_i32</c>), <see cref="ThrustScaled"/> (<c>+0x9D</c> inside <c>speed_cap_lo_i32</c>),
/// <see cref="GLoadInteger"/> (<c>+0xD7</c> inside the pitch accumulator),
/// <see cref="HeadingAuthority"/> (<c>+0xC1</c> inside <c>fuel_remaining_i32</c>) and the two
/// <c>pos_x</c> aliases.  They are read-only properties so the port cannot desynchronise them.
/// </para>
/// <para>
/// Not modelled: the far pointers at <c>+0x11A..+0x121</c>.  <c>+0x11E/+0x120</c> is the FME far pointer
/// (the port holds <see cref="Definition"/> instead); <c>+0x11A/+0x11C</c> is NOT the FMD pointer but the
/// PLAYER WORLD-OBJECT far pointer (<c>s_object_slot</c>, copied from <c>g_alt_object_farptr [0x00C0]</c>;
/// K0, F1) — the port kernel addresses the player's <c>WorldObject</c> directly.
/// </para>
/// </remarks>
[OriginalStruct("s_aircraft_master")]
public sealed class Aircraft
{
    private Aircraft(AircraftDefinition definition)
    {
        Definition = definition;
        ForwardVelocity = definition.InitBlocks[0].ToVelocityAxis(VelocityBlock.Forward);
        AngularVelocityA = definition.InitBlocks[1].ToVelocityAxis(VelocityBlock.AngularA);
        AngularVelocityB = definition.InitBlocks[2].ToVelocityAxis(VelocityBlock.AngularB);
        RollDeadAxis = definition.InitBlocks[3].ToControlAxis();
        HeadingAoaAxis = definition.InitBlocks[4].ToControlAxis();
        RollAliveAxis = definition.InitBlocks[5].ToControlAxis();
        PitchAxis = ControlAxisState.FromBounds(
            definition.MaxLoadFactorG,
            definition.MinLoadFactorG,
            definition.PitchAuthorityBase,
            definition.PitchAuthorityStep);
    }

    /// <summary>
    /// How many throttle presets the cockpit keys <c>'1'</c>..<c>'5'</c> select
    /// (<c>cmp dx,0x31 … cmp dx,0x35</c>, <c>image@0x2A438..0x2A440</c>).
    /// </summary>
    public const int ThrottlePresetCount = 5;

    /// <summary>
    /// Where the preset table starts inside the master: <c>+0xAC</c> — the original addresses it as
    /// <c>[bx + si + 0x7B]</c> with <c>si</c> = the ASCII key, and <c>0x7B + '1' = 0xAC</c>.
    /// </summary>
    public const int ThrottlePresetOffset = 0xAC;

    /// <summary>The static side: what the <c>.fmd</c>/<c>.fme</c> pair says this aircraft is.</summary>
    public AircraftDefinition Definition { get; }

    // ---------------------------------------------------------------------------------------
    // Velocity blocks — the three 16-B integrator blocks
    // ---------------------------------------------------------------------------------------

    /// <summary><c>+0x00</c> — the forward-velocity block (<c>vel_forward_i32</c> and its input/caps).</summary>
    [OriginalField("+0x00", "vel_forward_i32")]
    public VelocityAxisState ForwardVelocity { get; }

    /// <summary><c>+0x10</c> — angular-velocity block A; decayed by <c>angular_decay_rate_update @0x2B016</c>.</summary>
    [OriginalField("+0x10", "state_10_u32")]
    public VelocityAxisState AngularVelocityA { get; }

    /// <summary><c>+0x20</c> — angular-velocity block B; decayed by <c>angular_decay_rate_update @0x2B016</c>.</summary>
    [OriginalField("+0x20", "state_20_u32")]
    public VelocityAxisState AngularVelocityB { get; }

    /// <summary>
    /// <c>+0x01</c> — the live airspeed in feet per second: an unaligned <c>u16</c> alias of
    /// <c>vel_forward_i32</c>'s bytes 1-2, i.e. <c>ForwardVelocity.Value &gt;&gt; 8</c>.
    /// </summary>
    /// <remarks>
    /// Read on every FME check (<c>image@0x2A9CC</c>/<c>image@0x2ABA8</c>/<c>image@0x2A8FA</c>) and
    /// confirmed three times (P29 → P706).  Distinct from <see cref="AirspeedA"/>/
    /// <see cref="AirspeedB"/>, which are the loader's i32 caps.
    /// </remarks>
    [OriginalField("+0x01", "current_airspeed_fps_u16")]
    public ushort CurrentAirspeedFps => unchecked((ushort)(ForwardVelocity.Value >> 8));

    // ---------------------------------------------------------------------------------------
    // Control axes — the three s_ctrl_state_block overlays
    // ---------------------------------------------------------------------------------------

    /// <summary><c>+0x30</c> — the roll axis on the DEAD path (post-stall / crash), <c>image@0x2B651</c>.</summary>
    [OriginalField("+0x30", "state_30[4]")]
    public ControlAxisState RollDeadAxis { get; }

    /// <summary><c>+0x50</c> — the roll axis on the ALIVE path (the normal joystick X axis), <c>image@0x2B57F</c>.</summary>
    [OriginalField("+0x50", "state_50[4]")]
    public ControlAxisState RollAliveAxis { get; }

    /// <summary>
    /// <c>+0xD6</c> — the pitch / g-load axis (joystick Y), <c>image@0x2B4FD</c>.
    /// </summary>
    /// <remarks>
    /// The block spans <c>+0xD6..+0xE5</c>, so its accumulator is <see cref="GLoadQ8"/>, its bounds
    /// are <see cref="MaxLoadFactorG"/>/<see cref="MinLoadFactorG"/> (<c>+0xDE</c>/<c>+0xE0</c>) and
    /// its direction constants are <see cref="PitchAuthorityBase"/>/<see cref="PitchAuthorityStep"/>
    /// (<c>+0xE2</c>/<c>+0xE4</c>) — the properties below are views onto this object, one per
    /// original field name.
    /// </remarks>
    [OriginalField("+0xD6")]
    public ControlAxisState PitchAxis { get; }

    /// <summary>
    /// <c>+0x40</c> — the heading-from-AoA integrator block: the fourth
    /// <see cref="ControlAxisState"/> instance (<c>state_40[4]</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The claim is true only of <c>joystick_to_control_deflect</c>, which indeed never targets
    /// <c>+0x40</c>.  The block IS driven through the same <c>s_ctrl_state_block</c> consumer chain by
    /// <c>aircraft_ctrl_axes_relax_per_frame @image@0x2A5A2</c>, whose third call is
    /// <c>ctrl_axis_alive_normalize(&amp;master+0x40)</c> @<c>image@0x2B5C0</c> — it reads the block's
    /// <see cref="ControlAxisState.DirStep"/> at <c>+0x4E</c> and steps <c>+0x40</c> toward zero.
    /// report-only here.
    /// </para>
    /// <para>
    /// On the active arm the block is not integrated at all: the integrator STORES into it,
    /// <c>+0x44</c> first (<c>image@0x2B543</c>/<c>image@0x2B546</c>) and then <c>+0x40</c> from a
    /// read-back of <c>+0x44</c> (<c>image@0x2B553</c>/<c>image@0x2B556</c>), so both halves carry
    /// the same value after an active frame.
    /// </para>
    /// </remarks>
    [OriginalField("+0x40", "state_40[4]")]
    public ControlAxisState HeadingAoaAxis { get; }

    /// <summary>
    /// <c>+0x44</c> — the heading-from-AoA result, i32 (<c>state_44_i16</c> lo, <c>state_46_i16</c> hi).
    /// </summary>
    /// <remarks>
    /// Written at <c>image@0x2B543</c>/<c>image@0x2B546</c>.  It is the <c>+0x40</c> block's
    /// <see cref="ControlAxisState.Working"/> slot — the two are the same four bytes, and
    /// <see cref="Load"/> used to seed this from the <c>.fmd</c> block's <c>+0x00</c> word pair rather
    /// than its <c>+0x04</c> pair.
    /// </remarks>
    [OriginalField("+0x44", "state_44_i16")]
    public int HeadingFromAoa
    {
        get => HeadingAoaAxis.Working;
        set => HeadingAoaAxis.Working = value;
    }

    // ---------------------------------------------------------------------------------------
    // Position (§3: i32 per axis with per-axis wrap bounds; Z negated, left-handed)
    // ---------------------------------------------------------------------------------------

    /// <summary><c>+0x60</c> — world X, i32 (<c>image@0x2A3C8</c>).</summary>
    [OriginalField("+0x60", "attitude_roll_q8_i32")]
    public int AttitudeRoll { get; set; }

    /// <summary><c>+0x70</c> — world Y (altitude), i32 (<c>image@0x2A3AF</c>).</summary>
    [OriginalField("+0x70", "attitude_pitch_q8_i32")]
    public int AttitudePitch { get; set; }

    /// <summary>
    /// <c>+0x80</c> — world Z, i32 (<c>image@0x2A394</c>).
    /// </summary>
    /// <remarks>
    /// Z is <b>negated</b> relative to the source coordinates — <c>aircraft_pose_set</c> applies
    /// <c>NEG AX; ADC DX,0; NEG DX</c> at <c>image@0x2A38D</c> to Z and to neither X nor Y, i.e. the
    /// engine's frame is left-handed.  Documented, not "fixed".
    /// </remarks>
    [OriginalField("+0x80", "attitude_heading_q8_i32")]
    public int AttitudeHeading { get; set; }

    /// <summary>
    /// <c>+0x64</c> — the ROLL attitude block's <b>working</b> i32 (ex-"the X position block's"): the second slot of the same
    /// 16-byte integrator template the control axes use.
    /// </summary>
    /// <remarks>
    /// Its writer is <c>ctrl_axis_step_toward @image@0x2B7AC</c> called with <c>BX = &amp;master+0x60</c> — from the
    /// state-3 pull-up arm (<c>image@0x2B3C2</c>), which stores the pull-up bank target ×256 here and then steps
    /// <see cref="AttitudeRoll"/> toward it.  An earlier pass measured it moving in the control-integration stage on all three K0
    /// recordings.
    /// </remarks>
    [OriginalField("+0x64")]
    public int AttitudeRollWorking { get; set; }

    /// <summary><c>+0x68</c> — X toroidal-wrap upper bound, ×256-scaled (<c>world_wrap_axis @0x2B8DE</c>, P603).</summary>
    [OriginalField("+0x68", "attitude_roll_limit_upper_i16")]
    public short AttitudeRollLimitUpper { get; set; }

    /// <summary><c>+0x6A</c> — X toroidal-wrap lower bound.</summary>
    [OriginalField("+0x6A", "attitude_roll_limit_lower_i16")]
    public short AttitudeRollLimitLower { get; set; }

    /// <summary>
    /// <c>+0x74</c> — the PITCH attitude block's <b>working</b> i32 (ex-"the Y position block's"; the same template slot as
    /// <see cref="AttitudeRollWorking"/>).
    /// </summary>
    /// <remarks>
    /// P808 proposed <c>pos_y_working_i32</c> and it was never applied.  Two writers, both
    /// <c>ctrl_axis_step_toward</c> with <c>BX = &amp;master+0x70</c>: the relax arm's "park Y at −80 at rate 10"
    /// (<c>image@0x2A5DA</c>) and the state-3 arm's "drive Y toward −90 at the pull-up rate" (<c>image@0x2B367</c>).
    /// </remarks>
    [OriginalField("+0x74")]
    public int AttitudePitchWorking { get; set; }

    /// <summary><c>+0x78</c> — Y toroidal-wrap upper bound.</summary>
    [OriginalField("+0x78", "attitude_pitch_limit_upper_i16")]
    public short AttitudePitchLimitUpper { get; set; }

    /// <summary><c>+0x7A</c> — Y toroidal-wrap lower bound.</summary>
    [OriginalField("+0x7A", "attitude_pitch_limit_lower_i16")]
    public short AttitudePitchLimitLower { get; set; }

    /// <summary><c>+0x88</c> — Z toroidal-wrap upper bound.</summary>
    [OriginalField("+0x88", "attitude_heading_limit_upper_i16")]
    public short AttitudeHeadingLimitUpper { get; set; }

    /// <summary><c>+0x8A</c> — Z toroidal-wrap lower bound.</summary>
    [OriginalField("+0x8A", "attitude_heading_limit_lower_i16")]
    public short AttitudeHeadingLimitLower { get; set; }

    /// <summary>
    /// <c>+0x61</c> — an unaligned <c>u16</c> alias of <see cref="AttitudeRoll"/>'s bytes 1-2, read at
    /// <c>image@0x2B3A4</c> as the bank-angle contribution to the pitch computation (P36).
    /// </summary>
    [OriginalField("+0x61", "state_61_u16")]
    public ushort AttitudeRollMidWord => unchecked((ushort)(AttitudeRoll >> 8));

    /// <summary>
    /// <c>+0x62</c> — <see cref="AttitudeRoll"/>'s high word, checked for the ejection bank direction
    /// (<c>image@0x2B375</c>/<c>image@0x2B383</c>, P36).
    /// </summary>
    [OriginalField("+0x62", "state_62_i16")]
    public short AttitudeRollHighWord => unchecked((short)(AttitudeRoll >> 16));

    /// <summary>
    /// <c>+0x71</c> — an unaligned <c>i16</c> alias of <see cref="AttitudePitch"/>'s bytes 1-2, i.e. the
    /// pitch attitude in whole degrees.  Read by <c>crash_conditions_valid</c>
    /// (<c>mov ax,[si+0x71]</c> @<c>image@0x2C279</c>), whose threshold
    /// <see cref="CrashPitchLimitDegrees"/> is a signed BYTE.
    /// </summary>
    /// <remarks>
    /// The <c>+0x60/+0x70/+0x80</c> blocks are ATTITUDE accumulators in 1/256°, not positions — see
    /// <see cref="AttitudePitch"/>'s remarks.
    /// </remarks>
    [OriginalField("+0x71")]
    public short AttitudePitchMidWord => unchecked((short)(AttitudePitch >> 8));

    /// <summary>
    /// <c>+0x11</c> — an unaligned <c>i16</c> alias of <see cref="AngularVelocityA"/>'s bytes 1-2.
    /// Read by <c>crash_conditions_valid</c> (<c>mov ax,[si+0x11]</c> @<c>image@0x2C295</c>) and
    /// compared, in absolute value, against <see cref="CrashAngularRateLimit"/>.
    /// </summary>
    [OriginalField("+0x11")]
    public short AngularVelocityAMidWord => unchecked((short)(AngularVelocityA.Value >> 8));

    // ---------------------------------------------------------------------------------------
    // Ground-contact survival thresholds (crash_conditions_valid @image@0x2C25C)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// <c>+0x92</c> — the greatest <see cref="CurrentAirspeedFps"/> a ground contact can survive
    /// (<c>mov ax,[si+0x92] ; cmp [si+1],ax ; jg</c> @<c>image@0x2C28C</c>: strictly greater fails).
    /// Shipped: 350 fps for the jets, 300 for the P-51.
    /// </summary>
    [OriginalField("+0x92", "crash_threshold_a_u16")]
    public short CrashAirspeedLimit { get; set; }

    /// <summary>
    /// <c>+0x94</c> — the greatest <c>|</c><see cref="AngularVelocityAMidWord"/><c>|</c> a ground
    /// contact can survive (<c>image@0x2C29D</c>).  Shipped: 50 in all six <c>.fmd</c>s.
    /// </summary>
    [OriginalField("+0x94", "crash_threshold_b_u16")]
    public short CrashAngularRateLimit { get; set; }

    /// <summary>
    /// <c>+0x96</c> — the greatest descent rate a ground contact can survive, compared against the
    /// NEGATED mid-word of <c>g_vertical_speed_i32 [0xF1C1]</c>
    /// (<c>mov ax,[0xf1c1] ; neg ax ; cmp ax,[si+0x96] ; jg</c> @<c>image@0x2C2A3</c>).
    /// Shipped: 100 in all six <c>.fmd</c>s.
    /// </summary>
    [OriginalField("+0x96", "crash_threshold_c_u16")]
    public short CrashDescentRateLimit { get; set; }

    /// <summary>
    /// <c>+0x98</c> — the greatest <c>|pitch|</c> IN DEGREES a ground contact can survive
    /// (<c>mov al,[si+0x98] ; cbw</c> @<c>image@0x2C283</c>, against
    /// <see cref="AttitudePitchMidWord"/>).  Shipped: 25 in all six <c>.fmd</c>s.
    /// </summary>
    /// <remarks>
    /// The bytes disagree: BOTH are read as separate signed BYTES through <c>CBW</c>, and they gate
    /// different axes.  Report-only.
    /// </remarks>
    [OriginalField("+0x98", "crash_threshold_d_u16")]
    public sbyte CrashPitchLimitDegrees { get; set; }

    /// <summary>
    /// <c>+0x99</c> — the greatest <c>|roll|</c> IN DEGREES a ground contact can survive
    /// (<c>mov al,[si+0x99] ; cbw</c> @<c>image@0x2C270</c>, against
    /// <see cref="AttitudeRollMidWord"/>).  Shipped: 10 in all six <c>.fmd</c>s.
    /// </summary>
    [OriginalField("+0x99", "crash_threshold_e_u8")]
    public sbyte CrashRollLimitDegrees { get; set; }

    /// <summary>
    /// <c>+0x9A</c> — the taildragger ground attitude, in 1/8° per unit of 8
    /// (<c>mov al,[bx+0x9a] ; cbw ; shl ax,3</c> @<c>image@0x2C1C5</c>/<c>image@0x2C1D6</c>).
    /// </summary>
    /// <remarks>
    /// Shipped values are 0 for every jet and <b>15</b> for the P-51 — 15 × 8 = 120 BAM = 15° of
    /// nose-up.  <c>damage_recovery_or_tilt @image@0x2C184</c> adds it to the world object's pitch
    /// while the aircraft is on the ground below <c>airspeed_threshold_for_control</c>, and blends
    /// it out linearly between ¾ of that threshold and the threshold itself — i.e. it is the tail
    /// rising on the take-off roll, not damage. report-only.)
    /// </remarks>
    [OriginalField("+0x9A", "tilt_damage_factor_u8")]
    public sbyte GroundTiltFactor { get; set; }

    // ---------------------------------------------------------------------------------------
    // Energy: thrust, throttle, fuel
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// <c>+0x9C</c> — the thrust floor, i32; <c>0x6400</c> on the runway or <c>0x0500</c> in the air
    /// (<c>aircraft_pose_set</c>, <c>image@0x2A32F</c>).
    /// </summary>
    [OriginalField("+0x9C", "throttle_current_lo_i32")]   // Renamed H16 (scanner), schema re-exported H21
    public int ThrustFloor { get; set; }

    /// <summary>
    /// <c>+0x9D</c> — an unaligned <c>u16</c> alias of <see cref="ThrustFloor"/>'s bytes 1-2; three
    /// readers, zero writers (<c>image@0x2AD19</c>, <c>image@0x2AC5F</c>, <c>image@0x2A63C</c>).
    /// </summary>
    [OriginalField("+0x9D", "throttle_pct_u16")]   // Renamed H16 (scanner), schema re-exported H21
    public ushort ThrustScaled => unchecked((ushort)(ThrustFloor >> 8));

    /// <summary>
    /// <c>+0xA0</c> — the throttle target, i32; current thrust steps toward it and a flameout zeroes it
    /// (<c>aircraft_throttle_fuel_step @0x2A5EE</c>).
    /// </summary>
    /// <remarks>The scanner name is <c>speed_cap_hi_i32</c> (P22); P33's throttle-target reading is the later, more specific one.</remarks>
    [OriginalField("+0xA0", "throttle_target_lo_i32")]   // Renamed H16 (scanner), schema re-exported H21
    public int ThrottleTarget { get; set; }

    /// <summary><c>+0xA8</c> — thrust step-up rate, from the <c>.fmd</c>.</summary>
    [OriginalField("+0xA8", "throttle_accel_rate_i16")]
    public short ThrottleAccelRate { get; set; }

    /// <summary><c>+0xAA</c> — thrust step-down rate, from the <c>.fmd</c>.</summary>
    [OriginalField("+0xAA", "throttle_decel_rate_i16")]
    public short ThrottleDecelRate { get; set; }

    /// <summary><c>+0xB6</c> — the afterburner yaw-drive scale; read only while <see cref="AircraftStatusFlags.Afterburner"/> is set (P44).</summary>
    [OriginalField("+0xB6", "afterburner_scale_i16")]
    public short AfterburnerScale { get; set; }

    /// <summary>
    /// <c>+0xAC..+0xB0</c> — the five throttle presets keys <c>'1'</c>..<c>'5'</c> command, in whole
    /// percent (<c>mov al,[bx+si+0x7b]</c> @<c>image@0x2A450</c>, <c>si</c> = the ASCII key).
    /// </summary>
    /// <param name="index">0 for key <c>'1'</c> … 4 for key <c>'5'</c>.</param>
    /// <returns>The preset percentage the <c>.fmd</c> authored.</returns>
    /// <remarks>
    /// Read-only: <c>cockpit_key_dispatch</c> is the ONLY reference to these five bytes anywhere in
    /// the flight segment and it never writes them, so the port reads them straight out of
    /// <see cref="AircraftDefinition.RawFlightModel"/> rather than storing a copy — the loader's 1:1
    /// <c>.fmd</c>→master bulk copy (@<c>0x2A112</c>: "FMD[X] → master[+X] identity") makes the two
    /// the same bytes.  Shipped values are <c>{0, 25, 50, 75, 100}</c> for every flyable aircraft.
    /// </remarks>
    public byte ThrottlePresetPercent(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, ThrottlePresetCount);
        return Definition.RawFlightModel.Span[ThrottlePresetOffset + index];
    }

    /// <summary>
    /// <c>+0xB8</c> — the throttle CUT-OFF limit: while non-zero and reached by
    /// <see cref="ThrottleCutoffCounter"/>, every throttle command is forced to zero
    /// (<c>cmp word [bx+0xb8],0</c> @<c>image@0x2A22E</c>).
    /// </summary>
    /// <remarks>
    /// Read-only, and DEAD on shipped data: <c>+0xB8</c> and <c>+0xBA</c> have exactly three
    /// references in the whole flight segment — all three inside <c>hp_damage_speed_cap_compute
    /// @image@0x2A21D</c>, all three reads — and every shipped <c>.fmd</c> authors <c>+0xB8 =
    /// 0</c>, which short-circuits the pair.  Named for what the two reads do, not for a decoded
    /// semantic; an authored <c>.fmd</c> could switch it on <b>(open: what the counter counts —
    /// nothing writes it either)</b>.
    /// </remarks>
    [OriginalField("+0xB8")]
    public short ThrottleCutoffLimit =>
        BinaryPrimitives.ReadInt16LittleEndian(Definition.RawFlightModel.Span[0xB8..]);

    /// <summary>
    /// <c>+0xBA</c> — the counter compared against <see cref="ThrottleCutoffLimit"/>
    /// (<c>cmp word [bx+0xba],ax ; jl</c> @<c>image@0x2A239</c>: at or above the limit, cut off).
    /// </summary>
    /// <remarks>Read-only for the same reason as <see cref="ThrottleCutoffLimit"/>.</remarks>
    [OriginalField("+0xBA")]
    public short ThrottleCutoffCounter =>
        BinaryPrimitives.ReadInt16LittleEndian(Definition.RawFlightModel.Span[0xBA..]);

    /// <summary>
    /// <c>+0xC0</c> — fuel remaining, i32; burned by <c>aircraft_throttle_fuel_step</c> at ~512 Hz,
    /// doubled on afterburner.
    /// </summary>
    /// <remarks>
    /// Starts at <see cref="AircraftDefinition.InitialFuelQ8"/>.  A flameout (fuel exhausted) zeroes
    /// <see cref="ThrottleTarget"/>.
    /// </remarks>
    [OriginalField("+0xC0", "fuel_remaining_i32")]
    public int Fuel { get; set; }

    /// <summary>
    /// <c>+0xC1</c> — the per-frame heading-rate increment: an unaligned <c>i16</c> alias of
    /// <see cref="Fuel"/>'s bytes 1-2, added into <see cref="MassAccumulator"/> every frame.
    /// </summary>
    /// <remarks>
    /// <c>aircraft_per_frame_update @0x2A6CC</c>: <c>MOV AX,[BX+0xC1]; CDQ; ADD [BX+0xBC],AX; ADC
    /// [BX+0xBE],DX</c>.  An earlier reading called this "engine power as heading authority" because the loader
    /// seeds <c>+0xC0</c> with <c>FMD[+0xC8] &lt;&lt; 8</c> — but that field is the starting <b>fuel
    /// load</b> and the fuel field is burned down every tick, so what the frame update actually adds
    /// is the <i>current</i> fuel divided by 256, decaying over the sortie.  Reported in B4 §4; the
    /// port computes it from live <see cref="Fuel"/>, which is what the bytes do.
    /// The scanner has no field here — <c>+0xC1</c> was struck in P38 for having no writer.
    /// </remarks>
    public short HeadingAuthority => unchecked((short)(Fuel >> 8));

    /// <summary><c>+0xCA</c> — the fuel-tick dt accumulator; fires a tick at <c>0x500</c> (P33).</summary>
    [OriginalField("+0xCA", "fuel_tick_accum_i16")]
    public short FuelTickAccumulator { get; set; }

    /// <summary><c>+0xCC</c> — the per-aircraft fuel consumption rate (P33).</summary>
    [OriginalField("+0xCC", "fuel_rate_u8")]
    public byte FuelRate { get; set; }

    // ---------------------------------------------------------------------------------------
    // Attitude accumulators
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// <c>+0xBC</c> — the MASS accumulator, i32 (<c>mass_accum_lo_i16</c> plus <c>mass_accum_hi_i16</c> at
    /// <c>+0xBE</c>; K2 §4.4 / K3 §4.6).
    /// </summary>
    /// <remarks>
    /// <b>Dual-use:</b> the same word pair holds <see cref="AircraftDefinition.GrossWeightLb"/> at load time, and
    /// <see cref="Load"/> preserves that — the original starts integrating from the gross weight because the loader
    /// never clears it after reading it in the <see cref="AircraftDefinition.InitialHeadingAngularRate"/> formula
    /// (<c>image@0x2A1C4</c>). CLOSED K2 §4.4 / K3 §4.6, names applied R1 the per-frame chain is a <i>mass</i> sum,
    /// not a heading integral: <c>aircraft_per_frame_update</c> saves this pair, adds <see cref="HeadingAuthority"/>
    /// (= fuel in pounds) into it, derives <see cref="MassDiv32"/> = <c>accum &gt;&gt; 5</c>, then restores the saved
    /// value at the epilogue — so the derived word is <c>(weight + fuel) / 32</c>, and's "faster engine ⇒ higher
    /// heading authority" is really "heavier aircraft ⇒ larger divisor".
    /// </remarks>
    [OriginalField("+0xBC", "mass_accum_lo_i16")]
    public int MassAccumulator { get; set; }

    /// <summary><c>+0xBE</c> — the high word of <see cref="MassAccumulator"/>; zero on disk.</summary>
    [OriginalField("+0xBE", "mass_accum_hi_i16")]
    public short MassAccumulatorHigh => unchecked((short)(MassAccumulator >> 16));

    /// <summary>
    /// <c>+0xD0</c> — the performance limit.
    /// </summary>
    /// <remarks>
    /// Struck: no runtime writer exists image-wide.  Kept as a read-only view of
    /// <see cref="AircraftDefinition.PerformanceLimit"/> so the offset stays mapped without implying
    /// mutable state.
    /// </remarks>
    [OriginalField("+0xD0", "roll_rate_i16")]
    public short PerformanceLimit => unchecked((short)Definition.PerformanceLimit);

    /// <summary>
    /// <c>+0xD2</c> — the angular-rate scalar for the yaw component (P44); computed by the loader,
    /// see <see cref="AircraftDefinition.InitialHeadingAngularRate"/>.
    /// </summary>
    [OriginalField("+0xD2", "heading_angular_rate_i16")]
    public short HeadingAngularRate { get; set; }

    /// <summary>
    /// <c>+0xD4</c> — the current ALL-UP MASS ÷ 32 (<c>mass_accum &gt;&gt; 5</c>), written every frame at
    /// <c>image@0x2A6F5</c> and used as the divisor of every accumulated force.
    /// </summary>
    /// <remarks>
    /// It IS the mass term: K3 §4.6 predicted it to the unit as <c>(FMD[+0xBC] + FMD[+0xC8]) / 32</c> on
    /// the P-51 (273), MiG-15 (271) and F-4 (1,212).
    /// </remarks>
    [OriginalField("+0xD4", "mass_div32_i16")]
    public short MassDiv32 { get; set; }

    // ---------------------------------------------------------------------------------------
    // Load factor / AoA — views onto the +0xD6 control block plus the scan results
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// <c>+0xD6</c> — the Q8 fraction byte of the load factor.
    /// </summary>
    /// <remarks>
    /// It is the fractional byte, and it reads zero only because the two whole-number writers leave
    /// it zero; the joystick integrator produces fractions and the HUD prints them as the tenths
    /// digit @<c>image@0x0C6CC</c>.
    /// </remarks>
    [OriginalField("+0xD6", "gload_frac_byte_u8")]
    public byte GLoadFraction => unchecked((byte)PitchAxis.Current);

    /// <summary>
    /// The load factor in Q8.8 — the low word of the pitch axis' accumulator, aliased in DGROUP as
    /// <c>g_player_gload_q8</c> [0xF06E].  <c>0x0100</c> = 1.00 G.
    /// </summary>
    public short GLoadQ8 => unchecked((short)PitchAxis.Current);

    /// <summary>
    /// <c>+0xD7</c> — the integer part of the load factor, read as an unaligned word
    /// (<c>image@0x2AB81</c>); the FME lookup key and the "%2d G" the envelope overlay prints.
    /// </summary>
    /// <remarks>
    /// It is the load factor's integer part, range −4..+9, the same keyspace as
    /// <see cref="EnvelopeCurve.LoadFactorG"/>.
    /// </remarks>
    [OriginalField("+0xD7", "current_gload_int_i16")]
    public short GLoadInteger => unchecked((short)(PitchAxis.Current >> 8));

    /// <summary><c>+0xDA</c> — the pitch axis' scratch slot; zeroed on teleport (<c>image@0x2A3D0</c>).</summary>
    [OriginalField("+0xDA", "ctrl_aoa_working_i32")]
    public int PitchWorking => PitchAxis.Working;

    /// <summary>
    /// <c>+0xDE</c> — the maximum load factor: the pitch axis' <see cref="ControlAxisState.HiBound"/>
    /// and the upper bound of <c>aoa_range_scanner @0x2B12A</c>'s sweep.
    /// </summary>
    /// <remarks>Static twin: <see cref="AircraftDefinition.MaxLoadFactorG"/> (the <c>.fmd</c>'s <c>era_tier_u16</c>).</remarks>
    [OriginalField("+0xDE", "aoa_scan_max_i16")]
    public short MaxLoadFactorG => PitchAxis.HiBound;

    /// <summary><c>+0xE0</c> — the minimum load factor: the pitch axis' <see cref="ControlAxisState.LoBound"/> (−4).</summary>
    [OriginalField("+0xE0", "aoa_scan_min_i16")]
    public short MinLoadFactorG => PitchAxis.LoBound;

    /// <summary>
    /// <c>+0xE2</c> — the pitch axis' <see cref="ControlAxisState.BaseDir"/>, read only through the
    /// control-block pointer (<c>image@0x2B9CC</c>) and never re-written.
    /// </summary>
    /// <remarks>Static twin: <see cref="AircraftDefinition.PitchAuthorityBase"/> (the <c>.fmd</c>'s <c>stall_low_u16</c>).</remarks>
    [OriginalField("+0xE2", "aoa_ctrl_base_dir_i16")]
    public short PitchAuthorityBase => PitchAxis.BaseDir;

    /// <summary><c>+0xE4</c> — the pitch axis' <see cref="ControlAxisState.DirStep"/> (<c>image@0x2B9FC</c>).</summary>
    /// <remarks>Static twin: <see cref="AircraftDefinition.PitchAuthorityStep"/> (the <c>.fmd</c>'s <c>stall_high_u16</c>).</remarks>
    [OriginalField("+0xE4", "aoa_ctrl_dir_step_i16")]
    public short PitchAuthorityStep => PitchAxis.DirStep;

    /// <summary><c>+0xE6</c> — the reset sentinel; <c>aircraft_pose_set</c> writes <c>−1</c> (<c>image@0x2A3F1</c>).</summary>
    [OriginalField("+0xE6", "reset_sentinel_u16")]
    public short ResetSentinel { get; set; }

    /// <summary><c>+0xE8</c> — lowest load factor the envelope currently allows (output of <c>aoa_range_scanner</c>).</summary>
    [OriginalField("+0xE8", "aoa_valid_min_i16")]
    public short ValidLoadFactorMin { get; set; }

    /// <summary><c>+0xEA</c> — highest load factor the envelope currently allows.</summary>
    /// <remarks>Easy mode widens the scanned range by ±1.</remarks>
    [OriginalField("+0xEA", "aoa_valid_max_i16")]
    public short ValidLoadFactorMax { get; set; }

    /// <summary><c>+0xEC</c> — the induced-drag coefficient per unit of g-load excess.</summary>
    [OriginalField("+0xEC", "aoa_sensitivity_i16")]
    public short InducedDragCoefficient { get; set; }

    // ---------------------------------------------------------------------------------------
    // Control-surface authority and drag
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// <c>+0xEE</c> — elevator authority; degraded by <see cref="AircraftDamageFlags.Hull"/> hits
    /// (<c>image@0x2A592</c>) and blended by <c>(0x100 − deflection)</c> in the ejection state (P36).
    /// </summary>
    [OriginalField("+0xEE", "ctrl_elevator_i16")]
    public short ElevatorAuthority { get; set; }

    /// <summary><c>+0xF0</c> — roll authority gain; <i>increased</i> by <see cref="AircraftDamageFlags.RollAuthority"/> hits (<c>image@0x2A54D</c>).</summary>
    [OriginalField("+0xF0", "roll_gain_i16")]
    public short RollAuthorityGain { get; set; }

    /// <summary><c>+0xF2</c> — the primary heading-control input (P44).</summary>
    [OriginalField("+0xF2", "ctrl_heading_input_i16")]
    public short HeadingInput { get; set; }

    /// <summary><c>+0xFA</c> — the airbrake drag coefficient (key 'b' reads it at <c>image@0x2A4AB</c>).</summary>
    [OriginalField("+0xFA", "ctrl_surface_drag_a_i16")]
    public short AirbrakeDragCoefficient { get; set; }

    /// <summary><c>+0x100</c> — the landing-gear drag coefficient (key 'f' at <c>image@0x2A4C4</c>).</summary>
    [OriginalField("+0x100", "ctrl_surface_drag_d_i16")]
    public short GearDragCoefficient { get; set; }

    /// <summary>
    /// <c>+0xFC</c> — the drag coefficient the airbrake costs when the aircraft is inside the
    /// ground-proximity margin.
    /// </summary>
    /// <remarks>
    /// Read once image-wide: <c>vel_roll_aoa_correction_accum</c>'s phase E,
    /// <c>mov ax,[bx+0xfc]</c> @<c>image@0x2ADFB</c>, on the arm gated by
    /// <see cref="AircraftStatusFlags.Airbrake"/> AND <c>[0xF1C4] != 0</c>
    /// (<c>image@0x2ADE5</c>/<c>image@0x2ADEC</c>).  Unlike phase D's sum, it multiplies the mass
    /// term directly.  128 in all six shipped <c>.fmd</c>s.
    /// </remarks>
    [OriginalField("+0xFC", "ctrl_surface_drag_b_i16")]
    public short AirbrakeNearGroundDragCoefficient { get; set; }

    /// <summary>
    /// <c>+0xFE</c> — the drag coefficient the <see cref="AircraftStatusFlags.LandingGear"/> bit
    /// contributes to phase D's sum.
    /// </summary>
    /// <remarks>
    /// Read once image-wide: <c>mov di,[bx+0xfe]</c> @<c>image@0x2AD8F</c>, on the arm gated by
    /// bit 2 AND <c>[0xF1C4] == 0</c> (<c>image@0x2AD81</c>/<c>image@0x2AD88</c>) — the only one of
    /// phase D's three arms that is proximity-gated.  102 in both prop <c>.fmd</c>s and 153 in all
    /// four jets.  It never fired on any of the 86,287 frames, because bit 2 was clear
    /// on every one of them.
    /// </remarks>
    [OriginalField("+0xFE", "ctrl_surface_drag_c_i16")]
    public short LandingGearDragCoefficient { get; set; }

    /// <summary>
    /// <c>+0x102</c> — the Q8 control-authority BONUS the FME correction adds when the landing gear
    /// is down.
    /// </summary>
    /// <remarks>
    /// <c>mov si,[bx+0x102]; add si,0x100</c> (<c>image@0x2AE33</c>/<c>image@0x2AE37</c>) inside
    /// <c>vel_state10_component_accum</c>: the baseline authority is Q8 1.0 and the gear raises it by
    /// this many 1/256ths.  Sole reference image-wide.  76 in both prop <c>.fmd</c>s and 25 in all
    /// four jets.  <b> names no field at this offset</b> — reported, not renamed; the
    /// attribute carries no descriptor and the drift detector's <c>PendingScannerFields</c>
    /// allow-list records why.
    /// </remarks>
    [OriginalField("+0x102")]
    public short GearControlAuthorityBonusQ8 { get; set; }

    // ---------------------------------------------------------------------------------------
    // Timers, airspeed caps, flags, envelope corner
    // ---------------------------------------------------------------------------------------

    // ---------------------------------------------------------------------------------------
    // The three-state machine's timers (P36; all read/written by
    // aircraft_joystick_integrate_active @0x2B268)
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// <c>+0xF4</c> — the value <see cref="StateCountdown"/> is reloaded with when the aircraft
    /// enters state 2 (stall countdown): <c>image@0x2B599</c>/<c>image@0x2B59D</c>.
    /// </summary>
    [OriginalField("+0xF4", "state_F4_i16")]
    public short StallCountdownReload { get; set; }

    /// <summary>
    /// <c>+0xF6</c> — the value <see cref="StateCountdown"/> is reloaded with when the aircraft
    /// enters state 3 (pull-up / ejection): <c>image@0x2B49A</c>/<c>image@0x2B49E</c>.  The state-3
    /// arm also uses it as the numerator of its authority ramp
    /// (<c>mov ax,[bx+0xf6] ; sub ax,[bx+0xf8]</c> @<c>image@0x2B331</c>).
    /// </summary>
    [OriginalField("+0xF6", "state_F6_i16")]
    public short PullUpCountdownReload { get; set; }

    /// <summary>
    /// <c>+0xF8</c> — the live state countdown, decremented by <c>dt</c>.
    /// </summary>
    /// <remarks>
    /// Read and written SIGNED: state 2 subtracts <c>dt</c> and tests <c>jns</c>
    /// (<c>image@0x2B48F</c>/<c>image@0x2B493</c>), and the state-3 arm compares it against the
    /// signed immediate <c>0x9C00</c> = −25600 (<c>jle</c> @<c>image@0x2B42A</c>).  The scanner types
    /// it <c>state_f8_u16</c>; every observed use is signed.
    /// </remarks>
    [OriginalField("+0xF8", "state_f8_u16")]
    public short StateCountdown { get; set; }

    /// <summary><c>+0x104</c> — the reset source for <see cref="HoldAltitudeTimer"/> on a crash (P39).</summary>
    [OriginalField("+0x104", "hold_altitude_timer_init_u16")]
    public ushort HoldAltitudeTimerInit { get; set; }

    /// <summary>
    /// <c>+0x106</c> — while positive, locks the camera Y to the airspeed sum; decremented by dt each
    /// frame inside <c>aircraft_physics_apply_velocity</c> phase 11 (P37).
    /// </summary>
    [OriginalField("+0x106", "hold_altitude_timer_u16")]
    public ushort HoldAltitudeTimer { get; set; }

    /// <summary>
    /// <c>+0x108</c> — the low-speed advisory's message threshold, compared against
    /// <see cref="LowSpeedElapsed"/> (<c>image@0x2B614</c>) and again in the integrator's epilogue
    /// (<c>image@0x2B6FD</c>) to decide whether the two trim step-towards run.
    /// </summary>
    [OriginalField("+0x108", "state_108_i16")]
    public short LowSpeedWarningThreshold { get; set; }

    /// <summary>
    /// <c>+0x10A</c> — the limit <see cref="LowSpeedElapsed"/> has to reach for the low-speed timer
    /// to expire (<c>image@0x2B5E2</c>/<c>image@0x2B5EE</c>).
    /// </summary>
    [OriginalField("+0x10A", "state_10A_i16")]
    public short LowSpeedWarningLimit { get; set; }

    /// <summary>
    /// <c>+0x10C</c> — the below-minimum-speed dt accumulator: <c>+= dt</c> on the status-3 arm
    /// (<c>image@0x2B5EA</c>), zeroed on every other envelope verdict
    /// (<c>image@0x2B5A1</c>/<c>image@0x2B5CF</c>).
    /// </summary>
    [OriginalField("+0x10C", "state_10C_i16")]
    public short LowSpeedElapsed { get; set; }

    /// <summary><c>+0x10E</c> — airspeed cap A, i32; summed with <see cref="AirspeedB"/> for the ground-proximity compare (<c>image@0x2A2CB</c>).</summary>
    [OriginalField("+0x10E", "airspeed_a_i32")]
    public int AirspeedA { get; set; }

    /// <summary>
    /// <c>+0x116</c> — the player object's class-record GROUND CLEARANCE, i32.
    /// </summary>
    /// <remarks>
    /// REFUTED — the loader reads its far-ptr ARG (the player world object),
    /// dereferences the object's class pointer and takes the CLASS record's <c>+0x2C</c>: <c>mov es,dx
    /// / mov bx,ax / mov bx,es:[bx] / mov ax,[bx+0x2C] / mov [si+0x116],ax</c>
    /// @<c>image@0x2A165..0x2A16F</c>, with <c>[si+0x118]:= 0</c> @<c>image@0x2A173</c>.  That word is
    /// <see cref="Model.World.ClassRecord.GroundClearance"/> = <c>-BoundsMinY</c> (5,376 for the P-51
    /// and the FW-190, 6,656 for the F-4 …), not the <c>.fmd</c>'s 80.  It reads as a clearance
    /// everywhere it is used: <c>flight_check_alive_or_active @image@0x2C22C</c> sets the
    /// ground-proximity flag iff <c>AirspeedB + AirspeedA &gt;= playerObject.Y</c>, and
    /// <c>aircraft_pose_set</c> ORs the gear bit on the same comparison
    /// (<c>image@0x2A2C3..0x2A2E5</c>).  *(the scanner field is renamed
    /// <c>class_ground_clearance_i32</c>, so the <c>[OriginalField]</c> citation below follows it —
    /// <c>FlightSchemaLinkTests</c> asserts the two match.)*
    /// </remarks>
    [OriginalField("+0x116", "class_ground_clearance_i32")]
    public int AirspeedB { get; set; }

    /// <summary>
    /// <c>+0x114</c> — the altitude, in whole <c>0x10000</c> Q8-feet units, above which the
    /// integrator zeroes <see cref="HeadingInput"/>.
    /// </summary>
    /// <remarks>
    /// <c>mov ax,[bx+0x114]; les si,[bx+0x11a]; cmp es:[si+0xc],ax; jle</c>
    /// (<c>image@0x2B295..0x2B2A1</c>): the compared word is the <b>high word of the player world
    /// object's <c>pos_y</c></b>, so this is an altitude cut-off. A consequence of K0 finding F1:
    /// <c>+0x11A</c> is the player world object, not an FMD copy, so <c>es:[si+0xC]</c> is
    /// <c>pos_y</c>'s high word and not an airspeed; and the zeroed field is <c>+0xF2</c>
    /// <see cref="HeadingInput"/>, not the roll axis.  Report-only.
    /// </remarks>
    [OriginalField("+0x114", "state_114_i16")]
    public short AltitudeCutoffForHeadingInput { get; set; }

    /// <summary>
    /// <c>+0x122</c> — the active/ejection state: 0 inactive, 1 flying, 2 stall countdown, 3 pull-up
    /// ejection (P36's three-state machine; the loader writes 1 at <c>image@0x2A19A</c>).
    /// </summary>
    [OriginalField("+0x122", "active_flag_u8")]
    public byte ActiveState { get; set; }

    /// <summary><c>+0x123</c> — accumulated damage bits; the loader clears it (<c>image@0x2A195</c>).</summary>
    [OriginalField("+0x123", "damage_flags_u8")]
    public AircraftDamageFlags DamageFlags { get; set; }

    /// <summary><c>+0x124</c> — the status bits (afterburner, gear, ground contact, airbrake, crash, bounce).</summary>
    [OriginalField("+0x124", "status_flags_u8")]
    public AircraftStatusFlags StatusFlags { get; set; }

    /// <summary><c>+0x126</c> — the envelope's maximum X, accumulated by <c>flight_envelope_load</c> (<c>image@0x2AB07</c>).</summary>
    [OriginalField("+0x126", "envelope_corner_x")]
    public ushort EnvelopeCornerX { get; set; }

    /// <summary><c>+0x128</c> — the envelope's maximum Y (<c>image@0x2AB19</c>).</summary>
    [OriginalField("+0x128", "envelope_corner_y")]
    public ushort EnvelopeCornerY { get; set; }

    /// <summary>True when the aircraft is on or near the ground — <see cref="AircraftStatusFlags.LandingGear"/> SET.</summary>
    /// <remarks>
    /// <c>aircraft_pose_set</c> ORs the bit in when <c>airspeed_a + airspeed_b &gt;=
    /// playerObject.pos_y</c> — the ground-proximity margin — @<c>image@0x2A2D3..0x2A2E5</c> and clears it above it
    /// @<c>0x2A2F7</c>; the loader sets it @<c>0x2A190</c>; the 'g' key toggles it @<c>0x2A4E9</c>; it read CLEAR on
    /// every airborne frame observed.
    /// </remarks>
    public bool IsGearDown => StatusFlags.HasFlag(AircraftStatusFlags.LandingGear);

    /// <summary><c>+0xA4</c> — hit points; 100 at load, decremented per hit, never reset until the next scenario load.</summary>
    [OriginalField("+0xA4", "hp_remaining_i16")]
    public short HitPoints { get; set; }

    /// <summary><c>+0xA6</c> — the HP reference / speed-clamp upper bound, set by the damage path (P70).</summary>
    [OriginalField("+0xA6", "hp_reference_i16")]
    public short HitPointReference { get; set; }

    /// <summary>
    /// Builds the runtime state <c>flight_model_load_for_aircraft @0x2A112</c> leaves behind: the
    /// 298-byte bulk copy, then the loader's explicit overrides.
    /// </summary>
    /// <param name="definition">The parsed <c>.fmd</c>/<c>.fme</c> pair.</param>
    /// <remarks>
    /// <para>
    /// The bulk copy is <c>ealib_load_asset(mode=0xFF, DS:SI=&amp;master)</c> at
    /// <c>image@0x2A145</c>: <c>FMD[X] → master[+X]</c> for all 298 bytes.  The overrides are
    /// <c>fuel = FMD[+0xC8] &lt;&lt; 8</c> (<c>image@0x2A185</c>),
    /// <c>heading_angular_rate = (FMD[+0xBC] × FMD[+0xD0]) &gt;&gt; 11</c> (<c>image@0x2A1DF</c>),
    /// <c>master[+0x116] = playerObjectClass[+0x2C]</c>, the class's ground clearance, read through
    /// the far-ptr ARG @<c>image@0x2A165..0x2A16F</c>; this loader does not have the object, so
    /// <see cref="FlightColdStart"/> writes it, the FMD/FME far pointers, and the three
    /// flag bytes: <c>status |= 0x04</c>, <c>damage = 0</c>, <c>active = 1</c>
    /// (<c>image@0x2A190..0x2A19A</c>).  <c>flight_envelope_load @0x2AA76</c> then fills the envelope
    /// corner at <c>+0x126</c>/<c>+0x128</c>.
    /// </para>
    /// <para>
    /// Not reproduced: the two far pointers (the port holds <see cref="Definition"/> instead) and the
    /// <c>damage_recovery_or_tilt</c> call at <c>image@0x2A18D</c>, which is per-frame logic and out
    /// of scope here.
    /// </para>
    /// </remarks>
    public static Aircraft Load(AircraftDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        IReadOnlyList<AircraftInitBlock> blocks = definition.InitBlocks;
        Aircraft aircraft = new Aircraft(definition)
        {
            // --- bulk copy: every scalar the .fmd carries lands at its own offset ---
            AttitudeRoll = blocks[6].Value,
            AttitudePitch = blocks[7].Value,
            AttitudeHeading = blocks[8].Value,
            AttitudeRollWorking = blocks[6].Working,
            AttitudePitchWorking = blocks[7].Working,
            AttitudeRollLimitUpper = blocks[6].HiBound,
            AttitudeRollLimitLower = blocks[6].LoBound,
            AttitudePitchLimitUpper = blocks[7].HiBound,
            AttitudePitchLimitLower = blocks[7].LoBound,
            AttitudeHeadingLimitUpper = blocks[8].HiBound,
            AttitudeHeadingLimitLower = blocks[8].LoBound,
            // The +0x40 block's two i32 slots, from the .fmd block that lands on them.
            // HeadingFromAoa is the block's WORKING slot at +0x44, i.e. blocks[4].Working; the
            // block's CURRENT slot at +0x40 is blocks[4].Value and had no port field at all.
            HeadingFromAoa = blocks[4].Working,
            ThrustFloor = 0,
            ThrottleTarget = 0,
            HitPoints = definition.InitialHitPoints,
            HitPointReference = definition.InitialHitPointReference,
            ThrottleAccelRate = unchecked((short)definition.ThrottleAccelRate),
            ThrottleDecelRate = unchecked((short)definition.ThrottleDecelRate),
            AfterburnerScale = definition.AfterburnerScale,
            MassAccumulator = definition.GrossWeightLb,   // the empty weight in lb seeds the mass chain
            FuelRate = unchecked((byte)definition.FuelRate),
            ResetSentinel = definition.ResetSentinel,
            InducedDragCoefficient = definition.InducedDragCoefficient,
            RollAuthorityGain = definition.RollAuthorityGain,
            AirbrakeDragCoefficient = definition.AirbrakeDragCoefficient,
            ElevatorAuthority = definition.InitialElevatorAuthority,
            HeadingInput = definition.InitialHeadingInput,
            GearDragCoefficient = definition.GearDragCoefficient,
            HoldAltitudeTimerInit = definition.HoldAltitudeTimerInit,

            // --- loader overrides (§2's "five post-load overrides") ---
            Fuel = definition.InitialFuelQ8,
            HeadingAngularRate = definition.InitialHeadingAngularRate,
            // +0x116 is NOT definition.InitialAirspeedB — see the AirspeedB remarks.  It is the
            // PLAYER OBJECT's class-record ground clearance, which this .fmd-only loader cannot
            // know; FlightColdStart.Create writes it from the class record, and a trace seed gets
            // it from the record's own master bytes.
            AirspeedB = 0,
            ActiveState = 1,
            DamageFlags = AircraftDamageFlags.None,
            StatusFlags = definition.InitialStatusFlags | AircraftStatusFlags.LandingGear,

            // --- flight_envelope_load's 14-record walk ---
            EnvelopeCornerX = (ushort)definition.Envelope.Corner.X,
            EnvelopeCornerY = (ushort)definition.Envelope.Corner.Y,
        };

        // The +0x40 block's CURRENT slot — not reachable through the object initialiser because the
        // block itself is get-only.
        aircraft.HeadingAoaAxis.Current = blocks[4].Value;

        // The scalars the bulk copy lands past the nine init blocks and past every named .fmd field:
        // the three-state machine's timers and the low-speed advisory's thresholds.  They are read
        // straight out of the copied image (image@0x2A145) because the definition names no property
        // for them.
        ReadOnlySpan<byte> fmd = definition.RawFlightModel.Span;
        aircraft.StallCountdownReload = FlightModelWord(fmd, 0xF4);
        aircraft.PullUpCountdownReload = FlightModelWord(fmd, 0xF6);
        aircraft.StateCountdown = FlightModelWord(fmd, 0xF8);
        aircraft.LowSpeedWarningThreshold = FlightModelWord(fmd, 0x108);
        aircraft.LowSpeedWarningLimit = FlightModelWord(fmd, 0x10A);
        aircraft.LowSpeedElapsed = FlightModelWord(fmd, 0x10C);
        aircraft.AltitudeCutoffForHeadingInput = FlightModelWord(fmd, 0x114);

        // The three drag / authority constants the velocity-dynamics subtree reads and
        // AircraftDefinition names no property for.
        aircraft.AirbrakeNearGroundDragCoefficient = FlightModelWord(fmd, 0xFC);
        aircraft.LandingGearDragCoefficient = FlightModelWord(fmd, 0xFE);
        aircraft.GearControlAuthorityBonusQ8 = FlightModelWord(fmd, 0x102);

        // The five ground-contact survival thresholds and the taildragger ground attitude — read by
        // crash_conditions_valid @image@0x2C25C and damage_recovery_or_tilt @image@0x2C184.
        aircraft.CrashAirspeedLimit = FlightModelWord(fmd, 0x92);
        aircraft.CrashAngularRateLimit = FlightModelWord(fmd, 0x94);
        aircraft.CrashDescentRateLimit = FlightModelWord(fmd, 0x96);
        aircraft.CrashPitchLimitDegrees = unchecked((sbyte)fmd[0x98]);
        aircraft.CrashRollLimitDegrees = unchecked((sbyte)fmd[0x99]);
        aircraft.GroundTiltFactor = unchecked((sbyte)fmd[0x9A]);

        return aircraft;
    }

    /// <summary>One little-endian <c>i16</c> of the bulk-copied <c>.fmd</c> image.</summary>
    /// <param name="model">The 298-byte flight model.</param>
    /// <param name="offset">The master-struct offset, which is also the file offset (§2 bulk copy).</param>
    private static short FlightModelWord(ReadOnlySpan<byte> model, int offset) =>
        unchecked((short)(model[offset] | (model[offset + 1] << 8)));

    /// <summary>A short, stable description for test output and debugging.</summary>
    public override string ToString() =>
        $"{Definition.Name} @({AttitudeRoll}, {AttitudePitch}, {AttitudeHeading}) " +
        $"{CurrentAirspeedFps} fps, {HitPoints} hp, flags=0x{(byte)StatusFlags:X2}";
}
