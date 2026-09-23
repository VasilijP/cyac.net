using System.Globalization;
using CommandLine;
using CYAC.Port.Core.Model.World;
using CYAC.Port.Render;
using CYAC.Port.Render.Ground;
using mode13hx.Configuration;
using mode13hx.Model;
using Silk.NET.Input;

namespace CYAC.Port.Host.Configuration;

/// <summary>
/// The controls the PoC binds, on top of mode-13hx's built-in five.
/// </summary>
/// <remarks>
/// <para>
/// They are numbered from mode-13hx's <c>ControlEnum.APP_BASE</c> — the one extension the port
/// made to the vendored dependency (<c>external/mode-13hx/VENDOR.md</c>), so an application can
/// declare its own actions without the library having to know their names.
/// </para>
/// <para>
/// The stick set is the original's, not a modern invention: <c>kbd_numpad_joystick_position_set
/// @image@0x01819</c> is an 11-entry jump table over numpad scancodes <c>0x47..0x51</c> — Home / Up
/// / PgUp / (5) / Left / (−) / Right / (+) / End / Down / PgDn — that writes the calibration
/// extremes into <c>g_joystick_axis_x_clamped [0xE478]</c> and <c>g_joystick_axis_y_clamped
/// [0xE47A]</c>.  On a 1991 keyboard those scancodes ARE the arrow keys, which is why arrows and
/// numpad bind to the same controls here.
/// </para>
/// </remarks>
public static class FlyControls
{
    /// <summary>Numpad 4 / Left — <c>[0xE478] := g_joystick_x_min_i16</c> (<c>image@0x01874</c>).</summary>
    public const ControlEnum StickLeft = ControlEnum.APP_BASE + 0;

    /// <summary>Numpad 6 / Right — <c>[0xE478] := g_joystick_x_max_i16</c> (<c>image@0x01882</c>).</summary>
    public const ControlEnum StickRight = ControlEnum.APP_BASE + 1;

    /// <summary>Numpad 8 / Up — stick forward, <c>[0xE47A] := g_joystick_y_min_i16</c> (<c>image@0x01861</c>).</summary>
    public const ControlEnum StickForward = ControlEnum.APP_BASE + 2;

    /// <summary>Numpad 2 / Down — stick back, <c>[0xE47A] := g_joystick_y_max_i16</c> (<c>image@0x0188C</c>).</summary>
    public const ControlEnum StickBack = ControlEnum.APP_BASE + 3;

    /// <summary>Numpad 7 / Home — the NW corner (<c>image@0x0185C</c>).</summary>
    public const ControlEnum StickForwardLeft = ControlEnum.APP_BASE + 4;

    /// <summary>Numpad 9 / PgUp — the NE corner (<c>image@0x01869</c>).</summary>
    public const ControlEnum StickForwardRight = ControlEnum.APP_BASE + 5;

    /// <summary>Numpad 1 / End — the SW corner (<c>image@0x01887</c>).</summary>
    public const ControlEnum StickBackLeft = ControlEnum.APP_BASE + 6;

    /// <summary>Numpad 3 / PgDn — the SE corner (<c>image@0x01894</c>).</summary>
    public const ControlEnum StickBackRight = ControlEnum.APP_BASE + 7;

    /// <summary>Numpad 5 — the original's explicit no-op arm; here it CENTRES the stick.</summary>
    public const ControlEnum StickCentre = ControlEnum.APP_BASE + 8;

    /// <summary>Cockpit key <c>'8'</c> — throttle +5 % (<c>CockpitKeys.ThrottleUpKey</c>).</summary>
    public const ControlEnum ThrottleUp = ControlEnum.APP_BASE + 16;

    /// <summary>Cockpit key <c>'7'</c> — throttle −5 % (<c>CockpitKeys.ThrottleDownKey</c>).</summary>
    public const ControlEnum ThrottleDown = ControlEnum.APP_BASE + 17;

    /// <summary>Cockpit keys <c>'1'</c>..<c>'5'</c> — the throttle presets.</summary>
    public const ControlEnum ThrottlePreset1 = ControlEnum.APP_BASE + 18;

    /// <summary>Cockpit key <c>'6'</c> — afterburner toggle.</summary>
    public const ControlEnum Afterburner = ControlEnum.APP_BASE + 23;

    /// <summary>Cockpit key <c>'b'</c> — brakes (<c>master[+0x124]</c> bit 3, <c>image@0x2A49F</c>).</summary>
    public const ControlEnum Brakes = ControlEnum.APP_BASE + 24;

    /// <summary>Cockpit key <c>'f'</c> — flaps (bit 1, <c>image@0x2A4C4</c>).</summary>
    public const ControlEnum Flaps = ControlEnum.APP_BASE + 25;

    /// <summary>Cockpit key <c>'g'</c> — landing gear (bit 2, <c>image@0x2A4E9</c>).</summary>
    public const ControlEnum Gear = ControlEnum.APP_BASE + 26;

    /// <summary>
    /// F1 — the FORWARD view.  The original's <c>view_mode_setter @image@0x32690</c> reaches it
    /// through the scancode→view-id dispatch at <c>image@0x33256</c>, and the in-flight View menu
    /// spells it "Forward F1" (a captured frame of the original).
    /// </summary>
    public const ControlEnum ViewForward = ControlEnum.APP_BASE + 32;

    /// <summary>
    /// The EXTERNAL chase view.  The original puts it on Shift-F1 ("Press Shift For External",
    /// a captured frame of the original); mode-13hx's <c>Control</c> table binds plain keys, so
    /// the PoC uses F2 and <c>v</c> as well.
    /// </summary>
    public const ControlEnum ViewExternal = ControlEnum.APP_BASE + 33;

    /// <summary>
    /// UNMAPPED.  The ESC menu carries Restart Mission, so no accelerator is needed, and <c>R</c>
    /// is <see cref="RadarToggle"/>, the original's own key.  The restart paths that remain are
    /// the ESC menu's <b>Restart Mission</b> (<c>Menu.FlightMenuActions.RestartMissionId</c>),
    /// <c>--respawn</c>, and the <c>restart</c> / <c>respawn</c> script words the headless runs
    /// use.  The control keeps its number so no saved binding silently means something else; nothing
    /// binds a key to it.
    /// </summary>
    public const ControlEnum Restart = ControlEnum.APP_BASE + 46;

    /// <summary>
    /// The RADAR switch, <b>R</b>: the manual's own key ("Radar on/off", p.51), the ladder arm at
    /// <c>image@0x0126C</c> that calls <c>radar_mode_toggle_with_sweep_reset @image@0x0E1C5</c>.
    /// </summary>
    /// <remarks>
    /// The original's handler flips TWO things: the monitor's display mode
    /// (<c>g_cockpit_region_1.flags [0x3F06]</c> bit 5, <c>image@0x0E1E3</c>) and bit 3 of the
    /// PLAYER's engagement flags byte — the radar-EMITTING bit that the AIM-7's own fire gate reads
    /// (<c>image@0x0E1DE</c>, and <c>CombatSpawnFireEligibility</c> at <c>image@0x02D42</c>).  W1
    /// drives the display half and publishes the other as
    /// <c>Render.Cockpit.RadarState.PlayerEmitting</c>; writing it is simulation state, and W6's.
    /// </remarks>
    public const ControlEnum RadarToggle = ControlEnum.APP_BASE + 72;

    /// <summary>
    /// <b>Shift-E, EJECT</b>: the keyboard-ladder arm at <c>image@0x012F8</c>, command 69 =
    /// <c>'E'</c> shifted (the manual p.53).  The shift half is <see cref="ViewShift"/>, which
    /// <c>ControlInputSource</c> already reads.
    /// </summary>
    /// <remarks>
    /// Numbered clear of <see cref="ViewFunctionKey1"/>, which occupies <c>APP_BASE + 48..57</c>
    /// (F1..F10).
    /// </remarks>
    public const ControlEnum Eject = ControlEnum.APP_BASE + 60;

    /// <summary>Toggles between the two views the PoC ships.</summary>
    public const ControlEnum ViewToggle = ControlEnum.APP_BASE + 34;

    /// <summary>
    /// <b>Backspace, the COCKPIT toggle</b>: <c>gameplay_key_backspace_cockpit_toggle
    /// @image@0x013D8</c>, reached from the per-frame key ladder with K = 8 (ASCII backspace).  It
    /// flips <c>g_cockpit_visible_flag [0xE471]</c>, which the original persists in
    /// <c>yeager.cfg</c> at byte <c>0x1F</c>.
    /// </summary>
    public const ControlEnum CockpitToggle = ControlEnum.APP_BASE + 61;

    /// <summary>
    /// <c>h</c>: show or hide the HUD text overlay, the original's Ctrl-F "Flight Info"
    /// (<c>g_flight_info_visible [0xB0]</c>).  The host has no Ctrl modifier of its own, so the
    /// toggle gets its own key; the meaning is the original's.
    /// </summary>
    public const ControlEnum FlightInfoToggle = ControlEnum.APP_BASE + 62;

    /// <summary>
    /// <b>W</b>, the NAV key: <c>nav_slot_cycle_next @image@0x08D65</c> (key-ladder arm
    /// <c>image@0x0127D</c>) and, with <see cref="ViewShift"/> held, <c>nav_slot_cycle_prev
    /// @image@0x08D8A</c> (<c>image@0x0128C</c>).
    /// </summary>
    /// <remarks>
    /// The manual's own binding, p.53: "W — Direction of the next waypoint / Shift+W — Direction of
    /// the previous waypoint".  The shift half is the same <see cref="ViewShift"/> control the
    /// eighteen view keys and Shift-E read, combined in <c>ControlInputSource</c>.
    /// </remarks>
    public const ControlEnum NavCycle = ControlEnum.APP_BASE + 63;

    /// <summary>
    /// PORT ADDITION — <b>m</b>: centre the MAP on the player.  The original's map is always
    /// centred on him (<c>scene_frame_setup_or_redraw</c> seeds its origin from the camera object at
    /// <c>image@0x1E45E</c>), so it has no such key; the port's map can also frame the mission, and
    /// then it needs a way back.  Meaningful only while the map is up.
    /// </summary>
    public const ControlEnum MapCentre = ControlEnum.APP_BASE + 64;

    /// <summary>
    /// PORT ADDITION — <b>n</b>: frame the mission's objects (the editor's <c>FitToContent</c>).
    /// Meaningful only while the map is up.
    /// </summary>
    public const ControlEnum MapFit = ControlEnum.APP_BASE + 65;

    /// <summary>
    /// The SHIFT modifier of the original's view keys.  The scancode dispatch at
    /// <c>image@0x33256</c> keys on <c>(scancode_high &lt;&lt; 8) | modifier_low</c>, and the
    /// modifier byte is 2 for shifted; mode-13hx binds
    /// single keys, so the shift is a control of its own and the pair is combined in
    /// <c>ControlInputSource</c>.
    /// </summary>
    public const ControlEnum ViewShift = ControlEnum.APP_BASE + 47;

    /// <summary>F1..F10, the ten function keys the view dispatch reads.</summary>
    public const ControlEnum ViewFunctionKey1 = ControlEnum.APP_BASE + 48;

    /// <summary>
    /// The TRIGGER — <c>g_weapon_fire_event_active [0x32ED]</c>, the first of
    /// <c>weapon_fire_event_scheduler</c>'s three arming tests (<c>image@0x03514</c>).  HELD, not
    /// tapped: the scheduler fires once per 64-tick window while it is set.
    /// </summary>
    public const ControlEnum Fire = ControlEnum.APP_BASE + 40;

    /// <summary>
    /// Next weapon slot — <c>weapon_slot_next @image@0x03419</c> (ladder arm <c>image@0x0120B</c>).
    /// Bound to <c>]</c>, the manual's own key ("SELECTING YOUR WEAPON", p.44).
    /// </summary>
    /// <remarks>
    /// H26 CORRECTION — H4 bound this to <c>w</c>, which is the original's NAV key
    /// (<see cref="NavCycle"/>).  The manual gives the weapon keys as <c>]</c> and <c>[</c>, so the
    /// collision is resolved in the original's favour on both sides; <c>q</c> is kept as an alias
    /// for the previous slot because H4 shipped it and it collides with nothing.
    /// </remarks>
    public const ControlEnum WeaponNext = ControlEnum.APP_BASE + 41;

    /// <summary>
    /// Previous slot — <c>weapon_slot_prev @image@0x03406</c> (ladder arm <c>image@0x011F4</c>).
    /// Bound to <c>[</c> (manual p.44) and to <c>q</c>.
    /// </summary>
    public const ControlEnum WeaponPrevious = ControlEnum.APP_BASE + 42;

    /// <summary>Chaff — the ladder's cooked key <c>0x39</c> (<c>chaff_fire</c>, <c>image@0x01248</c>).</summary>
    public const ControlEnum Chaff = ControlEnum.APP_BASE + 43;

    /// <summary>Flare — cooked key <c>0x30</c> (<c>flare_fire</c>, <c>image@0x0125A</c>).</summary>
    public const ControlEnum Flare = ControlEnum.APP_BASE + 44;

    /// <summary>
    /// The manual's <b>Enter</b>: "target next object to right of current target" (p.48).  It is
    /// cooked key <c>0x0D</c>, the ladder arm at <c>image@0x01212</c> whose body sets <c>[0xBB] = 0
    /// / [0xBA] = 1</c> — list mode WITHOUT the free-camera selector, i.e. the screen-order cycle
    /// (<see cref="Core.Sim.Session.MissionSession.TargetCycleKey"/>).
    /// </summary>
    /// <remarks>
    /// The binding below has always passed <c>0x0D</c>; the <c>0x27</c> arm is
    /// <see cref="TargetNearest"/>, a key the port did not bind until now.
    /// </remarks>
    public const ControlEnum TargetCycle = ControlEnum.APP_BASE + 45;

    /// <summary>
    /// The manual's second targeting key: "target object closest to aiming crosshair" (p.48), bound
    /// to <b>'</b>.
    /// </summary>
    /// <remarks>
    /// Cooked key <c>0x27</c> (the apostrophe's own ASCII), the ladder arm at <c>image@0x01229</c>:
    /// <c>mov al,1 / mov [0xbb],al / mov [0xba],al</c>.  <c>[0x00BB]</c> is
    /// <see cref="Core.Sim.Combat.Player.PlayerCombatOffsets.LockOnFreeCameraMode"/>, which routes
    /// the selection through <c>target_select_by_freecam_proximity</c> instead of the screen-order
    /// walk — proximity to the aim point, exactly as the manual describes it.  The integer kernel
    /// handles this key (<c>MissionSession.DispatchLadderKey</c>); nothing was
    /// pressing it.
    /// </remarks>
    /// <remarks>
    /// Giving this the number <see cref="FrontEndTab"/> already has makes <c>'</c> and <b>Tab</b>
    /// ONE control: Tab fires the proximity-target key and <c>'</c> opens the menu.  Every control
    /// therefore needs a number of its own.
    /// </remarks>
    public const ControlEnum TargetNearest = ControlEnum.APP_BASE + 73;

    /// <summary>
    /// The four in-flight OVERLAY WINDOW toggles, <b>Shift-1</b>..<b>Shift-4</b> (the manual's "Press Shift-3 to turn
    /// on the Target Window", p.14).
    /// </summary>
    /// <remarks>
    /// They are not controls of their own: <c>Shift-n</c> is the same physical key as the port's throttle preset
    /// <c>n</c>, so <see cref="Input.ControlInputSource"/> splits the edge by whether <see cref="ViewShift"/> is held
    /// — the pattern Shift-E, Shift-W and unmapped already use.  This constant exists so the split has one
    /// place to name.
    /// </remarks>
    public const ControlEnum OverlayToggleFirst = ThrottlePreset1;

