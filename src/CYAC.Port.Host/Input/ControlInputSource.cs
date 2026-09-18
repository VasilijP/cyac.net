using System.Runtime.InteropServices;
using CYAC.Port.Core.Model.Mission;
using CYAC.Port.Core.Sim.Combat.Player;
using CYAC.Port.Core.Sim.Flight;
using CYAC.Port.Core.Sim.Session;
using CYAC.Port.Host.Configuration;
using CYAC.Port.Host.FrontEnd;
using CYAC.Port.Host.Menu;
using CYAC.Port.Host.Sim;
using CYAC.Port.Render;
using mode13hx.Model;

namespace CYAC.Port.Host.Input;

/// <summary>
/// The window's keyboard, read through mode-13hx's <see cref="Control"/> table.
/// </summary>
/// <remarks>
/// <para>
/// <c>EngineWindow.OnUpdateFrame</c> calls <c>Control.Reset()</c> and then OR-s
/// <c>IsKeyPressed</c> into every bound control, on the WINDOW thread, while the rasterizer thread
/// reads them.  A sample taken inside that reset-then-OR window sees a held key as released, which
/// would fabricate a release/press pair and fire an edge-triggered cockpit key twice.
/// </para>
/// <para>
/// The fix costs nothing and needs no change to the vendored library: a press edge is only armed
/// after the control has read RELEASED on <see cref="ArmingSamples"/> consecutive samples.  The race
/// window is microseconds inside a ~16 ms window-update tick, so it can never span two samples; a
/// genuine press after a genuine release always can.  The cost is that a key tapped and released
/// inside one host frame may be missed, which is true of any polled keyboard.
/// </para>
/// </remarks>
public sealed class ControlInputSource : IFlightInputSource
{
    /// <summary>Consecutive released samples needed before a press counts as an edge.</summary>
    public const int ArmingSamples = 2;

    private static readonly (ControlEnum Control, int Key)[] CockpitBindings =
    [
        (FlyControls.ThrottleUp, CockpitKeys.ThrottleUpKey),
        (FlyControls.ThrottleDown, CockpitKeys.ThrottleDownKey),
        (FlyControls.ThrottlePreset1 + 0, CockpitKeys.FirstPresetKey + 0),
        (FlyControls.ThrottlePreset1 + 1, CockpitKeys.FirstPresetKey + 1),
        (FlyControls.ThrottlePreset1 + 2, CockpitKeys.FirstPresetKey + 2),
        (FlyControls.ThrottlePreset1 + 3, CockpitKeys.FirstPresetKey + 3),
        (FlyControls.ThrottlePreset1 + 4, CockpitKeys.FirstPresetKey + 4),
        (FlyControls.Afterburner, CockpitKeys.AfterburnerKey),
        (FlyControls.Brakes, CockpitKeys.BrakeKey),
        (FlyControls.Flaps, CockpitKeys.FlapKey),
        (FlyControls.Gear, CockpitKeys.GearKey),

        // This table is the wrong door and it BROKE the working keys: the loop over it runs FIRST in
        // Sample and, for an active control, sets `_releasedRun[control] = 0` after emitting the
        // press — so the `Edged(MapWindowZoomIn)` below it always found the edge already spent and
        // returned false.  `.` and `,` stopped zooming the moment these two lines were added.  The
        // zoom is not a cockpit key anyway: CockpitKeys.Apply has no arm for 0x21/0x22, so the
        // cooked keys went to the flight kernel and were counted as unmodelled.  PageUp/PageDown now
        // reach the zoom through the MENU controls instead — see the windowZoomStep block below.
    ];

    private static readonly ControlEnum[] ViewBindings =
    [
        FlyControls.ViewForward, FlyControls.ViewExternal, FlyControls.ViewToggle,
        FlyControls.ViewFunctionKey1 + 0, FlyControls.ViewFunctionKey1 + 1,
        FlyControls.ViewFunctionKey1 + 2, FlyControls.ViewFunctionKey1 + 3,
        FlyControls.ViewFunctionKey1 + 4, FlyControls.ViewFunctionKey1 + 5,
        FlyControls.ViewFunctionKey1 + 6, FlyControls.ViewFunctionKey1 + 7,
        FlyControls.ViewFunctionKey1 + 8, FlyControls.ViewFunctionKey1 + 9,
    ];

