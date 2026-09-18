using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.Mission;
using CYAC.Port.Core.Model.World;
using CYAC.Port.Core.Sim.Flight;
using CYAC.Port.Core.Sim.Flight.ColdStart;
using CYAC.Port.Core.Sim.Flight.Trace;

namespace CYAC.Port.Core.Sim.Session;

/// <summary>
/// ONE historic mission, started from <c>data/</c> alone: the flight half and the combat half
/// (<see cref="CombatColdStart"/>) built together and bound into a <see cref="MissionSession"/>.
/// </summary>
/// <remarks>
/// <para>
/// The order is the original's.  <c>mission_state_machine</c> picks the record
/// (<c>scenario_bin_load @image@0x246FA</c> → <c>[0xC31A]</c> player aircraft, <c>[0xEF82]</c> the
/// <c>.S</c> name, <c>[0xF1C6]</c> the opponent stat block), <c>scene_or_mission_state_reset</c>
/// resets and loads the scene, and <c>active_aircraft_load_and_state_reset @image@0x224CA</c> then
/// builds the flight master from the spawn the load published.
/// </para>
/// <para>
/// The <c>at_site</c> DRAW is a parameter, not a computation — H2 §E2: the original draws it with
/// <c>prng_rand_bounded</c> at load time (<c>image@0x09FD0</c>) and the port cannot reproduce a
/// PRNG state from thousands of steps earlier, so a host chooses.
/// </para>
/// </remarks>
public static class MissionColdStart
{
    /// <summary>Opens a mission by its scenario-catalog SLOT.</summary>
    /// <param name="tree">The transformed data tree.</param>
    /// <param name="slot">The picker line 0..49.</param>
    /// <param name="siteDraw">Which site of the requested type each <c>at_site</c> anchor drew.</param>
    /// <param name="difficulty">The difficulty 0..3.</param>
    /// <param name="randomSeed">The Sim LFSR word to start on.</param>
    /// <param name="clock">The host's tick clock, or null for a fresh one.</param>
    /// <param name="siteDraws">Per-anchor draws, in anchor order (overrides <paramref name="siteDraw"/>).</param>
    /// <returns>The session, ready to step.</returns>
    /// <exception cref="InvalidDataException">The catalog has no such slot, or its module is missing.</exception>
    public static MissionSession Open(
        DataTree tree,
        int slot,
        int siteDraw = 0,
        int difficulty = 1,
        ushort randomSeed = 0x1234,
        TickClock? clock = null,
        IReadOnlyList<int>? siteDraws = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        MissionEntry entry = tree.Scenarios.Entries.FirstOrDefault(e => e.Slot == slot)
                             ?? throw new InvalidDataException(
                                 $"the scenario catalog has no slot {slot} (it holds {tree.Scenarios.Count}).");
        return Open(tree, entry, siteDraw, difficulty, randomSeed, clock, siteDraws);
    }

    /// <summary>
    /// Opens a TEST FLIGHT — the same two kernels a historic mission gets, over the shipped
    /// <c>FREE.S</c> and the era's own theatre.
    /// </summary>
    /// <param name="tree">The transformed data tree.</param>
    /// <param name="aircraftIndex">Which of the six flyable aeroplanes.</param>
    /// <param name="siteDraw">Which site of the requested type each <c>at_site</c> anchor drew.</param>
    /// <param name="difficulty">The difficulty 0..3.</param>
    /// <param name="randomSeed">The Sim LFSR word to start on.</param>
    /// <param name="clock">The host's tick clock, or null for a fresh one.</param>
    /// <returns>The session, ready to step.</returns>
    /// <remarks>
    /// <para>
    /// <b>Why this exists</b>.  A Test Flight used to open a FLIGHT-ONLY session — no combat half at
    /// all, so <c>MissionSession.Trigger</c> had nowhere to go and the guns were dead, the ammunition
    /// counter was blank and no round was ever drawn.  That was invisible while <c>fly --mission N</c>
    /// was the only way in; the hangar's <c>Fly</c> made it the first thing a player meets.
    /// </para>
    /// <para>
    /// The original has no such split: <c>FREE.S</c> is a <c>.S</c> container like the other fifty
    /// and Test Flight reaches <c>flight_session_start</c> through the same scenario load, so the
    /// weapon chain (<c>weapon_fire_event_scheduler @image@0x03514</c> and the whole ladder) is
    /// armed exactly as it is in a mission.  This factory is <see cref="Open"/> with the catalogue
    /// record replaced by the two names a Test Flight knows: <c>FREE.S</c> and the era theatre of
    /// <see cref="FlightColdStart.EraForAircraft"/>.
    /// </para>
    /// <para>
    /// It is still not a MISSION: <c>FREE.S</c> places the player, his home base and one nav
    /// waypoint and nothing else — no opponent, no win rule — so the sortie has nothing to win and
    /// the host does not end it (<c>MissionOutcome</c> is not attached to a Test Flight).
    /// </para>
    /// </remarks>
    public static MissionSession TestFlight(
        DataTree tree,
        int aircraftIndex,
        int siteDraw = 0,
        int difficulty = 1,
        ushort randomSeed = 0x1234,
        TickClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        MissionDefinition mission = tree.Missions[FlightColdStart.TestFlightMissionAsset];
        MissionDefinition theater = tree.Theaters[
            MissionVocabulary.TheaterAssetForEra[(int)FlightColdStart.EraForAircraft(aircraftIndex)]];
        return Build(tree, mission, theater, aircraftIndex, siteDraw, difficulty, randomSeed, clock);
    }

