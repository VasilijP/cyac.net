namespace CYAC.Port.Core.Sim.Combat.Effects;

/// <summary>
/// The SMOKE-PUFF pool — 15 pre-allocated world objects at <c>smoke_table [0xB8B0]</c> that the effect subsystems
/// switch on and off.
/// </summary>
/// <remarks>
/// <para>
/// The 15 × 9-byte slots live at <c>[0xB8B2..0xB930]</c> with the live count at <c>[0xB8B0]</c>
/// (<c>gx_subsystem_init_smoke @image@0x0B086</c> fills them by allocating 15 pool objects from the
/// class record <c>0x9AFA</c>, whose <c>+0x22</c> name string is literally <b>"smoke"</b>).
/// A puff is not a particle system: it is a POOL OBJECT with its active bit toggled, so the moment
/// <see cref="Allocate"/> sets that bit the renderer's own pool walk draws it, for free.
/// </para>
/// <para>Slot layout (<c>s_smoke_slot</c>):</para>
/// <list type="table">
///   <item><term><c>+0</c></term><description><c>u16</c> the pool object's near offset</description></item>
///   <item><term><c>+2</c></term><description><c>u16</c> the rising velocity, grown per frame and capped</description></item>
///   <item><term><c>+4</c></term><description><c>u16</c> the birth frame (<c>[0xF0C8]</c>) — the LRU key</description></item>
///   <item><term><c>+6</c></term><description><c>u16</c> the expiry frame</description></item>
///   <item><term><c>+8</c></term><description><c>u8</c> the kind (0 = a stationary puff, 3 = a trail, else a burst)</description></item>
/// </list>
/// </remarks>
public static class SmokePuffTable
{
    /// <summary><c>g_smoke_live_count [0xB8B0]</c>.</summary>
    public const int LiveCount = 0xB8B0;

    /// <summary>The FIRST slot (<c>cmp si,0xB8B2 / jae</c> @<c>image@0x0B128</c>).</summary>
    public const int FirstSlot = 0xB8B2;

    /// <summary>The LAST slot (<c>mov si,0xB930</c> @<c>image@0x0B104</c>).</summary>
    public const int LastSlot = 0xB930;

    /// <summary>One slot's stride: 9 bytes.</summary>
    public const int SlotStride = 9;

    /// <summary>How many slots there are: 15.</summary>
    public const int SlotCount = ((LastSlot - FirstSlot) / SlotStride) + 1;

    /// <summary>
    /// The LRU scan's own lower bound (<c>cmp bx,0xB8BB / jae</c> @<c>image@0x0B150</c>) — ONE SLOT
    /// HIGHER than the allocation scan's, so slot <c>0xB8B2</c> can never be evicted.  Shipped
    /// behaviour, reproduced.
    /// </summary>
    public const int LruLowestSlot = 0xB8BB;

    /// <summary>
    /// The class record every puff is instantiated from — a DGROUP near pointer to
    /// <c>image@0x4585A</c>, whose <c>+0x22</c> is the string "smoke"
    /// (<c>mov word [bp-0x18],0x9AFA</c> @<c>image@0x0B09A</c>).
    /// </summary>
    public const ushort SmokeClassRecord = 0x9AFA;

    /// <summary>Frames a kind-0 puff lives: <c>0x2D</c> (<c>image@0x0B1BE</c>).</summary>
    public const int LifetimeDefault = 0x2D;

    /// <summary>Frames a kind-3 puff lives: <c>0x19</c> (<c>image@0x0B1C6</c>).</summary>
    public const int LifetimeTrail = 0x19;

    /// <summary>Frames every other kind lives: 5 (<c>image@0x0B1B6</c>).</summary>
    public const int LifetimeOther = 5;

    /// <summary>The constant vertical drift a moving puff gets per frame: <c>0x1C00</c>.</summary>
    public const int RiseRate = 0x1C00;

    /// <summary>How fast the horizontal velocity grows per frame: <c>0x200</c>.</summary>
    public const int SpreadGrowth = 0x200;

    /// <summary>Its cap: <c>0x1400</c> (<c>cmp word [di+2],0x1400 / jle</c> @<c>image@0x0B29F</c>).</summary>
    public const int SpreadCap = 0x1400;

    /// <summary>The per-frame tumble applied to all three euler words: <c>0x50</c>.</summary>
    public const int TumbleStep = 0x50;

    /// <summary>The BAM circle: 2,880 units (<c>angle_wrap_bam @image@0x18410</c>).</summary>
    public const int BamCircle = 0x0B40;

    /// <summary><c>g_master_frame_counter [0xF0C8]</c> — the clock every puff deadline is on.</summary>
    public const int MasterFrameCounter = 0xF0C8;

