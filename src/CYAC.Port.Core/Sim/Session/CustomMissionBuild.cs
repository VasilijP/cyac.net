using System.Collections.Immutable;
using CYAC.Port.Core.Model.Mission;
using CYAC.Port.Core.Primitives;
using CYAC.Port.Core.Sim.Combat;
using CYAC.Port.Core.Sim.Combat.Geometry;
using CYAC.Port.Core.Sim.Combat.Lifecycle;
using CYAC.Port.Core.Sim.Combat.Vm;

namespace CYAC.Port.Core.Sim.Session;

/// <summary>
/// Everything a step that runs BETWEEN <c>scenario_load_dispatch</c>'s phase 1 (the two containers'
/// objects) and its phase 2 (the player) needs.
/// </summary>
/// <remarks>
/// The original has exactly one such step — <c>custom_mission_build_from_picks</c>, LCALL'd at
/// <c>image@0x09379</c> when <c>g_use_custom_mission_flag [0xEE04]</c> is set — so this seam exists for
/// it.  <see cref="CombatColdStartRequest.AfterScenarioLoad"/> carries it.
/// </remarks>
public sealed class ScenarioLoadedContext
{
    /// <summary>The load-time lifecycle context (registers, arena, prototypes, events, random).</summary>
    public required EngagementLifecycleContext Lifecycle { get; init; }

    /// <summary>The pool allocator the load used.</summary>
    public required PoolObjectAllocator Allocator { get; init; }

    /// <summary>The 46-entry aircraft-class table (<c>aircraft_class_table_lookup @image@0x24058</c>).</summary>
    public required AircraftClassTable ClassTable { get; init; }

    /// <summary>The geometry context, for the arc-descriptor install.</summary>
    public required EngagementGeometryContext Geometry { get; init; }

    /// <summary>The VM context a freshly compiled program runs on.</summary>
    public required EngagementVmContext Vm { get; init; }

    /// <summary>The AI-script far heap.</summary>
    public required IAiScriptHeap ScriptHeap { get; init; }

    /// <summary>The arena near offset of the 24-byte staging record.</summary>
    public required ushort ScratchRef { get; init; }

    /// <summary>The hangar slot 0..5 the session was opened with.</summary>
    public required int AircraftIndex { get; init; }
}

/// <summary>
/// The player's spawn exactly as the register file holds it — positions in OBJECT units, not the world
/// units <c>FlightSpawn</c> carries.
/// </summary>
/// <remarks>
/// The distinction is load-bearing here, not pedantry: the builder's X and Z are <c>(draw + 0x40) x
/// 264,000</c> (<c>image@0x27EB4</c>) and 264,000 is NOT a multiple of 256, so a round trip through
/// world units would silently truncate the low byte.  <c>FlightSpawn</c> is the CONTAINER's view (world
/// feet, shifted up for the object); this is the REGISTER's view, which is what
/// <c>scenario_load_dispatch</c> phase 2 stages from (<c>image@0x093C0</c>).
/// </remarks>
/// <param name="X">The object's X (<c>[0xEE34]</c>).</param>
/// <param name="Y">Its Y (<c>[0xEE38]</c>) — the altitude, <c>feet &lt;&lt; 8</c>.</param>
/// <param name="Z">Its Z (<c>[0xEE3C]</c>).</param>
/// <param name="Heading">Its heading (<c>[0xEE4C]</c>).</param>
/// <param name="Pitch">Its pitch (<c>[0xEE4E]</c>) — negative for a verb-0 dive.</param>
/// <param name="Roll">Its roll (<c>[0xEE50]</c>).</param>
/// <param name="InitialSpeedSeed"><c>g_player_init_speed_seed [0xEE52]</c>.</param>
/// <param name="TimerSeed"><c>g_player_init_timer_seed [0xEE56]</c>.</param>
public readonly record struct PlayerObjectSpawn(
    int X,
    int Y,
    int Z,
    Angle Heading,
    Angle Pitch,
    Angle Roll,
    short InitialSpeedSeed,
    short TimerSeed);

