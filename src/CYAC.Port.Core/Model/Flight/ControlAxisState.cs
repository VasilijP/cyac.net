using CYAC.Port.Core.Schema;

namespace CYAC.Port.Core.Model.Flight;

/// <summary>
/// One control axis' integration state — the original <c>s_ctrl_state_block</c>, a 16-byte overlay
/// the game applies at <b>three</b> different offsets of the aircraft master struct (roll-dead
/// <c>+0x30</c>, roll-alive <c>+0x50</c>, pitch/g-load <c>+0xD6</c>).
/// </summary>
/// <remarks>
/// <para>
/// Field class: the integer half of a <b>DUAL</b> pair — <see cref="Current"/> and
/// <see cref="Working"/> are stepped every frame, so they keep the original's i32 width and wrap
/// semantics; the four bounds are per-aircraft constants that arrive with the <c>.fmd</c>.
/// </para>
/// <para>
/// Source of truth: KNOWN_FIELDS["s_ctrl_state_block"]</c> — which asks for exactly
/// this type: <i>"A port should model this as a reusable ControlAxisState value applied at three
/// sites."</i>  The three call sites are <c>joystick_to_control_deflect @0x2B94E</c>, invoked once per
/// axis per frame from <c>aircraft_joystick_integrate_active @0x2B268</c>
/// (<c>image@0x2B4FD</c> pitch, <c>image@0x2B57F</c> roll-alive, <c>image@0x2B651</c> roll-dead) —
/// </para>
/// <para>
/// <b>Not the same type as <see cref="VelocityAxisState"/>.</b> The velocity blocks at master
/// <c>+0x00/+0x10/+0x20</c> share the field <i>positions</i> but assign velocity-domain semantics to
/// them, and <c>joystick_to_control_deflect</c> is never called with those bases.  Two domains, two
/// types.
/// </para>
/// <para>
/// <b>Bound polarity.</b>  <see cref="HiBound"/> is <c>+0x08</c> and <see cref="LoBound"/> is
/// <c>+0x0A</c>: the inverted "lower/upper" labels are corrected from the push order at
/// <c>image@0x2B63F</c>.
/// </para>
/// </remarks>
[OriginalStruct("s_ctrl_state_block")]
public sealed class ControlAxisState
{
    /// <summary>Bytes one instance of this overlay occupies in the original: <c>0x10</c>.</summary>
    public const int Bytes = 0x10;

    /// <summary>Master-struct offset of the roll-DEAD instance (post-stall / crash path).</summary>
    public const int RollDeadMasterOffset = 0x30;

    /// <summary>Master-struct offset of the roll-ALIVE instance (the normal roll axis).</summary>
    public const int RollAliveMasterOffset = 0x50;

    /// <summary>Master-struct offset of the pitch / g-load instance (joystick Y).</summary>
    public const int PitchMasterOffset = 0xD6;

    /// <summary>
    /// <c>+0x00</c> — the live control value; an i32 the original stores as the
    /// <c>current_lo_i16</c>/<c>current_hi_i16</c> pair (<c>+0x00</c>/<c>+0x02</c>).
    /// </summary>
    /// <remarks>
    /// The integration accumulator: <c>value_step_toward_target @0x2B73E</c> steps it toward
    /// <see cref="Working"/> each frame.  For the pitch instance this word pair is the Q8.8 <b>load
    /// factor</b> (<c>g_player_gload_q8</c> [0xF06E]) — see <see cref="Aircraft.GLoadQ8"/>.
    /// </remarks>
    [OriginalField("+0x00", "current_lo_i16")]
    public int Current { get; set; }

    /// <summary>
    /// <c>+0x04</c> — the scratch target; an i32 stored as the <c>working_lo_i16</c>/
    /// <c>working_hi_i16</c> pair (<c>+0x04</c>/<c>+0x06</c>).
    /// </summary>
    /// <remarks>
    /// <c>joystick_to_control_deflect</c> computes the muldiv-scaled step here and clamps it to
    /// <c>[<see cref="LoBound"/>, <see cref="HiBound"/>]</c> before integrating.
    /// </remarks>
    [OriginalField("+0x04", "working_lo_i16")]
    public int Working { get; set; }

    /// <summary><c>+0x08</c> — positive-axis scale cap (the <c>ctrl_hi</c> clamp).</summary>
    [OriginalField("+0x08", "hi_bound_i16")]
    public short HiBound { get; set; }

    /// <summary><c>+0x0A</c> — negative-axis scale cap (the <c>ctrl_lo</c> clamp).</summary>
    [OriginalField("+0x0A", "lo_bound_i16")]
    public short LoBound { get; set; }

    /// <summary>
    /// <c>+0x0C</c> — directional base step; a per-aircraft constant that arrives with the
    /// <c>.fmd</c> and is never re-written at runtime.
    /// </summary>
    /// <remarks>
    /// For the pitch instance this lands on master <c>+0xE2</c>, i.e. the FMD field the round-13b
    /// map calls <c>stall_low_u16</c> — the identity is proved mechanistically (bulk-copy + an
    /// image-wide scan finding zero direct writers).  The port keeps the two roles apart: the static
    /// side is <see cref="AircraftDefinition.PitchAuthorityBase"/>, this is the runtime read.
    /// </remarks>
    [OriginalField("+0x0C", "base_dir_i16")]
    public short BaseDir { get; set; }

    /// <summary>
    /// <c>+0x0E</c> — sign-flip "kick" and zero-axis decay target; per-aircraft constant, as
    /// <see cref="BaseDir"/> (pitch instance ⇒ master <c>+0xE4</c> = FMD <c>stall_high_u16</c>).
    /// </summary>
    [OriginalField("+0x0E", "dir_step_i16")]
    public short DirStep { get; set; }

    /// <summary>Creates an axis whose accumulators are zero and whose bounds come from the .fmd.</summary>
    /// <param name="hiBound">Positive-axis cap (<c>+0x08</c>).</param>
    /// <param name="loBound">Negative-axis cap (<c>+0x0A</c>).</param>
    /// <param name="baseDir">Directional base step (<c>+0x0C</c>).</param>
    /// <param name="dirStep">Sign-flip kick / decay target (<c>+0x0E</c>).</param>
    public static ControlAxisState FromBounds(short hiBound, short loBound, short baseDir, short dirStep) =>
        new() { HiBound = hiBound, LoBound = loBound, BaseDir = baseDir, DirStep = dirStep };

    /// <summary>A short, stable description for test output and debugging.</summary>
    public override string ToString() =>
        $"ctrl(cur={Current}, work={Working}, bounds=[{LoBound}, {HiBound}], dir={BaseDir}/{DirStep})";
}
