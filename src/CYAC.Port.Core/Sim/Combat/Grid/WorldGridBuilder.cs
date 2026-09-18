using CYAC.Port.Core.Model.World;

namespace CYAC.Port.Core.Sim.Combat.Grid;

/// <summary>
/// The 2-D cell-bucket array the grid build reads —
/// <c>g_grid_2d_cell_list_seg [0x0098] : g_grid_2d_cell_list_base_off [0x0092] + index*4</c>, with
/// <c>index = col * g_grid_2d_dim_a [0x008E] + row</c>.
/// </summary>
/// <remarks>
/// <para>
/// Each 4-byte cell entry's FIRST word is a near pointer into the object pool; the build then walks
/// that object's <c>[+0x04]</c> chain to its end.  The second word is never read by any of the four
/// consumers (<c>world_grid_recursive_subdivide</c>, <c>grid_2d_cell_best_object_insert</c>,
/// <c>grid_2d_tallest_obstacle_score_at (ex-grid_2d_object_proximity_score_at)</c>, <c>grid_2d_init_via_alloc_5000</c>).
/// </para>
/// <para>
/// <b>This array is why C2b cannot verify per P20 call.</b> Its segment word <c>[0x0098]</c> is ONE WORD past the
/// end of the combat trace's <c>object_pool_and_grid_segments [0x008A]+14</c> window, and the array itself lives in
/// that far segment, which nothing dumps.  Measured on the recordings: the pool's whole <c>[+0x04]</c> chain is a SINGLE
/// list (99 entries, 0x4F20 → 0x0002, strictly descending), so the cell heads are pointers INTO one shared chain and
/// an object's position does not determine where a cell's walk starts.
/// </para>
/// </remarks>
public interface IWorldGridCellIndex
{
    /// <summary>The number of ROWS — <c>g_grid_2d_dim_a [0x008E]</c>, the per-column stride.</summary>
    int RowCount { get; }

    /// <summary>The number of COLUMNS — <c>g_grid_2d_dim_b [0x0090]</c>.</summary>
    int ColumnCount { get; }

    /// <summary>The first object of one cell's list.</summary>
    /// <param name="column">0 &lt;= column &lt; <see cref="ColumnCount"/>.</param>
    /// <param name="row">0 &lt;= row &lt; <see cref="RowCount"/>.</param>
    /// <returns>A pool near offset, or 0 for an empty cell.</returns>
    ushort CellHead(int column, int row);
}

/// <summary>
/// <c>world_grid_init_pair @image@0x28404</c> → <c>world_grid_alloc_and_subdivide @image@0x28419</c>
/// → <c>world_grid_recursive_subdivide @image@0x29104</c> — the world grid's BUILD, run once per
/// scene from <c>mission_state_machine</c>'s <c>LCALL 0x3840:0x0004</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Correction, byte-proven.</b> The build side of the world-grid quadtree is
/// NOT <c>grid_2d_cell_best_object_insert @image@0x21AC2</c> — that function builds a different structure
/// (the 5000-byte proximity buffer <c>g_grid_2d_buffer_ptr [0x1156]</c> that
/// <see cref="Grid2dProximityScore"/> reads) from the same cell array.  The quadtree is built here, and it
/// is <b>not incremental</b>: there is no per-object insert at all.  The two arenas are snapshots of the
/// scene taken at load time, and the moving objects are covered separately by the query's own active-target
/// scan.
/// </para>
/// <para><b>Per node</b> (<c>image@0x29104</c>): compute the two midpoints, expand the node's
/// region to a ±1 CELL neighbourhood clamped to the grid, then scan those cells in
/// <c>for row: for col:</c> order.  Every object on a cell's list that (a) shares ANY bit with the
/// routing mask <c>[0xF13E]</c> and (b) overlaps the node's region on axis 0 and axis 2 — its class
/// record's box, widened by <b>±200 units</b> and compared on the HIGH WORD only — is appended to
/// the node.  If the append count reaches the cap the node becomes an INTERNAL node and recurses
/// into four quadrants; otherwise it becomes a LEAF.</para>
/// <para><b>The cap</b> is 10, except that a node whose span on EITHER axis is <c>&lt;= 8</c> gets
/// <c>0xFFFF</c> instead (<c>image@0x29194..0x291A5</c>) — a "never subdivide further" sentinel, so
/// small regions always become leaves however crowded they are.</para>
/// <para>Note the mask test here is <c>test</c> (<b>ANY</b> bit) whereas the query's own walkers use
/// <c>and</c>+<c>cmp</c> (<b>ALL</b> bits, and the query has ORed <see cref="WorldObjectFlags.Active"/>
/// into the mask by then).  A grid leaf can therefore hold objects the walker will reject.</para>
/// </remarks>
public static class WorldGridBuilder
{
    /// <summary>The append cap for a normally sized node (<c>mov cx,0xa</c> <c>image@0x29194</c>).</summary>
    public const int LeafCapacity = 10;

