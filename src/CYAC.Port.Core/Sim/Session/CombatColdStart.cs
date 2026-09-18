using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.Mission;
using CYAC.Port.Core.Sim.Combat;
using CYAC.Port.Core.Sim.Combat.Effects;
using CYAC.Port.Core.Sim.Combat.Geometry;
using CYAC.Port.Core.Sim.Combat.Grid;
using CYAC.Port.Core.Sim.Combat.Lifecycle;
using CYAC.Port.Core.Sim.Combat.Vm;
using CYAC.Port.Core.Sim.Flight;
using CYAC.Port.Core.Sim.Flight.ColdStart;

namespace CYAC.Port.Core.Sim.Session;

/// <summary>What a caller asks a combat cold start for.</summary>
/// <param name="Tree">The transformed data tree.</param>
/// <param name="Mission">The mission container (a historic <c>.S</c>, or <c>FREE.S</c>).</param>
/// <param name="Theater">The era's <c>.W</c> catalog the mission's <c>at_site</c> anchors draw from.</param>
/// <param name="AircraftIndex">The Hangar slot 0..5 (<c>g_active_aircraft_idx [0xC31A]</c>).</param>
/// <param name="SiteDraw">Which site of the requested type every <c>at_site</c> anchor drew.</param>
/// <param name="Difficulty">
/// <c>g_difficulty_level [0xF10E]</c> 0..3 — the index into all three admission tables
/// (<c>@0x2A10</c> probability, <c>@0x2A14</c> interval, <c>@0x2A18</c> capacity).
/// </param>
/// <param name="RandomSeed">The Sim LFSR word <c>g_prng_state [0x07A8]</c> starts at.</param>
public readonly record struct CombatColdStartRequest(
    DataTree Tree,
    MissionDefinition Mission,
    MissionDefinition Theater,
    int AircraftIndex,
    int SiteDraw,
    int Difficulty,
    ushort RandomSeed)
{
    /// <summary>
    /// Per-anchor draws, in anchor order, overriding <see cref="SiteDraw"/> where present.
    /// </summary>
    /// <remarks>
    /// A container may hold several <c>at_site</c> anchors of DIFFERENT types (ABB.S has a type-1
    /// marker and a type-6 home base), and the original draws each one independently
    /// (<c>prng_rand_bounded</c> @<c>image@0x09FD0</c>).  A single <see cref="SiteDraw"/> cannot
    /// express "the recording drew the sixth type-1 site"; this can.
    /// </remarks>
    public IReadOnlyList<int>? SiteDraws { get; init; }

    /// <summary>
    /// A step to run BETWEEN <c>scenario_load_dispatch</c>'s phase 1 and phase 2, i.e. exactly where the
    /// original LCALLs <c>custom_mission_build_from_picks @image@0x27E76</c> (<c>image@0x09379</c>, gated
    /// on <c>g_use_custom_mission_flag [0xEE04]</c>).
    /// </summary>
    /// <remarks>
    /// The CUSTOM MISSION path is the only caller: the builder has to see the containers' objects in the
    /// pool and has to write <c>[0xEE34..0xEE56]</c> before the player is spawned from them.
    /// </remarks>
    public Action<ScenarioLoadedContext>? AfterScenarioLoad { get; init; }

    /// <summary>
    /// Stage the player from the REGISTER FILE (<c>[0xEE34]</c> position, <c>[0xEE4C]</c> euler,
    /// <c>[0xEE52]</c>/<c>[0xEE56]</c> seeds) instead of re-reading the container.
    /// </summary>
    /// <remarks>
    /// That is what the original ALWAYS does (<c>image@0x093C0</c> stages from those words; the
    /// container only wrote them back at <c>image@0x0A08A</c>).  The port's normal path reads the
    /// document instead, because the loader's class-0 arm does not write the registers.  On the custom
    /// path the BUILDER is the writer, so the registers are the only truth.
    /// </remarks>
    public bool PlayerSpawnFromRegisters { get; init; }

    /// <summary>The draw list an anchor set is built from: <see cref="SiteDraws"/>, else <see cref="SiteDraw"/> repeated.</summary>
    /// <param name="anchorCount">How many anchors the container opens.</param>
    public IReadOnlyList<int> DrawsFor(int anchorCount)
    {
        int fallback = SiteDraw;
        if (SiteDraws is not { Count: > 0 } draws)
        {
            return [.. Enumerable.Repeat(fallback, anchorCount)];
        }

        int[] result = new int[Math.Max(anchorCount, draws.Count)];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = i < draws.Count ? draws[i] : fallback;
        }

        return result;
    }
}

