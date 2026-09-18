using System.Text;

namespace CYAC.Port.Core.Sim.Combat.Vm;

/// <summary>
/// The enemy-AI bytecode instruction set — the opcode numbers
/// <c>engagement_script_interpreter @image@0x05154</c> dispatches on.
/// </summary>
/// <remarks>
/// <para>
/// Source of truth, in order: the bytes (the low dispatch <c>image@0x0540E</c>, the
/// <c>0x0B/0x0C/0x0D + 0xD5/0xD6/0xD7</c> sub-dispatch <c>image@0x0572C</c> and the ten-entry word
/// jump table at <c>image@0x0577A</c> reached from <c>image@0x0575C</c>);
/// </para>
/// <para>
/// Encoding: every instruction is <c>[delay u16 LE][opcode u8][operands 0..N]</c>.  The fetch loop
/// at <c>image@0x05400</c> reads the delay word FIRST (<c>engagement_script_read_word
/// @image@0x0767A</c>) and then the opcode byte (<c>engagement_script_read_byte
/// @image@0x07666</c>).  The one exception the doc calls out — "<c>0xD8</c> ignores the prefix" —
/// is a doc wording slip, not a decode rule: <c>0xD8</c> reads the same leading delay word as
/// everything else and simply never commits it (<c>image@0x0548E</c> goes straight to
/// <c>read_word</c> for its own flags operand).
/// </para>
/// </remarks>
public static class AiScriptOpcode
{
    /// <summary>Phase 0 — idle; also zeroes <c>[0xED80]</c> (<c>image@0x055FC</c>).</summary>
    public const byte SetPhaseIdle = 0x00;

    /// <summary>Phase 2 — SET_ORIENTATION_TARGETS, three 3-byte composites (<c>image@0x0562C</c>).</summary>
    public const byte SetOrientationTargets = 0x02;

    /// <summary>Phase 7 — also sets <c>[0xED80] = 2</c> (<c>image@0x05664</c>).</summary>
    public const byte SetPhase07 = 0x07;

    /// <summary>Phase 8 — targets derived from <c>[0xED4A:0xED4C] + 0x0003E800</c> (<c>image@0x056A2</c>).</summary>
    public const byte SetPhaseDive = 0x08;

    /// <summary>Phase 9 — SET_HEADING_TARGETS, two 3-byte composites (<c>image@0x056CA</c>).</summary>
    public const byte SetHeadingTargets = 0x09;

    /// <summary>Phase A — four words (<c>image@0x056F8</c>).</summary>
    public const byte SetPhase0A = 0x0A;

    /// <summary>
    /// Phase B — four words.  Shares <c>0x0A</c>'s body: <c>image@0x05724</c> writes the phase byte
    /// and then <c>jmp 0x4e22</c> (<c>image@0x05729</c>) into <c>image@0x05702</c>, so the two
    /// opcodes differ ONLY in the phase they set.
    /// </summary>
    public const byte SetPhase0B = 0x0B;

    /// <summary>Phase C — one word into <c>[0xED81]</c>, the other three zeroed (<c>image@0x0560A</c>).</summary>
    public const byte SetPhase0C = 0x0C;

    /// <summary>Phase D — a byte plus three words (<c>image@0x05670</c>).</summary>
    public const byte SetPhase0D = 0x0D;

    /// <summary>KILL_ACTOR — one word, the actor-slot index (<c>image@0x0550C</c>).</summary>
    public const byte KillActor = 0xD5;

    /// <summary>SPAWN_ACTOR — four words (<c>image@0x054CE</c>).</summary>
    public const byte SpawnActor = 0xD6;

    /// <summary>CLEAR_SCRIPT_FLAGS — one word mask; <c>[0xED78] &amp;= ~(mask &amp; 0xFF)</c>.</summary>
    public const byte ClearScriptFlags = 0xD7;

    /// <summary>SET_SCRIPT_FLAGS / conditional exit — one word (<c>image@0x0548E</c>).</summary>
    public const byte SetScriptFlags = 0xD8;

    /// <summary>SET_HEADING — one word into <c>[0xED4E]</c> (<c>image@0x05484</c>).</summary>
    public const byte SetHeading = 0xD9;

