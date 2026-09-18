using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Sim.Combat;

/// <summary>
/// <c>engagement_fire_authority_compute @image@0x02A70</c> (643 B) — how likely this shot is to
/// count as a hit, as a 0..0x100 score, and <c>combat_engagement_state_update @image@0x02A1F</c>, the
/// caller that turns that score into the spawn record's "firing this frame" bit with one RNG draw.
/// </summary>
/// <remarks>
/// <para>
/// INT-only, and the RNG draw makes its call ORDER part of the determinism contract (row
/// <c>0x02A1F</c>).
/// </para>
/// <para>
/// Six factors, in the order the bytes apply them: RANGE, the class's per-shot weight, the
/// player-guided bonus, the <c>[0xE46D]</c> cheat bonus, the enemy aircraft-type table at DGROUP
/// <c>0x0DEC</c>, the shooter's own pitch, and — only when the TARGET is the player — an evasion
/// penalty accumulated in <c>g_fire_authority_penalty [0xB49C]</c> from the player's g-load and
/// three attitude deltas.
/// </para>
/// <para>
/// Because of the LFSR, both <c>combat_engagement_state_update</c> and its caller come straight
/// from the bytes here.
/// </para>
/// </remarks>
public static class EngagementFireAuthority
{
    /// <summary>
    /// <c>g_fire_authority_penalty [0xB49C]</c> — the evasion accumulator the four
    /// <c>image@0x039C8</c> calls minimise into, seeded to 0x100 and applied as a
    /// <c>&gt;&gt;8</c> scale at the end.
    /// </summary>
    public const int PenaltyDgroupOffset = 0xB49C;

    /// <summary>The four-byte aircraft-type fire bonus at DGROUP <c>0x0DEC</c>.</summary>
    public const int AircraftTypeBonusDgroupOffset = 0x0DEC;

    /// <summary><c>g_cheat_fire_authority_bonus [0xE46D]</c> — non-zero adds a flat +0x40.</summary>
    public const int CheatFlagDgroupOffset = 0xE46D;

    /// <summary><c>g_player_gload_q8 [0xF06E]</c> — the first evasion term.</summary>
    public const int PlayerGLoadDgroupOffset = 0xF06E;

    /// <summary>
    /// <c>image@0x039C8</c> — one evasion term: scale <paramref name="weight"/> by how far
    /// <paramref name="value"/> has travelled from <paramref name="low"/> toward
    /// <paramref name="high"/>, and MINIMISE <c>[0xB49C]</c> with <c>0x100 - that</c>.
    /// </summary>
    /// <remarks>
    /// Two guards: nothing happens while <c>value &lt;= low</c> (<c>cmp dx,ax / jge</c>
    /// @<c>image@0x039D0</c>), and <paramref name="value"/> is clamped to <paramref name="high"/>
    /// before the ratio (<c>image@0x039D4</c>).  The divide is the SIGNED
    /// <c>imul dx / idiv bx</c> pair at <c>image@0x11974</c>, so a zero span raises <c>#DE</c> in the
    /// original — the port lets <see cref="Fixed.MulDiv16Signed"/> throw for the same reason.
    /// </remarks>
    /// <param name="registers">The register file holding <c>[0xB49C]</c>.</param>
    /// <param name="weight">The term's maximum penalty — the original's stack argument.</param>
    /// <param name="value">The measured quantity — the original's <c>AX</c>.</param>
    /// <param name="low">The value at which the term starts biting — the original's <c>DX</c>.</param>
    /// <param name="high">The value at which it saturates — the original's <c>BX</c>.</param>
    public static void ApplyEvasionTerm(
        CombatRegisters registers, short weight, short value, short low, short high)
    {
        ArgumentNullException.ThrowIfNull(registers);

        if (low >= value)                                       // image@0x039D0 cmp dx,ax / jge
        {
            return;
        }

        short clamped = high < value ? high : value;            // image@0x039D4 cmp bx,ax / jl
        short numerator = unchecked((short)(clamped - low));    // image@0x039E0
        short span = unchecked((short)(high - low));            // image@0x039E3
        short scaled = Fixed.MulDiv16Signed(weight, numerator, span);   // image@0x039E9
        short candidate = unchecked((short)(0x0100 - scaled));  // image@0x039EE

        short current = unchecked((short)registers.Word(PenaltyDgroupOffset));
        if (current > candidate)                                // image@0x039F3 cmp / jle
        {
            registers.SetWord(PenaltyDgroupOffset, unchecked((ushort)candidate));
        }
    }