/// <summary>
/// The COMBAT half of a mission cold start: a register file, a pool arena with the mission's objects
/// in it, and the engagement list they hang off — everything <see cref="CombatKernel.StepFrame"/>
/// needs and no trace anywhere in the chain.
/// </summary>
/// <remarks>
/// <para>
/// The chain it reproduces is <c>scene_or_mission_state_reset @image@0x0C3FD</c>'s combat half
/// (already ported as <see cref="SceneResetDoors.MissionStateResetCombatHalf"/>) wrapped around
/// <c>scenario_load_dispatch @image@0x09305</c>: phase 1 parses the containers and spawns their
/// objects (<see cref="ScenarioObjectLoader"/>), phase 2 spawns the PLAYER
/// (<c>image@0x09395..0x09456</c>) and <c>engagement_state_init @image@0x0F6BC</c> resets the
/// per-mission combat bookkeeping.
/// </para>
/// <para>
/// <b>What is deliberately different from the machine.</b>  Three things, each because the port's
/// address space is not the 8086's:
/// </para>
/// <list type="number">
///   <item><description>
///     The pool SEGMENT is a synthetic word; the arena is a managed byte array whose near offsets
///     ARE the original's.
///   </description></item>
///   <item><description>
///     The scenario loader's 24-byte staging record lives at
///     <see cref="CombatSessionState.ScratchRecordRef"/>, a slot reserved ABOVE the allocator's
///     limit, where the original uses its parser's stack frame (<c>[bp-0x9c]</c>).
///   </description></item>
///   <item><description>
///     The player's 58-byte spawn TEMPLATE is zeroed.  The original leaves 56 of its 58 bytes as
///     stack residue (<c>mov [bp-0x52],ax</c> @<c>image@0x093BD</c> writes only the first word), and
///     residue is not a contract — a deterministic port must choose, and zero is the choice.
///   </description></item>
/// </list>
/// </remarks>
public static class CombatColdStart
{
    /// <summary>How many bytes the port gives the pool arena.</summary>
    /// <remarks>
    /// An earlier pass measured the shipped missions' used region at 19,331–21,887 bytes and CONSTANT within a
    /// mission (it is filled at load), so 48 KB is roughly a 2× margin and still inside one segment.
    /// </remarks>
    public const int ArenaBytes = 0xC000;

    /// <summary>The 24-byte staging record's near offset — the first slot past the arena's limit.</summary>
    public const ushort ScratchRecordRef = ArenaBytes;

    /// <summary>The synthetic pool segment (the original's <c>far_heap_alloc</c> result).</summary>
    /// <remarks>Any non-zero word will do; zero is the "no pool" sentinel every walker tests.</remarks>
    public const ushort PoolSegment = 0x7440;

    /// <summary><c>g_flyable_statblock_ptr_table [0x0FBC]</c> — six DGROUP pointers, one per flyable.</summary>
    public const int FlyableStatBlockPointerTable = 0x0FBC;

    /// <summary><c>g_record_aircraft_data [0xEF22]</c> — the player's RUNTIME engagement prototype.</summary>
    public const int PlayerPrototype = 0xEF22;

    /// <summary>Its length: the 46-byte aircraft stat block (<c>rep movsw cx=0x17</c> @<c>image@0x093BB</c>).</summary>
    public const int PlayerPrototypeBytes = 0x2E;

    /// <summary><c>g_active_aircraft_idx [0xC31A]</c>.</summary>
    public const int ActiveAircraftIndex = 0xC31A;

    /// <summary><c>g_scenario_era_selector [0x2A0E]</c>.</summary>
    public const int EraSelector = 0x2A0E;

