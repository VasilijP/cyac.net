using CYAC.Port.Render.Cockpit;

namespace CYAC.Port.Host.FrontEnd;

/// <summary>What activating a front-end widget asks the shell to do.</summary>
public enum FrontEndCommand
{
    /// <summary>Nothing — the key moved the focus, or was refused.</summary>
    None,

    /// <summary>strings.json 11 — fly the historic mission the command line named (F2 adds the pickers).</summary>
    FlyHistoricMission,

    /// <summary>strings.json 12 — greyed: the port has no mission builder in the front end yet.</summary>
    CreateMission,

    /// <summary>strings.json 54 — open the HANGAR (activity-dispatch entry 2).</summary>
    TestFlight,

    /// <summary>strings.json 14 — re-fly what <c>settings.json</c> remembers.</summary>
    LastMission,

    /// <summary>strings.json 55 — greyed: the port has no film system.</summary>
    ReviewFilm,

    /// <summary>strings.json 56 — leave the game.</summary>
    ExitToDos,

    /// <summary>strings.json 122 — the credits screen.</summary>
    Credits,

    /// <summary>The credits screen's own button: back to CHOOSE ACTIVITY (also any Exit that pops).</summary>
    Ok,

    /// <summary>An era row on CONFLICT SELECTION: open MISSION SELECTION for it.</summary>
    OpenMissionSelection,

    /// <summary>A mission row on MISSION SELECTION: open its MISSION DESCRIPTION.</summary>
    OpenMissionDescription,

    /// <summary>The page buttons on MISSION SELECTION.</summary>
    PreviousPage,

    /// <summary>The page buttons on MISSION SELECTION.</summary>
    NextPage,

    /// <summary>Ok on MISSION DESCRIPTION: start the chosen sortie.</summary>
    StartSortie,

    /// <summary>
    /// <c>Debrief</c> on DEBRIEFING: show the verdict (<c>[0xBC30] = 0</c>).
    /// </summary>
    ShowDebrief,

    /// <summary><c>Stats</c>: show MISSION STATS (<c>[0xBC30] = 1</c>).</summary>
    ShowStats,

    /// <summary><c>Done</c>: leave the debriefing for CHOOSE ACTIVITY.</summary>
    DoneDebriefing,

    /// <summary>The hangar's <c>←</c>: the previous encyclopedia page (<c>image@0x26C22</c>).</summary>
    PreviousAircraft,

    /// <summary>Its <c>→</c> (<c>image@0x26C80</c>).</summary>
    NextAircraft,

    /// <summary>Its <c>3d/2d</c>: flip the left column and reset the rotation (<c>image@0x26C98</c>).</summary>
    ToggleHangarView,

    /// <summary>
    /// Its <c>Fly</c>: start a test flight of the aeroplane on the page (<c>image@0x26CB9</c>).
    /// </summary>
    FlyTestFlight,

    /// <summary>
    /// MISSION DESCRIPTION's <c>Tactics</c>: open the comparison screen
    /// (<c>ui_plane_comparison_screen @image@0x26D05</c>, the briefing screen's widget id 1).
    /// </summary>
    OpenTactics,

    /// <summary>TACTICS' <c>←</c>: the previous flyable (<c>image@0x2710A</c>).</summary>
    PreviousAlly,

    /// <summary>Its <c>→</c> (<c>image@0x2713B</c>).</summary>
    NextAlly,

    /// <summary>Its <c>↑</c>: the previous <c>pi.bin</c> page (<c>image@0x2714B</c>).</summary>
    PreviousEnemy,

    /// <summary>Its <c>↓</c> (<c>image@0x2715F</c>).</summary>
    NextEnemy,

    /// <summary>
    /// CREATE MISSION's <c>Back Up</c> (widget 0): drop the last pick and show its picker again
    /// (<c>ui_create_mission_form</c>'s <c>AX == 0</c> arm, <c>image@0x27719..0x27740</c>).
    /// </summary>
    CreateMissionBackUp,

    /// <summary>
    /// CREATE MISSION's <c>Done</c> (widget 2) in the DONE state: fly the sentence
    /// (<c>image@0x27742</c> → <c>image@0x2775E</c>).
    /// </summary>
    FlyCustomMission,
}

