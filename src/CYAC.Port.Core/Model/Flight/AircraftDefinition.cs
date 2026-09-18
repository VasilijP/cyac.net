using CYAC.Port.Core.Data;
using CYAC.Port.Core.Schema;

namespace CYAC.Port.Core.Model.Flight;

/// <summary>
/// Everything an aircraft <b>is</b> before it flies: the parsed contents of its <c>.fmd</c> flight
/// model and <c>.fme</c> flight envelope.
/// </summary>
/// <remarks>
/// <para>
/// INT-only and immutable — authored content, not simulation state.  The runtime half lives in
/// <see cref="Aircraft"/>; the two are deliberately separate types because several offsets carry a
/// <c>.fmd</c> constant at load time and runtime state afterwards (the "dual-use" fields, which the
/// project's standing doctrine says a port should split).  Every such offset therefore appears on
/// <b>both</b> types, with the same original offset in both attributes.
/// </para>
/// <para>
/// <b>The file IS the struct.</b>  <c>flight_model_load_for_aircraft @0x2A112</c> loads the
/// <c>.fmd</c> with <c>ealib_load_asset(mode=0xFF, DS:SI=&amp;master)</c> (<c>image@0x2A145</c>),
/// which bulk-copies all 298 bytes 1:1 into <c>s_aircraft_master</c>: <c>FMD[X] → master[+X]</c>.
/// There is no field-by-field copy loop, which is why fields like HP have "no explicit writer".
/// So the offsets below are simultaneously <c>FlightModelDamageHeader</c> offsets and
/// <c>s_aircraft_master</c> offsets — the <see cref="OriginalFieldAttribute"/>s name the former and
/// the doc comments name the latter where the two field maps disagree.
/// </para>
/// <para>
/// The runtime reads the transformed tree, so a
/// definition is built from its <c>aircraft/&lt;name&gt;.json</c> document (<see cref="Load"/>).  The
/// document is self-describing — every tail field carries its own offset, width and name — and
/// <see cref="Data.AircraftDataCodec"/> turns it back into the two asset bodies, which is what
/// <see cref="RawFlightModel"/>/<see cref="RawEnvelope"/> hold and what <c>cyac-transform --verify</c>
/// proves byte-identical to the shipped assets.
/// </para>
/// </remarks>
[OriginalStruct("FlightModelDamageHeader")]
public sealed class AircraftDefinition
{
    /// <summary>Bytes in a <c>.fmd</c> file: 298 (<c>0x12A</c>).</summary>
    public const int FlightModelBytes = AircraftDataCodec.FlightModelBytes;

    /// <summary>Bytes in a <c>.fme</c> file: 504.</summary>
    public const int EnvelopeBytes = AircraftDataCodec.EnvelopeBytes;

    /// <summary>
    /// The six flyable aircraft, in <c>g_active_aircraft_idx</c> [0xC31A] order.
    /// </summary>
    /// <remarks>
    /// Not guessed: the literal contents of <c>g_aircraft_basename_table</c> at DGROUP+0x2A02, which
    /// <c>flight_model_load_for_aircraft</c> concatenates with ".FMD"/".FME"
    /// (<c>image@0x35EE</c>/<c>image@0x35F3</c>).
    /// </remarks>
    public static IReadOnlyList<string> FlyableBasenames { get; } =
        ["p51", "fw190", "f86", "mig15", "f4", "mig21"];

    // The transform reads them from pi.bin (the page the class table gives each flyable prototype)
    // into each aircraft/<basename>.json displayName, and DisplayName below carries the tree's.

