using CYAC.Port.Core.Sim.Combat.Geometry;
using CYAC.Port.Core.Sim.Combat.Lifecycle;
using CYAC.Port.Core.Sim.Combat.Player;
using CYAC.Port.Core.Sim.Combat.Vm;

namespace CYAC.Port.Core.Sim.Combat;

/// <summary>
/// The eleven stage boundaries of one combat frame — the trace's <c>CS0..CS10</c>, which are the
/// frame body's own trap addresses inside <c>mission_state_machine @image@0x008E6</c>.
/// </summary>
/// <remarks>
/// The enum exists so a verification can name a boundary without an integer, and so the driver's
/// ladder order is checkable against the oracle by construction: <see cref="CombatKernel.StepFrame"/>
/// raises every one of them, in this order, exactly once per frame.
/// </remarks>
public enum CombatStage
{
    /// <summary>CS0 <c>@image@0x00CC9</c> — dt is final and no combat of this frame has run.</summary>
    FramePre = 0,

    /// <summary>CS1 <c>@image@0x00CCC</c> — the projectile row returned.</summary>
    AfterProjectiles = 1,

    /// <summary>CS2 <c>@image@0x00CE5</c> — the five effect ticks returned.</summary>
    AfterEffects = 2,

    /// <summary>CS3 <c>@image@0x00D98</c> — advisor + input + the flight kernel returned.</summary>
    AfterFlight = 3,

    /// <summary>CS4 <c>@image@0x00E14</c> — the key ladder's LOOP HEAD, first arrival only.</summary>
    AfterPlayerFire = 4,

    /// <summary>CS5 <c>@image@0x01092</c> — the key ladder drained.</summary>
    BeforeEngagement = 5,

    /// <summary>CS6 <c>@image@0x01095</c> — <c>engagement_expiry_loop</c> returned.</summary>
    AfterEngagement = 6,

    /// <summary>CS7 <c>@image@0x0109A</c> — <c>engagement_per_frame_tick</c> returned.</summary>
    AfterPlayerTick = 7,

    /// <summary>CS8 <c>@image@0x0109F</c> — the admitter returned.</summary>
    AfterAdmission = 8,

    /// <summary>CS9 <c>@image@0x01708</c> — the render phase and the <c>.S</c> per-frame hook ran.</summary>
    AfterMissionHook = 9,

    /// <summary>CS10 <c>@image@0x017DF</c> — the HUD and the present ran; the frame is over.</summary>
    FrameEnd = 10,
}

/// <summary>
/// Which arm of <c>mission_state_machine</c>'s key ladder a delivered key belongs to.
/// </summary>
/// <remarks>
/// <para>
/// The ladder is a <c>dec ax / jne / jmp handler</c> chain of 80 handlers at
/// <c>image@0x00E27..0x01071</c> (decoded), and it is an EXTERNAL CHANNEL to this kernel in
/// exactly the sense the cockpit channel was: most of its arms belong to other subsystems
/// (the nav display, the view/camera selector, the ESC menu, the eject key).  The combat kernel
/// owns exactly TWO of them — the two dispensers — and the flight kernel owns the cockpit set
/// Everything else is named, counted and handed back.
/// </para>
/// </remarks>
public enum CombatLadderArm
{
    /// <summary>An arm no port kernel models; the channel dispatches it and it is COUNTED.</summary>
    External = 0,

    /// <summary><c>chaff_fire @image@0x0AF68</c> — the ladder's <c>lcall</c> at <c>image@0x01248</c>.</summary>
    Chaff = 1,

    /// <summary><c>flare_fire @image@0x0AFCC</c> — the ladder's <c>lcall</c> at <c>image@0x0125A</c>.</summary>
    Flare = 2,

    /// <summary>
    /// A cockpit key: <c>cockpit_key_dispatch_gated @image@0x2257C</c> (the ladder's fall-through
    /// arm at <c>image@0x0106C</c>).  It writes the AIRCRAFT MASTER, not combat state, so the combat
    /// kernel forwards it — in the joint run it reaches the <c>CockpitKeys</c>.
    /// </summary>
    Cockpit = 3,
}

/// <summary>One key the ladder dispatched this frame.</summary>
/// <param name="CookedKey">
/// The cooked key word <c>kbd_event_poll_and_classify @image@0x20488</c> produced —
/// <c>CYAC.Port.Core.Sim.CockpitKeyTranslator</c>'s <c>CookedKey.Word</c>.
/// </param>
/// <param name="Arm">Which ladder arm it reaches.</param>
public readonly record struct CombatLadderKey(int CookedKey, CombatLadderArm Arm);