    /// <summary>RETARGET_SLOT — no operands (<c>image@0x0546E</c>).</summary>
    public const byte RetargetSlot = 0xDA;

    /// <summary>CLEAR_COUNTER — <c>[0xC390] = 0</c>, no operands (<c>image@0x0544A</c>).</summary>
    public const byte ClearCounter = 0xDB;

    /// <summary>SET_FLIGHT_PARAMS — three words (<c>image@0x053DC</c>).</summary>
    public const byte SetFlightParams = 0xDC;

    /// <summary>RESET_PC — <c>[0xED76] = 0</c>, no operands (<c>image@0x05452</c>).</summary>
    public const byte ResetPc = 0xDD;

    /// <summary>FIRE_WEAPON — one word, the actor-slot index (<c>image@0x05520</c>).</summary>
    public const byte FireWeapon = 0xDE;

    /// <summary>PRINT_STRING — a NUL-terminated string (<c>image@0x055CA</c>).</summary>
    public const byte PrintString = 0xDF;

    /// <summary>CALL_SCRIPT_FUNCTION — one word (<c>image@0x05478</c>).</summary>
    public const byte CallScriptFunction = 0xE0;

    /// <summary>SET_FLAG — <c>[0xED59] |= 0x20</c>, no operands (<c>image@0x055F4</c>).</summary>
    public const byte SetEngageFlag = 0xE1;

    /// <summary>CLEAR_FLAG — <c>[0xED59] &amp;= ~0x20</c>, no operands (<c>image@0x055EC</c>).</summary>
    public const byte ClearEngageFlag = 0xE2;

    /// <summary>First named-place HEADING opcode; the load patcher rewrites it to <see cref="SetHeadingTargets"/>.</summary>
    public const byte NamedPlaceHeadingFirst = 0xE3;

    /// <summary>Last named-place HEADING opcode.</summary>
    public const byte NamedPlaceHeadingLast = 0xEF;

    /// <summary>First named-place ORIENT opcode; the patcher rewrites it to <see cref="SetOrientationTargets"/>.</summary>
    public const byte NamedPlaceOrientFirst = 0xF0;

    /// <summary>Last named-place ORIENT opcode.</summary>
    public const byte NamedPlaceOrientLast = 0xFC;

    /// <summary>The dormant "previous object's origin" named-place variant (<c>image@0x09651</c>).</summary>
    public const byte NamedPlaceOrientPrev = 0xFD;

    /// <summary>RETURN / script end (<c>image@0x0545A</c>).</summary>
    public const byte Return = 0xFE;

    /// <summary>SUSPEND — <c>[0xED76] = -1</c>, resume from the start next frame (<c>image@0x0578E</c>).</summary>
    public const byte Suspend = 0xFF;

    /// <summary>
    /// True when the opcode is one the dispatch treats as INVALID and aborts the stream on
    /// (<c>image@0x051C0</c>: <c>[0xED76] = -1</c>).
    /// </summary>
    /// <remarks>
    /// The low dispatch's <c>je</c> chain (<c>image@0x0540E..0x05447</c>) accepts only
    /// <c>0x00, 0x02, 0x07, 0x08, 0x09, 0x0A</c> before falling through, the <c>0x0572C</c>
    /// sub-dispatch adds <c>0x0B, 0x0C, 0x0D</c> and <c>0xD5, 0xD6, 0xD7</c>, and
    /// <c>image@0x0575C</c> adds <c>0xD8</c>, <c>0xD9..0xE2</c> (the jump table) and
    /// <c>0xFE</c>/<c>&gt;0xFE</c>.  Everything else lands on <c>image@0x048E0</c>.
    /// </remarks>
    /// <param name="opcode">The opcode byte.</param>
    public static bool IsInvalid(byte opcode) => opcode switch
    {
        SetPhaseIdle or SetOrientationTargets or SetPhase07 or SetPhaseDive or SetHeadingTargets
            or SetPhase0A or SetPhase0B or SetPhase0C or SetPhase0D => false,
        KillActor or SpawnActor or ClearScriptFlags or SetScriptFlags => false,
        >= SetHeading and <= ClearEngageFlag => false,
        Return or Suspend => false,
        _ => true,
    };

