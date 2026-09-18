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
/// The CAMERA and the SCENE: which view the frame is drawn from, what the camera follows,
/// and everything the display list is filled with — the player, the other aircraft, the
/// ground lattice, the cloud deck, the sun, the rounds and the smoke.
/// </summary>
/// <remarks>The reader's map of every one of this class's files is at the top of
/// <c>FlightRasterizer.cs</c>.</remarks>
public sealed partial class FlightRasterizer
{
    private readonly MeshModel? _cloudMesh;

    private readonly CloudDeckLattice? _cloudDeck;

    private readonly MeshModel? _groundGridMesh;

    private readonly MeshModel? _sunMesh;

    /// <summary>
    /// The grey ordinary round's streak mesh (<see cref="GunneryRounds.RoundMesh"/>). Built
    /// unconditionally now, because the dialog can switch the trail on mid-flight.
    /// </summary>
    private MeshModel? _roundMesh;

    /// <summary>The grey round's authored streak length and palette index, so either can be rebuilt.</summary>
    private double _roundLengthFeet;

    private int _roundColorIndex;

    /// <summary>The impact flash's mesh: the deferred-effect pool's own class, <c>explosio</c> [0x59EE].</summary>
    private readonly MeshModel? _flashMesh;

    /// <summary>
    /// The <c>smoke</c> class's own mesh (<see cref="SmokePuffTable.SmokeClassRecord"/>), which the
    /// presentation layer's puffs are drawn as.  Resolved unconditionally now, because the dialog can
    /// switch the layer on mid-flight.
    /// </summary>
    private readonly MeshModel? _smokeMesh;

    /// <summary><c>--smoke-ramp stretched</c>, for a smoke layer built after start-up.</summary>
    private readonly bool _smokeStretchRamp;

    /// <summary><c>--round-ground-hits</c>, for a gunnery trail built after start-up.</summary>
    private readonly bool _roundGroundHits;

    /// <summary>
    /// VECTOR MARKINGS — builds a mesh library for a markings mode, so the Port Settings row
    /// can swap the AIRCRAFT models live.  Set by the host after construction.
    /// </summary>
    public Func<MarkingsMode, MeshLibrary>? MeshFactory { get; set; }

    /// <summary>The markings mode the models were last built with.</summary>
    public MarkingsMode MarkingsMode { get; private set; } = MarkingsMode.Shipped;

    /// <summary>
    /// VECTOR MARKINGS — rebuilds every class mesh and the player's own mesh for a markings mode
    /// and swaps them under the live instances.  Scenery, effects and the ground keep the models
    /// they have (they carry no markings); a pool object looks its class up per frame, so the
    /// next frame draws the new models.
    /// </summary>
    /// <param name="mode">The mode.</param>
    public void ApplyMarkings(MarkingsMode mode)
    {
        if (MeshFactory is null || mode == MarkingsMode)
        {
            return;
        }

        MeshLibrary library = MeshFactory(mode);
        Dictionary<ushort, MeshModel> swapped = new Dictionary<ushort, MeshModel>(_classMeshes.Count);
        foreach ((ushort key, MeshModel mesh) in _classMeshes)
        {
            swapped[key] = library.TryGet(mesh.Basename) ?? mesh;
        }

        _classMeshes = swapped;
        if (_playerMesh is { } player)
        {
            _playerMesh = library.TryGet(player.Basename) ?? player;
        }

        MarkingsMode = mode;
    }

    /// <summary>Records the mode the models were built with at start (no rebuild).</summary>
    /// <param name="mode">The mode.</param>
    public void ApplyMarkingsInitial(MarkingsMode mode) => MarkingsMode = mode;

    private readonly int _cloudTiles;

    private readonly int _groundBallTiles;

    private readonly List<string> _combatObjectLines = [];

    /// <summary><c>--no-ejection-parts</c>: ignore the ejection meshes' leaf tags.</summary>
    private readonly bool _noEjectionParts;

    /// <summary><c>--smooth-objects</c>: interpolate coarse-stepped pool objects.</summary>
    private readonly bool _smoothObjects;

    /// <summary>
    /// What the last frame's BACKGROUND FUNCTION would paint over the whole target.
    /// </summary>
    /// <remarks>
    /// With <see cref="SceneFrameStats.BackgroundIfPainted"/>: since R3b the background is the
    /// fragment resolve's TERMINAL FUNCTION and no pass paints it, so these are not counts of
    /// painted pixels — they describe the horizon's geometry.  Most of the "sky" pixels a frame
    /// reports here are covered by scenery.
    /// </remarks>
    public HorizonFrameStats LastBackground => LastScene.BackgroundIfPainted;

    /// <summary>The most recent scene census.</summary>
    public SceneFrameStats LastScene { get; private set; }

