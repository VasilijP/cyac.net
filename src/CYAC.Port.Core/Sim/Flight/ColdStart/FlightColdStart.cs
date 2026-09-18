using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Model.Mission;
using CYAC.Port.Core.Model.World;
using CYAC.Port.Core.Sim.Flight.Trace;

namespace CYAC.Port.Core.Sim.Flight.ColdStart;

/// <summary>
/// Everything the port needs to build a flight's FIRST frame from the data tree alone — the request
/// side of <see cref="FlightColdStart"/>.
/// </summary>
/// <remarks>
/// One request describes one flight the way the original's six-global tuple does: which aircraft,
/// which container, which theater, and which site each of the container's <c>at_site</c> anchors
/// drew.
/// </remarks>
public sealed record FlightColdStartRequest
{
    /// <summary>The <c>.fmd</c>/<c>.fme</c> pair for <c>g_active_aircraft_idx [0xC31A]</c>.</summary>
    public required AircraftDefinition Aircraft { get; init; }

    /// <summary>
    /// The container that carries the class-0 PLAYER record — <c>FREE.S</c> for a Test Flight,
    /// the mission's own <c>.S</c> otherwise.
    /// </summary>
    public required MissionDefinition Mission { get; init; }

    /// <summary>The theater and the anchor draws (see <see cref="MissionSpawnAnchors"/>).</summary>
    public required MissionSpawnAnchors Anchors { get; init; }

    /// <summary>
    /// The four <c>yeager.cfg</c> joystick extremes, BEFORE the in-flight shrink
    /// (see <see cref="FlightColdStart.InFlightCalibration"/>).
    /// </summary>
    public JoystickCalibration ConfigCalibration { get; init; } =
        FlightColdStart.DefaultConfigCalibration;

    /// <summary>The state-3 pull-up tuning table <c>[0x35F8]</c>.</summary>
    public PullUpTuningTable PullUpTuning { get; init; }

    /// <summary>
    /// The class record the player's world object is instantiated from; defaults to the record whose
    /// name is the aircraft's basename.
    /// </summary>
    /// <remarks>
    /// The original takes it from the per-aircraft prototype table <c>[0x0FBC + 2*idx]</c>, copied to
    /// <c>[0xEF22]</c> and stored at the slot's <c>+0x00</c>
    /// (<c>image@0x093A5..0x093BD</c> → <c>spawn_dispatch_object</c> <c>image@0x06F4D</c>).  The
    /// flight kernel never reads it; the renderer does.
    /// </remarks>
    public ClassRecord? PlayerClass { get; init; }

    /// <summary>The pool the player object is inserted into; a fresh gameplay-sized arena by default.</summary>
    public WorldObjectPool? Pool { get; init; }
}