/// <summary>
/// The combat kernel's EXTERNAL CHANNELS — everything inside the frame body that is NOT combat
/// state, named one by one so that "what still comes from outside" is a list and not a feeling.
/// </summary>
/// <remarks>
/// <para>
/// Three NAMED external channels.  A closed loop that reproduces a
/// recording must be handed exactly these and nothing else; a byte that moves outside them is an
/// UNMAPPED EXTERNAL WRITE and fails the verification.
/// </para>
/// <para>
/// The five members map one-for-one onto the frame body's non-combat spans:
/// <list type="bullet">
///   <item><description>
///     <see cref="EffectTicks"/> — CS1 → CS2, the five <c>lcall</c>s at
///     <c>image@0x00CCC..0x00CE0</c>: <c>deferred_effect_pool_fire_tick @0x03C22</c>,
///     <c>per_object_tick @0x2C66F</c>, <c>subsystem4x19_per_frame_advance @0x0B5B0</c>,
///     <c>countermeasure_cloud_per_frame_drift @0x0AD24</c>,
///     <c>smoke_per_frame_physics_step @0x0B20F</c>.
///   </description></item>
///   <item><description>
///     <see cref="FlightAndInput"/> — CS2 → CS3: <c>flight_advisor_dispatch @0x0F3AF</c> (Fx), the
///     joystick / numpad stick read (<c>image@0x00CEA..0x00D90</c>) and
///     <c>flight_engine_per_frame_top @0x227C7</c>, i.e. BATCH 5's kernel.  In the joint run this is
///     the flight kernel itself, not an oracle.
///   </description></item>
///   <item><description>
///     <see cref="KeyLadder"/> / <see cref="DispatchLadderKey"/> — CS4 → CS5.
///   </description></item>
///   <item><description>
///     <see cref="RenderPhaseRuns"/> / <see cref="RenderPhase"/> / <see cref="RenderListHead"/> —
///     the render half of CS8 → CS9: <c>subsystem4x04_per_frame_dispatch @0x0B6EA</c> (Fx) and
///     <c>polygon_fill_mesh_render_setup @0x146B8</c>, whose display list the ported lock-on walks.
///   </description></item>
///   <item><description>
///     <see cref="HudPhase"/> — CS9 → CS10: <c>hud_per_frame_draw @0x0C5A7</c> and the present.
///   </description></item>
/// </list>
/// </para>
/// </remarks>
public interface ICombatFrameChannels
{
    /// <summary>CS1 → CS2 — the five effect ticks.</summary>
    /// <param name="context">The kernel context.</param>
    void EffectTicks(CombatKernelContext context);

    /// <summary>CS2 → CS3 — the advisor, the stick read and the flight kernel.</summary>
    /// <param name="context">The kernel context.</param>
    void FlightAndInput(CombatKernelContext context);

    /// <summary>The keys the ladder will dispatch this frame, in delivery order.</summary>
    /// <param name="context">The kernel context.</param>
    IReadOnlyList<CombatLadderKey> KeyLadder(CombatKernelContext context);

    /// <summary>Dispatches a ladder arm this kernel does not own.</summary>
    /// <param name="context">The kernel context.</param>
    /// <param name="key">The key.</param>
    void DispatchLadderKey(CombatKernelContext context, CombatLadderKey key);

    /// <summary>
    /// Whether the FULL render arm runs this frame (<c>image@0x01570</c>
    /// <c>cmp [0xc320],0xc / jne 0x1598</c>; the short arm at <c>0x01577..0x01595</c> jumps straight
    /// to the <c>.S</c> hook and skips <c>subsystem4x04</c> AND the lock-on).
    /// </summary>
    /// <param name="context">The kernel context.</param>
    bool RenderPhaseRuns(CombatKernelContext context);

    /// <summary>The Fx half of the render phase — <c>subsystem4x04</c> and the mesh render.</summary>
    /// <param name="context">The kernel context.</param>
    void RenderPhase(CombatKernelContext context);

