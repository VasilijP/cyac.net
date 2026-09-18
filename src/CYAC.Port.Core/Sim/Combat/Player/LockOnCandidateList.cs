using CYAC.Port.Core.Model.Combat;

namespace CYAC.Port.Core.Sim.Combat.Player;

/// <summary>
/// The RENDERER's per-frame sorted display list, as the lock-on sees it — the seam
/// <c>qualifying_object_list_build</c> and <c>engagement_object_frustum_qualify</c> are pure over.
/// </summary>
/// <remarks>
/// <para>
/// The list head is <c>g_engagement_object_list_head [0x0E90A]</c>, written by
/// <c>render_slot_sorted_list_merge @image@0x144BE</c> from the render slots; the nodes are
/// DGROUP records walked through <c>+0x02</c> with the DEFAULT segment (no prefix anywhere in
/// <c>image@0x02FD5..0x02FED</c>, <c>image@0x0307F..0x03091</c>, <c>image@0x030ED..0x030F3</c>).
/// </para>
/// <para>
/// MEASURED (<c>session_20260829_173046_det.ctr</c>, 1,501 frames): the head is <c>0xE446</c> on
/// every frame and the nodes the probes name span DGROUP <c>0xE2FC..0xE446</c> at a stride of 22
/// bytes — a region NO combat-trace window carries.  That, and not any property of the three
/// routines, is why they are seams: they are pure, but the state they are pure over
/// belongs to the renderer.
/// </para>
/// </remarks>
public interface ILockOnRenderNodes
{
    /// <summary><c>node[+0x02]</c> — the next node (<c>image@0x030F0</c>).</summary>
    /// <param name="node">The node's DGROUP near offset.</param>
    /// <returns>The next node, or 0 at the end of the list.</returns>
    ushort Next(ushort node);

    /// <summary><c>node[+0x14]</c> — the pool object the node draws (<c>image@0x03110</c>).</summary>
    /// <param name="node">The node's DGROUP near offset.</param>
    /// <returns>The object's pool near offset.</returns>
    ushort ObjectOf(ushort node);

    /// <summary>
    /// <c>node[+0x0A]</c> — the third of the three view-space position words, tested
    /// <c>&gt; 0</c> (signed) as the in-front-of-the-camera gate (<c>cmp word [di+0xA],0 / jle</c>
    /// @<c>image@0x0313F</c>).
    /// </summary>
    /// <param name="node">The node's DGROUP near offset.</param>
    /// <returns>The signed word.</returns>
    short DepthWord(ushort node);

    /// <summary>
    /// <c>object_screen_pos_project @image@0x036EA</c> on this node — the pair in the machine's
    /// out-pointer order, screen X first (see
    /// <see cref="IPlayerCombatEvents.ProjectPositionToScreen"/>).
    /// </summary>
    /// <param name="node">The node's DGROUP near offset.</param>
    /// <returns>The projected pair.</returns>
    (short ScreenX, short ScreenY) Project(ushort node);
}

/// <summary>
/// <c>qualifying_object_list_build @image@0x030C0</c> and
/// <c>engagement_object_frustum_qualify @image@0x03105</c>, ported over
/// <see cref="ILockOnRenderNodes"/>.
/// </summary>
/// <remarks>
/// Both bodies are transliterated from the bytes so the KNOWLEDGE ships; the driver still reaches
/// them through <see cref="IPlayerCombatEvents"/> because the simulation core has no renderer and therefore
/// no list (see <see cref="ILockOnRenderNodes"/>).  When the port's renderer lands, an
/// <c>IPlayerCombatEvents</c> implementation that owns the display list can answer
/// <c>BuildQualifyingObjectList</c> / <c>ObjectStillQualifies</c> with exactly these two methods.
/// </remarks>
public static class LockOnCandidateList
{
    /// <summary>
    /// The list cap — <c>cmp di,0x1e / jge</c> @<c>image@0x030DB</c>.  Note the caller's buffer is
    /// <c>[bp-0x40]</c>, i.e. 32 words: the cap is 30, two short of the buffer.
    /// </summary>
    public const int MaxCandidates = 0x1E;

    /// <summary>
    /// <c>qualifying_object_list_build @image@0x030C0</c> — walk the render list, append every node
    /// that qualifies, stop at <see cref="MaxCandidates"/>.
    /// </summary>
    /// <param name="head">The pushed <c>[bp+6]</c> — the list head.</param>
    /// <param name="candidates">The caller's buffer; NODES are written, not objects.</param>
    /// <param name="nodes">The render-list view.</param>
    /// <param name="qualifies">
    /// <c>engagement_object_frustum_qualify</c> (<c>call 0x3105</c> @<c>image@0x030D4</c>).
    /// </param>
    /// <returns>The original's <c>AX</c> — how many entries were written.</returns>
    /// <remarks>
    /// The cap arm at <c>image@0x030DE</c> jumps to the EXIT, so a full list stops the walk with
    /// the 30 it has — it does not keep scanning.  And the cap is tested only on a node that has
    /// already qualified, so a 31st qualifying node is dropped, never a rejected one.
    /// </remarks>
    public static int Build(
        ushort head,
        Span<ushort> candidates,
        ILockOnRenderNodes nodes,
        Func<ushort, bool> qualifies)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(qualifies);

