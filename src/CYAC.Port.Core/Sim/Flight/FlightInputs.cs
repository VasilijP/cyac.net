namespace CYAC.Port.Core.Sim.Flight;

/// <summary>
/// The seven-word tuning table the state-3 pull-up / ejection arm reads —
/// <c>g_joystick_calib_table_BASE [0x35F8..0x3605]</c>.
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  The scanner's name is a round-P36 guess: nothing in the image feeds these words to a
/// joystick read.  Every one of the seven is consumed by <c>aircraft_joystick_integrate_active</c>'s
/// state-3 arm (<c>image@0x2B301..0x2B3DE</c>) and nowhere else in the flight chain, and the arm is
/// the pull-up / ejection autopilot.  The port names them for what they do and keeps the original
/// identifier in the attributes.  <b>Report-only</b>: is outside this file's scope.
/// </para>
/// <para>
/// The table is compile-time read-only (zero writers image-wide,:3657), so this is a <c>readonly
/// record struct</c> loaded once per session.
/// </para>
/// </remarks>
/// <param name="SpeedErrorShift">
/// <c>[0x35F8]</c> — the left-shift count applied to <c>(envelopeSpeed − airspeed)</c>
/// (<c>mov cl,byte [0x35F8]</c> / <c>shl ax,cl</c> @<c>image@0x2B301</c>/<c>image@0x2B30B</c>).
/// Read as a BYTE even though the slot is a word.
/// </param>
/// <param name="SpeedErrorFloor">
/// <c>[0x35FA]</c> — the lower clamp on that shifted error (<c>image@0x2B310</c>, SIGNED <c>jge</c>).
/// </param>
/// <param name="SpeedErrorCeiling">
/// <c>[0x35FC]</c> — the upper clamp on it (<c>image@0x2B31F</c>, SIGNED <c>jle</c>).
/// </param>
/// <param name="CountdownShift">
/// <c>[0x35FE]</c> — the arithmetic right-shift count applied to
/// <c>(PullUpCountdownReload − StateCountdown)</c> (<c>sar ax,cl</c> @<c>image@0x2B339</c>).
/// Read as a BYTE.
/// </param>
/// <param name="CountdownCeiling">
/// <c>[0x3600]</c> — the upper clamp on that, and the second cap on the pull-up authority
/// (<c>image@0x2B33E</c>/<c>image@0x2B34A</c>).
/// </param>
/// <param name="BlendFloor">
/// <c>[0x3602]</c> — the lower clamp on the <c>0x100 − authority</c> pilot-input blend
/// (<c>image@0x2B3D8</c>, SIGNED <c>jge</c>).
/// </param>
/// <param name="PitchRate">
/// <c>[0x3604]</c> — the step rate handed to the pitch-block integrator, scaled by the authority
/// (<c>mov ax,[0x3604]</c> @<c>image@0x2B352</c> then <c>muldiv16_signed_shr8</c>).
/// </param>
public readonly record struct PullUpTuningTable(
    short SpeedErrorShift,
    short SpeedErrorFloor,
    short SpeedErrorCeiling,
    short CountdownShift,
    short CountdownCeiling,
    short BlendFloor,
    short PitchRate)
{
    /// <summary>Words in the table: 7 (KNOWN_GLOBAL_TYPES [0x35F8] = u16[7]</c>).</summary>
    public const int Words = 7;

    /// <summary>Reads the table out of a 14-byte little-endian window (a K0 trace global, say).</summary>
    /// <param name="window">At least <see cref="Words"/> × 2 bytes.</param>
    public static PullUpTuningTable FromWords(ReadOnlySpan<byte> window)
    {
        if (window.Length < Words * 2)
        {
            throw new ArgumentException(
                $"g_joystick_calib_table_BASE is {Words * 2} bytes; got {window.Length}",
                nameof(window));
        }

        static short Word(ReadOnlySpan<byte> w, int index) =>
            unchecked((short)(w[index * 2] | (w[(index * 2) + 1] << 8)));

        return new PullUpTuningTable(
            Word(window, 0), Word(window, 1), Word(window, 2), Word(window, 3),
            Word(window, 4), Word(window, 5), Word(window, 6));
    }
}