    /// <summary>
    /// Opens and closes the in-flight menu bar: the original's <b>ESC</b> (<c>image@0x00E95</c>),
    /// plus <b>Tab</b>.
    /// </summary>
    /// <remarks>
    /// <b>ESC cannot reach the port in a window.</b> mode-13hx closes the window on it
    /// unconditionally, before the control table is updated
    /// (<c>external/mode-13hx/src/Presentation/EngineWindow.cs</c>:122: <c>if
    /// (kb.IsKeyPressed(Key.Escape)) { window.Close; return; }</c>).  That file is outside this
    /// vendored window layer, so the binding is made anyway — it is the right one and it costs
    /// nothing — and <b>Tab</b> is bound beside it as the key that works today.  One guard in that
    /// line (or a <c>CommonOptions</c> flag) would give the port its ESC.
    /// </remarks>
    public const ControlEnum MenuToggle = ControlEnum.APP_BASE + 66;

    /// <summary>The menu's Home key (<c>image@0x20E2C</c>): the first row.</summary>
    public const ControlEnum MenuHome = ControlEnum.APP_BASE + 67;

    /// <summary>The menu's End key (<c>image@0x20E5F</c>): the last row.</summary>
    public const ControlEnum MenuEnd = ControlEnum.APP_BASE + 68;

    /// <summary>The menu's PgUp key (<c>image@0x20EBC</c>): the previous section.</summary>
    public const ControlEnum MenuPageUp = ControlEnum.APP_BASE + 69;

    /// <summary>The menu's PgDn key (<c>image@0x20E8C</c>): the next section.</summary>
    public const ControlEnum MenuPageDown = ControlEnum.APP_BASE + 70;

    /// <summary>
    /// The first of the thirty-six TYPE-AHEAD controls, <c>A</c>..<c>Z</c> then <c>0</c>..<c>9</c>.
    /// </summary>
    /// <remarks>
    /// mode-13hx's <c>KbControls</c> is a <c>Dictionary&lt;Key, Control&gt;</c>: one control per
    /// physical key, so a letter that is already a flight key (b, f, g, c, x, q, r, e, h, w, m, n,
    /// v) cannot get a second binding.  <see cref="Input.ControlInputSource"/> therefore maps those
    /// letters to their EXISTING control and reads its edge, and only the free keys are bound here.
    /// </remarks>
    public const ControlEnum MenuTypeAheadFirst = ControlEnum.APP_BASE + 80;

    /// <summary>How many type-ahead controls follow <see cref="MenuTypeAheadFirst"/>.</summary>
    public const int MenuTypeAheadCount = 36;

    /// <summary>
    /// The FRONT END's Tab key: the next widget in the focus ring (Shift+Tab, the previous).
    /// </summary>
    /// <remarks>
    /// It needs a control of its own because the front end must tell Tab from ESC, and both were on
    /// <see cref="MenuToggle"/>.  Tab keeps opening the ESC menu in flight —
    /// <see cref="Input.ControlInputSource"/> emits <c>FlightMenuKey.Open</c> for this control too —
    /// so nothing about the in-flight menu changes (M1's own alias survives).
    /// </remarks>
    public const ControlEnum FrontEndTab = ControlEnum.APP_BASE + 71;

    /// <summary>
    /// <b>Ctrl</b> as a MODIFIER, which is what the original uses it for.
    /// </summary>
    /// <remarks>
    /// In the original <c>Ctrl</c> is never a trigger: it is the shift on <c>Ctrl-Z</c> /
    /// <c>Ctrl-A</c> (the nearest-bogey and nearest-friendly advisories, manual p.52, ladder arms
    /// <c>image@0x01294</c> / <c>image@0x012A7</c>) and on the help menu's <c>Ctrl-F</c> /
    /// <c>Ctrl-T</c>.  Binding it to the guns meant a pilot asking where the bogeys are would fire;
    /// the rule is the original's favour, so the trigger keeps <b>Space</b> — which it
    /// always also had — and <c>Ctrl</c> becomes the modifier.
    /// </remarks>
    public const ControlEnum ModifierCtrl = ControlEnum.APP_BASE + 74;

    /// <summary>
    /// <b><c>.</c></b>: zoom the MAP WINDOW <b>in</b> one step.
    /// </summary>
    /// <remarks>
    /// Cooked key <c>0x2E</c>, the in-flight ladder arm at <c>image@0x012B2</c>:
    /// <c>cmp byte [0xC31C],0 / je</c> (in flight only), then
    /// <c>cmp word [0x3234],8 / jle / dec word [0x3234]</c> — one step DOWN the shared pixel-obj
    /// scale shift, floored at 8.  The key number is the ladder's own running compare chain from
    /// <c>image@0x00E27</c>, decoded here: the arm sits at key 46 = <c>'.'</c>.
    /// </remarks>
    public const ControlEnum MapWindowZoomIn = ControlEnum.APP_BASE + 77;

    /// <summary>
    /// <b><c>,</c></b>: zoom the MAP WINDOW <b>out</b> one step (cooked key <c>0x2C</c>, the arm at
    /// <c>image@0x012C7</c>: <c>cmp word [0x3234],0x0B / jge / inc word [0x3234]</c>, capped at 11).
    /// </summary>
    /// <remarks>
    /// The same word is the RADAR's and the RWR's scale, so these two keys rescale all three
    /// pixel-obj displays at once — the original's own coupling, not a port simplification
    /// (<c>per_frame_object_pixel_setup @image@0x0D741</c> reads <c>[0x3234]</c> for every context).
    /// </remarks>
    public const ControlEnum MapWindowZoomOut = ControlEnum.APP_BASE + 78;

    /// <summary><b>Ctrl-Z</b>: where the nearest BOGEY is (cooked key <c>0x1A</c>).</summary>
    /// <remarks>
    /// The ladder arm at <c>image@0x01294</c> is <c>cmp byte [0xC31C],0 / je</c> — in flight only —
    /// then <c>push 0x40 / lcall nearest_engaged_obj_advisory_show @image@0x23E62</c>.  The
    /// <c>0x40</c> is the engagement block's <c>+0x05</c> bit 6, the flag
    /// <see cref="Core.Model.Combat.EngagementStateFlags.CountsAsEnemyKill"/> already names.
    /// </remarks>
    public const ControlEnum NearestBogey = ControlEnum.APP_BASE + 75;

    /// <summary><b>Ctrl-A</b>: where the nearest FRIENDLY is (cooked key <c>0x01</c>).</summary>
    /// <remarks>The same arm at <c>image@0x012A7</c>, with <c>0</c> instead of <c>0x40</c>.</remarks>
    public const ControlEnum NearestFriendly = ControlEnum.APP_BASE + 76;

    // The RENDER SCRUTINY keys.  PORT ADDITIONS with no original counterpart: they turn the
    // aeroplane on the spot to judge the seam work and the shipped models, and let you
    // photograph what is on screen, dump the scene behind it as a static test case, and see the polygons
    // for what they are.  They sit above the type-ahead block (MenuTypeAheadFirst +
    // MenuTypeAheadCount = 116) so no number collides.

    /// <summary>
    /// <b>F12</b> or <b>P</b>: save a SCREENSHOT of the presented frame and, beside it, the SCENE
    /// DUMP that re-renders it (<c>--shot-dir</c>; <c>cyac-fly --render-scene</c>).
    /// </summary>
    public const ControlEnum DebugScreenshot = ControlEnum.APP_BASE + 116;

    /// <summary><b>K</b>: back-face culling on / off (<c>--backface-cull</c>).</summary>
    public const ControlEnum DebugCull = ControlEnum.APP_BASE + 117;

    /// <summary><b>L</b>: the wireframe, off → over the polygons → edges only (<c>--wireframe</c>).</summary>
    public const ControlEnum DebugWireframe = ControlEnum.APP_BASE + 118;

    /// <summary>
    /// <b>I</b>: contrasting face colours on / off; <b>Shift+I</b> reshuffles the assignment
    /// (<c>--face-colors</c>, <c>--face-color-seed</c>).
    /// </summary>
    public const ControlEnum DebugFaceColors = ControlEnum.APP_BASE + 119;

    /// <summary>
    /// <b>S</b>: the interior mask made visible, off → binary SILHOUETTE → the interior/ring
    /// classification over the picture (<c>--mask-view</c>).
    /// </summary>
    public const ControlEnum DebugMaskView = ControlEnum.APP_BASE + 120;
}

/// <summary>
/// What <c>cyac-fly fly</c> takes, on top of mode-13hx's <see cref="CommonOptions"/>.
/// </summary>
/// <remarks>
/// The verb is the default one, so both spellings in the H1 brief work:
/// <c>cyac-fly fly --seed-trace …</c> and <c>cyac-fly --headless --seed-trace … --frames N --out DIR</c>.
/// </remarks>
[Verb("fly", isDefault: true, HelpText = "Fly the port's integer flight kernel.")]
public sealed class FlyOptions : CommonOptions
{
    /// <summary>Creates the options and installs the CYAC key bindings.</summary>
    public FlyOptions()
    {
        // mode-13hx's defaults (WASD + space) mean nothing here; the CYAC set replaces them.
        KbControls.Clear();

        Bind(Key.Left, FlyControls.StickLeft);
        Bind(Key.Keypad4, FlyControls.StickLeft);
        Bind(Key.Right, FlyControls.StickRight);
        Bind(Key.Keypad6, FlyControls.StickRight);
        Bind(Key.Up, FlyControls.StickForward);
        Bind(Key.Keypad8, FlyControls.StickForward);
        Bind(Key.Down, FlyControls.StickBack);
        Bind(Key.Keypad2, FlyControls.StickBack);
        Bind(Key.Keypad7, FlyControls.StickForwardLeft);
        Bind(Key.Keypad9, FlyControls.StickForwardRight);
        Bind(Key.Keypad1, FlyControls.StickBackLeft);
        Bind(Key.Keypad3, FlyControls.StickBackRight);
        Bind(Key.Keypad5, FlyControls.StickCentre);

        Bind(Key.Equal, FlyControls.ThrottleUp);
        Bind(Key.KeypadAdd, FlyControls.ThrottleUp);
        Bind(Key.Minus, FlyControls.ThrottleDown);
        Bind(Key.KeypadSubtract, FlyControls.ThrottleDown);
        Bind(Key.Number1, FlyControls.ThrottlePreset1 + 0);
        Bind(Key.Number2, FlyControls.ThrottlePreset1 + 1);
        Bind(Key.Number3, FlyControls.ThrottlePreset1 + 2);
        Bind(Key.Number4, FlyControls.ThrottlePreset1 + 3);
        Bind(Key.Number5, FlyControls.ThrottlePreset1 + 4);
        Bind(Key.Number6, FlyControls.Afterburner);
        Bind(Key.B, FlyControls.Brakes);
        Bind(Key.F, FlyControls.Flaps);
        Bind(Key.G, FlyControls.Gear);

        // Combat.
        Bind(Key.Space, FlyControls.Fire);

        // Ctrl is the original's MODIFIER, not its trigger: see FlyControls.ModifierCtrl.  Space is
        // unchanged.
        Bind(Key.ControlLeft, FlyControls.ModifierCtrl);
        Bind(Key.ControlRight, FlyControls.ModifierCtrl);

        // The two SITUATIONAL-AWARENESS keys (manual p.52).  Z and A carry their own controls because
        // one physical key carries one mode-13hx Control; ControlInputSource reads them only while
        // Ctrl is held, and still offers them to the menu's type-ahead.
        Bind(Key.Z, FlyControls.NearestBogey);
        Bind(Key.A, FlyControls.NearestFriendly);
        // The weapon keys are the manual's own `]` and `[` (p.44).  H4 had them on `w`/`q`, and `w`
        // is the original's NAV key (manual p.53), so `w` goes back to navigation and `q` stays as
        // a harmless alias for the previous slot.
        Bind(Key.RightBracket, FlyControls.WeaponNext);
        Bind(Key.LeftBracket, FlyControls.WeaponPrevious);
        Bind(Key.Q, FlyControls.WeaponPrevious);
        Bind(Key.C, FlyControls.Chaff);
        Bind(Key.X, FlyControls.Flare);
        Bind(Key.Enter, FlyControls.TargetCycle);

        // The manual's OTHER targeting key (p.48): `'` targets the object closest to the aiming
        // crosshair, the ladder arm at image@0x01229 that lights the proximity selector.
        Bind(Key.Apostrophe, FlyControls.TargetNearest);
        // R is the RADAR SWITCH (manual p.51), and nothing else: neither restart accelerator is
        // mapped; the ESC menu's Restart Mission is the one door out, and --respawn / the
        // script words still work.
        Bind(Key.R, FlyControls.RadarToggle);

        // Shift-E ejects (image@0x012F8).  The shift is FlyControls.ViewShift, exactly as it is for
        // the view keys, so the pair is combined in ControlInputSource.
        Bind(Key.E, FlyControls.Eject);

        // Backspace shows and hides the cockpit (image@0x013D8).
        Bind(Key.Backspace, FlyControls.CockpitToggle);
        Bind(Key.H, FlyControls.FlightInfoToggle);

        // W cycles the NAV waypoint forward, Shift-W backward (image@0x0127D / image@0x0128C;
        // manual p.53).  The shift is FlyControls.ViewShift, as it is for the view keys and
        // Shift-E.
        Bind(Key.W, FlyControls.NavCycle);

        // The MAP's two port-added keys.  Its ZOOM keys are the original's own `+` / `−`
        // (gauge_zoom_in/out_keyhandler @image@0x0D8B5 / @image@0x0D8C4), which are already bound
        // above as the port's throttle: the rasterizer gives them to the map only while the map is
        // the active view.
        Bind(Key.M, FlyControls.MapCentre);
        Bind(Key.N, FlyControls.MapFit);

        // The RENDER SCRUTINY keys (port additions; FlyControls.DebugScreenshot and friends).  F12
        // and P both photograph, because a Mac keyboard's F12 is a media key until the system
        // setting says otherwise; K, L and I were free letters.  Each letter is also named in
        // ControlInputSource.TypeAheadControl, so the menu's type-ahead still sees it.
        Bind(Key.F12, FlyControls.DebugScreenshot);
        Bind(Key.P, FlyControls.DebugScreenshot);
        Bind(Key.K, FlyControls.DebugCull);
        Bind(Key.L, FlyControls.DebugWireframe);
        Bind(Key.I, FlyControls.DebugFaceColors);
        Bind(Key.S, FlyControls.DebugMaskView);

        // The MAP WINDOW's own zoom, and the original's own keys: `.` in, `,` out (image@0x012B2
        // / image@0x012C7, the ladder arms at cooked keys 46 and 44).  Both were unbound in the
        // port; neither collides with anything the PoC already uses.
        Bind(Key.Period, FlyControls.MapWindowZoomIn);
        Bind(Key.Comma, FlyControls.MapWindowZoomOut);

        // The original's eighteen view keys: F1..F10 with and without Shift.  F1/F2 keep their
        // H1 meaning when the shift is not held, because F1 IS Forward and F2 IS the cockpit
        // Back view.
        for (int i = 0; i < 10; i++)
        {
            Bind(Key.F1 + i, FlyControls.ViewFunctionKey1 + i);
        }

        Bind(Key.ShiftLeft, FlyControls.ViewShift);
        Bind(Key.ShiftRight, FlyControls.ViewShift);
        Bind(Key.V, FlyControls.ViewToggle);

        // The in-flight ESC menu bar.  ESC is the original's key (image@0x00E95) and is bound
        // here even though mode-13hx eats it (see FlyControls.MenuToggle); Tab is the one that
        // works in a window today.  The arrows, Enter and ESC inside the modal are the flight
        // controls read as EDGES by ControlInputSource — one physical key can carry only one
        // Control, and the rasterizer knows which meaning the frame is in.
        Bind(Key.Escape, FlyControls.MenuToggle);

        // Tab has a control of its own now, because the FRONT END has to tell Tab from ESC and one
        // physical key carries one Control. ControlInputSource still turns this control's edge into
        // FlightMenuKey.Open, so Tab opens the ESC menu in flight exactly as an earlier pass left it.
        Bind(Key.Tab, FlyControls.FrontEndTab);
        Bind(Key.Home, FlyControls.MenuHome);
        Bind(Key.End, FlyControls.MenuEnd);
        Bind(Key.PageUp, FlyControls.MenuPageUp);
        Bind(Key.PageDown, FlyControls.MenuPageDown);

        // The type-ahead letters and digits that are NOT already flight keys.  The rest are read
        // through the control they already have (ControlInputSource.TypeAheadControl).
        for (int i = 0; i < FlyControls.MenuTypeAheadCount; i++)
        {
            Key key = i < 26 ? Key.A + i : Key.Number0 + (i - 26);
            if (!KbControls.ContainsKey(key))
            {
                Bind(key, FlyControls.MenuTypeAheadFirst + i);
            }
        }
    }

