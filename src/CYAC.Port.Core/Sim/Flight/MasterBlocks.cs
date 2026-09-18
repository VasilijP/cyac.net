using CYAC.Port.Core.Model.Flight;

namespace CYAC.Port.Core.Sim.Flight;

/// <summary>
/// Which 16-byte integrator block of <c>s_aircraft_master</c> a kernel helper is pointed at, named
/// by its master-struct offset — the value the original loads into <c>BX</c>.
/// </summary>
/// <remarks>
/// <para>
/// The original's control helpers (<c>value_step_toward_target @image@0x2B73E</c>,
/// <c>ctrl_axis_step_toward @image@0x2B7AC</c>, <c>ctrl_axis_alive_normalize @image@0x2B7C8</c>,
/// <c>joystick_to_control_deflect @image@0x2B94E</c>, <c>world_wrap_axis @image@0x2B8DE</c>) are
/// <b>generic over the block base</b>: they are called with six different values of <c>BX</c> across
/// the two control-integration arms.  The port's model splits the same bytes across three types
/// (<see cref="ControlAxisState"/>, <see cref="VelocityAxisState"/> and loose position fields), so
/// this enum plus <see cref="MasterBlocks"/> restores the "any block" addressing the helpers need
/// without re-introducing raw offsets into the domain model.
/// </para>
/// <para>
/// Only the slots a helper actually touches are mapped; a slot the port has no field for throws
/// rather than inventing one (see <see cref="MasterBlocks"/>).
/// </para>
/// </remarks>
public enum MasterBlock
{
    /// <summary><c>+0x00</c> — the forward-velocity block (<see cref="Aircraft.ForwardVelocity"/>).</summary>
    ForwardVelocity = 0x00,

    /// <summary><c>+0x10</c> — angular velocity A (<see cref="Aircraft.AngularVelocityA"/>).</summary>
    AngularVelocityA = 0x10,

    /// <summary><c>+0x20</c> — angular velocity B (<see cref="Aircraft.AngularVelocityB"/>).</summary>
    AngularVelocityB = 0x20,

    /// <summary><c>+0x30</c> — the roll axis, ground-proximity path (<see cref="Aircraft.RollDeadAxis"/>).</summary>
    RollDead = 0x30,

    /// <summary><c>+0x40</c> — the heading-from-AoA block (<see cref="Aircraft.HeadingAoaAxis"/>).</summary>
    HeadingAoa = 0x40,

    /// <summary><c>+0x50</c> — the roll axis, normal path (<see cref="Aircraft.RollAliveAxis"/>).</summary>
    RollAlive = 0x50,

    /// <summary><c>+0x60</c> — the ROLL attitude block (<see cref="Aircraft.AttitudeRoll"/>); ex-"the X position
    /// block".</summary>
    AttitudeRoll = 0x60,

    /// <summary><c>+0x70</c> — the PITCH attitude block (<see cref="Aircraft.AttitudePitch"/>); ex-"the Y position
    /// block".</summary>
    AttitudePitch = 0x70,

    /// <summary>
    /// <c>+0x80</c> — the HEADING attitude block (<see cref="Aircraft.AttitudeHeading"/>); ex-"the Z position block".
    /// Added by K4: <c>aircraft_physics_apply_velocity</c> integrates and wraps it exactly like the other two
    /// (<c>image@0x2BE71</c> and <c>image@0x2BE3E</c>), but no control helper had ever been pointed at it, so the
    /// enum did not carry it.  Its WORKING slot (<c>+0x84</c>) still has no port field and no observed writer, so
    /// <see cref="MasterBlocks.Working"/> throws for it by design.
    /// </summary>
    AttitudeHeading = 0x80,

    /// <summary><c>+0xD6</c> — the pitch / g-load axis (<see cref="Aircraft.PitchAxis"/>).</summary>
    Pitch = 0xD6,
}

