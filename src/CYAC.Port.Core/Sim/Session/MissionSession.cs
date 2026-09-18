using CYAC.Port.Core.Model.Combat;
using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Model.World;
using CYAC.Port.Core.Primitives;
using CYAC.Port.Core.Sim.Combat;
using CYAC.Port.Core.Sim.Combat.Effects;
using CYAC.Port.Core.Sim.Combat.Geometry;
using CYAC.Port.Core.Sim.Combat.Grid;
using CYAC.Port.Core.Sim.Combat.Player;
using CYAC.Port.Core.Sim.Combat.Lifecycle;
using CYAC.Port.Core.Sim.Combat.Vm;
using CYAC.Port.Core.Sim.Flight;
using CYAC.Port.Core.Sim.Mission;

namespace CYAC.Port.Core.Sim.Session;

/// <summary>
/// THE JOINT LOOP — the verified combat driver with its external channels wired to the port's own
/// flight kernel, its own key ladder and its own renderer, instead of to a recording.
/// </summary>
/// <remarks>
/// <para>
/// <c>CombatKernelJointRunTests</c> proved the binding against the machine: the combat driver's
/// CS2 → CS3 channel IS <c>FlightKernel.Step</c>, the pose it computes is written into the arena
/// object <c>g_alt_object_farptr [0x00C0]</c> names, the K9 damage channel runs the other way, and
/// <c>master[+0x122]</c> ≡ <c>[0xF0BA]</c> flows COMBAT → FLIGHT.  This type is that binding with the
/// oracle removed: the same four exchanges, seeded from <see cref="CombatColdStart"/> and
/// <see cref="Flight.ColdStart.FlightColdStart"/> instead of from a trace's first record.
/// </para>
/// <para>
/// Nothing in the integer kernel changed to make this work — the whole host lives on the
/// <see cref="ICombatFrameChannels"/> surface the combat kernel declares.
/// </para>
/// </remarks>
public sealed class MissionSession : ICombatFrameChannels
{
    private readonly CombatSessionState _combat;
    private readonly FlightKernelState _flight;
    private readonly TickClock _clock;
    private readonly IKernelWorld _world;
    private readonly IKernelRandom _flightRandom;
    private readonly List<CombatLadderKey> _keys = [];
    private readonly SessionWorldGridAcquisition _acquisition;
    private readonly SessionPlayerCombatEvents _playerEvents;
    private readonly RenderSlotNodes _nodes;
    private int _seenCockpitTexts;

    /// <summary>
    /// <c>g_scene_frame_dt_scaled [0xF11C]</c> — the per-frame dt BOTH kernels read.  In the det
    /// build it is the fixed step (STEP_TICKS = 5).
    /// </summary>
    public const int SceneFrameDt = 0xF11C;

    /// <summary><c>g_master_frame_counter [0xF0C8]</c>.</summary>
    public const int MasterFrameCounter = 0xF0C8;

    /// <summary>The flight-end channel, <c>master[+0x122]</c> ≡ <c>g_text_string_mode [0xF0BA]</c>.</summary>
    public const int FlightEndChannel = 0xF0BA;

    /// <summary>
    /// The THROTTLE-PERCENT channel, <c>master[+0x9D]</c> ≡ <c>g_throttle_pct [0xF035]</c> — the
    /// second alias the two kernels share, after <see cref="FlightEndChannel"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>0xEF98 + 0x9D = 0xF035</c>, so the word the combat side reads IS the unaligned middle of the
    /// flight side&#8217;s <c>throttle_current_lo_i32</c> at <c>master+0x9C</c>
    /// (<see cref="Aircraft.ThrustScaled"/>) — the same window idiom as <c>g_airspeed [0xEF99]</c> =
    /// <c>master[+0x01]</c>.  The original needs no publish because there is only one struct; the port
    /// keeps two kernels, so the flight side must hand the word over, and the direction is FLIGHT →
    /// COMBAT because the flight side is the sole writer: <c>aircraft_pose_set</c> installs
    /// <c>0x00006400</c> (<c>image@0x2A32F</c>) and <c>aircraft_throttle_fuel_step</c> steps it toward
    /// the target every frame (<c>image@0x2A5F3..0x2A61A</c>).  F035 abs</c> finds six reads and zero
    /// writes — the writer goes through the master pointer, which is why a displacement scan cannot see
    /// it, and why the word reads as an engagement flag.
    /// </para>
    /// <para>
    /// Its combat-side readers are <c>PlayerSustainTick</c>&#8217;s ENGINE-meter arm
    /// (<c>image@0x0FD52</c>: climb while the engine is making power, otherwise decay) and the
    /// engine-out test (<c>image@0x0FF95</c>).  Without the publish both read a permanent zero, so a
    /// damaged engine never failed.  Behaviour on an UNDAMAGED sortie is unchanged by construction:
    /// the climb rate <c>[0xF1DB]</c> is 0 unless damage has set it (<c>image@0x0F718</c> zeroes it
    /// at cold start; only <c>image@0x0FB0B</c>/<c>0x0FCF2</c> raise it), and the decay arm floors the
    /// meter at 0 — which is where it already sat.
    /// </para>
    /// </remarks>
    public const int ThrottlePercentChannel = 0xF035;

    /// <summary>
    /// The TARGET-CYCLE key: cooked key <c>0x0D</c> (ENTER), the ladder arm at
    /// <c>image@0x01212</c> whose body <c>image@0x0121C</c> is
    /// <c>mov byte [0xBB],0 / mov byte [0xBA],1</c> — list mode WITHOUT the free-camera selector.
    /// </summary>
    public const int TargetCycleKey = 0x000D;

    /// <summary>Wires a session over a cold-started combat state and a cold-started flight state.</summary>
    /// <param name="combat">The combat half.</param>
    /// <param name="flight">The flight kernel's state.</param>
    /// <param name="clock">The port's tick clock.</param>
    /// <param name="world">The flight kernel's world seam (landing zones, HUD, advisor).</param>
    /// <param name="flightRandom">The flight kernel's own RNG seam (K2: it never draws).</param>
    public MissionSession(
        CombatSessionState combat,
        FlightKernelState flight,
        TickClock clock,
        IKernelWorld world,
        IKernelRandom flightRandom)
    {
        ArgumentNullException.ThrowIfNull(combat);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(flightRandom);

        _combat = combat;
        _flight = flight;
        _clock = clock;
        _world = world;
        _flightRandom = flightRandom;
        _acquisition = new SessionWorldGridAcquisition(
            combat.WorldGrid, combat.Registers, combat.Arena, combat.StaticData);
        Damage = new FlightDamageChannel(flight.Aircraft, this);

        // THE MISSION'S RULES.  The `.S` trailer module is what decides whether the sortie was a
        // success, and the port now RUNS it: `WinRuleEvaluator` interprets the IR whose
        // synthesiser re-emits the shipped module byte for byte.  Five of the 51 missions have no
        // model (ALONE / BOLO / GAUNTLET / INSTR / MOOLAH) and get a stub that is installed,
        // counts and never wins.
        WinRuleEvaluator? evaluator = combat.Mission.WinRules is { } winRules
            ? new WinRuleEvaluator(winRules, combat.Registers, combat.Arena)
            : null;
        Module = evaluator;
        IMissionModule? module = (IMissionModule?)evaluator
                                 ?? (UnmodelledMissions.Contains(combat.Mission.AssetName)
                                     ? new NullMissionModule(combat.Mission.AssetName)
                                     : null);
        UnmodelledModule = module as NullMissionModule;

        Context = CombatKernelContext.Create(
            combat.Registers,
            combat.Arena,
            combat.StaticData,
            combat.Prototypes,
            this,
            _acquisition,
            combat.ScriptHeap,
            Damage,
            module: module,
            terrain: Terrain,
            playerEvents: new SessionPlayerCombatEvents(
                combat.Arena,
                combat.Prototypes,
                _acquisition,
                combat.Countermeasures,
                combat.Registers),
            lifecycleEvents: combat.LifecycleEvents,
            projectileEvents: VmEffects,
            nodeEvents: VmEffects,
            vmEffects: VmEffects);

        // The AI script's 0xE0 hook reaches the module's export 2 through the verified dispatch,
        // exactly as image@0x0547C reaches image@0x08CAB.  It needs the kernel context, so it is
        // wired the moment that exists.
        combat.VmEffects.ModuleDispatch = Context.Lifecycle;

        _playerEvents = (SessionPlayerCombatEvents)Context.Player.Events;
        _nodes = new RenderSlotNodes(combat.Registers);

        // K8's channel is ONE byte with two homes — master[+0x122] and its DGROUP alias
        // g_text_string_mode [0xF0BA].  The port keeps a copy in each, so the combat side has to be
        // SEEDED from the flight side once; after that the flow is combat → flight, every frame.
        combat.Registers.SetByte(FlightEndChannel, flight.Aircraft.ActiveState);

        // The player pose the combat side reads is the flight side's, from frame 1.
        PublishPlayerPose();
        combat.Registers.SetWord(ThrottlePercentChannel, flight.Aircraft.ThrustScaled);
        combat.Registers.SetWord(SceneFrameDt, (ushort)TickClock.StepTicks);
        RenderListBuilder.PublishCameraConstants(combat.Registers);

        // The lock-on's two gate inputs (image@0x01656): the view-mode bit and the HUD's
        // target-info flag.  A flying cockpit view has both.
        combat.Registers.SetByte(LockOnGate.ViewModeBit0Flag, 1);
        combat.Registers.SetByte(LockOnGate.TargetInfoVisible, 1);

        // THE MODULE IS INSTALLED.  `wld_or_s_asset_parser`'s trailer handler publishes the
        // module's far pointer at load (`mov [0xFB8],0` @image@0x0A322 / `mov [0xFBA],ax`
        // @image@0x0A328), and all three combat dispatch sites refuse to call anything while
        // [0x0FBA] is 0 (image@0x08C08 / 0x08C79 / the fn2 site).  The port had never published it,
        // so the mission hook was dead on every sortie; the segment word is the evaluator's own
        // synthetic one, because the original's is whatever `far_heap_alloc` returned.
        if (module is not null)
        {
            combat.Registers.SetWord(
                LifecycleOffsets.MissionVtableSegment,
                WinRuleEvaluator.SyntheticModuleSegment);
        }
    }

