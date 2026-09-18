namespace CYAC.Port.Core.Sim.Combat.Grid;

/// <summary>
/// One of the two 5000-byte world-grid quadtree arenas — the bytes
/// <c>world_grid_recursive_subdivide @image@0x29104</c> emits and
/// <c>world_grid_aabb_inner_walker @image@0x290DE</c> walks.
/// </summary>
/// <remarks>
/// <para>
/// <b>Record layout, byte-derived from the emitter's own <c>stos</c> stream</b>
/// (<c>image@0x292AF..0x292E7</c> leaf, <c>image@0x292C0..0x29338</c> node) and cross-checked
/// against the walker's own operands (<c>image@0x290DE..0x290F5</c>):
/// </para>
/// <list type="table">
///   <item>
///     <term>INTERNAL NODE (13 bytes, <c>lea ax,[di+0xd]</c> <c>image@0x292C4</c>)</term>
///     <description>
///       <c>+0</c> tag byte <b>0</b>; <c>+1</c> <c>u16</c> midA (the A-axis midpoint, compared
///       against the walker's <c>CX</c>); <c>+3</c> <c>u16</c> midB (compared against <c>DX</c>);
///       <c>+5/+7/+9/+0xB</c> the four child offsets, in quadrant order
///       <c>(A&lt;midA,B&lt;midB) (A&gt;=midA,B&lt;midB) (A&lt;midA,B&gt;=midB) (A&gt;=midA,B&gt;=midB)</c>.
///     </description>
///   </item>
///   <item>
///     <term>LEAF (<c>3 + 2k</c> bytes)</term>
///     <description>
///       <c>+0</c> tag byte <b>1</b> (<c>mov byte es:[di],1</c> <c>image@0x292BA</c>); then
///       <c>k</c> object near-offsets as <c>u16</c>, terminated by a <c>0</c> word
///       (<c>xor ax,ax; stosw</c> <c>image@0x292AF</c>).
///     </description>
///   </item>
/// </list>
/// <para>
/// The tag byte is what the walker tests (<c>cmp byte [bx],0 / jne</c>): <b>0 = descend</b>, anything else = leaf.
/// Only 1 is ever emitted.
/// </para>
/// <para>
/// The root node always starts at offset 0 (<c>xor bx,bx</c> before each walker call,
/// <c>image@0x286CA</c>), and the emitter's own cursor <c>g_world_grid_build_cursor (ex-g_world_grid_alloc_ptr_hi) [0xF13C]</c>
/// starts at 0 (<c>mov word [0xf13c],0</c> <c>image@0x2842D</c>).
/// </para>
/// </remarks>
public sealed class WorldGridArena
{
    /// <summary>The allocation the original makes for each arena — <c>0x1388</c>
    /// (<c>mov ax,0x1388</c> <c>image@0x28422</c>), later shrunk to the used length.</summary>
    public const int AllocationBytes = 0x1388;

    /// <summary>The panic threshold the emitter checks a NODE header against
    /// (<c>cmp ax,0x137b / jl</c> <c>image@0x2918A</c>) — 5000 − 13.</summary>
    public const int NodeHeaderLimit = 0x137B;

    /// <summary>The panic threshold the emitter checks the LEAF terminator against
    /// (<c>cmp di,0x1386 / jbe</c> <c>image@0x292A4</c>).</summary>
    public const int LeafTerminatorLimit = 0x1386;

    /// <summary>The panic threshold the per-entry <c>stosw</c> checks
    /// (<c>cmp di,0x1388 / jb</c> <c>image@0x29264</c>).</summary>
    public const int EntryLimit = 0x1388;

    private readonly byte[] _bytes;

    /// <summary>Wraps an arena image.</summary>
    /// <param name="bytes">The arena bytes, from offset 0.</param>
    /// <param name="usedLength">The emitter's final <c>[0xF13C]</c> cursor, or the whole span.</param>
    public WorldGridArena(ReadOnlySpan<byte> bytes, int usedLength = -1)
    {
        _bytes = bytes.ToArray();
        UsedLength = usedLength < 0 ? _bytes.Length : usedLength;
    }

    /// <summary>The bytes the build actually wrote — the original's final <c>[0xF13C]</c>.</summary>
    public int UsedLength { get; }

    /// <summary>The whole arena.</summary>
    public ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>The tag byte of the record at <paramref name="offset"/>; 0 means "internal node".</summary>
    /// <param name="offset">A record's near offset within the arena.</param>
    /// <returns>The tag byte.</returns>
    public byte Tag(ushort offset) => _bytes[offset];

    /// <summary>One little-endian word of the arena.</summary>
    /// <param name="offset">The byte offset.</param>
    /// <returns>The word.</returns>
    public ushort Word(int offset) => (ushort)(_bytes[offset] | (_bytes[offset + 1] << 8));