/// <summary>
/// Reads and writes the six slots of the original's 16-byte integrator template
/// (<c>s_ctrl_state_block</c>) on any <see cref="MasterBlock"/> of an <see cref="Aircraft"/>.
/// </summary>
/// <remarks>
/// Slot layout, from KNOWN_FIELDS["s_ctrl_state_block"]</c>: <c>+0x00</c> current <c>i32</c>,
/// <c>+0x04</c> working <c>i32</c>, <c>+0x08</c> hi bound, <c>+0x0A</c> lo bound,
/// <c>+0x0C</c> base dir, <c>+0x0E</c> dir step.
/// </remarks>
public static class MasterBlocks
{
    /// <summary>The block's <c>+0x00</c> slot.</summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <param name="block">Which block.</param>
    public static int Current(Aircraft aircraft, MasterBlock block)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        return block switch
        {
            MasterBlock.ForwardVelocity => aircraft.ForwardVelocity.Value,
            MasterBlock.AngularVelocityA => aircraft.AngularVelocityA.Value,
            MasterBlock.AngularVelocityB => aircraft.AngularVelocityB.Value,
            MasterBlock.RollDead => aircraft.RollDeadAxis.Current,
            MasterBlock.HeadingAoa => aircraft.HeadingAoaAxis.Current,
            MasterBlock.RollAlive => aircraft.RollAliveAxis.Current,
            MasterBlock.AttitudeRoll => aircraft.AttitudeRoll,
            MasterBlock.AttitudePitch => aircraft.AttitudePitch,
            MasterBlock.AttitudeHeading => aircraft.AttitudeHeading,
            MasterBlock.Pitch => aircraft.PitchAxis.Current,
            _ => throw Unmapped(block, 0x00),
        };
    }

    /// <summary>Writes the block's <c>+0x00</c> slot.</summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <param name="block">Which block.</param>
    /// <param name="value">The new value.</param>
    public static void SetCurrent(Aircraft aircraft, MasterBlock block, int value)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        switch (block)
        {
            case MasterBlock.ForwardVelocity: aircraft.ForwardVelocity.Value = value; break;
            case MasterBlock.AngularVelocityA: aircraft.AngularVelocityA.Value = value; break;
            case MasterBlock.AngularVelocityB: aircraft.AngularVelocityB.Value = value; break;
            case MasterBlock.RollDead: aircraft.RollDeadAxis.Current = value; break;
            case MasterBlock.HeadingAoa: aircraft.HeadingAoaAxis.Current = value; break;
            case MasterBlock.RollAlive: aircraft.RollAliveAxis.Current = value; break;
            case MasterBlock.AttitudeRoll: aircraft.AttitudeRoll = value; break;
            case MasterBlock.AttitudePitch: aircraft.AttitudePitch = value; break;
            case MasterBlock.AttitudeHeading: aircraft.AttitudeHeading = value; break;
            case MasterBlock.Pitch: aircraft.PitchAxis.Current = value; break;
            default: throw Unmapped(block, 0x00);
        }
    }

    /// <summary>The block's <c>+0x04</c> slot.</summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <param name="block">Which block.</param>
    public static int Working(Aircraft aircraft, MasterBlock block)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        return block switch
        {
            MasterBlock.ForwardVelocity => aircraft.ForwardVelocity.Working,
            MasterBlock.AngularVelocityA => aircraft.AngularVelocityA.Working,
            MasterBlock.AngularVelocityB => aircraft.AngularVelocityB.Working,
            MasterBlock.RollDead => aircraft.RollDeadAxis.Working,
            MasterBlock.HeadingAoa => aircraft.HeadingAoaAxis.Working,
            MasterBlock.RollAlive => aircraft.RollAliveAxis.Working,
            MasterBlock.AttitudeRoll => aircraft.AttitudeRollWorking,
            MasterBlock.AttitudePitch => aircraft.AttitudePitchWorking,
            MasterBlock.Pitch => aircraft.PitchAxis.Working,
            _ => throw Unmapped(block, 0x04),
        };
    }

    /// <summary>Writes the block's <c>+0x04</c> slot.</summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <param name="block">Which block.</param>
    /// <param name="value">The new value.</param>
    public static void SetWorking(Aircraft aircraft, MasterBlock block, int value)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        switch (block)
        {
            case MasterBlock.ForwardVelocity: aircraft.ForwardVelocity.Working = value; break;
            case MasterBlock.AngularVelocityA: aircraft.AngularVelocityA.Working = value; break;
            case MasterBlock.AngularVelocityB: aircraft.AngularVelocityB.Working = value; break;
            case MasterBlock.RollDead: aircraft.RollDeadAxis.Working = value; break;
            case MasterBlock.HeadingAoa: aircraft.HeadingAoaAxis.Working = value; break;
            case MasterBlock.RollAlive: aircraft.RollAliveAxis.Working = value; break;
            case MasterBlock.AttitudeRoll: aircraft.AttitudeRollWorking = value; break;
            case MasterBlock.AttitudePitch: aircraft.AttitudePitchWorking = value; break;
            case MasterBlock.Pitch: aircraft.PitchAxis.Working = value; break;
            default: throw Unmapped(block, 0x04);
        }
    }

    /// <summary>The block's <c>+0x08</c> slot (the control domain's hi bound, the position domain's wrap upper).</summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <param name="block">Which block.</param>
    public static short HiBound(Aircraft aircraft, MasterBlock block)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        return block switch
        {
            MasterBlock.RollDead => aircraft.RollDeadAxis.HiBound,
            MasterBlock.HeadingAoa => aircraft.HeadingAoaAxis.HiBound,
            MasterBlock.RollAlive => aircraft.RollAliveAxis.HiBound,
            MasterBlock.AttitudeRoll => aircraft.AttitudeRollLimitUpper,
            MasterBlock.AttitudePitch => aircraft.AttitudePitchLimitUpper,
            MasterBlock.AttitudeHeading => aircraft.AttitudeHeadingLimitUpper,
            MasterBlock.Pitch => aircraft.PitchAxis.HiBound,
            _ => throw Unmapped(block, 0x08),
        };
    }

    /// <summary>The block's <c>+0x0A</c> slot (the control domain's lo bound, the position domain's wrap lower).</summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <param name="block">Which block.</param>
    public static short LoBound(Aircraft aircraft, MasterBlock block)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        return block switch
        {
            MasterBlock.RollDead => aircraft.RollDeadAxis.LoBound,
            MasterBlock.HeadingAoa => aircraft.HeadingAoaAxis.LoBound,
            MasterBlock.RollAlive => aircraft.RollAliveAxis.LoBound,
            MasterBlock.AttitudeRoll => aircraft.AttitudeRollLimitLower,
            MasterBlock.AttitudePitch => aircraft.AttitudePitchLimitLower,
            MasterBlock.AttitudeHeading => aircraft.AttitudeHeadingLimitLower,
            MasterBlock.Pitch => aircraft.PitchAxis.LoBound,
            _ => throw Unmapped(block, 0x0A),
        };
    }

    /// <summary>The block's <c>+0x0C</c> slot (the control domain's base direction step).</summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <param name="block">Which block.</param>
    public static short BaseDir(Aircraft aircraft, MasterBlock block)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        return block switch
        {
            MasterBlock.RollDead => aircraft.RollDeadAxis.BaseDir,
            MasterBlock.HeadingAoa => aircraft.HeadingAoaAxis.BaseDir,
            MasterBlock.RollAlive => aircraft.RollAliveAxis.BaseDir,
            MasterBlock.Pitch => aircraft.PitchAxis.BaseDir,
            _ => throw Unmapped(block, 0x0C),
        };
    }

    /// <summary>The block's <c>+0x0E</c> slot (the sign-flip kick and the decay rate).</summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <param name="block">Which block.</param>
    public static short DirStep(Aircraft aircraft, MasterBlock block)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        return block switch
        {
            MasterBlock.RollDead => aircraft.RollDeadAxis.DirStep,
            MasterBlock.HeadingAoa => aircraft.HeadingAoaAxis.DirStep,
            MasterBlock.RollAlive => aircraft.RollAliveAxis.DirStep,
            MasterBlock.Pitch => aircraft.PitchAxis.DirStep,
            _ => throw Unmapped(block, 0x0E),
        };
    }

    private static NotSupportedException Unmapped(MasterBlock block, int slot) =>
        new($"s_aircraft_master +0x{(int)block:X2} has no port field for template slot +0x{slot:X2}; "
            + "no observed caller of the control helpers reads it, so the port maps only the slots "
            + "the bytes use.");
}