    /// <summary>
    /// The head of the render display list <c>target_acquisition_state_machine_step</c> walks —
    /// the argument <c>polygon_fill_mesh_render_setup</c> pushes at <c>image@0x14B87</c>, which is
    /// <c>g_engagement_object_list_head [0xE90A]</c> and therefore already in the register file.
    /// </summary>
    /// <param name="context">The kernel context.</param>
    ushort RenderListHead(CombatKernelContext context);

    /// <summary>CS9 → CS10 — the HUD draw (its hit % is <c>engagement_hit_pct_compute</c>).</summary>
    /// <param name="context">The kernel context.</param>
    void HudPhase(CombatKernelContext context);
}

/// <summary>
/// The channels that do NOTHING but count — the shape a shipping port uses before its renderer,
/// its effect pool and its input layer exist, and the negative control a verification runs against.
/// </summary>
public class CountingCombatFrameChannels : ICombatFrameChannels
{
    /// <summary>How many frames asked for the effect ticks.</summary>
    public int EffectTickFrames { get; private set; }

    /// <summary>How many frames asked for the flight step.</summary>
    public int FlightFrames { get; private set; }

    /// <summary>How many ladder keys were handed back as unmodelled.</summary>
    public int ExternalKeys { get; private set; }

    /// <summary>How many frames ran the FULL render arm.</summary>
    public int RenderFrames { get; private set; }

    /// <summary>How many frames ran the HUD phase.</summary>
    public int HudFrames { get; private set; }

    /// <inheritdoc/>
    public virtual void EffectTicks(CombatKernelContext context) => EffectTickFrames++;

    /// <inheritdoc/>
    public virtual void FlightAndInput(CombatKernelContext context) => FlightFrames++;

    /// <inheritdoc/>
    public virtual IReadOnlyList<CombatLadderKey> KeyLadder(CombatKernelContext context) => [];

    /// <inheritdoc/>
    public virtual void DispatchLadderKey(CombatKernelContext context, CombatLadderKey key) =>
        ExternalKeys++;

    /// <inheritdoc/>
    public virtual bool RenderPhaseRuns(CombatKernelContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // image@0x01570 — the SHORT arm is taken when [0xC320] == 0x0C.  When the byte is outside
        // this trace's windows the port takes the full arm, which is what every recorded frame does.
        CombatRegisters registers = context.Registers;
        return !registers.Covers(CombatKernel.RenderArmSelector, 1)
            || registers.Byte(CombatKernel.RenderArmSelector) != CombatKernel.RenderArmShortValue;
    }

    /// <inheritdoc/>
    public virtual void RenderPhase(CombatKernelContext context) => RenderFrames++;

    /// <inheritdoc/>
    public virtual ushort RenderListHead(CombatKernelContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Registers.Covers(CombatKernel.RenderListHeadWord, 2)
            ? context.Registers.Word(CombatKernel.RenderListHeadWord)
            : (ushort)0;
    }

    /// <inheritdoc/>
    public virtual void HudPhase(CombatKernelContext context) => HudFrames++;
}

/// <summary>A hook a verification installs to inspect the state at every stage boundary.</summary>
public interface ICombatStageObserver
{
    /// <summary>Called immediately after the driver reaches a stage's trap address.</summary>
    /// <param name="stage">Which boundary.</param>
    /// <param name="context">The kernel context, at that instant.</param>
    void Stage(CombatStage stage, CombatKernelContext context);
}

/// <summary>Which parts of one frame ran — instrumentation only, never a decision input.</summary>
public sealed class CombatKernelCensus
{
    /// <summary>Frames the driver ran.</summary>
    public long Frames { get; internal set; }

    /// <summary>Key-ladder ITERATIONS (scheduler calls) — the trace's CS5 <c>aux</c>.</summary>
    public long LadderIterations { get; internal set; }

    /// <summary>Chaff dispenser arms taken.</summary>
    public long ChaffKeys { get; internal set; }

    /// <summary>Flare dispenser arms taken.</summary>
    public long FlareKeys { get; internal set; }

    /// <summary>Cockpit keys forwarded to the flight side.</summary>
    public long CockpitKeys { get; internal set; }

    /// <summary>Ladder arms handed to the channel.</summary>
    public long ExternalKeys { get; internal set; }

    /// <summary>Frames that ran the FULL render arm.</summary>
    public long RenderFrames { get; internal set; }

    /// <summary>Frames whose <c>[0x00B8]</c> gate let the player LOCK-ON run.</summary>
    public long LockOnCalls { get; internal set; }

