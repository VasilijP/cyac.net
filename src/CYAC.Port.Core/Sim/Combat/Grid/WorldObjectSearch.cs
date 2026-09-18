using CYAC.Port.Core.Model.World;

namespace CYAC.Port.Core.Sim.Combat.Grid;

/// <summary>
/// The two candidate walkers of the world-grid query —
/// <c>world_object_list_find_first_match (ex-world_object_list_find_nearest) @image@0x28F6A</c> (the LEAF ARRAY walker) and
/// <c>world_object_list_find_sub @image@0x2902A</c> (the LINKED-LIST walker, which also recurses).
/// </summary>
/// <remarks>
/// <para>
/// <b>Name correction (report-only).</b> Neither routine computes a distance or a minimum: each returns the FIRST
/// candidate that survives a four-stage filter, in list order.  "nearest" is a misnomer — the geometry is entirely in
/// the frustum/segment test.  The two differ only in HOW they enumerate candidates:
/// </para>
/// <list type="bullet">
///   <item>
///     <b>find_nearest</b> takes a FAR pointer <c>{off = leaf, seg = [0x2FAE]}</c>, adds 1 to skip
///     the leaf's tag byte, and reads consecutive <c>u16</c> object near-offsets out of the GRID
///     ARENA until a zero terminator (<c>les bp,[bp+4]; add bp,1</c> <c>image@0x28F6F</c>, then
///     <c>mov si,es:[bp]; add bp,2</c>).
///   </item>
///   <item>
///     <b>find_sub</b> takes a NEAR pointer into the object pool and follows
///     <c>s_pool_arena_entry[+0x04]</c> until it is zero.  The query calls it with
///     <c>g_scene_render_list_head [0x0096]</c>; both walkers call it recursively with a candidate's
///     <c>[+0x18]</c> first-child pointer whenever <see cref="WorldObjectFlags.HasChildren"/> is set.
///   </item>
/// </list>
/// <para><b>The shared filter chain</b> (<c>image@0x28F8F..0x2901E</c> and its byte-identical twin at
/// <c>image@0x29047..0x290D2</c>), in order:</para>
/// <list type="number">
///   <item>routing mask: <c>(flags &amp; [0xF13E]) == [0xF13E]</c> — ALL the mask's bits, and the
///     mask always carries <see cref="WorldObjectFlags.Active"/> because the query ORs 1 into it.</item>
///   <item>exclusion: the candidate is not <c>g_world_grid_exclude_nearptr [0xF144]</c>.</item>
///   <item>PRE-frustum engage-capable gate, only when <c>[0xF178] == 0</c> AND
///     <c>flags &amp; 0x0820 == 0x0800</c> (carries an engagement block, no children): reject when
///     the engagement block's own prototype has <c>[+0x0C] &amp; 8</c> set.</item>
///   <item>the segment/bounding-box hit test (<see cref="WorldObjectFrustumClip"/>).</item>
/// </list>
/// <para>
/// Survivors then branch: with children set, the walk RECURSES into the child list and returns the
/// child it finds (never the parent); without, a POST-frustum repeat of gate 3 decides.  Gate 3's
/// second copy is redundant whenever gate 3's first copy already ran — the port reproduces both
/// because they are two separate instruction sequences and a future recording could reach the second
/// through a path the first does not cover.
/// </para>
/// </remarks>
public static class WorldObjectSearch
{
    /// <summary>A defensive bound on one walk; the original has none.</summary>
    public const int WalkGuard = 4096;

    /// <summary>A defensive bound on child recursion; the original has none.</summary>
    public const int RecursionGuard = 32;

    /// <summary>
    /// <c>world_object_list_find_first_match @image@0x28F6A</c> — walks a grid leaf's object array.
    /// </summary>
    /// <param name="context">The query context.</param>
    /// <param name="arenaIndex">The grid arena the leaf lives in.</param>
    /// <param name="leaf">The leaf record's near offset within that arena.</param>
    /// <returns>The selected object's pool near offset, or 0.</returns>
    public static ushort FindFirstMatch(WorldGridQueryContext context, WorldGridArena arenaIndex, ushort leaf)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(arenaIndex);

