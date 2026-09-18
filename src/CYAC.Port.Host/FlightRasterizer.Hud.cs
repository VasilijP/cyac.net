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
/// The HUD: the text widgets and the markers drawn over the world — the designators, the
/// gun box and pipper, the advisory line, the hit percentage and the gunsight.
/// </summary>
/// <remarks>The reader's map of every one of this class's files is at the top of
/// <c>FlightRasterizer.cs</c>.</remarks>
public sealed partial class FlightRasterizer
{
    private readonly HudMessageTable? _hudMessages;

    private readonly bool _hudDemo;

    /// <summary>
    /// Whether the HUD text overlay is switched on (<c>g_flight_info_visible [0xB0]</c>); and
    /// settable, because the Graphics menu's "Flight Info" row flips it.
    /// </summary>
    public bool FlightInfoVisible
    {
        get => _flightInfoVisible;
        set => _flightInfoVisible = value;
    }

    /// <summary>What the HUD overlay drew on the last frame.</summary>
    public HudFrameStats LastHud => _lastHud;

    /// <summary>
    /// Everything the HUD overlay reads about this frame, out of the integer kernels.
    /// </summary>
    /// <param name="view">The frame's flight snapshot.</param>
    /// <param name="camera">The frame's camera, for the two markers the overlay projects.</param>
    /// <param name="cockpit">The cockpit layer's state, whose gate also chooses the layout block.</param>
    /// <param name="scale">The design-space mapping this frame is drawn with.</param>
    /// <param name="width">The window's width.</param>
    /// <param name="worldRows">How many rows the 3-D world filled.</param>
    /// <remarks>
    /// The renderer is handed integers and already-projected points.  Which target and which weapon are
    /// INTEGER decisions taken by the integer kernel; WHERE the marker lands is the port's own float
    /// projection, so the box sits on the aircraft as the port actually drew it (the V5 detail-level law,
    /// the same split H8's gunsight used).
    /// </remarks>
    private HudState BuildHudState(
        in FlightSnapshot view,
        in CameraPose camera,
        in CockpitState cockpit,
        in CockpitScale scale,
        int width,
        int worldRows,
        ViewMode effectiveView)
    {
        CockpitArt art = _cockpitArt!;
        bool cockpitDrawn = CockpitRenderer.CockpitDrawn(_cockpitOptions, cockpit);

        // The mask's forward-view term is the view the camera IS in, not the key the player last
        // pressed: after a fate trigger the death camera is outside the aeroplane, so the 0x400
        // marker arm (the waterline, the pipper and the target box) must go with it.
        bool forward = (int)effectiveView == CockpitLayout.CockpitPaintedViewId;
        HudLayout layout = cockpitDrawn ? art.Layout.HudFor(art.AircraftIndex) : art.Layout.HudDefault;

        // hud_sample_history_advance @image@0x0CF5D — the ring the gunsight leads with.
        uint accumulator = _session.Clock.FrameTimeAccumulator;
        _hudState.Sample(accumulator, view.HeadingDegrees, view.PitchDegrees);

        MissionSession? mission = _session.Mission;
        CombatRegisters? registers = mission?.Combat.Registers;
        _hudState.ObserveDamage(
            registers is null ? -1 : registers.Word(PlayerDamageAccumulator),
            layout,
            unchecked((ulong)_session.StepsRun));

        int slot = registers?.Byte(PlayerCombatOffsets.SelectedWeaponSlot) ?? -1;
        CockpitWeaponReadout readout = _weapons is null
            ? CockpitWeaponReadout.Empty(_strings)
            : CockpitWeaponReadout.For(_strings, _weapons, _session.AircraftBasename, slot);
        int rounds = registers is null
            ? readout.Rounds
            : registers.Word(PlayerCombatOffsets.WeaponSlotAmmo + (Math.Clamp(slot, 0, 3) * 2));

        (HudMarker? box, HudMarker? pipper) = BuildMarkers(
            camera, scale, art, forward, cockpitDrawn, width, worldRows);

        return new HudState(
            Suppressed: false,
            ForwardView: forward,
            FlightInfoVisible: _flightInfoVisible,
            CockpitDrawn: cockpitDrawn,
            AltitudeFeet: view.AltitudeFeet,
            AirspeedFps: view.AirspeedFps,
            GLoadQ8: _session.State.Aircraft.GLoadQ8,
            VerticalSpeed: _session.State.Window.VerticalSpeed,
            ThrottlePercent: view.ThrottlePercent,
            HeadingBam: view.Heading.Units,
            StatusFlags: view.StatusFlags,
            LandingReady: LandingReady(view),
            // The HUD's own "ZOOM:n" widget reads g_map_zoom_level [0xD8A0], the very word the
            // map's + / − keys move (gauge_dial_zoom_draw @image@0x0D85C prints 1 << ([0xD8A0] −
            // 7)).  So the widget follows the map, exactly as it does in the original — the port's
            // FIT, which the original has no level for, keeps the default.
            ZoomLevel: _mapOptions.ZoomLevel > 0 ? _mapOptions.ZoomLevel : DefaultZoomLevel,
            TimeCompressionShift: _session.Clock.CompressionShift,
            WeaponName: readout.PaddedName,
            WeaponRounds: rounds,

            // Settled — the atlas shows it: `G23:200 (81%)` in cfg 0x01.  The suffix is gated on
            // `g_scene_misc_word_BC [0x00BC]!= 0` (cmp word [0xbc],0 / je @image@0x0C8A5), which is
            // why 20_mig21_cfg02_notarget.png prints `G23:200` with no percentage while the
            // designator beside it already reads 81%.
            HitPercent: HudHitPercent(),
            Message: HudMessage(accumulator),
            MessageX: -1,
            TargetMarker: box,
            Pipper: pipper,
            HitMarkers: _hudState.HitMarkers,

            // The in-world designator labels, one per qualifying engagement object.
            Designators: BuildDesignators(
                camera, scale, art, width, worldRows, cockpitDrawn, guidedBoxDrawn: box is not null),

            // An overlay window does not overdraw the HUD's corners, it takes them over: the two
            // corner blocks are simply not drawn while a window owns the slot (measured across the
            // six [0xF1CB] variants, HudMask.WithoutCoveredBlocks). …but only when the block is
            // actually UNDER the band.  An earlier pass measured the MiG-21, whose HUD anchors sit inside the
            // window band's rows, and read the result as an unconditional suppression.  The F-4E's
            // anchors are far below it, and a captured frame of the original — cfg
            // 0x0F, all three windows up — prints ZOOM:1 / M61:750 (78%) and THR / VSI exactly as
            // it does with no window at all.  So the rule is the ROW overlap, which is the same
            // thing for the MiG and the right thing for the F-4.
            OverlayCoversTopLeft: OverlayWindowLayout.CoversHudTopLeft(_overlays)
                && HudBlockUnderTheWindowBand(layout.WaypointAnchor.Y),
            OverlayCoversTopRight: OverlayWindowLayout.CoversHudTopRight(_overlays)
                && HudBlockUnderTheWindowBand(layout.VsiAnchor.Y)
                && TargetInRangeViewCheck() != 0);
    }