    private AircraftDefinition(AircraftDefinitionDto document)
    {
        Name = document.Name ?? string.Empty;
        DisplayName = document.DisplayName ?? string.Empty;
        RawFlightModel = AircraftDataCodec.ToFlightModelBytes(document);
        RawEnvelope = AircraftDataCodec.ToEnvelopeBytes(document);

        List<AircraftInitBlockDto>? source = document.InitBlocks!;
        AircraftInitBlock[] blocks = new AircraftInitBlock[AircraftInitBlock.Count];
        for (int i = 0; i < blocks.Length; i++)
        {
            AircraftInitBlockDto b = source.Single(x => x.Index == i);
            blocks[i] = new AircraftInitBlock(
                i, b.Value, b.Working, (short)b.HiBound, (short)b.LoBound, (short)b.BaseDir, (short)b.DirStep);
        }

        InitBlocks = blocks;

        List<EnvelopeCurveDto>? records = document.Envelope!;
        EnvelopeCurve[] curves = new EnvelopeCurve[FlightEnvelope.CurveCount];
        int cornerX = 0;
        int cornerY = 0;
        for (int i = 0; i < curves.Length; i++)
        {
            EnvelopeCurveDto r = records[i];
            List<EnvelopePointDto>? slots = r.Points!;
            EnvelopePoint[] points = new EnvelopePoint[EnvelopeCurve.PointSlots];
            for (int k = 0; k < points.Length; k++)
            {
                points[k] = new EnvelopePoint((short)slots[k].AirspeedFps, (short)slots[k].AltitudeUnits);

                // flight_envelope_load's running maxima, over the AUTHORED points only and read
                // UNSIGNED (image@0x2A8xx) — the same accumulation FlightModelDecoder replicates.
                if (k >= r.PointCount)
                {
                    continue;
                }

                cornerX = Math.Max(cornerX, (ushort)(short)slots[k].AirspeedFps);
                cornerY = Math.Max(cornerY, (ushort)(short)slots[k].AltitudeUnits);
            }

            curves[i] = new EnvelopeCurve(
                i, (sbyte)r.LoadFactorG, (byte)r.PointCount, (byte)r.PeakIndex, (byte)r.HighSpeedIndex, points);
        }

        Envelope = new FlightEnvelope(curves, cornerX, cornerY);

        int Field(string name) => Tail(document, name);
        CrashThresholdA = (ushort)Field("perf_param_1");
        CrashThresholdB = (ushort)Field("tail_94");
        CrashThresholdC = (ushort)Field("tail_96");
        CrashThresholdD = (ushort)Field("tail_98");
        EnginePulseCount = (byte)Field("engine_pulse_count");
        InitialHitPoints = (short)Field("hp_initial");
        InitialHitPointReference = (short)Field("hp_reference");
        ThrottleAccelRate = (ushort)Field("perf_param_2a");
        ThrottleDecelRate = (ushort)Field("perf_param_2b");
        AfterburnerScale = (short)Field("supersonic_param");
        GrossWeightLb = (ushort)Field("gross_weight_lb");
        InitialFuelUnits = (ushort)Field("initial_fuel");
        FuelRate = (ushort)Field("armament_count");
        PerformanceLimit = (ushort)Field("perf_limit");
        MaxLoadFactorG = (short)Field("gload_max");
        MinLoadFactorG = (short)Field("gload_min");
        PitchAuthorityBase = (short)Field("gload_ctrl_base_dir");
        PitchAuthorityStep = (short)Field("gload_ctrl_dir_step");
        ResetSentinel = (short)Field("reset_sentinel");
        InducedDragCoefficient = (short)Field("control_rate");
        RollAuthorityGain = (short)Field("gear_limit");
        AirbrakeDragCoefficient = (short)Field("jet_flag_q8");
        InitialElevatorAuthority = (short)Field("tail_EE");
        InitialHeadingInput = (short)Field("tail_F2");
        GearDragCoefficient = (short)Field("tail_100");
        HoldAltitudeTimerInit = (ushort)Field("tail_104");
        ServiceCeilingFeet = Field("ceiling_ft_q8") >> 8;
        InitialStatusFlags = (AircraftStatusFlags)(byte)Field("status_flags_init");
    }