    /// <summary>The scenario-catalog slot to fly, or null for a Test Flight.</summary>
    [Option("mission", HelpText = "Fly a HISTORIC MISSION: a scenario-catalog slot 0..49, or a word matched against the mission titles. Enemies, guns and damage come with it.")]
    public string? Mission { get; set; }

    /// <summary>
    /// Headless only: fly the aeroplane at the nearest bandit and hold the trigger inside a
    /// firing cone (<see cref="Sim.PursuitAutopilot"/>).  A straight-line sortie merges once and
    /// never comes back, so this is what makes a headless run a FIGHT.
    /// </summary>
    [Option("pursue", Default = false, HelpText = "Headless test pilot: chase the nearest bandit and fire inside a cone.")]
    public bool Pursue { get; set; }

    /// <summary>Restart the sortie automatically when the flight ends.</summary>
    /// <remarks>
    /// Neither accelerator is mapped; what does it by hand is the ESC menu's <b>Restart Mission</b> row,
    /// and <c>R</c> is the radar switch. All three take the same reopen path — a cold start of the
    /// same mission and site.
    /// </remarks>
    [Option("respawn", Default = false, HelpText = "Restart the sortie when the flight ends (master[+0x122] = 0). The original goes to the debrief instead. By hand: the ESC menu's Restart Mission (a port QoL — the original cannot restart a mission at all; R is the radar switch).")]
    public bool Respawn { get; set; }

    /// <summary>The difficulty the admitter's three tables are indexed with.</summary>
    [Option("difficulty", Default = 1, HelpText = "0..3 — g_difficulty_level [0xF10E], the index into the admission probability/interval/capacity tables at DGROUP 0x2A10/0x2A14/0x2A18.")]
    public int Difficulty { get; set; }

    /// <summary>Run the startup checks without a window, print them and exit (0 = ready, 1 = a step failed).</summary>
    [Option("preflight", Default = false, HelpText = "Run the startup checks (home folder, locate and identify the original files, decode, transform, verify, install, load, enhance, the port's own port.json, the settings and the statistics files) without opening a window, print one line per step and exit: 0 when every step is ok, cached or a warning, 1 when one failed.")]
    public bool Preflight { get; set; }

    /// <summary>The home folder the startup checks use.</summary>
    [Option("home", HelpText = "The home folder that holds game/ (the drop zone; sources/ is accepted too), data/, port.json, settings.json, stats.json, screenshots/ and preflight.log. Default, first match wins: $CYAC_HOME; the first folder above the executable that holds a game/ or sources/ folder; the executable's own folder when it is writable (a portable install); else the per-user folder (%LOCALAPPDATA%\\CYAC on Windows, ~/Library/Application Support/CYAC on macOS, $XDG_DATA_HOME/cyac or ~/.local/share/cyac elsewhere), which is created if it is absent.")]
    public string? Home { get; set; }

    /// <summary>Where the original game files are, when they are not in the drop zone.</summary>
    [Option("game", HelpText = "Where your copy of the ORIGINAL game is: a directory (searched recursively, zips inside it included) or a .zip. Overrides the home folder's game/ drop zone, which is then not searched at all. The files are only read; nothing is written there.")]
    public string? Game { get; set; }

    /// <summary>Rebuild the data tree even when the installed one matches the originals.</summary>
    [Option("rebuild", Default = false, HelpText = "With --preflight: rebuild the data tree from the originals even when the installed tree already matches them.")]
    public bool Rebuild { get; set; }

    /// <summary>When the pre-flight dashboard is shown while the game starts.</summary>
    [Option("startup-check", Default = "always", HelpText = "always|problems - whether the PRE-FLIGHT DASHBOARD is drawn while the game starts. 'always' (the default) shows the ten startup steps and waits for Enter when they are done; 'problems' goes straight into the game unless a step warns or fails. The checks themselves always run; --preflight runs them on the console instead.")]
    public string? StartupCheck { get; set; }

    /// <summary>With <c>--preflight</c>: also render the dashboard to a PNG, with no window.</summary>
    [Option("shot", HelpText = "With --preflight: also draw the DASHBOARD into this PNG - no window, no graphics device - at --width x --height. The console report is printed as usual.")]
    public string? Shot { get; set; }

    /// <summary>Which moment <c>--shot</c> draws.</summary>
    [Option("shot-at", Default = "final", HelpText = "With --preflight --shot: which moment to draw. 'final' (the default) is the finished check; a step name (home, locate, identify, decode, transform, verify, install, load, enhance, userstate) draws the frame that step was RUNNING in, so a mid-run dashboard can be photographed without a window.")]
    public string? ShotAt { get; set; }

    /// <summary>The transformed data tree; otherwise <c>DataLocator</c>'s search.</summary>
    [Option("data", HelpText = "Path to the transformed data tree (default: $CYAC_DATA, beside the exe, or the dev walk-up).")]
    public string? DataPath { get; set; }

    /// <summary>
    /// The aircraft to fly a TEST FLIGHT in — the default start, needing no trace at all.
    /// </summary>
    /// <remarks>
    /// A <c>data/aircraft</c> basename (<c>p51</c>, <c>fw190</c>, <c>f86</c>, <c>mig15</c>,
    /// <c>f4</c>, <c>mig21</c>) or a Hangar slot 0..5.  The era, and therefore the theater, follows
    /// from it: <c>[0x2A0E] = idx &gt;&gt; 1</c> (<c>image@0x26CD5..0x26CDA</c>).
    /// </remarks>
    [Option("test-flight", HelpText = "Start a TEST FLIGHT in this aircraft (basename or Hangar slot 0..5) from the data tree alone. Default when --seed-trace is absent.")]
    public string? TestFlight { get; set; }

    /// <summary>
    /// Fly a CUSTOM MISSION straight from the command line, without the menu.
    /// </summary>
    /// <remarks>
    /// The grammar is <c>CustomMissionSpec</c>'s: five '/'-separated fields
    /// (aircraft/altitude/verb/clauses/skill), or the hex wire form <c>settings.json</c> stores.
    /// </remarks>
    [Option("custom", HelpText = "Fly a CUSTOM MISSION (the Create Mission sentence) without the menu: <aircraft>/<altitude>/<verb>/<clauses>/<skill>, e.g. p51/10000/jumped/2xme109+3xfw190/good. Words come from the pickers' own tables (strings.json); a count clause is <n>x<enemy>, 1..3 of them joined with '+'. The hex wire form settings.json stores is accepted too.")]
    public string? Custom { get; set; }

    /// <summary>Which of the theater's type-6 sites the Test Flight spawns on.</summary>
    [Option("site", Default = 0, HelpText = "Which type-6 theater site FREE.S's at_site marker draws (the original draws it at random with prng_rand_bounded @image@0x09FD0). 0..2 in every shipped theater.")]
    public int Site { get; set; }

    /// <summary>The flight trace whose first complete frame seeds the session.</summary>
    /// <remarks>
    /// The VERIFICATION tool, not the normal start: it is what <c>--replay</c> checks the kernel
    /// against, and what a developer uses to begin mid-sortie with <c>--seed-step</c>.  The normal
    /// start is <c>--test-flight</c>.
    /// </remarks>
    [Option("seed-trace", HelpText = "seed the session from a cyac-flight-trace record instead of the cold start (use with --replay, or with --seed-step to start mid-sortie).")]
    public string? SeedTrace { get; set; }

    /// <summary>Seed from the first trace record at or after this simulation step.</summary>
    [Option("seed-step", Default = 0u, HelpText = "Seed from the first trace record at or after this simulation step (0 = the trace's first record, which is often a parked aircraft).")]
    public uint SeedStep { get; set; }

    /// <summary>Render frames to PNG files instead of opening a window.</summary>
    [Option("headless", Default = false, HelpText = "Render frames to PNG files instead of opening a window.")]
    public bool Headless { get; set; }

    /// <summary>How many host frames the headless run renders.</summary>
    /// <remarks>
    /// Spelled <c>--frame-count</c>, not <c>--frames</c>: mode-13hx's <see cref="CommonOptions"/>
    /// already owns <c>-l, --frames</c> (the frame prerender limit), and CommandLineParser throws on
    /// a duplicate long name.
    /// </remarks>
    [Option("frame-count", Default = 0, HelpText = "Headless: how many host frames to render (0 = none). Named --frame-count because mode-13hx owns -l/--frames.")]
    public int Frames { get; set; }

    /// <summary>Save only every N-th rendered frame (all of them still render).</summary>
    [Option("save-every", Default = 1, HelpText = "Headless: write only every N-th rendered frame to PNG (every frame still renders and still steps the simulation).")]
    public int SaveEvery { get; set; }

    /// <summary>Where the headless run writes.</summary>
    [Option("out", HelpText = "Headless: output directory for the PNG frames and the replay log.")]
    public string? OutputDirectory { get; set; }

    /// <summary>Seed <c>--replay</c>'s first segment from the ported cold start.</summary>
    [Option("cold-start", Default = false, HelpText = "Headless --replay: seed the FIRST segment from the ported cold start instead of the trace's own record, so the replay verifies the cold start too.")]
    public bool ColdStartReplay { get; set; }

    /// <summary>Drive the kernel from the trace's recorded inputs and check it against the trace.</summary>
    [Option("replay", Default = false, HelpText = "Headless: drive the kernel from the trace's RECORDED inputs and compare every post-step master block against the trace's S7 record.")]
    public bool Replay { get; set; }

    /// <summary>The host frame time the headless run pretends elapsed.</summary>
    [Option("frame-seconds", Default = 1.0 / 60.0, HelpText = "Headless: the wall time each rendered host frame stands for.")]
    public double FrameSeconds { get; set; }

    /// <summary>A scripted key sequence for the headless free run.</summary>
    [Option("script", HelpText = "Headless free run: a key script, e.g. \"0-2:back,2-4:right\" (seconds-range : control names separated by '+'). 'advisor<n>' raises Yeager advisory action code n (0..20) — the capture instrument for the CHUCK YEAGER window, which has no key of its own because the original has none either.")]
    public string? Script { get; set; }

    /// <summary>Keep the stick where a key left it instead of centring on release.</summary>
    [Option("stick-latch", Default = false, HelpText = "Reproduce the ORIGINAL's keyboard stick, which latches: a numpad key sets the axis and nothing recentres it (image@0x01819 has no release path). Default is hold-to-deflect.")]
    public bool StickLatch { get; set; }

    /// <summary>
    /// The camera's horizontal field of view —
    /// <see cref="CameraLens.DefaultHorizontalFovDegrees"/>: the original's emitted projector has a
    /// focal length of exactly <c>2^([0xD8A2]+[0xD8A0])</c> = 128 pixels, so its full-screen
    /// 320-pixel window subtends <c>2·atan(160/128)</c> = 102.68°.
    /// </summary>
    [Option("fov", Default = CameraLens.DefaultHorizontalFovDegrees, HelpText = "Horizontal field of view in degrees (default = the original's own 102.68°, derived from its projector's 2^7-pixel focal length).")]
    public double Fov { get; set; }

    /// <summary>Run in a window; full screen is the player's default (see <see cref="ApplyPlayerDefaults"/>).</summary>
    [Option("windowed", Default = false, HelpText = "Run in a window of --width x --height instead of full screen. Full screen is the default; -f/--fullscreen is accepted and names that default.")]
    public bool Windowed { get; set; }

    /// <summary>Present without vertical sync; vsync is the player's default (see <see cref="ApplyPlayerDefaults"/>).</summary>
    [Option("no-vsync", Default = false, HelpText = "Present frames as fast as the GPU allows, tearing included. VSync is the default; -v/--vsync is accepted and names that default.")]
    public bool NoVSync { get; set; }

    /// <summary>Which view the frame is drawn from.</summary>
    [Option("view", Default = "cockpit", HelpText = "Which of the original's eighteen view keys to start in: cockpit|back|left|right|up|down (F1..F6), chase|extback|extright|extleft|below|above (Shift-F1..F6), planetotarget (F7), targettoplane (F8), flyby (F10), targetcockpit (Shift-F7), exttarget (Shift-F8), circling (Shift-F9), missile (Shift-F10). F9 (map) is out of scope.")]
    public string? View { get; set; }

    /// <summary>Do not draw the theater's scenery meshes.</summary>
    [Option("no-scenery", Default = false, HelpText = "Draw only the horizon and the objects — no theater scenery.")]
    public bool NoScenery { get; set; }

    /// <summary>How far behind the aircraft the external chase camera sits, in world units.</summary>
    [Option("chase-distance", Default = CameraRig.DefaultChaseDistanceWorldUnits, HelpText = "External view: how far behind the aircraft the camera sits, in world units.")]
    public double ChaseDistance { get; set; }

    /// <summary>How far above the aircraft the external chase camera sits, in degrees of elevation.</summary>
    [Option("chase-elevation", Default = CameraRig.DefaultChaseElevationDegrees, HelpText = "External view: the camera's elevation above the aircraft, in degrees.")]
    public double ChaseElevation { get; set; }

    /// <summary>A hard Manhattan draw-distance ceiling; 0 = none.</summary>
    /// <remarks>
    /// The port draws effectively unlimited visibility and the scene is 747 instances, so <b>0
    /// means NO ceiling at all</b> and the class thresholds are only honoured under
    /// <see cref="ClassicCull"/>.
    /// </remarks>
    [Option("draw-distance", Default = 0.0, HelpText = "Hard Manhattan draw-distance ceiling in world units (0 = unlimited, the default).")]
    public double DrawDistance { get; set; }

    /// <summary>Honour each mesh class's own cull distance, the way the original does.</summary>
    [Option("classic-cull", Default = false, HelpText = "Cull each class at its own lodThresholds[0]x256 (mesh_visibility_lod_select @image@0x16BE8) — the original's 1991 fill-rate budget.")]
    public bool ClassicCull { get; set; }

