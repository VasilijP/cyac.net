using CYAC.Port.Core.Model.Combat;
using CYAC.Port.Core.Primitives;
using CYAC.Port.Core.Sim.Session;

namespace CYAC.Port.Core.Sim.Combat.Effects;

/// <summary>
/// The CHAFF / FLARE cloud pool — five <c>0x1C</c>-byte slots at <c>g_countermeasure_table_BASE
/// [0xB82E]</c> descending to <c>[0xB7BE]</c>, each owning one pool object, and the spawner that
/// lights one.
/// </summary>
/// <remarks>
/// <para>
/// Batch 7 H16 seam 2 (H14 O8 / H15 queue).  Until this landed,
/// <c>PortedPlayerCombatEvents.SpawnCountermeasureCloud</c> returned 0 for everyone, so
/// <c>Countermeasures.Deploy</c> took its pool-full arm (<c>image@0x0AF9F</c>): no cloud, no stock
/// decrement, no decoy window — the cockpit's counters sat at 15/15 for ever.
/// </para>
/// <para>
/// The slot layout is KNOWN_FIELDS["s_countermeasure_slot"]</c>, byte-proven from this spawner's own
/// stores: <c>+0x00</c> type tag (1 chaff, 2 flare), <c>+0x02</c> the pool object's near pointer,
/// <c>+0x04</c> <c>vel_x_i32</c>, <c>+0x08</c> <c>vel_alt_i32</c>, <c>+0x0C</c> <c>vel_y_i32</c>,
/// <c>+0x10</c> the raw spawn frame time, <c>+0x14</c> spawn + <c>0x500</c> (the despawn deadline)
/// and <c>+0x18</c> spawn + <c>0x100</c> (after which friction starts).
/// </para>
/// </remarks>
public static class CountermeasurePool
{
    /// <summary>The LAST slot — <c>mov si,0xb82e</c> @<c>image@0x0AB89</c> / <c>image@0x0ABCB</c>.</summary>
    public const int LastSlot = 0xB82E;

    /// <summary>The scan's lower bound — <c>cmp si,0xb7be / jae</c> @<c>image@0x0ABA1</c>.</summary>
    public const int FirstSlot = 0xB7BE;

    /// <summary>One slot's stride: <c>0x1C</c> = 28 bytes (<c>sub si,0x1c</c> @<c>image@0x0AB9E</c>).</summary>
    public const int SlotStride = 0x1C;

    /// <summary>How many slots: <b>5</b>.</summary>
    public const int SlotCount = ((LastSlot - FirstSlot) / SlotStride) + 1;

    /// <summary>
    /// The class record every slot's object is BORN with: <c>0x5080</c> = <c>chaff</c>
    /// (<c>mov word [bp-0x18],0x5080</c> @<c>image@0x0AB84</c>).
    /// </summary>
    public const ushort ChaffClassRecord = 0x5080;

    /// <summary>
    /// The class record a FLARE spawn re-stamps the object with: <c>0x6B34</c> = <c>flare</c>
    /// (<c>mov word es:[di],0x6b34</c> @<c>image@0x0AC76</c>; the chaff arm writes
    /// <c>0x5080</c> back @<c>image@0x0AC80</c>).
    /// </summary>
    public const ushort FlareClassRecord = 0x6B34;

    /// <summary>
    /// The despawn window in frame-time units: <c>add ax,0x500</c> @<c>image@0x0AC92</c>.
    /// </summary>
    public const int DespawnFrameTime = 0x500;

    /// <summary>
    /// The ballistic window: <c>add ax,0x100</c> @<c>image@0x0ACA5</c> — friction only starts after
    /// it (<c>image@0x0AE40</c>), which the port does not model.
    /// </summary>
    public const int BallisticFrameTime = 0x100;

    /// <summary>
    /// The Y component the ejection vector is built from before rotation: <c>0x4B00</c> = 19,200
    /// (<c>mov word [bp-6],0x4b00</c> @<c>image@0x0ACB6</c>), shifted left 2 into the slot, so the
    /// cloud leaves at ±76,800 position units per second along the launch heading.
    /// </summary>
    public const short EjectSpeed = 0x4B00;

