namespace CYAC.Port.Core.Sim.Combat.Geometry;

/// <summary>
/// <c>shot_trajectory_proximity_accum @image@0x08510</c> — the cached snapshot of the acquisition
/// target's pool object, and (on a hit check) the closest-approach accumulator built from it.
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  This is the routine that gives the manoeuvring engine its "intercept position":
/// <c>engagement_slot_angle_update</c> calls it first thing on every call (<c>image@0x066F1</c>,
/// <c>AL = 0</c>) and then reads <c>[0xEDB6]</c>/<c>[0xEDBA]</c>/ <c>[0xEDBE]</c> as the target's
/// X/Y/Z.  Those globals are not an independently-maintained "intercept" — they are bytes 6..17
/// of a 24-byte COPY of the target object, taken here.
/// </para>
/// <para>
/// Two <c>rep movsw cx=0x0C</c> do the work (<c>image@0x0859A</c> and <c>image@0x085AB</c>):
/// pool → <c>[0xEDC8..0xEDDF]</c> ("current"), then current → <c>[0xEDB0..0xEDC7]</c>
/// ("reference").  The copy is skipped entirely while a six-key cache is fresh, which is why the
/// intercept position visibly lags the target by up to a frame.
/// </para>
/// <para>
/// Source of truth: the original's bytes <c>image@0x08510..0x0864C</c> (
///).  The port implements the ACTIVE compute path too.
/// </para>
/// </remarks>
public static class ShotTargetSnapshot
{
    /// <summary>The cache's six keys, in the order <c>image@0x08519</c> tests them.</summary>
    private const int CacheFrameLo = 0xB53E;
    private const int CacheFrameHi = 0xB540;
    private const int CachePlayerSlot = 0xB542;
    private const int CacheTarget = 0xB544;
    private const int CacheWeaponType = 0xB546;
    private const int CacheHitFlag = 0xB547;

