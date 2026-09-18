using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Model.World;
using CYAC.Port.Core.Primitives;
using CYAC.Port.Core.Sim.Flight;

namespace CYAC.Port.Render;

/// <summary>
/// The read-only view of one simulation frame that the renderer draws — the whole of what crosses
/// the sim → render boundary.
/// </summary>
/// <param name="X">The player's world X (<c>WorldObject +0x06</c>).</param>
/// <param name="Y">The player's world Y — altitude in Q8 feet (<c>WorldObject +0x0A</c>).</param>
/// <param name="Z">The player's world Z (<c>WorldObject +0x0E</c>).</param>
/// <param name="Heading">Compass heading (<c>WorldObject +0x12</c>), BAM: <c>0x0B40</c> = 360°.</param>
/// <param name="Pitch">Pitch attitude (<c>WorldObject +0x14</c>), BAM.</param>
/// <param name="Roll">Roll attitude (<c>WorldObject +0x16</c>), BAM.</param>
/// <param name="AirspeedFps">
/// <c>master[+0x01] current_airspeed_fps_u16</c> — the aircraft's speed in feet per second.
/// </param>
/// <param name="ThrottlePercent">
/// <c>g_throttle_pct [0xF035]</c> = <c>master[+0x9D]</c> — the CURRENT throttle percent, i.e. the
/// unaligned <c>&gt;&gt; 8</c> window of <c>master[+0x9C] throttle_current_lo_i32</c>
/// (<see cref="Aircraft.ThrustScaled"/>).  The HUD's <c>"THR: %3d%%"</c> arm pushes <c>[0xF035]</c>
/// (<c>image@0x0C728</c>), the fuel burn reads the same word as a percentage (<c>mov dx,[bx+0x9d]</c>,
/// <c>image@0x0C63C</c>… <c>image@0x2A63C</c>) and the engine-note path passes it as the throttle
/// argument (<c>image@0x29E3D</c>); the target lives at <c>+0xA0</c> and the current value chases it,
/// so the two differ for the whole of a spool-up.
/// </param>
/// <param name="Active">
/// <c>master[+0x122] != 0</c> — the aircraft is flying rather than deactivated by the post-chain
/// kill tail.
/// </param>
/// <param name="StatusFlags">
/// <c>master[+0x124]</c> — gear / flaps / brakes / afterburner, the bits the cockpit keys toggle.
/// </param>
/// <remarks>
/// <para>
/// The renderer NEVER touches <c>FlightKernelState</c> itself: the host builds one of these per
/// presented frame and hands it over, so a renderer cannot write into simulation state even by accident
/// ("a renderer must never write back into sim state").  Everything here is an integer straight out of
/// the integer kernel; the float conversions live on the render side of the fence
/// (<see cref="PitchRadians"/>, <see cref="RollRadians"/>).
/// </para>
/// <para>
/// Altitude in feet is <c>Y &gt;&gt; 8</c> — <c>altitude_ft_get @image@0x0F5AE</c>, the same shift
/// <c>cockpit_horizon_compass_draw</c>'s <c>"%5ld FT"</c> readout uses.
/// </para>
/// </remarks>
public readonly record struct FlightSnapshot(
    int X,
    int Y,
    int Z,
    Angle Heading,
    Angle Pitch,
    Angle Roll,
    int AirspeedFps,
    int ThrottlePercent,
    bool Active,
    byte StatusFlags)
{
    /// <summary>Altitude in whole feet — <c>altitude_ft_get @image@0x0F5AE</c>: <c>pos_y &gt;&gt; 8</c>.</summary>
    public int AltitudeFeet => Y >> 8;

    /// <summary>Compass heading in degrees, 0..360 — the BAM divided by 8.</summary>
    public double HeadingDegrees => Heading.ToDegrees();

    /// <summary>Pitch in degrees, signed, nose-up positive.</summary>
    public double PitchDegrees => SignedDegrees(Pitch);

    /// <summary>Roll in degrees, signed.  See <see cref="RollRadians"/> for the sign convention.</summary>
    public double RollDegrees => SignedDegrees(Roll);

    /// <summary>Pitch in radians, nose-up positive.</summary>
    public double PitchRadians => PitchDegrees * Math.PI / 180.0;

    /// <summary>
    /// Roll in radians; positive is <b>right wing down</b>.
    /// </summary>
    /// <remarks>
    /// The sign follows from the game's own data rather than an assumption: the keyboard-as-joystick
    /// table (<c>kbd_numpad_joystick_position_set @image@0x01819</c>,*.c</c>) maps numpad
    /// <b>Right</b> to <c>g_joystick_axis_x_clamped:= g_joystick_x_max_i16</c>, so a POSITIVE X axis
    /// is stick-right; and on the reference traces a positive X axis drives <c>master[+0x60]
    /// attitude_roll_q8_i32</c> — published as this BAM — in the POSITIVE direction (measured by
    /// <c>CYAC.Port.Host --headless --replay</c>'s attitude-sign census). Stick right
    /// rolls right, therefore positive roll is right wing down.
    /// </remarks>
    public double RollRadians => RollDegrees * Math.PI / 180.0;

    /// <summary>Builds the frame's view from the integer kernel's state.</summary>
    /// <param name="state">The kernel state after the frame's last step.</param>
    public static FlightSnapshot From(FlightKernelState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        WorldObject player = state.Player;
        Aircraft aircraft = state.Aircraft;
        return new FlightSnapshot(
            player.X,
            player.Y,
            player.Z,
            player.Heading,
            player.Pitch,
            player.Roll,
            aircraft.CurrentAirspeedFps,
            aircraft.ThrustScaled,
            aircraft.ActiveState != 0,
            (byte)aircraft.StatusFlags);
    }

    // The attitude BAMs are stored canonical (0..0x0B3F); a single Angle.Normalize step maps them
    // to the signed [-0x5A0, 0x5A0) range the readouts and the geometry want.
    private static double SignedDegrees(Angle angle) =>
        Angle.Normalize(unchecked((short)angle.Units)) / (double)Angle.UnitsPerDegree;
}