    /// <summary>The five slot offsets, LAST first — the order both walks take.</summary>
    /// <returns>The slots.</returns>
    public static IEnumerable<int> Slots()
    {
        for (int slot = LastSlot; slot >= FirstSlot; slot -= SlotStride)
        {
            yield return slot;
        }
    }

    /// <summary>
    /// <c>gx_subsystem_init_5x1C @image@0x0AB70</c> — give the five slots their cloud objects.
    /// </summary>
    /// <param name="table">The five slots' bytes.</param>
    /// <param name="allocate">
    /// The insert: <c>pool_insert_with_bbox_or(template, parent 0x8A)</c>, supplied by the caller so
    /// this file stays inside <c>Sim/Combat</c> — the same seam <see cref="CraterPool.Initialise"/>
    /// takes.
    /// </param>
    /// <returns>How many objects were allocated.</returns>
    /// <remarks>
    /// All five are born from the CHAFF record and inactive (the insert never sets the active bit);
    /// a flare spawn re-stamps the class word on the spot.
    /// </remarks>
    public static int Initialise(CountermeasureTable table, Func<ushort, ushort> allocate)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(allocate);

        int allocated = 0;
        foreach (int slot in Slots())
        {
            table.SetWord(slot + 0x02, allocate(ChaffClassRecord));       // image@0x0AB9B
            allocated++;
        }

