using CYAC.Port.Core.Primitives;

namespace CYAC.Port.Core.Sim.Combat.Player;

/// <summary>
/// <c>engagement_per_frame_tick @image@0x0FC51</c> — frame-ladder row 9, the CS6 → CS7 stage: the
/// PLAYER's per-frame SUSTAIN tick.
/// </summary>
/// <remarks>
/// <para>
/// One call per frame from <c>mission_state_machine @image@0x01095</c>, between the engagement
/// expiry loop and the admitter — so between CS6 and CS7 the frame body makes exactly ONE call, and
/// the whole combat register file is a comparable surface (the shape C5 used for CS7 → CS8).
/// </para>
/// <para>
/// <b>The odd-frame gate.</b>  Everything above <c>image@0x0FEEF</c> runs only when the master frame
/// counter has CHANGED since the last call and is ODD
/// (<c>cmp si,[0xbd08] / je … / test si,1 / je …</c> @<c>image@0x0FC6E..0x0FC78</c>), i.e. at half
/// the frame rate.  The burst-mode shake and the tail below it run every frame.
/// </para>
/// <para>
/// <b>Report-only brief correction.</b> The C6 brief says the tick does a "boundary clamp via
/// <c>world_extents_pos_clamp @image@0x0B8F5</c>".  It does not — a recursive-descent disassembly of
/// <c>[0x0FC51, 0x0FFCA)</c> finds no call to <c>0x0B8F5</c> at all.  What is there is
/// <see cref="DegradeControlAuthority"/>: the four control-authority bounds
/// <c>[0xC30C]/[0xC310]/[0xC314]/[0xC318]</c> are rewritten every odd frame as <c>meter · bound /
/// 100</c> from the ORIGINALS at <c>[0xE47C..0xE485]</c> (<c>image@0x0FDC8..0x0FE11</c>), so
/// airframe damage shrinks the stick's usable travel.
/// </para>
/// </remarks>
public static class PlayerSustainTick
{
    /// <summary>The engine meter's saturation value — <c>cmp byte ptr [0xf1da],0x64</c>.</summary>
    public const byte MeterMax = 0x64;

    /// <summary>The engine meter's warning threshold — <c>cmp byte ptr [0xf1da],0x32</c>.</summary>
    public const byte MeterWarning = 0x32;

    /// <summary>The grace the death path grants — <c>add ax,4</c> @<c>image@0x0FE54</c>.</summary>
    public const int DeathGraceFrames = 4;

    /// <summary>How far ahead of the death deadline the cooldown warning fires — <c>add ax,0x3c</c>.</summary>
    public const int CooldownWarningLead = 0x3C;

    /// <summary>Runs one call of the sustain tick.</summary>
    /// <param name="context">The player-side context.</param>
    public static void Step(PlayerCombatContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CombatRegisters registers = context.Registers;
        PlayerCombatCensus census = context.Census;
        census.SustainCalls++;

        // image@0x0FC59..0x0FC67
        if (registers.Byte(PlayerCombatOffsets.InputMode) != 0
            || registers.Byte(PlayerCombatOffsets.AircraftLoadAck) != 0)
        {
            census.SustainSuppressed++;
            return;
        }

        // image@0x0FC6A..0x0FC84
        ushort frame = registers.Word(PlayerCombatOffsets.MasterFrameCounter);
        bool oddFrame =
            frame != registers.Word(PlayerCombatOffsets.SustainLastFrame) && (frame & 1) != 0;
        registers.SetWord(PlayerCombatOffsets.SustainLastFrame, frame);

        if (oddFrame)
        {
            census.SustainOddFrames++;
            OddFrameBody(context, frame);
        }

        BurstModeShake(context, frame);
        Tail(context);
    }