    /// <summary>Which camera the last frame was drawn from.</summary>
    public ViewMode View => _view;

    /// <summary>Selects a camera, exactly as a view key does.</summary>
    /// <param name="view">The view.</param>
    public void SelectView(ViewMode view)
    {
        if (view == ViewMode.Map && _mapMode == MapMode.Off)
        {
            return;
        }

        if (view == ViewMode.Map && _view != ViewMode.Map)
        {
            _viewBeforeMap = _view;
        }

        _view = view;
    }

    // ---------------------------------------------------------------------------------------
    // The PORT SETTINGS surface.  The settings table (Settings/PortSettings.cs) owns what
    //   each knob MEANS; the rasterizer owns the state, and these are the seams the table's live
    //   appliers write through.  Every one of them is presentation: none reaches the verified
    //   kernel, which is why a settings change is byte-inert by construction.
    // ---------------------------------------------------------------------------------------

    /// <summary>The whole scene-render option block, so one applier can change one field.</summary>
    public SceneRenderOptions SceneOptions
    {
        get => _sceneOptions;
        set => _sceneOptions = value;
    }

    /// <summary>
    /// THE LIVE SMOKE LAYER the seven smoke rows of the settings dialog write, created on demand so
    /// that a run started with <c>--smoke-trail off</c> can still be switched ON.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Program.OpenSession</c> builds a layer only when the option says <c>on</c>, so with the row
    /// off the session carries none and there is no object for an applier to write.  This creates a
    /// DISABLED one instead — disabled draws nothing, hides nothing (<c>AddScene</c> and
    /// <c>SmokeLine</c> both ask <c>Enabled</c> now) and bears nothing — and the
    /// <c>smoke-trail</c> row's own applier is what turns it on.  Every one of the seven rows goes
    /// through here, and the store pushes all of them at <c>Attach</c>, so an object created by the
    /// first of them ends the push holding all seven values.
    /// </para>
    /// <para>
    /// The two knobs that have NO dialog row — <c>--smoke-ramp</c> and the fate machine the wreck
    /// trail needs for the player's own fall — are carried in from the command line and the session.
    /// </para>
    /// </remarks>
    public SmokeTrail Smoke => _session.Smoke ??= new SmokeTrail
    {
        Enabled = false,
        StretchRamp = _smokeStretchRamp,
        Fate = _session.Fate,
        PresentedPosition = PresentedFeet,
    };

    /// <summary>
    /// The pose an object is being DRAWN at, in feet, for every consumer that is not the mesh
    /// renderer (the designator labels, the F9 map, the scope contacts, the smoke layer's wreck
    /// emitter).  False when smoothing is off or the object has not been presented, in which case
    /// the caller keeps the sim's own pose.
    /// </summary>
    /// <param name="objectRef">The pool object.</param>
    /// <param name="x">Presented X, feet.</param>
    /// <param name="y">Presented Y, feet.</param>
    /// <param name="z">Presented Z, feet.</param>
    /// <returns>Whether a presented pose exists.</returns>
    private bool PresentedFeet(ushort objectRef, out double x, out double y, out double z)
    {
        if (_smoothObjects && _smoother.TryGetPresented(objectRef, out PoseSmoother.Pose pose))
        {
            x = pose.X;
            y = pose.Y;
            z = pose.Z;
            return true;
        }

        x = y = z = 0.0;
        return false;
    }

    /// <summary>The census's window onto <see cref="PresentedFeet"/> (<c>--pose-census</c>).</summary>
    /// <param name="objectRef">The pool object.</param>
    /// <param name="pose">Its presented pose, feet and degrees.</param>
    /// <returns>Whether a presented pose exists.</returns>
    public bool TryGetPresentedPose(ushort objectRef, out PoseSmoother.Pose pose)
    {
        if (_smoothObjects && _smoother.TryGetPresented(objectRef, out pose))
        {
            return true;
        }

        pose = default;
        return false;
    }

    /// <summary><see cref="PresentedFeet"/> with the angles, for the map's chevrons.</summary>
    private PoseSmoother.Pose PresentedPose(in CombatSceneObject live) =>
        _smoothObjects && _smoother.TryGetPresented(live.ObjectRef, out PoseSmoother.Pose pose)
            ? pose
            : new PoseSmoother.Pose(
                live.X, live.Y, live.Z, live.HeadingDegrees, live.PitchDegrees, live.RollDegrees);

    /// <summary>
    /// THE LIVE GUNNERY TRAIL the three round rows of the settings dialog write, created on demand so that a
    /// run started with <c>--rounds off</c> can still be switched ON.
    /// </summary>
    /// <remarks>The same law as <see cref="Smoke"/>; <c>--round-ground-hits</c> is the no-row knob.</remarks>
    public GunneryRounds Gunnery => _session.Gunnery ??= new GunneryRounds
    {
        Enabled = false,
        GroundHits = _roundGroundHits,
    };