    /// <summary>
    /// The scancode→view-id map, as (function key index, shifted) → view (byte-derived at
    /// <c>image@0x33256..0x332CA</c>). F9 now selects <see cref="ViewMode.Map"/>, the port's own map
    /// screen; the dispatch's own answer for <c>0x4300</c> is <c>mov ax,0x0C</c>
    /// @<c>image@0x33288</c>.
    /// </summary>
    private static readonly ViewMode?[] PlainFunctionKeys =
    [
        ViewMode.CockpitForward, ViewMode.CockpitBack, ViewMode.CockpitLeft, ViewMode.CockpitRight,
        ViewMode.CockpitUp, ViewMode.CockpitDown, ViewMode.PlaneToTarget, ViewMode.TargetToPlane,
        ViewMode.Map, ViewMode.FlyBy,
    ];

    private static readonly ViewMode?[] ShiftedFunctionKeys =
    [
        ViewMode.ExternalForward, ViewMode.ExternalBack, ViewMode.ExternalRight,
        ViewMode.ExternalLeft, ViewMode.ExternalBelow, ViewMode.ExternalAbove,
        ViewMode.TargetCockpit, ViewMode.ExternalTarget, ViewMode.Circling, ViewMode.Missile,
    ];

    /// <summary>The combat ladder arms the kernel owns, with their COOKED key codes (C10).</summary>
    private static readonly (ControlEnum Control, int Key)[] CombatBindings =
    [
        (FlyControls.Chaff, CombatKeyLadder.ChaffKey),
        (FlyControls.Flare, CombatKeyLadder.FlareKey),
        (FlyControls.TargetCycle, MissionSession.TargetCycleKey),

        // The manual's `'` (p.48): the same ladder, the proximity selector arm.
        (FlyControls.TargetNearest, CombatKeyLadder.LockOnListModeKey),
    ];

    /// <summary>
    /// <b>Shift-1</b>..<b>Shift-4</b>, in the manual's own order: envelope, target, map, Yeager
    /// (p.14).  The bits are <see cref="CockpitOverlayFlags"/>' own 0x01/0x02/0x04/0x08, so the
    /// n-th key flips the n-th bit — which is why the table is the bit values in order and not a
    /// mapping.
    /// </summary>
    // The ORDER is the manual's and the Help menu's, not the bit order: Shift-1 = MAP, Shift-2 =
    // ENVELOPE, Shift-3 = TARGET, Shift-4 = YEAGER (manual: "The Map Window (Shift-1)", "press
    // Shift-2 [for] the flight envelope", "the Target Window (press Shift-3)", "Press Shift-4 to
    // turn the Yeager Window on"; the Help items 0x406..0x409 push cooked 0x21/0x40/0x23/0x24 at
    // image@0x21790..0x217A2). Bound the keys in [0xF1CB] BIT order, which is visible at
    // the window: "there are greyed out placeholders already, under Help".
    private static readonly CockpitOverlayFlags[] OverlayToggleBindings =
    [
        CockpitOverlayFlags.Map, CockpitOverlayFlags.Envelope,
        CockpitOverlayFlags.Target, CockpitOverlayFlags.Yeager,
    ];

    private static readonly ControlEnum[] WeaponBindings =
    [
        FlyControls.WeaponPrevious, FlyControls.WeaponNext, FlyControls.RadarToggle, FlyControls.Eject,
        FlyControls.CockpitToggle, FlyControls.FlightInfoToggle, FlyControls.NavCycle,
        FlyControls.MapCentre, FlyControls.MapFit,

        // The two situational-awareness letters.  They are read as EDGES under the Ctrl
        // modifier, so their arming runs have to exist before Edge can ask for them.
        FlyControls.NearestBogey, FlyControls.NearestFriendly,
    ];

