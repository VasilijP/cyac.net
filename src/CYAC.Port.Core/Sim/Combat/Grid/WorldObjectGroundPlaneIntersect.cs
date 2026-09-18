namespace CYAC.Port.Core.Sim.Combat.Grid;

/// <summary>
/// <c>world_object_ground_plane_intersect (ex-world_object_range_project) @image@0x28E60</c> — the last gate of the world-grid query's filter
/// chain, run when the query's <c>[bp+0xA]</c> argument is non-zero.
/// </summary>
/// <remarks>
/// <para>
/// <b>Role, re-derived from the bytes(a genuine reframe — the scanner calls it "an approximate world
/// position … a fallback HUD/radar position"):</b> this is the segment-versus- GROUND-PLANE intersection.  The
/// dispatch reads the SIGN of each end point's <b>axis-1</b> (altitude) high byte — <c>[0xF173]</c> is P2's,
/// <c>[0xF167]</c> is P1's — and:
/// </para>
/// <list type="bullet">
///   <item><b>P2 below the plane</b> (<c>[0xF173] &lt; 0</c>) → PATH A: clamp axis 0 and axis 2 to
///     the pairwise minimum, then LERP both by axis 1 to the crossing, and emit that point with its
///     axis-1 word left <b>zero</b> — the ground point.  CF=1 (blocked).</item>
///   <item><b>P2 above, P1 below</b> (<c>[0xF167] &lt; 0</c>) → PATH B: emit P1 verbatim (a six-word
///     copy of <c>g_world_grid_vec1_scratch [0x2F96]</c>).  CF=1 (blocked).</item>
///   <item><b>both above</b> → DEFAULT, CF=<b>0</b> (not blocked).</item>
/// </list>
/// <para>
/// The DEFAULT arm's CF=0 is itself a correction established from the bytes: its <c>jge</c> at
/// <c>image@0x28E71</c> targets a SEPARATE five-byte tail at <c>image@0x28E5A</c> (<c>clc; pop di; pop si; pop bp;
/// ret</c>) that MSC produced by epilogue-tail sharing with the preceding function's <c>stc</c> version.  The decoded
/// C's single shared "carry set unconditionally" epilogue is wrong for this arm, and the query's own <c>jae</c>
/// (<c>image@0x2879A</c>) branches on exactly that bit.
/// </para>
/// <para>
/// Axis-1 is the altitude axis in this block; the emitted record's <c>+4/+6</c> words are written as
/// zero on PATH A and are the only reason the port keeps a six-word block rather than a triple.
/// <c>+0xC/+0xE</c> are genuinely never written by any path.
/// </para>
/// </remarks>
public static class WorldObjectGroundPlaneIntersect
{
    /// <summary>The outcome of one call — the original's carry flag.</summary>
    public enum Outcome
    {
        /// <summary>DEFAULT: both end points above the plane; CF=0, the query falls through to <c>AX=0</c>.</summary>
        NotBlocked,

        /// <summary>PATH A: the segment crosses the plane; CF=1, the query returns <c>0xFFFF</c>.</summary>
        CrossingPoint,

        /// <summary>PATH B: P1 is already below the plane; CF=1, the query returns <c>0xFFFF</c>.</summary>
        StartBelow,
    }

    /// <summary>Runs the projector.</summary>
    /// <param name="state">The world-grid DGROUP block.</param>
    /// <param name="output">The caller's output block, or null when <c>[0xF15E]</c> is 0.</param>
    /// <param name="counters">The arm census.</param>
    /// <returns>Which arm ran — the original's CF.</returns>
    public static Outcome Run(WorldGridState state, WorldGridOutputBlock? output, WorldGridCounters counters)
    {
        ArgumentNullException.ThrowIfNull(counters);

        // Dispatch on the HIGH BYTE of each end point's axis-1 high word (image@0x28E65..0x28E71).
        sbyte p2AltMsb = unchecked((sbyte)(state.Frustum(9) >> 8));    // [0xF173]
        if (p2AltMsb < 0)
        {
            counters.RangePathA++;
            PathA(state, output, counters);
            return Outcome.CrossingPoint;
        }

        sbyte p1AltMsb = unchecked((sbyte)(state.Frustum(3) >> 8));    // [0xF167]
        if (p1AltMsb >= 0)
        {
            counters.RangeDefault++;
            return Outcome.NotBlocked;
        }

        counters.RangePathB++;
        if (output is not null && output.Enabled)
        {
            // `mov cx,6; mov si,0x2f96; rep movsw` (image@0x28E7F..0x28E86) — P1 verbatim.
            for (int w = 0; w < 6; w++)
            {
                output.Words[w] = state.Vec1(w);
            }

            output.Writes++;
        }

        return Outcome.StartBelow;
    }

