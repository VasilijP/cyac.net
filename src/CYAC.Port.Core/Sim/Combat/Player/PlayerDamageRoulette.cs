using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Sim.Combat.Player;

/// <summary>
/// <c>weapon_fire_combat_loop @image@0x0F748</c> — P16 in the trace: the PLAYER-DAMAGE ROULETTE. One
/// call per hit that lands on the player, from the fire resolver's sole door (<c>image@0x0BEA0</c>).
/// </summary>
/// <remarks>
/// <para>
/// The name is an inherited misnomer — there is no loop over weapons here.  The body is
/// (a) three suppression gates, (b) a FATAL roll, (c) two survivability gates, (d) a
/// WEIGHTED roulette over the 25-entry effect table at <c>[0xBD0E]</c>, (e) a linear rescue scan,
/// and (f) a 24-arm jump table at <c>image@0x0FBD5</c> whose arms post cockpit text and apply the
/// damage.
/// </para>
/// <para>
/// <b>The effect-selection order vs the port's <c>PlayerDamageTable</c> model.</b>  The original
/// picks an ENTRY, not an effect id: the arm number is <c>(cursor −
/// [0xBD0E]) &gt;&gt; 1</c> (<c>image@0x0F883</c>), i.e. the entry's INDEX in the table, and the
/// table is walked with a stride of 2 — <c>+0</c> is the entry's WEIGHT and <c>+1</c> its GATE byte
/// (<c>target_hit_check @image@0x0FC09</c> reads <c>[si+1]</c> three ways: <c>&amp; 3</c> = how many
/// times this effect may ever fire, bit6 = "needs damage ≥ 10", bit7 = "needs the accumulated damage
/// to have reached the ceiling").  So the arm index IS the table index — there is no indirection and
/// no separate effect id, and any model that stores an (effect, weight) pair keyed by effect id must
/// preserve the table ORDER or the roulette diverges. The 24 arms are <see cref="ArmCount"/>; the
/// table has 25 entries (<c>0x32</c> bytes, <c>image@0x0F864</c>), so entry 24 can be SELECTED by
/// the linear scan and then falls out of the dispatch's <c>cmp ax,0x17 / ja</c> guard
/// (<c>image@0x0FBC8</c>) — a shipped no-op slot, counted as
/// <see cref="PlayerCombatCensus.RouletteNoEffect"/>.
/// </para>
/// <para>
/// <b>The master half is K9's.</b>  Every arm that changes the aeroplane goes through
/// <see cref="IAircraftDamageChannel"/>; <c>Sim/Flight/AircraftDamage</c> already reproduces those
/// writes and is consumed, never re-ported.  What another part of the port owns and verifies is the
/// DGROUP half: <c>[0xF1CC]</c>, <c>[0xF1E0+n]</c>, <c>[0xBD00]</c>, <c>[0xBD02]</c>,
/// <c>[0xBD06]</c>, <c>[0xBD12]</c>, <c>[0xF1DA..0xF1DC]</c>, <c>[0xF17E]</c>, <c>[0xF084]</c>,
/// <c>[0xF1CE]</c>, <c>[0xED2C]</c>, <c>[0xED24]</c>, <c>[0xED32]</c>, <c>[0xED33]</c>, and the RNG.
/// </para>
/// </remarks>
public static class PlayerDamageRoulette
{
    /// <summary>How many arms the jump table at <c>image@0x0FBD5</c> has.</summary>
    public const int ArmCount = 24;

    /// <summary>The effect table's stride — <c>add word ptr [bp-4],2</c>.</summary>
    public const int EffectEntryBytes = 2;

    /// <summary>The effect table's byte length — <c>add ax,0x32</c> @<c>image@0x0F864</c>.</summary>
    public const int EffectTableBytes = 0x32;

    /// <summary>How many weighted rolls the roulette makes before falling back — <c>cmp word ptr [bp-6],0xa</c>.</summary>
    public const int WeightedRolls = 10;

    /// <summary>How many entries the linear rescue scan visits — <c>cmp word ptr [bp-6],0x19</c>.</summary>
    public const int LinearScanEntries = 25;

