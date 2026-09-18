using CYAC.Port.Core.Model.Combat;

namespace CYAC.Port.Core.Sim.Combat.Effects;

/// <summary>
/// The 4-row EFFECT-EMITTER table <c>subsystem4x19_table [0xB84C..0xB897]</c> — the thing that makes a damaged
/// aircraft trail smoke and a hit puff.
/// </summary>
/// <remarks>
/// <para>
/// A row is a REPEATING EMITTER, not a particle: it names an owner (a pool object) or a fixed point,
/// and every <c>interval</c> frames it lights one <see cref="SmokePuffTable"/> puff at that owner's
/// current position until its lifetime runs out.  That is why the original needs only 15 puffs and
/// 4 emitters to draw a smoking aeroplane.
/// </para>
/// <para>Row layout (<c>s_4x19_effect_slot</c>, 0x19 = 25 bytes):</para>
/// <list type="table">
///   <item><term><c>+0x00</c></term><description><c>u8</c> active</description></item>
///   <item><term><c>+0x01</c></term><description><c>u8</c> the puff KIND handed to <c>smoke_slot_alloc_and_fill</c></description></item>
///   <item><term><c>+0x02</c></term><description><c>u8</c> a classifier the LRU scan skips on when non-zero</description></item>
///   <item><term><c>+0x03..+0x0E</c></term><description>three <c>i32</c> — the fixed emission point, used when there is no owner</description></item>
///   <item><term><c>+0x0F</c></term><description><c>u16</c> the OWNER's pool near offset, or 0</description></item>
///   <item><term><c>+0x11</c></term><description><c>u16</c> the emission interval in scaled frames</description></item>
///   <item><term><c>+0x13</c></term><description><c>u16</c> the attach timestamp — the LRU key</description></item>
///   <item><term><c>+0x15</c></term><description><c>u16</c> the row's own expiry frame (<c>0xFFFF</c> = never)</description></item>
///   <item><term><c>+0x17</c></term><description><c>u16</c> the next emission frame</description></item>
/// </list>
/// <para>
/// All four deadlines are on <c>g_frame_count_scaled [0xF0D0]</c>, which <c>scene_frame_timer_advance</c> sets to
/// <c>frameTime &gt;&gt; 6</c> — a clock four times faster than the master frame counter the puffs themselves are on.
/// </para>
/// </remarks>
public static class EffectEmitterTable
{
    /// <summary>The LAST row (<c>mov si,0xB897</c> @<c>image@0x0B476</c>).</summary>
    public const int LastRow = 0xB897;

    /// <summary>The FIRST (<c>cmp si,0xB84C</c> @<c>image@0x0B479</c>).</summary>
    public const int FirstRow = 0xB84C;

    /// <summary>One row's stride: <c>0x19</c> = 25 bytes.</summary>
    public const int RowStride = 0x19;

    /// <summary>How many rows there are: 4.</summary>
    public const int RowCount = ((LastRow - FirstRow) / RowStride) + 1;

    /// <summary><c>g_frame_count_scaled [0xF0D0]</c> — the clock every row deadline is on.</summary>
    public const int ScaledFrameCounter = 0xF0D0;

    /// <summary>The interval a row gets when the caller passes 0: <c>0x10</c> (<c>image@0x0B53C</c>).</summary>
    public const int DefaultInterval = 0x10;

    /// <summary>The lifetime a row gets when the caller passes 0: <c>0x2D</c> (<c>image@0x0B55E</c>).</summary>
    public const int DefaultLifetime = 0x2D;

    /// <summary>The lifetime value meaning "never expire": <c>−1</c> → <c>0xFFFF</c>.</summary>
    public const int LifetimeForever = -1;

    /// <summary>The fixed emission point's Y, hard-coded by the attach: <c>0x00001900</c>.</summary>
    /// <remarks><c>mov word [si+7],0x1900 ; mov word [si+9],0</c> @<c>image@0x0B4FA</c>.</remarks>
    public const int FixedPointY = 0x1900;