    /// <summary>How stippled records are drawn.</summary>
    [Option("alpha", Default = "on", HelpText = "Translucency: on (composite record[+4]'s measured coverage as a real alpha — propeller discs, shadows, clouds, canopy glass), off (draw them solid), or dither (RETRO: quantise that same alpha to 0/1 through an 8x8 ordered matrix in SCREEN space at the host's own pixel pitch, so a 50% record is a checkerboard of single host pixels and a soft puff a radial gradient of dots; edges stay analytic unless --edges hard). 'stipple' and 'classic' are accepted as aliases of 'dither'.")]
    public string? Alpha { get; set; }

    /// <summary>How a fragment's AREA term is resolved.</summary>
    /// <remarks>
    /// with <c>--alpha dither</c> this is the other half of the full retro look — hard polygon
    /// edges AND dot-pattern translucency.
    /// </remarks>
    [Option("edges", Default = "analytic", HelpText = "analytic|hard - how a 3-D fragment's AREA coverage is resolved. 'analytic' (the default) gives each pixel the exact fraction of it the primitive covers - anti-aliasing that is continuous in sub-pixel position. 'hard' is the classic centre sample (1 if the pixel's centre is inside the primitive, else 0); with --alpha dither it is the FULL retro look.")]
    public string? Edges { get; set; }

    /// <summary>Which level of detail every instance draws.</summary>
    [Option("lod", Default = "max", HelpText = "Level of detail: max (every instance draws its densest LOD — the default; no geometry switching anywhere) or classic (the original's distance cascade, mesh_visibility_lod_select @image@0x16BE8, with anti-shimmer hysteresis).")]
    public string? Lod { get; set; }

    /// <summary>How many whole cloud-lattice periods are instantiated around the camera.</summary>
    [Option("cloud-tiles", Default = CloudDeck.DefaultTiles, HelpText = "Cloud deck SIZE, not an on/off switch: how many EXTRA 32,768-unit lattice periods are tiled around the camera's own, per axis. The loop runs -n..n, so n tiles draw (2n+1)^2 lattice cells of 9 clouds each and n=0 still draws the original's own 9, which pop at the wrap boundary. To fly with NO clouds use --clouds off.")]
    public int CloudTiles { get; set; }

    /// <summary>Whether the ground reference grid is drawn.</summary>
    [Option("ground-balls", Default = "fixed", HelpText = "Ground reference grid: fixed (the DEFAULT — a world-aligned lattice at the original's band-0 pitch of 1,024 ft, balls of one fixed 8-ft radius, tiled around the camera with a distance fade, never snapped or re-scaled), classic (the original's single `spheres` object re-snapped every frame and re-scaled per altitude band — alloc_slot_b_camera_pos_snap_update @image@0x2DCF4), or off (= g_graphics_detail_level [0xF108] 0).")]
    public string? GroundBalls { get; set; }

    /// <summary>How many whole fixed-lattice periods of ground balls are drawn per axis.</summary>
    [Option("ground-ball-tiles", Default = GroundGrid.DefaultFixedTiles, HelpText = "--ground-balls fixed: how many whole 9,216-unit lattice periods to draw around the camera per axis (3 = 49 instances = 3,969 balls).")]
    public int GroundBallTiles { get; set; }

    /// <summary>
    /// INSPECTION: place one instance of a named mesh class straight ahead of the camera.
    /// </summary>
    /// <remarks>
    /// Presentation only.  It exists because the effect classes (<c>smoke</c>, <c>explosio</c>) are
    /// spawned by the combat kernel at moments a headless sortie cannot be made to reach on demand —
    /// H5a §6 B1 — so the only honest way to PHOTOGRAPH how they look is to put one in front of the
    /// lens and say so.
    /// </remarks>
    [Option("effect-probe", HelpText = "Inspection: draw one instance of this mesh class (e.g. smoke, explosio) 60 world units ahead of the camera, to photograph how an effect looks. Presentation only.")]
    public string? EffectProbe { get; set; }

    /// <summary>How far ahead of the camera the effect probe sits, in world units.</summary>
    [Option("effect-probe-range", Default = 60.0, HelpText = "Inspection: how far ahead of the camera --effect-probe places its instance, in world units.")]
    public double EffectProbeRange { get; set; }

    /// <summary>How many probe instances to place, for a burst.</summary>
    /// <remarks>
    /// The original spawns ONE <c>bullet</c> object per 64-tick window
    /// (<c>weapon_fire_event_scheduler @image@0x03514</c>), so a real sortie never shows twenty at
    /// once; this exists to MEASURE the halo's cost at a count the port might one day reach when the
    /// per-gun ballistics model would give each gun its own rounds.
    /// </remarks>
    [Option("effect-probe-count", Default = 1, HelpText = "place this many --effect-probe instances instead of one, on a grid across the view at --effect-probe-range, so the per-projectile cost of an effect (e.g. the tracer halo) can be measured at a chosen count.")]
    public int EffectProbeCount { get; set; }

    /// <summary>Draw the effect classes as flat coins and leave the explosion anchor blank.</summary>
    [Option("hard-effects", Default = false, HelpText = "Draw smoke/chaff discs hard-edged and leave the opcode-4 EXPLOSION anchor blank — what the PoC did before that. The default softens the effect discs and gives the anchor the body deferred_effect_render @image@0x03E18 draws (palette 7, 8..28 world units).")]
    public bool HardEffects { get; set; }

    /// <summary>Grow the smoke puffs as the original's prepare callback does.</summary>
    [Option("smoke-growth", Default = "on", HelpText = "on|off — run the smoke class's prepare callback (smoke_sprite_render_params_setup @image@0x0B2FF) on every puff: its three discs grow from 25 to 200/280/240 world units over the puff's life (45 s stationary, 25 s wreck column, 5 s damage trail) and take the kind's colours. 'off' is the earlier look: the static 16–20-unit records (which read as puffs of smoke too small).")]
    public string? SmokeGrowth { get; set; }

    /// <summary>A tuning multiplier on the grown smoke radii.</summary>
    /// <remarks>H24 raised the DEFAULT from 1.0 to 1.3; 1.0 is H23's look.</remarks>
    [Option("smoke-size", Default = 1.3, HelpText = "Multiplier on the grown smoke disc radii (1 = the original's own law; 1.3 = the default, a little bigger than the original's). Presentation only.")]
    public double SmokeSize { get; set; }

    /// <summary>The long-lived presentation smoke layer.</summary>
    [Option("smoke-trail", Default = "on", HelpText = "on|off — bear a PRESENTATION puff for every puff spawn and let it live --smoke-life times longer, so a wreck trails a real column (an authorised deviation). 'off' is the look: the sim's own 15 puffs, drawn as the original draws them.")]
    public string? SmokeTrail { get; set; }

    /// <summary>The life multiplier for the presentation layer's puffs.</summary>
    [Option("smoke-life", Default = 3.0, HelpText = "Multiplier on a presentation puff's life over the original's (5 s damage trail / 25 s wreck column / 45 s stationary). 3 = the default (15 / 75 / 135 s); 1 = the original's durations with the layer still on. Presentation only.")]
    public double SmokeLife { get; set; }

    /// <summary>Which span H23's size ramp runs over for a presentation puff.</summary>
    [Option("smoke-ramp", Default = "original", HelpText = "original|stretched — which span the growth ramp (25 -> 200 world units) runs over for a LAYER puff: 'original' (the default) reaches full size in the original's own 5/25/45 s and then HOLDS it while the puff keeps climbing, so a puff is never smaller than the original's at the same age; 'stretched' spreads the ramp over the whole --smoke-life, which grows more slowly and reads thinner mid-life. Both end at the same radii. Presentation only.")]
    public string? SmokeRamp { get; set; }

    /// <summary>The trailing fraction of a presentation puff's life spent fading out.</summary>
    [Option("smoke-fade", Default = 0.25, HelpText = "The fraction of a presentation puff's life over which it fades to nothing, so it does not pop out (the port's own; the original pops). 0 = pop. Presentation only.")]
    public double SmokeFade { get; set; }

    /// <summary>The layer-owned WRECK TRAIL behind a shot-down aircraft.</summary>
    [Option("wreck-smoke", Default = "on", HelpText = "on|off — a SHOT-DOWN aircraft (any bandit, and the player) trails the smoke layer's own dark puffs from the kill to the ground, then stops (an authorised deviation: the original's emitter row caps the damage trail at four beads 4 s apart). 'off' leaves exactly as it was. Presentation only.")]
    public string? WreckSmoke { get; set; }

    /// <summary>The wreck trail's cadence.</summary>
    [Option("wreck-smoke-interval", Default = CYAC.Port.Core.Sim.Session.SmokeTrail.DefaultWreckIntervalSeconds, HelpText = "Seconds between the wreck trail's puffs (0.12 = the tuned default; 0.25 was the first one tried; the original's own damage row emits every 4 s, which is 1,600 ft apart at fighter speed). Presentation only.")]
    public double WreckSmokeInterval { get; set; }

    /// <summary>The wreck trail's palette index.</summary>
    [Option("wreck-smoke-color", Default = 8, HelpText = "Palette index (0..127) all three of a wreck puff's discs are painted in: 8 (the default) is the original's own dark grey for a damage trail, 0 is black (which reads as a hole against the ground at 1080p), 4 is dark red. Presentation only.")]
    public int WreckSmokeColor { get; set; }

    /// <summary>A size multiplier on the wreck trail's puffs alone.</summary>
    /// <remarks>
    /// Their <c>settings.json</c> holds <b>0.68</b>, a finer column at the 0.12 s cadence.  There
    /// is no Core constant for this one: <c>SmokeTrail.WreckSizeScale</c>'s own initialiser stays
    /// 1.0 (the LIBRARY default, for a caller that specifies nothing), and the host always
    /// specifies — so this attribute IS the shipped default.
    /// </remarks>
    [Option("wreck-smoke-size", Default = 0.68, HelpText = "Multiplier on a WRECK-TRAIL puff's grown radii only (0.68 = the tuned default; 1.0 is the same ramp the layer's other puffs run; --smoke-size still applies to everything). Presentation only.")]
    public double WreckSmokeSize { get; set; }

    /// <summary>The mission DEBRIEF overlay.</summary>
    [Option("debrief", Default = "on", HelpText = "on|off — draw the post-mission DEBRIEF when the sortie ends: the `.S` module's own get_debrief_text verdict (or, for a death, the strings.bin 'Augured in' family), the post-mission mode 4/5/6 and the gunnery census. 'off' keeps the fate banner alone.")]
    public string? Debrief { get; set; }

    /// <summary>H25 CHEAT — report N slot-destroyed events to the mission module.</summary>
    [Option("mission-kills", Default = 0, HelpText = "CHEAT, for photographs: tell the mission module that actor slots 1..N were destroyed, through the VERIFIED dispatch (image@0x08C72) with those slots' own near pointers. It destroys nothing; the module's own guard and counters do the rest. Exists because the headless test pilot cannot win a dogfight.")]
    public int MissionKills { get; set; }

    /// <summary>Force the sortie to end as a SURVIVOR at a stated simulated second.</summary>
    [Option("debrief-at", Default = -1.0, HelpText = "CHEAT, for photographs: end the sortie as a SURVIVOR at this simulated second and show the debrief, without flying home and landing. Negative is off. The verdict is still the module's own get_debrief_text over the counters the sortie really reached.")]
    public double DebriefAt { get; set; }

    /// <summary>The NAV readout, a labelled DEVIATION the original does not have.</summary>
    /// <remarks>
    /// The 1991 cockpit answers "where is the waypoint" with the compass deviation lights and the
    /// bearing needle alone — the manual (p.53) tells the pilot to press <b>F9</b> for the map to
    /// find the RANGE.  The port has no map yet, so this is a text stand-in; it is off the cockpit
    /// bitmap, in the world's own rows.  The NAV waypoint itself is cycled with <b>W</b> and
    /// <b>Shift+W</b>, exactly as the original binds it.
    /// </remarks>
    [Option("nav-readout", Default = "compact", HelpText = "off|compact|full — the NAV waypoint readout (name, relative bearing, range). The original has NO such readout (its answer is the compass deviation lights, the bearing needle and the F9 map), so this is a labelled deviation. 'full' adds the absolute bearing, the range in feet, the slot count and whether the waypoint is TRACKING live actors. Cycle the waypoint with W / Shift+W.")]
    public string? NavReadout { get; set; }

    /// <summary>Where the F9 MAP is drawn.</summary>
    /// <remarks>
    /// The original replaces the 3-D view with the map screen and keeps flying
    /// (<c>mission_per_frame_render_phase</c>'s <c>[0xC320] == 0x0C</c> branch, <c>image@0x01570</c>);
    /// <c>inset</c> is a port addition that keeps the world and puts the map in the corner.
    /// </remarks>
    [Option("map", Default = "full", HelpText = "full|inset|off — how F9 draws the map. 'full' is the original's own behaviour (the 2-D map screen REPLACES the 3-D view; the simulation keeps running). 'inset' is a labelled DEVIATION: the world stays and the map is a mini-map in the bottom-right corner. 'off' disables the map view entirely.")]
    public string? Map { get; set; }

    /// <summary>The map's zoom: the original's levels 7..12, or the port's fit.</summary>
    [Option("map-zoom", Default = "fit", HelpText = "fit|7..12 — the map's zoom. 7..12 are the original's own g_map_zoom_level [0xD8A0] steps (the HUD prints ZOOM:1..ZOOM:32); note the shipped clamp at image@0x1E48A makes 7/8 and 11/12 the same map scale. 'fit' is a PORT ADDITION that frames the mission's objects. + and - step it while the map is up; m centres on the player, n re-fits.")]
    public string? MapZoom { get; set; }

    /// <summary>How much of the theatre the map draws.</summary>
    [Option("map-scenery", Default = "all", HelpText = "all|courses — 'courses' draws only what the ORIGINAL's map draws (rivers and roads: screen_buffer_iter_dispatch @image@0x1E766 enqueues nothing else). 'all' adds every other scenery class's footprint, a labelled port addition.")]
    public string? MapScenery { get; set; }

    /// <summary>The unit the map's scale bar and rings are quoted in.</summary>
    [Option("map-units", Default = "nm", HelpText = "nm|ft — the unit of the map's scale bar and range rings (both port additions; the original's map carries no scale at all).")]
    public string? MapUnits { get; set; }

    /// <summary>The range rings around the player.</summary>
    [Option("map-rings", Default = "on", HelpText = "on|off — the range rings around the player. PORT ADDITION.")]
    public string? MapRings { get; set; }

    /// <summary>Whether the player's marker blinks the way the original's does.</summary>
    [Option("map-blink", Default = "on", HelpText = "on|off — the player's cross blinks on 3 frames of 4, which is the original's own g_map_symbol_blink_phase [0xBA14] gate (image@0x1E722 / image@0x1E879). 'off' draws it every frame.")]
    public string? MapBlink { get; set; }

    /// <summary>The map's text: waypoint names, the caption, the ring labels.</summary>
    [Option("map-labels", Default = "on", HelpText = "on|off — the map's text. The waypoint NAMES are the original's (image@0x1EA74); the caption and the ring/scale labels are port additions.")]
    public string? MapLabels { get; set; }

    /// <summary>Whether the HUD text widgets stay on over the map.</summary>
    /// <remarks>
    /// They do in the original: <c>a captured frame of the original shows heading, altitude, speed, G, THR, VSI and
    /// <c>ZOOM:1</c> in yellow over the map, which is the non-forward-view mask <c>0x2F7</c>
    /// (<c>HudMask</c>, <c>image@0x0C620</c>).
    /// </remarks>
    [Option("map-hud", Default = "on", HelpText = "on|off — keep the HUD text widgets over the map. ON is the ORIGINAL's behaviour. OFF gives a clean map for a photograph.")]
    public string? MapHud { get; set; }