    /// <summary>
    /// The five menu keys that have a control of their very own.  The four arrows and Enter do NOT:
    /// mode-13hx binds one control per physical key, so those are the FLIGHT controls read a second
    /// way and the rasterizer decides which meaning a frame's edge carries.
    /// </summary>
    private static readonly (ControlEnum Control, FlightMenuKey Key)[] MenuBindings =
    [
        (FlyControls.MenuToggle, FlightMenuKey.Open),

        // Tab moved off MenuToggle so the FRONT END can tell it from ESC; it keeps opening the
        // ESC menu in flight.
        (FlyControls.FrontEndTab, FlightMenuKey.Open),
        (FlyControls.MenuHome, FlightMenuKey.Home),
        (FlyControls.MenuEnd, FlightMenuKey.End),
        (FlyControls.MenuPageUp, FlightMenuKey.PageUp),
        (FlyControls.MenuPageDown, FlightMenuKey.PageDown),
    ];

    /// <summary>
    /// The FRONT END's own keys.  Four of them are controls the flight already owns and are read a
    /// second way (the same trick M1 plays with the arrows); the shell decides the meaning,
    /// because while a sortie is up nothing reads this list at all.
    /// </summary>
    private static readonly (ControlEnum Control, FrontEndKey Key)[] FrontEndBindings =
    [
        (FlyControls.StickForward, FrontEndKey.Up),
        (FlyControls.StickBack, FrontEndKey.Down),
        (FlyControls.StickLeft, FrontEndKey.Left),
        (FlyControls.StickRight, FrontEndKey.Right),
        (FlyControls.TargetCycle, FrontEndKey.Select),   // Enter
        (FlyControls.Fire, FrontEndKey.Select),          // Space
        (FlyControls.MenuToggle, FrontEndKey.Back),      // ESC

        // The CREATE MISSION pickers' three extra keys.  Each reuses the control the PHYSICAL key
        // already carries in flight (Home/End are the stats panel's, Backspace is the cockpit
        // toggle), exactly as the arrows reuse the stick's: the front end is not in flight, so no
        // key is bound twice at any one moment.
        (FlyControls.MenuHome, FrontEndKey.Home),        // Home  — image@0x27AE8's 0x4700 arm
        (FlyControls.MenuEnd, FrontEndKey.End),          // End   — its 0x4F00 arm
        (FlyControls.CockpitToggle, FrontEndKey.BackUp), // Backspace — widget 0's accelerator 0x08
    ];

    /// <summary>The arrow keys, which are the STICK controls read as edges.</summary>
    private static readonly (ControlEnum Control, FlightMenuKey Key)[] MenuArrowBindings =
    [
        (FlyControls.StickForward, FlightMenuKey.Up),
        (FlyControls.StickBack, FlightMenuKey.Down),
        (FlyControls.StickLeft, FlightMenuKey.Left),
        (FlyControls.StickRight, FlightMenuKey.Right),
    ];