    /// <summary>The four row offsets, LAST first — the order every walker uses.</summary>
    public static IEnumerable<int> Rows()
    {
        for (int row = LastRow; row >= FirstRow; row -= RowStride)
        {
            yield return row;
        }
    }

    /// <summary>
    /// <c>slot_4x19_clear_for_owner @image@0x0B582</c> — drop every row an object owns.
    /// </summary>
    /// <param name="registers">The register file.</param>
    /// <param name="ownerRef">The owner's pool near offset.</param>
    /// <returns>How many rows were cleared.</returns>
    public static int ClearForOwner(CombatRegisters registers, ushort ownerRef)
    {
        ArgumentNullException.ThrowIfNull(registers);
        if (ownerRef == 0)
        {
            return 0;
        }

        int cleared = 0;
        foreach (int row in Rows())
        {
            if (registers.Word(row + 0x0F) == ownerRef)
            {
                registers.SetByte(row, 0);                          // slot_4x19_clear_active @0x0B5AC
                cleared++;
            }
        }

        return cleared;
    }

    /// <summary>
    /// <c>subsystem4x19_row_attach @image@0x0B467</c> — start an effect on an object (or at a point).
    /// </summary>
    /// <param name="registers">The register file.</param>
    /// <param name="arena">The pool (needed only for the ownerless arm's immediate puff).</param>
    /// <param name="position">The fixed emission point; ignored when <paramref name="ownerRef"/> is non-zero.</param>
    /// <param name="ownerRef">The owner's pool near offset, or 0 for a fixed point.</param>
    /// <param name="interval">Frames between emissions; 0 means <see cref="DefaultInterval"/>.</param>
    /// <param name="firstDelay">Frames until the FIRST emission.</param>
    /// <param name="lifetime">Frames the row lives; 0 means <see cref="DefaultLifetime"/>, −1 means forever.</param>
    /// <param name="classifier">The row's <c>+0x02</c> byte.</param>
    /// <param name="kind">The puff kind the row emits.</param>
    /// <returns>The row used, or −1 when all four are busy and none could be evicted.</returns>
    /// <remarks>
    /// The row is chosen in three steps (<c>image@0x0B46F..0x0B4D1</c>): every row the owner already holds is
    /// dropped, then the first FREE row is taken, and failing that the least-recently attached row whose <c>+0x02</c>
    /// classifier is 0.  The ownerless arm also lights ONE puff immediately at the fixed point
    /// (<c>image@0x0B51A</c>), which is what makes a ground impact visible on the frame it happens.
    /// </remarks>
    public static int Attach(
        CombatRegisters registers,
        PoolArena arena,
        CombatPosition position,
        ushort ownerRef,
        int interval,
        int firstDelay,
        int lifetime,
        byte classifier,
        byte kind)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(arena);

        ClearForOwner(registers, ownerRef);                          // image@0x0B476..0x0B48C

        int row = -1;
        foreach (int candidate in Rows())
        {
            if (registers.Byte(candidate) == 0)
            {
                row = candidate;
                break;                                               // image@0x0B493
            }
        }

        if (row < 0)
        {
            // image@0x0B4A1..0x0B4D1 — the LRU over rows whose +0x02 classifier is 0.
            ushort oldest = 0;
            foreach (int candidate in Rows())
            {
                if (registers.Byte(candidate + 2) != 0)
                {
                    continue;
                }

                if (row < 0 || registers.Word(candidate + 0x13) < oldest)
                {
                    row = candidate;
                    oldest = registers.Word(candidate + 0x13);
                }
            }

            if (row < 0)
            {
                return -1;                                           // image@0x0B4CC
            }

            registers.SetByte(row, 0);
        }

