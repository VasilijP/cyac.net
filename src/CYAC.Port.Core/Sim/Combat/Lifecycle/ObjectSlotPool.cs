namespace CYAC.Port.Core.Sim.Combat.Lifecycle;

/// <summary>
/// The 3-slot <c>s_object_slot</c> DESTRUCTION / DEBRIS pool at DGROUP <c>[0xBB4C..0xBC1A]</c> — a
/// THIRD pool, neither the engagement VM's arena nor the film's.
/// </summary>
/// <remarks>
/// <para>
/// Three <c>0x45</c>-byte records at <c>0xBB4C</c>, <c>0xBB91</c> and <c>0xBBD6</c>, built once at
/// mission load by <c>object_pool_init_3_slots @image@0x2C2B6</c>, which allocates each slot's three
/// ext-pool objects and stores their near pointers at <c>+0x04</c> / <c>+0x06</c> / <c>+0x08</c>.
/// </para>
/// <para>
/// MEASURED (over the six shipped reference windows — 99,066 stage records): the nine ext
/// pointers are IDENTICAL on every recording and never change — slot 0 <c>{0x05C4, 0x05E1,
/// 0x05F9}</c>, slot 1 <c>{0x0577, 0x0594, 0x05AC}</c>, slot 2 <c>{0x052A, 0x0547, 0x055F}</c> — so
/// the pool has FIXED IDENTITY for a mission and only its flags, animation words and frame stamps
/// move.  Only slot 2 is ever activated on the recordings; slots 0 and 1 are cold on all six recordings.
/// </para>
/// <para>
/// Source of truth: the original's bytes, read against KNOWN_GLOBAL_TYPES[0xBB4C]
/// = 'u8[207]'</c> and /.
/// </para>
/// </remarks>
public static class ObjectSlotPool
{
    /// <summary>The FIRST slot's DGROUP address, <c>0xBB4C</c>.</summary>
    public const int FirstSlot = LifecycleOffsets.ObjectSlotTable;

    /// <summary>The LAST slot's DGROUP address, <c>0xBBD6</c> — where both walks START.</summary>
    public const int LastSlot = FirstSlot + (2 * LifecycleOffsets.ObjectSlotStride);

    /// <summary>One slot's length, <c>0x45</c>.</summary>
    public const int SlotBytes = LifecycleOffsets.ObjectSlotStride;

    /// <summary>The whole table's length, <c>3 × 0x45 = 207</c>.</summary>
    public const int TableBytes = LifecycleOffsets.ObjectSlotCount * SlotBytes;

    // ── the slot record's fields, every one byte-verified────────────────────────────

    /// <summary><c>+0x00</c> — the ACTIVE flag (<c>mov byte ptr [si],1</c> @<c>image@0x2C54C</c>).</summary>
    public const int ActiveFlag = 0x00;

    /// <summary>
    /// <c>+0x01</c> — the PLAYER-OWNED flag (the allocator's <c>arg2</c>).  It is what makes
    /// <see cref="Deactivate"/> trip <c>[0xEE58]</c>.
    /// </summary>
    public const int PlayerOwnedFlag = 0x01;

    /// <summary><c>+0x02</c> — the allocator's <c>arg1</c>; also the launch-velocity selector.</summary>
    public const int KindFlag = 0x02;

    /// <summary><c>+0x03</c> — the allocator's <c>arg0</c>; cleared by the non-ext deactivate arm.</summary>
    public const int StageFlag = 0x03;

    /// <summary><c>+0x04</c> — <c>ext_ptr_a</c>, the key <see cref="FindByExtPointerA"/> matches.</summary>
    public const int ExtPointerA = 0x04;

    /// <summary><c>+0x06</c> — <c>ext_ptr_b</c>.</summary>
    public const int ExtPointerB = 0x06;

    /// <summary><c>+0x08</c> — <c>ext_ptr_c</c>.</summary>
    public const int ExtPointerC = 0x08;

