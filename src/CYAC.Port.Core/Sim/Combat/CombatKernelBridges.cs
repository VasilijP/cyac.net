using CYAC.Port.Core.Sim.Combat.Lifecycle;
using CYAC.Port.Core.Sim.Combat.Player;
using CYAC.Port.Core.Sim.Combat.Vm;

namespace CYAC.Port.Core.Sim.Combat;

/// <summary>
/// The BRIDGES: every seam the kernel's pieces declare, filled with their own verified
/// body rather than an oracle.
/// </summary>
/// <remarks>
/// <para>
/// Each builder verified its piece with the neighbouring pieces held as SEAMS, fed from the
/// recording — that is what makes a per-stage verification a proof rather than a coincidence.  The
/// assembled kernel is the moment those seams get real bodies, and the bridges below are exactly
/// that substitution, one type per seam, each naming the two sides.
/// </para>
/// <para>
/// They live in <c>CYAC.Port.Core</c> and not in the test project on purpose: a shipping port needs
/// the wired graph, and the verification then proves the SHIPPING configuration rather than a
/// test-only one (the <c>FlightKernel</c> made the same choice for <c>IVelocityDynamics</c>).
/// Every bridge is a pure delegation — no logic lives here.
/// </para>
/// </remarks>
internal static class CombatKernelBridgeNotes
{
    /// <summary>The seam-to-body table, for a reader here.</summary>
    /// <remarks>
    /// <list type="table">
    ///   <item><term><c>IEngagementVm</c> (C2)</term><description>C4's <c>EngagementVm</c>.</description></item>
    ///   <item><term><c>IPlayerDamage</c> (C2)</term><description>C6's <c>PlayerDamageRoulette</c>.</description></item>
    ///   <item><term><c>IEngagementLifecycle</c> (C2)</term><description>C5's coalition spawn, mission hook and destruction pool.</description></item>
    ///   <item><term><c>ITargetAcquisition</c> (C2)</term><description>C2b's <c>WorldGridQuery</c> (the caller supplies it: it needs the grid arenas).</description></item>
    ///   <item><term><c>IEngagementScriptInterpreter</c> (C3b)</term><description>C4's <c>EngagementVmAdapter</c>.</description></item>
    ///   <item><term><c>ITargetSelection</c> (C3b)</term><description>C6's <c>TargetSelectionCluster</c>.</description></item>
    ///   <item><term><c>ISpawnSlotAllocator</c> (C3b)</term><description>C6's <c>CombatSpawnSlotAllocator</c>.</description></item>
    ///   <item><term><c>IObjectSlotPool</c> (C3b)</term><description>C5's <c>ObjectSlotPool</c>.</description></item>
    ///   <item><term><c>IEngagementNodeMissionHook</c> (C3b)</term><description>C5's <c>MissionModuleDispatch</c>.</description></item>
    ///   <item><term><c>IEngagementNodePass</c> (C1)</term><description>C3b's <c>EngagementNodePass</c>.</description></item>
    ///   <item><term><c>IAircraftDamageChannel</c> (C6)</term><description>the <c>Sim/Flight/AircraftDamage</c> — the JOINT run binds it.</description></item>
    ///   <item><term><c>IMissionModule</c> (C5)</term><description>still a seam: the <c>.S</c> module is native x86 outside the image.</description></item>
    /// </list>
    /// </remarks>
    public const string SeamTable = "see the remarks";
}

/// <summary>
/// C4's bytecode VM behind C2's <see cref="IEngagementVm"/> seam.
/// </summary>
/// <param name="context">The VM context, sharing the frame's register file and arena.</param>
public sealed class PortedEngagementVm(EngagementVmContext context) : IEngagementVm
{
    private readonly EngagementVmContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    /// <summary>The VM context the kernel runs the interpreter in.</summary>
    public EngagementVmContext Context => _context;

    /// <inheritdoc/>
    public void Advance(CombatRegisters registers, PoolArena arena, ushort blockRef, byte mode) =>
        EngagementVmStep.Advance(_context, blockRef, mode);
}

/// <summary>
/// C6's player-damage roulette behind C2's <see cref="IPlayerDamage"/> seam.
/// </summary>
/// <param name="context">The player-side context, sharing the frame's register file and arena.</param>
public sealed class PortedPlayerDamage(PlayerCombatContext context) : IPlayerDamage
{
    private readonly PlayerCombatContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc/>
    public void Apply(CombatRegisters registers, short damage) =>
        PlayerDamageRoulette.Apply(_context, damage);
}

/// <summary>
/// C5's lifecycle behind C2's <see cref="IEngagementLifecycle"/> seam — the three calls the damage
/// resolver makes.
/// </summary>
/// <param name="context">The lifecycle context, sharing the frame's register file and arena.</param>
public sealed class PortedEngagementLifecycle(EngagementLifecycleContext context)
    : IEngagementLifecycle
{
    private readonly EngagementLifecycleContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc/>
    public void PlayerContactCoalitionSpawn(
        CombatRegisters registers, PoolArena arena, ushort victimRef, ushort attackerId) =>
        EngagementCoalitionSpawn.Run(_context, victimRef, attackerId);

    /// <inheritdoc/>
    public void OnSlotDestroyed(CombatRegisters registers, PoolArena arena, ushort victimRef) =>
        MissionModuleDispatch.OnSlotDestroyed(_context, victimRef);

    /// <inheritdoc/>
    public bool TryDestructionSlotShortCircuit(ushort victimRef, byte guidedFlag)
    {
        // image@0x0BEE0 / 0x0BEF5 — the decoy short circuit: find the destruction-pool slot whose
        // ext pointer A is the struck object, and if there is one, deactivate it and deal no damage.
        _context.Census.SlotLookups++;
        ushort slot = ObjectSlotPool.FindByExtPointerA(_context.Registers, victimRef);
        if (slot == 0)
        {
            return false;
        }

        _context.Census.SlotLookupHits++;
        ObjectSlotPool.Deactivate(_context, slot, guidedFlag);
        return true;
    }
}

