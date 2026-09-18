using System.Diagnostics;
using System.Globalization;
using CYAC.Port.Core.Model.Cockpit;
using CYAC.Port.Core.Model.Combat;
using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Model.Mission;
using CYAC.Port.Core.Model.World;
using CYAC.Port.Core.Sim;
using CYAC.Port.Core.Sim.Combat;
using CYAC.Port.Core.Sim.Combat.Effects;
using CYAC.Port.Core.Sim.Combat.Lifecycle;
using CYAC.Port.Core.Sim.Combat.Player;
using CYAC.Port.Core.Sim.Flight;
using CYAC.Port.Core.Sim.Session;
using CYAC.Port.Host.Configuration;
using CYAC.Port.Host.Headless;
using CYAC.Port.Host.Input;
using CYAC.Port.Host.Menu;
using CYAC.Port.Host.Sound;
using CYAC.Port.Host.Settings;
using CYAC.Port.Host.Sim;
using CYAC.Port.Host.Stats;
using CYAC.Port.Render;
using CYAC.Port.Render.Cockpit;
using CYAC.Port.Render.Ground;
using CYAC.Port.Render.Map;
using mode13hx;
using mode13hx.Controls;
using mode13hx.Presentation;
using mode13hx.Util;

namespace CYAC.Port.Host;

/// <summary>
/// The four OVERLAY WINDOWS and the MAP: the advisor panel, the envelope panel, the scope
/// and its contacts, the map scene and the navigation waypoints and bearings.
/// </summary>
/// <remarks>The reader's map of every one of this class's files is at the top of
/// <c>FlightRasterizer.cs</c>.</remarks>
public sealed partial class FlightRasterizer
{
    /// <summary>What the MAP window plotted in the frame just rendered.</summary>
    public int MapDotsLastFrame => _lastMapDots;

    /// <summary>What the ENVELOPE window drew in the frame just rendered.</summary>
    public int EnvelopeShapesLastFrame => _lastEnvelopeShapes;

    /// <summary><c>--nav-readout</c>: how much of the NAV block is drawn.</summary>
    private readonly NavReadoutMode _navReadout;

    /// <summary>
    /// The live <c>g_stipple_scale_shift [0x3234]</c>: the MAP window's zoom, and the scale of all
    /// three pixel-obj displays.  9 at load (<c>image@0x00ABD</c>), clamped 8..11 by the <c>.</c>
    /// and <c>,</c> keys (<c>image@0x012B2</c> / <c>image@0x012C7</c>).
    /// </summary>
    public int MapScaleShift { get; private set; } = MapWindow.DefaultScaleShift;

    /// <summary>How many times a map-window zoom key was pressed this sortie.</summary>
    public int MapZoomKeyPresses { get; private set; }

    /// <summary>How many advisories the integer kernels RAISED this sortie.</summary>
    public int AdvisoriesRaised => _advisor?.Raised ?? 0;

    /// <summary>How many advisories reached Chuck's window this sortie.</summary>
    public int AdvisoriesSpoken => _advisor?.Spoken ?? 0;

    /// <summary>How many the cooldown, a one-shot bit or the random gate refused.</summary>
    public int AdvisoriesSuppressed => _advisor?.Suppressed ?? 0;

    /// <summary>The one-shot bitmask <c>[0xBCFA]/[0xBCFC]</c>, for the census line.</summary>
    public uint AdvisorMask => _advisor?.Mask ?? 0;

    /// <summary>How many text lines the advisor window drew last frame (0, 1 or 2).</summary>
    public int AdvisorLinesDrawn => _lastAdvisorLines;

    /// <summary>
    /// The frame's OVERLAY WINDOW state: which windows are up, and the band strings another part of the port owns.  The
    /// window CONTENTS are their own slices (W1..W5) and extend <see cref="OverlayWindowState"/> as
    /// they land.
    /// </summary>
    /// <param name="view">The frame's sim snapshot — the envelope window's marker reads it.</param>
    /// <returns>What the chrome layer draws this frame.</returns>
    /// <remarks>
    /// The TARGET window's second gate is <see cref="OverlayTargetPresent"/> — the original's
    /// <c>target_in_range_view_check</c> through the <c>lcall</c> at <c>image@0x0EAB1</c>.  W0
    /// answers it with "the lock-on has a target", which is the necessary half; the range and view
    /// terms of that check are <b>(open)</b> until W2 decodes them.
    /// </remarks>
    /// <summary>
    /// <c>prng_rand8</c>'s stand-in for the advisory message gate at <c>image@0x0F03F</c>, drawn
    /// from the session's <c>Fx.Indicators</c> stream.
    /// </summary>
    /// <returns>A draw in 0..255.</returns>
    private int NextAdvisorGateDraw() => (int)_session.FxIndicators.NextBounded(256);