    /// <summary>
    /// Whether a HUD corner block's rows fall inside the overlay windows' band.
    /// </summary>
    /// <param name="anchorRow">The block's own layout anchor row.</param>
    /// <returns>True when a window that owns the corner would cover the block.</returns>
    /// <remarks>
    /// Both corner blocks are drawn UPWARD from their anchor — <c>anchor − 0x11 … anchor</c> for the
    /// left group (TIME / ZOOM / weapon) and <c>anchor − 0x0B … anchor</c> for the right (THR / VSI)
    /// — so the anchor is the block's LAST row, and a block whose anchor is inside the band lies
    /// wholly inside it.  Measured over the six shipped layouts
    /// (<c>data/exe/tables/cockpit_layout.json</c>): the MiG-21's anchors are row 61 and the
    /// FW-190's 69, both inside the band's 19..88, and the P-51's 104, the F-86's and MiG-15's and
    /// the F-4's 114 are all below it — which is exactly the split the atlas shows between
    /// <c>21_mig21_cfg0F_target.png</c> (no corner text) and <c>30_f4_110.png</c> (both corners
    /// printed under all three windows).  Only the rows are tested: whenever
    /// <see cref="OverlayWindowLayout.CoversHudTopLeft"/> is true the LEFT slot is occupied (the
    /// cursor rule puts the map there and moves the envelope to 128), so a row overlap is a real
    /// overlap.
    /// </remarks>
    private static bool HudBlockUnderTheWindowBand(int anchorRow) =>
        anchorRow >= OverlayWindowLayout.Top
        && anchorRow <= OverlayWindowLayout.Top + OverlayWindowLayout.Height - 1;

    /// <summary>How many times the two advisory keys were pressed this sortie.</summary>
    public int AdvisoryRequests { get; private set; }