/// <summary>The two button looks the atlas shows.</summary>
public enum FrontEndButtonStyle
{
    /// <summary>
    /// An ACTIVITY-LIST row: teal label, and the focused one is drawn SUNKEN with the pale-teal ink
    /// (<c>02</c>'s <c>Fly Historic Mission</c>, <c>03</c>'s <c>World War II</c>).
    /// </summary>
    ActivityList,

    /// <summary>
    /// A COMMAND button: grey label, raised whether or not it is focused — the focus shows only in
    /// the <c>► ◄</c> marks (<c>14</c>'s <c>Ok</c> carries them on a raised button).
    /// </summary>
    Command,
}

/// <summary>One front-end widget: where it is, what it says and what it does.</summary>
/// <param name="Command">What activating it asks for.</param>
/// <param name="X">Design column of its left edge.</param>
/// <param name="Y">Design row of its top edge.</param>
/// <param name="Width">Its width in design columns.</param>
/// <param name="Height">Its height in design rows.</param>
/// <param name="Label">Its label, from <c>data/strings.json</c> — never a literal.</param>
/// <param name="Style">Which of the two looks it wears.</param>
public sealed record FrontEndButton(
    FrontEndCommand Command,
    int X,
    int Y,
    int Width,
    int Height,
    string Label,
    FrontEndButtonStyle Style)
{
    /// <summary>Whether the port can do what this button asks.</summary>
    /// <remarks>
    /// Settable, not <c>init</c>: a widget's enablement changes while its screen is up — F4's hangar
    /// greys <c>Fly</c> per aircraft (the original's screen) and F2's page buttons grey at the ends of
    /// the list.  It is also what lets <c>FrontEndPixelTests</c> render the screen in the state the
    /// ORIGINAL is in (nothing greyed) and diff that against the atlas.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>Why it is greyed, for the log and the report — empty when it is live.</summary>
    /// <remarks>
    /// Settable, like <see cref="Enabled"/>: F2's page buttons change their reason as the page turns
    /// (the reason a Previous Page is dead on page 1 differs from a Next Page dead on the last page).
    /// </remarks>
    public string DisabledReason { get; set; } = string.Empty;

    /// <summary>
    /// The letter that selects it — the manual's "first letter" rule (p. 20), upper case, or
    /// <c>'\0'</c> when it has none.
    /// </summary>
    public char Letter { get; init; }

    /// <summary>
    /// How many pixels of BEVEL stand between the drawn box and the widget's own <c>s_ui_widget</c>
    /// rectangle, which is what the original hit-tests against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A command button is drawn as a two-pixel bevel around its caption area
    /// (<c>FrontEndPainter.Bevel</c>, F1 §2.1), and the rectangle in the record is that caption
    /// area.  It is not a guess: the three shipped <c>s_ui_widget</c> arrays in the data tree give
    /// every bottom-row button's rectangle, and each one is EXACTLY this screen's drawn box shrunk
    /// by two on each side — MISSION SELECTION's <c>[0x2EE4]</c> records
    /// (12, 172, 88, −1) / (−1, 172, 88, −1) / (208, 172, 40, −1) against the drawn
    /// (10, 170, 92, 15) / (106, 170, 92, 15) / (206, 170, 44, 15), MISSION DESCRIPTION's
    /// <c>[0x380A]</c> and TACTICS' <c>[0x3AEC]</c> likewise (the <c>−1</c> height is
    /// <c>widget_one_init</c>'s default 11, <c>MOV word [SI+0xE],0x0B</c> @<c>image@0x2E228</c>;
    /// the <c>−1</c> x is its horizontal auto-layout <c>prev.x + prev.w + 8</c>).
    /// <c>WidgetRectanglesAreTheShippedRecords</c> asserts all fifteen of them — twelve buttons and
    /// the three mission rows.
    /// </para>
    /// <para>
    /// So a click that lands on the bevel itself hits nothing, exactly as in the original —
    /// <c>ui_hit_test_against_widget_list @image@0x2E3EA</c> walks the list calling
    /// <c>point_in_rect @image@0x13C95</c> with <c>&amp;widget[+0x08]</c>, the rectangle, and
    /// nothing else.
    /// </para>
    /// </remarks>
    public const int BevelInset = 2;

    /// <summary>The widget's own hit rectangle: the drawn box less its bevel.</summary>
    public (int X, int Y, int Width, int Height) Rect =>
        (X + BevelInset, Y + BevelInset, Width - (2 * BevelInset), Height - (2 * BevelInset));

    /// <summary>Whether a design point is inside the widget's rectangle.</summary>
    /// <param name="x">The design column.</param>
    /// <param name="y">The design row.</param>
    public bool Contains(int x, int y)
    {
        (int rx, int ry, int rw, int rh) = Rect;
        return x >= rx && x < rx + rw && y >= ry && y < ry + rh;
    }

    /// <summary>
    /// Where the original parks the mouse pointer when Tab reaches this widget:
    /// <c>(rect_x + rect_w − 3, rect_y + rect_h − 3)</c>
    /// (<c>widget_mouse_cursor_position_set @image@0x2E4DD</c>, the right-edge arm).
    /// </summary>
    public (int X, int Y) PointerPlacement
    {
        get
        {
            (int rx, int ry, int rw, int rh) = Rect;
            return (rx + rw - FrontEndPointer.PlacementInset, ry + rh - FrontEndPointer.PlacementInset);
        }
    }

    /// <summary>Draws it.</summary>
    /// <param name="surface">The design surface.</param>
    /// <param name="font">The game's <c>propbold</c>.</param>
    /// <param name="focused">Whether the focus ring is on it.</param>
    public void Render(in FrontEndPainter.Surface surface, CockpitFont font, bool focused)
    {
        ArgumentNullException.ThrowIfNull(font);
        bool sunken = focused && Style == FrontEndButtonStyle.ActivityList;
        FrontEndPainter.Bevel(
            surface,
            X,
            Y,
            Width,
            Height,
            sunken ? FrontEndPainter.Sunken : FrontEndPainter.Raised,
            sunken ? FrontEndPainter.SunkenFillIndex : FrontEndPainter.FillIndex);

        (int ink, int emboss) = (Style, sunken) switch
        {
            (FrontEndButtonStyle.ActivityList, true) =>
                (FrontEndPainter.ListFocusInkIndex, FrontEndPainter.ListFocusEmbossIndex),
            (FrontEndButtonStyle.ActivityList, false) =>
                (FrontEndPainter.ListInkIndex, FrontEndPainter.TitleEmbossIndex),
            _ => (FrontEndPainter.TitleInkIndex, FrontEndPainter.TitleEmbossIndex),
        };

        int labelX = X + (Width / 2) - (FrontEndPainter.Measure(font, Label) / 2);
        int labelRow = Y + LabelRowFor(Height, font.Height);
        FrontEndPainter.Text(surface, font, Label, labelX, labelRow, ink, emboss, !Enabled);
        if (!focused)
        {
            return;
        }

        // It does, and it stipples them. The original's screen shows the hangar on an enemy-only aircraft —
        // `Fly` is greyed AND carries the marks, and the marks are stippled with the caption.
        // Drawing them solid left 12 mismatching pixels against that frame; stippling them makes
        // the diff exact.
        FrontEndPainter.Text(
            surface,
            font,
            FrontEndPainter.FocusMarkLeftText,
            X + FrontEndPainter.FocusMarkInset,
            labelRow,
            ink,
            emboss,
            !Enabled);
        FrontEndPainter.Text(
            surface,
            font,
            FrontEndPainter.FocusMarkRightText,
            X + Width - FrontEndPainter.FocusMarkRightInset,
            labelRow,
            ink,
            emboss,
            !Enabled);
    }

    /// <summary>
    /// Where a caption's pen row sits inside a button of the given height: vertically centred.
    /// </summary>
    /// <remarks>
    /// F1's buttons were all 15 design rows tall and used a fixed pen offset of 4.  F2's CONFLICT
    /// SELECTION buttons are 25 tall (the original's screen), and their captions sit at +8.  Both are the
    /// caption CENTRED in the button: <c>round((height − fontHeight) / 2)</c> with .NET's default
    /// round-half-to-even gives 4 for 15 and 8 for 25 — the two values the atlas shows — so F1's
    /// pixel-exact 15-tall buttons are unchanged and the 25-tall ones land where the original's screen
    /// puts them (both verified).
    /// </remarks>
    /// <param name="height">The button's height in design rows.</param>
    /// <param name="fontHeight">The caption font's height.</param>
    public static int LabelRowFor(int height, int fontHeight) =>
        (int)Math.Round((height - fontHeight) / 2.0, MidpointRounding.ToEven);
}