/// <summary>What one custom-mission build produced.</summary>
/// <param name="PlayerSpawn">
/// The pose the builder wrote into <c>[0xEE34..0xEE56]</c>, in the shape both kernels read it.
/// </param>
/// <param name="FormationHeading">
/// The formation's heading word <c>[bp-0x30]</c> — every enemy's heading, and the angle their authored
/// formation offsets were rotated by.
/// </param>
/// <param name="FormationSpeed">
/// <c>[bp-0xCC]</c> — the slowest of the per-enemy arc-descriptor speeds, given to every enemy.
/// </param>
/// <param name="EnemyObjects">The arena near offsets the spawn returned, in spawn order.</param>
/// <param name="MissionDraws">
/// How many bits the <c>Sim.Mission</c> stream advanced, for the determinism test's pin.
/// </param>
/// <param name="FireCountdownArmed">
/// Whether <c>[0xED6B]</c> was armed — true only for verb row 0 (<c>image@0x28307</c>).
/// </param>
public readonly record struct CustomMissionBuildResult(
    PlayerObjectSpawn PlayerSpawn,
    ushort FormationHeading,
    short FormationSpeed,
    bool FireCountdownArmed,
    ImmutableArray<ushort> EnemyObjects,
    long MissionDraws);

/// <summary>
/// <c>custom_mission_build_from_picks @image@0x27E76</c> (0x4EC = 1,260 B, FAR) — the CREATE MISSION
/// form's picks turned into live engine state: the player's own spawn is OVERWRITTEN, then the picked
/// enemies are placed in a verb-dependent formation and armed.
/// </summary>
/// <remarks>
/// <para>
/// It runs over <c>FREE.S</c> (the module <c>scenario_record_filename_set_freeflight @image@0x24A16</c>
/// installs), between phase 1 and phase 2 of <c>scenario_load_dispatch</c> — so everything it writes
/// into <c>[0xEE34..0xEE56]</c> is what phase 2 and
/// <c>active_aircraft_load_and_state_reset @image@0x224CA</c> then read.  That is why
/// <see cref="CombatColdStartRequest.PlayerSpawnFromRegisters"/> exists: on this path the register file,
/// not the container, is the player's spawn.
/// </para>
/// <para>
/// Phases, with the <c>image@</c> each ports (the full byte walk is):
/// </para>
/// <list type="bullet">
///   <item><description><b>A/B</b> <c>image@0x27E7F..0x27F0B</c> — three flags, then the player's X and
///   Z as <c>(prng_rand_bounded(0x80) + 0x40) × 0x0004_0740</c> and his heading as
///   <c>prng_rand_bounded(0xB40)</c>.  The seed MIX (<c>[0xF290]</c> out of the BIOS tick,
///   <c>prng_rand16</c> and <c>[0xF10C]</c>) is deliberately NOT reproduced: the tick is not a
///   ruling <see cref="MissionColdStart.Open"/> carries).</description></item>
///   <item><description><b>C</b> <c>image@0x27F0B..0x2812A</c> — the walk over the stride-2 value
///   array, with arms for types 0, 1, 2, 4 and 7.</description></item>
///   <item><description><b>D</b> <c>image@0x2813C..0x2833F</c> — the player's opening airspeed, the
///   formation's AABB centring, and one <see cref="EngagementSlotSpawnInit"/> per
///   enemy.</description></item>
///   <item><description><b>E</b> <c>image@0x28344..0x28361</c> — a page flip.  PRESENTATION;
///   omitted, because it writes no simulation state.</description></item>
/// </list>
/// <para>
/// Determinism: every draw inside the builder goes through the <c>Sim.Mission</c> stream, which is what
/// assigns to all 18 of its call sites.  The two draws made by routines it CALLS —
/// <c>spawn_slot_idx_by_alliance_roll @image@0x2837C</c> and the fire countdown in
/// <c>engagement_slot_spawn_init @image@0x06E3B</c> — are assigned <c>Sim.AI</c> by the same map, and
/// come from there.
/// </para>
/// </remarks>
public static class CustomMissionBuild
{
    /// <summary>
    /// The 32-bit multiplicand the spawn draws are scaled by: <c>0x0004_0740</c> = 264,000
    /// (<c>mov ax,0x740 / mov dx,4 / push dx / push ax</c> @<c>image@0x27EB4</c>, consumed by
    /// <c>mulu32 @image@0x0073E</c>).
    /// </summary>
    public const int SpawnScale = 0x0004_0740;