    /// <summary><c>image@0x0FC91..0x0FEEE</c> — the half-rate body.</summary>
    private static void OddFrameBody(PlayerCombatContext context, ushort frame)
    {
        CombatRegisters registers = context.Registers;
        PlayerCombatCensus census = context.Census;
        IPlayerCombatEvents events = context.Events;

        // image@0x0FC91..0x0FCB6 — the fuel leak.  [0xF1CE] is an i32 LEAK RATE, not a score (the
        // P62 scanner name g_combat_score_lo was a misnomer; an earlier pass proved the leak rate and the
        // scanner now carries g_fuel_leak_rate_lo — RENAMED R1).
        if (registers.Byte(PlayerCombatOffsets.FuelLeakFlag) != 0)
        {
            census.SustainFuelDrains++;
            int rate = ReadInt32(registers, PlayerCombatOffsets.FuelLeakRate);
            int fuel = unchecked(ReadInt32(registers, PlayerCombatOffsets.Fuel) - rate);
            if (unchecked((short)(fuel >> 16)) < 0)
            {
                fuel = 0;
            }

            WriteInt32(registers, PlayerCombatOffsets.Fuel, fuel);
        }

        // image@0x0FCB6..0x0FCD8 — the dry-tank message, once.
        if (ReadInt32(registers, PlayerCombatOffsets.Fuel) == 0
            && registers.Byte(PlayerCombatOffsets.OutOfFuelShown) == 0)
        {
            census.SustainOutOfFuel++;
            registers.SetByte(PlayerCombatOffsets.OutOfFuelShown, 1);
            events.ShowCockpitText(0x1DF);
        }

        // image@0x0FCD8..0x0FD04 — the second meter decays by the score accumulator.
        if (unchecked((sbyte)registers.Byte(PlayerCombatOffsets.MeterB)) > 0)
        {
            census.SustainMeterBDecays++;
            sbyte remaining = unchecked((sbyte)(
                registers.Byte(PlayerCombatOffsets.MeterB)
                - registers.Byte(PlayerCombatOffsets.WeaponScoreAccum)));
            registers.SetByte(PlayerCombatOffsets.MeterB, unchecked((byte)remaining));
            if (remaining <= 0)
            {
                registers.SetByte(PlayerCombatOffsets.MeterB, 0);
                registers.SetByte(PlayerCombatOffsets.EngineClimbRate, 4);
                events.ShowCockpitText(0x1EB);
            }
        }

        // image@0x0FD04..0x0FD45 — the AIRFRAME meter decays by the cannon-hit mask; hitting zero
        // restores three damage classes at once.
        if (unchecked((sbyte)registers.Byte(PlayerCombatOffsets.MeterAirframe)) > 0)
        {
            sbyte remaining = unchecked((sbyte)(
                registers.Byte(PlayerCombatOffsets.MeterAirframe)
                - registers.Byte(PlayerCombatOffsets.CannonHitBitmask)));
            registers.SetByte(PlayerCombatOffsets.MeterAirframe, unchecked((byte)remaining));
            if (remaining <= 0)
            {
                census.SustainAirframeRepairs++;
                registers.SetByte(PlayerCombatOffsets.MeterAirframe, 0);
                context.Damage.Apply(0x10, 0);
                context.Damage.Apply(0x08, 0);
                context.Damage.Apply(0x20, 0);
            }
        }

        // image@0x0FD45..0x0FDB9 — the ENGINE meter.
        if (unchecked((sbyte)registers.Byte(PlayerCombatOffsets.MeterEngine)) < MeterMax)
        {
            census.SustainEngineSteps++;
            sbyte before = unchecked((sbyte)registers.Byte(PlayerCombatOffsets.MeterEngine));

            if (unchecked((short)registers.Word(PlayerCombatOffsets.ThrottlePercent)) > 0)
            {
                registers.SetByte(
                    PlayerCombatOffsets.MeterEngine,
                    unchecked((byte)(before + registers.Byte(PlayerCombatOffsets.EngineClimbRate))));
            }
            else
            {
                registers.SetByte(PlayerCombatOffsets.MeterEngine, unchecked((byte)(before - 1)));
            }

            if (unchecked((sbyte)registers.Byte(PlayerCombatOffsets.MeterEngine)) < 0)
            {
                registers.SetByte(PlayerCombatOffsets.MeterEngine, 0);
            }

            sbyte after = unchecked((sbyte)registers.Byte(PlayerCombatOffsets.MeterEngine));
            if (before < MeterWarning && after >= MeterWarning)
            {
                events.ShowCockpitText(0x1F6);
            }

            if (after >= MeterMax)
            {
                census.SustainEngineFailures++;
                registers.SetByte(PlayerCombatOffsets.MeterEngine, MeterMax);
                events.CombatEventNotify(2);
                context.Damage.Apply(0x01, 0x64);
                events.ScheduleWeaponTimer(
                    unchecked((short)(context.Random.RandBounded(0x0A) + 0x0A)));
            }
        }

        DegradeControlAuthority(context);

        // image@0x0FE14..0x0FE28 — the mission-abort deadline.
        if (unchecked((ushort)registers.Word(PlayerCombatOffsets.AbortDeadlineFrame)) <= frame)
        {
            census.SustainAbortArms++;
            events.ArmDestroyedFlagDeadline();
            events.SceneReset(0);
        }

        // image@0x0FE29..0x0FE3B — three frames before it, an advisory.
        if (unchecked((ushort)(registers.Word(PlayerCombatOffsets.AbortDeadlineFrame) - 3)) <= frame)
        {
            events.AdvisorMessage(2);
        }

        // image@0x0FE3C..0x0FE7D — the DEATH deadline, or its 60-frame warning.
        if (unchecked((ushort)registers.Word(PlayerCombatOffsets.DeathDeadlineFrame)) <= frame)
        {
            census.SustainDeaths++;
            registers.SetByte(PlayerCombatOffsets.ObjectDestroyedFlag, 1);
            events.SceneReset(2);
            registers.SetWord(0xC390, unchecked((ushort)(frame + DeathGraceFrames)));
        }
        else if (unchecked((ushort)(frame + CooldownWarningLead))
            >= registers.Word(PlayerCombatOffsets.DeathDeadlineFrame)
            && registers.Byte(PlayerCombatOffsets.CooldownWarningShown) == 0)
        {
            census.SustainCooldownWarnings++;
            events.ShowCockpitText(0x209);
            registers.SetByte(PlayerCombatOffsets.CooldownWarningShown, 1);
        }

        // image@0x0FE7E..0x0FEC8 — the 0/1/2 hit-event ladder.
        byte severity = registers.Byte(PlayerCombatOffsets.HitEventSeverity);
        if (severity == 1)
        {
            census.SustainHitEvents++;
            registers.SetWord(
                PlayerCombatOffsets.PlayerDamageAccum,
                unchecked((ushort)(registers.Word(PlayerCombatOffsets.PlayerDamageAccum) + 5)));

            byte roll = context.Random.Rand8();
            short scaled = Fixed.MulDiv16Signed(
                0x0A,
                unchecked((short)registers.Word(0xEF99)),
                unchecked((short)registers.Word(0xEFA0)));
            if (unchecked((short)(scaled + 5)) > roll)
            {
                events.ShowCockpitText(0x7B);
                events.CombatEventNotify(0);
            }
        }
        else if (severity == 2)
        {
            census.SustainHitEvents++;
            registers.SetWord(
                PlayerCombatOffsets.PlayerDamageAccum,
                unchecked((ushort)(registers.Word(PlayerCombatOffsets.PlayerDamageAccum) + 5)));
        }

        // image@0x0FEC9..0x0FEEE — the G-induced blackout roll.
        if (registers.Byte(PlayerCombatOffsets.BlackoutEnabled) != 0)
        {
            short gLoad = unchecked((short)registers.Word(PlayerCombatOffsets.PlayerGLoad));
            if (gLoad >= 6 || gLoad <= -4)
            {
                census.SustainBlackouts++;
                if (context.Random.Rand8() < 3)
                {
                    events.SceneReset(1);
                }
            }
        }
    }