        return allocated;
    }

    /// <summary>
    /// <c>countermeasure_cloud_spawn @image@0x0ABC3</c> — light one cloud at the player.
    /// </summary>
    /// <param name="table">The five slots' bytes.</param>
    /// <param name="arena">The pool arena.</param>
    /// <param name="kind">1 = chaff, 2 = flare — the original's <c>[bp+6]</c>.</param>
    /// <param name="heading">The wrapped launch heading — <c>[bp+8]</c>.</param>
    /// <param name="originRef">
    /// The position triple's near offset — <c>[bp+0x0A]</c>, which both dispensers pass as the
    /// player object <c>+6</c>.
    /// </param>
    /// <param name="frameTimeAccum">
    /// <c>g_frame_time_accum [0xF0D2:0xF0D4]</c> as an unsigned 32-bit value.
    /// </param>
    /// <returns>The cloud's pool near offset, or 0 when every slot's object is busy.</returns>
    /// <remarks>
    /// Ported store for store from <c>image@0x0ABCB..0x0AD0C</c>.  The two calls the port does not
    /// model are both presentation or recording: <c>pending_sprite_slot_enqueue(0xC330, obj)</c>
    /// @<c>image@0x0AC63</c> (the renderer builds its own list) and
    /// <c>film_record_countermeasure_spawn @image@0x30DD4</c> @<c>image@0x0AD04</c> (no film).
    /// </remarks>
    public static ushort Spawn(
        CountermeasureTable table,
        PoolArena arena,
        byte kind,
        short heading,
        ushort originRef,
        uint frameTimeAccum)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(arena);

        // image@0x0ABDB..0x0ABFA — the first slot whose object is INACTIVE, walking DOWN.
        int slot = -1;
        foreach (int candidate in Slots())
        {
            ushort obj = table.Word(candidate + 0x02);
            if (obj != 0 && arena.Covers(obj, 4) && (arena.Byte((ushort)(obj + 0x02)) & 1) == 0)
            {
                slot = candidate;
                break;
            }
        }

        if (slot < 0)
        {
            return 0;                                                     // image@0x0ABFC
        }

        ushort cloud = table.Word(slot + 0x02);                       // image@0x0AC01

        // image@0x0AC1A..0x0AC55 — copy the origin's three i32 into the object's +6/+0xA/+0xE.
        arena.SetWord((ushort)(cloud + 0x06), arena.Word(originRef));
        arena.SetWord((ushort)(cloud + 0x08), arena.Word((ushort)(originRef + 2)));
        arena.SetWord((ushort)(cloud + 0x0A), arena.Word((ushort)(originRef + 4)));
        arena.SetWord((ushort)(cloud + 0x0C), arena.Word((ushort)(originRef + 6)));
        arena.SetWord((ushort)(cloud + 0x0E), arena.Word((ushort)(originRef + 8)));
        arena.SetWord((ushort)(cloud + 0x10), arena.Word((ushort)(originRef + 10)));

        // image@0x0AC59 — switch it on.
        arena.SetByte((ushort)(cloud + 0x02), (byte)(arena.Byte((ushort)(cloud + 0x02)) | 1));

        // image@0x0AC6A..0x0AC84 — the type tag, and the object's class word.
        table.SetByte(slot, kind);
        arena.SetWord(cloud, kind == 2 ? FlareClassRecord : ChaffClassRecord);

        // image@0x0AC85..0x0ACB0 — the three stamps.
        SetU32(table, slot + 0x10, frameTimeAccum);
        SetU32(table, slot + 0x14, unchecked(frameTimeAccum + DespawnFrameTime));
        SetU32(table, slot + 0x18, unchecked(frameTimeAccum + BallisticFrameTime));

        // image@0x0ACB1..0x0ACF3 — the ejection velocity: rotate (0, 0x4B00) about the DGROUP pivot
        // [0x0680] (which is {0, 0}) by the launch heading, then << 2 into the slot's two i32.
        short vx = 0;
        short vy = EjectSpeed;
        Vector2Rotate.RotateInPlace(heading, 0, 0, ref vx, ref vy);
        SetU32(table, slot + 0x04, unchecked((uint)(vx << 2)));
        SetU32(table, slot + 0x0C, unchecked((uint)(vy << 2)));
        SetU32(table, slot + 0x08, 0);                                // image@0x0ACEF/0x0ACF2

        return cloud;                                                     // image@0x0AD0C
    }

    /// <summary>
    /// <c>countermeasure_cloud_per_frame_drift @image@0x0AD24</c> — the DESPAWN half only.
    /// </summary>
    /// <param name="table">The five slots' bytes.</param>
    /// <param name="arena">The pool arena.</param>
    /// <param name="frameTimeAccum">The frame-time accumulator.</param>
    /// <returns>How many clouds this frame switched off.</returns>
    /// <remarks>
    /// <para>
    /// PARTIAL BY DECLARATION.  The original's tick is 483 bytes: a random attitude per frame above
    /// graphics detail 2 (three <c>prng_rand_bounded(0xB40)</c> draws,
    /// <c>image@0x0AD7E..0x0ADB1</c>), gravity on <c>vel_alt</c>, friction after the ballistic
    /// window and the position integration.  What is ported here is the arm that decides a cloud is
    /// FINISHED (<c>image@0x0AD4C..0x0AD76</c>) and calls <c>countermeasure_cloud_deactivate
    /// @image@0x0AD15</c> — because without it the five slots never come back and the sixth
    /// deployment of a sortie would fail for ever.  The ballistics stay counted in
    /// <see cref="EffectCensus.CountermeasureDriftsSkipped"/>, which is what that counter has always
    /// named; a cloud therefore hangs where it was dispensed instead of falling away.  The PRNG
    /// draws are deliberately NOT taken: a partial tick that consumed the shared stream would move
    /// every later draw.
    /// </para>
    /// <para>
    /// The two conditions, byte for byte: the frame time has passed <c>+0x14</c>
    /// (<c>cmp [di+0x16],dx / jl</c> … <c>cmp [di+0x14],ax / jb</c>, <c>image@0x0AD53..0x0AD5D</c>),
    /// or the object's own Y <c>i32</c> at <c>+0x0A</c> has fallen to zero or below
    /// (<c>cmp es:[si+0xc],0 / jg / jl / cmp es:[si+0xa],0 / jne</c>,
    /// <c>image@0x0AD62..0x0AD70</c>) — the cloud reached the ground.
    /// </para>
    /// </remarks>
    public static int Expire(CountermeasureTable table, PoolArena arena, uint frameTimeAccum)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(arena);

        int expired = 0;
        foreach (int slot in Slots())
        {
            ushort cloud = table.Word(slot + 0x02);
            if (cloud == 0 || !arena.Covers(cloud, 0x12)
                || (arena.Byte((ushort)(cloud + 0x02)) & 1) == 0)
            {
                continue;                                                 // image@0x0AD3B
            }

            bool done = ReadU32(table, slot + 0x14) < frameTimeAccum;  // image@0x0AD53
            if (!done)
            {
                int y = unchecked((int)(arena.Word((ushort)(cloud + 0x0A))
                    | (arena.Word((ushort)(cloud + 0x0C)) << 16)));
                done = y <= 0;                                            // image@0x0AD62
            }

            if (done)
            {
                arena.SetByte(
                    (ushort)(cloud + 0x02),
                    (byte)(arena.Byte((ushort)(cloud + 0x02)) & 0xFE));   // image@0x0AD1E
                expired++;
            }
        }

        return expired;
    }

    private static uint ReadU32(CountermeasureTable table, int offset) =>
        unchecked((uint)(table.Word(offset) | (table.Word(offset + 2) << 16)));

    private static void SetU32(CountermeasureTable table, int offset, uint value)
    {
        table.SetWord(offset, unchecked((ushort)value));
        table.SetWord(offset + 2, unchecked((ushort)(value >> 16)));
    }
}