/// <summary>
/// Builds the flight kernel's frame-1 state from the data tree — the port's replacement for the
/// original's cold-start chain, with no trace involved.
/// </summary>
/// <remarks>
/// <para>
/// <b>The chain this ports</b> (step 2;
/// </para>
/// <list type="number">
///   <item><description><c>ui_aircraft_stats_panel</c>'s Fly button commits the aircraft index into
///     <c>[0xC31A]</c>, derives the era <c>[0x2A0E] = idx &gt;&gt; 1</c> (<c>image@0x26CD5..0x26CDA</c>)
///     and arms the Test Flight container by <c>strcpy(g_record_filename_buf [0xEF82], "free.s")</c>
///     (<c>scenario_record_filename_set_freeflight @image@0x24A16</c>, called
///     @<c>image@0x26CDD</c>);</description></item>
///   <item><description><c>scenario_load_dispatch @0x09305</c> parses the era's theater
///     (<c>image@0x09335</c>) and then <c>[0xEF82]</c> (<c>image@0x0935F</c>), so a Test Flight loads
///     GERMANY/KOREA/VIETNAM <b>and</b> FREE.S;</description></item>
///   <item><description>FREE.S's class-0 PLAYER record latches the spawn — see
///     <see cref="FlightSpawn"/> — and phase 2 spawns the world object at it
///     (<c>image@0x093C8..0x093FF</c>);</description></item>
///   <item><description><c>active_aircraft_load_and_state_reset @0x224CA</c> loads the <c>.fmd</c> +
///     <c>.fme</c> through <c>flight_model_load_for_aircraft @0x2A112</c> (= <see cref="Aircraft.Load"/>)
///     and applies the <c>[0xEE52]</c> speed seed (<c>image@0x22525..0x2253A</c>).  Its
///     <c>aircraft_pose_set</c> call is altitude-gated (<c>image@0x224FF..0x22520</c>) and does NOT
///     run for a parked start;</description></item>
///   <item><description>the first frame then runs through
///     <c>flight_engine_initial_state_setup @0x22689</c>'s <c>[0xC316] &lt;= 1</c> arm
///     (<c>lcall aircraft_per_frame_update</c> @<c>image@0x226A9</c>) — i.e. BEFORE
///     <c>flight_engine_first_frame_arm</c>, which only then advances the gate to 2.</description></item>
/// </list>
/// <para>
/// <b>Deliberately not here.</b> <c>flight_session_start @image@0x301CC</c> is the film/telemetry session
/// initialiser and writes nothing the kernel reads (*.c</c>);
/// <c>flight_engine_first_frame_arm</c>'s effects follow the first frame, not precede it; the film ring,
/// the HUD history fill (<c>hud_orientation_history_init_fill @0x0CF13</c>) and the scene's non-player
/// objects belong to later steps of the plan.
/// </para>
/// </remarks>
public static class FlightColdStart
{
    /// <summary>The container the Hangar's Fly button arms for a Test Flight: <c>FREE.S</c>.</summary>
    /// <remarks><c>scenario_record_filename_set_freeflight @image@0x24A16</c>, literal at DGROUP+0x2F75.</remarks>
    public const string TestFlightMissionAsset = "FREE.S";

    /// <summary>World units → world-object units: <c>&lt;&lt; 8</c>.</summary>
    /// <remarks>
    /// The container stores an i24 that the reader already shifts down by 8
    /// (<c>read_3bytes @image@0x0A403</c>), and the site table's own i32s are the shifted-up form the
    /// object slot wants (<c>image@0x0A008</c>).
    /// </remarks>
    public const int WorldToObjectShift = 8;

    /// <summary>
    /// How far the flight loop squeezes the calibration window before the model tick: 10 per side.
    /// </summary>
    /// <remarks>
    /// <c>flight_input_axis_clamp_and_scale @image@0x2279C..0x227AB</c> shrinks (<c>+10</c> on the two
    /// minima, <c>-10</c> on the two maxima) and <c>flight_input_axis_rangelimit_grow
    /// @image@0x227B2</c> grows it back after the tick — so inside <c>aircraft_per_frame_update</c>,
    /// which is where every verification trap sits, the window is always the shrunk one.
    /// </remarks>
    public const int CalibrationInFlightShrink = 10;

    /// <summary>DGROUP's base in the layer-1 image: <c>image@0x3BD60</c>.</summary>
    /// <remarks>The value <c>data/exe/*.json</c> publishes as <c>dgroupImageBase</c>.</remarks>
    public const int DgroupImageBase = 0x3BD60;

    /// <summary>Where the pull-up tuning table lives in DGROUP: <c>[0x35F8]</c>, 7 words.</summary>
    public const int PullUpTuningDgroupOffset = 0x35F8;

    /// <summary>
    /// The calibration <c>input_mode_set</c> installs when no joystick is calibrated: ±105.
    /// </summary>
    /// <remarks><c>mov ax,0xFF97</c> / <c>mov ax,0x69</c> @<c>image@0x2360A</c>..<c>image@0x23619</c>.</remarks>
    public static JoystickCalibration DefaultConfigCalibration { get; } =
        new(-GameConfig.DefaultJoystickCalibration, GameConfig.DefaultJoystickCalibration,
            -GameConfig.DefaultJoystickCalibration, GameConfig.DefaultJoystickCalibration);