    /// <summary>
    /// Puts the advisor queue on this sortie's seams.
    /// </summary>
    /// <remarks>
    /// Three doors, one per wrapper: the flight kernel's own advisories arrive through
    /// <see cref="HostWorld.DispatchFlightAdvisor"/> (rate-limited, <c>image@0x2B44C</c>), the combat
    /// kernels' through <c>SessionEffects.AdvisorObserver</c> (direct) and the mission module's
    /// through <c>SessionLifecycleEvents.AdvisorObserver</c> (one-shot, <c>image@0x08C69</c>).  The
    /// two delegates are made once per mission, not once per frame.
    /// </remarks>
    private void EnsureAdvisorWired()
    {
        if (_advisor is null)
        {
            return;
        }

        _session.World.Advisor = _advisor;
        MissionSession? mission = _session.Mission;
        if (ReferenceEquals(_advisorWiredTo, mission))
        {
            return;
        }

        _advisorWiredTo = mission;
        if (mission is null)
        {
            return;
        }

        AdvisorDispatch? queue = _advisor;
        mission.VmEffects.AdvisorObserver =
            code => queue.Raise(code, AdvisorDispatchKind.Direct);
        mission.Combat.LifecycleEvents.AdvisorObserver =
            code => queue.Raise(code, AdvisorDispatchKind.OneShot);
    }

    private OverlayWindowState BuildOverlayWindowState(in FlightSnapshot view)
    {
        ushort selected = TargetInRangeViewCheck();

        // The MAP window's footer is its zoom, `x` + (1 << (0x0B − shift))
        // (image@0x0EB51..0x0EB63), and its face plots the SAME contact list the two scopes use:
        // one pixel-obj engine, three contexts.
        MapPanelState map = new MapPanelState(MapScaleShift, _lastRadar.Contacts);
        string mapFooter = _overlays.HasFlag(CockpitOverlayFlags.Map)
            ? MapWindow.ZoomLabel(MapScaleShift)
            : string.Empty;

        EnvelopePanelState envelope = BuildEnvelopePanelState(in view);
        string envelopeFooter = _overlays.HasFlag(CockpitOverlayFlags.Envelope)
            ? EnvelopeFooter(_session.State.Aircraft.GLoadInteger, view.Y)
            : string.Empty;

        // The advisor's own law runs here, once a frame, over whatever the kernels raised during
        // the steps this frame presented.  It is the only window whose CHROME depends on its
        // content: Chuck is on the band only while a message's display window is open.
        AdvisorPanelState advisor = BuildAdvisorPanelState();

        return selected == 0
            ? new OverlayWindowState(
                Visible: _overlays,
                MapFooter: mapFooter,
                EnvelopeFooter: envelopeFooter,
                MapPanel: map,
                EnvelopePanel: envelope,
                AdvisorPanel: advisor)
            : new OverlayWindowState(
                Visible: _overlays,
                TargetPresent: true,
                TargetTitle: TargetTypeName(selected),
                TargetPanel: BuildTargetPanelState(selected),
                MapFooter: mapFooter,
                EnvelopeFooter: envelopeFooter,
                MapPanel: map,
                EnvelopePanel: envelope,
                AdvisorPanel: advisor);
    }

    /// <summary>
    /// Runs <c>ai_advisor_message_dispatch</c>'s law for this frame.
    /// </summary>
    /// <returns>What the YEAGER window shows, or <see cref="AdvisorPanelState.Silent"/>.</returns>
    /// <remarks>
    /// The two clocks are the original's own: <c>g_frame_count_scaled [0xF0D0]</c> for the display
    /// window and the cooldown, <c>g_master_frame_counter [0xF0C8]</c> for the repeat throttle.  The
    /// two conditions are code 16's: <c>g_active_aircraft_idx [0xC31A]</c> — the cockpit art knows
    /// which aeroplane it is — and <c>g_input_state_bitfield [0xF0BC]</c>, which the port models as
    /// the aircraft's own status byte (they are one byte, <c>s_aircraft_master + 0x124</c>).
    /// </remarks>
    private AdvisorPanelState BuildAdvisorPanelState()
    {
        if (_advisor is null)
        {
            return AdvisorPanelState.Silent;
        }

        EnsureAdvisorWired();
        return _advisor.Pump(
            _session.Clock.FrameCountScaled,
            _session.Clock.MasterFrameCounter,
            _cockpitArt?.AircraftIndex ?? 0,
            (int)_session.State.Aircraft.StatusFlags);
    }

    private int _envelopeFooterG = int.MinValue;

    private int _envelopeFooterFeet = int.MinValue;

    private string _envelopeFooter = string.Empty;

    /// <summary>
    /// The ENVELOPE window's footer, re-formatted only when one of its two numbers moves.
    /// </summary>
    /// <param name="loadFactorG">The integer load factor.</param>
    /// <param name="altitudeQ8">The player object's altitude, Q8 feet.</param>
    /// <returns>The string the footer band prints.</returns>
    /// <remarks>
    /// Named residual: the altitude changes on most frames in a climb or a dive, so the cache only
    /// removes the allocation while the aeroplane holds a foot — which is exactly the state where a
    /// steady-state allocation would be a defect.  The other two footers (the map's zoom, the
    /// target panel's five rows) format unconditionally; this one does not.
    /// </remarks>
    private string EnvelopeFooter(int loadFactorG, int altitudeQ8)
    {
        int feet = altitudeQ8 >> 8;
        if (loadFactorG != _envelopeFooterG || feet != _envelopeFooterFeet)
        {
            _envelopeFooterG = loadFactorG;
            _envelopeFooterFeet = feet;
            _envelopeFooter = EnvelopeWindow.Footer(_strings, loadFactorG, altitudeQ8);
        }

        return _envelopeFooter;
    }