    /// <summary>
    /// Opens a CUSTOM MISSION — the Test-Flight base with <c>custom_mission_build_from_picks
    /// @image@0x27E76</c> run over it.
    /// </summary>
    /// <param name="tree">The transformed data tree.</param>
    /// <param name="picks">What the CREATE MISSION form collected.</param>
    /// <param name="randomSeed">
    /// The word the <c>Sim</c> streams start on.  The original MIXES its seed from the BIOS tick,
    /// <c>prng_rand16</c> and <c>[0xF10C]</c> into <c>[0xF290]</c> (<c>image@0x27E7F..0x27EA0</c>); the
    /// port does not reproduce that, because the tick is not a contract and gives the seed to the host —
    /// the same ruling <see cref="Open"/> already carries.
    /// </param>
    /// <param name="clock">The host's tick clock, or null for a fresh one.</param>
    /// <returns>The session, ready to step.</returns>
    /// <remarks>
    /// <para>
    /// The order is the original's (<c>scenario_load_dispatch @image@0x09305</c>): the era theatre and
    /// <c>FREE.S</c> are parsed and their objects spawned FIRST, THEN the builder runs (it overwrites
    /// <c>[0xEE34..0xEE56]</c> and spawns the picked enemies), THEN phase 2 spawns the player — from the
    /// REGISTER FILE, which is what <see cref="CombatColdStartRequest.PlayerSpawnFromRegisters"/> selects.
    /// The flight half is then re-pointed at the same words, so both kernels put the player at the same
    /// X/Y/Z/heading (<see cref="Build"/>'s "over the SAME container and the SAME anchor draws" rule).
    /// </para>
    /// <para>
    /// There is no <c>difficulty</c> parameter: the original FORCES <c>g_difficulty_level [0xF10E]</c> to
    /// 1 (<c>mov byte [0xF10E],1</c> @<c>image@0x27EAF</c>) and the front-end concept's ruling is "for now
    /// we just reproduce".  A host that later wants the Diff button here needs a way to pass a difficulty
    /// through <see cref="CustomMissionBuild"/> and skip that write;
    /// </para>
    /// <para>
    /// The <c>at_site</c> draw keeps <see cref="TestFlight"/>'s default (0): <c>FREE.S</c>'s anchors are
    /// drawn the same way a Test Flight draws them, and the builder overwrites the player's placement
    /// anyway — only the theatre's own scenery still depends on the draw.
    /// </para>
    /// </remarks>
    public static MissionSession Custom(
        DataTree tree,
        CustomMissionPicks picks,
        ushort randomSeed = 0x1234,
        TickClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(picks);

        MissionDefinition mission = tree.Missions[FlightColdStart.TestFlightMissionAsset];
        MissionDefinition theater = tree.Theaters[
            MissionVocabulary.TheaterAssetForEra[picks.Era]];
        RandomStreams streams = RandomStreams.FromBinarySeed(randomSeed);
        CustomMissionFormations formations = tree.CustomMissionFormations;
        CustomMissionAltitudes altitudes = tree.CustomMissionAltitudes;

        CustomMissionBuildResult? built = null;
        MissionSession session = Build(
            tree,
            mission,
            theater,
            picks.PlayerAircraftRow,
            siteDraw: 0,
            difficulty: 1,                      // image@0x27EAF — forced, not a parameter
            randomSeed,
            clock,
            siteDraws: null,
            afterScenarioLoad: context => built = CustomMissionBuild.Run(context, picks, streams, formations, altitudes),
            playerSpawnFromRegisters: true);

        session.CustomBuild = built
            ?? throw new InvalidOperationException(
                "the custom-mission build did not run; CombatColdStart.AfterScenarioLoad was not "
                    + "invoked, which means the load sequence changed shape.");
        session.CustomPicks = picks;
        return session;
    }