    /// <summary>The bound of the spawn draws: <c>prng_rand_bounded(0x80)</c> (<c>image@0x27EBC</c>).</summary>
    public const int SpawnDrawBound = 0x80;

    /// <summary>…and their offset: <c>+0x40</c> (<c>image@0x27EC4</c>).</summary>
    public const int SpawnDrawOffset = 0x40;

    /// <summary>
    /// The altitude the enemies are floored at: <c>0x0007D000</c> object units = 2,000 world feet
    /// (<c>cmp dx,7 / cmp ax,0xD000 / jae</c> @<c>image@0x282D4..0x282E9</c>).  A CONSTANT, not the
    /// <c>[0xEDA2]</c> register <c>engagement_slot_spawn_init_with_pos</c> clamps to.
    /// </summary>
    public const int EnemyAltitudeFloor = 0x0007_D000;

    /// <summary>
    /// How far apart successive CLAUSES are placed: 5,000 world feet along the clause-spacing heading
    /// (<c>mov word [bp-0x3A],0x1388</c> @<c>image@0x28102</c>).
    /// </summary>
    public const int ClauseSpacing = 0x1388;

    /// <summary>
    /// The non-allied altitude bonus: <c>+0x2EE</c> = 750 world feet, added to a slot's Y when the
    /// class prototype's <c>+0x0C</c> bit 7 is CLEAR (<c>image@0x280E9</c>).
    /// </summary>
    public const int NonAlliedAltitudeBonus = 0x2EE;

    /// <summary><c>g_scene_byte_flag_EDE1 [0xEDE1]</c> — set to 1 (<c>image@0x27EA5</c>).</summary>
    public const int SceneFlagDgroupOffset = 0xEDE1;

    /// <summary><c>g_render_byte_flag_F0E4 [0xF0E4]</c> — cleared (<c>image@0x27EAA</c>).</summary>
    public const int RenderFlagDgroupOffset = 0xF0E4;

    /// <summary><c>g_player_spawn_pos [0xEE34]</c> — three <c>i32</c> in object units.</summary>
    public const int PlayerSpawnPositionDgroupOffset = 0xEE34;

    /// <summary>
    /// The STRADDLING view of the altitude i32 <c>[0xEE38]</c>: <c>[0xEE39]</c> reads back as the
    /// altitude in world feet (<c>cmp word [0xEE39],0x2710</c> @<c>image@0x27FA8</c>).
    /// </summary>
    public const int PlayerAltitudeFeetDgroupOffset = 0xEE39;

    /// <summary><c>g_player_init_speed_seed [0xEE52]</c> — the builder's phase-D answer.</summary>
    public const int PlayerSpeedSeedDgroupOffset = 0xEE52;

    /// <summary><c>g_player_init_timer_seed [0xEE56]</c> — forced to 0x64 (<c>image@0x2815D</c>).</summary>
    public const int PlayerTimerSeedDgroupOffset = 0xEE56;

    /// <summary>…the value it is forced to: 100.</summary>
    public const ushort PlayerTimerSeedValue = 0x64;

    /// <summary><c>g_custom_mission_spawn_count [0xF292]</c> (<c>image@0x28262</c>).</summary>
    public const int SpawnCountDgroupOffset = 0xF292;

    /// <summary>The altitude gate the verb-0 arm's dive is behind: 10,000 ft (<c>image@0x27FA8</c>).</summary>
    public const int DiveAltitudeGateFeet = 0x2710;

    /// <summary>
    /// The difficulty a custom mission is FORCED to: 1 (<c>mov byte [0xF10E],1</c>
    /// @<c>image@0x27EAF</c>).  The form has no <c>Diff:</c> button and the builder takes no
    /// difficulty parameter, so a host that shows one would be inventing it.
    /// </summary>
    public const int ForcedDifficulty = 1;