    /// <summary>
    /// Frames on which the port's own <see cref="LockOnGate"/> computed <c>[0x00B8] = 1</c>
    /// (<c>image@0x01664</c>).  Equal to <see cref="LockOnCalls"/> whenever the full render arm ran,
    /// and the number the machine's P14 activation count is compared against.
    /// </summary>
    public long LockOnGateOpen { get; internal set; }

    /// <summary>Frames whose expiry loop popped at least one node.</summary>
    public long EngagementFrames { get; internal set; }

    /// <summary>Chaff dispenses the ported <c>chaff_fire</c> actually made.</summary>
    public long ChaffFired { get; internal set; }

    /// <summary>Flare dispenses the ported <c>flare_fire</c> actually made.</summary>
    public long FlareFired { get; internal set; }
}

/// <summary>
/// Everything one combat frame needs: the shared state, the six sub-contexts the kernel's pieces
/// each own, and the external channels.
/// </summary>
/// <remarks>
/// <para>
/// The invariant that makes the assembly correct is that there is exactly ONE
/// <see cref="CombatRegisters"/> and ONE <see cref="PoolArena"/> in the whole frame, and every
/// sub-context holds that same pair — the original has one DGROUP and one arena, and every piece of
/// the kernel is verified against a state decoded from the same window.  <see cref="Create"/> is
/// the only sanctioned way to build the graph, precisely so that cannot be got wrong.
/// </para>
/// </remarks>
public sealed class CombatKernelContext
{
    private CombatKernelContext(
        CombatRegisters registers,
        PoolArena arena,
        ICombatStaticData staticData,
        IEngagementPrototypes prototypes,
        ICombatRandom random,
        ProjectileKernelContext projectiles,
        EngagementGeometryContext geometry,
        EngagementNodeContext node,
        EngagementLifecycleContext lifecycle,
        PlayerCombatContext player,
        ICombatFrameChannels channels,
        ICombatEvents combatEvents)
    {
        Registers = registers;
        Arena = arena;
        StaticData = staticData;
        Prototypes = prototypes;
        Random = random;
        Projectiles = projectiles;
        Geometry = geometry;
        Node = node;
        Lifecycle = lifecycle;
        Player = player;
        Channels = channels;
        CombatEvents = combatEvents;
    }

    /// <summary>The one DGROUP combat register file.</summary>
    public CombatRegisters Registers { get; }

    /// <summary>The one pool arena.</summary>
    public PoolArena Arena { get; }

    /// <summary>The constant DGROUP tables (weapon classes, prototypes, tuning).</summary>
    public ICombatStaticData StaticData { get; }

    /// <summary>The engagement class prototypes.</summary>
    public IEngagementPrototypes Prototypes { get; }

    /// <summary>The Sim LFSR — by law it advances <c>[0x07A8]</c> inside <see cref="Registers"/>.</summary>
    public ICombatRandom Random { get; }

    /// <summary>C2's context — the projectile row (CS0 → CS1).</summary>
    public ProjectileKernelContext Projectiles { get; }

    /// <summary>C3a's context — the manoeuvring geometry.</summary>
    public EngagementGeometryContext Geometry { get; }

    /// <summary>C3b's context — the per-node FSM.</summary>
    public EngagementNodeContext Node { get; }

    /// <summary>C5's context — admission, lifecycle and the <c>.S</c> module.</summary>
    public EngagementLifecycleContext Lifecycle { get; }

    /// <summary>C6's context — the player side.</summary>
    public PlayerCombatContext Player { get; }

    /// <summary>The external channels.</summary>
    public ICombatFrameChannels Channels { get; }

    /// <summary>The expiry loop's outbound notifications (C1's seam).</summary>
    public ICombatEvents CombatEvents { get; }

    /// <summary>The per-node FSM the expiry loop runs — C3b's real body.</summary>
    public IEngagementNodePass NodePass { get; private init; } = null!;

    /// <summary>This run's arm census.</summary>
    public CombatKernelCensus Census { get; } = new();