    /// <summary>The aircraft's asset basename (e.g. <c>"p51"</c>), or empty when parsed from raw bytes.</summary>
    public string Name { get; }

    /// <summary>
    /// The aircraft's display name, as its tree document carries it (the transform reads it from
    /// <c>pi.bin</c>), or empty when the document has none or the definition was parsed from raw
    /// bytes.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>The 298 raw <c>.fmd</c> bytes, exactly as the loader would bulk-copy them.</summary>
    public ReadOnlyMemory<byte> RawFlightModel { get; }

    /// <summary>The 504 raw <c>.fme</c> bytes.</summary>
    public ReadOnlyMemory<byte> RawEnvelope { get; }

    /// <summary>The nine 16-byte blocks at <c>0x00..0x8F</c> that seed the master struct's blocks.</summary>
    public IReadOnlyList<AircraftInitBlock> InitBlocks { get; }

    /// <summary>The parsed <c>.fme</c> — 14 curves plus the corner the loader accumulates.</summary>
    public FlightEnvelope Envelope { get; }

    // ---------------------------------------------------------------------------------------
    // The 154-byte scalar tail.
    // FlightModelDamageHeader; the rest carry their s_aircraft_master / FlightModelDecoder
    // citation in the doc comment (the bulk copy makes the two offset spaces identical).
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// <c>+0x92</c> — the first crash-validation threshold (300 props / 350 jets).
    /// </summary>
    /// <remarks>
    /// Two names, one word: <c>FlightModelDamageHeader.perf_param_1_u16</c> ("likely max-G clip",
    /// an earlier reading) and <c>s_aircraft_master.crash_threshold_a_u16</c> ("tested by
    /// <c>crash_conditions_valid</c>").  The port takes the runtime-traced reading.
    /// </remarks>
    [OriginalField("+0x92", "perf_param_1_u16")]
    public ushort CrashThresholdA { get; }

    /// <summary><c>+0x94</c> — crash-validation threshold; 50 in all six files (<c>crash_threshold_b_u16</c>, P39).</summary>
    public ushort CrashThresholdB { get; }

    /// <summary><c>+0x96</c> — crash-validation threshold; 100 in all six files (<c>crash_threshold_c_u16</c>, P39).</summary>
    public ushort CrashThresholdC { get; }

    /// <summary>
    /// <c>+0x98</c> — crash-validation threshold; 2585 in all six files (<c>crash_threshold_d_u16</c>, P39).
    /// </summary>
    /// <remarks>
    /// The scanner also names <c>crash_threshold_e_u8</c> at <c>+0x99</c>, which is this word's high
    /// byte (10) — see <see cref="CrashThresholdE"/>.
    /// </remarks>
    public ushort CrashThresholdD { get; }

    /// <summary><c>+0x99</c> — the unaligned high byte of <see cref="CrashThresholdD"/> (<c>crash_threshold_e_u8</c>, P39).</summary>
    public byte CrashThresholdE => (byte)(CrashThresholdD >> 8);

    /// <summary>
    /// <c>+0x9A</c> — 15 (P-51), 12 (FW-190), 0 (all four jets).
    /// </summary>
    /// <remarks>
    /// Two readings, one byte: <c>engine_pulse_count_u8</c> ("prop crankshaft count") and
    /// <c>s_aircraft_master.tilt_damage_factor_u8</c> (multiplied by 8 in the severe-tilt zone of
    /// <c>damage_recovery_or_tilt @0x2C184</c>).  The scanner claims they are "different structs";
    /// the bulk copy says they are the same byte.  <b>(open — reported)</b>: under the P40 reading
    /// every jet has a tilt-damage factor of zero.
    /// </remarks>
    [OriginalField("+0x9A", "engine_pulse_count_u8")]
    public byte EnginePulseCount { get; }