    /// <summary>
    /// <c>image@0x0FDBA..0x0FE13</c> — the four control-authority bounds, rewritten from their
    /// originals scaled by the airframe meter.
    /// </summary>
    private static void DegradeControlAuthority(PlayerCombatContext context)
    {
        CombatRegisters registers = context.Registers;
        if (unchecked((sbyte)registers.Byte(PlayerCombatOffsets.MeterAirframe)) >= MeterMax
            || (registers.Byte(0xEF25) & 8) == 0)
        {
            return;
        }

        context.Census.SustainControlDegradations++;
        sbyte meter = unchecked((sbyte)registers.Byte(PlayerCombatOffsets.MeterAirframe));

        // image@0x0FDCC/0x0FDDF/0x0FDF2/0x0FE05 — the SOURCE offsets are +0, +2, +4 and +8: the
        // original SKIPS [0xE482], which is why they are written out rather than looped.
        Scale(0, 0xC30C);
        Scale(2, 0xC310);
        Scale(4, 0xC314);
        Scale(8, 0xC318);

        void Scale(int sourceDelta, int destination) =>
            registers.SetWord(
                destination,
                unchecked((ushort)Fixed.MulDiv16Signed(
                    meter,
                    unchecked((short)registers.Word(PlayerCombatOffsets.ControlBoundsBase + sourceDelta)),
                    0x64)));
    }