    /// <summary><c>g_scene_frame_dt_scaled [0xF11C]</c>.</summary>
    public const int FrameDt = 0xF11C;

    /// <summary>The 15 slot offsets, LAST first — the order every walker uses.</summary>
    public static IEnumerable<int> Slots()
    {
        for (int slot = LastSlot; slot >= FirstSlot; slot -= SlotStride)
        {
            yield return slot;
        }
    }

    /// <summary>
    /// <c>gx_subsystem_init_smoke @image@0x0B086</c> — allocate the 15 pool objects, once per session.
    /// </summary>
    /// <param name="registers">The combat register file.</param>
    /// <param name="allocate">
    /// The insert: <c>pool_insert_with_bbox_or(record, parent 0x8A)</c> — the caller supplies it so
    /// this file stays inside <c>Sim/Combat</c>.
    /// </param>
    /// <returns>How many slots were filled.</returns>
    public static int Initialise(CombatRegisters registers, Func<ushort, ushort> allocate)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(allocate);

        int filled = 0;
        foreach (int slot in Slots())
        {
            registers.SetWord(slot, allocate(SmokeClassRecord));
            filled++;
        }

        registers.SetWord(LiveCount, 0);                            // image@0x0B0BD
        return filled;
    }

    /// <summary>
    /// <c>smoke_expire_slot @image@0x0B1F3</c> — clear the puff's active bit and drop the live count.
    /// </summary>
    /// <param name="registers">The register file.</param>
    /// <param name="arena">The pool.</param>
    /// <param name="slot">The slot's DGROUP offset.</param>
    public static void Expire(CombatRegisters registers, PoolArena arena, int slot)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(arena);

        ushort obj = registers.Word(slot);
        if (obj != 0 && arena.Covers(obj, 4))
        {
            arena.SetByte((ushort)(obj + 2), (byte)(arena.Byte((ushort)(obj + 2)) & 0xFE));
        }

        registers.SetWord(LiveCount, unchecked((ushort)(registers.Word(LiveCount) - 1)));
    }

    /// <summary>
    /// <c>smoke_slot_alloc_and_fill @image@0x0B0FC</c> — light one puff at a point.
    /// </summary>
    /// <param name="registers">The register file.</param>
    /// <param name="arena">The pool.</param>
    /// <param name="position">The 12 bytes copied into the object's <c>+0x06</c> (three <c>i32</c>).</param>
    /// <param name="kind">The <c>AL</c> argument: the puff's own type byte.</param>
    /// <returns>The slot used, or −1 when every slot is busy and none can be evicted.</returns>
    /// <remarks>
    /// The free-slot scan takes the first slot whose object is INACTIVE
    /// (<c>test byte es:[bx+2],1</c> @<c>image@0x0B11E</c>); if there is none it evicts the LRU by
    /// birth frame <c>+4</c> — over the SHORTER range (see <see cref="LruLowestSlot"/>).
    /// </remarks>
    public static int Allocate(
        CombatRegisters registers, PoolArena arena, CombatPosition position, byte kind)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(arena);

        int slot = -1;
        foreach (int candidate in Slots())
        {
            ushort obj = registers.Word(candidate);
            if (obj == 0 || !arena.Covers(obj, 4))
            {
                continue;
            }

            if ((arena.Byte((ushort)(obj + 2)) & 1) == 0)
            {
                slot = candidate;
                break;
            }
        }

        if (slot < 0)
        {
            // image@0x0B137..0x0B15F — the LRU eviction.
            slot = FirstSlot;
            ushort oldest = registers.Word(FirstSlot + 4);
            for (int candidate = LastSlot; candidate >= LruLowestSlot; candidate -= SlotStride)
            {
                if (registers.Word(candidate + 4) < oldest)
                {
                    slot = candidate;
                    oldest = registers.Word(candidate + 4);
                }
            }

            Expire(registers, arena, slot);
        }

        ushort target = registers.Word(slot);
        if (target == 0 || !arena.Covers(target, 0x12))
        {
            return -1;
        }

        // image@0x0B16E..0x0B185 — twelve bytes into the object's +0x06, then the ACTIVE bit.
        new CombatObjectView(arena, target).Position = position;
        arena.SetByte((ushort)(target + 2), (byte)(arena.Byte((ushort)(target + 2)) | 1));

        ushort now = registers.Word(MasterFrameCounter);
        registers.SetWord(slot + 2, 0);                              // image@0x0B19A
        registers.SetWord(slot + 4, now);                            // image@0x0B1A2
        registers.SetWord(slot + 6, unchecked((ushort)(now + Lifetime(kind))));
        registers.SetByte(slot + 8, kind);                           // image@0x0B1D5
        registers.SetWord(LiveCount, unchecked((ushort)(registers.Word(LiveCount) + 1)));
        return slot;
    }

    /// <summary>How many frames a puff of one kind lives (<c>image@0x0B1A5..0x0B1C9</c>).</summary>
    /// <param name="kind">The puff's kind byte.</param>
    public static int Lifetime(byte kind) => kind switch
    {
        0 => LifetimeDefault,
        3 => LifetimeTrail,
        _ => LifetimeOther,
    };

    /// <summary>
    /// <c>smoke_per_frame_physics_step @image@0x0B20F</c> — one of the five effect ticks: expire
    /// the dead puffs, drift and tumble the live ones.
    /// </summary>
    /// <param name="registers">The register file.</param>
    /// <param name="arena">The pool.</param>
    /// <returns>How many puffs are still alive after the step.</returns>
    /// <remarks>
    /// <para>
    /// Per live puff: if <c>slot[+6] &lt;= [0xF0C8]</c> it expires; otherwise, when its kind is
    /// non-zero, its X gains <c>muldiv_shr8(slot[+2], dt)</c>, its Y gains
    /// <c>muldiv_shr8(0x1C00, dt)</c> and <c>slot[+2]</c> itself grows by
    /// <c>muldiv_shr8(0x200, dt)</c> capped at <c>0x1400</c> — so a puff rises, spreads and
    /// accelerates its spread.  Every live puff, kind or not, tumbles <c>0x50</c> BAM on all three
    /// euler words (<c>angle_wrap_bam @image@0x18410</c>).
    /// </para>
    /// </remarks>
    public static int PerFrameStep(CombatRegisters registers, PoolArena arena)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(arena);

        if (registers.Word(LiveCount) == 0)
        {
            return 0;                                                // image@0x0B217
        }

        ushort now = registers.Word(MasterFrameCounter);
        short dt = unchecked((short)registers.Word(FrameDt));
        int live = 0;
        foreach (int slot in Slots())
        {
            ushort obj = registers.Word(slot);
            if (obj == 0 || !arena.Covers(obj, 0x18) || (arena.Byte((ushort)(obj + 2)) & 1) == 0)
            {
                continue;                                            // image@0x0B238
            }

            if (registers.Word(slot + 6) <= now)
            {
                Expire(registers, arena, slot);                      // image@0x0B24A
                continue;
            }

            live++;
            CombatObjectView view = new CombatObjectView(arena, obj);
            if (registers.Byte(slot + 8) != 0)
            {
                CombatPosition position = view.Position;
                int spread = MulShr8(unchecked((short)registers.Word(slot + 2)), dt);
                position = new CombatPosition(
                    unchecked(position.X + spread),
                    unchecked(position.Y + MulShr8(unchecked((short)RiseRate), dt)),
                    position.Z);
                view.Position = position;

                int grown = registers.Word(slot + 2) + MulShr8(unchecked((short)SpreadGrowth), dt);
                registers.SetWord(
                    slot + 2,
                    unchecked((ushort)(grown > SpreadCap ? SpreadCap : grown)));
            }

            // image@0x0B2AB..0x0B2E9 — the tumble, three euler words, each wrapped to the BAM circle.
            for (int axis = 0; axis < 3; axis++)
            {
                ushort at = unchecked((ushort)(obj + 0x12 + (2 * axis)));
                arena.SetWord(at, WrapBam(unchecked((short)(arena.Word(at) + TumbleStep))));
            }
        }

        return live;
    }

    /// <summary>
    /// <c>image@0x11980</c> — <c>imul dx ; mov al,ah ; mov ah,dl</c>: the signed 16-bit product
    /// shifted right 8, truncated to a word, then sign-extended by the caller's <c>cwd</c>.
    /// </summary>
    /// <param name="a">The first factor.</param>
    /// <param name="b">The second.</param>
    public static int MulShr8(short a, short b)
    {
        int product = a * b;
        return unchecked((short)((product >> 8) & 0xFFFF));
    }

    /// <summary>
    /// <c>angle_wrap_bam @image@0x18410</c> — fold a signed angle into <c>[0, 0x0B40)</c> by
    /// repeated add/subtract, exactly as the original loops.
    /// </summary>
    /// <param name="angle">The unwrapped angle.</param>
    public static ushort WrapBam(short angle)
    {
        int value = angle;
        while (value < 0)
        {
            value += BamCircle;
        }

        while (value >= BamCircle)
        {
            value -= BamCircle;
        }

        return (ushort)value;
    }
}
