using CYAC.Port.Core.Model.Mission;
using CYAC.Port.Core.Model.World;
using CYAC.Port.Core.Sim.Combat;
using CYAC.Port.Core.Sim.Combat.Lifecycle;
using CYAC.Port.Core.Sim.Combat.Vm;
using CYAC.Port.Core.Sim.Flight.ColdStart;

namespace CYAC.Port.Core.Sim.Session;

/// <summary>What one scenario load produced — instrumentation, and the renderer's index.</summary>
public sealed class ScenarioLoadCensus
{
    /// <summary>Stream objects the loader walked.</summary>
    public int ObjectsSeen { get; internal set; }

    /// <summary>Objects that reached <c>spawn_dispatch_object</c> with an engagement block.</summary>
    public int EngagementSpawns { get; internal set; }

    /// <summary>Objects inserted as plain scenery (class-table flag 1, or <c>prim_4d00</c>).</summary>
    public int ScenerySpawns { get; internal set; }

    /// <summary>Actor-slot / named-place registrations made.</summary>
    public int NamedPlaces { get; internal set; }

    /// <summary>Objects pushed into <c>g_active_target_table [0xEDE6]</c>.</summary>
    public int TargetTablePushes { get; internal set; }

    /// <summary>Objects whose placement the port could not resolve to a point.</summary>
    public int UnresolvedPlacements { get; internal set; }

    /// <summary>Named-mesh / nav-waypoint / ground-effect openers the loader did not model.</summary>
    public int UnmodelledOpeners { get; internal set; }

    /// <summary>Class ids whose class-table entry is flag 2 (no shipped mission authors one).</summary>
    public int UnsupportedClassIds { get; internal set; }

    /// <summary>Objects that carry an authored AI script the port could not install, with a reason.</summary>
    public int AuthoredScriptsSkipped { get; internal set; }

    /// <summary>Objects whose authored AI script WAS assembled, patched and installed.</summary>
    public int AuthoredScriptsInstalled { get; internal set; }

    /// <summary>Bytes of authored bytecode installed.</summary>
    public int AuthoredScriptBytes { get; internal set; }

    /// <summary>Named-place pseudo-ops the load-time patcher rewrote.</summary>
    public int AuthoredScriptPatches { get; internal set; }

    /// <summary>Why each refused script was refused, in order (at most 16 kept).</summary>
    public IReadOnlyList<string> AuthoredScriptRefusals => _refusals;

    private readonly List<string> _refusals = [];

    /// <summary>Records a refusal.</summary>
    /// <param name="reason">Why the script could not be installed.</param>
    internal void Refuse(string reason)
    {
        AuthoredScriptsSkipped++;
        if (_refusals.Count < 16)
        {
            _refusals.Add(reason);
        }
    }

    /// <summary>Load-time <c>engagement_slot_fsm_advance</c> calls (<c>image@0x0A24F</c>).</summary>
    public int LoadTimeVmSteps { get; internal set; }

    /// <summary>The mission's own <c>mission_altitude</c> directive, or <see cref="CloudDeck.NoDeck"/>.</summary>
    public int MissionAltitude { get; internal set; } = CloudDeck.NoDeck;
}

/// <summary>One object the loader put into the pool — what the renderer needs to draw it.</summary>
/// <param name="ObjectRef">Its near offset in the arena.</param>
/// <param name="ClassRecordRef">The DGROUP class record its <c>+0x00</c> points at.</param>
/// <param name="MissionClassId">The authored class id.</param>
/// <param name="ActorSlot">Its actor slot, or −1.</param>
public readonly record struct LoadedSceneObject(
    ushort ObjectRef, ushort ClassRecordRef, int MissionClassId, int ActorSlot);