    /// <summary><c>g_difficulty_level [0xF10E]</c>.</summary>
    public const int DifficultyLevel = 0xF10E;

    /// <summary><c>g_alt_object_farptr [0x00C0]</c> — the PLAYER's world object.</summary>
    public const int PlayerObjectFarPointer = 0x00C0;

    /// <summary><c>[0x00C4]</c> — the second object phase 2 inserts beside the player.</summary>
    public const int SecondObjectFarPointer = 0x00C4;

    /// <summary><c>g_player_engagement_block_farptr_off [0xEF1E]</c>.</summary>
    public const int PlayerEngagementBlockFarPointer = 0xEF1E;

    /// <summary><c>g_type_resource_table [0x2550]</c> — 14 <c>{u16 type, u16 resource}</c> entries.</summary>
    public const int TypeResourceTable = 0x2550;

    /// <summary>Builds the whole combat state.</summary>
    /// <param name="request">What to build.</param>
    /// <returns>The state, ready for a <see cref="CombatKernelContext"/>.</returns>
    public static CombatSessionState Create(in CombatColdStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Tree);
        ArgumentNullException.ThrowIfNull(request.Mission);
        ArgumentNullException.ThrowIfNull(request.Theater);

        CombatRegisters registers = new CombatRegisters(SessionRegisterWindows.All);
        PoolArena arena = new PoolArena(new byte[ArenaBytes + PoolObjectAllocator.FullRecordBytes], 0);
        SessionCombatStaticData staticData = new SessionCombatStaticData(request.Tree.Constants) { Live = registers };
        SessionEngagementPrototypes prototypes = new SessionEngagementPrototypes(staticData);
        AircraftClassTable classTable = AircraftClassTable.Load(request.Tree);
        PoolObjectAllocator allocator = new PoolObjectAllocator(arena, registers, staticData);

        allocator.Initialise(PoolSegment, ArenaBytes);
        registers.SetWord(ActiveAircraftIndex, (ushort)request.AircraftIndex);
        registers.SetWord(
            EraSelector, (ushort)FlightColdStart.EraForAircraft(request.AircraftIndex));
        registers.SetByte(DifficultyLevel, (byte)request.Difficulty);
        registers.RandomState = request.RandomSeed == 0 ? (ushort)1 : request.RandomSeed;

        // The four calibration words engagement_state_init copies into the player XY bounds
        // (image@0x0F727..0x0F73C).  They are the config window, which input_mode_set defaults to
        // ±105.  The runtime does not read the ORIGINAL's saved state and the port has no joystick,
        // so the ±105 input_mode_set installs with nothing calibrated IS the port's value.
        JoystickCalibration calibration = FlightColdStart.DefaultConfigCalibration;
        registers.SetWord(0xE47C, unchecked((ushort)calibration.XMinimum));
        registers.SetWord(0xE47E, unchecked((ushort)calibration.XMaximum));
        registers.SetWord(0xE480, unchecked((ushort)calibration.YMinimum));
        registers.SetWord(0xE484, unchecked((ushort)calibration.YMaximum));

        // image@0x093A5 — the PLAYER's runtime engagement prototype: 46 bytes copied out of the
        // per-flyable stat-block table.  C1 §4.2 found this record and could not explain who wrote
        // it; this is the writer.
        ushort statBlock = staticData.Word(
            FlyableStatBlockPointerTable + (request.AircraftIndex * 2));
        registers.Write(
            PlayerPrototype, staticData.ImageSpan(statBlock, PlayerPrototypeBytes));

        SessionLifecycleEvents events = new SessionLifecycleEvents(allocator, arena) { Registers = registers };
        EngagementGeometryContext geometry = new EngagementGeometryContext
        {
            Registers = registers,
            Arena = arena,
            StaticData = staticData,
            Terrain = new SessionTerrainProximity(),
        };
        EngagementLifecycleContext lifecycle = new EngagementLifecycleContext
        {
            Geometry = geometry,
            Random = new RegisterFileCombatRandom(registers),
            Prototypes = prototypes,
            Module = NoMissionModule.Instance,
            Events = events,
        };