    /// <summary>
    /// The ENVELOPE window's state: the curve the current load factor selects, the corner that scales
    /// it, and the aeroplane's own place on it.
    /// </summary>
    /// <param name="view">The frame's sim snapshot.</param>
    /// <returns>What <see cref="OverlayWindowRenderer.RenderEnvelopeContents"/> draws.</returns>
    /// <remarks>
    /// <para>
    /// Every input is the original's own global, read from the integer kernel rather than
    /// re-derived: <c>g_player_gload_int [0xF06F]</c> is <see cref="Aircraft.GLoadInteger"/>,
    /// <c>g_airspeed [0xEF99]</c> is <see cref="Aircraft.CurrentAirspeedFps"/>, the two scale
    /// denominators <c>[0xF0BE]</c>/<c>[0xF0C0]</c> are <see cref="Aircraft.EnvelopeCornerX"/> and
    /// <see cref="Aircraft.EnvelopeCornerY"/> (<c>master[+0x126]</c>/<c>[+0x128]</c>), and the
    /// altitude is the player object's <c>+0x0A</c>.  <see cref="FlightEnvelope.Find"/> IS
    /// <c>fme_record_lookup_by_key @image@0x2AB36</c>, so the window renders the very table the
    /// flight model flies, rather than a second derivation of it.
    /// </para>
    /// <para>
    /// The blink phase is <see cref="_hostFrames"/>, the port's own presented-frame counter, standing
    /// in for the scene render context's <c>[0xC332]</c>.  Named deviation: the original's counter
    /// advances once per SCENE render and the port's once per presented frame — the same cadence in
    /// flight, but under time compression or a paused menu the two would drift.  The blink's period
    /// is what matters and it is one frame either way.
    /// </para>
    /// </remarks>
    private EnvelopePanelState BuildEnvelopePanelState(in FlightSnapshot view)
    {
        Aircraft aircraft = _session.State.Aircraft;
        EnvelopeCurve? curve = _session.Definition.Envelope.Find(aircraft.GLoadInteger);
        return new EnvelopePanelState(
            curve,
            aircraft.EnvelopeCornerX,
            aircraft.EnvelopeCornerY,
            aircraft.CurrentAirspeedFps,
            view.Y,
            unchecked((int)_hostFrames));
    }

    /// <summary>
    /// What the F-4's and the MiG-21's two scopes plot: every live object but the player's, through
    /// the projector both scopes share.
    /// </summary>
    /// <param name="view">The frame's flight snapshot, for the player's own pose.</param>
    /// <returns>The contacts, or null when the sortie has no combat state.</returns>
    /// <remarks>
    /// <para>
    /// There are no scope buffers.  Both scopes run the pixel-obj engine, which projects each
    /// object's world X/Z into the scope with <c>pixel_obj_world_to_screen_project
    /// @image@0x0D3B4</c>: the camera-relative delta is shifted right by 11, rotated by
    /// <c>wrap(−heading)</c> about a pivot measured as (0,0), then shifted by <c>shift − 2</c> and
    /// offset by the region's own screen centre with Y inverted.  This method produces that
    /// rotation's two outputs, in feet, plus the unrotated Manhattan metric the range gate tests.
    /// </para>
    /// <para>
    /// The WALK is still the port's own: the engagement list <c>g_engagement_expiry_list_head
    /// [0xEDAA]</c>, where the original walks the scene render list <c>[0x96]</c> and the 10-slot
    /// pixel-obj table.  The population is the same live combat objects; the port has no scene
    /// render list.  <b>(open)</b> — the original's slot table holds at most TEN contacts at a time
    /// (<c>[0x31E4]</c>, 10 × 8 B) and its REGISTER pass runs one frame in four
    /// (<c>pixel_render_tick @image@0x0D4FE</c>), so a very busy sky may plot fewer blips than this
    /// walk offers.  What would settle it: an emulator run in a crowded engagement dumping
    /// <c>[0x31E4..0x3233]</c> frame by frame beside the screenshot — the harness for it is +
    /// <c>W1_state.py</c>, which already prints the slot table.
    /// </para>
    /// </remarks>
    private IReadOnlyList<ScopeContact>? ScopeContacts(in FlightSnapshot view)
    {
        if (_session.Mission is not { } mission)
        {
            return null;
        }

        PoolArena arena = mission.Combat.Arena;
        ushort player = mission.Combat.Registers.PlayerObjectRef;
        ushort locked = mission.Combat.Registers.Word(LockOnTargetWord);

        // The MAP's altitude nibble compares the contact's Y against the CAMERA OBJECT's
        // ([0x00C0], image@0x0D334), which in flight is the player's own object.
        int playerY = arena.Covers(player, 0x18)
            ? new Core.Sim.Combat.CombatObjectView(arena, player).Position.Y
            : 0;

        // wrap(−heading): the projector negates the camera heading before rotating (image@0x0D40B).
        double theta = -view.Heading.Units * 2.0 * Math.PI / DialNeedle.FullTurnBam;
        double sin = Math.Sin(theta);
        double cos = Math.Cos(theta);

        // Reused frame to frame: no new per-frame allocation in steady state, and the list never
        // outlives the frame that builds it.
        _scopeContacts.Clear();
        foreach (ushort node in arena.WalkList(mission.Combat.Registers.ExpiryListHead))
        {
            ushort objectRef = arena.NodeOwnerObject(node);
            if (objectRef == 0 || objectRef == player || !arena.Covers(objectRef, 0x18))
            {
                continue;
            }

            CombatObjectView pool = new Core.Sim.Combat.CombatObjectView(arena, objectRef);
            CombatPosition position = pool.Position;

            // The arena keeps world units, 1/256 of a foot each, and so does the
            // flight snapshot; the scopes' own scale is in feet. a contact is plotted where the
            // object is DRAWN (PoseSmoother), so a far blip slides instead of holding for ~2 s and
            // jumping.
            double dx = (position.X - view.X) / 256.0;
            double dz = (position.Z - view.Z) / 256.0;
            if (PresentedFeet(objectRef, out double px, out _, out double pz))
            {
                dx = px - (view.X / 256.0);
                dz = pz - (view.Z / 256.0);
            }

            _scopeContacts.Add(new ScopeContact(
                RightFeet: (dx * cos) - (dz * sin),
                AheadFeet: (dx * sin) + (dz * cos),
                ManhattanFeet: Math.Abs(dx) + Math.Abs(dz),
                Threat: ThreatOf(arena, pool, player),
                Locked: objectRef == locked,

                // What the MAP window needs on top of the scopes: the altitude bit (EQUAL takes the
                // high nibble, image@0x0D340's `jb`), the colour class and the wide-dot flag.  The
                // three are free here — the walk is already open.
                AtOrAbovePlayer: position.Y >= playerY,
                MapDot: MapDotOf(arena, pool),
                Wide: WideDot(mission, pool)));
        }

        return _scopeContacts;
    }