    /// <summary>Opens a mission from its catalog entry.</summary>
    /// <param name="tree">The transformed data tree.</param>
    /// <param name="entry">The catalog record.</param>
    /// <param name="siteDraw">Which site of the requested type each <c>at_site</c> anchor drew.</param>
    /// <param name="difficulty">The difficulty 0..3.</param>
    /// <param name="randomSeed">The Sim LFSR word to start on.</param>
    /// <param name="clock">The host's tick clock, or null for a fresh one.</param>
    /// <param name="siteDraws">Per-anchor draws, in anchor order (overrides <paramref name="siteDraw"/>).</param>
    /// <returns>The session, ready to step.</returns>
    /// <exception cref="InvalidDataException">The mission's <c>.S</c> or theater is missing.</exception>
    public static MissionSession Open(
        DataTree tree,
        MissionEntry entry,
        int siteDraw = 0,
        int difficulty = 1,
        ushort randomSeed = 0x1234,
        TickClock? clock = null,
        IReadOnlyList<int>? siteDraws = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(entry);

        MissionDefinition mission = tree.Container(entry.ModuleAssetName)
                                    ?? throw new InvalidDataException(
                                        $"slot {entry.Slot} names {entry.ModuleAssetName}, which the data tree has no "
                                        + "document for (regenerate it with cyac-transform).");
        MissionDefinition theater = tree.Container(entry.TheaterAssetName)
                                    ?? throw new InvalidDataException(
                                        $"the era's theater {entry.TheaterAssetName} is not in the data tree.");

        return Build(
            tree, mission, theater,
            mission.PlayerAircraftIndex ?? entry.PlayerAircraftIndex,
            siteDraw, difficulty, randomSeed, clock, siteDraws);
    }

    /// <summary>Builds both kernels over one container pair — the body <see cref="Open"/> and
    /// <see cref="TestFlight"/> share.</summary>
    /// <param name="tree">The transformed data tree.</param>
    /// <param name="mission">The <c>.S</c> container to fly.</param>
    /// <param name="theater">The <c>.W</c> theatre it is flown over.</param>
    /// <param name="aircraftIndex">Which of the six flyable aeroplanes.</param>
    /// <param name="siteDraw">Which site of the requested type each <c>at_site</c> anchor drew.</param>
    /// <param name="difficulty">The difficulty 0..3.</param>
    /// <param name="randomSeed">The Sim LFSR word to start on.</param>
    /// <param name="clock">The host's tick clock, or null for a fresh one.</param>
    /// <param name="siteDraws">Per-anchor draws, in anchor order.</param>
    /// <param name="afterScenarioLoad">
    /// The step that runs between phase 1 and phase 2 — the custom-mission builder, or null.
    /// </param>
    /// <param name="playerSpawnFromRegisters">
    /// Stage the player from <c>[0xEE34..0xEE56]</c> rather than the container (the custom path).
    /// </param>
    private static MissionSession Build(
        DataTree tree,
        MissionDefinition mission,
        MissionDefinition theater,
        int aircraftIndex,
        int siteDraw,
        int difficulty,
        ushort randomSeed,
        TickClock? clock,
        IReadOnlyList<int>? siteDraws = null,
        Action<ScenarioLoadedContext>? afterScenarioLoad = null,
        bool playerSpawnFromRegisters = false)
    {
        CombatSessionState combat = CombatColdStart.Create(new CombatColdStartRequest(
            tree, mission, theater, aircraftIndex, siteDraw, difficulty, randomSeed)
        {
            SiteDraws = siteDraws,
            AfterScenarioLoad = afterScenarioLoad,
            PlayerSpawnFromRegisters = playerSpawnFromRegisters,
        });

        MissionSpawnAnchors anchors = combat.Anchors;
        // The flight half, over the SAME container and the SAME anchor draws, so both kernels put
        // the player in the same place (FlightColdStart.TestFlight's mission-agnostic sibling).
        string basename = Model.Flight.AircraftDefinition.FlyableBasenames[aircraftIndex];
        FlightSeedResult flight = FlightColdStart.Create(new FlightColdStartRequest
        {
            Aircraft = tree.Aircraft[basename],
            Mission = mission,
            Anchors = anchors,
            // The built-in ±105, not the tree's config.json (the port has no joystick).
            ConfigCalibration = FlightColdStart.DefaultConfigCalibration,
            PullUpTuning = FlightColdStart.PullUpTuning(tree),
            PlayerClass = Model.World.ClassRegistry.IsLoaded
                ? Model.World.ClassRegistry.Find(basename)
                : null,
        }, out AircraftPoseResult? pose);

        if (playerSpawnFromRegisters)
        {
            // The BOTH-KERNELS rule.  FlightColdStart reads the player's spawn off the container
            // (FlightSpawn.FromMission), which on this path is FREE.S's parked placement — but the
            // builder has just overwritten [0xEE34..0xEE56], and phase 2 above already staged the
            // combat object from those words.  Re-point the flight half at the same words, in the
            // order active_aircraft_load_and_state_reset @image@0x224CA does it: the object's pose,
            // then the altitude gate (image@0x224F8), then the [0xEE52] speed seed (image@0x22525).
            // (A `FlightColdStartRequest.PlayerSpawnOverride` would be the cleaner seam; it is
            // outside this file's scope.)
            pose = RepointPlayerSpawn(
                flight, CombatColdStart.PlayerSpawnFromRegisterFile(combat.Registers));
        }

        MissionWorld world = new MissionWorld
        {
            Zones = LandingZoneTable.FromTheater(
                theater, LandingZoneTable.ShippedCaptureRadius(tree)),
            State = flight.State,
        };

        return new MissionSession(combat, flight.State, clock ?? new TickClock(), world, new NoKernelRandom())
        {
            ColdStartPose = pose,
            FlightSeed = flight,
        };
    }