    /// <summary><c>+0xA4</c> — starting hit points; <b>100 for all six aircraft</b> (<c>hp_remaining_i16</c>, P70).</summary>
    /// <remarks>
    /// The value arrives by memcpy, which is why P75 found no instruction writing it — §P70-H1.
    /// </remarks>
    public short InitialHitPoints { get; }

    /// <summary><c>+0xA6</c> — the HP reference / speed-clamp bound; 0 on disk (<c>hp_reference_i16</c>, P70).</summary>
    public short InitialHitPointReference { get; }

    /// <summary><c>+0xA8</c> — thrust step-up rate (<c>throttle_accel_rate_i16</c>, P33): 10/10/30/20/40/40.</summary>
    /// <remarks>Round 13b reads the same word as <c>perf_param_2a_u16</c> ("gun-burst len?"); P33's runtime trace wins.</remarks>
    [OriginalField("+0xA8", "perf_param_2a_u16")]
    public ushort ThrottleAccelRate { get; }

    /// <summary><c>+0xAA</c> — thrust step-down rate (<c>throttle_decel_rate_i16</c>, P33); equals <see cref="ThrottleAccelRate"/> in all six files.</summary>
    [OriginalField("+0xAA", "perf_param_2b_u16")]
    public ushort ThrottleDecelRate { get; }

    /// <summary>
    /// <c>+0xB6</c> — the afterburner yaw-drive scale (<c>afterburner_scale_i16</c>, P44), read only
    /// while <see cref="AircraftStatusFlags.Afterburner"/> is set.
    /// </summary>
    /// <remarks>
    /// Non-zero for exactly two aircraft: the F-4E (512) and the MiG-21MF (460) — the only two of the
    /// six with a real afterburner.  <c>FlightModelDecoder</c> calls this field
    /// <c>supersonic_param</c>; the P44 name is the runtime-traced one and the data agrees with it.
    /// </remarks>
    public short AfterburnerScale { get; }

    /// <summary>
    /// <c>+0xBC</c> — gross weight in pounds (7125 / 7055 / 12000 / 7000 / 29500 / 11500).
    /// </summary>
    /// <remarks>
    /// <b>Dual-use, split by the port.</b> At load this is the <c>.fmd</c> constant <c>perf_scalar_max_u16</c>; it
    /// is a weight in pounds, which the game's own manual settles — its data blocks read "Weight; 7,125 lbs" for
    /// the P-51D and "Weight: 7,055 lbs" for the FW-190A-8, the exact values here (and <c>pi.bin</c>'s
    /// <c>WEIGHT</c> for those two).  In flight the same word pair is <see cref="Aircraft.MassAccumulator"/>'s low
    /// half (<c>mass_accum_lo_i16</c>, ex-<c>heading_accum_lo_i16</c> — RENAMED R1, K2 §4.4 / K3 §4.6).  The
    /// loader reads it exactly once, in the <see cref="InitialHeadingAngularRate"/> formula, before any
    /// integration starts.
    /// </remarks>
    [OriginalField("+0xBC", "perf_scalar_max_u16")]
    public ushort GrossWeightLb { get; }

    /// <summary>
    /// <c>+0xC8</c> — the starting fuel quantity; the loader stores <c>this &lt;&lt; 8</c> into the
    /// fuel field (<see cref="InitialFuelQ8"/>).
    /// </summary>
    /// <remarks>
    /// The scanner name is <c>fuel_initial_lb_u16</c> (1614 / 1632 / 2040 / 1680 / 9293 / 3105, round
    /// 13b) — K3 §4.6 proved the field is the initial fuel LOAD in pounds; 1,614 lb is the P-51D's
    /// exact internal capacity.  The only decoded use is as fuel: <c>image@0x2A179..0x2A189</c> loads
    /// it, shifts left by 8 and writes master <c>+0xC0 fuel_remaining_i32</c>, which
    /// <c>aircraft_throttle_fuel_step @0x2A5EE</c> then burns down.  <c>FlightModelDecoder</c> renamed
    /// it <c>initial_fuel</c> on that evidence and the port follows.  <b>This is the only
    /// <c>FlightModelDamageHeader</c> offset with no <c>s_aircraft_master</c> counterpart</b>.
    /// </remarks>
    [OriginalField("+0xC8", "fuel_initial_lb_u16")]
    public ushort InitialFuelUnits { get; }