    /// <summary>Runs the whole builder.</summary>
    /// <param name="context">The post-load context.</param>
    /// <param name="picks">The form's picks.</param>
    /// <param name="streams">
    /// The determinism kernel's streams: <c>Sim.Mission</c> for the builder's own 18 draw sites and
    /// <c>Sim.AI</c> for the two its callees make.
    /// </param>
    /// <param name="formations">
    /// The formation offsets <c>enemy_slot_fill_position @image@0x283A3</c> places the enemies with, from
    /// the data tree (<c>DataTree.CustomMissionFormations</c>).
    /// </param>
    /// <param name="altitudes">
    /// The altitude each ALTITUDE picker row stands for, which the type-1 arm spawns the player at, from
    /// the data tree (<c>DataTree.CustomMissionAltitudes</c>).
    /// </param>
    /// <returns>The player's spawn and the formation it built.</returns>
    public static CustomMissionBuildResult Run(
        ScenarioLoadedContext context,
        CustomMissionPicks picks,
        RandomStreams streams,
        CustomMissionFormations formations,
        CustomMissionAltitudes altitudes)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(picks);
        ArgumentNullException.ThrowIfNull(streams);
        ArgumentNullException.ThrowIfNull(formations);
        ArgumentNullException.ThrowIfNull(altitudes);

        SimRandomStream mission = streams.SimMission;
        SimRandomStream ai = streams.SimAi;
        CombatRegisters r = context.Lifecycle.Registers;
        ICombatStaticData data = context.Lifecycle.StaticData;
        long drawsBefore = mission.BitsDrawn;

        // ── phase B: the three flags (image@0x27EA5..0x27EAF) ──────────────────────────────────────
        r.SetByte(SceneFlagDgroupOffset, 1);
        r.SetByte(RenderFlagDgroupOffset, 0);
        // FORCED; no difficulty parameter exists
        r.SetByte(LifecycleOffsets.DifficultyLevel, ForcedDifficulty);

        // ── phase B: the player's X, Z and heading (image@0x27EB4..0x27F08) ────────────────────────
        int spawnX = (mission.RandBounded(SpawnDrawBound) + SpawnDrawOffset) * SpawnScale;
        int spawnZ = (mission.RandBounded(SpawnDrawBound) + SpawnDrawOffset) * SpawnScale;
        ushort spawnHeading = mission.RandBounded((ushort)Angle.FullCircle);
        WriteI32(r, PlayerSpawnPositionDgroupOffset, spawnX);                 // image@0x27ECF
        WriteI32(r, PlayerSpawnPositionDgroupOffset + 8, spawnZ);             // image@0x27EF1
        r.SetWord(SessionRegisterWindows.PlayerInitEuler, spawnHeading);      // image@0x27F00
        r.SetWord(SessionRegisterWindows.PlayerInitEuler + 4, 0);             // image@0x27F05
        r.SetWord(SessionRegisterWindows.PlayerInitEuler + 2, 0);             // image@0x27F08

        // ── phase C: the walk (image@0x27F0E..0x2812A) ─────────────────────────────────────────────
        byte[] pairs = picks.ToValueArray();
        WalkState state = new WalkState(formations);

        for (int at = 0; at + 1 < pairs.Length; at += 2)
        {
            byte type = pairs[at];
            byte row = pairs[at + 1];
            switch (type)
            {
                case CustomMissionPicks.AircraftType:                         // image@0x27F15
                    r.SetWord(CombatColdStart.ActiveAircraftIndex, row);
                    break;

                case CustomMissionPicks.AltitudeType:                         // image@0x27F56
                    WriteI32(
                        r,
                        PlayerSpawnPositionDgroupOffset + 4,
                        altitudes.Feet(row) << 8);
                    break;

                case CustomMissionPicks.VerbType:                             // image@0x27F76
                    VerbArm(r, mission, row, state);
                    break;

                case CustomMissionPicks.EnemyType:                            // image@0x2808E
                    EnemyArm(context, ai, row, pairs[at - 1], state);
                    break;

                case CustomMissionPicks.SkillType:                            // image@0x2812D
                    state.Skill = row;
                    break;

                default:
                    break;                                                    // image@0x27F54 — types 3/5/6
            }
        }

