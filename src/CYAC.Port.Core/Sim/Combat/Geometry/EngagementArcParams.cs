using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Sim.Combat.Geometry;

/// <summary>
/// <c>engagement_arc_param_update @image@0x0661C</c> — the per-pass refresh of the arc parameter
/// block <c>[0xED98..0xEDA0]</c>, <c>[0xED8E]</c> and the arc TIER <c>[0xB532]</c> that the
/// manoeuvring engine then integrates against.
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  Two near callers, both behind the same gate
/// (<c>mov bx,[0xED54] / test byte [bx+0x0D],2 / je</c>): the per-shot FSM's prologue
/// (<c>image@0x041A7</c>, C3b) and <c>engagement_arc_desc_init_and_speed_select</c>
/// (<c>image@0x06E8A</c>).  It is therefore NOT reachable from a P6 probe, and this build
/// unit-tests it from the bytes rather than verifying it per call.
/// </para>
/// <para>
/// <b>The data it reads is an ARC TABLE.</b> <c>[0xED54]</c> is the weapon-descriptor near pointer;
/// its <c>+0x26</c> is a near pointer to a 30-byte arc descriptor
/// (<c>engagement_arc_desc_init_and_speed_select @image@0x06E56</c> copies exactly 28 bytes of it
/// into <c>[0xED8E..0xEDAA]</c> with a <c>rep movsw cx=0x0E</c>).  That descriptor's <c>+0x1C</c>
/// is the head of a SORTED table of 6-byte records — the "range table" — whose
/// <c>+0x02</c> word is the key and whose <c>+0x00</c> word points at a variable-length band string
/// (see <see cref="ArcFloor"/> / <see cref="ArcCeiling"/> / <see cref="ArcTier"/>).  All of it
/// lives in DGROUP, so the port reads it through <see cref="ICombatStaticData"/> — knowledge, never
/// embedded bytes.  <b>Transform ask</b>: neither the arc descriptors nor the range tables have a
/// home in the open-format data tree yet.
/// </para>
/// <para>
/// Source of truth: the original's bytes <c>image@0x0661C..0x066E3</c>, <c>image@0x0864E..0x08736</c>
/// and <c>image@0x2BA9A..0x2BAD6</c>.
/// </para>
/// </remarks>
public static class EngagementArcParams
{
    /// <summary>Each range-table record is six bytes: a band pointer and a key.</summary>
    public const int RangeRecordBytes = 6;

    /// <summary>
    /// Runs the update.
    /// </summary>
    /// <param name="context">The geometry context.</param>
    public static void Update(EngagementGeometryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        EngagementAngleView view = context.View;
        ICombatStaticData data = context.StaticData;

        // P1 — image@0x06623: the key is the fire position's Y HIGH word, halved UNSIGNED.
        ushort descriptor = data.Word(view.WeaponDescriptorTable + 0x26);
        ushort record = SelectRecord(context);                              // image@0x06632

        ArcFloor(context, record);                                          // image@0x06639
        ArcCeiling(context, record);                                        // image@0x0663E
        view.ArcHeading = unchecked((short)data.Word(record + 4));          // image@0x06641

        // P5/P6 — image@0x0664C: the tier, then the decrement rule and the floor of 2.
        short tier = TierFrom(context, record);
        view.ArcTier = tier;                                                // image@0x06676

        // P7 — image@0x06679: the tier's LOW BYTE shifted up 8 (the truncation happens first),
        // interpolated against the accumulator, then >>5 signed and clamped to 0x168.
        short interpolated = LinearInterpClamped(
            unchecked((short)((tier & 0xFF) << 8)), view.SpeedIntegerPart);
        short bankScale = unchecked((short)(interpolated >> 5));            // image@0x06686
        if (bankScale > 0x0168)                                             // image@0x0668A
        {
            bankScale = 0x0168;
        }

        view.BankScaleBase = bankScale;                                     // image@0x06692

        // P8 — image@0x06695: the arc INCREASE rate.
        ushort scaledB = (ushort)(view.ArcParamB << 7);                     // image@0x06695
        ushort divisor = data.Word(descriptor + 0x0E);                      // image@0x0669E
        ushort quotient = MulDiv16Unsigned(
            scaledB, unchecked((ushort)view.SpeedIntegerPart), divisor);
        short rate = unchecked((short)((view.ArcParamA << 7) - quotient));  // image@0x066B1
        view.ArcIncreaseRate = rate;                                        // image@0x066BD

        // P9 — image@0x066C1: the pre-decay floor can raise it.
        short floorCandidate = unchecked((short)((view.ArcParamC << 8) + view.ArcDecayRate));
        if (floorCandidate < rate)                                          // image@0x066CB jge
        {
            view.ArcIncreaseRate = floorCandidate;                          // image@0x066CF
        }

        // P10 — image@0x066D2: never negative.
        if (view.ArcIncreaseRate < 0)
        {
            view.ArcIncreaseRate = 0;                                       // image@0x066D9
        }
    }