    /// <summary>
    /// <c>nearest_engaged_obj_advisory_show @image@0x23E62</c>: the line <b>Ctrl-Z</b> and
    /// <b>Ctrl-A</b> put on the message strip.
    /// </summary>
    /// <param name="bogeys">
    /// True for <c>Ctrl-Z</c> (the arm pushes <c>0x40</c>), false for <c>Ctrl-A</c> (it pushes 0).
    /// </param>
    /// <returns>The line, e.g. <c>NEAREST BOGEY IS AT 4 O'CLOCK HI</c>.</returns>
    /// <remarks>
    /// <para>
    /// The original walks the scene render list and keeps the object whose 2-D Manhattan distance
    /// from the player is least (<c>obj2d_manhattan_dist @image@0x2440E</c>, compared on the HIGH
    /// word at <c>image@0x23EEF</c>), among those that pass <c>object_is_engaged_check</c>, carry
    /// the requested value of engagement-block bit 6 (<c>and ax,0x40 / cmp ax,[bp+6]</c>
    /// @<c>image@0x23EA6</c>) and are not in phase 6 (<c>image@0x23EB2</c>).  It then formats
    /// <c>"NEAREST BOGEY IS AT "</c> / <c>"NEAREST FRIENDLY IS AT "</c> plus
    /// <c>oclock_bearing_format</c>'s own string, or one of the two <c>"CAN'T FIND ANY …"</c>
    /// literals when nothing matched.
    /// </para>
    /// <para>
    /// Bit 6 of the engagement block's <c>+0x05</c> is a SECOND reader for the flag
    /// <see cref="EngagementStateFlags.CountsAsEnemyKill"/> already names — it is the HOSTILE bit,
    /// and this key is what makes that visible to the player.
    /// </para>
    /// <para>
    /// Byte-inert: the advisory reads the pool and posts a host notice; it writes nothing.
    /// </para>
    /// </remarks>
    private string NearestObjectAdvisory(bool bogeys)
    {
        string prefix = bogeys
            ? _panelLabels?.NearestBogeyPrefix ?? string.Empty
            : _panelLabels?.NearestFriendlyPrefix ?? string.Empty;
        string missing = bogeys
            ? _panelLabels?.NoBogeys ?? string.Empty
            : _panelLabels?.NoFriendlies ?? string.Empty;

        if (_session.Mission is not { } mission)
        {
            return missing;
        }

        PoolArena arena = mission.Combat.Arena;
        CombatRegisters registers = mission.Combat.Registers;
        ushort player = registers.PlayerObjectRef;
        ICombatStaticData data = mission.Context.StaticData;
        if (player == 0 || !arena.Covers(player, 0x18))
        {
            return missing;
        }

        CombatObjectView eye = new CombatObjectView(arena, player);
        ushort nearest = 0;
        long best = long.MaxValue;

        foreach (CombatSceneObject live in CombatSceneObjects.Live(registers, arena, ownAircraftView: false))
        {
            if (live.IsPlayer || !arena.Covers(live.ObjectRef, 0x18))
            {
                continue;
            }

            // object_is_engaged_check @image@0x23F9C — active, not the engagement-slot selector, and
            // its class prototype is engage-capable with the VM-abort bits clear.
            CombatObjectView pool = new CombatObjectView(arena, live.ObjectRef);
            if ((pool.Flags & 0x0803) != 0x0801)
            {
                continue;
            }

            ushort prototype = pool.EngagementPrototypeRef;
            ushort prototypeFlags = prototype == 0 ? (ushort)0 : data.Word(prototype + 0x0C);
            if ((prototypeFlags & 0x08) == 0 || (prototypeFlags & 0x03) != 0)
            {
                continue;
            }

            ushort block = pool.EngagementBlockRef;
            bool hostile = (arena.Byte((ushort)(block + 0x05))
                & (int)EngagementStateFlags.CountsAsEnemyKill) != 0;    // image@0x23EA6
            if (hostile != bogeys || arena.Byte((ushort)(block + 0x0D)) == 6)   // image@0x23EB2
            {
                continue;
            }

            CombatPosition here = pool.Position;
            long score = Math.Abs((long)here.X - eye.Position.X)
                + Math.Abs((long)here.Z - eye.Position.Z);              // image@0x2440E
            if (score < best)
            {
                best = score;
                nearest = live.ObjectRef;
            }
        }

        return nearest == 0
            ? missing
            : prefix + TargetPanel.ClockBearing(
                _strings, eye.Position, eye.Heading, new CombatObjectView(arena, nearest).Position);
    }

    /// <summary>
    /// The HUD's own <c>(nn%)</c> suffix: <c>engagement_hit_pct_compute</c> for
    /// <c>g_scene_misc_word_BC [0x00BC]</c>.
    /// </summary>
    /// <returns>The percentage, or −1 to print no suffix.</returns>
    /// <remarks>
    /// <c>image@0x0C8A5..0x0C8D6</c>: the suffix is appended only while <c>[0x00BC]</c> is non-zero
    /// AND the weapon's own name resolved, and it is formatted with <c>" (%d%%)"</c>
    /// (<c>DGROUP[0x3199]</c>, <c>image@0x3EEF9</c>).
    /// </remarks>
    private int HudHitPercent()
    {
        if (_session.Mission is not { } mission)
        {
            return -1;
        }

        ushort engaged = mission.Combat.Registers.Word(LockOnTargetWord);   // image@0x0C8A5
        return engaged == 0 ? -1 : HitPercentFor(mission, engaged);
    }

