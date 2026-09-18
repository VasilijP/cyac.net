namespace CYAC.Port.Core.Sim.Combat.Vm;

/// <summary>
/// <c>engagement_slot_fsm_advance @image@0x04F64</c> (0x6E = 110 B, FAR) — the wrapper that runs the
/// bytecode VM for ONE engagement block: guard, snapshot, one VM mode, restore, then either re-arm
/// the block's expiry deadline and re-sort it or retire the linked slot.
/// </summary>
/// <remarks>
/// <para>
/// This is the implementation of C2's <see cref="IEngagementVm"/> seam.  Its four LCALL doors are
/// <c>image@0x0261B</c> (<c>AL = 7</c>, the spawn/film allocator), <c>image@0x0A24F</c>
/// (<c>AL = 0</c>, <c>wld_or_s_asset_parser</c> booting a freshly-parsed tag-0x89 script),
/// <c>image@0x0BF14</c> (<c>AL = 4</c>) and <c>image@0x0C05C</c> (<c>AL = 5</c>, both the fire
/// handler).  <b>All four execute <c>sub dx,dx</c> immediately before the call</b>, so
/// the <c>DX</c> argument is DEAD in the shipping build and the re-armed deadline is always exactly
/// <c>[0xF0C8]</c>.  It is still modelled, and defaults to 0.
/// </para>
/// <para>
/// The guard is <b>per-CLASS</b>, not per-slot: <c>test byte ptr [bx+0xc],8</c>
/// @<c>image@0x04F76</c> carries NO <c>0x26</c> prefix, so it is a DGROUP read through
/// <c>pool_slot[+0] = class_proto_nearptr</c>.
/// </para>
/// <para>
/// The snapshot can silently RE-ARM the VM before the interpreter runs: its SHORT path
/// (<c>pool_slot[+5] &amp; 0x10</c>) forces <c>[0xED74] = 0</c> and <c>[0xED76] = -1</c>
/// which is why a mode that only inspects the PC can behave
/// differently for a compact block.
/// </para>
/// </remarks>
public sealed class EngagementVmStep : IEngagementVm
{
    private readonly Func<CombatRegisters, PoolArena, EngagementVmContext> _context;

    /// <summary>Creates the step over a context factory.</summary>
    /// <param name="contextFactory">
    /// Builds the VM context for a given register file and arena.  A factory rather than a fixed
    /// context because C2's seam signature hands both in per call.
    /// </param>
    public EngagementVmStep(Func<CombatRegisters, PoolArena, EngagementVmContext> contextFactory) =>
        _context = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));

    /// <summary>The frame offset the original takes in <c>DX</c> — dead in the shipping build.</summary>
    public short FrameOffset { get; init; }

    /// <inheritdoc/>
    public void Advance(CombatRegisters registers, PoolArena arena, ushort blockRef, byte mode) =>
        Advance(_context(registers, arena), blockRef, mode);

    /// <summary>Runs one step over an explicit context.</summary>
    /// <param name="context">The VM context.</param>
    /// <param name="blockRef">The engagement block's pool near offset — the original's <c>BX</c>.</param>
    /// <param name="mode">The mode byte — the original's <c>AL</c>.</param>
    /// <param name="frameOffset">The original's <c>DX</c>; 0 at every shipping call site.</param>
    /// <returns>Which of the two tail arms ran.</returns>
    public static EngagementVmStepOutcome Advance(
        EngagementVmContext context, ushort blockRef, byte mode, short frameOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(context);

        CombatRegisters registers = context.Registers;
        PoolArena arena = context.Arena;

        ushort prototype = arena.Word(blockRef);                            // image@0x04F73
        if ((context.StaticData.Byte(prototype + 0x0C) & 8) == 0)           // image@0x04F76
        {
            return EngagementVmStepOutcome.ClassNotVmCapable;
        }

        EngagementStateCopy.Snapshot(arena, registers, blockRef, context.Prototypes);   // image@0x04F7E
        EngagementVm.Run(context, mode);                                    // image@0x04F84
        EngagementStateCopy.Restore(arena, registers, blockRef);            // image@0x04F89

        ushort linked = arena.Word((ushort)(blockRef + 2));                 // image@0x04F9C
        if ((arena.Byte((ushort)(linked + 2)) & 1) == 0)                    // image@0x04FA0
        {
            // image@0x04FC2 — the linked slot is dead: retire it.
            EngagementExpiryLoop.TargetDeparture(
                arena, registers, context.Prototypes, context.Events, linked);
            return EngagementVmStepOutcome.Departed;
        }

        arena.SetWord(                                                      // image@0x04FAE
            (ushort)(blockRef + 0x0B),
            unchecked((ushort)(frameOffset + registers.Word(0xF0C8))));

        if (registers.Byte(0x0F0B) == 0)                                    // image@0x04FB2
        {
            return EngagementVmStepOutcome.RearmedWithoutResort;
        }

        EngagementList.SortInsert(arena, registers, blockRef);              // image@0x04FB9
        return EngagementVmStepOutcome.RearmedAndResorted;
    }
}

/// <summary>Which tail arm one <see cref="EngagementVmStep"/> call took.</summary>
public enum EngagementVmStepOutcome
{
    /// <summary>
    /// The class prototype's <c>+0x0C</c> bit 3 was clear, so the VM did not run at all
    /// (<c>image@0x04F7A</c>).
    /// </summary>
    ClassNotVmCapable,

    /// <summary>
    /// The deadline was re-armed and the node re-sorted into <c>[0xEDAA]</c>
    /// (<c>image@0x04FB9</c>).
    /// </summary>
    RearmedAndResorted,

    /// <summary>
    /// The deadline was re-armed but <c>g_scene_init_guard_flag [0x0F0B]</c> was 0, so the node was
    /// left where it was (<c>image@0x04FB2</c>).
    /// </summary>
    RearmedWithoutResort,

    /// <summary>The linked slot's ACTIVE bit was clear, so it was retired (<c>image@0x04FC9</c>).</summary>
    Departed,
}

/// <summary>
/// Binds an <see cref="EngagementVmContext"/> to C3b's <see cref="IEngagementScriptInterpreter"/>
/// signature, which carries only the register file, the arena and the mode.
/// </summary>
/// <remarks>
/// <b>The unification ruling: the two seams stay SEPARATE.</b> They model two
/// different functions with two different ABIs — <see cref="IEngagementVm"/> is
/// <c>engagement_slot_fsm_advance</c> (FAR, <c>BX</c> = a pool block, snapshot/restore around the
/// call) and <see cref="IEngagementScriptInterpreter"/> is the interpreter itself (NEAR, no block
/// argument, the caller having already filled the scratch).  Merging them would force one of the two
/// callers to fabricate the other's argument.  This adapter is the bridge instead, and it changes no
/// verified number: C1's and C3b's runs keep calling exactly the seam they always called.
/// </remarks>
/// <param name="context">The bound context.</param>
public sealed class EngagementVmAdapter(EngagementVmContext context) : IEngagementScriptInterpreter
{
    private readonly EngagementVmContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc/>
    public void Run(CombatRegisters registers, PoolArena arena, byte mode) =>
        EngagementVm.Run(_context, mode);
}