    /// <summary>The frame's scope contacts, reused (see <see cref="ScopeContacts"/>).</summary>
    private readonly List<ScopeContact> _scopeContacts = [];

    /// <summary>
    /// Which of the MAP window's four colour bytes an object takes:
    /// <c>pixel_obj_project_forward_view @image@0x0D2C0</c> chooses at REGISTER time and stores the
    /// byte in the slot's <c>+0x04</c>.
    /// </summary>
    /// <param name="arena">The pool arena.</param>
    /// <param name="pool">The object.</param>
    /// <returns>The colour class.</returns>
    /// <remarks>
    /// <para>
    /// The original splits on the object's own <c>0x0800</c> flag, which
    /// <c>pixel_obj_slot_register @image@0x0D590</c> turns into the slot's <c>stipple_type</c>: an
    /// object that CARRIES AN ENGAGEMENT BLOCK takes <c>[0x325C]</c> when that block's <c>+0x05</c>
    /// bit 6 — the HOSTILE bit — is set and <c>[0x325B]</c> when it is clear
    /// (<c>image@0x0D2CD..0x0D2E1</c>); anything else is looked up in the 30-slot combat spawn table
    /// (<c>spawn_slot_lookup_or_echo @image@0x03729</c>) and takes <c>[0x325D]</c> while it is a
    /// guided shot still holding its original target, else <c>[0x325E]</c>.
    /// </para>
    /// <para>
    /// In practice the port's walk only ever produces the first two, and so does the ORIGINAL: a
    /// contact must also carry pool flag <c>0x2000</c> to be registered at all
    /// (<c>image@0x0D493</c>), no shipped class filter word sets that bit
    /// (<c>data/exe/classes.json</c> — every <c>poolFilterWord</c> is 0x0000/0x0200/0x0204/0x0214/
    /// 0x1110), and in twelve paired dumps it was set on every live AI aeroplane and on nothing else
    /// — not on the player, not on scenery, not on a bullet or a missile.  The two spawn colours are
    /// therefore modelled and unreachable; see the report's <b>(open)</b> on the bit's writer.
    /// </para>
    /// </remarks>
    private static MapDotColour MapDotOf(
        Core.Sim.Combat.PoolArena arena, Core.Sim.Combat.CombatObjectView pool)
    {
        if (!pool.CarriesEngagement)
        {
            return MapDotColour.Spawn;
        }

        ushort block = pool.EngagementBlockRef;
        if (!arena.Covers(block, 0x06))
        {
            return MapDotColour.Spawn;
        }

        // image@0x0D2D5 — `test word es:[si+5],0x40`, the HOSTILE bit the Ctrl-Z advisory also asks
        // for (EngagementStateFlags.CountsAsEnemyKill, W2 §8).
        return (arena.Byte((ushort)(block + 0x05)) & 0x40) != 0
            ? MapDotColour.Hostile
            : MapDotColour.Friendly;
    }

