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
/// The COCKPIT PANEL: the state the cockpit renderer is handed each frame — the dials, the
/// radar, the target panel and its silhouette, and the captions the panel prints.
/// </summary>
/// <remarks>The reader's map of every one of this class's files is at the top of
/// <c>FlightRasterizer.cs</c>.</remarks>
public sealed partial class FlightRasterizer
{
    /// <summary>The aircraft's cockpit art, for the census printer.</summary>
    public CockpitArt? CockpitArt => _cockpitArt;

    /// <summary>
    /// Whether the aircraft's RADAR is switched on.  The manual's key is <b>R</b> (p.51, "Radar
    /// on/off"), which reaches <c>radar_mode_toggle_with_sweep_reset @image@0x0E1C5</c> from the
    /// ladder at <c>image@0x0126C</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The restart accelerators are unmapped outright: the ESC menu carries Restart Mission, which
    /// the original had no equivalent of, so no accelerator is needed.  <b>R is the radar switch,
    /// full stop</b>, and the ONE restart path is the ESC menu's Restart Mission — plus
    /// <c>--respawn</c> and the <c>restart</c>/<c>respawn</c> script words.
    /// </para>
    /// <para>
    /// MEASURED: a mission starts with the radar OFF — <c>[0x3F06] = 0x0C</c> (bit 5 clear) at 88 M
    /// instructions into two independent runs of the original, and
    /// bit 5 SET is the blip arm.
    /// </para>
    /// </remarks>
    public bool RadarOn { get; private set; }

    /// <summary>How many times the radar key was pressed this sortie — for the readout and tests.</summary>
    public int RadarKeyPresses { get; private set; }

    /// <summary>
    /// Whether this aircraft HAS a radar monitor at all: region 1's rectangle is zero-width for
    /// every aircraft but the F-4E and the MiG-21MF (<c>g_cockpit_region_1_radar_per_ac_rects
    /// [0x3EBE]</c>, and <c>data/exe/tables/cockpit_layout.json</c> region 1).  It is the region
    /// flags' bit 3 — the ENABLED gate the key handler tests before it touches anything (<c>test
    /// byte [0x3F06],8 / je</c> @<c>image@0x0E1D3</c>), so on a P-51 the <b>R</b> key does nothing
    /// at all, not even to the emitter bit.
    /// </summary>
    public bool HasRadar =>
        _cockpitArt is { } art && art.Layout.Regions[1].RectFor(art.AircraftIndex).IsPresent;

    /// <summary>The cockpit and HUD option block (filter, needles, window, stroke, style).</summary>
    public CockpitOptions CockpitOptions
    {
        get => _cockpitOptions;
        set => _cockpitOptions = value;
    }

    /// <summary>
    /// <c>--cockpit-view screen</c>: draw the 3-D world into the WHOLE window and paint the panel
    /// over it, instead of into the aircraft's viewport rows alone.
    /// </summary>
    /// <remarks>
    /// The original composes the world into the viewport rectangle and puts the window's centre at
    /// <c>(y_min + y_max) &gt;&gt; 1</c> (<c>image@0x118A4</c>), which is why the P-51's horizon sits
    /// on row 67 of 200 rather than on row 100 — that is <c>false</c> here and the default.
    /// <c>true</c> is a PORT ADDITION: the same lens, the same panel, but the world's centre at the
    /// screen's centre, which is what a modern flight sim does and what makes the view through the
    /// canopy feel less like looking down.
    /// </remarks>
    public bool CockpitViewFullScreen { get; set; }

    /// <summary>Whether this aircraft's cockpit art loaded at all (a null pair flies bare).</summary>
    public bool HasCockpitArt => _cockpitArt is not null;

    /// <summary>
    /// Whether the strip shows its "a help feature has been used this mission" square (manual
    /// p.24).  The port exposes none of the Help menu's six cheats, so the only thing that can set
    /// it is one of the port's OWN labelled cheats.
    /// </summary>
    public bool HelpFeatureUsed =>
        _foeHitPoints > 0
        || _session.CheatDeathAtSeconds >= 0
        || _session.CheatDebriefAtSeconds >= 0
        || _session.CheatMissionKills > 0;

