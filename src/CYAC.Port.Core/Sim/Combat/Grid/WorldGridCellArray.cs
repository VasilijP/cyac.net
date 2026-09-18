namespace CYAC.Port.Core.Sim.Combat.Grid;

/// <summary>
/// The 2-D CELL-HEAD ARRAY every world-grid consumer reads — <c>[0x0098]:[0x0092]</c>, <c>[0x008E]</c>
/// × <c>[0x0090]</c> entries of 4 bytes — built by bucketing the pool's object list into a fixed grid
/// of 65,536-world-unit squares.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where it is built, and why nobody found it before.</b> The brief and H4 §5 A2 both expected
/// <c>grid_2d_cell_best_object_insert @image@0x21AC2</c> to FILL this array.  It does not: it READS it
/// (<c>mov cx,[0x98] / mov es,cx / mov ax,es:[bx]</c> @<c>image@0x21B05..0x21B0B</c>) to build the
/// 5,000-byte proximity buffer <c>[0x1156]</c>.  An exhaustive Capstone sweep for every instruction
/// that WRITES <c>[0x008E]</c>, <c>[0x0090]</c>, <c>[0x0092]</c> or <c>[0x0098]</c> finds NONE —
/// because the four words are written through a pointer, as fields <c>+0x04</c>/<c>+0x06</c>/
/// <c>+0x08</c>/<c>+0x0E</c> of the pool descriptor at DGROUP <c>0x008A</c>, by
/// <c>mesh_sort_insert_and_encode @image@0x1C7BA</c> — whose scanner name describes its LATER phases
/// (the mesh display-list encode) and hides that its first phase is the world grid's own index.
/// </para>
/// <para>
/// <b>The call site is the scene load's tail</b>: <c>mission_state_machine @image@0x00C53</c> →
/// <c>session_init_dispatcher_b @image@0x32532</c>, which is
/// </para>
/// <code>
/// image@0x325B9  lcall pool_arena_finalize_and_list_head @image@0x153D2   ; DX:AX = the arena list
/// image@0x325AF  ax:dx := 0x00040740                                      ; the world extent, twice
/// image@0x325C4  lcall mesh_sort_insert_and_encode(record 0x8A, list, extentX, extentZ)
/// </code>
/// <para>
/// so the index is built ONCE, after the containers are parsed and the pool is closed — it is not
/// incremental, which is what <see cref="WorldGridBuilder"/> already deduced from the other side.
/// </para>
/// <para>
/// <b>The grid's shape</b> (<c>image@0x1C7D8..0x1C80C</c>): <c>dim = hi16(extent + 0xFFFF)</c>,
/// floored at 1 — note <c>add ax,0xFFFF ; adc dx,0</c> is <c>+65535</c> and NOT <c>−1</c>, so this is
/// <c>ceil(extent / 65536)</c>.  With the shipped extent <c>0x00040740</c> = 263,488 world units on
/// both axes that is <b>5 × 5 = 25 cells</b>, each 65,536 world units square — exactly the 25 × 4-byte
/// array the combat trace's sidecar carries (<c>desc … 0505</c>, 100 bytes).
/// </para>
/// <para>
/// <b>The bucketing</b> (<c>image@0x1C8A0..0x1C97D</c>), per object of the list, walking
/// <c>obj[+0x04]</c>:
/// </para>
/// <code>
/// a = idiv32(sar32(obj.pos_x, 8), 65536)   clamped to [0, dimA-1]
/// b = idiv32(sar32(obj.pos_z, 8), 65536)   clamped to [0, dimB-1]
/// cell_list_push_front(cells[dimA*b + a], obj)     ; image@0x187AE
/// </code>
/// <para>
/// <c>cell_list_push_front</c> RE-USES the object's own <c>+0x04</c> link (<c>es:[obj+4]:= old
/// head</c>), so after the build the pool's sibling chain is no longer one list: it is 25 threads
/// through the same field.  That is the measured shape — "the pool's whole
/// <c>[+0x04]</c> chain is a SINGLE list … the cell heads are pointers INTO one shared chain" — the
/// chain is shared because the cells thread it, not because the cells index it.
/// </para>
/// <para>
/// <b>The render list is NOT bucketed.</b> The list handed to the build is the ARENA list
/// (<c>[0xB13C]</c>), and every engage-capable object the parser spawns went onto
/// <c>g_render_object_list_head [0x0096]</c> instead (<c>pool_child_link_push_front</c>, H4 §0.1), so
/// aircraft never enter a cell.  The grid indexes SCENERY only, and the query covers the moving
/// objects with its own active-target scan.
/// </para>
/// </remarks>
public sealed class WorldGridCellArray : IWorldGridCellIndex
{
    private readonly ushort[] _heads;
    private readonly ushort[] _second;

