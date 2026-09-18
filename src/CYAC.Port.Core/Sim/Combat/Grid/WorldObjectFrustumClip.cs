using CYAC.Port.Core.Model.World;

namespace CYAC.Port.Core.Sim.Combat.Grid;

/// <summary>
/// The six-word output block the world-grid subsystem fills through
/// <c>g_world_grid_output_ptr [0xF15E]</c> — three <c>i32</c> world coordinates.
/// </summary>
/// <remarks>
/// The pointer is a DGROUP NEAR offset and every write to it is DS-relative (byte-verified: none of
/// the six <c>mov [bx+n],</c> forms at <c>image@0x28E1C..0x28E55</c> carries a <c>0x26</c> ES
/// prefix).  In practice the callers point it at their own stack frame — the projectile row uses
/// <c>ss:[bp-0x24]</c> — which is DGROUP too because <c>SS == DS == DGROUP</c>.  The port
/// therefore models it as an explicit out-parameter rather than a DGROUP address.
/// </remarks>
public sealed class WorldGridOutputBlock
{
    /// <summary>Whether the caller supplied a pointer at all — the original's <c>[0xF15E] != 0</c>.</summary>
    public bool Enabled { get; set; }

    /// <summary>The six words, X lo/hi, Y lo/hi, Z lo/hi.</summary>
    public ushort[] Words { get; } = new ushort[6];

    /// <summary>How many times something wrote the block.</summary>
    public int Writes { get; set; }

    /// <summary>The block as a position triple.</summary>
    public CombatPosition Position => new(Axis(0), Axis(1), Axis(2));

    /// <summary>One axis as a signed 32-bit value.</summary>
    /// <param name="axis">0 = X, 1 = Y, 2 = Z.</param>
    /// <returns>The value.</returns>
    public int Axis(int axis) =>
        unchecked((int)(((uint)Words[(2 * axis) + 1] << 16) | Words[2 * axis]));

    /// <summary>Sets one axis from a 32-bit value.</summary>
    /// <param name="axis">0 = X, 1 = Y, 2 = Z.</param>
    /// <param name="value">The value.</param>
    public void SetAxis(int axis, int value)
    {
        Words[2 * axis] = unchecked((ushort)value);
        Words[(2 * axis) + 1] = unchecked((ushort)(value >> 16));
    }
}

/// <summary>
/// <c>world_object_frustum_clip_test</c> ENTRY4 <c>@image@0x28932</c> — the per-candidate hit test
/// the two list walkers and the query's own engagement scan call.
/// </summary>
/// <remarks>
/// <para>
/// <b>Semantic reframe (report-only, re-derived here from the
/// bytes):</b> despite the name this is NOT frustum-vs-object culling.  It clips the SEGMENT P1→P2
/// — the two world points the query staged into <c>[0xF160..0xF176]</c>, made object-relative —
/// against the CANDIDATE'S OWN bounding box (its class record's <c>+0x30..+0x47</c> planes, widened
/// by <c>±[0xF140/2]</c>), by Cohen–Sutherland.  It is a line-of-sight / shot-ray test.
/// </para>
/// <para><b>Two shipped quirks reproduced verbatim — do not "fix" either:</b></para>
/// <list type="number">
///   <item>
///     <b>Exhaustion ACCEPTS.</b>  <c>dec byte [0x2FD6]</c> reaching 0 jumps to the accept tail
///     (<c>image@0x28D7C</c>), never to a reject.
///   </item>
///   <item>
///     <b>The K-block bug.</b>  The arm entered when P1 is already inside (<c>oc1 == 0</c>)
///     dispatches on <c>[0x2FD4]</c> — P1's own outcode, zero by construction there — instead of
///     <c>[0x2FD5]</c>.  All six P2-clip arms are therefore dead code, and a call in state
///     (<c>oc1 == 0, oc2 != 0, oc1 &amp; oc2 == 0</c>) is a pure spin that drains the counter to 0
///     and accepts with P2 never clipped.
///   </item>
/// </list>
/// <para>
/// <b>Two arms this port does NOT implement</b> (both are tripwired rather than guessed): the
/// <c>image@0x28DBF</c> D1 projection call (never observed) and the <c>image@0x28D80</c> bisect
/// refinement through ENTRY1/ENTRY2 (0.036 % of 2.4 M recorded calls).
/// </para>
/// </remarks>
public static class WorldObjectFrustumClip
{
    /// <summary>The clip loop's initial counter (<c>mov byte [0x2fd6],0xf</c> <c>image@0x28B43</c>).</summary>
    public const int ClipIterations = 15;