    /// <summary>What the cockpit layer drew on the last frame.</summary>
    public CockpitFrameStats LastCockpit => _lastCockpit;

    /// <summary>Whether the cockpit is currently switched on (Backspace toggles it).</summary>
    public bool CockpitEnabled => _cockpitOptions.Enabled;

    /// <summary>
    /// Everything the cockpit layer reads about this frame, out of the integer kernels.
    /// </summary>
    /// <param name="view">The frame's flight snapshot.</param>
    /// <remarks>
    /// The value sources are <c>cockpit_dial_state_compute_all @image@0x01FCF</c>'s own, phase by
    /// phase; the four status bits are the ones the region state functions read out of
    /// <c>g_input_state_bitfield [0xF0BC]</c>, which the port carries as <c>master[+0x124]</c>.
    /// Outside a mission the combat-only values are zero, which is what the registers themselves
    /// hold before a mission arms them.
    /// </remarks>
    /// <param name="view">The frame's flight snapshot.</param>
    /// <param name="effectiveView">
    /// The view the camera is ACTUALLY in this frame, which is the death camera's substitution
    /// after a fate trigger and the player's own choice otherwise.
    /// </param>
    private CockpitState BuildCockpitState(in FlightSnapshot view, ViewMode effectiveView)
    {
        MissionSession? mission = _session.Mission;
        CombatRegisters? registers = mission?.Combat.Registers;
        // H10a fix pass — the weapon+ammo readout.  In a mission the live registers own it; in a
        // Test Flight nothing has published a loadout, so the port reads the same 18-byte record
        // player_weapon_loadout_publish reads (exe/weapons.json), which is what makes the F-4's
        // readout say "M61 750" instead of nothing.
        int slot = registers?.Byte(PlayerCombatOffsets.SelectedWeaponSlot) ?? -1;
        CockpitWeaponReadout readout = _weapons is null
            ? CockpitWeaponReadout.Empty(_strings)
            : CockpitWeaponReadout.For(_strings, _weapons, _session.AircraftBasename, slot);
        int rounds = registers is null
            ? readout.Rounds
            : registers.Word(PlayerCombatOffsets.WeaponSlotAmmo + (Math.Clamp(slot, 0, 3) * 2));

        // The COUNTERMEASURE STOCK.  In a mission the live registers own it, because
        // WeaponLoadout.Publish already writes [0xED32]/[0xED33] from the same 18-byte record
        // (player_weapon_loadout_publish @image@0x2762D); a Test Flight has no registers, so the
        // port reads the record straight out of exe/weapons.json — which is what makes the F-4's
        // and the MiG-21's counters read 15 / 15 instead of 00 / 00.
        (int Chaff, int Flare) stock = registers is not null
            ? (Chaff: (int)registers.Byte(0xED32), Flare: (int)registers.Byte(0xED33))
            : _weapons is null
                ? (Chaff: 0, Flare: 0)
                : CockpitWeaponReadout.CountermeasureStock(_weapons, _session.AircraftBasename);

        bool crashed = registers is not null
            ? registers.Byte(PlayerEject.PlayerNotFlyingFlag) != 0
            : _session.Fate is { Enabled: true, Passive: false, Phase: not PlayerFatePhase.Flying };

        (int CompassBam, int PointerBam) navBearing = BuildNavBearings(in view);

        return new CockpitState(
            ViewId: (int)effectiveView,
            Crashed: crashed,
            StatusFlags: view.StatusFlags,
            PitchBam: view.Pitch.Units,
            RollBam: view.Roll.Units,
            HeadingBam: view.Heading.Units,
            AltitudeFeet: view.AltitudeFeet,
            VerticalSpeedMidWord: _session.State.Window.VerticalSpeedMidWord,
            AirspeedFps: view.AirspeedFps,
            FuelMidWord: unchecked((short)(_session.State.Aircraft.Fuel >> 8)),

            // Slot 5 is the THROTTLE gauge.  image@0x020F6 feeds it [0xF035], which image@0x0C728
            // prints as "THR: %3d%%" — and the port's own kernel keeps that quantity as
            // master[+0xA0] >> 8.  It used to be fed the combat register [0xF035], which nothing
            // in the port writes, so the gauge never moved.
            ThrottlePercent: view.ThrottlePercent,
            CompassBam: navBearing.CompassBam,
            BearingPointerBam: navBearing.PointerBam,
            // The three PERCENT METERS.  In a mission the live registers own them (the sustain tick
            // drains A and B, image@0x0FCD8..0x0FD45); a Test Flight has no registers, and the port
            // used to feed 0 — three needles pinned at the wrong end. engagement_state_init
            // @image@0x0F6EE/0x0F6F1 sets [0xF1D8] and [0xF1D9] to 0x64 at EVERY scenario load and
            // [0xF1DA] starts at 0 (image@0x0F715), which is exactly what
            // a captured frame of the original three gauges read (1,045 / 1,045 / 1,440 BAM).
            PercentMeterA: registers?.Byte(PlayerCombatOffsets.MeterAirframe) ?? FreshMeterPercent,
            PercentMeterB: registers?.Byte(PlayerCombatOffsets.MeterAirframe + 1) ?? FreshMeterPercent,
            PercentMeterC: registers?.Byte(PlayerCombatOffsets.MeterAirframe + 2) ?? 0,
            WeaponRounds: rounds,
            WeaponName: readout.Name,
            ChaffCount: stock.Chaff,
            FlareCount: stock.Flare,
            Radar: BuildRadarState(in view));
    }

