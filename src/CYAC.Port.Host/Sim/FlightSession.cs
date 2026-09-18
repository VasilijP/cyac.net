using System.Globalization;
using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Model.Mission;
using CYAC.Port.Core.Model.World;
using CYAC.Port.Core.Sim;
using CYAC.Port.Core.Sim.Combat;
using CYAC.Port.Core.Sim.Flight;
using CYAC.Port.Core.Sim.Flight.ColdStart;
using CYAC.Port.Core.Sim.Flight.Trace;
using CYAC.Port.Core.Sim.Session;
using CYAC.Port.Host.Input;
using CYAC.Port.Render;

namespace CYAC.Port.Host.Sim;

/// <summary>
/// One flight the host is running: the verified integer kernel, the port's clock, its random
/// streams, and the keyboard state that feeds it.
/// </summary>
/// <remarks>
/// <para>
/// The step loop is the port's law (step 1): wall time enters exactly once, at
/// <see cref="StepPresenter"/>, which turns elapsed host seconds into whole 5-tick simulation
/// steps; every step then runs <c>FlightKernel.Step(state, inputs, clock, streams, world)</c> —
/// the <see cref="TickClock"/> overload, so <c>dt</c> is the clock's and time compression
/// multiplies STEPS, never <c>dt</c>.
/// </para>
/// <para>
/// The initial state now comes from <see cref="FromColdStart"/> — <see cref="FlightColdStart"/>
/// builds it from the data tree alone (step 2).  <see cref="FromTrace"/> survives as the
/// VERIFICATION tool: it is what <c>--replay</c> seeds from, and what a
/// developer uses to start mid-sortie.
/// </para>
/// </remarks>
public sealed class FlightSession
{
    /// <summary>
    /// How many simulation steps one host frame may run before the surplus is discarded.
    /// </summary>
    /// <remarks>
    /// <see cref="TickClock.MaxStepsPerHostFrame"/> (128) is the port's absolute ceiling; a PoC that
    /// stalls for two seconds should drop the time, not spend a quarter of a second catching up.
    /// The surplus is dropped, never banked — the same rule the original used when it clamped its
    /// measured dt and then reset the tick baseline to now (<c>image@0x0C179</c>).
    /// </remarks>
    public const int MaxCatchUpSteps = 8;

    private readonly HostWorld _world = new();
    private TickClock _clock = new();
    private readonly StepPresenter _presenter;
    private readonly RandomStreams _streams;
    private readonly KeyboardStick _stick;

    private FlightSession(
        FlightKernelState state,
        AircraftDefinition definition,
        string aircraftBasename,
        bool latchingStick)
    {
        State = state;
        Definition = definition;
        AircraftBasename = aircraftBasename;
        _presenter = new StepPresenter(_clock);
        _streams = RandomStreams.FromMasterSeed(SessionSeed);
        _stick = new KeyboardStick { Latching = latchingStick };
        SeedGearAngle();
    }

    /// <summary>
    /// The master seed the PoC runs on.  Fixed so two runs of the same script are identical; the
    /// kernel's single <c>prng_rand8</c> draw never fires on shipped data anyway.
    /// </summary>
    public const ulong SessionSeed = 0x0000_C1AC_0000_0001UL;

    /// <summary>The kernel state, mutated in place by every step.</summary>
    public FlightKernelState State { get; }

    /// <summary>The aircraft this session is flying.</summary>
    public AircraftDefinition Definition { get; }

    /// <summary>Its <c>data/aircraft</c> basename.</summary>
    public string AircraftBasename { get; }

    /// <summary>The port's clock.</summary>
    public TickClock Clock => _clock;

    /// <summary>Where the kernel's notifications go.</summary>
    public HostWorld World => _world;

    /// <summary>
    /// The <c>Fx.Indicators</c> random stream, the one the project's own RNG census assigns to
    /// the advisory message gate at <c>image@0x0F03F</c> ("FLIGHT-ADVISOR message gate … Text
    /// only").
    /// </summary>
    /// <remarks>
    /// An <c>Fx.*</c> draw can never reach a decision, so reading it from the presentation layer
    /// is byte-inert by construction — which is exactly why the advisor's 50 % kill gate is
    /// allowed to be a real random draw rather than a fixed answer.
    /// </remarks>
    public Core.Sim.FxRandomStream FxIndicators => _streams.FxIndicators;

    /// <summary>The keyboard stick.</summary>
    public KeyboardStick Stick => _stick;

    /// <summary>How many simulation steps this session has run.</summary>
    public long StepsRun { get; private set; }

    /// <summary>How many steps were discarded by <see cref="MaxCatchUpSteps"/>.</summary>
    public long DroppedSteps { get; private set; }

    /// <summary>
    /// <c>g_gear_deploy_angle_bam [0xEF96]</c> — the landing gear's animation state, 0 EXTENDED …
    /// <c>0x2D0</c> RETRACTED.
    /// </summary>
    /// <remarks>
    /// SIM state, not render state: the original steps it once per flight frame inside
    /// <c>flight_engine_per_frame_top @image@0x227C7</c> phase 4, so the port steps it once per
    /// simulation STEP, right after the kernel (<see cref="GearDeployAngle"/>).  It is deliberately
    /// NOT inside <c>FlightKernel.Step</c>: that function is verified frame-for-frame against the
    /// recordings and nothing may be added to it.  <c>flight_gear_angle_reset
    /// @image@0x22A56</c> is the initial value, which is 0 for every start the port can make.
    /// </remarks>
    /// <remarks>
    /// Seeded from the aircraft's own gear bit, not from <c>GearDeployAngle.Reset</c>: a HISTORIC
    /// mission starts AIRBORNE with the gear UP (<c>aircraft_pose_set</c> clears <c>master[+0x124]
    /// &amp; 4</c>), and starting the animation at <see cref="GearDeployAngle"/>'s EXTENDED end drew
    /// the first ~1.6 s of every mission with the wheels down — measured on the port's own first
    /// frame.
    /// </remarks>
    public int GearAngleBam { get; private set; } = GearDeployAngle.Reset();