    /// <summary><c>+0x40</c> — the frame the slot was allocated on; the eviction key.</summary>
    public const int AllocationFrame = 0x40;

    /// <summary>
    /// <c>slot_find_by_ext_ptr_a @image@0x2C380</c> — the slot whose <c>ext_ptr_a</c> is
    /// <paramref name="objectRef"/>, or 0.
    /// </summary>
    /// <param name="registers">The combat register file (the table is in the trace's window).</param>
    /// <param name="objectRef">The pool object to look up.</param>
    /// <returns>The slot's DGROUP address, or 0 when no slot owns the object.</returns>
    /// <remarks>
    /// FAR, plain <c>retf</c> (the CALLER pops its one word).  It walks DOWNWARD from <c>0xBBD6</c>
    /// in <c>0x45</c> strides while <c>bx &gt;= 0xBB4C</c> UNSIGNED (<c>image@0x2C386</c>), so slot 2
    /// wins a tie.  It is what the damage resolver asks on every resolved hit (<c>image@0x0BEE0</c>):
    /// a hit on a chaff/flare cloud finds its slot and deals no damage.
    /// </remarks>
    public static ushort FindByExtPointerA(CombatRegisters registers, ushort objectRef)
    {
        ArgumentNullException.ThrowIfNull(registers);
        for (int slot = LastSlot; slot >= FirstSlot; slot -= SlotBytes)         // image@0x2C386 jb
        {
            if (registers.Word(slot + ExtPointerA) == objectRef)                // image@0x2C38F
            {
                return (ushort)slot;                                            // image@0x2C399
            }
        }

        return 0;                                                               // image@0x2C39D
    }

    /// <summary>
    /// <c>slot_deactivate_and_clear @image@0x2C3A3</c> — retire one slot, and trip
    /// <c>g_player_slot_destroyed_flag [0xEE58]</c> when the slot was player-owned.
    /// </summary>
    /// <param name="context">The lifecycle context.</param>
    /// <param name="slotAddress">The slot's DGROUP address (<c>arg0</c>).</param>
    /// <param name="deactivateExtObjects">
    /// <c>arg1</c>'s low byte.  Non-zero ⇒ run <see cref="ExtPoolSlotDeactivate"/> (which clears the
    /// three ext objects and emits a film departure); zero ⇒ just clear <c>+0x03</c>.
    /// </param>
    /// <remarks>
    /// FAR, plain <c>retf</c>.  The <c>[0xEE58]</c> trip (<c>image@0x2C3C4</c>) is the flag behind
    /// the likely shipped cockpit-key gate bug on <c>[0xEE58]</c>:
    /// once set, nothing in the destruction path clears it.  It is accompanied by <c>[0xC390]:= 0</c>
    /// (<c>image@0x2C3C9</c>).  MEASURED: <c>[0xEE58]</c> is 0 on all 99,066 stage records of the six
    /// reference windows — the bug is not exercised by the recordings.
    /// </remarks>
    public static void Deactivate(
        EngagementLifecycleContext context, ushort slotAddress, byte deactivateExtObjects)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters r = context.Registers;
        context.Census.SlotDeactivations++;

        if (deactivateExtObjects != 0)                                          // image@0x2C3A6
        {
            ExtPoolSlotDeactivate(context, slotAddress);                        // image@0x2C3AF
        }
        else
        {
            r.SetByte(slotAddress + StageFlag, 0);                              // image@0x2C3B7
        }