    /// <summary>
    /// Builds the whole context graph over one state, wiring every seam that has a body.
    /// </summary>
    /// <param name="registers">The DGROUP combat register file (mutated in place).</param>
    /// <param name="arena">The pool arena (mutated in place).</param>
    /// <param name="staticData">The constant DGROUP surface.</param>
    /// <param name="prototypes">The engagement class prototypes.</param>
    /// <param name="channels">The external channels.</param>
    /// <param name="acquisition">The world-grid query (C2b) — the projectile row's target acquisition.</param>
    /// <param name="scriptHeap">The far heap the AI programs live in (C4).</param>
    /// <param name="damage">The K9 flight-kernel damage channel (C6's master-write bridge).</param>
    /// <param name="module">The <c>.S</c> mission module (C5's WinRule-IR seam).</param>
    /// <param name="terrain">C3a's terrain-proximity seam — C2b's 2-D proximity scorer.</param>
    /// <param name="playerEvents">The player side's out-calls (renderer / film / sound / text).</param>
    /// <param name="projectileEvents">The projectile row's out-calls.</param>
    /// <param name="nodeEvents">The per-node FSM's out-calls.</param>
    /// <param name="lifecycleEvents">The lifecycle's out-calls.</param>
    /// <param name="vmEffects">The VM's opcode-driven effect calls.</param>
    /// <param name="combatEvents">The expiry loop's out-calls.</param>
    /// <param name="vmSeam">
    /// Overrides C4's ported <c>engagement_slot_fsm_advance</c> with an oracle.  A port seeded in
    /// the MIDDLE of a mission has no far script heap — the AI programs live in a segment
    /// <c>far_heap_alloc</c> handed the mission at load — so a verification that starts from a
    /// stage record keeps the VM oracle-fed exactly as C1's and C3b's did.  Null = the ported VM.
    /// </param>
    /// <param name="interpreterSeam">The same override for the interpreter itself.  Null = ported.</param>
    /// <returns>The wired context.</returns>
    public static CombatKernelContext Create(
        CombatRegisters registers,
        PoolArena arena,
        ICombatStaticData staticData,
        IEngagementPrototypes prototypes,
        ICombatFrameChannels channels,
        ITargetAcquisition acquisition,
        IAiScriptHeap scriptHeap,
        IAircraftDamageChannel damage,
        IMissionModule? module = null,
        ITerrainProximity? terrain = null,
        IPlayerCombatEvents? playerEvents = null,
        IProjectileEvents? projectileEvents = null,
        IEngagementNodeEvents? nodeEvents = null,
        ILifecycleEvents? lifecycleEvents = null,
        IVmScriptEffects? vmEffects = null,
        ICombatEvents? combatEvents = null,
        IEngagementVm? vmSeam = null,
        IEngagementScriptInterpreter? interpreterSeam = null)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(staticData);
        ArgumentNullException.ThrowIfNull(prototypes);
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(acquisition);
        ArgumentNullException.ThrowIfNull(scriptHeap);
        ArgumentNullException.ThrowIfNull(damage);

        // ONE random source for the whole frame.  The det build keeps every Sim.* combat site on
        // the single LFSR16 word [0x07A8], and a verification proves the DRAW COUNT AND ORDER by
        // comparing that word at every stage — so the driver must not hand different pieces
        // different generators.
        RegisterFileCombatRandom random = new RegisterFileCombatRandom(registers);

        EngagementGeometryContext geometry = new EngagementGeometryContext
        {
            Registers = registers,
            Arena = arena,
            StaticData = staticData,
            Terrain = terrain ?? UnavailableTerrainProximity.Instance,
        };

        PortedPlayerCombatEvents? portedPlayerEvents = playerEvents is null
            ? new PortedPlayerCombatEvents(arena) { Geometry = geometry, Registers = registers }
            : null;
        PlayerCombatContext player = new PlayerCombatContext
        {
            Registers = registers,
            Arena = arena,
            StaticData = staticData,
            Random = random,
            Damage = damage,
            Events = playerEvents ?? portedPlayerEvents!,
        };

        EngagementLifecycleContext lifecycle = new EngagementLifecycleContext
        {
            Geometry = geometry,
            Random = random,
            Prototypes = prototypes,
            Module = module ?? NoMissionModule.Instance,
            Events = lifecycleEvents ?? new PortedLifecycleEvents(arena),
        };

        EngagementVmContext vm = new EngagementVmContext
        {
            Geometry = geometry,
            Heap = scriptHeap,
            Random = random,
            Prototypes = prototypes,
            Effects = vmEffects ?? UnavailableVmScriptEffects.Instance,
        };