    /// <summary>Folds an angle delta into <c>[0, 0x2D0]</c> the way the evasion block does.</summary>
    /// <remarks>
    /// <c>angle_normalize_i16 @image@0x244C8</c>, absolute value, then
    /// <c>if (d &gt; 0x2D0) d = |d - 0x5A0|</c> (<c>image@0x02C45..0x02C4F</c> and its two clones).
    /// </remarks>
    private static short FoldQuadrant(short delta)
    {
        short d = Angle.Normalize(delta);
        if (d < 0)
        {
            d = unchecked((short)-d);
        }

        if (d > Angle.QuarterCircle)
        {
            d = unchecked((short)(d - Angle.HalfCircle));
            if (d < 0)
            {
                d = unchecked((short)-d);
            }
        }

        return d;
    }

    /// <summary>
    /// Computes the fire authority for one shot.
    /// </summary>
    /// <param name="context">The kernel context.</param>
    /// <param name="targetRef">The target's pool object — the original's <c>[bp+4]</c>.</param>
    /// <param name="ownerRef">The shooter's pool object — the original's <c>[bp+6]</c>.</param>
    /// <param name="weaponClassRef">The weapon class descriptor — the original's <c>[bp+8]</c>.</param>
    /// <returns>The authority, clamped to <c>[0, 0x100]</c>.</returns>
    public static short Compute(
        ProjectileKernelContext context, ushort targetRef, ushort ownerRef, ushort weaponClassRef)
    {
        ArgumentNullException.ThrowIfNull(context);

        CombatRegisters registers = context.Registers;
        PoolArena arena = context.Arena;
        WeaponClassView weapon = new WeaponClassView(context.StaticData, weaponClassRef);
        CombatObjectView owner = new CombatObjectView(arena, ownerRef);
        CombatObjectView target = new CombatObjectView(arena, targetRef);

        // image@0x02A9E..0x02AA4 — the TARGET's engagement class prototype (a DGROUP near pointer
        // read out of the object's engagement block, `object_pool_get_engagement_nearptr`).
        ushort targetPrototype = target.EngagementPrototypeRef;

        // image@0x02AB0..0x02AC9 — the 3-D Manhattan range, >> 12, LOW WORD ONLY (`mov [bp-0xe],ax`).
        int manhattan = CombatGeometry.ManhattanDistance3d(owner.Position, target.Position);
        short scaledRange = unchecked((short)(manhattan >> 12));

        // image@0x02ACC..0x02AD5 — the weapon's own range, ×16.
        short weaponRange = unchecked((short)(weapon.Range << 4));

        // image@0x02AD8..0x02AF4 — (range << 6) / prototype[6 + class.Kind].  The dividend is the
        // full 32-bit DX:AX (`cwd` at image@0x02ADB, then `shl_i32_by_cl` at image@0x02ADE) and the
        // divide is `lcall 0x201D:0x1798` → image@0x11968 = `f7 f3 cb` = DIV BX; RETF, i.e. an
        // UNSIGNED 32-over-16 divide.  `0x201D:0x1798` resolves to image@0x11968, not to
        // `mul16_unsigned @image@0x11970`; the operation is a DIVIDE, which is also what makes the
        // per-class byte behave like a target SIZE — the B-52D's 128 halves the effective range and
        // makes it the easiest thing in the sky to hit.
        //
        // The (short) cast below is EXACT for every input the original survives: `div bx` raises
        // #DE once the quotient passes 0xFFFF, which on the shipped divisors (64/96/128,
        // data/exe/tables/engagement.json) needs `scaledRange` to go negative, i.e. a Manhattan
        // range of 2^27 world units — about 99 miles.  Inside that domain the quotient is at most
        // 0x7FFF and the cast is a no-op, so the port keeps the truncation rather than adding a
        // throw to a band the original itself cannot leave defined.
        uint numerator = unchecked((uint)(scaledRange << 6));
        byte divisor = context.StaticData.Byte(targetPrototype + 6 + weapon.Kind);
        if (divisor == 0)
        {
            throw new InvalidOperationException(
                "engagement_fire_authority_compute (image@0x02AEF) divides by "
                    + $"prototype[0x{targetPrototype + 6 + weapon.Kind:X4}], which is zero here; the "
                    + "original raises #DE and lands in int00_divzero_isr.");
        }

        short ratio = unchecked((short)(numerator / divisor));

        short authority;
        if (weaponRange >= ratio)                               // image@0x02AF7 cmp / jl 0x2B0E
        {
            // image@0x02AFC..0x02B0C — inside range: 0x100 scaled down by how far out we are.
            authority = unchecked((short)(
                0x0100 - Fixed.MulDiv16Signed(0x00C0, ratio, weaponRange)));
        }
        else
        {
            // image@0x02B0E..0x02B18 — past twice the range there is no authority at all.
            if (unchecked((short)(weaponRange << 1)) <= ratio)
            {
                return 0;                                       // image@0x02BC9 sub ax,ax
            }

            // image@0x02B1B..0x02B2C — between one and two ranges: 0x40 down to 0.
            authority = unchecked((short)(
                0x0040 - Fixed.MulDiv16Signed(
                    0x0040, unchecked((short)(ratio - weaponRange)), weaponRange)));
        }

        // image@0x02B31..0x02B44 — × the class's per-shot weight (UNSIGNED mul), >> 6.
        uint weighted = unchecked((uint)(ushort)authority * context.StaticData.Byte(weaponClassRef + 1));
        authority = unchecked((short)(weighted >> 6));

        bool ownerIsPlayer = registers.PlayerObjectRef == ownerRef;

        if (ownerIsPlayer && (weapon.ClassFlags & 0x10) != 0)   // image@0x02B46..0x02B53
        {
            // image@0x02B55..0x02B7B — a guided shot the PLAYER fired gets a second, gentler curve
            // measured against HALF the weapon range, and keeps whichever score is higher.
            short halfRange = unchecked((short)(weaponRange >> 1));
            if (halfRange > ratio)                              // image@0x02B5D cmp / jle
            {
                short alternative = unchecked((short)-(short)(
                    Fixed.MulDiv16Signed(0x0100, ratio, halfRange) - 0x0100));
                if (alternative > authority)                    // image@0x02B77 cmp / jle
                {
                    authority = alternative;
                }
            }

            if (registers.Byte(CheatFlagDgroupOffset) != 0)     // image@0x02B7D
            {
                context.Census.CheatBias++;
                authority = unchecked((short)(authority + 0x40));   // image@0x02B84
            }
        }

        if (!ownerIsPlayer)                                     // image@0x02B87 cmp / je 0x2BAA
        {
            // image@0x02B90..0x02BA8 — the shooter's own engagement flags pick one of four
            // aircraft-type bonuses at DGROUP 0x0DEC.
            EngagementBlockView ownerBlock = new EngagementBlockView(arena, owner.EngagementBlockRef);
            int index = ownerBlock.Flags & 3;
            authority = unchecked((short)(
                authority + context.StaticData.Byte(AircraftTypeBonusDgroupOffset + index)));
        }

        if ((weapon.ClassFlags & 0x10) != 0)                    // image@0x02BAA
        {
            // image@0x02BB0..0x02BF1 — a guided shot loses authority the further its LAUNCHER is
            // from wings-level, folded into [0, 0x5A0] and scaled against a half circle.
            short pitch = owner.Elevation;
            if (pitch >= Angle.HalfCircle)                       // image@0x02BBA cmp ax,0x5a0 / jl
            {
                short folded = pitch < Angle.ThreeQuarterCircle  // image@0x02BBF cmp ax,0x870 / jl
                    ? unchecked((short)(Angle.FullCircle - pitch))   // image@0x02BCE
                    : unchecked((short)(pitch - Angle.HalfCircle));  // image@0x02BC4

                if (weapon.Kind == 0)                            // image@0x02BD7 cmp byte [si],0
                {
                    folded = unchecked((short)((folded >> 1) + Angle.QuarterCircle));
                }

                authority = Fixed.MulDiv16Signed(authority, folded, Angle.HalfCircle);
            }
        }

        if (targetRef == registers.PlayerObjectRef)             // image@0x02BF3
        {
            ApplyPlayerEvasion(context, owner);
            if (unchecked((short)registers.Word(PenaltyDgroupOffset)) < 0x0100)   // image@0x02CC2
            {
                authority = Fixed.MulDiv16SignedShr8(
                    authority, unchecked((short)registers.Word(PenaltyDgroupOffset)));
            }
        }

        if (authority < 0)                                      // image@0x02CD7 or di,di / jge
        {
            return 0;
        }

        return authority > 0x0100 ? (short)0x0100 : authority;  // image@0x02CDE
    }