    /// <summary>
    /// The mission's rules, running: the interpreter for the <c>.S</c> module's win-rule IR, or
    /// <see langword="null"/> when this mission is one of the five with no model.
    /// </summary>
    public WinRuleEvaluator? Module { get; }

    /// <summary>
    /// The stub that stands in for an UNMODELLED mission's rules (ALONE / BOLO / GAUNTLET / INSTR /
    /// MOOLAH), or <see langword="null"/> when the mission has a real model.
    /// </summary>
    public NullMissionModule? UnmodelledModule { get; }

    /// <summary>
    /// Whether the mission's objective has been met — the port's own LATCH over the module's
    /// per-call win flag.
    /// </summary>
    /// <remarks>
    /// <c>[0xB562]</c> is not a latch: <c>combat_vtable_slot_dispatch</c> clears it before every
    /// dispatch (<c>image@0x08C0F</c>) and reads it back after (<c>image@0x08C60</c>).  The original
    /// does not need one — it raises the advisory each time and the DEBRIEF asks the module again at
    /// the end — but a host that wants to say "objective complete" in flight does.
    /// </remarks>
    public bool MissionWon => Module?.HasWon ?? false;

    /// <summary>The simulated second the objective was first met, or −1.</summary>
    public double MissionWonAtSeconds { get; private set; } = -1.0;

    /// <summary>
    /// The last RADIO CALL the mission module made, or null: the module's non-zero <c>DX:AX</c>,
    /// resolved back to its text.
    /// </summary>
    /// <remarks>
    /// The path is the original's own: <c>check_win_condition</c> returns a far pointer, the
    /// dispatch shows it with <c>show_cockpit_text_string @image@0x0CC5B</c>
    /// (<c>lcall</c> @<c>image@0x08C5B</c>) and the appender stamps a SIX-unit expiry on the HUD
    /// message strip.  <see cref="RadioMessageExpiry"/> is that stamp, on
    /// <c>g_frame_time_accum [0xF0D2]</c>.
    /// </remarks>
    public string? RadioMessage { get; private set; }

    /// <summary>When <see cref="RadioMessage"/> stops being shown, on the frame-time accumulator.</summary>
    public uint RadioMessageExpiry { get; private set; }

    /// <summary>
    /// The mission's NAV SLOTS: <c>g_nav_slot_count [0xEF90]</c>, <c>g_nav_slot_current_index
    /// [0xEF92]</c> and <c>g_nav_slot_initialized_flag [0xB563]</c>, in the verified register
    /// file, with the loader's waypoint records beside them.
    /// </summary>
    public NavSlots Nav => _combat.Nav;

    /// <summary>
    /// The last <c>"NAV n: name"</c> label the NAV key posted, or null.
    /// </summary>
    /// <remarks>
    /// The original's own path is <c>nav_waypoint_show_current @image@0x08D31</c> →
    /// <c>show_cockpit_text_string @image@0x0CC5B</c>, i.e. the SAME message strip and the same
    /// <c>[0xBA38]</c> buffer the module's radio call and the flight kernel's warnings use, with the
    /// same six-unit expiry (<c>image@0x08D5C</c>, <c>image@0x0CC64</c>).
    /// </remarks>
    public string? NavMessage { get; private set; }

    /// <summary>When <see cref="NavMessage"/> stops being shown, on the frame-time accumulator.</summary>
    public uint NavMessageExpiry { get; private set; }

    /// <summary>How many NAV labels this sortie has posted.</summary>
    public int NavMessagesPosted { get; private set; }

    /// <summary>How many radio calls this sortie has heard.</summary>
    public int RadioMessagesPosted { get; private set; }

    /// <summary>The wired kernel context.</summary>
    public CombatKernelContext Context { get; }

    /// <summary>
    /// Which arm of <c>aircraft_pose_set @image@0x2A25C</c> the cold start ran, or
    /// <see langword="null"/> when the spawn was on the deck and the altitude gate stayed shut.
    /// </summary>
    public Flight.ColdStart.AircraftPoseResult? ColdStartPose { get; init; }

    /// <summary>
    /// The flight half exactly as <see cref="Flight.ColdStart.FlightColdStart.Create"/> returned it — the state
    /// AND the pool the player object lives in.
    /// </summary>
    /// <remarks>
    /// A replay harness needs the same <see cref="Flight.Trace.FlightSeedResult"/> a trace seed produces, and it
    /// must be THIS one: a host that built its own would be verifying a different cold start from
    /// the one it flies.  (H5a addendum: the host's <c>--replay --cold-start</c> used to build a
    /// Test-Flight cold start unconditionally, which seeded a historic mission's replay with a
    /// PARKED aeroplane.)
    /// </remarks>
    public Flight.Trace.FlightSeedResult? FlightSeed { get; init; }

    /// <summary>
    /// What <c>custom_mission_build_from_picks @image@0x27E76</c> produced, or <see langword="null"/>
    /// for every other mission kind.
    /// </summary>
    /// <remarks>
    /// The front end reads it for the post-mission line (the original's only reader of
    /// <c>g_custom_mission_spawn_count [0xF292]</c>) and the tests read it to verify the formation.
    /// </remarks>
    public CustomMissionBuildResult? CustomBuild { get; internal set; }

    /// <summary>The picks a CUSTOM MISSION was built from, or <see langword="null"/>.</summary>
    public Model.Mission.CustomMissionPicks? CustomPicks { get; internal set; }

    /// <summary>The combat half.</summary>
    public CombatSessionState Combat => _combat;

    /// <summary>The port's clock, shared with whatever host drives the session.</summary>
    public TickClock Clock => _clock;

    /// <summary>The flight kernel's world seam — the landing-zone table, the HUD and the advisor.</summary>
    public IKernelWorld WorldSeam => _world;

    /// <summary>The flight kernel's state.</summary>
    public FlightKernelState Flight => _flight;

    /// <summary>The K9 damage channel bound to the flight kernel's own aircraft.</summary>
    public IAircraftDamageChannel Damage { get; }

    /// <summary>How many combat frames have run.</summary>
    public long Frames { get; private set; }

