namespace CYAC.Port.Core.Sim.Combat.Grid;

/// <summary>
/// The thirteen word arguments of <c>world_grid_frustum_query_and_select @image@0x28540</c>, named
/// by role.  Field order is the order the original PUSHES them, which is the reverse of the callee's
/// stack order.
/// </summary>
/// <param name="ExcludeRef"><c>[bp+0x1E]</c> → <c>g_world_grid_exclude_nearptr [0xF144]</c>.</param>
/// <param name="Vector1">
/// <c>[bp+0x1A]/[bp+0x1C]</c> — a FAR pointer to six words copied into
/// <c>g_world_grid_vec1_scratch [0x2F96]</c>.  Three <c>i32</c>: the segment's first end point.
/// </param>
/// <param name="Vector2">
/// <c>[bp+0x16]/[bp+0x18]</c> — the same for <c>g_world_grid_vec2_scratch [0x2FA2]</c>: the second
/// end point.
/// </param>
/// <param name="Vector2SourceOffset">
/// The near half of that second far pointer.  Only the D1 fall-through arm ever observes it (the
/// prologue's <c>rep movsw</c> leaves <c>SI</c> at <c>offset + 12</c>, and that arm uses <c>SI</c>
/// as a grid-arena leaf offset) — see <see cref="WorldGridQuery"/>.
/// </param>
/// <param name="HalfRangeLo"><c>[bp+0x12]</c> → <c>[0xF140]</c>.</param>
/// <param name="HalfRangeHi"><c>[bp+0x14]</c> → <c>[0xF142]</c>.</param>
/// <param name="OutputPtr"><c>[bp+0x10]</c> → <c>g_world_grid_output_ptr [0xF15E]</c>; 0 disables
/// every writeback.</param>
/// <param name="RoutingMask"><c>[bp+0xE]</c>; the query ORs 1 into it before storing
/// (<c>or ax,1</c> <c>image@0x2857C</c>), so <c>0x1000</c> becomes <c>0x1001</c> and <c>0x0100</c>
/// becomes <c>0x0101</c> — the two values the dispatch tests.</param>
/// <param name="VisMode"><c>[bp+0xC]</c>, LOW BYTE only → <c>[0xF178]</c>.</param>
/// <param name="RangeGate"><c>[bp+0xA]</c>, byte-tested: runs
/// <see cref="WorldObjectGroundPlaneIntersect"/>.</param>
/// <param name="SubGate"><c>[bp+8]</c>, byte-tested: runs
/// <see cref="WorldObjectSearch.FindSub(WorldGridQueryContext, ushort)"/> over the scene list.</param>
/// <param name="LoopGate"><c>[bp+6]</c>, byte-tested: runs the active-target backward scan.</param>
public readonly record struct WorldGridQueryRequest(
    ushort ExcludeRef,
    ushort[] Vector1,
    ushort[] Vector2,
    ushort Vector2SourceOffset,
    ushort HalfRangeLo,
    ushort HalfRangeHi,
    ushort OutputPtr,
    ushort RoutingMask,
    byte VisMode,
    byte RangeGate,
    byte SubGate,
    byte LoopGate);

/// <summary>Everything one world-grid query reads and writes.</summary>
public sealed class WorldGridQueryContext
{
    /// <summary>The DGROUP block the whole subsystem communicates through.</summary>
    public required WorldGridState State { get; init; }

    /// <summary>The object pool — the segment <c>g_object_pool_segment [0x0094]</c> names.</summary>
    public required PoolArena Arena { get; init; }

    /// <summary>The DGROUP surface class records and engagement prototypes are read from.</summary>
    public required ICombatStaticData StaticData { get; init; }

    /// <summary>The two grid arenas.</summary>
    public IWorldGridIndex Index { get; init; } = UnavailableWorldGridIndex.Instance;

    /// <summary>The caller's six-word output block, or null when it passes a null pointer.</summary>
    public WorldGridOutputBlock? Output { get; init; }

    /// <summary>The arm census.</summary>
    public WorldGridCounters Counters { get; init; } = new();
}