    /// <summary>
    /// The type-ahead letters whose key is NOT already a flight key, so they have a control of their
    /// own (<c>FlyControls.MenuTypeAheadFirst + index</c>).  The other thirteen letters and the six
    /// bound digits are read from the edges this frame has already computed, which is why none of
    /// them appears here — see <c>TypeAhead</c>.
    /// </summary>
    private const string TypeAheadCharacters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    /// <summary>
    /// The control a type-ahead character is read through: its own when
    /// <c>FlyOptions</c> found the physical key free, and otherwise the control that key already
    /// carries.  <b>This switch and <c>FlyOptions</c>' binding loop are the same fact stated twice</b>
    /// — the loop binds a type-ahead control only when the key is unbound, and every key it skips
    /// is named here.  If another letter is ever bound, add it here in the same commit.
    /// </summary>
    /// <param name="index">The character's index in <see cref="TypeAheadCharacters"/>.</param>
    private static ControlEnum TypeAheadControl(int index) => index switch
    {
        0 => FlyControls.NearestFriendly,                // A Ctrl-A (manual p.52)
        1 => FlyControls.Brakes,                        // B
        2 => FlyControls.Chaff,                         // C
        4 => FlyControls.Eject,                         // E
        5 => FlyControls.Flaps,                         // F
        6 => FlyControls.Gear,                          // G
        7 => FlyControls.FlightInfoToggle,              // H
        8 => FlyControls.DebugFaceColors,               // I contrast colours
        10 => FlyControls.DebugCull,                    // K back-face cull
        11 => FlyControls.DebugWireframe,               // L wireframe
        12 => FlyControls.MapCentre,                    // M
        13 => FlyControls.MapFit,                       // N
        15 => FlyControls.DebugScreenshot,              // P screenshot (also F12)
        16 => FlyControls.WeaponPrevious,               // Q
        18 => FlyControls.DebugMaskView,                // S mask view
        17 => FlyControls.RadarToggle,                  // R the radar switch (manual p.51)
        21 => FlyControls.ViewToggle,                   // V
        22 => FlyControls.NavCycle,                     // W
        23 => FlyControls.Flare,                        // X
        25 => FlyControls.NearestBogey,                 // Z Ctrl-Z (manual p.52)
        27 => FlyControls.ThrottlePreset1 + 0,          // 1
        28 => FlyControls.ThrottlePreset1 + 1,          // 2
        29 => FlyControls.ThrottlePreset1 + 2,          // 3
        30 => FlyControls.ThrottlePreset1 + 3,          // 4
        31 => FlyControls.ThrottlePreset1 + 4,          // 5
        32 => FlyControls.Afterburner,                  // 6
        _ => FlyControls.MenuTypeAheadFirst + index,
    };

    /// <summary>Whether the character at this index has a control of its very own.</summary>
    /// <param name="index">The character's index.</param>
    private static bool HasOwnControl(int index) =>
        TypeAheadControl(index) == FlyControls.MenuTypeAheadFirst + index;

    /// <summary>Every control whose press edge this frame has already resolved, and what it was.</summary>
    private readonly Dictionary<ControlEnum, bool> _edges = [];

    private readonly List<int> _combat = [];
    private readonly List<FlightMenuKey> _menuKeys = [];
    private readonly List<FrontEndKey> _frontEndKeys = [];
    private readonly List<CockpitOverlayFlags> _overlayToggles = [];

    /// <summary>The RENDER SCRUTINY controls, pre-armed like every other edge.</summary>
    private static readonly ControlEnum[] DebugBindings =
    [
        FlyControls.DebugScreenshot, FlyControls.DebugCull, FlyControls.DebugWireframe,
        FlyControls.DebugFaceColors, FlyControls.DebugMaskView,
    ];

    private readonly List<DebugKey> _debugKeys = [];

    private readonly Dictionary<ControlEnum, int> _releasedRun = [];
    private readonly List<int> _pressed = [];

    /// <summary>Creates the source and pre-arms every cockpit and view control.</summary>
    public ControlInputSource()
    {
        foreach ((ControlEnum control, int _) in CockpitBindings)
        {
            _releasedRun[control] = ArmingSamples;
        }

        foreach (ControlEnum control in ViewBindings)
        {
            _releasedRun[control] = ArmingSamples;
        }

        foreach ((ControlEnum control, int _) in CombatBindings)
        {
            _releasedRun[control] = ArmingSamples;
        }

        foreach (ControlEnum control in WeaponBindings)
        {
            _releasedRun[control] = ArmingSamples;
        }

        // The menu's own keys, the four stick controls read as EDGES, and the type-ahead
        // letters that are not already flight keys.
        foreach ((ControlEnum control, FlightMenuKey _) in MenuBindings)
        {
            _releasedRun[control] = ArmingSamples;
        }

        // The front end's own keys.  SPACE is the only one that was not already edged: the flight
        // reads FIRE as a HELD control, so its arming run has to exist before Edge can ask for
        // it.
        foreach ((ControlEnum control, FrontEndKey _) in FrontEndBindings)
        {
            _releasedRun[control] = ArmingSamples;
        }

        foreach ((ControlEnum control, FlightMenuKey _) in MenuArrowBindings)
        {
            _releasedRun[control] = ArmingSamples;
        }

        foreach (ControlEnum control in DebugBindings)
        {
            _releasedRun[control] = ArmingSamples;
        }

        for (int i = 0; i < FlyControls.MenuTypeAheadCount; i++)
        {
            if (HasOwnControl(i))
            {
                _releasedRun[FlyControls.MenuTypeAheadFirst + i] = ArmingSamples;
            }
        }
    }