    /// <summary>A node span at or below this on either axis forces a leaf (<c>cmp ...,8 / jle</c>).</summary>
    public const int ForceLeafSpan = 8;

    /// <summary>The ±margin the per-object overlap test adds (<c>0xC8</c>).</summary>
    public const int OverlapMargin = 200;

    /// <summary>The routing mask <c>world_grid_init_pair</c> builds the LO arena with.</summary>
    public const ushort LoArenaBuildMask = 0x1000;

    /// <summary>The routing mask it builds the HI arena with.</summary>
    public const ushort HiArenaBuildMask = 0x0100;

    /// <summary>
    /// <c>world_grid_init_pair @image@0x28404</c> — builds both arenas, LO first.
    /// </summary>
    /// <param name="cells">The 2-D cell-bucket array.</param>
    /// <param name="arena">The object pool.</param>
    /// <param name="staticData">The DGROUP surface class records live in.</param>
    /// <returns>The pair, ready for <see cref="WorldGridQuery"/>.</returns>
    public static WorldGridIndex BuildPair(
        IWorldGridCellIndex cells, PoolArena arena, ICombatStaticData staticData) =>
        new(
            Build(cells, arena, staticData, LoArenaBuildMask),
            Build(cells, arena, staticData, HiArenaBuildMask));

    /// <summary>
    /// <c>world_grid_alloc_and_subdivide @image@0x28419</c> — one arena, from the whole grid.
    /// </summary>
    /// <param name="cells">The 2-D cell-bucket array.</param>
    /// <param name="arena">The object pool.</param>
    /// <param name="staticData">The DGROUP surface class records live in.</param>
    /// <param name="routingMask">
    /// <c>0x1000</c> or <c>0x0100</c>.  Note this is the RAW mask: the query ORs 1 into it before
    /// the walkers see it, but the BUILD tests the raw value.
    /// </param>
    /// <returns>The built arena, trimmed to the bytes the build actually wrote.</returns>
    public static WorldGridArena Build(
        IWorldGridCellIndex cells, PoolArena arena, ICombatStaticData staticData, ushort routingMask)
    {
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(staticData);

        BuildRun build = new BuildRun(cells, arena, staticData, routingMask);

        // `sub ax,ax; push ax; push ax; mov ah,[0x8e]; sub al,al; push ax; mov ah,[0x90]; push ax`
        // (image@0x28433..0x28442) — the whole grid, in "cell << 8" units.
        build.Subdivide(0, (ushort)(cells.RowCount << 8), 0, (ushort)(cells.ColumnCount << 8));
        return new WorldGridArena(build.Bytes, build.Cursor);
    }

    private sealed class BuildRun
    {
        private readonly IWorldGridCellIndex _cells;
        private readonly PoolArena _arena;
        private readonly ICombatStaticData _staticData;
        private readonly ushort _routingMask;
        private readonly byte[] _bytes = new byte[WorldGridArena.AllocationBytes];

        public BuildRun(
            IWorldGridCellIndex cells, PoolArena arena, ICombatStaticData staticData, ushort routingMask)
        {
            _cells = cells;
            _arena = arena;
            _staticData = staticData;
            _routingMask = routingMask;
        }

        /// <summary><c>g_world_grid_build_cursor (ex-g_world_grid_alloc_ptr_hi) [0xF13C]</c>.</summary>
        public int Cursor { get; private set; }

        public ReadOnlySpan<byte> Bytes => _bytes;

