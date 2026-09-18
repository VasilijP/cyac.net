using CYAC.Port.Core.Model.Combat;

namespace CYAC.Port.Core.Sim.Combat.Effects;

/// <summary>
/// The GROUND-MARK pool <c>[0xBD18..0xBD30]</c> — five <c>crater</c> objects, LRU, planted on the
/// ground wherever something hit it.
/// </summary>
/// <remarks>
/// <para>
/// <c>gx_subsystem_init_5x06 @image@0x2DB2A</c> gives each of the five 6-byte slots a pool object
/// built from the STATIC template at DGROUP <c>[0x4720]</c> (<c>image@0x40480</c>), whose
/// <c>+0x00</c> is <c>0x52A2</c> — <c>g_class_record_crater</c>, render-layer priority <c>0x1E</c>,
/// the ground-decal class that sorts between <c>rural</c> and <c>river</c>.  So all five are
/// craters, and <c>subsystem5x06_per_frame_slot_fire @image@0x2DB69</c> is what puts one down.
/// </para>
/// <para>
/// <b>What the fire does</b> (<c>image@0x2DB69..0x2DC69</c>, four arguments = a world X and Z as two
/// <c>i32</c>): take the first slot whose object is INACTIVE walking <c>[0xBD30]</c> down; if none is
/// free, evict the one with the oldest <c>+0x02</c> timestamp — starting from <c>[0xBD18]</c> and ITS
/// stamp as the incumbent and then scanning <c>[0xBD30]</c> down to <c>[0xBD1E]</c>, so the first
/// slot is the DEFAULT candidate and the only one never re-compared (<c>mov si,0xBD18 / mov
/// ax,[0xBD1A]</c> @<c>image@0x2DBA1</c>, <c>cmp bx,0xBD1E / jae</c> @<c>image@0x2DBD9</c>) — the
/// same shipped asymmetry the deferred-effect pool has.  Then, if its graphics-detail byte is high
/// enough, give the crater a RANDOM heading (<c>prng_rand_bounded(0xB40)</c> @<c>image@0x2DBFB</c>)
/// and otherwise 0; write the X and Z, set <b>Y to zero</b> (<c>image@0x2DC1D</c> — a crater lies on
/// the ground plane, always); switch it on; queue it for the 8-frame pending-sprite pass; and stamp
/// the frame-time accumulator.
/// </para>
/// <para>
/// <b>Who fires it</b> — three doors matter to the kill sequence, all byte-verified:
/// <c>engagement_slot_impact_and_depart @image@0x08BB9</c> (a killed aircraft reaching the ground),
/// <c>per_object_tick @image@0x2C7BA</c> (an ejected pilot reaching it) and
/// <c>flight_engine_first_frame_arm @image@0x22613</c>.
/// </para>
/// </remarks>
public static class CraterPool
{
    /// <summary>The LAST slot (<c>mov si,0xBD30</c> @<c>image@0x2DB31</c>) — where both walks start.</summary>
    public const int LastSlot = 0xBD30;

    /// <summary>The FIRST (<c>cmp si,0xBD18 / jae</c> @<c>image@0x2DB95</c>).</summary>
    public const int FirstSlot = 0xBD18;

    /// <summary>One slot's stride: 6 bytes — an object near pointer and an <c>i32</c> stamp.</summary>
    public const int SlotStride = 6;

    /// <summary>How many slots: <b>5</b>.</summary>
    public const int SlotCount = ((LastSlot - FirstSlot) / SlotStride) + 1;

    /// <summary>
    /// The eviction scan's lower bound (<c>cmp bx,0xBD1E / jae</c> @<c>image@0x2DBD9</c>) — one
    /// slot higher than the free scan's.  <see cref="FirstSlot"/> is the scan's INCUMBENT, so it is
    /// chosen when nothing beats its own stamp and is never compared against itself.
    /// </summary>
    public const int EvictionLowestSlot = 0xBD1E;

    /// <summary>
    /// The template's <c>+0x00</c>: <c>g_class_record_crater [0x52A2]</c>.
    /// </summary>
    /// <remarks>
    /// <c>0x4720</c> is not a "magic" number and 0x8A is not a class tag:
    /// a static 24-byte pool TEMPLATE at <c>image@0x40480</c> whose first word is this class-record
    /// near pointer, and <c>0x8A</c> is <c>pool_insert_with_bbox_or</c>'s parent argument.
    /// </remarks>
    public const ushort CraterClassRecord = 0x52A2;

    /// <summary>The BAM circle a crater's random heading is drawn from: <c>0xB40</c>.</summary>
    /// <remarks>
    /// The original gates the draw on its graphics-detail byte and gives the crater heading 0 below
    /// the threshold (<c>image@0x2DBF1</c>).  The port always takes the RANDOM heading — the arm
    /// the shipped configuration runs — because nothing in <c>Sim/</c> may name a presentation
    /// setting (the determinism law), and because making the RNG draw
    /// conditional on a display option would make the simulation's stream depend on it.  A
    /// deliberate deviation, and the only one in this file.
    /// </remarks>
    public const ushort HeadingBamCircle = 0x0B40;

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
    /// <c>gx_subsystem_init_5x06 @image@0x2DB2A</c> — give the five slots their crater objects.
    /// </summary>
    /// <param name="registers">The combat register file.</param>
    /// <param name="allocate">
    /// The insert: <c>pool_insert_with_bbox_or(template, parent 0x8A)</c>, supplied by the caller so
    /// this file stays inside <c>Sim/Combat</c>.
    /// </param>
    /// <returns>How many objects were allocated.</returns>
    public static int Initialise(CombatRegisters registers, Func<ushort, ushort> allocate)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(allocate);