        registers.SetByte(row, 1);                                   // image@0x0B4D4
        if (ownerRef != 0)
        {
            registers.SetWord(row + 0x0F, ownerRef);                 // image@0x0B4E0
        }
        else
        {
            registers.SetWord(row + 0x0F, 0);                        // image@0x0B4E5
            WriteI32(registers, row + 3, position.X);
            WriteI32(registers, row + 7, FixedPointY);
            WriteI32(registers, row + 0x0B, position.Z);
            SmokePuffTable.Allocate(
                registers,
                arena,
                new CombatPosition(position.X, FixedPointY, position.Z),
                0);                                                  // image@0x0B51A
        }

        registers.SetByte(row + 1, kind);                            // image@0x0B525
        registers.SetByte(row + 2, classifier);                      // image@0x0B52B
        registers.SetWord(row + 0x11, (ushort)(interval != 0 ? interval : DefaultInterval));
        ushort now = registers.Word(ScaledFrameCounter);
        registers.SetWord(row + 0x13, now);                          // image@0x0B547

        ushort expiry = lifetime == LifetimeForever
            ? (ushort)0xFFFF
            : unchecked((ushort)(now + (lifetime != 0 ? lifetime : DefaultLifetime)));
        registers.SetWord(row + 0x15, expiry);                       // image@0x0B56D
        registers.SetWord(row + 0x17, unchecked((ushort)(now + firstDelay)));
        return row;
    }

    /// <summary>
    /// <c>subsystem4x19_per_frame_advance @image@0x0B5B0</c> — one of the five effect ticks:
    /// expire the dead rows and emit from the live ones.
    /// </summary>
    /// <param name="registers">The register file.</param>
    /// <param name="arena">The pool.</param>
    /// <returns>How many puffs the tick emitted.</returns>
    /// <remarks>
    /// The owner gate is the reason a shot-down aeroplane stops smoking: a row whose owner's pool
    /// object is no longer ACTIVE is dropped (<c>test byte es:[bx+2],1 / je</c>
    /// @<c>image@0x0B5D9</c>).  A live row emits at the OWNER's current position
    /// (<c>owner + 6</c>, the pool object's position triple) so the trail follows the aircraft.
    /// </remarks>
    public static int PerFrameAdvance(CombatRegisters registers, PoolArena arena)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(arena);

        ushort now = registers.Word(ScaledFrameCounter);
        int emitted = 0;
        foreach (int row in Rows())
        {
            if (registers.Byte(row) == 0)
            {
                continue;                                            // image@0x0B5BD
            }

            if (registers.Word(row + 0x15) <= now)
            {
                registers.SetByte(row, 0);                           // image@0x0B61B
                continue;
            }

            ushort owner = registers.Word(row + 0x0F);
            if (owner != 0 &&
                (!arena.Covers(owner, 0x12) || (arena.Byte((ushort)(owner + 2)) & 1) == 0))
            {
                registers.SetByte(row, 0);                           // image@0x0B5DE
                continue;
            }

            if (registers.Word(row + 0x17) > now)
            {
                continue;                                            // image@0x0B5E0
            }

            CombatPosition point = owner != 0
                ? new CombatObjectView(arena, owner).Position        // image@0x0B5EB — owner + 6
                : new CombatPosition(
                    ReadI32(registers, row + 3),
                    ReadI32(registers, row + 7),
                    ReadI32(registers, row + 0x0B));                 // image@0x0B5F7 — the fixed point

            if (SmokePuffTable.Allocate(registers, arena, point, registers.Byte(row + 1)) >= 0)
            {
                emitted++;
            }

            registers.SetWord(
                row + 0x17,
                unchecked((ushort)(registers.Word(row + 0x11) + now)));   // image@0x0B616
        }

        return emitted;
    }

    private static void WriteI32(CombatRegisters registers, int at, int value)
    {
        registers.SetWord(at, unchecked((ushort)value));
        registers.SetWord(at + 2, unchecked((ushort)(value >> 16)));
    }

    private static int ReadI32(CombatRegisters registers, int at) =>
        registers.Word(at) | (registers.Word(at + 2) << 16);
}