    /// <summary>
    /// The frame's RADAR state: the switch, the sweep clock and the contacts the two scopes may
    /// plot.
    /// </summary>
    /// <param name="view">The frame's flight snapshot, for the player's own pose.</param>
    /// <returns>What the two scope regions draw from.</returns>
    /// <remarks>
    /// The scale shift is <c>g_stipple_scale_shift [0x3234]</c>: landed W3 —
    /// <see cref="MapScaleShift"/> is the live word, and because ONE shift serves all three
    /// pixel-obj contexts the map's zoom keys rescale the radar and the RWR with it.  That is the
    /// original's own coupling: <c>per_frame_object_pixel_setup @image@0x0D741</c> reads the same
    /// word whichever context armed it.
    /// </remarks>
    private RadarState BuildRadarState(in FlightSnapshot view)
    {
        // Kept for the MAP window, which is built later in the same frame (the windows are drawn
        // after the cockpit and the HUD) and plots this very list.
        _lastRadar = new RadarState(
            On: RadarOn,
            PlayerEmitting: RadarOn,
            SweepPhase: RadarScope.SweepPhase(
                _session.Clock.FrameTimeAccumulator, _radarSweepDeadline),
            ScaleShift: MapScaleShift,
            RwrSampleCounter: unchecked((int)(_session.Clock.FrameSteps & 0xFF)),
            Contacts: ScopeContacts(in view));
        return _lastRadar;
    }