    /// <param name="opcode">The opcode byte.</param>
    public static string Mnemonic(byte opcode) => opcode switch
    {
        SetPhaseIdle => "SET_PHASE_IDLE",
        SetOrientationTargets => "SET_ORIENTATION_TARGETS",
        SetPhase07 => "SET_PHASE_07",
        SetPhaseDive => "SET_PHASE_DIVE",
        SetHeadingTargets => "SET_HEADING_TARGETS",
        SetPhase0A => "SET_PHASE_0A",
        SetPhase0B => "SET_PHASE_0B",
        SetPhase0C => "SET_PHASE_0C",
        SetPhase0D => "SET_PHASE_0D",
        KillActor => "KILL_ACTOR",
        SpawnActor => "SPAWN_ACTOR",
        ClearScriptFlags => "CLEAR_SCRIPT_FLAGS",
        SetScriptFlags => "SET_SCRIPT_FLAGS",
        SetHeading => "SET_HEADING",
        RetargetSlot => "RETARGET_SLOT",
        ClearCounter => "CLEAR_COUNTER",
        SetFlightParams => "SET_FLIGHT_PARAMS",
        ResetPc => "RESET_PC",
        FireWeapon => "FIRE_WEAPON",
        PrintString => "PRINT_STRING",
        CallScriptFunction => "CALL_SCRIPT_FUNCTION",
        SetEngageFlag => "SET_ENGAGE_FLAG",
        ClearEngageFlag => "CLEAR_ENGAGE_FLAG",
        >= NamedPlaceHeadingFirst and <= NamedPlaceHeadingLast =>
            $"NAMED_PLACE_HEADING_{opcode - NamedPlaceHeadingFirst}",
        >= NamedPlaceOrientFirst and <= NamedPlaceOrientLast =>
            $"NAMED_PLACE_ORIENT_{opcode - NamedPlaceOrientFirst}",
        NamedPlaceOrientPrev => "NAMED_PLACE_ORIENT_PREV",
        Return => "EOF",
        Suspend => "SUSPEND",
        0x01 => "INVALID_01",
        0x03 => "INVALID_03",
        0x04 => "INVALID_04",
        0x05 => "INVALID_05",
        0x06 => "INVALID_06",
        _ => $"UNKNOWN_{opcode:X2}",
    };
}

/// <summary>One decoded bytecode instruction.</summary>
/// <param name="Offset">Its byte offset in the program — the PC the fetch loop started it at.</param>
/// <param name="Delay">The leading delay word (<c>0xFFFE</c>/<c>0xFFFF</c> are sentinels).</param>
/// <param name="Opcode">The opcode byte.</param>
/// <param name="Length">Total bytes, delay word and opcode included.</param>
/// <param name="Words">Word operands, in stream order (empty when the opcode takes none).</param>
/// <param name="Composites">
/// 3-byte composite operands as the i32 <c>engagement_script_read_byte3 @image@0x07694</c> answers —
/// <c>(b0 &lt;&lt; 8) | (b1 &lt;&lt; 16) | (b2 &lt;&lt; 24)</c>, i.e. world units × 256.
/// </param>
/// <param name="ByteOperand">The single byte operand opcode <c>0x0D</c> reads; else 0.</param>
/// <param name="Text">The NUL-terminated string opcode <c>0xDF</c> reads; else null.</param>
public readonly record struct AiScriptInstruction(
    int Offset,
    ushort Delay,
    byte Opcode,
    int Length,
    IReadOnlyList<ushort> Words,
    IReadOnlyList<int> Composites,
    byte ByteOperand,
    string? Text)
{
    /// <summary>The opcode's mnemonic.</summary>
    public string Mnemonic => AiScriptOpcode.Mnemonic(Opcode);

    public override string ToString()
    {
        StringBuilder text = new StringBuilder();
        text.Append($"+{Offset:X4}  [delay=0x{Delay:X4}]  {Mnemonic}");
        foreach (int composite in Composites)
        {
            text.Append($"  0x{(uint)composite:X8}");
        }

        foreach (ushort word in Words)
        {
            text.Append($"  0x{word:X4}");
        }

        if (Text is not null)
        {
            text.Append($"  \"{Text}\"");
        }

        return text.ToString();
    }
}

