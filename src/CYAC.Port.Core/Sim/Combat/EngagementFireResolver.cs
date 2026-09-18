using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Sim.Combat;

/// <summary>
/// <c>engagement_slot_fire_handler @image@0x0BD57</c> (907 B) — the damage resolver: roll a damage
/// word, subtract the victim's armour, and apply it to the player, to an enemy, or to nothing;
/// then always retire the shot.
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  ONE door image-wide — <c>image@0x02A14</c> in <c>combat_object_tick</c>, a WRAPPING
/// near call that a naive door census misses.
/// </para>
/// <para>
/// Six-word argument block, mapped from the two call sites (<c>image@0x029D5</c> with AL=1 and
/// <c>image@0x02A05</c> with AL=0): <c>[bp+0x0E]</c> the spawn record,
/// <c>[bp+0x0A]/[bp+0x0C]</c> the victim object far pointer, <c>[bp+0x06]/[bp+0x08]</c> a far
/// pointer to the caller's six-word impact position, and <c>[bp+0x04]</c> the AL flag — "this shot
/// is firing this frame".
/// </para>
/// <para>
/// <c>0x0BD57</c>): the damage roll <c>prng_rand_bounded(0x100 | 0x80)</c>
/// (<c>image@0x0BE2B</c>), then the 1-in-16 armour halving <c>prng_rand8</c>
/// (<c>image@0x0BE53</c>), then — only on the smoke arm (<c>image@0x0BF67</c>) or the close-range
/// kill arm (<c>image@0x0C046</c>) — one more <c>prng_rand8</c>.
/// </para>
/// </remarks>
public static class EngagementFireResolver
{
    /// <summary><c>g_accuracy_gun_rounds [0xED36]</c> — rounds the player fired from a GUN class.</summary>
    public const int GunRoundsDgroupOffset = 0xED36;

    /// <summary><c>g_accuracy_missile_rounds [0xED3A]</c> — the guided-class counterpart.</summary>
    public const int MissileRoundsDgroupOffset = 0xED3A;

    /// <summary><c>g_new_engagement_owner [0xEDE2]</c> — the attacker the VM's mode 4 reads.</summary>
    public const int NewEngagementOwnerDgroupOffset = 0xEDE2;

    /// <summary><c>g_player_kill_tally_a [0xF102]</c>.</summary>
    public const int KillTallyADgroupOffset = 0xF102;

    /// <summary><c>g_player_kill_tally_b [0xF106]</c> — chosen by the victim block's flags bit6.</summary>
    public const int KillTallyBDgroupOffset = 0xF106;

    /// <summary><c>g_close_range_lock_flag [0x0F80]</c> — armed around the close-range kill's VM step.</summary>
    public const int CloseRangeFlagDgroupOffset = 0x0F80;

    /// <summary><c>[0xC32F]</c> — non-zero blocks player damage entirely (<c>image@0x0BE93</c>).</summary>
    public const int PlayerDamageSuppressDgroupOffset = 0xC32F;

    /// <summary>The victim reference that means "the shot missed" (<c>image@0x0BD88</c>).</summary>
    public const ushort NearMissRef = 0xFFFF;

    private static void Bail(ProjectileKernelContext context, NoDamageReason reason)
    {
        context.Census.NoDamage++;
        context.Census.NoDamageReasons[(int)reason]++;
    }

    /// <summary>What the body decided before it reached the shared tail.</summary>
    private enum DamageOutcome
    {
        /// <summary>Straight to the epilogue — the original's <c>jmp 0xC097</c>.</summary>
        Epilogue,

        /// <summary>Through the hit-point write-back and the coalition call — <c>jmp 0xC07C</c>.</summary>
        WriteBackHitPoints,
    }

    /// <summary>The mutable locals the body threads through its arms.</summary>
    private struct Locals
    {
        /// <summary><c>[bp-0x14]</c> — the class's bit0, forced to 1 on a kill.</summary>
        public bool LethalClass;

        /// <summary><c>[bp-2]</c> — the player took damage, so the impact sound is suppressed.</summary>
        public bool PlayerWasDamaged;

        /// <summary><c>[bp-4]</c> — <c>engagement_kill_finalize</c> ran.</summary>
        public bool KillFinalized;

        /// <summary><c>[bp-0x0C]</c> — the victim's new hit points.</summary>
        public byte HitPoints;
    }

