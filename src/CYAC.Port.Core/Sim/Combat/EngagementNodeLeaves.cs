using CYAC.Port.Core.Sim.Combat.Geometry;

namespace CYAC.Port.Core.Sim.Combat;

/// <summary>
/// The small leaves the per-node FSM and the acquisition state machine share: the player-pressure
/// counters, the two eligibility filters, the fire-angle cone, the acquisition window flags, the
/// close-range kill check, the kill tally and the depart bookkeeping.
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  Every routine here is a straight transliteration of the original's bytes.
/// </para>
/// <para>
/// The recurring shape is a FAR pointer to <c>DS:0xED54</c> — the DGROUP scratch copy of the
/// engagement record.  Every one of the four pressure counters is called as <c>mov ax,0xed54 /
/// push ds / push ax / call</c>, so <c>es:[bx+N]</c> inside them is simply <c>[0xED54 + N]</c>:
/// <c>+0x00</c> is the class-prototype near pointer <c>[0xED54]</c>, <c>+0x11</c> is the
/// acquisition state <c>[0xED65]</c>, <c>+0x10</c> the type-slot index <c>[0xED64]</c> and
/// <c>+0x1B</c> the current target <c>[0xED6F]</c>.
/// </para>
/// </remarks>
public static class EngagementNodeLeaves
{
    /// <summary>
    /// <c>engagement_player_F122_increment @image@0x0C272</c> — "one more engagement is targeting
    /// the player".
    /// </summary>
    /// <param name="context">The node context.</param>
    public static void PlayerPressureIncrement(EngagementNodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!PressureGate(context))
        {
            return;
        }