    /// <summary>A tuning multiplier on a grown smoke disc's coverage.</summary>
    [Option("smoke-density", Default = 1.0, HelpText = "Multiplier on a grown smoke disc's coverage (1 = the record's own 25 % under the soft falloff; the original's three masks are disjoint, so its overlapped puff core is 75 % solid — try 2..3 to match). Presentation only.")]
    public double SmokeDensity { get; set; }

    /// <summary>The player aeroplane's INTERIOR MASK (<c>Raster/InteriorMask.cs</c>).</summary>
    [Option("seam-mask", Default = "on", HelpText = "on|off — the INTERIOR MASK for the player's own aeroplane in the external views: its silhouette is rasterised once per frame by the centre rule and eroded by a pixel, and inside it a shared-edge pixel the two faces left short of full coverage is made opaque (the coverage-weighted mean of the faces), which removes the dotted 'rivet lines' along shared edges seen up close. The outline ring keeps the exact-area anti-aliasing. OFF is the earlier look.")]
    public string? SeamMask { get; set; }

    // ------------------------------------------------------------------------------------------
    // The RENDER SCRUTINY switches.  Command-line
    //   twins of the K / L / I keys and F12, so a headless capture and the --render-scene re-render
    //   can ask for the same picture as the interactive session.  Deliberately NOT Port Settings rows: a saved
    //   "wireframe only" would be a surprise at the next launch, and these are instruments.
    // ------------------------------------------------------------------------------------------

    /// <summary>single-sided records rejected by the winding test, or drawn from both sides.</summary>
    [Option("backface-cull", Default = "on", HelpText = "on|off — back-face culling of single-sided records (the K key in flight). OFF draws every record from both sides and leaves the depth test to sort them: a hole that closes is a face turned the wrong way, a hole that stays is a missing face.")]
    public string? BackfaceCull { get; set; }

    /// <summary>The wireframe mode.</summary>
    [Option("wireframe", Default = "off", HelpText = "off|overlay|only — polygon EDGES (the L key cycles them in flight): 'overlay' draws them over the filled polygons with hidden edges removed by the depth test, 'only' draws nothing but the edges. One host pixel wide at 1920, scaled with the window (--wire-width).")]
    public string? Wireframe { get; set; }

    /// <summary>The edge line's colour.</summary>
    [Option("wire-color", Default = -1, HelpText = "Palette index of the wireframe edges, or -1 (the default) for automatic: white over the paint, black over the contrast colours.")]
    public int WireColor { get; set; }

    /// <summary>The edge line's width.</summary>
    [Option("wire-width", Default = 1.0, HelpText = "Wireframe edge width in host pixels at a 1920-wide target, scaled with the target; floored at one pixel.")]
    public double WireWidth { get; set; }

    /// <summary>The vertex weld's distance.</summary>
    [Option("weld", Default = CYAC.Port.Core.Model.World.VertexWeld.DefaultEpsilonModelUnits, HelpText = "Asset conditioning: weld an UN-WELDED CORNER — two vertices of one LOD within this many model units that no record bridges and that both carry an open edge — into one (VertexWeld). At the default 1 it welds three in the shipped fleet: the P-51's canopy/spine corner, whose seam showed as a hole behind the canopy and a crack down the flank, and the Yak-9's mirrored pair of one-unit steps under the nose. 0 = off (the 1991 geometry as authored).")]
    public double Weld { get; set; }

    /// <summary>Where a face's colour comes from.</summary>
    [Option("face-colors", Default = "paint", HelpText = "paint|contrast — every polygon in its own palette colour (with the markings over it), or each polygon in a CONTRASTING flat colour (magenta, cyan, yellow, white, black, red, green, blue, orange, purple, ...) so it can be told from its neighbours; the markings shader is bypassed. The I key toggles it in flight; Shift+I reshuffles (--face-color-seed).")]
    public string? FaceColors { get; set; }

    /// <summary>The contrast assignment's reshuffle counter.</summary>
    [Option("face-color-seed", Default = 0, HelpText = "Which contrast assignment --face-colors contrast uses; every value is a different shuffle (Shift+I steps it).")]
    public int FaceColorSeed { get; set; }

    /// <summary>The interior mask made visible.</summary>
    [Option("mask-view", Default = "off", HelpText = "off|silhouette|interior — the player aeroplane's INTERIOR MASK made visible (the S key cycles it in flight). 'silhouette' replaces the world with the mask's raw centre-rule silhouette: white inside, black outside, RED where the closing filled a hole or sub-pixel gap the model leaves open — a binary picture, no smooth edges. 'interior' stamps the classification over the picture: GREEN = interior (the pixels the seam rule may heal), RED = the outline ring it leaves alone.")]
    public string? MaskView { get; set; }

    /// <summary>Where F12 puts its pictures.</summary>
    [Option("shot-dir", Default = "screenshots", HelpText = "Directory F12 / P writes to: shot_<UTC stamp>_<n>.png (the presented frame, cockpit and HUD included) and shot_<same>.scene.json beside it (the 3-D scene behind the frame — camera, lens, every instance, the palette and the render options — which `--render-scene <file>` re-renders headless). A relative path is taken from the data tree's parent — the HOME folder, where port.json, settings.json and stats.json live — so the default is <home>/screenshots, which the startup checks create.")]
    public string? ShotDirectory { get; set; }

    /// <summary>re-render a scene dump instead of flying.</summary>
    [Option("render-scene", HelpText = "Re-render a scene dump written by F12 (shot_*.scene.json) to a PNG and exit: the static test case. Output = --out (a .png path, or a directory that gets <dump name>.png; default beside the dump). --width/--height re-size the target; --wireframe, --backface-cull, --face-colors, --face-color-seed, --seam-mask, --edges, --alpha, --tile and --threads override the dump's own values when given on the command line.")]
    public string? RenderScene { get; set; }

    [Option("bitmap-explosions", Default = "on", HelpText = "on|off — the Graphics menu's `Bitmap Explosions` option (g_bitmap_explosions_flag [0xC31E]). ON draws the exp.rle sprite for a record whose +0x0C fork byte is non-zero (image@0x03EDF); OFF draws the six-disc particle burst the original falls back to. The shipped yeager.cfg has it ON.")]
    public string? BitmapExplosions { get; set; }

    /// <summary>Suppress an explosion's debris shards (the original's detail gate).</summary>
    [Option("no-effect-debris", Default = false, HelpText = "Do not draw an explosion's debris shards — the original's `cmp byte [0xF108],1 / jb ret` low-detail gate (image@0x03CDF). The default draws the 8 palette-15 shards of the disc arm and the 16 palette-12 shards of the burst arm.")]
    public bool NoEffectDebris { get; set; }

    /// <summary>CHEAT — start every enemy engagement with this many hit points.</summary>
    [Option("foe-hp", Default = 0, HelpText = "CHEAT (labelled): start every live engagement with this many hit points instead of its prototype's (an Me-109 ships with 80). An INITIAL-STATE edit only — every step of the kill after it is the integer kernel's. Use it to photograph the kill sequence headlessly; 0 (the default) is off.")]
    public int FoeHitPoints { get; set; }

    /// <summary>After a KILL, save every frame for this many frames.</summary>
    [Option("kill-burst", Default = 0, HelpText = "when the kill counter goes up, save EVERY frame for this many frames regardless of --save-every, so the whole kill sequence (flash, disc, debris, ejection, fall, impact, crater) can be photographed headlessly. 0 (the default) is off.")]
    public int KillBurst { get; set; }

    /// <summary>Headless: press the lock-on LIST key every N frames.</summary>
    /// <summary>The in-world designator labels' on/off switch.</summary>
    [Option("designator-labels", Default = "on", HelpText = "on|off — the labels under enemy aircraft (the target's type and the chance-to-hit). The original always draws them; this is a PORT control, because at a modern resolution a furball carries a lot more of them than a 320x200 screen ever showed. The yellow box on the selected target is not affected.")]
    public string? DesignatorLabels { get; set; }

    /// <summary>Host pixels per font pixel for those labels.</summary>
    [Option("designator-label-scale", Default = 1, HelpText = "Host pixels per FONT pixel for the in-world designator labels. 1 (the default) draws them 1:1 in real pixels, the same physical size at 1080p and at 4K; 2 or 3 suit a large screen viewed from a distance. The label's ANCHOR still comes from the design-space projection, so it keeps riding its target - only the glyphs stop being enlarged with the panel art.")]
    public int DesignatorLabelScale { get; set; }

    /// <summary>Those labels' opacity.</summary>
    [Option("designator-opacity", Default = 0.33, HelpText = "0..1 - how opaque the in-world designator labels are. 1 is solid, which is what the original draws; 0.33 (the default, chosen after flying it) lets the scene through so a crowded sky stays readable.")]
    public double DesignatorOpacity { get; set; }

    /// <summary>Host pixels per design pixel for the overlay windows.</summary>
    [Option("window-scale", Default = 0, HelpText = "Host pixels per DESIGN pixel for the four in-flight overlay windows (MAP / ENVELOPE / TARGET / Yeager). 0 is AUTO - a quarter of the design scale, floored at 1, which is the ESC menu's own rule: 1 at 1080p, 2 at 4K. 1 is a strict 1:1, so the window's 4-pixel border stays four real pixels instead of being enlarged with the panel art. The window's PLACE still comes from the panel's mapping, so it stays in its corner.")]
    public int WindowScale { get; set; }

    /// <summary>Which in-flight overlay windows start up.</summary>
    [Option("windows", Default = "cfg", HelpText = "cfg|off|all or a list of envelope,target,map,yeager — which of the four IN-FLIGHT OVERLAY WINDOWS start visible (g_inflight_overlay_visibility [0xF1CB], persisted at yeager.cfg@0x1D). 'cfg' (the default) is the port's own default — Target|Map, which is what the original's yeager.cfg ships too (The running game no longer reads the tree's config.json; port.json holds the port's choice). Shift-1 envelope / Shift-2 target / Shift-3 map / Shift-4 Yeager toggle them in flight (manual p.14).")]
    public string? Windows { get; set; }

    [Option("target-cycle", Default = 0, HelpText = "queue the lock-on's LIST key (the ladder's cooked 0x27, MissionSession.TargetCycleKey — 'Enter' at the window) every N frames, so a headless sortie acquires a target and the five TARGET views (F7, F8, Shift-F7, Shift-F8) have an anchor. 0 (the default) is off.")]
    public int TargetCycle { get; set; }

    /// <summary>H8 addendum — the DEGENERATE-PROJECTION watch.</summary>
    [Option("projection-census", Default = false, HelpText = "watch every projected polygon and report, per class, the largest screen span it produced, with the record index, the instance's distance from the eye and the record's nearest view-space Z. A polygon over 4x the screen diagonal is a mesh sitting ON the camera and is counted as TRIPPED.")]
    public bool ProjectionCensus { get; set; }

    /// <summary>H7 D3 / H8 §11.8 — do not draw the PLAYER's own aeroplane.</summary>
    [Option("no-player-mesh", Default = false, HelpText = "Do not draw the player's own aircraft in the external views — the companion to --camera that asked for, and the discriminator that says whether a suspect polygon belongs to the player's mesh or to something else.")]
    public bool NoPlayerMesh { get; set; }

    /// <summary>Ignore projection-census frames before this one.</summary>
    [Option("projection-from", Default = 0, HelpText = "--projection-census ignores frames before this one, so a census can be narrowed onto one event (the kill at frame ~13,000) instead of a whole sortie.")]
    public int ProjectionFrom { get; set; }

    /// <summary>The INSTRUMENT census.</summary>
    [Option("instrument-census", Default = false, HelpText = "census what value the port feeds each of the ten cockpit dial slots and each of the ten instrument regions over the run, and whether it ever CHANGES (a needle can be drawn perfectly and still be fed a constant). Prints one line per instrument at the end of a headless run.")]
    public bool InstrumentCensus { get; set; }

    /// <summary>The POSE census.</summary>
    [Option("pose-census", HelpText = "headless — after every rendered frame, append one CSV row per live NON-PLAYER object (frame, sim seconds, object ref, class ref, x, y, z, heading, distance to the player) to this file, so stepped / stationary-then-jump motion can be measured against distance instead of eyeballed.")]
    public string? PoseCensus { get; set; }

    /// <summary>The ENGAGEMENT-AI arm census (the "bandits fly away" investigation).</summary>
    /// <remarks>
    /// Pure observation: it prints the per-arm counters the integer kernel already keeps
    /// (<c>EngagementNodeCensus</c>, <c>image@0x0416C</c>'s arms, and the player-side scorer's
    /// <c>PlayerCombatCensus</c>), so a sortie in which no bandit ever acquires the player can NAME
    /// the gate that refused instead of being eyeballed.  Nothing here is read by the simulation.
    /// </remarks>
    [Option("ai-census", Default = false, HelpText = "at the end of a headless run, print every NON-ZERO per-arm counter of the engagement-AI node pass (weapon_fire_combat_loop_per_shot @image@0x0416C) and of the player-side target scorer (combat_target_score_and_fire @image@0x07952), so a sortie in which the AI never engages can name the gate that refused. Observation only.")]
    public bool AiCensus { get; set; }

    /// <summary>The KILL-CROSSTALK census.</summary>
    [Option("kill-census", Default = false, HelpText = "census every destruction-slot arm (which object the allocator was given as the debris PARENT, where the ejecting pilot/canopy landed, and how far that is from the player) and every AI-owned live shot slot (what its s_combat_spawn_record[+0x08] target is, and whether that target has already departed). Prints a CROSSTALK line and up to sixteen sample lines at the end of a headless run.")]
    public bool KillCensus { get; set; }

    /// <summary>The AGE, in frame-time units, the effect probe's instance is drawn at.</summary>
    [Option("effect-age", Default = -1, HelpText = "Inspection: draw --effect-probe's instance as a deferred-effect record of this AGE (0..256 = the record's own 0x100-unit life), so both arms of deferred_effect_render can be photographed at a chosen moment. -1 (the default) draws the ageless fallback.")]
    public int EffectAge { get; set; }

    /// <summary>The FORK byte the effect probe's instance carries.</summary>
    [Option("effect-fork", Default = 0, HelpText = "Inspection: the record[+0x0C] FORK byte --effect-probe's instance carries — 0 = the growing palette-7 disc and its 8 shards (effect_particle_draw), non-zero = the bitmap explosion / six-disc burst (image@0x03E30).")]
    public int EffectFork { get; set; }

    /// <summary>The PLAYER-FATE machine (an authorised deviation).</summary>
    [Option("fate", Default = "on", HelpText = "on|off — the port's OWN death/destruction logic (an authorised deviation): when the sortie ends the flight kernel stops, the wreck falls or rests instead of sliding, the kill sequence plays on the player's own object, the camera leaves the cockpit and a banner names the cause. `off` restores the earlier behaviour (the kernel keeps stepping and the wreck slides).")]
    public string? Fate { get; set; }

    /// <summary>How long the wreck is held before the sortie is over.</summary>
    [Option("fate-hold", Default = CYAC.Port.Core.Sim.Session.PlayerFate.DefaultHoldSeconds, HelpText = "How many seconds the wreck is held after it comes to rest before the sortie is over (and --respawn restarts). The original goes to the debrief four frames after the verdict instead (image@0x1004A arms [0xC390] := frame + 4).")]
    public double FateHold { get; set; }

    /// <summary>Scripted Shift-E for a headless run.</summary>
    [Option("eject-at", Default = -1.0, HelpText = "Headless: pull the Shift-E EJECT handle at this simulated second (image@0x012F8, the manual's 500-mph rule and all). Negative is off; at the window the key is Shift-E.")]
    public double EjectAt { get; set; }

