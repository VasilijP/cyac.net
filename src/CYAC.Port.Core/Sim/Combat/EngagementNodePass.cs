using CYAC.Port.Core.Primitives;
using CYAC.Port.Core.Sim.Combat.Geometry;

namespace CYAC.Port.Core.Sim.Combat;

/// <summary>
/// <c>weapon_fire_combat_loop_per_shot @image@0x0416C</c> — the 14-arm per-engagement FSM the
/// expiry loop runs for every live node, once per frame.
/// </summary>
/// <remarks>
/// <para>
/// INT-only.  It has EXACTLY ONE door image-wide — the <c>call 0x416c</c> at <c>image@0x073E5</c>
/// inside <c>engagement_expiry_loop</c> — takes no arguments and discards its return value, so C1's
/// <see cref="IEngagementNodePass"/> seam is the whole ABI.
/// </para>
/// <para>
/// Shape: a PROLOGUE that stages two per-node quantities, a 14-entry jump table on the phase byte
/// <c>[0xED61]</c>, and a four-part EPILOGUE (§F1 clamps, §F2 the acquisition gate, §F3 the
/// shot-class chain, §F4/§F5 the ground-impact gate and the next-pass schedule).  Only case A can
/// re-dispatch (<c>jmp 0x41c9</c> @<c>image@0x04471</c>, after the interpreter may have rewritten
/// the phase), so the switch sits in a loop.
/// </para>
/// <para>
/// Two "rare" arms are RÉGIME arms, live on some recordings and dead on others (the B5
/// mandate): the prologue ELSE arm <c>image@0x041AC</c> and the epilogue SKIP-ACQ arm
/// <c>image@0x047DC</c>.  Both are modelled and both are counted.
/// </para>
/// <para>
/// Source of truth: the original's bytes <c>image@0x0416C..0x048B7</c> — a recursive-descent walk
/// seeded with the entry and the 14 decoded switch targets reaches 604 instructions covering 1,825
/// of 1,868 bytes (the 43 others are 15 <c>0x90</c> pads and the 28-byte jump table at
/// <c>image@0x04752</c>), which reproduces the same census exactly (the
/// is knowledge only).
/// </para>
/// </remarks>
/// <param name="context">The node context this pass runs in.</param>
public sealed class EngagementNodePass(EngagementNodeContext context) : IEngagementNodePass
{
    /// <summary>The DGROUP base of the 14-byte per-phase ATTRIBUTE table.</summary>
    /// <remarks>
    /// <para>
    /// Indexed by the PHASE, not phase×2 (<c>mov bl,[0xed61] / sub bh,bh / test byte
    /// [bx+0xf0e],N</c>).  Three bits are read: bit0 "run the acquisition
    /// FSM" (§F2), bit3 "block the low-altitude ground impact" (§F4) and bit4 "skip the
    /// arc-accumulator altitude cap" (§F1).
    /// </para>
    /// <para>
    /// It is CONSTANT: an exhaustive scan of the L1 image for every direct-addressed write form
    /// (<c>A2</c>/<c>A3</c>/<c>C6 06</c>/<c>C7 06</c>/<c>88 /r</c>/<c>89 /r</c>) into
    /// <c>[0x0F0E..0x0F1B]</c> found ZERO sites, so it is read through
    /// <see cref="ICombatStaticData"/> like the rest of the constant DGROUP.
    /// </para>
    /// </remarks>
    public const int PhaseAttributeTable = 0x0F0E;

    private readonly EngagementNodeContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    /// <summary>The context this pass runs in.</summary>
    public EngagementNodeContext Context => _context;

    /// <inheritdoc/>
    /// <remarks>
    /// The seam's three arguments are C1's; the original reads all of them out of DGROUP, so they
    /// are checked for consistency and otherwise unused.  <paramref name="nodeRef"/> in particular
    /// is the node the loop popped and is NOT an argument of <c>image@0x0416C</c>.
    /// </remarks>
    public void Run(CombatRegisters registers, PoolArena arena, ushort nodeRef)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(arena);
        if (!ReferenceEquals(registers, _context.Registers) || !ReferenceEquals(arena, _context.Arena))
        {
            throw new EngagementNodeSeamException(
                "EngagementNodePass was built over a different register file / arena than the "
                    + "expiry loop handed it; weapon_fire_combat_loop_per_shot reads DGROUP and the "
                    + "pool directly, so the two must be the same objects.");
        }