    /// <summary>
    /// <c>target_in_range_view_check @image@0x0A557</c>, the TARGET window's SECOND gate: the
    /// <c>lcall 0x108e:0x9c77</c> at <c>image@0x0EAB1</c> whose <c>AL</c> must be non-zero.
    /// </summary>
    /// <returns>The selected object's arena offset when the window may draw, else 0.</returns>
    /// <remarks>
    /// <para>
    /// Decoded W2 — four conditions, none of them angular: a target is SELECTED
    /// (<c>g_hud_selected_object_key [0x00BE]!= 0</c>, <c>image@0x0A55F</c>); its pool record's
    /// <c>+0x02</c> bit 0 is set (<c>image@0x0A581</c>); it is not the player's own object (<c>cmp
    /// [0xBE],[0x00C0]</c>, <c>image@0x0A58F</c>); and the MANHATTAN sum of the three position HIGH
    /// words is under <c>0x493</c> (<c>image@0x0A5C3</c>).
    /// </para>
    /// <para>
    /// And the word is <c>[0x00BE]</c>, not the <c>[0x00BC]</c> W0 used: the two are different
    /// globals, and <c>[0x00BC]</c> is cleared between frames while <c>[0x00BE]</c> holds the
    /// selection (measured — <c>b_f4_150.dg</c> has <c>[0xBE] = 0x5399</c> with <c>[0xBC] = 0</c>,
    /// and its frame still shows the window).
    /// </para>
    /// <para>
    /// The high-word Manhattan sum is in units of 65 536 world units = 256 feet, so <c>0x493</c>
    /// is about 300 000 feet of summed axis distance — a generous gate, but a real one: it is what
    /// makes the window vanish for a target on the far side of the theatre.
    /// </para>
    /// </remarks>
    private ushort TargetInRangeViewCheck()
    {
        if (_session.Mission is not { } mission)
        {
            return 0;
        }

        CombatRegisters registers = mission.Combat.Registers;
        ushort selected = registers.Word(SelectedObjectWord);           // image@0x0A55F
        ushort player = registers.PlayerObjectRef;
        PoolArena arena = mission.Combat.Arena;
        if (selected == 0 || selected == player                          // image@0x0A58F
            || !arena.Covers(selected, 0x18) || !arena.Covers(player, 0x18))
        {
            return 0;
        }

        CombatObjectView target = new CombatObjectView(arena, selected);
        if ((target.Flags & 0x0001) == 0)                                // image@0x0A581
        {
            return 0;
        }

        CombatPosition a = target.Position;
        CombatPosition b = new CombatObjectView(arena, player).Position;

        // image@0x0A591..0x0A5C1 — three 16-bit subtractions of the position HIGH words, each made
        // absolute by the cwd/xor/sub idiom, summed in a 16-bit register.
        static int HiDelta(int lhs, int rhs) =>
            Math.Abs((int)unchecked((short)((lhs >> 16) - (rhs >> 16))));

        int manhattan = unchecked((ushort)(
            HiDelta(a.Z, b.Z) + HiDelta(a.Y, b.Y) + HiDelta(a.X, b.X)));
        return manhattan >= TargetViewRangeLimit ? (ushort)0 : selected; // image@0x0A5C3
    }

    /// <summary>
    /// Paints the TARGET window's LIVE SILHOUETTE into its content rectangle.
    /// </summary>
    /// <param name="frame">The whole window's pixels.</param>
    /// <param name="width">Its width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="scale">The design → window mapping.</param>
    /// <remarks>
    /// See <see cref="TargetSilhouetteView"/> for the camera and the lens.  The mesh, the pose and
    /// the class's own camera distance all come from the live pool object, so a MiG-21 shows a
    /// MiG-21 at the aspect the player is actually looking at it from.
    /// </remarks>
    private void DrawTargetSilhouette(Span<uint> frame, int width, int height, CockpitScale scale)
    {
        if (_session.Mission is not { } mission || _palette is null || _classMeshes.Count == 0)
        {
            return;
        }

        PoolArena arena = mission.Combat.Arena;
        CombatRegisters registers = mission.Combat.Registers;
        ushort selected = registers.Word(SelectedObjectWord);
        ushort player = registers.PlayerObjectRef;
        if (selected == 0 || !arena.Covers(selected, 0x18) || !arena.Covers(player, 0x18))
        {
            return;
        }

        // The pose, the gear state and the hidden leaves are the SAME ones AddCombatObjects hands
        // the world renderer, so the panel and the world agree about the aeroplane in them.
        foreach (CombatSceneObject live in CombatSceneObjects.Live(registers, arena, ownAircraftView: false))
        {
            if (live.ObjectRef != selected
                || !_classMeshes.TryGetValue(live.ClassRecordRef, out MeshModel? mesh))
            {
                continue;
            }

            // The registry slot's +0x0D byte, in 16-foot steps — radar_closest_approach_compute
            // @image@0x0A4D3 (see TargetPanel.SilhouetteCameraDistance). read off the MESH, not the
            // 23-record class registry.  For some aircraft — the P-47 and the Yak-9 — the target
            // view would otherwise come out black: both are runtime registry slots outside
            // exe/classes.json, and their +0x0D byte is 0x19 like every other fighter's, so the
            // byte has to be read off the mesh.  The mesh carries the slot's byte and the extent arm's inputs,
            // so no class is ever answered 0.
            int distance = TargetPanel.SilhouetteCameraDistance(mesh);
            if (distance <= 0)
            {
                return;
            }

            // UNITS.  CombatSceneObjects.Live hands the renderer FEET (the arena's Q8 world units
            // divided by 256), and the class record's distance is in the arena's own units, so the
            // camera's distance and the player's position are converted to match.
            CombatPosition eyePool = new CombatObjectView(arena, player).Position;
            Vec3 eye = new Vec3(
                eyePool.X / (double)TargetPanel.WorldUnitsPerFoot,
                eyePool.Y / (double)TargetPanel.WorldUnitsPerFoot,
                eyePool.Z / (double)TargetPanel.WorldUnitsPerFoot);
            _targetSilhouette ??= new TargetSilhouetteView(_palette, _colors, _sceneOptions);
            _targetSilhouette.Render(
                frame,
                width,
                height,
                scale,
                mesh,
                new SilhouettePose(
                    live.X, live.Y, live.Z,
                    live.HeadingDegrees, live.PitchDegrees, live.RollDegrees),
                eye,
                distance / (double)TargetPanel.WorldUnitsPerFoot,
                live.GearDown ? GearDeployAngle.Extended : GearDeployAngle.Retracted,

                // No afterburner plume: the original raises the plume tag only while rendering the
                // object the camera follows (image@0x2D8F9), which is never the target.
                FlameLeaves.Hidden(mesh.Basename, lit: false));
            return;
        }
    }

