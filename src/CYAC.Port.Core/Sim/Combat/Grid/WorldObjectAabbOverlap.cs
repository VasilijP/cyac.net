namespace CYAC.Port.Core.Sim.Combat.Grid;

/// <summary>
/// The class-record fields the world-grid subsystem reads.  The pointer lives at
/// <c>s_pool_arena_entry[+0x00]</c> and is a DGROUP near offset, so the record is read through
/// <see cref="ICombatStaticData"/> exactly as the machine reads it (no <c>ES:</c> override on any of
/// these accesses — verified byte by byte).
/// </summary>
public static class WorldObjectClassRecord
{
    /// <summary>
    /// <c>+0x08/+0x0A</c> — the object's own half-extent, <c>i32</c>, used by the cheap dual-AABB
    /// pre-test (<c>mov cx,[bp+8]; mov bp,[bp+0xa]</c> <c>image@0x28494..0x28498</c>).
    /// </summary>
    public const int HalfExtentOffset = 0x08;

    /// <summary><c>+0x2E</c> — the filter word <c>pool_insert_with_bbox_or @image@0x153BF</c> ORs
    /// into the object's flag word.  It is what puts an object into one grid or the other
    /// (<c>0x0200</c> / <c>0x0204</c> / <c>0x0214</c>).</summary>
    public const int FilterWordOffset = 0x2E;

    /// <summary><c>+0x30/+0x32</c> — bounding-box X MIN, <c>i32</c>.</summary>
    public const int BoxXMinOffset = 0x30;

    /// <summary><c>+0x34/+0x36</c> — bounding-box X MAX, <c>i32</c>.</summary>
    public const int BoxXMaxOffset = 0x34;

    /// <summary><c>+0x38/+0x3A</c> — bounding-box Y MIN, <c>i32</c>.</summary>
    public const int BoxYMinOffset = 0x38;

    /// <summary><c>+0x3C/+0x3E</c> — bounding-box Y MAX, <c>i32</c>.</summary>
    public const int BoxYMaxOffset = 0x3C;

    /// <summary><c>+0x40/+0x42</c> — bounding-box Z MIN, <c>i32</c>.</summary>
    public const int BoxZMinOffset = 0x40;

    /// <summary><c>+0x44/+0x46</c> — bounding-box Z MAX, <c>i32</c>.</summary>
    public const int BoxZMaxOffset = 0x44;

    /// <summary><c>+0x48</c> — the bisect-refinement grid pointer; non-zero routes the clip through
    /// <c>world_object_frustum_clip_test</c> ENTRY1/ENTRY2 (<c>image@0x28D80</c>).</summary>
    public const int BisectGridOffset = 0x48;

    /// <summary>Reads one signed 32-bit class-record field.</summary>
    /// <param name="staticData">The DGROUP surface.</param>
    /// <param name="classRef">The class record's DGROUP near offset.</param>
    /// <param name="fieldOffset">The field's offset within the record.</param>
    /// <returns>The value.</returns>
    public static int Int32(ICombatStaticData staticData, ushort classRef, int fieldOffset)
    {
        ArgumentNullException.ThrowIfNull(staticData);
        ushort lo = staticData.Word(classRef + fieldOffset);
        ushort hi = staticData.Word(classRef + fieldOffset + 2);
        return unchecked((int)(((uint)hi << 16) | lo));
    }
}