    /// <summary>
    /// The range-table record this pass selects — <c>engagement_arc_param_update</c>'s P1
    /// (<c>image@0x06623..0x06632</c>).
    /// </summary>
    /// <remarks>
    /// The key is the fire position's Y HIGH word halved UNSIGNED (<c>shr</c> @<c>image@0x06626</c>)
    /// — i.e. the engagement's ALTITUDE band picks the arc profile.
    /// </remarks>
    /// <param name="context">The geometry context.</param>
    /// <returns>The selected record's near offset.</returns>
    public static ushort SelectRecord(EngagementGeometryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        EngagementAngleView view = context.View;
        ICombatStaticData data = context.StaticData;
        ushort key = (ushort)(view.Registers.Word(0xED47) >> 1);
        ushort descriptor = data.Word(view.WeaponDescriptorTable + 0x26);
        ushort table = data.Word(descriptor + 0x1C);
        return RangeTableLookup(data, table, key);
    }

    /// <summary>
    /// <c>engagement_arc_param_update</c>'s P5/P6 (<c>image@0x0664C..0x06672</c>) — the arc TIER,
    /// after the decrement rule and the floor of 2.
    /// </summary>
    /// <remarks>
    /// Split out because the tier is the ONE value this routine leaves in a global (<c>[0xB532]</c>)
    /// that <c>engagement_slot_angle_update</c> reads and no probe record carries; a verification
    /// reconstructs it from state a probe DOES carry.  The decrement rule fires
    /// when the accumulator is still more than <c>0x64</c> short of the arc heading, OR the slot has
    /// no weapon state at all.
    /// </remarks>
    /// <param name="context">The geometry context.</param>
    /// <param name="record">The record <see cref="SelectRecord"/> chose.</param>
    /// <returns>The tier, at least 2.</returns>
    public static short TierFrom(EngagementGeometryContext context, ushort record)
    {
        ArgumentNullException.ThrowIfNull(context);
        EngagementAngleView view = context.View;
        short tier = ArcTier(context.StaticData, record, view.SpeedIntegerPart);
        bool decrement =
            unchecked((short)(view.SpeedIntegerPart + 0x64)) < view.ArcHeading  // image@0x06658
            || (view.SlotFlags & 3) == 0;                                                // image@0x0665E
        if (decrement)
        {
            tier = unchecked((short)(tier - 1));                            // image@0x06665
        }

        return tier < 2 ? (short)2 : tier;                                  // image@0x06668
    }

    /// <summary>
    /// <c>engagement_range_table_lookup @image@0x0864E</c> — walk a sorted 6-byte record table for
    /// the first record whose <c>+0x02</c> key is at or above <paramref name="key"/>.
    /// </summary>
    /// <remarks>
    /// Two exits with DIFFERENT results.  The normal one (<c>image@0x08666</c>) returns the
    /// matching record; the SENTINEL one (<c>image@0x0866C</c>, taken when a record's key is
    /// <c>-1</c>) returns that record MINUS six — i.e. the LAST real record.  The key compare is
    /// UNSIGNED (<c>jae</c> @<c>image@0x0865E</c>) while the sentinel test is a signed
    /// <c>cmp...,-1</c>, so a key of <c>0xFFFF</c> can never match through the normal exit.
    /// </remarks>
    /// <param name="data">The constant DGROUP regions.</param>
    /// <param name="table">The table's first record.</param>
    /// <param name="key">The value being looked up.</param>
    /// <returns>The selected record's near offset.</returns>
    public static ushort RangeTableLookup(ICombatStaticData data, ushort table, ushort key)
    {
        ArgumentNullException.ThrowIfNull(data);
        ushort record = table;
        for (int guard = 0; guard < 4096; guard++)
        {
            ushort recordKey = data.Word(record + 2);
            if (unchecked((short)recordKey) == -1)                          // image@0x08655
            {
                return unchecked((ushort)(record - RangeRecordBytes));      // image@0x0866C
            }

            if (recordKey >= key)                                           // image@0x0865B jae
            {
                return record;                                              // image@0x08666
            }

            record = unchecked((ushort)(record + RangeRecordBytes));        // image@0x08660
        }

        throw new EngagementGeometrySeamException(
            $"engagement_range_table_lookup @image@0x0864E walked 4096 records from 0x{table:X4} "
                + "without finding a key or the -1 sentinel; the table is not terminated.");
    }