    /// <summary>Degrees per engine angle unit — <c>360 / 0xB40</c>.</summary>
    private const double DegreesPerAngleUnit = 360.0 / TargetPanel.FullTurnUnits;

    /// <summary>
    /// The TARGET window's five content lines, read from the selected object.
    /// </summary>
    /// <param name="selected">The object <see cref="TargetInRangeViewCheck"/> passed.</param>
    /// <returns>What <c>OverlayWindowRenderer.RenderTargetContents</c> draws.</returns>
    /// <remarks>
    /// The order and the gates are <c>cockpit_radar_scope_draw</c>'s own
    /// (<c>image@0x0A7E1..0x0A94C</c>); see <see cref="TargetPanel"/> for the anchors.  Every string
    /// is composed here and every label comes out of the data tree, never a retyped literal.
    /// </remarks>
    private TargetPanelState BuildTargetPanelState(ushort selected)
    {
        if (_session.Mission is not { } mission)
        {
            return default;
        }

        PoolArena arena = mission.Combat.Arena;
        CombatRegisters registers = mission.Combat.Registers;
        CombatObjectView target = new CombatObjectView(arena, selected);
        ushort block = target.EngagementBlockRef;
        ushort prototype = target.EngagementPrototypeRef;
        ICombatStaticData data = mission.Context.StaticData;

        // proto[+0x0C]: bit3 = engage-capable (the label gate), bits0-1 = VM-abort (the speed gate).
        ushort prototypeFlags = prototype == 0 ? (ushort)0 : data.Word(prototype + 0x0C);
        bool engageCapable = (prototypeFlags & 0x08) != 0;              // image@0x0A7E4
        byte phase = arena.Byte((ushort)(block + 0x0D));                // image@0x0A81E
        byte lockState = arena.Byte((ushort)(block + 0x11));            // image@0x0A815

        string manoeuvre = engageCapable
            ? _panelLabels?.Manoeuvre(arena.Byte((ushort)(block + 0x2C))) ?? string.Empty
            : string.Empty;                                             // image@0x0A7F1

        // image@0x0A829 — a second gate on the PHASE byte's own flag before the lock-state label.
        string lockLabel = engageCapable && _panelLabels is { } labels && labels.PhaseShowsLockState(phase)
            ? labels.LockState(lockState)
            : string.Empty;

        CombatObjectView player = new CombatObjectView(arena, registers.PlayerObjectRef);
        string clock = TargetPanel.ClockBearing(
            _strings, player.Position, player.Heading, target.Position); // image@0x0A8CE

        // image@0x0A84D / image@0x0A853 — the speed line needs BOTH gates.  The number is followed by
        //   the panel's own speed suffix, DGROUP [0x0FFA] (P4-R2: from the tree).
        string speed = (prototypeFlags & 0x03) == 0 && phase != 0
            ? TargetPanel.MilesPerHour(unchecked((short)arena.Word((ushort)(block + 0x26))))
                .ToString(CultureInfo.InvariantCulture) + _strings.TargetSpeedSuffix
            : string.Empty;

        long feet = TargetPanel.SortedApproxDistance(target.Position, player.Position)
            / TargetPanel.WorldUnitsPerFoot;                            // image@0x0A8F2/0x0A8F7
        string range = string.Create(CultureInfo.InvariantCulture, $"{feet}'");

        return new TargetPanelState(
            Manoeuvre: manoeuvre,
            LockState: lockLabel,
            Clock: clock,
            Speed: speed,
            Range: range,
            RangeCentred: speed.Length == 0,                             // image@0x0A802 / 0x0A859
            Caption: EngagementCaption(mission, selected, block),        // image@0x0EE6C
            WeaponStation: arena.Byte((ushort)(block + 0x05)) & 3);      // image@0x0EEA2
    }