    /// <summary>
    /// <c>engagement_hit_pct_compute @image@0x031CB</c> for one pool object, gathered from the
    /// live register file and the static data.
    /// </summary>
    /// <param name="mission">The live mission.</param>
    /// <param name="objectRef">The target's arena offset.</param>
    /// <returns>0..100, or −1 when it cannot be computed.</returns>
    /// <remarks>
    /// Byte-inert by construction — <see cref="TargetHitChance"/> only READS.  See its remarks for
    /// which two arms of the sim-side body are omitted and why neither can apply here.
    /// </remarks>
    private int HitPercentFor(MissionSession mission, ushort objectRef)
    {
        PoolArena arena = mission.Combat.Arena;
        CombatRegisters registers = mission.Combat.Registers;
        ushort weaponClass = registers.Word(PlayerCombatOffsets.SelectedWeaponClass);
        ushort player = registers.PlayerObjectRef;
        if (weaponClass == 0 || player == 0
            || !arena.Covers(objectRef, 0x18) || !arena.Covers(player, 0x18))
        {
            return -1;
        }

        CombatObjectView target = new CombatObjectView(arena, objectRef);
        ushort prototype = target.EngagementPrototypeRef;
        if (prototype == 0)
        {
            return -1;
        }

        CombatObjectView owner = new CombatObjectView(arena, player);
        ICombatStaticData data = mission.Context.StaticData;
        byte kind = data.Byte(weaponClass);
        return TargetHitChance.Percent(new TargetHitChanceInputs(
            Owner: owner.Position,
            OwnerElevation: owner.Elevation,
            Target: target.Position,
            PrototypeDivisor: data.Byte(prototype + 6 + kind),
            WeaponRange: data.Byte(weaponClass + 0x05),
            WeaponWeight: data.Byte(weaponClass + 0x01),
            WeaponKind: kind,
            MinimumRange: data.Byte(weaponClass + 0x04),
            Guided: (data.Byte(weaponClass + 0x24) & (int)WeaponClassFlags.Guided) != 0,
            CheatBias: registers.Byte(EngagementFireAuthority.CheatFlagDgroupOffset) != 0));
    }

    /// <summary>
    /// The IN-WORLD TARGET DESIGNATOR labels: <c>hud_engagement_label_draw @image@0x0CDB4</c>'s walk,
    /// with the port's own projector.
    /// </summary>
    /// <param name="camera">The frame's camera.</param>
    /// <param name="scale">The design mapping.</param>
    /// <param name="art">The aircraft's art.</param>
    /// <param name="width">The window's width.</param>
    /// <param name="worldRows">How many rows the world filled.</param>
    /// <param name="cockpitDrawn">Whether the panel is painted.</param>
    /// <param name="guidedBoxDrawn">
    /// Whether the GUIDED weapon's own 15 × 15 box was built this frame — the original's
    /// <c>[bp-6]</c> flag (<c>image@0x0CA34</c>), which suppresses the selected object's 9 × 9 one.
    /// </param>
    /// <returns>This frame's labels; the list is reused between frames.</returns>
    /// <remarks>
    /// <para>
    /// The original walks the engagement list and applies four guards per node
    /// (<c>image@0x0CDD1..0x0CE2B</c>): the object's pool flags must be
    /// <c>(flags &amp; 0x901) == 0x901</c>; the node's <c>+0x0A</c> must be non-negative; its
    /// engagement prototype's <c>+0x0C</c> must have bit 3 set and bits 0-1 clear; and its projected
    /// screen point must be inside the clip rectangle.  The port applies the object-side guards and
    /// lets <see cref="Project"/> answer the last one.
    /// </para>
    /// <para>
    /// The WALK is the port's own live-object walk rather than the original's engagement list — the
    /// same population, and the same per-object guards.  <b>(open)</b>: an object that is live but off
    /// the engagement list would be labelled here and not in the original; nothing in the captured
    /// frames distinguishes the two, and the reference frames only ever have one label up.
    /// </para>
    /// </remarks>
    private IReadOnlyList<HudDesignator>? BuildDesignators(
        in CameraPose camera,
        in CockpitScale scale,
        CockpitArt art,
        int width,
        int worldRows,
        bool cockpitDrawn,
        bool guidedBoxDrawn)
    {
        _designators.Clear();
        if (_session.Mission is not { } mission || _panelLabels is null)
        {
            return null;
        }

        PoolArena arena = mission.Combat.Arena;
        CombatRegisters registers = mission.Combat.Registers;
        ushort player = registers.PlayerObjectRef;
        ushort selected = registers.Word(SelectedObjectWord);
        ICombatStaticData data = mission.Context.StaticData;

        foreach (CombatSceneObject live in CombatSceneObjects.Live(registers, arena, ownAircraftView: false))
        {
            if (live.IsPlayer || !arena.Covers(live.ObjectRef, 0x18))
            {
                continue;
            }

            CombatObjectView pool = new CombatObjectView(arena, live.ObjectRef);
            if ((pool.Flags & 0x0901) != 0x0901)                        // image@0x0CDD1
            {
                continue;
            }

            ushort prototype = pool.EngagementPrototypeRef;
            ushort prototypeFlags = prototype == 0 ? (ushort)0 : data.Word(prototype + 0x0C);
            if ((prototypeFlags & 0x08) == 0 || (prototypeFlags & 0x03) != 0)
            {
                continue;                                               // image@0x0CDF5 / 0x0CDFB
            }

            // The label rides the DRAWN pose (PoseSmoother), the same point the mesh is at, so it
            // cannot sit on a held sim position while the aeroplane slides.
            PoseSmoother.Pose shown = PresentedPose(in live);
            Vec3 world = new Vec3(shown.X, shown.Y, shown.Z);
            if (!Project(
                    camera, scale, world - camera.Eye, width, worldRows, art, cockpitDrawn,
                    out double sx, out double sy))
            {
                continue;                                               // image@0x0CE0D..0x0CE2B
            }

            _designators.Add(new HudDesignator(
                sx,
                sy,
                _panelLabels.TypeName(prototype),
                HitPercentFor(mission, live.ObjectRef),

                // image@0x0CE32 — block[+0x1B] is who this object is engaging.
                arena.Word((ushort)(pool.EngagementBlockRef + 0x1B)) == player,

                // The SELECTED object also gets the yellow 9 × 9 box.  The call site's gate
                // (image@0x0CB44..0x0CB68): the 0x100 widget arm, the GUIDED box not drawn this frame
                // ([bp-6]), and either the flight-info flag or the object's own flag-word bit 3.  The
                // guided arm is HudState.TargetMarker, so `box is null` is [bp-6] == 0.
                Selected: live.ObjectRef == selected && guidedBoxDrawn == false
                    && (_flightInfoVisible || (pool.Flags & 0x0008) != 0)));
        }

        return _designators;
    }

