using CYAC.Port.Core.Sim.Combat.Geometry;

namespace CYAC.Port.Core.Sim.Combat.Grid;

/// <summary>
/// <c>grid_2d_tallest_obstacle_score_at (ex-grid_2d_object_proximity_score_at) @image@0x21CEB</c> — the 2-D proximity score the shot-angle
/// selector falls back on at close range.  This is C3a's <see cref="ITerrainProximity"/> seam, now
/// implemented.
/// </summary>
/// <remarks>
/// <para>
/// <b>Name note (report-only).</b> calls it a TERRAIN value and C3a inherited that.  It reads no terrain at all: it
/// scores nearby POOL OBJECTS by "how high does it stand, discounted by how far away it is", over the 2-D cell buffer
/// <c>g_grid_2d_buffer_ptr [0x1156]</c>.  The caller uses the result as a minimum shot elevation, so the role is "the
/// tallest thing in the way", not "the ground height here".
/// </para>
/// <para><b>Shape</b> (byte-verified against <c>image@0x21CEB..0x21DF3</c>):</para>
/// <list type="number">
///   <item>the cell indices are the SIGN-EXTENDED TOP BYTES of the two 32-bit coordinates —
///     <c>row = (sbyte)(x &gt;&gt; 24)</c> against <c>g_grid_2d_dim_a [0x008E]</c>,
///     <c>col = (sbyte)(z &gt;&gt; 24)</c> against <c>g_grid_2d_dim_b [0x0090]</c>.  Out of range —
///     including <b>equal</b> to the bound (<c>cmp [0x8e],di / jle</c>) — returns 0.</item>
///   <item><c>cell = buffer[(col * dimA + row) * 2]</c>; 0 returns 0.  Otherwise the word is an
///     offset into the buffer's SECONDARY region, where a <c>0</c>-terminated array of pool near
///     pointers lives (<c>grid_2d_cell_best_object_insert</c>'s append log).</item>
///   <item>per candidate: <c>height = (i16)((class[+0x3C..0x3F] + object[+0x0A..0x0D]) &gt;&gt; 8)</c>
///     — the class box's Y-MAX plus the object's own altitude.  Skip when
///     <c>height &lt;= best</c> (SIGNED, equality included).</item>
///   <item><c>dist = (i32)(manhattan2d(query, object) &gt;&gt; 8)</c>; skip when
///     <c>dist &gt;= (i16)(height * 4)</c> sign-extended (SIGNED 32-bit, equality skips).</item>
///   <item><c>candidate = height - (i16)(dist / 4)</c>; keep it when <c>candidate &gt; best</c>
///     (SIGNED, equality excluded).</item>
/// </list>
/// <para>
/// The <c>height * 4</c> is a 16-bit <c>shl ax,1 / shl ax,1</c> BEFORE the <c>cdq</c> (<c>image@0x21DA4</c>), so a
/// tall object wraps — reproduced, not fixed.
/// </para>
/// <para>
/// The buffer is a far heap segment no trace window carries, so this class needs the <see cref="IGrid2dCellBuffer"/>
/// seam.  Its own arm is never reached by any recording — an earlier pass measured the caller reaching it <b>0 times in 27,177 P6 calls</b> — so
/// the verification arms it with the tripwire and this implementation is unit-tested from the bytes only.
/// </para>
/// </remarks>
/// <param name="Buffer">The 5000-byte 2-D grid buffer.</param>
/// <param name="Arena">The object pool.</param>
/// <param name="StaticData">The DGROUP surface class records live in.</param>
/// <param name="RowCount"><c>g_grid_2d_dim_a [0x008E]</c>.</param>
/// <param name="ColumnCount"><c>g_grid_2d_dim_b [0x0090]</c>.</param>
public sealed record Grid2dProximityScore(
    IGrid2dCellBuffer Buffer,
    PoolArena Arena,
    ICombatStaticData StaticData,
    int RowCount,
    int ColumnCount) : ITerrainProximity
{
    /// <summary>A defensive bound on the secondary-region walk; the original has none.</summary>
    public const int WalkGuard = 2500;

    /// <summary>How many candidates the last call actually scored.</summary>
    public int LastNodesVisited { get; private set; }

    /// <inheritdoc/>
    public ushort ScoreAt(int x, int z)
    {
        ArgumentNullException.ThrowIfNull(Buffer);
        ArgumentNullException.ThrowIfNull(Arena);
        ArgumentNullException.ThrowIfNull(StaticData);
        LastNodesVisited = 0;

        // image@0x21CF3..0x21D13 — sign-extended TOP BYTES, exclusive upper bounds.
        int row = unchecked((sbyte)(x >> 24));
        int column = unchecked((sbyte)(z >> 24));
        if (row < 0 || RowCount <= row || column < 0 || ColumnCount <= column)
        {
            return 0;
        }

        // image@0x21D15..0x21D2E — col-major, 2-byte cells.
        int cellOffset = unchecked((ushort)(((column * RowCount) + row) << 1));
        ushort cell = Buffer.Word(cellOffset);
        if (cell == 0)
        {
            return 0;
        }

        short best = 0;
        int cursor = cell;
        int steps = 0;
        while (true)
        {
            if (++steps > WalkGuard)
            {
                throw new WorldGridSeamException(
                    "grid_2d_tallest_obstacle_score_at @image@0x21DD2 walked past " + WalkGuard
                        + " secondary entries without a terminator — the buffer is malformed.");
            }

            ushort candidate = Buffer.Word(cursor);
            if (candidate == 0)
            {
                return unchecked((ushort)best);
            }

            LastNodesVisited++;
            Score(candidate, x, z, ref best);
            cursor = unchecked((ushort)(cursor + 2));
        }
    }

    private void Score(ushort candidate, int queryX, int queryZ, ref short best)
    {
        // image@0x21D4E..0x21D6E — class box Y-max + the object's own altitude, >>8.
        ushort classRef = Arena.Word(candidate);
        int altitude = ReadInt32(candidate, 0x0A);
        int boxTop = WorldObjectClassRecord.Int32(StaticData, classRef, WorldObjectClassRecord.BoxYMaxOffset);
        short height = unchecked((short)(unchecked(boxTop + altitude) >> 8));

        if (height <= best)
        {
            return;
        }

        // image@0x21D75..0x21D9E — 2-D Manhattan distance, then >>8.
        int objX = ReadInt32(candidate, 0x06);
        int objZ = ReadInt32(candidate, 0x0E);
        int distance = Manhattan2d(objZ, objX, queryZ, queryX) >> 8;

        // image@0x21DA2..0x21DB3 — `shl ax,1; shl ax,1` is 16-BIT, then `cdq`.
        int threshold = unchecked((short)(height << 2));
        if (distance >= threshold)
        {
            return;
        }

        // image@0x21DB5..0x21DC4 — muldiv32_signed(distance, 4): the dividend is the distance
        // (the decoded C swapped the operands; the bytes' push order settles it).
        short scored = unchecked((short)(height - (short)(distance / 4)));
        if (scored > best)
        {
            best = scored;
        }
    }

    private int ReadInt32(ushort objectRef, int offset)
    {
        ushort lo = Arena.Word((ushort)(objectRef + offset));
        ushort hi = Arena.Word((ushort)(objectRef + offset + 2));
        return unchecked((int)(((uint)hi << 16) | lo));
    }

    /// <summary>
    /// <c>obj2d_manhattan_dist @image@0x2440E</c> — <c>|Δa| + |Δb|</c> over two 32-bit axis pairs.
    /// The operand pairing is the push order at <c>image@0x21D75..0x21D90</c>: the object's Z pair
    /// then its X pair, then the query's Z pair then its X pair.
    /// </summary>
    private static int Manhattan2d(int objA, int objB, int queryA, int queryB) =>
        unchecked(Abs32(objA - queryA) + Abs32(objB - queryB));

    private static int Abs32(int v) => v < 0 ? unchecked(-v) : v;
}
