using CYAC.Port.Core.Model.Combat;
using CYAC.Port.Core.Sim.Combat;
using CYAC.Port.Core.Sim.Combat.Grid;

namespace CYAC.Port.Core.Sim.Session;

/// <summary>
/// <c>engagement_kill_query @image@0x226B2</c> — the "did I fly into something?" probe
/// <c>flight_engine_per_frame_top</c> makes once per frame, and the <c>kill_result</c> its Path A tests
/// (<c>image@0x2298F</c>: <c>call 0x226b2 / mov [bp-0x10],ax</c>).
/// </summary>
/// <remarks>
/// <para>
/// The whole function, re-derived from the bytes for H9:
/// </para>
/// <code>
/// 226B9  les bx,[0xc0]                            ; the PLAYER's own pool object
/// 226BD  cmp word es:[bx+0xc],7 / jl 0x226d2 / jg 0x226ce
/// 226C6  cmp word es:[bx+0xa],0xd000 / jb 0x226d2
/// 226CE sub ax,ax / jmp ret; pos_y &gt;= 0x0007D000 → 0
/// 226D2  push bx                                  ; ExcludeRef
/// 226D3 push (es:bx+6) TWICE; vector1 == vector2 == the player's position
/// 226DE  push [0xf0b0] / push [0xf0ae]            ; the half-range, i32
/// 226E6  push lea [bp-0xc] / push 0x100           ; the output block; routing mask → 0x101 = HI
/// 226EE  push 0 ×4                                ; visMode / rangeGate / subGate / loopGate
/// 226F4  lcall world_grid_frustum_query_and_select @image@0x28540
/// 226FB  cmp si,-1 / je → 0                       ; the ground sentinel is discarded
/// </code>
/// <list type="bullet">
///   <item><description>
///     <c>0x0007D000 &gt;&gt; 8 = 2,000 ft</c>: <b>the query does not run above 2,000 ft at all</b>,
///     so nothing above that altitude can ever be rammed.
///   </description></item>
///   <item><description>
///     The two "segment end points" are the SAME far pointer, and the half-range
///     <c>[0xF0AE]/[0xF0B0]</c> is a DGROUP i32 with <b>zero writers image-wide</b> shipping as
///     <b>0</b> (<c>image@0x4AE0E</c>).  So this is a <b>degenerate POINT probe</b> at the player's own
///     position — "am I inside something's box?" — not a swept-volume collision test.  It is the
///     other half of H4's "NO mid-air collision in the original": there is no swept test, but there
///     IS a point test, and only below 2,000 ft.
///   </description></item>
///   <item><description>
///     Every gate byte is 0, so the ground-plane arm (<c>rangeGate</c>) never runs and the
///     <c>0xFFFF</c> sentinel the caller discards can never be produced anyway.
///   </description></item>
/// </list>
/// <para>
/// Path A then tests bit 3 of the named object's <c>+0x03</c> and, when it is set, calls
/// <c>engagement_kill_finalize @image@0x0C36B</c> on it — the same finaliser the port already runs
/// for an enemy kill (<c>CombatSpawnDepart.KillFinalize</c>) — before ending the session.
/// </para>
/// </remarks>
public static class PlayerProximityKill
{
    /// <summary>
    /// The altitude ceiling, in position units: <c>0x0007D000</c> = 2,000 ft
    /// (<c>image@0x226BD..0x226CC</c>, a signed lexicographic i32 compare done as
    /// <c>cmp high,7 / jl / jg</c> then <c>cmp low,0xD000 / jb</c>).
    /// </summary>
    public const int AltitudeCeiling = 0x0007D000;

    /// <summary>The routing mask the call site pushes (<c>image@0x226EA</c>); the query ORs 1 into it.</summary>
    public const ushort RoutingMask = 0x0100;

    /// <summary>
    /// <c>[0xF0AE]</c> — the query's half-range, low word.  Zero writers image-wide; ships as 0.
    /// </summary>
    public const int HalfRangeWord = 0xF0AE;

    /// <summary>The object's position triple starts at <c>+0x06</c>.</summary>
    public const int PositionOffset = 0x06;

    /// <summary>Runs the query for this frame.</summary>
    /// <param name="registers">The combat register file (for <c>[0x00C0]</c> and <c>[0xF0AE]</c>).</param>
    /// <param name="arena">The pool.</param>
    /// <param name="acquisition">The session's ported world-grid query.</param>
    /// <returns>The object the player is inside, or 0.</returns>
    public static ushort Query(
        CombatRegisters registers, PoolArena arena, ITargetAcquisition acquisition)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(acquisition);

        ushort player = registers.Word(PlayerEject.PlayerObjectWord);       // image@0x226B9
        if (player == 0 || !arena.Covers(player, 0x18))
        {
            return 0;
        }

        CombatPosition position = new CombatObjectView(arena, player).Position;
        if (position.Y >= AltitudeCeiling)                                  // image@0x226BD
        {
            return 0;
        }

        int halfRange = unchecked(
            (int)(((uint)registers.Word(HalfRangeWord + 2) << 16) | registers.Word(HalfRangeWord)));

        ushort answer = acquisition.Query(
            new GridQueryRequest(
                ExcludeRef: player,
                LocalPositionCopy: position,
                SelfPositionRef: (ushort)(player + PositionOffset),
                HalfRange: halfRange,
                RoutingMask: RoutingMask,
                VisMode: 0,
                RangeGate: 0,
                SubGate: 0,
                LoopGate: 0),
            out _);

        // image@0x226FB — the caller keeps only a real object reference.
        return answer == WorldGridQuery.BlockedSentinel ? (ushort)0 : answer;
    }
}