        // ── phase D: the player's opening airspeed (image@0x2813C..0x2815D) ────────────────────────
        ushort playerStatBlock = data.Word(
            CombatColdStart.FlyableStatBlockPointerTable + (picks.PlayerAircraftRow * 2));
        short playerSpeed = EngagementArcDescriptorInit.InitialiseAndSelectSpeed(
            context.Geometry,
            playerStatBlock,
            r.Word(PlayerAltitudeFeetDgroupOffset),
            averageSpeeds: state.VerbRow != 2);                               // image@0x2813C dl
        r.SetWord(PlayerSpeedSeedDgroupOffset, unchecked((ushort)playerSpeed));
        r.SetWord(PlayerTimerSeedDgroupOffset, PlayerTimerSeedValue);

        // ── phase D1/D2: the per-enemy speeds and the AABB centre (image@0x28163..0x2825C) ─────────
        short formationSpeed = 0x7FFF;                                        // image@0x28163
        int minX = 0, maxX = 0, minY = 0, maxY = 0, minZ = 0, maxZ = 0;       // image@0x28169 — all 0
        foreach (PlacedEnemy enemy in state.Enemies)
        {
            short speed = EngagementArcDescriptorInit.InitialiseAndSelectSpeed(
                context.Geometry,
                enemy.PrototypeRef,
                unchecked((ushort)(enemy.Y + StraddleWord(state.OriginObject[1]))),  // image@0x28195
                averageSpeeds: state.VerbRow != 0);                           // image@0x281A1 dl
            if (speed < formationSpeed)
            {
                formationSpeed = speed;                                       // image@0x281B7
            }

            minX = Math.Min(minX, enemy.X);
            maxX = Math.Max(maxX, enemy.X);
            minY = Math.Min(minY, enemy.Y);
            maxY = Math.Max(maxY, enemy.Y);
            minZ = Math.Min(minZ, enemy.Z);
            maxZ = Math.Max(maxZ, enemy.Z);
        }

        int centreX = -((maxX + minX) / 2);                                   // image@0x2822A
        int centreY = -((maxY + minY) / 2);                                   // image@0x2823C
        int centreZ = -((maxZ + minZ) / 2);                                   // image@0x2824E
        r.SetWord(SpawnCountDgroupOffset, (ushort)state.Enemies.Count);       // image@0x28262

        // ── phase D3: place and arm each enemy (image@0x28274..0x2833F) ────────────────────────────
        bool countdown = state.VerbRow == 0;                                  // image@0x28307
        ImmutableArray<ushort>.Builder spawned = ImmutableArray.CreateBuilder<ushort>(state.Enemies.Count);
        foreach (PlacedEnemy enemy in state.Enemies)
        {
            short rotatedX = unchecked((short)(enemy.X + centreX));           // image@0x28277
            short rotatedZ = unchecked((short)(enemy.Z + centreZ));           // image@0x2827F
            Vector2RotateAroundPivot.RotateInPlace(                           // image@0x28293
                unchecked((short)state.FormationHeading), 0, 0, ref rotatedX, ref rotatedZ);

            int worldX = (rotatedX << 8) + state.OriginObject[0];             // image@0x28298..0x282AF
            int worldY = ((enemy.Y + centreY) << 8) + state.OriginObject[1];  // image@0x282B3..0x282D0
            if (worldY < EnemyAltitudeFloor)                                  // image@0x282D4
            {
                worldY = EnemyAltitudeFloor;                                  // image@0x282E0
            }

            int worldZ = (rotatedZ << 8) + state.OriginObject[2];             // image@0x282EC..0x28303

            spawned.Add(EngagementSlotSpawnInit.Run(                          // image@0x2832A
                context.Lifecycle,
                context.Vm,
                context.ScriptHeap,
                context.ScratchRef,
                enemy.PrototypeRef,
                new CombatPosition(worldX, worldY, worldZ),
                (state.FormationHeading, 0, 0),
                formationSpeed,
                (ushort)state.Skill,
                countdown,
                new SimStreamCombatRandom(ai)));
        }