    /// <summary><c>image@0x0FEEF..0x0FF79</c> — the burst-mode cockpit shake; runs EVERY frame.</summary>
    private static void BurstModeShake(PlayerCombatContext context, ushort frame)
    {
        CombatRegisters registers = context.Registers;
        byte mode = registers.Byte(PlayerCombatOffsets.BurstMode);
        if (mode == 0
            || registers.Word(PlayerCombatOffsets.BurstDeadlineFrame) > frame)
        {
            return;
        }

        context.Census.SustainBurstShakes++;

        short gate;
        short amplitude;
        if (mode == 1)
        {
            // image@0x0FF09..0x0FF2C
            registers.SetWord(PlayerCombatOffsets.BurstShakeY, 0);
            registers.SetWord(PlayerCombatOffsets.BurstShakeX, 0);
            registers.SetWord(
                PlayerCombatOffsets.BurstDeadlineFrame,
                unchecked((ushort)(context.Random.RandBounded(5) + frame + 2)));
            gate = 0x96;
            amplitude = 0x32;
        }
        else
        {
            // image@0x0FF35..0x0FF4B
            registers.SetWord(
                PlayerCombatOffsets.BurstDeadlineFrame,
                unchecked((ushort)(context.Random.RandBounded(3) + frame)));
            gate = 0xF0;
            amplitude = 0x64;
        }

        // image@0x0FF4C..0x0FF79 — two centred draws; the second is at half the amplitude.
        if (context.Random.Rand8() >= gate)
        {
            return;
        }

        short half = unchecked((short)(amplitude >> 1));
        registers.SetWord(
            PlayerCombatOffsets.BurstShakeX,
            unchecked((ushort)(context.Random.RandBounded(amplitude) - half)));
        registers.SetWord(
            PlayerCombatOffsets.BurstShakeY,
            unchecked((ushort)(context.Random.RandBounded(half) - (short)(amplitude >> 2))));
    }

    /// <summary><c>image@0x0FF7A..0x0FFC3</c> — the once-per-scaled-frame blink advance.</summary>
    private static void Tail(PlayerCombatContext context)
    {
        CombatRegisters registers = context.Registers;
        ushort scaled = registers.Word(PlayerCombatOffsets.FrameCountScaled);
        if (registers.Word(PlayerCombatOffsets.SustainTailGate) > scaled)
        {
            return;
        }

        registers.SetWord(PlayerCombatOffsets.SustainTailGate, unchecked((ushort)(scaled + 1)));

        if (registers.Byte(PlayerCombatOffsets.SustainTailDisable) == 0 && !EngineOutOrBlacked())
        {
            if (unchecked((short)registers.Word(PlayerCombatOffsets.AltitudeCompareA))
                > unchecked((short)registers.Word(PlayerCombatOffsets.AltitudeCompareB)))
            {
                return;
            }
        }

        context.Census.SustainTailAdvances++;
        context.Events.AdvanceBlinkTimer(0x80);

        bool EngineOutOrBlacked()
        {
            // image@0x0FF8E..0x0FFB0
            if (unchecked((sbyte)registers.Byte(PlayerCombatOffsets.MeterEngine)) >= MeterMax
                && registers.Word(PlayerCombatOffsets.ThrottlePercent) != 0)
            {
                return true;
            }

            if (registers.Byte(PlayerCombatOffsets.BlackoutEnabled) == 0)
            {
                return false;
            }

            short gLoad = unchecked((short)registers.Word(PlayerCombatOffsets.PlayerGLoad));
            return gLoad >= 6 || gLoad <= -4;
        }
    }

    private static int ReadInt32(CombatRegisters registers, int address) =>
        unchecked((int)(registers.Word(address) | (registers.Word(address + 2) << 16)));

    private static void WriteInt32(CombatRegisters registers, int address, int value)
    {
        // The original writes low-then-high on the drain (`sub`/`sbb` @image@0x0FC9F/0x0FCA3) and
        // high-then-low on the clamp (image@0x0FCB0/0x0FCB3); both touch the same four bytes, so the
        // write SET — which is what a verification compares — is identical either way.
        registers.SetWord(address, unchecked((ushort)value));
        registers.SetWord(address + 2, unchecked((ushort)(value >> 16)));
    }
}
