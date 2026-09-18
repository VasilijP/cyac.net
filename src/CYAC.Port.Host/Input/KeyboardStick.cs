using CYAC.Port.Core.Sim.Flight;

namespace CYAC.Port.Host.Input;

/// <summary>Which way a keyboard "stick" is being held on one axis.</summary>
public enum StickPush
{
    /// <summary>Nothing held — centre.</summary>
    None = 0,

    /// <summary>Toward the axis's calibration MINIMUM (left, or stick forward).</summary>
    Minimum = -1,

    /// <summary>Toward the axis's calibration MAXIMUM (right, or stick back).</summary>
    Maximum = 1,
}

/// <summary>
/// The keyboard-as-joystick path: numpad / arrow presses become the two clamped axes the flight
/// kernel reads, <c>g_joystick_axis_x_clamped [0xE478]</c> and
/// <c>g_joystick_axis_y_clamped [0xE47A]</c>.
/// </summary>
/// <remarks>
/// <para>
/// The values are the original's, byte for byte: <c>kbd_numpad_joystick_position_set
/// @image@0x01819</c> is an 11-entry jump table over scancodes <c>0x47..0x51</c> that writes ONE of
/// three values per axis — the live calibration minimum <c>[0xE47C]</c>/<c>[0xE480]</c>, zero, or the
/// live calibration maximum <c>[0xE47E]</c>/<c>[0xE484]</c>.  Nothing between: a keyboard pilot flies
/// at full deflection or none.  Those four words are also <c>joystick_to_control_deflect
/// @image@0x2B94E</c>'s <c>idiv</c> divisors, so the deflection a full push produces is exactly ±1 of
/// whatever the calibration says.
/// </para>
/// <para>
/// <b>Which way is up.</b> An earlier pass measured it on the genuine machine's own records rather than assuming:
/// over the Test Flight trace a POSITIVE pitch BAM accompanies a CLIMB on 782 frames and a descent on
/// 0, and a positive Y axis moves the pitch BAM up on 297 frames against 77 (gravity owns the rest).
/// So <c>y_max</c> — which numpad <b>Down</b> / <b>2</b> selects (<c>image@0x0188C</c>) — is the
/// PULL, and <c>y_min</c>, selected by numpad <b>Up</b> / <b>8</b> (<c>image@0x01861</c>), is the
/// PUSH.  Pull back to climb, exactly as one would hope, and exactly opposite to the parameter names
/// on <c>JoystickCalibration</c>.
/// </para>
/// <para>
/// <b>Release.</b> H1 finding: the original has NO release path.  <c>image@0x01819</c> is called with
/// a scancode, writes the axes and returns; the only arms that write zero are the four EDGE
/// directions (Up/Down write <c>[0xE478]:= 0</c>, Left/Right write <c>[0xE47A]:= 0</c>), and numpad 5
/// / − / + fall straight through to <c>return 1</c> without touching either axis
/// (<c>image@0x018A0</c>).  So in mode 2 the stick <b>LATCHES</b>: whatever the last direction key
/// set stays set until another one is pressed.  That is faithful but hostile to a modern player, so
/// <see cref="Latching"/> offers both and the PoC defaults to hold-to-deflect (<c>--stick-latch</c>
/// restores the original).
/// </para>
/// </remarks>
public sealed class KeyboardStick
{
    private StickPush _x;
    private StickPush _y;

    /// <summary>Reproduce the original's latching stick instead of centring on release.</summary>
    public bool Latching { get; init; }

    /// <summary>The X push currently in effect.</summary>
    public StickPush X => _x;

    /// <summary>The Y push currently in effect.</summary>
    public StickPush Y => _y;

    /// <summary>Feeds one frame's held directions.</summary>
    /// <param name="left">Left / numpad 4 (or a NW/SW corner key) is held.</param>
    /// <param name="right">Right / numpad 6 (or a NE/SE corner key) is held.</param>
    /// <param name="forward">Up / numpad 8 (or a NW/NE corner key) is held — stick forward.</param>
    /// <param name="back">Down / numpad 2 (or a SW/SE corner key) is held — stick back.</param>
    /// <param name="centre">Numpad 5 is held — the original's no-op arm, used here to recentre.</param>
    /// <remarks>
    /// Opposing keys cancel, which is what the original's single-scancode dispatch cannot express
    /// and is the only place this layer is not a transcription.
    /// </remarks>
    public void Update(bool left, bool right, bool forward, bool back, bool centre = false)
    {
        StickPush x = (left, right) switch
        {
            (true, false) => StickPush.Minimum,
            (false, true) => StickPush.Maximum,
            _ => StickPush.None,
        };
        StickPush y = (forward, back) switch
        {
            (true, false) => StickPush.Minimum,
            (false, true) => StickPush.Maximum,
            _ => StickPush.None,
        };

        if (centre)
        {
            _x = StickPush.None;
            _y = StickPush.None;
            return;
        }

        if (!Latching)
        {
            _x = x;
            _y = y;
            return;
        }

        // The original writes an axis only when a key names it, and never writes it back to centre.
        if (x != StickPush.None || y != StickPush.None)
        {
            _x = x;
            _y = y;
        }
    }

    /// <summary>The two axis words for a calibration window.</summary>
    /// <param name="calibration">The live calibration extremes, <c>[0xE47C..0xE485]</c>.</param>
    public (short X, short Y) Axes(JoystickCalibration calibration) => (
        Resolve(_x, calibration.XMinimum, calibration.XMaximum),
        Resolve(_y, calibration.YMinimum, calibration.YMaximum));

    private static short Resolve(StickPush push, short minimum, short maximum) => push switch
    {
        StickPush.Minimum => minimum,
        StickPush.Maximum => maximum,
        _ => 0,
    };
}
