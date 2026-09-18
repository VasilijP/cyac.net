using CYAC.Port.Core.Model.Combat;
using CYAC.Port.Core.Primitives;
using CYAC.Port.Core.Sim.Combat.Geometry;

namespace CYAC.Port.Core.Sim.Combat.Vm;

/// <summary>
/// <c>engagement_script_interpreter @image@0x05154</c> (0x14C7 = 5,319 B) — the enemy-AI bytecode
/// VM: a mode dispatcher, an opcode fetch/execute loop, ~24 opcode handlers, a set of autonomous
/// (non-opcode) phase transitions, and a BEHAVIOUR COMPILER that writes new bytecode into the very
/// buffer it is interpreting.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shape of the port.</b> This is a labelled transliteration, not a restructuring: every block is
/// a C# label named for its image address and every branch is the original's, so the file diffs
/// against the disassembly line for line.  That is deliberate — the function is a 1,809-instruction
/// compiler-generated spaghetti of 130+ basic blocks with three computed jump tables and no
/// recoverable high-level structure, and the rule here is bit-exactness, not readability.
/// </para>
/// <para>
/// <b>Register model.</b>  <c>ax/bx/cx/dx/si/di</c> are locals with the original's 16-bit wrap;
/// 32-bit pairs are kept as <c>(ax, dx)</c> exactly as MSC does, so <c>adc</c>/<c>sbb</c> chains and
/// the <c>&lt;&lt; 8</c> byte-shuffle idiom (<c>mov dh,dl / mov dl,ah / mov ah,al / sub al,al</c>)
/// transliterate directly.  Stack slots keep their <c>[bp-NN]</c> names.
/// </para>
/// <para>
/// <b>Three computed jump tables</b>, read from the image and reproduced as C# switches:
/// <c>image@0x0577A</c> (10 entries, opcodes <c>0xD9..0xE2</c>), <c>image@0x05DF8</c> (9 entries,
/// <c>rand8 &amp; 0x0F &lt;= 8</c>) and <c>image@0x05EC4</c> (16 entries, <c>rand8 &amp; 0x0F</c>).
/// The last one explains C0's "unreachable <c>ret</c> @<c>image@0x05EE0</c>": that byte is the low
/// half of the table's fifteenth entry (<c>0x53C3</c> → <c>image@0x05CA3</c>), i.e. DATA.
/// </para>
/// <para>
/// <b>RNG.</b> Every draw goes through <see cref="ICombatRandom"/>, which advances
/// <c>g_prng_state_lo [0x07A8]</c> in the register file — so a verification proves the draw COUNT
/// and ORDER from the post-state word alone.
/// </para>
/// </remarks>
public static class EngagementVm
{
    /// <summary>Runs one interpreter entry.</summary>
    /// <param name="context">The VM context.</param>
    /// <param name="mode">The original's <c>AL</c> — spilled to <c>[bp-0x80]</c> @<c>image@0x0515A</c>.</param>
    public static void Run(EngagementVmContext context, byte mode)
    {
        ArgumentNullException.ThrowIfNull(context);

        CombatRegisters r = context.Registers;
        IAiScriptHeap heap = context.Heap;
        EngagementVmCensus census = context.Census;

        census.Entries++;
        census.Mode[mode < 8 ? mode : 8]++;

        // Registers.
        ushort ax = 0, bx = 0, cx = 0, dx = 0, si = 0, di = 0;

        // Stack slots, by their [bp-NN] names.
        ushort s7E = 0, s7C = 0, s2E = 0, s2C = 0, s2A = 0, s28 = 0, s26 = 0, s24 = 0;
        ushort s1C = 0, s1A = 0, s18 = 0, s16 = 0, s14 = 0, s12 = 0, s10 = 0;
        ushort sE = 0, sC = 0, sA = 0, s6 = 0, s4 = 0, s2 = 0;
        ushort delay = 0;            // [bp-0x20]
        byte opcode = 0;             // [bp-0x22]
        List<byte> stringBuffer = new List<byte>();

        // ── prologue (image@0x05154) ───────────────────────────────────────────────────────────
        r.SetWord(0xED5F, 0);                                             // image@0x0515F
        r.SetWord(0xEDAC, 0);                                             // image@0x05162
        if (r.Byte(0xED61) == 6)                                          // image@0x05165
        {
            census.Phase6Guard++;
            return;
        }

        EngagementNodeLeaves.AcquisitionWindowCheck(NodeContext(context)); // image@0x0516F

        switch (mode)                                                     // image@0x05172 dec-chain
        {
            case 2: census.Mode2TimerGuard++; goto L52B2;
            case 4: census.Mode4NewEngagement++; goto L52D0;
            case 5: census.Mode5Kill++; goto L53B6;
            case 7: census.Mode7Prng++; goto L51CA;
            default: goto L5190;                                          // incl. 0; the last DEC is dead
        }

    // ── the script-PC / frame-gate path (image@0x05190) ────────────────────────────────────────
    L5190:
        census.Visit(0x05190);
        if (Pc() == 0xFFFF)                                               // image@0x05190
        {
            goto L54AA;
        }

        ax = r.Word(0xF0C8);                                              // image@0x0519A
        if (r.Word(0xED62) <= ax)                                         // image@0x0519D jbe
        {
            goto L51B6;
        }

        if (mode != 1)                                                    // image@0x051A3
        {
            goto L6615;
        }

        if (r.Word(0xED62) != 0xFFFE)                                     // image@0x051AC
        {
            goto L6615;
        }

    L51B6:
        census.Visit(0x051B6);
        if (Pc() != 0xFFFF)                                               // image@0x051B6
        {
            goto L5400;
        }

    L51C0:                                                                 // the INVALID / abort arm
        SetPc(0xFFFF);                                                    // image@0x051C0
        goto L54AA;

    // ── mode 7: the prng arm (image@0x051CA) ───────────────────────────────────────────────────
    L51CA:
        census.Visit(0x051CA);
        bx = r.Word(0xED54);
        if ((Proto(bx + 0x0C) & 3) != 0)                                  // image@0x051CE
        {
            goto L6615;
        }

        if (Proto(bx + 0x28) == 0)                                        // image@0x051D7
        {
            goto L6615;
        }

        ax = Rand8();                                                     // image@0x051E0
        cx = Skill(0x0F40);
        if ((short)cx < (short)ax)                                        // image@0x051F4 jge → continue
        {
            goto L6615;
        }

        if ((r.Byte(0xED78) & 8) != 0)                                    // image@0x051F9
        {
            SetPc(0xFFFF);
        }

        if (Pc() != 0xFFFF)                                               // image@0x05206
        {
            goto L6615;
        }

        if (EngagementNodeLeaves.Phase0RangeCheck(NodeContext(context)))  // image@0x05210
        {
            goto L57BF;
        }

        ax = (ushort)(Rand8() & 3);                                       // image@0x0521A
        if (ax == 0)
        {
            goto L5234;
        }

        if (ax == 1)
        {
            goto L525C;
        }

    L5227:
        census.Visit(0x05227);
        if (EnsureScript())                                               // image@0x05227
        {
            goto L5FC2;
        }

        goto L523B;                                                       // image@0x05231 jmp 0x495b

    L5234:
        census.Visit(0x05234);
        if (r.Word(0xED6F) == 0)                                          // image@0x05234
        {
            goto L525C;
        }

    L523B:
        census.Visit(0x0523B);
        r.SetByte(0xED61, 0x0C);                                          // image@0x0523B
        r.SetByte(0xED80, 7);
        r.SetWord(0xED81, r.Word(0xED6F));
        census.Compiler(7);
        if (r.Word(0xB53C) >= 0x1388)                                     // image@0x0524B jae
        {
            ax = 0x0C;                                                    // image@0x05256
            goto L6543;
        }

        goto L6540;

    L525C:
        census.Visit(0x0525C);
        if (r.Word(0xED6F) == 0)                                          // image@0x0525C
        {
            goto L5227;
        }

    L5263:
        census.Visit(0x05263);
        r.SetByte(0xED80, 7);                                             // image@0x05263
        census.Compiler(7);
        CommitTimer(6);                                                   // image@0x05268
        r.SetByte(0xED61, 0x0B);
        r.SetWord(0xED81, 0x5140);
        ax = (ushort)(r.Word(0xED98) + 0x92);
        if ((short)r.Word(0xED7A) <= (short)ax)                           // image@0x0527F jle
        {
            goto L52A9;
        }

        ax = Rand8();                                                     // image@0x05285
        if (ax >= 0x96)                                                   // image@0x0528D jl → continue
        {
            goto L652E;
        }

        if (r.Word(0xED47) <= 0x2EE)                                      // image@0x05292 ja → continue
        {
            goto L652E;
        }

        ax = r.Word(0xED47);                                              // image@0x0529D
        if (r.Word(0xEDBB) <= ax)                                         // image@0x052A0 ja → L52A9
        {
            goto L652E;
        }

    L52A9:
        census.Visit(0x052A9);
        r.SetWord(0xED83, 0x5014);                                        // image@0x052A9
        goto L6534;

    // ── mode 2 (image@0x052B2) ─────────────────────────────────────────────────────────────────
    L52B2:
        census.Visit(0x052B2);
        if ((r.Byte(0xED78) & 1) != 0)                                    // image@0x052B2
        {
            SetPc(0xFFFF);
        }

        // The two legs are easy to swap.  The bytes are `cmp word [0xED76],-1 /
        // jne 0x52C9 (epilogue) / jmp 0x5190` (image@0x052BF..0x052C6): a script that is still RUNNING
        // returns, and an ABANDONED one (flag 0x01 above, or a PC that was already 0xFFFF) re-enters the
        // script gate at image@0x05190, whose `je 0x54AA` hands the bandit to the free-AI manoeuvre
        // selector.  Found flying Create Mission: every custom enemy carries flags 0x3F
        // (engagement_slot_spawn_init @image@0x06DF0), so on acquiring the player it dropped its script
        // and then flew straight for ever (phase 0x0B, timer 0xFFFF), where the original's same bandit
        // re-arms its timer and manoeuvres.  Shipped
        // bandits authored with flag 0x01 took the same wrong leg.
        if (Pc() != 0xFFFF)                                               // image@0x052BF / 0x052C4 jne
        {
            return;                                                       // image@0x052C9 epilogue
        }

        goto L5190;                                                       // image@0x052C6

    // ── mode 4: the new-engagement arm (image@0x052D0) ─────────────────────────────────────────
    L52D0:
        census.Visit(0x052D0);
        bx = r.Word(0xED54);
        if ((Proto(bx + 0x0C) & 3) != 0)                                  // image@0x052D4
        {
            goto L6615;
        }

        if (r.Byte(0xED61) == 8 || r.Byte(0xED61) == 9)                   // image@0x052DD / 0x052E7
        {
            goto L6615;
        }

        s4 = Pc() != 0xFFFF ? (ushort)1 : (ushort)0;                      // image@0x052F1..0x052FE
        if ((r.Byte(0xED78) & 4) != 0)                                    // image@0x05301
        {
            SetPc(0xFFFF);
        }

        if (Pc() != 0xFFFF)                                               // image@0x0530E
        {
            goto L6615;
        }

        if (r.Word(0xED6F) == 0)                                          // image@0x05318
        {
            goto L5351;
        }

        ax = r.Word(0xED6F);                                              // image@0x0531F
        if (r.Word(0x00C0) == ax && (byte)s4 == 0)                        // image@0x05322 / 0x05328
        {
            goto L6615;
        }

        ax = r.Word(0xEDE2);                                              // image@0x05331
        if (r.Word(0x00C0) == ax && (r.Byte(0xED59) & 0x40) != 0)         // image@0x05334 / 0x0533A
        {
            ax = Rand8();                                                 // image@0x05341
            if ((short)ax < 0x3C)
            {
                r.SetWord(0xED6F, r.Word(0xEDE2));                        // image@0x0534B
            }
        }

    L5351:
        census.Visit(0x05351);
        if (r.Byte(0xED80) is 9 or 0x0A or 0x10)                          // image@0x05351/0x0535B/0x05365
        {
            goto L6615;
        }

        if (EngagementNodeLeaves.Phase0RangeCheck(NodeContext(context)))  // image@0x0536F
        {
            goto L57BF;
        }

        ax = Rand8();                                                     // image@0x05379
        if ((short)ax >= 0x3C)
        {
            goto L5227;
        }

    L5386:
        census.Visit(0x05386);
        if (r.Word(0xED47) >= 0x3E8)                                      // image@0x05386 jb → continue
        {
            goto L62D4;
        }

    L5391:
        census.Visit(0x05391);
        ax = Rand8();                                                     // image@0x05391
        cx = Skill(0x0F64);
        if ((short)cx > (short)ax)                                        // image@0x053A5 jle → L53AA
        {
            goto L57AC;
        }

        ax = (ushort)(Rand8() & 0x0F);                                    // image@0x053AA
        goto L5EB6;

    // ── mode 5: the close-range kill arm (image@0x053B6) ───────────────────────────────────────
    L53B6:
        census.Visit(0x053B6);
        bx = r.Word(0xED54);
        if ((Proto(bx + 0x0C) & 3) != 0)                                  // image@0x053BA
        {
            goto L6615;
        }

        SetPc(0xFFFF);                                                    // image@0x053C3
        WeaponFireOutcome.Resolve(context, CombatRegisters.ScratchDgroupOffset);   // image@0x053CE
        return;                                                           // image@0x053D6 epilogue

    // ── opcode 0xDC SET_FLIGHT_PARAMS (image@0x053DC) ──────────────────────────────────────────
    L53DC:
        census.Visit(0x053DC);
        AddWrap(0xED4E, (short)ReadWord());                               // image@0x053DC..0x053E3
        AddWrap(0xED50, (short)ReadWord());                               // image@0x053E8..0x053EF
        AddWrap(0xED52, (short)ReadWord());                               // image@0x053F4..0x053FB
        goto L5400;

    // ── the fetch loop (image@0x05400) ─────────────────────────────────────────────────────────
    L5400:
        census.Visit(0x05400);
        delay = ReadWord();                                               // image@0x05400
        opcode = ReadByte();                                              // image@0x05406
        ax = opcode;
        census.OpcodesExecuted++;
        census.Opcode[opcode]++;

        if (ax == 0xD8)
        {
            goto L548E;
        }

        if (ax > 0xD8)
        {
            goto L575C;
        }

        if (ax == 0x0A)
        {
            goto L56F8;
        }

        if (ax > 0x0A)
        {
            goto L572C;
        }

        switch (ax)                                                       // image@0x05425..0x05444
        {
            case 0x00: goto L55FC;
            case 0x02: goto L562C;
            case 0x07: goto L5664;
            case 0x08: goto L56A2;
            case 0x09: goto L56CA;
            default:
                census.InvalidOpcodes++;
                goto L51C0;                                               // image@0x05447
        }

    // ── the opcode handlers ────────────────────────────────────────────────────────────────────
    L544A:                                                                 // 0xDB CLEAR_COUNTER
        r.SetWord(0xC390, 0);                                             // image@0x0544A
        goto L5400;

    L5452:                                                                 // 0xDD RESET_PC
        SetPc(0);                                                         // image@0x05452
        goto L5400;

    L545A:                                                                 // 0xFE RETURN
        r.SetByte(0xED3E, (byte)(r.Byte(0xED3E) & 0xFE));                 // image@0x0545A
        bx = r.Word(0xED56);                                              // image@0x0545F
        context.Events.RecordDeparture(bx);                                // image@0x05463 film record
        return;                                                           // image@0x05468 epilogue

    L546E:                                                                 // 0xDA RETARGET_SLOT
        ExpiryInit();                                                     // image@0x0546E..0x05472
        goto L5400;

    L5478:                                                                 // 0xE0 CALL_SCRIPT_FUNCTION
        ax = ReadWord();                                                  // image@0x05478
        context.Effects.CallScriptFunction(ax);                           // image@0x0547C
        goto L5400;

    L5484:                                                                 // 0xD9 SET_HEADING
        r.SetWord(0xED4E, ReadWord());                                    // image@0x05484
        goto L5400;

    L548E:                                                                 // 0xD8 SET_SCRIPT_FLAGS
        ax = ReadWord();                                                  // image@0x0548E
        sA = ax;
        r.SetByte(0xED78, (byte)(r.Byte(0xED78) | (byte)ax));             // image@0x05494
        if (ax != 1)                                                      // image@0x05498
        {
            goto L5400;
        }

        if (r.Word(0xED6F) == 0)                                          // image@0x054A0
        {
            goto L5400;
        }

    L54AA:
        census.Visit(0x054AA);
        bx = r.Word(0xED54);
        if ((Proto(bx + 0x0C) & 3) == 0)                                  // image@0x054AE jne → L54B7
        {
            goto L5794;
        }

        r.SetByte(0xED61, 0);                                             // image@0x054B7
        ax = 0xFFFF;
        goto L6612;

    L54C2:                                                                 // 0xD7 CLEAR_SCRIPT_FLAGS
        ax = ReadWord();                                                  // image@0x054C2
        r.SetByte(0xED78, (byte)(r.Byte(0xED78) & (byte)~(byte)ax));      // image@0x054C5
        goto L5400;

    L54CE:                                                                 // 0xD6 SPAWN_ACTOR
        sA = ReadWord();                                                  // image@0x054CE slot index
        s24 = ReadWord();                                                 // image@0x054D4 type
        delay = ReadWord();                                               // image@0x054DA heading → [bp-0x20]
        s26 = ReadWord();                                                 // image@0x054E0 duration
        bx = (ushort)(sA * 2);                                            // image@0x054F6
        context.Effects.SpawnActor(new VmSpawnActorRequest(               // image@0x05503
            (byte)s24, 1, delay, 0, s26, Actor(bx), 0, 0));
        goto L5400;

    L550C:                                                                 // 0xD5 KILL_ACTOR
        ax = ReadWord();                                                  // image@0x0550C
        bx = (ushort)(ax * 2);                                            // image@0x05511
        context.Effects.KillActor(Actor(bx));                             // image@0x05517
        goto L5400;

    L5520:                                                                 // 0xDE FIRE_WEAPON
        ax = ReadWord();                                                  // image@0x05520
        sA = ax;
        bx = (ushort)(ax * 2);                                            // image@0x05526
        ax = Actor(bx);                                                   // image@0x0552A
        dx = r.Word(0x0094);                                              // image@0x0552E
        s2A = ax;
        s28 = dx;
        bx = ax;
        if ((context.Arena.Byte((ushort)(bx + 2)) & 1) == 0)              // image@0x0553C
        {
            goto L5400;
        }

        context.Effects.ScheduleDeferredEffect(new VmDeferredEffectRequest(   // image@0x0555F
            ax,
            1,
            context.Arena.Word((ushort)(bx + 8)),
            context.Arena.Word((ushort)(bx + 6)),
            0,
            0,
            context.Arena.Word((ushort)(bx + 0x10)),
            context.Arena.Word((ushort)(bx + 0x0E))));

        context.Effects.ImpactSound((ushort)(s2A + 6));                   // image@0x05564..0x05571

        bx = s2A;
        si = context.Arena.Word(bx);                                      // image@0x05579
        ax = Class(si + 0x2C);                                            // image@0x0557C — DS-relative
        dx = 0;
        {
            short hi = (short)context.Arena.Word((ushort)(bx + 0x0C));
            if (0 > hi)                                                   // image@0x05581 jg
            {
                goto L55A8;
            }

            if (0 >= hi && ax >= context.Arena.Word((ushort)(bx + 0x0A))) // image@0x05589 jae
            {
                goto L55A8;
            }
        }

        // image@0x0558F — the far thunk answers the actor's engagement block, which the outcome
        // resolver then runs on.
        s2E = context.Arena.EngagementBlockRef(s2A);
        s2C = r.Word(0x0094);
        WeaponFireOutcome.Resolve(context, s2E);                          // image@0x0559C
        goto L5400;

    L55A8:
        census.Visit(0x055A8);
        EngagementExpiryLoop.TargetDeparture(                             // image@0x055A8
            context.Arena, r, context.Prototypes, context.Events, s2A);
        bx = s2A;
        context.Effects.SlotFire(                                         // image@0x055BE
            unchecked((int)(context.Arena.Word((ushort)(bx + 6))
                | ((uint)context.Arena.Word((ushort)(bx + 8)) << 16))),
            unchecked((int)(context.Arena.Word((ushort)(bx + 0x0E))
                | ((uint)context.Arena.Word((ushort)(bx + 0x10)) << 16))));
        goto L5400;

    L55CA:                                                                 // 0xDF PRINT_STRING
        stringBuffer.Clear();                                             // image@0x055CA lea ax,[bp-0x7e]
        while (true)                                                      // image@0x055D0..0x055DD
        {
            byte value = ReadByte();
            stringBuffer.Add(value);
            if (value == 0)
            {
                break;
            }
        }

        context.Effects.PrintString(                                      // image@0x055E4
            System.Text.Encoding.Latin1.GetString(
                stringBuffer.ToArray(), 0, stringBuffer.Count - 1));
        goto L5400;

    L55EC:                                                                 // 0xE2 CLEAR_FLAG
        r.SetByte(0xED59, (byte)(r.Byte(0xED59) & 0xDF));                 // image@0x055EC
        goto L5400;

    L55F4:                                                                 // 0xE1 SET_FLAG
        r.SetByte(0xED59, (byte)(r.Byte(0xED59) | 0x20));                 // image@0x055F4
        goto L5400;

    L55FC:                                                                 // 0x00 SET_PHASE_IDLE
        r.SetByte(0xED61, 0);                                             // image@0x055FE
        r.SetByte(0xED80, 0);                                             // image@0x05601
        census.Compiler(0);
        ax = delay;                                                       // image@0x05604
        goto L6612;

    L560A:                                                                 // 0x0C SET_PHASE_0C
        r.SetByte(0xED61, 0x0C);                                          // image@0x0560A
        CommitTimer(delay);                                               // image@0x05612
        r.SetWord(0xED81, ReadWord());                                    // image@0x05615
        r.SetWord(0xED87, 0);                                             // image@0x0561D
        r.SetWord(0xED85, 0);
        r.SetWord(0xED83, 0);
        return;                                                           // image@0x05626 epilogue

    L562C:                                                                 // 0x02 SET_ORIENTATION_TARGETS
        r.SetByte(0xED61, 2);                                             // image@0x0562C
        r.SetByte(0xED80, 0);
        census.Compiler(0);
        CommitTimer(delay);                                               // image@0x05639
        {
            int a = ReadByte3();                                          // image@0x0563C
            int b = ReadByte3();                                          // image@0x05645
            int c = ReadByte3();                                          // image@0x0564E
            s1A = (ushort)a;
            s18 = (ushort)(a >> 16);
            s16 = (ushort)b;
            s14 = (ushort)(b >> 16);
            s12 = (ushort)c;
            s10 = (ushort)(c >> 16);
        }

        goto L5657;

    L5651:
        census.Visit(0x05651);
        s12 = ax;                                                         // image@0x05651
        s10 = dx;

    L5657:
        census.Visit(0x05657);
        // image@0x05657 — engagement_octant_init_and_store @image@0x07644 over the six-word block
        // at [bp-0x1A]: (x, y, z) as three 32-bit values.
        FirePosGeometry.StoreSentinelPositionAndArmOctant(
            context.View,
            new CombatPosition(
                unchecked((int)(s1A | ((uint)s18 << 16))),
                unchecked((int)(s16 | ((uint)s14 << 16))),
                unchecked((int)(s12 | ((uint)s10 << 16)))));
        return;                                                           // image@0x0565D epilogue

    L5664:                                                                 // 0x07 SET_PHASE_07
        r.SetByte(0xED61, 7);                                             // image@0x05664
        r.SetByte(0xED80, 2);
        census.Compiler(2);
        ax = delay;
        goto L6612;                                                       // image@0x0566E jmp 0x4d24

    L5670:                                                                 // 0x0D SET_PHASE_0D
        r.SetByte(0xED61, 0x0D);                                          // image@0x05670
        r.SetByte(0xED80, 0);
        census.Compiler(0);
        CommitTimer(delay);                                               // image@0x0567D
        r.SetByte(0xED87, ReadByte());                                    // image@0x05680
        r.SetWord(0xED88, 0);                                             // image@0x05686
        r.SetWord(0xED81, ReadWord());                                    // image@0x0568C
        r.SetWord(0xED83, ReadWord());                                    // image@0x05692
        r.SetWord(0xED85, ReadWord());                                    // image@0x05698
        goto L6615;

    L56A2:                                                                 // 0x08 SET_PHASE_DIVE
        r.SetByte(0xED61, 8);                                             // image@0x056A2
        r.SetByte(0xED80, 0);
        census.Compiler(0);
        CommitTimer(delay);                                               // image@0x056AF
        {
            int v = unchecked((int)(r.Word(0xED4A) | ((uint)r.Word(0xED4C) << 16)) + 0x0003E800);
            r.SetWord(0xED83, (ushort)v);                                 // image@0x056BF
            r.SetWord(0xED85, (ushort)(v >> 16));
        }

        goto L6615;

    L56CA:                                                                 // 0x09 SET_HEADING_TARGETS
        r.SetByte(0xED61, 9);                                             // image@0x056CA
        r.SetByte(0xED80, 0);
        census.Compiler(0);
        CommitTimer(delay);                                               // image@0x056D7
        {
            int a = ReadByte3();                                          // image@0x056DA
            r.SetWord(0xED81, (ushort)a);
            r.SetWord(0xED83, (ushort)(a >> 16));
            int b = ReadByte3();                                          // image@0x056E4
            r.SetWord(0xED85, (ushort)b);
            r.SetWord(0xED87, (ushort)(b >> 16));
        }

        r.SetWord(0xED89, r.Word(0xED50));                                // image@0x056EE
        goto L6615;

    L56F8:                                                                 // 0x0A SET_PHASE_0A
        r.SetByte(0xED61, 0x0A);                                          // image@0x056F8
        r.SetByte(0xED80, 0);                                             // image@0x056FD
        census.Compiler(0);
        goto L5702;

    L5724:                                                                 // 0x0B SET_PHASE_0B (shares 0x0A's tail)
        r.SetByte(0xED61, 0x0B);                                          // image@0x05724

    L5702:
        census.Visit(0x05702);
        CommitTimer(delay);                                               // image@0x05705
        r.SetWord(0xED81, ReadWord());                                    // image@0x05708
        r.SetWord(0xED83, ReadWord());                                    // image@0x0570E
        r.SetWord(0xED85, ReadWord());                                    // image@0x05714
        ax = ReadWord();                                                  // image@0x0571A

    L571D:
        census.Visit(0x0571D);
        r.SetWord(0xED87, ax);                                            // image@0x0571D
        goto L6615;

    L572C:                                                                 // the 0x0B/0x0C/0x0D + 0xD5..0xD7 sub-dispatch
        switch (ax)                                                       // image@0x0572C..0x05758
        {
            case 0x0D: goto L5670;
            case 0x0B: goto L5724;
            case 0x0C: goto L560A;
            case 0xD5: goto L550C;
            case 0xD6: goto L54CE;
            case 0xD7: goto L54C2;
            default:
                census.InvalidOpcodes++;
                goto L51C0;
        }

    L575C:                                                                 // the high dispatch
        if (ax == 0xFE)                                                   // image@0x0575C
        {
            goto L545A;
        }

        if (ax > 0xFE)                                                    // image@0x05764 — 0xFF SUSPEND
        {
            goto L51C0;                                                   // image@0x0578E
        }

        ax = (ushort)(ax - 0xD9);                                         // image@0x05766
        if (ax > 9)                                                       // image@0x05769 jbe
        {
            census.InvalidOpcodes++;
            goto L51C0;
        }

        switch (ax)                                                       // image@0x0577A — the 10-entry table
        {
            case 0: goto L5484;   // 0xD9
            case 1: goto L546E;   // 0xDA
            case 2: goto L544A;   // 0xDB
            case 3: goto L53DC;   // 0xDC
            case 4: goto L5452;   // 0xDD
            case 5: goto L5520;   // 0xDE
            case 6: goto L55CA;   // 0xDF
            case 7: goto L5478;   // 0xE0
            case 8: goto L55F4;   // 0xE1
            default: goto L55EC;  // 0xE2
        }

    // ── the outer arms (image@0x05794 onwards) ─────────────────────────────────────────────────
    L5794:
        census.Visit(0x05794);
        ax = FirePosGeometry.HighWordClip(context.View, 0xED46);          // image@0x05794
        if (ax < r.Word(0xEDA4))                                          // image@0x0579B jb
        {
            goto L57B8;
        }

    L57A1:
        census.Visit(0x057A1);
        if (r.Word(0xED47) < 0xBB8)                                       // image@0x057A1 jb
        {
            goto L57AC;
        }

        goto L5F90;

    L57AC:
        census.Visit(0x057AC);
        r.SetByte(0xED80, 3);                                             // image@0x057AC
        census.Compiler(3);
        ax = HeadingTargetSelect();                                       // image@0x057B1
        goto L5F98;

    L57B8:
        census.Visit(0x057B8);
        if (!EngagementNodeLeaves.Phase0RangeCheck(NodeContext(context)))  // image@0x057B8
        {
            goto L57F0;
        }

    L57BF:
        census.Visit(0x057BF);
        if (!EnsureScript())                                              // image@0x057BF
        {
            RestartMode8();                                               // image@0x057C7
            goto L6615;
        }

        r.SetByte(0xED78, (byte)(r.Byte(0xED78) & 0xF0));                 // image@0x057CE
        AiScriptMemory.WriteWord(r, heap, 0x000F);                        // image@0x057D3
        AiScriptMemory.WriteByte(r, heap, AiScriptOpcode.SetPhaseIdle);
        AiScriptMemory.WriteWord(r, heap, 0xFFFE);                        // image@0x057DE
        AiScriptMemory.WriteByte(r, heap, AiScriptOpcode.SetPhaseDive);
        census.EmittedBytes += 6;

    L57E9:
        census.Visit(0x057E9);
        AiScriptMemory.EmitSuspend(r, heap);                                  // image@0x057E9
        census.EmittedBytes += 3;
        goto L51B6;

    L57F0:
        census.Visit(0x057F0);
        if (r.Byte(0xED64) != 0xFF)                                       // image@0x057F0
        {
            ax = r.Word(0xF0C8);
            if (r.Word(0xED7D) > ax)                                      // image@0x057FA jbe
            {
                goto L5890;
            }
        }

        // image@0x05803 — reached only by falling through the two tests above.
        if ((r.Byte(0xED5A) & 2) != 0)                                    // image@0x05803
        {
            goto L5890;
        }

        if (Pc() != 0xFFFF)                                               // image@0x0580D
        {
            goto L6615;
        }

        bx = r.Word(0xED54);
        if ((Proto(bx + 0x0C) & 3) != 0)                                  // image@0x0581B
        {
            goto L6615;
        }

        if ((r.Byte(0xED5A) & 2) != 0)                                    // image@0x05824
        {
            goto L6615;
        }

        r.SetByte(0xED5A, (byte)(r.Byte(0xED5A) | 2));                    // image@0x0582E
        ax = (ushort)((r.Byte(0xED59) & 0x40) != 0 ? 1 : 0);              // image@0x05833..0x0583E
        s1C = ax;
        {
            bool attributed = ax == 1;                                    // image@0x05848 sbb/neg
            int zone = ZoneProgramGenerator.ProximityScan(                // image@0x05853
                r,
                unchecked((int)(r.Word(0xED42) | ((uint)r.Word(0xED44) << 16))),
                unchecked((int)(r.Word(0xED4A) | ((uint)r.Word(0xED4C) << 16))),
                attributed,
                !attributed,
                out _);
            s7E = (ushort)zone;                                           // image@0x05856
            if (zone == 0)                                                // image@0x05859
            {
                census.ZoneScanEmpty++;
                goto L54AA;
            }
        }

        if (!EnsureScript())                                              // image@0x05860
        {
            goto L54AA;
        }

        {
            ushort source = r.Word(s7E + 0x0C);                           // image@0x0586D
            Span<byte> from = heap.Block(source);
            Span<byte> into = heap.Block(r.Word(AiScriptMemory.ScriptSegmentOffset));
            int n = Math.Min(AiScriptMemory.ProgramBufferBytes, Math.Min(from.Length, into.Length));
            from[..n].CopyTo(into);                                       // image@0x0587D far_memcpy 0x32
            census.ZoneProgramCopyIns++;
        }

        SetPc(0);                                                         // image@0x05882
        r.SetByte(0xED78, 0x10);                                          // image@0x05888
        goto L51B6;

    L5890:
        census.Visit(0x05890);
        if (r.Word(0xED6F) != 0)                                          // image@0x05890
        {
            bx = r.Word(0xED54);
            if ((Proto(bx + 0x0C) & 0x80) == 0)                           // image@0x0589B
            {
                goto L59E0;
            }
        }

        // image@0x058A4 — reached only by falling through the target/prototype tests.
        if (r.Word(0xED47) >= 0x7D0)                                      // image@0x058A4 jae
        {
            goto L58BC;
        }

        r.SetByte(0xED61, 7);                                             // image@0x058AC
        r.SetByte(0xED80, 2);
        census.AutoPhase7Short++;
        census.Compiler(2);
        ax = 0xFFFE;
        goto L6612;

    L58BC:
        census.Visit(0x058BC);
        if (!EnsureScript())                                              // image@0x058BC
        {
            goto L523B;
        }

        r.SetByte(0xED78, (byte)(r.Byte(0xED78) | 0x0D));                 // image@0x058C6
        sE = 0;                                                           // image@0x058CB
        sC = 0x2710;                                                      // image@0x058D0
        {
            // image@0x058D5..0x058E1 — angle_vec2_rotate_inplace: pt = &[bp-0xE] (pushed FIRST),
            // pivot = the DGROUP pair at 0x0680 (SECOND), angle = [0xED4E] (LAST).
            short px = (short)sE;
            short py = (short)sC;
            Vector2Rotate.RotateInPlace(
                (short)r.Word(0xED4E),
                (short)context.StaticData.Word(0x0680),
                (short)context.StaticData.Word(0x0682),
                ref px,
                ref py);
            sE = (ushort)px;
            sC = (ushort)py;
        }

        AiScriptMemory.WriteWord(r, heap, 0xFFFE);                        // image@0x058E6
        AiScriptMemory.WriteByte(r, heap, AiScriptOpcode.SetOrientationTargets);
        census.EmittedBytes += 3;

        s16 = r.Word(0xED46);                                             // image@0x058F1
        s14 = r.Word(0xED48);
        if ((short)Unaligned15() < 0x834)                                 // image@0x058FE jge
        {
            s16 = 0x3400;                                                 // image@0x05905
            s14 = 8;
        }

        if ((short)s14 > 0xFA)                                            // image@0x0590F jle
        {
            s16 = 0;                                                      // image@0x05916
            s14 = 0xFA;
        }

        ax = r.Word(0xEDA4);                                              // image@0x05920
        if (Unaligned15() > ax)                                           // image@0x05923 jbe
        {
            // image@0x05928 — the << 8 byte shuffle with DX zeroed first.
            uint shifted = (uint)ax << 8;
            s16 = (ushort)shifted;
            s14 = (ushort)(shifted >> 16);
        }

        {
            int wide = Shl8((short)sC);                                   // image@0x05938
            cx = (ushort)wide;                                            // image@0x05944
            bx = (ushort)(wide >> 16);
            int sum = unchecked(wide + (int)(r.Word(0xED4A) | ((uint)r.Word(0xED4C) << 16)));
            s12 = (ushort)sum;                                            // image@0x05950
            s10 = (ushort)(sum >> 16);

            int wideX = Shl8((short)sE);                                  // image@0x05956
            si = (ushort)wideX;                                           // image@0x05962
            di = (ushort)(wideX >> 16);
            int sumX = unchecked(wideX + (int)(r.Word(0xED42) | ((uint)r.Word(0xED44) << 16)));
            s1A = (ushort)sumX;                                           // image@0x0596E
            s18 = (ushort)(sumX >> 16);
            s7E = cx;                                                     // image@0x05974
            s7C = bx;

            AiScriptMemory.Write3Bytes(r, heap, sumX);                    // image@0x0597A
            AiScriptMemory.Write3Bytes(r, heap,
                unchecked((int)(s16 | ((uint)s14 << 16))));               // image@0x05983
            AiScriptMemory.Write3Bytes(r, heap, sum);                     // image@0x0598C
            AiScriptMemory.WriteWord(r, heap, 0xFFFE);                    // image@0x0598F
            AiScriptMemory.WriteByte(r, heap, AiScriptOpcode.SetOrientationTargets);
            census.EmittedBytes += 12;

            int backZ = unchecked((int)(r.Word(0xED4A) | ((uint)r.Word(0xED4C) << 16))
                - (int)(cx | ((uint)bx << 16)));                          // image@0x0599A
            s12 = (ushort)backZ;
            s10 = (ushort)(backZ >> 16);
            int backX = unchecked((int)(r.Word(0xED42) | ((uint)r.Word(0xED44) << 16))
                - (int)(si | ((uint)di << 16)));                          // image@0x059AD
            s1A = (ushort)backX;
            s18 = (ushort)(backX >> 16);

            AiScriptMemory.Write3Bytes(r, heap, backX);                   // image@0x059BE
            AiScriptMemory.Write3Bytes(r, heap,
                unchecked((int)(s16 | ((uint)s14 << 16))));               // image@0x059C7
            AiScriptMemory.Write3Bytes(r, heap, backZ);                   // image@0x059D0
            AiScriptMemory.WriteWord(r, heap, 0);                         // image@0x059D3
            AiScriptMemory.WriteByte(r, heap, AiScriptOpcode.ResetPc);
            census.EmittedBytes += 12;
        }

        goto L51B6;

    L59E0:
        census.Visit(0x059E0);
        context.Geometry.Snapshot.Refresh(context.Geometry, true);        // image@0x059E2 (AL = 1)
        if ((r.Byte(0xED5A) & 4) == 0)                                    // image@0x059E5
        {
            goto L5A70;
        }

        // image@0x059F3: AL = 1 means the target is INSIDE the world extents, and `jne` then skips
        // the waypoint re-engage.
        if (context.Support.ObjectWithinWorldExtents(r, context.Arena, r.Word(0xED6F)))
        {
            goto L5A70;
        }

        r.SetByte(0xED61, 2);                                             // image@0x059FA
        r.SetByte(0xED80, 0);
        census.AutoPhase2Waypoint++;
        census.Compiler(0);
        CommitTimer(0xFFFE);                                              // image@0x05A05
        {
            ReadOnlySpan<byte> waypoint = r.Read(0xEDB6, 12);                            // image@0x05A15 rep movsw cx=6
            s1A = (ushort)(waypoint[0] | (waypoint[1] << 8));
            s18 = (ushort)(waypoint[2] | (waypoint[3] << 8));
            s16 = (ushort)(waypoint[4] | (waypoint[5] << 8));
            s14 = (ushort)(waypoint[6] | (waypoint[7] << 8));
            s12 = (ushort)(waypoint[8] | (waypoint[9] << 8));
            s10 = (ushort)(waypoint[10] | (waypoint[11] << 8));
        }

        if ((short)r.Word(0xF0E8) > (short)s18)                           // image@0x05A1A jle
        {
            s1A = r.Word(0xF0E6);                                         // image@0x05A20
            s18 = r.Word(0xF0E8);
        }

        if ((short)r.Word(0xF0EC) < (short)s18)                           // image@0x05A30 jge
        {
            s1A = r.Word(0xF0EA);                                         // image@0x05A36
            s18 = r.Word(0xF0EC);
        }

        if ((short)r.Word(0xF0F0) > (short)s10)                           // image@0x05A46 jle
        {
            s12 = r.Word(0xF0EE);                                         // image@0x05A4C
            s10 = r.Word(0xF0F0);
        }

        if ((short)r.Word(0xF0F4) >= (short)s10)                          // image@0x05A5C jl
        {
            goto L5657;
        }

        ax = r.Word(0xF0F2);                                              // image@0x05A65
        dx = r.Word(0xF0F4);
        goto L5651;

    L5A70:
        census.Visit(0x05A70);
        if (mode == 8)                                                    // image@0x05A70
        {
            goto L5263;
        }

        ax = r.Word(0xED6F);                                              // image@0x05A79
        if (r.Word(0x00C0) != ax)                                         // image@0x05A7C
        {
            goto L5AD7;
        }

        if (r.Word(0xB53C) <= 0x1388 || r.Word(0xB53C) >= 0x4E20)         // image@0x05A82 / 0x05A8A
        {
            goto L5AD7;
        }

        context.Support.BearingAndElevationTo(
            r, context.Arena, ax, out short bearing, out short elevation);
        s2 = (ushort)bearing;                                             // image@0x05AA5 out-words
        s6 = (ushort)elevation;
        ax = (ushort)Abs16(Angle.Normalize(                               // image@0x05AAA..0x05AB8
            unchecked((short)(r.Word(0xED4E) - s2))));
        if ((short)ax > 0xA0)                                             // image@0x05ABA jg
        {
            goto L5AD7;
        }

        ax = (ushort)Abs16(Angle.Normalize(                               // image@0x05ABF..0x05ACD
            unchecked((short)(r.Word(0xED50) - s6))));
        if ((short)ax > 0xA0)                                             // image@0x05ACF jg
        {
            goto L5AD7;
        }

        goto L5B62;

    L5AD7:
        census.Visit(0x05AD7);
        // image@0x05AD7: sbb ax,ax / and ax,0xEC78 / add ax,0x3A98 (16-bit wrap).
        ax = r.Byte(0xB538) == 0 ? (ushort)0x2710 : (ushort)0x3A98;
        if (ax < r.Word(0xB53C))                                          // image@0x05AE4 jae
        {
            goto L523B;
        }

        if (r.Byte(0xB53A) == 0)                                          // image@0x05AED
        {
            goto L5CEA;
        }

        if (r.Byte(0xB539) == 0)                                          // image@0x05AF7
        {
            goto L5C48;
        }

        ax = Rand8();                                                     // image@0x05B01
        cx = Skill(0x0F44);
        if ((short)cx > (short)ax)                                        // image@0x05B15 jle
        {
            goto L5EE4;
        }

        ax = Rand8();                                                     // image@0x05B1A
        cx = Skill(0x0F48);
        if ((short)cx > (short)ax)                                        // image@0x05B2E jle
        {
            goto L5391;   // image@0x05B30/0x05C77/0x05D37/0x05E39: jmp 0x4ab1 = image@0x05391
        }

        if (r.Word(0xB53C) <= 0xBB8)                                      // image@0x05B33 ja
        {
            goto L5BE6;
        }

        if (r.Byte(0xB538) == 0)                                          // image@0x05B3E cmp with ch=0
        {
            goto L5BE6;
        }

        ax = Rand8();                                                     // image@0x05B47
        if ((short)ax >= 0x32)
        {
            goto L5BE6;
        }

    L5B54:
        census.Visit(0x05B54);
        if (EnsureScript())                                               // image@0x05B54
        {
            goto L64B4;
        }

        goto L523B;

    L5B62:
        census.Visit(0x05B62);
        ax = (ushort)Abs16(Angle.Normalize(unchecked((short)(            // image@0x05B62..0x05B75
            r.Word(0xED4E) - context.Arena.Word((ushort)(r.Word(0x00C0) + 0x12))))));
        if ((short)ax < 0x4D8)                                            // image@0x05B77 jge
        {
            goto L5AD7;
        }

        ax = (ushort)Abs16(Angle.Normalize(unchecked((short)(            // image@0x05B7F..0x05B93
            context.Arena.Word((ushort)(r.Word(0x00C0) + 0x14)) + r.Word(0xED50)))));
        if ((short)ax > 0xC8)                                             // image@0x05B95 jle
        {
            goto L5AD7;
        }

        ax = Rand8();                                                     // image@0x05B9D
        if ((short)ax > 0xB4)
        {
            goto L5AD7;
        }

        ax = Rand8();                                                     // image@0x05BAA
        if ((ax & 1) != 0)
        {
            if (EnsureScript())                                           // image@0x05BB3
            {
                goto L63C2;
            }

            goto L523B;
        }

        r.SetByte(0xED80, 7);                                             // image@0x05BC0
        census.Compiler(7);
        CommitTimer(0x0A);                                                // image@0x05BC5
        r.SetByte(0xED61, 0x0B);                                          // image@0x05BCB
        census.AutoPhaseBFirst++;
        r.SetWord(0xED83, 0x5CF8);                                        // image@0x05BD0
        r.SetWord(0xED81, 0x5140);
        r.SetWord(0xED85, 0x5140);
        ax = r.Word(0xED9C);                                              // image@0x05BDF
        goto L571D;

    L5BE6:
        census.Visit(0x05BE6);
        if (r.Byte(0xB534) != 0)                                          // image@0x05BE6
        {
            ax = Rand8();                                                 // image@0x05BED
            if ((short)ax >= 0x32)
            {
                goto L5C0A;
            }
        }
        else
        {
            goto L5C0A;
        }

    L5BF7:
        census.Visit(0x05BF7);
        ax = (ushort)(r.Word(0xED98) + 0x92);                             // image@0x05BF7
        if ((short)r.Word(0xED7A) > (short)ax)                            // image@0x05BFD jle
        {
            goto L5F86;
        }

        goto L57AC;                                                       // image@0x05C06

    L5C0A:
        census.Visit(0x05C0A);
        ax = (ushort)(Rand8() & 3);                                       // image@0x05C0A
        if (ax == 0)
        {
            goto L5C20;
        }

        if (ax == 1)
        {
            goto L5263;
        }

        if (ax == 2)
        {
            goto L5BF7;
        }

        goto L523B;

    L5C20:
        census.Visit(0x05C20);
        r.SetByte(0xED80, 7);                                             // image@0x05C20
        census.Compiler(7);
        CommitTimer(6);                                                   // image@0x05C25
        r.SetByte(0xED61, 0x0B);                                          // image@0x05C2B
        census.AutoPhaseBSecond++;
        r.SetWord(0xED81, 0x5140);                                        // image@0x05C30
        r.SetWord(0xED83, 0x5140);
        r.SetWord(0xED85, 0x4D58);                                        // image@0x05C39
        r.SetWord(0xED87, 0xA240);
        goto L6615;

    L5C48:
        census.Visit(0x05C48);
        ax = Rand8();                                                     // image@0x05C48
        cx = Skill(0x0F4C);
        if ((short)cx > (short)ax)                                        // image@0x05C5C jle
        {
            goto L5EE4;
        }

        ax = Rand8();                                                     // image@0x05C61
        cx = Skill(0x0F50);
        if ((short)cx > (short)ax)                                        // image@0x05C75 jle
        {
            goto L5391;   // image@0x05B30/0x05C77/0x05D37/0x05E39: jmp 0x4ab1 = image@0x05391
        }

        ax = Rand8();                                                     // image@0x05C7A
        if ((short)ax >= 0x32)
        {
            goto L523B;
        }

        bx = context.Arena.EngagementBlockRef(r.Word(0xED6F));            // image@0x05C87
        ax = unchecked((ushort)(r.Word(0xED7A) - context.Arena.Word((ushort)(bx + 0x26))));
        sA = ax;                                                          // image@0x05C9B
        if ((short)ax <= 0x32)                                            // image@0x05C9E jle
        {
            goto L5CD4;
        }

    L5CA3:
        census.Visit(0x05CA3);
        si = FirePosGeometry.HighWordClip(context.View, 0xEDBA);          // image@0x05CA3
        ax = FirePosGeometry.HighWordClip(context.View, 0xED46);          // image@0x05CB0
        ax = (ushort)Abs16(unchecked((short)(ax - si)));                  // image@0x05CB3
        if ((short)ax > 0x12C)                                            // image@0x05CBA jle
        {
            goto L523B;
        }

        ax = (ushort)(r.Word(0xED98) + 0x92);                             // image@0x05CC2
        if ((short)r.Word(0xED7A) > (short)ax)                            // image@0x05CC8 jle
        {
            goto L60F8;
        }

        goto L523B;

    L5CD4:
        census.Visit(0x05CD4);
        if ((short)ax < unchecked((short)0xFFCE))                         // image@0x05CD4 jl
        {
            goto L523B;
        }

    L5CDC:
        census.Visit(0x05CDC);
        if (r.Word(0xED47) >= 0x7D0)                                      // image@0x05CDC jb
        {
            goto L6138;
        }

        goto L5263;

    L5CEA:
        census.Visit(0x05CEA);
        if (r.Byte(0xB539) == 0)                                          // image@0x05CEA
        {
            goto L5E0A;
        }

        if ((r.Byte(0xED59) & 3) == 0)                                    // image@0x05CF4
        {
            ax = Rand8();                                                 // image@0x05CFB
            if ((short)ax < 0x64)
            {
                goto L57AC;
            }
        }

        ax = Rand8();                                                     // image@0x05D08
        cx = Skill(0x0F54);
        if ((short)cx > (short)ax)                                        // image@0x05D1C jle
        {
            goto L5EE4;
        }

        ax = Rand8();                                                     // image@0x05D21
        cx = Skill(0x0F58);
        if ((short)cx > (short)ax)                                        // image@0x05D35 jle
        {
            goto L5391;   // image@0x05B30/0x05C77/0x05D37/0x05E39: jmp 0x4ab1 = image@0x05391
        }

        if (r.Word(0xB53C) >= 0x7D0)                                      // image@0x05D3A jae
        {
            goto L5D74;
        }

        ax = (ushort)(r.Word(0xED98) + 0x92);                             // image@0x05D42
        if ((short)r.Word(0xED7A) <= (short)ax)                           // image@0x05D48 jle
        {
            goto L5D74;
        }

        ax = Rand8();                                                     // image@0x05D4E
        if ((short)ax >= 0x32)
        {
            goto L5D74;
        }

    L5D58:
        census.Visit(0x05D58);
        if (r.Word(0xED47) < 0x1388)                                      // image@0x05D58 jae
        {
            goto L5391;
        }

        bx = r.Word(0xED54);                                              // image@0x05D63
        if ((Proto(bx + 0x0D) & 4) != 0)                                  // image@0x05D67
        {
            goto L6336;
        }

        goto L5391;

    L5D74:
        census.Visit(0x05D74);
        if (r.Byte(0xB538) != 0)                                          // image@0x05D74
        {
            ax = Rand8();                                                 // image@0x05D7B
            if ((short)ax < 0x32)
            {
                goto L5B54;
            }
        }

        if (r.Byte(0xB534) != 0)                                          // image@0x05D88
        {
            ax = Rand8();                                                 // image@0x05D8F
            if ((short)ax < 0x32)
            {
                goto L5D99;
            }
        }

        goto L5DB6;

    L5D99:
        census.Visit(0x05D99);
        ax = (ushort)(r.Word(0xED98) + 0x92);                             // image@0x05D99
        if ((short)r.Word(0xED7A) > (short)ax)                            // image@0x05D9F jle
        {
            goto L6042;
        }

    L5DA8:
        census.Visit(0x05DA8);
        if (EnsureScript())                                               // image@0x05DA8
        {
            goto L61BA;
        }

        goto L523B;

    L5DB6:
        census.Visit(0x05DB6);
        ax = Rand8();                                                     // image@0x05DB6
        if ((short)ax < 0x32)
        {
            goto L5386;
        }

        if (r.Byte(0xB538) != 0 && r.Word(0xED47) > 0x1388)               // image@0x05DC3 / 0x05DCA
        {
            ax = Rand8();                                                 // image@0x05DD2
            if ((short)ax < 0x32)
            {
                goto L57A1;
            }
        }

        ax = (ushort)(Rand8() & 0x0F);                                    // image@0x05DDF
        if (ax > 8)                                                       // image@0x05DE7 jbe
        {
            goto L523B;
        }

        switch (ax)                                                       // image@0x05DF8 — the 9-entry table
        {
            case 0: goto L5BF7;
            case 1: goto L57A1;
            case 2: goto L5D99;
            case 3: goto L5E9A;
            case 4: goto L5E76;
            case 5: goto L635D;
            case 6: goto L5E88;
            case 7: goto L5DA8;
            default: goto L5386;
        }

    L5E0A:
        census.Visit(0x05E0A);
        ax = Rand8();                                                     // image@0x05E0A
        cx = Skill(0x0F5C);
        if ((short)cx > (short)ax)                                        // image@0x05E1E jle
        {
            goto L5EE4;
        }

        ax = Rand8();                                                     // image@0x05E23
        cx = Skill(0x0F60);
        if ((short)cx > (short)ax)                                        // image@0x05E37 jle
        {
            goto L5391;   // image@0x05B30/0x05C77/0x05D37/0x05E39: jmp 0x4ab1 = image@0x05391
        }

        if (r.Byte(0xB534) != 0)                                          // image@0x05E3C cmp with ch=0
        {
            ax = Rand8();                                                 // image@0x05E42
            if ((short)ax < 0x32)
            {
                goto L5BF7;
            }
        }

        if (r.Byte(0xB538) != 0)                                          // image@0x05E4F
        {
            ax = Rand8();                                                 // image@0x05E56
            if ((short)ax < 0x32)
            {
                goto L57AC;
            }
        }

        ax = (ushort)(Rand8() & 7);                                       // image@0x05E63
        if (ax == 0)
        {
            goto L5E76;
        }

        if (ax == 1)
        {
            goto L5227;
        }

        goto L523B;

    L5E76:
        census.Visit(0x05E76);
        ax = (ushort)(r.Word(0xED98) + 0x92);                             // image@0x05E76
        if ((short)r.Word(0xED7A) > (short)ax)                            // image@0x05E7C jle
        {
            goto L627C;
        }

        goto L5DA8;

    L5E88:
        census.Visit(0x05E88);
        r.SetByte(0xED80, 0x0A);                                          // image@0x05E88
        census.Compiler(0x0A);
        if (EnsureScript())                                               // image@0x05E8D
        {
            goto L6026;
        }

        goto L523B;

    L5E9A:
        census.Visit(0x05E9A);
        if (r.Word(0xED47) < 0x2710)                                      // image@0x05E9A jae
        {
            goto L5391;
        }

        bx = r.Word(0xED54);                                              // image@0x05EA5
        if ((Proto(bx + 0x0D) & 4) != 0)                                  // image@0x05EA9
        {
            goto L6086;
        }

        goto L5391;

    L5EB6:
        census.Visit(0x05EB6);
        if (ax > 0x0F)                                                    // image@0x05EB6 ja
        {
            goto L5EE4;
        }

        switch (ax)                                                       // image@0x05EC4 — the 16-entry table
        {
            case 0x0: goto L57AC;
            case 0x1: goto L5BF7;
            case 0x2: goto L57A1;
            case 0x3: goto L635D;
            case 0x4: goto L5E88;
            case 0x5: goto L5D99;
            case 0x6: goto L5E9A;
            case 0x7: goto L5386;
            case 0x8: goto L5DA8;
            case 0x9: goto L5E76;
            case 0xA: goto L5D58;
            case 0xB: goto L5263;
            case 0xC: goto L5B54;
            case 0xD: goto L5227;
            case 0xE: goto L5CA3;
            default: goto L5CDC;
        }

    L5EE4:
        census.Visit(0x05EE4);
        if (r.Byte(0xB538) == 0)                                          // image@0x05EE4
        {
            goto L523B;
        }

        ax = r.Word(0xED6F);                                              // image@0x05EEE
        if (r.Word(0x00C0) != ax)                                         // image@0x05EF1
        {
            goto L523B;
        }

        if ((r.Byte(0xED59) & 3) == 0)                                    // image@0x05EFA
        {
            goto L523B;
        }

        if (r.Word(0xB53C) > 0x2710)                                      // image@0x05F04 jbe
        {
            if (EnsureScript())                                           // image@0x05F0C
            {
                goto L6420;
            }

            goto L523B;
        }

        if (r.Byte(0xB53A) != 0)                                          // image@0x05F1A
        {
            if (r.Byte(0xB534) == 0)                                      // image@0x05F21
            {
                goto L523B;
            }

            ax = Rand8();                                                 // image@0x05F2B
            if ((short)ax >= 0x14)
            {
                goto L523B;
            }

            goto L5BF7;
        }

        if (r.Byte(0xB539) != 0                                           // image@0x05F3C
            && r.Word(0xB53C) < 0x1388                                    // image@0x05F43 jae
            && r.Word(0xB53C) < 0x7D0)                                    // image@0x05F4B jae
        {
            ax = (ushort)(r.Word(0xED98) + 0x92);                         // image@0x05F53
            if ((short)r.Word(0xED7A) > (short)ax)                        // image@0x05F59 jle
            {
                ax = Rand8();                                             // image@0x05F5F
                if ((short)ax < 0x1E)
                {
                    goto L5D58;
                }

                ax = Rand8();                                             // image@0x05F6C
                if ((short)ax < 0x1E)
                {
                    goto L5E76;
                }
            }
        }

        if (EnsureScript())                                               // image@0x05F79
        {
            goto L6450;
        }

        goto L57AC;

    L5F86:
        census.Visit(0x05F86);
        r.SetByte(0xED80, 2);                                             // image@0x05F86
        census.Compiler(2);
        ax = r.Word(0xED92);
        goto L5F98;

    L5F90:
        census.Visit(0x05F90);
        r.SetByte(0xED80, 4);                                             // image@0x05F90
        census.Compiler(4);
        ax = r.Word(0xED94);

    L5F98:
        census.Visit(0x05F98);
        sA = ax;                                                          // image@0x05F98
        if (!EnsureScript())                                              // image@0x05F9B
        {
            goto L523B;
        }

        r.SetByte(0xED78, (byte)(r.Byte(0xED78) | 0x40));                 // image@0x05FA5
        // image@0x05FAA: push 0 (first), push [0xED9C] (second); AX = 6, DX = [0xED4E], BX = [bp-0xA].
        EmitOp0B(6, r.Word(0xED4E), sA, 0, r.Word(0xED9C));                // image@0x05FBB
        goto L57E9;                                                       // image@0x05FBE

    L5FC2:
        census.Visit(0x05FC2);
        r.SetByte(0xED78, (byte)(r.Byte(0xED78) | 0x40));                 // image@0x05FC2
        ax = Rand8();                                                     // image@0x05FC7
        if ((ax & 1) != 0)
        {
            r.SetByte(0xED80, 9);                                         // image@0x05FD0
            census.Compiler(9);
            ax = (ushort)(r.Word(0xED4E) + 0x550);                        // image@0x05FD5
        }
        else
        {
            r.SetByte(0xED80, 0x0A);                                      // image@0x05FDE
            census.Compiler(0x0A);
            ax = (ushort)(r.Word(0xED4E) - 0x550);                        // image@0x05FE3
        }

        sA = ax;                                                          // image@0x05FE9
        si = HeadingTargetSelect();                                       // image@0x05FF4
        ax = Angle.Wrap((short)sA).Units;                                 // image@0x05FFE
        EmitOp0B(0xFFFE, ax, si, 0x7FFF, r.Word(0xED9A));                 // image@0x06007 → 0x05FBB
        goto L57E9;

    L600C:
        census.Visit(0x0600C);
        r.SetByte(0xED78, (byte)(r.Byte(0xED78) | 0x40));                 // image@0x0600C
        bx = HeadingTargetSelect();                                       // image@0x06019
        EmitOp0B(6, 0x21C8, bx, 0x7FFF, r.Word(0xED9A));                  // image@0x06021 → 0x05FBB
        goto L57E9;

    L6026:
        census.Visit(0x06026);
        r.SetByte(0xED78, (byte)(r.Byte(0xED78) | 0x40));                 // image@0x06026
        bx = HeadingTargetSelect();                                       // image@0x06033
        EmitOp0B(6, 0x21C0, bx, 0x7FFF, r.Word(0xED9A));                  // image@0x0603B → 0x05FBB
        goto L57E9;

    L6042:
        census.Visit(0x06042);
        if (!EnsureScript())                                              // image@0x06042
        {
            goto L523B;
        }

        r.SetByte(0xED80, 0x0B);                                          // image@0x0604C
        census.Compiler(0x0B);
        s7E = 0x0A;                                                       // image@0x0606A default
        if (r.Byte(0xB534) != 0)                                          // image@0x06051
        {
            ax = Rand8();                                                 // image@0x06058
            if ((short)ax < 0x80)
            {
                s7E = 0x0F;                                               // image@0x06062
            }
        }

        dx = PickAngleConst();                                            // image@0x06077
        EmitOp0B(s7E, dx, r.Word(0xED92), 0x7FFF, r.Word(0xED9A));        // image@0x06083 → 0x05FBB
        goto L57E9;

    L6086:
        census.Visit(0x06086);
        if (!EnsureScript())                                              // image@0x06086
        {
            goto L523B;
        }

        r.SetByte(0xED78, (byte)(r.Byte(0xED78) | 0x40));                 // image@0x06090
        r.SetByte(0xED80, 0x0C);                                          // image@0x06095
        census.Compiler(0x0C);
        EmitOp0B(0xFFFE, r.Word(0xED4E), 0, 0x5A0, r.Word(0xED9C));       // image@0x060AB
        EmitOp0B(0xFFFE, r.Word(0xED4E), 0x870, 0x5A0, r.Word(0xED9C));   // image@0x060C0
        EmitOp0B(0xFFFE, r.Word(0xED4E), 0x5A0, 0x5A0, r.Word(0xED9C));   // image@0x060D5
        AiScriptMemory.WriteWord(r, heap, 0);                             // image@0x060D8
        AiScriptMemory.WriteByte(r, heap, AiScriptOpcode.SetFlightParams);
        AiScriptMemory.WriteWord(r, heap, 0x5A0);                         // image@0x060E2
        AiScriptMemory.WriteWord(r, heap, 0x5A0);                         // image@0x060E8
        AiScriptMemory.WriteWord(r, heap, 0x5A0);                         // image@0x060EE
        census.EmittedBytes += 9;
        goto L57E9;

    L60F8:
        census.Visit(0x060F8);
        if (!EnsureScript())                                              // image@0x060F8
        {
            goto L523B;
        }

        r.SetByte(0xED78, (byte)(r.Byte(0xED78) | 0x40));                 // image@0x06102
        context.Geometry.Snapshot.Refresh(context.Geometry, false);       // image@0x06109 (AL = 0)
        r.SetByte(0xED80, 0x0D);                                          // image@0x0610C
        census.Compiler(0x0D);
        EmitOp0B(4, 0x1C20, 0x1D60, 0x7FFF, 0xFE0C);                      // image@0x06122
        EmitOp0B(4, 0x1C20, 0x1C20, 0x7FFF, 0xFE0C);                      // image@0x06135 → 0x05FBB
        goto L57E9;

    L6138:
        census.Visit(0x06138);
        si = FirePosGeometry.HighWordClip(context.View, 0xEDBA);          // image@0x06138
        ax = FirePosGeometry.HighWordClip(context.View, 0xED46);          // image@0x06145
        ax = (ushort)Abs16(unchecked((short)(ax - si)));                  // image@0x06148
        if ((short)ax > 0x12C)                                            // image@0x0614F jle
        {
            goto L523B;
        }

        if (!EnsureScript())                                              // image@0x06157
        {
            goto L523B;
        }

        r.SetByte(0xED78, (byte)(r.Byte(0xED78) | 0x40));                 // image@0x06161
        ax = (ushort)Abs16(unchecked((short)(r.Word(0xED47) - r.Word(0xEDBB))));   // image@0x06166
        if ((short)ax > 0x15E)                                            // image@0x06172 jle
        {
            goto L523B;
        }

        r.SetByte(0xED80, 0x0E);                                          // image@0x0617A
        census.Compiler(0x0E);
        context.Geometry.Snapshot.Refresh(context.Geometry, false);       // image@0x06181
        EmitOp0B(5, 0x1C20, 0x1B30, 0x7FFF, r.Word(0xED9C));              // image@0x06195
        EmitOp0B(3, 0x1C20, 0x1C20, 0x7FFF, r.Word(0xED9C));              // image@0x061A8
        EmitOp0B(5, 0x1C20, 0x1C20, 0x7FFF, r.Word(0xED9C));              // image@0x061B6 → 0x06130
        goto L57E9;

    L61BA:
        census.Visit(0x061BA);
        r.SetByte(0xED78, (byte)(r.Byte(0xED78) | 0x40));                 // image@0x061BA
        r.SetByte(0xED80, 1);                                             // image@0x061BF
        census.Compiler(1);
        ax = (ushort)(RandBounded(0x140) - 0xA0);                         // image@0x061C7
        sA = ax;
        if (r.Word(0xED47) < 0xBB8 && (short)ax < 0)                      // image@0x061D2 / 0x061DA
        {
            ax = (ushort)RandBounded(0xA0);                               // image@0x061E1
            sA = ax;
        }

        EmitOp0B(                                                         // image@0x06204
            0xFFFE,
            Angle.Wrap(unchecked((short)(r.Word(0xED4E) - 0x168))).Units,
            sA,
            r.Word(0xED96),
            r.Word(0xED9A));
        EmitOp0B(                                                         // image@0x06224
            0xFFFE,
            Angle.Wrap(unchecked((short)(r.Word(0xED4E) + 0x168))).Units,
            sA,
            unchecked((ushort)-(short)r.Word(0xED96)),
            r.Word(0xED9A));
        ax = Rand8();                                                     // image@0x06227
        if ((short)ax <= 0x50)
        {
            goto L57E9;
        }

        EmitOp0B(                                                         // image@0x0624F
            0xFFFE,
            Angle.Wrap(unchecked((short)(r.Word(0xED4E) - 0x168))).Units,
            sA,
            r.Word(0xED96),
            r.Word(0xED9A));
        ax = Rand8();                                                     // image@0x06252
        if ((short)ax >= 0x50)
        {
            goto L57E9;
        }

        EmitOp0B(                                                         // image@0x06279 → 0x05FB8
            0xFFFE,
            Angle.Wrap(unchecked((short)(r.Word(0xED4E) + 0x168))).Units,
            sA,
            unchecked((ushort)-(short)r.Word(0xED96)),
            r.Word(0xED9A));
        goto L57E9;

    L627C:
        census.Visit(0x0627C);
        bx = r.Word(0xED54);                                              // image@0x0627C
        if ((Proto(bx + 0x0D) & 4) == 0)                                  // image@0x06280
        {
            goto L5391;
        }

        if (!EnsureScript())                                              // image@0x06289
        {
            goto L523B;
        }

        r.SetByte(0xED80, 8);                                             // image@0x06293
        census.Compiler(8);
        EmitOp0B(0xFFFE, r.Word(0xED4E), 0x2D0, 0, r.Word(0xED9A));       // image@0x062A9
        EmitOp0B(3, r.Word(0xEDC2), 0x2D0, 0, r.Word(0xED9A));            // image@0x062BD
        EmitOp0B(3, r.Word(0xEDC2), 0, 0, r.Word(0xED9A));                // image@0x062D0 → 0x05FBB
        goto L57E9;

    L62D4:
        census.Visit(0x062D4);
        if (!EnsureScript())                                              // image@0x062D4
        {
            goto L523B;
        }

        r.SetByte(0xED78, (byte)(r.Byte(0xED78) | 0x40));                 // image@0x062DE
        r.SetByte(0xED80, 0x10);                                          // image@0x062E3
        census.Compiler(0x10);
        for (int i = 0; i < 3; i++)                                       // image@0x062E8 / 0x06302 / 0x0631C
        {
            si = ShotAngleDecide();
            dx = PickAngleConst();
            EmitOp0B(5, dx, si, 0x7FFF, r.Word(0xED9A));
        }

        goto L57E9;

    L6336:
        census.Visit(0x06336);
        if (!EnsureScript())                                              // image@0x06336
        {
            goto L523B;
        }

        r.SetByte(0xED78, (byte)(r.Byte(0xED78) | 0x40));                 // image@0x06340
        ax = (ushort)(r.Word(0xED98) + 0x92);                             // image@0x06345
        if ((short)r.Word(0xED7A) > (short)ax)                            // image@0x0634B jg
        {
            goto L6370;
        }

        ax = Rand8();                                                     // image@0x06351
        if ((ax & 1) == 0)
        {
            goto L5E88;
        }

    L635D:
        census.Visit(0x0635D);
        r.SetByte(0xED80, 9);                                             // image@0x0635D
        census.Compiler(9);
        if (EnsureScript())                                               // image@0x06362
        {
            goto L600C;
        }

        goto L523B;

    L6370:
        census.Visit(0x06370);
        r.SetByte(0xED80, 0x0F);                                          // image@0x06370
        census.Compiler(0x0F);
        EmitOp0B(0xFFFE, r.Word(0xED4E), 0x2D0, 0, r.Word(0xED9A));       // image@0x06386
        EmitOp0B(0xFFFE, r.Word(0xED4E), 0x5A0, 0, r.Word(0xED9A));       // image@0x0639A
        EmitOp0B(0xFFFE, r.Word(0xED4E), 0x870, 0, r.Word(0xED9A));       // image@0x063AE
        EmitOp0B(0xFFFE, r.Word(0xED4E), 0, 0, r.Word(0xED9A));           // image@0x063BF → 0x062CE
        goto L57E9;

    L63C2:
        census.Visit(0x063C2);
        r.SetByte(0xED78, (byte)(r.Byte(0xED78) | 0x40));                 // image@0x063C2
        r.SetByte(0xED80, 7);                                             // image@0x063C7
        census.Compiler(7);
        sA = 0x5528;                                                      // image@0x063E4 default
        if (r.Byte(0xB534) != 0)                                          // image@0x063CC
        {
            ax = Rand8();                                                 // image@0x063D3
            if ((short)ax < 0x80)
            {
                sA = 0x60E0;                                              // image@0x063DD
            }
        }

        ax = (ushort)(Rand8() & 1);                                       // image@0x063F1
        dx = ax == 1 ? (ushort)0x7080 : (ushort)0x3200;                   // image@0x063F9..0x06402
        EmitOp0B(8, dx, sA, 0x5140, r.Word(0xED9C));                      // image@0x0640C
        EmitOp0B(8, 0x5140, sA, 0x4588, r.Word(0xED9C));                  // image@0x0641D → 0x05FB8
        goto L57E9;

    L6420:
        census.Visit(0x06420);
        r.SetByte(0xED78, (byte)(r.Byte(0xED78) | 0x40));                 // image@0x06420
        r.SetByte(0xED80, 7);                                             // image@0x06425
        census.Compiler(7);
        AiScriptMemory.WriteWord(r, heap, 0xFFFE);                        // image@0x0642A
        AiScriptMemory.WriteByte(r, heap, AiScriptOpcode.SetPhase0C);
        AiScriptMemory.WriteWord(r, heap, r.Word(0xED6F));                // image@0x06435
        census.EmittedBytes += 5;
        EmitOp0B(0x0C, 0x7FFE, 0x50, 0x7FFF, r.Word(0xED9C));             // image@0x0644C → 0x05FBB
        goto L57E9;

    L6450:
        census.Visit(0x06450);
        r.SetByte(0xED80, 0x11);                                          // image@0x06450
        census.Compiler(0x11);
        EmitOp0B(                                                         // image@0x06470
            3, Angle.Wrap(unchecked((short)(r.Word(0xED4E) - 0x50))).Units,
            0x50, 0x7FFF, r.Word(0xED9C));
        EmitOp0B(3, r.Word(0xED4E), 0xA0, 0x7FFF, r.Word(0xED9C));        // image@0x06485
        EmitOp0B(                                                         // image@0x064A3
            3, Angle.Wrap(unchecked((short)(r.Word(0xED4E) + 0x50))).Units,
            0x50, 0x7FFF, r.Word(0xED9C));
        EmitOp0B(3, r.Word(0xED4E), 0, 0x7FFF, r.Word(0xED9C));           // image@0x064B1 → 0x063BB
        goto L57E9;

    L64B4:
        census.Visit(0x064B4);
        r.SetByte(0xED78, (byte)(r.Byte(0xED78) | 0x40));                 // image@0x064B4
        r.SetByte(0xED80, 0x11);                                          // image@0x064B9
        census.Compiler(0x11);
        ax = (ushort)(Rand8() & 1);                                       // image@0x064BE
        s7E = ax == 1 ? (ushort)0x7468 : (ushort)0x2E18;                  // image@0x064C6..0x064CE
        s2E = 0x4D58;                                                     // image@0x064EC default
        if (r.Byte(0xB534) != 0)                                          // image@0x064D4
        {
            ax = Rand8();                                                 // image@0x064DB
            if ((short)ax < 0x96)
            {
                s2E = 0x5CF8;                                             // image@0x064E5
            }
        }

        EmitOp0B(5, s7E, s2E, 0x4588, r.Word(0xED9C));                    // image@0x06502
        EmitOp0B(5, s7E, 0x5140, 0x4588, r.Word(0xED9C));                 // image@0x06516
        EmitOp0B(5, s7E, s2E, 0x4588, r.Word(0xED9C));                    // image@0x0652A → 0x05FBB
        goto L57E9;

    L652E:
        census.Visit(0x0652E);
        r.SetWord(0xED83, 0x526C);                                        // image@0x0652E
        r.SetWord(0xED85, 0x526C);
        ax = r.Word(0xED9A);
        goto L571D;

    L6534:
        census.Visit(0x06534);
        r.SetWord(0xED85, 0x526C);                                        // image@0x06534 (the L652E tail)
        ax = r.Word(0xED9A);
        goto L571D;

    L6540:
        census.Visit(0x06540);
        ax = 6;                                                           // image@0x06540

    L6543:
        census.Visit(0x06543);
        CommitTimer(ax);                                                  // image@0x06543
        ax = Skill(0x0F68);                                               // image@0x06546
        sA = ax;
        r.SetWord(0xED83, (ushort)RandBounded(sA));                       // image@0x06556
        r.SetWord(0xED85, (ushort)RandBounded(sA));                       // image@0x06561
        r.SetWord(0xED87, (ushort)RandBounded(sA));                       // image@0x0656C
        if (r.Byte(0xB53A) != 0 && r.Byte(0xB539) == 0)                   // image@0x06574 / 0x0657B
        {
            ax = Rand8();                                                 // image@0x06582
            if ((short)ax < 0x64)
            {
                r.SetWord(0xED87, 0);                                     // image@0x0658C
                r.SetWord(0xED85, 0);
                r.SetWord(0xED83, 0);
            }
        }

        ax = r.Word(0xED6F);                                              // image@0x06597
        if (r.Word(0x00C0) != ax)                                         // image@0x0659A
        {
            goto L6615;
        }

        if (r.Byte(0xB539) == 0 || r.Byte(0xB53A) != 0)                   // image@0x065A0 / 0x065A7
        {
            goto L65EA;
        }

        ax = Rand8();                                                     // image@0x065AE
        cx = Skill(0x0F6C);
        if ((short)cx <= (short)ax)                                       // image@0x065C2 jle
        {
            goto L65EA;
        }

        ax = Rand8();                                                     // image@0x065C4
        // image@0x065C9: sbb cx,cx / and CL,0x92 (CH stays 0xFF) / add cx,0xC8 -> 0x005A or 0x00C8.
        cx = r.Byte(0xB534) < 1 ? (ushort)0x005A : (ushort)0x00C8;
        if ((short)cx <= (short)ax)                                       // image@0x065D9 jle
        {
            r.SetWord(0xED85, 0xFC18);                                    // image@0x065E4
        }
        else
        {
            r.SetWord(0xED85, 0x03E8);                                    // image@0x065DB
        }

    L65EA:
        census.Visit(0x065EA);
        ax = (ushort)(r.Word(0xEDA2) + 0x96);                             // image@0x065EA
        if (r.Word(0xED47) >= ax)                                         // image@0x065F0 jae
        {
            goto L6615;
        }

        ax = r.Word(0xEDA2);                                              // image@0x065F6
        if (r.Word(0xEDBB) >= ax)                                         // image@0x065F9 jae
        {
            goto L6615;
        }

        ax = Rand8();                                                     // image@0x065FF
        if ((short)ax >= 0x64)
        {
            goto L6615;
        }

        r.SetWord(0xED85, 0x7530);                                        // image@0x06609
        ax = 0x0A;

    L6612:
        census.Visit(0x06612);
        CommitTimer(ax);                                                  // image@0x06612

    L6615:
        census.Visit(0x06615);
        return;                                                           // image@0x06615 epilogue

        // ── local helpers ─────────────────────────────────────────────────────────────────────
        ushort Pc() => r.Word(AiScriptMemory.ScriptPcOffset);

        void SetPc(ushort value) => r.SetWord(AiScriptMemory.ScriptPcOffset, value);

        byte Proto(int offset) => context.StaticData.Byte(offset);

        ushort Class(int offset) => context.StaticData.Word(offset);

        // image@0x0552A `mov ax,[bx-0x11a6]` with DS = DGROUP: (bx - 0x11A6) mod 2^16 = bx + 0xEE5A.
        ushort Actor(int index) => r.Word((0xEE5A + index) & 0xFFFF);

        ushort Skill(int table) => context.StaticData.Byte(table + (r.Byte(0xED59) & 3));

        byte ReadByte()
        {
            byte value = AiScriptMemory.ReadByte(r, heap);
            return value;
        }

        ushort ReadWord() => AiScriptMemory.ReadWord(r, heap);

        int ReadByte3() => AiScriptMemory.ReadByte3(r, heap);

        ushort Rand8()
        {
            census.RandomDraws++;
            return context.Random.Rand8();
        }

        int RandBounded(int bound)
        {
            census.RandomDraws++;
            return context.Random.RandBounded(bound);
        }

        bool EnsureScript()
        {
            bool had = r.Word(AiScriptMemory.ScriptSegmentOffset) != 0;
            bool ok = AiScriptMemory.EnsureScriptLoaded(r, heap);
            if (!had && ok)
            {
                census.ProgramsAllocated++;
            }

            return ok;
        }

        void EmitOp0B(ushort d, ushort targetD, ushort targetB, ushort first, ushort second)
        {
            AiScriptMemory.EmitSetTargets(r, heap, d, targetD, targetB, first, second);
            census.EmittedBytes += 11;
            census.EmittedSetTargets++;
        }

        void CommitTimer(ushort value) => EngagementScriptTimer.Commit(r, value);

        void AddWrap(int dgroupOffset, short deltaValue) =>
            r.SetWord(dgroupOffset, new Angle(r.Word(dgroupOffset)).Add(deltaValue).Units);

        void ExpiryInit()
        {
            EngagementState block = r.Scratch;
            EngagementList.ExpiryInit(block, context.Prototypes, r.Word(0xF0C8), context.Random);
            census.RandomDraws++;
            r.Scratch = block;
        }

        ushort HeadingTargetSelect() => EngagementShotAngles.HeadingTargetSelect(context);

        ushort PickAngleConst() => EngagementShotAngles.PickAngleConst(context);

        ushort ShotAngleDecide() => EngagementShotAngles.ShotAngleDecide(context);

        void RestartMode8() => EngagementShotAngles.RestartMode8(context);

        ushort Unaligned15() => (ushort)((s16 >> 8) | ((s14 & 0xFF) << 8));

        int Shl8(short value) => unchecked((int)((uint)value << 8));

        EngagementNodeContext NodeContext(EngagementVmContext c) => new()
        {
            Geometry = c.Geometry,
            Random = c.Random,
        };

        static short Abs16(short value) => value < 0 ? unchecked((short)-value) : value;
    }
}