    /// <summary>
    /// Stop running the FLIGHT half of the frame.  The combat half keeps going: bandits fly on,
    /// effects age, the destruction pool ticks and the ejection sequence plays, exactly as they do
    /// in the original's four remaining frames.
    /// </summary>
    /// <remarks>
    /// Set only by <see cref="PlayerFate"/> and only from its trigger frame onward; false on every
    /// verifying path.  It is the port's stand-in for the original simply leaving the flight loop
    /// (<c>flight_engine_per_frame_top</c> Path A → <c>flight_session_end @image@0x30328</c>).
    /// </remarks>
    public bool FlightStepSuppressed { get; set; }

    /// <summary>
    /// What <see cref="DamageCheckStage.CheckCrash"/> did on the last flight step, so the fate
    /// machine can tell a CRASH from any other way <c>master[+0x122]</c> could reach 0.
    /// </summary>
    public CrashCheckResult LastCrashCheck { get; private set; }

    /// <summary>Damage applications the combat side drove into the flight master.</summary>
    public long DamageApplications { get; private set; }

    /// <summary>The stick this frame's flight step reads.</summary>
    public short StickX { get; set; }

    /// <summary>…and its Y axis.</summary>
    public short StickY { get; set; }

    /// <summary>
    /// The player's TRIGGER — <c>g_weapon_fire_event_active [0x32ED]</c>, the first of the
    /// scheduler's three-way arming tests (<c>image@0x03514</c>).  Held, not edge-triggered: the
    /// scheduler fires once per 64-tick window for as long as it is set, which is what makes a gun
    /// burst a burst.
    /// </summary>
    public bool Trigger { get; set; }

    /// <summary>Whether the mission runs on the EASY difficulty gate the flight kernel takes.</summary>
    public bool EasyDifficulty { get; set; } = true;

    /// <summary>
    /// The session's EFFECT subsystems — the five per-frame ticks and every combat out-call that
    /// feeds them (<see cref="SessionEffects"/>).
    /// </summary>
    public SessionEffects VmEffects => _combat.VmEffects;

    /// <summary>The terrain-proximity seam, answered as the flat plane the game actually has.</summary>
    public SessionTerrainProximity Terrain { get; } = new();

    /// <summary>The session's world-grid query — H4's permissive seam, replaced by the real index.</summary>
    public SessionWorldGridAcquisition Acquisition => _acquisition;

    /// <summary>How many world-grid queries the session answered.</summary>
    public long GridQueries => _acquisition.Queries;

    /// <summary>
    /// How many of them fell into a grid seam the port cannot answer and were answered permissively.
    /// </summary>
    public long GridQueriesStubbed => _acquisition.SeamMisses;

    /// <summary>The five effect ticks' census (they are stubbed).</summary>
    public EffectTickCensus Effects { get; } = new();

    /// <summary>
    /// The player-side combat seams, so the host can drain the two SOUND counters the verified
    /// kernel records rather than fire them from inside it.
    /// </summary>
    /// <remarks>
    /// <c>PlayFireTone</c> (<c>sfx_weapon_type_tone_dispatch @image@0x29A79</c>, called at
    /// <c>image@0x0350A</c>) and <c>PlayCountermeasureSound</c>
    /// (<c>sfx_countermeasure_deploy_sound @image@0x29B29</c>, <c>image@0x0AFC5</c> /
    /// <c>image@0x0B029</c>) already RECORD every occurrence on <c>PortedPlayerCombatEvents</c> — the
    /// fire tones as a list of weapon-class refs in order, the countermeasures as a count.  The host
    /// reads the new entries once a frame, which is the granularity the audio pump has anyway, and
    /// no verified seam implementation is touched.
    /// </remarks>
    public SessionPlayerCombatEvents PlayerEvents => _playerEvents;

    /// <summary>Selects the next weapon slot (<c>weapon_slot_next @image@0x03419</c>).</summary>
    public void SelectNextWeapon() => WeaponLoadout.SelectNext(_combat.Registers);

    /// <summary>Selects the previous slot (<c>weapon_slot_prev @image@0x03406</c>).</summary>
    public void SelectPreviousWeapon() => WeaponLoadout.SelectPrevious(_combat.Registers);

    /// <summary>The selected weapon slot's rounds remaining.</summary>
    public int RoundsRemaining =>
        _combat.Registers.Word(WeaponLoadout.SlotAmmo + (_combat.Registers.Word(WeaponLoadout.SelectedSlot) * 2));

    /// <summary>Queues one cooked key for the NEXT frame's ladder.</summary>
    /// <param name="cookedKey">The cooked key word (<see cref="CockpitKeyTranslator"/>).</param>
    public void QueueKey(int cookedKey) =>
        _keys.Add(new CombatLadderKey(cookedKey, CombatKeyLadder.Classify(cookedKey)));

    /// <summary>
    /// THE HIT CENSUS — what the player's guns have actually achieved, and what the enemies have.
    /// </summary>
    /// <remarks>
    /// Every number is a live DGROUP word the integer kernel writes, named by its offset, so the
    /// readout is evidence rather than host bookkeeping: <c>[0xF1CC]</c> is the player's DAMAGE TAKEN:
    /// <c>weapon_fire_combat_loop @image@0x0F748</c> adds the damage word to it and
    /// <c>damage_pct_hud_show @image@0x100D0</c> divides it by the ceiling; the rounds the player's guns
    /// HIT are <c>g_gun_rounds_hit [0xED36]</c>, credited by <c>engagement_slot_fire_handler
    /// @image@0x0BDE1..0x0BE0B</c> and read as the numerator by <c>performance_rating_classifier</c> from
    /// <c>mission_stats_screen @image@0x25F2D</c>, <c>[0xF106]</c> is the kill tally,
    /// <c>[0xF1D8]/[0xF1D9]</c> are the player's two hull meters and <c>[0xBD08]/[0xBD14]</c> are the
    /// hit-percentage accumulators <c>engagement_hit_pct_compute @image@0x031CB</c> divides.
    /// </remarks>
    public HitCensus Hits => new(
        RoundsFired,
        _combat.Registers.Word(EngagementFireResolver.GunRoundsDgroupOffset),   // [0xED36] — F3 correction
        _combat.Registers.Word(0xF106),
        _combat.Registers.Byte(0xF1D8),
        _combat.Registers.Byte(0xF1D9),
        DamageApplications,
        _combat.Registers.Byte(0xBD08),
        _combat.Registers.Byte(0xBD14),
        [.. EnemyHullStates()]);

    /// <summary>Rounds the player's guns have spent — the selected slot's ammo, integrated down.</summary>
    public long RoundsFired { get; private set; }

    /// <summary>Every live engagement object's remaining hit points, in list order.</summary>
    /// <remarks>
    /// A list NODE <b>is</b> an <c>s_engagement_state</c> (C1: the 55-byte record has two homes, the
    /// object's embedded block and the list node), and <c>+0x04</c> is its hit points
    /// (<see cref="Model.Combat.EngagementState.HitPoints"/>).
    /// </remarks>
    public IEnumerable<int> EnemyHullStates()
    {
        // The ENGAGEMENT LIST, not the render list: only an object with a node on
        // g_engagement_expiry_list_head [0xEDAA] has an engagement block worth reading, and the
        // render list also carries projectiles and scenery whose +0x04 is something else entirely.
        foreach (ushort node in _combat.Arena.WalkList(_combat.Registers.ExpiryListHead))
        {
            yield return _combat.Arena.Byte((ushort)(node + 0x04));
        }
    }

    /// <summary>
    /// A <b>CHEAT</b>, for photographing the kill sequence: give every live engagement the same
    /// small hit-point count.
    /// </summary>
    /// <param name="hitPoints">The value to write into each node's <c>+0x04</c>, 1…255.</param>
    /// <returns>How many engagements were weakened.</returns>
    /// <remarks>
    /// <para>
    /// It is an INITIAL-STATE edit and nothing else: every step downstream — the armour subtraction,
    /// the half-hit-point damage-smoke threshold, <c>Killed</c>, <c>engagement_kill_finalize</c>,
    /// the death phase, the destruction slot, the ejection, the ground impact and the crater — is
    /// the integer kernel's own, unmodified.  It exists because the crude headless test pilot
    /// cannot take an 80-hit-point Me-109 down in a reasonable sortie, and
    /// a kill has to be PHOTOGRAPHED, not asserted.
    /// </para>
    /// <para>
    /// Label it wherever it is used.  The host does: <c>--foe-hp N</c> prints
    /// <c>CHEAT</c> on the scene line.
    /// </para>
    /// </remarks>
    public int CheatWeakenEnemies(byte hitPoints)
    {
        ArgumentOutOfRangeException.ThrowIfZero(hitPoints);
        int weakened = 0;
        foreach (ushort node in _combat.Arena.WalkList(_combat.Registers.ExpiryListHead))
        {
            if (_combat.Arena.Byte((ushort)(node + 0x04)) > hitPoints)
            {
                _combat.Arena.SetByte((ushort)(node + 0x04), hitPoints);
                weakened++;
            }
        }

        return weakened;
    }