/// <summary>
/// C6's target-selection cluster behind C3b's <see cref="ITargetSelection"/> seam.
/// </summary>
/// <param name="context">The player-side context, sharing the frame's register file and arena.</param>
public sealed class PortedTargetSelection(PlayerCombatContext context) : ITargetSelection
{
    private readonly PlayerCombatContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc/>
    public ushort ScoreAndFire(
        EngagementNodeContext context, bool preferExisting, bool fireEnabled, out short cooldown) =>
        TargetSelectionCluster.ScoreAndFire(_context, preferExisting, fireEnabled, out cooldown);

    /// <inheritdoc/>
    public bool QualifyFromGlobals(EngagementNodeContext context, byte mode) =>
        TargetSelectionCluster.QualifyFromGlobals(_context, mode);

    /// <inheritdoc/>
    public bool SightLine(
        EngagementNodeContext context, ushort targetRef, ushort weaponBlockRef, ushort ownerSlot) =>
        TargetSelectionCluster.SightLine(_context, targetRef, weaponBlockRef, ownerSlot);

    /// <inheritdoc/>
    public void GuidanceAngles(
        EngagementNodeContext context, out short heading, out short elevation) =>
        TargetSelectionCluster.GuidanceAngles(_context, out heading, out elevation);

    /// <inheritdoc/>
    public void FillSpawnRecord(
        EngagementNodeContext context,
        ref CombatPosition parameters,
        ushort leadSource,
        ushort targetRef,
        ushort weaponClassRef,
        bool invertLead) =>
        parameters = WeaponFireScheduler.FillSpawnRecord(
            _context, targetRef, weaponClassRef, leadSource, invertLead ? (sbyte)1 : (sbyte)0);
}

/// <summary>
/// C6's spawn-slot allocator behind C3b's <see cref="ISpawnSlotAllocator"/> seam — the AI door of
/// <c>combat_spawn_slot_alloc_and_film_record @image@0x02423</c> (<c>image@0x083CB</c>).
/// </summary>
/// <param name="context">The player-side context, sharing the frame's register file and arena.</param>
public sealed class PortedSpawnSlotAllocator(PlayerCombatContext context) : ISpawnSlotAllocator
{
    private readonly PlayerCombatContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc/>
    public bool Allocate(EngagementNodeContext context, in AiShotRequest request) =>
        CombatSpawnSlotAllocator.Allocate(_context, new SpawnSlotRequest(
            WeaponClassRef: request.WeaponClassRef,
            OwnerObject: request.OwnerSlot,
            Parameters: request.Parameters,
            FireFlag: request.FireFlag,
            TargetRef: request.TargetRef,
            Elevation: request.Elevation,
            Heading: request.Heading,
            LauncherSpeedQ8: request.SpeedQ8));
}

/// <summary>
/// C5's destruction/debris pool behind C3b's <see cref="IObjectSlotPool"/> seam.
/// </summary>
/// <param name="context">The lifecycle context, sharing the frame's register file and arena.</param>
/// <remarks>
/// The argument mapping is the caller's own push order, byte-verified:
/// <c>enemy_spawn_with_angle_pos_init</c> pushes <c>[0xED56]</c> (the owner), the <c>i32</c> speed
/// <c>[0xED79:0xED7B]</c>, a literal 0, the prototype's <c>+0x0C &amp; 0x40</c> selector and the
/// <c>prng_rand8 &gt;= 0x14</c> variant, in that order (<c>image@0x08ACE..0x08AF8</c>); a far cdecl
/// callee reads them back as <c>[bp+6]</c>… — so the LAST push is <c>slot_alloc_and_activate</c>'s
/// FIRST parameter, the <c>+0x03</c> stage flag (<c>mov cl,[bp+6] / mov [si+3],cl</c>
/// <c>@image@0x2C555</c>), and <c>[0xED56]</c> is the sixth, the parent object.
/// </remarks>
public sealed class PortedObjectSlotPool(EngagementLifecycleContext context) : IObjectSlotPool
{
    private readonly EngagementLifecycleContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc/>
    public void AllocateAndActivate(
        EngagementNodeContext context,
        ushort ownerSlot,
        int arcAccumulator,
        byte prototypeSelector,
        bool variant) =>
        ObjectSlotPool.Allocate(
            _context,
            stageFlag: variant ? (byte)1 : (byte)0,
            kindFlag: prototypeSelector,
            playerOwned: 0,
            seed: arcAccumulator,
            parentRef: ownerSlot);
}

/// <summary>
/// C5's <c>.S</c>-module dispatcher behind C3b's <see cref="IEngagementNodeMissionHook"/> seam
/// (<c>combat_vtable_slot_fn2_dispatch @image@0x08C72</c>, the kill tally's door
/// <c>image@0x0874E</c>).
/// </summary>
/// <param name="context">The lifecycle context, sharing the frame's register file and arena.</param>
public sealed class PortedNodeMissionHook(EngagementLifecycleContext context)
    : IEngagementNodeMissionHook
{
    private readonly EngagementLifecycleContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc/>
    public void OnSlotDestroyed(CombatRegisters registers, ushort slotReference) =>
        MissionModuleDispatch.OnSlotDestroyed(_context, slotReference);
}