    /// <summary>The era a Test Flight flies in: the aircraft index halved.</summary>
    /// <param name="aircraftIndex">0..5, the Hangar's player slot.</param>
    /// <remarks><c>cdq ; sub ax,dx ; sar ax,1 ; mov [0x2A0E],al</c> @<c>image@0x26CD5..0x26CDA</c>.</remarks>
    public static MissionEra EraForAircraft(int aircraftIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(aircraftIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            aircraftIndex, AircraftDefinition.FlyableBasenames.Count);
        return (MissionEra)(aircraftIndex >> 1);
    }

    /// <summary>The four cfg extremes as the flight model actually sees them.</summary>
    /// <param name="config">The <c>yeager.cfg</c> window (<c>[0xE45E..0xE465]</c> → <c>[0xE47C..]</c>).</param>
    /// <remarks>
    /// NOT on the runtime's path any more.  The cold starts use <see cref="DefaultConfigCalibration"/>: the
    /// port has no joystick, so there is nothing to read a calibration for, and the data tree's
    /// <c>config.json</c> is the ORIGINAL's saved state, which the running game no longer reads at all.  Kept
    /// because it is still how a <c>yeager.cfg</c>'s four words are read — by the tools, and by whatever adds
    /// joystick support.
    /// </remarks>
    public static JoystickCalibration InFlightCalibration(JoystickCalibration config) =>
        new(
            unchecked((short)(config.XMinimum + CalibrationInFlightShrink)),
            unchecked((short)(config.XMaximum - CalibrationInFlightShrink)),
            unchecked((short)(config.YMinimum + CalibrationInFlightShrink)),
            unchecked((short)(config.YMaximum - CalibrationInFlightShrink)));

    /// <summary>The cfg's four joystick extremes, in the order the calibration record wants.</summary>
    /// <param name="config">The parsed <c>config.json</c>.</param>
    /// <remarks>
    /// <c>mission_state_machine @image@0x0097D..0x00992</c> copies <c>[0xE45E]/[0xE460]/[0xE462]/
    /// [0xE464]</c> into <c>[0xE47C]/[0xE480]/[0xE47E]/[0xE484]</c> — x-min, y-min, x-max, y-max, the
    /// same order <c>cfg_write_persistent_blob @image@0x2D707</c> writes back.
    /// </remarks>
    public static JoystickCalibration ConfigCalibration(GameConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new JoystickCalibration(
            config.JoystickXMin, config.JoystickXMax, config.JoystickYMin, config.JoystickYMax);
    }

    /// <summary>The pull-up tuning table, from <c>exe/tables/flight_tuning.json</c>.</summary>
    /// <param name="tree">The opened data tree.</param>
    /// <remarks>
    /// The <c>flight_tuning</c> table landed, so the seven words come from the document like
    /// everything else and the running port opens no image.
    /// </remarks>
    public static PullUpTuningTable PullUpTuning(DataTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return PullUpTuningTable.FromWords(
            tree.Constants.Span(PullUpTuningDgroupOffset, PullUpTuningTable.Words * 2));
    }

    /// <summary>
    /// Assembles a Test Flight request out of a data tree: the Hangar's aircraft choice and one
    /// site draw are all a host has to supply.
    /// </summary>
    /// <param name="tree">The opened data tree.</param>
    /// <param name="aircraftIndex">The Hangar's player slot 0..5 (<c>[0xC31A]</c>).</param>
    /// <param name="siteDraw">
    /// Which site of the anchor's type FREE.S's <c>at_site</c> pick landed on; the original draws it
    /// with <c>prng_rand_bounded</c> (<c>image@0x09FD0</c>), the port lets the host choose.
    /// </param>
    public static FlightColdStartRequest TestFlight(DataTree tree, int aircraftIndex, int siteDraw = 0)
    {
        ArgumentNullException.ThrowIfNull(tree);

        string basename = AircraftDefinition.FlyableBasenames[aircraftIndex];
        MissionEra era = EraForAircraft(aircraftIndex);
        string theaterAsset = MissionVocabulary.TheaterAssetForEra[(int)era];

        MissionDefinition theater = tree.Theaters[theaterAsset];
        MissionDefinition mission = tree.Missions[TestFlightMissionAsset];

        return new FlightColdStartRequest
        {
            Aircraft = tree.Aircraft[basename],
            Mission = mission,
            Anchors = new MissionSpawnAnchors(theater, [.. Enumerable.Repeat(siteDraw, Math.Max(1, mission.AnchorCount))]),
            // The built-in ±105, not the tree's config.json (the port has no joystick).
            ConfigCalibration = DefaultConfigCalibration,
            PullUpTuning = PullUpTuning(tree),
            PlayerClass = ClassRegistry.IsLoaded ? ClassRegistry.Find(basename) : null,
        };
    }

