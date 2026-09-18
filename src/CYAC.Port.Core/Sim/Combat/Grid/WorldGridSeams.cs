namespace CYAC.Port.Core.Sim.Combat.Grid;

/// <summary>
/// The two world-grid quadtree arenas the query dispatches between —
/// <c>g_world_grid_lo_seg [0xF136]</c> (built with routing mask <c>0x1000</c>) and
/// <c>g_world_grid_hi_seg [0xF138]</c> (mask <c>0x0100</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>This seam has an oracle.</b>  A trace that captures both arenas and the cell-head array into
/// a <c>.tables.bin</c> sidecar answers every grid-query call the projectile row makes from the
/// captured arenas, so the port's own answers can drive the row and be compared.  The paragraph
/// below is why a trace WITHOUT those tables cannot do it.  Both arenas are far heap segments; the combat trace carries their
/// SEGMENT WORDS (the <c>world_grid_and_mission_state [0xF136]+74</c> stage window) and the build
/// cursor <c>[0xF13C]</c>, but never their CONTENTS.  Nor can the contents be rebuilt from the
/// dumped object arena: <c>world_grid_recursive_subdivide @image@0x29104</c> reads the 2-D
/// cell-head array at <c>[0x98]:[0x92]+(col*[0x8E]+row)*4</c>, and that array's segment word
/// <c>[0x98]</c> is one word past the end of the trace's <c>object_pool_and_grid_segments
/// [0x008A]+14</c> window.
/// </para>
/// <para>
/// A port RUNTIME never needs this seam to be oracle-fed: it builds its own arenas with
/// <see cref="WorldGridBuilder"/> from its own object pool, exactly as the original does at scene
/// start (<c>world_grid_init_pair @image@0x28404</c>).  The seam exists so that a VERIFICATION can
/// be handed the original's arenas which it now does; and <c>WorldGridBuilderReproductionTests</c>
/// shows the two agree: the ported build reproduces BOTH captured arenas BYTE-IDENTICALLY on all
/// six windows, 12 of 12.
/// </para>
/// </remarks>
public interface IWorldGridIndex
{
    /// <summary>The arena selected by routing mask <c>0x1001</c> — <c>g_world_grid_lo_seg</c>.</summary>
    WorldGridArena? Lo { get; }

    /// <summary>The arena selected by routing mask <c>0x0101</c> — <c>g_world_grid_hi_seg</c>.</summary>
    WorldGridArena? Hi { get; }
}

/// <summary>An <see cref="IWorldGridIndex"/> holding two already-built arenas.</summary>
/// <param name="Lo">The lo/far arena (mask <c>0x1000</c>), or null when the scene has none.</param>
/// <param name="Hi">The hi/near arena (mask <c>0x0100</c>), or null when the scene has none.</param>
public readonly record struct WorldGridIndex(WorldGridArena? Lo, WorldGridArena? Hi) : IWorldGridIndex
{
    /// <inheritdoc/>
    public WorldGridArena? Lo { get; } = Lo;

    /// <inheritdoc/>
    public WorldGridArena? Hi { get; } = Hi;
}

/// <summary>
/// The tripwire world-grid index: every access throws, naming the arm and the oracle that is missing.
/// </summary>
/// <remarks>
/// The K5/C2/C3a convention — "the subsystem did not run" is a visible decision, never a silent
/// default.  A verification catches this and counts the call as unverifiable rather than passing.
/// </remarks>
public sealed class UnavailableWorldGridIndex : IWorldGridIndex
{
    /// <summary>The shared instance.</summary>
    public static UnavailableWorldGridIndex Instance { get; } = new();

    /// <inheritdoc/>
    public WorldGridArena? Lo => throw Fail("0x1001 (g_world_grid_lo_seg [0xF136])");

    /// <inheritdoc/>
    public WorldGridArena? Hi => throw Fail("0x0101 (g_world_grid_hi_seg [0xF138])");

    private static WorldGridSeamException Fail(string which) => new(
        $"world_grid_frustum_query_and_select @image@0x28540 dispatched to grid arena {which}, "
            + "but no arena was supplied.  The combat trace carries the arena SEGMENT word and the "
            + "build cursor [0xF13C] but not the arena BYTES, and the build's own input (the 2-D "
            + "cell-head array at [0x98]:[0x92]) is not dumped either — see "
            + "and the oracle ask.  A port runtime should build one with WorldGridBuilder.");
}

/// <summary>The 2-D proximity grid buffer <c>g_grid_2d_buffer_ptr [0x1156]</c>.</summary>
/// <remarks>
/// Built by <c>grid_2d_init_via_alloc_5000 @image@0x21A60</c> from the SAME un-dumped cell-head
/// array as the world-grid quadtree, and read by <c>grid_2d_tallest_obstacle_score_at (ex-grid_2d_object_proximity_score_at)
/// @image@0x21CEB</c> (<see cref="Grid2dProximityScore"/>).  A far heap segment, so it is a seam for
/// exactly the same measured reason as <see cref="IWorldGridIndex"/>.
/// </remarks>
public interface IGrid2dCellBuffer
{
    /// <summary>One little-endian word of the 5000-byte buffer.</summary>
    /// <param name="byteOffset">The offset within the buffer.</param>
    ushort Word(int byteOffset);
}