    /// <inheritdoc/>
    public FlightInputFrame Sample(double elapsedSeconds)
    {
        _pressed.Clear();
        _edges.Clear();
        _overlayToggles.Clear();
        int mapZoomStep = 0;
        foreach ((ControlEnum control, int key) in CockpitBindings)
        {
            bool active = Active(control);
            _edges[control] = false;
            if (active)
            {
                if (_releasedRun[control] >= ArmingSamples)
                {
                    // Shift-1..Shift-4 are the OVERLAY WINDOW toggles and NOT the throttle presets
                    // they share a physical key with: the shifted edge goes to the window list
                    // instead of the cockpit list, exactly as Shift-E and Shift-W split their own
                    // keys.  Preset 5 has no window, so it stays a throttle key whether or not the
                    // shift is down.
                    int preset = control - FlyControls.OverlayToggleFirst;
                    if ((uint)preset < (uint)OverlayToggleBindings.Length
                        && Active(FlyControls.ViewShift))
                    {
                        _overlayToggles.Add(OverlayToggleBindings[preset]);
                        _edges[control] = true;
                        _releasedRun[control] = 0;
                        continue;
                    }

                    _pressed.Add(key);
                    _edges[control] = true;

                    // The same edge is the MAP's zoom key: `+` / `=` reach gauge_zoom_in_keyhandler
                    // @image@0x0D8B5 and `−` / `_` gauge_zoom_out_keyhandler @image@0x0D8C4 in the
                    // original, where they are not throttle keys at all (the throttle is 7/8 and
                    // 1..5).  The rasterizer decides which meaning the frame gets, by whether the
                    // map is up.
                    if (control == FlyControls.ThrottleUp)
                    {
                        mapZoomStep = 1;
                    }
                    else if (control == FlyControls.ThrottleDown)
                    {
                        mapZoomStep = -1;
                    }
                }

                _releasedRun[control] = 0;
            }
            else if (_releasedRun[control] < ArmingSamples)
            {
                _releasedRun[control]++;
            }
        }

        ViewMode? requested = null;
        bool toggle = false;
        foreach (ControlEnum control in ViewBindings)
        {
            if (!Edged(control))
            {
                continue;
            }

            if (control == FlyControls.ViewForward)
            {
                requested = ViewMode.CockpitForward;
            }
            else if (control == FlyControls.ViewExternal)
            {
                requested = ViewMode.ExternalChase;
            }
            else if (control == FlyControls.ViewToggle)
            {
                toggle = true;
            }
            else
            {
                int index = control - FlyControls.ViewFunctionKey1;
                ViewMode?[] table = Active(FlyControls.ViewShift) ? ShiftedFunctionKeys : PlainFunctionKeys;
                if ((uint)index < (uint)table.Length && table[index] is { } selected)
                {
                    requested = selected;
                }
            }
        }

        HeldControls held = new HeldControls(
            Left: Active(FlyControls.StickLeft)
                || Active(FlyControls.StickForwardLeft) || Active(FlyControls.StickBackLeft),
            Right: Active(FlyControls.StickRight)
                || Active(FlyControls.StickForwardRight) || Active(FlyControls.StickBackRight),
            Forward: Active(FlyControls.StickForward)
                || Active(FlyControls.StickForwardLeft) || Active(FlyControls.StickForwardRight),
            Back: Active(FlyControls.StickBack)
                || Active(FlyControls.StickBackLeft) || Active(FlyControls.StickBackRight),
            Centre: Active(FlyControls.StickCentre));

        _combat.Clear();
        foreach ((ControlEnum control, int key) in CombatBindings)
        {
            if (Edged(control))
            {
                _combat.Add(key);
            }
        }

        int weaponStep = 0;
        if (Edged(FlyControls.WeaponPrevious))
        {
            weaponStep = -1;
        }
        else if (Edged(FlyControls.WeaponNext))
        {
            weaponStep = 1;
        }

        // SHIFT-E, and only with the shift: plain `e` is not the eject key.  The shift half is the
        // same FlyControls.ViewShift the eighteen view keys read.
        bool ejectEdge = Edged(FlyControls.Eject) && Active(FlyControls.ViewShift);

        // The RADAR key.  Neither restart accelerator is mapped, so R is the
        // manual's radar switch (p.51) and carries no modifier meaning at all.  The restart still
        // has its door: the ESC menu's Restart Mission, --respawn and the script words.
        bool radarEdge = Edged(FlyControls.RadarToggle);

        // The two SITUATIONAL-AWARENESS keys.  Ctrl is a MODIFIER here, exactly as ViewShift is for
        // Shift-E / Shift-W: the letter's own edge, gated on the modifier being held, so a bare Z or
        // A stays free for the menu's type-ahead.
        bool ctrl = Active(FlyControls.ModifierCtrl);
        bool nearestBogey = ctrl && Edged(FlyControls.NearestBogey);
        bool nearestFriendly = ctrl && Edged(FlyControls.NearestFriendly);

        // The NAV key: W forward, Shift-W backward (image@0x0127D / image@0x0128C).  One control
        // plus the shared shift, exactly as the eighteen view keys and Shift-E are read.
        int navStep = 0;
        if (Edged(FlyControls.NavCycle))
        {
            navStep = Active(FlyControls.ViewShift) ? -1 : 1;
        }

        bool cockpitToggle = Edged(FlyControls.CockpitToggle);
        bool flightInfoToggle = Edged(FlyControls.FlightInfoToggle);
        bool mapCentre = Edged(FlyControls.MapCentre);
        bool mapFit = Edged(FlyControls.MapFit);

        // The MAP WINDOW's zoom, the original's own two keys: `.` steps the shared pixel-obj
        // scale shift down (zoom IN, image@0x012B2) and `,` steps it up (zoom OUT,
        // image@0x012C7).  Read as EDGES: the original's arms fire once per cooked key, and the
        // ladder's own clamps live in Render.Cockpit.MapWindow.
        int windowZoomStep = 0;
        if (Edged(FlyControls.MapWindowZoomIn))
        {
            windowZoomStep = -1;
        }
        else if (Edged(FlyControls.MapWindowZoomOut))
        {
            windowZoomStep = 1;
        }

        // The RENDER SCRUTINY keys, read as edges; I splits on the shared ViewShift exactly as
        // Shift-E and Shift-W do (bare I toggles the contrast colours, Shift+I reshuffles them).
        // Every one of these is presentation state in the rasterizer.
        _debugKeys.Clear();
        if (Edged(FlyControls.DebugScreenshot))
        {
            _debugKeys.Add(DebugKey.Screenshot);
        }

        if (Edged(FlyControls.DebugCull))
        {
            _debugKeys.Add(DebugKey.CullToggle);
        }

        if (Edged(FlyControls.DebugWireframe))
        {
            _debugKeys.Add(DebugKey.WireframeCycle);
        }

        if (Edged(FlyControls.DebugFaceColors))
        {
            _debugKeys.Add(
                Active(FlyControls.ViewShift) ? DebugKey.FaceColorsReshuffle : DebugKey.FaceColorsToggle);
        }

        if (Edged(FlyControls.DebugMaskView))
        {
            _debugKeys.Add(DebugKey.MaskViewCycle);
        }

        // The menu's keys, gathered EVERY frame.  Five have controls of their own; the four arrows
        // are the stick controls read as edges and Enter is the target-cycle key's edge, because
        // one physical key can carry only one mode-13hx Control.  The rasterizer gives them their
        // menu meaning only while the bar is up.
        _menuKeys.Clear();
        foreach ((ControlEnum control, FlightMenuKey key) in MenuBindings)
        {
            if (Edged(control))
            {
                _menuKeys.Add(key);
            }
        }

        foreach ((ControlEnum control, FlightMenuKey key) in MenuArrowBindings)
        {
            if (Edged(control))
            {
                _menuKeys.Add(key);
            }
        }

        if (_edges.TryGetValue(FlyControls.TargetCycle, out bool enter) && enter)
        {
            _menuKeys.Add(FlightMenuKey.Enter);
        }

        // A PORT ADDITION: PAGE UP / PAGE DOWN also work the map window's zoom. They are the ESC
        // menu's own paging keys (bound above), so this is the same trick the arrows play for the
        // stick and `+`/`-` play for the throttle: ONE physical key, both meanings emitted every
        // frame, and the rasterizer honours the flight one only while the bar is DOWN.  The edges
        // are read out of `_edges`, which the MenuBindings loop has just filled — computing them
        // again here would find them spent, which is exactly the bug the CockpitBindings entries
        // caused.
        if (windowZoomStep == 0)
        {
            if (_edges.TryGetValue(FlyControls.MenuPageUp, out bool pageUp) && pageUp)
            {
                windowZoomStep = -1;
            }
            else if (_edges.TryGetValue(FlyControls.MenuPageDown, out bool pageDown) && pageDown)
            {
                windowZoomStep = 1;
            }
        }

        // The FRONT END's keys, gathered from the edges this frame has already resolved so no
        // control's edge is consumed twice.  Tab and Shift+Tab are one control and the shared
        // ViewShift, exactly as Shift-E and Shift-W are read.
        _frontEndKeys.Clear();
        if (_edges.TryGetValue(FlyControls.FrontEndTab, out bool tab) ? tab : Edged(FlyControls.FrontEndTab))
        {
            _frontEndKeys.Add(
                Active(FlyControls.ViewShift) ? FrontEndKey.Previous : FrontEndKey.Next);
        }

        foreach ((ControlEnum control, FrontEndKey key) in FrontEndBindings)
        {
            if (_edges.TryGetValue(control, out bool edge) ? edge : Edged(control))
            {
                _frontEndKeys.Add(key);
            }
        }


        // The MOUSE.  Motion is consumed (not just read): EngineWindow ADDS into these two Deltas
        // every window update and never clears them, so a reader that does not take them away
        // would see the whole run's travel every frame.
        (int mouseDx, int mouseDy) = (TakeDelta(ControlEnum.MOUSE_DELTA_X),
                                      TakeDelta(ControlEnum.MOUSE_DELTA_Y));

        return new FlightInputFrame(
            held, _pressed, requested, toggle, Active(FlyControls.Fire), _combat, weaponStep,
            Respawn: false, ejectEdge, cockpitToggle,
            flightInfoToggle, navStep, mapZoomStep,
            mapCentre, mapFit, RestartNow: false, _menuKeys, TypeAhead(), _frontEndKeys,
            mouseDx, mouseDy, LeftMouseButton(), MouseWarp: null,
            OverlayToggles: _overlayToggles, RadarToggle: radarEdge,
            NearestBogey: nearestBogey, NearestFriendly: nearestFriendly,
            WindowZoomStep: windowZoomStep, DebugKeys: _debugKeys);
    }

