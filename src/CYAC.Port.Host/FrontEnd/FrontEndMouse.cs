using CYAC.Port.Core.Data;
using CYAC.Port.Host.Input;

namespace CYAC.Port.Host.FrontEnd;

/// <summary>
/// One front-end frame's INPUT: the pointer, the keys, and the click, in that order.
/// </summary>
/// <remarks>
/// <para>
/// <b>The one rule that keeps the pointer and the <c>► ◄</c> ring together.</b>  The pointer's
/// hit-test wins when it is over a widget (the ring follows it), and the pointer follows the ring
/// whenever a KEY moved it.  So whatever Space or Enter would press is what the pointer is standing
/// on — which is the original's model exactly.  The original has only ONE focus,
/// <c>g_widget_currently_hovered [0xBD62]</c>, the widget the cursor is over; <b>Space is a
/// synthetic click on it</b> (<c>ui_per_frame_input_poll</c> forces the button latch
/// <c>[0xBD5A] = 1</c> on keycode 0x20 and falls straight into the hover dispatch,
/// <c>image@0x2E616</c>), and <b>Tab moves the cursor</b>, not a separate focus
/// (<c>widget_tab_cursor_navigate @image@0x2E435</c> ends in
/// <c>widget_mouse_cursor_position_set @image@0x2E4DD</c> — which is why
/// the original's screen after a Tab onto Credits is the same screen with nothing changed but
/// the pointer's position, F1 finding 1).
/// </para>
/// <para>
/// <b>The two deviations, both deliberate.</b>  (1) When the pointer stands over NOTHING the
/// original has no target and Space does nothing at all; the port leaves the ring where it was, so
/// a keyboard-only player is never left without one.  (2) The arrow keys move the marks and NOT the
/// cursor in the original (they are row navigation, <c>widget_row_step @image@0x2E55F</c>); here
/// they move both, for the same reason.
/// </para>
/// <para>
/// <b>What is deliberately absent.</b>  The wheel does nothing — the original's mouse had none, and
/// the port reproduces rather than extends; paging MISSION SELECTION on it would be a port
/// addition and is not made.  The right button does nothing either, and that is not an
/// omission: the poll latches <c>[0xBD5A] = (g_mouse_button_state [0x4741] == 1)</c>
/// (<c>image@0x2E605..0x2E616</c>) — an equality against the whole button mask, whose bit 1 is the
/// right button (<c>mouse_event_dispatcher @image@0x2E046</c>) — so a right button held alongside
/// the left reads as NO button and cancels the click.  <see cref="FlightInputFrame.MouseLeft"/>
/// carries that composite.
/// </para>
/// </remarks>
public sealed class FrontEndMouse
{
    private readonly FrontEndPointer? _pointer;
    private double _x;
    private double _y;
    private bool _leftWasDown;
    private int _pressedWidget = FrontEndScreen.NoWidget;
    private FrontEndScreen? _pressedOn;

    /// <summary>Builds the mouse over the original's own arrow.</summary>
    /// <param name="tree">The data tree the cursor art comes from.</param>
    public FrontEndMouse(DataTree tree)
        : this(FrontEndPointer.Load(tree))
    {
    }

    /// <summary>Builds the mouse over a given pointer (null = the tree has no cursor art).</summary>
    /// <param name="pointer">The pointer.</param>
    public FrontEndMouse(FrontEndPointer? pointer)
    {
        _pointer = pointer;
        _x = pointer?.X ?? FrontEndPointer.StartPosition.X;
        _y = pointer?.Y ?? FrontEndPointer.StartPosition.Y;
    }

    /// <summary>The pointer, or null when the tree carries no cursor art.</summary>
    public FrontEndPointer? Pointer => _pointer;

    /// <summary>Whether the left button was down on the last frame applied.</summary>
    public bool LeftDown => _leftWasDown;