    /// <summary>Runs ONE gameplay frame — the whole ladder CS0..CS10.</summary>
    /// <returns>The kernel's running census.</returns>
    public CombatKernelCensus Step()
    {
        int ammoBefore = RoundsRemaining;
        // Frame body step 0 (image@0x00B43): the scene clock, which makes dt AND the master frame
        // counter every deadline in the kernel is armed against.
        SceneFrameTimer.Advance(_combat.Registers, TickClock.StepTicks);
        _combat.Registers.SetByte(0x32ED, Trigger ? (byte)1 : (byte)0);
        CombatKernelCensus census = CombatKernel.StepFrame(Context);
        int ammoAfter = RoundsRemaining;
        if (ammoAfter < ammoBefore)
        {
            RoundsFired += ammoBefore - ammoAfter;
        }

        _keys.Clear();
        Frames++;
        DrainMissionModule();
        return census;
    }

    /// <summary>
    /// Reads what the mission module did this frame: its radio call and its win latch.
    /// </summary>
    /// <remarks>
    /// The radio call arrives by the original's own path — the module's non-zero <c>DX:AX</c> went
    /// to <c>show_cockpit_text_string</c> through <see cref="SessionLifecycleEvents.ShowCockpitText"/>
    /// — and is stamped with the appender's own SIX-unit expiry on
    /// <c>g_frame_time_accum [0xF0D2]</c> (<see cref="Model.Cockpit.HudMessageTable.MessageTicks"/>,
    /// <c>image@0x0CC64</c> / <c>image@0x0CC3F..0x0CC4C</c>).
    /// </remarks>
    private void DrainMissionModule()
    {
        if (Module is not { } module)
        {
            return;
        }

        if (module.HasWon && MissionWonAtSeconds < 0)
        {
            MissionWonAtSeconds = Frames * TickClock.StepTicks / TickClock.TicksPerSecond;
        }

        int posts = _combat.LifecycleEvents.CockpitTexts;
        if (posts == _seenCockpitTexts)
        {
            return;
        }

        _seenCockpitTexts = posts;
        if (module.ResolveText(_combat.LifecycleEvents.LastCockpitText) is not { } text)
        {
            return;
        }

        RadioMessage = text;
        RadioMessagesPosted++;
        RadioMessageExpiry = unchecked(
            FrameTimeAccumulator + (uint)Model.Cockpit.HudMessageTable.MessageTicks);
    }

    /// <summary>Whether the radio call is still up, on the frame-time accumulator.</summary>
    /// <param name="accumulator">The current <c>g_frame_time_accum [0xF0D2]</c>.</param>
    /// <returns>The message, or null.</returns>
    public string? RadioMessageAt(uint accumulator) =>
        RadioMessage is { } text && unchecked((int)(accumulator - RadioMessageExpiry)) < 0
            ? text
            : null;

    /// <summary>
    /// The NAV key: <c>nav_slot_cycle_next @image@0x08D65</c> (<b>W</b>, dispatch <c>image@0x0127D</c>)
    /// and <c>nav_slot_cycle_prev @image@0x08D8A</c> (<b>Shift+W</b>, dispatch <c>image@0x0128C</c>), with
    /// the label they post on the message strip.
    /// </summary>
    /// <param name="step">+1 for the next waypoint, −1 for the previous; anything else is ignored.</param>
    /// <returns>The posted label, or null when the selected slot has no record.</returns>
    /// <remarks>
    /// Both dispatch arms are guarded by <c>cmp byte [0xC31C],0</c> — the in-mission gate
    /// (<c>image@0x01274</c> / <c>image@0x01285</c>) — which is why this lives on the mission
    /// session and a Test Flight without a mission never reaches it.
    /// </remarks>
    public string? CycleNavWaypoint(int step)
    {
        string? label = step switch
        {
            > 0 => Nav.CycleNext(),
            < 0 => Nav.CyclePrevious(),
            _ => null,
        };

        if (label is null)
        {
            return null;
        }

        NavMessage = label;
        NavMessagesPosted++;
        NavMessageExpiry = unchecked(
            FrameTimeAccumulator + (uint)Model.Cockpit.HudMessageTable.MessageTicks);
        return label;
    }

    /// <summary>Whether the NAV label is still up, on the frame-time accumulator.</summary>
    /// <param name="accumulator">The current <c>g_frame_time_accum [0xF0D2]</c>.</param>
    /// <returns>The label, or null.</returns>
    public string? NavMessageAt(uint accumulator) =>
        NavMessage is { } text && unchecked((int)(accumulator - NavMessageExpiry)) < 0
            ? text
            : null;

    /// <summary><c>g_frame_time_accum [0xF0D2]</c>, the 32-bit stamp the strip's expiry is on.</summary>
    private uint FrameTimeAccumulator => unchecked((uint)(
        _combat.Registers.Word(SceneFrameTimer.FrameTime)
        | (_combat.Registers.Word(SceneFrameTimer.FrameTime + 2) << 16)));

