using System.Buffers.Binary;
using CYAC.Port.Core.Model.Flight;

namespace CYAC.Port.Core.Sim.Flight;

/// <summary>
/// One field of <c>s_aircraft_master</c> that the port's <see cref="Aircraft"/> models, and where it
/// lives in the original's 298-byte struct.
/// </summary>
/// <param name="Offset">Byte offset inside <c>s_aircraft_master</c>.</param>
/// <param name="Width">1, 2 or 4 bytes.</param>
/// <param name="OriginalName">
/// </param>
/// <param name="ReadOnly">
/// True when the port derives the field from <see cref="AircraftDefinition"/> instead of storing it
/// (the original has no runtime writer for it either).
/// </param>
public readonly record struct AircraftMasterField(
    int Offset, int Width, string OriginalName, bool ReadOnly);

/// <summary>
/// Maps the original's <c>s_aircraft_master</c> byte image onto the port's <see cref="Aircraft"/>,
/// both ways, and says exactly which bytes the port does NOT model.
/// </summary>
/// <remarks>
/// <para>
/// This is the verification bridge: a K0 flight-trace record carries 298 raw bytes of the live
/// struct, and a stage test has to turn them into an <see cref="Aircraft"/>, run a port function, and
/// compare the result against the next record.  The map is B4's
/// <c>[OriginalField]</c> attributes, transcribed here as data so a test can enumerate it.
/// </para>
/// <para>
/// <b>The unmodelled bytes are the point.</b>  <see cref="UnmodelledOffsets"/> is not a to-do list —
/// it is the evidence a stage test needs to make the assertion that matters: <i>the stage did not
/// touch a byte the port does not model</i>.  A byte that moves in a trace but is not in
/// <see cref="ModelledFields"/> is a field B4 missed, and the test says so by name and offset.
/// </para>
/// <para>
/// <b>Not modelled by construction:</b> the two far pointers at <c>+0x11A..+0x121</c>.
/// <c>+0x11A/+0x11C</c> is the player world object (K0 finding F1) and <c>+0x11E/+0x120</c> is the FME
/// table — in the port those are a <c>WorldObject</c> reference and
/// <see cref="AircraftDefinition.Envelope"/>, so their BYTES have no port counterpart and a
/// round-trip cannot reproduce them.  They are listed as unmodelled and skipped deliberately.
/// </para>
/// </remarks>
public static class AircraftMasterCodec
{
    /// <summary>Bytes of <c>s_aircraft_master</c>: 298 (<c>0x12A</c>).</summary>
    public const int MasterBytes = AircraftDefinition.FlightModelBytes;

    private sealed record Entry(
        int Offset,
        int Width,
        string Name,
        Func<Aircraft, int> Get,
        Action<Aircraft, int>? Set);

    private static readonly Entry[] Table = BuildTable();

    private static readonly bool[] Modelled = BuildMask();

    /// <summary>Every field the port models, in offset order.</summary>
    public static IReadOnlyList<AircraftMasterField> ModelledFields { get; } =
        [.. Table.Select(e => new AircraftMasterField(e.Offset, e.Width, e.Name, e.Set is null))];

    /// <summary>Every byte offset of the 298 the port does NOT model, ascending.</summary>
    public static IReadOnlyList<int> UnmodelledOffsets { get; } =
        [.. Enumerable.Range(0, MasterBytes).Where(o => !Modelled[o])];

    /// <summary>Whether a given byte of the struct is covered by a modelled field.</summary>
    /// <param name="offset">A byte offset inside <c>s_aircraft_master</c>.</param>
    public static bool IsModelled(int offset) =>
        (uint)offset < MasterBytes && Modelled[offset];

    /// <summary>
    /// Builds the aircraft a 298-byte master snapshot describes: <see cref="Aircraft.Load"/> for the
    /// shape, then every modelled field overwritten from the bytes.
    /// </summary>
    /// <param name="master">The snapshot — at least <see cref="MasterBytes"/> bytes.</param>
    /// <param name="definition">The flying aircraft's parsed <c>.fmd</c>/<c>.fme</c> pair.</param>
    public static Aircraft FromMasterBytes(ReadOnlySpan<byte> master, AircraftDefinition definition)
    {
        Aircraft aircraft = Aircraft.Load(definition);
        ApplyMasterBytes(master, aircraft);
        return aircraft;
    }

