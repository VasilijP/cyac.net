using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Model.World;
using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Sim.Flight.ColdStart;

/// <summary>
/// Which arm of <c>aircraft_pose_set @image@0x2A25C</c> ran, and what it produced.
/// </summary>
/// <param name="Airborne">
/// True when the spawn was above its own ground clearance — the arm at <c>image@0x2A2F7</c>, which
/// raises the gear, looks the airspeed up in the flight envelope and commits full throttle.
/// </param>
/// <param name="ForwardVelocity">The <c>i32</c> written into <c>master[+0x000]</c>.</param>
/// <param name="Throttle">The word written into <c>master[+0x09C]</c> and <c>master[+0x0A0]</c>.</param>
/// <param name="EnvelopeSpeed">
/// The raw <c>aircraft_fme_speed_interpolate</c> answer on the airborne arm (before the <c>+100</c>
/// and the <c>&lt;&lt; 8</c>), or 0 on the ground arm.
/// </param>
public readonly record struct AircraftPoseResult(
    bool Airborne, int ForwardVelocity, ushort Throttle, ushort EnvelopeSpeed);

/// <summary>
/// <c>aircraft_pose_set @image@0x2A25C</c> — the routine that makes an AIRBORNE start fly: it raises
/// the gear, sets full throttle and gives the aircraft the airspeed its own flight envelope says it
/// needs at that altitude.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the port needs it.</b>  <c>active_aircraft_load_and_state_reset @image@0x224CA</c> calls it
/// through an altitude gate before it applies the container's own speed seed:
/// </para>
/// <code>
/// image@0x224F8  ax:dx := [0xF0AE]:[0xF0B0]
/// image@0x224FF  les bx,[0x00C0]                            ; the PLAYER world object
/// image@0x22503  cmp es:[bx+0x0C],dx ; jl skip ; jg do ; cmp es:[bx+0x0A],ax ; jbe skip
/// image@0x22520  lcall aircraft_pose_set(&amp;object.pos_x, AL = 0)
/// image@0x22525  cmp [0xEE52],-1 ; je …                     ; only THEN the authored speed seed
/// </code>
/// <para>
/// <c>[0xF0AE]:[0xF0B0]</c> has <b>no writer image-wide</b> (an exhaustive Capstone sweep for every
/// instruction naming either word finds six reads — <c>image@0x224F8</c>, <c>0x225D5</c>,
/// <c>0x22690</c>, <c>0x226DE</c>, <c>0x22995</c>, <c>0x25CC5</c> — and no write) and its static image
/// value is 0, so the gate reads simply "the spawn is above the ground plane".  A parked Test Flight
/// (<c>pos_y = 0</c>) skips it (H2 §A3); every airborne mission start runs it.
/// </para>
/// <para>
/// <b>The airborne arm's airspeed is an ENVELOPE lookup, not authored data</b> (<c>image@0x2A2FC</c>):
/// <c>fme_record_lookup_by_key(master, 1)</c> picks the curve whose ordinal is 1 and
/// <c>aircraft_fme_speed_interpolate</c> reads it at the spawn altitude; the answer gets
/// <c>+ 100</c> and <c>&lt;&lt; 8</c>.  For a P-51 at 7,000 ft that is <c>(147 + 100) &lt;&lt; 8 =
/// 63232</c>, which is byte-for-byte the historic recording's first
/// <c>master[+0x000] vel_forward_i32</c>.
/// </para>
/// <para>
/// <b>What it does not do:</b> the position copy at <c>image@0x2A267..0x2A2AA</c> is a self-copy when
/// the loader calls it (the argument is the object's own <c>+0x06</c>), and <c>AL = 0</c> keeps the
/// authored euler.  The port models the copy anyway, because the Location menu's three other doors
/// (<c>ingame_menu_state_refresh</c> @<c>image@0x217C7</c>/<c>0x21818</c>/<c>0x21867</c>) pass a
/// different point.
/// </para>
/// <para>
/// <b>One byte the port does not model:</b> step 8 zeroes the WORD at <c>master[+0x090]</c>
/// (<c>image@0x2A3E0</c>), which <see cref="AircraftMasterCodec"/> does not carry.  Every shipped
/// <c>.fmd</c> already holds 0 there (<c>data/aircraft/*.json</c> <c>tail[0]</c> "0 in all 6"), so the
/// write is a no-op on shipped data; it is named here rather than silently dropped.
/// </para>
/// </remarks>
public static class AircraftPoseSet
{
    /// <summary>
    /// The gate value <c>active_aircraft_load_and_state_reset</c> compares the spawn altitude
    /// against: <c>[0xF0AE]:[0xF0B0]</c>, which no instruction in the image writes and whose static
    /// value is 0.
    /// </summary>
    public const int AltitudeGate = 0;