    /// <inheritdoc/>
    /// <remarks>
    /// CS1 → CS2, <c>image@0x00CCC..0x00CE0</c>.  Three of the five ticks are PORTED
    /// (<see cref="SessionEffects.RunTicks"/>: the deferred-effect fire, the emitter rows and the
    /// smoke physics) and the other two are counted with their exact cost named
    /// (<see cref="EffectCensus"/>).
    /// </remarks>
    public void EffectTicks(CombatKernelContext context)
    {
        Effects.Frames++;
        VmEffects.RunTicks();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// CS2 → CS3 — the JOINT exchange.  The advisor and the stick read are the host's; the flight
    /// step is the kernel; the pose it computes is written into the arena object
    /// <c>[0x00C0]</c> names, exactly as <c>aircraft_physics_apply_velocity</c>'s phase 9b does
    /// (<c>image@0x2BFF0</c>/<c>0x2C003</c>/<c>0x2C016</c>).
    /// </remarks>
    public void FlightAndInput(CombatKernelContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters registers = context.Registers;
        ushort dt = registers.Word(SceneFrameDt);

        // The sortie is over: the ORIGINAL stops running flight frames the moment
        // flight_engine_per_frame_top takes Path A (image@0x229AA), and four frames later the
        // screen is the debrief.  The port's fate machine holds the wreck instead, and this is
        // where "no more flight frames" is spelt.  FALSE by construction on every verifying path
        // (nothing sets it under --replay, and a joint run never builds a PlayerFate).
        if (!FlightStepSuppressed)
        {
            FlightFrameInputs inputs = new FlightFrameInputs(
                StickX, StickY, registers.Word(MasterFrameCounter), EasyDifficulty, dt);
            LastCrashCheck = FlightKernel.Step(
                _flight,
                inputs,
                FlightKernel.ResolveDt(DtPolicy.TickClock, _flight, _clock, dt),
                _flightRandom,
                _world,
                VelocityDynamics.Instance).CrashCheck;
        }

        PublishPlayerPose();

        // K8's channel, [0xF0BA] ≡ master[+0x122].  The JOINT RUN copies it COMBAT → FLIGHT
        // because there the RECORDING is authoritative — the machine's scene really did write the
        // byte and the port must consume it.  A LIVE host is the other case: the only writer outside
        // the flight chain is `mov byte [0xF0BA],0` @image@0x2265A inside
        // `scene_setup_or_camera_reset`, which a host calls explicitly at a scene change, so between
        // scene changes the FLIGHT kernel is the byte's sole writer — and what it writes there is
        // the CRASH VERDICT (`DamageCheckStage.CheckCrash` sets ActiveState = 0).
        //
        // Copying combat → flight here would therefore ERASE the crash: measured, a mission sortie
        // flown into the ground kept master[+0x122] = 1 for ever, because the stale DGROUP copy was
        // written back over the kernel's own verdict every frame.  The live binding is the mirror.
        registers.SetByte(FlightEndChannel, _flight.Aircraft.ActiveState);

        // The THROTTLE, the second shared alias.  master[+0x9D] ≡ [0xF035]; the flight side owns the
        // word (aircraft_throttle_fuel_step steps master+0x9C toward master+0xA0 every frame,
        // image@0x2A5F3..0x2A61A) and the combat side only reads it, so the publish belongs here,
        // beside the pose, and it publishes the CURRENT throttle, not the target.
        registers.SetWord(ThrottlePercentChannel, _flight.Aircraft.ThrustScaled);
    }

    /// <inheritdoc/>
    public IReadOnlyList<CombatLadderKey> KeyLadder(CombatKernelContext context) => _keys;

    /// <inheritdoc/>
    /// <remarks>
    /// The ladder's non-combat arms.  <see cref="CombatLadderArm.Cockpit"/> is the flight kernel's
    /// <c>cockpit_key_dispatch_gated @image@0x2257C</c> and reaches the flight master; every other
    /// arm belongs to a subsystem the PoC has not built (the nav display, the ESC menu, eject).
    /// </remarks>
    public void DispatchLadderKey(CombatKernelContext context, CombatLadderKey key)
    {
        ArgumentNullException.ThrowIfNull(context);

        // image@0x01229 — the ladder arm that OPENS the lock-on's LIST mode: `mov byte [0xBB],1 /
        // mov byte [0xBA],1` on cooked key 0x27 (C10's key-ladder correction; It is not the combat
        // kernel's own arm — the port models it here because it is two stores and it is the ONLY
        // way a player ever acquires a target.
        if (key.CookedKey is TargetCycleKey or CombatKeyLadder.LockOnListModeKey)
        {
            // TWO arms open the list, and they differ in ONE byte.  Cooked key 0x0D (ENTER,
            // handler image@0x01212 → image@0x0121C) sets `[0xBB] = 0; [0xBA] = 1`; cooked key
            // 0x27 (image@0x01229) sets `mov al,1; [0xBB] = [0xBA] = al`.  `[0x00BB]` is
            // PlayerCombatOffsets.LockOnFreeCameraMode, which routes the selection through
            // `target_select_by_freecam_proximity` instead of the screen-order walk — so ENTER is
            // the target-cycle key a pilot wants and 0x27 is the free-camera one.
            context.Registers.SetByte(
                0x00BB, key.CookedKey == CombatKeyLadder.LockOnListModeKey ? (byte)1 : (byte)0);
            context.Registers.SetByte(0x00BA, 1);
            Effects.TargetCycles++;
            return;
        }

        if (key.Arm != CombatLadderArm.Cockpit)
        {
            Effects.UnmodelledKeys++;
            return;
        }

        CockpitKeys.Apply(
            _flight.Aircraft,
            key.CookedKey,
            _flight.Player.Y,
            _flight.Window.GroundProximityFlag);
        Effects.CockpitKeys++;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <c>image@0x01570</c> — the short arm is taken only when <c>[0xC320] == 0x0C</c>, which is a
    /// non-gameplay screen; a flying host always runs the full arm.
    /// </remarks>
    public bool RenderPhaseRuns(CombatKernelContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Registers.Byte(CombatKernel.RenderArmSelector)
            != CombatKernel.RenderArmShortValue;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The Fx half of the render phase.  The port's renderer draws from the pool directly and does
    /// not build the original's display list, so the LOCK-ON is given the list it can walk: the
    /// engagement objects themselves.  <see cref="SessionPlayerCombatEvents"/> answers the two
    /// questions the lock-on asks of the renderer.
    /// </remarks>
    public void RenderPhase(CombatKernelContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Effects.RenderFrames++;

        // The AIRCRAFT SHADOWS.  subsystem4x04_per_frame_dispatch @image@0x0B6EA runs from the
        // render phase (lcall 0x108e:0xae0a @image@0x015C5, right after the view anchor's own
        // setup), which is where it belongs: the pass reads the VIEW ANCHOR's range and the render
        // object list.  It writes only its own five slots and the pose of their objects.
        ShadowsLive = CYAC.Port.Core.Sim.Combat.Effects.ShadowTable.PerFrameUpdate(context.Lifecycle);

        RenderedObjects = RenderListBuilder.Build(
            context.Registers, context.Arena, context.Registers.PlayerObjectRef);
        _playerEvents.SetRenderList(_nodes, LockOnClipRect.From(context.Registers));
    }

    /// <summary>How many objects the last frame's display list held.</summary>
    public int RenderedObjects { get; private set; }

    /// <summary>How many of the five aircraft-shadow slots are following something.</summary>
    public int ShadowsLive { get; private set; }

    /// <inheritdoc/>
    public ushort RenderListHead(CombatKernelContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Registers.Word(CombatKernel.RenderListHeadWord);
    }

    /// <inheritdoc/>
    public void HudPhase(CombatKernelContext context)
    {
        Effects.HudFrames++;
    }

    /// <summary>The player pose the combat kernel reads, refreshed from the flight kernel.</summary>
    private void PublishPlayerPose()
    {
        ushort playerRef = _combat.Registers.PlayerObjectRef;
        if (playerRef == 0 || !_combat.Arena.Covers(playerRef, 0x18))
        {
            return;
        }

        new CombatObjectView(_combat.Arena, playerRef).Position =
            new CombatPosition(_flight.Player.X, _flight.Player.Y, _flight.Player.Z);
        _combat.Arena.SetWord((ushort)(playerRef + 0x12), _flight.Player.Heading.Units);
        _combat.Arena.SetWord((ushort)(playerRef + 0x14), _flight.Player.Pitch.Units);
        _combat.Arena.SetWord((ushort)(playerRef + 0x16), _flight.Player.Roll.Units);
    }

    /// <summary>K9's damage channel bound to the flight kernel's own aircraft.</summary>
    private sealed class FlightDamageChannel(Model.Flight.Aircraft aircraft, MissionSession session)
        : IAircraftDamageChannel
    {
        /// <inheritdoc/>
        public void Apply(byte typeBits, short amountPercent)
        {
            session.DamageApplications++;
            AircraftDamage.Apply(aircraft, (AircraftDamageFlags)typeBits, amountPercent);
        }

        /// <inheritdoc/>
        public void HalveElevatorBounds() => AircraftDamage.HalveElevatorBounds(aircraft);

        /// <inheritdoc/>
        public void HalveAileronAuthority() => AircraftDamage.HalveAileronAuthority(aircraft);

        /// <inheritdoc/>
        public void DoubleInducedDrag() => AircraftDamage.DoubleInducedDrag(aircraft);
    }
}

/// <summary>
/// The terrain-proximity seam, answered as ZERO — the flat plane CYAC actually has.
/// </summary>
/// <remarks>
/// <c>grid_2d_tallest_obstacle_score_at @image@0x21CEB</c> scores the tallest obstacle around a
/// ground-plane point, and <c>engagement_shot_angle_select_by_range @image@0x04D29</c> compares it
/// UNSIGNED against the clipped range: a bigger score means "there is a hill between me and the
/// shot".  CYAC's terrain is a flat plane with named meshes standing on it
/// (<c>project_terrain_resolved</c>, runtime-proven), so 0 — "flat ground here" — is the truthful
/// answer for everything except the handful of <c>mountain</c> / <c>mount2</c> instances, and it is
/// the answer the six reference recordings never needed at all (the arm fired 0 times in 27,177
/// calls).  Closing it properly needs the 2-D cell buckets <c>grid_2d_cell_best_object_insert
/// @image@0x21B05</c> fills at spawn.
/// </remarks>
public sealed class SessionTerrainProximity : ITerrainProximity
{
    /// <summary>How many scores were answered.</summary>
    public long Scores { get; private set; }

    /// <inheritdoc/>
    public ushort ScoreAt(int x, int z)
    {
        Scores++;
        return 0;
    }
}

/// <summary>How often the host answered a channel it does not model yet.</summary>
public sealed class EffectTickCensus
{
    /// <summary>Frames that asked for the five effect ticks.</summary>
    public long Frames { get; internal set; }

    /// <summary>Frames that ran the full render arm.</summary>
    public long RenderFrames { get; internal set; }

    /// <summary>Frames that ran the HUD phase.</summary>
    public long HudFrames { get; internal set; }

    /// <summary>Cockpit keys forwarded to the flight side.</summary>
    public long CockpitKeys { get; internal set; }

    /// <summary>Ladder keys whose arm belongs to a subsystem the PoC has not built.</summary>
    public long UnmodelledKeys { get; internal set; }

    /// <summary>Target-cycle presses (the lock-on's list mode, <c>image@0x01229</c>).</summary>
    public long TargetCycles { get; internal set; }
}

/// <summary>
/// The world-grid seam, answered PERMISSIVELY — the H4 stopgap for the spatial index the port does
/// not build yet.
/// </summary>
/// <remarks>
/// <para>
/// <c>world_grid_frustum_query_and_select @image@0x28540</c> answers "is anything in the way", and
/// every combat reader treats a NON-zero answer as a REFUSAL:
/// <c>enemy_target_acquisition_state_machine</c> aborts its fire gate on
/// <c>or ax,ax / je</c> (<c>image@0x08365</c>) and the lock-on's fifth frustum gate qualifies a
/// candidate only when the answer is 0 (<c>image@0x031B4</c>).  Answering 0 therefore means
/// "line of sight is clear", which is what a scene with no terrain occlusion actually is — the port
/// draws a flat plane with named meshes on it (<c>project_terrain_resolved</c>).
/// </para>
/// <para>
/// What it COSTS, precisely: a projectile's target RE-acquisition
/// (<c>combat_object_tick</c>'s phase 5) never finds a new target, so a missile that loses its lock
/// flies on ballistically instead of picking up another contact; and neither enemies nor the player
/// are ever refused a shot for a hill in the way.  Building the real index needs the 2-D cell
/// buckets <c>grid_2d_cell_best_object_insert</c> fills at spawn — <see cref="WorldGridBuilder"/> is
/// already ported and waits only for that <see cref="IWorldGridCellIndex"/>.
/// </para>
/// </remarks>
public sealed class SessionTargetAcquisition : ITargetAcquisition, ILockOnGridQuery
{
    /// <summary>How many queries were answered.</summary>
    public long Queries { get; private set; }

    /// <inheritdoc/>
    public ushort Query(in GridQueryRequest request, out CombatPosition selectionPoint)
    {
        Queries++;
        selectionPoint = default;
        return 0;
    }

    /// <inheritdoc/>
    public ushort Query(ushort node, ushort objectRef)
    {
        Queries++;
        return 0;
    }
}

/// <summary>
/// The player side's out-calls for a RUNNING host, with the lock-on's render-list questions answered
/// from the pool instead of from the original's display list.
/// </summary>
/// <param name="arena">The pool arena.</param>
/// <param name="prototypes">The engagement class prototypes.</param>
/// <param name="gridQuery">The world-grid seam the fifth frustum gate consults.</param>
/// <param name="countermeasures">
/// H16 seam 2 — the five countermeasure slots (<c>g_countermeasure_table_BASE [0xB82E]</c>
/// descending to <c>[0xB7BE]</c>).  Null leaves <see cref="SpawnCountermeasureCloud"/> on the base
/// class's counting body, which is what every non-session harness wants.
/// </param>
/// <param name="clock">
/// H16 seam 2 — the register file the three cloud stamps are taken from (<c>g_frame_time_accum
/// [0xF0D2:0xF0D4]</c>, <c>image@0x0AC85</c>).
/// </param>
public sealed class SessionPlayerCombatEvents(
    PoolArena arena,
    IEngagementPrototypes prototypes,
    ILockOnGridQuery gridQuery,
    CountermeasureTable? countermeasures = null,
    CombatRegisters? clock = null)
    : PortedPlayerCombatEvents(arena)
{
    private readonly IEngagementPrototypes _prototypes =
        prototypes ?? throw new ArgumentNullException(nameof(prototypes));

    private readonly ILockOnGridQuery _gridQuery =
        gridQuery ?? throw new ArgumentNullException(nameof(gridQuery));

    private readonly CountermeasureTable? _countermeasures = countermeasures;

    private readonly CombatRegisters? _clock = clock;

    /// <summary>How many frames the lock-on ran a candidate walk on.</summary>
    public int LockOnWalks { get; private set; }

    /// <summary>How many chaff/flare clouds this session actually dispensed.</summary>
    public int CountermeasureCloudsSpawned { get; private set; }

    /// <summary>
    /// H16 seam 2 — <c>countermeasure_cloud_spawn @image@0x0ABC3</c>, for real.
    /// </summary>
    /// <param name="kind">1 = chaff, 2 = flare.</param>
    /// <param name="heading">The wrapped launch heading.</param>
    /// <param name="origin">The player object's <c>+6</c> — the position triple.</param>
    /// <returns>The cloud's pool near offset, or 0 when every slot is busy.</returns>
    /// <remarks>
    /// The base class only recorded the request and answered 0, which made
    /// <c>Countermeasures.Deploy</c> take its pool-full arm (<c>image@0x0AF9F</c>): the stock was
    /// never decremented, no decoy window opened and the cockpit's counters stayed at 15/15.
    /// <see cref="Combat.Effects.CountermeasurePool.Spawn"/> is the ported body; the base's
    /// <c>CountermeasureClouds</c> list keeps recording every request, spawned or not.
    /// </remarks>
    public override ushort SpawnCountermeasureCloud(byte kind, short heading, ushort origin)
    {
        ushort cloud = base.SpawnCountermeasureCloud(kind, heading, origin);
        if (_countermeasures is null || _clock is null)
        {
            return cloud;
        }

        cloud = CountermeasurePool.Spawn(
            _countermeasures,
            Arena,
            kind,
            heading,
            origin,
            unchecked((uint)(_clock.Word(0xF0D2) | (_clock.Word(0xF0D4) << 16))));
        if (cloud != 0)
        {
            CountermeasureCloudsSpawned++;
        }

        return cloud;
    }

    /// <summary>
    /// Publishes the display list the lock-on walks for one frame.
    /// </summary>
    /// <param name="nodes">The render-slot nodes the port's renderer produced.</param>
    /// <param name="clip">The screen clip rectangle.</param>
    public void SetRenderList(RenderSlotNodes nodes, LockOnClipRect clip)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        RenderList = new LockOnRenderList(nodes, Arena, _prototypes, clip, _gridQuery);
        LockOnWalks++;
    }
}

/// <summary>One live pool object as the host's renderer needs it.</summary>
/// <param name="ObjectRef">Its arena near offset.</param>
/// <param name="ClassRecordRef">The DGROUP class record its <c>+0x00</c> names.</param>
/// <param name="X">World X, in world units (the arena's <c>i32 &gt;&gt; 8</c>).</param>
/// <param name="Y">World Y (up).</param>
/// <param name="Z">World Z.</param>
/// <param name="HeadingDegrees">Heading, from the <c>+0x12</c> BAM word (2,880 to the circle).</param>
/// <param name="PitchDegrees">Pitch, from <c>+0x14</c>.</param>
/// <param name="RollDegrees">Roll, from <c>+0x16</c>.</param>
/// <param name="IsPlayer">Whether this is the player's own aircraft.</param>
/// <param name="GearDown">
/// Whether the ORIGINAL would draw this object's landing gear down (<see cref="Combat.EnemyGearState"/>,
/// <c>mesh_lod_prepare_gear_and_flame_state @image@0x2D9A6</c>). It is a per-OBJECT decision taken from
/// the object's own <c>+0x03</c> flag bit 3 and its own <c>+0x25</c> engagement phase — the player's
/// animated <c>[0xEF96]</c> angle never reaches anyone else.  Meaningless for <see cref="IsPlayer"/>,
/// whose gear the flight side animates.
/// </param>
/// <param name="EffectAgeFrameTime">
/// When this object is the pool object of a live DEFERRED-EFFECT record, how many frame-time units old
/// that record is (<c>age = g_frame_time_accum_lo [0xF0D2] − record[+2]</c>, <c>image@0x03C62</c>);
/// <c>−1</c> when the object is not an effect.  The record's life is
/// <see cref="Combat.Effects.DeferredEffectPool.FlashFrameTime"/> = <c>0x100</c>, so the age is the
/// explosion's own clock and everything the callback draws is a function of it.
/// </param>
/// <param name="EffectFork">
/// The record's <c>+0x0C</c> byte — the FORK <c>deferred_effect_render @image@0x03E18</c> takes at
/// <c>image@0x03E30</c>: <c>0</c> ⇒ <c>effect_particle_draw</c> (the growing palette-7 disc and its
/// eight shards), non-zero ⇒ the bitmap explosion or the six-disc particle burst.
/// </param>
/// <param name="EffectSequence">
/// The record's <c>+0x0A</c> sequence number (<c>g_deferred_effect_sequence [0x0DFC]</c>), low byte —
/// the per-explosion PRNG-ish orientation seed the debris ring is rotated by (<c>mov bl,[bx+0xa] / add
/// bl,cl / and bx,7</c> @<c>image@0x03D60</c>).
/// </param>
/// <param name="HiddenLeafNodes">
/// The paint-tree LEAF NODES (<c>image@</c> addresses, the keys of <c>MeshLod.PaintLeaves</c>) this
/// object's per-class prepare callback would tag HIDDEN this frame — the ejection meshes' pilot/seat
/// split and the parachutist's limb set (<see cref="Combat.Lifecycle.EjectionLeafTags"/>,
/// <c>image@0x2C9F4</c> / <c>image@0x2CAC8</c>). Empty for every other class.
/// </param>
/// <param name="AirspeedFps">
/// The object's own true airspeed in ft/s, or 0 when it carries no engagement block — the <c>+0x26</c>
/// word of <c>s_engagement_state</c> (<c>EngagementState.SpeedFps</c>, the <c>&gt;&gt;8</c> view of the
/// Q8 speed at <c>+0x25</c> that the manoeuvring kernel integrates and <c>[0xED7A]</c> reads).  The
/// sound path's engine-pitch law (<c>engine_sound_pitch_compute @image@0x29C20</c>) takes exactly this
/// quantity in <c>BX</c>.
/// </param>
/// <param name="SmokePuff">
/// When the object is a SMOKE PUFF, its slot's kind, age and span — what the <c>smoke</c> class's
/// prepare callback <c>smoke_sprite_render_params_setup @image@0x0B2FF</c> reads to size and colour the
/// three discs every frame (the law is <c>CYAC.Port.Render.SmokeLook</c>). The lookup is the callback's
/// own slot walk, done here on the sim's side of the fence
/// (<see cref="Combat.Effects.SmokePuffState.Read"/>); null for every other object.
/// </param>
/// <param name="Hostile">
/// The object's own SIDE — flag word bit 10, <see cref="Model.World.WorldObjectFlags.Hostile"/>. It is
/// fixed at spawn by the <c>.S</c> parser (<c>or byte es:[bx+3],4</c> @<c>image@0x0A1D4</c>, the sole
/// writer image-wide) and the game itself reads it to colour an object in the BOX VIEW: <c>and al,0xFD /
/// add ax,0xFF0C</c> @<c>image@0x33E6C</c> gives <c>0xFF09</c> (lt. blue, "Friendlies and bailed-out
/// pilots") when clear and <c>0xFF0C</c> (lt. red, "Hostiles") when set — the manual's Identification
/// Key, p.67.  The map plots aircraft with the same two colours.
/// </param>
/// <param name="CarriesEngagement">
/// Flag word bit 11 — the arena record is followed by an engagement block (<c>test byte es:[bx+3],8</c>
/// @<c>image@0x0298E</c>).  It is the cheapest honest answer to "is this thing a COMBATANT rather than a
/// puff of smoke, a bullet or a shadow", which is what decides whether the map plots a glyph for it.
/// </param>
public readonly record struct CombatSceneObject(
    ushort ObjectRef,
    ushort ClassRecordRef,
    double X,
    double Y,
    double Z,
    double HeadingDegrees,
    double PitchDegrees,
    double RollDegrees,
    bool IsPlayer,
    bool GearDown = true,
    int EffectAgeFrameTime = -1,
    byte EffectFork = 0,
    byte EffectSequence = 0,
    int AirspeedFps = 0,
    IReadOnlyList<int>? HiddenLeafNodes = null,
    Combat.Effects.SmokePuffState? SmokePuff = null,
    bool Hostile = false,
    bool CarriesEngagement = false);

/// <summary>
/// Reads the live pool as a list of drawable objects — the SIM's own view of what is in the world.
/// </summary>
/// <remarks>
/// <para>
/// The walk is the original's own: the sibling chain from <c>g_render_object_list_head [0x0096]</c>
/// through each record's <c>+0x04</c> (<c>pool_insert_no_parent @image@0x152BB</c> writes that link),
/// with the <c>Active</c> flag bit 0 as the filter — which is exactly what
/// <c>gx_subsystem_init_30x1B</c> clears on a spawn slot's idle projectile
/// (<c>and byte es:[bx+2],0xFE</c> @<c>image@0x023EC</c>) and what the shot allocator sets again when
/// the slot fires (<c>shot.Flags = 0x0001</c>, <c>image@0x025D3</c>).  So an idle projectile is
/// invisible and a live one is drawn, for free.
/// </para>
/// <para>
/// Units: the arena keeps <c>world &lt;&lt; 8</c> and the angles are the 2,880-unit BAM circle, so a
/// caller gets world units and degrees and the renderer never sees a shift.
/// </para>
/// </remarks>
public static class CombatSceneObjects
{
    /// <summary>The Active flag bit — <c>WorldObjectFlags.Active</c>.</summary>
    public const ushort ActiveFlag = 0x0001;

    /// <summary>Degrees per BAM unit: the circle is 2,880 units.</summary>
    public const double DegreesPerUnit = 360.0 / 2880.0;

    /// <summary>Walks the live pool.</summary>
    /// <param name="registers">The combat register file (for the list head and the player).</param>
    /// <param name="arena">The pool arena.</param>
    /// <param name="limit">A sanity cap on the walk.</param>
    /// <param name="ownAircraftView">
    /// Whether the frame is drawn from one of the six own-aircraft views (F1..F6) — the
    /// <c>[0xE470]</c> view bit the ejection meshes' prepare callbacks read
    /// (<see cref="Combat.Lifecycle.EjectionLeafTags.IsOwnAircraftView"/>).
    /// </param>
    /// <param name="phaseAttributes">
    /// The phase-attribute table the gear verdict reads
    /// (<see cref="Combat.EnemyGearState.GearDown"/>), or null for the process-wide one
    /// <c>DataTree.InstallGlobalTables</c> installs.
    /// </param>
    /// <returns>Every ACTIVE object, in list order.</returns>
    public static IEnumerable<CombatSceneObject> Live(
        CombatRegisters registers,
        PoolArena arena,
        int limit = 512,
        bool ownAircraftView = false,
        Combat.EngagementPhaseAttributes? phaseAttributes = null)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(arena);

        ushort player = registers.PlayerObjectRef;
        byte engagementMode = registers.Byte(0xF28A);   // g_model_preview_mode_flag (ex g_engagement_mode_flag) — 0 in flight

        // The EXPLOSION CLOCK.  deferred_effect_render @image@0x03E4A walks the ten records from
        // [0xB520] down to [0xB4A2] looking for the one whose +0x00 is the object being drawn
        // (g_render_current_object_id [0xEA0C], image@0x03E60..0x03E68); everything the callback
        // draws is then a function of that record's age, fork byte and sequence.  The port does
        // the same walk ONCE per frame here, sim-side, and hands the renderer three numbers — it
        // never gives the renderer a pool pointer.
        Dictionary<ushort, (int Age, byte Fork, byte Sequence)> effects = new Dictionary<ushort, (int Age, byte Fork, byte Sequence)>();
        int now = unchecked((int)ReadEffectClock(registers));
        foreach (int record in Combat.Effects.DeferredEffectPool.Records())
        {
            ushort obj = registers.Word(record);
            if (obj != 0)
            {
                effects[obj] = (
                    unchecked((ushort)(now - registers.Word(record + 2))),   // image@0x03C62/0x03E6F
                    registers.Byte(record + 0x0C),                           // image@0x03E30
                    unchecked((byte)registers.Word(record + 0x0A)));         // image@0x03D60
            }
        }

        ushort cursor = registers.Word(Combat.Lifecycle.LifecycleOffsets.RenderListHead);
        for (int visited = 0; cursor != 0 && visited < limit; visited++)
        {
            if (!arena.Covers(cursor, 0x18))
            {
                yield break;
            }

            CombatObjectView view = new CombatObjectView(arena, cursor);
            ushort next = arena.Word((ushort)(cursor + 0x04));
            if ((view.Flags & ActiveFlag) != 0 && view.ClassRef != 0)
            {
                CombatPosition position = view.Position;
                yield return new CombatSceneObject(
                    cursor,
                    view.ClassRef,
                    position.X / 256.0,
                    position.Y / 256.0,
                    position.Z / 256.0,
                    unchecked((ushort)view.Heading) * DegreesPerUnit,
                    unchecked((ushort)view.Elevation) * DegreesPerUnit,
                    arena.Word((ushort)(cursor + 0x16)) * DegreesPerUnit,
                    cursor == player,
                    // The original's own per-object gear verdict (image@0x2D9A6): the object's
                    // +0x03 flag bit 3 and its +0x25 engagement phase, with [0xF28A] as the global
                    // override.  Reading it HERE keeps it on the sim's side of the fence — the
                    // renderer is handed a verdict, not a pool pointer.
                    EnemyGearState.GearDown(
                        engagementMode,
                        arena.Byte((ushort)(cursor + 0x03)),
                        arena.Byte((ushort)(cursor + 0x25)),
                        phaseAttributes),
                    effects.TryGetValue(cursor, out (int Age, byte Fork, byte Sequence) effect) ? effect.Age : -1,
                    effect.Fork,
                    effect.Sequence,
                    // The engagement block's own speed word, for the engine-pitch law.  An object
                    // without one (a bullet, a puff, a crater) simply reports 0.
                    view.CarriesEngagement
                        ? arena.Word((ushort)(view.EngagementBlockRef + 0x26))
                        : 0,
                    // The ejection meshes' prepare callbacks, run sim-side like the gear verdict:
                    // which paint-tree leaves this object hides (the seat hides the pilot's
                    // parts, the pilot hides the seat's, the parachutist shows one limb set).
                    // Every other class gets an empty list.
                    Combat.Lifecycle.EjectionLeafTags.HiddenLeafNodes(
                        registers, cursor, view.ClassRef, ownAircraftView),
                    // The smoke class's prepare callback, run sim-side like the two above: which
                    // smoke slot this object is, how old, and of what kind, so the renderer can
                    // grow the three discs (25 → 200 world units) as the original does instead of
                    // drawing the static 16–20-unit records.
                    view.ClassRef == Combat.Effects.SmokePuffTable.SmokeClassRecord
                        ? Combat.Effects.SmokePuffState.Read(registers, cursor)
                        : null,
                    // The two flag-word bits the MAP needs, read where every other
                    // per-object verdict is read: on the sim's side of the fence.
                    (view.Flags & (ushort)Model.World.WorldObjectFlags.Hostile) != 0,
                    view.CarriesEngagement);
            }

            cursor = next;
        }
    }

    /// <summary>
    /// <c>g_frame_time_accum [0xF0D2]</c>'s low word — the clock an effect's age is measured on
    /// (<c>mov ax,[0xf0d2] / sub ax,[bx+2]</c> @<c>image@0x03C62</c>, a 16-bit subtraction).
    /// </summary>
    /// <param name="registers">The register file.</param>
    /// <returns>The low word.</returns>
    private static ushort ReadEffectClock(CombatRegisters registers) =>
        registers.Word(Combat.Effects.DeferredEffectPool.FrameTimeAccumulator);
}

/// <summary>
/// The AI script's effect calls for a RUNNING host: counted, named, and — where the effect is one of
/// the five stubbed effect ticks — deliberately dropped.
/// </summary>
/// <remarks>
/// <para>
/// The tripwire <c>UnavailableVmScriptEffects</c> is the right default for a VERIFICATION (an arm
/// no recording reached must announce itself), but a game cannot throw: An earlier pass proved that an
/// authored program really does run (<c>0xDF PRINT_STRING</c> fires on four recordings) and the
/// close-range kill path reaches <c>0xD6 SPAWN_ACTOR</c> — the port hit it on frame ~15,000 of a
/// headless sortie.  Every one of these seven opcodes lands in a subsystem the PoC has not built:
/// </para>
/// <list type="bullet">
///   <item><description><c>0xD5 KILL_ACTOR</c> / <c>0xD6 SPAWN_ACTOR</c> —
///   <c>slot_4x19_clear_for_owner @image@0x0B582</c> / <c>subsystem4x19_row_attach @image@0x0B467</c>,
///   i.e. the TRAIL-ROW pool, whose per-frame tick is one of the five effect ticks the host stubs.
///   </description></item>
///   <item><description><c>0xDE FIRE_WEAPON</c>'s three calls — the deferred-effect record
///   (<c>image@0x03A7C</c>, again an effect tick), the impact SFX (no audio yet) and
///   <c>subsystem5x06_per_frame_slot_fire @image@0x2DB69</c>.</description></item>
///   <item><description><c>0xDF PRINT_STRING</c> — the cockpit text line (no HUD text yet); the
///   string IS captured, so a host can show it later.</description></item>
///   <item><description><c>0xE0 CALL_SCRIPT_FUNCTION</c> — the <c>.S</c> module's
///   <c>on_secondary_event</c>, which is the WinRule evaluator.</description></item>
/// </list>
/// </remarks>
public sealed class SessionVmScriptEffects : IVmScriptEffects
{
    private readonly List<string> _messages = [];

