namespace CYAC.Port.Core.Sim.Combat.Player;

/// <summary>
/// The renderer's per-frame display list, read out of the RENDER-SLOT BLOCK <c>[0xD8A4..0xE45B]</c>
/// in the combat register file — the concrete <see cref="ILockOnRenderNodes"/> the player lock-on
/// walks.
/// </summary>
/// <remarks>
/// <para>
/// The block is 3,000 bytes and its base and length are LITERAL immediates at all five
/// <c>polygon_fill_mesh_render_setup @image@0x146B8</c> doors (<c>mov ax,0xD8A4</c> /
/// <c>mov ax,0xBB8</c> at <c>image@0x016C7</c>/<c>0x016CB</c> — the gameplay one — and four more).
/// The setup stores them in <c>g_per_mesh_state_slot_list_base [0xE838]</c> /
/// <c>g_per_mesh_state_slot_list_limit [0xE83A]</c> (<c>image@0x14735</c>/<c>0x1473C</c>) and uses
/// the block from both ends: a near-pointer BATCH array grows UP from <c>[0xE95C]</c> while the
/// 22-byte NODE records are bump-allocated DOWN from
/// <c>[0xEA00] = base + len - 0x16 = 0xE446</c> (<c>image@0x147B0..0x147B7</c>,
/// <c>sub word [0xEA00],0x16</c> @<c>image@0x16EDD</c>).
/// </para>
/// <para>
/// The nodes are read on PLAIN DS — <c>mov si,[bp+6] / cmp word [si+0x14],ax / mov si,[si+2]</c>
/// (<c>image@0x02FD5..0x02FED</c>, <c>image@0x0307F..0x03091</c>) carry no segment prefix, while
/// the OBJECT a node names is read with an explicit <c>ES:</c> after <c>mov cx,[0x94] / mov
/// es,cx</c>.  So a DGROUP register window is the right shape for them (C9 §2.2;
/// </para>
/// <para>
/// Until the port owns a renderer this view is fed from the ORACLE — the combat trace's
/// <c>render_slot_block</c> stage window, which is <see cref="CombatFieldClass.Presentation"/> so
/// the driver verification MASKS it as an output while a lock-on verification READS it as an
/// input.  When the port's renderer lands it writes the same 3,000 bytes and nothing here changes.
/// </para>
/// </remarks>
/// <param name="registers">The DGROUP combat register file, whose window carries the block.</param>
public sealed class RenderSlotNodes(CombatRegisters registers) : ILockOnRenderNodes
{
    /// <summary>The block's base — <c>mov ax,0xD8A4</c> @<c>image@0x016C7</c>.</summary>
    public const int BlockBase = 0xD8A4;

    /// <summary>The block's length — <c>mov ax,0xBB8</c> @<c>image@0x016CB</c>.</summary>
    public const int BlockLength = 0xBB8;

    /// <summary>One past the block's last byte — <c>0xD8A4 + 0xBB8</c>.</summary>
    public const int BlockEnd = BlockBase + BlockLength;

    /// <summary>A node record's size — <c>sub word [0xEA00],0x16</c> @<c>image@0x16EDD</c>.</summary>
    public const int NodeBytes = 0x16;

    /// <summary>
    /// The topmost node slot — <c>[0xEA00]</c>'s initial value, <c>base + len - 0x16</c>
    /// (<c>image@0x147B0..0x147B7</c>).
    /// </summary>
    public const int TopNode = BlockEnd - NodeBytes;

    /// <summary>
    /// The most nodes the block can hold before <c>g_render_arena_overflow [0xE8AA]</c> trips —
    /// <c>24N &lt; 0xBA2 - 0x18</c> (<c>image@0x16EF1..0x16EFD</c>).  Measured maximum over the
    /// recordings: 45.
    /// </summary>
    public const int NodeCapacity = 123;

    private readonly CombatRegisters _registers =
        registers ?? throw new ArgumentNullException(nameof(registers));

    /// <summary>Whether a near offset can be a node the block holds.</summary>
    /// <param name="node">The candidate near offset.</param>
    /// <returns><see langword="true"/> when the whole 22-byte record is inside the block.</returns>
    public static bool IsNode(ushort node) => node >= BlockBase && node + NodeBytes <= BlockEnd;

    /// <inheritdoc/>
    public ushort Next(ushort node) => _registers.Word(unchecked((ushort)(node + 0x02)));

    /// <inheritdoc/>
    public ushort ObjectOf(ushort node) => _registers.Word(unchecked((ushort)(node + 0x14)));

    /// <inheritdoc/>
    public short DepthWord(ushort node) =>
        unchecked((short)_registers.Word(unchecked((ushort)(node + 0x0A))));

    /// <inheritdoc/>
    public (short ScreenX, short ScreenY) Project(ushort node) =>
        ObjectScreenProjection.Project(_registers, node);

    /// <summary>
    /// <c>image@0x02FD5..0x02FED</c> / <c>image@0x0307F..0x03091</c> — walk the list for the node
    /// whose <c>+0x14</c> names <paramref name="objectRef"/>.
    /// </summary>
    /// <param name="head">The list head, <c>g_engagement_object_list_head [0xE90A]</c>.</param>
    /// <param name="objectRef">The pool near offset to look for.</param>
    /// <returns>The node, or 0.</returns>
    /// <remarks>
    /// The machine's loop is unbounded; this one refuses to walk past
    /// <see cref="NodeCapacity"/> nodes or off the block, because a register file seeded from a
    /// trace can in principle be handed a corrupt head and the original would then hang.  Both
    /// guards throw rather than silently answering 0 — a bad walk is a finding.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The walk leaves the block or exceeds its capacity.</exception>
    public ushort FindObject(ushort head, ushort objectRef)
    {
        ushort node = head;
        int steps = 0;
        while (node != 0)
        {
            Validate(node, ref steps);
            if (ObjectOf(node) == objectRef)
            {
                return node;
            }

            node = Next(node);
        }

        return 0;
    }

    /// <summary>How many nodes the list holds — a census, not a machine operation.</summary>
    /// <param name="head">The list head.</param>
    /// <returns>The node count.</returns>
    /// <exception cref="InvalidOperationException">The walk leaves the block or exceeds its capacity.</exception>
    public int Count(ushort head)
    {
        ushort node = head;
        int steps = 0;
        while (node != 0)
        {
            Validate(node, ref steps);
            node = Next(node);
        }

        return steps;
    }

    private static void Validate(ushort node, ref int steps)
    {
        if (!IsNode(node))
        {
            throw new InvalidOperationException(
                $"render-list node 0x{node:X4} is outside the render-slot block "
                    + $"[0x{BlockBase:X4}..0x{BlockEnd - 1:X4}] (image@0x016C7 / image@0x016CB).");
        }

        if (++steps > NodeCapacity)
        {
            throw new InvalidOperationException(
                $"the render list walked more than {NodeCapacity} nodes, which the block's own "
                    + "overflow guard (image@0x16EF1) makes impossible.");
        }
    }
}