    /// <summary>
    /// <c>+0xCC</c> — the per-aircraft fuel consumption rate (6 / 6 / 8 / 6 / 35 / 17).
    /// </summary>
    /// <remarks>
    /// <c>s_aircraft_master.fuel_rate_u8</c> (P33) reads the low byte of the word an earlier reading calls
    /// <c>armament_count_u16</c>.  The port takes the runtime-traced reading; as an armament count
    /// the F-4E's 35 has no plausible meaning, whereas as a fuel rate the ordering (thirsty F-4,
    /// frugal props) does.
    /// </remarks>
    [OriginalField("+0xCC", "armament_count_u16")]
    public ushort FuelRate { get; }

    /// <summary>
    /// <c>+0xD0</c> — the performance limit the loader multiplies into
    /// <see cref="InitialHeadingAngularRate"/> (99 / 89 / 138 / 153 / 225 / 199).
    /// </summary>
    /// <remarks>
    /// Struck: an image-wide scan found zero runtime writers of master <c>+0xD0</c>; it is
    /// one <c>.fmd</c> constant with two readers, <c>flight_model_load_for_aircraft @0x2A1CC</c> and
    /// <c>vel_roll_aoa_correction_accum @0x2ACE8</c>.  <see cref="Aircraft.PerformanceLimit"/> is
    /// therefore a pass-through to this value, not a separate runtime field.
    /// </remarks>
    [OriginalField("+0xD0", "perf_limit_u16")]
    public ushort PerformanceLimit { get; }

    /// <summary>
    /// <c>+0xDE</c> — the maximum load factor in G: +7 prop, +8 Korea jet, +9 Vietnam jet.
    /// </summary>
    /// <remarks>
    /// This is the pitch <see cref="ControlAxisState.HiBound"/> (block base <c>+0xD6</c>, so
    /// <c>+0xD6 + 0x08 = +0xDE</c>) and the upper bound of <c>aoa_range_scanner @0x2B12A</c>'s sweep
    /// (<c>aoa_scan_max_i16</c>, P33).  Corrected here — the reset sentinel is <c>+0xE6</c>, not
    /// <c>+0xDE</c>; and the round-13b <c>era_tier_u16</c> reading is the same three values seen as
    /// a per-era bucket.  Reported in B4 §4.
    /// </remarks>
    [OriginalField("+0xDE", "era_tier_u16")]
    public short MaxLoadFactorG { get; }

    /// <summary><c>+0xE0</c> — the minimum load factor in G: −4 in all six files (<c>aoa_scan_min_i16</c>, P33; pitch <c>lo_bound</c>).</summary>
    public short MinLoadFactorG { get; }

    /// <summary>
    /// <c>+0xE2</c> — the pitch axis' directional base step (6 / 6 / 3 / 3 / 10 / 10).
    /// </summary>
    /// <remarks>
    /// <b>Dual-use, split by the port.</b> Round 13b named this <c>stall_low_u16</c> from a data
    /// pattern; the verified role is the AoA/pitch <see cref="ControlAxisState.BaseDir"/>
    /// (<c>aoa_ctrl_base_dir_i16</c>, read only through the control-block pointer at
    /// <c>image@0x2B9CC</c>, never re-written).  The runtime half is
    /// <see cref="Aircraft.PitchAuthorityBase"/>.  The "stall" semantic remains an unverified
    /// hypothesis (warning box).
    /// </remarks>
    [OriginalField("+0xE2", "stall_low_u16")]
    public short PitchAuthorityBase { get; }