    /// <summary>
    /// Whether a contact plots as a TWO-pixel span: <c>prototype[+0x0C] &amp; 0x80</c>
    /// (<c>pixel_obj_slot_register @image@0x0D5C2..0x0D5D0</c>, drawn at <c>image@0x0D698</c>).
    /// </summary>
    /// <param name="mission">The live session, for its static prototype table.</param>
    /// <param name="pool">The object.</param>
    /// <returns>True for the three BOMBER prototypes.</returns>
    /// <remarks>
    /// Bit 7 is set on exactly the B-17E, the B-29 and the B-52D in
    /// <c>data/exe/tables/engagement.json</c> (flags 0x98 / 0xDC / 0xDC), so this is the original's
    /// "a bomber is a fat dot" rule — on the radar and the RWR as well as here, since the flag is
    /// read once per slot for every pixel-obj context.
    /// </remarks>
    private static bool WideDot(
        Core.Sim.Session.MissionSession mission, Core.Sim.Combat.CombatObjectView pool)
    {
        if (!pool.CarriesEngagement)
        {
            return false;
        }

        ushort prototype = pool.EngagementPrototypeRef;
        return prototype != 0 && (mission.Context.StaticData.Word(prototype + 0x0C) & 0x80) != 0;
    }

    /// <summary>
    /// What the RWR shows for one object: <c>pixel_obj_color_select_rwr @image@0x0D374</c>.
    /// </summary>
    /// <param name="arena">The pool arena.</param>
    /// <param name="pool">The object.</param>
    /// <param name="player">The player object's arena offset — the original's <c>[0x00C0]</c>.</param>
    /// <returns>Silent, Steady or Blinking.</returns>
    /// <remarks>
    /// <para>
    /// The gate is bit 3 of the engagement block's flags byte <c>+0x05</c> (<c>image@0x0D387</c>) —
    /// the RADAR-EMITTING bit, which <c>radar_mode_toggle_with_sweep_reset @image@0x0E1DE</c> is what
    /// sets on the player.  A blip blinks only when that emitter's acquisition state is TRACKING
    /// (<c>+0x11 == 3</c>; the 5-entry label table at <c>image@0x3CD50</c> reads SEARCHING /
    /// SEARCHING / (null) / TRACKING / FIRING) <b>and</b> its current target
    /// (<c>+0x1B</c>) is the player.
    /// </para>
    /// <para>
    /// Against the manual ("Radar Warning Receiver"): the shipped code has ONE blink law, on state 3 —
    /// FIRING does not blink, and there is no slow "searching" flash.  Ported as measured.
    /// </para>
    /// <para>
    /// The callback reads <c>[si + 0x18]</c> unconditionally, where the port's
    /// <c>CombatObjectView.EngagementBlockRef</c> also honours the compact-record offset
    /// <c>+0x12</c> (<c>object_pool_get_engagement_fieldoff @image@0x0225A</c>); the difference only
    /// bites for a compact record, which the RWR's own stipple gate rejects anyway.
    /// </para>
    /// </remarks>
    private static ScopeThreat ThreatOf(
        Core.Sim.Combat.PoolArena arena, Core.Sim.Combat.CombatObjectView pool, ushort player)
    {
        ushort block = pool.EngagementBlockRef;
        if (!arena.Covers(block, 0x1D))
        {
            return ScopeThreat.Silent;
        }

        if ((arena.Byte((ushort)(block + 0x05)) & 0x08) == 0)
        {
            return ScopeThreat.Silent;
        }

        bool tracking = arena.Byte((ushort)(block + 0x11)) == 3
            && arena.Word((ushort)(block + 0x1B)) == player;
        return tracking ? ScopeThreat.Blinking : ScopeThreat.Steady;
    }

    /// <summary>
    /// The two BEARING instruments' values: dial slot 4's relative bearing pointer, and dial slot
    /// 3's first needle (the F-86's absolute bearing, everyone else's heading).
    /// </summary>
    /// <param name="view">The frame's flight snapshot.</param>
    /// <returns>The two BAM values <see cref="CockpitState"/> carries.</returns>
    /// <remarks>
    /// <c>object_bearing_to_slot_compute @image@0x08E72</c> takes the XZ bearing from the player to
    /// the current nav slot <c>g_nav_slot_current_index [0xEF92]</c> and, with <c>AL = 1</c>,
    /// subtracts the heading.  W and Shift+W are bound and both instruments follow the live index.
    /// A Test Flight has no waypoints at all, which is the original's own zero case
    /// (<c>image@0x08E89</c> returns 0 when <c>slot_record_get_pos</c> fails).
    /// </remarks>
    private (int CompassBam, int PointerBam) BuildNavBearings(in FlightSnapshot view)
    {
        int heading = view.Heading.Units;
        if (NavWaypoint() is not { } waypoint)
        {
            // image@0x08E89 — no slot, no bearing; the compass card still shows the heading.
            return (heading, 0);
        }

        if (ResolveNavWaypoint(in waypoint) is not { } at)
        {
            return (heading, 0);
        }

        int absolute = NavBearing.Absolute(view.X, view.Z, at.X, at.Z);
        int pointer = NavBearing.Relative(absolute, heading);

        // image@0x0206C — only the F-86 puts the bearing on the compass card's first needle; every
        // other aircraft is fed its own heading there (image@0x0208F).
        bool sabre = _cockpitArt is { Basename: "f86" };
        return (sabre ? absolute : heading, pointer);
    }