    /// <summary>Outcode bit: the coordinate is below the axis's LOW plane.</summary>
    private const int OcXLow = 0x04, OcXHigh = 0x08, OcYHigh = 0x01, OcYLow = 0x02, OcZLow = 0x10, OcZHigh = 0x20;

    /// <summary>Runs the test for one candidate.</summary>
    /// <param name="state">The world-grid DGROUP block.</param>
    /// <param name="arena">The object pool.</param>
    /// <param name="staticData">The DGROUP surface (class records, engagement prototypes).</param>
    /// <param name="objectRef">The candidate's pool near offset — the original's <c>BX</c>.</param>
    /// <param name="output">The caller's output block, or null when <c>[0xF15E]</c> is 0.</param>
    /// <param name="counters">The arm census.</param>
    /// <returns><c>true</c> when the segment hits the candidate (the original's CF=1).</returns>
    public static bool Test(
        WorldGridState state,
        PoolArena arena,
        ICombatStaticData staticData,
        ushort objectRef,
        WorldGridOutputBlock? output,
        WorldGridCounters counters)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(staticData);
        ArgumentNullException.ThrowIfNull(counters);

        // ---- pretest (image@0x28935..0x2894A) ------------------------------------------------
        ushort classRef = arena.Word(objectRef);
        if (!WorldObjectAabbOverlap.Test(state, arena, staticData, objectRef, classRef))
        {
            counters.FrustumAabbReject++;
            return false;
        }

        // ---- position extraction, >>8-scaled (image@0x28950..0x2898F) -------------------------
        int objX = ScaledPosition(arena, objectRef, 0x06);
        int objY = ScaledPosition(arena, objectRef, 0x0A);
        int objZ = ScaledPosition(arena, objectRef, 0x0E);

        // ---- delta chain (image@0x28992..0x28A1A) ---------------------------------------------
        // Each axis is a 32-bit subtract whose result must equal the sign extension of its own low
        // word, or the call rejects: the clip only works in i16 object space.
        if (!Delta(state, 0, objX, out short p1x) ||
            !Delta(state, 1, objY, out short p1y) ||
            !Delta(state, 2, objZ, out short p1z) ||
            !Delta(state, 3, objX, out short p2x) ||
            !Delta(state, 4, objY, out short p2y) ||
            !Delta(state, 5, objZ, out short p2z))
        {
            counters.FrustumDeltaReject++;
            return false;
        }

        // ---- rotcode lookup + coordinate swap/negate (image@0x28A20..0x28AC6) -----------------
        WorldObjectFlags flags = (WorldObjectFlags)arena.Word((ushort)(objectRef + 0x02));
        ushort rotCode = 0;
        if (!flags.HasFlag(WorldObjectFlags.NoOrientation))
        {
            ushort rawRot = arena.Word((ushort)(objectRef + 0x12));
            if (rawRot != 0)
            {
                switch (rawRot)
                {
                    case 0x2D0:
                        (p1x, p1z) = (p1z, Negate(p1x));
                        (p2x, p2z) = (p2z, Negate(p2x));
                        rotCode = 0x2D0;
                        break;

                    case 0x5A0:
                        p1x = Negate(p1x);
                        p1z = Negate(p1z);
                        p2x = Negate(p2x);
                        p2z = Negate(p2z);
                        rotCode = 0x5A0;
                        break;

                    case 0x870:
                        (p1x, p1z) = (Negate(p1z), p1x);
                        (p2x, p2z) = (Negate(p2z), p2x);
                        rotCode = 0x870;
                        GuardProjectionArm(state, arena, staticData, objectRef, flags);
                        break;

                    default:
                        // Unknown code — NO transform at all, and the shared tail still runs.
                        rotCode = rawRot;
                        GuardProjectionArm(state, arena, staticData, objectRef, flags);
                        break;
                }
            }
        }

        // ---- plane constants (image@0x28AC9..0x28B3B) ------------------------------------------
        int half = state.HalfRange;
        short planeXLow = Plane(staticData, classRef, WorldObjectClassRecord.BoxXMinOffset, -half);
        short planeXHigh = Plane(staticData, classRef, WorldObjectClassRecord.BoxXMaxOffset, half);
        short planeYLow = Plane(staticData, classRef, WorldObjectClassRecord.BoxYMinOffset, -half);
        short planeYHigh = Plane(staticData, classRef, WorldObjectClassRecord.BoxYMaxOffset, half);
        short planeZLow = Plane(staticData, classRef, WorldObjectClassRecord.BoxZMinOffset, -half);
        short planeZHigh = Plane(staticData, classRef, WorldObjectClassRecord.BoxZMaxOffset, half);