    /// <summary>
    /// Moves an already-built flight half onto a different spawn — <c>active_aircraft_load_and_state_reset
    /// @image@0x224CA</c>'s three steps, re-run over the pose the CUSTOM-MISSION builder published.
    /// </summary>
    /// <param name="flight">The flight seed <see cref="FlightColdStart.Create(FlightColdStartRequest, out AircraftPoseResult?)"/> produced.</param>
    /// <param name="spawn">The spawn the register file now holds, in OBJECT units.</param>
    /// <returns>The pose-setter's result when the altitude gate opened, else null.</returns>
    private static AircraftPoseResult? RepointPlayerSpawn(
        Flight.Trace.FlightSeedResult flight, PlayerObjectSpawn spawn)
    {
        WorldObject player = flight.State.Player;
        player.X = spawn.X;
        player.Y = spawn.Y;
        player.Z = spawn.Z;
        player.Heading = spawn.Heading;
        player.Pitch = spawn.Pitch;
        player.Roll = spawn.Roll;

        AircraftPoseResult? pose = AircraftPoseSet.GateOpens(player.Y)   // image@0x224F8
            ? AircraftPoseSet.Apply(
                flight.State.Aircraft, player, (player.X, player.Y, player.Z), clearEuler: false)
            : null;

        if (spawn.InitialSpeedSeed != FlightSpawn.NoSeed)          // image@0x22525
        {
            flight.State.Aircraft.ForwardVelocity.Value = spawn.InitialSpeedSeed << 8;
        }

        return pose;
    }
}

/// <summary>The flight kernel's world seam for a running mission.</summary>
/// <remarks>
/// The same shape as the host's Test-Flight world (H2 §E4): the theater's own type-2 / type-6 sites
/// ARE the landing-zone table <c>crash_secondary_check @image@0x09255</c> scans, and a false answer
/// is not neutral — it makes every ground contact fatal.
/// </remarks>
public sealed class MissionWorld : IKernelWorld
{
    /// <summary>The theater's landing zones.</summary>
    public LandingZoneTable? Zones { get; set; }

    /// <summary>Where the query reads the player from.</summary>
    public FlightKernelState? State { get; set; }

    /// <summary>HUD warnings the kernel posted.</summary>
    public long HudPosts { get; private set; }

    /// <summary>Flight-advisor dispatches.</summary>
    public long Advisories { get; private set; }

    /// <summary>The most recent HUD message id, or −1.</summary>
    public int LastHudMessageId { get; private set; } = -1;

    /// <summary>The most recent advisory class, or −1.</summary>
    public int LastAdvisoryClass { get; private set; } = -1;

    /// <inheritdoc/>
    public void PostHudWarning(int messageId, int messageTable)
    {
        HudPosts++;
        LastHudMessageId = messageId;
    }

    /// <inheritdoc/>
    public void DispatchFlightAdvisor(byte advisoryClass)
    {
        Advisories++;
        LastAdvisoryClass = advisoryClass;
    }

    /// <inheritdoc/>
    public bool IsWithinLandingZone() =>
        Zones is not null && State is not null && Zones.Contains(State.Player.X, State.Player.Z);
}

/// <summary>
/// The flight kernel's own <c>prng_rand8</c> seam.  An earlier pass measured that the one site (the state-3
/// pull-up arm) never fires on shipped data; a draw here would perturb nothing the combat side
/// depends on, because the combat LFSR is a different word.
/// </summary>
public sealed class NoKernelRandom : IKernelRandom
{
    /// <summary>How many draws the flight kernel made.</summary>
    public int Draws { get; private set; }

    /// <inheritdoc/>
    public byte NextRand8()
    {
        Draws++;
        return 0;
    }
}