        int count = 0;                                          // DI, image@0x030C7
        ushort node = head;
        while (node != 0)                                       // image@0x030F6
        {
            if (qualifies(node))                                // image@0x030D4, `or al,al / je`
            {
                if (count >= MaxCandidates)                     // image@0x030DB
                {
                    return count;                               // image@0x030DE → the exit
                }

                candidates[count] = node;                       // image@0x030EA, `mov [bx],ax`
                count++;
            }

            node = nodes.Next(node);                            // image@0x030ED..0x030F3
        }

        return count;
    }

    /// <summary>
    /// <c>engagement_object_frustum_qualify @image@0x03105</c> — the five gates, in the machine's
    /// order, with the fifth (the world-grid line-of-sight oracle) behind
    /// <paramref name="gridQuery"/>.
    /// </summary>
    /// <param name="node">The pushed <c>[bp+4]</c> — a render-list node.</param>
    /// <param name="nodes">The render-list view.</param>
    /// <param name="arena">The object pool.</param>
    /// <param name="engagementHitPoints">
    /// <c>object_pool_get_engagement_fieldoff @image@0x02276</c> then <c>[bx+9]</c>
    /// (<c>image@0x03131..0x03136</c>) — the engagement block's
    /// <c>s_engagement_class_proto[+0x09] initial_hit_points_u8</c>; <c>0xFF</c> is the
    /// "no engagement record" sentinel.
    /// </param>
    /// <param name="clip">The screen clip rectangle <c>[0xE628..0xE62F]</c>.</param>
    /// <param name="gridQuery">
    /// <c>world_grid_frustum_query_and_select @image@0x28540</c> (P20) as the door at
    /// <c>image@0x031B4</c> calls it: the answer is the caller's whole verdict, and a NON-ZERO answer
    /// REJECTS (<c>or ax,ax / jne</c> @<c>image@0x031B9</c>).
    /// </param>
    /// <returns>The original's <c>AL</c>.</returns>
    /// <remarks>
    /// The flag gate is <c>(flags &amp; 0x0901) == 0x0901</c> (<c>image@0x03126</c>) — bits 0
    /// (active), 8 and 11 — read in the pool segment <c>[0x0094]</c> with an explicit <c>ES:</c>
    /// prefix, unlike everything else in the routine.
    /// </remarks>
    public static bool Qualifies(
        ushort node,
        ILockOnRenderNodes nodes,
        PoolArena arena,
        Func<ushort, byte> engagementHitPoints,
        LockOnClipRect clip,
        Func<ushort, ushort> gridQuery)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(engagementHitPoints);
        ArgumentNullException.ThrowIfNull(gridQuery);

        ushort objectRef = nodes.ObjectOf(node);                        // image@0x03110
        CombatObjectView view = new CombatObjectView(arena, objectRef);
        if ((view.Flags & 0x0901) != 0x0901)                            // image@0x03122..0x0312C
        {
            return false;
        }

        if (engagementHitPoints(objectRef) == 0xFF)                     // image@0x03131..0x0313A
        {
            return false;
        }

        if (nodes.DepthWord(node) <= 0)                                 // image@0x0313F, signed jle
        {
            return false;
        }

        (short screenX, short screenY) = nodes.Project(node);                   // image@0x03145..0x0314E
        if (clip.XMin > screenX || clip.XMax < screenX                  // image@0x03151..0x0315E
            || clip.YMin > screenY || clip.YMax < screenY)              // image@0x03160..0x0316D
        {
            return false;
        }

        return gridQuery(node) == 0;                                    // image@0x031B4..0x031BF
    }
}

/// <summary>
/// The screen clip rectangle <c>engagement_object_frustum_qualify</c> pre-filters against —
/// <c>g_gfx_clip_x_min [0xE628]</c>, <c>[0xE62A]</c>, <c>[0xE62C]</c>, <c>[0xE62E]</c>, all signed
/// (<c>jg</c>/<c>jl</c> at <c>image@0x03158</c>/<c>0x0315E</c>/<c>0x03167</c>/<c>0x0316D</c>).
/// </summary>
/// <param name="XMin"><c>[0xE628]</c>.</param>
/// <param name="XMax"><c>[0xE62A]</c>.</param>
/// <param name="YMin"><c>[0xE62C]</c>.</param>
/// <param name="YMax"><c>[0xE62E]</c>.</param>
public readonly record struct LockOnClipRect(short XMin, short XMax, short YMin, short YMax)
{
    /// <summary>Reads the rectangle out of the combat register file.</summary>
    /// <param name="registers">The DGROUP combat register file.</param>
    /// <returns>The four clip bounds.</returns>
    public static LockOnClipRect From(CombatRegisters registers)
    {
        ArgumentNullException.ThrowIfNull(registers);
        return new LockOnClipRect(
            unchecked((short)registers.Word(0xE628)),
            unchecked((short)registers.Word(0xE62A)),
            unchecked((short)registers.Word(0xE62C)),
            unchecked((short)registers.Word(0xE62E)));
    }
}