        // ---- Cohen-Sutherland loop (image@0x28B3B..0x28D7C) -----------------------------------
        byte oc1 = Outcode(p1x, p1y, p1z, planeXLow, planeXHigh, planeYLow, planeYHigh, planeZLow, planeZHigh);
        byte oc2 = Outcode(p2x, p2y, p2z, planeXLow, planeXHigh, planeYLow, planeYHigh, planeZLow, planeZHigh);
        int counter = ClipIterations;
        bool accepted;

        while (true)
        {
            if ((oc1 | oc2) == 0)
            {
                counters.FrustumTrivialAccept++;
                accepted = true;
                break;
            }

            counter--;
            if (counter == 0)
            {
                // quirk 1: exhaustion ACCEPTS.
                counters.FrustumExhaustionAccept++;
                accepted = true;
                break;
            }

            if ((oc1 & oc2) != 0)
            {
                counters.FrustumTrivialReject++;
                accepted = false;
                break;
            }

            short dx = unchecked((short)(p2x - p1x));
            short dy = unchecked((short)(p2y - p1y));
            short dz = unchecked((short)(p2z - p1z));

            if (oc1 != 0)
            {
                bool ok;
                if ((oc1 & OcXLow) != 0)
                {
                    ok = ClipArm(planeXLow, ref p1x, ref p1y, ref p1z, dx, dy, dz);
                }
                else if ((oc1 & OcXHigh) != 0)
                {
                    ok = ClipArm(planeXHigh, ref p1x, ref p1y, ref p1z, dx, dy, dz);
                }
                else if ((oc1 & OcYHigh) != 0)
                {
                    ok = ClipArm(planeYHigh, ref p1y, ref p1x, ref p1z, dy, dx, dz);
                }
                else if ((oc1 & OcYLow) != 0)
                {
                    ok = ClipArm(planeYLow, ref p1y, ref p1x, ref p1z, dy, dx, dz);
                }
                else if ((oc1 & OcZLow) != 0)
                {
                    ok = ClipArm(planeZLow, ref p1z, ref p1x, ref p1y, dz, dx, dy);
                }
                else if ((oc1 & OcZHigh) != 0)
                {
                    ok = ClipArm(planeZHigh, ref p1z, ref p1x, ref p1y, dz, dx, dy);
                }
                else
                {
                    ok = true;   // structurally unreachable: oc1 is composed only of those six bits.
                }

                if (!ok)
                {
                    throw new WorldGridSeamException(
                        "world_object_frustum_clip_test @image@0x28C0x clip arm faulted: the original's "
                            + "IDIV would raise #DE (zero divisor or a quotient outside i16).  The "
                            + "port declines this case; it has no model for the #DE "
                            + "handler.  Report the candidate and the staged frustum.");
                }

                oc1 = Outcode(
                    p1x, p1y, p1z, planeXLow, planeXHigh, planeYLow, planeYHigh, planeZLow, planeZHigh);
            }
            else
            {
                // quirk 2 (the K-block bug): dispatches on oc1 (== 0 here), so no arm fires.
                counters.FrustumDeadP2Arm++;
                oc2 = Outcode(
                    p2x, p2y, p2z, planeXLow, planeXHigh, planeYLow, planeYHigh, planeZLow, planeZHigh);
            }
        }

        if (!accepted)
        {
            counters.RejectFrustum++;
            return false;
        }

        // ---- post-clip: the bisect valve (image@0x28D80) ---------------------------------------
        ushort bisectGrid = staticData.Word(classRef + WorldObjectClassRecord.BisectGridOffset);
        if (bisectGrid != 0)
        {
            throw new WorldGridSeamException(
                "world_object_frustum_clip_test @image@0x28D80: the candidate's class record carries a "
                    + $"bisect-refinement grid ([+0x48] = 0x{bisectGrid:X4}), which routes the accept "
                    + "through ENTRY1/ENTRY2 (image@0x28836/0x2889E).  Neither this port nor "
                    + "this port models that refinement (0.036 % of the recorded sorties); see "
                    + " §2.");
        }

        // ---- rotcode undo, P1 only (image@0x28D8F..0x28DD1) ------------------------------------
        switch (rotCode)
        {
            case 0: break;
            case 0x2D0: (p1x, p1z) = (Negate(p1z), p1x); break;
            case 0x5A0: p1x = Negate(p1x); p1z = Negate(p1z); break;
            case 0x870: (p1x, p1z) = (p1z, Negate(p1x)); break;
            default: break;   // unknown code — no transform, exactly as on the way in.
        }