    /// <summary>
    /// <c>image@0x02BFE..0x02CD5</c> — the four evasion terms that only apply when the TARGET is the
    /// player: g-load, then the heading delta (skipped in a mid-pitch band), the elevation delta and
    /// the shooter's own pitch.
    /// </summary>
    private static void ApplyPlayerEvasion(ProjectileKernelContext context, CombatObjectView owner)
    {
        CombatRegisters registers = context.Registers;
        registers.SetWord(PenaltyDgroupOffset, 0x0100);         // image@0x02BFE

        short gLoad = unchecked((short)registers.Word(PlayerGLoadDgroupOffset));   // image@0x02C08
        if (gLoad < 0)
        {
            gLoad = unchecked((short)-gLoad);                   // image@0x02C0B cwd/xor/sub
        }

        ApplyEvasionTerm(registers, 0x0080, gLoad, 0x0300, 0x0500);   // image@0x02C16

        CombatObjectView player = new CombatObjectView(context.Arena, registers.PlayerObjectRef);

        // image@0x02C1D..0x02C2B — the heading term is skipped while the player's pitch is inside
        // (0x1E0, 0x960): at those attitudes heading is not what the shot has to lead.
        if (player.Elevation <= 0x01E0 || player.Elevation >= 0x0960)
        {
            short headingDelta = FoldQuadrant(
                unchecked((short)(player.Heading - owner.Heading)));   // image@0x02C2D..0x02C4F
            ApplyEvasionTerm(registers, 0x00C0, headingDelta, 0x00F0, 0x01E0);   // image@0x02C5F
        }

        short elevationDelta = FoldQuadrant(
            unchecked((short)(player.Elevation - owner.Elevation)));   // image@0x02C62..0x02C88
        ApplyEvasionTerm(registers, 0x00C0, elevationDelta, 0x00F0, 0x01E0);   // image@0x02C98

        short ownerPitch = owner.Elevation;                     // image@0x02C9B
        if (ownerPitch > Angle.QuarterCircle)                   // image@0x02CA5 cmp ax,0x2d0 / jle
        {
            ownerPitch = unchecked((short)(ownerPitch - Angle.HalfCircle));
            if (ownerPitch < 0)
            {
                ownerPitch = unchecked((short)-ownerPitch);
            }
        }

        ApplyEvasionTerm(registers, 0x00C0, ownerPitch, 0x0078, 0x0230);   // image@0x02CBF
    }
}