    /// <summary>Rebuilds the grey round's streak mesh at a new length.</summary>
    /// <param name="lengthFeet">The streak length in feet.</param>
    public void SetRoundLength(double lengthFeet)
    {
        _roundLengthFeet = Math.Max(1.0, lengthFeet);
        RebuildRoundMesh();
    }

    /// <summary>Rebuilds the grey round's streak mesh in a new palette colour.</summary>
    /// <param name="colorIndex">The palette index.</param>
    public void SetRoundColor(int colorIndex)
    {
        _roundColorIndex = Math.Clamp(colorIndex, 0, 255);
        RebuildRoundMesh();
    }

    // The mesh exists for every run now, so a length or colour chosen while `--rounds` is off is
    // still there when the row is switched on.
    private void RebuildRoundMesh() =>
        _roundMesh = GunneryRounds.RoundMesh(_roundLengthFeet, _roundColorIndex);

    /// <summary>
    /// The view the camera was ACTUALLY in on the last rendered frame: the same thing, except
    /// after a fate trigger, where an interior choice becomes the circling death camera.
    /// </summary>
    public ViewMode EffectiveView { get; private set; }

    /// <summary>How many COMBAT pool objects the last frame drew.</summary>
    public int CombatInstances => _combatInstances;

    /// <summary>
    /// One line per COMBAT pool object the last frame drew: its class, where it is, and the
    /// original's own gear verdict for it (<see cref="EnemyGearState"/>).
    /// </summary>
    /// <remarks>
    /// A diagnostic the headless summary prints — it is what lets a fly-over be aimed at a bandit
    /// with <c>--camera</c> instead of hunted for in a contact sheet.
    /// </remarks>
    public IReadOnlyList<string> CombatObjectLines => _combatObjectLines;

    /// <summary>How many cloud instances the last frame submitted (the tiled deck's census).</summary>
    public int CloudInstances => _cloudInstances;

    /// <summary>
    /// The ground grid's scale exponent last frame (<see cref="GroundGrid.ScaleShiftExponentFor"/>),
    /// or −1 when the grid was not drawn.
    /// </summary>
    public int GroundGridExponent => _groundGridExponent;

    /// <summary>How many rows of the frame the 3-D world filled on the last frame.</summary>
    public int WorldRows => _worldRows;

    /// <summary>
    /// Puts this frame's nine cloud objects into the snapshot, wrapped around the camera.
    /// </summary>
    /// <param name="camera">Where the camera is — the port's stand-in for <c>s_view_anchor</c>.</param>
    /// <remarks>
    /// <c>cloud_deck_spawn @image@0x2CC38</c> spawns nine <c>cloud</c> objects at
    /// <c>[0xF100] &lt;&lt; 8</c> with X and Z zero, and
    /// <c>cloud_reposition_for_idx @image@0x2CD9E</c> re-lays them on a 32,768-world-unit lattice
    /// around the view anchor every frame.  The port does the same thing without a pool: the deck is
    /// a pure function of the camera position (<see cref="CloudDeckLattice.Position(int, int, int)"/>), so it needs no
    /// per-frame state and costs nine instances.
    /// </remarks>
    private void AddCloudDeck(in CameraPose camera)
    {
        if (_cloudMesh is null || _cloudDeck is null || _snapshot is null || !_cloudsOn
            || _session.CloudAltitudeFeet == CloudDeck.NoDeck)
        {
            _cloudInstances = 0;
            return;
        }

        int anchorX = (int)camera.Eye.X;
        int anchorZ = (int)camera.Eye.Z;
        int count = 0;
        for (int tileZ = -_cloudTiles; tileZ <= _cloudTiles; tileZ++)
        {
            for (int tileX = -_cloudTiles; tileX <= _cloudTiles; tileX++)
            {
                for (int i = 0; i < CloudDeck.Count; i++)
                {
                    (int x, int z) = _cloudDeck.Position(i, tileX, tileZ, anchorX, anchorZ);
                    double fade = CloudDeck.FadeAt(
                        x - camera.Eye.X, z - camera.Eye.Z, _cloudTiles);
                    if (fade <= 0.0)
                    {
                        continue;
                    }

                    count++;
                    _snapshot.Add(new SceneInstance(
                        _cloudMesh,
                        x,
                        _session.CloudAltitudeFeet,
                        z,
                        HeadingDegrees: 0.0,
                        Opacity: fade));
                }
            }
        }

        _cloudInstances = count;
    }