/// <summary>
/// One front-end screen: a backdrop, some buttons, and the key model the manual describes.
/// </summary>
/// <remarks>
/// The focus ring is the screen's button list in order and it WRAPS, because that is what a ring of
/// seven widgets with no visible edge has to do.  A greyed button stays IN the ring and refuses
/// Space / Enter / its letter — "greyed, not hidden": the player must be able to see that Create
/// Mission exists and that this build will not run it.
/// </remarks>
public abstract class FrontEndScreen
{
    private readonly List<FrontEndButton> _buttons = [];

    /// <summary>What the screen is called, for the log.</summary>
    public abstract string Name { get; }

    /// <summary>Its widgets, in focus-ring order.</summary>
    public IReadOnlyList<FrontEndButton> Buttons => _buttons;

    /// <summary>Which widget has the focus.</summary>
    public int Focus { get; private set; }

    /// <summary>The focused widget.</summary>
    public FrontEndButton Focused => _buttons[Focus];

    /// <summary>Adds a widget to the ring.</summary>
    /// <param name="button">The widget.</param>
    protected void Add(FrontEndButton button) => _buttons.Add(button);

    /// <summary>Puts the focus on a widget by index (clamped).</summary>
    /// <param name="index">Its position in the ring.</param>
    public void FocusOn(int index) =>
        Focus = _buttons.Count == 0 ? 0 : Math.Clamp(index, 0, _buttons.Count - 1);