    /// <summary>
    /// The two markers of the <c>0x400</c> arm: the guided-weapon box, or the gun pipper.
    /// </summary>
    /// <param name="camera">The frame's camera.</param>
    /// <param name="scale">The design-space mapping.</param>
    /// <param name="art">The aircraft's art, for its index and its viewport.</param>
    /// <param name="forward">Whether this is the forward view.</param>
    /// <param name="cockpitDrawn">Whether the panel is painted, which bounds the world's rows.</param>
    /// <param name="width">The window's width.</param>
    /// <param name="worldRows">How many rows the world filled.</param>
    /// <remarks>
    /// <c>image@0x0C9FD</c> gates the whole block on <c>g_active_aircraft_idx &gt;= 2</c> — the four
    /// JETS — so the P-51 and the FW-190 get the waterline alone, and
    /// <c>test byte [[0xED1E] + 0x24], 0x10</c> then splits guided (the box) from gun (the pipper).
    /// The pipper's lag is the projectile's TIME OF FLIGHT when there is a target and
    /// <see cref="HudOverlayState.DefaultLagTicks"/> otherwise (<c>image@0x0D128</c> /
    /// <c>image@0x0D137</c>).
    /// </remarks>
    private (HudMarker? Box, HudMarker? Pipper) BuildMarkers(
        in CameraPose camera,
        in CockpitScale scale,
        CockpitArt art,
        bool forward,
        bool cockpitDrawn,
        int width,
        int worldRows)
    {
        if (!forward || art.AircraftIndex < JetAircraftIndex)
        {
            return (null, null);
        }

        // --hud-demo: both markers at fixed offsets from the boresight, for photography.
        if (_hudDemo)
        {
            PanelRect centre = art.Viewport;
            double cx = (centre.X + centre.Right - 1) / 2.0;
            double cy = (centre.Y + centre.Bottom - 1) / 2.0;
            return (new HudMarker(cx + 40, cy - 18, true, 0), new HudMarker(cx - 34, cy + 14, false, 4));
        }

        MissionSession? mission = _session.Mission;
        bool guided = false;
        int reach = 0;
        int speed = 0;
        ushort locked = 0;
        if (mission is { } live)
        {
            CombatRegisters registers = live.Combat.Registers;
            locked = registers.Word(LockOnTargetWord);
            ushort weaponClass = registers.Word(PlayerCombatOffsets.SelectedWeaponClass);
            if (weaponClass != 0)
            {
                ICombatStaticData data = live.Context.StaticData;
                guided = (data.Byte(weaponClass + 0x24) & (int)WeaponClassFlags.Guided) != 0;
                reach = data.Byte(weaponClass + 0x05) << 16;
                speed = unchecked((short)data.Word(weaponClass + 0x14));
            }
        }

        if (guided)
        {
            // The BOX rides the locked target, projected the way the port drew it.
            if (locked == 0 || mission is null || !mission.Combat.Arena.Covers(locked, 0x18))
            {
                return (null, null);
            }

            CombatPosition target = new CombatObjectView(mission.Combat.Arena, locked).Position;
            Vec3 world = new Vec3(target.X / 256.0, target.Y / 256.0, target.Z / 256.0);
            if (!Project(
                    camera, scale, world - camera.Eye, width, worldRows, art, cockpitDrawn,
                    out double bx, out double by))
            {
                return (null, null);
            }

            // image@0x0CA61..0x0CA71 — the diamond needs rounds in the slot AND a clear sight line.
            bool confirmed = mission.RoundsRemaining > 0;
            return (new HudMarker(bx, by, confirmed, 0), null);
        }

        // The PIPPER: a ray at the attitude `lag` accumulator units ago.
        int lag = HudOverlayState.DefaultLagTicks;
        long distance = -1;
        if (locked != 0 && mission is not null && mission.Combat.Arena.Covers(locked, 0x18))
        {
            // The original's OWN metric, decoded: abs3d_dist_approx_sorted @image@0x0A95B (see
            // TargetPanel.SortedApproxDistance), verified against the atlas to the unit.
            CombatPosition pool = new CombatObjectView(mission.Combat.Arena, locked).Position;
            WorldObject player = _session.State.Player;
            distance = TargetPanel.SortedApproxDistance(
                pool, new CombatPosition((int)player.X, (int)player.Y, (int)player.Z));
            if (speed > 0)
            {
                lag = (int)Math.Clamp(Math.Min(distance, reach) / speed, 1, 240);
            }
        }

        uint accumulator = _session.Clock.FrameTimeAccumulator;
        if (!_hudState.AttitudeAt(accumulator, lag, out double heading, out double pitch))
        {
            return (null, null);
        }

        Basis3 basis = Basis3.FromEuler(heading * Math.PI / 180.0, pitch * Math.PI / 180.0, 0.0);
        if (!Project(
                camera, scale, basis.Forward * PipperRayLength, width, worldRows, art, cockpitDrawn,
                out double px, out double py))
        {
            return (null, null);
        }

        return (null, new HudMarker(px, py, false, LeadDots(reach, distance)));
    }

