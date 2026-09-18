using CYAC.Port.Core.Primitives;
using CYAC.Port.Core.Schema;

namespace CYAC.Port.Core.Model.World;

/// <summary>
/// The scene's object pool: the flat set of live <see cref="WorldObject"/>s plus the parent/child
/// tree the renderer and the spatial walkers traverse.
/// </summary>
/// <remarks>
/// <para>
/// The original is <b>arena #4</b>: a bump heap of <c>s_pool_arena_entry</c> records, described by
/// <c>s_pool_arena_descriptor</c> at <c>g_mesh_pool_arena_descriptor [0xB13C]</c>, living in
/// <c>g_mesh_arena_workspace_seg [0xE840]</c>. The port keeps the arena's <b>semantic</b> constraints
/// — the byte budget, the derived entry ceilings, the filter-word OR at insert, and the
/// abort-on-overflow — but not its allocator: there are no arena offsets in this type, and objects are
/// never recycled in place.
/// </para>
/// <para>
/// The <c>s_pool_arena_descriptor</c> head/tail/base/end/cursor word pairs are deliberately not
/// modelled; they describe a 16-bit bump allocator, which the port replaces outright.  They stay
/// reachable through the schema (<c>StateSchema.Embedded.Struct("s_pool_arena_descriptor")</c>) for
/// any comparator that needs to map port state back onto original memory.
/// </para>
/// </remarks>
[OriginalGlobal("g_mesh_pool_arena_descriptor")]
[OriginalGlobal("g_mesh_arena_workspace_seg")]
public sealed class WorldObjectPool
{
    /// <summary>
    /// Arena size for a normal gameplay session: 28,000 B.
    /// </summary>
    /// <remarks>
    /// <c>session_init_dispatcher_a</c> passes <c>0x6D60</c> at
    /// <c>image@0x1015A</c>.
    /// </remarks>
    public const int ArenaBytesGameplay = 28_000;

    /// <summary>
    /// Arena size for a film-review session: 40,000 B.
    /// </summary>
    /// <remarks>
    /// <c>session_init_dispatcher_b</c> passes <c>0x9C40</c> at <c>image@0x3257D</c> — film review
    /// reconstructs a whole recorded scene, so it needs the bigger workspace.
    /// </remarks>
    public const int ArenaBytesFilmReview = 40_000;

    /// <summary>
    /// Record size when the orientation triple is present: <c>0x18</c> = 24 B.
    /// </summary>
    /// <remarks>
    /// <c>pool_compute_bucket_head @0x1527E</c>, <c>image@0x1528A..0x15298</c>: bit1 CLEAR ⇒ 0x18.
    /// </remarks>
    public const int EntryBytesWithOrientation = 0x18;

    /// <summary>
    /// Record size when <see cref="WorldObjectFlags.NoOrientation"/> is set: <c>0x12</c> = 18 B.
    /// The three orientation words are simply not copied.
    /// </summary>
    public const int EntryBytesCompact = 0x12;

    private readonly List<WorldObject> _objects = [];
    private readonly List<WorldObject> _roots = [];

    /// <summary>Creates a pool with the original's gameplay byte budget.</summary>
    public WorldObjectPool()
        : this(ArenaBytesGameplay)
    {
    }

    /// <summary>Creates a pool with an explicit byte budget.</summary>
    /// <param name="arenaBytes">
    /// The arena's size in bytes — <see cref="ArenaBytesGameplay"/> or
    /// <see cref="ArenaBytesFilmReview"/> for the shipped sessions.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="arenaBytes"/> is not positive.</exception>
    public WorldObjectPool(int arenaBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(arenaBytes);
        ArenaBytes = arenaBytes;
    }

    /// <summary>The arena's byte budget.</summary>
    public int ArenaBytes { get; }

    /// <summary>
    /// How many <see cref="EntryBytesWithOrientation"/>-sized records the budget holds:
    /// 1,166 in gameplay, 1,666 in film review.
    /// </summary>
    public int MaxEntriesWithOrientation => ArenaBytes / EntryBytesWithOrientation;

    /// <summary>
    /// How many <see cref="EntryBytesCompact"/>-sized records the budget holds:
    /// 1,555 in gameplay, 2,222 in film review.  The real ceiling is between the two, because the
    /// original mixes record sizes in one bump heap.
    /// </summary>
    public int MaxEntriesCompact => ArenaBytes / EntryBytesCompact;

    /// <summary>Arena bytes the live objects account for, summing each object's own record size.</summary>
    public int UsedBytes
    {
        get
        {
            int total = 0;
            foreach (WorldObject obj in _objects)
            {
                total += obj.ArenaEntryBytes;
            }

            return total;
        }
    }

    /// <summary>Arena bytes still available.</summary>
    public int FreeBytes => ArenaBytes - UsedBytes;

    /// <summary>Every live object, in insertion order.</summary>
    public IReadOnlyList<WorldObject> Objects => _objects;

    /// <summary>The objects with no parent, in insertion order.</summary>
    public IReadOnlyList<WorldObject> Roots => _roots;