        AiScriptHeap scriptHeap = new AiScriptHeap();
        CountermeasureTable countermeasures = new CountermeasureTable();
        SessionEffects vmEffects = new SessionEffects(
            registers, arena, lifecycle, lifecycle.Random, countermeasures);
        PortedEngagementVm vm = new PortedEngagementVm(new EngagementVmContext
        {
            Geometry = geometry,
            Heap = scriptHeap,
            Random = lifecycle.Random,
            Prototypes = prototypes,
            Effects = vmEffects,
        });

        // session init BEFORE the mission load — gx_subsystem_init_30x1B @image@0x023B6 gives
        // every spawn slot the pool object combat_spawn_slot_alloc_and_film_record REUSES.
        int spawnSlots = CombatSubsystemInit.SpawnTable(
            registers, arena, allocator, ScratchRecordRef);

        // and the EFFECT pools: 15 smoke puffs (gx_subsystem_init_smoke @image@0x0B086) and 10
        // deferred-effect flashes (gx_subsystem_init_10x0E @image@0x03A04).  Without them every
        // effect call has no object to switch on, which is what an earlier pass measured as "counted, never
        // thrown".
        int effectObjects = CombatSubsystemInit.EffectTables(
            registers, arena, allocator, ScratchRecordRef, countermeasures);

        // And the DESTRUCTION pool: object_pool_init_3_slots @image@0x2C2B6 gives each of the
        // three s_object_slot records its pilot, seat and canopy.  Without it the slot the kill
        // path allocates points at nothing and the whole ejection sequence is invisible.
        effectObjects += CombatSubsystemInit.ObjectSlotTable(lifecycle, allocator, ScratchRecordRef);

        // And the AIRCRAFT-SHADOW table: gx_subsystem_init_4x04 @image@0x0B630 gives the five
        // shadow slots their pool objects.  Without it every non-player aeroplane flies over the
        // ground with nothing under it, which is the defect this fixes.
        effectObjects += Combat.Effects.ShadowTable.Initialise(
            registers, arena, allocator, ScratchRecordRef);

        MissionSpawnAnchors anchors = new MissionSpawnAnchors(
            request.Theater, request.DrawsFor(request.Mission.AnchorCount));

        // scenario_load_dispatch PHASE 1 parses TWO containers: the era's THEATRE (image@0x09335,
        // [bx+0xFAA] by era) and only then g_record_filename_buf [0xEF82] (image@0x0935F).  H4
        // loaded only the mission, so the pool held the aircraft and nothing else — no scenery to
        // occlude a shot, no scenery for the world grid to index.  The theatre goes first, exactly
        // as the original parses it, and its own at_site anchors are its own.
        ScenarioObjectLoader theaterLoader = new ScenarioObjectLoader(
            lifecycle,
            allocator,
            classTable,
            request.Theater,
            MissionSpawnAnchors.First(request.Theater),
            ScratchRecordRef,
            vm,
            scriptHeap);
        ScenarioObjectLoader loader = new ScenarioObjectLoader(
            lifecycle, allocator, classTable, request.Mission, anchors, ScratchRecordRef, vm,
            scriptHeap);
        ScenarioLoadSequence sequence = new ScenarioLoadSequence([theaterLoader, loader]);

        // image@0x09322: scenario_load_dispatch's LAST call before it parses the two containers
        // is nav_slot_state_reset @image@0x08CD6, so a new sortie starts on slot 0 with the
        // first-press guard down.  It writes zeros into three bytes that are already zero in a
        // fresh register file, so the reset itself is inert; what is not inert is the
        // REGISTRATION below, which is the original's own [0xEF90].
        NavSlots nav = new NavSlots(registers, request.Tree.InFlightStrings);
        nav.Reset();

        // image@0x0C49E..0x0C4C6 — the combat half of the mission reset, with the scenario load
        // inside the [0x0F0B] guard window and a sort rebuild after it.
        SceneResetDoors.MissionStateResetCombatHalf(registers, arena, sequence);