    /// <summary>Puts the gear animation where the aircraft's own status flag says it is.</summary>
    private void SeedGearAngle() =>
        GearAngleBam = State.Aircraft.StatusFlags.HasFlag(AircraftStatusFlags.LandingGear)
            ? GearDeployAngle.Extended
            : GearDeployAngle.Retracted;

    /// <summary>
    /// The mission's cloud-deck altitude in feet, or <see cref="CloudDeck.NoDeck"/> when the mission
    /// has none.
    /// </summary>
    /// <remarks>
    /// <c>[0xF100] g_mission_altitude</c>, the <c>.S</c> <c>mission_altitude</c> directive.  The Test
    /// Flight's <c>FREE.S</c> carries 0, which makes the original DRAW one at load
    /// (<c>image@0x2CC4A..0x2CCAE</c>); the host resolves that draw so a run is reproducible — see
    /// <see cref="ResolveCloudAltitude"/>.
    /// </remarks>
    public int CloudAltitudeFeet { get; private set; } = CloudDeck.NoDeck;

    /// <summary>Chooses the mission's cloud-deck altitude.</summary>
    /// <param name="authored">
    /// The mission's own <c>mission_altitude</c>: <see cref="CloudDeck.NoDeck"/>, a literal altitude,
    /// or <see cref="CloudDeck.RollAtLoad"/> to draw one.
    /// </param>
    /// <param name="request">
    /// What the host asked for: <c>null</c> = follow the mission, a non-negative value = that
    /// altitude outright, <see cref="CloudDeck.NoDeck"/> = suppress the deck.
    /// </param>
    /// <remarks>
    /// The original's draw is <c>prng_bounded(0x1F40) + 0xBB8</c> above the player's altitude, with a
    /// <c>prng() &lt; 0x32</c> arm that gives the mission no clouds at all.  The port takes the
    /// MIDPOINT of that span rather than adding a random stream the recordings cannot verify —
    /// a deterministic 3,000 + 4,000 = 7,000 ft above the spawn — and lets
    /// <c>--cloud-altitude</c> say otherwise.  <c>(open)</c>: which PRNG stream the original draws
    /// from, and therefore the exact deck of a given sortie.
    /// </remarks>
    public void ResolveCloudAltitude(int authored, int? request)
    {
        if (request is { } forced)
        {
            CloudAltitudeFeet = forced;
            return;
        }

        CloudAltitudeFeet = authored switch
        {
            CloudDeck.NoDeck => CloudDeck.NoDeck,
            CloudDeck.RollAtLoad => CloudDeck.AltitudeFromDraw(
                State.Player.Y >> 8, CloudDeck.MinimumOffsetFeet + (CloudDeck.AboveSpanFeet / 2)),
            _ => authored,
        };
    }

    /// <summary>The step of the trace record the session was seeded from; 0 for a cold start.</summary>
    public uint SeedStep { get; private init; }

    /// <summary>How the session's initial state was built.</summary>
    public string SeedDescription { get; private init; } = "trace";

    /// <summary>The scene's object pool — the player object plus whatever a later step adds.</summary>
    public WorldObjectPool Pool { get; private init; } = new();

    /// <summary>
    /// The theater catalog this flight is over — <c>GERMANY.W</c>, <c>KOREA.W</c> or
    /// <c>VIETNAM.W</c>, the era's own (<c>scenario_load_dispatch @image@0x09335</c> indexes
    /// <c>[bx+0xFAA]</c> with <c>bx = [0x2A0E]*2</c>).
    /// </summary>
    /// <remarks>
    /// A cold start knows it outright.  A trace seed carries no theater, so the era is recovered the
    /// way the Hangar's Fly button sets it: <c>[0x2A0E] = aircraft_idx &gt;&gt; 1</c>
    /// (<c>image@0x26CD5..0x26CDA</c>, H2 §A1).
    /// </remarks>
    public string TheaterAssetName { get; private init; } = MissionVocabulary.TheaterAssetForEra[0];

    /// <summary>Seconds of simulated time (steps × the step's own duration).</summary>
    public double SimulatedSeconds =>
        StepsRun * TickClock.StepTicks / TickClock.TicksPerSecond;