    /// <summary>Resolves one impact.</summary>
    /// <param name="context">The kernel context.</param>
    /// <param name="record">The spawn record — <c>[bp+0x0E]</c>.</param>
    /// <param name="victimRef">The struck object, 0 = none, <c>0xFFFF</c> = a near miss.</param>
    /// <param name="impact">The six-word impact position the caller supplies — <c>[bp+0x06]</c>.</param>
    /// <param name="firingThisFrame">The AL flag — <c>[bp+0x04]</c>.</param>
    public static void Resolve(
        ProjectileKernelContext context,
        SpawnRecordRef record,
        ushort victimRef,
        CombatPosition impact,
        bool firingThisFrame)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Census.FireHandlerCalls++;
        context.Observer?.FireResolved(record, victimRef, firingThisFrame);

        WeaponClassView weapon = new WeaponClassView(context.StaticData, record.WeaponClassRef);
        Locals locals = new Locals { LethalClass = (weapon.ClassFlags & 0x01) != 0 };   // image@0x0BD63

        DamageOutcome outcome = Body(context, record, victimRef, impact, firingThisFrame, weapon, ref locals);

        if (outcome == DamageOutcome.WriteBackHitPoints)
        {
            // image@0x0C07C..0x0C095 — store the victim's new hit points, then let the
            // coalition/contact reaction see the hit.
            CombatObjectView victim = new CombatObjectView(context.Arena, victimRef);
            EngagementBlockView block = new EngagementBlockView(context.Arena, victim.EngagementBlockRef);
            block.HitPoints = locals.HitPoints;
            context.Census.CoalitionSpawnCalls++;
            context.Lifecycle.PlayerContactCoalitionSpawn(
                context.Registers, context.Arena, victimRef, record.OwnerId);
        }

        // image@0x0C097 — EVERY path retires the shot and schedules the impact effect.
        CombatSpawnDepart.Depart(context, record);
        context.Events.ScheduleDeferredEffect(
            impact,
            victimRef,
            locals.LethalClass ? (byte)1 : (byte)0,
            locals.KillFinalized ? (byte)1 : (byte)0);