    /// <summary>
    /// <c>+0xE4</c> — the pitch axis' sign-flip kick / zero-stick decay target (8 / 8 / 9 / 9 / 10 / 10).
    /// </summary>
    /// <remarks>As <see cref="PitchAuthorityBase"/>: <c>stall_high_u16</c> / <c>aoa_ctrl_dir_step_i16</c>.</remarks>
    [OriginalField("+0xE4", "stall_high_u16")]
    public short PitchAuthorityStep { get; }

    /// <summary><c>+0xE6</c> — the reset sentinel, <c>−1</c> on disk in all six files (<c>reset_sentinel_u16</c>, P22).</summary>
    /// <remarks><c>aircraft_pose_set</c> re-writes <c>0xFFFF</c> here on every teleport (<c>image@0x2A3F1</c>).</remarks>
    public short ResetSentinel { get; }

    /// <summary>
    /// <c>+0xEC</c> — the induced-drag coefficient applied per unit of g-load excess
    /// (12 / 15 / 24 / 18 / 60 / 55).
    /// </summary>
    /// <remarks>
    /// Reframed: the sole image-wide reference is
    /// <c>image@0x2AD62</c> in <c>vel_roll_aoa_correction_accum</c>, where it multiplies the
    /// <c>|n| − 1.00 G</c> excess — the threshold is the hard-coded <c>0x100</c>, this is the
    /// multiplier.
    /// </remarks>
    [OriginalField("+0xEC", "control_rate_u16")]
    public short InducedDragCoefficient { get; }

    /// <summary>
    /// <c>+0xF0</c> — roll authority gain (230 / 256 / 230 / 256 / 640 / 537).
    /// </summary>
    /// <remarks>
    /// Round 13b reads it as <c>gear_limit_u16</c>; the runtime field is
    /// <c>s_aircraft_master.roll_gain_i16</c> (P45), which
    /// <c>aircraft_master_damage_apply2 @0x2A4F8</c> degrades on a
    /// <see cref="AircraftDamageFlags.RollAuthority"/> hit (<c>image@0x2A54D</c>) — so it is runtime
    /// state as well as a constant, and <see cref="Aircraft.RollAuthorityGain"/> is the mutable half.
    /// </remarks>
    [OriginalField("+0xF0", "gear_limit_u16")]
    public short RollAuthorityGain { get; }

    /// <summary>
    /// <c>+0xFA</c> — the airbrake drag coefficient: 0 for both props, <c>0x100</c> (1.0 in Q8.8)
    /// for all four jets.
    /// </summary>
    /// <remarks>
    /// Round 13b calls it <c>afterburner_avail_u16</c>, but only two of the four jets have an
    /// afterburner (see <see cref="AfterburnerScale"/>), while all four have speed brakes and neither
    /// prop does.  The runtime role is pinned: the other image-wide reference to
    /// <c>ctrl_surface_drag_a_i16</c> is <c>cmp word [bx+0xfa],0</c> at <c>image@0x2A4AB</c>, inside
    /// the key-'b' (airbrake) handler, right after <c>xor byte [bx+0x124],8</c>.
    /// </remarks>
    [OriginalField("+0xFA", "afterburner_avail_u16")]
    public short AirbrakeDragCoefficient { get; }

    /// <summary><c>+0xEE</c> — starting elevator authority; <c>0x100</c> (1.0 in Q8.8) in all six files (<c>ctrl_elevator_i16</c>, P36).</summary>
    public short InitialElevatorAuthority { get; }

    /// <summary><c>+0xF2</c> — the starting heading-control input; <c>0x100</c> in all six files (<c>ctrl_heading_input_i16</c>, P44).</summary>
    public short InitialHeadingInput { get; }

    /// <summary><c>+0x100</c> — the landing-gear drag coefficient; 153 in all six files (<c>ctrl_surface_drag_d_i16</c>).</summary>
    public short GearDragCoefficient { get; }