/// <summary>
/// <c>world_grid_frustum_query_and_select @image@0x28540</c> together with its no-prologue
/// fall-through tail <c>world_grid_aabb_test @image@0x286C2</c> — one function with one
/// <c>retf 0x1A</c> exit at <c>image@0x287A6</c>.
/// </summary>
/// <remarks>
/// <para>
/// The spatial ORACLE of the combat engine: five call sites ask it "what does this segment hit?".
/// It stages its thirteen arguments into DGROUP, precomputes two query AABBs, descends the quadtree
/// arena its routing mask selects, and runs up to five candidate sources in a fixed order.  The
/// return value is <b>the caller's whole answer</b>: an object near offset, <c>0xFFFF</c> for
/// "blocked by the ground", or 0 for nothing.
/// </para>
/// <para><b>Argument staging</b> (<c>image@0x28545..0x286BE</c>), all unconditional:</para>
/// <list type="number">
///   <item><c>[0xF144]</c>, the two 12-byte vector copies into <c>[0x2F96]</c>/<c>[0x2FA2]</c>,
///     <c>[0xF140/2]</c>, <c>[0xF15E]</c>, <c>[0xF13E] = arg | 1</c>, <c>[0xF178]</c>.</item>
///   <item>six <b>point packs</b> into <c>[0xF160..0xF176]</c>: each axis is
///     <c>(i32)raw &gt;&gt; 8</c>, done with a byte shuffle plus <c>CBW</c>.</item>
///   <item>twelve <b>AABB blocks</b> into <c>[0xF146..0xF15C]</c>: <c>axis ∓ halfrange</c> as a
///     32-bit SUB/SBB or ADD/ADC of which <b>only the high word is stored</b> — the low word is
///     genuinely discarded (<c>sub ax,bx; sbb dx,cx; mov [dest],dx</c>).</item>
/// </list>
/// <para><b>Grid dispatch</b> (<c>image@0x286C2..0x2872E</c>): <c>[0xF13E] == 0x1001</c> selects
/// <c>g_world_grid_lo_seg [0xF136]</c>, <c>== 0x0101</c> selects <c>g_world_grid_hi_seg [0xF138]</c>,
/// and <b>anything else falls straight through to the shared tail</b> — see
/// <see cref="RunNeitherArm"/>.  Each live branch runs the walker twice, with
/// <c>(cx,dx) = ([0x2F98],[0x2FA0])</c> then <c>([0x2FA4],[0x2FAC])</c>: the HIGH words of each end
/// point's axis 0 and axis 2.</para>
/// <para><b>Candidate sources</b> (<c>image@0x2872E..0x287A6</c>), first non-zero wins:</para>
/// <list type="number">
///   <item><see cref="WorldObjectSearch.FindFirstMatch"/> on the first leaf — ALWAYS.</item>
///   <item>the same on the second leaf — only when the two leaves differ.</item>
///   <item><see cref="WorldObjectSearch.FindSub"/> from <c>g_scene_render_list_head [0x0096]</c> —
///     gated by <c>[bp+8]</c>.</item>
///   <item>a BACKWARD scan of <c>g_active_target_table [0xEDE6]</c> (count <c>[0xEE02]</c>), calling
///     the clip test directly per non-null entry — gated by <c>[bp+6]</c>.  <c>[0xEDE4]</c>
///     is never read; the loop stops at <c>cmp si,0xEDE6 / jae</c>.</item>
///   <item><see cref="WorldObjectGroundPlaneIntersect"/> — gated by <c>[bp+0xA]</c>; "blocked" yields
///     <c>0xFFFF</c>.</item>
/// </list>
/// </remarks>
public static class WorldGridQuery
{
    /// <summary>The routing mask that selects the LO arena, after the query's own <c>or ax,1</c>.</summary>
    public const ushort LoArenaMask = 0x1001;

    /// <summary>The routing mask that selects the HI arena, after the query's own <c>or ax,1</c>.</summary>
    public const ushort HiArenaMask = 0x0101;

    /// <summary>The sentinel the query returns when the ground plane blocks the segment.</summary>
    public const ushort BlockedSentinel = 0xFFFF;

    /// <summary>Runs one query.</summary>
    /// <param name="context">Everything the query reads and writes.</param>
    /// <param name="request">The thirteen arguments.</param>
    /// <returns>The original's <c>AX</c>: an object near offset, <see cref="BlockedSentinel"/>, or 0.</returns>
    public static ushort Run(WorldGridQueryContext context, in WorldGridQueryRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request.Vector1);
        ArgumentNullException.ThrowIfNull(request.Vector2);

        WorldGridState state = context.State;
        WorldGridCounters counters = context.Counters;

        Stage(state, request);
        if (context.Output is not null)
        {
            context.Output.Enabled = request.OutputPtr != 0;
        }

        (WorldGridArena arena, ushort firstLeaf, ushort secondLeaf) = Dispatch(context, request);

        // ---- source 1: the first leaf (image@0x2872E) -----------------------------------------
        ushort selected = WorldObjectSearch.FindFirstMatch(context, arena, firstLeaf);
        if (selected != 0)
        {
            counters.SelectedObject++;
            return selected;
        }

        // ---- source 2: the second leaf, only when it differs (image@0x2873A) -------------------
        if (firstLeaf != secondLeaf)
        {
            counters.SecondLeafSearch++;
            selected = WorldObjectSearch.FindFirstMatch(context, arena, secondLeaf);
            if (selected != 0)
            {
                counters.SelectedObject++;
                return selected;
            }
        }