    /// <summary>Actor kills the script asked for.</summary>
    public long ActorKills { get; private set; }

    /// <summary>Actor spawns.</summary>
    public long ActorSpawns { get; private set; }

    /// <summary>Deferred-effect records the script scheduled.</summary>
    public long DeferredEffects { get; private set; }

    /// <summary>Impact sounds.</summary>
    public long ImpactSounds { get; private set; }

    /// <summary>Slot fires.</summary>
    public long SlotFires { get; private set; }

    /// <summary><c>.S</c> module callbacks.</summary>
    public long ScriptFunctionCalls { get; private set; }

    /// <summary>The cockpit-text lines the mission's scripts printed, in order.</summary>
    public IReadOnlyList<string> Messages => _messages;

    /// <inheritdoc/>
    public void KillActor(ushort actorRecordRef) => ActorKills++;

    /// <inheritdoc/>
    public void SpawnActor(in VmSpawnActorRequest request) => ActorSpawns++;

    /// <inheritdoc/>
    public void ScheduleDeferredEffect(in VmDeferredEffectRequest request) => DeferredEffects++;

    /// <inheritdoc/>
    public void ImpactSound(ushort actorRecordRef) => ImpactSounds++;

    /// <inheritdoc/>
    public void SlotFire(int x, int z) => SlotFires++;

