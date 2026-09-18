namespace CYAC.Port.Core.Sim.Combat.Vm;

/// <summary>
/// One landing-zone record — the 16-byte entry <c>landing_zone_entry_add @image@0x090AE</c> appends
/// to the table at <c>[0xB616]</c> (count in <c>g_landing_zone_count [0xB614]</c>).
/// </summary>
/// <param name="CenterX">
/// <c>+0x00</c> i32 — the pair the proximity scan compares against <c>[0xED42]</c>
/// (<c>image@0x091E8</c>), i.e. the zone centre's X.  Written from the caller's LAST two pushed
/// words (<c>[bp+0xA]/[bp+0xC]</c>, <c>image@0x090C3</c>).
/// </param>
/// <param name="CenterZ">
/// <c>+0x08</c> i32 — compared against <c>[0xED4A]</c> (<c>image@0x091EA</c>), i.e. Z.  Written from
/// <c>[bp+6]/[bp+8]</c> (<c>image@0x090D6</c>).  This is the coordinate the generated program's
/// offsets are derived from.
/// </param>
/// <param name="ScriptSegment"><c>+0x0C</c> — the 50-byte program block's segment (<c>image@0x090F0</c>).</param>
/// <param name="Attributes">
/// <c>+0x0E</c> — the caller's first pushed word (<c>[bp+4]</c>, <c>image@0x090E2</c>).  Bit 6 is the
/// class the proximity scan filters on (<c>test byte ptr [si+0xe],0x40</c> @<c>image@0x091BF</c>).
/// </param>
/// <remarks>
/// <c>+0x04</c> and <c>+0x06</c> are explicitly ZEROED (<c>image@0x090CE..0x090D3</c>) — the record's
/// only unexplained words, and the reason the scan's distance callee reads only two of the three
/// axes.
/// </remarks>
public readonly record struct LandingZoneEntry(
    int CenterX, int CenterZ, ushort ScriptSegment, ushort Attributes);

/// <summary>
/// A named place — one 12-byte record of the table at <c>g_named_place_table [0xEE74]</c> the
/// authored-script patcher resolves symbolic locations from.
/// </summary>
/// <param name="X"><c>+0x00</c> i32.</param>
/// <param name="Y"><c>+0x04</c> i32.</param>
/// <param name="Z"><c>+0x08</c> i32.</param>
public readonly record struct NamedPlace(int X, int Y, int Z);

/// <summary>
/// The mission-LOAD half of the AI-script pipeline: the per-zone program COMPILER
/// (<c>landing_zone_entry_add @image@0x090AE</c>), the nearest-zone scan the interpreter's copy-in
/// arm uses (<c>landing_zone_proximity_scan @image@0x0919F</c> +
/// <c>landing_zone_bbox_distance @image@0x08FAF</c>), and the authored-script named-place patcher
/// (<c>ai_script_named_place_patch @image@0x094EF</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two program sources, one interpreter.</b>  A node's program buffer
/// (<c>s_engagement_state +0x20 script_seg</c>) is filled either by this generator — a five-instruction
/// navigation program compiled from a zone's centre, copied in at <c>image@0x0587D</c> — or by
/// <c>wld_or_s_asset_parser</c>'s tag-<c>0x89</c> handler, which heap-allocates
/// <c>max(N, 50)</c> bytes (<c>cmp ax,0x32 / jge / mov ax,0x32</c> @<c>image@0x09D4D</c>), copies the
/// authored bytes in, runs them through <see cref="Patch"/>, and writes the segment and <c>PC = 0</c>
/// straight into the object's engagement overlay (<c>image@0x0A22F</c>/<c>0x0A236</c>).
/// </para>
/// <para>
/// <b>Law L1.</b>  Nothing here holds original data: the generator is a FORMULA, the patcher is a
/// rewrite rule, and both take their coordinates from the caller.
/// </para>
/// </remarks>
public static class ZoneProgramGenerator
{
    /// <summary><c>g_landing_zone_count [0xB614]</c>.</summary>
    public const int ZoneCountOffset = 0xB614;