        // nav_slot_record_register @image@0x08CE4 runs INSIDE the parse, once per nav_waypoint
        // opener (image@0x0A118).  The port's loaders collect the openers instead of calling it, so
        // the registrations are replayed here in the same stream order — the theatre's first, the
        // mission's second, so a slot the mission re-uses wins.  (No shipped .W theatre registers a
        // waypoint; the loop is there because the parser's dispatch is the same for both
        // containers.)
        foreach (NavWaypointPlacement waypoint in theaterLoader.NavWaypoints)
        {
            nav.Register(waypoint);
        }

        foreach (NavWaypointPlacement waypoint in loader.NavWaypoints)
        {
            nav.Register(waypoint);
        }

        // image@0x09373..0x0937E — the one step that runs between the two phases: when
        // g_use_custom_mission_flag [0xEE04] is set, custom_mission_build_from_picks overwrites the
        // player's spawn and spawns the picked enemies BEFORE phase 2 reads [0xEE34..0xEE56].
        request.AfterScenarioLoad?.Invoke(new ScenarioLoadedContext
        {
            Lifecycle = lifecycle,
            Allocator = allocator,
            ClassTable = classTable,
            Geometry = geometry,
            Vm = vm.Context,
            ScriptHeap = scriptHeap,
            ScratchRef = ScratchRecordRef,
            AircraftIndex = request.AircraftIndex,
        });

        ushort playerObject = SpawnPlayerObject(lifecycle, allocator, staticData, request, anchors);
        EngagementStateInit(registers, staticData, request.AircraftIndex);

        // The Armament screen's commit — without it [0xED1E] is 0 and the player's trigger is dead
        // (WeaponFireScheduler's fifth gate).
        WeaponLoadout.Publish(registers, staticData, request.AircraftIndex);

        // session_init_dispatcher_b @image@0x32532 — the scene load's TAIL: close the arena's list
        // (pool_arena_finalize_and_list_head @image@0x153D2), bucket it into the 5x5 cell array
        // (mesh_sort_insert_and_encode @image@0x1C7BA phase 1) and build the two quadtree arenas.
        SessionWorldGrid worldGrid = SessionWorldGrid.Build(registers, arena, staticData);