    /// <summary>Draws the screen.</summary>
    /// <param name="surface">The design surface.</param>
    /// <param name="fonts">The three front-end fonts (F2).</param>
    public abstract void Render(in FrontEndPainter.Surface surface, FrontEndFonts fonts);

    /// <summary>
    /// Applies one key.  The default is the manual's model, which every screen shares.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>The command a Select fired, or <see cref="FrontEndCommand.None"/>.</returns>
    public virtual FrontEndCommand Press(FrontEndKey key)
    {
        switch (key)
        {
            case FrontEndKey.Next or FrontEndKey.Down or FrontEndKey.Right:
                Step(1);
                return FrontEndCommand.None;
            case FrontEndKey.Previous or FrontEndKey.Up or FrontEndKey.Left:
                Step(-1);
                return FrontEndCommand.None;
            case FrontEndKey.Select:
                return Activate(Focus);
            case FrontEndKey.Back:
                return Back();
            default:
                // Home / End / Backspace belong to the CREATE MISSION pickers' grid and mean
                // nothing anywhere else.  Listed explicitly rather than left to fall into Back,
                // which is what the pre-F8 `default` would have done with them.
                return FrontEndCommand.None;
        }
    }

    /// <summary>
    /// Applies one typed character — the manual's "the first letter of an option selects it".
    /// </summary>
    /// <param name="letter">The character, upper case.</param>
    /// <returns>The command it fired, or <see cref="FrontEndCommand.None"/>.</returns>
    /// <remarks>
    /// F8 made it virtual: the CREATE MISSION pickers give every visible row a digit accelerator of
    /// its own (<c>si[0x10] = index + 0x31</c> @<c>image@0x27949</c>), and those rows are not in
    /// the button ring.
    /// </remarks>
    public virtual FrontEndCommand Type(char letter)
    {
        for (int i = 0; i < _buttons.Count; i++)
        {
            if (_buttons[i].Letter != '\0'
                && char.ToUpperInvariant(_buttons[i].Letter) == char.ToUpperInvariant(letter))
            {
                FocusOn(i);
                return Activate(i);
            }
        }

        return FrontEndCommand.None;
    }

    /// <summary>No widget is under the pointer.</summary>
    public const int NoWidget = -1;

    /// <summary>
    /// Which widget a design point is over, or <see cref="NoWidget"/>.
    /// </summary>
    /// <remarks>
    /// The handle is the widget's position in the ring, which is also the id the original's own
    /// records carry for the screens that have a shipped <c>s_ui_widget</c> array.  A screen with
    /// clickable furniture that is not a button in the ring (MISSION SELECTION's three mission rows)
    /// overrides this and numbers them the way the original numbers them.
    /// </remarks>
    /// <param name="x">The design column.</param>
    /// <param name="y">The design row.</param>
    public virtual int HitTest(int x, int y)
    {
        for (int i = 0; i < _buttons.Count; i++)
        {
            if (_buttons[i].Contains(x, y))
            {
                return i;
            }
        }

        return NoWidget;
    }

