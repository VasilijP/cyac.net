namespace CYAC.Port.Core.Sim.Combat.Grid;

/// <summary>
/// A typed view of the DGROUP register file the world-grid subsystem stages and reads — the
/// <c>g_world_grid_*</c> block at <c>[0x2F96..0x2FFF]</c> plus <c>[0xF136..0xF178]</c>.
/// </summary>
/// <remarks>
/// <para>
/// The whole subsystem communicates through DGROUP globals rather than arguments: the query stages
/// its thirteen stack arguments into these words and every callee below it reads them back.  The
/// port reproduces that literally — a "cleaner" argument-passing rewrite would change which values
/// a callee sees when a caller stages only some of them, and the original depends on exactly that
/// (see <see cref="WorldObjectGroundPlaneIntersect"/>, which reads <c>[0xF160..0xF176]</c> the query staged).
/// </para>
/// <para>
/// Backed by C1's <see cref="CombatRegisters"/>, the kernel's DGROUP surface, so the same object
/// carries this block and every other combat global.  A trace stage record covers all of it:
/// <c>g_world_grid_vec1_scratch [0x2F96]+73</c> and <c>world_grid_and_mission_state [0xF136]+74</c>.
/// </para>
/// </remarks>
/// <param name="Registers">The DGROUP surface.</param>
public readonly record struct WorldGridState(CombatRegisters Registers)
{
    // ---- the query's own staging area (image@0x28545..0x286BE) -------------------------------

    /// <summary><c>g_world_grid_vec1_scratch [0x2F96]</c> — the six words copied from far pointer 1.</summary>
    public const int Vec1DgroupOffset = 0x2F96;

    /// <summary><c>g_world_grid_vec2_scratch [0x2FA2]</c> — the six words copied from far pointer 2.</summary>
    public const int Vec2DgroupOffset = 0x2FA2;

    /// <summary><c>g_world_grid_query_seg [0x2FAE]</c> — the arena segment the dispatch selected.</summary>
    public const int QuerySegmentDgroupOffset = 0x2FAE;

    /// <summary><c>g_world_grid_aabb_bounds [0xF146]</c> — the two query AABBs, hi-words only.</summary>
    public const int AabbBoundsDgroupOffset = 0xF146;

    /// <summary>The observer frustum <c>[0xF160..0xF177]</c> — two packed world points, 3 × i32 each.</summary>
    public const int FrustumDgroupOffset = 0xF160;

    /// <summary>One word of <c>g_world_grid_vec1_scratch</c> (index 0..5).</summary>
    /// <param name="index">The word index.</param>
    /// <returns>The word.</returns>
    public ushort Vec1(int index) => Registers.Word(Vec1DgroupOffset + (2 * index));

    /// <summary>Sets one word of <c>g_world_grid_vec1_scratch</c>.</summary>
    /// <param name="index">The word index.</param>
    /// <param name="value">The word.</param>
    public void SetVec1(int index, ushort value) => Registers.SetWord(Vec1DgroupOffset + (2 * index), value);

    /// <summary>One word of <c>g_world_grid_vec2_scratch</c> (index 0..5).</summary>
    /// <param name="index">The word index.</param>
    /// <returns>The word.</returns>
    public ushort Vec2(int index) => Registers.Word(Vec2DgroupOffset + (2 * index));

    /// <summary>Sets one word of <c>g_world_grid_vec2_scratch</c>.</summary>
    /// <param name="index">The word index.</param>
    /// <param name="value">The word.</param>
    public void SetVec2(int index, ushort value) => Registers.SetWord(Vec2DgroupOffset + (2 * index), value);

    /// <summary><c>[0x2FAE]</c> — the grid arena segment the last dispatch resolved to.</summary>
    public ushort QuerySegment
    {
        get => Registers.Word(QuerySegmentDgroupOffset);
        set => Registers.SetWord(QuerySegmentDgroupOffset, value);
    }

    /// <summary><c>g_world_grid_lo_seg [0xF136]</c>.</summary>
    public ushort LoArenaSegment
    {
        get => Registers.Word(0xF136);
        set => Registers.SetWord(0xF136, value);
    }

    /// <summary><c>g_world_grid_hi_seg [0xF138]</c>.</summary>
    public ushort HiArenaSegment
    {
        get => Registers.Word(0xF138);
        set => Registers.SetWord(0xF138, value);
    }

    /// <summary><c>g_world_grid_build_segment (ex-g_world_grid_alloc_ptr_lo) [0xF13A]</c> — the arena being built.</summary>
    public ushort BuildSegment
    {
        get => Registers.Word(0xF13A);
        set => Registers.SetWord(0xF13A, value);
    }

    /// <summary><c>g_world_grid_build_cursor (ex-g_world_grid_alloc_ptr_hi) [0xF13C]</c> — the build's bump cursor.</summary>
    public ushort BuildCursor
    {
        get => Registers.Word(0xF13C);
        set => Registers.SetWord(0xF13C, value);
    }

    /// <summary><c>g_world_grid_routing_mask [0xF13E]</c> — the argument ORed with 1
    /// (<c>or ax,1</c> <c>image@0x2857C</c>).</summary>
    public ushort RoutingMask
    {
        get => Registers.Word(0xF13E);
        set => Registers.SetWord(0xF13E, value);
    }

    /// <summary><c>g_world_grid_halfrange_lo [0xF140]</c>.</summary>
    public ushort HalfRangeLo
    {
        get => Registers.Word(0xF140);
        set => Registers.SetWord(0xF140, value);
    }

    /// <summary><c>g_world_grid_halfrange_hi [0xF142]</c>.</summary>
    public ushort HalfRangeHi
    {
        get => Registers.Word(0xF142);
        set => Registers.SetWord(0xF142, value);
    }

    /// <summary>The half-range as one signed 32-bit value.</summary>
    public int HalfRange => unchecked((int)(((uint)HalfRangeHi << 16) | HalfRangeLo));

    /// <summary><c>g_world_grid_exclude_nearptr [0xF144]</c>.</summary>
    public ushort ExcludeRef
    {
        get => Registers.Word(0xF144);
        set => Registers.SetWord(0xF144, value);
    }

    /// <summary>One word of the query AABB block (index 0..11).</summary>
    /// <param name="index">The word index.</param>
    /// <returns>The bound.</returns>
    public ushort AabbBound(int index) => Registers.Word(AabbBoundsDgroupOffset + (2 * index));

    /// <summary>Sets one word of the query AABB block.</summary>
    /// <param name="index">The word index.</param>
    /// <param name="value">The bound.</param>
    public void SetAabbBound(int index, ushort value) =>
        Registers.SetWord(AabbBoundsDgroupOffset + (2 * index), value);

    /// <summary><c>g_world_grid_output_ptr [0xF15E]</c> — the six-word output block's near pointer.</summary>
    public ushort OutputPtr
    {
        get => Registers.Word(0xF15E);
        set => Registers.SetWord(0xF15E, value);
    }

    /// <summary>One word of the observer frustum block (index 0..11).</summary>
    /// <param name="index">The word index.</param>
    /// <returns>The word.</returns>
    public ushort Frustum(int index) => Registers.Word(FrustumDgroupOffset + (2 * index));

    /// <summary>Sets one word of the observer frustum block.</summary>
    /// <param name="index">The word index.</param>
    /// <param name="value">The word.</param>
    public void SetFrustum(int index, ushort value) =>
        Registers.SetWord(FrustumDgroupOffset + (2 * index), value);

    /// <summary><c>[0xF178]</c> — the visibility-mode byte; non-zero relaxes the engage-capable gates.</summary>
    public byte VisMode
    {
        get => Registers.Byte(0xF178);
        set => Registers.SetByte(0xF178, value);
    }

    // ---- the frustum-clip working set (image@0x28935..0x28E55) -------------------------------

    /// <summary><c>[0x2FB0]</c> — the candidate's own position, <c>&gt;&gt;8</c>-scaled, 3 × i32.</summary>
    public const int ClipObjectPositionDgroupOffset = 0x2FB0;

    /// <summary><c>[0x2FBC]</c> — the working P1/P2 coordinates, 6 × i16.</summary>
    public const int ClipPointsDgroupOffset = 0x2FBC;

    /// <summary><c>[0x2FC8]</c> — the six clip-plane constants, i16.</summary>
    public const int ClipPlanesDgroupOffset = 0x2FC8;

    /// <summary><c>[0x2FD4]</c>/<c>[0x2FD5]</c> — the P1/P2 Cohen-Sutherland outcodes.</summary>
    public const int ClipOutcodeDgroupOffset = 0x2FD4;

    /// <summary><c>[0x2FD6]</c> — the clip iteration counter, initialised to 15.</summary>
    public const int ClipCounterDgroupOffset = 0x2FD6;

    /// <summary><c>[0x2FD7]</c> — the rotation code the clip applied, replayed in reverse afterwards.</summary>
    public const int ClipRotCodeDgroupOffset = 0x2FD7;

    /// <summary><c>[0x2FDD]</c> — the "projection reached" byte the clip zeroes and later re-reads.</summary>
    public const int ClipReachFlagDgroupOffset = 0x2FDD;

    /// <summary><c>[0x2FDE..0x2FED]</c> — <c>world_object_ground_plane_intersect (ex-world_object_range_project)</c>'s eight scratch words.</summary>
    public const int RangeScratchDgroupOffset = 0x2FDE;

    /// <summary><c>[0x2FF2]</c>/<c>[0x2FF4]</c> — the candidate's near offset and pool segment.</summary>
    public const int ClipObjectPtrDgroupOffset = 0x2FF2;

    /// <summary><c>g_clip_heightmap_ptr [0x2FF6]</c> — the candidate's class-record near pointer.</summary>
    public const int ClipDescriptorPtrDgroupOffset = 0x2FF6;

    // ---- the globals the walkers read but nobody stages ---------------------------------------

    /// <summary><c>g_object_pool_segment [0x0094]</c>.</summary>
    public ushort PoolSegment => Registers.Word(0x0094);

    /// <summary><c>g_scene_render_list_head [0x0096]</c> — <c>find_sub</c>'s start pointer.</summary>
    public ushort SceneListHead => Registers.Word(0x0096);

    /// <summary><c>g_active_target_count [0xEE02]</c>.</summary>
    public ushort ActiveTargetCount => Registers.Word(0xEE02);

    /// <summary><c>g_active_target_table [0xEDE6]</c> — the first VALID entry (P681: <c>[0xEDE4]</c>
    /// is never read; the scan stops at <c>cmp si,0xEDE6 / jae</c> <c>image@0x2878B</c>).</summary>
    public const int ActiveTargetTableDgroupOffset = 0xEDE6;

    public const int ActiveTargetTableCapacity = 14;

    /// <summary>One entry of the active-target table.</summary>
    /// <param name="index">0-based index into the <c>u16[14]</c>.</param>
    /// <returns>The entry.</returns>
    public ushort ActiveTarget(int index) => Registers.Word(ActiveTargetTableDgroupOffset + (2 * index));
}