/// <summary>
/// The 140 bytes of DGROUP the five countermeasure slots occupy — <c>[0xB7BE..0xB849]</c> — held
/// port-side rather than in <see cref="CombatRegisters"/>.
/// </summary>
/// <remarks>
/// <para>
/// WHY IT IS NOT IN THE REGISTER FILE (a real defect found by running it): the declared combat-register
/// window <c>countermeasure_table</c> is <c>0xB7BE</c> + <b>114</b> bytes, i.e. <c>0xB7BE..0xB82F</c> —
/// but the table is FIVE slots of <c>0x1C</c> descending from <c>0xB82E</c> (<c>mov si,0xb82e</c>
/// @<c>image@0x0AB89</c>, <c>sub si,0x1c</c> @<c>image@0x0AB9E</c>, <c>cmp si,0xb7be / jae</c>
/// @<c>image@0x0ABA1</c>), so it really runs to <c>0xB849</c> = <b>140</b> bytes.  The last slot's own
/// <c>+0x02</c> (<c>0xB830</c>) is already past the window, and the very first write of
/// <c>gx_subsystem_init_5x1C</c> throws.
/// </para>
/// <para>
/// The window's LENGTH is wire format — <c>CombatRegistersCodec.LayoutMatchesDeclaration</c> checks it
/// against the header of every recorded <c>.ctr</c> trace, so widening it would invalidate the recorded
/// verification corpus.  The honest move is to keep the bytes here, addressed by their real DGROUP
/// offsets, and report the 26-byte shortfall.  Nothing verified reads them: the window is declared
/// with <c>0 fn(s)</c> and no ported kernel body touches the table.
/// </para>
/// </remarks>
public sealed class CountermeasureTable
{
    private readonly byte[] _bytes = new byte[Bytes];

    /// <summary>The table's DGROUP base: <c>0xB7BE</c>.</summary>
    public const int Base = CountermeasurePool.FirstSlot;

    /// <summary>Its real length: 5 x 0x1C = <b>140</b> bytes, to <c>0xB849</c> inclusive.</summary>
    public const int Bytes = CountermeasurePool.SlotCount * CountermeasurePool.SlotStride;

    /// <summary>Reads one word by DGROUP offset.</summary>
    /// <param name="dgroupOffset">The offset.</param>
    /// <returns>The word.</returns>
    public ushort Word(int dgroupOffset) =>
        unchecked((ushort)(_bytes[dgroupOffset - Base] | (_bytes[dgroupOffset - Base + 1] << 8)));

    /// <summary>Writes one word by DGROUP offset.</summary>
    /// <param name="dgroupOffset">The offset.</param>
    /// <param name="value">The word.</param>
    public void SetWord(int dgroupOffset, ushort value)
    {
        _bytes[dgroupOffset - Base] = unchecked((byte)value);
        _bytes[dgroupOffset - Base + 1] = unchecked((byte)(value >> 8));
    }

    /// <summary>Reads one byte by DGROUP offset.</summary>
    /// <param name="dgroupOffset">The offset.</param>
    /// <returns>The byte.</returns>
    public byte Byte(int dgroupOffset) => _bytes[dgroupOffset - Base];

    /// <summary>Writes one byte by DGROUP offset.</summary>
    /// <param name="dgroupOffset">The offset.</param>
    /// <param name="value">The byte.</param>
    public void SetByte(int dgroupOffset, byte value) => _bytes[dgroupOffset - Base] = value;
}