        Run();
    }

    /// <summary>Runs one per-node pass over the context's own state.</summary>
    public void Run()
    {
        EngagementNodeFrame frame = new EngagementNodeFrame();
        Prologue();
        Dispatch(frame);
        Epilogue(frame);
        _context.Census.Passes++;
    }

    // ══════════════════════════════════════════════════════════════════════════ §A, the prologue

    /// <summary>
    /// §A, <c>image@0x04174..0x041B6</c> — clear the two per-pass outputs, compute this node's dt,
    /// stage the arc pre-decay rate, and either update the arc parameters or take régime arm (i).
    /// </summary>
    private void Prologue()
    {
        CombatRegisters registers = _context.Registers;
        EngagementAngleView view = _context.View;

        registers.SetByte(0xEDE0, 0);                                   // image@0x04176
        registers.SetByte(0x0F0C, 0);                                   // image@0x04179

        // image@0x0417C: [0xEDAE] is a per-node DT — the frame time since this
        // engagement's LAST pass, whose only writer is this function's own §F5 stamp (C3a
        // §4.1 #9).
        registers.SetWord(
            0xEDAE,
            unchecked((ushort)(registers.Word(0xF0D2) - registers.Word(0xED5D))));

        // image@0x04186: cos_x4(elevation) as a 32-bit value, arithmetic >>3, low word stored.
        int cosX4 = TrigTables.NavCosX4(new Angle(unchecked((ushort)view.Elevation)));
        registers.SetWord(0xB52E, unchecked((ushort)(short)(cosX4 >> 3)));   // image@0x0419A

        ushort prototype = view.WeaponDescriptorTable;                  // image@0x0419D
        if ((_context.StaticData.Byte(prototype + 0x0D) & 2) != 0)      // image@0x041A1 test / je
        {
            _context.Census.PrologueArcParams++;
            EngagementArcParams.Update(_context.Geometry);              // image@0x041A7
            return;
        }

        // ── RÉGIME ARM (i) — image@0x041AC ───────────────────────────────────────────────────
        _context.Census.PrologueElse++;
        registers.SetWord(0xED9E, unchecked((ushort)(registers.Word(0xED9E) << 8)));   // image@0x041AE
        registers.SetWord(0xB532, 0);                                   // image@0x041B2
    }

    // ══════════════════════════════════════════════════════════════════════ §C + the jump table

    /// <summary>
    /// §C (<c>image@0x041B8</c>) and the switch (<c>image@0x04744</c>).  The bound test is
    /// <c>ja</c> — UNSIGNED — so a phase byte above <c>0x0D</c> takes the reset arm rather than
    /// indexing off the end.
    /// </summary>
    private void Dispatch(EngagementNodeFrame frame)
    {
        CombatRegisters registers = _context.Registers;

        byte phase = registers.Byte(0xED61);                            // image@0x041B8
        bool isC = phase == 0x0C;
        if (isC)
        {
            _context.Census.PhaseIsC++;
        }

        registers.SetByte(0xEDE4, isC ? (byte)1 : (byte)0);             // image@0x041C6

        while (true)
        {
            phase = registers.Byte(0xED61);                             // image@0x041C9
            _context.Census.Dispatches++;

            if (phase > 0x0D)                                           // image@0x04744 cmp/ja
            {
                _context.Census.Phase[14]++;
                ResetArm();
                return;
            }

            _context.Census.Phase[phase]++;
            switch (phase)
            {
                case 0x0:
                    Case0();
                    return;
                case 0x1:
                case 0x3:
                case 0x5:
                    ResetArm();
                    return;
                case 0x2:
                    Case2(frame);
                    return;
                case 0x4:
                case 0xC:
                    PhaseChain(frame);
                    return;
                case 0x6:
                    Case6();
                    return;
                case 0x7:
                    Case7();
                    return;
                case 0x8:
                    Case8();
                    return;
                case 0x9:
                    Case9(frame);
                    return;
                case 0xA:
                    if (CaseA(frame))
                    {
                        continue;                                       // image@0x04471 jmp 0x41c9
                    }

                    return;
                case 0xB:
                    CaseB();
                    return;
                default:
                    CaseD();
                    return;
            }
        }
    }

    /// <summary>The reset arm, <c>image@0x04736</c>: park the node in phase <c>0x0B</c>.</summary>
    private void ResetArm()
    {
        _context.Census.ResetArm++;
        _context.Registers.SetByte(0xED61, 0x0B);
        _context.Registers.SetWord(0xED62, 0);
    }

    // ══════════════════════════════════════════════════════════════════════════════ the 14 arms

    /// <summary>
    /// Case 0, <c>image@0x041D2</c> — the IDLE countdown: <c>[0xEDAC] = max(0, [0xED62] −
    /// [0xF0C8])</c>, unsigned.
    /// </summary>
    private void Case0()
    {
        CombatRegisters registers = _context.Registers;
        ushort master = registers.Word(0xF0C8);
        ushort deadline = registers.Word(0xED62);

        ushort remaining;
        if (deadline <= master)                                         // image@0x041D9 jbe
        {
            _context.Census.Case0Expired++;
            remaining = 0;                                              // image@0x041E4
        }
        else
        {
            _context.Census.Case0Counting++;
            remaining = unchecked((ushort)(deadline - master));         // image@0x041DE
        }

        registers.SetWord(0xEDAC, remaining);                           // image@0x041E6
    }

    /// <summary>
    /// Case 2, <c>image@0x0426C</c> — fly to the SENTINEL-CODED waypoint and latch "arrived" in
    /// <c>[0xED59]</c> bit2 when the octant window accepts.
    /// </summary>
    private void Case2(EngagementNodeFrame frame)
    {
        CombatRegisters registers = _context.Registers;
        EngagementAngleView view = _context.View;

        ushort distance = FirePosGeometry.SentinelDistance2dScaled(view);   // image@0x0426C
        ushort rate;
        if (distance <= 0x0FA0)                                         // image@0x0426F jbe
        {
            _context.Census.Case2NearArc++;
            rate = registers.Word(0xED9A);                              // image@0x0427A
        }
        else
        {
            _context.Census.Case2FarArc++;
            rate = registers.Word(0xED9C);                              // image@0x04274
        }

        frame.EvasionRate = rate;                                       // image@0x0427D
        frame.Vector = FirePosGeometry.LoadSentinelPosition(view);      // image@0x04283 (bx = &frame)
        EngagementEvasionBearing.Setup(_context, frame, rate);          // image@0x0428C

        if ((registers.Byte(0xED59) & 4) != 0)                          // image@0x0428F test / je
        {
            _context.Census.Case2EarlyExit++;
            return;
        }

        if (FirePosGeometry.OctantWindowCheck(view))                    // image@0x04299
        {
            _context.Census.Case2OctantHit++;
            LatchArrived();                                             // image@0x042A3
        }
        else
        {
            _context.Census.Case2OctantMiss++;
        }
    }

    /// <summary>
    /// Cases 4 and C, <c>image@0x042AC</c> — the PURSUIT chain: snapshot the acquisition target,
    /// build a lead point, score the firing window, evade, and latch "in range".
    /// </summary>
    /// <remarks>
    /// The chain swaps <c>[0xED81]</c> and <c>[0xED6F]</c> FOUR times (the 3-XOR idiom at
    /// <c>image@0x042AC</c>, <c>0x042E0</c>, <c>0x0437F</c> and <c>0x04397</c>), so the geometry
    /// leaves in the middle see the acquisition target where they expect the sentinel word — and
    /// the pair is back to normal by the time PC5 reads <c>[0xED81]</c> again.
    /// </remarks>
    private void PhaseChain(EngagementNodeFrame frame)
    {
        CombatRegisters registers = _context.Registers;
        EngagementAngleView view = _context.View;

        SwapTargetAndSentinel();                                        // image@0x042AC
        bool hitCheck = registers.Byte(0xED61) == 0x0C;                 // image@0x042C1
        _context.Geometry.Snapshot.Refresh(_context.Geometry, hitCheck);   // image@0x042CE

        // ── PC1 image@0x042D1: the function's ONLY string op — rep movsw cx=6 from [0xEDCE]
        // (the CURRENT snapshot's position triple) into the frame's vector.
        frame.Vector = new CombatPosition(
            SnapshotInt32(0xEDCE), SnapshotInt32(0xEDD2), SnapshotInt32(0xEDD6));
        SwapTargetAndSentinel();                                        // image@0x042E0

        int manhattan = FirePosGeometry.ManhattanDistance2d(            // image@0x04301
            view.FirePosition, frame.Int32(0x16), frame.Int32(0x0E));

        // ── PC2 image@0x04304 ──────────────────────────────────────────────────────────────────
        if (unchecked((short)(manhattan >> 16)) < 2)                    // cmp dx,2 / jge
        {
            _context.Census.PcBiasArm++;
            frame.SetInt32(0x16, unchecked(frame.Int32(0x16) + 0x6400));   // image@0x04309
            frame.SetInt32(0x12, unchecked(frame.Int32(0x12) + 0x6400));   // image@0x04312
            frame.SetInt32(0x0E, unchecked(frame.Int32(0x0E) + 0x6400));   // image@0x0431B
        }
        else
        {
            // image@0x04326: each sentinel word sign-extended and shifted left 8 (the
            // `cwd / mov dh,dl / mov dl,ah / mov ah,al / sub al,al` Q8.8 promote).
            _context.Census.PcAccumulateArm++;
            frame.SetInt32(0x16, unchecked(frame.Int32(0x16) + PromoteShl8(registers, 0xED83)));
            frame.SetInt32(0x12, unchecked(frame.Int32(0x12) + PromoteShl8(registers, 0xED85)));
            frame.SetInt32(0x0E, unchecked(frame.Int32(0x0E) + PromoteShl8(registers, 0xED87)));
        }

        // ── the join image@0x0435C: a 0xA00 FLOOR on the lead point's altitude ─────────────────
        // image@0x04362's `sub al,al` masks the low byte before the compare.  Because 0xA00's own
        // low byte is 0 the masking cannot change a `jae`, but it is modelled literally.
        ushort low = (ushort)(frame.Word(0x12) & 0xFF00);
        short high = frame.Signed(0x10);
        bool clamp = high < 0 || (high == 0 && low < 0x0A00);           // jg / jl / jae
        if (clamp)
        {
            _context.Census.PcAltitudeClamp++;
            frame.SetWord(0x12, 0x0A00);                                // image@0x0436F
            frame.SetWord(0x10, 0);
        }

        FirePosGeometry.ProximityLeadSet(view, frame.Vector);           // image@0x0437C

        // ── PC3 image@0x0437F ──────────────────────────────────────────────────────────────────
        SwapTargetAndSentinel();
        EngagementNodeLeaves.AcquisitionWindowCheck(_context);          // image@0x04394

        // ── PC4 image@0x04397 ──────────────────────────────────────────────────────────────────
        SwapTargetAndSentinel();
        ushort rate;
        if (registers.Byte(0xB538) != 0                                 // image@0x043AC
            && unchecked((sbyte)(registers.Byte(0xED59) & 3)) >= 2)     // image@0x043B3 and/cmp/jl
        {
            _context.Census.PcMidArcRate++;
            rate = unchecked((ushort)(short)(
                unchecked((short)(registers.Word(0xED9A) + registers.Word(0xED9C))) >> 1));
        }
        else if (registers.Byte(0xB53A) != 0)                           // image@0x043C8
        {
            _context.Census.PcFixedRate++;
            rate = 0xA628;                                              // image@0x043CF
        }
        else
        {
            _context.Census.PcHeadingRate++;
            rate = registers.Word(0xED9A);                              // image@0x043D6
        }

        frame.EvasionRate = rate;                                       // image@0x043D9
        EngagementEvasionBearing.Setup(_context, frame, rate);          // image@0x043E2

        // ── PC5 image@0x043E5 ──────────────────────────────────────────────────────────────────
        if ((registers.Byte(0xED59) & 4) != 0)                          // test / je
        {
            _context.Census.PcEarlyExit++;
            return;
        }

        ushort target = registers.Word(0xED81);                         // image@0x043EF
        ushort distance = FirePosGeometry.Distance3dScaled(             // image@0x043F5
            view.FirePosition,
            EngagementNodeLeaves.ObjectPosition(_context.Arena, target),
            false,
            0,
            0,
            0);
        if (distance < 0x07D0)                                          // image@0x043F8 jb
        {
            _context.Census.PcCloseRange++;
            LatchArrived();                                             // image@0x04400 -> 0x042A3
        }
    }

    /// <summary>
    /// Case 6, <c>image@0x04708</c> — the SPAWN/sweep phase, followed by the ground test that
    /// departs the slot.
    /// </summary>
    private void Case6()
    {
        CombatRegisters registers = _context.Registers;

        EnemySpawnAngleInit.Step(_context);                             // image@0x04708

        if ((registers.Byte(0xED3E) & 1) == 0)                          // image@0x0470B test / je
        {
            _context.Census.Case6NotArmed++;
            return;
        }

        // image@0x04712: {lo = prototype[+0x2C], hi = 0} against the fire position's ALTITUDE.
        int ground = _context.StaticData.Word(registers.Word(0xED3C) + 0x2C);
        int altitude = unchecked((int)(registers.Word(0xED46) | ((uint)registers.Word(0xED48) << 16)));

        if (EngagementNodeLeaves.GreaterOrEqual(ground, altitude))      // image@0x0471B..0x0472A
        {
            _context.Census.Case6Depart++;
            EngagementNodeLeaves.SlotImpactAndDepart(_context);         // image@0x0472F
        }
        else
        {
            _context.Census.Case6Alive++;
        }

        registers.SetWord(0xEDAC, 2);                                   // image@0x041F1
    }

    /// <summary>
    /// Case 7, <c>image@0x04404</c> — the EVASIVE break: climb to <c>[0xED92]</c>, roll level, walk
    /// the arc accumulator toward the arc heading and latch "in range" past <c>0x7D0</c>.
    /// </summary>
    private void Case7()
    {
        EngagementAngleView view = _context.View;

        short scale = EngagementAngleSteppers.BankSinScale(view);       // image@0x04404
        EngagementAngleSteppers.ElevationStepToward(                    // image@0x0440C
            view, unchecked((short)_context.Registers.Word(0xED92)), scale);
        EngagementAngleSteppers.BankStepToward(                         // image@0x04415
            view, 0, unchecked((short)_context.Registers.Word(0xED90)));
        EngagementSpeed.StepTowardHeading(_context.Geometry);            // image@0x04418
        EngagementSpeed.AccumulateFirePosition(_context.Geometry);       // image@0x0441B
        RangeLatchAndSchedule(0x07D0);                                  // image@0x0441E
    }

    /// <summary>
    /// Case 8, <c>image@0x0450C</c> — the SHALLOW pursuit: walk the arc accumulator and, while the
    /// aim point is at or beyond the sentinel Z, nudge the elevation down toward <c>0x50</c>.
    /// </summary>
    private void Case8()
    {
        CombatRegisters registers = _context.Registers;
        EngagementAngleView view = _context.View;

        EngagementSpeed.StepTowardHeading(_context.Geometry);            // image@0x0450C

        if (unchecked((short)registers.Word(0xED7A))                    // image@0x04512 cmp / jl
            >= unchecked((short)registers.Word(0xED98)))
        {
            int z = unchecked((int)(registers.Word(0xED4A) | ((uint)registers.Word(0xED4C) << 16)));
            int sentinel = unchecked((int)(registers.Word(0xED83) | ((uint)registers.Word(0xED85) << 16)));
            if (!EngagementNodeLeaves.Above(sentinel, z))               // image@0x0451F..0x0452B
            {
                _context.Census.Case8Nudge++;
                short scale = unchecked((short)(EngagementAngleSteppers.BankSinScale(view) >> 2));
                EngagementAngleSteppers.ElevationStepToward(view, 0x50, scale);   // image@0x04539
            }
        }

        EngagementSpeed.AccumulateFirePosition(_context.Geometry);       // image@0x0453C
        RangeLatchAndSchedule(0x01F4);                                  // image@0x0453F -> jmp 0x4424
    }

    /// <summary>
    /// Case 9, <c>image@0x04548</c> — the LOITER/ALTITUDE-HOLD phase: pick one of three regimes
    /// (above / climb / level), fly it, floor the altitude at the class ceiling, and walk the fire
    /// position toward the sentinel X.
    /// </summary>
    private void Case9(EngagementNodeFrame frame)
    {
        CombatRegisters registers = _context.Registers;
        EngagementAngleView view = _context.View;

        ushort ceilingLow = _context.StaticData.Word(registers.Word(0xED3C) + 0x2C);   // image@0x0454C
        frame.SetWord(0x28, ceilingLow);
        frame.SetWord(0x26, 0);
        int ceiling = unchecked((int)(uint)ceilingLow);

        int z = unchecked((int)(registers.Word(0xED4A) | ((uint)registers.Word(0xED4C) << 16)));
        int sentinelZ = unchecked((int)(registers.Word(0xED85) | ((uint)registers.Word(0xED87) << 16)));

        if (EngagementNodeLeaves.Above(sentinelZ, z))                   // image@0x0455E..0x0456A
        {
            _context.Census.Case9Above++;
            if (unchecked((short)registers.Word(0xED9A)) > 0xFA)        // image@0x0456C jle
            {
                EngagementSpeed.StepToward(_context.Geometry, 0xFA);     // image@0x04577
            }
            else
            {
                EngagementSpeed.StepTowardHeading(_context.Geometry);    // image@0x0457C
            }

            frame.SetWord(                                              // image@0x0457F jbe
                0x2E, registers.Word(0xED47) > 0xC8 ? (ushort)0x0AF0 : (ushort)0);
            frame.SetWord(6, 0);
            frame.SetByte(0x2C, 1);                                     // image@0x04596
        }
        else
        {
            int altitude = unchecked(
                (int)(registers.Word(0xED46) | ((uint)registers.Word(0xED48) << 16)));
            if (EngagementNodeLeaves.Above(altitude, ceiling))          // image@0x045A2..0x045AE
            {
                _context.Census.Case9Climb++;
                frame.SetWord(0x2E, 0x0B18);                            // image@0x045B0
                frame.SetWord(6, 0x28);
                EngagementSpeed.StepToward(_context.Geometry, 0x96);     // image@0x045BD
                frame.SetByte(0x2C, 1);                                 // image@0x04596
            }
            else
            {
                _context.Census.Case9Level++;
                frame.SetWord(0x2E, 0);                                 // image@0x045C4
                frame.SetWord(6, 0);
                registers.SetWord(                                      // image@0x045CA
                    0xEDA0, unchecked((ushort)(registers.Word(0xEDA0) + 0x28)));
                EngagementSpeed.StepTowardZero(_context.Geometry);       // image@0x045CF
                frame.SetByte(0x2C, 0);                                 // image@0x045D2
            }
        }

        // ── the shared flight block image@0x045D6 ──────────────────────────────────────────────
        EngagementAngleSteppers.ElevationStepToward(                    // image@0x045E0
            view, frame.Signed(6), unchecked((short)(EngagementAngleSteppers.BankSinScale(view) >> 1)));
        SwapWords(registers, 0xED50, 0xED89);                           // image@0x045E3
        EngagementAngleSteppers.ElevationStepToward(                    // image@0x04600
            view, frame.Signed(0x2E), EngagementAngleSteppers.BankSinScale(view));

        short baseStep = EngagementAngleSteppers.BankScaleBaseStep(view);   // image@0x04603
        registers.SetWord(                                              // image@0x0460D / image@0x04610
            0xED4E,
            unchecked((ushort)AngleStep.Toward(
                unchecked((short)registers.Word(0xED4E)), 0, baseStep)));
        EngagementAngleSteppers.BankStepToward(                         // image@0x04619
            view, 0, unchecked((short)registers.Word(0xED90)));
        EngagementSpeed.AccumulateFirePosition(_context.Geometry);       // image@0x0461C
        SwapWords(registers, 0xED50, 0xED89);                           // image@0x0461F

        // ── the altitude FLOOR image@0x04634 ───────────────────────────────────────────────────
        int currentAltitude = unchecked(
            (int)(registers.Word(0xED46) | ((uint)registers.Word(0xED48) << 16)));
        if (!EngagementNodeLeaves.GreaterOrEqual(currentAltitude, ceiling))   // jg / jl / jae
        {
            registers.SetWord(0xED46, ceilingLow);                      // image@0x04648
            registers.SetWord(0xED48, 0);
            if (frame.Byte(0x2C) != 0                                   // image@0x0464F je
                && EngagementNodeLeaves.IsPlayerSelectedView(_context)) // image@0x04655
            {
                _context.Census.Case9FloorCue++;
                _context.Events.PlayAltitudeClampTone();                // image@0x0465C
            }
        }

        // ── the sentinel-X walk image@0x04661 ──────────────────────────────────────────────────
        Case9WalkTowardSentinelX();

        // ── image@0x046F8: the arc accumulator decides which schedule the arm takes ────────────
        bool arcZero = (registers.Word(0xED7B) | registers.Word(0xED79)) == 0;
        if (arcZero)
        {
            LatchArrived();                                             // image@0x04704 -> 0x04429
        }

        registers.SetWord(0xEDAC, 2);                                   // image@0x041F1
    }

    /// <summary>
    /// Case 9's fire-position walk, <c>image@0x04661..0x046F5</c>: step the fire position's X by
    /// <c>±(0x32 · dt) &lt;&lt; 8</c> toward the sentinel X and SNAP on overshoot.
    /// </summary>
    private void Case9WalkTowardSentinelX()
    {
        CombatRegisters registers = _context.Registers;
        int sentinel = unchecked((int)(registers.Word(0xED81) | ((uint)registers.Word(0xED83) << 16)));
        int x = unchecked((int)(registers.Word(0xED42) | ((uint)registers.Word(0xED44) << 16)));

        bool snap;
        if (EngagementNodeLeaves.Above(sentinel, x))                    // image@0x04668..0x04674
        {
            int step = Step32();                                        // image@0x04676
            x = unchecked(x + step);                                    // image@0x0468B add/adc
            SetFirePositionX(registers, x);
            snap = !EngagementNodeLeaves.GreaterOrEqual(sentinel, x);    // image@0x0469A..0x046A8
        }
        else if (!EngagementNodeLeaves.GreaterOrEqual(sentinel, x))     // image@0x046AA..0x046B6
        {
            int step = Step32();                                        // image@0x046B8
            x = unchecked(x - step);                                    // image@0x046CD sub/sbb
            SetFirePositionX(registers, x);
            snap = EngagementNodeLeaves.Above(sentinel, x);              // image@0x046DC..0x046E8
        }
        else
        {
            return;                                                     // sentinel == x, nothing to do
        }

        if (snap)
        {
            registers.SetWord(0xED42, registers.Word(0xED81));          // image@0x046EA
            registers.SetWord(0xED44, registers.Word(0xED83));
        }
    }

    /// <summary>
    /// Case A, <c>image@0x04432</c> — the SCRIPT-ATTACHED phase: copy a named object's pose into
    /// the engagement, or fall back to the interpreter and re-dispatch.
    /// </summary>
    /// <returns><c>true</c> when the arm asks for a re-dispatch (<c>image@0x04471</c>).</returns>
    private bool CaseA(EngagementNodeFrame frame)
    {
        CombatRegisters registers = _context.Registers;
        PoolArena arena = _context.Arena;

        ushort anchor = registers.Word(0xED81);                         // image@0x04437
        ushort segment = registers.PoolSegment;                         // image@0x04432
        frame.SetWord(0x2C, anchor);                                    // image@0x0443D
        frame.SetWord(0x2A, segment);

        ushort pose = arena.Word((ushort)(anchor + 2));                 // image@0x04443
        frame.SetWord(0x28, pose);                                      // image@0x04447
        frame.SetWord(0x26, segment);

        bool copy = (arena.Word((ushort)(pose + 2)) & 1) != 0;          // image@0x0444F test / je
        if (copy)
        {
            byte phase = arena.Byte((ushort)(anchor + 0x0D));           // image@0x04459
            copy = (_context.StaticData.Byte(PhaseAttributeTable + phase) & 2) != 0;   // image@0x0445F
        }

        if (!copy)
        {
            _context.Census.CaseAInterpreter++;
            registers.SetWord(0xED76, 0xFFFF);                          // image@0x04466
            _context.Interpreter.Run(registers, arena, 0);              // image@0x0446E
            return true;                                                // image@0x04471 jmp 0x41c9
        }

        _context.Census.CaseAPose++;

        // image@0x04474..0x044C7: the pose object's three positions, each OFFSET by the sentinel
        // triple promoted to Q8.8.
        SetFirePosition(
            registers,
            0xED42,
            unchecked(PromoteShl8(registers, 0xED83)
                + EngagementNodeLeaves.ReadInt32(arena, (ushort)(pose + 0x06))));
        SetFirePosition(
            registers,
            0xED46,
            unchecked(PromoteShl8(registers, 0xED85)
                + EngagementNodeLeaves.ReadInt32(arena, (ushort)(pose + 0x0A))));
        SetFirePosition(
            registers,
            0xED4A,
            unchecked(PromoteShl8(registers, 0xED87)
                + EngagementNodeLeaves.ReadInt32(arena, (ushort)(pose + 0x0E))));

        registers.SetWord(0xED4E, arena.Word((ushort)(pose + 0x12)));   // image@0x044C8
        registers.SetWord(0xED50, arena.Word((ushort)(pose + 0x14)));   // image@0x044CF
        registers.SetWord(0xED52, arena.Word((ushort)(pose + 0x16)));   // image@0x044D6

        registers.SetWord(0xED79, arena.Word((ushort)(anchor + 0x25))); // image@0x044E0
        registers.SetWord(0xED7B, arena.Word((ushort)(anchor + 0x27)));
        registers.SetWord(0xEDAC, 2);                                   // image@0x044EF

        ushort remaining = unchecked(
            (ushort)(arena.Word((ushort)(anchor + 0x0B)) - registers.Word(0xF0C8)));   // image@0x044F5
        frame.SetWord(8, remaining);
        if (remaining < 2)                                              // image@0x04500 jb
        {
            registers.SetWord(0xEDAC, remaining);                       // image@0x041E6
        }

        return false;
    }

    /// <summary>Case B, <c>image@0x041EC</c> — one manoeuvring step with <c>AL = 1</c>.</summary>
    private void CaseB()
    {
        _context.Census.CaseBAngleUpdate++;
        EngagementSlotAngleUpdate.Step(_context.Geometry, 1);           // image@0x041EE
        _context.Registers.SetWord(0xEDAC, 2);                          // image@0x041F1
    }

    /// <summary>
    /// Case D, <c>image@0x041FA</c> — BALLISTIC drift: advance the fire position by the sentinel
    /// velocity triple scaled by dt, and emit a smoke trail on a timer.
    /// </summary>
    private void CaseD()
    {
        CombatRegisters registers = _context.Registers;
        short dt = unchecked((short)registers.Word(0xEDAE));

        AddFirePosition(registers, 0xED42, Fixed.IMul16(unchecked((short)registers.Word(0xED81)), dt));
        AddFirePosition(registers, 0xED46, Fixed.IMul16(unchecked((short)registers.Word(0xED83)), dt));
        AddFirePosition(registers, 0xED4A, Fixed.IMul16(unchecked((short)registers.Word(0xED85)), dt));

        ushort master = registers.Word(0xF0C8);
        if (registers.Word(0xED88) <= master                            // image@0x04239 ja
            && registers.Byte(0xED87) != 0)                             // image@0x0423F je
        {
            _context.Census.CaseDSmoke++;
            _context.Events.AllocateSmokeSlot(_context.View.FirePosition, 3);   // image@0x0424F
            registers.SetWord(                                          // image@0x04257
                0xED88, unchecked((ushort)(registers.Byte(0xED87) + master)));
        }

        registers.SetWord(0xEDAC, 3);                                   // image@0x04263
    }

    // ═══════════════════════════════════════════════════════════════════════════ the epilogue

    /// <summary>
    /// §F1..§F5, <c>image@0x0476E..0x048B7</c>.
    /// </summary>
    private void Epilogue(EngagementNodeFrame frame)
    {
        CombatRegisters registers = _context.Registers;

        // ── §F1 image@0x0476E: two i32 sign clamps and the arc-accumulator altitude cap ────────
        if (unchecked((short)registers.Word(0xED48)) < 0)               // jge
        {
            _context.Census.ClampZ++;
            registers.SetWord(0xED48, 0);                               // image@0x04777
            registers.SetWord(0xED46, 0);
        }

        if (unchecked((short)registers.Word(0xED7B)) < 0)               // jge
        {
            _context.Census.ClampArc++;
            registers.SetWord(0xED7B, 0);                               // image@0x04786
            registers.SetWord(0xED79, 0);
        }

        byte attribute = PhaseAttribute();                              // image@0x0478C
        if ((attribute & 0x10) != 0)                                    // image@0x04792 test / jne
        {
            _context.Census.AltitudeCapSkipped++;
        }
        else
        {
            short floor = unchecked((short)registers.Word(0xED98));     // image@0x04799
            if (unchecked((short)registers.Word(0xED7A)) < floor)       // image@0x0479C jge
            {
                _context.Census.AltitudeCap++;
                int promoted = unchecked(floor << 8);                   // image@0x047A2 the shuffle
                registers.SetWord(0xED79, unchecked((ushort)promoted));
                registers.SetWord(0xED7B, unchecked((ushort)(promoted >> 16)));
            }
        }

        EngagementNodeLeaves.CloseRangeProximityKillCheck(_context);    // image@0x047B2

        // ── §F2 image@0x047B5: the acquisition gate ────────────────────────────────────────────
        frame.AcquisitionStateAtGate = registers.Byte(0xED65);          // image@0x047B8
        attribute = PhaseAttribute();                                   // image@0x047BB

        bool runAcquisition = (attribute & 1) != 0;                     // image@0x047C1 test / je
        if (runAcquisition && unchecked((short)registers.Word(0xED76)) != -1)   // image@0x047C8 je
        {
            runAcquisition = (registers.Byte(0xED78) & 0x10) != 0;      // image@0x047CF test / je
        }

        if (runAcquisition)
        {
            _context.Census.AcqFsmRan++;
            _context.Acquisition.Step(_context);                        // image@0x047D6
        }
        else
        {
            // ── RÉGIME ARM (ii) image@0x047DC ────────────────────────────────────────────────
            _context.Census.SkipAcqArm++;
            if (registers.Byte(0xED65) >= 2)                            // jb 0x47f6 (UNSIGNED)
            {
                _context.Census.SkipAcqCleared++;
                EngagementNodeLeaves.PlayerPressureDecrement(_context); // image@0x047E8
                registers.SetByte(0xED65, 0);                           // image@0x047EB
                registers.SetWord(0xED6F, 0);                           // image@0x047F0
            }
        }

        // ── §F3 image@0x047F6: the four-arm shot-class chain ───────────────────────────────────
        int shotClass = -1;
        if (frame.AcquisitionStateAtGate < 2 && registers.Byte(0xED65) >= 2)   // jae / jb
        {
            shotClass = 2;                                              // image@0x04803
            _context.Census.ShotClass[0]++;
        }
        else if (registers.Word(0xED62) <= registers.Word(0xF0C8))      // image@0x0480B ja
        {
            shotClass = 0;                                              // image@0x04811
            _context.Census.ShotClass[1]++;
        }
        else if ((registers.Byte(0xED59) & 4) != 0                      // image@0x04816 je
            && registers.Word(0xED62) == 0xFFFE)                        // image@0x0481D jne
        {
            shotClass = 1;                                              // image@0x04824
            _context.Census.ShotClass[2]++;
        }
        else if (registers.Byte(0x0F0C) != 0)                           // image@0x04828 je
        {
            shotClass = 8;                                              // image@0x0482F
            _context.Census.ShotClass[3]++;
        }
        else
        {
            _context.Census.ShotClass[4]++;
        }

        if (shotClass >= 0)
        {
            _context.Interpreter.Run(registers, _context.Arena, (byte)shotClass);   // image@0x04831
        }

        // ── §F4 image@0x04834: the five-conjunct low-altitude ground impact ────────────────────
        if (LowAltitudeSpawnGate())
        {
            _context.Census.LowAltitudeSpawn++;
            EngagementNodeLeaves.SlotImpactAndDepart(_context);         // image@0x04863
            EngagementNodeLeaves.KillTallyAndSlotDispatch(_context);    // image@0x04866
        }

        // ── §F5 image@0x0486B: the last-pass stamp and the next-pass schedule ──────────────────
        registers.SetWord(0xED5D, registers.Word(0xF0D2));              // image@0x0486E

        bool unadjusted = (registers.Word(0xED3E) & 0x8008) != 0;       // image@0x04871 test / jne
        if (!unadjusted)
        {
            ushort prototype = registers.Word(0xED54);                  // image@0x04879
            bool ownerCheck = (_context.StaticData.Byte(prototype + 0x0C) & 3) != 0;   // jne 0x488a
            if (!ownerCheck)
            {
                if ((registers.Byte(0xED3F) & 0x40) != 0)               // image@0x04883 jne
                {
                    unadjusted = true;
                }
                else
                {
                    ownerCheck = true;
                }
            }

            if (!unadjusted && ownerCheck && registers.Word(0xED56) == registers.Word(0x00BE))
            {
                unadjusted = true;                                      // image@0x0488D jne
            }
        }

        ushort next;
        if (unadjusted)
        {
            next = registers.Word(0xF0C8);                              // image@0x04893
        }
        else
        {
            _context.Census.ScheduleAdjusted++;
            ushort sleep = registers.Word(0xEDAC);

            // The play-profile sleep cap (EngagementNodeContext.SleepCapSeconds): the node comes back
            // sooner than the arm asked, so the aircraft is moved in ≤ cap-second steps instead of
            // the original's 2–4 s ones.  [0xEDAC] itself is left as the arm wrote it.  -1 is the
            // original's law.
            int cap = _context.SleepCapSeconds;
            if (cap >= 0 && sleep > cap)
            {
                _context.Census.SleepCapped++;
                sleep = (ushort)cap;
            }

            next = unchecked((ushort)(sleep + registers.Word(0xF0C8)));
            frame.SetWord(0x2C, next);                                  // image@0x048A1 DEAD STORE
            frame.SetWord(0x2A, 0);                                     // image@0x048A4 DEAD STORE
            if (next > 0x1FFC)                                          // image@0x048A7 jbe
            {
                _context.Census.ScheduleClamped++;
                next = 0x1FFC;                                          // image@0x048AC
            }
        }

        registers.SetWord(0xED5F, next);                                // image@0x048AF
    }

    /// <summary>§F4's five conjuncts, <c>image@0x04834..0x04861</c>.</summary>
    private bool LowAltitudeSpawnGate()
    {
        CombatRegisters registers = _context.Registers;

        short high = unchecked((short)registers.Word(0xED48));
        if (high > 0)                                                   // image@0x04839 jg
        {
            return false;
        }

        if (high == 0 && registers.Word(0xED46) > 0x2800)               // image@0x0483D ja
        {
            return false;
        }

        if ((registers.Byte(0xED59) & 1) == 0)                          // image@0x04845 je
        {
            return false;
        }

        ushort prototype = registers.Word(0xED54);                      // image@0x0484C
        if ((_context.StaticData.Byte(prototype + 0x0C) & 3) != 0)      // image@0x04850 jne
        {
            return false;
        }

        return (PhaseAttribute() & 8) == 0;                             // image@0x0485C jne
    }

    // ═══════════════════════════════════════════════════════════════════════════════ helpers

    /// <summary>The current phase's attribute byte.</summary>
    private byte PhaseAttribute() =>
        _context.StaticData.Byte(PhaseAttributeTable + _context.Registers.Byte(0xED61));

    /// <summary>
    /// <c>image@0x042A3</c> / <c>image@0x04429</c> — set <c>[0xED59]</c> bit2, the "arrived / in
    /// range" latch the manoeuvring engine and §F3 both read.
    /// </summary>
    private void LatchArrived() =>
        _context.Registers.SetByte(0xED59, (byte)(_context.Registers.Byte(0xED59) | 4));

    /// <summary>Cases 7 and 8's shared tail, <c>image@0x0441E</c> / <c>image@0x04424</c>.</summary>
    private void RangeLatchAndSchedule(ushort threshold)
    {
        // [0xED47] is a DELIBERATELY UNALIGNED word — the fire position's ALTITUDE shifted right 8
        // — and the compare is UNSIGNED (`jae`).
        if (_context.Registers.Word(0xED47) >= threshold)
        {
            _context.Census.Case7RangeLatch++;
            LatchArrived();                                             // image@0x04429
        }

        _context.Registers.SetWord(0xEDAC, 2);                          // image@0x041F1
    }

    /// <summary>The 3-XOR swap of <c>[0xED81]</c> and <c>[0xED6F]</c> (<c>image@0x042AC</c>).</summary>
    private void SwapTargetAndSentinel() => SwapWords(_context.Registers, 0xED81, 0xED6F);

    private static void SwapWords(CombatRegisters registers, int a, int b)
    {
        ushort va = registers.Word(a);
        ushort vb = registers.Word(b);
        registers.SetWord(a, vb);
        registers.SetWord(b, va);
    }

    /// <summary>The <c>cwd / mov dh,dl / mov dl,ah / mov ah,al / sub al,al</c> Q8.8 promote.</summary>
    private static int PromoteShl8(CombatRegisters registers, int dgroupOffset) =>
        unchecked((int)(short)registers.Word(dgroupOffset) << 8);

    private int SnapshotInt32(int dgroupOffset) => _context.View.Int32(dgroupOffset);

    private static void SetFirePosition(CombatRegisters registers, int dgroupOffset, int value)
    {
        registers.SetWord(dgroupOffset, unchecked((ushort)value));
        registers.SetWord(dgroupOffset + 2, unchecked((ushort)(value >> 16)));
    }

    private static void SetFirePositionX(CombatRegisters registers, int value) =>
        SetFirePosition(registers, 0xED42, value);

    private static void AddFirePosition(CombatRegisters registers, int dgroupOffset, int delta)
    {
        int current = unchecked(
            (int)(registers.Word(dgroupOffset) | ((uint)registers.Word(dgroupOffset + 2) << 16)));
        SetFirePosition(registers, dgroupOffset, unchecked(current + delta));
    }

    /// <summary>Case 9's <c>0x32 · dt</c> step, promoted to Q8.8 (<c>image@0x04676</c>).</summary>
    private int Step32() =>
        unchecked(
            (int)Fixed.MulDiv16SignedShr8(0x32, unchecked((short)_context.Registers.Word(0xEDAE)))
            << 8);
}
