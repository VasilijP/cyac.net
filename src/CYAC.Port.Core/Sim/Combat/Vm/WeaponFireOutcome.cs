namespace CYAC.Port.Core.Sim.Combat.Vm;

/// <summary>
/// <c>weapon_fire_outcome_and_evasion_init @image@0x08812</c> (0x1AE = 430 B) — the shot's HIT
/// verdict and, when the target survives, its evasion manoeuvre parameters.
/// </summary>
/// <remarks>
/// <para>
/// It belongs to the VM: An earlier pass measured that its ONLY two doors are <c>image@0x053CE</c> (the
/// interpreter's mode-5 close-range kill arm) and <c>image@0x0559C</c> (its opcode-<c>0xDE</c>
/// FIRE_WEAPON handler) — the C3 brief had mis-attributed its eleven RNG draws to the per-shot FSM.
/// </para>
/// <para>
/// <b>The block argument is polymorphic.</b>  The far pointer the callers push is <c>DS:0xED54</c>
/// (the DGROUP VM SCRATCH) from mode 5, and <c>[0x0094]:blockRef</c> (a POOL block) from the
/// <c>0xDE</c> handler.  <see cref="VmBlockRef"/> carries that distinction; everything the routine
/// reads through the block's <c>+0x02</c> owner pointer is in the pool segment either way
/// (<c>mov es,[0x94]</c> @<c>image@0x0883F</c>).
/// </para>
/// <para>
/// <b>Draw census — eleven, in order.</b> <c>0x08861</c> hit-eligibility; <c>0x0889E</c> evasion
/// gate; <c>0x088B5</c> pitch gate; <c>0x088C2</c> pitch magnitude; <c>0x088DA</c> heading
/// magnitude; <c>0x088F3</c> alt kick; <c>0x0892C</c> frame-max gate; <c>0x08939</c> frame-max;
/// <c>0x0894D</c>/<c>0x08961</c>/<c>0x0897A</c> the post-hit pitch/heading/alt triple.  A TWELFTH
/// lives in the nested <c>engagement_spawn_frame_threshold_reset @image@0x087FE</c>
/// (<c>rand_bounded(12) + [0xF0D0] + 4 → [0xED8A]</c>), which only the hit arm calls.
/// </para>
/// </remarks>
public static class WeaponFireOutcome
{
    /// <summary>Runs the resolver.</summary>
    /// <param name="context">The VM context.</param>
    /// <param name="block">The engagement block the caller pushed as a far pointer.</param>
    public static void Resolve(EngagementVmContext context, VmBlockRef block)
    {
        ArgumentNullException.ThrowIfNull(context);

        CombatRegisters r = context.Registers;
        PoolArena arena = context.Arena;
        EngagementVmCensus census = context.Census;

        block.SetByte(context, 0x0D, 6);                                    // image@0x0881D phase = 6
        block.SetByte(context, 0x05, (byte)(block.Byte(context, 0x05) & 0xFB));   // image@0x08822
        block.SetWord(context, 0x0E, 0xFFFE);                               // image@0x08827
        block.SetByte(context, 0x2C, 0);                                    // image@0x0882F
        r.SetByte(0xED81, 0);                                               // image@0x08833

        ushort owner = block.Word(context, 0x02);                           // image@0x0883B
        r.SetWord(0xED86, arena.Word((ushort)(owner + 0x12)));              // image@0x08841 heading
        r.SetWord(0xED88, arena.Word((ushort)(owner + 0x14)));              // image@0x08851 pitch
        r.SetWord(0xED8C, 0xFFFF);                                          // image@0x0885B
        r.SetWord(0xED8A, 0xFFFF);                                          // image@0x0885E

        ushort draw = Rand8();                                              // image@0x08861
        if ((short)draw < 0xB4)                                             // image@0x08866 jge
        {
            SpawnFrameThresholdReset(context);                              // image@0x0886B
            ushort prototype = block.Word(context, 0x00);                   // image@0x08871
            if ((context.StaticData.Byte(prototype + 0x0C) & 0x80) != 0     // image@0x08874
                || arena.Word(owner) == 0x2104)                             // image@0x0887C
            {
                r.SetByte(0xED81, (byte)(r.Byte(0xED81) | 1));              // image@0x08883 HIT
            }
        }

        ushort proto2 = block.Word(context, 0x00);                          // image@0x0888B
        if ((context.StaticData.Byte(proto2 + 0x0C) & 0x80) != 0)           // image@0x0888E
        {
            goto PostHitEvasion;                                            // image@0x08894
        }

        if (r.Byte(0x0F80) != 0)                                            // image@0x08897
        {
            goto LockedOn;                                                  // image@0x0889C
        }

        draw = Rand8();                                                     // image@0x0889E
        if ((short)draw < 0x28)                                             // image@0x088A3 jge
        {
            goto PostHitEvasion;                                            // image@0x088A8
        }

        if ((short)draw >= 0xE6)                                            // image@0x088AB jge
        {
            goto LockedOn;
        }

        r.SetByte(0xED81, (byte)(r.Byte(0xED81) | 2));                      // image@0x088B0 EVADING
        census.EvasionArmed++;

        {
            byte pitch;
            if ((short)Rand8() < 0x78)                                      // image@0x088B5 jl
            {
                pitch = 0;                                                  // image@0x088D2
            }
            else
            {
                int magnitude = Bounded(0xF0) - 0x78;                       // image@0x088C2
                pitch = unchecked((byte)((-magnitude) >> 4));               // image@0x088CA sar 4
            }

            r.SetByte(0xED83, pitch);                                       // image@0x088D4
        }

        {
            int magnitude = Bounded(0xF0) - 0x9B0;                          // image@0x088DA
            r.SetByte(0xED82, unchecked((byte)((-magnitude) >> 4)));        // image@0x088E8
        }

        r.SetByte(0xED84, 5);                                               // image@0x088EB
        r.SetByte(0xED85, unchecked((byte)((Bounded(0x190) + 0x320) >> 4)));   // image@0x088F3
        if ((sbyte)r.Byte(0xED83) > 0)                                      // image@0x08902 jg
        {
            r.SetByte(0xED85, unchecked((byte)-(sbyte)r.Byte(0xED85)));     // image@0x0890C neg
        }

        goto Spawn;

    LockedOn:
        r.SetWord(0xED8A, 0xFFFF);                                          // image@0x08914
        r.SetByte(0xED82, 0x8C);                                            // image@0x0891A
        r.SetByte(0xED84, 0x0A);                                            // image@0x0891F
        r.SetByte(0xED83, 0);                                               // image@0x08926
        r.SetByte(0xED85, 0);
        census.OutcomeLockedOn++;
        if ((short)Rand8() < 0x80)                                          // image@0x0892C jge
        {
            r.SetWord(                                                      // image@0x08945
                0xED8C,
                unchecked((ushort)(Bounded(0x10) + r.Word(0xF0D0) + 8)));   // image@0x08939
        }

        goto Spawn;

    PostHitEvasion:
        {
            int magnitude = Bounded(0xF0) - 0x78;                           // image@0x0894D
            r.SetByte(0xED83, unchecked((byte)((-magnitude) >> 4)));        // image@0x0895B
        }

        {
            int magnitude = Bounded(0xF0) - 0x9B0;                          // image@0x08961
            r.SetByte(0xED82, unchecked((byte)((-magnitude) >> 4)));        // image@0x0896F
        }

        r.SetByte(0xED84, 5);                                               // image@0x08972
        r.SetByte(0xED85, unchecked((byte)((Bounded(0x50) + 0xA0) >> 4)));  // image@0x0897A
        if ((sbyte)r.Byte(0xED83) >= 0)                                     // image@0x08989 jl
        {
            r.SetByte(0xED85, unchecked((byte)-(sbyte)r.Byte(0xED85)));     // image@0x08990 neg
        }

        r.SetByte(0xED81, (byte)(r.Byte(0xED81) | 2));                      // image@0x08994
        census.OutcomePostHitEvasion++;

    Spawn:
        // image@0x08999..0x089B5 — the UNCONDITIONAL subsystem4x19_row_attach (ex-projectile_spawn): an output EVENT, eight words.
        context.Effects.SpawnActor(new VmSpawnActorRequest(
            3, 0, 0xFFFF, 0, 2, block.Word(context, 0x02), 0, 0));
        census.OutcomeSpawns++;

        ushort Rand8()
        {
            census.RandomDraws++;
            return context.Random.Rand8();
        }

        int Bounded(int bound)
        {
            census.RandomDraws++;
            return context.Random.RandBounded(bound);
        }
    }