    /// <summary>H9 CHEAT — arm the player's own death deadline.</summary>
    [Option("player-death-at", Default = -1.0, HelpText = "CHEAT (labelled): arm the player's own DEATH DEADLINE ([0xBD06]) at this simulated second, so a headless sortie can actually be shot down — the ported sustain tick then runs the whole real death path (image@0x0FE3C). An initial-state edit and nothing else; negative (the default) is off.")]
    public double PlayerDeathAt { get; set; }

    /// <summary>Draw the text readout over the frame in the window; see <see cref="DrawReadout"/>.</summary>
    [Option("readout", Default = false, HelpText = "Draw the text readout over the frame in the WINDOW. The window never draws it unless asked - it is an instrument, and a player would see a wall of text over the game. A headless run draws it into every saved frame unless --no-readout says otherwise.")]
    public bool Readout { get; set; }

    /// <summary>Do not draw the text readout over the frame.</summary>
    [Option("no-readout", Default = false, HelpText = "Do not draw the text readout over the frame — for photographing the scene itself. The headless summary still prints every line.")]
    public bool NoReadout { get; set; }

    /// <summary>Let the rasterizer wait for a free frame slot; fast mode is the player's default (see <see cref="ApplyPlayerDefaults"/>).</summary>
    [Option("no-fast", Default = false, HelpText = "Switch mode-13hx's fast mode OFF: the rasterizer waits for a free frame slot instead of dropping its oldest unread frame. Fast mode is the default; --fast is accepted and names that default.")]
    public bool NoFast { get; set; }

    /// <summary>Do not draw the <c>sun</c> object.</summary>
    [Option("no-sun", Default = false, HelpText = "Do not draw the `sun` object — the single white disc the engine keeps 100 world units above the camera (scene_or_mission_state_reset @image@0x0C461 + alloc_slot_a_camera_pos_update @image@0x2DC9D).")]
    public bool NoSun { get; set; }

    /// <summary>How the sky/ground transition is painted.</summary>
    [Option("horizon", Default = "refined", HelpText = "Horizon band: refined (the original's 31-entry palette ramp 224..254, interpolated), classic (the same ramp, stepped) or flat (a hard split).")]
    public string? Horizon { get; set; }

    /// <summary>The horizon band's thickness in degrees of elevation.</summary>
    [Option("horizon-band", Default = HorizonRenderer.DefaultBandDegrees, HelpText = "Horizon haze band thickness perpendicular to the horizon, in DEGREES of elevation at low altitude (default 8.93 = the 20 scanlines of a captured frame of the original through the original's own 2^7-pixel focal length). Its GROUND half grows to 30/39 of the total by 8,192 ft, per image@0x18C3E. 0 turns the band off.")]
    public double HorizonBand { get; set; }

    /// <summary>Whether the mission's cloud deck is drawn.</summary>
    [Option("clouds", Default = "on", HelpText = "Cloud deck: on (follow the mission's mission_altitude, drawing one for the Test Flight), off (the Graphics->Clouds toggle, g_clouds_flag [0xB6] = 0), or an ALTITUDE in feet.")]
    public string? Clouds { get; set; }

    /// <summary>Force the rendered gear angle instead of following the simulation.</summary>
    /// <remarks>
    /// An INSPECTION knob, not simulation state: it overrides only what the renderer is told, so a
    /// headless run can hold the gear at any point of its travel.  0 = extended, 0x2D0 (720) =
    /// retracted; −1 = follow <c>g_gear_deploy_angle_bam</c>.
    /// </remarks>
    [Option("gear-angle", Default = -1, HelpText = "Inspection: render the landing gear at this angle (0 = down, 720 = up); -1 follows the simulation.")]
    public int GearAngle { get; set; }

    /// <summary>How dial needles are drawn.</summary>
    /// <summary>The canopy bullet-hole decals' radial opacity.</summary>
    [Option("hit-marker-alpha", Default = "0.9,0.1", HelpText = "Canopy bullet-hole (spider-web) decals: opacity at the CENTRE and at the far EDGE, 'centre,edge' in 0..1, falling linearly with distance. 1,1 = the original's opaque blit.")]
    public string? HitMarkerAlpha { get; set; }

    /// <summary>Where in the pivot pixel the needle turns.</summary>
    [Option("needle-nudge", Default = "0.5,0.5", HelpText = "Where inside the dialinit pivot PIXEL the dial needles turn, 'dx,dy' in 320x200 design pixels. 0.5,0.5 (default) = the pixel's centre, where the original's line filler roots the needle (measured against yeager_006/007); 0,0 = the look (the pixel's top-left corner); 1,1 = the centre of the bitmap's 2x2 hub blob.")]
    public string? NeedleNudge { get; set; }

    [Option("needles", Default = "tapered", HelpText = "tapered|line — dial needles as tapered needles over a hub (default) or as a constant-width anti-aliased line. The original draws one-pixel lines (dial_slot_needle_line_draw @image@0x0195C).")]
    public string? Needles { get; set; }

    /// <summary>How the artificial horizon's round window is cut.</summary>
    [Option("instrument-window", Default = "analytic", HelpText = "analytic|bitmap — the artificial horizon's window as a circle fitted to the aircraft's _horiz mask and anti-aliased at window resolution (default), or the 320x200 mask bitmap scaled.")]
    public string? InstrumentWindow { get; set; }

    /// <summary>How the HUD's vector marks are stroked.</summary>
    [Option("hud-style", Default = "refined", HelpText = "refined|classic — the pipper, lead dots, waterline, target box and lock diamond as anti-aliased strokes at window resolution (default) or as the 1991 pixel runs scaled. The geometry is the original's in both.")]
    public string? HudStyle { get; set; }

    /// <summary>The refined HUD marks' stroke width, in 320×200 design pixels.</summary>
    [Option("hud-stroke", Default = 0.15, HelpText = "Refined HUD style: the stroke width of the pipper ring, lead dots' scale, waterline, target box and lock diamond, in 320x200 design pixels (the 1991 runs are 1.0 wide). 0.15 (the default) is a thin, precise sight a play-test preferred for aiming; 0 = 0.64, the earlier period-looking stroke.")]
    public double HudStroke { get; set; }

    /// <summary>The decal lift, in model units.</summary>
    [Option("decal-lift", Default = CYAC.Port.Core.Model.World.DecalConditioning.DefaultEpsilonModelUnits, HelpText = "Asset conditioning: how far a MARKING painted on a surface (a star, a cross, the Sabre's band, a window) is lifted above the panel it decorates so the depth test keeps it in front, in model units. 0 = off (the star and the band vanish again).")]
    public double DecalLift { get; set; }

    /// <summary>The SHEET INFLATION pass off (the 1991 zero-thickness wings, tailplanes, fins and gear doors drawn as
    /// authored).</summary>
    [Option("no-sheet-inflate", Default = false, HelpText = "Asset conditioning OFF for the sheets: draw the zero-thickness wings, tailplanes, fins and gear doors exactly as the 1991 models author them (two coplanar polygons that tie in depth). Default: they are inflated into solids with an airfoil section and their markings are cloned to both skins.")]
    public bool NoSheetInflate { get; set; }

    /// <summary>One factor over every sheet thickness ratio (a tuning knob for the A/B).</summary>
    [Option("sheet-thickness", Default = 1.0, HelpText = "Asset conditioning: multiply every inflated sheet's thickness ratio by this factor (1 = the fleet defaults: wing 12%→8% of chord root→tip, tailplane 9%→6%, fin 8%→5%).")]
    public double SheetThickness { get; set; }

    /// <summary>VECTOR MARKINGS — which markings the aircraft wear.</summary>
    [Option("markings", Default = "vector", HelpText = "vector|shipped|off — the port's projected VECTOR markings (SDF insignia, bands, codes, chipping; the shipped records they replace are hidden; the default since a tuning pass over the fleet), the 1991 polygon markings as authored, or none. Also a Port Settings row.")]
    public string? Markings { get; set; }

    /// <summary>VECTOR MARKINGS — a directory overlaying the built-in pictures, font and placements.</summary>
    [Option("markings-dir", Default = null, HelpText = "A directory with pictures/*.json, font.json and placements/<class>.diff.json (the edits over the generated base) or a full placements/<class>.json, overlaying the built-in vector markings (what the resource browser's Apply Markings tab saves). Default: the checkout's src/CYAC.Port.Core/Markings when found from the data tree, else the built-ins alone.")]
    public string? MarkingsDir { get; set; }

    /// <summary>The tracer's own on-screen width floor.</summary>
    [Option("tracer-floor", Default = CYAC.Port.Render.Ground.LineWidthModel.DefaultTracerFloorHostPixels, HelpText = "The smallest width a TRACER core may be drawn at, in host pixels at a 1920-pixel-wide window (the look a play-test kept). --line-floor now governs every OTHER class and defaults to 0, so thin lines shrink naturally with distance.")]
    public double TracerFloor { get; set; }

    /// <summary>The ENVELOPE window at start (the ESC menu row's twin).</summary>
    [Option("window-envelope", Default = "off", HelpText = "on|off - whether the ENVELOPE window (Shift-2, Help > Envelope Window) starts up. The four --window-* words are the shipped yeager.cfg@0x1D byte's bits (TARGET and MAP on) and the ESC menu's rows; an explicit --windows word overrides all four (probes and tests).")]
    public string? WindowEnvelope { get; set; }

    /// <summary>The TARGET window at start.</summary>
    [Option("window-target", Default = "on", HelpText = "on|off - whether the TARGET window (Shift-3, Help > Target Window) starts up. See --window-envelope.")]
    public string? WindowTarget { get; set; }

    /// <summary>The MAP window at start.</summary>
    [Option("window-map", Default = "on", HelpText = "on|off - whether the MAP window (Shift-1, Help > Map Window) starts up. See --window-envelope.")]
    public string? WindowMap { get; set; }

    /// <summary>The YEAGER window at start.</summary>
    [Option("window-yeager", Default = "off", HelpText = "on|off - whether Chuck Yeager's advisor window (Shift-4, Help > Yeager Window) starts up. See --window-envelope.")]
    public string? WindowYeager { get; set; }

    /// <summary>presentation-side smoothing of deadline-scheduled pool objects.</summary>
    [Option("smooth-objects", Default = "on", HelpText = "on|off — interpolate the drawn pose of pool objects the engagement queue integrates in coarse steps (far bandits), so they fly instead of teleporting. Presentation only; the simulation is untouched. Off draws every object at its sim pose.")]
    public string? SmoothObjects { get; set; }

    /// <summary>The AI node sleep cap (play profile).</summary>
    [Option("ai-sleep-cap", Default = -2, HelpText = "cap an AI engagement node's sleep at N master-counter SECONDS. 0 = the node runs every frame, so a far bandit is moved every frame instead of once every 2-4 s (THE DEFAULT everywhere since - a play-test ruled it harmless after flying it; measured: the AI's decisions land on the same frames and the RNG draws the same count); 1 = at most one second; -1 = the original's law. Unset: 0, except under --replay, which verifies against the original and keeps -1 unless you say otherwise. The script deadlines and the acquisition timers are absolute and untouched; see EngagementNodeContext.SleepCapSeconds.")]
    public int AiSleepCap { get; set; }

    /// <summary>The admitter's cold start (play profile).</summary>
    [Option("ai-admitter-cold-start", HelpText = "on|off - let the periodic re-engagement admitter run from the mission's first frame instead of only after something has first attacked you. The original opens that gate only once an enemy has acquired YOU ([0xF0CE], image@0x0BC23), so bandits that lose sight of you - typically jets, which leave the ±130 degree search cone within seconds of a tail chase - are never re-committed and simply fly their cruise leg out of the sortie (temp/queue/Q2_report.md). On, a drifting bandit is periodically re-committed through the game's own commit arm and comes back. Unset: on, except under --replay, which verifies against the original and keeps it off unless you say otherwise. Not byte-exact; see EngagementLifecycleContext.AdmitterColdStart.")]
    public string? AiAdmitterColdStart { get; set; }

    /// <summary>
    /// <see cref="AiAdmitterColdStart"/> resolved for this run: unset is ON, except under
    /// <see cref="Replay"/>, which verifies against the original's law and keeps it OFF.
    /// </summary>
    /// <remarks>
    /// The same rule <c>--ai-sleep-cap</c> follows (its unset sentinel is <c>-2</c>): a play session
    /// gets the fix, a verification run gets the bytes.  Anything but <c>off</c> is on, so
    /// <c>--ai-admitter-cold-start on</c> also turns it on inside a replay.
    /// </remarks>
    internal bool AdmitterColdStartResolved =>
        AiAdmitterColdStart?.Trim() switch
        {
            { Length: > 0 } value => !string.Equals(value, "off", StringComparison.OrdinalIgnoreCase),
            _ => !Replay,
        };

    /// <summary>Keep drawing the player's aircraft mesh after it has hit the ground.</summary>
    [Option("wreck-mesh", Default = "off", HelpText = "off|on — after the player's aircraft comes to rest, draw its mesh as the wreck. Off (default) leaves the crater and its smoke, as the original leaves a destroyed aircraft.")]
    public string? WreckMesh { get; set; }

    /// <summary>Do not pose the aircraft's landing gear.</summary>
    [Option("no-gear-animation", Default = false, HelpText = "Draw every aircraft in its authored gear-down pose instead of posing it from g_gear_deploy_angle_bam [0xEF96].")]
    public bool NoGearAnimation { get; set; }

    /// <summary>
    /// H18 A/B knob: also draw the shape records no paint-tree leaf emits (the BSP split planes
    /// the original never paints — the parachute's "sail").
    /// </summary>
    [Option("tree-orphans", Default = false, HelpText = "A/B: also draw the records no paint-tree leaf emits (BSP split planes the original never paints, e.g. the grey 'sail' between the parachute's ropes). Off = the original.")]
    public bool TreeOrphans { get; set; }

    /// <summary>
    /// H18 A/B knob: draw the ejection meshes whole, ignoring their prepare callbacks' leaf tags
    /// (the seat would then carry a second pilot and the parachutist four arms).
    /// </summary>
    [Option("no-ejection-parts", Default = false, HelpText = "A/B: ignore the eject1/eject4 prepare callbacks (image@0x2C9F4/0x2CAC8) and draw those meshes whole — the seat with a second pilot, the parachutist with both limb sets.")]
    public bool NoEjectionParts { get; set; }

    /// <summary>
    /// INSPECTION: draw every frame from a FIXED camera instead of the aircraft's.
    /// </summary>
    /// <remarks>
    /// Presentation only — the camera is host-side state (<see cref="CameraRig"/>) and the
    /// simulation never sees it, exactly like <c>--gear-angle</c>.  It exists so a headless run can
    /// put the eye at a stated point over the airfield and produce a before/after PNG pair that is
    /// comparable pixel for pixel (item 1).
    /// </remarks>
    [Option("camera", HelpText = "Inspection: draw from a FIXED camera 'x,y,z[,heading[,pitch[,roll]]]' in world units and degrees, instead of the aircraft's. Presentation only — the simulation is untouched.")]
    public string? Camera { get; set; }

    /// <summary>
    /// INSPECTION: how far the fixed <c>--camera</c> moves per rendered frame, in world units.
    /// </summary>
    /// <remarks>
    /// A deterministic fly-over: one headless run sweeps the eye across the airfield and the
    /// <c>--near-census</c> accumulates over every frame of the pass.
    /// </remarks>
    [Option("camera-step", HelpText = "Inspection: move the fixed --camera by 'dx,dy,dz' world units per rendered frame — a deterministic fly-over.")]
    public string? CameraStep { get; set; }