        EngagementNodeContext node = new EngagementNodeContext
        {
            Geometry = geometry,
            Random = random,
            Interpreter = interpreterSeam ?? new EngagementVmAdapter(vm),
            Acquisition = EnemyTargetAcquisition.Seam,
            TargetSelection = new PortedTargetSelection(player),
            WorldGrid = acquisition,
            SpawnAllocator = new PortedSpawnSlotAllocator(player),
            ObjectSlots = new PortedObjectSlotPool(lifecycle),
            MissionHook = new PortedNodeMissionHook(lifecycle),
            Events = nodeEvents ?? NullEngagementNodeEvents.Instance,
        };

        ProjectileKernelContext projectiles = new ProjectileKernelContext
        {
            Registers = registers,
            Arena = arena,
            StaticData = staticData,
            Random = random,
            Acquisition = acquisition,
            Vm = vmSeam ?? new PortedEngagementVm(vm),
            PlayerDamage = new PortedPlayerDamage(player),
            Lifecycle = new PortedEngagementLifecycle(lifecycle),
            Events = projectileEvents ?? NullProjectileEvents.Instance,
        };

        CombatKernelContext context = new CombatKernelContext(
            registers,
            arena,
            staticData,
            prototypes,
            random,
            projectiles,
            geometry,
            node,
            lifecycle,
            player,
            channels,
            combatEvents ?? NullCombatEvents.Instance)
        {
            NodePass = new EngagementNodePass(node),
        };
        return context;
    }
}

/// <summary>
/// The assembled per-frame COMBAT DRIVER — the gameplay body of <c>mission_state_machine @image@0x008E6</c>, in
/// the original's own order, over the twelve pieces verified stage by stage.
/// </summary>
/// <remarks>
/// <para>
/// <b>The ladder</b> (parent's byte census, re-derived from the emulator instrument's own trap map):
/// </para>
/// <list type="table">
///   <item>
///     <term>CS0 → CS1</term>
///     <description><see cref="CombatSpawnDriver.Step"/> — every active spawn slot, walked
///     BACKWARDS from slot 29 (<c>image@0x026A0</c>), through <c>combat_object_tick</c>'s five
///     phases, the fire authority, the damage resolver and the departure (C2).</description>
///   </item>
///   <item>
///     <term>CS1 → CS2</term>
///     <description>the five effect ticks — an EXTERNAL CHANNEL
///     (<see cref="ICombatFrameChannels.EffectTicks"/>); they are not combat state.</description>
///   </item>
///   <item>
///     <term>CS2 → CS3</term>
///     <description>advisor + stick + the FLIGHT kernel — an EXTERNAL CHANNEL, and in the joint run
///     the <c>FlightKernel</c> itself.</description>
///   </item>
///   <item>
///     <term>CS3 → CS4</term>
///     <description><see cref="WeaponFireScheduler.FireStage"/> — the ground-shadow pose copy, the
///     derived key/trigger gate <c>[0xC31C]</c>, and the first scheduler call (C6).</description>
///   </item>
///   <item>
///     <term>CS4 → CS5</term>
///     <description>the KEY LADDER loop: per delivered key, the arm, then the scheduler again
///     (<c>image@0x00E08</c> is the loop top).  The two dispenser arms are ported; the rest is the
///     channel's.</description>
///   </item>
///   <item>
///     <term>CS5 → CS6</term>
///     <description><see cref="EngagementExpiryLoop.Step"/> running C3b's real per-node FSM, which
///     runs C3a's geometry, C4's bytecode VM and C6's selection cluster.</description>
///   </item>
///   <item>
///     <term>CS6 → CS7</term>
///     <description><see cref="PlayerSustainTick.Step"/> — fuel leak, damage meters, deadlines (C6),
///     whose master writes go out through the K9 damage channel.</description>
///   </item>
///   <item>
///     <term>CS7 → CS8</term>
///     <description><see cref="EngagementAdmission.Step"/> — the difficulty-throttled admitter (C5).</description>
///   </item>
///   <item>
///     <term>CS8 → CS9</term>
///     <description>the render phase: <c>subsystem4x04</c> and the mesh render are the channel's
///     (Fx), <see cref="PlayerTargetLock.Step"/> is the ported player LOCK-ON, and
///     <see cref="MissionModuleDispatch.PerFrameHook"/> is the <c>.S</c> module's per-frame vtable
///     hook.</description>
///   </item>
///   <item>
///     <term>CS9 → CS10</term>
///     <description>the HUD draw — the channel's; its hit % is
///     <c>engagement_hit_pct_compute @image@0x031CB</c>.</description>
///   </item>
/// </list>
/// <para>
/// <b>What remains a seam, and whose it is.</b>  Five: the five effect ticks and the render/HUD
/// halves (presentation and other subsystems — the channel's, by design); the flight step (the
/// kernel — bound in the joint run); the <c>.S</c> mission module's native code
/// (<see cref="IMissionModule"/> — the WinRule-IR evaluator);
/// <see cref="IAircraftDamageChannel"/> (the <c>AircraftDamage</c>, CONSUMED not re-ported);
/// and <see cref="ITargetAcquisition"/> when a caller has no grid arenas to hand (C2b's
/// <c>WorldGridQuery</c> is the real body and is verified per call).
/// </para>
/// </remarks>
public static class CombatKernel
{
    /// <summary>
    /// <c>[0xC320]</c> — the byte the render arm is selected on (<c>image@0x01570</c>).
    /// </summary>
    public const int RenderArmSelector = 0xC320;