/// <summary>
/// <c>world_grid_aabb_overlap_test @image@0x28490</c> — the cheap dual-AABB pre-reject inside
/// <c>world_object_frustum_clip_test</c> ENTRY4 (its sole caller, <c>image@0x28947</c>).
/// </summary>
/// <remarks>
/// <para>
/// Six two-bound gates, in the order X-max, X-min, Y-max, Y-min, Z-max, Z-min.  Each gate widens the
/// candidate's position on one axis by the class record's own half-extent
/// (<see cref="WorldObjectClassRecord.HalfExtentOffset"/>) as a 32-bit SUB/SBB or ADD/ADC, keeps only
/// the HIGH word, and compares it SIGNED against the corresponding bound of each of the two query
/// AABBs the query staged into <c>g_world_grid_aabb_bounds [0xF146..0xF15C]</c>.  A gate passes if
/// EITHER box's bound admits it; the first gate where BOTH miss fails the whole test (CF=0).  Only
/// the final Z-min gate can set CF=1.
/// </para>
/// <para>
/// <b>SHIPPED BUG reproduced verbatim (byte-verified, <c>39 16 48 f1</c> at both sites):</b> the
/// Z-max gate's first bound is <c>[0xF148]</c> — box 1's <b>X</b>-max — not <c>[0xF150]</c>, box 1's
/// Z-max.  <c>[0xF150]</c> is therefore the one word of the twelve that is written and never read.
/// Registered as a quirk; a "fix" would change which objects the grid returns.
/// </para>
/// </remarks>
public static class WorldObjectAabbOverlap
{
    // Indices into WorldGridState.AabbBound: (dgroupOffset - 0xF146) / 2.
    private const int Box1XMin = 0, Box1XMax = 1, Box1YMin = 2, Box1YMax = 3, Box1ZMin = 4;
    private const int Box2XMin = 6, Box2XMax = 7, Box2YMin = 8, Box2YMax = 9, Box2ZMin = 10, Box2ZMax = 11;

    /// <summary>The single word of the query AABB block that no reader ever reads (see the bug note).</summary>
    public const int UnreadBoundIndex = 5;

    /// <summary>Runs the pre-test.</summary>
    /// <param name="state">The world-grid DGROUP block.</param>
    /// <param name="arena">The object pool.</param>
    /// <param name="staticData">The DGROUP surface the class record lives in.</param>
    /// <param name="objectRef">The candidate's pool near offset.</param>
    /// <param name="classRef">
    /// The candidate's class-record near offset — the value the caller has just staged into
    /// <c>g_clip_heightmap_ptr [0x2FF6]</c>, which is what the machine reads here.
    /// </param>
    /// <returns><c>true</c> when all three axes overlap at least one query box (the original's CF=1).</returns>
    public static bool Test(
        WorldGridState state, PoolArena arena, ICombatStaticData staticData, ushort objectRef, ushort classRef)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(staticData);

        int half = WorldObjectClassRecord.Int32(staticData, classRef, WorldObjectClassRecord.HalfExtentOffset);

        int posX = ReadPosition(arena, objectRef, 0x06);
        int posY = ReadPosition(arena, objectRef, 0x0A);
        int posZ = ReadPosition(arena, objectRef, 0x0E);

        // image@0x2849B.. — gate order is fixed; each is `pos -/+ half` then a signed hi-word compare.
        if (!Gate(posX, half, isMax: true, state.AabbBound(Box1XMax), state.AabbBound(Box2XMax)))
        {
            return false;
        }

        if (!Gate(posX, half, isMax: false, state.AabbBound(Box1XMin), state.AabbBound(Box2XMin)))
        {
            return false;
        }

        if (!Gate(posY, half, isMax: true, state.AabbBound(Box1YMax), state.AabbBound(Box2YMax)))
        {
            return false;
        }

        if (!Gate(posY, half, isMax: false, state.AabbBound(Box1YMin), state.AabbBound(Box2YMin)))
        {
            return false;
        }

        // the shipped bug: box 1's X-max, not its Z-max (image@0x28507, `39 16 48 f1`).
        if (!Gate(posZ, half, isMax: true, state.AabbBound(Box1XMax), state.AabbBound(Box2ZMax)))
        {
            return false;
        }

        return Gate(posZ, half, isMax: false, state.AabbBound(Box1ZMin), state.AabbBound(Box2ZMin));
    }

    private static int ReadPosition(PoolArena arena, ushort objectRef, int offset)
    {
        ushort lo = arena.Word((ushort)(objectRef + offset));
        ushort hi = arena.Word((ushort)(objectRef + offset + 2));
        return unchecked((int)(((uint)hi << 16) | lo));
    }

    private static bool Gate(int position, int half, bool isMax, ushort bound1, ushort bound2)
    {
        // 32-bit SUB/SBB (isMax) or ADD/ADC (!isMax); only the hi word is ever compared.
        int widened = isMax ? unchecked(position - half) : unchecked(position + half);
        short hi = unchecked((short)(widened >> 16));

        bool first = isMax ? (short)bound1 >= hi : (short)bound1 <= hi;
        if (first)
        {
            return true;
        }

        return isMax ? (short)bound2 >= hi : (short)bound2 <= hi;
    }
}