    /// <summary>
    /// The LEFT mouse button, debounced against the window thread's reset-then-OR window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Which control it is.</b>  <c>EngineWindow.OnUpdateFrame</c> OR-s the left button into
    /// <c>config.MouseControls[MOUSE_BUTTON_LEFT]</c>, and mode-13hx's
    /// <c>CommonOptions</c>:68 binds that dictionary slot to <c>Control.Create(VK_USE)</c> — the
    /// same instance its default <c>Key.Space</c> binding uses.  CYAC's <c>FlyOptions</c> clears
    /// <c>KbControls</c> and never binds <c>VK_USE</c> to any key, so in THIS application
    /// <c>VK_USE</c> is the left mouse button and nothing else.  That is a fact about the port's own
    /// binding table, so it is pinned by a test
    /// (<c>NoKeyboardKeyIsBoundToTheControlTheLeftMouseButtonSets</c>) rather than left to trust —
    /// and it is why no local change to the vendored tree was needed
    /// (<c>external/mode-13hx/VENDOR.md</c>; the upstream slot/instance mismatch is reported, not
    /// patched).
    /// </para>
    /// <para>
    /// <b>Why the debounce.</b>  A sample taken inside <c>Control.Reset()</c>'s window reads a held
    /// button as released — the same race the class's arming rule exists for.  For a KEY that costs
    /// a missed press; for the mouse it would fabricate a RELEASE, and the original activates
    /// widgets on the release, so it would fabricate a CLICK.  A release is therefore believed only
    /// after <see cref="ArmingSamples"/> consecutive released samples, which no microsecond-wide
    /// race can span.
    /// </para>
    /// </remarks>
    private bool LeftMouseButton()
    {
        bool down = Active(ControlEnum.VK_USE) && !Active(ControlEnum.MOUSE_BUTTON_RIGHT);
        if (down)
        {
            _mouseReleasedRun = 0;
            return true;
        }

        if (_mouseReleasedRun < ArmingSamples)
        {
            _mouseReleasedRun++;
        }

        return _mouseReleasedRun < ArmingSamples;
    }