    /// <summary>
    /// Puts this frame's GROUND REFERENCE GRID — the "ground balls" — into the snapshot.
    /// </summary>
    /// <param name="camera">Where the camera is (the port's <c>s_view_anchor</c>).</param>
    /// <remarks>
    /// One <c>spheres</c> object, exactly as the original has it
    /// (<see cref="GroundGrid"/>): snapped to its own lattice around the camera, drawn at the
    /// altitude-derived scale the original writes into the class's registry slot every frame, and
    /// switched off above 30,000 ft.  The <c>--ground-balls off</c> knob is the original's
    /// <c>g_graphics_detail_level [0xF108] = 0</c>.
    /// </remarks>
    private void AddGroundGrid(in CameraPose camera)
    {
        _groundGridExponent = -1;
        _groundBallInstances = 0;
        if (_groundGridMesh is null || _snapshot is null)
        {
            return;
        }

        int altitude = (int)camera.Eye.Y;
        if (!GroundGrid.IsVisible(altitude, GroundGrid.MinimumDetailLevel))
        {
            return;
        }

        if (_groundBallsFixed)
        {
            AddFixedGroundLattice(camera);
            return;
        }

        int exponent = GroundGrid.ScaleShiftExponentFor(altitude);
        (int x, int y, int z) = GroundGrid.Position((int)camera.Eye.X, (int)camera.Eye.Z, exponent);
        _groundGridExponent = exponent;
        _groundBallInstances = 1;
        _snapshot.Add(new SceneInstance(
            _groundGridMesh,
            x,
            y,
            z,
            HeadingDegrees: 0.0,
            WorldScaleOverride: Math.ScaleB(1.0, exponent)));
    }

    private void AddSun(in CameraPose camera)
    {
        if (_sunMesh is null || _snapshot is null)
        {
            return;
        }

        (double x, double y, double z) = SunDisc.Position(camera.Eye.X, camera.Eye.Y, camera.Eye.Z);
        // At INFINITE depth: no aircraft flies behind the sun (SceneInstance.AtInfinity).
        _snapshot.Add(new SceneInstance(_sunMesh, x, y, z, HeadingDegrees: 0.0, AtInfinity: true));
    }

    /// <summary>
    /// The port's DEFAULT ground reference grid: a world-aligned lattice that never moves.
    /// </summary>
    /// <param name="camera">Where the camera is.</param>
    /// <remarks>
    /// <para>
    /// The balls should not jump: they sit at FIXED world positions and a FIXED size, rendered
    /// naturally.  The original does the opposite twice over — it re-snaps its
    /// single 9 × 9 object to a camera-relative lattice every frame AND rewrites the class's scale
    /// exponent per altitude band (<see cref="GroundGrid.Position"/>,
    /// <see cref="GroundGrid.ScaleShiftExponentFor"/>, both from
    /// <c>alloc_slot_b_camera_pos_snap_update @image@0x2DCF4</c>) — so the pattern jumps a whole cell
    /// at a time and re-scales as you climb.  That is a 1991 fill-rate answer to "draw a ground
    /// reference with 81 discs", not the effect it was after.
    /// </para>
    /// <para>
    /// The port instead tiles the SAME shipped mesh at exponent 0 on the world lattice, exactly as it
    /// tiles the cloud deck: a ball's world position is a pure function of its cell, the camera only
    /// picks which tiles are instantiated, and the outermost ring fades to nothing
    /// (<see cref="GroundGrid.FixedFadeAt"/>) so a tile can never pop.
    /// </para>
    /// </remarks>
    private void AddFixedGroundLattice(in CameraPose camera)
    {
        _groundGridExponent = 0;
        int anchorX = (int)camera.Eye.X;
        int anchorZ = (int)camera.Eye.Z;
        int count = 0;
        for (int tileZ = -_groundBallTiles; tileZ <= _groundBallTiles; tileZ++)
        {
            for (int tileX = -_groundBallTiles; tileX <= _groundBallTiles; tileX++)
            {
                (int x, int y, int z) = GroundGrid.FixedTilePosition(tileX, tileZ, anchorX, anchorZ);
                double fade = GroundGrid.FixedFadeAt(
                    x - camera.Eye.X, z - camera.Eye.Z, _groundBallTiles);
                if (fade <= 0.0)
                {
                    continue;
                }

                count++;
                _snapshot!.Add(new SceneInstance(
                    _groundGridMesh!,
                    x,
                    y,
                    z,
                    HeadingDegrees: 0.0,
                    Opacity: fade,
                    WorldScaleOverride: 1.0));
            }
        }

        _groundBallInstances = count;
    }