    /// <summary>
    /// Where a nav waypoint IS this frame — <c>slot_record_get_pos @image@0x08E0D</c>.
    /// </summary>
    /// <param name="waypoint">The registered waypoint.</param>
    /// <returns>Its position in POSITION units, or null when a tracking waypoint has no live actor.</returns>
    /// <remarks>
    /// A static waypoint is its own recorded point (<c>record[0] == 0</c> → the 12-byte copy); a
    /// TRACKING one walks its up-to-three actor slots and takes the FIRST whose spawn slot still
    /// holds a live pool object (<c>spawn_slot_pos_check_and_copy @image@0x08DA9</c> over
    /// <c>g_named_place_nearptr_table [0xEE5A]</c>).
    /// </remarks>
    private (double X, double Z)? ResolveNavWaypoint(in NavWaypointPlacement waypoint)
    {
        if (_session.Mission is not { } mission)
        {
            return waypoint.Tracks ? null : (waypoint.PosX, waypoint.PosZ);
        }

        // The resolution moved into the Core beside the rest of the nav state, and gained the two
        // per-slot tests the host's copy was missing: the engagement sub-record's type-6 arm and
        // the CRATER sentinel, which is what makes a tracked WRECK stop answering.
        return NavSlots.ResolvePosition(waypoint, mission.Combat.Arena, mission.Combat.Registers)
            is { } at
            ? (at.X, at.Z)
            : null;
    }

    /// <summary>The nav waypoints this sortie registered, for the instrument census.</summary>
    private string DescribeNavWaypoints()
    {
        if (_session.Mission is not { } mission)
        {
            return "none (Test Flight)";
        }

        string current = NavWaypoint() is { } waypoint
            ? $"slot {waypoint.Slot} \"{waypoint.Label}\" "
                + (waypoint.Tracks
                    ? $"tracking actor(s) {string.Join('/', waypoint.TrackedActorSlots)}"
                    : $"at ({waypoint.PosX >> 8}, {waypoint.PosZ >> 8})")
            : $"slot {CurrentNavSlot} NOT REGISTERED";
        return $"{mission.Nav.Count} waypoint(s) [0xEF90], current = {current}";
    }

    /// <summary>What the last frame's map drew.</summary>
    public MapFrameStats LastMap => _lastMap;

    /// <summary>How many frames the map has drawn.</summary>
    public int MapFrames => _mapFrames;

    /// <summary>The map layer's MEAN cost over those frames, in milliseconds.</summary>
    public double MapMeanMilliseconds => _mapFrames > 0 ? _mapMilliseconds / _mapFrames : 0.0;

    /// <summary>Whether the map is the active view.</summary>
    public bool MapUp => _mapMode != MapMode.Off && _view == ViewMode.Map;

    /// <summary>The map's zoom level, or 0 for the port's FIT.</summary>
    public int MapZoomLevel => _mapOptions.ZoomLevel;