/// <summary>
/// One placed NAV WAYPOINT: the record <c>nav_slot_record_register @image@0x08CE4</c> puts into
/// <c>g_nav_slot_record_array [0xB564]</c>, kept beside the load for the cockpit's two bearing
/// instruments.
/// </summary>
/// <param name="Slot">
/// Its index into <c>[0xB564]</c> (stride <c>0x2C</c>) — the value
/// <c>g_nav_slot_current_index [0xEF92]</c> selects.
/// </param>
/// <param name="Label">The name the nav line and the briefing map print.</param>
/// <param name="PosX">Its world X in POSITION units (world &lt;&lt; 8).</param>
/// <param name="PosY">Its world Y.</param>
/// <param name="PosZ">Its world Z.</param>
/// <param name="TrackedActorSlots">
/// For a TRACKING waypoint (placement tag 7): the actor slots it follows, in the order
/// <c>slot_record_get_pos</c> tries them.  Empty for a static one.
/// </param>
public readonly record struct NavWaypointPlacement(
    int Slot, string Label, int PosX, int PosY, int PosZ, IReadOnlyList<int> TrackedActorSlots)
{
    /// <summary>
    /// Whether the waypoint follows live actors rather than sitting at
    /// (<see cref="PosX"/>, <see cref="PosY"/>, <see cref="PosZ"/>) —
    /// <c>slot_record_get_pos @image@0x08E0D</c>'s second path.
    /// </summary>
    public bool Tracks => TrackedActorSlots.Count > 0;
}

/// <summary>
/// The mission's OBJECT PLACEMENT — <c>wld_or_s_asset_parser</c>'s position-tag exit
/// (<c>image@0x0A07C..0x0A30D</c>) driven by the transformed mission document instead of by the
/// <c>.S</c> byte stream.
/// </summary>
/// <remarks>
/// <para>
/// The port does not re-implement the 3,264-byte tag interpreter: <c>cyac-transform</c> has already
/// turned every <c>.S</c> into the same authoring model the parser builds in its stack frame
/// (<see cref="MissionObject"/>'s attributes ARE the parser's locals — <c>[bp-0xda]</c> engage-class,
/// <c>[bp-0xd8]</c> object flags, <c>[bp-0x9e]</c> skill, <c>[bp-0x44]</c> initial speed,
/// <c>[bp-0xd6]</c> actor slot).  What is ported here is what the parser DOES with them once a
/// position tag closes an object, because that is the part that makes objects exist.
/// </para>
/// <para>
/// It is an <see cref="IScenarioLoader"/>, so it runs exactly where the original's does: inside
/// <c>SceneResetDoors.MissionStateResetCombatHalf</c>'s guard window, with
/// <c>g_scene_init_guard_flag [0x0F0B]</c> at 0 so every engagement-list insert degrades to a PUSH
/// FRONT and the whole chain is sorted afterwards (<c>image@0x0C4B9..0x0C4C6</c>).
/// </para>
/// </remarks>
public sealed class ScenarioObjectLoader : IScenarioLoader
{
    private readonly EngagementLifecycleContext _lifecycle;
    private readonly PoolObjectAllocator _allocator;
    private readonly AircraftClassTable _classTable;
    private readonly MissionDefinition _mission;
    private readonly MissionSpawnAnchors _anchors;
    private readonly ushort _scratchRef;
    private readonly IEngagementVm _vm;
    private readonly IAiScriptHeap? _scriptHeap;
    private readonly List<(ushort BlockRef, MissionObject Authored)> _pendingScripts = [];
    private readonly List<LoadedSceneObject> _objects = [];

    /// <summary>
    /// The DGROUP pseudo-record the parser passes as "parent" for a tier-0/1 engagement spawn
    /// (<c>mov ax,0x8a</c> @<c>image@0x0A181</c>).  Its <c>+0x0C</c> is
    /// <c>g_render_object_list_head [0x0096]</c>, so the object is pushed onto the front of the list
    /// the admitter's pressure sweep walks.
    /// </summary>
    public const ushort RenderListParentRecord = 0x008A;

    /// <summary><c>g_named_place_nearptr_table [0xEE5A]</c> — u16[13], one per actor slot.</summary>
    public const int NamedPlaceNearPtrTable = 0xEE5A;