/// <summary>
/// The decoder for the bytecode the VM interprets — the read side of the <c>[delay][op][operands]</c>
/// encoding, with exactly the operand counts each handler consumes.
/// </summary>
/// <remarks>
/// <para>
/// This is a DISASSEMBLER, not the interpreter: it exists so a test can prove a generated or authored
/// program's shape, and so the port agrees with on every buffer the C0 traces recorded.
/// <see cref="EngagementVm"/> does its own fetching through the same three helpers the original uses,
/// because the interpreter's PC is a piece of verified state.
/// </para>
/// <para>
/// Operand counts, each from its handler: <c>0x02</c> 3× composite (<c>image@0x0563C..0x0564E</c>);
/// <c>0x09</c> 2× composite (<c>image@0x056DA/0x056E4</c>); <c>0x0A</c>/<c>0x0B</c> 4× word
/// (<c>image@0x05708..0x0571A</c>); <c>0x0C</c> 1× word (<c>image@0x05615</c>); <c>0x0D</c> byte +
/// 3× word (<c>image@0x05680..0x05698</c>); <c>0xD5</c> 1 word (<c>image@0x0550C</c>); <c>0xD6</c>
/// 4 words (<c>image@0x054CE..0x054E0</c>); <c>0xD7</c>/<c>0xD8</c>/<c>0xD9</c>/<c>0xDE</c>/<c>0xE0</c>
/// 1 word; <c>0xDC</c> 3 words (<c>image@0x053DC..0x053F4</c>); <c>0xDF</c> a NUL-terminated string
/// (<c>image@0x055D0..0x055DD</c>); everything else none.  The named-place ranges are COMPILE-TIME
/// only and carry 2 (heading) or 3 (orient) composites, consumed by
/// <c>ai_script_named_place_patch @image@0x094EF</c> in situ.
/// </para>
/// </remarks>
public static class AiScriptDecoder
{
    private static readonly ushort[] NoWords = [];
    private static readonly int[] NoComposites = [];

    /// <summary>
    /// <c>engagement_script_read_byte3 @image@0x07694</c>'s value: the MSC long
    /// <c>DX:AX</c> with <c>AX = b0 &lt;&lt; 8</c> and <c>DX = b1 | (b2 &lt;&lt; 8)</c> after the
    /// <c>xchg dx,ax</c> @<c>image@0x076A2</c>.  World units are this <c>&gt;&gt; 8</c>.
    /// </summary>
    /// <param name="b0">The first stream byte.</param>
    /// <param name="b1">The second.</param>
    /// <param name="b2">The third.</param>
    public static int Composite(byte b0, byte b1, byte b2) =>
        unchecked((int)(((uint)b0 << 8) | ((uint)b1 << 16) | ((uint)b2 << 24)));

    /// <summary>How many operand bytes an opcode consumes after the delay word and the opcode byte.</summary>
    /// <param name="opcode">The opcode byte.</param>
    /// <returns>The count, or <c>-1</c> for <c>PRINT_STRING</c> whose length is data-dependent.</returns>
    public static int OperandBytes(byte opcode) => opcode switch
    {
        AiScriptOpcode.SetOrientationTargets => 9,
        AiScriptOpcode.SetHeadingTargets => 6,
        AiScriptOpcode.SetPhase0A or AiScriptOpcode.SetPhase0B => 8,
        AiScriptOpcode.SetPhase0C => 2,
        AiScriptOpcode.SetPhase0D => 7,
        AiScriptOpcode.KillActor => 2,
        AiScriptOpcode.SpawnActor => 8,
        AiScriptOpcode.ClearScriptFlags or AiScriptOpcode.SetScriptFlags => 2,
        AiScriptOpcode.SetHeading => 2,
        AiScriptOpcode.SetFlightParams => 6,
        AiScriptOpcode.FireWeapon => 2,
        AiScriptOpcode.PrintString => -1,
        AiScriptOpcode.CallScriptFunction => 2,
        >= AiScriptOpcode.NamedPlaceHeadingFirst and <= AiScriptOpcode.NamedPlaceHeadingLast => 6,
        >= AiScriptOpcode.NamedPlaceOrientFirst and <= AiScriptOpcode.NamedPlaceOrientPrev => 9,
        _ => 0,
    };