    /// <summary>The envelope curve ordinal the airborne arm looks up: 1 (<c>mov ax,1</c> @<c>image@0x2A304</c>).</summary>
    public const int AirborneCurveOrdinal = 1;

    /// <summary>The constant the interpolated airspeed gains before the shift (<c>add ax,0x64</c> @<c>image@0x2A311</c>).</summary>
    public const int AirborneSpeedBias = 100;

    /// <summary>The throttle the airborne arm commits: <c>0x6400</c> = 100 % (<c>image@0x2A322</c>).</summary>
    public const ushort AirborneThrottle = 0x6400;

    /// <summary>The throttle the ground arm commits: <c>0x0500</c> = 5 % (<c>image@0x2A2F2</c>).</summary>
    public const ushort GroundThrottle = 0x0500;

    /// <summary>
    /// Whether <c>active_aircraft_load_and_state_reset</c>'s altitude gate would call the
    /// pose-setter for a spawn at <paramref name="objectY"/>.
    /// </summary>
    /// <param name="objectY">The player world object's <c>pos_y</c> (<c>+0x0A</c>, Q8 feet).</param>
    /// <remarks>
    /// <c>cmp es:[bx+0x0C],dx / jl / jg / cmp es:[bx+0x0A],ax / jbe</c> @<c>image@0x22503..0x2250F</c>
    /// — a signed <c>i32</c> "strictly greater than" on the object's Y.
    /// </remarks>
    public static bool GateOpens(int objectY) => objectY > AltitudeGate;

    /// <summary>
    /// Runs <c>aircraft_pose_set</c> over an aircraft and its world object.
    /// </summary>
    /// <param name="aircraft">The flight master (<c>SI</c>, and <c>[0xF1BC]</c> — the same block).</param>
    /// <param name="player">The player world object (<c>master[+0x11A]</c>).</param>
    /// <param name="position">
    /// The point the pose is set to, in world-object units.  The loader passes the object's own
    /// position, which makes the copy a no-op.
    /// </param>
    /// <param name="clearEuler">
    /// The <c>AL</c> argument (<c>cmp byte [bp-6],0</c> @<c>image@0x2A2AB</c>): non-zero zeroes the
    /// object's three euler words.  The loader passes 0.
    /// </param>
    /// <returns>Which arm ran and what it committed.</returns>
    public static AircraftPoseResult Apply(
        Aircraft aircraft, WorldObject player, (int X, int Y, int Z) position, bool clearEuler)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        ArgumentNullException.ThrowIfNull(player);

        // 1. image@0x2A267..0x2A2AA — the three i32 into the object's +0x06/+0x0A/+0x0E.
        player.X = position.X;
        player.Y = position.Y;
        player.Z = position.Z;

        // 2. image@0x2A2AB — AL != 0 zeroes the euler triple.
        if (clearEuler)
        {
            player.Heading = default;
            player.Pitch = default;
            player.Roll = default;
        }

        // 3. image@0x2A2BF..0x2A2E3 — the ground test.  master[+0x116] is the CLASS's ground
        //    clearance (H2 §A8), master[+0x10E] the airspeed_a i32; the sum is compared against the
        //    object's pos_y as a signed 32-bit value.
        int groundSum = unchecked(aircraft.AirspeedB + aircraft.AirspeedA);
        bool airborne = groundSum < player.Y;