    /// <summary>
    /// Everything the map draws this frame, read out of the sim.
    /// </summary>
    /// <param name="view">The frame's flight snapshot.</param>
    /// <remarks>
    /// <para>
    /// The four ORIGINAL layers come from where the original reads them: the scenery from the
    /// theatre's <c>.W</c> placements (the same list the enqueuer
    /// <c>screen_buffer_iter_dispatch @image@0x1E766</c> walks), the waypoints from
    /// <see cref="NavSlots"/> — the port's <c>g_nav_slot_record_array [0xB564]</c> — resolved through
    /// the original's own <c>slot_record_get_pos @image@0x08E0D</c>, and the player from the flight
    /// kernel's own position.
    /// </para>
    /// <para>
    /// The AIRCRAFT and WRECK glyphs are a PORT ADDITION.  The filter is the arena's own answer to
    /// "is this a combatant": flag word bit 11, "carries an engagement block"
    /// (<c>CombatSceneObject.CarriesEngagement</c>), plus the crater class record <c>0x52A2</c> that
    /// <c>engagement_kill_finalize</c> re-classes a dead one into.  So bullets, smoke puffs, chaff,
    /// shadows and the ground-reference spheres are not plotted — none of them carries a block.
    /// </para>
    /// </remarks>
    private MapScene BuildMapScene(in FlightSnapshot view)
    {
        double playerX = view.X / 256.0;
        double playerZ = view.Z / 256.0;

        List<MapActor> actors = new List<MapActor>(32)
        {
            new(playerX, playerZ, view.HeadingDegrees, MapActorKind.Player),
        };

        List<MapWaypoint> waypoints = new List<MapWaypoint>(4);
        double minX = playerX, maxX = playerX, minZ = playerZ, maxZ = playerZ;

        if (_session.Mission is { } mission)
        {
            foreach (CombatSceneObject live in CombatSceneObjects.Live(mission.Combat.Registers, mission.Combat.Arena))
            {
                if (live.IsPlayer)
                {
                    continue;
                }

                bool crater = live.ClassRecordRef == CraterPool.CraterClassRecord;
                if (!crater && !live.CarriesEngagement)
                {
                    continue;
                }

                MapActorKind kind = crater
                    ? MapActorKind.Wreck
                    : live.Hostile ? MapActorKind.Hostile : MapActorKind.Friendly;

                // The map plots the DRAWN pose.
                PoseSmoother.Pose shown = PresentedPose(in live);
                actors.Add(new MapActor(shown.X, shown.Z, shown.HeadingDegrees, kind));

                if (!crater)
                {
                    minX = Math.Min(minX, shown.X);
                    maxX = Math.Max(maxX, shown.X);
                    minZ = Math.Min(minZ, shown.Z);
                    maxZ = Math.Max(maxZ, shown.Z);
                }
            }

            NavSlots nav = mission.Nav;
            for (int slot = 0; slot < nav.Count; slot++)
            {
                if (nav.Record(slot) is not { } waypoint)
                {
                    continue;
                }

                if (NavSlots.ResolvePosition(waypoint, mission.Combat.Arena, mission.Combat.Registers)
                    is not { } at)
                {
                    continue;   // a tracking waypoint whose actors are all wrecks answers nothing
                }

                double wx = at.X / 256.0, wz = at.Z / 256.0;
                waypoints.Add(new MapWaypoint(
                    slot, waypoint.Label, wx, wz, slot == nav.CurrentIndex, waypoint.Tracks));
                minX = Math.Min(minX, wx);
                maxX = Math.Max(maxX, wx);
                minZ = Math.Min(minZ, wz);
                maxZ = Math.Max(maxZ, wz);
            }
        }

        // A fit rectangle with a floor, so a mission whose objects all sit on top of the player does
        // not fit to a point.  10,000 world units ≈ 1.6 nm.
        const double MinimumFitSpan = 10000.0;
        double padX = Math.Max(0.0, (MinimumFitSpan - (maxX - minX)) / 2.0);
        double padZ = Math.Max(0.0, (MinimumFitSpan - (maxZ - minZ)) / 2.0);

        return new MapScene(_snapshot?.World.Statics ?? [], actors, waypoints)
        {
            PlayerX = playerX,
            PlayerZ = playerZ,
            BlinkPhase = (int)(_hostFrames & 3),
            TheatreName = _snapshot?.World.Name ?? string.Empty,
            MissionName = _session.Mission is { } named ? named.Combat.Mission.AssetName : string.Empty,
            FitBounds = (minX - padX, minZ - padZ, maxX + padX, maxZ + padZ),
        };
    }

    /// <summary>The current nav slot's placement, or null when the sortie has none.</summary>
    /// <remarks>
    /// The record now comes out of <see cref="NavSlots"/>, which is the port's
    /// <c>g_nav_slot_record_array [0xB564]</c>: the two containers' registrations are replayed into
    /// it in the order <c>scenario_load_dispatch</c> parses them (<c>image@0x09335</c> then
    /// <c>image@0x0935F</c>), so a slot both containers use keeps the MISSION's, exactly as the
    /// overwriting <c>nav_slot_record_register @image@0x08CE4</c> leaves it. That walked the
    /// loaders in the reverse of the registration order and got the same answer only because no
    /// shipped theatre registers a waypoint.
    /// </remarks>
    private NavWaypointPlacement? NavWaypoint() => _session.Mission?.Nav.Current;

    /// <summary>
    /// <c>g_camera_zoom_level [0xD8A0]</c>'s shipped value — the ZOOM readout's <c>1 &lt;&lt; (n − 7)</c>
    /// is <c>2</c> in every in-flight screenshot, so the port starts there and has no zoom key yet.
    /// </summary>
    private const int DefaultZoomLevel = 8;

    /// <summary>
    /// The NAV key ladder is bound, so the index is the register's own live value; see
    /// <see cref="CurrentNavSlot"/>.
    /// </summary>
    /// <remarks>
    /// <c>nav_slot_state_reset @image@0x08CD6</c> zeroes the index at every scene load and the two
    /// cycle thunks move it (<c>image@0x08D65</c> / <c>image@0x08D8A</c>), so without a mission
    /// session — a Test Flight — it is 0 and there are no records to select anyway.
    /// </remarks>
    private int CurrentNavSlot => _session.Mission?.Nav.CurrentIndex ?? 0;

    /// <summary>
    /// The <c>sun</c>: one white disc 100 world units straight above the camera.
    /// </summary>
    /// <param name="camera">Where the camera is — the port's stand-in for <c>s_view_anchor</c>.</param>
    /// <remarks>
    /// <see cref="SunDisc"/>: <c>scene_or_mission_state_reset @image@0x0C461</c> spawns it and
    /// <c>alloc_slot_a_camera_pos_update @image@0x2DC9D</c> pins it to the anchor's X and Z with <c>Y + 0x6400</c>
    /// position units.  It was decoded and deliberately left out at first; it is drawn now.
    /// </remarks>
    /// <summary><c>--needle-nudge "dx,dy"</c> in design pixels; default (0.5, 0.5) = the pivot pixel's
    /// centre.</summary>
    /// <summary>
    /// Parses <c>--windows</c>: <c>cfg</c> (the shipped <c>yeager.cfg@0x1D</c> byte), <c>off</c>, <c>all</c>, or a
    /// comma-separated list of <c>envelope</c> / <c>target</c> / <c>map</c> / <c>yeager</c>.
    /// </summary>
    /// <param name="word">The option's text, or null.</param>
    /// <param name="fromConfig">What the data tree's config says, for <c>cfg</c>.</param>
    /// <returns>The initial visibility mask.</returns>
    /// <exception cref="FormatException">The list names something else.</exception>
    /// <summary>
    /// Raised when a Shift-1..4 press changes the live overlay byte, with the new mask; the settings store keeps its
    /// four window rows in step with it.  Not raised by <see cref="SetOverlay"/>, which is the rows' own door.
    /// </summary>
    public event Action<CockpitOverlayFlags>? OverlaysChanged;