    /// <summary>
    /// <c>engagement_arc_floor @image@0x08676</c> — the arc FLOOR from the record's band string,
    /// raised to the descriptor's own minimum.
    /// </summary>
    /// <remarks>
    /// The record's <c>+0x00</c> word is a near pointer to a byte string; the floor is that first
    /// byte's low 6 bits shifted up 4 (<c>image@0x0867A</c>) — so the band's granularity is 16 arc
    /// units and its two high bits are a CONTINUATION code (see <see cref="ArcCeiling"/>).  The
    /// descriptor's <c>+0x0A</c> is a floor under the floor (<c>image@0x0868B</c>).
    /// </remarks>
    /// <param name="context">The geometry context.</param>
    /// <param name="record">The selected range-table record.</param>
    public static void ArcFloor(EngagementGeometryContext context, ushort record)
    {
        ArgumentNullException.ThrowIfNull(context);
        ICombatStaticData data = context.StaticData;
        EngagementAngleView view = context.View;

        ushort band = data.Word(record);                                    // image@0x08678
        short floor = (short)((data.Byte(band) & 0x3F) << 4);               // image@0x0867A
        ushort descriptor = data.Word(view.WeaponDescriptorTable + 0x26);
        short minimum = unchecked((short)data.Word(descriptor + 0x0A));     // image@0x0868B
        view.ArcFloor = floor >= minimum ? floor : minimum;                 // image@0x0868E jge
    }

    /// <summary>
    /// <c>engagement_arc_ceiling @image@0x08698</c> — the arc CEILING: the SUM of a run of band
    /// bytes, capped by the descriptor's own maximum.
    /// </summary>
    /// <remarks>
    /// The band string is a run-length encoding: each byte contributes <c>(byte &amp; 0x3F)
    /// &lt;&lt; 4</c> to the total and the two HIGH bits say "another byte follows" (<c>test
    /// dl,0xc0 / jne</c> @<c>image@0x086C1</c>).  So an arc band can be arbitrarily wide while
    /// every byte stays inside 6 bits of magnitude.  The descriptor's <c>+0x0E</c> caps the
    /// result (<c>image@0x086D0</c>).
    /// </remarks>
    /// <param name="context">The geometry context.</param>
    /// <param name="record">The selected range-table record.</param>
    public static void ArcCeiling(EngagementGeometryContext context, ushort record)
    {
        ArgumentNullException.ThrowIfNull(context);
        ICombatStaticData data = context.StaticData;
        EngagementAngleView view = context.View;

        ushort cursor = data.Word(record);                                  // image@0x086A4
        short total = 0;
        for (int guard = 0; guard < 4096; guard++)
        {
            byte b = data.Byte(cursor);                                     // image@0x086B4
            total = unchecked((short)(total + ((b & 0x3F) << 4)));          // image@0x086BD
            if ((b & 0xC0) == 0)                                            // image@0x086C1
            {
                break;
            }

            cursor = unchecked((ushort)(cursor + 1));                       // image@0x086AC
            if (guard == 4095)
            {
                throw new EngagementGeometrySeamException(
                    "engagement_arc_ceiling @image@0x086AC walked 4096 band bytes without a "
                        + "terminator; the band string is not terminated.");
            }
        }

        ushort descriptor = data.Word(view.WeaponDescriptorTable + 0x26);
        short cap = unchecked((short)data.Word(descriptor + 0x0E));         // image@0x086CD
        view.ArcCeiling = cap >= total ? total : cap;                       // image@0x086D0 jge
    }

