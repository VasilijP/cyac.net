using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Sim.Combat;

/// <summary>
/// The tracking phase's qualification cluster: <c>combat_spawn_fire_eligibility_check
/// @image@0x02CF3</c> (eight gates), <c>target_fire_angle_qualify @image@0x0383A</c> (the
/// attitude-alignment gate) and <c>combat_spawn_altitude_window_tick @image@0x038C1</c> (the one RNG
/// draw on this row).
/// </summary>
/// <remarks>
/// <para>
/// INT-only: the whole cluster decides whether a guided shot keeps its lock, and the altitude window
/// draws from the Sim LFSR, so its call ORDER is part of the determinism contract.
/// </para>
/// <para>
/// The whole cluster — the altitude window, its <c>prng_rand8</c> and the tracking phase — is
/// taken from the bytes.  Gate 1 rejecting 15,419 of 15,419 calls is a measurement of ONE
/// recording, not a law — the census here counts
/// the draws.
/// </para>
/// </remarks>
public static class CombatSpawnFireEligibility
{
    /// <summary>
    /// <c>combat_spawn_altitude_window_tick @image@0x038C1</c> — a once-per-shot dice roll, gated on
    /// the shot's own pitch, that makes a guided shot give up its track.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three gates then a draw.  The class byte <c>+0x03</c> being zero disables the window entirely
    /// (<c>image@0x038CB</c>); <c>StatusFlags</c> bit2 makes it fire at most once per shot
    /// (<c>image@0x038D1</c>, set at <c>image@0x038F0</c>); and the shot's own elevation word must be
    /// inside <c>[0x01B8, 0x03E8]</c> — a narrow 27°..125° band around the nose-down régime
    /// (<c>image@0x038E4</c>/<c>0x038EA</c>, both SIGNED).
    /// </para>
    /// <para>
    /// The outcome is <c>class[+0x03] &gt; draw</c> OR the shot has no target at all
    /// (<c>image@0x03900</c>/<c>0x03904</c>); either way it sets <c>StatusFlags</c> bit3 — "fly
    /// level" — and returns 1.  The caller then treats a 1 as a FAILURE and detaches
    /// (<c>image@0x02DA2 or al,al / je 0x2DB0</c>): the window models losing lock, not gaining it.
    /// </para>
    /// </remarks>
    /// <param name="context">The kernel context.</param>
    /// <param name="record">The spawn record.</param>
    /// <returns><c>true</c> when the shot lost its track this frame.</returns>
    public static bool AltitudeWindowTick(ProjectileKernelContext context, SpawnRecordRef record)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Census.AltitudeWindowTicks++;

        WeaponClassView weapon = new WeaponClassView(context.StaticData, record.WeaponClassRef);
        if (weapon.AltitudeWindowChance == 0)                   // image@0x038CB
        {
            return false;
        }

        if ((record.StatusFlags & 0x04) != 0)                   // image@0x038D1
        {
            return false;
        }

        CombatObjectView self = new CombatObjectView(context.Arena, record.PoolObjectRef);
        short pitch = self.Elevation;                           // image@0x038E0
        if (pitch < 0x01B8 || pitch > 0x03E8)                   // image@0x038E4 / 0x038EA (SIGNED)
        {
            return false;
        }

        record.StatusFlags = unchecked((byte)(record.StatusFlags | 0x04));   // image@0x038F0
        context.Census.AltitudeWindowDraws++;

        byte draw = context.Random.Rand8();                     // image@0x038F4 lcall 0x201d:0x9d36
        short chance = weapon.AltitudeWindowChance;             // image@0x038FB (zero-extended)

        // image@0x03900 cmp cx,ax / jg 0x390A ; else image@0x03904 cmp word [si+8],0 / jne 0x3912
        if (chance <= draw && record.TargetRef != 0)
        {
            return false;
        }

