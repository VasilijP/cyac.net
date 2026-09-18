using CYAC.Port.Core.Model.Combat;

namespace CYAC.Port.Core.Sim.Combat;

/// <summary>
/// The engagement expiry LIST — a singly-linked chain of engagement blocks, sorted by
/// <see cref="EngagementState.FrameDeadline"/>, headed by
/// <c>g_engagement_expiry_list_head [0xEDAA]</c>.
/// </summary>
/// <remarks>
/// <para>
/// A node IS an engagement block inside a pool object (there is no separate node record), so a node
/// is identified by its arena near offset and its link is the block's <c>+0x07</c> word.  Node
/// identity, ordering and the unsigned compare are all part of the contract: two nodes with EQUAL
/// deadlines swap places on every pass, because
/// <see cref="NodeSortedInsert"/> inserts before an equal key.  That is visible in the reference
/// trace — the four due nodes of <c>v11_b2_clear_det</c> step 58,500 come out in the exact reverse
/// order at 58,501 and back again at 58,502.
/// </para>
/// <para>
/// All six routines are transliterated from the bytes; each method's remarks name its address.
/// </para>
/// </remarks>
public static class EngagementList
{
    /// <summary>
    /// <c>engagement_list_node_sorted_insert @image@0x02352</c> — insert one node into a chain by
    /// deadline, returning the chain's new head.  <c>cdecl</c>, <c>ret 4</c>.
    /// </summary>
    /// <param name="arena">The pool arena.</param>
    /// <param name="registers">The combat register file (for the scene-init guard).</param>
    /// <param name="nodeRef">The node to insert.</param>
    /// <param name="chainHead">The chain's current head, or 0.</param>
    /// <returns>The chain's new head.</returns>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>empty chain ⇒ <c>node->next = 0</c>; return node
    /// (<c>image@0x02361..0x0236B</c>);</description></item>
    /// <item><description><c>g_scene_init_guard_flag [0x0F0B] == 0</c> ⇒ plain PUSH FRONT,
    /// <b>no sorting at all</b> (<c>cmp byte ptr [0xf0b],0 / jne</c> @<c>image@0x02373</c>) — the
    /// mission-load path builds the list unordered and
    /// <see cref="SortRebuild"/> sorts it later;</description></item>
    /// <item><description>otherwise walk while <c>node.deadline &gt; cursor.deadline</c>
    /// (<c>cmp ax,es:[bp+0xb] / jbe</c> @<c>image@0x0238E</c> — UNSIGNED, and <c>jbe</c> means an
    /// EQUAL key inserts BEFORE), then splice.</description></item>
    /// </list>
    /// </remarks>
    public static ushort NodeSortedInsert(
        PoolArena arena, CombatRegisters registers, ushort nodeRef, ushort chainHead)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(registers);

        if (chainHead == 0)
        {
            arena.SetNodeNext(nodeRef, 0);
            return nodeRef;
        }

        if (registers.SceneInitGuard == 0)
        {
            arena.SetNodeNext(nodeRef, chainHead);
            return nodeRef;
        }

        ushort head = chainHead;
        ushort cursor = chainHead;
        ushort previous = 0;
        while (cursor != 0 && arena.NodeDeadline(nodeRef) > arena.NodeDeadline(cursor))
        {
            previous = cursor;
            cursor = arena.NodeNext(cursor);
        }

        if (previous != 0)
        {
            arena.SetNodeNext(previous, nodeRef);
        }
        else
        {
            head = nodeRef;
        }