    /// <summary>
    /// <c>engagement_arc_tier_index @image@0x086DE</c> — which band of the record's run-length
    /// string <paramref name="position"/> falls in, as a signed tier.
    /// </summary>
    /// <remarks>
    /// Walks the same band string accumulating <c>(byte &amp; 0x3F) &lt;&lt; 4</c> until the total
    /// reaches <paramref name="position"/>, meanwhile counting the two high bits as
    /// <c>+1</c> (code 1) or <c>-1</c> (code 2) — so the tier is a SIGNED count of how many
    /// "up" bands minus "down" bands lie before the position.  A string that ends before the
    /// position is reached returns 0 (<c>image@0x0872A</c>).
    /// </remarks>
    /// <param name="data">The constant DGROUP regions.</param>
    /// <param name="record">The selected range-table record.</param>
    /// <param name="position">The arc accumulator's integer part.</param>
    /// <returns>The tier.</returns>
    public static short ArcTier(ICombatStaticData data, ushort record, short position)
    {
        ArgumentNullException.ThrowIfNull(data);
        ushort cursor = data.Word(record);                                  // image@0x086E5
        short total = 0;
        short tier = 0;

        for (int guard = 0; guard < 4096; guard++)
        {
            byte b = data.Byte(cursor);                                     // image@0x086FA
            total = unchecked((short)(total + ((b & 0x3F) << 4)));          // image@0x08701
            if (total >= position)                                          // image@0x08704 jge
            {
                return tier;                                                // image@0x08730
            }

            int code = b & 0xC0;                                            // image@0x08709
            if (code == 0)
            {
                return 0;                                                   // image@0x0872A
            }

            if (code == 0x40)
            {
                tier = unchecked((short)(tier + 1));                        // image@0x0871C
            }
            else if (code == 0x80)
            {
                tier = unchecked((short)(tier - 1));                        // image@0x08722
            }

            cursor = unchecked((ushort)(cursor + 1));                       // image@0x08725
        }

        throw new EngagementGeometrySeamException(
            "engagement_arc_tier_index @image@0x086DE walked 4096 band bytes without terminating.");
    }

    /// <summary>
    /// <c>linear_interp_clamped @image@0x2BA9A</c> — <c>x · 156 / max(y, 125)</c>, clamped into
    /// <c>[-640, 640]</c> and then scaled by 16.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The divisor is <c>max(y, 0x7D)</c> (<c>image@0x2BA9E</c>, SIGNED) — a floor of 125 that
    /// stops the ratio exploding near zero.  The clamps are asymmetric in FORM but not in value:
    /// the high clamp tests <c>&gt; 0x280</c> and returns the literal <c>0x2800</c>
    /// (<c>image@0x2BAB4</c>), the low one tests <c>&lt; -640</c> and returns <c>0xD800</c>
    /// (<c>image@0x2BAC4</c>), and everything between is shifted left 4 (<c>image@0x2BAD0</c>) — so
    /// all three arms are "the ratio × 16".
    /// </para>
    /// </remarks>
    /// <param name="x">The original's <c>AX</c>.</param>
    /// <param name="y">The original's <c>DX</c>.</param>
    /// <returns>The interpolated value.</returns>
    /// <exception cref="OverflowException">The ratio does not fit 16 bits — the original's <c>#DE</c>.</exception>
    public static short LinearInterpClamped(short x, short y)
    {
        short divisor = y >= 0x7D ? y : (short)0x7D;                        // image@0x2BA9E jge
        short ratio = Fixed.MulDiv16Signed(x, 0x9C, divisor);               // image@0x2BAAD
        if (ratio > 0x0280)                                                 // image@0x2BAB4 jle
        {
            return 0x2800;                                                  // image@0x2BABA
        }

        if (ratio < unchecked((short)0xFD80))                               // image@0x2BAC4 jge
        {
            return unchecked((short)0xD800);                                // image@0x2BACA
        }

        return unchecked((short)(ratio << 4));                              // image@0x2BAD0
    }

    /// <summary>
    /// <c>muldiv16_unsigned @image@0x1197A</c> — <c>(a · b) / divisor</c> with an unsigned
    /// <c>MUL</c> and an unsigned <c>DIV</c>.
    /// </summary>
    /// <param name="a">The original's <c>AX</c>.</param>
    /// <param name="b">The original's <c>DX</c>.</param>
    /// <param name="divisor">The original's <c>BX</c>.</param>
    /// <returns>The quotient.</returns>
    /// <exception cref="DivideByZeroException"><paramref name="divisor"/> is 0 — the original's <c>#DE</c>.</exception>
    /// <exception cref="OverflowException">The quotient does not fit 16 bits — also <c>#DE</c>.</exception>
    public static ushort MulDiv16Unsigned(ushort a, ushort b, ushort divisor)
    {
        if (divisor == 0)
        {
            throw new DivideByZeroException(
                "muldiv16_unsigned (image@0x1197A) divides by zero here; the original raises #DE.");
        }

        uint quotient = ((uint)a * b) / divisor;
        if (quotient > ushort.MaxValue)
        {
            throw new OverflowException(
                $"muldiv16_unsigned: {a}·{b}/{divisor} = {quotient} does not fit an unsigned "
                    + "16-bit word; the original raises #DE (divide overflow).");
        }

        return (ushort)quotient;
    }
}