    /// <summary>
    /// Applies one front-end frame: motion and hover, then the keys, then the button edges.
    /// </summary>
    /// <param name="frontEnd">The front end.</param>
    /// <param name="input">The frame's input.</param>
    /// <param name="scale">Host pixels per design pixel, for the motion deltas.</param>
    /// <param name="act">What to do with each request the frame raises.</param>
    public void Frame(
        FrontEnd frontEnd, in FlightInputFrame input, int scale, Action<FrontEndRequest> act)
    {
        ArgumentNullException.ThrowIfNull(frontEnd);
        ArgumentNullException.ThrowIfNull(act);

        if (Move(input, scale) && _pointer is not null)
        {
            frontEnd.Hover(frontEnd.Current.HitTest(_pointer.X, _pointer.Y));
        }

        (int X, int Y) before = frontEnd.PointerTarget;
        FrontEndScreen screen = frontEnd.Current;
        foreach (FrontEndKey key in input.FrontEndKeys ?? [])
        {
            act(frontEnd.Press(key));
        }

        if (input.MenuTypeAhead != '\0')
        {
            act(frontEnd.Type(input.MenuTypeAhead));
        }

        // Tab, Shift+Tab and the arrows moved the ring on the SAME screen: bring the pointer with
        // them, which is what the original's Tab does to the hardware cursor.  A screen CHANGE
        // leaves the pointer where the player's hand left it, as a hardware cursor stays put.
        if (_pointer is not null && ReferenceEquals(screen, frontEnd.Current)
            && frontEnd.PointerTarget != before)
        {
            WarpTo(frontEnd.PointerTarget);
        }

        Click(frontEnd, input, act);
    }

    /// <summary>Puts the pointer at a design point and resyncs the fractional position.</summary>
    /// <param name="point">The design point.</param>
    public void WarpTo((int X, int Y) point)
    {
        _pointer?.MoveTo(point.X, point.Y);
        _x = _pointer?.X ?? point.X;
        _y = _pointer?.Y ?? point.Y;
    }

    /// <summary>Applies this frame's motion; true when the pointer actually moved.</summary>
    /// <param name="input">The frame's input.</param>
    /// <param name="scale">Host pixels per design pixel.</param>
    private bool Move(in FlightInputFrame input, int scale)
    {
        if (_pointer is null)
        {
            return false;
        }

        if (input.MouseWarp is { } warp)
        {
            (int wasX, int wasY) = (_pointer.X, _pointer.Y);
            WarpTo((warp.X, warp.Y));
            return _pointer.X != wasX || _pointer.Y != wasY;
        }

        if (input.MouseDx == 0 && input.MouseDy == 0)
        {
            return false;
        }

        // The window reports WINDOW pixels; a design pixel is `scale` of them.  The position is
        // kept fractional so a slow mouse still moves at scale 10 — an integer position would drop
        // every step smaller than one design pixel and the pointer would stick.
        (int x, int y) = (_pointer.X, _pointer.Y);
        double by = Math.Max(1, scale);
        _x = Math.Clamp(_x + (input.MouseDx / by), 0, FrontEndPainter.DesignWidth - 1);
        _y = Math.Clamp(_y + (input.MouseDy / by), 0, FrontEndPainter.DesignHeight - 1);
        _pointer.MoveTo((int)_x, (int)_y);
        return _pointer.X != x || _pointer.Y != y;
    }

    /// <summary>The left button's press and release edges, and what the release presses.</summary>
    /// <remarks>
    /// The original activates on the RELEASE, over the widget the press latched:
    /// <c>ui_per_frame_input_poll</c> reaches its "widget activated" arm only on the transition to
    /// button-UP and only when the hit-test still finds the hovered widget
    /// (<c>image@0x2E73D..0x2E769</c>).  A press that slides off before the release fires nothing —
    /// and neither does a release on a different SCREEN, which the original cannot even reach
    /// because a screen change tears its widget list down.
    /// </remarks>
    /// <param name="frontEnd">The front end.</param>
    /// <param name="input">The frame's input.</param>
    /// <param name="act">What to do with the request the click raises.</param>
    private void Click(FrontEnd frontEnd, in FlightInputFrame input, Action<FrontEndRequest> act)
    {
        if (_pointer is null)
        {
            return;
        }

        // A key changed screens with the button still held: forget the press rather than hold a
        // reference to a screen the stack has already disposed.
        if (_pressedOn is not null && !ReferenceEquals(_pressedOn, frontEnd.Current))
        {
            _pressedWidget = FrontEndScreen.NoWidget;
            _pressedOn = null;
        }

        bool down = input.MouseLeft;
        if (down && !_leftWasDown)
        {
            _pressedWidget = frontEnd.Current.HitTest(_pointer.X, _pointer.Y);
            _pressedOn = frontEnd.Current;
            frontEnd.Hover(_pressedWidget);
        }
        else if (!down && _leftWasDown)
        {
            int released = frontEnd.Current.HitTest(_pointer.X, _pointer.Y);
            if (released != FrontEndScreen.NoWidget && released == _pressedWidget
                && ReferenceEquals(_pressedOn, frontEnd.Current))
            {
                act(frontEnd.Click(released));
            }

            _pressedWidget = FrontEndScreen.NoWidget;
            _pressedOn = null;
        }

        _leftWasDown = down;
    }
}