        // ---- output writeback (image@0x28E00..0x28E55) ------------------------------------------
        if (output is not null && output.Enabled)
        {
            output.SetAxis(0, unchecked(p1x + objX) << 8);
            output.SetAxis(1, unchecked(p1y + objY) << 8);
            output.SetAxis(2, unchecked(p1z + objZ) << 8);
            output.Writes++;
            counters.FrustumOutputWritten++;
        }

        return true;
    }

    /// <summary>
    /// The <c>image@0x28DBF</c> arm the port declines: a sub-object whose engagement
    /// prototype is marked <c>[+0x0C] &amp; 0x20</c> makes the clip call a projection routine this
    /// port does not model.  Measured 0 occurrences on the recorded sorties.
    /// </summary>
    private static void GuardProjectionArm(
        WorldGridState state, PoolArena arena, ICombatStaticData staticData, ushort objectRef,
        WorldObjectFlags flags)
    {
        _ = state;
        if (!flags.HasFlag(WorldObjectFlags.CarriesEngagement))
        {
            return;
        }

        ushort deref = arena.Word((ushort)(objectRef + 0x18));
        if ((staticData.Word(deref + 0x0C) & 0x20) == 0)
        {
            return;
        }

        throw new WorldGridSeamException(
            "world_object_frustum_clip_test @image@0x28DBF reached the projection arm (object "
                + $"0x{objectRef:X4}, deref 0x{deref:X4}, prototype [+0x0C] bit5 set).  The verified "
                + "lift measured 0 occurrences of this arm on every recorded sortie and declines "
                + "it; the port has no model for it either.");
    }

    private static short Negate(short v) => unchecked((short)-v);

    private static int ScaledPosition(PoolArena arena, ushort objectRef, int offset)
    {
        ushort lo = arena.Word((ushort)(objectRef + offset));
        ushort hi = arena.Word((ushort)(objectRef + offset + 2));
        // `mov dl,dh; mov dh,al; mov al,ah; cbw` — bit-exact to an arithmetic (i32)raw >> 8.
        return unchecked((int)(((uint)hi << 16) | lo)) >> 8;
    }

    private static bool Delta(WorldGridState state, int axisIndex, int objScaled, out short low)
    {
        ushort pointLo = state.Frustum(2 * axisIndex);
        ushort pointHi = state.Frustum((2 * axisIndex) + 1);
        int point = unchecked((int)(((uint)pointHi << 16) | pointLo));
        int delta = unchecked(point - objScaled);
        low = unchecked((short)delta);

        // `cwd; cmp cx,dx; jne reject` — the 32-bit result must BE the sign extension of its low word.
        return delta == low;
    }

    private static short Plane(ICombatStaticData staticData, ushort classRef, int fieldOffset, int adjust)
    {
        int v = WorldObjectClassRecord.Int32(staticData, classRef, fieldOffset);
        return unchecked((short)(unchecked(v + adjust) >> 8));
    }

    private static byte Outcode(
        short x, short y, short z,
        short xLow, short xHigh, short yLow, short yHigh, short zLow, short zHigh)
    {
        byte code = 0;
        if (x < xLow) { code |= OcXLow; }
        if (x > xHigh) { code |= OcXHigh; }
        if (y < yLow) { code |= OcYLow; }
        if (y > yHigh) { code |= OcYHigh; }
        if (z < zLow) { code |= OcZLow; }
        if (z > zHigh) { code |= OcZHigh; }
        return code;
    }

    /// <summary>
    /// One clip arm (<c>image@0x28B8B</c> and its five twins — byte-identical in shape):
    /// <c>t = plane - clipped; otherA += (t*deltaA)/divisor; otherB += (t*deltaB)/divisor;
    /// clipped = plane</c>, with <c>IMUL</c>/<c>IDIV</c> semantics (truncating division).
    /// </summary>
    private static bool ClipArm(
        short plane, ref short clipped, ref short otherA, ref short otherB,
        short divisor, short deltaA, short deltaB)
    {
        if (divisor == 0)
        {
            return false;   // the original's IDIV raises #DE.
        }

        short t = unchecked((short)(plane - clipped));

        long qA = (long)((int)t * deltaA) / divisor;
        if (qA is > short.MaxValue or < short.MinValue)
        {
            return false;   // IDIV quotient overflow is also #DE on 8086.
        }

        long qB = (long)((int)t * deltaB) / divisor;
        if (qB is > short.MaxValue or < short.MinValue)
        {
            return false;
        }

        otherA = unchecked((short)(otherA + (short)qA));
        otherB = unchecked((short)(otherB + (short)qB));
        clipped = plane;
        return true;
    }
}