    /// <summary>
    /// The ENGAGEMENT CAPTION: the string the engagement block's <c>+0x1E</c> points at.
    /// </summary>
    /// <param name="arena">The pool arena.</param>
    /// <param name="block">The target's engagement block.</param>
    /// <returns>The caption, or empty when the block carries no pointer.</returns>
    /// <remarks>
    /// <para>
    /// <c>cockpit_target_info_panel_draw</c> reads <c>es:[block+0x1E]</c> as a POOL near pointer,
    /// copies the NUL-terminated string it addresses far-to-near with <c>strcpy_far_to_near</c> and
    /// draws it centred at <c>g_cockpit_hud_row + 0x3E</c> (<c>image@0x0EE6C..0x0EE91</c>).
    /// </para>
    /// <para>
    /// MEASURED: it is the mission's own <c>pilot_name</c> attribute (id 151).  On the Dragon's Jaw
    /// sortie, with the aeroplane still on the runway and a wingman of the player's own flight
    /// selected, the block reads <c>+0x1E = 0x5399</c> and the pool holds <c>"Wingman\0"</c> there —
    /// and the frame's footer band prints <c>Wingman</c> at design column 264
    /// (a captured frame of the original, taken with a paired DGROUP
    /// and pool dump by <c>W2_probe.sh</c> case D).
    /// </para>
    /// <para>
    /// <b>(open) — the PORT's arena never carries one.</b> <c>ScenarioObjectLoader</c> writes
    /// <c>blockRef + 0x1E = 0</c> outright (<c>image@0x0A1C9</c>, its own comment already reads "name
    /// ptr") because the port has no string heap in the arena to intern
    /// <see cref="MissionObject.PilotName"/> into.  This reader is the original's own path and lights
    /// up the moment the arena carries one; giving the loader somewhere to put the string is a
    /// <c>Core/Sim/Session</c> change, not a host one.
    /// </para>
    /// </remarks>
    private string EngagementCaption(MissionSession mission, ushort objectRef, ushort block)
    {
        string interned = InternedCaption(mission.Combat.Arena, block);
        return interned.Length > 0 ? interned : AuthoredPilotName(mission, objectRef);
    }