/// <summary>
/// The joystick axis calibration window the control integrator divides by —
/// <c>[0xE47C]</c>/<c>[0xE47E]</c>/<c>[0xE480]</c>/<c>[0xE484]</c>.
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  These are the per-axis extremes the calibration screen wrote (they mirror <c>yeager.cfg</c>
/// bytes <c>0x05..0x0C</c>,:2702-2712), and <c>joystick_to_control_deflect @image@0x2B94E</c> uses one of
/// them as the <b>divisor</b> of its <c>idiv</c> — so a zero here is a shipped divide fault, not a port bug.
/// </para>
/// <para>
/// The X axis is passed NEGATED (three <c>neg ax</c> at <c>image@0x2B570</c>/<c>0x2B576</c>/ <c>0x2B57C</c>)
/// on the ground-proximity arm and un-negated on the other (<c>image@0x2B645..0x2B64D</c>) — the port
/// reproduces that at the call sites, not here.
/// </para>
/// <para>
/// <c>[0xE482]</c>/<c>[0xE483]</c> sit inside the same DGROUP window but are NOT read by the flight chain,
/// so they are deliberately absent.
/// </para>
/// <para>
/// <b>Which word is the pull</b> — settled by two independent witnesses.
/// <c>aircraft_joystick_integrate_active</c> calls the deflector for the pitch axis with <c>push [0xE484];
/// push [0xE480]</c> (<c>image@0x2B4F5</c>/<c>image@0x2B4F9</c>), so <c>[bp+4] = [0xE480]</c> and <c>[bp+6]
/// = [0xE484]</c>; the body then branches on the axis sign (<c>or si,si; jle</c> @<c>image@0x2B96E</c>) and
/// takes <c>[bp+6] = [0xE484]</c> together with the control block's <b>HI</b> bound <c>+0x08</c> for a
/// POSITIVE axis, <c>[bp+4] = [0xE480]</c> with the <b>LO</b> bound <c>+0x0A</c> for a negative one
/// (<c>image@0x2B972</c>/<c>image@0x2B986</c>).  For the pitch block <c>&amp;master+0xD6</c> those two
/// bounds are <see cref="Aircraft.MaxLoadFactorG"/>/<see cref="Aircraft.MinLoadFactorG"/>, so a positive
/// axis drives toward maximum G — a pull — dividing by <see cref="YMaximum"/>.  H1's census of the genuine
/// machine agrees (782 climbs / 0 descents on a positive pitch BAM; 297 / 77 on a positive <c>[0xE47A]</c>),
/// as does <c>kbd_numpad_joystick_position_set @image@0x01819</c>, whose numpad-Down arm writes <c>y:=
/// y_max</c>.
/// </para>
/// </remarks>
/// <param name="XMinimum"><c>[0xE47C] g_joystick_x_min_i16</c> — the divisor for a LEFT deflection.</param>
/// <param name="XMaximum"><c>[0xE47E] g_joystick_x_max_i16</c> — the divisor for a RIGHT deflection.</param>
/// <param name="YMinimum">
/// <c>[0xE480] g_joystick_y_min_i16</c> — the divisor for a <b>PUSH</b> (+ H2 §A7 — the two words were
/// swapped).
/// </param>
/// <param name="YMaximum">
/// <c>[0xE484] g_joystick_y_max_i16</c> — the divisor for a <b>PULL</b> (+ H2 §A7).
/// </param>
public readonly record struct JoystickCalibration(
    short XMinimum, short XMaximum, short YMinimum, short YMaximum);

/// <summary>
/// The DGROUP window the flight kernel's control-integration stage reads and writes <b>outside</b>
/// the aircraft master struct: the two joystick mirrors, the ground-proximity flag, the cached
/// airspeed hand-off, plus the read-only calibration and pull-up tuning.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is mutable.</b>  Three of its members are kernel OUTPUTS, not inputs, and the
/// original reads them back within the same frame:
/// </para>
/// <list type="bullet">
///   <item><see cref="JoystickX"/>/<see cref="JoystickY"/> are zeroed by the speed gates at
///     <c>image@0x2B2BE</c>/<c>image@0x2B2CA</c> and rescaled by the state-3 arm at
///     <c>image@0x2B3F2</c>/<c>image@0x2B400</c>, and the deflection calls downstream read the
///     updated values;</item>
///   <item><see cref="GroundProximityFlag"/> is written at <c>image@0x2B28B</c> from
///     <c>flight_check_alive_or_active</c> and read four times afterwards
///     (<c>image@0x2B2A9</c>/<c>0x2B4AE</c>/<c>0x2B559</c>/<c>0x2B6B1</c>);</item>
///   <item><see cref="CachedAirspeedHi"/> is a pure write (<c>image@0x2B530</c>) that the
///     apply-velocity stage consumes next.</item>
/// </list>
/// <para>
/// The driver seeds the two mirrors from <c>[0xE478]/[0xE47A]</c> before the stage runs
/// (<c>image@0x2A6AE</c>/<c>image@0x2A6B4</c>) — that is K5's job, not this type's.
/// </para>
/// </remarks>
public sealed class FlightInputs
{
    /// <summary>
    /// The per-axis calibration extremes — an INPUT the kernel only reads, but one that the input
    /// stage OUTSIDE the kernel can move between frames.
    /// </summary>
    /// <remarks>
    /// The four words are moved every frame by a PAIR of ±10 steps that normally cancels:
    /// `flight_input_axis_clamp_and_scale @image@0x22724` SHRINKS the window (`add [0xE47C],0xA /
    /// sub [0xE47E],0xA / add [0xE480],0xA / sub [0xE484],0xA` @image@0x2279C..0x227AB) before the
    /// chain, and `flight_input_axis_rangelimit_grow @image@0x227B2` GROWS it back after (call
    /// @image@0x228F6).  Both live in `flight_engine_per_frame_top @image@0x227C7`'s normal path, so
    /// a frame that reaches the chain through its OTHER door — `flight_engine_initial_state_setup
    /// @image@0x22689`, taken when `[0xC316]!= 0` — skips both and leaves the previous frame's grow
    /// unmatched, permanently widening the window by 10. Measured once in 219,715 verified frames,
    /// on `session_20260829_123953_det` at step 181,305: (−96, 96, −96, 96) → (−106, 106, −106,
    /// 106).  The port therefore takes it per frame.
    /// </remarks>
    public JoystickCalibration Calibration { get; set; }

    /// <summary>The state-3 pull-up tuning table — read-only for the whole flight.</summary>
    public PullUpTuningTable PullUpTuning { get; init; }

    /// <summary>
    /// <c>[0xF1B8] g_active_aircraft_joystick_x_mirror</c> — the roll axis this frame.
    /// </summary>
    public short JoystickX { get; set; }

    /// <summary>
    /// <c>[0xF1BA] g_active_aircraft_joystick_y_mirror</c> — the pitch axis this frame.
    /// </summary>
    public short JoystickY { get; set; }

    /// <summary>
    /// <c>[0xF1C4] g_ground_proximity_margin_flag (ex-g_aircraft_alive_flag)</c> — the byte
    /// <c>flight_check_alive_or_active @image@0x2C22C</c> returns.
    /// </summary>
    /// <remarks>
    /// the scanner global is now <c>g_ground_proximity_margin_flag</c>; the 48 bytes say
    /// <b>ground proximity</b>: the function returns 1 iff <c>AirspeedB + AirspeedA &gt;=
    /// playerObject.Y</c> as a signed <c>i32</c> (<c>image@0x2C231..0x2C251</c>), i.e. iff the
    /// aircraft is at or below a speed-derived height.
    /// </remarks>
    public byte GroundProximityFlag { get; set; }

    /// <summary>
    /// <c>[0xF1BE] g_airspeed_hi_cached_i16</c> — the airspeed hand-off to apply-velocity, written
    /// once per integrate frame at <c>image@0x2B530</c>.
    /// </summary>
    public short CachedAirspeedHi { get; set; }

    /// <summary>
    /// <c>[0xF1C0..0xF1C3] g_vertical_speed_i32</c> — written unconditionally by apply-velocity
    /// phase 11 (<c>image@0x2C053</c>) as the NEGATED world-Y velocity slot.
    /// </summary>
    /// <remarks>
    /// It has two readers with two different widths, and the port must keep both consistent:
    /// <c>damage_or_crash_check</c> reads the whole <c>i32</c> (<c>image@0x2C127</c>) and
    /// <c>crash_conditions_valid</c> reads the UNALIGNED middle word at <c>[0xF1C1]</c>
    /// (<c>image@0x2C2A3</c>) — see <see cref="VerticalSpeedMidWord"/>.
    /// </remarks>
    public int VerticalSpeed { get; set; }

    /// <summary>
    /// <c>[0xF1C1]</c> — the unaligned middle word of <see cref="VerticalSpeed"/>, which is what the
    /// crash validator's descent-rate test actually reads.
    /// </summary>
    public short VerticalSpeedMidWord => unchecked((short)(VerticalSpeed >> 8));

    /// <summary>
    /// <c>[0x3606] g_wind_drift_accumulator</c> — a NEGATIVE accumulator that biases the pitch rate
    /// and decays back toward zero.
    /// </summary>
    /// <remarks>
    /// Two writers, both in this kernel's stages: apply-velocity phase 3 decays it
    /// (<c>image@0x2BDB0</c>/<c>image@0x2BDC8</c>) and <c>damage_or_crash_check</c>'s impact body
    /// arms it from the descent rate (<c>image@0x2C154</c>/<c>image@0x2C15A</c>).  It never moved on
    /// any of the three recordings — arming it needs a ground impact.
    /// </remarks>
    public short WindDriftAccumulator { get; set; }
}