        return new CombatSessionState
        {
            WorldGrid = worldGrid,
            TheaterLoader = theaterLoader,
            Nav = nav,
            Registers = registers,
            Arena = arena,
            Allocator = allocator,
            StaticData = staticData,
            Prototypes = prototypes,
            ClassTable = classTable,
            Loader = loader,
            Anchors = anchors,
            Mission = request.Mission,
            Theater = request.Theater,
            PlayerObjectRef = playerObject,
            ScriptHeap = scriptHeap,
            VmEffects = vmEffects,
            LifecycleEvents = events,
            SpawnSlotObjects = spawnSlots,
            EffectPoolObjects = effectObjects,
            Countermeasures = countermeasures,
        };
    }

    /// <summary>
    /// <c>scenario_load_dispatch</c> PHASE 2 — the player's own world object plus the second object
    /// beside it (<c>image@0x093C0..0x09456</c>).
    /// </summary>
    /// <param name="lifecycle">The load-time lifecycle context.</param>
    /// <param name="allocator">The pool allocator.</param>
    /// <param name="staticData">The constant DGROUP surface.</param>
    /// <param name="request">The cold-start request.</param>
    /// <param name="anchors">The anchor draws.</param>
    /// <returns>The player object's near offset.</returns>
    private static ushort SpawnPlayerObject(
        EngagementLifecycleContext lifecycle,
        PoolObjectAllocator allocator,
        SessionCombatStaticData staticData,
        in CombatColdStartRequest request,
        MissionSpawnAnchors anchors)
    {
        CombatRegisters registers = lifecycle.Registers;
        PoolArena arena = lifecycle.Arena;
        PlayerObjectSpawn spawn = request.PlayerSpawnFromRegisters
            ? PlayerSpawnFromRegisterFile(registers)
            : ToObjectSpawn(FlightSpawn.FromMission(request.Mission, anchors));

        // image@0x093C0 — the staging record: flags 0x0101, then the pose out of [0xEE34]/[0xEE4C].
        Span<byte> staging = arena.Span(ScratchRecordRef, PoolObjectAllocator.FullRecordBytes);
        staging.Clear();
        arena.SetWord((ushort)(ScratchRecordRef + 0x02), 0x0101);
        WriteI32(arena, (ushort)(ScratchRecordRef + 0x06), spawn.X);
        WriteI32(arena, (ushort)(ScratchRecordRef + 0x0A), spawn.Y);
        WriteI32(arena, (ushort)(ScratchRecordRef + 0x0E), spawn.Z);
        arena.SetWord((ushort)(ScratchRecordRef + 0x12), spawn.Heading.Units);
        arena.SetWord((ushort)(ScratchRecordRef + 0x14), spawn.Pitch.Units);
        arena.SetWord((ushort)(ScratchRecordRef + 0x16), spawn.Roll.Units);

        Span<byte> template = stackalloc byte[SpawnDispatchObject.TemplateBytes];
        template.Clear();
        template[0] = unchecked((byte)PlayerPrototype);
        template[1] = (byte)(PlayerPrototype >> 8);

        uint inserted = SpawnDispatchObject.Run(         // image@0x093F7
            lifecycle,
            template,
            ScratchRecordRef,
            ScenarioObjectLoader.RenderListParentRecord,
            flag2: 0,
            compactBlock: 0,
            engageFlag: 0);
        ushort playerObject = unchecked((ushort)inserted);

        registers.SetWord(PlayerObjectFarPointer, playerObject);            // image@0x093FC
        registers.SetWord(PlayerObjectFarPointer + 2, allocator.Segment);
        arena.SetByte(                                                      // image@0x09407
            (ushort)(playerObject + 3),
            (byte)(arena.Byte((ushort)(playerObject + 3)) & 0xDF));

        ushort block = arena.EngagementBlockRef(playerObject);              // image@0x0940C
        registers.SetWord(PlayerEngagementBlockFarPointer, block);          // image@0x09413
        registers.SetWord(PlayerEngagementBlockFarPointer + 2, allocator.Segment);
        PushActiveTarget(registers, block);                                 // image@0x0941C

        // image@0x09421..0x09456 — a SECOND object beside the player, class = the type→resource
        // lookup on the player object's own class record.
        arena.Span(ScratchRecordRef, PoolObjectAllocator.FullRecordBytes).Clear();
        arena.SetWord(ScratchRecordRef, TypeResource(staticData, arena.Word(playerObject)));
        arena.SetWord((ushort)(ScratchRecordRef + 0x02), 1);
        ushort second = allocator.InsertWithParent(
            ScratchRecordRef, ScenarioObjectLoader.RenderListParentRecord);
        registers.SetWord(SecondObjectFarPointer, second);                  // image@0x09453
        registers.SetWord(SecondObjectFarPointer + 2, allocator.Segment);

        return playerObject;
    }

    /// <summary>
    /// The player's spawn as the ORIGINAL reads it in phase 2: out of
    /// <c>g_player_spawn_pos [0xEE34]</c> (three <c>i32</c> in OBJECT units),
    /// <c>g_player_init_euler_seed [0xEE4C]</c> and the two seeds at <c>[0xEE52]</c> / <c>[0xEE56]</c>.
    /// </summary>
    /// <param name="registers">The register file, after the custom-mission builder has written it.</param>
    /// <returns>The spawn, in the OBJECT units the register words hold.</returns>
    public static PlayerObjectSpawn PlayerSpawnFromRegisterFile(CombatRegisters registers)
    {
        ArgumentNullException.ThrowIfNull(registers);
        return new PlayerObjectSpawn(
            ReadI32(registers, CustomMissionBuild.PlayerSpawnPositionDgroupOffset),
            ReadI32(registers, CustomMissionBuild.PlayerSpawnPositionDgroupOffset + 4),
            ReadI32(registers, CustomMissionBuild.PlayerSpawnPositionDgroupOffset + 8),
            Primitives.Angle.FromUnits(registers.Word(SessionRegisterWindows.PlayerInitEuler)),
            Primitives.Angle.Wrap(
                unchecked((short)registers.Word(SessionRegisterWindows.PlayerInitEuler + 2))),
            Primitives.Angle.Wrap(
                unchecked((short)registers.Word(SessionRegisterWindows.PlayerInitEuler + 4))),
            unchecked((short)registers.Word(CustomMissionBuild.PlayerSpeedSeedDgroupOffset)),
            unchecked((short)registers.Word(CustomMissionBuild.PlayerTimerSeedDgroupOffset)));
    }

    /// <summary>The container path's <see cref="FlightSpawn"/> in the same OBJECT-unit shape.</summary>
    /// <param name="spawn">The container's spawn (world units).</param>
    /// <returns>The object-unit spawn.</returns>
    public static PlayerObjectSpawn ToObjectSpawn(FlightSpawn spawn) => new(
        spawn.ObjectX,
        spawn.ObjectY,
        spawn.ObjectZ,
        spawn.Heading,
        spawn.Pitch,
        spawn.Roll,
        spawn.InitialSpeedSeed,
        spawn.TimerSeed);

    /// <summary>
    /// <c>subsystem4x04_type_to_resource_lookup @image@0x0B6CA</c> — a linear scan of the 14-entry
    /// <c>{u16 type, u16 resource}</c> table at DGROUP <c>0x2550</c>, stride 4, zero-terminated.
    /// </summary>
    /// <param name="staticData">The constant DGROUP surface.</param>
    /// <param name="type">The type word — here the player object's class-record pointer.</param>
    /// <returns>The matching resource word, or 0.</returns>
    private static ushort TypeResource(SessionCombatStaticData staticData, ushort type)
    {
        for (int at = TypeResourceTable; staticData.Word(at) != 0; at += 4)
        {
            if (staticData.Word(at) == type)
            {
                return staticData.Word(at + 2);
            }
        }

        return 0;
    }

    /// <summary>
    /// <c>engagement_state_init @image@0x0F6BC</c> — the one-shot per-mission combat-bookkeeping
    /// reset, transliterated from the bytes.
    /// </summary>
    /// <param name="registers">The register file.</param>
    /// <param name="staticData">The constant DGROUP surface (the two per-aircraft tables).</param>
    /// <param name="aircraftIndex">The Hangar slot.</param>
    public static void EngagementStateInit(
        CombatRegisters registers, ICombatStaticData staticData, int aircraftIndex)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(staticData);

        registers.SetWord(0xBD0E, staticData.Word(0x4596 + (aircraftIndex * 2)));  // image@0x0F6C6
        registers.SetWord(0xF1CC, 0);                                              // image@0x0F6CF
        registers.Span(0xF1E0, 0x19).Clear();                                      // image@0x0F6D7
        registers.SetWord(0xF1DE, staticData.Word(0x45A2 + (aircraftIndex * 2)));  // image@0x0F6E9
        registers.SetByte(0xF1D9, 0x64);                                           // image@0x0F6EE
        registers.SetByte(0xF1D8, 0x64);                                           // image@0x0F6F1
        registers.SetWord(0xF1D0, 0);                                              // image@0x0F6F6
        registers.SetWord(0xF1CE, 0);                                              // image@0x0F6F9
        registers.SetWord(0xBD14, 0);                                              // image@0x0F6FC
        registers.SetByte(0xBD00, 0);                                              // image@0x0F701
        registers.SetByte(0xBD12, 0);                                              // image@0x0F704
        registers.SetByte(0xBD0A, 0);                                              // image@0x0F707
        registers.SetWord(0xBD06, 0x1FFC);                                         // image@0x0F70D
        registers.SetWord(0xBD0C, 0x1FFC);                                         // image@0x0F710
        registers.SetByte(0xF1DA, 0);                                              // image@0x0F715
        registers.SetByte(0xF1DB, 0);                                              // image@0x0F718
        registers.SetByte(0xF1D2, 0);                                              // image@0x0F71B
        registers.SetByte(0xF1DC, 0);                                              // image@0x0F71E
        registers.SetByte(0xEE58, 0);                                              // image@0x0F721
        registers.SetByte(0xBD04, 0);                                              // image@0x0F724
        registers.SetWord(0xC30C, registers.Word(0xE47C));                         // image@0x0F72A
        registers.SetWord(0xC310, registers.Word(0xE47E));                         // image@0x0F730
        registers.SetWord(0xC314, registers.Word(0xE480));                         // image@0x0F736
        registers.SetWord(0xC318, registers.Word(0xE484));                         // image@0x0F73C
        registers.SetWord(0xF1D6, 0);                                              // image@0x0F741
        registers.SetWord(0xF1D4, 0);                                              // image@0x0F744
    }

    private static void PushActiveTarget(CombatRegisters registers, ushort blockRef)
    {
        ushort count = registers.Word(0xEE02);
        registers.SetWord(0xEDE6 + (count * 2), blockRef);
        registers.SetWord(0xEE02, (ushort)(count + 1));
    }

    private static void WriteI32(PoolArena arena, ushort at, int value)
    {
        arena.SetWord(at, unchecked((ushort)value));
        arena.SetWord((ushort)(at + 2), unchecked((ushort)(value >> 16)));
    }

    private static int ReadI32(CombatRegisters registers, int at) =>
        registers.Word(at) | (registers.Word(at + 2) << 16);
}