    /// <summary>
    /// How many of the pipper's four range dots are lit — <c>image@0x0D22F..0x0D2B6</c>.
    /// </summary>
    /// <param name="reach">The weapon class's reach, <c>desc[+0x05] &lt;&lt; 16</c>.</param>
    /// <param name="distance">The distance to the target, or −1 when there is none.</param>
    /// <remarks>
    /// The original clamps the distance to the reach first and then compares <c>reach &gt;&gt;
    /// 8</c> against <c>dist &gt;&gt; 8</c> at 1, ¾, ½ and ¼ of the reach, so an out-of-range
    /// target lights nothing and a very close one lights all four. *(closed by an earlier pass —
    /// <c>abs3d_dist_approx_sorted</c> IS decoded: the three absolute axis deltas sorted ascending,
    /// then <c>max + ((mid × 5) &gt;&gt; 4) + (min &gt;&gt; 2)</c>.
    /// <see cref="TargetPanel.SortedApproxDistance"/> is that body, verified against the original's
    /// own frame to the unit, and the ladder now measures with it.)*
    /// </remarks>
    private static int LeadDots(int reach, long distance)
    {
        if (reach <= 0 || distance < 0)
        {
            return 0;
        }

        long d = Math.Min(distance, reach) >> 8;
        long r = reach >> 8;
        if (r <= d)
        {
            return 0;
        }

        int dots = 1;
        if (r * 3 / 4 > d)
        {
            dots++;
        }

        if (r / 2 > d)
        {
            dots++;
        }

        if (r / 4 > d)
        {
            dots++;
        }

        return dots;
    }

    /// <summary>Projects a camera-relative offset into DESIGN space, inside the world's rows.</summary>
    /// <param name="camera">The camera.</param>
    /// <param name="scale">The design-space mapping.</param>
    /// <param name="offset">The offset from the eye.</param>
    /// <param name="width">The window's width.</param>
    /// <param name="worldRows">How many rows the world filled.</param>
    /// <param name="art">The aircraft's art, for the viewport the marker must stay inside.</param>
    /// <param name="cockpitDrawn">Whether the viewport is the aircraft's or the whole screen.</param>
    /// <param name="designX">The design column.</param>
    /// <param name="designY">The design row.</param>
    /// <returns>False when the point is behind the eye or outside the viewport.</returns>
    private bool Project(
        in CameraPose camera,
        in CockpitScale scale,
        Vec3 offset,
        int width,
        int worldRows,
        CockpitArt art,
        bool cockpitDrawn,
        out double designX,
        out double designY)
    {
        designX = 0.0;
        designY = 0.0;
        Vec3 local = camera.Basis.ToLocal(offset);
        if (local.Z <= 1.0)
        {
            return false;
        }

        ScreenPoint point = Projection.ToScreen(
            local, _lens.FocalLengthPixels(width), width / 2.0, worldRows / 2.0);
        designX = scale.ToDesignX(point.X);
        designY = scale.ToDesignY(point.Y);
        // The marker must stay inside the same rectangle the world is clipped to — the aircraft's
        // viewport while the panel is painted, the whole screen when it is not (image@0x01641).
        int bottom = cockpitDrawn ? art.Viewport.Bottom : CockpitLayout.DesignHeight;
        return designX >= 0
            && designX < CockpitLayout.DesignWidth
            && designY >= 0
            && designY < bottom;
    }