/// <summary>The tripwire 2-D proximity buffer: it throws, naming the arm and the missing oracle.</summary>
public sealed class UnavailableGrid2dCellBuffer : IGrid2dCellBuffer
{
    /// <summary>The shared instance.</summary>
    public static UnavailableGrid2dCellBuffer Instance { get; } = new();

    /// <inheritdoc/>
    public ushort Word(int byteOffset) => throw new WorldGridSeamException(
        "grid_2d_tallest_obstacle_score_at @image@0x21CEB read the 2-D grid buffer "
            + "g_grid_2d_buffer_ptr [0x1156], a far heap segment no trace window carries "
            + ".  This arm fired 0 times in 27,177 traced P6 calls; "
            + "the tripwire has now fired, so the buffer oracle must be wired up.");
}

/// <summary>A world-grid seam the kernel needed and did not have.</summary>
/// <remarks>Distinct from an ordinary failure: it names an input no recording had to supply.</remarks>
public sealed class WorldGridSeamException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Which seam, and why it is missing.</param>
    public WorldGridSeamException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with an inner cause.</summary>
    /// <param name="message">Which seam, and why it is missing.</param>
    /// <param name="innerException">The cause.</param>
    public WorldGridSeamException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Per-arm activation counts for the whole world-grid subtree — the arm census, so a
/// verification can report which arms a recording reached and a tripwire can name the ones it did not.
/// </summary>
public sealed class WorldGridCounters
{
    /// <summary>Query calls that took branch A (<c>[0xF13E] == 0x1001</c>, <c>image@0x286C2</c>).</summary>
    public long DispatchLoArena { get; set; }

    /// <summary>Query calls that took branch B (<c>[0xF13E] == 0x0101</c>, <c>image@0x286F8</c>).</summary>
    public long DispatchHiArena { get; set; }

    /// <summary>
    /// Query calls that took the D1 "neither mask" fall-through (<c>jne 0x2872E</c>
    /// <c>image@0x286FE</c>) — the arm the emulator lift declined.
    /// </summary>
    public long DispatchNeither { get; set; }

    /// <summary>The second walker leaf differed from the first, so find-nearest ran twice.</summary>
    public long SecondLeafSearch { get; set; }

    /// <summary>The <c>[bp+8]</c> gate was on, so <c>world_object_list_find_sub</c> ran.</summary>
    public long SubListSearch { get; set; }

    /// <summary>The <c>[bp+6]</c> gate was on and <c>[0xEE02]</c> was non-zero.</summary>
    public long EngagementScan { get; set; }

    /// <summary>Iterations of the engagement backward scan.</summary>
    public long EngagementScanIterations { get; set; }

    /// <summary>The engagement scan hit (frustum accept) and returned the object.</summary>
    public long EngagementScanHit { get; set; }

    /// <summary>The <c>[bp+0xA]</c> gate was on, so <c>world_object_ground_plane_intersect (ex-world_object_range_project)</c> ran.</summary>
    public long GroundPlaneIntersect { get; set; }

    /// <summary>Range-project reported blocked (CF=1) and the query returned <c>0xFFFF</c>.</summary>
    public long RangeBlocked { get; set; }

    /// <summary>Queries that returned an object near-offset.</summary>
    public long SelectedObject { get; set; }

    /// <summary>Queries that returned 0 (nothing selected).</summary>
    public long SelectedNothing { get; set; }

    /// <summary>Candidates rejected by the routing-mask test (<c>image@0x28F99</c>).</summary>
    public long RejectMask { get; set; }

    /// <summary>Candidates rejected because they are the excluded object (<c>[0xF144]</c>).</summary>
    public long RejectExcluded { get; set; }

    /// <summary>Candidates rejected by the PRE-frustum engage-capable gate (<c>image@0x28FBA</c>).</summary>
    public long RejectEngageCapablePre { get; set; }

    /// <summary>Candidates rejected by the frustum hit test (<c>image@0x28FD4</c>).</summary>
    public long RejectFrustum { get; set; }

    /// <summary>Candidates rejected by the POST-frustum engage-capable gate (<c>image@0x29012</c>).</summary>
    public long RejectEngageCapablePost { get; set; }

    /// <summary>Candidates whose <see cref="Model.World.WorldObjectFlags.HasChildren"/> bit sent the
    /// walk into <c>world_object_list_find_sub</c> (<c>image@0x28FDF</c>).</summary>
    public long ChildRecursion { get; set; }

    /// <summary>Frustum tests rejected by the cheap dual-AABB pre-test (<c>image@0x28947</c>).</summary>
    public long FrustumAabbReject { get; set; }

    /// <summary>Frustum tests rejected because a P1/P2 delta did not fit its own sign extension.</summary>
    public long FrustumDeltaReject { get; set; }

    /// <summary>Frustum tests that accepted with the trivial <c>oc1|oc2 == 0</c> outcode.</summary>
    public long FrustumTrivialAccept { get; set; }