    /// <summary>
    /// Runs the routine.
    /// </summary>
    /// <param name="context">The geometry context.</param>
    /// <param name="isHitCheck">
    /// The original's <c>AL</c>.  <c>false</c> means "position tracking only" and the routine stops
    /// after the cache work (<c>image@0x085AE</c>) — which is what
    /// <c>engagement_slot_angle_update</c> always passes.
    /// </param>
    public static void Accumulate(EngagementGeometryContext context, bool isHitCheck)
    {
        ArgumentNullException.ThrowIfNull(context);
        EngagementAngleView view = context.View;
        CombatRegisters registers = context.Registers;
        byte hitFlag = isHitCheck ? (byte)1 : (byte)0;

        // ── the six-key cache (image@0x08519..0x08556) ─────────────────────────────────────────
        bool keysMatch =
            registers.Word(CacheFrameLo) == registers.Word(0xF0D2)          // image@0x08520
            && registers.Word(CacheFrameHi) == registers.Word(0xF0D4)       // image@0x08526
            && registers.Word(CacheTarget) == view.AcquisitionTarget        // image@0x0852F
            && registers.Word(CachePlayerSlot) == view.PlayerSlotReference  // image@0x08538
            && registers.Byte(CacheWeaponType) == view.TypeSlotIndex;       // image@0x08541

        if (keysMatch)
        {
            byte cachedFlag = registers.Byte(CacheHitFlag);
            if (cachedFlag == hitFlag || cachedFlag != 0)
            {
                // EXIT A (image@0x0854F) and EXIT B (image@0x08556): no writes at all.
                context.Census.SnapshotCacheHit++;
                return;
            }
        }

        // ── the refresh (image@0x08559..0x085AD) ───────────────────────────────────────────────
        context.Census.SnapshotRefreshed++;
        registers.SetWord(CacheFrameLo, registers.Word(0xF0D2));
        registers.SetWord(CacheFrameHi, registers.Word(0xF0D4));
        registers.SetWord(CacheTarget, view.AcquisitionTarget);
        registers.SetWord(CachePlayerSlot, view.PlayerSlotReference);
        registers.SetByte(CacheWeaponType, view.TypeSlotIndex);
        registers.SetByte(CacheHitFlag, hitFlag);

        CopySnapshot(context, view.AcquisitionTarget);

        if (!isHitCheck)
        {
            return;                                                         // EXIT C image@0x085B2
        }

        if (view.PlayerObject != view.AcquisitionTarget)
        {
            return;                                                         // EXIT D image@0x085BE
        }

        // ── weapon-descriptor lookup + the exclude bit (image@0x085C1..0x085DA) ────────────────
        //
        // THE 0xFF SENTINEL.  `image@0x085C1` reads the type-slot index as a plain byte and indexes
        // the prototype's FOUR-entry weapon-slot table with it — `mov al,[0xed64] / sub ah,ah / mov
        // si,ax / shl si,1 / mov bx,[0xed54] / mov ax,[bx+si+0x0e]`, 16 bytes ending in the
        // `[bx+si+disp8]` form.  There is NO bound test.  But `[0xED64]` also carries the
        // acquisition machine's "no weapon slot" sentinel `0xFF`:
        // `enemy_target_acquisition_state_machine` writes it at `image@0x0812C` (`mov byte
        // [0xed64],0xff`) before its four-slot scan and leaves it there when every slot's
        // ammunition byte `[0xED66+slot]` is 0 (`image@0x08182 je`), then latches that state
        // forever — `image@0x081DE` returns with `[0xED6B] = 0x7FF0` and the prologue `cmp byte
        // [0xed64],0xff / jne` at `image@0x07F84` refuses every later pass.  The node's PHASE is
        // not reset, so a PURSUIT node (`[0xED61] == 0x0C`, `image@0x042C1`) keeps arriving here
        // with `AL = 1` and an index of 0xFF, reading `[prototype + 0x0E + 0x1FE]` — 0x20C bytes
        // past a 0x2E-byte record.
        //
        // That is the ORIGINAL's own out-of-range read, not a port artefact, and what it lands on
        // is pure memory layout.  Over the 19 shipped prototypes
        // (`data/exe/tables/engagement.json`): 15 read a "descriptor" whose `+0x24` happens to have
        // bit 0x10 clear and fall through into the ACTIVE path with a garbage lead distance; 4
        // (P-51D, Me-110B, Yak-9, B-52D) happen to have it set and exit; and one of the 15 — the
        // FW-190A (`0x1812 + 0x20C = 0x1A1E` → 0x3240, whose `+0x16` word at DGROUP 0x3256 is
        // 0x0000) — divides by zero at `image@0x085FC`, a shipped `#DE`. For the MiG-15 (`0x1F60 +
        // 0x20C = 0x216C` → 0x4960) the "descriptor" is inside `g_view_mode_label_ptr_table` /
        // `g_film_obj_type_table` (``).
        //
        // PORT CHOICE (quirk `acq-no-weapon-slot-indexes-past-the-table`, FIX): the sentinel means
        // "this engagement has no weapon at all", so there is no shot whose flight time could move
        // the lead point — the port takes EXIT E and accumulates nothing.  It does NOT reproduce
        // the original's layout-dependent garbage (which the port could not read anyway: the
        // transformed tree publishes labelled documents, not all 64 KB of DGROUP).
        if (view.TypeSlotIndex >= EnemyTargetAcquisition.SlotCount)
        {
            context.Census.SnapshotNoWeaponSlot++;
            return;                                                         // port EXIT E
        }

        int descriptorSlot = view.WeaponDescriptorTable + (view.TypeSlotIndex * 2) + 0x0E;
        ushort descriptor = context.StaticData.Word(descriptorSlot);
        if ((context.StaticData.Byte(descriptor + 0x24) & 0x10) != 0)
        {
            return;                                                         // EXIT E image@0x085DA
        }

        if ((view.SlotFlags & 3) < 1)
        {
            return;                                                         // EXIT F image@0x085E3
        }

        ActivePath(context, descriptor);
    }