        if (r.Byte(slotAddress + PlayerOwnedFlag) != 0)                         // image@0x2C3BE
        {
            context.Census.SlotDeactivationPlayerTrips++;
            r.SetByte(LifecycleOffsets.PlayerSlotDestroyedFlag, 1);             // image@0x2C3C4
            r.SetWord(LifecycleOffsets.ViewLockObject, 0);                      // image@0x2C3C9
        }
    }

    /// <summary>
    /// <c>ext_pool_slot_deactivate @image@0x2C34F</c> — clear a slot's ACTIVE flag and drop bit0 of
    /// the <c>+0x02</c> flags byte of each of its three ext-pool objects.
    /// </summary>
    /// <param name="context">The lifecycle context.</param>
    /// <param name="slotAddress">The slot's DGROUP address (the original's <c>BX</c>).</param>
    /// <remarks>
    /// NEAR, register argument.  It returns immediately when the slot is already inactive
    /// (<c>cmp byte ptr [bx],0 / je</c> @<c>image@0x2C350</c>), and ends with the film departure
    /// record for <c>ext_ptr_a</c>'s object (<c>lcall 0x401c:0xa86</c> @<c>image@0x2C379</c> =
    /// <c>film_obj_departure_record @image@0x30C46</c>).
    /// </remarks>
    public static void ExtPoolSlotDeactivate(EngagementLifecycleContext context, ushort slotAddress)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters r = context.Registers;
        if (r.Byte(slotAddress + ActiveFlag) == 0)                              // image@0x2C350
        {
            return;
        }

        r.SetByte(slotAddress + ActiveFlag, 0);                                 // image@0x2C355
        ClearExtObjectActiveBit(context, r.Word(slotAddress + ExtPointerA));    // image@0x2C361
        ClearExtObjectActiveBit(context, r.Word(slotAddress + ExtPointerB));    // image@0x2C369
        ClearExtObjectActiveBit(context, r.Word(slotAddress + ExtPointerC));    // image@0x2C371

        context.Events.RecordObjectDeparture(r.Word(slotAddress + ExtPointerA)); // image@0x2C379
    }

    /// <summary>
    /// <c>slot_alloc_and_activate @image@0x2C4F6</c> — take a free slot (or evict the oldest), arm
    /// it, and seed the two decay integrators that animate the wreck.
    /// </summary>
    /// <param name="context">The lifecycle context.</param>
    /// <param name="stageFlag">The first pushed word's low byte → <c>+0x03</c>.</param>
    /// <param name="kindFlag">The second pushed word's low byte → <c>+0x02</c>, and the two velocity selectors.</param>
    /// <param name="playerOwned">The third pushed word's low byte → <c>+0x01</c>.</param>
    /// <param name="seed">The 32-bit fourth/fifth argument the two integrators are seeded from.</param>
    /// <param name="parentRef">The sixth argument: the parent pool object the debris comes off.</param>
    /// <returns>The DGROUP address of the slot that was used.</returns>
    /// <remarks>
    /// <para>
    /// FAR, <c>retf 0x0C</c>, six pushed words (<c>[bp+6]</c>…<c>[bp+0x10]</c>).
    /// </para>
    /// <para>
    /// <b>A shipped READ-BEFORE-WRITE.</b> The eviction candidate starts as <c>mov cx,[bp-2]</c>
    /// (<c>image@0x2C504</c>) — an UNINITIALISED stack local, since the prologue is <c>sub sp,8</c> and nothing has
    /// written <c>[bp-2]</c>.  It is only observable when every slot is busy AND every busy slot either is
    /// player-owned or has <c>+0x40 &gt;= 0xFFFF</c>, because <c>DX</c> starts at <c>0xFFFF</c> and any smaller frame
    /// stamp overwrites <c>CX</c>.  The port reproduces the shape by CENSUSING the eviction arm and throwing on the
    /// genuinely-undefined case rather than inventing a value — see
    /// <see cref="LifecycleCensus.SlotAllocationsEvicted"/>.
    /// </para>
    /// <para>
    /// The two integrator seeds are hard constants of the original:
    /// <c>+0x10 = 0x0780</c> when <c>kindFlag == 0</c> and <c>0x12C0</c> otherwise
    /// (<c>sbb ax,ax / and ax,0xf4c0 / add ax,0x12c0</c> @<c>image@0x2C57B</c>);
    /// <c>+0x12 = 0xF060</c>, <c>+0x14 = 0x0640</c>;
    /// <c>+0x34 = 0x0320</c> when <c>kindFlag == 0</c> and <c>0x1900</c> otherwise
    /// (<c>and ax,0xea20 / add ah,0x19</c> — an EIGHT-BIT add whose carry is dropped,
    /// <c>image@0x2C5F3</c>); <c>+0x36 = 0xC180</c>, <c>+0x38 = 0x07D0</c>.
    /// </para>
    /// </remarks>
    public static ushort Allocate(
        EngagementLifecycleContext context,
        byte stageFlag,
        byte kindFlag,
        byte playerOwned,
        int seed,
        ushort parentRef)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters r = context.Registers;
        LifecycleCensus census = context.Census;
        census.SlotAllocations++;

        // ── choose a slot (image@0x2C4FE..0x2C534) ─────────────────────────────────────────────
        ushort oldestFrame = 0xFFFF;                                            // DX
        int best = -1;                                                          // CX, uninitialised
        int slot = LastSlot;
        bool free = false;
        for (; slot >= FirstSlot; slot -= SlotBytes)                            // image@0x2C521 jae
        {
            if (r.Byte(slot + ActiveFlag) == 0)                                 // image@0x2C509
            {
                free = true;
                break;
            }

            if (r.Byte(slot + PlayerOwnedFlag) != 0)                            // image@0x2C50E
            {
                continue;
            }

            ushort frame = r.Word(slot + AllocationFrame);
            if (frame < oldestFrame)                                            // image@0x2C514 jae
            {
                oldestFrame = frame;
                best = slot;
            }
        }

        if (free)
        {
            census.SlotAllocationsFree++;
        }
        else
        {
            census.SlotAllocationsEvicted++;
            if (best < 0)
            {
                throw new InvalidOperationException(
                    "slot_alloc_and_activate @image@0x2C4F6 evicted with no candidate: the original "
                        + "reads the UNINITIALISED local [bp-2] here (image@0x2C504).  The port "
                        + "refuses to invent a value; see §4.");
            }

            slot = best;                                                        // image@0x2C52D
            ExtPoolSlotDeactivate(context, (ushort)slot);                       // image@0x2C531
        }

        // ── arm it (image@0x2C534..0x2C56A) ────────────────────────────────────────────────────
        r.SetByte(slot + PlayerOwnedFlag, playerOwned);                         // image@0x2C537
        if (playerOwned != 0)                                                   // image@0x2C53A
        {
            r.SetWord(LifecycleOffsets.DestructionFocusObject, r.Word(slot + ExtPointerA));
            r.SetWord(LifecycleOffsets.DestructionFocusObject + 2, r.PoolSegment); // image@0x2C548
        }

        r.SetByte(slot + ActiveFlag, 1);                                        // image@0x2C54C
        r.SetByte(slot + KindFlag, kindFlag);                                   // image@0x2C552
        r.SetByte(slot + StageFlag, stageFlag);                                 // image@0x2C558
        context.Events.InitialiseExtPoolObject(r.Word(slot + ExtPointerA), parentRef); // image@0x2C561
        context.Events.InitialiseExtPoolObject(r.Word(slot + ExtPointerC), parentRef); // image@0x2C56A

        // ── integrator A, +0x0A..+0x1B (image@0x2C56D..0x2C5D7) ────────────────────────────────
        r.Write(slot + 0x0A, stackalloc byte[0x12]);                            // image@0x2C579 rep stosb
        r.SetWord(slot + 0x10, kindFlag == 0 ? (ushort)0x0780 : (ushort)0x12C0); // image@0x2C587
        r.SetWord(slot + 0x16, unchecked((ushort)(seed >> 4)));                 // image@0x2C59D (sar_i32_by_cl, CL=4)

        short ax = unchecked((short)r.Word(slot + 0x0A));
        short ay = unchecked((short)r.Word(slot + 0x10));
        short az = unchecked((short)r.Word(slot + 0x16));
        context.Events.RotateDebrisOffset(parentRef, ref ax, ref ay, ref az);   // image@0x2C5B2
        r.SetWord(slot + 0x0A, unchecked((ushort)ax));
        r.SetWord(slot + 0x10, unchecked((ushort)ay));
        r.SetWord(slot + 0x16, unchecked((ushort)az));

        r.SetWord(slot + 0x0E, unchecked((ushort)(Abs16(ax) >> 3)));            // image@0x2C5C0
        r.SetWord(slot + 0x14, 0x0640);                                         // image@0x2C5C3
        r.SetWord(slot + 0x1A, unchecked((ushort)(Abs16(az) >> 3)));            // image@0x2C5D4
        r.SetWord(slot + 0x12, 0xF060);                                         // image@0x2C5D7

        // ── integrator B, +0x2E..+0x3F (image@0x2C5DC..0x2C638) ────────────────────────────────
        r.Write(slot + 0x2E, stackalloc byte[0x12]);                            // image@0x2C5E8 rep stosb
        r.SetWord(slot + 0x3A, unchecked((ushort)((seed >> 4) - 0x0C80)));      // image@0x2C5F0
        r.SetWord(slot + 0x34, kindFlag == 0 ? (ushort)0x0320 : (ushort)0x1900); // image@0x2C5FF

        short bx = unchecked((short)r.Word(slot + 0x2E));
        short by = unchecked((short)r.Word(slot + 0x34));
        short bz = unchecked((short)r.Word(slot + 0x3A));
        context.Events.RotateDebrisOffset(parentRef, ref bx, ref by, ref bz);   // image@0x2C613
        r.SetWord(slot + 0x2E, unchecked((ushort)bx));
        r.SetWord(slot + 0x34, unchecked((ushort)by));
        r.SetWord(slot + 0x3A, unchecked((ushort)bz));

        r.SetWord(slot + 0x32, unchecked((ushort)(Abs16(bx) >> 4)));            // image@0x2C624
        r.SetWord(slot + 0x38, 0x07D0);                                         // image@0x2C627
        r.SetWord(slot + 0x3E, unchecked((ushort)(Abs16(bz) >> 4)));            // image@0x2C635
        r.SetWord(slot + 0x36, 0xC180);                                         // image@0x2C638

        // ── stamps and the two out-calls (image@0x2C63D..0x2C662) ──────────────────────────────
        ushort frameNow = r.MasterFrameCounter;                                 // image@0x2C63D
        r.SetWord(slot + AllocationFrame, frameNow);                            // image@0x2C640
        r.SetByte(slot + 0x42, 0);                                              // image@0x2C643
        r.SetWord(slot + 0x43, unchecked((ushort)(frameNow + 2)));              // image@0x2C649 (unaligned)

        ushort extA = r.Word(slot + ExtPointerA);
        context.Arena.SetWord(extA, 0x5334);                                    // image@0x2C655
        context.Events.RecordSlotAllocation(extA, playerOwned);                 // image@0x2C662

        return (ushort)slot;
    }

    private static void ClearExtObjectActiveBit(
        EngagementLifecycleContext context, ushort objectRef)
    {
        byte flags = context.Arena.Byte((ushort)(objectRef + 0x02));
        context.Arena.SetByte((ushort)(objectRef + 0x02), (byte)(flags & 0xFE));
    }

    /// <summary>The <c>cwd / xor / sub</c> 16-bit absolute value; <c>|0x8000| = 0x8000</c>.</summary>
    /// <param name="value">The value.</param>
    /// <returns>Its magnitude, wrapping at 16 bits.</returns>
    private static short Abs16(short value)
    {
        int sign = value >> 15;
        return unchecked((short)((value ^ sign) - sign));
    }
}