    /// <summary>Decodes the instruction at <paramref name="pc"/>.</summary>
    /// <param name="program">The program bytes.</param>
    /// <param name="pc">The byte offset to decode at.</param>
    /// <param name="instruction">The decoded instruction on success.</param>
    /// <returns>False when the stream ends before the instruction is complete.</returns>
    public static bool TryDecode(ReadOnlySpan<byte> program, int pc, out AiScriptInstruction instruction)
    {
        instruction = default;
        if (pc < 0 || pc + 3 > program.Length)
        {
            return false;
        }

        ushort delay = (ushort)(program[pc] | (program[pc + 1] << 8));
        byte opcode = program[pc + 2];
        int cursor = pc + 3;

        if (opcode == AiScriptOpcode.PrintString)
        {
            int start = cursor;
            while (cursor < program.Length && program[cursor] != 0)
            {
                cursor++;
            }

            if (cursor >= program.Length)
            {
                return false;                          // no terminator inside the buffer
            }

            string text = Encoding.Latin1.GetString(program[start..cursor]);
            cursor++;                                  // consume the NUL (image@0x055DD loop exit)
            instruction = new AiScriptInstruction(
                pc, delay, opcode, cursor - pc, NoWords, NoComposites, 0, text);
            return true;
        }

        int operandBytes = OperandBytes(opcode);
        if (cursor + operandBytes > program.Length)
        {
            return false;
        }

        byte byteOperand = 0;
        if (opcode == AiScriptOpcode.SetPhase0D)
        {
            byteOperand = program[cursor];
            cursor++;
            operandBytes--;
        }

        int compositeCount = CompositeCount(opcode);
        int[] composites = compositeCount == 0 ? NoComposites : new int[compositeCount];
        for (int i = 0; i < compositeCount; i++)
        {
            composites[i] = Composite(program[cursor], program[cursor + 1], program[cursor + 2]);
            cursor += 3;
            operandBytes -= 3;
        }

        int wordCount = operandBytes / 2;
        ushort[] words = wordCount == 0 ? NoWords : new ushort[wordCount];
        for (int i = 0; i < wordCount; i++)
        {
            words[i] = (ushort)(program[cursor] | (program[cursor + 1] << 8));
            cursor += 2;
        }

        instruction = new AiScriptInstruction(
            pc, delay, opcode, cursor - pc, words, composites, byteOperand, null);
        return true;
    }

    /// <summary>Walks a whole program from offset 0, stopping at <c>0xFE</c>, <c>0xFF</c> or the end.</summary>
    /// <param name="program">The program bytes.</param>
    /// <returns>The instructions in stream order.</returns>
    /// <remarks>
    /// The walk stops at <c>SUSPEND</c> as well as <c>EOF</c> because both terminate the fetch loop
    /// (<c>image@0x0578E</c> re-arms the PC, <c>image@0x0545A</c> returns), and a listing that ran on
    /// past them would be disassembling whatever the heap block held before.  An INVALID opcode also
    /// stops the walk — it aborts the stream at <c>image@0x051C0</c>.
    /// </remarks>
    public static IReadOnlyList<AiScriptInstruction> Disassemble(ReadOnlySpan<byte> program)
    {
        List<AiScriptInstruction> list = new List<AiScriptInstruction>();
        int pc = 0;
        while (TryDecode(program, pc, out AiScriptInstruction instruction))
        {
            list.Add(instruction);
            pc += instruction.Length;
            if (instruction.Opcode is AiScriptOpcode.Return or AiScriptOpcode.Suspend
                || AiScriptOpcode.IsInvalid(instruction.Opcode))
            {
                break;
            }
        }

        return list;
    }

    private static int CompositeCount(byte opcode) => opcode switch
    {
        AiScriptOpcode.SetOrientationTargets => 3,
        AiScriptOpcode.SetHeadingTargets => 2,
        >= AiScriptOpcode.NamedPlaceHeadingFirst and <= AiScriptOpcode.NamedPlaceHeadingLast => 2,
        >= AiScriptOpcode.NamedPlaceOrientFirst and <= AiScriptOpcode.NamedPlaceOrientPrev => 3,
        _ => 0,
    };
}