    /// <summary>
    /// Diagnostic: census the instances near the camera and report what happened to each.
    /// </summary>
    [Option("near-census", Default = 0.0, HelpText = "Diagnostic: for every instance whose origin is within this MANHATTAN radius (world units) of the camera, report whether it was drawn or which test rejected it. 0 = off.")]
    public double NearCensus { get; set; }

    /// <summary>
    /// How near an instance must be for the renderer to take NO whole-instance visibility
    /// decision about it.
    /// </summary>
    [Option("near-exempt", Default = SceneRenderOptions.DefaultNearInstanceExemptionWorldUnits, HelpText = "Inside this Manhattan distance (world units) the renderer never rejects a WHOLE instance — it clips the faces instead (the fix for 'parts of the base disappear when flying over them'). 0 restores the bounding-sphere reject at every distance, which is the A/B.")]
    public double NearExempt { get; set; }

    /// <summary>
    /// A DEPRECATED ALIAS for <c>--edges hard</c>.
    /// </summary>
    /// <remarks>
    /// Folded into <see cref="CYAC.Port.Render.EdgeMode"/>: the two both map to the centre test
    /// once the background is the resolve's terminal function, and two switches for one decision
    /// can disagree.  Passing this sets <c>--edges hard</c> — every edge in the frame, not only the
    /// horizon's — and says so once on the console.  It will be removed; the one control is the
    /// settings table's "Polygon edges" row.
    /// </remarks>
    [Option("no-aa", Default = false, HelpText = "DEPRECATED: an alias for --edges hard. It used to harden the horizon's split alone; the split is now one edge among all of them, so this hardens the whole frame and prints one line saying so.")]
    public bool NoAntiAlias { get; set; }

    // Super-sampling is retired: --ssaa now fails to parse, which is the same loud failure
    // --raster and --ground already give: a script that asks for a renderer that no longer exists
    // should be told, not silently handed a different one.


    /// <summary>
    /// The TILE SIDE the 3-D display list is binned into, in target pixels.
    /// </summary>
    /// <remarks>
    /// A pure performance knob: the frame is bit-identical at every tile size
    /// (<c>CYAC.Port.Render.Tests.TileInvarianceTests</c>), so this exists to be measured, not to
    /// be tuned by eye.  128 is a reasonable starting point.
    /// </remarks>
    [Option("tile", Default = 128, HelpText = "the side in TARGET pixels of the square tiles the 3-D display list is binned into and rasterised in (default 128; 4..8192; a value that does not divide the frame is legal). A PERFORMANCE knob only - the picture is bit-identical at every tile size.")]
    public int Tile { get; set; }

    /// <summary>
    /// How many threads draw the frame's tiles.  0 (the default) means the processor count.
    /// </summary>
    /// <remarks>
    /// 1 runs the identical code on the render thread, which is what the library ships; the host
    /// ships the whole machine.  Like <see cref="Tile"/> it cannot reach the picture.
    /// </remarks>
    [Option("threads", Default = 0, HelpText = "how many threads rasterise the 3-D world's tiles. 0 (the default) = the processor count; 1 = the serial path through the same code. A PERFORMANCE knob only - the picture is bit-identical at every thread count.")]
    public int Threads { get; set; }

    // Deleted with GroundMode: the flat y = 0 world goes through the ONE pipeline now, in the
    // DrawLayer.GroundDecal tier, with its lines and ribbons widened in the ground plane exactly as
    // H15 widened them.  There is no second path to select and nothing the option could mean.

    /// <summary>H15 / the physical widths of the line classes, in feet.</summary>
    [Option("line-width", HelpText = "the PHYSICAL width in feet of each class of line, e.g. 'road=30,river=40,mark=3,tracer=0.5,line=0.5,rope=0.15,strut=0.3' (the defaults). road covers city and urban streets, mark covers the runway markings, the airfield outline and the SAM site, tracer is the bullet's core, rope is a parachute shroud line (eject2/3/4), strut is an aircraft gear leg or airframe line, line is any other object's detail lines. 'mesh:<basename>=<feet>' overrides one mesh (shipped: mesh:trees=2 for the trunks, mesh:hedge=3 for the treeline stems). A DEVIATION - the original has no widths at all - so tune them by eye.")]
    public string? LineWidth { get; set; }

    /// <summary>The smallest width a line may be drawn at, in host pixels at 1,920 wide.</summary>
    [Option("line-floor", Default = LineWidthModel.DefaultFloorHostPixels, HelpText = "the smallest width a non-tracer line may be DRAWN at, in host pixels at a 1920-pixel-wide window. default 0 = OFF (lines shrink naturally with distance); 1.5 restores the line floor. The tracer has its own --tracer-floor.")]
    public double LineFloor { get; set; }

    /// <summary>Print the ground-extraction census and exit the render loop's first frame.</summary>
    [Option("ground-census", Default = false, HelpText = "print the analytic ground layer's EXTRACTION census - which classes it takes, how many polygons and capsules the loaded theatre yields, their colours, and which faces are left in the 3-D pass and why.")]
    public bool GroundCensus { get; set; }

    /// <summary>Whether a sub-floor thread is dimmed as well as widened.</summary>
    [Option("line-thread", Default = "on", HelpText = "on|off - whether a THREAD (a parachute shroud line, a landing-gear leg) thinner than --line-floor is DIMMED by the fraction of a pixel it really covers as well as widened to the floor, so it reads as a semi-transparent thread instead of a solid sausage. 'off' restores the earlier single rule (widen at full brightness) for every class, which is the A/B.")]
    public string? LineThread { get; set; }

    /// <summary>Print the LINE census over the whole mesh library.</summary>
    [Option("line-census", Default = false, HelpText = "print the LINE census - every LINE shape record in every mesh, its LOD, whether --lod max draws it, whether it belongs to a landing-gear articulation leaf, its width class, the width it will be drawn at, its palette colours and its length in feet.")]
    public bool LineCensus { get; set; }

    /// <summary>Which halves of the tracer halo are drawn.</summary>
    /// <remarks>
    /// An A/B switch: <c>a</c> is the tracer-coloured glow disc, <c>b</c> the
    /// relative lightening toward white, <c>ab</c> both (see <see cref="TracerHalo"/>).
    /// </remarks>
    [Option("tracer-halo", Default = "ab", HelpText = "off|a|b|ab - the tracer's special-effect halo. 'a' is a semi-transparent tracer-coloured glow, more transparent toward the edge; 'b' LIGHTENS whatever is under it by a relative amount (each pixel moves --tracer-glow of the way to white), so it barely shows over a day sky and glows over dark ground or a night sky; 'ab' is both. A DEVIATION - the original draws one flat palette-32 line and nothing else.")]
    public string? TracerHalo { get; set; }

    /// <summary>The halo's physical radius in feet.</summary>
    [Option("tracer-halo-radius", Default = CYAC.Port.Render.TracerHalo.DefaultRadiusFeet, HelpText = "the tracer halo's radius about the projectile's own segment, in FEET (world units).")]
    public double TracerHaloRadius { get; set; }

    /// <summary>The halo disc's peak opacity.</summary>
    [Option("tracer-halo-alpha", Default = CYAC.Port.Render.TracerHalo.DefaultAlpha, HelpText = "half A's peak opacity at the centre of the halo profile (the rim is always 0). 0 switches A off.")]
    public double TracerHaloAlpha { get; set; }

    /// <summary>The halo's smallest on-screen radius, in host pixels at 1,920 wide.</summary>
    [Option("tracer-halo-floor", Default = CYAC.Port.Render.TracerHalo.DefaultFloorHostPixels, HelpText = "the smallest RADIUS the halo may be drawn at, in host pixels at a 1920-pixel-wide window (scaled with the width, like --line-floor), so a distant burst keeps a visible bloom. 0 switches the floor off and a far tracer's halo shrinks with its range.")]
    public double TracerHaloFloor { get; set; }

    /// <summary>Half B's peak relative lightening.</summary>
    [Option("tracer-glow", Default = CYAC.Port.Render.TracerHalo.DefaultGlow, HelpText = "half B's peak lightening - a pixel under the centre of the halo moves this fraction of its own distance to white (c += (255-c)*g). 0.10 is a play-test suggested 10%. 0 switches B off.")]
    public double TracerGlow { get; set; }

    /// <summary>The palette index the halo is drawn in.</summary>
    /// <remarks>
    /// Their <c>settings.json</c> holds <b>32</b>, which is the palest red of the tracer ramp —
    /// <c>data/palettes/palette.json</c> index 32 = 6-bit (63, 39, 39) = RGB (255, 158, 158).  That
    /// is the same colour the .PNT bullet record carries, so the bake PINS it rather than changing
    /// it: a halo drawn behind a round of another colour keeps the tracer's red instead of following
    /// the round.  There is no Render constant for this one: <c>TracerHalo</c>'s own
    /// <c>ColorIndex</c> parameter stays −1 (the LIBRARY default), and the host always specifies —
    /// so this attribute IS the shipped default.
    /// </remarks>
    [Option("tracer-color", Default = 32, HelpText = "the palette index the halo's glow is drawn in; 32 (the default, the tuned value) is the palest red of the tracer ramp - RGB (255,158,158) - which is also what the.PNT bullet record carries; -1 takes the drawn record's own colour instead.")]
    public int TracerColor { get; set; }

    /// <summary>The grey ordinary rounds that trail every tracer.</summary>
    [Option("rounds", Default = "on", HelpText = "on|off - PER-ROUND GUNNERY. The weapon table's ammoPerShot (5 for the .50s, 2 for the heavy cannon, 10 for the fast MGs) is the rounds per burst; the ported bullet object stays the one tracer and the sole damage authority, and N-1 GREY rounds trail it at the cyclic spacing (64 ticks / N), each with a hit test and a hit flash but no damage (a tuning decision). 'off' is one tracer per burst.")]
    public string? Rounds { get; set; }

    /// <summary>A grey round's streak length in feet.</summary>
    /// <remarks>
    /// Their <c>settings.json</c> holds <b>113</b>.  There is no Core/Render constant for this one
    /// — the length is a host mesh parameter (<c>FlightRasterizer.SetRoundLength</c>) — so this
    /// attribute IS the single source of truth.
    /// </remarks>
    [Option("round-length", Default = 113.0, HelpText = "the grey round's streak LENGTH in feet (the shipped tracer is a 64-foot line). 113 = the tuned default (an earlier P-51 setting was larger).")]
    public double RoundLength { get; set; }

    /// <summary>A grey round's physical width in feet.</summary>
    [Option("round-width", Default = LineWidthModel.DefaultRoundFeet, HelpText = "the grey round's physical WIDTH in feet; a thread, so below --round-floor it is widened to the floor and dimmed to its coverage.")]
    public double RoundWidth { get; set; }

    /// <summary>The grey round's own on-screen floor.</summary>
    [Option("round-floor", Default = LineWidthModel.DefaultRoundFloorHostPixels, HelpText = "the smallest width a grey round is DRAWN at, host pixels at a 1920-wide window (0 = none: a far round vanishes).")]
    public double RoundFloor { get; set; }

    /// <summary>The grey round's palette index.</summary>
    [Option("round-color", Default = 15, HelpText = "the palette index an ordinary round is drawn in. 15 = white (a play-test finding: white reads most naturally); 19 = a mid grey (46,46,46); 20 = the darker grey of palette 7.")]
    public int RoundColor { get; set; }

    /// <summary>A multiplier on the hit spheres.</summary>
    [Option("round-hit-scale", Default = 1.0, HelpText = "proportional scale on every hit BOX a grey round is tested against — 1 (the default) is the integer kernel's own class-record box (+0x30..+0x47) to the foot, 0.5 halves every half-extent. Presentation only: it moves flashes, never damage.")]
    public double RoundHitScale { get; set; }

    /// <summary>Whether a ground strike lights a flash.</summary>
    [Option("round-ground-hits", Default = "on", HelpText = "on|off - a grey round reaching the ground plane lights a half-size flash.")]
    public string? RoundGroundHits { get; set; }

    /// <summary>A spacing override.</summary>
    /// <remarks>
    /// Their <c>settings.json</c> holds <b>202</b> ft, a little tighter than the cyclic law's 220
    /// ft for a 5-round .50-calibre burst.  There is no Core constant for this one:
    /// <c>GunneryRounds.SpacingWorldUnits</c>'s own default stays 0 = "take the cyclic law" (the
    /// LIBRARY default, and the integer kernel's own law), and the host always specifies — so
    /// this attribute IS the shipped default.  Set the row back to 0 for the cyclic law.
    /// </remarks>
    [Option("round-spacing", Default = 202.0, HelpText = "the spacing between consecutive rounds of a belt, in FEET; 202 = the tuned default; still means the cyclic law speed x 64 ticks / N (220 ft for a 5-round.50-calibre burst at 4,400 ft/s).")]
    public double RoundSpacing { get; set; }

    /// <summary>
    /// The headless PIXEL CENSUS: hard edges, and the frame-to-frame flicker of the world.
    /// </summary>
    [Option("pixel-census", Default = false, HelpText = "census the rendered WORLD rows of every headless frame — hard-edge pixel pairs (a max-channel step of 64+), total variation, A-B-A flicker toggles and mean temporal jerk — so jaggedness and shimmer are a number, not an impression. (it used to name --ssaa, which is gone; the dial it measures now is --edges analytic|hard.)")]
    public bool PixelCensus { get; set; }

    /// <summary>Restrict <c>--pixel-census</c> to one rectangle.</summary>
    [Option("census-rect", HelpText = "restrict --pixel-census to the rectangle 'x,y,w,h' (default: the whole 3-D world). Give it the same rectangle as --crop and the numbers describe exactly the picture the crop shows.")]
    public string? CensusRect { get; set; }

    /// <summary>Write a magnified crop beside every saved frame.</summary>
    [Option("crop", HelpText = "also write '<frame>.crop.png' — the rectangle 'x,y,w,h' of the frame, magnified by --crop-scale with NEAREST sampling, so distant scenery can be inspected pixel by pixel.")]
    public string? Crop { get; set; }

    /// <summary>The crop's magnification.</summary>
    [Option("crop-scale", Default = 4, HelpText = "--crop's nearest-neighbour magnification (default 4x).")]
    public int CropScale { get; set; }

    /// <summary>
    /// The COCKPIT: the player's own <c>g_cockpit_visible_flag [0xE471]</c>, which
    /// Backspace toggles at run time exactly as the original's key ladder does.
    /// </summary>
    [Option("cockpit", Default = "on", HelpText = "on|off — draw the cockpit panel, its compositor overlay, its instrument regions and its dials (g_cockpit_visible_flag [0xE471]; Backspace toggles it at the window, image@0x013D8). The original paints it in the FORWARD cockpit view only.")]
    public string? Cockpit { get; set; }

    /// <summary>
    /// Where the 3-D world's own centre falls: the aircraft's VIEWPORT (the original) or the SCREEN
    /// (a port addition).
    /// </summary>
    /// <remarks>
    /// The original composes the world into the viewport rectangle and puts the clip window's centre
    /// at <c>(y_min + y_max) &gt;&gt; 1</c> (<c>image@0x118A4</c>), which is why the P-51's horizon
    /// sits on row 67 of 200 and not on row 100.  <c>screen</c> keeps the same lens and the same
    /// panel and lets the world fill the whole window instead, so the horizon is where a modern
    /// flight sim puts it.
    /// </remarks>
    [Option("cockpit-view", Default = "viewport", HelpText = "viewport|screen — where the 3-D world's centre falls. 'viewport' is the ORIGINAL: the world fills the aircraft's viewport rows alone, so the horizon sits on row 67 of 200 in a P-51 (image@0x118A4 puts the clip window's centre at (y_min+y_max)>>1). 'screen' is a PORT ADDITION: the world fills the whole window and the panel is drawn over it, so the horizon is at the middle of the screen.")]
    public string? CockpitView { get; set; }