    /// <summary>
    /// The world extent both axes are divided by, at both shipped call sites:
    /// <c>0x00040740</c> = 263,488 world units (<c>mov ax,0x740 ; mov dx,4</c>
    /// @<c>image@0x101A1</c> / <c>image@0x325AF</c>).
    /// </summary>
    public const int ShippedWorldExtent = 0x00040740;

    /// <summary>One cell's side in world units: 65,536 (the divisor at <c>image@0x1C8AB</c>).</summary>
    public const int CellSideWorldUnits = 0x10000;

    /// <summary>Bytes the original allocates for the array: <c>0x258</c> = 600.</summary>
    /// <remarks><c>mov ax,0x258 ; lcall far_heap_alloc</c> @<c>image@0x1C815</c>.</remarks>
    public const int AllocationBytes = 0x258;

    /// <summary>
    /// The largest cell-array size the allocation admits: <c>0x256</c> = 598 bytes = 149 cells
    /// (<c>cmp ax,0x256 ; jbe</c> @<c>image@0x1C84B</c>; a bigger grid aborts with the OOM modal).
    /// </summary>
    public const int MaximumCellBytes = 0x256;

    /// <summary>Bytes one cell entry occupies: 4 (two words, only the first of which is ever read).</summary>
    public const int CellBytes = 4;

    /// <summary><c>g_mesh_pool_arena_descriptor [0xB13C]</c> — the arena list's HEAD near offset.</summary>
    public const int ArenaListHead = 0xB13C;

    /// <summary><c>[0xB14C]</c> — the arena list's TAIL near offset (its segment is <c>[0xB14E]</c>).</summary>
    public const int ArenaListTail = 0xB14C;

    /// <summary>The base offset the original gives the array inside its own segment: 2.</summary>
    /// <remarks>
    /// <c>[bp-0xc] := 0 ; add [bp-0xc],2</c> @<c>image@0x1C81F</c>/<c>0x1C82E</c>, then
    /// <c>record[+8] := [bp-0xc]</c> — the same "offset 0 is the null" convention the pool arena uses
    /// (H4 §0.1: the pool cursor also starts at base + 2).
    /// </remarks>
    public const int BaseOffset = 2;

    private WorldGridCellArray(int rowCount, int columnCount)
    {
        RowCount = rowCount;
        ColumnCount = columnCount;
        _heads = new ushort[rowCount * columnCount];
        _second = new ushort[rowCount * columnCount];
    }

    /// <summary>The divisions on axis 0 (X) — <c>g_grid_2d_dim_a [0x008E]</c>, the per-column stride.</summary>
    public int RowCount { get; }

    /// <summary>The divisions on axis 2 (Z) — <c>g_grid_2d_dim_b [0x0090]</c>.</summary>
    public int ColumnCount { get; }

    /// <summary>How many objects the build bucketed.</summary>
    public int ObjectsBucketed { get; private set; }

    /// <summary>How many cells ended up non-empty.</summary>
    public int OccupiedCells
    {
        get
        {
            int n = 0;
            foreach (ushort head in _heads)
            {
                if (head != 0)
                {
                    n++;
                }
            }

            return n;
        }
    }

    /// <inheritdoc/>
    public ushort CellHead(int column, int row)
    {
        if ((uint)column >= (uint)ColumnCount || (uint)row >= (uint)RowCount)
        {
            return 0;
        }

        return _heads[(column * RowCount) + row];
    }