/// <summary>
/// <c>combat_engagement_state_update @image@0x02A1F</c> — set the spawn record's "target locked"
/// bit and decide, with ONE random draw against the fire authority, whether this shot counts as a
/// hit this frame.
/// </summary>
/// <remarks>
/// <para>
/// Two doors, both inside <c>combat_object_tick</c>: <c>image@0x027A2</c> (the tracking phase, once
/// the 2-D proximity score is <c>&lt;= 8</c>) and <c>image@0x029CC</c> (right after a fresh
/// acquisition).
/// </para>
/// <para>
/// Bit1 — "firing this frame" — is set on THREE paths (<c>image@0x02A62</c>): no target at all, a
/// target that does not carry an engagement block, and a target that does but whose fire authority
/// LOSES the draw is the only path that CLEARS it (<c>image@0x02A5C and byte [bx+0x1a],0xfd</c>). So
/// the default is "this shot hits" and the authority is a chance to MISS.
/// </para>
/// </remarks>
public static class CombatEngagementStateUpdate
{
    /// <summary>Runs the update.</summary>
    /// <param name="context">The kernel context.</param>
    /// <param name="record">The spawn record — the original's <c>[bp+6]</c>.</param>
    /// <param name="targetRef">The target's pool object — the original's <c>[bp+4]</c>.</param>
    public static void Update(
        ProjectileKernelContext context, SpawnRecordRef record, ushort targetRef)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Census.StateUpdateCalls++;

        record.StatusFlags = unchecked((byte)(record.StatusFlags | 0x01));   // image@0x02A26

        if (targetRef != 0)                                     // image@0x02A2A
        {
            CombatObjectView target = new CombatObjectView(context.Arena, targetRef);
            if (target.CarriesEngagement)                       // image@0x02A39 test es:[bx+3],8
            {
                byte draw = context.Random.Rand8();              // image@0x02A40 lcall 0x201d:0x9d36
                short authority = EngagementFireAuthority.Compute(
                    context, targetRef, record.OwnerId, record.WeaponClassRef);   // image@0x02A52

                if (authority < draw)                            // image@0x02A55 cmp ax,si / jge
                {
                    record.StatusFlags = unchecked((byte)(record.StatusFlags & 0xFD));   // image@0x02A5C
                    return;
                }

                record.StatusFlags = unchecked((byte)(record.StatusFlags | 0x02));
                return;
            }
        }

        record.StatusFlags = unchecked((byte)(record.StatusFlags | 0x02));   // image@0x02A65
    }
}