    /// <summary>The value that takes the SHORT render arm (<c>cmp [0xc320],0xc / jne 0x1598</c>).</summary>
    public const byte RenderArmShortValue = 0x0C;

    /// <summary>
    /// <c>g_in_flight_key_gate [0xC31C]</c> — the derived latch the key ladder's loop top tests
    /// before every scheduler call (<c>image@0x00E08</c>).
    /// </summary>
    public const int InFlightKeyGate = 0xC31C;

    /// <summary>
    /// <c>[0x00B8]</c> — the byte the LOCK-ON's own call site is gated on
    /// (<c>cmp byte ptr [0xb8],0 / je 0x14b93</c> @<c>image@0x14B80</c>, inside
    /// <c>polygon_fill_mesh_render_setup</c>).
    /// </summary>
    public const int LockOnEnabled = 0x00B8;

    /// <summary>
    /// <c>g_engagement_object_list_head [0xE90A]</c> — the ONE argument that call site pushes
    /// (<c>push word ptr [0xe90a]</c> @<c>image@0x14B87</c>), i.e. the render display list the
    /// lock-on walks.
    /// </summary>
    /// <remarks>
    /// An earlier pass found it in the register file rather than in the renderer: the head is a DGROUP word the
    /// combat trace already carries, so the ported lock-on runs on the real list and is compared
    /// like everything else, instead of walking an empty one behind a seam.
    /// </remarks>
    public const int RenderListHeadWord = 0xE90A;

