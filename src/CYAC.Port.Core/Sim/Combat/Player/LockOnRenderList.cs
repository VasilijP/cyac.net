namespace CYAC.Port.Core.Sim.Combat.Player;

/// <summary>
/// <c>world_grid_frustum_query_and_select @image@0x28540</c> (P20) as
/// <c>engagement_object_frustum_qualify</c>'s FIFTH gate calls it
/// (<c>lcall 0x3840:0x140</c> @<c>image@0x031B4</c>).
/// </summary>
/// <remarks>
/// The eleven pushed words are the node's object (<c>node[+0x14]</c>), a stack copy of the
/// per-mesh state block <c>[0xE822..0xE82D]</c>, the object's own position (<c>obj+6</c> in the
/// pool segment), three zero words, <c>0x1000</c> and four more zeros
/// (<c>image@0x03196..0x031B3</c>).  A NON-ZERO answer REJECTS the node
/// (<c>or ax,ax / jne</c> @<c>image@0x031B9</c>).
/// </remarks>
public interface ILockOnGridQuery
{
    /// <summary>Runs the query for one render-list node.</summary>
    /// <param name="node">The node's DGROUP near offset.</param>
    /// <param name="objectRef">The node's object — the pushed <c>node[+0x14]</c>.</param>
    /// <returns>The original's <c>AX</c>: non-zero REJECTS.</returns>
    ushort Query(ushort node, ushort objectRef);
}

/// <summary>
/// The player lock-on's whole RENDER-LIST side, run over real nodes:
/// <c>qualifying_object_list_build @image@0x030C0</c>, <c>engagement_object_frustum_qualify
/// @image@0x03105</c>, the two chain walks (<c>image@0x02FD5</c> / <c>image@0x0307F</c>) and
/// <c>object_screen_pos_project @image@0x036EA</c>.
/// </summary>
/// <remarks>
/// <para>
/// Trace format v1.6's stage window <c>render_slot_block [0xD8A4]+0xBB8</c> (ask G1) puts the
/// whole 3,000-byte renderer arena on every stage record, and
/// <see cref="ObjectScreenProjection"/> reconstructs the projector from the bytes, so this class
/// runs the arms outright.
/// </para>
/// <para>
/// The only question left outside is the FIFTH frustum gate, the world-grid line-of-sight query
/// (<see cref="ILockOnGridQuery"/>) — the same P20 seam every other combat piece
/// keeps.
/// </para>
/// </remarks>
/// <param name="nodes">The render list (see <see cref="RenderSlotNodes"/>).</param>
/// <param name="arena">The object pool the nodes name.</param>
/// <param name="prototypes">The engagement class prototypes.</param>
/// <param name="clip">The screen clip rectangle <c>[0xE628..0xE62F]</c>.</param>
/// <param name="gridQuery">The fifth gate.</param>
public sealed class LockOnRenderList(
    ILockOnRenderNodes nodes,
    PoolArena arena,
    IEngagementPrototypes prototypes,
    LockOnClipRect clip,
    ILockOnGridQuery gridQuery)
{
    private readonly ILockOnRenderNodes _nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
    private readonly PoolArena _arena = arena ?? throw new ArgumentNullException(nameof(arena));
    private readonly IEngagementPrototypes _prototypes =
        prototypes ?? throw new ArgumentNullException(nameof(prototypes));
    private readonly ILockOnGridQuery _gridQuery =
        gridQuery ?? throw new ArgumentNullException(nameof(gridQuery));

    /// <summary>How many times <see cref="Qualifies"/> ran.</summary>
    public int QualifyCalls { get; private set; }

    /// <summary>How many of those reached the world-grid gate.</summary>
    public int GridQueries { get; private set; }

    /// <summary>How many times a candidate list was built.</summary>
    public int Builds { get; private set; }

    /// <summary>How many times a node was projected.</summary>
    public int Projections { get; private set; }

    /// <summary>The render list this instance walks.</summary>
    public ILockOnRenderNodes Nodes => _nodes;

    /// <summary>The chain walk — <c>image@0x02FD5..0x02FED</c> / <c>image@0x0307F..0x03091</c>.</summary>
    /// <param name="head">The list head.</param>
    /// <param name="objectRef">The object to find.</param>
    /// <returns>The node, or 0.</returns>
    public ushort Find(ushort head, ushort objectRef)
    {
        ushort node = head;
        while (node != 0)
        {
            if (_nodes.ObjectOf(node) == objectRef)
            {
                return node;
            }

            node = _nodes.Next(node);
        }

        return 0;
    }

    /// <summary><c>node[+0x14]</c>.</summary>
    /// <param name="node">The node.</param>
    /// <returns>The object it names.</returns>
    public ushort ObjectOf(ushort node) => _nodes.ObjectOf(node);

    /// <summary><c>engagement_object_frustum_qualify @image@0x03105</c>.</summary>
    /// <param name="node">The node.</param>
    /// <returns>The original's <c>AL</c>.</returns>
    public bool Qualifies(ushort node)
    {
        QualifyCalls++;
        return LockOnCandidateList.Qualifies(
            node, _nodes, _arena, HitPoints, clip, ObjectGridQuery);
    }

    /// <summary><c>qualifying_object_list_build @image@0x030C0</c>.</summary>
    /// <param name="head">The list head.</param>
    /// <param name="candidates">The caller's 32-word buffer.</param>
    /// <returns>How many entries were written.</returns>
    public int Build(ushort head, Span<ushort> candidates)
    {
        Builds++;
        return LockOnCandidateList.Build(head, candidates, _nodes, Qualifies);
    }

    /// <summary><c>object_screen_pos_project @image@0x036EA</c>.</summary>
    /// <param name="node">The node.</param>
    /// <returns>The screen pair.</returns>
    public (short ScreenX, short ScreenY) Project(ushort node)
    {
        Projections++;
        return _nodes.Project(node);
    }

    /// <summary>
    /// <c>object_pool_get_engagement_fieldoff @image@0x02276</c> then <c>[bx+9]</c> — note this is
    /// the helper that returns the WORD at the engagement field, not the one at
    /// <c>image@0x0225A</c> that returns its OFFSET; the word is the engagement class prototype's
    /// DGROUP near pointer.
    /// </summary>
    private byte HitPoints(ushort objectRef) =>
        _prototypes.InitialHitPoints(_arena.Word(_arena.EngagementBlockRef(objectRef)));

    private ushort ObjectGridQuery(ushort node)
    {
        GridQueries++;
        return _gridQuery.Query(node, _nodes.ObjectOf(node));
    }
}