    /// <summary>
    /// The message strip's text — what the kernel last posted, while it is still up.
    /// </summary>
    /// <param name="accumulator">The frame-time accumulator.</param>
    /// <remarks>
    /// <para>
    /// The producer is the integer kernel itself: <c>IKernelWorld.PostHudWarning</c> is the
    /// original's <c>show_cockpit_text_string @image@0x0CC5B</c> call, and
    /// <c>hud_message_append @image@0x0CBFC</c> stamps the expiry
    /// <c>g_frame_time_accum + (6 &lt;&lt; 8)</c> — <see cref="HudMessageTable.MessageTicks"/>.  The
    /// text comes out of <c>exe/strings.json</c>, not out of the image copy.
    /// </para>
    /// <para>
    /// <b>(open)</b>: the original's <c>[0xF17E]</c> sticky-message latch (which makes one message
    /// refuse to be replaced) and the two built-in stall lines <c>"APPROACHING STALL"</c> /
    /// <c>"STALL"</c> that <c>draw_string</c> falls back on when <c>[0xF0BA]</c> is 2 or 3 are not
    /// ported: the port has no producer for <c>[0xF0BA]</c> yet.
    /// </para>
    /// </remarks>
    private string? HudMessage(uint accumulator)
    {
        // The HOST's own notice outranks both producers while it is up.  There is exactly one,
        // "MISSION RESTARTED", and it is posted at the instant the session it would otherwise have
        // to share a clock with is replaced; a fresh mission's opening radio call arrives a moment
        // later and takes the strip back when the notice expires.
        if (_hostNotice is { } notice && _hostNoticeSeconds > 0.0)
        {
            return notice;
        }

        // The MISSION MODULE's radio calls share the strip with the flight kernel's warnings,
        // because in the original they share `show_cockpit_text_string @image@0x0CC5B` and its one
        // 0x46-byte buffer at [0xBA38].  The module's is preferred while it is up: both stamp the
        // same 6-unit expiry, and a "Bogeys at eight o'clock!" that a stall warning erased would be
        // a message the player never sees. and so does the NAV label, through the very same call
        // (nav_waypoint_show_current @image@0x08D5C is an lcall to show_cockpit_text_string). The
        // original has ONE buffer, so the LAST writer owns the strip; both stamp the same 6-unit
        // expiry, so the greater expiry IS the later post.
        if (_session.Mission is { } live)
        {
            string? nav = live.NavMessageAt(accumulator);
            string? radio = live.RadioMessageAt(accumulator);
            if (nav is not null && radio is not null)
            {
                return unchecked((int)(live.NavMessageExpiry - live.RadioMessageExpiry)) >= 0
                    ? nav
                    : radio;
            }

            if ((nav ?? radio) is { } single)
            {
                return single;
            }
        }

        long posts = _session.World.HudPosts;
        if (posts != _seenHudPosts)
        {
            _seenHudPosts = posts;
            _hudMessage = _hudMessages?.Text(_session.World.LastHudMessageId);
            _hudMessageExpiry = unchecked(accumulator + HudMessageTable.MessageTicks);
        }

        if (_hudMessage is null)
        {
            return null;
        }

        return unchecked((int)(accumulator - _hudMessageExpiry)) < 0 ? _hudMessage : null;
    }

    /// <summary>
    /// Whether the VSI line earns its <c>"*** VSI:"</c> landing cue.
    /// </summary>
    /// <param name="view">The frame's snapshot.</param>
    /// <remarks>
    /// <c>image@0x0C76C..0x0C78A</c>: the player object's <c>+0x0C</c> — the HIGH word of its Q8
    /// altitude — must be at most 2, i.e. the aircraft is below <c>0x30000 &gt;&gt; 8 = 768</c> feet;
    /// <c>g_input_state_bitfield [0xF0BC]</c> bit 2 (the gear) must be set; and
    /// <c>crash_conditions_valid @image@0x2C25C</c> must return 0, i.e. a touchdown right now would
    /// be survivable.
    /// </remarks>
    private bool LandingReady(in FlightSnapshot view) =>
        (view.Y >> 16) <= 2
        && (view.StatusFlags & 0x04) != 0
        && !DamageCheckStage.CrashConditionsReject(
            _session.State.Aircraft, _session.State.Window.VerticalSpeedMidWord);