    /// <summary>
    /// The pointer has moved over a widget: bring the <c>► ◄</c> ring with it.
    /// </summary>
    /// <remarks>
    /// The original has ONE focus, not two: <c>g_widget_currently_hovered [0xBD62]</c> is the widget
    /// the cursor stands on, and Space activates exactly that
    /// (<c>ui_per_frame_input_poll</c> forces the button-state latch on keycode 0x20 and falls into
    /// the hover dispatch, <c>image@0x2E616</c>).  The port's ring is its keyboard-visible stand-in
    /// for that hover, so the two are kept identical whenever the pointer is over a widget.
    /// </remarks>
    /// <param name="handle">The widget the pointer is over.</param>
    public virtual void Hover(int handle) => FocusOn(handle);

    /// <summary>Presses the widget a click released over.</summary>
    /// <remarks>
    /// A click IS a Select on the widget it landed on, and that is the original's own model rather
    /// than a convenience: <c>ui_per_frame_input_poll</c> treats Space as a synthetic mouse click
    /// (keycode 0x20 forces the button latch and falls into the hover dispatch,
    /// <c>image@0x2E616</c>), so the two arrive at the same widget by the same road.  Going through
    /// <see cref="Press(FrontEndKey)"/> is what gives a click every per-widget Select behaviour a
    /// screen has written for the keyboard — MISSION DESCRIPTION's <c>Diff:</c> cycling in place,
    /// for one — with no second implementation to keep in step.
    /// </remarks>
    /// <param name="handle">The widget.</param>
    /// <returns>The command it fired, or <see cref="FrontEndCommand.None"/> when it is greyed.</returns>
    public virtual FrontEndCommand Click(int handle)
    {
        if ((uint)handle >= (uint)_buttons.Count)
        {
            return FrontEndCommand.None;
        }

        FocusOn(handle);
        return Press(FrontEndKey.Select);
    }

    /// <summary>Whether the widget under the pointer would accept a click.</summary>
    /// <param name="handle">The widget.</param>
    public virtual bool Accepts(int handle) =>
        (uint)handle < (uint)_buttons.Count && _buttons[handle].Enabled;

    /// <summary>
    /// Where the pointer belongs for the current selection: the widget the next Space or Enter
    /// would press.
    /// </summary>
    /// <remarks>
    /// Tab moves the POINTER in the original (F1 finding 1 — the original's screen after a Tab is
    /// the same screen with the pointer moved onto Credits and nothing else changed), through
    /// <c>widget_tab_cursor_navigate @image@0x2E435</c> → <c>widget_mouse_cursor_position_set</c>.
    /// The shell reads this after every key and warps the pointer when it has changed, so the two
    /// can never disagree about what Space will press.
    /// </remarks>
    public virtual (int X, int Y) PointerTarget => Focused.PointerPlacement;

    /// <summary>A widget's label, for the refusal log.</summary>
    /// <param name="handle">The widget.</param>
    public virtual string LabelOf(int handle) =>
        (uint)handle < (uint)_buttons.Count ? _buttons[handle].Label : string.Empty;

    /// <summary>Why a widget is greyed, for the refusal log.</summary>
    /// <param name="handle">The widget.</param>
    public virtual string ReasonOf(int handle) =>
        (uint)handle < (uint)_buttons.Count ? _buttons[handle].DisabledReason : string.Empty;

    /// <summary>What Esc does here — nothing on the root menu.</summary>
    protected virtual FrontEndCommand Back() => FrontEndCommand.None;

    /// <summary>Fires a widget, unless it is greyed.</summary>
    /// <param name="index">Its position in the ring.</param>
    protected FrontEndCommand Activate(int index) =>
        _buttons.Count == 0 || !_buttons[index].Enabled
            ? FrontEndCommand.None
            : _buttons[index].Command;

    private void Step(int by)
    {
        if (_buttons.Count == 0)
        {
            return;
        }

        Focus = ((Focus + by) % _buttons.Count + _buttons.Count) % _buttons.Count;
    }
}