    /// <summary>Takes a mouse-motion control's accumulated delta and zeroes it.</summary>
    /// <param name="control">The control.</param>
    private static int TakeDelta(ControlEnum control) =>
        Interlocked.Exchange(ref Control.Create(control).Delta, 0);

    private int _mouseReleasedRun = ArmingSamples;

    /// <summary>
    /// The letter or digit whose press edge landed this frame, for the menu's type-ahead, or
    /// <c>'\0'</c>.
    /// </summary>
    /// <remarks>
    /// Every own-control character is polled even after a match, so its arming run keeps advancing;
    /// the aliased ones are read out of the edges this frame has already resolved, so no control's
    /// edge is ever consumed twice.
    /// </remarks>
    private char TypeAhead()
    {
        char found = '\0';
        for (int i = 0; i < FlyControls.MenuTypeAheadCount; i++)
        {
            bool edge = HasOwnControl(i)
                ? Edge(FlyControls.MenuTypeAheadFirst + i)
                : _edges.TryGetValue(TypeAheadControl(i), out bool was) && was;
            if (edge && found == '\0')
            {
                found = TypeAheadCharacters[i];
            }
        }

        return found;
    }

    /// <summary>A press edge, remembered so a second reader of the same control sees the same answer.</summary>
    /// <param name="control">The control.</param>
    private bool Edged(ControlEnum control)
    {
        bool edge = Edge(control);
        _edges[control] = edge;
        return edge;
    }