    /// <summary>
    /// Applies one hit to the player.
    /// </summary>
    /// <param name="context">The player-side context.</param>
    /// <param name="damage">The resolver's computed damage word — the original's <c>[bp+6]</c>.</param>
    public static void Apply(PlayerCombatContext context, short damage)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters registers = context.Registers;
        PlayerCombatCensus census = context.Census;
        census.RouletteCalls++;

        // image@0x0F74E..0x0F763 — invincibility, the load acknowledgement and the input mode.
        if (registers.Byte(PlayerCombatOffsets.CheatInvincible) != 0
            || registers.Byte(PlayerCombatOffsets.AircraftLoadAck) != 0
            || registers.Byte(PlayerCombatOffsets.InputMode) != 0)
        {
            census.RouletteSuppressed++;
            return;
        }

        short ceiling = unchecked((short)registers.Word(PlayerCombatOffsets.DamageCeiling));

        // image@0x0F766..0x0F76C — the accumulator takes the hit first, whatever happens next.
        short accumulated = unchecked(
            (short)(registers.Word(PlayerCombatOffsets.PlayerDamageAccum) + damage));
        registers.SetWord(PlayerCombatOffsets.PlayerDamageAccum, unchecked((ushort)accumulated));

        // image@0x0F770..0x0F799 — the FATAL roll: past the ceiling, a big single hit that also
        // passes twice the ceiling raises the kill chance from 10/256 to 128/256.
        if (accumulated > ceiling)
        {
            short threshold = 0x0A;
            if (damage > 0x50 && accumulated > unchecked((short)(ceiling << 1)))
            {
                threshold = 0x80;
            }

            if (context.Random.Rand8() < threshold)
            {
                census.RouletteFatal++;
                Dispatch(context, 23, damage);
                return;
            }
        }

        // image@0x0F79C..0x0F7B6 — the difficulty gate: ceiling × hitProbability[difficulty] >> 8
        // must not exceed the accumulated damage.
        byte difficulty = registers.Byte(PlayerCombatOffsets.DifficultyIndex);
        short probability =
            context.StaticData.Byte(PlayerCombatOffsets.HitProbabilityTable + difficulty);
        if (Fixed.MulDiv16SignedShr8(ceiling, probability) > accumulated)
        {
            census.RouletteNoEffect++;
            return;
        }

        // image@0x0F7B9..0x0F7F7 — the effect chance: min(accum·50/ceiling, 25) + damage/8, capped
        // at 50, rolled against prng_rand_bounded(100).
        short chance = Fixed.MulDiv16Signed(accumulated, 0x32, ceiling);
        if (chance > 0x19)
        {
            chance = 0x19;
        }

        chance = unchecked((short)(chance + (damage >> 3)));
        if (chance > 0x32)
        {
            chance = 0x32;
        }

        if (context.Random.RandBounded(0x64) > chance)
        {
            census.RouletteNoEffect++;
            return;
        }

        // image@0x0F7FA..0x0F801 — the damage the ARMS see is clamped, in the caller's own frame.
        if (damage > 0xFF)
        {
            damage = 0xFF;
        }

        ushort tableBase = registers.Word(PlayerCombatOffsets.EffectWeightTablePtr);
        int cursor = tableBase;

        // image@0x0F806..0x0F84C — up to ten weighted rolls.  Each roll walks the table adding
        // weights until the running total EXCEEDS the roll, then offers that entry to the gate.
        for (int roll = 0; roll < WeightedRolls; roll++)
        {
            int target = context.Random.RandBounded(0x64);
            cursor = tableBase;
            int running = 0;
            while (true)
            {
                running = unchecked((short)(running + context.StaticData.Byte(cursor)));
                if (running > target)
                {
                    break;
                }

                cursor += EffectEntryBytes;
            }

            if (HitCheck(context, cursor, damage))
            {
                census.RouletteWeightedHits++;
                Dispatch(context, ArmIndex(cursor, tableBase), damage);
                return;
            }
        }