    /// <summary><c>+0x104</c> — the reset source for the hold-altitude timer; 128 in all six files (<c>hold_altitude_timer_init_u16</c>, P39).</summary>
    public ushort HoldAltitudeTimerInit { get; }

    /// <summary>
    /// <c>+0x112</c> — service ceiling in feet (41 900 / 37 400 / 47 000 / 49 500 / 56 000 / 58 000).
    /// </summary>
    /// <remarks>
    /// Stored as a Q8 i32, so the value is <c>FMD[+0x112] &gt;&gt; 8</c>
    /// (<c>FlightModelDecoder.FmdFile.CeilingFt</c>).  still records this word pair as "u32
    /// per-aircraft fingerprint (purpose unknown)" — reported in B4 §4.
    /// </remarks>
    public int ServiceCeilingFeet { get; }

    /// <summary>
    /// <c>+0x124</c> — the status byte the <c>.fmd</c> ships: <c>0x20</c> props, <c>0x60</c> jets.
    /// </summary>
    /// <remarks>
    /// Round 13b reads the same two bytes as <c>prop_count_u16</c> (32 / 96); the runtime treats
    /// <c>+0x124</c> as a bit field throughout and the loader ORs
    /// <see cref="AircraftStatusFlags.LandingGear"/> into it at <c>image@0x2A190</c>.
    /// <c>+0x125</c> is zero in all six files.
    /// </remarks>
    [OriginalField("+0x124", "prop_count_u16")]
    public AircraftStatusFlags InitialStatusFlags { get; }

    // ---------------------------------------------------------------------------------------
    // Derived facts
    // ---------------------------------------------------------------------------------------

    /// <summary>Block 0's <c>+0x08</c> word — the maximum forward speed in feet per second.</summary>
    /// <remarks>683 / 630 / 977 / 1029 / 2132 / 1974 fps (<c>FlightModelDecoder.FmdFile.MaxForwardSpeedFps</c>).</remarks>
    public int MaxForwardSpeedFps => InitBlocks[0].HiBound;

    /// <summary>
    /// Block 2's <c>+0x0C</c> word (file offset <c>0x2C</c>) — 80 in all six files.
    /// </summary>
    /// <remarks>
    /// REFUTED — the <c>[bx+0x2C]</c> at <c>image@0x2A16C</c> is NOT an FMD offset.
    /// Three instructions earlier the loader loads its own far-ptr ARG into <c>ES:BX</c> (<c>mov es,dx
    /// / mov bx,ax</c> @<c>image@0x2A165</c>) — the PLAYER WORLD OBJECT, the same K0-F1 pointer the
    /// <c>+0x11A</c> misnomer was about — and then dereferences the object's class pointer (<c>mov
    /// bx,es:[bx]</c> @<c>image@0x2A169</c>).  So <c>master[+0x116]</c> is the CLASS RECORD's
    /// <c>+0x2C</c>, i.e. <see cref="Model.World.ClassRecord.GroundClearance"/>, and the shipped P-51
    /// value is 5,376 — not this field's 80.  Confirmed against the genuine machine's first Test
    /// Flight record (<c>FlightColdStartTests</c>) and by what the byte is FOR:
    /// <c>flight_check_alive_or_active @image@0x2C22C</c> returns 1 iff <c>AirspeedB + AirspeedA &gt;=
    /// playerObject.Y</c>, i.e. the aircraft is within its own wheel height of the ground.  This
    /// property is kept because the <c>.fmd</c> word exists; nothing reads it any more.
    /// </remarks>
    public short InitialAirspeedB => InitBlocks[2].BaseDir;

    /// <summary>What the loader writes to master <c>+0xC0</c>: <c><see cref="InitialFuelUnits"/> &lt;&lt; 8</c>.</summary>
    /// <remarks>
    /// <c>image@0x2A179..0x2A189</c>.  The original sign-extends the word before shifting (<c>cdq</c>
    /// at <c>image@0x2A17D</c>), so a value ≥ <c>0x8000</c> would start the aircraft with negative
    /// fuel; no shipped file comes near that.
    /// </remarks>
    public int InitialFuelQ8 => (short)InitialFuelUnits << 8;