    /// <summary>Runs ONE combat frame — the whole gameplay body, CS0 through CS10.</summary>
    /// <param name="context">The wired kernel context; every piece of state is mutated in place.</param>
    /// <param name="observer">A per-stage hook, or null.</param>
    /// <returns>The frame's census.</returns>
    public static CombatKernelCensus StepFrame(
        CombatKernelContext context, ICombatStageObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters registers = context.Registers;
        CombatKernelCensus census = context.Census;
        ICombatFrameChannels channels = context.Channels;

        observer?.Stage(CombatStage.FramePre, context);

        // ── CS0 → CS1 : the PROJECTILE row (image@0x00CC9) ───────────────────────────────────
        CombatSpawnDriver.Step(context.Projectiles);
        observer?.Stage(CombatStage.AfterProjectiles, context);

        // ── CS1 → CS2 : the five effect ticks (image@0x00CCC..0x00CE0) — EXTERNAL ────────────
        channels.EffectTicks(context);
        observer?.Stage(CombatStage.AfterEffects, context);

        // ── CS2 → CS3 : advisor + stick + flight_engine_per_frame_top — EXTERNAL / JOINT ─────
        channels.FlightAndInput(context);
        observer?.Stage(CombatStage.AfterFlight, context);

        // ── CS3 → CS4 : the fire stage (image@0x00D98..0x00E13) ──────────────────────────────
        WeaponFireScheduler.FireStage(context.Player);
        census.LadderIterations++;                              // the loop top's first arrival
        observer?.Stage(CombatStage.AfterPlayerFire, context);

        // ── CS4 → CS5 : the key ladder LOOP (image@0x00E08 .. image@0x01071) ─────────────────
        // Per iteration: pop one key, run its arm, jump back to the loop top and call the
        // scheduler again (gated on [0xC31C]).  Iterations = keys + 1, which is the count the
        // trace carries in CS5's `aux`.
        foreach (CombatLadderKey key in channels.KeyLadder(context))
        {
            switch (key.Arm)
            {
                case CombatLadderArm.Chaff:                     // image@0x01248
                    census.ChaffKeys++;
                    if (Countermeasures.FireChaff(context.Player))
                    {
                        census.ChaffFired++;
                    }

                    break;

                case CombatLadderArm.Flare:                     // image@0x0125A
                    census.FlareKeys++;
                    if (Countermeasures.FireFlare(context.Player))
                    {
                        census.FlareFired++;
                    }

                    break;

                case CombatLadderArm.Cockpit:                   // image@0x0106C, the K10
                    census.CockpitKeys++;
                    channels.DispatchLadderKey(context, key);
                    break;

                default:
                    census.ExternalKeys++;
                    channels.DispatchLadderKey(context, key);
                    break;
            }

            census.LadderIterations++;
            if (registers.Byte(InFlightKeyGate) != 0)           // image@0x00E08
            {
                WeaponFireScheduler.Step(context.Player);
            }
        }

        observer?.Stage(CombatStage.BeforeEngagement, context);

        // ── CS5 → CS6 : the ENGAGEMENT pass (image@0x01092) ──────────────────────────────────
        EngagementExpiryCensus engagement = EngagementExpiryLoop.Step(
            context.Arena,
            registers,
            context.Prototypes,
            context.NodePass,
            context.CombatEvents,
            context.Random);
        if (engagement.NodesPopped > 0)
        {
            census.EngagementFrames++;
        }

        observer?.Stage(CombatStage.AfterEngagement, context);

        // ── CS6 → CS7 : the player SUSTAIN tick (image@0x01095) ──────────────────────────────
        PlayerSustainTick.Step(context.Player);
        observer?.Stage(CombatStage.AfterPlayerTick, context);

        // ── CS7 → CS8 : the ADMITTER (image@0x0109A) ─────────────────────────────────────────
        EngagementAdmission.Step(context.Lifecycle);
        observer?.Stage(CombatStage.AfterAdmission, context);

        // ── CS8 → CS9 : the render phase and the .S per-frame hook ───────────────────────────
        if (channels.RenderPhaseRuns(context))
        {
            census.RenderFrames++;

            // image@0x01656..0x0168C: the two gate bytes are COMPUTED here, inside the full render
            // arm, and cleared again at image@0x016E8 before CS9's trap at image@0x01708. They are
            // within-frame transients: every stage record reads them 0, which is why a CS8-seeded
            // driver used to run the lock-on zero times.
            //
            // Order note: the machine computes them AFTER subsystem4x04_per_frame_dispatch
            // (lcall @image@0x015C5) and BEFORE the mesh render (lcall @image@0x016D9); this port
            // bundles both into channels.RenderPhase, and the gate is computed first because
            // subsystem4x04 writes none of the three inputs — [0xE46F]'s only writer image-wide is
            // publish_view_mode @image@0x23875, [0xC31C]'s two are at image@0x00DE2..0x00DFE, and
            // [0x00B1] moves only on the Ctrl-T key arm.
            //
            // The gate's first term is taken THROUGH the republish guard
            // `view_mode_radar_track_guard @image@0x238BE` — mission_state_machine's very first
            // call after CS8's trap, whose three arms can re-publish the view mode and so move
            // [0xE46F] inside the span the port seeds from.  Measured: it fires on ONE of the
            // 9,006 windowed frames, and it is exactly the frame that has to be declared.
            bool lockOnEnabled = LockOnGate.Compute(registers, context.StaticData);
            if (lockOnEnabled)
            {
                census.LockOnGateOpen++;
            }

            channels.RenderPhase(context);                      // subsystem4x04 + the mesh render
            if (registers.Byte(LockOnEnabled) != 0)             // image@0x14B80
            {
                census.LockOnCalls++;
                PlayerTargetLock.Step(context.Player, channels.RenderListHead(context));
            }

            LockOnGate.ClearAfterRender(registers);             // image@0x016E8..0x016ED
        }

        MissionModuleDispatch.PerFrameHook(context.Lifecycle);  // image@0x01703
        observer?.Stage(CombatStage.AfterMissionHook, context);

        // ── CS9 → CS10 : the HUD draw and the present (image@0x01708..0x017DF) ───────────────
        channels.HudPhase(context);
        census.Frames++;
        observer?.Stage(CombatStage.FrameEnd, context);

        return census;
    }
}
