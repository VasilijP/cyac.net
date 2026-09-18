namespace CYAC.Port.Core.Model.Mission;

/// <summary>
/// The active input device — persisted at <c>yeager.cfg@0x04</c>, live at
/// <c>g_input_mode [0xE45C]</c>.
/// </summary>
/// <remarks>
/// <para>
/// The scanner wins under the project's precedence rule and this enum follows it.  The consequence is
/// not cosmetic: the shipped <c>sources/yeager.cfg</c> holds 2, so the shipping configuration is
/// <see cref="Keyboard"/>, not joystick (reported).
/// </para>
/// </remarks>
public enum InputMode
{
    /// <summary>1 — analogue joystick (calibrated by the four <c>cfg@0x05..0x0C</c> words).</summary>
    Joystick = 1,

    /// <summary>2 — keyboard.  The value the shipped <c>yeager.cfg</c> carries.</summary>
    Keyboard = 2,

    /// <summary>3 — mouse.</summary>
    Mouse = 3,

    /// <summary>4 — high-resolution mouse.</summary>
    HighResolutionMouse = 4,
}