    /// <summary>
    /// Inserts a new object of <paramref name="classRecord"/> into the pool.
    /// </summary>
    /// <param name="classRecord">The class to instantiate.</param>
    /// <param name="x">World X.</param>
    /// <param name="y">World Y (up).</param>
    /// <param name="z">World Z.</param>
    /// <param name="heading">Yaw.</param>
    /// <param name="pitch">Pitch.</param>
    /// <param name="roll">Roll.</param>
    /// <param name="parent">The parent to attach to, or <see langword="null"/> for a root.</param>
    /// <returns>The new object.</returns>
    /// <remarks>
    /// <para>
    /// Reproduces the original's insert semantics, in the original's order:
    /// </para>
    /// <list type="number">
    /// <item><description>the record size is chosen from the orientation triple —
    /// <c>pool_insert_with_flag2 @0x1536C</c> sets bit1 when all three words are zero, and
    /// <c>pool_compute_bucket_head @0x1527E</c> then allocates 0x12 instead of 0x18 bytes;</description></item>
    /// <item><description>the object starts <see cref="WorldObjectFlags.Active"/>;</description></item>
    /// <item><description>the class's <see cref="ClassRecord.PoolFilterWord"/> is OR-ed into the
    /// entry's flag word — <c>pool_insert_with_bbox_or @0x153A1</c>,
    /// <c>mov bx,es:[si] / mov ax,[bx+0x2e] / or es:[si+2],ax</c> @<c>image@0x153BC..0x153C5</c>;</description></item>
    /// <item><description>a child is appended to the tail of the parent's chain and the parent gains
    /// <see cref="WorldObjectFlags.HasChildren"/> — <c>pool_insert_no_parent @0x152A4</c>,
    /// <c>image@0x152CE..0x152F0</c>.</description></item>
    /// </list>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="parent"/> does not belong to this pool.</exception>
    /// <exception cref="InvalidOperationException">The arena budget is exhausted.</exception>
    public WorldObject Insert(
        ClassRecord classRecord,
        int x = 0,
        int y = 0,
        int z = 0,
        Angle heading = default,
        Angle pitch = default,
        Angle roll = default,
        WorldObject? parent = null)
    {
        ArgumentNullException.ThrowIfNull(classRecord);
        if (parent is not null && !_objects.Contains(parent))
        {
            throw new ArgumentException("parent does not belong to this pool.", nameof(parent));
        }

        WorldObject obj = new WorldObject(classRecord)
        {
            X = x,
            Y = y,
            Z = z,
            Heading = heading,
            Pitch = pitch,
            Roll = roll,
            Flags = WorldObjectFlags.Active | classRecord.PoolFilterWord,
        };

        if (obj.HasZeroOrientation)
        {
            obj.Flags |= WorldObjectFlags.NoOrientation;
        }

        if (UsedBytes + obj.ArenaEntryBytes > ArenaBytes)
        {
            // The original does not degrade here: pool_arena_write_or_abort @0x15236 compares the
            // cursor against the arena end (unsigned JBE @image@0x15250) and, on overflow, LCALLs
            // oom_error_abort_modal @image@0x23151 — which does not return.  Throwing is the
            // faithful port of "abort", and it keeps the failure loud instead of silent.
            throw new InvalidOperationException(
                $"world-object pool arena exhausted: {UsedBytes} of {ArenaBytes} B used, " +
                $"{obj.ArenaEntryBytes} B more requested (original: oom_error_abort_modal @image@0x23151).");
        }

        // quirk registry: FIX-candidate — the ORIGINAL's *pending-sprite* table (a different table,
        // not this arena) has no LRU: on overflow it overwrites slot 0.  Deliberately NOT reproduced
        // here, and not this type's table.

        _objects.Add(obj);
        if (parent is null)
        {
            _roots.Add(obj);
        }
        else
        {
            parent.AddChild(obj);
        }

        return obj;
    }

    /// <summary>
    /// Removes an object and its whole subtree from the pool.
    /// </summary>
    /// <param name="obj">The object to remove.</param>
    /// <returns><see langword="true"/> when the object was in this pool.</returns>
    public bool Remove(WorldObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        if (!_objects.Contains(obj))
        {
            return false;
        }

        foreach (WorldObject doomed in obj.DescendantsAndSelf().Reverse())
        {
            _objects.Remove(doomed);
            _roots.Remove(doomed);
        }

        obj.Parent?.RemoveChild(obj);
        return true;
    }

    /// <summary>Empties the pool.</summary>
    public void Clear()
    {
        _objects.Clear();
        _roots.Clear();
    }

    /// <summary>
    /// Walks the whole tree depth-first, roots in insertion order, children before the next sibling —
    /// the traversal order of <c>pool_arena_entry_tree_walk_dispatch @0x159BD</c>.
    /// </summary>
    public IEnumerable<WorldObject> Walk()
    {
        foreach (WorldObject root in _roots)
        {
            foreach (WorldObject obj in root.DescendantsAndSelf())
            {
                yield return obj;
            }
        }
    }
}