        arena.SetNodeNext(nodeRef, cursor);
        return head;
    }

    /// <summary>
    /// <c>engagement_list_sorted_insert @image@0x07194</c> — MERGE a sorted chain into the main list.
    /// </summary>
    /// <param name="arena">The pool arena.</param>
    /// <param name="registers">The combat register file; its <c>[0xEDAA]</c> head is updated.</param>
    /// <param name="chain">The chain to merge, or 0 for nothing to do.</param>
    /// <remarks>
    /// A textbook merge of two deadline-sorted chains: advance the MAIN cursor while
    /// <c>main.deadline &lt; incoming.deadline</c> (<c>cmp es:[bx+0xb],ax / jae</c>
    /// @<c>image@0x071E0</c> — UNSIGNED again, and <c>jae</c> means an EQUAL key takes the incoming
    /// node FIRST), otherwise splice the incoming node in and advance the incoming cursor.  An empty
    /// main list adopts the chain whole (<c>image@0x071A3..0x071AC</c>).
    /// </remarks>
    public static void SortedInsertChain(PoolArena arena, CombatRegisters registers, ushort chain)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(registers);

        if (chain == 0)
        {
            return;
        }

        if (registers.ExpiryListHead == 0)
        {
            registers.ExpiryListHead = chain;
            return;
        }

        ushort cursor = registers.ExpiryListHead;
        ushort previous = 0;
        ushort incoming = chain;

        while (incoming != 0)
        {
            if (cursor != 0 && arena.NodeDeadline(cursor) < arena.NodeDeadline(incoming))
            {
                previous = cursor;
                cursor = arena.NodeNext(cursor);
                continue;
            }

            if (previous != 0)
            {
                arena.SetNodeNext(previous, incoming);
            }
            else
            {
                registers.ExpiryListHead = incoming;
            }

            previous = incoming;
            ushort next = arena.NodeNext(incoming);
            arena.SetNodeNext(incoming, cursor);
            incoming = next;
        }
    }

    /// <summary>
    /// <c>engagement_list_sort_rebuild @image@0x07154</c> — pop every node off the main list and
    /// re-insert it, leaving the list sorted.  FAR (<c>retf</c>).
    /// </summary>
    /// <param name="arena">The pool arena.</param>
    /// <param name="registers">The combat register file.</param>
    /// <remarks>
    /// It is the mission-load path's counterpart to the guard-off PUSH FRONT above.  Because
    /// <see cref="NodeSortedInsert"/> puts an equal key FIRST, a rebuild REVERSES every run of equal
    /// deadlines — a real, reproducible behaviour, not an implementation detail.
    /// </remarks>
    public static void SortRebuild(PoolArena arena, CombatRegisters registers)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(registers);

        ushort accumulator = 0;
        while (registers.ExpiryListHead != 0)
        {
            ushort node = registers.ExpiryListHead;
            registers.ExpiryListHead = arena.NodeNext(node);
            accumulator = NodeSortedInsert(arena, registers, node, accumulator);
        }

        registers.ExpiryListHead = accumulator;
    }

    /// <summary>
    /// <c>engagement_list_node_unlink @image@0x070DB</c> — remove a node from the main list.
    /// </summary>
    /// <param name="arena">The pool arena.</param>
    /// <param name="registers">The combat register file.</param>
    /// <param name="nodeRef">The node to unlink.</param>
    /// <remarks>
    /// A linear search from the head, with the head case handled separately
    /// (<c>image@0x0713E..0x0714B</c>).  A node that is not in the list is a no-op.
    /// </remarks>
    public static void Unlink(PoolArena arena, CombatRegisters registers, ushort nodeRef)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(registers);

        ushort cursor = registers.ExpiryListHead;
        ushort previous = 0;
        while (cursor != 0)
        {
            if (cursor == nodeRef)
            {
                if (previous != 0)
                {
                    arena.SetNodeNext(previous, arena.NodeNext(cursor));
                }
                else
                {
                    registers.ExpiryListHead = arena.NodeNext(cursor);
                }

                return;
            }

            previous = cursor;
            cursor = arena.NodeNext(cursor);
        }
    }

    /// <summary>
    /// <c>engagement_list_sort_insert @image@0x087DA</c> — unlink a node and re-insert it in order.
    /// </summary>
    /// <param name="arena">The pool arena.</param>
    /// <param name="registers">The combat register file.</param>
    /// <param name="nodeRef">The node whose deadline just changed.</param>
    /// <remarks>
    /// Two doors: <c>engagement_slot_fsm_advance @image@0x04FB9</c> and
    /// <c>engagement_new_slot_select_and_commit @image@0x0BD4E</c>.  It is the "my deadline moved,
    /// re-file me" operation.
    /// </remarks>
    public static void SortInsert(PoolArena arena, CombatRegisters registers, ushort nodeRef)
    {
        Unlink(arena, registers, nodeRef);
        registers.ExpiryListHead =
            NodeSortedInsert(arena, registers, nodeRef, registers.ExpiryListHead);
    }

    /// <summary>
    /// <c>engagement_list_node_init @image@0x07046</c> — initialise a fresh engagement block.
    /// </summary>
    /// <param name="block">The block, whose <c>+0x00</c> prototype reference is already set.</param>
    /// <param name="ownerObjectRef">The pool object the block belongs to — the original's <c>AX</c>.</param>
    /// <param name="prototypes">The prototype table.</param>
    /// <param name="frameCounter"><c>g_master_frame_counter [0xF0C8]</c>, for the expiry arm.</param>
    /// <param name="random">The RNG seam, for the expiry arm's one draw.</param>
    /// <returns>True when the block was armed as a full engagement (the prototype is engage-capable).</returns>
    /// <remarks>
    /// <para>
    /// <c>spawn_dispatch_object</c> calls it on a 58-byte TEMPLATE on its own stack frame
    /// (<c>lea bx,[bp-0x3e]</c> @<c>image@0x06FB6</c>) before handing the template to the arena — so
    /// this operates on a block, not on the arena.
    /// </para>
    /// <para>
    /// Unconditional: <c>+0x04 := prototype[+0x09]</c>, <c>+0x02 := owner</c>, <c>+0x1E := 0</c>.
    /// Then <c>test byte ptr [proto+0x0c],8</c> (<c>image@0x0705E</c>) gates the rest:
    /// <c>+0x05 := 0</c> (a WORD store, so <c>+0x06</c> too), <see cref="ExpiryInit"/>, then
    /// <c>+0x20 := 0</c>, <c>+0x09 := 0</c>, <c>+0x22 := −1</c>, <c>+0x0E := −1</c>,
    /// <c>+0x1B := 0</c>, <c>+0x07 := 0</c>, <c>+0x27 := 0</c>, <c>+0x25 := 0</c>,
    /// <c>+0x0D := 0</c>, <c>+0x2B := 0</c> (<c>image@0x07064..0x07093</c>).
    /// </para>
    /// </remarks>
    public static bool NodeInit(
        EngagementState block,
        ushort ownerObjectRef,
        IEngagementPrototypes prototypes,
        ushort frameCounter,
        ICombatRandom random)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(prototypes);
        ArgumentNullException.ThrowIfNull(random);

        ushort prototypeRef = block.PrototypeRef;
        block.HitPoints = prototypes.InitialHitPoints(prototypeRef);
        block.OwnerObjectRef = ownerObjectRef;
        block.Unknown0x1E = 0;

        if ((prototypes.FlagsWord(prototypeRef) & 0x08) == 0)
        {
            return false;
        }

        block.Flags = EngagementStateFlags.None;        // the word store at +0x05 clears +0x06 too
        block.LoadState = 0;
        ExpiryInit(block, prototypes, frameCounter, random);

        block.ScriptSegment = 0;
        block.Unknown0x09 = 0;
        block.ScriptPc = -1;
        block.FsmLoopCount = 0xFFFF;
        block.AcquisitionTarget = 0;
        block.NextNode = 0;
        block.SpeedQ8 = 0;                              // +0x25 and +0x27 as one 32-bit zero
        block.Phase = 0;
        block.Unknown0x2B = 0;
        return true;
    }

    /// <summary>
    /// <c>engagement_list_node_expiry_init @image@0x0709B</c> — arm a block's engagement:
    /// its random phase, its acquisition state, its init parameters and its expiry frame.
    /// </summary>
    /// <param name="block">The block.</param>
    /// <param name="prototypes">The prototype table.</param>
    /// <param name="frameCounter"><c>g_master_frame_counter [0xF0C8]</c>.</param>
    /// <param name="random">The RNG seam.</param>
    /// <remarks>
    /// Its ONE random draw is <c>prng_rand_bounded(12)</c> (<c>mov ax,0xc / lcall</c>
    /// @<c>image@0x0709C</c>) into <c>+0x17</c>.  Then <c>+0x11:= 0</c>, <c>+0x10:= 0</c>, a 4-byte
    /// memcpy of <c>prototype[+0x16]</c> into <c>+0x12</c>, <c>+0x29:= 60 × prototype[+0x2D] +
    /// frameCounter</c> (an 8-bit <c>MUL</c>, so the product is at most 15,300 and the sum wraps mod
    /// 2^16), and finally <c>+0x06 &amp;= 0xFD</c>.
    /// </remarks>
    public static void ExpiryInit(
        EngagementState block,
        IEngagementPrototypes prototypes,
        ushort frameCounter,
        ICombatRandom random)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(prototypes);
        ArgumentNullException.ThrowIfNull(random);

        block.RandomPhaseIndex = (ushort)random.RandBounded(12);
        block.AcquisitionState = 0;
        block.TypeSlotIndex = 0;
        block.InitParams = prototypes.InitParams(block.PrototypeRef);
        block.ExpiryFrame = unchecked((ushort)((60 * prototypes.ExpirySeconds(block.PrototypeRef))
            + frameCounter));
        block.LoadState &= 0xFD;
    }
}