        // ---- source 3: the scene list (image@0x2874A) ------------------------------------------
        if (request.SubGate != 0)
        {
            counters.SubListSearch++;
            selected = WorldObjectSearch.FindSub(context, state.SceneListHead);
            if (selected != 0)
            {
                counters.SelectedObject++;
                return selected;
            }
        }

        // ---- source 4: the active-target backward scan (image@0x2875B) -------------------------
        if (request.LoopGate != 0)
        {
            ushort hit = ScanActiveTargets(context);
            if (hit != 0)
            {
                counters.SelectedObject++;
                return hit;
            }
        }

        // ---- source 5: the ground-plane projector (image@0x28791) ------------------------------
        if (request.RangeGate != 0)
        {
            counters.GroundPlaneIntersect++;
            if (WorldObjectGroundPlaneIntersect.Run(state, context.Output, counters) !=
                WorldObjectGroundPlaneIntersect.Outcome.NotBlocked)
            {
                counters.RangeBlocked++;
                return BlockedSentinel;
            }
        }

        counters.SelectedNothing++;
        return 0;
    }

    /// <summary>Stages the thirteen arguments into DGROUP, exactly as <c>image@0x28545..0x286BE</c> does.</summary>
    /// <param name="state">The world-grid DGROUP block.</param>
    /// <param name="request">The arguments.</param>
    public static void Stage(WorldGridState state, in WorldGridQueryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Vector1);
        ArgumentNullException.ThrowIfNull(request.Vector2);

        state.ExcludeRef = request.ExcludeRef;
        for (int w = 0; w < 6; w++)
        {
            state.SetVec1(w, request.Vector1[w]);
            state.SetVec2(w, request.Vector2[w]);
        }

        state.HalfRangeLo = request.HalfRangeLo;
        state.HalfRangeHi = request.HalfRangeHi;
        state.OutputPtr = request.OutputPtr;
        state.RoutingMask = (ushort)(request.RoutingMask | 1);
        state.VisMode = request.VisMode;

        // Six point packs: (i32)raw >> 8, stored as a 32-bit pair (image@0x28588..0x28606).
        for (int axis = 0; axis < 3; axis++)
        {
            int p1 = Combine(request.Vector1[2 * axis], request.Vector1[(2 * axis) + 1]) >> 8;
            state.SetFrustum(2 * axis, unchecked((ushort)p1));
            state.SetFrustum((2 * axis) + 1, unchecked((ushort)(p1 >> 16)));

            int p2 = Combine(request.Vector2[2 * axis], request.Vector2[(2 * axis) + 1]) >> 8;
            state.SetFrustum(6 + (2 * axis), unchecked((ushort)p2));
            state.SetFrustum(6 + (2 * axis) + 1, unchecked((ushort)(p2 >> 16)));
        }

        // Twelve AABB blocks, HIGH WORD ONLY (image@0x28606..0x286C1).
        int half = Combine(request.HalfRangeLo, request.HalfRangeHi);
        for (int axis = 0; axis < 3; axis++)
        {
            int v1 = Combine(request.Vector1[2 * axis], request.Vector1[(2 * axis) + 1]);
            state.SetAabbBound(2 * axis, HighWord(unchecked(v1 - half)));
            state.SetAabbBound((2 * axis) + 1, HighWord(unchecked(v1 + half)));

            int v2 = Combine(request.Vector2[2 * axis], request.Vector2[(2 * axis) + 1]);
            state.SetAabbBound(6 + (2 * axis), HighWord(unchecked(v2 - half)));
            state.SetAabbBound(6 + (2 * axis) + 1, HighWord(unchecked(v2 + half)));
        }
    }

    private static (WorldGridArena Arena, ushort First, ushort Second) Dispatch(
        WorldGridQueryContext context, in WorldGridQueryRequest request)
    {
        WorldGridState state = context.State;
        WorldGridCounters counters = context.Counters;
        ushort mask = state.RoutingMask;

        if (mask == LoArenaMask)
        {
            counters.DispatchLoArena++;
            WorldGridArena arena = Require(context.Index.Lo, "g_world_grid_lo_seg [0xF136]");
            ushort first = WorldGridWalker.FindLeaf(arena, state.Vec1(1), state.Vec1(5));
            ushort second = WorldGridWalker.FindLeaf(arena, state.Vec2(1), state.Vec2(5));
            state.QuerySegment = state.LoArenaSegment;
            return (arena, first, second);
        }

        if (mask == HiArenaMask)
        {
            counters.DispatchHiArena++;
            WorldGridArena arena = Require(context.Index.Hi, "g_world_grid_hi_seg [0xF138]");
            ushort first = WorldGridWalker.FindLeaf(arena, state.Vec1(1), state.Vec1(5));
            ushort second = WorldGridWalker.FindLeaf(arena, state.Vec2(1), state.Vec2(5));
            state.QuerySegment = state.HiArenaSegment;
            return (arena, first, second);
        }

        counters.DispatchNeither++;
        return RunNeitherArm(context, request);
    }

    /// <summary>
    /// The D1 "neither mask" arm — <c>jne 0x2872E</c> at <c>image@0x286FE</c>, which the verified
    /// emulator lift DECLINED and this port implements.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The jump lands on the SHARED TAIL, so nothing sets up the walk at all: the walker never runs,
    /// <c>g_world_grid_query_seg [0x2FAE]</c> keeps whatever the PREVIOUS query left in it, and
    /// <c>SI</c>/<c>DI</c> — which the tail uses as the two "leaf" offsets — hold the residue of the
    /// prologue's own two <c>rep movsw</c> copies (byte-verified: nothing between
    /// <c>image@0x28563</c> and <c>image@0x286FE</c> touches either register).  That makes
    /// <c>SI = (the second far pointer's offset) + 12</c> and <c>DI = 0x2FAE</c> — the copy's own
    /// destination cursor, one word past <c>[0x2FA2]+12</c>.
    /// </para>
    /// <para>
    /// So the arm walks the PREVIOUS query's arena at two arbitrary offsets and treats whatever it
    /// finds as null-terminated arrays of object near pointers.  It is not a "no result" arm: it is
    /// a genuine read of stale structure, and it can return an object.  The emulator lift measured
    /// <b>0 of 145,684</b> calls reaching it (2,463 + 143,221 = 145,684 exactly), and the six v1.4
    /// reference windows plus their pool variants show 100,373 P20 calls all taking a live branch,
    /// so this arm is never reached by any recording — but it is reachable by construction and the port implements it
    /// rather than declining.
    /// </para>
    /// </remarks>
    /// <param name="context">The query context.</param>
    /// <param name="request">The arguments.</param>
    /// <returns>The arena and the two stale "leaf" offsets.</returns>
    public static (WorldGridArena Arena, ushort First, ushort Second) RunNeitherArm(
        WorldGridQueryContext context, in WorldGridQueryRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        WorldGridState state = context.State;

        ushort stale = state.QuerySegment;
        WorldGridArena? arena =
            stale != 0 && stale == state.LoArenaSegment ? context.Index.Lo
            : stale != 0 && stale == state.HiArenaSegment ? context.Index.Hi
            : null;

        if (arena is null)
        {
            throw new WorldGridSeamException(
                "world_grid_frustum_query_and_select @image@0x286FE took the D1 'neither mask' arm "
                    + $"(routing mask 0x{state.RoutingMask:X4}) and the stale g_world_grid_query_seg "
                    + $"[0x2FAE] = 0x{stale:X4} names neither arena, so the port cannot say which "
                    + "bytes the machine would have walked.  This arm fired 0 times in 145,684 lift "
                    + "calls and 0 of 100,373 traced P20 calls — report the recording that reached it.");
        }

        // SI after the prologue's second `rep movsw`; DI is the copy's own destination cursor.
        ushort si = unchecked((ushort)(request.Vector2SourceOffset + 12));
        const ushort di = WorldGridState.QuerySegmentDgroupOffset;
        return (arena, si, di);
    }

    private static ushort ScanActiveTargets(WorldGridQueryContext context)
    {
        WorldGridState state = context.State;
        WorldGridCounters counters = context.Counters;

        ushort count = state.ActiveTargetCount;
        if (count == 0)
        {
            return 0;
        }

        if (count > WorldGridState.ActiveTargetTableCapacity)
        {
            throw new WorldGridSeamException(
                $"world_grid_frustum_query_and_select @image@0x28761: g_active_target_count [0xEE02] "
                    + $"is {count}, past g_active_target_table's own declared u16[14] capacity — the "
                    + "machine would read off the end of the array.");
        }

        counters.EngagementScan++;
        for (int index = count - 1; index >= 0; index--)
        {
            counters.EngagementScanIterations++;
            ushort entry = state.ActiveTarget(index);
            if (entry == 0)
            {
                continue;
            }

            ushort objectRef = context.Arena.Word((ushort)(entry + 0x02));
            if (WorldObjectFrustumClip.Test(
                    state, context.Arena, context.StaticData, objectRef, context.Output, counters))
            {
                counters.EngagementScanHit++;
                return objectRef;
            }
        }

        return 0;
    }

    private static WorldGridArena Require(WorldGridArena? arena, string which) =>
        arena ?? throw new WorldGridSeamException(
            $"world_grid_frustum_query_and_select @image@0x28540 dispatched to {which}, but the "
                + "context has no arena for it.");

    private static int Combine(ushort lo, ushort hi) => unchecked((int)(((uint)hi << 16) | lo));

    private static ushort HighWord(int value) => unchecked((ushort)(value >> 16));
}