        /// <summary>One node — <c>world_grid_recursive_subdivide @image@0x29104</c>.</summary>
        public void Subdivide(ushort aLo, ushort aHi, ushort bLo, ushort bHi)
        {
            int nodeStart = Cursor;                                          // [0x3012]
            ushort spanA = unchecked((ushort)(aHi - aLo + 1));               // [0x300C]
            ushort midA = unchecked((ushort)((spanA >> 1) + aLo));           // [bp-6]
            ushort spanB = unchecked((ushort)(bHi - bLo + 1));               // [0x300E]
            ushort midB = unchecked((ushort)((spanB >> 1) + bLo));           // [bp-8]

            // The ±1 cell neighbourhood, clamped (image@0x29148..0x29183).  The index is the HIGH
            // BYTE of each bound, sign-extended by CBW.
            int rowMin = Math.Max(unchecked((sbyte)(aLo >> 8)) - 1, 0);
            int rowMax = Math.Min(unchecked((sbyte)(aHi >> 8)) + 1, _cells.RowCount - 1);
            int colMin = Math.Max(unchecked((sbyte)(bLo >> 8)) - 1, 0);
            int colMax = Math.Min(unchecked((sbyte)(bHi >> 8)) + 1, _cells.ColumnCount - 1);

            Guard(nodeStart >= WorldGridArena.NodeHeaderLimit, "node header", nodeStart);

            // The append cap (image@0x29194..0x291A5): 0xFFFF when either span is small.
            int remaining = (short)spanA <= ForceLeafSpan || (short)spanB <= ForceLeafSpan
                ? 0xFFFF
                : LeafCapacity;

            int write = nodeStart + 1;
            bool overflowed = false;

            // Both loops are DO-WHILE in the original (the body is entered before either bound is
            // tested — image@0x291B2/0x291B8 fall straight into 0x291BE), so an empty range still
            // scans one cell.  Reproduced literally.
            int row = rowMin;
            do
            {
                int col = colMin;
                do
                {
                    ushort candidate = _cells.CellHead(col, row);
                    while (candidate != 0)
                    {
                        if (Qualifies(candidate, aLo, aHi, bLo, bHi))
                        {
                            Guard(write >= WorldGridArena.EntryLimit, "leaf entry", write);
                            WriteWord(write, candidate);
                            write += 2;
                            if (--remaining == 0)
                            {
                                overflowed = true;
                                break;
                            }
                        }

                        candidate = _arena.Word((ushort)(candidate + 0x04));
                    }

                    col++;
                }
                while (!overflowed && col <= colMax);

                row++;
            }
            while (!overflowed && row <= rowMax);

            if (!overflowed)
            {
                // LEAF (image@0x292A0..0x292BE): terminator, then the tag byte goes back at the head.
                Guard(write > WorldGridArena.LeafTerminatorLimit, "leaf terminator", write);
                WriteWord(write, 0);
                Cursor = write + 2;
                _bytes[nodeStart] = 1;
                return;
            }

            // INTERNAL NODE (image@0x292C0..0x29338): a 13-byte header, then four children.
            Cursor = nodeStart + 0x0D;
            Guard(nodeStart >= WorldGridArena.NodeHeaderLimit, "node header", nodeStart);
            _bytes[nodeStart] = 0;
            WriteWord(nodeStart + 1, midA);
            WriteWord(nodeStart + 3, midB);

            WriteWord(nodeStart + 5, unchecked((ushort)Cursor));
            Subdivide(aLo, midA, bLo, midB);          // quadrant 0: A low,  B low
            WriteWord(nodeStart + 7, unchecked((ushort)Cursor));
            Subdivide(midA, aHi, bLo, midB);          // quadrant 1: A high, B low
            WriteWord(nodeStart + 9, unchecked((ushort)Cursor));
            Subdivide(aLo, midA, midB, bHi);          // quadrant 2: A low,  B high
            WriteWord(nodeStart + 11, unchecked((ushort)Cursor));
            Subdivide(midA, aHi, midB, bHi);          // quadrant 3: A high, B high
        }

        /// <summary>The per-object test (<c>image@0x291F0..0x29262</c>).</summary>
        private bool Qualifies(ushort candidate, ushort aLo, ushort aHi, ushort bLo, ushort bHi)
        {
            // `test es:[bx+2],ax` — ANY bit of the mask, not all of them.
            if ((_arena.Word((ushort)(candidate + 0x02)) & _routingMask) == 0)
            {
                return false;
            }

            ushort classRef = _arena.Word(candidate);
            int posA = Position(candidate, 0x06);
            int posB = Position(candidate, 0x0E);

            int aMax = unchecked(posA
                + WorldObjectClassRecord.Int32(_staticData, classRef, WorldObjectClassRecord.BoxXMaxOffset)
                + OverlapMargin);
            if (unchecked((short)(aMax >> 16)) < (short)aLo)
            {
                return false;
            }

            int aMin = unchecked(posA
                + WorldObjectClassRecord.Int32(_staticData, classRef, WorldObjectClassRecord.BoxXMinOffset)
                - OverlapMargin);
            if (unchecked((short)(aMin >> 16)) >= (short)aHi)
            {
                return false;
            }

            int bMax = unchecked(posB
                + WorldObjectClassRecord.Int32(_staticData, classRef, WorldObjectClassRecord.BoxZMaxOffset)
                + OverlapMargin);
            if (unchecked((short)(bMax >> 16)) < (short)bLo)
            {
                return false;
            }

            int bMin = unchecked(posB
                + WorldObjectClassRecord.Int32(_staticData, classRef, WorldObjectClassRecord.BoxZMinOffset)
                - OverlapMargin);
            return unchecked((short)(bMin >> 16)) < (short)bHi;
        }

        private int Position(ushort candidate, int offset)
        {
            ushort lo = _arena.Word((ushort)(candidate + offset));
            ushort hi = _arena.Word((ushort)(candidate + offset + 2));
            return unchecked((int)(((uint)hi << 16) | lo));
        }

        private void WriteWord(int offset, ushort value)
        {
            _bytes[offset] = (byte)value;
            _bytes[offset + 1] = (byte)(value >> 8);
        }

        private static void Guard(bool tripped, string what, int cursor)
        {
            if (tripped)
            {
                throw new WorldGridSeamException(
                    $"world_grid_recursive_subdivide: the {what} cursor reached 0x{cursor:X4}, which "
                        + "is where the original calls its OOM abort modal (LCALL 0x30B2:0x0062 — "
                        + "image@0x2918F / 0x2926A / 0x292AA / 0x292D0).  That path never returns.");
            }
        }
    }
}