    /// <summary>A press edge on a control, under the same two-sample arming rule.</summary>
    /// <param name="control">The control.</param>
    private bool Edge(ControlEnum control)
    {
        // A control this source has never seen counts as fully ARMED, and is registered on the spot.
        //
        //   It used to be indexed straight out of the dictionary, which threw
        //   KeyNotFoundException for any control that was not in one of the constructor's pre-arm
        //   tables.  An earlier pass read FlyControls.MapWindowZoomIn/Out through Edged() without adding them to
        //   a table, so `.` and `,` would have CRASHED the window on the first press; the crash was
        //   masked while those controls sat in CockpitBindings (whose loop pre-arms them) and came
        //   straight back when that wrong entry was removed — which is how MapZoomKeyTests found it.
        //   Defaulting here fixes the whole class rather than these two: pre-arming is what every
        //   table in the constructor does, so an unregistered control simply gets the same
        //   treatment, and a first press is honoured instead of being swallowed.
        ref int released = ref CollectionsMarshal.GetValueRefOrAddDefault(
            _releasedRun, control, out bool existed);
        if (!existed)
        {
            released = ArmingSamples;
        }

        if (!Active(control))
        {
            if (released < ArmingSamples)
            {
                released++;
            }

            return false;
        }

        bool armed = released >= ArmingSamples;
        released = 0;
        return armed;
    }

    private static bool Active(ControlEnum control) => Control.Create(control).Active;
}