    /// <summary>
    /// The array as the trace's sidecar dumps it: <c>RowCount * ColumnCount</c> entries of
    /// <c>{u16 head, u16 second}</c>, little-endian.
    /// </summary>
    /// <returns>A fresh buffer of <c>RowCount * ColumnCount * 4</c> bytes.</returns>
    public byte[] ToPayload()
    {
        byte[] bytes = new byte[_heads.Length * CellBytes];
        for (int i = 0; i < _heads.Length; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 4), _heads[i]);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan((i * 4) + 2), _second[i]);
        }

        return bytes;
    }

    /// <summary>The grid dimension one world extent produces: <c>hi16(extent + 0xFFFF)</c>, floored at 1.</summary>
    /// <param name="extent">The world extent on that axis, in world units.</param>
    /// <remarks><c>add ax,0xFFFF ; adc dx,0 ; record[+n] := dx ; cmp …,1 ; jge</c> @<c>image@0x1C7D8</c>.</remarks>
    public static int Dimension(int extent)
    {
        long biased = (uint)extent + 0xFFFFL;
        int dim = (int)((biased >> 16) & 0xFFFF);
        return dim < 1 ? 1 : dim;
    }

    /// <summary>
    /// <c>mesh_sort_insert_and_encode @image@0x1C7BA</c> phase 1 — allocates the cell array and
    /// buckets a pool list into it.
    /// </summary>
    /// <param name="arena">The pool arena the list lives in.</param>
    /// <param name="listHead">
    /// The arena's own list head, as <c>pool_arena_finalize_and_list_head @image@0x153D2</c> returns
    /// it (see <see cref="FinalizeArenaList"/>).
    /// </param>
    /// <param name="extentX">The world extent on axis 0; the shipped call sites pass
    /// <see cref="ShippedWorldExtent"/>.</param>
    /// <param name="extentZ">The world extent on axis 2.</param>
    /// <returns>The built index.</returns>
    /// <exception cref="InvalidOperationException">
    /// The grid would need more than <see cref="MaximumCellBytes"/> bytes — the original's
    /// <c>oom_error_abort_modal</c> arm (<c>image@0x1C850</c>).
    /// </exception>
    public static WorldGridCellArray Build(
        PoolArena arena,
        ushort listHead,
        int extentX = ShippedWorldExtent,
        int extentZ = ShippedWorldExtent)
    {
        ArgumentNullException.ThrowIfNull(arena);

        int dimA = Dimension(extentX);
        int dimB = Dimension(extentZ);
        WorldGridCellArray cells = new WorldGridCellArray(dimA, dimB);
        if (dimA * dimB * CellBytes > MaximumCellBytes)
        {
            throw new InvalidOperationException(
                $"a {dimA}x{dimB} cell array needs {dimA * dimB * CellBytes} bytes; the original "
                    + $"allocates {AllocationBytes} and aborts above {MaximumCellBytes} "
                    + "(image@0x1C84B).");
        }

        // image@0x1C8A0..0x1C97D — walk the list, bucket each object, push it FRONT of its cell.
        ushort cursor = listHead;
        int guard = 0;
        while (cursor != 0)
        {
            if (!arena.Covers(cursor, 0x12) || ++guard > 4096)
            {
                break;
            }

            CombatObjectView view = new CombatObjectView(arena, cursor);
            CombatPosition position = view.Position;
            ushort next = arena.Word((ushort)(cursor + 0x04));

            int a = Clamp(CellOf(position.X), dimA);
            int b = Clamp(CellOf(position.Z), dimB);
            int index = (dimA * b) + a;

            // cell_list_push_front @image@0x187AE: obj[+4] := old head, cell head := obj.
            arena.SetWord((ushort)(cursor + 0x04), cells._heads[index]);
            cells._heads[index] = cursor;
            cells.ObjectsBucketed++;

            cursor = next;
        }

        return cells;
    }

    /// <summary>
    /// <c>pool_arena_finalize_and_list_head @image@0x153D2</c> — closes the arena's list and returns
    /// its head.
    /// </summary>
    /// <param name="arena">The pool arena.</param>
    /// <param name="registers">The combat register file (the arena descriptor's home).</param>
    /// <returns>The arena list head's near offset, or 0.</returns>
    /// <remarks>
    /// <para>
    /// Three things: if the TAIL <c>[0xB14C]:[0xB14E]</c> is non-null its <c>+0x04</c> link is zeroed
    /// (<c>image@0x153DF</c> — the list gets its terminator, which is why a walk of the shipped pool
    /// terminates at all); the far heap block is shrunk to <c>cursor − base + 1</c>
    /// (<c>image@0x153F1</c>, a managed port has nothing to shrink); and the HEAD
    /// <c>[0xB13C]:[0xB13E]</c> is returned in <c>DX:AX</c>.
    /// </para>
    /// </remarks>
    public static ushort FinalizeArenaList(PoolArena arena, CombatRegisters registers)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(registers);

        ushort tail = registers.Word(ArenaListTail);
        if (tail != 0 && arena.Covers(tail, 0x06))
        {
            arena.SetWord((ushort)(tail + 0x04), 0);
        }

        return registers.Word(ArenaListHead);
    }

    /// <summary>Which cell one coordinate falls in, before clamping.</summary>
    /// <param name="positionUnits">A world-object coordinate (world units &lt;&lt; 8).</param>
    /// <remarks>
    /// <c>sar_i32_by_cl(pos, 8)</c> then a SIGNED 32-bit divide by 65,536 (<c>image@0x00770</c>,
    /// which truncates toward zero) — <c>image@0x1C8B2..0x1C8C6</c>.
    /// </remarks>
    public static int CellOf(int positionUnits) => (positionUnits >> 8) / CellSideWorldUnits;

    /// <summary>The original's clamp: below 0 → 0, at or above the dimension → dimension − 1.</summary>
    /// <param name="cell">The raw cell index.</param>
    /// <param name="dimension">The axis's cell count.</param>
    /// <remarks><c>or ax,ax / jge</c> then <c>cmp [bx+n],ax / jg</c> @<c>image@0x1C8CE..0x1C8E5</c>.</remarks>
    public static int Clamp(int cell, int dimension) =>
        cell < 0 ? 0 : (cell >= dimension ? dimension - 1 : cell);
}