        if (!locals.PlayerWasDamaged)                           // image@0x0C0C7
        {
            context.Events.PlayImpactSound(impact, locals.LethalClass ? (byte)1 : (byte)0);
        }
    }

    private static DamageOutcome Body(
        ProjectileKernelContext context,
        SpawnRecordRef record,
        ushort victimRef,
        CombatPosition impact,
        bool firingThisFrame,
        WeaponClassView weapon,
        ref Locals locals)
    {
        CombatRegisters registers = context.Registers;
        PoolArena arena = context.Arena;
        bool attackerIsPlayer = record.OwnerId == registers.PlayerObjectRef;   // image@0x0BD7A

        if (victimRef == NearMissRef)                           // image@0x0BD88
        {
            context.Census.NearMiss++;
            if (locals.LethalClass)                             // image@0x0BD8E
            {
                context.Events.NearMissEffect(impact.Z, impact.X);   // image@0x0BD97..0x0BDA6
            }

            return DamageOutcome.Epilogue;
        }

        if (victimRef == 0)                                     // image@0x0BDB1
        {
            Bail(context, NoDamageReason.NoVictim);
            return DamageOutcome.Epilogue;
        }

        CombatObjectView victim = new CombatObjectView(arena, victimRef);
        if (!victim.CarriesEngagement)                          // image@0x0BDBA test es:[bx+3],8
        {
            Bail(context, NoDamageReason.NoEngagementBlock);
            return DamageOutcome.Epilogue;
        }

        EngagementBlockView block = new EngagementBlockView(arena, victim.EngagementBlockRef);   // image@0x0BDC1
        ushort prototypeRef = block.PrototypeRef;               // image@0x0BDD0
        if (context.StaticData.Byte(prototypeRef + 0x09) == 0xFF)   // image@0x0BDD8
        {
            Bail(context, NoDamageReason.PrototypeSentinel);
            return DamageOutcome.Epilogue;
        }

        // image@0x0BDE1..0x0BE0B — accuracy bookkeeping, PLAYER shots only, split by weapon class.
        if (attackerIsPlayer)
        {
            int counter = (weapon.ClassFlags & 0x10) != 0
                ? MissileRoundsDgroupOffset                     // image@0x0BDF7
                : GunRoundsDgroupOffset;                        // image@0x0BE07
            registers.SetWord(
                counter, unchecked((ushort)(registers.Word(counter) + weapon.AmmoPerShot)));
        }

        // image@0x0BE0B..0x0BE45 — a PLAYER-fired guided shot rolls 0..0xFF, everything else
        // 0..0x7F; the roll is biased by +0xC0 and scaled by the class's +0x22 through
        // muldiv16_signed_shr8.
        int bound = attackerIsPlayer && (weapon.ClassFlags & 0x10) != 0 ? 0x0100 : 0x0080;
        short roll = unchecked((short)context.Random.RandBounded(bound));
        short damage = Fixed.MulDiv16SignedShr8(
            weapon.DamageScale, unchecked((short)(roll + 0x00C0)));

        // image@0x0BE48..0x0BE68 — the victim's armour, halved on a 1-in-16 draw.
        short armour = context.StaticData.Byte(prototypeRef + 0x0A);
        if (context.Random.Rand8() < 0x10)                      // image@0x0BE53 cmp ax,0x10 / jge
        {
            armour = unchecked((short)(armour >> 1));
        }

        damage = unchecked((short)(damage - armour));
        if (damage <= 0)                                        // image@0x0BE6B cmp / jg
        {
            Bail(context, NoDamageReason.ArmourAbsorbed);
            return DamageOutcome.Epilogue;
        }

        damage = unchecked((short)(weapon.DamageMultiplier * damage));   // image@0x0BE74..0x0BE81

        if (victimRef == registers.PlayerObjectRef)             // image@0x0BE84
        {
            // image@0x0BE8D..0x0BE98 — a shot not firing this frame, or a suppressed player, is
            // silently dropped.
            if (!firingThisFrame || registers.Byte(PlayerDamageSuppressDgroupOffset) != 0)
            {
                Bail(context, NoDamageReason.PlayerNotHittable);
                return DamageOutcome.Epilogue;
            }

            context.Census.PlayerHits++;
            context.Census.PlayerDamageCalls++;
            context.PlayerDamage.Apply(registers, damage);      // image@0x0BEA0
            locals.PlayerWasDamaged = true;                     // image@0x0BEA6
            context.Events.PlayerHitFeedback(damage, locals.LethalClass ? (byte)1 : (byte)0);
            return DamageOutcome.Epilogue;
        }

        // image@0x0BEDF..0x0BF01 — a countermeasure cloud absorbs the shot instead.
        context.Census.CountermeasureLookups++;
        if (context.Lifecycle.TryDestructionSlotShortCircuit(
                victimRef, locals.LethalClass ? (byte)1 : (byte)0))
        {
            context.Census.CountermeasureHits++;
            locals.HitPoints = 0;                               // image@0x0BEFC
            return DamageOutcome.WriteBackHitPoints;
        }

        // image@0x0BF04..0x0BF14 — every hit on a live contact starts an engagement (VM mode 4),
        // whether or not damage follows.
        registers.SetWord(NewEngagementOwnerDgroupOffset, record.OwnerId);
        context.Census.VmAdvances++;
        context.Vm.Advance(registers, arena, block.Offset, 4);

        // image@0x0BF19..0x0BF32 — an AI shot on a class whose prototype lacks bit3 never gets to
        // deal damage, whatever the caller said.
        if (!attackerIsPlayer && (context.StaticData.Byte(prototypeRef + 0x0C) & 0x08) == 0)
        {
            firingThisFrame = false;
        }

        if (!firingThisFrame)
        {
            Bail(context, NoDamageReason.NotFiringThisFrame);
            return DamageOutcome.Epilogue;
        }

        if (block.HitPoints == 0)                               // image@0x0BF38
        {
            Bail(context, NoDamageReason.AlreadyDead);
            return DamageOutcome.Epilogue;
        }

        short remaining = unchecked((short)(block.HitPoints - damage));   // image@0x0BF42..0x0BF4B

        if (remaining > 0)
        {
            return SurvivedHit(context, record, victimRef, block, prototypeRef, remaining, ref locals);
        }

        return Killed(
            context, record, victimRef, victim, block, prototypeRef, weapon, attackerIsPlayer, ref locals);
    }

    /// <summary>image@0x0BF52..0x0BFB3 — the victim survived: maybe start smoke, then advise.</summary>
    private static DamageOutcome SurvivedHit(
        ProjectileKernelContext context,
        SpawnRecordRef record,
        ushort victimRef,
        EngagementBlockView block,
        ushort prototypeRef,
        short remaining,
        ref Locals locals)
    {
        context.Census.EnemyHits++;

        // Dropping below HALF the prototype's initial hit points starts damage smoke, once — the
        // block's +0x06 bit3 latches it (image@0x0BF55..0x0BF9D).
        byte half = unchecked((byte)(context.StaticData.Byte(prototypeRef + 0x09) >> 1));
        if (remaining < half && (block.LoadState & 0x08) == 0)
        {
            context.Census.SmokeStarts++;
            byte variant = (context.Random.Rand8() & 1) == 1 ? (byte)1 : (byte)2;   // image@0x0BF67
            context.Events.SpawnDamageSmoke(block.OwnerObjectRef, variant);         // image@0x0BF92
            block.LoadState = unchecked((byte)(block.LoadState | 0x08));            // image@0x0BF9A
        }

        context.Events.AdvisoryText(record.OwnerId, victimRef);   // image@0x0BFAB
        locals.HitPoints = unchecked((byte)remaining);
        return DamageOutcome.WriteBackHitPoints;
    }

    /// <summary>image@0x0BFB6..0x0C07A — the KILL path.</summary>
    private static DamageOutcome Killed(
        ProjectileKernelContext context,
        SpawnRecordRef record,
        ushort victimRef,
        CombatObjectView victim,
        EngagementBlockView block,
        ushort prototypeRef,
        WeaponClassView weapon,
        bool attackerIsPlayer,
        ref Locals locals)
    {
        CombatRegisters registers = context.Registers;
        PoolArena arena = context.Arena;
        context.Census.Kills++;

        if (attackerIsPlayer)                                   // image@0x0BFB6
        {
            // image@0x0BFBC..0x0BFCA — AL = 1 when the victim block's flags bit6 is CLEAR.
            context.Events.AdvisorMessage((block.Flags & 0x40) != 0 ? (byte)0 : (byte)1);
        }

        context.Events.RadioKillCall(victimRef);                 // image@0x0BFD5
        locals.LethalClass = true;                               // image@0x0BFDC
        locals.HitPoints = 0;                                    // image@0x0BFE0

        // image@0x0BFE5..0x0C006 — only the player's kills tally, and only for prototypes whose
        // +0x0C has neither of bits 0/1.
        if (attackerIsPlayer && (context.StaticData.Byte(prototypeRef + 0x0C) & 0x03) == 0)
        {
            context.Census.PlayerKillsTallied++;
            int tally = (block.Flags & 0x40) != 0
                ? KillTallyBDgroupOffset                         // image@0x0BFFE
                : KillTallyADgroupOffset;                        // image@0x0C004
            registers.SetWord(tally, unchecked((ushort)(registers.Word(tally) + 1)));
        }

        context.Census.MissionHookCalls++;
        context.Lifecycle.OnSlotDestroyed(registers, arena, victimRef);   // image@0x0C00B

        // image@0x0C011..0x0C039 — the CLOSE-RANGE arm: a prototype with bit3 set and neither of
        // bits 0/1, whose victim is still above its own class record's ground clearance.  The
        // altitude test is a signed 32-bit compare of (0:clearance) against the object's +0x0A.
        byte prototypeFlags = context.StaticData.Byte(prototypeRef + 0x0C);
        if ((prototypeFlags & 0x08) != 0 && (prototypeFlags & 0x03) == 0)
        {
            int clearance = context.StaticData.Word(victim.ClassRef + 0x2C);
            if (clearance < victim.Position.Y)                   // image@0x0C02D
            {
                context.Census.CloseRangeKills++;

                if ((weapon.ClassFlags & 0x01) != 0)             // image@0x0C040
                {
                    if (context.Random.Rand8() < 0x50)           // image@0x0C046..0x0C04E
                    {
                        registers.SetByte(CloseRangeFlagDgroupOffset, 1);
                    }
                }

                context.Census.VmAdvances++;
                context.Vm.Advance(registers, arena, block.Offset, 5);   // image@0x0C05C
                registers.SetByte(CloseRangeFlagDgroupOffset, 0);        // image@0x0C063
                locals.KillFinalized = false;                            // image@0x0C066
                return DamageOutcome.WriteBackHitPoints;
            }
        }

        CombatSpawnDepart.KillFinalize(context, victimRef);      // image@0x0C071
        locals.KillFinalized = true;                             // image@0x0C078
        return DamageOutcome.WriteBackHitPoints;
    }
}