    /// <summary>Overload for a DGROUP-resident block (the interpreter's mode-5 scratch).</summary>
    /// <param name="context">The VM context.</param>
    /// <param name="dgroupOffset">The DGROUP offset — always <c>0xED54</c> at that site.</param>
    public static void Resolve(EngagementVmContext context, int dgroupOffset) =>
        Resolve(context, VmBlockRef.InRegisters((ushort)dgroupOffset));

    /// <summary>Overload for a pool-resident block (the opcode-<c>0xDE</c> site).</summary>
    /// <param name="context">The VM context.</param>
    /// <param name="poolOffset">The block's pool near offset.</param>
    public static void Resolve(EngagementVmContext context, ushort poolOffset) =>
        Resolve(context, VmBlockRef.InArena(poolOffset));

    /// <summary>
    /// <c>engagement_spawn_frame_threshold_reset @image@0x087FE</c> —
    /// <c>[0xED8A] = rand_bounded(12) + [0xF0D0] + 4</c>.
    /// </summary>
    /// <param name="context">The VM context.</param>
    public static void SpawnFrameThresholdReset(EngagementVmContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Census.RandomDraws++;
        int draw = context.Random.RandBounded(12);                          // image@0x087FE
        context.Registers.SetWord(                                          // image@0x0880D
            0xED8A, unchecked((ushort)(draw + context.Registers.Word(0xF0D0) + 4)));
    }
}