        // [0x4722]
        // (g_gx_subsystem_init_status_5x06) lies outside every combat register window, and nothing
        // in the ported surface reads it; the write is inert here.  Reported, not forced.
        int allocated = 0;
        foreach (int slot in Slots())
        {
            registers.SetWord(slot, allocate(CraterClassRecord));      // image@0x2DB46
            allocated++;
        }

        return allocated;
    }

    /// <summary>
    /// <c>subsystem5x06_per_frame_slot_fire @image@0x2DB69</c> — put a crater on the ground.
    /// </summary>
    /// <param name="registers">The register file.</param>
    /// <param name="arena">The pool arena.</param>
    /// <param name="x">World X, in position units (<c>world &lt;&lt; 8</c>).</param>
    /// <param name="z">World Z.</param>
    /// <param name="random">The PRNG the random heading is drawn from.</param>
    /// <returns>The slot used, or −1 when no slot has an object.</returns>
    public static int Fire(
        CombatRegisters registers, PoolArena arena, int x, int z, ICombatRandom? random = null)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(arena);

        int slot = -1;
        foreach (int candidate in Slots())                             // image@0x2DB81
        {
            ushort obj = registers.Word(candidate);
            if (obj != 0 && arena.Covers(obj, 4) && (arena.Byte((ushort)(obj + 2)) & 1) == 0)
            {
                slot = candidate;
                break;
            }
        }

        if (slot < 0)
        {
            // image@0x2DBA1..0x2DBE5 — evict the oldest, but never [0xBD18].
            slot = FirstSlot;
            uint oldest = ReadU32(registers, FirstSlot + 2);
            for (int candidate = LastSlot; candidate >= EvictionLowestSlot; candidate -= SlotStride)
            {
                uint stamp = ReadU32(registers, candidate + 2);
                if (stamp < oldest)
                {
                    slot = candidate;
                    oldest = stamp;
                }
            }

            ushort victim = registers.Word(slot);
            if (victim != 0 && arena.Covers(victim, 4))
            {
                // effect_slot_pool_entry_deactivate @image@0x2DC6A
                arena.SetByte(
                    (ushort)(victim + 2), (byte)(arena.Byte((ushort)(victim + 2)) & 0xFE));
            }
        }

        ushort target = registers.Word(slot);
        if (target == 0 || !arena.Covers(target, 0x18))
        {
            return -1;
        }

        // image@0x2DBFB — a random heading about the BAM circle (see HeadingBamCircle's remarks
        // for why the original's detail gate is not reproduced).
        ushort heading = random is not null
            ? unchecked((ushort)random.RandBounded(HeadingBamCircle))
            : (ushort)0;
        arena.SetWord((ushort)(target + 0x12), heading);

        // image@0x2DC0D..0x2DC2F — X and Z as given, and Y ZERO: a crater lies on the ground.
        new CombatObjectView(arena, target).Position =
            new CombatPosition(x, 0, z);
        arena.SetByte((ushort)(target + 2), (byte)(arena.Byte((ushort)(target + 2)) | 1));

        WriteU32(registers, slot + 2, ReadU32(registers, DeferredEffectPool.FrameTimeAccumulator));
        return slot;
    }

    /// <summary>
    /// <c>subsystem5x06_table_deactivate_all @image@0x2DB51</c> — clear every crater, for a scene
    /// reset.
    /// </summary>
    /// <param name="registers">The register file.</param>
    /// <param name="arena">The pool arena.</param>
    /// <returns>How many were switched off.</returns>
    public static int DeactivateAll(CombatRegisters registers, PoolArena arena)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(arena);

        int cleared = 0;
        foreach (int slot in Slots())
        {
            ushort obj = registers.Word(slot);
            if (obj != 0 && arena.Covers(obj, 4) && (arena.Byte((ushort)(obj + 2)) & 1) != 0)
            {
                arena.SetByte((ushort)(obj + 2), (byte)(arena.Byte((ushort)(obj + 2)) & 0xFE));
                cleared++;
            }
        }

        return cleared;
    }

    /// <summary>How many craters are on the ground right now.</summary>
    /// <param name="registers">The register file.</param>
    /// <param name="arena">The pool arena.</param>
    /// <returns>The count, 0…5.</returns>
    public static int LiveCount(CombatRegisters registers, PoolArena arena)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(arena);

        int live = 0;
        foreach (int slot in Slots())
        {
            ushort obj = registers.Word(slot);
            if (obj != 0 && arena.Covers(obj, 4) && (arena.Byte((ushort)(obj + 2)) & 1) != 0)
            {
                live++;
            }
        }

        return live;
    }

    private static uint ReadU32(CombatRegisters registers, int at) =>
        (uint)(registers.Word(at) | (registers.Word(at + 2) << 16));

    private static void WriteU32(CombatRegisters registers, int at, uint value)
    {
        registers.SetWord(at, unchecked((ushort)value));
        registers.SetWord(at + 2, unchecked((ushort)(value >> 16)));
    }
}