    /// <summary>
    /// The AUTHORED half of the caption: the mission's own <c>pilot_name</c> for the actor slot this
    /// object was spawned into.
    /// </summary>
    /// <param name="mission">The live mission.</param>
    /// <param name="objectRef">The object's arena offset.</param>
    /// <returns>The name, or empty when the mission authored none for that slot.</returns>
    /// <remarks>
    /// <para>
    /// The original interns the string into the object pool and points at it from the block's
    /// <c>+0x1E</c>; the port's <c>ScenarioObjectLoader</c> has no string heap there and writes 0
    /// (<c>image@0x0A1C9</c>), so the panel reads the SAME string from the same place the loader read
    /// it: the mission document.  <c>g_named_place_nearptr_table [0xEE5A]</c> is the u16[13] the
    /// loader fills with <c>slot → objectRef</c> (<c>image@0x08EF4</c>,
    /// <c>ScenarioObjectLoader</c>:625), so the object's ACTOR SLOT is a lookup and the slot's
    /// authored object is <see cref="MissionObject.ActorSlot"/>.
    /// </para>
    /// <para>
    /// MEASURED against the original: on <b>Bolo</b> (scenario slot 35, <c>bolo.s</c>) the selected
    /// wingman's block carries <c>+0x1E = 0x5399</c> and the pool holds <c>"Wingman\0"</c> there,
    /// beside the mission's own interned <c>"Home Base"</c>, <c>"Wingman"</c> and its radio lines.
    /// <c>data/missions/bolo.json</c> authors that very <c>pilot_name</c>; <c>dragon.json</c>
    /// authors none, which is why every Dragon's Jaw frame has an empty footer.
    /// </para>
    /// </remarks>
    private static string AuthoredPilotName(MissionSession mission, ushort objectRef)
    {
        CombatRegisters registers = mission.Combat.Registers;
        int slot = -1;
        for (int i = 0; i < ScenarioObjectLoader.ActorSlotCount; i++)
        {
            if (registers.Word(ScenarioObjectLoader.NamedPlaceNearPtrTable + (i * 2)) == objectRef)
            {
                slot = i;
                break;
            }
        }

        if (slot < 0)
        {
            return string.Empty;
        }

        foreach (MissionObject authored in mission.Combat.Mission.Objects)
        {
            if (authored.ActorSlot == slot && authored.PilotName is { Length: > 0 } name)
            {
                return name;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// The caption the ARENA carries, if it carries one — the original's own path
    /// (<c>image@0x0EE6C</c>).
    /// </summary>
    /// <param name="arena">The pool arena.</param>
    /// <param name="block">The target's engagement block.</param>
    /// <returns>The string, or empty.</returns>
    private static string InternedCaption(PoolArena arena, ushort block)
    {
        ushort at = arena.Word((ushort)(block + 0x1E));
        if (at == 0 || !arena.Covers(at, 1))
        {
            return string.Empty;
        }

        Span<char> text = stackalloc char[CaptionMaxLength];
        int length = 0;
        while (length < text.Length && arena.Covers((ushort)(at + length), 1))
        {
            byte c = arena.Byte((ushort)(at + length));
            if (c == 0)
            {
                break;
            }

            // A pointer that happens to address object bytes is not a caption: the original would
            // print the rubbish, the port declines to.
            if (c is < 0x20 or > 0x7E)
            {
                return string.Empty;
            }

            text[length++] = (char)c;
        }

        return length == 0 ? string.Empty : new string(text[..length]);
    }

    /// <summary>How long a caption the panel's own 92-byte buffer can hold (<c>image@0x0EDAE</c>).</summary>
    private const int CaptionMaxLength = 0x5C - 1;

    /// <summary>
    /// The target's TYPE, from its engagement prototype's <c>+0x04</c> name pointer
    /// (<c>image@0x0EDEC</c>).
    /// </summary>
    /// <param name="selected">The selected object.</param>
    /// <returns>The name, or empty when the tree does not carry one.</returns>
    private string TargetTypeName(ushort selected)
    {
        if (_session.Mission is not { } mission)
        {
            return string.Empty;
        }

        ushort prototype = new CombatObjectView(mission.Combat.Arena, selected)
            .EngagementPrototypeRef;
        return prototype == 0 ? string.Empty : _panelLabels?.TypeName(prototype) ?? string.Empty;
    }

    /// <summary><c>g_hud_selected_object_key [0x00BE]</c> — what the target keys select.</summary>
    private const int SelectedObjectWord = 0x00BE;

    /// <summary>The Manhattan high-word limit the view check applies: <c>0x493</c>.</summary>
    private const int TargetViewRangeLimit = 0x0493;

    /// <summary>How many frames painted the cockpit layer.</summary>
    public int CockpitFrames => _cockpitFrames;
}