    /// <summary>The zone array's base — <c>[0xB616]</c>, stride 16 (<c>shl si,4</c> @<c>image@0x090B9</c>).</summary>
    public const int ZoneArrayOffset = 0xB616;

    /// <summary>Bytes per zone record.</summary>
    public const int ZoneRecordBytes = 16;

    /// <summary>
    /// The scan's walk base — <c>0xB606</c>, TEN bytes below the array (<c>add si,0xb606</c>
    /// @<c>image@0x091B9</c> vs <c>add si,0xb616</c> @<c>image@0x090BB</c>). With <c>si = 0xB606 +
    /// 16·count</c> and the loop test <c>si &gt;= 0xB616</c>, the scan visits entries <c>count-1 …
    /// 0</c> — the same records, walked BACKWARDS.
    /// </summary>
    public const int ScanBaseOffset = 0xB606;

    /// <summary>
    /// <c>g_landing_zone_capture_radius [0x9E74]</c> — the i32 the distance leaf takes 1.5× of.
    /// </summary>
    public const int CaptureRadiusOffset = 0x9E74;

    /// <summary>The generated program's length in bytes: 12 + 12 + 9 + 3 + 3 + 3.</summary>
    public const int GeneratedProgramBytes = 42;

    /// <summary>The altitude composite both <c>SET_ORIENTATION_TARGETS</c> instructions carry.</summary>
    /// <remarks><c>mov ax,0xd000 / mov dx,7</c> @<c>image@0x0910E</c> and <c>image@0x09148</c>.</remarks>
    public const int AltitudeComposite = 0x0007D000;

    /// <summary>
    /// The FIRST offset subtracted from the zone's Z: <c>0x000AF000</c>
    /// (<c>sub ax,0xf000 / sbb dx,0xa</c> @<c>image@0x0911D</c>).  Every other offset is derived
    /// from this one.
    /// </summary>
    public const int OffsetA = 0x000AF000;

    /// <summary>
    /// Instruction 1's third composite is <c>Z − OffsetA − 0x00753000</c>
    /// (<c>sub ax,0x3000 / sbb dx,0x75</c> @<c>image@0x09127</c>).
    /// </summary>
    public const int OffsetAToB = 0x00753000;

    /// <summary>
    /// Instruction 2's third composite is <c>Z − OffsetA − 0x00271000</c>
    /// (<c>sub ax,0x1000 / sbb dx,0x27</c> @<c>image@0x09155</c>).
    /// </summary>
    public const int OffsetAToC = 0x00271000;

    /// <summary>
    /// Instruction 3's second composite is <c>Z − OffsetA − 0x0007D000</c>
    /// (<c>sub ax,0xd000 / sbb dx,7</c> @<c>image@0x09176</c>).
    /// </summary>
    public const int OffsetAToD = 0x0007D000;

    /// <summary>Instruction 4's delay word: <c>0x00B4</c> (<c>mov ax,0xb4</c> @<c>image@0x0917F</c>).</summary>
    public const ushort IdleDelay = 0x00B4;