    /// <summary>
    /// <c>g_engagement_rounds_accum [0xF1CC]</c> — the total damage the player has taken, which
    /// <c>weapon_fire_combat_loop @image@0x0F748</c> adds to on every hit.
    /// </summary>
    private const int PlayerDamageAccumulator = 0xF1CC;

    /// <summary>
    /// The first aircraft index whose cockpit gets a target marker at all — <c>image@0x0C9FD</c>'s
    /// <c>cmp word [0xC31A], 2</c>.  The P-51 and the FW-190 fly with the waterline alone.
    /// </summary>
    private const int JetAircraftIndex = 2;

    /// <summary>How far along the historical attitude the pipper's ray is projected.</summary>
    /// <remarks>
    /// The original builds a 256,000-unit ray and shifts each component right by 8
    /// (<c>image@0x0D17B</c> then three <c>0x1000:0x720</c> calls), i.e. 1,000 units; a pure
    /// direction projects to the same point at any positive length, so the port uses a round number
    /// in its own world units.
    /// </remarks>
    private const double PipperRayLength = 1000.0;

    /// <summary>
    /// What <c>engagement_state_init @image@0x0F6EE</c> seeds the first two percent meters with at
    /// every scenario load: <c>0x64</c> = 100 %.
    /// </summary>
    private const int FreshMeterPercent = 0x64;

    /// <summary>
    /// The GUNSIGHT: the boresight reticle, the lock-on box on the kernel's own locked target, and
    /// its range.
    /// </summary>
    /// <param name="canvas">The frame's canvas.</param>
    /// <param name="camera">This frame's camera.</param>
    /// <param name="width">The target's width.</param>
    /// <param name="height">Its height.</param>
    /// <remarks>
    /// <para>
    /// The BORESIGHT is a fixed cross at the viewport centre — where <c>+Z</c> in the camera's frame
    /// projects, i.e. straight down the gun line.  The original's <c>hud_per_frame_draw
    /// @image@0x0C5A7</c> also draws a LEAD reticle a thousand units along the ray the aircraft was
    /// pointing <c>lock_countdown</c> frames ago ; that is the vector HUD's
    /// job and a later step.
    /// </para>
    /// <para>
    /// The BOX is drawn on <c>g_lockon_target [0x00BC]</c> — the object the verified
    /// <c>PlayerTargetLock</c> chose this frame — projected with the PORT's own float camera, so the
    /// box lands where the aircraft is actually drawn at host resolution while the kernel's own
    /// decision stays integer arithmetic on the original's 320×200 screen (the V5 detail-level law).
    /// </para>
    /// </remarks>
    private void DrawGunsight(Canvas canvas, in CameraPose camera, int width, int height)
    {
        if (_session.Mission is not { } mission)
        {
            return;
        }

        int cx = width / 2;
        int cy = height / 2;
        int arm = Math.Max(6, height / 90);
        int gap = arm / 2;

        canvas.SetPenColor(Func.EncodePixelColor(40, 255, 90));
        canvas.MoveTo(cx - arm - gap, cy).LineTo(cx - gap, cy);
        canvas.MoveTo(cx + gap, cy).LineTo(cx + arm + gap, cy);
        canvas.MoveTo(cx, cy - arm - gap).LineTo(cx, cy - gap);
        canvas.MoveTo(cx, cy + gap).LineTo(cx, cy + arm + gap);

        ushort locked = mission.Combat.Registers.Word(0x00BC);
        if (locked == 0 || !mission.Combat.Arena.Covers(locked, 0x18))
        {
            return;
        }

        CombatPosition target = new CombatObjectView(mission.Combat.Arena, locked).Position;
        Vec3 world = new Vec3(target.X / 256.0, target.Y / 256.0, target.Z / 256.0);
        Vec3 local = camera.Basis.ToLocal(world - camera.Eye);
        if (local.Z <= 1.0)
        {
            return;
        }

        ScreenPoint point = Projection.ToScreen(local, _lens.FocalLengthPixels(width), cx, cy);
        int half = Math.Max(6, (int)(height / 24.0 / Math.Max(1.0, local.Length / 400.0)));
        int bx = (int)point.X;
        int by = (int)point.Y;
        canvas.SetPenColor(Func.EncodePixelColor(255, 210, 40));
        canvas.MoveTo(bx - half, by - half)
            .LineTo(bx + half, by - half)
            .LineTo(bx + half, by + half)
            .LineTo(bx - half, by + half)
            .LineTo(bx - half, by - half);
        canvas.DrawString(
            string.Create(CultureInfo.InvariantCulture, $"{local.Length:F0}"),
            bx - half,
            by + half + 4,
            Canvas.Font9X16);
    }
}