/// <summary>
/// A reference to an engagement block that may live EITHER in DGROUP (the VM scratch at
/// <c>[0xED54]</c>) or in the pool arena — the polymorphism the far pointer
/// <c>weapon_fire_outcome_and_evasion_init</c> takes carries in the original.
/// </summary>
/// <param name="IsRegisterResident">True when the block is the DGROUP scratch.</param>
/// <param name="Offset">The DGROUP offset or the pool near offset.</param>
public readonly record struct VmBlockRef(bool IsRegisterResident, ushort Offset)
{
    /// <summary>A block in the combat register file (the scratch).</summary>
    /// <param name="dgroupOffset">Its DGROUP offset.</param>
    public static VmBlockRef InRegisters(ushort dgroupOffset) => new(true, dgroupOffset);

    /// <summary>A block in the pool arena.</summary>
    /// <param name="poolOffset">Its near offset.</param>
    public static VmBlockRef InArena(ushort poolOffset) => new(false, poolOffset);

    /// <summary>Reads a word at a field offset.</summary>
    /// <param name="context">The VM context.</param>
    /// <param name="field">The field's offset inside the block.</param>
    public ushort Word(EngagementVmContext context, int field)
    {
        ArgumentNullException.ThrowIfNull(context);
        return IsRegisterResident
            ? context.Registers.Word(Offset + field)
            : context.Arena.Word((ushort)(Offset + field));
    }

    /// <summary>Reads a byte at a field offset.</summary>
    /// <param name="context">The VM context.</param>
    /// <param name="field">The field's offset inside the block.</param>
    public byte Byte(EngagementVmContext context, int field)
    {
        ArgumentNullException.ThrowIfNull(context);
        return IsRegisterResident
            ? context.Registers.Byte(Offset + field)
            : context.Arena.Byte((ushort)(Offset + field));
    }

    /// <summary>Writes a word at a field offset.</summary>
    /// <param name="context">The VM context.</param>
    /// <param name="field">The field's offset inside the block.</param>
    /// <param name="value">The value.</param>
    public void SetWord(EngagementVmContext context, int field, ushort value)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (IsRegisterResident)
        {
            context.Registers.SetWord(Offset + field, value);
        }
        else
        {
            context.Arena.SetWord((ushort)(Offset + field), value);
        }
    }

    /// <summary>Writes a byte at a field offset.</summary>
    /// <param name="context">The VM context.</param>
    /// <param name="field">The field's offset inside the block.</param>
    /// <param name="value">The value.</param>
    public void SetByte(EngagementVmContext context, int field, byte value)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (IsRegisterResident)
        {
            context.Registers.SetByte(Offset + field, value);
        }
        else
        {
            context.Arena.SetByte((ushort)(Offset + field), value);
        }
    }
}