    /// <summary>
    /// Compiles a zone's five-instruction navigation program into <paramref name="program"/>.
    /// </summary>
    /// <param name="program">At least <see cref="GeneratedProgramBytes"/> bytes.</param>
    /// <param name="centerX">The zone's <c>+0x00</c> coordinate.</param>
    /// <param name="centerZ">The zone's <c>+0x08</c> coordinate.</param>
    /// <returns>How many bytes were written — always <see cref="GeneratedProgramBytes"/>.</returns>
    /// <remarks>
    /// <para>
    /// The emitted opcodes are <c>02 02 09 00 DA FF</c>
    /// The three composite operands per
    /// <c>0x02</c> are (zone X, a fixed altitude, a Z offset); <c>0x09</c> takes (zone X, a Z
    /// offset).  All five differ ONLY in the Z offset, which is what makes the program a
    /// three-waypoint approach.
    /// </para>
    /// <para>
    /// <b>Report-only defect in</b> (`build_generated_program`, its <c>--selftest</c>): it gives
    /// instruction 1 the offset <c>coord1 − 0x000AF000</c> and instruction 2 <c>coord1 −
    /// 0x00802000</c>, and its instruction 4 delay is <c>0xB400</c>. The bytes say instruction 1
    /// carries <c>Z − 0x00802000</c>, instruction 2 carries <c>Z − 0x00320000</c> (a constant the
    /// script does not have at all), and the delay is <c>0x00B4</c>.  Only the opcode sequence and
    /// the 42-byte length were right.
    /// </para>
    /// </remarks>
    public static int WriteGeneratedProgram(Span<byte> program, int centerX, int centerZ)
    {
        int offsetA = unchecked(centerZ - OffsetA);                  // image@0x0911D
        int offsetB = unchecked(offsetA - OffsetAToB);               // image@0x09127
        int offsetC = unchecked(offsetA - OffsetAToC);               // image@0x09155
        int offsetD = unchecked(offsetA - OffsetAToD);               // image@0x09176

        int cursor = 0;
        WriteWord(program, ref cursor, 0xFFFE);                      // image@0x090FA
        WriteByte(program, ref cursor, AiScriptOpcode.SetOrientationTargets);
        Write3(program, ref cursor, centerX);                        // image@0x09105
        Write3(program, ref cursor, AltitudeComposite);              // image@0x0910E
        Write3(program, ref cursor, offsetB);                        // image@0x09131

        WriteWord(program, ref cursor, 0xFFFE);                      // image@0x09134
        WriteByte(program, ref cursor, AiScriptOpcode.SetOrientationTargets);
        Write3(program, ref cursor, centerX);                        // image@0x0913F
        Write3(program, ref cursor, AltitudeComposite);              // image@0x09148
        Write3(program, ref cursor, offsetC);                        // image@0x0915B

        WriteWord(program, ref cursor, 0xFFFE);                      // image@0x0915E
        WriteByte(program, ref cursor, AiScriptOpcode.SetHeadingTargets);
        Write3(program, ref cursor, centerX);                        // image@0x09169
        Write3(program, ref cursor, offsetD);                        // image@0x0917C

        WriteWord(program, ref cursor, IdleDelay);                   // image@0x0917F
        WriteByte(program, ref cursor, AiScriptOpcode.SetPhaseIdle);  // image@0x09185

        WriteWord(program, ref cursor, 0);                           // image@0x0918A
        WriteByte(program, ref cursor, AiScriptOpcode.RetargetSlot);  // image@0x0918F

        WriteWord(program, ref cursor, 0);                           // image@0x09194 emit_eof
        WriteByte(program, ref cursor, AiScriptOpcode.Suspend);

        return cursor;
    }

    /// <summary>
    /// <c>landing_zone_entry_add @image@0x090AE</c> — appends a zone record, allocates its 50-byte
    /// program block and compiles the program into it through the real write cursor.
    /// </summary>
    /// <param name="registers">The combat register file.</param>
    /// <param name="heap">The script heap.</param>
    /// <param name="centerX">The zone centre's <c>+0x00</c> coordinate.</param>
    /// <param name="centerZ">Its <c>+0x08</c> coordinate.</param>
    /// <param name="attributes">The <c>+0x0E</c> word.</param>
    /// <returns>The new record.</returns>
    public static LandingZoneEntry AddZone(
        CombatRegisters registers,
        IAiScriptHeap heap,
        int centerX,
        int centerZ,
        ushort attributes)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(heap);

        ushort index = registers.Word(ZoneCountOffset);              // image@0x090B5
        int record = ZoneArrayOffset + (index * ZoneRecordBytes);
        registers.SetWord(ZoneCountOffset, (ushort)(index + 1));      // image@0x090BF inc

        WriteI32(registers, record + 0x00, centerX);                  // image@0x090C9
        registers.SetWord(record + 0x04, 0);                          // image@0x090D3
        registers.SetWord(record + 0x06, 0);                          // image@0x090D0
        WriteI32(registers, record + 0x08, centerZ);                  // image@0x090DC
        registers.SetWord(record + 0x0E, attributes);                 // image@0x090E5