        int cursor = leaf + 1;
        int steps = 0;
        while (true)
        {
            if (++steps > WalkGuard)
            {
                throw new WorldGridSeamException(
                    "world_object_list_find_first_match @image@0x28F7D walked past " + WalkGuard
                        + " leaf entries without a terminator — the arena is malformed.");
            }

            ushort candidate = arenaIndex.Word(cursor);
            cursor += 2;
            if (candidate == 0)
            {
                return 0;
            }

            ushort selected = Consider(context, candidate, 0);
            if (selected != 0)
            {
                return selected;
            }
        }
    }

    /// <summary>
    /// <c>world_object_list_find_sub @image@0x2902A</c> — walks a <c>[+0x04]</c>-threaded object list.
    /// </summary>
    /// <param name="context">The query context.</param>
    /// <param name="head">The first object's pool near offset.</param>
    /// <returns>The selected object's pool near offset, or 0.</returns>
    public static ushort FindSub(WorldGridQueryContext context, ushort head) => FindSub(context, head, 0);

    private static ushort FindSub(WorldGridQueryContext context, ushort head, int depth)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (depth > RecursionGuard)
        {
            throw new WorldGridSeamException(
                "world_object_list_find_sub @image@0x2909B recursed past " + RecursionGuard
                    + " levels — the object pool's [+0x18] child chain is cyclic.");
        }

        ushort candidate = head;
        int steps = 0;
        while (candidate != 0)
        {
            if (++steps > WalkGuard)
            {
                throw new WorldGridSeamException(
                    "world_object_list_find_sub @image@0x29038 walked past " + WalkGuard
                        + " list nodes — the object pool's [+0x04] chain is cyclic.");
            }

            ushort selected = Consider(context, candidate, depth);
            if (selected != 0)
            {
                return selected;
            }

            candidate = context.Arena.Word((ushort)(candidate + 0x04));
        }

        return 0;
    }

    /// <summary>The filter chain both walkers share; returns the accepted near offset or 0.</summary>
    private static ushort Consider(WorldGridQueryContext context, ushort candidate, int depth)
    {
        WorldGridState state = context.State;
        PoolArena arena = context.Arena;
        WorldGridCounters counters = context.Counters;

        WorldObjectFlags flags = (WorldObjectFlags)arena.Word((ushort)(candidate + 0x02));
        ushort mask = state.RoutingMask;

        // (1) routing mask — ALL the mask's bits must be present (image@0x28F99).
        if (((ushort)flags & mask) != mask)
        {
            counters.RejectMask++;
            return 0;
        }

        // (2) exclusion (image@0x28FA3).
        if (candidate == state.ExcludeRef)
        {
            counters.RejectExcluded++;
            return 0;
        }

        // (3) PRE-frustum engage-capable gate (image@0x28FA9..0x28FD0).
        if (state.VisMode == 0 && ((ushort)flags & 0x0820) == 0x0800)
        {
            if (EngageCapable(context, candidate, flags))
            {
                counters.RejectEngageCapablePre++;
                return 0;
            }
        }

        // (4) the segment/bounding-box hit test (image@0x28FD4).
        if (!WorldObjectFrustumClip.Test(state, arena, context.StaticData, candidate, context.Output, counters))
        {
            return 0;
        }

        // Survivor with children: recurse and return the CHILD, never the parent (image@0x28FDF).
        if (flags.HasFlag(WorldObjectFlags.HasChildren))
        {
            counters.ChildRecursion++;
            return FindSub(context, arena.Word((ushort)(candidate + 0x18)), depth + 1);
        }

        // POST-frustum repeat of gate 3 (image@0x28FF0..0x2901A).
        if (state.VisMode != 0)
        {
            return candidate;
        }

        if (!flags.HasFlag(WorldObjectFlags.CarriesEngagement))
        {
            return candidate;
        }

        if (!EngageCapable(context, candidate, flags))
        {
            return candidate;
        }

        counters.RejectEngageCapablePost++;
        return 0;
    }

    /// <summary>
    /// The <c>[+0x0C] &amp; 8</c> test both gates perform: read the candidate's engagement-block
    /// near pointer (<c>+0x12</c> when <see cref="WorldObjectFlags.NoOrientation"/> is set, else
    /// <c>+0x18</c> — <c>object_pool_get_engagement_fieldoff @image@0x0225A</c>'s own rule), then
    /// test bit 3 of the DGROUP word at <c>that pointer + 0x0C</c>.
    /// </summary>
    /// <remarks>
    /// The block's first word is the class PROTOTYPE near pointer, and the machine reads
    /// <c>[+0x0C]</c> with NO <c>ES:</c> override, so the read is DGROUP — the same bit
    /// <c>object_is_engaged_check @image@0x23FBE</c> tests.
    /// </remarks>
    private static bool EngageCapable(WorldGridQueryContext context, ushort candidate, WorldObjectFlags flags)
    {
        int fieldOffset = flags.HasFlag(WorldObjectFlags.NoOrientation) ? 0x12 : 0x18;
        ushort block = context.Arena.Word((ushort)(candidate + fieldOffset));
        return (context.StaticData.Word(block + 0x0C) & 0x08) != 0;
    }
}