        return new CustomMissionBuildResult(
            CombatColdStart.PlayerSpawnFromRegisterFile(r),
            state.FormationHeading,
            formationSpeed,
            countdown,
            spawned.ToImmutable(),
            mission.BitsDrawn - drawsBefore);
    }

    /// <summary>
    /// The VERB arm — <c>image@0x27F76..0x2808B</c>, the three sub-variants the 2026-05 decode elided.
    /// </summary>
    /// <param name="r">The register file.</param>
    /// <param name="mission">The <c>Sim.Mission</c> stream.</param>
    /// <param name="verbRow">The verb pick 0..2.</param>
    /// <param name="state">The walk's locals.</param>
    private static void VerbArm(
        CombatRegisters r, SimRandomStream mission, byte verbRow, WalkState state)
    {
        // image@0x27F76 — the player's position triple and pose angles into the frame.
        state.OriginObject[0] = ReadI32(r, PlayerSpawnPositionDgroupOffset);
        state.OriginObject[1] = ReadI32(r, PlayerSpawnPositionDgroupOffset + 4);
        state.OriginObject[2] = ReadI32(r, PlayerSpawnPositionDgroupOffset + 8);
        state.FormationHeading = r.Word(SessionRegisterWindows.PlayerInitEuler);
        state.VerbRow = verbRow;                                              // image@0x27F93

        short elevation;
        short displacement;
        int distance;

        if (verbRow == 0)
        {
            // ── "jumped": same-heading, AHEAD and BELOW, 5,000..7,999 ft ──────────────────────────
            state.FormationHeading = Add(state.FormationHeading, mission.RandCenteredN16(0x19));

            // image@0x27FA8 — at or above 10,000 ft the PLAYER starts NOSE-DOWN 10°..40°.
            if (r.Word(PlayerAltitudeFeetDgroupOffset) >= DiveAltitudeGateFeet)
            {
                short pitch = unchecked((short)-(mission.RandBounded(0xF0) + 0x50));
                r.SetWord(                                                    // image@0x27FBD
                    SessionRegisterWindows.PlayerInitEuler + 2, unchecked((ushort)pitch));
            }

            displacement = unchecked((short)(                                 // image@0x27FC6
                mission.RandCenteredN16(0x28) + r.Word(SessionRegisterWindows.PlayerInitEuler)));
            elevation = unchecked((short)(                                    // image@0x27FD1
                unchecked((short)r.Word(SessionRegisterWindows.PlayerInitEuler + 2))
                    - mission.RandBounded(0xC8)));
            distance = mission.RandBounded(0xBB8) + 0x1388;                   // image@0x27FE0
        }
        else if (verbRow == 1)
        {
            // ── "saw": head-on, AHEAD and LEVEL, 5,000..7,999 ft ─────────────────────────────────
            state.FormationHeading = Add(                                     // image@0x27FF6
                state.FormationHeading,
                unchecked((short)(mission.RandCenteredN16(0x1E) + Angle.HalfCircle)));
            displacement = unchecked((short)(                                 // image@0x2800A
                mission.RandCenteredN16(0x1E) + r.Word(SessionRegisterWindows.PlayerInitEuler)));
            elevation = mission.RandCenteredN16(0xA);                         // image@0x28015
            distance = mission.RandBounded(0xBB8) + 0x1388;                   // image@0x27FE0 (shared)
        }
        else
        {
            // ── "was jumped by": same-heading, BEHIND and ABOVE, 3,000..4,999 ft ─────────────────
            state.FormationHeading = Add(                                     // image@0x28020
                state.FormationHeading, mission.RandCenteredN16(0x28));
            displacement = unchecked((short)(                                 // image@0x28031
                mission.RandCenteredN16(0xA) + state.FormationHeading + Angle.HalfCircle));
            elevation = unchecked((short)(mission.RandBounded(0xA0) + 0xF0));  // image@0x2803E
            distance = mission.RandBounded(0x7D0) + 0xBB8;                    // image@0x2804A
        }

        // image@0x28055..0x2805F — accumulate the polar displacement into the player's position.
        CombatPosition accumulated = CombatGeometry.AccumulateDistance3d(
            new CombatPosition(state.OriginObject[0], state.OriginObject[1], state.OriginObject[2]),
            distance << 8,
            elevation,
            displacement);
        state.OriginObject[0] = accumulated.X;
        state.OriginObject[1] = accumulated.Y;
        state.OriginObject[2] = accumulated.Z;

        state.ClauseOrigin[0] = 0;                                            // image@0x28070 memset 6
        state.ClauseOrigin[1] = 0;
        state.ClauseOrigin[2] = 0;
        state.ClauseSpacingHeading = Angle.Wrap(                              // image@0x2807B..0x28088
            unchecked((short)(mission.RandBounded(0x1E0) - 0xF0))).Units;
    }

    /// <summary>The ENEMY arm — <c>image@0x2808E..0x2812A</c>.</summary>
    /// <param name="context">The post-load context.</param>
    /// <param name="ai">
    /// The <c>Sim.AI</c> stream — the arm's ONLY draw is the alliance roll, which attributes to
    /// <c>spawn_slot_idx_by_alliance_roll</c>'s own entry, not to the builder's.
    /// </param>
    /// <param name="classId">The picked class id.</param>
    /// <param name="count">
    /// The PRECEDING pair's row byte — the original reads it as <c>local_e[-1]</c>
    /// (<c>mov al,[bx-1]</c> @<c>image@0x280F8</c>).
    /// </param>
    /// <param name="state">The walk's locals.</param>
    private static void EnemyArm(
        ScenarioLoadedContext context,
        SimRandomStream ai,
        byte classId,
        byte count,
        WalkState state)
    {
        ICombatStaticData data = context.Lifecycle.StaticData;
        if (context.ClassTable.Lookup(classId) is not { } entry || !entry.IsEngagementPrototype)
        {
            // The original would spawn whatever AX came back as; the port refuses a class the table
            // does not answer with a prototype (the ENEMY picker offers none such).
            return;
        }

        ushort prototypeRef = entry.Value;                                    // image@0x2809A
        bool allied = (data.Byte(prototypeRef + 0x0C) & 0x80) != 0;
        int row = FormationRow(ai, allied);                                   // image@0x280A4

        for (int i = 0; i < count && state.Enemies.Count < CustomMissionPicks.MaxEnemies; i++)
        {
            (short X, short Y, short Z) offset = state.Formations.Offset(row, i);                 // image@0x280E0
            int x = offset.X + state.ClauseOrigin[0];
            int y = offset.Y + state.ClauseOrigin[1];
            int z = offset.Z + state.ClauseOrigin[2];
            if (allied)
            {
                // image@0x283DD..0x283F9 — a 1.5x looser formation, the halving truncating toward zero.
                x += offset.X / 2;
                y += offset.Y / 2;
                z += offset.Z / 2;
            }
            else
            {
                y += NonAlliedAltitudeBonus;                                  // image@0x280E9
            }

            state.Enemies.Add(new PlacedEnemy(prototypeRef, classId, x, y, z));
        }

        // image@0x28102..0x28126 — step the clause origin 5,000 ft along the clause-spacing heading.
        short stepX = ClauseSpacing;
        short stepZ = 0;
        Vector2RotateAroundPivot.RotateInPlace(
            unchecked((short)state.ClauseSpacingHeading), 0, 0, ref stepX, ref stepZ);
        state.ClauseOrigin[0] += stepX;
        state.ClauseOrigin[2] += stepZ;
    }

    /// <summary>
    /// <c>spawn_slot_idx_by_alliance_roll @image@0x2837C</c> — which of the three formation rows this
    /// clause flies.
    /// </summary>
    /// <param name="ai">The <c>Sim.AI</c> stream (the map's assignment for both of its draws).</param>
    /// <param name="allied">The class prototype's <c>+0x0C</c> bit 7.</param>
    /// <returns>Row 1 or 2 for an allied class, 0 or 1 otherwise.</returns>
    public static int FormationRow(SimRandomStream ai, bool allied)
    {
        ArgumentNullException.ThrowIfNull(ai);
        return allied
            ? (ai.Rand8() < 0x80 ? 2 : 1)                                     // image@0x28382..0x28391
            : (ai.Rand8() >= 0x80 ? 1 : 0);                                   // image@0x28396..0x283A0
    }

    private static ushort Add(ushort angle, short delta) =>
        Angle.FromUnits(angle).Add(delta).Units;                              // image@0x1842A

    /// <summary>
    /// The original's STRADDLING read of an <c>i32</c>'s bytes 1..2 — <c>mov ax,[bp-0xC1]</c> on the
    /// 12-byte origin block (<c>image@0x28195</c>), i.e. the value <c>&gt;&gt; 8</c> taken as a word.
    /// </summary>
    /// <param name="value">The 32-bit value whose middle word is wanted.</param>
    /// <returns>Its bytes 1..2 as a signed word.</returns>
    private static short StraddleWord(int value) => unchecked((short)(value >> 8));

    private static void WriteI32(CombatRegisters r, int at, int value)
    {
        r.SetWord(at, unchecked((ushort)value));
        r.SetWord(at + 2, unchecked((ushort)(value >> 16)));
    }

    private static int ReadI32(CombatRegisters r, int at) =>
        r.Word(at) | (r.Word(at + 2) << 16);

    /// <summary>One enemy the walk recorded, before the AABB centring.</summary>
    /// <param name="PrototypeRef">Its engagement class prototype.</param>
    /// <param name="ClassId">The picked class id it came from.</param>
    /// <param name="X">Its formation X in world feet (<c>triple[0]</c>).</param>
    /// <param name="Y">Its formation Y in world feet (<c>triple[1]</c>).</param>
    /// <param name="Z">Its formation Z in world feet (<c>triple[2]</c>).</param>
    private readonly record struct PlacedEnemy(
        ushort PrototypeRef, int ClassId, int X, int Y, int Z);

    /// <param name="formations">The formation offsets the enemy arm places with.</param>
    private sealed class WalkState(CustomMissionFormations formations)
    {
        /// <summary>The formation-offset table (DGROUP data, not a frame slot).</summary>
        public CustomMissionFormations Formations { get; } = formations;

        /// <summary><c>[bp-0xC6]</c> — the formation origin in OBJECT units.</summary>
        public int[] OriginObject { get; } = new int[3];

        /// <summary><c>[bp-0xBA]</c> — the clause origin in WORLD units.</summary>
        public int[] ClauseOrigin { get; } = new int[3];

        /// <summary><c>[bp-0x30]</c> — the formation heading.</summary>
        public ushort FormationHeading { get; set; }

        /// <summary><c>[bp-0xA]</c> — the clause-spacing heading.</summary>
        public ushort ClauseSpacingHeading { get; set; }

        /// <summary><c>[bp-4]</c> — the last verb row.</summary>
        public int VerbRow { get; set; }

        /// <summary><c>[bp-0xAE]</c> — the skill row.</summary>
        public int Skill { get; set; }

        /// <summary><c>[bp-0x9C]</c> / <c>[bp-0x2A]</c> — the recorded slots.</summary>
        public List<PlacedEnemy> Enemies { get; } = new(CustomMissionPicks.MaxEnemies);
    }
}

/// <param name="stream">The stream to draw from.</param>
public sealed class SimStreamCombatRandom(SimRandomStream stream) : ICombatRandom
{
    private readonly SimRandomStream _stream =
        stream ?? throw new ArgumentNullException(nameof(stream));

    /// <inheritdoc/>
    public byte Rand8() => (byte)_stream.Rand8();

    /// <inheritdoc/>
    public int RandBounded(int bound)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bound);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bound, 0x8000);
        return _stream.RandBounded((ushort)bound);
    }
}
