using CYAC.Port.Core.Model.Mission;
using CYAC.Port.Host.FrontEnd;
using CYAC.Port.Host.Menu;
using CYAC.Port.Host.Sim;
using CYAC.Port.Render;

namespace CYAC.Port.Host.Input;

/// <summary>One host frame's input: what is held, which cockpit keys were pressed, which view was asked for.</summary>
/// <param name="Held">The stick directions held.</param>
/// <param name="CockpitKeys">Cooked cockpit keys whose PRESS edge landed in this frame.</param>
/// <param name="RequestedView">
/// The view a view key selected this frame, or <see langword="null"/> when none was pressed.  View
/// selection is a PRESENTATION decision and never reaches the simulation — the original's own
/// <c>publish_view_mode @image@0x2376A</c> writes only camera state.
/// </param>
/// <param name="ToggleView">True when the frame asked for the other view rather than a named one.</param>
/// <param name="Trigger">Whether the FIRE control is held this frame (<c>[0x32ED]</c>).</param>
/// <param name="CombatKeys">
/// Cooked COMBAT keys whose press edge landed this frame — the ladder arms the combat kernel owns
/// (chaff <c>0x39</c>, flare <c>0x30</c>, the lock-on list mode <c>0x27</c>).
/// </param>
/// <param name="WeaponStep">−1 for the previous weapon slot, +1 for the next, 0 for neither.</param>
/// <param name="Respawn">
/// No key sets it; the restart accelerators are unmapped.  It survives for the <c>respawn</c> script
/// word and <c>--respawn</c>, and is still honoured only once the sortie is over.
/// </param>
/// <param name="Eject">
/// True when SHIFT-E's press edge landed this frame: the pilot pulled the handle (<c>image@0x012F8</c>, command 69 =
/// <c>'E'</c> shifted).
/// </param>
/// <param name="ToggleFlightInfo">
/// True when the HUD-overlay key's press edge landed this frame: the original's Ctrl-F "Flight Info" item, which
/// flips <c>g_flight_info_visible [0xB0]</c> and so switches the mask between <c>0x7F7</c> and <c>0x500</c>
/// (<c>image@0x0C627</c>).  Presentation only.
/// </param>
/// <param name="ToggleCockpit">
/// True when BACKSPACE's press edge landed this frame: show or hide the cockpit
/// (<c>gameplay_key_backspace_cockpit_toggle @image@0x013D8</c>, which flips <c>g_cockpit_visible_flag [0xE471]</c>).
/// Presentation only — it never reaches the simulation.
/// </param>
/// <param name="NavStep">
/// +1 when <b>W</b>'s press edge landed this frame (<c>nav_slot_cycle_next @image@0x08D65</c>, ladder arm
/// <c>image@0x0127D</c>), −1 for <b>Shift+W</b> (<c>nav_slot_cycle_prev @image@0x08D8A</c>, <c>image@0x0128C</c>), 0
/// for neither.
/// </param>
/// <param name="MapZoomStep">
/// +1 when the ZOOM-IN key's press edge landed this frame, −1 for ZOOM-OUT, 0 for neither. The keys are the
/// original's own: <c>gauge_zoom_in_keyhandler @image@0x0D8B5</c> is reached from <c>+</c> / <c>=</c> /
/// keypad-<c>+</c> and <c>gauge_zoom_out_keyhandler @image@0x0D8C4</c> from <c>−</c> / <c>_</c> / keypad-<c>−</c>
/// (1160).  They are the same physical keys the port's H1 throttle uses, so the rasterizer honours the zoom only
/// while the MAP is the active view and lets them be the throttle everywhere else (H27 deviation D8).
/// </param>
/// <param name="MapCentre">
/// PORT ADDITION — centre the map on the player.  The original's map is always centred on him, so it needs no
/// key.
/// </param>
/// <param name="MapFit">
/// PORT ADDITION — frame the mission's objects, the editor's <c>FitToContent</c>.
/// </param>
/// <param name="RestartNow">
/// PORT ADDITION — the key is unmapped; the ESC menu's Restart Mission and the <c>restart</c>
/// script word are what set it: restart the mission AT ONCE, mid-flight included.  The original has no such key — its
/// only ways out of a sortie are Ctrl-Q and the <c>?</c> menu's "End Mission", both of which go to the debrief
/// (<c>flight_session_end @image@0x30328</c>) — so this is a port QoL, guarded by the shift so it cannot be hit by
/// accident.  The shift half is the same <c>FlyControls.ViewShift</c> that Shift-E and Shift-W read.
/// </param>
/// <param name="MenuKeys">
/// The in-flight ESC menu's own key edges this frame, in order.  They are reported EVERY frame, open or shut: the
/// rasterizer decides, because most of these keys are flight controls when the bar is down.
/// </param>
/// <param name="MenuTypeAhead">
/// A letter or digit whose press edge landed this frame, for the menu's type-ahead search, or <c>'\0'</c>.  Like
/// <paramref name="MenuKeys"/> it is reported whether or not the bar is up.
/// </param>
/// <param name="FrontEndKeys">
/// The FRONT END's own key edges this frame (Tab / Shift+Tab / the arrows / Space / Enter / Esc), in order.  Like
/// <paramref name="MenuKeys"/> they are reported every frame and the shell decides: while a sortie is up the flight
/// rasterizer reads the frame and these are ignored, and while the menu is up nothing else reads it.  A LETTER still
/// arrives as <paramref name="MenuTypeAhead"/> — the front end's first-letter rule and the ESC menu's type-ahead are
/// the same character, read once.
/// </param>
/// <param name="MouseDx">
/// How far the mouse moved horizontally this frame, in WINDOW pixels.  The window puts every mouse in
/// <c>CursorMode.Raw</c> (<c>EngineWindow.OnLoad</c>), which hides and locks the OS cursor and reports motion rather
/// than a place, so the host integrates the deltas into its own pointer (<see cref="FrontEnd.HostShell"/>) and
/// divides by the front end's integer scale on the way. Ignored by the flight, which has no mouse control mode in
/// this release.
/// </param>
/// <param name="MouseDy">The vertical half of the same.</param>
/// <param name="MouseLeft">
/// Whether the LEFT mouse button is down this frame, with the right button up.  The composite is the original's own
/// test: <c>ui_per_frame_input_poll</c> latches <c>[0xBD5A] = (g_mouse_button_state [0x4741] == 1)</c>
/// (<c>image@0x2E5FB..0x2E616</c>), and <c>[0x4741]</c> is the ISR's button mask with bit 0 left and bit 1 right
/// (<c>mouse_event_dispatcher @image@0x2E046</c>), so a right button held alongside the left reads as "no button" and
/// the click never fires.  HELD, not edged: the shell computes the press and release edges, because the original
/// activates on the RELEASE.
/// </param>
/// <param name="OverlayToggles">
/// The in-flight OVERLAY WINDOW bits whose toggle edge landed this frame: <b>Shift-1</b> envelope, <b>Shift-2</b>
/// target, <b>Shift-3</b> map, <b>Shift-4</b> Yeager (the manual p.14, and the Help menu's four Window items).  Each
/// flips one bit of <c>g_inflight_overlay_visibility [0xF1CB]</c>.  Reported every frame; the rasterizer owns the
/// live byte.
/// </param>
/// <param name="MouseWarp">
/// A headless script's <c>mouseto:</c> / <c>click:</c> word: put the pointer at this DESIGN point before anything
/// else this frame.  Null from a real mouse, which only ever reports motion.
/// </param>
/// <param name="NearestBogey">
/// <b>Ctrl-Z</b>'s edge: report where the nearest BOGEY is (<c>image@0x01294</c>).
/// </param>
/// <param name="NearestFriendly">
/// <b>Ctrl-A</b>'s edge: the nearest FRIENDLY (<c>image@0x012A7</c>).
/// </param>
/// <param name="AdvisorCode">
/// An ADVISORY ACTION CODE to raise this frame, or -1.  The script word <c>advisor&lt;n&gt;</c> only; there is no key
/// for it, because in the original nothing but the game itself ever raises one.  It is the port's capture instrument
/// for the YEAGER window, the same role W1's <c>radar</c> and W2's <c>bogey</c>/<c>friendly</c> words play — and it
/// is byte-inert, because the advisor queue it feeds is presentation state.
/// </param>
/// <param name="RadarToggle">
/// The manual's <b>R</b> (p.51): switch the aircraft's radar on or off.  Since the key carries nothing else — the
/// port's restart accelerators are unmapped, leaving the ESC menu's Restart Mission as the one door
/// (<see cref="Respawn"/> and <see cref="RestartNow"/> stay for <c>--respawn</c> and the <c>respawn</c> /
/// <c>restart</c> script words).
/// </param>
/// <param name="WindowZoomStep">
/// The MAP WINDOW's zoom keys, <c>.</c> and <c>,</c>: <c>−1</c> steps the shared pixel-obj scale shift
/// <c>g_stipple_scale_shift [0x3234]</c> DOWN (zoom in, <c>image@0x012B2</c>), <c>+1</c> steps it up (zoom out,
/// <c>image@0x012C7</c>).  Distinct from <see cref="FlightInputFrame.MapZoomStep"/>, which is H27's port-added zoom
/// for the F9 FULL-SCREEN map — the two are different subsystems in the original and neither key touches the other.
/// </param>
/// <param name="DebugKeys">
/// The RENDER SCRUTINY key edges this frame, in order (<see cref="DebugKey"/>): F12 / P photograph, K flips the
/// back-face cull, L cycles the wireframe, I toggles the contrast colours and Shift+I reshuffles them.  Port
/// additions, presentation only.
/// </param>
public readonly record struct FlightInputFrame(
    HeldControls Held,
    IReadOnlyList<int> CockpitKeys,
    ViewMode? RequestedView = null,
    bool ToggleView = false,
    bool Trigger = false,
    IReadOnlyList<int>? CombatKeys = null,
    int WeaponStep = 0,
    bool Respawn = false,
    bool Eject = false,
    bool ToggleCockpit = false,
    bool ToggleFlightInfo = false,
    int NavStep = 0,
    int MapZoomStep = 0,
    bool MapCentre = false,
    bool MapFit = false,
    bool RestartNow = false,
    IReadOnlyList<FlightMenuKey>? MenuKeys = null,
    char MenuTypeAhead = '\0',
    IReadOnlyList<FrontEndKey>? FrontEndKeys = null,
    int MouseDx = 0,
    int MouseDy = 0,
    bool MouseLeft = false,
    (int X, int Y)? MouseWarp = null,
    IReadOnlyList<CockpitOverlayFlags>? OverlayToggles = null,
    bool RadarToggle = false,
    bool NearestBogey = false,
    bool NearestFriendly = false,
    int WindowZoomStep = 0,
    int AdvisorCode = -1,
    IReadOnlyList<DebugKey>? DebugKeys = null);

/// <summary>One RENDER SCRUTINY key edge (<see cref="FlightInputFrame.DebugKeys"/>).</summary>
public enum DebugKey
{
    /// <summary>F12 / P — save the presented frame as a PNG and the scene behind it as a dump.</summary>
    Screenshot = 0,

    /// <summary>K — back-face culling on / off.</summary>
    CullToggle = 1,

    /// <summary>L — wireframe off → overlay → only → off.</summary>
    WireframeCycle = 2,

    /// <summary>I — contrasting face colours on / off.</summary>
    FaceColorsToggle = 3,

    /// <summary>Shift+I — reshuffle the contrast colours (switching them on if they were off).</summary>
    FaceColorsReshuffle = 4,

    /// <summary>S — the mask view, off → silhouette → interior → off.</summary>
    MaskViewCycle = 5,
}

/// <summary>Where a host frame's input comes from — a keyboard, or a script.</summary>
public interface IFlightInputSource
{
    /// <summary>Samples one host frame.</summary>
    /// <param name="elapsedSeconds">Wall seconds since the previous sample.</param>
    FlightInputFrame Sample(double elapsedSeconds);
}