    /// <summary>
    /// The ACTIVE compute path (<c>image@0x085E5..0x08646</c>): scale the range by the weapon's
    /// speed and walk the closest-approach vector <c>[0xEDCE]</c> that far along the target's own
    /// heading.
    /// </summary>
    private static void ActivePath(EngagementGeometryContext context, ushort descriptor)
    {
        EngagementAngleView view = context.View;
        ushort target = view.AcquisitionTarget;

        ushort distance = FirePosGeometry.Distance3dScaled(
            view.FirePosition,
            ObjectPosition(context.Arena, target),
            applySentinelBias: false,
            0,
            0,
            0);                                                             // image@0x085EB
        if (distance >= 0x3FFF)
        {
            return;                                                         // image@0x085EE cmp/jae
        }

        ushort speed = context.StaticData.Word(descriptor + 0x16);
        if (speed == 0)
        {
            // The original's `div word ptr [bx+0x16]` raises #DE here.  The port refuses instead of
            // inventing a value — no observed caller reaches it.
            throw new DivideByZeroException(
                "shot_trajectory_proximity_accum @image@0x085FC divides by the weapon descriptor's "
                    + $"+0x16 speed, which is 0 for descriptor 0x{descriptor:X4}; the original "
                    + "raises #DE.");
        }

        // image@0x085F3: shl ax,1 / shl ax,1 then an UNSIGNED 32/16 divide with DX pre-zeroed.
        ushort quotient = (ushort)(((uint)distance * 4) / speed);
        if (quotient == 0)
        {
            return;                                                         // image@0x08602 or/je
        }

        ushort block = context.Arena.EngagementBlockRef(target);            // image@0x0860A
        short blockRange = unchecked((short)context.Arena.Word((ushort)(block + 0x26)));

        // image@0x08627: abs16, then an UNSIGNED 16x16 multiply whose HIGH word is then DISCARDED
        // by `shr ax,1 / shr ax,1 / sub dx,dx` — only AX is shifted, so the result is
        // ((product & 0xFFFF) >> 2), which is then <<8 by the byte shuffle.
        ushort magnitude = unchecked((ushort)Math.Abs((int)blockRange));
        ushort low = unchecked((ushort)(magnitude * quotient));
        int step = (low >> 2) << 8;

        CombatPosition start = new CombatPosition(
            view.Int32(EngagementAngleView.TargetSnapshotCurOffset + 0x06),
            view.Int32(EngagementAngleView.TargetSnapshotCurOffset + 0x0A),
            view.Int32(EngagementAngleView.TargetSnapshotCurOffset + 0x0E));
        CombatPosition advanced = CombatGeometry.AccumulateDistance3d(
            start,
            step,
            view.Signed(0xEDC4),                                            // elevation, image@0x08623
            view.InterceptHeading);                                         // heading, image@0x0861F

        view.SetInt32(EngagementAngleView.TargetSnapshotCurOffset + 0x06, advanced.X);
        view.SetInt32(EngagementAngleView.TargetSnapshotCurOffset + 0x0A, advanced.Y);
        view.SetInt32(EngagementAngleView.TargetSnapshotCurOffset + 0x0E, advanced.Z);
    }

    /// <summary>
    /// The two <c>rep movsw</c>: pool object → <c>[0xEDC8]</c> → <c>[0xEDB0]</c>, 24 bytes each.
    /// </summary>
    /// <remarks>
    /// Eight of the 48 destination bytes have nowhere to land: <c>[0xEDC6..0xEDCD]</c> is a DGROUP
    /// HOLE that no combat register window covers — neither C0's trace layout nor the port's own
    /// <see cref="CombatRegisterWindows"/> declaration (the two agree).  Nothing in this subsystem
    /// reads them, so they are COUNTED (<see cref="EngagementGeometryCensus.SnapshotHoleBytes"/>)
    /// rather than silently dropped.
    /// </remarks>
    private static void CopySnapshot(EngagementGeometryContext context, ushort targetRef)
    {
        CombatRegisters registers = context.Registers;
        int length = EngagementAngleView.TargetSnapshotBytes;

        if (!context.Arena.Covers(targetRef, length))
        {
            throw new EngagementGeometrySeamException(
                $"shot_trajectory_proximity_accum @image@0x0859A copies {length} bytes from pool "
                    + $"offset 0x{targetRef:X4}, which is outside the arena this run was given"
                    + (targetRef == 0
                        ? " — g_acq_current_target [0xED6F] is 0 and the machine reads the head of "
                            + "the pool segment there, which a probe record does not carry."
                        : "."));
        }

        ReadOnlySpan<byte> source = context.Arena.Read(targetRef, length);
        for (int i = 0; i < length; i++)
        {
            WriteOrCount(context, EngagementAngleView.TargetSnapshotCurOffset + i, source[i]);
        }

        for (int i = 0; i < length; i++)
        {
            int from = EngagementAngleView.TargetSnapshotCurOffset + i;
            byte value = registers.Covers(from, 1) ? registers.Byte(from) : source[i];
            WriteOrCount(context, EngagementAngleView.TargetSnapshotRefOffset + i, value);
        }
    }

    private static void WriteOrCount(EngagementGeometryContext context, int dgroupOffset, byte value)
    {
        if (context.Registers.Covers(dgroupOffset, 1))
        {
            context.Registers.SetByte(dgroupOffset, value);
            return;
        }

        context.Census.SnapshotHoleBytes++;
    }

    private static CombatPosition ObjectPosition(PoolArena arena, ushort objectRef) => new(
        ReadInt32(arena, (ushort)(objectRef + 0x06)),
        ReadInt32(arena, (ushort)(objectRef + 0x0A)),
        ReadInt32(arena, (ushort)(objectRef + 0x0E)));

    private static int ReadInt32(PoolArena arena, ushort nearOffset) =>
        unchecked((int)(arena.Word(nearOffset) | ((uint)arena.Word((ushort)(nearOffset + 2)) << 16)));
}