    /// <summary>
    /// Puts this frame's COMBAT POOL into the snapshot — every live object except the player's own
    /// aircraft, drawn from the pose the arena carries.
    /// </summary>
    /// <remarks>
    /// The SIM owns the list (<see cref="CombatSceneObjects.Live"/> walks
    /// <c>g_render_object_list_head [0x0096]</c> exactly as the admitter's own sweep does) and the
    /// renderer only reads it.  A pool object's class-record pointer is the key: the same
    /// <c>ClassRecord.DgroupOffset</c> the transformed <c>exe/classes.json</c> carries, so a bullet
    /// draws as <c>bullet</c>, a Me-109 as <c>me109</c> and an explosion as <c>explosio</c> with no
    /// special cases.
    /// </remarks>
    private void AddCombatObjects(ViewMode effectiveView)
    {
        _combatInstances = 0;
        _enemyGearDown = 0;
        _combatObjectLines.Clear();
        if (_snapshot is null || _session.Mission is not { } mission || _classMeshes.Count == 0)
        {
            return;
        }

        // The ejection meshes' prepare callbacks read [0xE470], the "own-aircraft view" bit
        // (set on view modes 0..5, the F1..F6 cockpit views): the player's own ejected pilot
        // loses his head disc there.  The verdict is taken sim-side in Live.
        bool ownAircraftView = EjectionLeafTags.IsOwnAircraftView((int)effectiveView);
        double now = _session.SimulatedSeconds;

        // With the wreck's mesh gone (the aircraft at rest), its SHADOW goes too: the subsystem4x04
        // slot whose +0x02 owner is the player names the shadow object to drop. The original never
        // reaches this state — a destroyed aircraft is re-classed to `crater` and the shadow
        // subsystem retires a crater's shadow on its own. And a shadow object no slot OWNS is not
        // drawn at all: in the original a shadow is lit only while its slot names an owner (Claim
        // @image@0x0B81A / Evict @image@0x0B69E), so an active-but-unowned shadow — the wreck's,
        // after the flight kernel stopped — is a state the original never presents.
        bool playerAtRest = _session.Fate is { Enabled: true, Passive: false, Phase: PlayerFatePhase.Wreck or PlayerFatePhase.Ended };
        _orphanShadows.Clear();
        ushort playerShadow = 0;
        {
            CombatRegisters r = mission.Combat.Registers;
            ushort player = r.PlayerObjectRef;

            // The player's OWN shadow is not a table slot: the cold start spawns a SECOND object
            // beside the player (image@0x09421..0x09456, class = the type→resource lookup on the
            // player's class, i.e. `p51sh`) and publishes it at [0x00C4].  It goes with the mesh.
            if (playerAtRest && !_wreckMesh)
            {
                playerShadow = r.Word(CombatColdStart.SecondObjectFarPointer);
            }

            for (int slot = ShadowTable.FirstSlot; slot <= ShadowTable.LastSlot; slot += ShadowTable.SlotBytes)
            {
                ushort owner = r.Word(slot + ShadowTable.OwnerObject);
                ushort shadow = r.Word(slot + ShadowTable.ShadowObject);
                if (owner == 0 && shadow != 0)
                {
                    _orphanShadows.Add(shadow);
                }
                else if (owner == player && playerAtRest && !_wreckMesh)
                {
                    _orphanShadows.Add(shadow);   // a table shadow the wreck still owned
                }
            }
        }

        _smoother.BeginFrame();
        foreach (CombatSceneObject live in CombatSceneObjects.Live(
            mission.Combat.Registers, mission.Combat.Arena, ownAircraftView: ownAircraftView))
        {
            if (live.IsPlayer || live.ObjectRef == _suppressObjectRef
                || (playerShadow != 0 && live.ObjectRef == playerShadow)
                || _orphanShadows.Contains(live.ObjectRef)
                || !_classMeshes.TryGetValue(live.ClassRecordRef, out MeshModel? mesh))
            {
                continue;
            }

            // While the presentation SMOKE LAYER is on, the sim's own puffs are already REPRESENTED:
            // the layer bore one of its own for every spawn and is still flying it long after the
            // verified slot expired.  Drawing both would double every puff for the first few
            // seconds of its life.  --smoke-trail off draws these and bears no layer. `is not null`
            // became `is { Enabled: true }`: the layer object now exists for every run (it is
            // created disabled so the dialog has something to switch on), so presence is no longer
            // the question — whether it is BEARING is.  Switching it off mid-flight therefore brings
            // the sim's own puffs back on the very next frame, while the layer's own live puffs go
            // on expiring beside them; the overlap is at most the first third of a mirrored puff's
            // life and reads as one denser puff, which is the cheaper price than a hole where the
            // smoke was.
            if (_session.Smoke is { Enabled: true }
                && live.ClassRecordRef == Core.Sim.Combat.Effects.SmokePuffTable.SmokeClassRecord)
            {
                continue;
            }

            // The deadline-scheduled objects (far bandits) are integrated in coarse steps by the
            // original's engagement queue; the smoother interpolates their DRAWN pose between the
            // sim's updates.  Every-step objects pass through untouched (PoseSmoother).
            PoseSmoother.Pose pose = new PoseSmoother.Pose(
                live.X, live.Y, live.Z, live.HeadingDegrees, live.PitchDegrees, live.RollDegrees);
            if (_smoothObjects)
            {
                pose = _smoother.Present(live.ObjectRef, live.ClassRecordRef, in pose, now);
            }

            // H5b item 3: the gear a NON-PLAYER aircraft shows is the original's own per-object
            // two-state decision, not the player's animated angle — CombatSceneObject.GearDown
            // (EnemyGearState, mesh_lod_prepare_gear_and_flame_state @image@0x2D9A6).  A mesh
            // with no gear articulation ignores the angle, so it costs nothing to pass it.
            _snapshot.Add(new SceneInstance(
                mesh,
                pose.X,
                pose.Y,
                pose.Z,
                pose.HeadingDegrees,
                pose.PitchDegrees,
                pose.RollDegrees,
                live.GearDown
                    ? Core.Sim.Flight.GearDeployAngle.Extended
                    : Core.Sim.Flight.GearDeployAngle.Retracted,
                Opacity: 1.0,
                WorldScaleOverride: 0.0,
                // The deferred-effect record's clock, fork byte and sequence, so the
                // opcode-4 anchor can grow, fade and throw the debris the callback throws.
                EffectAgeFrameTime: live.EffectAgeFrameTime,
                EffectFork: live.EffectFork,
                EffectSequence: live.EffectSequence,
                // Which paint-tree leaves the object's own prepare callback hides this frame (the
                // seat hides the pilot's parts and vice versa; one limb set under the chute).
                // --no-ejection-parts is the A/B that draws the meshes whole. and the AFTERBURNER
                // PLUME is hidden on every OTHER aeroplane, always: the tag byte is one DGROUP
                // global per mesh and the original raises it only while rendering the object the
                // camera follows (image@0x2D8F9), which is the player's.  Without this an AI
                // MiG-21 or F-4 burns for the whole sortie.
                HiddenLeafNodes: FlameLeaves.Merge(
                    mesh.Basename, lit: false, _noEjectionParts ? null : live.HiddenLeafNodes),
                // A smoke puff's kind, age and span, so the renderer can run the smoke class's
                // prepare-callback law and grow the three discs as the original does
                // (SmokeLook); null for every other object.
                SmokePuff: live.SmokePuff));
            _combatInstances++;
            _enemyGearDown += live.GearDown ? 1 : 0;
            _combatObjectLines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{mesh.Basename,-9} [{live.ObjectRef:X4}] at ({live.X,8:F0},{live.Y,7:F0},{live.Z,8:F0}) hdg {live.HeadingDegrees,6:F1}"
                    + $"{EngagementTag(mission, in live)}"
                    + $"  gear {(live.GearDown ? "DOWN" : "UP  ")}"
                    + $"  articulated {(mesh.Gear is null ? "no" : "yes")}"));
        }

        _smoother.EndFrame();
        _smoothedObjects = _smoother.SmoothedLastFrame;
    }

    /// <summary>
    /// The census column that says whether a pool object is a FALLING WRECK: its engagement PHASE
    /// byte (<c>object[+0x25]</c> = <c>block[+0x0D]</c>, the byte
    /// <c>EngagementNodePass.Dispatch</c> switches on through <c>[0xED61]</c>) and its block's
    /// prototype word (<c>block[+0x00]</c>, which <c>engagement_kill_finalize</c> stamps with
    /// <c>0x2540</c> @<c>image@0x0C3EA</c>).  Blank for anything without an engagement block.
    /// </summary>
    private static string EngagementTag(MissionSession mission, in CombatSceneObject live)
    {
        if (!live.CarriesEngagement)
        {
            return "                 ";
        }

        PoolArena arena = mission.Combat.Arena;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"  phase {arena.Byte((ushort)(live.ObjectRef + 0x25)),2} proto {arena.Word((ushort)(live.ObjectRef + 0x18)):X4}");
    }

    /// <summary>
    /// The grey rounds and their impact flashes, drawn beside the pool's own objects.
    /// </summary>
    /// <remarks>
    /// A round is the <c>round</c> mesh at the round's position under the tracer's own heading and
    /// elevation — the same two words <see cref="CombatSceneObjects.Live"/> hands the renderer for
    /// the <c>bullet</c> it trails.  A flash is the <c>explosio</c> anchor with the impact's age on
    /// the effect clock, fork 0 (the growing disc and its eight shards — what
    /// <c>deferred_effect_render @image@0x03E18</c> draws for a gun hit, whose parameter byte is the
    /// class's lethal bit, 0 for every gun) and the impact's own sequence seed; a ground strike is
    /// drawn at half size (<see cref="GunneryRounds.GroundFlashScale"/>).
    /// </remarks>
    private void AddGunnery()
    {
        _roundInstances = 0;
        _flashInstances = 0;
        if (_snapshot is null || _session.Mission is not { } mission
            || _session.Gunnery is not { } gunnery || _roundMesh is null)
        {
            return;
        }

        foreach (GunneryRounds.Round round in gunnery.Rounds)
        {
            _snapshot.Add(new SceneInstance(
                _roundMesh, round.X, round.Y, round.Z, round.HeadingDegrees, round.PitchDegrees));
            _roundInstances++;
        }

        if (_flashMesh is null)
        {
            return;
        }

        long now = GunneryRounds.FrameTime(mission.Combat.Registers);
        foreach (GunneryRounds.Impact impact in gunnery.Impacts)
        {
            _snapshot.Add(new SceneInstance(
                _flashMesh,
                impact.X,
                impact.Y,
                impact.Z,
                HeadingDegrees: 0.0,
                WorldScaleOverride: impact.Ground ? GunneryRounds.GroundFlashScale : 0.0,
                EffectAgeFrameTime: impact.AgeAt(now),
                EffectFork: 0,
                EffectSequence: impact.Sequence));
            _flashInstances++;
        }
    }

    /// <summary>
    /// The PRESENTATION SMOKE LAYER's puffs, drawn in place of the sim's own.
    /// </summary>
    /// <remarks>
    /// Each layer puff is one instance of the <c>smoke</c> class's mesh at the puff's own position,
    /// under the tumble the original's physics step gives it, carrying its
    /// <see cref="Core.Sim.Combat.Effects.SmokePuffState"/> so the renderer runs H23's size-and-colour
    /// law (<c>SmokeLook</c>) — over the STRETCHED span, so the ramp is slower and ends at the same
    /// 200 / 280 / 240 world units.  The fade (<c>--smoke-fade</c>) is the instance's
    /// <see cref="SceneInstance.Opacity"/>, which multiplies every face's coverage.
    /// </remarks>
    private void AddSmoke()
    {
        _smokeInstances = 0;
        _wreckSmokeInstances = 0;
        if (_snapshot is null || _session.Smoke is not { } smoke || _smokeMesh is null)
        {
            return;
        }

        // --wreck-smoke-size scales a WRECK-TRAIL puff and nothing else.  It rides on the instance's
        // world scale rather than on the ramp because the renderer's law (SmokeLook) is handed the
        // kind, the age and the span and nothing else; a disc's drawn radius is `radius × scale ×
        // projection`, so the two are the same multiplication.
        double wreckScale = _smokeMesh.WorldScale * Math.Max(0.0, smoke.WreckSizeScale);

        foreach (SmokeTrail.Puff puff in smoke.Puffs)
        {
            _snapshot.Add(new SceneInstance(
                _smokeMesh,
                puff.X,
                puff.Y,
                puff.Z,
                puff.HeadingDegrees,
                puff.PitchDegrees,
                puff.RollDegrees,
                GearAngleBam: -1,
                Opacity: puff.Opacity,
                WorldScaleOverride: puff.IsWreckTrail ? wreckScale : 0.0,
                SmokePuff: puff.State));
            _smokeInstances++;
            _wreckSmokeInstances += puff.IsWreckTrail ? 1 : 0;
        }
    }

    /// <summary>
    /// What the DEATH camera watches, or null while the player is still flying.
    /// </summary>
    /// <returns>
    /// The ejecting PILOT's pose when the pilot really left the aeroplane and his pool object is
    /// live, otherwise the wreck's own (null when the fate machine has not fired).
    /// </returns>
    /// <remarks>
    /// The pilot is the destruction slot's <c>ext_ptr_a</c> — H7 §0.2's <c>eject1</c> object, which
    /// walks <c>eject1 → eject4</c> as the chute opens.  The slot the Shift-E arm allocated is the
    /// one whose <c>+0x01</c> player-owned flag is set (<c>image@0x2C537</c>), and the original
    /// itself points its own view subject at that sequence
    /// (<c>[0xBC] := 0 ; [0xBE] := [0x00C0]</c>, <c>image@0x01342</c>).
    /// </remarks>
    private CameraSubject? FateSubject()
    {
        if (_session.Fate is not { Enabled: true, Passive: false } fate
            || fate.Phase == PlayerFatePhase.Flying)
        {
            return null;
        }

        if (fate.WantsPilotView && _session.Mission is { } mission
            && PilotObjectRef(mission) is { } pilot
            && PoolSubject(mission, pilot) is { } pose)
        {
            return pose;
        }

        return CameraSubject.FromSnapshot(_session.Snapshot());
    }

    /// <summary>The ejecting pilot's pool object, or null when no player-owned slot is live.</summary>
    /// <param name="mission">The mission session.</param>
    private static ushort? PilotObjectRef(Core.Sim.Session.MissionSession mission)
    {
        CombatRegisters registers = mission.Combat.Registers;
        for (int slot = Core.Sim.Combat.Lifecycle.ObjectSlotPool.FirstSlot;
             slot <= Core.Sim.Combat.Lifecycle.ObjectSlotPool.LastSlot;
             slot += Core.Sim.Combat.Lifecycle.ObjectSlotPool.SlotBytes)
        {
            if (registers.Byte(slot + Core.Sim.Combat.Lifecycle.ObjectSlotPool.ActiveFlag) != 0
                && registers.Byte(slot + Core.Sim.Combat.Lifecycle.ObjectSlotPool.PlayerOwnedFlag) != 0)
            {
                return registers.Word(slot + Core.Sim.Combat.Lifecycle.ObjectSlotPool.ExtPointerA);
            }
        }

        return null;
    }

    /// <summary>
    /// The LOCKED TARGET's pose, for the five views anchored to it (bits
    /// 2/3/7).
    /// </summary>
    /// <returns>The pose, or null when nothing is locked.</returns>
    private CameraSubject? TargetSubject()
    {
        if (_session.Mission is not { } mission)
        {
            return null;
        }

        ushort target = mission.Combat.Registers.Word(LockOnTargetWord);
        return PoolSubject(mission, target);
    }

    /// <summary>
    /// The newest LIVE projectile's pose, for Shift-F10.
    /// </summary>
    /// <remarks>
    /// The spawn table's slots are reused, so "newest" is the live slot with the largest expire
    /// frame (<c>s_combat_spawn_record[+0x14]</c>) — the one that will be around longest.
    /// </remarks>
    /// <returns>The pose, or null when nothing is in the air.</returns>
    private CameraSubject? MissileSubject()
    {
        if (_session.Mission is not { } mission)
        {
            return null;
        }

        CombatRegisters registers = mission.Combat.Registers;
        ushort best = 0;
        ushort bestExpiry = 0;
        for (int i = 0; i < Core.Sim.Combat.CombatSpawnTable.SlotCount; i++)
        {
            int slot = Core.Sim.Combat.CombatSpawnTable.TableDgroupOffset
                + (i * Core.Sim.Combat.CombatSpawnTable.SlotBytes);
            if (registers.Word(slot) == 0)
            {
                continue;
            }

            ushort expiry = registers.Word(slot + 0x14);
            if (best == 0 || expiry >= bestExpiry)
            {
                best = registers.Word(slot + 0x02);
                bestExpiry = expiry;
            }
        }

        return PoolSubject(mission, best);
    }

    /// <summary><c>[0x00BC]</c> — <c>g_lockon_target</c>, the object the kernel locked this frame.</summary>
    private const int LockOnTargetWord = 0x00BC;

    /// <summary>
    /// The six views whose flag byte carries bit 1, "cockpit-interior" (<c>0x63 0x42 0x42 0x42 0x62
    /// 0x62</c> for ids 0..5, and no other id has the bit).
    /// </summary>
    /// <param name="view">The view.</param>
    public static bool IsCockpitInterior(ViewMode view) => (int)view <= (int)ViewMode.CockpitDown;

    /// <summary>One pool object's pose, or null when the reference is empty or out of the arena.</summary>
    private static CameraSubject? PoolSubject(
        Core.Sim.Session.MissionSession mission, ushort objectRef)
    {
        PoolArena arena = mission.Combat.Arena;
        if (objectRef == 0 || !arena.Covers(objectRef, 0x18))
        {
            return null;
        }

        CombatObjectView pool = new Core.Sim.Combat.CombatObjectView(arena, objectRef);
        CombatPosition position = pool.Position;
        const double degrees = Core.Sim.Session.CombatSceneObjects.DegreesPerUnit;
        return new CameraSubject(
            position.X / 256.0,
            position.Y / 256.0,
            position.Z / 256.0,
            unchecked((ushort)pool.Heading) * degrees,
            unchecked((ushort)pool.Elevation) * degrees * Math.PI / 180.0,
            arena.Word((ushort)(objectRef + 0x16)) * degrees * Math.PI / 180.0);
    }
}