    /// <summary>A node's A-axis midpoint (<c>[bx+1]</c>, compared against the walker's CX).</summary>
    /// <param name="node">The node's near offset.</param>
    /// <returns>The midpoint.</returns>
    public ushort NodeMidA(ushort node) => Word(node + 1);

    /// <summary>A node's B-axis midpoint (<c>[bx+3]</c>, compared against the walker's DX).</summary>
    /// <param name="node">The node's near offset.</param>
    /// <returns>The midpoint.</returns>
    public ushort NodeMidB(ushort node) => Word(node + 3);

    /// <summary>A node's child offset (<c>[bx+5+2*quadrant]</c>).</summary>
    /// <param name="node">The node's near offset.</param>
    /// <param name="quadrant">0..3, in the walker's own encoding.</param>
    /// <returns>The child record's near offset.</returns>
    public ushort NodeChild(ushort node, int quadrant) => Word(node + 5 + (2 * quadrant));

    /// <summary>The object near-offsets a leaf record holds, in emission order.</summary>
    /// <param name="leaf">The leaf record's near offset (its tag byte).</param>
    /// <returns>The entries, up to but excluding the zero terminator.</returns>
    public IEnumerable<ushort> LeafEntries(ushort leaf)
    {
        int p = leaf + 1;
        while (true)
        {
            ushort entry = Word(p);
            if (entry == 0)
            {
                yield break;
            }

            yield return entry;
            p += 2;
        }
    }
}

/// <summary>
/// <c>world_grid_aabb_inner_walker @image@0x290DE</c> — the 38-byte no-prologue iterative quadtree
/// descent the query calls four times (twice per arena branch).
/// </summary>
/// <remarks>
/// <para>
/// Byte-exact shape (<c>image@0x290DE..0x29103</c>):
/// <code>
///   cmp byte [bx],0 ; jne leaf
/// loop:
///   xor ax,ax
///   cmp dx,[bx+3] ; cmc ; rcl ax,1        ; ax = (dx &gt;= midB) ? 1 : 0
///   cmp cx,[bx+1] ; cmc ; rcl ax,1        ; ax = (ax &lt;&lt; 1) | (cx &gt;= midA)
///   shl ax,1
///   add bx,ax ; mov bx,[bx+5]
///   cmp byte [bx],0 ; je loop
/// leaf:
///   mov ax,ss ; mov ds,ax ; mov ax,bx ; ret
/// </code>
/// The <c>cmc</c> after each <c>cmp</c> turns the unsigned borrow into "greater-or-equal", so both
/// comparisons are <b>unsigned</b>.  The routine makes ZERO memory writes and returns the leaf's
/// near offset in AX.
/// </para>
/// <para>
/// The two comparands are always the HIGH words of the query point's axis 0 and axis 2 — the query
/// stages them at <c>[0x2F98]</c>/<c>[0x2FA0]</c> for point 1 and <c>[0x2FA4]</c>/<c>[0x2FAC]</c>
/// for point 2 (<c>image@0x286CC..0x286D0</c>, <c>image@0x286DF..0x286E3</c>).
/// </para>
/// </remarks>
public static class WorldGridWalker
{
    /// <summary>
    /// A defensive convergence bound.  The original has no guard at all: a malformed arena would
    /// spin forever, so the port names the derailment instead of hanging.
    /// </summary>
    public const int DescentGuard = 64;

    /// <summary>Descends from the root to the leaf covering <paramref name="a"/>/<paramref name="b"/>.</summary>
    /// <param name="arena">The grid arena.</param>
    /// <param name="a">The A-axis comparand — the walker's <c>CX</c>, the query point's X high word.</param>
    /// <param name="b">The B-axis comparand — the walker's <c>DX</c>, the query point's Z high word.</param>
    /// <returns>The leaf record's near offset (the walker's <c>AX</c>).</returns>
    public static ushort FindLeaf(WorldGridArena arena, ushort a, ushort b)
    {
        ArgumentNullException.ThrowIfNull(arena);

        ushort node = 0;
        int steps = 0;
        while (arena.Tag(node) == 0)
        {
            if (++steps > DescentGuard)
            {
                throw new WorldGridSeamException(
                    "world_grid_aabb_inner_walker @image@0x290DE did not converge after "
                        + $"{DescentGuard} descents — the arena is malformed or cyclic.");
            }

            // `cmp dx,[bx+3]; cmc; rcl ax,1` then `cmp cx,[bx+1]; cmc; rcl ax,1` — both UNSIGNED.
            int quadrant = (b >= arena.NodeMidB(node) ? 2 : 0) | (a >= arena.NodeMidA(node) ? 1 : 0);
            node = arena.NodeChild(node, quadrant);
        }

        return node;
    }
}