    /// <inheritdoc/>
    public void PrintString(string text)
    {
        if (_messages.Count < 64)
        {
            _messages.Add(text);
        }
    }

    /// <inheritdoc/>
    public void CallScriptFunction(ushort functionIndex) => ScriptFunctionCalls++;
}

/// <summary>
/// The readout: whether the guns are hitting anything.
/// </summary>
/// <param name="RoundsFired">Rounds spent out of the selected weapon slot.</param>
/// <param name="RoundsOnTarget">
/// That word is the player's damage taken <c>g_gun_rounds_hit [0xED36]</c>: the rounds the player's guns
/// hit.
/// </param>
/// <param name="Kills"><c>[0xF106]</c> — the kill tally.</param>
/// <param name="PlayerHullA"><c>[0xF1D8]</c>.</param>
/// <param name="PlayerHullB"><c>[0xF1D9]</c>.</param>
/// <param name="DamageApplications">K9 damage events the combat side drove into the flight master.</param>
/// <param name="ScoreShots"><c>[0xBD08]</c> — the hit-percentage denominator.</param>
/// <param name="ScoreHits"><c>[0xBD14]</c> — its numerator.</param>
/// <param name="EnemyHull">Every live engagement object's remaining hit points.</param>
public readonly record struct HitCensus(
    long RoundsFired,
    int RoundsOnTarget,
    int Kills,
    int PlayerHullA,
    int PlayerHullB,
    long DamageApplications,
    int ScoreShots,
    int ScoreHits,
    IReadOnlyList<int> EnemyHull);