        record.StatusFlags = unchecked((byte)(record.StatusFlags | 0x08));   // image@0x0390A
        return true;
    }

    /// <summary>
    /// <c>target_fire_angle_qualify @image@0x0383A</c> — the air-to-ground sight-line gate: are the
    /// two objects' ATTITUDES (not their bearing) within <c>0x118</c> (35°) of each other?
    /// </summary>
    /// <remarks>
    /// Elevation first, with the same straight-up/straight-down escape the cone check has (within
    /// <c>0x118</c> of <c>0x2D0</c> or <c>0x870</c> returns true outright,
    /// <c>image@0x0386C..0x0388C</c>), then heading.  Both deltas are folded at the half circle.
    /// </remarks>
    /// <param name="context">The kernel context.</param>
    /// <param name="ownerRef">The firing object (the original's <c>[bp+8]</c>).</param>
    /// <param name="targetRef">The target (the original's <c>[bp+4]</c>).</param>
    /// <returns><c>true</c> when the attitudes qualify.</returns>
    public static bool FireAngleQualify(
        ProjectileKernelContext context, ushort ownerRef, ushort targetRef)
    {
        ArgumentNullException.ThrowIfNull(context);

        CombatObjectView owner = new CombatObjectView(context.Arena, ownerRef);
        CombatObjectView target = new CombatObjectView(context.Arena, targetRef);

        short d = unchecked((short)(owner.Elevation - target.Elevation));   // image@0x03844..0x0384B
        if (d < 0)
        {
            d = unchecked((short)-d);                           // image@0x0384E cwd/xor/sub
        }

        if (d > Angle.HalfCircle)                               // image@0x03855
        {
            d = unchecked((short)(Angle.FullCircle - d));
        }

        if (d > 0x0118)                                         // image@0x03862 cmp cx,0x118 / jle
        {
            return false;
        }

        short up = unchecked((short)(owner.Elevation - Angle.QuarterCircle));   // image@0x03870
        if (up < 0)
        {
            up = unchecked((short)-up);
        }

        if (up < 0x0118)                                        // image@0x03878 jl 0x38B3
        {
            return true;
        }

        short down = unchecked((short)(owner.Elevation - Angle.ThreeQuarterCircle));   // image@0x03881
        if (down < 0)
        {
            down = unchecked((short)-down);
        }

        if (down < 0x0118)                                      // image@0x03889 jl 0x38B3
        {
            return true;
        }

        short h = unchecked((short)(owner.Heading - target.Heading));   // image@0x0388E..0x03895
        if (h < 0)
        {
            h = unchecked((short)-h);
        }

        if (h > Angle.HalfCircle)                               // image@0x038A0
        {
            h = unchecked((short)(Angle.FullCircle - h));
        }

        // image@0x038AD cmp cx,0x118 / jg 0x3868 (the FALSE arm)
        return h <= 0x0118;
    }

    /// <summary>
    /// <c>combat_spawn_fire_eligibility_check @image@0x02CF3</c> — the eight gates a tracking guided
    /// shot must keep passing; failing any of them DETACHES the shot (releases the active counter and
    /// zeroes its target).
    /// </summary>
    /// <remarks>
    /// The gates, in order, all from the bytes:
    /// <list type="number">
    ///   <item>the weapon class must be GUIDED — <c>class[+0x24] &amp; 0x10</c>
    ///     (<c>image@0x02D16</c>);</item>
    ///   <item>the shot's <c>+0x04</c> owner-or-slot object must be ACTIVE
    ///     (<c>image@0x02D1F</c>);</item>
    ///   <item>the target object must be ACTIVE (<c>image@0x02D26</c>);</item>
    ///   <item>when the class kind is 1, the shot's OWNER must carry an engagement block whose
    ///     <c>+0x05</c> flags have bit3 set — the HOSTILE/engaging gate
    ///     (<c>image@0x02D31..0x02D42</c>);</item>
    ///   <item>the target must be inside the shot's own aiming cone
    ///     (<c>image@0x02D5B</c>);</item>
    ///   <item>and, when the shot's object is NOT its <c>+0x04</c> object, inside that object's cone
    ///     too (<c>image@0x02D79</c>);</item>
    ///   <item>when the class is guided AND its kind byte is 0, the attitudes must qualify
    ///     (<c>image@0x02D97</c>);</item>
    ///   <item>finally the altitude window must NOT fire (<c>image@0x02D9F</c>).</item>
    /// </list>
    /// </remarks>
    /// <param name="context">The kernel context.</param>
    /// <param name="record">The spawn record.</param>
    public static void Check(ProjectileKernelContext context, SpawnRecordRef record)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Census.EligibilityChecks++;

        WeaponClassView weapon = new WeaponClassView(context.StaticData, record.WeaponClassRef);
        ushort ownerSlotRef = record.OwnerOrSlot1;
        ushort targetRef = record.TargetRef;

        if (Qualifies(context, record, weapon, ownerSlotRef, targetRef))
        {
            return;
        }

        // image@0x02DA6 — DETACH.
        CombatSpawnDepart.DecrementActiveCounter(context, record);
        record.TargetRef = 0;
    }

    private static bool Qualifies(
        ProjectileKernelContext context,
        SpawnRecordRef record,
        WeaponClassView weapon,
        ushort ownerSlotRef,
        ushort targetRef)
    {
        if ((weapon.ClassFlags & 0x10) == 0)                    // image@0x02D16
        {
            return false;
        }

        PoolArena arena = context.Arena;
        if ((arena.Word((ushort)(ownerSlotRef + 0x02)) & 1) == 0)   // image@0x02D1F
        {
            return false;
        }

        if ((arena.Word((ushort)(targetRef + 0x02)) & 1) == 0)      // image@0x02D26
        {
            return false;
        }

        if (weapon.Kind == 1)                                   // image@0x02D31
        {
            CombatObjectView ownerObject = new CombatObjectView(arena, record.OwnerId);
            EngagementBlockView ownerBlock = new EngagementBlockView(arena, ownerObject.EngagementBlockRef);
            if ((ownerBlock.Flags & 0x08) == 0)                 // image@0x02D42
            {
                return false;
            }
        }

        CombatObjectView self = new CombatObjectView(arena, record.PoolObjectRef);
        CombatObjectView target = new CombatObjectView(arena, targetRef);

        if (!CombatGeometry.BearingConeCheck(                   // image@0x02D5B
                self.Position, self.Heading, self.Elevation, target.Position, weapon.ConeHalfAngle))
        {
            return false;
        }

        if (record.PoolObjectRef != ownerSlotRef)               // image@0x02D62
        {
            CombatObjectView slotObject = new CombatObjectView(arena, ownerSlotRef);
            if (!CombatGeometry.BearingConeCheck(               // image@0x02D79
                    slotObject.Position,
                    slotObject.Heading,
                    slotObject.Elevation,
                    target.Position,
                    weapon.ConeHalfAngle))
            {
                return false;
            }
        }

        if ((weapon.ClassFlags & 0x10) != 0 && weapon.Kind == 0)   // image@0x02D82 / 0x02D88
        {
            if (!FireAngleQualify(context, ownerSlotRef, targetRef))   // image@0x02D97
            {
                return false;
            }
        }

        // image@0x02D9F — a firing altitude window means the shot LOSES its track.
        return !AltitudeWindowTick(context, record);
    }
}