/// <summary>The combat half of a live mission — everything the kernel context is built over.</summary>
public sealed class CombatSessionState
{
    /// <summary>The DGROUP combat register file.</summary>
    public required CombatRegisters Registers { get; init; }

    /// <summary>The pool arena.</summary>
    public required PoolArena Arena { get; init; }

    /// <summary>The allocator that filled it.</summary>
    public required PoolObjectAllocator Allocator { get; init; }

    /// <summary>The constant DGROUP surface.</summary>
    public required SessionCombatStaticData StaticData { get; init; }

    /// <summary>The engagement class prototypes.</summary>
    public required SessionEngagementPrototypes Prototypes { get; init; }

    /// <summary>The aircraft-class table.</summary>
    public required AircraftClassTable ClassTable { get; init; }

    /// <summary>The scenario loader, with its census and object index.</summary>
    public required ScenarioObjectLoader Loader { get; init; }

    /// <summary>The THEATRE's loader — the <c>.W</c> container parsed before the mission.</summary>
    public required ScenarioObjectLoader TheaterLoader { get; init; }

    /// <summary>
    /// The mission's NAV SLOTS: <c>[0xEF90]</c> / <c>[0xEF92]</c> / <c>[0xB563]</c> in the
    /// verified register file, with the registered waypoint records beside them.
    /// </summary>
    public required NavSlots Nav { get; init; }