        int forwardVelocity;
        ushort throttle;
        ushort envelopeSpeed = 0;
        if (airborne)
        {
            // 3b. image@0x2A2F7 — gear UP, then the envelope lookup.
            aircraft.StatusFlags &= ~AircraftStatusFlags.LandingGear;
            EnvelopeCurve curve = FlightEnvelopeQueries.FindCurve(
                                      aircraft.Definition.Envelope, AirborneCurveOrdinal)
                                  ?? throw new InvalidOperationException(
                                      $"{aircraft.Definition.Name}'s .fme has no curve with ordinal "
                                      + $"{AirborneCurveOrdinal}; the original would call "
                                      + "aircraft_fme_speed_interpolate with the NULL far pointer "
                                      + "fme_record_lookup_by_key returns (image@0x2AB6C) and read the far heap.");

            envelopeSpeed = FlightEnvelopeQueries.InterpolateSpeed(aircraft, player.Y, curve, out _);

            // image@0x2A311: add ax,0x64 (16-bit) ; cwd ; shl_i32_by_cl(8).
            forwardVelocity = unchecked((short)(envelopeSpeed + AirborneSpeedBias)) << 8;
            throttle = AirborneThrottle;
        }
        else
        {
            // 3a. image@0x2A2E5 — gear DOWN, stopped, idle throttle.
            aircraft.StatusFlags |= AircraftStatusFlags.LandingGear;
            forwardVelocity = 0;
            throttle = GroundThrottle;
        }

        // 4/5. image@0x2A327..0x2A33F — throttle target and thrust floor share the word; the high
        //      word is zeroed (sub dx,dx @image@0x2A325).
        aircraft.ThrottleTarget = throttle;
        aircraft.ThrustFloor = throttle;
        aircraft.ForwardVelocity.Value = forwardVelocity;

        // 6. image@0x2A342..0x2A371 — the velocity blocks' CURRENT i32 and the three control
        //    blocks' current + working.  (+0x14/+0x24, the two velocity WORKING i32s, are NOT
        //    zeroed: the original writes only the two words of each.)
        aircraft.AngularVelocityA.Value = 0;
        aircraft.AngularVelocityB.Value = 0;
        aircraft.RollDeadAxis.Current = 0;
        aircraft.RollDeadAxis.Working = 0;
        aircraft.HeadingAoaAxis.Current = 0;
        aircraft.HeadingAoaAxis.Working = 0;
        aircraft.RollAliveAxis.Current = 0;
        aircraft.RollAliveAxis.Working = 0;

        // 7. image@0x2A374..0x2A3CD — the attitude accumulators from the object's euler:
        //    sext(euler) << 8 >> 3, and the yaw negated (32-bit neg @image@0x2A38D).
        aircraft.AttitudeHeading = -EulerToAttitude(player.Heading);
        aircraft.AttitudePitch = EulerToAttitude(player.Pitch);
        aircraft.AttitudeRoll = EulerToAttitude(player.Roll);

        // 8. image@0x2A3CE..0x2A3F6 — the AoA control block, the two timers, the active flag and the
        //    reset sentinel.  (master[+0x090]'s word is zeroed too; see the type remarks.)
        aircraft.PitchAxis.Current = 0;
        aircraft.PitchAxis.Working = 0;
        aircraft.HoldAltitudeTimer = 0;
        aircraft.StateCountdown = 0;
        aircraft.ActiveState = 1;
        aircraft.ResetSentinel = unchecked((short)0xFFFF);

        return new AircraftPoseResult(airborne, forwardVelocity, throttle, envelopeSpeed);
    }

    /// <summary>
    /// One euler word turned into its attitude accumulator: <c>(sext16(units) &lt;&lt; 8) &gt;&gt; 3</c>.
    /// </summary>
    /// <param name="angle">The object's euler word (<c>+0x12</c>, <c>+0x14</c> or <c>+0x16</c>).</param>
    /// <remarks>
    /// <c>cwd</c> then <c>shl_i32_by_cl @image@0x0020A</c> with <c>cl = 8</c>, then
    /// <c>sar_i32_by_cl @image@0x001D0</c> with <c>cl = 3</c> — an ARITHMETIC right shift, so the
    /// net effect on the shipped range is <c>units &lt;&lt; 5</c>.
    /// </remarks>
    public static int EulerToAttitude(Angle angle) =>
        (unchecked((short)angle.Units) << 8) >> 3;
}