    /// <summary>The ESC menu's window rows: switch one window on or off.</summary>
    /// <param name="window">The window's bit.</param>
    /// <param name="on">Whether it is up.</param>
    public void SetOverlay(CockpitOverlayFlags window, bool on) =>
        _overlays = on ? _overlays | window : _overlays & ~window;

    /// <summary>The live overlay byte — the port's mirror of <c>[0xF1CB]</c>.</summary>
    public CockpitOverlayFlags Overlays => _overlays;

    /// <summary>
    /// What the NAV readout says this frame, from the same state the two bearing
    /// instruments read.
    /// </summary>
    /// <param name="view">The frame's flight snapshot.</param>
    /// <returns>The readout's state, ready for <see cref="NavReadout.Lines"/>.</returns>
    public NavReadout.State NavReadoutState(in FlightSnapshot view)
    {
        if (_session.Mission is not { } mission)
        {
            return new NavReadout.State(
                _navReadout, false, 0, 0, null, false, null, 0, 0);
        }

        NavSlots nav = mission.Nav;
        NavWaypointPlacement? waypoint = nav.Current;
        double? range = null;
        int relative = 0;
        int absolute = 0;
        if (waypoint is { } placed && ResolveNavWaypoint(in placed) is { } at)
        {
            double dx = at.X - view.X;
            double dz = at.Z - view.Z;
            range = Math.Sqrt((dx * dx) + (dz * dz));
            absolute = NavBearing.Absolute(view.X, view.Z, at.X, at.Z);
            relative = NavBearing.Relative(absolute, view.Heading.Units);
        }

        return new NavReadout.State(
            _navReadout,
            true,
            nav.CurrentIndex,
            nav.Count,
            waypoint?.Label,
            waypoint?.Tracks ?? false,
            range,
            relative,
            absolute);
    }

    /// <summary>
    /// Draws the NAV readout in the WORLD's own rows, clear of the cockpit bitmap.
    /// </summary>
    /// <param name="canvas">The frame's canvas.</param>
    /// <param name="view">The frame's flight snapshot.</param>
    /// <param name="width">The target's width.</param>
    /// <param name="worldRows">How many rows the 3-D world filled — the cockpit starts below them.</param>
    /// <remarks>
    /// It is drawn with the host's own text layer, exactly where H9's fate banner and H25's debrief
    /// are drawn, because it is a DEVIATION and not one of the original's eleven HUD widgets: the
    /// 1991 HUD has no nav block (<see cref="NavReadout"/>).  Top-RIGHT, so it never collides with
    /// the flight-info readout at top-left.
    /// </remarks>
    private void DrawNavReadout(Canvas canvas, in FlightSnapshot view, int width, int worldRows)
    {
        if (_navReadout == NavReadoutMode.Off)
        {
            return;
        }

        // The TARGET window owns the top-right corner, and this block is a PORT ADDITION
        // the original has no room for: while the window is up it would print across the
        // window's own title band.  Suppressed, exactly as the HUD's own THR / VSI pair is
        // (OverlayWindowLayout.CoversHudTopRight).
        if (_overlays.HasFlag(CockpitOverlayFlags.Target) && TargetInRangeViewCheck() != 0)
        {
            return;
        }

        IReadOnlyList<string> lines = NavReadout.Lines(NavReadoutState(in view));
        if (lines.Count == 0)
        {
            return;
        }

        int longest = 0;
        foreach (string line in lines)
        {
            longest = Math.Max(longest, line.Length);
        }

        int x = Math.Max(0, width - (longest * 9) - 16);

        // Below the HUD's own top-right group (the altitude / speed / G stack, which the layout
        // scales with the window), and above the panel: the cockpit owns every row from worldRows
        // down.  A fifth of the world's rows clears the stack at every window size the PoC runs.
        int y = Math.Max(16, worldRows / 5);
        if (y + (lines.Count * 20) > worldRows)
        {
            y = Math.Max(0, worldRows - (lines.Count * 20));
        }

        canvas.SetPenColor(Func.EncodePixelColor(0, 0, 0));
        int shadow = y;
        foreach (string line in lines)
        {
            canvas.DrawString(line, x + 1, shadow + 1, Canvas.Font9X16);
            shadow += 20;
        }

        canvas.SetPenColor(Func.EncodePixelColor(120, 255, 170));
        foreach (string line in lines)
        {
            canvas.DrawString(line, x, y, Canvas.Font9X16);
            y += 20;
        }
    }
}