    /// <summary>
    /// Opens a TEST FLIGHT from the data tree alone — no trace anywhere in the chain.
    /// </summary>
    /// <param name="tree">The transformed data tree.</param>
    /// <param name="aircraftIndex">The Hangar's player slot 0..5 (<c>g_active_aircraft_idx [0xC31A]</c>).</param>
    /// <param name="siteDraw">
    /// Which of the theater's type-6 sites <c>FREE.S</c>'s <c>at_site</c> marker drew.  The original
    /// draws it with <c>prng_rand_bounded</c> (<c>image@0x09FD0</c>); the host chooses.
    /// </param>
    /// <param name="latchingStick">Reproduce the original's latching keyboard stick.</param>
    /// <remarks>
    /// The chain is <see cref="FlightColdStart"/>'s: the Hangar's Fly button commits the aircraft
    /// index and arms <c>FREE.S</c> (<c>image@0x26CD5..0x26CDD</c>), <c>scenario_load_dispatch</c>
    /// parses the era's theater and that container, and its class-0 PLAYER record puts the aircraft
    /// on an airstrip with the engine off.
    /// </remarks>
    public static FlightSession FromColdStart(
        DataTree tree, int aircraftIndex, int siteDraw, bool latchingStick)
    {
        ArgumentNullException.ThrowIfNull(tree);

        // A Test Flight is a MISSION-BACKED session.  Without one the trigger reaches nothing and
        // the guns are dead in a Test Flight started from the menu.  The original flies FREE.S through the same scenario load as the other fifty
        // containers and arms the same weapon chain; see MissionColdStart.TestFlight.
        TickClock clock = new TickClock();
        MissionSession mission = MissionColdStart.TestFlight(
            tree, aircraftIndex, siteDraw, clock: clock);
        string basename = AircraftDefinition.FlyableBasenames[aircraftIndex];
        string theaterAsset = MissionVocabulary.TheaterAssetForEra[
            (int)FlightColdStart.EraForAircraft(aircraftIndex)];

        FlightSession session = new FlightSession(
            mission.Flight, tree.Aircraft[basename], basename, latchingStick)
        {
            SeedStep = 0,
            Pool = new WorldObjectPool(),
            TheaterAssetName = theaterAsset,
            Mission = mission,
            IsTestFlight = true,
            SeedDescription =
                $"cold start — {FlightColdStart.TestFlightMissionAsset} over {theaterAsset}, "
                    + $"at_site draw {siteDraw}",
        };

        session._clock = clock;

        // The theater's own type-2 / type-6 sites are the landing-zone table crash_secondary_check
        // scans, so the host can answer the kernel's one world query for real.
        session._world.Zones = LandingZoneTable.FromTheater(
            tree.Theaters[theaterAsset], LandingZoneTable.ShippedCaptureRadius(tree));
        session._world.State = mission.Flight;
        return session;
    }

    /// <summary>
    /// Opens a HISTORIC MISSION — both kernels, the mission's objects in the pool, and the combat
    /// driver running the whole gameplay ladder every step.
    /// </summary>
    /// <param name="tree">The transformed data tree.</param>
    /// <param name="slot">The scenario catalog's picker line 0..49.</param>
    /// <param name="siteDraw">Which theater site each <c>at_site</c> anchor drew.</param>
    /// <param name="difficulty">The difficulty 0..3 (<c>g_difficulty_level [0xF10E]</c>).</param>
    /// <param name="latchingStick">Reproduce the original's latching keyboard stick.</param>
    public static FlightSession FromMission(
        DataTree tree, int slot, int siteDraw, int difficulty, bool latchingStick)
    {
        ArgumentNullException.ThrowIfNull(tree);
        TickClock clock = new TickClock();
        MissionSession mission = MissionColdStart.Open(tree, slot, siteDraw, difficulty, clock: clock);
        MissionEntry entry = tree.Scenarios.Entries.First(e => e.Slot == slot);
        int aircraftIndex = mission.Combat.Mission.PlayerAircraftIndex ?? entry.PlayerAircraftIndex;
        string basename = AircraftDefinition.FlyableBasenames[aircraftIndex];

        FlightSession session = new FlightSession(
            mission.Flight, tree.Aircraft[basename], basename, latchingStick)
        {
            SeedStep = 0,
            Pool = new WorldObjectPool(),
            TheaterAssetName = entry.TheaterAssetName,
            Mission = mission,
            SeedDescription =
                $"mission {slot} \"{entry.Title}\" — {mission.Combat.Mission.AssetName} over "
                    + $"{entry.TheaterAssetName}, at_site draw {siteDraw}",
        };

        session._clock = clock;
        session._world.State = mission.Flight;

        // The MISSION's flight kernel consults MissionColdStart's own MissionWorld (it is the seam
        // MissionSession was constructed with), which already carries the theatre's landing zones;
        // this HostWorld is only the readout's HUD/advisor counter on this path. Giving it the same
        // table costs nothing and stops it reading as a landmine.
        session._world.Zones = LandingZoneTable.FromTheater(
            tree.Theaters[entry.TheaterAssetName],
            LandingZoneTable.ShippedCaptureRadius(tree));
        return session;
    }

    /// <summary>
    /// Opens a CUSTOM MISSION: the sentence the CREATE MISSION pickers built, flown through C1's
    /// <c>MissionColdStart.Custom</c> (<c>custom_mission_build_from_picks @image@0x27E76</c>).
    /// </summary>
    /// <param name="tree">The transformed data tree.</param>
    /// <param name="picks">The seven picks.</param>
    /// <param name="title">The composed sentence, for the statistics record and the log.</param>
    /// <param name="randomSeed">The LFSR seed the builder draws its geometry from.</param>
    /// <param name="latchingStick">Reproduce the original's latching keyboard stick.</param>
    /// <remarks>
    /// There is no <c>siteDraw</c> and no <c>difficulty</c>: the module is <c>FREE.S</c> (whose
    /// anchors draw as a Test Flight's do) and the builder OVERWRITES the player's placement
    /// anyway, while the difficulty is forced to
    /// <see cref="CustomMissionBuild.ForcedDifficulty"/> at <c>image@0x27EAF</c>.  The theatre
    /// follows the aeroplane, exactly as the fly exit sets it
    /// (<c>[0x2A0E] = aircraftRow &gt;&gt; 1</c>, <c>image@0x27766</c>).
    /// </remarks>
    public static FlightSession FromCustom(
        DataTree tree,
        CustomMissionPicks picks,
        string title,
        ushort randomSeed,
        bool latchingStick)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(picks);
        ArgumentNullException.ThrowIfNull(title);
        TickClock clock = new TickClock();
        MissionSession mission = MissionColdStart.Custom(tree, picks, randomSeed, clock);
        string basename = AircraftDefinition.FlyableBasenames[picks.PlayerAircraftRow];
        string theaterAsset = MissionVocabulary.TheaterAssetForEra[picks.Era];