    /// <summary>Builds the frame-1 kernel state and the pool that owns the player object.</summary>
    /// <param name="request">What to fly, where.</param>
    /// <returns>The same shape <c>FlightTraceSeed.CreateState</c> returns, built from data alone.</returns>
    public static FlightSeedResult Create(FlightColdStartRequest request) =>
        Create(request, out _);

    /// <summary>Builds the frame-1 kernel state and reports which pose arm ran.</summary>
    /// <param name="request">What to fly, where.</param>
    /// <param name="pose">
    /// The <c>aircraft_pose_set @image@0x2A25C</c> result when the altitude gate opened (an AIRBORNE
    /// mission start), or <see langword="null"/> when it did not (a parked Test Flight).
    /// </param>
    /// <returns>The same shape <c>FlightTraceSeed.CreateState</c> returns, built from data alone.</returns>
    public static FlightSeedResult Create(
        FlightColdStartRequest request, out AircraftPoseResult? pose)
    {
        ArgumentNullException.ThrowIfNull(request);

        FlightSpawn spawn = FlightSpawn.FromMission(request.Mission, request.Anchors);
        Aircraft aircraft = Aircraft.Load(request.Aircraft);

        WorldObjectPool pool = request.Pool ?? new WorldObjectPool();
        ClassRecord playerClass = request.PlayerClass ?? DefaultPlayerClass(request.Aircraft);

        // flight_model_load_for_aircraft's fourth override: master[+0x116] is the PLAYER OBJECT's
        // class-record ground clearance, dereferenced through the loader's far-ptr arg (mov es,dx /
        // mov bx,ax / mov bx,es:[bx] / mov ax,[bx+0x2C] @image@0x2A165..0x2A16F), with [+0x118]:= 0
        // @image@0x2A173.  Aircraft.Load cannot know it; the cold start can.
        aircraft.AirspeedB = playerClass.GroundClearance;

        WorldObject player = pool.Insert(
            playerClass,
            spawn.ObjectX,
            spawn.ObjectY,
            spawn.ObjectZ,
            spawn.Heading,
            spawn.Pitch,
            spawn.Roll);

        // active_aircraft_load_and_state_reset's ALTITUDE GATE (image@0x224F8..0x22520): an
        // airborne spawn runs aircraft_pose_set, which raises the gear, commits 100 % throttle and
        // takes the airspeed out of the flight envelope.  A parked spawn (pos_y = 0) skips it.
        pose = AircraftPoseSet.GateOpens(player.Y)
            ? AircraftPoseSet.Apply(
                aircraft, player, (player.X, player.Y, player.Z), clearEuler: false)
            : null;

        // active_aircraft_load_and_state_reset @image@0x22525: the [0xEE52] seed, <<8, becomes the
        // master's forward-velocity i32 — AFTER the pose-setter, so an authored initial_speed wins
        // over the envelope answer.  -1 leaves whatever is there alone.
        if (spawn.InitialSpeedSeed != FlightSpawn.NoSeed)
        {
            aircraft.ForwardVelocity.Value = spawn.InitialSpeedSeed << 8;
        }

        FlightInputs window = new FlightInputs
        {
            Calibration = InFlightCalibration(request.ConfigCalibration),
            PullUpTuning = request.PullUpTuning,
        };

        return new FlightSeedResult(new FlightKernelState(aircraft, player, window), pool);
    }

    private static ClassRecord DefaultPlayerClass(AircraftDefinition definition) =>
        (ClassRegistry.IsLoaded ? ClassRegistry.Find(definition.Name) : null)
        ?? throw new InvalidOperationException(
            $"no class record named '{definition.Name}'; load exe/classes.json (DataTree." +
            "InstallGlobalTables) or set FlightColdStartRequest.PlayerClass.");
}