        context.Registers.SetWord(0xF122, unchecked((ushort)(context.Registers.Word(0xF122) + 1)));
    }

    /// <summary>
    /// <c>engagement_player_F122_decrement @image@0x0C292</c> — the guarded twin.
    /// </summary>
    /// <remarks>The decrement is FLOORED at zero (<c>cmp word [0xf122],0 / jle</c>
    /// @<c>image@0x0C2AA</c>) while the increment is unguarded — an asymmetry the port keeps.</remarks>
    /// <param name="context">The node context.</param>
    public static void PlayerPressureDecrement(EngagementNodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!PressureGate(context))
        {
            return;
        }

        short value = unchecked((short)context.Registers.Word(0xF122));
        if (value > 0)                                              // image@0x0C2AF jle
        {
            context.Registers.SetWord(0xF122, unchecked((ushort)(value - 1)));
        }
    }

    /// <summary>
    /// <c>engagement_player_F128_increment @image@0x0C2B9</c> — "one more engagement is in
    /// acquisition state 3 against the player, with a type-1 weapon".
    /// </summary>
    /// <param name="context">The node context.</param>
    public static void PlayerLockPressureIncrement(EngagementNodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!LockPressureGate(context))
        {
            return;
        }

        context.Registers.SetWord(0xF128, unchecked((ushort)(context.Registers.Word(0xF128) + 1)));
    }

    /// <summary>
    /// <c>engagement_player_F128_decrement @image@0x0C2F1</c> — the guarded twin
    /// (floored at zero, <c>image@0x0C31F</c>).
    /// </summary>
    /// <param name="context">The node context.</param>
    public static void PlayerLockPressureDecrement(EngagementNodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!LockPressureGate(context))
        {
            return;
        }

        short value = unchecked((short)context.Registers.Word(0xF128));
        if (value > 0)                                              // image@0x0C324 jle
        {
            context.Registers.SetWord(0xF128, unchecked((ushort)(value - 1)));
        }
    }

    /// <summary>
    /// <c>engagement_slot_player_selected_view_check @image@0x078B8</c> — is this engagement the one
    /// the player's camera is watching?
    /// </summary>
    /// <param name="context">The node context.</param>
    /// <returns>The original's <c>AL</c>.</returns>
    public static bool IsPlayerSelectedView(EngagementNodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters registers = context.Registers;
        return registers.Word(0xED56) == registers.Word(0x00BE)     // image@0x078BB
            && (registers.Byte(0xF12A) & 4) != 0;                   // image@0x078C1
    }

    /// <summary>
    /// <c>target_type_filter_check @image@0x078D0</c> — the "this target is off limits" filter.
    /// </summary>
    /// <remarks>
    /// The argument is the candidate's ENGAGEMENT BLOCK far pointer, and the first test compares its
    /// near offset against <c>g_player_engagement_block_off [0xEF1E]</c>
    /// (<c>image@0x078D6</c>) — the reading C1 §4.7 established.  For the PLAYER the answer is
    /// "off limits" unless the difficulty byte <c>[0xC316]</c> is positive or the damage-suppress
    /// flag <c>[0xC32F]</c> is set; for anyone else, only a block in phase 6.
    /// </remarks>
    /// <param name="context">The node context.</param>
    /// <param name="blockRef">The candidate's engagement-block near offset.</param>
    /// <returns>The original's <c>AL</c>: true means FILTERED OUT.</returns>
    public static bool TargetTypeFiltered(EngagementNodeContext context, ushort blockRef)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters registers = context.Registers;

        if (registers.Word(0xEF1E) == blockRef)                     // image@0x078D6
        {
            return unchecked((sbyte)registers.Byte(0xC316)) > 0     // image@0x078DC jg
                || registers.Byte(0xC32F) != 0;                     // image@0x078E3 je
        }

        return context.Arena.Byte((ushort)(blockRef + 0x0D)) == 6;   // image@0x078F5
    }

    /// <summary>
    /// <c>target_engagement_eligibility_check @image@0x07904</c> — may this engagement keep the
    /// object in <paramref name="objectRef"/> as its target?
    /// </summary>
    /// <param name="context">The node context.</param>
    /// <param name="objectRef">The candidate object's pool near offset — the original's <c>BX</c>.</param>
    /// <returns>The original's <c>AL</c>.</returns>
    public static bool TargetEligible(EngagementNodeContext context, ushort objectRef)
    {
        ArgumentNullException.ThrowIfNull(context);
        PoolArena arena = context.Arena;

        if ((arena.Word((ushort)(objectRef + 2)) & 1) == 0)         // image@0x07912 test es:[si+2],1
        {
            return false;
        }

        ushort block = arena.EngagementBlockRef(objectRef);         // image@0x07920 the far thunk
        byte flags = arena.Byte((ushort)(block + 5));               // image@0x0792E

        // image@0x07932: xor al,[0xed59] / test al,0x40 — the two records' bit6 must DIFFER, i.e.
        // the candidate must be on the other side.
        if (((flags ^ context.Registers.Byte(0xED59)) & 0x40) == 0)
        {
            return false;
        }

        if ((flags & 0x20) != 0)                                    // image@0x0793A
        {
            return false;
        }

        return !TargetTypeFiltered(context, block);                 // image@0x07943
    }

    /// <summary>
    /// <c>target_fire_angle_qualify @image@0x0383A</c> — are the shooter's and the player's
    /// orientations within 35° of each other in both axes?
    /// </summary>
    /// <remarks>
    /// <para>
    /// It compares two ORIENTATION words, not a bearing: <c>shooter[+0x14]</c> against
    /// <c>player[+0x14]</c> and then <c>shooter[+0x12]</c> against <c>player[+0x12]</c>, each folded
    /// at the half circle (<c>image@0x03855</c>, <c>image@0x038A0</c>) and compared against
    /// <c>0x118</c> (35°).
    /// </para>
    /// <para>
    /// Between them sits the same straight-up / straight-down ESCAPE an earlier pass measured in
    /// <c>combat_bearing_cone_check</c>: an elevation within <c>0x118</c> of <c>0x2D0</c> (UP) or
    /// <c>0x870</c> (DOWN) returns TRUE without testing heading (<c>image@0x0386C..0x0388C</c>).
    /// </para>
    /// </remarks>
    /// <param name="context">The node context.</param>
    /// <param name="shooterRef">The pushed <c>[0xED56]</c> — the firing engagement's object.</param>
    /// <param name="playerRef">The pushed <c>[0x00C0]</c> — the player's object.</param>
    /// <returns>The original's <c>AL</c>.</returns>
    public static bool FireAngleQualifies(
        EngagementNodeContext context, ushort shooterRef, ushort playerRef)
    {
        ArgumentNullException.ThrowIfNull(context);
        PoolArena arena = context.Arena;

        short shooterElevation = unchecked((short)arena.Word((ushort)(shooterRef + 0x14)));
        short playerElevation = unchecked((short)arena.Word((ushort)(playerRef + 0x14)));
        if (FoldedDelta(shooterElevation, playerElevation) > 0x118)     // image@0x03862 jle
        {
            return false;
        }

        if (Abs16(unchecked((short)(shooterElevation - 0x2D0))) < 0x118)  // image@0x03878 jl
        {
            return true;
        }

        if (Abs16(unchecked((short)(shooterElevation - 0x870))) < 0x118)  // image@0x03889 jl
        {
            return true;
        }

        short shooterHeading = unchecked((short)arena.Word((ushort)(shooterRef + 0x12)));
        short playerHeading = unchecked((short)arena.Word((ushort)(playerRef + 0x12)));
        return FoldedDelta(shooterHeading, playerHeading) <= 0x118;      // image@0x038AD jg
    }

    /// <summary>
    /// <c>engagement_slot_phase0_range_check @image@0x05026</c> — the idle-phase altitude test.
    /// </summary>
    /// <remarks>
    /// The C3b brief attributes this to the FSM's case 0.  It is NOT called from the FSM: a
    /// recursive-descent walk of <c>image@0x0416C..0x048B7</c> lists 51 call sites and
    /// <c>0x05026</c> is not among them, and case 0's body (<c>image@0x041D2..0x041E9</c>) is only
    /// the <c>[0xEDAC] = max(0, [0xED62] − [0xF0C8])</c> countdown.  It is ported here because it
    /// shares the FSM's whole read set and belongs to the same cluster.
    /// </remarks>
    /// <param name="context">The node context.</param>
    /// <returns>The original's <c>AL</c>.</returns>
    public static bool Phase0RangeCheck(EngagementNodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters registers = context.Registers;
        if (registers.Byte(0xED61) != 0)                                // image@0x05026
        {
            return false;
        }

        ushort prototype = registers.Word(0xED3C);                      // image@0x0502D
        // image@0x05031: {lo = prototype[+0x2C], hi = 0} against the fire position's Y — a 32-bit
        // signed-high / unsigned-low compare.
        int ceiling = context.StaticData.Word(prototype + 0x2C);
        int altitude = unchecked((int)(registers.Word(0xED46) | ((uint)registers.Word(0xED48) << 16)));
        return GreaterOrEqual(ceiling, altitude);                       // image@0x05036..0x05042
    }

    /// <summary>
    /// <c>acq_target_engagement_window_check @image@0x0504C</c> — the five fire-decision flags
    /// <c>[0xB534]</c>, <c>[0xB538]</c>, <c>[0xB539]</c>, <c>[0xB53A]</c> and <c>[0xB53C]</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It writes nothing at all when there is no acquisition target (<c>image@0x05052</c>), which is
    /// why the five flags visibly persist across a target-less pass.
    /// </para>
    /// <para>
    /// The two angles are measured against the SAME bearing — ours (<c>[0xED4E]</c>) and the
    /// target's (<c>[0xEDC2]</c>) — and their thresholds are OPPOSITE senses:
    /// <c>[0xB53A]</c> is "we are pointing at it" (<c>&lt;= 0x2D0</c>) and <c>[0xB539]</c> is
    /// "it is pointing AWAY from us" (<c>&gt;= 0x2D0</c>).
    /// </para>
    /// </remarks>
    /// <param name="context">The node context.</param>
    public static void AcquisitionWindowCheck(EngagementNodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters registers = context.Registers;
        EngagementAngleView view = context.View;

        ushort target = view.AcquisitionTarget;
        if (target == 0)                                                // image@0x05052
        {
            return;
        }

        short bearing = CombatGeometry.Bearing2d(                       // image@0x0507C
            view.FirePosition, new CombatPosition(view.InterceptX, 0, view.InterceptZ));

        short ourDelta = Fold(Abs16(unchecked((short)(view.Heading - bearing))));   // image@0x05084
        registers.SetByte(0xB53A, ourDelta <= 0x2D0 ? (byte)1 : (byte)0);          // image@0x050A0 jg

        short theirDelta = Fold(Abs16(unchecked((short)(view.InterceptHeading - bearing))));
        registers.SetByte(0xB539, theirDelta >= 0x2D0 ? (byte)1 : (byte)0);        // image@0x050C8 jl

        registers.SetWord(                                              // image@0x050DD
            0xB53C,
            FirePosGeometry.Distance3dScaled(
                view.FirePosition, ObjectPosition(context.Arena, target), false, 0, 0, 0));

        // image@0x050E7: the near thunk @0x02276 answers the target's CLASS PROTOTYPE, then its
        // +0x26 arc-descriptor pointer.
        ushort theirPrototype = context.Arena.Word(context.Arena.EngagementBlockRef(target));
        ushort theirArc = context.StaticData.Word(theirPrototype + 0x26);

        registers.SetByte(                                              // image@0x050F8 jae
            0xB534,
            context.StaticData.Byte(theirArc + 0x18) < view.ArcParamA ? (byte)1 : (byte)0);

        short reach = unchecked((short)(context.StaticData.Word(theirArc + 0x0E) + 0x4B));
        ushort ourArc = context.StaticData.Word(view.WeaponDescriptorTable + 0x26);
        registers.SetByte(                                              // image@0x05117 jle
            0xB538,
            unchecked((short)context.StaticData.Word(ourArc + 0x0E)) > reach ? (byte)1 : (byte)0);
    }

    /// <summary>
    /// <c>engagement_close_range_proximity_kill_check @image@0x08758</c> — the "we flew into the
    /// player" kill.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The byte there is a <c>0x90</c> alignment pad and the only call site (<c>image@0x047B2</c>)
    /// targets <c>0x08758</c>.  Extent and entry disagree by one — a report-only scanner note.
    /// </para>
    /// <para>
    /// The proximity test is deliberately COARSE: it compares only the HIGH words of the three
    /// coordinates (<c>[0xED44]</c>/<c>[0xED48]</c>/<c>[0xED4C]</c> against the player object's
    /// <c>+0x08</c>/<c>+0x0C</c>/<c>+0x10</c>) and accepts a Manhattan sum of at most 3 — a
    /// ±3×65,536-unit box (<c>image@0x08798</c>).
    /// </para>
    /// </remarks>
    /// <param name="context">The node context.</param>
    public static void CloseRangeProximityKillCheck(EngagementNodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters registers = context.Registers;

        if (registers.Byte(0xE46B) != 0)                                // image@0x08758 cheat gate
        {
            return;
        }

        if (unchecked((sbyte)registers.Byte(0xC316)) > 0)               // image@0x0875F jg
        {
            return;
        }

        ushort player = registers.PlayerObjectRef;
        PoolArena arena = context.Arena;

        int dz = Abs16(unchecked((short)(registers.Word(0xED4C) - arena.Word((ushort)(player + 0x10)))));
        int dy = Abs16(unchecked((short)(registers.Word(0xED48) - arena.Word((ushort)(player + 0x0C)))));
        int dx = Abs16(unchecked((short)(registers.Word(0xED44) - arena.Word((ushort)(player + 0x08)))));
        short sum = unchecked((short)(dz + (dy + dx)));                 // image@0x08794 add bx,ax / add cx,bx
        if (sum > 3)                                                    // image@0x08798 jg
        {
            return;
        }

        ushort distance = FirePosGeometry.Distance3dScaled(             // image@0x087A3
            context.View.FirePosition, ObjectPosition(arena, player), false, 0, 0, 0);

        // image@0x087AA..0x087B9: cl = (prototype[+0x0C] & 0x80) == 0 ? 2 : 1 — the `cmp cx,1 /
        // sbb cl,cl / and cl,1 / add cl,1` idiom, i.e. a class-dependent halving of the radius.
        int shift = (context.StaticData.Byte(context.View.WeaponDescriptorTable + 0x0C) & 0x80) == 0
            ? 2
            : 1;

        // image@0x087C0: a WORD read of the prototype's +0x09 (the byte pair +0x09/+0x0A).
        short radius = unchecked((short)context.StaticData.Word(registers.Word(0xED3C) + 9));
        radius = unchecked((short)(radius >> shift));                   // image@0x087C3 sar dx,cl

        if (unchecked((ushort)radius) <= distance)                      // image@0x087C7 jbe
        {
            return;
        }

        context.Census.CloseRangeKill++;
        context.Interpreter.Run(context.Registers, context.Arena, 5);   // image@0x087CB
        registers.SetByte(0xEDE5, 1);                                   // image@0x087CE
        KillTallyAndSlotDispatch(context);                              // image@0x087D4 push cs / call
    }

    /// <summary>
    /// <c>engagement_kill_tally_and_slot_dispatch @image@0x08738</c> — bump one of the two kill
    /// counters and fire the <c>.S</c> module's <c>on_slot_destroyed</c> hook.
    /// </summary>
    /// <remarks>
    /// <c>[0xED59]</c> bit6 selects the counter: SET goes to <c>[0xF106]</c> and clear to
    /// <c>[0xF102]</c> (<c>image@0x08738</c>).  Neither counter is inside a PROBE record's window
    /// set, so a P2 verification CENSUSES this pair rather than comparing it.
    /// </remarks>
    /// <param name="context">The node context.</param>
    public static void KillTallyAndSlotDispatch(EngagementNodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters registers = context.Registers;

        if ((registers.Byte(0xED59) & 0x40) != 0)                       // image@0x08738
        {
            context.Census.KillTallyEnemy++;
            registers.SetWord(0xF106, unchecked((ushort)(registers.Word(0xF106) + 1)));
        }
        else
        {
            context.Census.KillTallyFriendly++;
            registers.SetWord(0xF102, unchecked((ushort)(registers.Word(0xF102) + 1)));
        }

        context.MissionHook?.OnSlotDestroyed(registers, registers.Word(0xED56));  // image@0x0874E
    }

    /// <summary>
    /// The shared bookkeeping prologue of <c>engagement_slot_impact_and_depart</c>,
    /// <c>image@0x08B72</c>: drop both pressure counters, clear <c>[0xED3E]</c> bit0 and emit the
    /// three departure notifications.
    /// </summary>
    /// <param name="context">The node context.</param>
    public static void SlotDepartBookkeeping(EngagementNodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters registers = context.Registers;

        PlayerPressureDecrement(context);                               // image@0x08B77
        PlayerLockPressureDecrement(context);                           // image@0x08B7F
        registers.SetByte(0xED3E, (byte)(registers.Byte(0xED3E) & 0xFE));  // image@0x08B82

        ushort slot = registers.Word(0xED56);
        context.Events.RecordDeparture(slot);                           // image@0x08B8B
        context.Events.ClearSubsystem4x19(slot);                        // image@0x08B94
        if (IsPlayerSelectedView(context))                              // image@0x08B99
        {
            context.Events.PlayDepartureTone();                         // image@0x08BA0
        }
    }

    /// <summary>
    /// <c>engagement_slot_impact_and_depart @image@0x08BA6</c> — the bookkeeping plus the ground
    /// impact's three effect calls.
    /// </summary>
    /// <param name="context">The node context.</param>
    public static void SlotImpactAndDepart(EngagementNodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        SlotDepartBookkeeping(context);                                 // image@0x08BA6
        context.Events.ImpactEffects(context.View.FirePosition);         // image@0x08BB9/0x08BDD/0x08BE9
    }

    /// <summary>
    /// <c>acq_timer_advance @image@0x07450</c> — <c>[0xEDAC] = min([0xEDAC], AX)</c>, UNSIGNED.
    /// </summary>
    /// <param name="context">The node context.</param>
    /// <param name="candidate">The original's <c>AX</c>.</param>
    public static void TimerAdvance(EngagementNodeContext context, ushort candidate)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Registers.Word(0xEDAC) > candidate)                 // image@0x07450 jbe
        {
            context.Registers.SetWord(0xEDAC, candidate);
        }
    }

    /// <summary>A pool object's position triple — its <c>+0x06</c>, <c>+0x0A</c> and <c>+0x0E</c>.</summary>
    /// <param name="arena">The arena.</param>
    /// <param name="objectRef">The object's near offset.</param>
    /// <returns>The triple.</returns>
    public static CombatPosition ObjectPosition(PoolArena arena, ushort objectRef)
    {
        ArgumentNullException.ThrowIfNull(arena);
        return new CombatPosition(
            ReadInt32(arena, (ushort)(objectRef + 0x06)),
            ReadInt32(arena, (ushort)(objectRef + 0x0A)),
            ReadInt32(arena, (ushort)(objectRef + 0x0E)));
    }

    /// <summary>A 32-bit little-endian value in the arena.</summary>
    /// <param name="arena">The arena.</param>
    /// <param name="nearOffset">The low word's near offset.</param>
    /// <returns>The value.</returns>
    public static int ReadInt32(PoolArena arena, ushort nearOffset)
    {
        ArgumentNullException.ThrowIfNull(arena);
        return unchecked((int)(arena.Word(nearOffset) | ((uint)arena.Word((ushort)(nearOffset + 2)) << 16)));
    }

    /// <summary>The original's 32-bit "greater or equal": signed high word, UNSIGNED low word.</summary>
    /// <param name="a">The left value.</param>
    /// <param name="b">The right value.</param>
    /// <returns>Whether <paramref name="a"/> is at or above <paramref name="b"/>.</returns>
    public static bool GreaterOrEqual(int a, int b)
    {
        short ah = unchecked((short)(a >> 16));
        short bh = unchecked((short)(b >> 16));
        return ah != bh ? ah > bh : unchecked((ushort)a) >= unchecked((ushort)b);
    }

    /// <summary>The original's strict 32-bit "greater than", same mixed comparison.</summary>
    /// <param name="a">The left value.</param>
    /// <param name="b">The right value.</param>
    /// <returns>Whether <paramref name="a"/> is strictly above <paramref name="b"/>.</returns>
    public static bool Above(int a, int b)
    {
        short ah = unchecked((short)(a >> 16));
        short bh = unchecked((short)(b >> 16));
        return ah != bh ? ah > bh : unchecked((ushort)a) > unchecked((ushort)b);
    }

    /// <summary>The <c>cdq / xor / sub</c> 16-bit absolute value the combat code uses everywhere.</summary>
    /// <param name="value">The value.</param>
    /// <returns>Its magnitude, wrapping at <see cref="short.MinValue"/> exactly as the original does.</returns>
    public static short Abs16(short value) => value < 0 ? unchecked((short)-value) : value;

    /// <summary>Folds an angular magnitude at the half circle — <c>if (d &gt; 0x5A0) d = 0xB40 − d</c>.</summary>
    /// <param name="delta">The magnitude.</param>
    /// <returns>The folded magnitude.</returns>
    public static short Fold(short delta) =>
        delta > Primitives.Angle.HalfCircle
            ? unchecked((short)(Primitives.Angle.FullCircle - delta))
            : delta;

    private static short FoldedDelta(short a, short b) => Fold(Abs16(unchecked((short)(a - b))));

    /// <summary>
    /// The condition all four pressure counters share (<c>image@0x0C275..0x0C288</c>): the
    /// engagement's current target IS the player, and its class prototype opts in at <c>+0x28</c>.
    /// </summary>
    private static bool PressureGate(EngagementNodeContext context)
    {
        CombatRegisters registers = context.Registers;
        if (registers.Word(0xED6F) != registers.PlayerObjectRef)        // image@0x0C27B
        {
            return false;
        }

        ushort prototype = registers.Word(0xED54);                      // image@0x0C281 es:[bx]
        return context.StaticData.Byte(prototype + 0x28) != 0;          // image@0x0C284
    }

    /// <summary>
    /// The two extra conjuncts of the <c>[0xF128]</c> pair (<c>image@0x0C2D4..0x0C2E5</c>): the
    /// acquisition state <c>[0xED65]</c> must be exactly 3, and the type slot's weapon descriptor
    /// must have <c>+0x00 == 1</c> (through <c>engagement_slot_descriptor_for_type
    /// @image@0x0C254</c>).
    /// </summary>
    private static bool LockPressureGate(EngagementNodeContext context)
    {
        if (!PressureGate(context))
        {
            return false;
        }

        CombatRegisters registers = context.Registers;
        if (registers.Byte(0xED65) != 3)                                // image@0x0C2D4
        {
            return false;
        }

        ushort prototype = registers.Word(0xED54);
        ushort descriptor = context.StaticData.Word(
            prototype + 0x0E + (registers.Byte(0xED64) * 2));           // image@0x0C254..0x0C268
        return context.StaticData.Byte(descriptor) == 1;                // image@0x0C2E2
    }
}