    /// <summary>Overwrites every modelled field of an existing aircraft from a master snapshot.</summary>
    /// <param name="master">The snapshot — at least <see cref="MasterBytes"/> bytes.</param>
    /// <param name="aircraft">The aircraft to fill.</param>
    public static void ApplyMasterBytes(ReadOnlySpan<byte> master, Aircraft aircraft)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        RequireLength(master.Length);
        foreach (Entry entry in Table)
        {
            entry.Set?.Invoke(aircraft, Read(master, entry.Offset, entry.Width));
        }
    }

    /// <summary>
    /// Writes every modelled field back into a 298-byte buffer, leaving the unmodelled bytes exactly
    /// as they were — so a caller that starts from the original snapshot gets a byte-identical buffer
    /// iff the port models the state faithfully.
    /// </summary>
    /// <param name="aircraft">The aircraft to serialise.</param>
    /// <param name="destination">The buffer — at least <see cref="MasterBytes"/> bytes.</param>
    public static void WriteMasterBytes(Aircraft aircraft, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        RequireLength(destination.Length);
        foreach (Entry entry in Table)
        {
            Write(destination, entry.Offset, entry.Width, entry.Get(aircraft));
        }
    }

    /// <summary>Reads one field's raw bits out of a master snapshot.</summary>
    /// <param name="master">The snapshot.</param>
    /// <param name="offset">The field's offset.</param>
    /// <param name="width">1, 2 or 4.</param>
    public static int Read(ReadOnlySpan<byte> master, int offset, int width) => width switch
    {
        1 => master[offset],
        2 => BinaryPrimitives.ReadUInt16LittleEndian(master.Slice(offset, 2)),
        4 => BinaryPrimitives.ReadInt32LittleEndian(master.Slice(offset, 4)),
        _ => throw new ArgumentOutOfRangeException(nameof(width), width, "field width must be 1, 2 or 4"),
    };

    private static void Write(Span<byte> destination, int offset, int width, int value)
    {
        switch (width)
        {
            case 1:
                destination[offset] = unchecked((byte)value);
                break;
            case 2:
                BinaryPrimitives.WriteUInt16LittleEndian(
                    destination.Slice(offset, 2), unchecked((ushort)value));
                break;
            default:
                BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset, 4), value);
                break;
        }
    }

    private static void RequireLength(int length)
    {
        if (length < MasterBytes)
        {
            throw new ArgumentException(
                $"an s_aircraft_master window is {MasterBytes} bytes; got {length}");
        }
    }

    private static bool[] BuildMask()
    {
        bool[] mask = new bool[MasterBytes];
        foreach (Entry entry in Table)
        {
            for (int i = 0; i < entry.Width; i++)
            {
                mask[entry.Offset + i] = true;
            }
        }

        return mask;
    }

    private static Entry[] BuildTable()
    {
        List<Entry> entries = new List<Entry>();

        // --- the three 16-byte velocity blocks --------------
        entries.AddRange(Velocity(0x00, "vel_forward", a => a.ForwardVelocity));
        entries.AddRange(Velocity(0x10, "state_10", a => a.AngularVelocityA));
        entries.AddRange(Velocity(0x20, "state_20", a => a.AngularVelocityB));

        // --- the three s_ctrl_state_block overlays ------------------------------------------
        entries.AddRange(ControlAxis(0x30, "state_30", a => a.RollDeadAxis));
        entries.AddRange(ControlAxis(0x50, "state_50", a => a.RollAliveAxis));
        entries.AddRange(ControlAxis(0xD6, "ctrl_aoa_block", a => a.PitchAxis));

        // The FOURTH control block: +0x40 is driven by ctrl_axis_alive_normalize from the relax
        // arm (image@0x2B5C0) and stored into by the active arm (image@0x2B543..0x2B556).  Its
        // working slot at +0x44 IS Aircraft.HeadingFromAoa, so the two are one entry, not two.
        entries.AddRange(ControlAxis(0x40, "state_40", a => a.HeadingAoaAxis));

        // --- position and its per-axis wrap bounds -------------------------------------------
        entries.Add(new(0x60, 4, "attitude_roll_q8_i32", a => a.AttitudeRoll, (a, v) => a.AttitudeRoll = v));
        entries.Add(new(0x64, 4, "attitude_roll_working_i32", a => a.AttitudeRollWorking, (a, v) => a.AttitudeRollWorking = v));
        entries.Add(new(0x68, 2, "attitude_roll_limit_upper_i16", a => a.AttitudeRollLimitUpper, (a, v) => a.AttitudeRollLimitUpper = S16(v)));
        entries.Add(new(0x6A, 2, "attitude_roll_limit_lower_i16", a => a.AttitudeRollLimitLower, (a, v) => a.AttitudeRollLimitLower = S16(v)));
        entries.Add(new(0x70, 4, "attitude_pitch_q8_i32", a => a.AttitudePitch, (a, v) => a.AttitudePitch = v));
        entries.Add(new(0x74, 4, "attitude_pitch_working_i32", a => a.AttitudePitchWorking, (a, v) => a.AttitudePitchWorking = v));
        entries.Add(new(0x78, 2, "attitude_pitch_limit_upper_i16", a => a.AttitudePitchLimitUpper, (a, v) => a.AttitudePitchLimitUpper = S16(v)));
        entries.Add(new(0x7A, 2, "attitude_pitch_limit_lower_i16", a => a.AttitudePitchLimitLower, (a, v) => a.AttitudePitchLimitLower = S16(v)));
        entries.Add(new(0x80, 4, "attitude_heading_q8_i32", a => a.AttitudeHeading, (a, v) => a.AttitudeHeading = v));
        entries.Add(new(0x88, 2, "attitude_heading_limit_upper_i16", a => a.AttitudeHeadingLimitUpper, (a, v) => a.AttitudeHeadingLimitUpper = S16(v)));
        entries.Add(new(0x8A, 2, "attitude_heading_limit_lower_i16", a => a.AttitudeHeadingLimitLower, (a, v) => a.AttitudeHeadingLimitLower = S16(v)));

        // --- ground-contact survival thresholds (K4;
        entries.Add(new(0x92, 2, "crash_threshold_a_u16", a => a.CrashAirspeedLimit, (a, v) => a.CrashAirspeedLimit = S16(v)));
        entries.Add(new(0x94, 2, "crash_threshold_b_u16", a => a.CrashAngularRateLimit, (a, v) => a.CrashAngularRateLimit = S16(v)));
        entries.Add(new(0x96, 2, "crash_threshold_c_u16", a => a.CrashDescentRateLimit, (a, v) => a.CrashDescentRateLimit = S16(v)));
        entries.Add(new(0x98, 1, "crash_threshold_d_u16", a => (byte)a.CrashPitchLimitDegrees, (a, v) => a.CrashPitchLimitDegrees = unchecked((sbyte)v)));
        entries.Add(new(0x99, 1, "crash_threshold_e_u8", a => (byte)a.CrashRollLimitDegrees, (a, v) => a.CrashRollLimitDegrees = unchecked((sbyte)v)));
        entries.Add(new(0x9A, 1, "tilt_damage_factor_u8", a => (byte)a.GroundTiltFactor, (a, v) => a.GroundTiltFactor = unchecked((sbyte)v)));

        // --- energy: thrust, throttle, fuel --------------------------------------------------------
        entries.Add(new(0x9C, 4, "throttle_current_lo_i32", a => a.ThrustFloor, (a, v) => a.ThrustFloor = v));
        entries.Add(new(0xA0, 4, "throttle_target_lo_i32", a => a.ThrottleTarget, (a, v) => a.ThrottleTarget = v));
        entries.Add(new(0xA4, 2, "hp_remaining_i16", a => a.HitPoints, (a, v) => a.HitPoints = S16(v)));
        entries.Add(new(0xA6, 2, "hp_reference_i16", a => a.HitPointReference, (a, v) => a.HitPointReference = S16(v)));
        entries.Add(new(0xA8, 2, "throttle_accel_rate_i16", a => a.ThrottleAccelRate, (a, v) => a.ThrottleAccelRate = S16(v)));
        entries.Add(new(0xAA, 2, "throttle_decel_rate_i16", a => a.ThrottleDecelRate, (a, v) => a.ThrottleDecelRate = S16(v)));
        entries.Add(new(0xB6, 2, "afterburner_scale_i16", a => a.AfterburnerScale, (a, v) => a.AfterburnerScale = S16(v)));
        entries.Add(new(0xBC, 4, "mass_accum_lo_i16", a => a.MassAccumulator, (a, v) => a.MassAccumulator = v));
        entries.Add(new(0xC0, 4, "fuel_remaining_i32", a => a.Fuel, (a, v) => a.Fuel = v));
        entries.Add(new(0xCA, 2, "fuel_tick_accum_i16", a => a.FuelTickAccumulator, (a, v) => a.FuelTickAccumulator = S16(v)));
        entries.Add(new(0xCC, 1, "fuel_rate_u8", a => a.FuelRate, (a, v) => a.FuelRate = unchecked((byte)v)));

        // --- attitude accumulators -----------------------------------------------------------------
        // +0xD0 has NO runtime writer image-wide, so the port derives it from the .fmd and
        // the codec treats it as read-only: a round-trip still proves the two agree.
        entries.Add(new(0xD0, 2, "roll_rate_i16", a => a.PerformanceLimit, null));
        entries.Add(new(0xD2, 2, "heading_angular_rate_i16", a => a.HeadingAngularRate, (a, v) => a.HeadingAngularRate = S16(v)));
        entries.Add(new(0xD4, 2, "mass_div32_i16", a => a.MassDiv32, (a, v) => a.MassDiv32 = S16(v)));

        // --- envelope scan results and the reset sentinel -------------------------------------------
        entries.Add(new(0xE6, 2, "reset_sentinel_u16", a => a.ResetSentinel, (a, v) => a.ResetSentinel = S16(v)));
        entries.Add(new(0xE8, 2, "aoa_valid_min_i16", a => a.ValidLoadFactorMin, (a, v) => a.ValidLoadFactorMin = S16(v)));
        entries.Add(new(0xEA, 2, "aoa_valid_max_i16", a => a.ValidLoadFactorMax, (a, v) => a.ValidLoadFactorMax = S16(v)));
        entries.Add(new(0xEC, 2, "aoa_sensitivity_i16", a => a.InducedDragCoefficient, (a, v) => a.InducedDragCoefficient = S16(v)));

        // --- control-surface authority and drag -----------------------------------------------------
        entries.Add(new(0xEE, 2, "ctrl_elevator_i16", a => a.ElevatorAuthority, (a, v) => a.ElevatorAuthority = S16(v)));
        entries.Add(new(0xF0, 2, "roll_gain_i16", a => a.RollAuthorityGain, (a, v) => a.RollAuthorityGain = S16(v)));
        entries.Add(new(0xF2, 2, "ctrl_heading_input_i16", a => a.HeadingInput, (a, v) => a.HeadingInput = S16(v)));
        entries.Add(new(0xFA, 2, "ctrl_surface_drag_a_i16", a => a.AirbrakeDragCoefficient, (a, v) => a.AirbrakeDragCoefficient = S16(v)));
        entries.Add(new(0x100, 2, "ctrl_surface_drag_d_i16", a => a.GearDragCoefficient, (a, v) => a.GearDragCoefficient = S16(v)));

        // The two remaining drag coefficients and the gear authority bonus, all read by the
        // velocity-dynamics subtree (image@0x2ADFB / image@0x2AD8F / image@0x2AE33).
        entries.Add(new(0xFC, 2, "ctrl_surface_drag_b_i16", a => a.AirbrakeNearGroundDragCoefficient, (a, v) => a.AirbrakeNearGroundDragCoefficient = S16(v)));
        entries.Add(new(0xFE, 2, "ctrl_surface_drag_c_i16", a => a.LandingGearDragCoefficient, (a, v) => a.LandingGearDragCoefficient = S16(v)));
        entries.Add(new(0x102, 2, "gear_control_authority_bonus_q8", a => a.GearControlAuthorityBonusQ8, (a, v) => a.GearControlAuthorityBonusQ8 = S16(v)));

        // --- the three-state machine's timers and the low-speed advisory thresholds ------------
        entries.Add(new(0xF4, 2, "state_F4_i16", a => a.StallCountdownReload, (a, v) => a.StallCountdownReload = S16(v)));
        entries.Add(new(0xF6, 2, "state_F6_i16", a => a.PullUpCountdownReload, (a, v) => a.PullUpCountdownReload = S16(v)));
        entries.Add(new(0xF8, 2, "state_f8_u16", a => a.StateCountdown, (a, v) => a.StateCountdown = S16(v)));
        entries.Add(new(0x108, 2, "state_108_i16", a => a.LowSpeedWarningThreshold, (a, v) => a.LowSpeedWarningThreshold = S16(v)));
        entries.Add(new(0x10A, 2, "state_10A_i16", a => a.LowSpeedWarningLimit, (a, v) => a.LowSpeedWarningLimit = S16(v)));
        entries.Add(new(0x10C, 2, "state_10C_i16", a => a.LowSpeedElapsed, (a, v) => a.LowSpeedElapsed = S16(v)));
        entries.Add(new(0x114, 2, "state_114_i16", a => a.AltitudeCutoffForHeadingInput, (a, v) => a.AltitudeCutoffForHeadingInput = S16(v)));

        // --- timers, airspeed caps, flags, envelope corner ------------------------------------------
        entries.Add(new(0x104, 2, "hold_altitude_timer_init_u16", a => a.HoldAltitudeTimerInit, (a, v) => a.HoldAltitudeTimerInit = U16(v)));
        entries.Add(new(0x106, 2, "hold_altitude_timer_u16", a => a.HoldAltitudeTimer, (a, v) => a.HoldAltitudeTimer = U16(v)));
        entries.Add(new(0x10E, 4, "airspeed_a_i32", a => a.AirspeedA, (a, v) => a.AirspeedA = v));
        // The field is the aircraft CLASS's GROUND CLEARANCE;
        entries.Add(new(0x116, 4, "class_ground_clearance_i32", a => a.AirspeedB, (a, v) => a.AirspeedB = v));
        entries.Add(new(0x122, 1, "active_flag_u8", a => a.ActiveState, (a, v) => a.ActiveState = unchecked((byte)v)));
        entries.Add(new(0x123, 1, "damage_flags_u8", a => (byte)a.DamageFlags, (a, v) => a.DamageFlags = (AircraftDamageFlags)unchecked((byte)v)));
        entries.Add(new(0x124, 1, "status_flags_u8", a => (byte)a.StatusFlags, (a, v) => a.StatusFlags = (AircraftStatusFlags)unchecked((byte)v)));
        entries.Add(new(0x126, 2, "envelope_corner_x", a => a.EnvelopeCornerX, (a, v) => a.EnvelopeCornerX = U16(v)));
        entries.Add(new(0x128, 2, "envelope_corner_y", a => a.EnvelopeCornerY, (a, v) => a.EnvelopeCornerY = U16(v)));

        return [.. entries.OrderBy(e => e.Offset)];
    }

    private static IEnumerable<Entry> Velocity(
        int origin, string prefix, Func<Aircraft, VelocityAxisState> pick) =>
    [
        new(origin + 0x00, 4, $"{prefix}_i32", a => pick(a).Value, (a, v) => pick(a).Value = v),
        new(origin + 0x04, 4, $"{prefix}_working_i32", a => pick(a).Working, (a, v) => pick(a).Working = v),
        new(origin + 0x08, 2, $"{prefix}_bound_hi_i16", a => pick(a).BoundHigh, (a, v) => pick(a).BoundHigh = S16(v)),
        new(origin + 0x0A, 2, $"{prefix}_bound_lo_i16", a => pick(a).BoundLow, (a, v) => pick(a).BoundLow = S16(v)),
        new(origin + 0x0C, 2, $"{prefix}_cap_pos_i16", a => pick(a).CapPositive, (a, v) => pick(a).CapPositive = S16(v)),
        new(origin + 0x0E, 2, $"{prefix}_cap_neg_i16", a => pick(a).CapNegative, (a, v) => pick(a).CapNegative = S16(v)),
    ];

    private static IEnumerable<Entry> ControlAxis(
        int origin, string prefix, Func<Aircraft, ControlAxisState> pick) =>
    [
        new(origin + 0x00, 4, $"{prefix}_current_i32", a => pick(a).Current, (a, v) => pick(a).Current = v),
        new(origin + 0x04, 4, $"{prefix}_working_i32", a => pick(a).Working, (a, v) => pick(a).Working = v),
        new(origin + 0x08, 2, $"{prefix}_hi_bound_i16", a => pick(a).HiBound, (a, v) => pick(a).HiBound = S16(v)),
        new(origin + 0x0A, 2, $"{prefix}_lo_bound_i16", a => pick(a).LoBound, (a, v) => pick(a).LoBound = S16(v)),
        new(origin + 0x0C, 2, $"{prefix}_base_dir_i16", a => pick(a).BaseDir, (a, v) => pick(a).BaseDir = S16(v)),
        new(origin + 0x0E, 2, $"{prefix}_dir_step_i16", a => pick(a).DirStep, (a, v) => pick(a).DirStep = S16(v)),
    ];

    private static short S16(int value) => unchecked((short)value);

    private static ushort U16(int value) => unchecked((ushort)value);
}