        // image@0x0F84E..0x0F881 — the rescue scan: 25 entries from wherever the last roll stopped,
        // wrapping at tableBase + 0x32, offered with a fixed damage of 10.
        census.RouletteLinearScans++;
        for (int step = 0; step < LinearScanEntries; step++)
        {
            cursor += EffectEntryBytes;
            if (unchecked((ushort)cursor) >= unchecked((ushort)(tableBase + EffectTableBytes)))
            {
                cursor = tableBase;
            }

            if (HitCheck(context, cursor, 0x0A))
            {
                Dispatch(context, ArmIndex(cursor, tableBase), damage);
                return;
            }
        }

        census.RouletteNoEffect++;
    }

    /// <summary>
    /// <c>target_hit_check @image@0x0FC09</c> — may this table entry fire?
    /// </summary>
    /// <param name="context">The player-side context.</param>
    /// <param name="entryAddress">The original's <c>BX</c> — the entry's DGROUP address.</param>
    /// <param name="damage">The original's <c>AX</c>.</param>
    /// <returns>The original's <c>AL</c>.</returns>
    public static bool HitCheck(PlayerCombatContext context, int entryAddress, short damage)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters registers = context.Registers;

        // image@0x0FC11 — a zero WEIGHT disables the entry outright.  The table
        // g_damage_effect_weight_table_ptr [0xBD0E] points at is CONSTANT DGROUP (0x44CE on the
        // shipped image), not a runtime window, so it is read through ICombatStaticData.
        // <b>Transform ask:</b> those 25 (weight, gate) pairs are the most modder-visible damage
        // tuning in the game and have no home in the data tree yet — they join C2's [0x0DEC] fire
        // bonus and C5's three difficulty tables.
        if (context.StaticData.Byte(entryAddress) == 0)
        {
            return false;
        }

        byte gate = context.StaticData.Byte(entryAddress + 1);

        // image@0x0FC1C..0x0FC2B — the low two bits are the entry's LIFETIME hit budget, counted in
        // g_damage_effect_hit_counts [0xF1E0 + index].  `jae` is UNSIGNED.
        int index = ArmIndex(entryAddress, registers.Word(PlayerCombatOffsets.EffectWeightTablePtr));
        if (registers.Byte(PlayerCombatOffsets.EffectHitCounts + index) >= (gate & 3))
        {
            return false;
        }

        // image@0x0FC2D..0x0FC37 — bit6: this effect needs a hit of at least 10.
        if ((gate & 0x40) != 0 && damage < 0x0A)
        {
            return false;
        }

        // image@0x0FC3B..0x0FC48 — bit7: this effect only unlocks once the accumulated damage has
        // reached the ceiling.
        if ((gate & 0x80) != 0
            && unchecked((short)registers.Word(PlayerCombatOffsets.PlayerDamageAccum))
                < unchecked((short)registers.Word(PlayerCombatOffsets.DamageCeiling)))
        {
            return false;
        }

        return true;
    }

    /// <summary><c>image@0x0F883..0x0F88C</c>: the entry's index — a SIGNED halving of the byte delta.</summary>
    private static int ArmIndex(int entryAddress, ushort tableBase) =>
        unchecked((short)(entryAddress - tableBase)) >> 1;

    /// <summary>
    /// The 24-arm dispatch at <c>image@0x0FBC8</c> plus the shared <c>[0xF1E0+n]</c> bump the
    /// selection performs first (<c>image@0x0F891</c>).
    /// </summary>
    /// <param name="context">The player-side context.</param>
    /// <param name="arm">The selected entry's index.</param>
    /// <param name="damage">The clamped damage word.</param>
    public static void Dispatch(PlayerCombatContext context, int arm, short damage)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters registers = context.Registers;
        PlayerCombatCensus census = context.Census;
        IPlayerCombatEvents events = context.Events;
        ICombatStaticData statics = context.StaticData;

        // image@0x0F88F..0x0F891 — the lifetime counter, bumped BEFORE the guard, so even the
        // out-of-range entry 24 is counted.
        int countAddress = PlayerCombatOffsets.EffectHitCounts + arm;
        registers.SetByte(countAddress, unchecked((byte)(registers.Byte(countAddress) + 1)));

        // image@0x0FBC8..0x0FBCB — the shipped guard: entry 24 has no arm.
        if (arm > 0x17)
        {
            census.RouletteNoEffect++;
            return;
        }

        census.RouletteEffects++;
        census.RouletteArms[arm]++;

        switch (arm)
        {
            case 0:  // image@0x0F898 — "FIRE"
                events.CombatEventNotify(1);
                events.ScheduleWeaponTimer(
                    unchecked((short)(context.Random.RandBounded(5) + 0x0A)));
                break;

            case 1:  // image@0x0F91B — a FUEL LEAK: the drain rate gains twice the tank capacity.
                {
                    short capacity = unchecked((short)registers.Word(PlayerCombatOffsets.FuelCapacity));
                    int gain = unchecked((short)(capacity << 1));
                    int rate = unchecked(
                        (int)(registers.Word(PlayerCombatOffsets.FuelLeakRate)
                            | (registers.Word(PlayerCombatOffsets.FuelLeakRate + 2) << 16)));
                    rate = unchecked(rate + gain);
                    registers.SetWord(PlayerCombatOffsets.FuelLeakRate, unchecked((ushort)rate));
                    registers.SetWord(
                        PlayerCombatOffsets.FuelLeakRate + 2, unchecked((ushort)(rate >> 16)));
                    HitSpark(context, 2);   // image@0x0F929
                    events.ShowCockpitText(0x144);
                }

                break;

            case 2:  // image@0x0F8B0 — a HYDRAULIC/score hit.
                {
                    byte gain = unchecked((byte)(context.Random.RandBounded(2) + 1));
                    registers.SetByte(
                        PlayerCombatOffsets.WeaponScoreAccum,
                        unchecked((byte)(registers.Byte(PlayerCombatOffsets.WeaponScoreAccum) + gain)));
                    HitSpark(context, 1);   // image@0x0F8BE
                    events.ShowCockpitText(0x11A);
                }

                break;

            case 3:  // image@0x0F8E5 — a CANNON hit: `rand8 & mask`, then `mask += that + 1`.
                {
                    byte mask = registers.Byte(PlayerCombatOffsets.CannonHitBitmask);
                    byte gain = unchecked((byte)((context.Random.Rand8() & mask) + 1));
                    registers.SetByte(
                        PlayerCombatOffsets.CannonHitBitmask, unchecked((byte)(mask + gain)));
                    HitSpark(context, 2);   // image@0x0F8F4
                    events.ShowCockpitText(0x12C);
                }

                break;

            case 4:  // image@0x0F950 — the burst deadline is cleared.
                registers.SetWord(PlayerCombatOffsets.BurstDeadlineFrame, 0);
                events.ShowCockpitText(0x156);
                break;

            case 5:  // image@0x0F95F — "ELEVATORS DAMAGED"
                events.ShowCockpitText(0xA5);
                context.Damage.HalveElevatorBounds();
                break;

            case 6:  // image@0x0F977 — "AILERONS DAMAGED"
                events.ShowCockpitText(0xB7);
                context.Damage.HalveAileronAuthority();
                break;

            case 7:  // image@0x0F997 — text only.
                events.ShowCockpitText(0x16C);
                break;

            case 8:  // image@0x0F9A0 — "WING DAMAGED": hull + roll authority, then double the drag.
                events.ShowCockpitText(0xEF);
                context.Damage.Apply(0x40, 0x64);
                context.Damage.Apply(0x02, 0x64);
                context.Damage.DoubleInducedDrag();
                registers.SetWord(
                    PlayerCombatOffsets.HitScoreAccum,
                    unchecked((ushort)(registers.Word(PlayerCombatOffsets.HitScoreAccum) << 1)));
                break;

            case 9:  // image@0x0F9D0 — gated on input bit2.
                if ((registers.Byte(PlayerCombatOffsets.InputStateBits) & 4) == 0)
                {
                    return;
                }

                events.ShowCockpitText(0xD5);
                context.Damage.Apply(0x08, 0);
                break;

            case 10:  // image@0x0F9F7
                events.ShowCockpitText(0xC8);
                context.Damage.Apply(0x10, 0);
                break;

            case 11:  // image@0x0FA0C
                events.ShowCockpitText(0xE1);
                context.Damage.Apply(0x20, 0);
                break;

            case 12:
            case 13:
            case 14:  // image@0x0FA21 — a WEAPON STATION is knocked out.
                WeaponStationDamaged(context, arm - 12);
                break;

            case 15:  // image@0x0FA99 — "ENGINE DAMAGED"
                events.ShowCockpitText(0x00);
                context.Damage.Apply(
                    0x01, unchecked((short)(context.Random.RandBounded(0x14) + 0x0A)));
                break;

            case 16:  // image@0x0FABC — "ENGINE FAILURE", with two message variants under input bit6.
                context.Damage.Apply(0x01, 0x64);
                if ((registers.Byte(PlayerCombatOffsets.InputStateBits) & 0x40) != 0)
                {
                    events.ShowCockpitText(
                        context.Random.RandBounded(1) != 0 ? (ushort)0x28 : (ushort)0x38);
                }
                else
                {
                    events.ShowCockpitText(0x4B);
                }

                break;

            case 17:  // image@0x0FAFE — the engine meter starts climbing.
                events.ShowCockpitText(0x0F);
                registers.SetByte(PlayerCombatOffsets.EngineClimbRate, 4);
                registers.SetByte(PlayerCombatOffsets.BurstStateB, 1);
                break;

            case 18:  // image@0x0FB18 — the engine meter is slammed to 99 and climbs by 1.
                registers.SetByte(PlayerCombatOffsets.MeterEngine, 0x63);
                registers.SetByte(PlayerCombatOffsets.EngineClimbRate, 1);
                registers.SetByte(PlayerCombatOffsets.BurstStateB, 1);
                break;

            case 19:  // image@0x0FB28 — the radar is knocked into its other mode.
                events.ShowCockpitText(0x97);
                events.ToggleRadarMode();
                break;

            case 20:  // image@0x0FB3D — the chaff dispenser is destroyed.
                registers.SetByte(PlayerCombatOffsets.ChaffCount, 0);
                events.ShowCockpitText(0x17E);
                break;

            case 21:  // image@0x0FB4A — the flare dispenser is destroyed.
                registers.SetByte(PlayerCombatOffsets.FlareCount, 0);
                events.ShowCockpitText(0x197);
                break;

            case 22:  // image@0x0FB57 — the PILOT is hit; the death deadline depends on the kill flag.
                {
                    events.EnqueueDamageIndicator();
                    ushort master = registers.Word(PlayerCombatOffsets.MasterFrameCounter);
                    if (registers.Byte(PlayerCombatOffsets.DamageEffectHitCount22) == 1)
                    {
                        events.ShowCockpitText(0x1B0);
                        short minutes =
                            unchecked((short)(context.Random.RandBounded(5) + 0x0A));
                        registers.SetWord(
                            PlayerCombatOffsets.DeathDeadlineFrame,
                            unchecked((ushort)((minutes * 0x3C) + master)));
                    }
                    else
                    {
                        events.ShowCockpitText(0x1C0);
                        registers.SetWord(
                            PlayerCombatOffsets.DeathDeadlineFrame, unchecked((ushort)(master + 0x5A)));
                    }
                }

                break;

            case 23:  // image@0x0FB9E — the FATAL arm, which falls through into arm 17's body.
                events.ShowCockpitText(0x230);
                registers.SetByte(PlayerCombatOffsets.PilotHitFlag, 1);
                events.CombatEventNotify(3);
                events.ScheduleWeaponTimer(
                    unchecked((short)(context.Random.RandBounded(5) + 5)));

                // image@0x0FBC5 `jmp 0xfb10` lands INSIDE arm 17's body, one instruction PAST its
                // `mov byte ptr [0xf1db],4` (image@0x0FB0B) — so the fatal arm sets [0xF1DC] alone.
                registers.SetByte(PlayerCombatOffsets.BurstStateB, 1);
                break;

            default:
                break;
        }

        _ = statics;
        _ = damage;
    }

    /// <summary>
    /// The three visual arms' shared eight-word <c>subsystem4x19_row_attach (ex-projectile_spawn)</c> call
    /// (<c>image@0x0F8BE..0x0F8D7</c> and its two copies) — a hit spark on the player's own object.
    /// </summary>
    private static void HitSpark(PlayerCombatContext context, ushort selector) =>
        context.Events.SpawnTrail(new TrailSpawnRequest(
            Selector: selector,
            Flags: 0,
            Lifetime: 0x14,
            Reserved: 0,
            Kind: 2,
            ObjectRef: context.Registers.Word(PlayerCombatOffsets.PlayerObject),
            ExtraA: 0,
            ExtraB: 0));

    /// <summary>
    /// <c>image@0x0FA21..0x0FA96</c> — arms 12/13/14.  The ammo-halving arm.
    /// </summary>
    /// <remarks>
    /// <b>The brief's third call-out — does the halving's rounding match
    /// <c>WeaponClass.AmmoPerShot</c> semantics?</b> Yes, and deliberately: the arm computes
    /// <c>half = (table − sign(table)) &gt;&gt; 1</c> (a round-TOWARD-ZERO halving, the MSC
    /// <c>x/2</c> idiom at <c>image@0x0FA5F..0x0FA62</c>), then <c>(half / perShot) · perShot</c>
    /// with a TRUNCATING <c>idiv</c> (<c>image@0x0FA6E..0x0FA70</c>) — i.e. it rounds the loss
    /// DOWN to a whole number of <c>AmmoPerShot</c> bursts, exactly the granularity
    /// <c>weapon_fire_check_and_spawn</c> decrements in (<c>image@0x034F4</c>).  So the surviving
    /// ammo is always a multiple of the burst size and the player never ends up with a partial
    /// burst that cannot be fired.  The subtraction is clamped at zero (<c>jns</c>
    /// @<c>image@0x0FA76</c>).
    /// </remarks>
    private static void WeaponStationDamaged(PlayerCombatContext context, int station)
    {
        CombatRegisters registers = context.Registers;
        IPlayerCombatEvents events = context.Events;

        // image@0x0FA2A..0x0FA46 — the message depends on the STATION's own class, read from the
        // per-station class-pointer array at [0xED24].
        ushort stationClass = registers.Word(PlayerCombatOffsets.RouletteWeaponClass + (station * 2));
        events.ShowCockpitText(
            (context.StaticData.Byte(stationClass + 0x24) & WeaponFireScheduler.GuidedBit) != 0
                ? (ushort)0x109
                : (ushort)0xFC);

        if (registers.Word(PlayerCombatOffsets.RouletteAmmoMode) == 1)
        {
            // image@0x0FA52..0x0FA7E — HALVE the FIRST station's ammo, rounded down to a whole
            // number of bursts.  The arm reads [0xED24]/[0xED2C] (station 0), NOT the damaged
            // station's own entry — a shipped asymmetry, kept.
            int tableIndex = unchecked((ushort)(0x12 * registers.Word(0xC31A)));
            short capacity = unchecked((short)context.StaticData.Word(0x4404 + tableIndex));
            short half = unchecked((short)((capacity - (capacity < 0 ? -1 : 0)) >> 1));
            byte perShot =
                context.StaticData.Byte(registers.Word(PlayerCombatOffsets.RouletteWeaponClass) + 0x2C);
            short loss = unchecked((short)(Fixed.IDiv16(half, perShot).Quotient * perShot));
            short remaining =
                unchecked((short)(registers.Word(PlayerCombatOffsets.WeaponSlotAmmo) - loss));
            registers.SetWord(
                PlayerCombatOffsets.WeaponSlotAmmo, unchecked((ushort)(remaining < 0 ? 0 : remaining)));
        }
        else
        {
            // image@0x0FA80..0x0FA8B — the station is wiped: no class, no ammo.
            registers.SetWord(PlayerCombatOffsets.RouletteWeaponClass + (station * 2), 0);
            registers.SetWord(PlayerCombatOffsets.WeaponSlotAmmo + (station * 2), 0);
        }

        events.RefreshWeaponSlotName();
    }
}