    /// <summary>How the 320×200 cockpit art is resampled to the window.</summary>
    /// <remarks>
    /// The port's choice: the cockpit bitmap is resampled to NEAREST, so the shipped look is the
    /// blocky original rather than a filtered one.  The menu row keeps <c>smooth</c>
    /// one Left/Right press away.  There is no Render constant for this one:
    /// <c>CockpitOptions.Filter</c>'s own default parameter stays <c>CockpitFilter.Smooth</c> (the
    /// LIBRARY default, which the Render tests exercise both ways explicitly), and the host always
    /// specifies — so this attribute IS the shipped default.
    /// </remarks>
    [Option("cockpit-filter", Default = "nearest", HelpText = "smooth|nearest — how the cockpit art is resampled: nearest (the default: the 1991 pixels, enlarged — a deliberate choice to ship the blocky original) or smooth (bilinear over the opaque pixels; the art was drawn for a 1.2:1 CRT).")]
    public string? CockpitFilter { get; set; }

    /// <summary>How the design space maps onto the window.</summary>
    [Option("cockpit-fit", Default = "stretch", HelpText = "stretch|uniform — stretch maps 320x200 onto the whole window (non-uniform, the default); uniform keeps the art's 320:200 aspect and pillar-boxes it, with the 3-D viewport still full width.")]
    public string? CockpitFit { get; set; }

    /// <summary>
    /// The HUD overlay's own switch: <c>g_flight_info_visible [0xB0]</c>, the original's Ctrl-F
    /// "Flight Info" item in the in-flight Graphics menu.
    /// </summary>
    [Option("flight-info", Default = "on", HelpText = "on|off — the HUD text overlay (g_flight_info_visible [0xB0], the Graphics menu's Ctrl-F item; Ctrl-F toggles it at the window). With it off the forward view keeps only the gunsight and the target marker, exactly as image@0x0C633's si=0x500 mask does.")]
    public string? FlightInfo { get; set; }

    /// <summary>
    /// INSPECTION: draw the guided-weapon marker and the pipper's four lead dots at fixed offsets,
    /// so the whole overlay can be photographed without flying a sortie to a live lock.
    /// </summary>
    [Option("hud-demo", Default = false, HelpText = "Inspection: force the HUD's target box, its confirmed lock diamond and all four of the pipper's lead dots, at fixed offsets from the boresight. Nothing in the simulation changes; it is the --effect-probe of the HUD.")]
    public bool HudDemo { get; set; }

    /// <summary>The sound path's own switch.</summary>
    [Option("sound", Default = "on", HelpText = "on|off — the SOUND path: the adl tone generators (adldrive.drv, rendered at 48 kHz), the two continuous channels and every kernel sound seam. --wav renders it anywhere; --audio-output picks the live device.")]
    public string? Sound { get; set; }

    /// <summary>Which live output device the host opens.</summary>
    [Option("audio-output", Default = "auto", HelpText = "auto|mac|sdl|none — the LIVE output device. 'auto' opens macOS' AudioToolbox AudioQueue on a Mac (with SDL2 behind it should the queue refuse) and SDL2's audio device on Windows and Linux; 'mac' and 'sdl' force one, which is how the cross-platform path is exercised on a Mac; 'none' opens nothing and the sortie plays in silence. --wav and a --headless run without --wav open no device whatever this says.")]
    public string? AudioOutput { get; set; }

    /// <summary>The mixer's master volume.</summary>
    [Option("sound-volume", Default = 0.8, HelpText = "Master volume, 0..1 (the mixer's own scale, applied after the generators).")]
    public double SoundVolume { get; set; }

    /// <summary>
    /// <c>g_audio_mute_mask [0xE483]</c>: the System menu's own five sound switches.
    /// </summary>
    [Option("sound-mask", Default = "0xFF", HelpText = "g_audio_mute_mask [0xE483] as a byte: bit0 master (gates audio_event_dispatcher itself, image@0x29A43), bit1 engine (image@0x29D7C), bit2 radar-warning (image@0x2A03F), bit3 stall (image@0x29FD1), bit4 lock (image@0x2A081). The shipped yeager.cfg holds 0xFE.")]
    public string? SoundMask { get; set; }

    /// <summary>
    /// How opaque the ESC menu's strip and popup panels are, 0..1.
    /// </summary>
    /// <remarks>
    /// The original's panels are solid; the port fades them so a frozen scene stays visible behind
    /// the menu while a render option is being tuned.  Text is never faded.  The <c>?</c> menu's
    /// "Menu Opacity" row cycles the same value at run time.
    /// </remarks>
    [Option("menu-opacity", Default = 0.85, HelpText = "ESC menu panel opacity 0..1 (1 = the original's solid grey; the default 0.85 keeps the frozen scene visible behind it). Text is always opaque.")]
    public double MenuOpacity { get; set; } = Menu.FlightMenuController.DefaultOpacity;

    /// <summary>
    /// The ESC menu's own INTEGER pixel scale: host pixels per 320 × 200 design pixel.
    /// </summary>
    /// <remarks>
    /// The menu is a bitmap widget whose drawing rules are pixel-exact at 1×, so it is scaled by
    /// whole pixels only — never by the cockpit's fractional 5.4.  <c>auto</c> is
    /// <c>floor(min(w/320, h/200) / 4)</c> — a quarter of the current pixel size: 1 at 1080p,
    /// 2 at 4K.
    /// </remarks>
    [Option("menu-scale", Default = "auto", HelpText = "ESC menu pixel scale: auto|1|2|3|4|... host pixels per design pixel. 'auto' is a QUARTER of the cockpit's own design scale (1 at 1080p, 2 at 4K) — the menu is a bitmap widget and is only ever scaled by whole pixels. The ? menu's 'Menu Size' row cycles it at run time.")]
    public string? MenuScale { get; set; }

    /// <summary><see cref="MenuScale"/> as the controller wants it (0 = auto).</summary>
    /// <returns>Host pixels per design pixel, or 0 for auto.</returns>
    public int ResolveMenuScale()
    {
        string? text = MenuScale?.Trim();
        if (string.IsNullOrEmpty(text) || text.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return Menu.FlightMenuController.AutoScale;
        }

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int scale)
            && scale > 0
                ? Math.Min(scale, MaxMenuScale)
                : Menu.FlightMenuController.AutoScale;
    }

    /// <summary>The largest <c>--menu-scale</c> the host accepts (a 4K window holds 6 of them).</summary>
    public const int MaxMenuScale = 16;

    /// <summary>
    /// How many host pixels one 320×200 design pixel of a FRONT-END screen is worth.
    /// </summary>
    /// <remarks>
    /// <c>auto</c> is the largest whole 320×200 that fits: 5 at 1080p, 10 at 4K, the design centred
    /// with the rest of the window painted in the panel's dark tone.  Whole pixels for the reason
    /// M1b gives — these widgets are one-pixel bevel lines and a one-row emboss under every glyph.
    /// This is a DIFFERENT knob from <c>--menu-scale</c>: that one sizes the ESC menu bar, which
    /// floats over a flying sortie and is deliberately small.
    /// </remarks>
    [Option("frontend-scale", Default = "auto", HelpText = "Front-end (CHOOSE ACTIVITY, Credits, ...) pixel scale: auto|1|2|3|... host pixels per 320x200 design pixel. 'auto' is the largest whole screen that fits (5 at 1080p, 10 at 4K), centred.")]
    public string? FrontEndScale { get; set; }

    /// <summary>
    /// Headless only: render the SHELL (the front end) rather than a sortie.
    /// </summary>
    /// <remarks>
    /// A window run always builds the shell; a headless run does not, because the batteries, the
    /// replays and every <c>--frame-count</c> run in the project are flights and must stay exactly
    /// what they were (protocol §5).  With this flag the headless loop drives a
    /// <see cref="FrontEnd.HostShell"/> instead, and the <c>--script</c>'s front-end words
    /// (<c>tab</c>, <c>space</c>, …) walk it.
    /// </remarks>
    [Option("frontend", Default = false, HelpText = "Headless: render the front-end shell (CHOOSE ACTIVITY) instead of a sortie; --script's tab/shifttab/space/enter/esc/up/down/key:X words drive it, and a sortie it starts is rendered too.")]
    public bool FrontEnd { get; set; }

    /// <summary>
    /// Where <c>settings.json</c> lives, when it is not beside the data tree's parent.
    /// </summary>
    /// <remarks>
    /// The default is <c>&lt;data root&gt;/../settings.json</c> — the repo root when the port runs
    /// out of the checkout.  It is never inside <c>data/</c>, which <c>cyac-transform</c> owns and
    /// regenerates.  A DIRECTORY is accepted and <c>settings.json</c> is taken inside it.
    /// </remarks>
    [Option("settings", HelpText = "Where the port's settings.json lives (default: beside the data tree's PARENT directory, i.e. the repo root in a checkout). A directory is accepted and settings.json is taken inside it.")]
    public string? SettingsPath { get; set; }

    /// <summary>Print the settings table and the file the run resolved, then keep flying.</summary>
    [Option("settings-census", Default = false, HelpText = "Print every port setting — its value, whether it is live or takes effect on the next sortie, whether it is at its default (*) or pinned by the command line (~) — and the resolved settings.json path.")]
    public bool SettingsCensus { get; set; }

    /// <summary>
    /// Where <c>stats.json</c> lives, when it is not beside the data tree's parent.
    /// </summary>
    /// <remarks>
    /// A SIBLING of <c>settings.json</c>, with the same path rule and the same directory-accepted
    /// spelling.  Giving it explicitly is also what lets a HEADLESS run keep statistics: without it,
    /// <c>--headless</c> / <c>--replay</c> / <c>--frame-count</c> never write the file, so the
    /// replays and test runs cannot litter a player's own statistics.
    /// </remarks>
    [Option("stats", HelpText = "Where the port's stats.json lives (default: beside the data tree's PARENT directory, i.e. the repo root in a checkout). A directory is accepted. Giving this explicitly is also what makes a HEADLESS run keep statistics — without it a headless run records nothing.")]
    public string? StatsPath { get; set; }

    /// <summary>Print every mission's statistics line, then keep flying.</summary>
    [Option("stats-census", Default = false, HelpText = "Print the per-mission statistics the run resolved: one line per mission with a record, plus the derived totals, and the resolved stats.json path.")]
    public bool StatsCensus { get; set; }

    /// <summary>Print one line per menu row: its id, its label and what the port does with it.</summary>
    [Option("menu-wiring", Default = false, HelpText = "Print the ESC menu's item table: every row's id, label, whether it is wired, and the image@ citation or the reason it is not.")]
    public bool MenuWiring { get; set; }

    /// <summary>Render the sortie's audio to a WAV instead of a device.</summary>
    [Option("wav", HelpText = "Headless: render the sortie's audio to this WAV file (48 kHz stereo). Implies no live device, and is how a headless run is listened to.")]
    public string? Wav { get; set; }

    /// <summary>How much audio a <c>--wav</c> run keeps.</summary>
    [Option("wav-seconds", Default = 120.0, HelpText = "--wav: stop accumulating after this many seconds of audio (48 kHz stereo float is ~384 kB/s in memory, so an unbounded 40,000-frame kill sortie would be gigabytes). The sortie itself runs on.")]
    public double WavSeconds { get; set; }

    /// <summary>Dump the port's own driver-call log for a tone histogram.</summary>
    [Option("tone-log", HelpText = "Write the port's own <log>.drvcalls.csv-shaped driver-call census here, so its tone histogram can be compared with the emulator's on a real session.")]
    public string? ToneLog { get; set; }

    /// <summary>Which views the player's own engine is heard in.</summary>
    [Option("engine-views", Default = "all", HelpText = "all|original — where the PLAYER's engine is heard. 'original' is the 1991 rule (image@0x29D86 / image@0x29E4A: the cockpit views and the engaged external-target view, plus a fixed tone in the circling view — silence everywhere else). 'all' (the default, a labelled DEVIATION) keeps it sounding in every view, attenuated by the camera's distance through the original's own fly-by law.")]
    public string? EngineViews { get; set; }

    /// <summary>How many positional sources may sound at once.</summary>
    [Option("sound-sources", Default = 8, HelpText = "How many NON-player aeroplanes are mixed as positional sound sources (their engines, and their guns when they fire), nearest first. 0 restores the original's single-engine mix. Each source is its own FM synth — the driver's four SFX slots are deliberately not shared.")]
    public int SoundSources { get; set; }

    /// <summary>How far a positional source may be heard.</summary>
    [Option("sound-radius", Default = 10000, HelpText = "How far, in feet, a positional source may be and still be mixed. The default is the original's own audible radius for a positional sound (sfx_object_impact_dispatch drops an impact at cmp si,0x2710, image@0x29BB5), by which range the distance law is already about -60 dB down.")]
    public int SoundRadius { get; set; }

    /// <summary>The outside-listening low-pass.</summary>
    [Option("engine-lowpass", Default = 2000.0, HelpText = "DEVIATION: a one-pole low-pass, in hertz, applied to every engine heard from OUTSIDE a cockpit — the port's 'through the air' colour, which is what makes your own engine change a little when the camera leaves the cockpit. 0 switches it off (the original has no filter at all).")]
    public double EngineLowPass { get; set; }

    /// <summary>The session-reopen reset (a diagnostic switch).</summary>
    [Option("audio-reset", Default = "on", HelpText = "on|off — run audio_continuous_state_reset @image@0x298BD when a session reopens (a respawn or the fate machine's restart), as mission_state_machine does at image@0x00B05. 'off' reproduces the restart-loop bug (the previous sortie's mechanism deadline survives, so the gear sound runs for the whole of the next one) and exists only to measure it.")]
    public string? AudioReset { get; set; }

    private void Bind(Key key, ControlEnum control) => KbControls[key] = Control.Create(control);

    /// <summary>
    /// Whether the text readout is drawn into the frame: in the window only on <c>--readout</c>, in a
    /// headless run unless <c>--no-readout</c>.  The caller still ANDs in whether the font is reachable.
    /// </summary>
    public bool DrawReadout => Headless ? !NoReadout : Readout && !NoReadout;

    /// <summary>
    /// Turns the parsed command line into the player's defaults: full screen, vsync and fast mode
    /// on unless <c>--windowed</c>, <c>--no-vsync</c> or <c>--no-fast</c> says otherwise.
    /// </summary>
    /// <remarks>
    /// <c>-f</c>, <c>-v</c> and <c>--fast</c> are mode-13hx's own switches
    /// (<c>external/mode-13hx/src/Configuration/CommonOptions.cs</c>) and default to OFF there, which
    /// is right for a presentation-layer demo and wrong for a game a player starts by double-clicking
    /// it: a 1080p window over the desktop, tearing, and a rasterizer that stalls on vsync.  A
    /// CommandLineParser switch cannot be negated on the command line, so the defaults are inverted
    /// HERE, after the parse, through three opt-out switches the host owns rather than by editing the
    /// vendored defaults.  The three original switches stay accepted and name the default.
    /// </remarks>
    public void ApplyPlayerDefaults()
    {
        Fullscreen = !Windowed;
        VSync = !NoVSync;
        Fast = !NoFast;
    }
}