    /// <summary>The world grid built after the load.</summary>
    public required SessionWorldGrid WorldGrid { get; init; }

    /// <summary>How many EFFECT pool objects the session init allocated (15 puffs + 10 flashes).</summary>
    public int EffectPoolObjects { get; init; }

    /// <summary>
    /// The five CHAFF/FLARE cloud slots (<c>gx_subsystem_init_5x1C @image@0x0AB70</c>), held
    /// outside <see cref="Registers"/> because the declared window is 26 bytes short of the real
    /// table (see <see cref="Combat.Effects.CountermeasureTable"/>).
    /// </summary>
    public required Combat.Effects.CountermeasureTable Countermeasures { get; init; }

    /// <summary>The anchor draws the load used.</summary>
    public required MissionSpawnAnchors Anchors { get; init; }

    /// <summary>The mission container.</summary>
    public required MissionDefinition Mission { get; init; }

    /// <summary>The theater catalog.</summary>
    public required MissionDefinition Theater { get; init; }

    /// <summary>The player's world object near offset.</summary>
    public required ushort PlayerObjectRef { get; init; }

    /// <summary>The AI-script far heap.</summary>
    public required AiScriptHeap ScriptHeap { get; init; }

    /// <summary>The AI script's effect calls, shared with the running session.</summary>
    public required SessionEffects VmEffects { get; init; }

    /// <summary>The lifecycle out-calls, with the allocator wired in.</summary>
    public required SessionLifecycleEvents LifecycleEvents { get; init; }

    /// <summary>How many of the 30 spawn slots got their pre-allocated pool object.</summary>
    public required int SpawnSlotObjects { get; init; }
}