        ushort segment = heap.Allocate(AiScriptMemory.ProgramBufferBytes);   // image@0x090E8
        registers.SetWord(record + 0x0C, segment);                    // image@0x090F0
        AiScriptMemory.CursorInit(registers, 0, segment);             // image@0x090F7

        Span<byte> staging = stackalloc byte[GeneratedProgramBytes];
        WriteGeneratedProgram(staging, centerX, centerZ);
        foreach (byte value in staging)
        {
            AiScriptMemory.WriteByte(registers, heap, value);
        }

        return new LandingZoneEntry(centerX, centerZ, segment, attributes);
    }

    /// <summary>
    /// <c>landing_zone_bbox_distance @image@0x08FAF</c> — the CHEBYSHEV (square) proximity of a
    /// point to a zone centre: per axis, <c>max(0, |Δhigh| − threshold)</c>, summed.
    /// </summary>
    /// <param name="captureRadius">
    /// <c>g_landing_zone_capture_radius [0x9E74]</c>; the threshold is the HIGH word of 1.5× it
    /// (<c>sar dx,1 / rcr ax,1</c> then <c>add/adc</c> @<c>image@0x08FBE..0x08FE6</c>).
    /// </param>
    /// <param name="pointX">The point's X (the caller pushes its high half; see remarks).</param>
    /// <param name="pointZ">The point's Z.</param>
    /// <param name="zoneX">The zone centre's X.</param>
    /// <param name="zoneZ">The zone centre's Z.</param>
    /// <returns>0 when the point is inside the zone; a positive excess otherwise.</returns>
    /// <remarks>
    /// Only the HIGH words take part: the callee's operands are the two i32 pairs and it works on
    /// their high halves throughout (P335 — "bbox" is that pilot's misnomer, it is a square).
    /// Two axes only: the zone record's <c>+0x04</c> pair is the zeroed one and is never read.
    /// </remarks>
    public static int BboxDistance(
        int captureRadius, int pointX, int pointZ, int zoneX, int zoneZ)
    {
        int threshold = unchecked((int)((uint)(captureRadius + (captureRadius >> 1)) >> 16));
        int excessZ = Excess(High(pointZ) - High(zoneZ), threshold);
        int excessX = Excess(High(pointX) - High(zoneX), threshold);
        return excessZ + excessX;

        static int High(int value) => (short)(value >> 16);
        static int Excess(int delta, int limit)
        {
            int magnitude = delta < 0 ? -delta : delta;
            int excess = magnitude - limit;
            return excess < 0 ? 0 : excess;
        }
    }

    /// <summary>
    /// <c>landing_zone_proximity_scan @image@0x0919F</c> — the nearest ELIGIBLE zone to a point.
    /// </summary>
    /// <param name="registers">The combat register file (the zone table and the capture radius).</param>
    /// <param name="pointX">The probe point's X — the interpreter passes <c>[0xED42]</c>.</param>
    /// <param name="pointZ">Its Z — <c>[0xED4A]</c>.</param>
    /// <param name="acceptAttributed">
    /// The original's <c>[bp+6]</c>: a zone whose <c>+0x0E</c> has bit 6 SET is eligible only when
    /// this is non-zero (<c>image@0x091BF..0x091C9</c>).
    /// </param>
    /// <param name="acceptPlain">
    /// The original's <c>[bp+8]</c>: the same for a zone WITHOUT bit 6 (<c>image@0x091CD</c>).
    /// </param>
    /// <param name="bestDistance">The winning distance, or <c>0x7FFF</c> when nothing qualified.</param>
    /// <returns>
    /// The winning record's DGROUP offset (the original returns the near pointer in <c>AX</c>), or 0.
    /// </returns>
    /// <remarks>
    /// The walk is BACKWARDS (<c>sub si,0x10</c> @<c>image@0x09202</c>) from the last record, and the
    /// comparison is <c>jge → keep the incumbent</c>, so among equal distances the LOWEST-INDEXED
    /// zone wins.  The seed is <c>0x7FFF</c> (<c>image@0x091AC</c>), so a distance of exactly
    /// <c>0x7FFF</c> never displaces "nothing".
    /// </remarks>
    public static int ProximityScan(
        CombatRegisters registers,
        int pointX,
        int pointZ,
        bool acceptAttributed,
        bool acceptPlain,
        out short bestDistance)
    {
        ArgumentNullException.ThrowIfNull(registers);

        int best = 0;                                                 // image@0x091A7 [bp-4] = 0
        short bestSoFar = 0x7FFF;                                     // image@0x091AC [bp-2]
        int captureRadius = ReadI32(registers, CaptureRadiusOffset);

        int count = registers.Word(ZoneCountOffset);
        for (int record = ScanBaseOffset + (count * ZoneRecordBytes);
             record >= ZoneArrayOffset;
             record -= ZoneRecordBytes)
        {
            bool attributed = (registers.Byte(record + 0x0E) & 0x40) != 0;   // image@0x091BF
            if (attributed ? !acceptAttributed : !acceptPlain)
            {
                continue;
            }

            short distance = (short)BboxDistance(                     // image@0x091F0
                captureRadius,
                pointX,
                pointZ,
                ReadI32(registers, record + 0x00),
                ReadI32(registers, record + 0x08));

            if (distance < bestSoFar)                                 // image@0x091F7 jge → keep
            {
                best = record;
                bestSoFar = distance;
            }
        }

        bestDistance = bestSoFar;
        return best;
    }

    /// <summary>
    /// <c>ai_script_named_place_patch @image@0x094EF</c> — rewrites an authored program's
    /// COMPILE-TIME named-place opcodes into real ones with absolute coordinates.
    /// </summary>
    /// <param name="program">The program bytes, patched IN PLACE.</param>
    /// <param name="length">How many bytes the tag-0x89 record carried.</param>
    /// <param name="places">The <c>[0xEE74]</c> table, 12 bytes per place.</param>
    /// <param name="previousObjectOrigin">
    /// <c>[0xB556]</c> — the previous object's parse-time origin, the source opcode <c>0xFD</c> uses
    /// (<c>mov ax,0xb556</c> @<c>image@0x09652</c>).
    /// </param>
    /// <returns>How many instructions were rewritten.</returns>
    /// <remarks>
    /// <para>
    /// Two ranges, two rewrites.  <c>0xF0..0xFC</c> becomes <c>0x02 SET_ORIENTATION_TARGETS</c> and
    /// its THREE composites get the place's X, Y and Z added
    /// (<c>image@0x094B6..0x094E5</c>, the composites at <c>+1</c>, <c>+4</c>, <c>+7</c>);
    /// <c>0xE3..0xEF</c> becomes <c>0x09 SET_HEADING_TARGETS</c> and its TWO composites get the
    /// place's X and Z (<c>image@0x095FA..0x09623</c>).  The place index is the OPCODE BYTE itself,
    /// never an operand.
    /// </para>
    /// <para>
    /// The add is done on the composite's own i32 (<c>image@0x09481</c> reads the word at
    /// <c>ptr-1</c> and zeroes <c>AL</c> — i.e. exactly <see cref="AiScriptDecoder.Composite"/>) and
    /// stored back as <c>value &gt;&gt; 8</c> LE24, so the authored operands are RELATIVE offsets in
    /// the same wire format the interpreter reads.
    /// </para>
    /// <para>
    /// The walk advances by each opcode's own length, and its length table has one hole: a RAW
    /// <c>0x09</c> is charged 3 bytes (<c>image@0x09526</c>) rather than 9, because after patching
    /// the cursor has already moved past the instruction and a raw <c>0x09</c> is not supposed to
    /// occur in authored source.  Reproduced deliberately.
    /// </para>
    /// </remarks>
    public static int Patch(
        Span<byte> program,
        int length,
        IReadOnlyList<NamedPlace> places,
        NamedPlace previousObjectOrigin = default)
    {
        ArgumentNullException.ThrowIfNull(places);

        int patched = 0;
        int cursor = 0;
        while (length > cursor)                                       // image@0x09502 ja
        {
            int opcodeAt = cursor + 2;                                // image@0x0950A inc si ×2
            if (opcodeAt >= program.Length)
            {
                break;
            }

            byte opcode = program[opcodeAt];
            if (opcode is >= AiScriptOpcode.NamedPlaceOrientFirst and < AiScriptOpcode.NamedPlaceOrientPrev)
            {
                int index = opcode - AiScriptOpcode.NamedPlaceOrientFirst;   // image@0x09598
                PatchOrient(program, opcodeAt, Place(places, index));
                patched++;
                cursor = opcodeAt + 10;                               // image@0x095DD add si,0xa
            }
            else if (opcode == AiScriptOpcode.NamedPlaceOrientPrev)
            {
                PatchOrient(program, opcodeAt, previousObjectOrigin);  // image@0x09651
                patched++;
                cursor = opcodeAt + 10;
            }
            else if (opcode is >= AiScriptOpcode.NamedPlaceHeadingFirst
                             and <= AiScriptOpcode.NamedPlaceHeadingLast)
            {
                int index = opcode - AiScriptOpcode.NamedPlaceHeadingFirst;  // image@0x095F6
                NamedPlace place = Place(places, index);
                program[opcodeAt] = AiScriptOpcode.SetHeadingTargets;  // image@0x095FA
                AddComposite(program, opcodeAt + 1, place.X);         // image@0x09614
                AddComposite(program, opcodeAt + 4, place.Z);         // image@0x09623
                patched++;
                cursor = opcodeAt + 7;                                // image@0x09626 add si,7
            }
            else if (opcode == AiScriptOpcode.PrintString)
            {
                int scan = opcodeAt + 1;                              // image@0x09657 inc si
                while (scan < program.Length && program[scan - 1] != 0)
                {
                    scan++;
                }

                cursor = scan;
            }
            else
            {
                int step = PatchWalkLength(opcode);
                if (step == 0)
                {
                    cursor = opcodeAt;                                // image@0x095E8: rescan
                    if (cursor <= opcodeAt - 2)
                    {
                        break;
                    }
                }
                else
                {
                    cursor = opcodeAt + step;
                }
            }
        }

        return patched;

        static NamedPlace Place(IReadOnlyList<NamedPlace> table, int index) =>
            index >= 0 && index < table.Count ? table[index] : default;
    }

    /// <summary>
    /// How many bytes AFTER the opcode byte the patcher's own walk charges an opcode.
    /// </summary>
    /// <param name="opcode">The opcode.</param>
    /// <returns>The step, or 0 for the "unknown byte" rescan arm.</returns>
    /// <remarks>
    /// This is deliberately SEPARATE from <see cref="AiScriptDecoder.OperandBytes"/>: the two tables
    /// agree everywhere except the raw <c>0x09</c> hole described on <see cref="Patch"/>, and a test
    /// asserts exactly that (<c>image@0x0950C..0x09585</c>).
    /// </remarks>
    public static int PatchWalkLength(byte opcode) => opcode switch
    {
        AiScriptOpcode.SetPhaseIdle => 1,                             // image@0x09514 → 0x0962C
        AiScriptOpcode.SetOrientationTargets => 10,                   // image@0x0951B → 0x095DD
        AiScriptOpcode.SetPhaseDive or AiScriptOpcode.SetHeadingTargets => 1,   // image@0x09526
        AiScriptOpcode.SetPhase0A => 9,                               // image@0x0952C → 0x09630
        AiScriptOpcode.SetPhase0D => 8,                               // image@0x09534 → 0x09645
        AiScriptOpcode.KillActor => 3,                                // image@0x0953C → 0x0964B
        AiScriptOpcode.SpawnActor => 9,                               // image@0x09542 → 0x0963F
        AiScriptOpcode.ClearScriptFlags or AiScriptOpcode.SetScriptFlags
            or AiScriptOpcode.SetHeading => 3,                        // image@0x0954C
        AiScriptOpcode.RetargetSlot or AiScriptOpcode.ClearCounter => 1,        // image@0x09559
        AiScriptOpcode.ResetPc => 1,                                  // image@0x09559
        AiScriptOpcode.FireWeapon => 3,                               // image@0x0955F
        AiScriptOpcode.CallScriptFunction => 3,                       // image@0x0956B
        AiScriptOpcode.SetEngageFlag or AiScriptOpcode.ClearEngageFlag => 1,    // image@0x09574
        AiScriptOpcode.Return or AiScriptOpcode.Suspend => 1,         // image@0x09585
        _ => 0,
    };

    /// <summary>
    /// The one instruction the patcher REWRITES without being a named place: <c>0x0A</c>'s
    /// first word operand is an ACTOR-SLOT index in authored source and becomes <c>[0xEE5A +
    /// 2·idx] + 0x18</c> (<c>image@0x09630..0x0963C</c>).
    /// </summary>
    /// <param name="program">The program bytes.</param>
    /// <param name="opcodeAt">The offset of the <c>0x0A</c> opcode byte.</param>
    /// <param name="actorRecordRef">The word the actor table holds for that index.</param>
    public static void PatchActorReference(Span<byte> program, int opcodeAt, ushort actorRecordRef)
    {
        ushort patched = (ushort)(actorRecordRef + 0x18);             // image@0x09639
        program[opcodeAt + 1] = (byte)patched;
        program[opcodeAt + 2] = (byte)(patched >> 8);
    }

    private static void PatchOrient(Span<byte> program, int opcodeAt, NamedPlace place)
    {
        program[opcodeAt] = AiScriptOpcode.SetOrientationTargets;      // image@0x094B6
        AddComposite(program, opcodeAt + 1, place.X);                 // image@0x094C5
        AddComposite(program, opcodeAt + 4, place.Y);                 // image@0x094D5
        AddComposite(program, opcodeAt + 7, place.Z);                 // image@0x094E5
    }

    /// <summary>
    /// The patcher's add-helper <c>@image@0x09478</c>: read the 3-byte composite as an i32, add a
    /// coordinate, store <c>value &gt;&gt; 8</c> back as LE24.
    /// </summary>
    private static void AddComposite(Span<byte> program, int at, int coordinate)
    {
        if (at + 3 > program.Length)
        {
            return;
        }

        int value = AiScriptDecoder.Composite(program[at], program[at + 1], program[at + 2]);
        uint sum = unchecked((uint)(value + coordinate));
        program[at] = (byte)(sum >> 8);
        program[at + 1] = (byte)(sum >> 16);
        program[at + 2] = (byte)(sum >> 24);
    }

    private static void WriteByte(Span<byte> buffer, ref int cursor, byte value)
    {
        buffer[cursor] = value;
        cursor++;
    }

    private static void WriteWord(Span<byte> buffer, ref int cursor, ushort value)
    {
        buffer[cursor] = (byte)value;
        buffer[cursor + 1] = (byte)(value >> 8);
        cursor += 2;
    }

    private static void Write3(Span<byte> buffer, ref int cursor, int value)
    {
        uint raw = unchecked((uint)value);
        buffer[cursor] = (byte)(raw >> 8);
        buffer[cursor + 1] = (byte)(raw >> 16);
        buffer[cursor + 2] = (byte)(raw >> 24);
        cursor += 3;
    }

    private static int ReadI32(CombatRegisters registers, int dgroupOffset) =>
        unchecked((int)(registers.Word(dgroupOffset) | ((uint)registers.Word(dgroupOffset + 2) << 16)));

    private static void WriteI32(CombatRegisters registers, int dgroupOffset, int value)
    {
        registers.SetWord(dgroupOffset, (ushort)value);
        registers.SetWord(dgroupOffset + 2, (ushort)(value >> 16));
    }
}