        FlightSession session = new FlightSession(
            mission.Flight, tree.Aircraft[basename], basename, latchingStick)
        {
            SeedStep = 0,
            Pool = new WorldObjectPool(),
            TheaterAssetName = theaterAsset,
            Mission = mission,
            CustomTitle = title,
            SeedDescription =
                $"custom mission — {FlightColdStart.TestFlightMissionAsset} over {theaterAsset}, "
                    + $"{picks.EnemyCount} opponent(s), seed 0x{randomSeed:X4}",
        };

        session._clock = clock;
        session._world.State = mission.Flight;
        session._world.Zones = LandingZoneTable.FromTheater(
            tree.Theaters[theaterAsset], LandingZoneTable.ShippedCaptureRadius(tree));
        return session;
    }

    /// <summary>
    /// Switches the PLAYER-FATE machine on for this session (the host's <c>--fate</c> knob).
    /// </summary>
    /// <param name="holdSeconds">How long the wreck is held before the sortie is over.</param>
    /// <returns>The machine.</returns>
    /// <remarks>
    /// The resting altitude is the aeroplane's own <c>master[+0x116]</c> class ground clearance
    ///, so a wreck rests exactly where the original leaves a parked aeroplane.
    /// </remarks>
    /// <param name="passive">
    /// <c>--fate off</c>: MEASURE only.  The kernel keeps stepping, the wreck keeps sliding and
    /// nothing is drawn differently — but <see cref="PlayerFate.RestDriftX"/> still says how far it
    /// slid, which is the BEFORE number.
    /// </param>
    public PlayerFate EnableFate(double holdSeconds, bool passive = false)
    {
        Fate = new PlayerFate
        {
            Enabled = true,
            Passive = passive,
            GroundClearance = State.Aircraft.AirspeedB,
            HoldSeconds = holdSeconds,
        };
        return Fate;
    }

    /// <summary>The mission this session is flying.  A Test Flight flies <c>FREE.S</c>.</summary>
    public MissionSession? Mission { get; private init; }

    /// <summary>
    /// The CREATE MISSION picks this sortie was built from, or null.  It is what makes the
    /// sortie's statistics record its own (<c>SortieRecorder.Identity</c>) and what the DEBRIEFING
    /// screen reads to know it is a custom one.
    /// </summary>
    public CustomMissionPicks? CustomPicks => Mission?.CustomPicks;

    /// <summary>The composed sentence, for the statistics record; empty otherwise.</summary>
    public string CustomTitle { get; private init; } = string.Empty;

    /// <summary>
    /// Whether this session is a TEST FLIGHT — <c>FREE.S</c> over the aeroplane's era theatre, with
    /// no opponent and no win rule.
    /// </summary>
    /// <remarks>
    /// It flies with the same two kernels a historic mission gets (so that the guns work), so
    /// <see cref="Mission"/> alone no longer tells the two apart.  What still does differ: the host
    /// attaches no <c>MissionOutcome</c> to a Test Flight, because there is nothing to win and
    /// nothing to be debriefed on, and the front end never lands one on the DEBRIEFING screen
    /// (<c>SortieOutcome.LandsOnDebriefing</c>).
    /// </remarks>
    public bool IsTestFlight { get; private init; }

    /// <summary>
    /// CHEAT (<c>--foe-hp</c>) — cap every live engagement's hit points at this value each frame; 0
    /// is off.  See <see cref="MissionSession.CheatWeakenEnemies"/> for why it exists and what it
    /// does NOT change.
    /// </summary>
    public int CheatEnemyHitPoints { get; set; }

    /// <summary>How many engagements the cheat has weakened.</summary>
    public int CheatWeakenings { get; private set; }

    /// <summary>
    /// Opens a session seeded from the first COMPLETE frame (all eight stage records) of a trace.
    /// </summary>
    /// <param name="tracePath">The <c>cyac-flight-trace</c> file.</param>
    /// <param name="tree">The data tree the aircraft definition comes from.</param>
    /// <param name="latchingStick">Reproduce the original's latching keyboard stick.</param>
    /// <param name="fromStep">
    /// Seed from the first record at or after this simulation step.  The trace's FIRST record is
    /// often a parked aircraft — <c>session_20260902_133956_det</c> opens at step 246 with the P-51
    /// on the apron, throttle 0 — so a demonstration usually wants a step in the middle of the
    /// flight.  0 means "the first record there is".
    /// </param>
    /// <exception cref="InvalidDataException">The trace holds no complete frame.</exception>
    public static FlightSession FromTrace(
        string tracePath, DataTree tree, bool latchingStick, uint fromStep = 0)
    {
        ArgumentNullException.ThrowIfNull(tree);
        using FlightTraceReader reader = FlightTraceReader.Open(tracePath);
        foreach (FlightTraceRecord record in reader.Records())
        {
            if (record.IsProbe || record.Id != 0 || record.Step < fromStep)
            {
                continue;
            }

            string basename = FlightTraceSeed.AircraftBasename(record);
            AircraftDefinition definition = tree.Aircraft[basename];
            FlightSeedResult seed = FlightTraceSeed.CreateState(
                definition,
                record,
                FlightTraceSeed.ReadCalibration(record),
                FlightTraceSeed.ReadPullUpTuning(record));
            int aircraftIndex = AircraftIndexOf(basename);
            return new FlightSession(seed.State, definition, basename, latchingStick)
            {
                SeedStep = record.Step,
                Pool = seed.Pool,
                TheaterAssetName =
                    MissionVocabulary.TheaterAssetForEra[(int)FlightColdStart.EraForAircraft(aircraftIndex)],
                SeedDescription = "trace step " + record.Step.ToString(CultureInfo.InvariantCulture),
            };
        }

        throw new InvalidDataException(
            $"{tracePath} carries no S0 flight record at or after step {fromStep}");
    }

    /// <summary>
    /// Runs the simulation forward by one host frame's worth of wall time.
    /// </summary>
    /// <param name="elapsedSeconds">Seconds since the previous host frame.</param>
    /// <param name="held">The stick directions held this frame.</param>
    /// <param name="cockpitKeys">Cockpit keys whose press edge landed this frame.</param>
    /// <param name="trigger">Whether the FIRE control is held (<c>[0x32ED]</c>).</param>
    /// <param name="combatKeys">Cooked combat ladder keys whose press edge landed this frame.</param>
    /// <param name="weaponStep">−1 / +1 to step the weapon slot, 0 for neither.</param>
    /// <returns>How many simulation steps ran.</returns>
    public int Advance(
        double elapsedSeconds,
        in HeldControls held,
        IReadOnlyList<int> cockpitKeys,
        bool trigger = false,
        IReadOnlyList<int>? combatKeys = null,
        int weaponStep = 0)
    {
        ArgumentNullException.ThrowIfNull(cockpitKeys);

        if (Mission is { } combat)
        {
            combat.Trigger = trigger;
            foreach (int key in combatKeys ?? [])
            {
                combat.QueueKey(key);
            }

            if (weaponStep < 0)
            {
                combat.SelectPreviousWeapon();
            }
            else if (weaponStep > 0)
            {
                combat.SelectNextWeapon();
            }
        }

        // The cockpit keys are edge events, not state: apply each once, before the frame's steps,
        // through the same wrapper gate the genuine machine has.
        foreach (int key in cockpitKeys)
        {
            ApplyCockpitKey(key);
        }

        _stick.Update(held.Left, held.Right, held.Forward, held.Back, held.Centre);

        int steps = _presenter.Advance(elapsedSeconds);
        if (steps > MaxCatchUpSteps)
        {
            DroppedSteps += steps - MaxCatchUpSteps;
            steps = MaxCatchUpSteps;
        }

        for (int i = 0; i < steps; i++)
        {
            Step();
        }

        return steps;
    }

    /// <summary>
    /// The headless test pilot, when <c>--pursue</c> is on: it overrides the stick and the trigger.
    /// </summary>
    public PursuitAutopilot? Pursuit { get; set; }

    /// <summary>
    /// The PLAYER'S OWN END: the fate machine that stops the slide and plays the destruction
    /// sequence.  Null when <c>--fate off</c>.
    /// </summary>
    /// <remarks>
    /// An AUTHORISED DEVIATION — see <see cref="PlayerFate"/> for what the original does instead (it
    /// cuts to the debrief within four frames and draws no crash at all). It is never built on a
    /// verifying path: <c>--replay</c> runs <see cref="TraceReplay"/>, which does not use
    /// <see cref="FlightSession"/> at all.
    /// </remarks>
    public PlayerFate? Fate { get; set; }

    /// <summary>
    /// The PER-ROUND GUNNERY trail (the grey ordinary rounds), or null when <c>--rounds off</c>.
    /// Presentation only: it reads the mission's combat state after every step and writes none of it
    /// (<see cref="GunneryRounds"/>); never built on a verifying path.
    /// </summary>
    public GunneryRounds? Gunnery { get; set; }

    /// <summary>
    /// The long-lived SMOKE LAYER (bigger, much longer puffs), or null when <c>--smoke-trail
    /// off</c>.  Presentation only, on H22's pattern: it reads the mission's combat state after
    /// every step and writes none of it (<see cref="SmokeTrail"/>); never built on a verifying
    /// path.
    /// </summary>
    public SmokeTrail? Smoke { get; set; }

    /// <summary>
    /// The sortie's END and its DEBRIEF, or null when the host does not want one
    /// (<c>--debrief off</c>).  Read-only over the sim, like <see cref="Gunnery"/>.
    /// </summary>
    public MissionOutcome? Outcome { get; set; }

    /// <summary>
    /// H25 CHEAT (labelled, <c>--debrief-at</c>) — end the sortie as a SURVIVOR at this simulated
    /// second so the debrief can be PHOTOGRAPHED without flying home.  Negative is off.
    /// </summary>
    /// <remarks>
    /// It changes nothing about the verdict: the module's <c>get_debrief_text</c> still runs over
    /// the counters the sortie really reached.  It only skips the flying-home part, which a headless
    /// test pilot cannot do.
    /// </remarks>
    public double CheatDebriefAtSeconds { get; set; } = -1.0;

    /// <summary>
    /// H25 CHEAT (labelled, <c>--mission-kills N</c>) — tell the mission module that actor slots 1..N
    /// were destroyed, so the objective can be PHOTOGRAPHED.  0 is off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It destroys nothing.  It makes the same call the kill path makes —
    /// <c>combat_vtable_slot_fn2_dispatch @image@0x08C72</c> with an actor slot's own near pointer
    /// out of <c>g_spawn_slot_nearptr_table [0xEE5A]</c>, exactly as
    /// <c>engagement_kill_tally_and_slot_dispatch @image@0x08738</c> pushes <c>[0xED56]</c> — so the
    /// slot lookup, the module's guard on the victim index and its <c>inc</c> are all the real ones.
    /// </para>
    /// <para>
    /// It exists because the headless test pilot cannot win a dogfight: measured over a 160,000-frame
    /// ABB sortie with <c>--pursue --foe-hp 1</c> it scored ONE kill and then flew into the ground.
    /// A win has to be photographed, not asserted (the same reasoning as <c>--foe-hp</c>, H5a §6 B1).
    /// </para>
    /// </remarks>
    public int CheatMissionKills { get; set; }

    /// <summary>Whether <see cref="CheatMissionKills"/> has fired.</summary>
    public bool CheatMissionKillsArmed { get; private set; }

    /// <summary>A scripted Shift-E: the next step runs the eject arm.</summary>
    public bool EjectRequested { get; set; }

    /// <summary>What the last <see cref="PlayerEject.Execute"/> did, or null.</summary>
    public PlayerEjectResult? LastEject { get; private set; }

    /// <summary>How many <c>engagement_kill_query</c> probes named an object.</summary>
    public long ProximityKillHits { get; private set; }

    /// <summary>How many were made.</summary>
    public long ProximityKillQueries { get; private set; }

    /// <summary>
    /// H9 CHEAT (labelled) — arm the player's own DEATH DEADLINE (<c>[0xBD06]</c>,
    /// <see cref="PlayerCombatOffsets.DeathDeadlineFrame"/>) at this simulated second, so a
    /// headless sortie can be shot down.  Negative is off.
    /// </summary>
    /// <remarks>
    /// It is an INITIAL-STATE edit into the integer kernel's own input and nothing else: the
    /// sustain tick then runs the whole real death path itself (<c>engagement_per_frame_tick
    /// @image@0x0FE3C</c> → <c>[0xEE58]:= 1</c>, <c>scene_setup_or_camera_reset(2)</c>,
    /// <c>[0xC390]:= frame + 4</c>).  The alternative — a bandit actually shooting the player
    /// down — takes tens of thousands of headless frames and cannot be scheduled.
    /// </remarks>
    public double CheatDeathAtSeconds { get; set; } = -1.0;

    /// <summary>Whether the death-deadline cheat has fired.</summary>
    public bool CheatDeathArmed { get; private set; }

    /// <summary>Runs exactly one simulation step with the stick as it currently stands.</summary>
    public void Step()
    {
        (short x, short y) = _stick.Axes(State.Window.Calibration);
        if (Mission is { } mission)
        {
            // The headless test pilot (--pursue): an INPUT source, never sim state.
            if (Pursuit is { } autopilot && autopilot.Fly(mission) is { } flown)
            {
                x = flown.X;
                y = flown.Y;
                mission.Trigger = flown.Trigger;
            }

            // The combat driver owns the frame: its CS2 → CS3 channel runs FlightKernel.Step, so
            // the host must NOT step the flight kernel itself (the joint run). CHEAT, labelled —
            // --foe-hp: cap every live engagement's hit points so a headless sortie can actually
            // SCORE a kill and the sequence can be photographed.  Applied per frame because the
            // ladder admits engagements over time; it only ever LOWERS, so a bandit the player has
            // already hurt keeps its damage.  Nothing else changes: every step of the kill after
            // this is the integer kernel's own.
            if (CheatEnemyHitPoints > 0)
            {
                CheatWeakenings += mission.CheatWeakenEnemies((byte)CheatEnemyHitPoints);
            }

            // The pilot's own Shift-E, BEFORE the frame (the key ladder runs at the top of
            // mission_state_machine, image@0x0E27).
            bool ejected = ApplyEject(mission);

            // H9 CHEAT — --player-death-at: arm the kernel's own death deadline and let the
            // verified sustain tick do the rest.
            ApplyDeathCheat(mission);

            // H25 CHEAT — --mission-kills: report N slot-destroyed events through the VERIFIED
            // dispatch, once, after the first frame has populated the slot table.
            ApplyMissionKillCheat(mission);

            mission.StickX = x;
            mission.StickY = y;
            mission.Step();
            RunFate(mission, ejected);

            // The grey rounds, AFTER the frame: the ported bullet has been spawned (CS3 → CS4)
            // and every live shot has been stepped (CS0 → CS1), and the trail only reads.
            Gunnery?.Step(mission);

            // The smoke layer, likewise AFTER the frame: the five effect ticks have run (CS1 → CS2),
            // so every puff this frame lit is in a slot and every live puff has taken its own step;
            // the layer only reads.
            Smoke?.Step(mission);

            // The sortie's own end, likewise AFTER the frame and likewise read-only: the fate
            // machine has taken its step and the mission module has had its per-frame hook.
            if (CheatDebriefAtSeconds >= 0 && SimulatedSeconds >= CheatDebriefAtSeconds)
            {
                Outcome?.ForceEnd(this, CYAC.Port.Core.Sim.Mission.MissionDebriefOutcome.Survived);
            }

            Outcome?.Observe(this);
            GearAngleBam = GearDeployAngle.Step(
                GearAngleBam,
                _clock.Dt,
                State.Aircraft.StatusFlags.HasFlag(AircraftStatusFlags.LandingGear),
                previewMode: false);
            _clock.AdvanceStep();
            StepsRun++;
            return;
        }

        FlightFrameInputs inputs = new FlightFrameInputs(
            x,
            y,
            _clock.MasterFrameCounter,
            EasyDifficulty: true,
            RecordedDt: 0);

        // A Test Flight has no combat kernel, so the fate machine's only trigger here is the flight
        // chain's own crash verdict, and its only sequence is stopping the slide.
        if (Fate is { Enabled: true, FlightStopped: true } stopped)
        {
            stopped.Step(
                State,
                Observe(null, CrashCheckOutcome.AlreadyInactive, false, false),
                StepSeconds,
                StepsRun);
            _clock.AdvanceStep();
            StepsRun++;
            return;
        }

        FlightKernelStepResult result = FlightKernel.Step(State, inputs, _clock, _streams, _world);
        Fate?.Step(
            State,
            Observe(null, result.CrashCheck.Outcome, result.CrashCheck.KilledBy, false),
            StepSeconds,
            StepsRun);

        // The gear animation, exactly where the original runs it: once per flight frame, AFTER the
        // kernel's own work (flight_engine_per_frame_top @image@0x227C7 phase 4, lcall
        // @image@0x2294F).  The bit it reads is master[+0x124] & 0x04, which is the same bit index
        // as g_input_state_bitfield [0xF0BC] & 4 (image@0x31341 sets that one from g_film_gear_state
        // [0xC06E]).
        GearAngleBam = GearDeployAngle.Step(
            GearAngleBam,
            _clock.Dt,
            State.Aircraft.StatusFlags.HasFlag(AircraftStatusFlags.LandingGear),
            previewMode: false);

        _clock.AdvanceStep();
        StepsRun++;
    }

    /// <summary>One simulation step's duration in seconds — the fixed <c>STEP_TICKS = 5</c>.</summary>
    public static double StepSeconds => TickClock.StepTicks / TickClock.TicksPerSecond;

    /// <summary>Builds the fate machine's per-step report.</summary>
    /// <param name="mission">The mission session, or null for a Test Flight.</param>
    /// <param name="outcome">What <c>DamageCheckStage.CheckCrash</c> did this step.</param>
    /// <param name="killedByConditions"><see cref="CrashCheckResult.KilledBy"/>.</param>
    /// <param name="ejected">Whether the Shift-E arm ran this step.</param>
    private PlayerFateObservation Observe(
        MissionSession? mission, CrashCheckOutcome outcome, bool killedByConditions, bool ejected)
    {
        byte destroyed = 0;
        ushort ram = 0;
        if (mission is not null)
        {
            destroyed = mission.Combat.Registers.Byte(PlayerEject.PlayerSlotDestroyedFlag);

            // engagement_kill_query @image@0x226B2, once per frame exactly as
            // flight_engine_per_frame_top makes it (image@0x2298F).  It returns 0 above 2,000 ft
            // without touching the grid at all.  A PASSIVE machine (--fate off) does not make it:
            // the query stages its arguments in DGROUP, and a measurement-only mode must leave the
            // pre-H9 register file exactly as it was.
            if (Fate is { Passive: false })
            {
                ProximityKillQueries++;
                ram = PlayerProximityKill.Query(
                    mission.Combat.Registers, mission.Combat.Arena, mission.Acquisition);
                if (ram != 0)
                {
                    ProximityKillHits++;
                }
            }
        }

        return new PlayerFateObservation(
            State.Aircraft.ActiveState, outcome, killedByConditions, destroyed, ram, ejected);
    }

    /// <summary>Runs the fate machine for a MISSION step and answers what it asked for.</summary>
    /// <param name="mission">The mission session.</param>
    /// <param name="ejected">Whether the Shift-E arm ran this step.</param>
    private void RunFate(MissionSession mission, bool ejected)
    {
        if (Fate is not { Enabled: true, Passive: false } fate)
        {
            // A PASSIVE machine still observes (it is how the slide is measured); it just changes
            // nothing.
            Fate?.Step(
                State,
                Observe(mission, mission.LastCrashCheck.Outcome, mission.LastCrashCheck.KilledBy, ejected),
                StepSeconds,
                StepsRun);
            return;
        }

        bool stopped = fate.Step(
            State,
            Observe(mission, mission.LastCrashCheck.Outcome, mission.LastCrashCheck.KilledBy, ejected),
            StepSeconds,
            StepsRun);
        mission.FlightStepSuppressed = stopped;
        if (!stopped)
        {
            return;
        }

        // THE PLAYER IS NO LONGER FLYING.  `g_player_not_flying_flag [0xC32F]` is the ONE gate
        // before player damage, and it
        // is also the first term of the derived in-flight key gate `[0xC31C]` (image@0x00DE9), so
        // setting it takes the controls away too.  The eject arm sets it itself (image@0x0132B); the
        // port sets it on the OTHER causes as well, which is a labelled DEVIATION — the original
        // does not need to, because it ends the session four frames later and the PoC holds the
        // wreck instead.
        mission.Combat.Registers.SetByte(PlayerEject.PlayerNotFlyingFlag, 1);

        // H7's own kill sequence, on the PLAYER's object.  SpawnDamageSmoke attaches the
        // subsystem4x19 emitter row weapon_fire_combat_loop attaches to a hurt bandit
        // (image@0x0BF6C); ImpactEffects is engagement_slot_impact_and_depart's three-call tail
        // (image@0x08BA9) — the crater, the bitmap explosion and the smoke column over it.
        ushort player = mission.Combat.Registers.PlayerObjectRef;
        if (fate.TakeSmokeTrail() && player != 0)
        {
            mission.VmEffects.SpawnDamageSmoke(player, 0);
        }

        if (fate.TakeImpact())
        {
            mission.VmEffects.ImpactEffects(
                new CYAC.Port.Core.Sim.Combat.CombatPosition(State.Player.X, 0, State.Player.Z));
        }
    }

    /// <summary>Runs the Shift-E arm when the host asked for it.</summary>
    /// <param name="mission">The mission session.</param>
    /// <returns>True when the arm really ejected the pilot.</returns>
    private bool ApplyEject(MissionSession mission)
    {
        if (!EjectRequested)
        {
            return false;
        }

        EjectRequested = false;
        if (Fate is not { Enabled: true, Passive: false, Phase: PlayerFatePhase.Flying } fate)
        {
            return false;
        }

        PlayerEjectResult result = PlayerEject.Execute(
            mission.Context.Lifecycle,
            State.Aircraft.CurrentAirspeedFps,
            State.Aircraft.ForwardVelocity.Value);
        LastEject = result;
        if (!result.Ejected)
        {
            return false;
        }

        fate.EjectionWasFatal = result.Fatal;
        return true;
    }

    /// <summary>H25 CHEAT — reports <see cref="CheatMissionKills"/> kills to the module.</summary>
    /// <param name="mission">The mission session.</param>
    private void ApplyMissionKillCheat(MissionSession mission)
    {
        if (CheatMissionKillsArmed || CheatMissionKills <= 0 || StepsRun == 0)
        {
            return;
        }

        CheatMissionKillsArmed = true;
        CombatRegisters registers = mission.Combat.Registers;
        for (int slot = 1; slot <= CheatMissionKills; slot++)
        {
            ushort nearPtr = registers.Word(
                CYAC.Port.Core.Sim.Combat.Lifecycle.LifecycleOffsets.SlotNearPtrTable + (slot * 2));
            CYAC.Port.Core.Sim.Combat.Lifecycle.MissionModuleDispatch.OnSlotDestroyed(
                mission.Context.Lifecycle, nearPtr);
        }
    }

    /// <summary>H9 CHEAT — arms the kernel's own death deadline at <see cref="CheatDeathAtSeconds"/>.</summary>
    /// <param name="mission">The mission session.</param>
    private void ApplyDeathCheat(MissionSession mission)
    {
        if (CheatDeathArmed || CheatDeathAtSeconds < 0
            || SimulatedSeconds < CheatDeathAtSeconds)
        {
            return;
        }

        CheatDeathArmed = true;

        // The sustain tick compares `[0xBD06] <= frame` UNSIGNED on its next odd frame, so writing
        // the current frame counter is the smallest possible edit that makes the real path run.
        CombatRegisters registers = mission.Combat.Registers;
        registers.SetWord(
            CYAC.Port.Core.Sim.Combat.Player.PlayerCombatOffsets.DeathDeadlineFrame,
            registers.Word(MissionSession.MasterFrameCounter));
    }

    /// <summary>Applies one cooked cockpit key, subject to the wrapper gate.</summary>
    /// <param name="key">The cooked key word — see <see cref="CockpitKeys"/>.</param>
    /// <returns>True when the key was dispatched.</returns>
    public bool ApplyCockpitKey(int key)
    {
        // The port can evaluate the wrapper's gate exactly, because [0xC316]!= 0 and
        // master[+0x122] == 0 are written by the same six instructions of
        // scene_setup_or_camera_reset (image@0x22655 / image@0x2265A).
        if (Mission is { } mission)
        {
            // The combat kernel's own key ladder dispatches it (image@0x0106C -> CockpitKeys).
            mission.QueueKey(key);
            return true;
        }

        byte loadAckProxy = State.Aircraft.ActiveState == 0 ? (byte)1 : (byte)0;
        if (!CockpitKeys.WrapperAccepts(loadAckProxy))
        {
            return false;
        }

        CockpitKeys.Apply(
            State.Aircraft, key, State.Player.Y, State.Window.GroundProximityFlag);
        return true;
    }

    /// <summary>The Hangar slot of a flyable basename, or 0 when it is not one of the six.</summary>
    /// <param name="basename">An aircraft basename such as <c>p51</c>.</param>
    private static int AircraftIndexOf(string basename)
    {
        IReadOnlyList<string> names = AircraftDefinition.FlyableBasenames;
        for (int i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], basename, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>The renderer's view of the session as it now stands.</summary>
    public FlightSnapshot Snapshot() => FlightSnapshot.From(State);
}

/// <summary>The stick directions a host frame found held.</summary>
/// <param name="Left">Left / numpad 4 / a west corner key.</param>
/// <param name="Right">Right / numpad 6 / an east corner key.</param>
/// <param name="Forward">Up / numpad 8 / a north corner key.</param>
/// <param name="Back">Down / numpad 2 / a south corner key.</param>
/// <param name="Centre">Numpad 5.</param>
public readonly record struct HeldControls(
    bool Left, bool Right, bool Forward, bool Back, bool Centre);
