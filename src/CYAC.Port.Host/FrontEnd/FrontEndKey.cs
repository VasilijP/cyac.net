namespace CYAC.Port.Host.FrontEnd;

/// <summary>
/// The keys the original's front-end screens react to (the manual's model, p. 20).
/// </summary>
/// <remarks>
/// <para>
/// The manual's paragraph is "Tab moves the cursor, Space selects, Esc backs up, the first letter of
/// an option selects it; the arrow keys also move".  The atlas shows that in the original <b>Tab
/// moves the mouse POINTER</b> and Space activates whatever the pointer is over
/// (a captured frame of the original is pixel-identical to
/// <c>02_main_menu.png</c> after seven Tabs — only the pointer moved, into the Credits button — and
/// the next Space opened the credits).  The port had no mouse at first, so Tab moves the
/// port's own focus ring instead and the `► ◄` marks the original puts on a list's selected row are
/// what shows where the ring is.
/// </para>
/// <para>
/// A letter arrives separately, as <c>FlightInputFrame.MenuTypeAhead</c> — the same character the
/// ESC menu's type-ahead reads, so no key is bound twice.
/// </para>
/// </remarks>
public enum FrontEndKey
{
    /// <summary>Tab — the next widget in the ring, wrapping.</summary>
    Next,

    /// <summary>Shift+Tab — the previous widget, wrapping.</summary>
    Previous,

    /// <summary>Up — the previous widget (the original's own "two rows" quirk is NOT kept).</summary>
    Up,

    /// <summary>Down — the next widget.</summary>
    Down,

    /// <summary>Left — the previous widget (a horizontal row of buttons reads this way).</summary>
    Left,

    /// <summary>Right — the next widget.</summary>
    Right,

    /// <summary>Space or Enter — activate the focused widget.</summary>
    Select,

    /// <summary>Esc — back up one screen; nothing at all on the root menu.</summary>
    Back,

    /// <summary>
    /// Home: the FIRST row of a grid (<c>create_mission_picker_one</c>'s <c>0x4700</c> arm,
    /// <c>image@0x27AE8</c>).  Only the CREATE MISSION pickers have a grid, so every other screen
    /// ignores it.
    /// </summary>
    Home,

    /// <summary>End: the LAST row (its <c>0x4F00</c> arm, <c>image@0x27AFC</c>).</summary>
    End,

    /// <summary>
    /// Backspace: the CREATE MISSION form's <c>Back Up</c>, which is widget 0's own
    /// accelerator <c>0x08</c> in the shipped template (<c>image@0x40960</c>).
    /// </summary>
    BackUp,
}