    private static void PathA(WorldGridState state, WorldGridOutputBlock? output, WorldGridCounters counters)
    {
        _ = counters;

        // Step 1 / Step 2 (image@0x28E8A..0x28ECD): the textbook 16:16 signed-32 min idiom.
        (ushort lo, ushort hi) clampX = Min32(
            state.Frustum(0), state.Frustum(1), state.Frustum(6), state.Frustum(7));
        (ushort lo, ushort hi) clampZ = Min32(
            state.Frustum(4), state.Frustum(5), state.Frustum(10), state.Frustum(11));

        // Step 3 (image@0x28ED0..0x28EED): four 16-bit deltas off the clamped minima.
        short dx1 = unchecked((short)(state.Frustum(0) - clampX.lo));
        short dx2 = unchecked((short)(state.Frustum(6) - clampX.lo));
        short dz1 = unchecked((short)(state.Frustum(4) - clampZ.lo));
        short dz2 = unchecked((short)(state.Frustum(10) - clampZ.lo));

        // Step 4 (image@0x28EF0..0x28F1C): lerp both axes by the altitude span; ONE shared divisor.
        short dxDiff = unchecked((short)(dx2 - dx1));
        short dzDiff = unchecked((short)(dz2 - dz1));
        short depthDenom = unchecked((short)(state.Frustum(8) - state.Frustum(2)));
        short numerator = unchecked((short)state.Frustum(2));

        if (depthDenom == 0)
        {
            throw new WorldGridSeamException(
                "world_object_ground_plane_intersect @image@0x28F00: the altitude span [0xF170]-[0xF164] is 0, "
                    + "so the original's IDIV raises #DE.  The port declines this case; the "
                    + "port has no model for the fault handler.");
        }

        dx1 = unchecked((short)(dx1 - Divide(dxDiff, numerator, depthDenom)));
        dz1 = unchecked((short)(dz1 - Divide(dzDiff, numerator, depthDenom)));

        // Step 5 (image@0x28F20..0x28F64): pack, gated on the output pointer.
        if (output is null || !output.Enabled)
        {
            return;
        }

        output.SetAxis(0, unchecked(dx1 + Combine(clampX)) << 8);
        output.SetAxis(1, 0);   // the ground plane — `sub ax,ax` twice, image@0x28F3B
        output.SetAxis(2, unchecked(dz1 + Combine(clampZ)) << 8);
        output.Writes++;
    }

    private static short Divide(short diff, short numerator, short divisor)
    {
        long q = (long)((int)diff * numerator) / divisor;
        if (q is > short.MaxValue or < short.MinValue)
        {
            throw new WorldGridSeamException(
                "world_object_ground_plane_intersect @image@0x28F00: the IDIV quotient does not fit i16, so the "
                    + "original raises #DE.  The port declines this case.");
        }

        return unchecked((short)q);
    }

    private static int Combine((ushort Lo, ushort Hi) pair) =>
        unchecked((int)(((uint)pair.Hi << 16) | pair.Lo));

    /// <summary>
    /// <c>min</c> of two 16:16 signed pairs, in the original's own idiom: compare the high words
    /// SIGNED, tie-break on the low words UNSIGNED, keep the first when it is less-or-equal.
    /// </summary>
    private static (ushort Lo, ushort Hi) Min32(ushort aLo, ushort aHi, ushort bLo, ushort bHi)
    {
        if ((short)aHi < (short)bHi)
        {
            return (aLo, aHi);
        }

        if ((short)aHi > (short)bHi)
        {
            return (bLo, bHi);
        }

        return aLo <= bLo ? (aLo, aHi) : (bLo, bHi);
    }
}