    /// <summary>
    /// What the loader writes to master <c>+0xD2</c>:
    /// <c>(<see cref="GrossWeightLb"/> × <see cref="PerformanceLimit"/>) &gt;&gt; 11</c>.
    /// </summary>
    /// <remarks>
    /// <c>image@0x2A1C4..0x2A1DF</c> — an unsigned 32-bit multiply (<c>mulu32</c>) followed by an
    /// arithmetic shift right of 11.  P-51: <c>(7125 × 99) &gt;&gt; 11 = 344</c>; F-4E:
    /// <c>(29500 × 225) &gt;&gt; 11 = 3240</c>.
    /// </remarks>
    public short InitialHeadingAngularRate =>
        unchecked((short)(int)((uint)GrossWeightLb * PerformanceLimit >> 11));

    /// <summary>True when the aircraft is one of the four jets (<see cref="AirbrakeDragCoefficient"/> ≠ 0).</summary>
    public bool IsJet => AirbrakeDragCoefficient != 0;

    /// <summary>True when the aircraft has an afterburner — the F-4E and the MiG-21MF only.</summary>
    public bool HasAfterburner => AfterburnerScale != 0;

    /// <summary>
    /// Loads one aircraft from its transformed <c>aircraft/&lt;name&gt;.json</c> document — the
    /// runtime's ONLY path (transform-plan L2: the port reads the data tree, never a <c>.lib</c>).
    /// </summary>
    /// <remarks>
    /// It was an adapter over <c>CYAC.Formats.FlightModelDecoder</c>, and the data-tree rule retires that
    /// reference from the runtime.  The document already carries every block, every named tail field
    /// with its offset and width, and all 14 curves including their filler point slots, so nothing
    /// is lost; <see cref="RawFlightModel"/> and <see cref="RawEnvelope"/> are rebuilt from it
    /// through <see cref="Data.AircraftDataCodec"/>, the same code <c>cyac-transform --inverse</c>
    /// uses, and <c>--verify</c> proves those bytes identical to the shipped assets.
    /// </remarks>
    /// <param name="document">The parsed aircraft document.</param>
    /// <exception cref="InvalidDataException">The document is incomplete or malformed.</exception>
    public static AircraftDefinition Load(AircraftDefinitionDto document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.InitBlocks is not { Count: AircraftInitBlock.Count })
        {
            throw new InvalidDataException(
                $"aircraft '{document.Name}': expected {AircraftInitBlock.Count} init blocks, found " +
                $"{document.InitBlocks?.Count ?? 0}");
        }

        if (document.Envelope is not { Count: FlightEnvelope.CurveCount })
        {
            throw new InvalidDataException(
                $"aircraft '{document.Name}': expected {FlightEnvelope.CurveCount} envelope curves, " +
                $"found {document.Envelope?.Count ?? 0}");
        }

        return new AircraftDefinition(document);
    }

    // The tail is a list of named fields rather than a map, because its ORDER and its per-field
    // offsets are what make the document invertible; a lookup by name is what this type wants.
    private static int Tail(AircraftDefinitionDto document, string name)
    {
        foreach (AircraftTailFieldDto field in document.Tail ?? [])
        {
            if (string.Equals(field.Name, name, StringComparison.Ordinal))
            {
                return field.Value;
            }
        }

        throw new InvalidDataException(
            $"aircraft '{document.Name}': the flight model has no tail field named '{name}'");
    }

    /// <summary>A short, stable description for test output and debugging.</summary>
    public override string ToString() =>
        $"{(Name.Length == 0 ? "aircraft" : Name)}: {MaxForwardSpeedFps} fps, " +
        $"{ServiceCeilingFeet} ft, {MinLoadFactorG}..{MaxLoadFactorG} G";
}