    /// <summary><c>g_named_place_coord_table [0xEE74]</c> — 13 × three <c>i32</c>.</summary>
    public const int NamedPlaceCoordTable = 0xEE74;

    /// <summary><c>g_wld_fixed_coord_entry [0xB556]</c> — the LAST placed position (6 words).</summary>
    public const int LastPlacedPosition = 0xB556;

    /// <summary>How many actor slots there are: 13 (<c>[0xEE5A]</c> is u16[13]).</summary>
    public const int ActorSlotCount = 13;

    /// <summary>Wires a loader over one mission and one live combat state.</summary>
    /// <param name="lifecycle">The kernel's lifecycle context (registers, arena, prototypes, events).</param>
    /// <param name="allocator">The pool allocator whose seams the spawn goes through.</param>
    /// <param name="classTable">The aircraft-class table.</param>
    /// <param name="mission">The mission document.</param>
    /// <param name="anchors">The <c>at_site</c> draws.</param>
    /// <param name="vm">
    /// The bytecode VM — <c>engagement_slot_fsm_advance @image@0x04F64</c>.  The parser calls it with <c>AL =
    /// 0</c> on every engage-capable object it spawns (<c>image@0x0A24F</c>), which is the MISSION-LOAD VM
    /// DOOR: C0's door×mode histogram counted it firing ZERO times inside a gameplay window precisely because
    /// it only ever fires at load.  Without it every bandit stays in phase 0 (the idle countdown) for the
    /// whole sortie.
    /// </param>
    /// <param name="scratchRef">
    /// The arena near offset of the 24-byte STAGING record.  The original stages it on the parser's
    /// stack (<c>[bp-0x9c]</c>); the port's <see cref="PoolArena"/> is its object address space, so
    /// the staging record is a reserved slot ABOVE the allocator's limit and is never inserted.
    /// </param>
    public ScenarioObjectLoader(
        EngagementLifecycleContext lifecycle,
        PoolObjectAllocator allocator,
        AircraftClassTable classTable,
        MissionDefinition mission,
        MissionSpawnAnchors anchors,
        ushort scratchRef,
        IEngagementVm vm,
        IAiScriptHeap? scriptHeap = null)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentNullException.ThrowIfNull(classTable);
        ArgumentNullException.ThrowIfNull(mission);
        ArgumentNullException.ThrowIfNull(anchors);
        ArgumentNullException.ThrowIfNull(vm);
        _vm = vm;
        _lifecycle = lifecycle;
        _allocator = allocator;
        _classTable = classTable;
        _mission = mission;
        _anchors = anchors;
        _scratchRef = scratchRef;
        _scriptHeap = scriptHeap;
    }

    /// <summary>What the load produced.</summary>
    public ScenarioLoadCensus Census { get; } = new();

    /// <summary>Every object the load put into the pool, in stream order.</summary>
    public IReadOnlyList<LoadedSceneObject> Objects => _objects;

    /// <summary>
    /// The container's NAV WAYPOINTS, in stream order: presentation state for the cockpit's bearing
    /// instruments, never written into the registers.
    /// </summary>
    public IReadOnlyList<NavWaypointPlacement> NavWaypoints => _navWaypoints;

    private readonly List<NavWaypointPlacement> _navWaypoints = [];

    /// <inheritdoc/>
    public void LoadScenario()
    {
        CombatRegisters registers = _lifecycle.Registers;
        Census.MissionAltitude = _mission.MissionAltitude ?? CloudDeck.NoDeck;

        foreach (MissionObject authored in _mission.Objects)
        {
            Census.ObjectsSeen++;
            if (authored.Placement.Resolved is not { } placement)
            {
                // A nav waypoint may be a TRACKING one: placement tag 7 names up to three actor
                // slots and slot_record_get_pos @image@0x08E0D resolves it LIVE, first slot with a
                // spawned object winning (spawn_slot_pos_check_and_copy @image@0x08DA9). TOP.S's
                // slot 0 "B-29 Group" is one, and it is the slot the cockpit's bearing pointer
                // defaults to — dropping it left the needle dead.
                if (authored.Kind == MissionObjectKind.NavWaypoint
                    && authored.Placement.Kind == MissionPlacementKind.TrackActors)
                {
                    _navWaypoints.Add(new NavWaypointPlacement(
                        authored.NavSlot,
                        authored.Label,
                        0,
                        0,
                        0,
                        [.. authored.Placement.LiveTrackedActorSlots]));
                }

                Census.UnresolvedPlacements++;
                continue;
            }

            MissionPosition world = _anchors.ToWorld(placement);
            int posX = world.X << FlightColdStart.WorldToObjectShift;
            int posY = world.Y << FlightColdStart.WorldToObjectShift;
            int posZ = world.Z << FlightColdStart.WorldToObjectShift;
            ushort objectRef = 0;

            switch (authored.Kind)
            {
                case MissionObjectKind.Marker:
                    break;                                          // no object; only a named place

                case MissionObjectKind.NavWaypoint:
                    // image@0x0A0FF, nav_slot_record_register @image@0x08CE4: the record goes into
                    // g_nav_slot_record_array [0xB564] and its POSITION is what
                    // object_bearing_to_slot_compute @image@0x08E72 reads back for the cockpit's
                    // bearing pointer (dial slot 4) and the F-86's compass card (slot 3).  The
                    // port keeps the placement beside the load and writes NOTHING into the
                    // registers, so the integer kernel and every replay are untouched.
                    _navWaypoints.Add(new NavWaypointPlacement(
                        authored.NavSlot, authored.Label, posX, posY, posZ, []));
                    Census.UnmodelledOpeners++;
                    break;

                case MissionObjectKind.NamedMesh:
                case MissionObjectKind.GroundEffect:
                    // image@0x0A0E5 / 0x0A11E — wld_named_mesh_place and subsystem4x19_row_attach.
                    // Scenery and HUD furniture, not combat state.
                    Census.UnmodelledOpeners++;
                    break;

                default:
                    // MissionObjectKind.Airport comes through here too: its ClassId is
                    // MissionVocabulary.AirportClassId (26), whose class-table entry is flag 1 with
                    // value 0x4D00 — the same class-record pointer the parser's prim-0x4D00 opener
                    // stages directly (image@0x09BA6).
                    objectRef = SpawnClassInstance(authored, posX, posY, posZ);
                    break;
            }

            if (authored.ActorSlot is int slot && slot >= 0 && slot < ActorSlotCount)
            {
                RegisterNamedPlace(slot, authored, objectRef, posX, posY, posZ);
            }

            InstallPendingScripts();

            // image@0x0A2FD..0x0A30B — the LAST placed position, which the next rel_prev chains off.
            WritePosition(registers, LastPlacedPosition, posX, posY, posZ);
        }
    }

    /// <summary>The class-instance arm — <c>image@0x0A084..0x0A254</c>.</summary>
    /// <param name="authored">The authored object.</param>
    /// <param name="posX">Its world X in position units.</param>
    /// <param name="posY">Its world Y.</param>
    /// <param name="posZ">Its world Z.</param>
    /// <returns>The inserted object's near offset, or 0.</returns>
    private ushort SpawnClassInstance(MissionObject authored, int posX, int posY, int posZ)
    {
        if (_classTable.Lookup(authored.ClassId) is not { } entry)
        {
            Census.UnsupportedClassIds++;
            return 0;
        }

        if (entry.Flag == 0 && entry.Value == AircraftClassEntry.PlayerSentinel)
        {
            // image@0x0A08A — the class-0 PLAYER record.  Its four values live OUTSIDE the combat
            // register file and H2's FlightSpawn.FromMission already reads them off the same
            // document, so the combat loader does not duplicate the branch; the player's own world
            // object is inserted by scenario_load_dispatch's PHASE 2 (image@0x093F7), which
            // CombatColdStart runs after this load.
            return 0;
        }

        if (entry.Flag == 0 && entry.Value == AircraftClassEntry.HomeBaseSentinel)
        {
            WritePosition(
                _lifecycle.Registers, SessionRegisterWindows.HomeBasePosition, posX, posY, posZ);
            return 0;                                               // image@0x0A0D2
        }

        if (entry.IsClassRecord)
        {
            return SpawnScenery(entry.Value, posX, posY, posZ, authored);
        }

        if (!entry.IsEngagementPrototype)
        {
            Census.UnsupportedClassIds++;                           // flag 2: no shipped mission
            return 0;
        }

        return SpawnEngagement(entry.Value, authored, posX, posY, posZ);
    }

    /// <summary>
    /// The ENGAGEMENT arm — <c>image@0x0A14E..0x0A254</c> when <c>prototype[+0x0C] &amp; 8</c>, else
    /// the non-engaging spawn at <c>image@0x0A256</c>.
    /// </summary>
    /// <param name="prototypeRef">The engagement class prototype's DGROUP offset.</param>
    /// <param name="authored">The authored object.</param>
    /// <param name="posX">World X in position units.</param>
    /// <param name="posY">World Y.</param>
    /// <param name="posZ">World Z.</param>
    /// <returns>The inserted object's near offset.</returns>
    private ushort SpawnEngagement(
        ushort prototypeRef, MissionObject authored, int posX, int posY, int posZ)
    {
        PoolArena arena = _lifecycle.Arena;
        ICombatStaticData data = _lifecycle.StaticData;

        // image@0x0A151..0x0A15C — the object is LIFTED by its class's ground clearance before the
        // insert, and image@0x0A2D7 subtracts it again so the next rel_prev chains off the authored
        // point.  The port resolves placements statically, so only the lift is applied.
        ushort classRef = data.Word(prototypeRef);
        int clearance = data.Word(classRef + 0x2C);
        StageRecord(posX, posY + clearance, posZ, authored);

        ushort flags = data.Word(prototypeRef + 0x0C);              // image@0x0A160
        Span<byte> template = stackalloc byte[SpawnDispatchObject.TemplateBytes];
        template.Clear();
        template[0] = (byte)prototypeRef;
        template[1] = (byte)(prototypeRef >> 8);

        bool engages = (flags & 0x08) != 0;                         // image@0x0A165 test al,8
        ushort parent = (flags & 3) < 2 ? RenderListParentRecord : (ushort)0; // image@0x0A175

        uint inserted = SpawnDispatchObject.Run(
            _lifecycle,
            template,
            _scratchRef,
            engages ? parent : (ushort)0,
            flag2: engages ? (byte)0 : (byte)1,
            compactBlock: 0,
            engageFlag: engages ? (byte)1 : (byte)0);
        ushort objectRef = unchecked((ushort)inserted);
        if (objectRef == 0)
        {
            return 0;
        }

        ushort blockRef = arena.EngagementBlockRef(objectRef);      // image@0x0226E
        ApplyAuthoredBlockFields(objectRef, blockRef, prototypeRef, classRef, authored, engages);
        _objects.Add(new LoadedSceneObject(
            objectRef, classRef, authored.ClassId, authored.ActorSlot ?? -1));
        if (engages)
        {
            Census.EngagementSpawns++;
        }
        else
        {
            Census.ScenerySpawns++;
        }

        return objectRef;
    }

    /// <summary>The authored writes the parser makes ON the spawned block, <c>image@0x0A19B..0x0A254</c>.</summary>
    /// <param name="objectRef">The inserted object.</param>
    /// <param name="blockRef">Its embedded engagement block.</param>
    /// <param name="prototypeRef">The class prototype.</param>
    /// <param name="classRef">The class record.</param>
    /// <param name="authored">The authored object.</param>
    /// <param name="engages">Whether the block is a full engagement block.</param>
    private void ApplyAuthoredBlockFields(
        ushort objectRef,
        ushort blockRef,
        ushort prototypeRef,
        ushort classRef,
        MissionObject authored,
        bool engages)
    {
        PoolArena arena = _lifecycle.Arena;
        ICombatStaticData data = _lifecycle.StaticData;

        int objectFlags = (int)authored.ObjectFlags;                // [bp-0xd8], default 0x40
        int engageClass = authored.EngagementClass ?? 1;            // [bp-0xda], default 1

        // image@0x0A1B2 / 0x0A1BA — TWO word ORs at block[+0x05], so 0x100/0x400 land in [+0x06].
        OrWord(arena, (ushort)(blockRef + 0x05), (ushort)engageClass);
        OrWord(arena, (ushort)(blockRef + 0x05), (ushort)objectFlags);

        if ((objectFlags & 0x40) != 0)                              // image@0x0A1CD test al,0x40
        {
            arena.SetByte(
                (ushort)(objectRef + 3),
                (byte)(arena.Byte((ushort)(objectRef + 3)) | 0x04)); // pool flag word bit 0x0400
        }

        if (!engages)
        {
            return;                                                  // the 5-byte stub arm
        }

        arena.SetByte((ushort)(blockRef + 0x1D), (byte)(authored.Skill ?? 0xFF)); // image@0x0A1C2
        arena.SetWord((ushort)(blockRef + 0x1E), 0);                 // image@0x0A1C9 (name ptr)

        // image@0x0A1DC..0x0A218 — the INITIAL SPEED, tier-0 classes only, and only when the object
        // spawned ABOVE its own ground clearance (i.e. airborne; a parked bandit gets none).
        if ((data.Word(prototypeRef + 0x0C) & 3) == 0)
        {
            int y = arena.Word((ushort)(objectRef + 0x0A))
                | (arena.Word((ushort)(objectRef + 0x0C)) << 16);
            int clearance = data.Word(classRef + 0x2C);
            if (y > clearance)
            {
                int speed = authored.InitialSpeed
                    ?? data.Word(data.Word(prototypeRef + 0x26) + 0x0C);
                int q8 = speed << 8;                                 // image@0x0A20D shl_i32_by_cl 8
                arena.SetWord((ushort)(blockRef + 0x25), unchecked((ushort)q8));
                arena.SetWord((ushort)(blockRef + 0x27), unchecked((ushort)(q8 >> 16)));
            }
        }

        if (authored.HasAiScript)
        {
            // image@0x0A228..0x0A243 — block[+0x20] = the script's far-heap SEGMENT, [+0x22] = pc 0,
            // [+0x24] = the script flags, [+0x0E] = 0.  The install is DEFERRED to the end of the
            // object's close, because ai_script_named_place_patch reads the named-place tables and
            // the object's OWN slot is registered at the same close (the original registers before
            // it patches; the port's loader registers after SpawnClassInstance returns).
            _pendingScripts.Add((blockRef, authored));
        }

        if (authored.JoinsTargetTable)                               // image@0x0A291
        {
            PushActiveTarget(blockRef);
        }

        // image@0x0A24F — the MISSION-LOAD VM door: engagement_slot_fsm_advance(block, AL = 0).
        _vm.Advance(_lifecycle.Registers, _lifecycle.Arena, blockRef, 0);
        Census.LoadTimeVmSteps++;
    }

    /// <summary>
    /// The AUTHORED-SCRIPT install — <c>image@0x0A228..0x0A247</c> with the assembler and the
    /// load-time patcher in front of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four steps, in the original's order: assemble the document's steps into the tag-<c>0x89</c>
    /// wire form (<see cref="AiScriptAssembler"/>); run
    /// <c>ai_script_named_place_patch @image@0x094EF</c> over the bytes so the place-relative
    /// pseudo-ops become absolute-coordinate ops; allocate a far-heap block of
    /// <c>max(N, 0x32)</c> bytes (<c>cmp ax,0x32 / jge / mov ax,0x32</c>
    /// @<c>image@0x09D4D..0x09D52</c>) and copy the program in; then stamp the engagement block's
    /// <c>+0x20</c> segment, <c>+0x22</c> pc, <c>+0x0E</c> timer and <c>+0x24</c> script flags.
    /// </para>
    /// </remarks>
    private void InstallPendingScripts()
    {
        if (_pendingScripts.Count == 0)
        {
            return;
        }

        CombatRegisters registers = _lifecycle.Registers;
        PoolArena arena = _lifecycle.Arena;
        foreach ((ushort blockRef, MissionObject authored) in _pendingScripts)
        {
            if (_scriptHeap is null)
            {
                Census.Refuse("no script heap was supplied to the loader");
                continue;
            }

            AiScriptAssembly assembly = AiScriptAssembler.Assemble(authored.AiScript, authored.AiScriptHex);
            if (!assembly.Ok)
            {
                Census.Refuse(assembly.Refusal!);
                continue;
            }

            byte[] program = assembly.Program;
            AiScriptNamedPlacePatch.Result patch = AiScriptNamedPlacePatch.Apply(program, registers);
            if (patch.Derailed)
            {
                Census.Refuse("the load-time patcher's walk derailed on the assembled bytes");
                continue;
            }

            ushort segment = _scriptHeap.Allocate(
                Math.Max(program.Length, AiScriptMemory.ProgramBufferBytes));
            if (segment == 0)
            {
                Census.Refuse("the script far heap is exhausted");
                continue;
            }

            program.CopyTo(_scriptHeap.Block(segment));
            arena.SetWord((ushort)(blockRef + 0x20), segment);        // image@0x0A22F
            arena.SetWord((ushort)(blockRef + 0x22), 0);              // image@0x0A236
            arena.SetWord((ushort)(blockRef + 0x0E), 0);              // image@0x0A23A
            arena.SetByte((ushort)(blockRef + 0x24), (byte)authored.ScriptFlags);   // image@0x0A243

            Census.AuthoredScriptsInstalled++;
            Census.AuthoredScriptBytes += program.Length;
            Census.AuthoredScriptPatches += patch.OrientPatches + patch.HeadingPatches
                + patch.ActorPatches;
        }

        _pendingScripts.Clear();
    }

    /// <summary>The plain-scenery arm — <c>image@0x0A2AC..0x0A2D1</c>.</summary>
    /// <param name="classRef">The class record's DGROUP offset.</param>
    /// <param name="posX">World X in position units.</param>
    /// <param name="posY">World Y.</param>
    /// <param name="posZ">World Z.</param>
    /// <param name="authored">The authored object.</param>
    /// <returns>The inserted object's near offset.</returns>
    private ushort SpawnScenery(
        ushort classRef, int posX, int posY, int posZ, MissionObject authored)
    {
        PoolArena arena = _lifecycle.Arena;
        int clearance = _lifecycle.StaticData.Word(classRef + 0x2C); // image@0x0A2B6
        StageRecord(posX, posY + clearance, posZ, authored);
        arena.SetWord(_scratchRef, classRef);

        ushort objectRef = _allocator.InsertWithFlag2(_scratchRef);  // image@0x0A2C8
        if (objectRef == 0)
        {
            return 0;
        }

        Census.ScenerySpawns++;
        _objects.Add(new LoadedSceneObject(
            objectRef, classRef, authored.ClassId, authored.ActorSlot ?? -1));
        return objectRef;
    }

    /// <summary>
    /// Stages the 24-byte pool record the insert copies — the parser's <c>[bp-0x9c]</c> frame, whose
    /// defaults are set once per object at <c>image@0x09B0A..0x09B18</c> (flags word = 1 = ACTIVE,
    /// euler = 0).
    /// </summary>
    /// <param name="posX">World X in position units.</param>
    /// <param name="posY">World Y.</param>
    /// <param name="posZ">World Z.</param>
    /// <param name="authored">The authored object, for its heading / aux vector.</param>
    private void StageRecord(int posX, int posY, int posZ, MissionObject authored)
    {
        PoolArena arena = _lifecycle.Arena;
        arena.Span(_scratchRef, PoolObjectAllocator.FullRecordBytes).Clear();
        arena.SetWord((ushort)(_scratchRef + 0x02), 1);              // image@0x09B18
        WriteArenaPosition(arena, (ushort)(_scratchRef + 0x06), posX, posY, posZ);

        if (authored.AuxVector is { Count: >= 3 } aux)               // attr 0x99, image@0x09BB5
        {
            arena.SetWord((ushort)(_scratchRef + 0x12), (ushort)aux[0]);
            arena.SetWord((ushort)(_scratchRef + 0x14), (ushort)aux[1]);
            arena.SetWord((ushort)(_scratchRef + 0x16), (ushort)aux[2]);
        }
        else if (authored.HeadingUnits is int heading)               // attr 0x80
        {
            arena.SetWord((ushort)(_scratchRef + 0x12), (ushort)heading);
        }
    }

    /// <summary>
    /// <c>named_place_entry_register @image@0x08EE7</c> — the three parallel actor-slot tables.
    /// </summary>
    /// <param name="slot">The actor slot 0..12.</param>
    /// <param name="authored">The authored object.</param>
    /// <param name="objectRef">The spawned object's near offset, or 0.</param>
    /// <param name="posX">World X in position units.</param>
    /// <param name="posY">World Y.</param>
    /// <param name="posZ">World Z.</param>
    private void RegisterNamedPlace(
        int slot, MissionObject authored, ushort objectRef, int posX, int posY, int posZ)
    {
        CombatRegisters registers = _lifecycle.Registers;
        registers.SetWord(NamedPlaceNearPtrTable + (slot * 2), objectRef);       // image@0x08EF4
        WritePosition(registers, NamedPlaceCoordTable + (slot * 12), posX, posY, posZ); // image@0x08F0E
        registers.SetByte(
            SessionRegisterWindows.NamedPlaceTypeTable + slot,
            (byte)(authored.PlaceType ?? authored.Placement.SiteType));          // image@0x08F16
        Census.NamedPlaces++;
    }

    /// <summary>
    /// <c>engagement_target_table_push @image@0x0743E</c> — append a block to
    /// <c>g_active_target_table [0xEDE6]</c> and bump <c>g_active_target_count [0xEE02]</c>.
    /// </summary>
    /// <param name="blockRef">The engagement block's near offset.</param>
    private void PushActiveTarget(ushort blockRef)
    {
        CombatRegisters registers = _lifecycle.Registers;
        ushort count = registers.Word(0xEE02);
        registers.SetWord(0xEDE6 + (count * 2), blockRef);
        registers.SetWord(0xEE02, (ushort)(count + 1));
        Census.TargetTablePushes++;
    }

    private static void OrWord(PoolArena arena, ushort at, ushort bits) =>
        arena.SetWord(at, (ushort)(arena.Word(at) | bits));

    private static void WritePosition(
        CombatRegisters registers, int at, int posX, int posY, int posZ)
    {
        registers.SetWord(at + 0x00, unchecked((ushort)posX));
        registers.SetWord(at + 0x02, unchecked((ushort)(posX >> 16)));
        registers.SetWord(at + 0x04, unchecked((ushort)posY));
        registers.SetWord(at + 0x06, unchecked((ushort)(posY >> 16)));
        registers.SetWord(at + 0x08, unchecked((ushort)posZ));
        registers.SetWord(at + 0x0A, unchecked((ushort)(posZ >> 16)));
    }

    private static void WriteArenaPosition(
        PoolArena arena, ushort at, int posX, int posY, int posZ)
    {
        arena.SetWord(at, unchecked((ushort)posX));
        arena.SetWord((ushort)(at + 2), unchecked((ushort)(posX >> 16)));
        arena.SetWord((ushort)(at + 4), unchecked((ushort)posY));
        arena.SetWord((ushort)(at + 6), unchecked((ushort)(posY >> 16)));
        arena.SetWord((ushort)(at + 8), unchecked((ushort)posZ));
        arena.SetWord((ushort)(at + 10), unchecked((ushort)(posZ >> 16)));
    }
}