    /// <summary>Frustum tests that accepted by draining the 15-step clip counter (the ACCEPTS quirk).</summary>
    public long FrustumExhaustionAccept { get; set; }

    /// <summary>Frustum tests rejected by the trivial <c>oc1 &amp; oc2 != 0</c> outcode.</summary>
    public long FrustumTrivialReject { get; set; }

    /// <summary>Iterations of the Cohen-Sutherland clip loop that ran the DEAD P2 arm (the K-block bug).</summary>
    public long FrustumDeadP2Arm { get; set; }

    /// <summary>Frustum tests that wrote the six-word output block through <c>[0xF15E]</c>.</summary>
    public long FrustumOutputWritten { get; set; }

    /// <summary>Range-project took PATH A (<c>[0xF173] &lt; 0</c>).</summary>
    public long RangePathA { get; set; }

    /// <summary>Range-project took PATH B (<c>[0xF167] &lt; 0</c>).</summary>
    public long RangePathB { get; set; }

    /// <summary>Range-project took the DEFAULT arm (CF=0, <c>image@0x28E5A</c>).</summary>
    public long RangeDefault { get; set; }

    /// <summary>Adds another census into this one.</summary>
    /// <param name="other">The census to fold in.</param>
    public void Add(WorldGridCounters other)
    {
        ArgumentNullException.ThrowIfNull(other);
        DispatchLoArena += other.DispatchLoArena;
        DispatchHiArena += other.DispatchHiArena;
        DispatchNeither += other.DispatchNeither;
        SecondLeafSearch += other.SecondLeafSearch;
        SubListSearch += other.SubListSearch;
        EngagementScan += other.EngagementScan;
        EngagementScanIterations += other.EngagementScanIterations;
        EngagementScanHit += other.EngagementScanHit;
        GroundPlaneIntersect += other.GroundPlaneIntersect;
        RangeBlocked += other.RangeBlocked;
        SelectedObject += other.SelectedObject;
        SelectedNothing += other.SelectedNothing;
        RejectMask += other.RejectMask;
        RejectExcluded += other.RejectExcluded;
        RejectEngageCapablePre += other.RejectEngageCapablePre;
        RejectFrustum += other.RejectFrustum;
        RejectEngageCapablePost += other.RejectEngageCapablePost;
        ChildRecursion += other.ChildRecursion;
        FrustumAabbReject += other.FrustumAabbReject;
        FrustumDeltaReject += other.FrustumDeltaReject;
        FrustumTrivialAccept += other.FrustumTrivialAccept;
        FrustumExhaustionAccept += other.FrustumExhaustionAccept;
        FrustumTrivialReject += other.FrustumTrivialReject;
        FrustumDeadP2Arm += other.FrustumDeadP2Arm;
        FrustumOutputWritten += other.FrustumOutputWritten;
        RangePathA += other.RangePathA;
        RangePathB += other.RangePathB;
        RangeDefault += other.RangeDefault;
    }

    /// <summary>The census as (name, count) pairs, in arm order.</summary>
    /// <returns>One pair per arm.</returns>
    public IEnumerable<(string Name, long Count)> Rows()
    {
        yield return ("dispatch LO arena (0x1001)", DispatchLoArena);
        yield return ("dispatch HI arena (0x0101)", DispatchHiArena);
        yield return ("dispatch NEITHER (D1 fall-through)", DispatchNeither);
        yield return ("second leaf search", SecondLeafSearch);
        yield return ("find_sub gate", SubListSearch);
        yield return ("engagement scan", EngagementScan);
        yield return ("engagement scan iterations", EngagementScanIterations);
        yield return ("engagement scan HIT", EngagementScanHit);
        yield return ("range project", GroundPlaneIntersect);
        yield return ("range BLOCKED (0xFFFF)", RangeBlocked);
        yield return ("selected an object", SelectedObject);
        yield return ("selected nothing", SelectedNothing);
        yield return ("reject: routing mask", RejectMask);
        yield return ("reject: excluded self", RejectExcluded);
        yield return ("reject: engage-capable (pre-frustum)", RejectEngageCapablePre);
        yield return ("reject: frustum", RejectFrustum);
        yield return ("reject: engage-capable (post-frustum)", RejectEngageCapablePost);
        yield return ("child recursion (bit5)", ChildRecursion);
        yield return ("frustum: AABB pre-reject", FrustumAabbReject);
        yield return ("frustum: delta reject", FrustumDeltaReject);
        yield return ("frustum: trivial accept", FrustumTrivialAccept);
        yield return ("frustum: exhaustion accept (quirk)", FrustumExhaustionAccept);
        yield return ("frustum: trivial reject", FrustumTrivialReject);
        yield return ("frustum: dead P2 arm (K-block bug)", FrustumDeadP2Arm);
        yield return ("frustum: output written", FrustumOutputWritten);
        yield return ("range: PATH A", RangePathA);
        yield return ("range: PATH B", RangePathB);
        yield return ("range: DEFAULT", RangeDefault);
    }
}
