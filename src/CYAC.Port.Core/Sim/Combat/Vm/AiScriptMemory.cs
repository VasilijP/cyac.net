namespace CYAC.Port.Core.Sim.Combat.Vm;

/// <summary>
/// The far heap the VM's program buffers live in — <c>far_heap_alloc</c> as
/// <c>ensure_script_loaded @image@0x048B8</c> and <c>landing_zone_entry_add @image@0x090AE</c> call
/// it (<c>lcall 0x201d:0x3dde</c>), plus byte access to an allocated block.
/// </summary>
/// <remarks>
/// <para>
/// The original addresses a program by SEGMENT alone: <c>g_engagement_script_seg [0xED74]</c> is a
/// paragraph address and the program always starts at offset 0
/// (<c>engagement_script_read_byte @image@0x07666</c> builds <c>ES:BX</c> from
/// <c>[0xED74]:[0xED76]</c>).  The port keeps that shape — a block is identified by its segment
/// word — so a verification can seed the heap straight from a trace record's
/// <c>execSeg</c> plus its 64-byte exec window.
/// </para>
/// <para>
/// <b>Two allocators, two sizes.</b>  <c>ensure_script_loaded</c> asks for exactly <c>0x32</c> = 50
/// bytes (<c>mov ax,0x32</c> @<c>image@0x048BF</c>).  The AUTHORED-script path in
/// <c>wld_or_s_asset_parser</c> asks for <c>max(N, 50)</c> where <c>N</c> is the tag-0x89 record's
/// own length (<c>cmp ax,0x32 / jge / mov ax,0x32</c> @<c>image@0x09D4D..0x09D52</c>), so an
/// authored program is NOT limited to 50 bytes.  See <see cref="ProgramBufferBytes"/>.
/// </para>
/// </remarks>
public interface IAiScriptHeap
{
    /// <summary>Allocates a zero-filled block and returns its segment (0 = out of memory).</summary>
    /// <param name="byteCount">How many bytes.</param>
    ushort Allocate(int byteCount);

    /// <summary>The bytes of an allocated block; an EMPTY span when the segment is unknown.</summary>
    /// <param name="segment">The block's segment word.</param>
    Span<byte> Block(ushort segment);
}

/// <summary>
/// The default <see cref="IAiScriptHeap"/> — a dictionary of blocks keyed by a synthetic segment.
/// </summary>
/// <remarks>
/// The port has no 8086 address space, so segments are handed out as opaque increasing words.  The
/// only property the VM depends on is that a segment identifies a block and that <c>0</c> means "no
/// program" (<c>cmp word ptr [0xed74],0</c> @<c>image@0x048B8</c>).
/// </remarks>
public sealed class AiScriptHeap : IAiScriptHeap
{
    private readonly Dictionary<ushort, byte[]> _blocks = [];
    private ushort _next = 0x1000;

    /// <summary>How many blocks are live.</summary>
    public int BlockCount => _blocks.Count;

    /// <summary>Installs a block at a chosen segment — for seeding from a trace record.</summary>
    /// <param name="segment">The segment word the trace recorded.</param>
    /// <param name="bytes">The block's contents.</param>
    public void Install(ushort segment, ReadOnlySpan<byte> bytes)
    {
        if (segment == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(segment), "segment 0 means 'no program'");
        }

        _blocks[segment] = bytes.ToArray();
    }

    /// <summary>True when a segment has a block.</summary>
    /// <param name="segment">The segment word.</param>
    public bool Has(ushort segment) => _blocks.ContainsKey(segment);

    /// <inheritdoc/>
    public ushort Allocate(int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteCount);
        ushort segment = _next;
        _next = (ushort)(_next + 1);
        _blocks[segment] = new byte[byteCount];
        return segment;
    }

    /// <inheritdoc/>
    public Span<byte> Block(ushort segment) =>
        _blocks.TryGetValue(segment, out byte[]? block) ? block : [];
}

/// <summary>
/// The VM's program-buffer plumbing: the fetch helpers, the secondary WRITE cursor
/// <c>[0xB6A6]/[0xB6A8]</c> and the four emitters that make the interpreter a self-modifying
/// behaviour compiler.
/// </summary>
/// <remarks>
/// All of these are DGROUP-stateful leaves: they read and write the combat register file, so they
/// take it rather than owning any state.  Every one is a transliteration of a named function — the
/// names are's.
/// </remarks>
public static class AiScriptMemory
{
    /// <summary><c>g_engagement_script_seg [0xED74]</c>.</summary>
    public const int ScriptSegmentOffset = 0xED74;

    /// <summary><c>g_engagement_script_pc [0xED76]</c> — an i16; <c>-1</c> = armed / not running.</summary>
    public const int ScriptPcOffset = 0xED76;

    /// <summary><c>g_engagement_timer_init [0xED78]</c> — the VM FLAGS byte.</summary>
    public const int ScriptFlagsOffset = 0xED78;

    /// <summary><c>g_script_cursor_off [0xB6A6]</c> — the write cursor's offset half.</summary>
    public const int WriteCursorOffsetOffset = 0xB6A6;

    /// <summary><c>g_script_cursor_seg [0xB6A8]</c> — the write cursor's segment half.</summary>
    public const int WriteCursorSegmentOffset = 0xB6A8;

    /// <summary>
    /// The size <c>ensure_script_loaded</c> allocates for a RUNTIME-GENERATED program:
    /// <c>0x32</c> = 50 bytes (<c>image@0x048BF</c>).
    /// </summary>
    public const int ProgramBufferBytes = 0x32;

    // ---- the three fetch helpers ---------------------------------------------------------------

    /// <summary>
    /// <c>engagement_script_read_byte @image@0x07666</c> — one byte at <c>[0xED74]:[0xED76]</c>,
    /// post-incrementing the PC.
    /// </summary>
    /// <param name="registers">The combat register file.</param>
    /// <param name="heap">The script heap.</param>
    /// <returns>The byte; 0 when the PC is outside the block (see remarks).</returns>
    /// <remarks>
    /// The original has no bounds check at all — <c>mov al, es:[bx]</c> reads whatever the paragraph
    /// holds.  The port answers 0 for an out-of-block read and COUNTS it through
    /// <see cref="EngagementVmCensus.ScriptReadsOutsideBlock"/> so a verification can see it rather
    /// than silently disagreeing with the machine.
    /// </remarks>
    public static byte ReadByte(CombatRegisters registers, IAiScriptHeap heap)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(heap);

        ushort pc = registers.Word(ScriptPcOffset);
        registers.SetWord(ScriptPcOffset, (ushort)(pc + 1));       // image@0x07669 inc
        Span<byte> block = heap.Block(registers.Word(ScriptSegmentOffset));
        return pc < block.Length ? block[pc] : (byte)0;            // image@0x07675
    }

    /// <summary>
    /// <c>engagement_script_read_word @image@0x0767A</c> — two byte reads, LOW first
    /// (<c>image@0x07680</c> then <c>image@0x07686</c>).
    /// </summary>
    /// <param name="registers">The combat register file.</param>
    /// <param name="heap">The script heap.</param>
    public static ushort ReadWord(CombatRegisters registers, IAiScriptHeap heap)
    {
        byte low = ReadByte(registers, heap);
        byte high = ReadByte(registers, heap);
        return (ushort)(low | (high << 8));                        // image@0x0768B mov al,[bp-2] / mov ah,cl
    }

    /// <summary>
    /// <c>engagement_script_read_byte3 @image@0x07694</c> — the 3-byte composite as the MSC long
    /// <c>DX:AX</c> after the <c>xchg dx,ax</c> @<c>image@0x076A3</c>.
    /// </summary>
    /// <param name="registers">The combat register file.</param>
    /// <param name="heap">The script heap.</param>
    /// <returns><c>(b0 &lt;&lt; 8) | (b1 &lt;&lt; 16) | (b2 &lt;&lt; 24)</c> — world units × 256.</returns>
    public static int ReadByte3(CombatRegisters registers, IAiScriptHeap heap)
    {
        int high = ReadByte(registers, heap) << 8;                 // image@0x0769A mov ah,al / xor al,al
        ushort low = ReadWord(registers, heap);
        return unchecked((int)(((uint)low << 16) | (uint)high));   // DX:AX with DX = the word
    }

    // ---- the write cursor and the emitters -----------------------------------------------------

    /// <summary>
    /// <c>engagement_script_cursor_init @image@0x09027</c> — sets the secondary write cursor.
    /// </summary>
    /// <param name="registers">The combat register file.</param>
    /// <param name="offset">The offset half (<c>[bp+4]</c>, the SECOND pushed word).</param>
    /// <param name="segment">The segment half (<c>[bp+6]</c>, the FIRST pushed word).</param>
    public static void CursorInit(CombatRegisters registers, ushort offset, ushort segment)
    {
        ArgumentNullException.ThrowIfNull(registers);
        registers.SetWord(WriteCursorOffsetOffset, offset);        // image@0x09030
        registers.SetWord(WriteCursorSegmentOffset, segment);      // image@0x09033
    }

    /// <summary><c>ai_script_write_byte @image@0x0906C</c> — one byte, cursor post-incremented.</summary>
    /// <param name="registers">The combat register file.</param>
    /// <param name="heap">The script heap.</param>
    /// <param name="value">The byte.</param>
    public static void WriteByte(CombatRegisters registers, IAiScriptHeap heap, byte value)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(heap);

        ushort offset = registers.Word(WriteCursorOffsetOffset);
        registers.SetWord(WriteCursorOffsetOffset, (ushort)(offset + 1));  // image@0x09070 inc
        Span<byte> block = heap.Block(registers.Word(WriteCursorSegmentOffset));
        if (offset < block.Length)
        {
            block[offset] = value;                                 // image@0x09074
        }
    }

    /// <summary><c>ai_script_write_word @image@0x0905F</c> — one LE word, cursor += 2.</summary>
    /// <param name="registers">The combat register file.</param>
    /// <param name="heap">The script heap.</param>
    /// <param name="value">The word.</param>
    public static void WriteWord(CombatRegisters registers, IAiScriptHeap heap, ushort value)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(heap);

        ushort offset = registers.Word(WriteCursorOffsetOffset);
        registers.SetWord(WriteCursorOffsetOffset, (ushort)(offset + 2));  // image@0x09063 add 2
        Span<byte> block = heap.Block(registers.Word(WriteCursorSegmentOffset));
        if (offset < block.Length)
        {
            block[offset] = (byte)value;                           // image@0x09068 mov es:[bx],ax
        }

        if (offset + 1 < block.Length)
        {
            block[offset + 1] = (byte)(value >> 8);
        }
    }

    /// <summary>
    /// <c>ai_script_write_3bytes @image@0x0903B</c> — the three bytes of <c>value &gt;&gt; 8</c> as
    /// LE24, the exact mirror of <see cref="ReadByte3"/>.
    /// </summary>
    /// <param name="registers">The combat register file.</param>
    /// <param name="heap">The script heap.</param>
    /// <param name="value">The MSC long the caller holds in <c>DX:AX</c>.</param>
    /// <remarks>
    /// The original spills <c>DX:AX</c> to the stack (<c>push dx; push ax</c>) and
    /// <c>far_memcpy</c>s the THREE bytes at <c>[bp-3]</c> — i.e. bytes 1..3 of the little-endian
    /// dword (<c>image@0x09040..0x09051</c>), then bumps the cursor by 3.
    /// </remarks>
    public static void Write3Bytes(CombatRegisters registers, IAiScriptHeap heap, int value)
    {
        uint raw = unchecked((uint)value);
        WriteByte(registers, heap, (byte)(raw >> 8));
        WriteByte(registers, heap, (byte)(raw >> 16));
        WriteByte(registers, heap, (byte)(raw >> 24));
    }

    /// <summary>
    /// <c>ai_script_emit_op0B_settargets @image@0x09078</c> — emits one complete
    /// <c>0x0B SET_PHASE_0B</c> instruction, eleven bytes.
    /// </summary>
    /// <param name="registers">The combat register file.</param>
    /// <param name="heap">The script heap.</param>
    /// <param name="delay">The original's <c>AX</c> — the instruction's delay word.</param>
    /// <param name="targetD">The original's <c>DX</c> — emitted as the FIRST word operand.</param>
    /// <param name="targetB">The original's <c>BX</c> — the second.</param>
    /// <param name="first">The FIRST pushed stack word (<c>[bp+6]</c>) — the third.</param>
    /// <param name="second">The SECOND pushed stack word (<c>[bp+4]</c>) — the fourth.</param>
    public static void EmitSetTargets(
        CombatRegisters registers,
        IAiScriptHeap heap,
        ushort delay,
        ushort targetD,
        ushort targetB,
        ushort first,
        ushort second)
    {
        WriteWord(registers, heap, delay);                          // image@0x0907D
        WriteByte(registers, heap, AiScriptOpcode.SetPhase0B);      // image@0x09080
        WriteWord(registers, heap, targetD);                        // image@0x09085 [bp-4] = DX
        WriteWord(registers, heap, targetB);                        // image@0x0908B [bp-2] = BX
        WriteWord(registers, heap, first);                          // image@0x09091 [bp+6]
        WriteWord(registers, heap, second);                         // image@0x09097 [bp+4]
    }

    /// <summary>
    /// <c>ai_script_emit_suspend (ex-ai_script_emit_eof) @image@0x090A3</c> — the three-byte terminator <c>00 00 FF</c>.
    /// </summary>
    /// <param name="registers">The combat register file.</param>
    /// <param name="heap">The script heap.</param>
    /// <remarks>
    /// MISNOMER (report-only): the opcode it writes is <c>0xFF SUSPEND</c> (<c>mov al,0xff</c>
    /// @<c>image@0x090A8</c>), not <c>0xFE</c> RETURN/EOF.  The emitted programs therefore SUSPEND — they re-arm and
    /// resume from the start next frame — they never end.
    /// </remarks>
    public static void EmitSuspend(CombatRegisters registers, IAiScriptHeap heap)
    {
        WriteWord(registers, heap, 0);                              // image@0x090A3
        WriteByte(registers, heap, AiScriptOpcode.Suspend);         // image@0x090A8
    }

    /// <summary>
    /// <c>ensure_script_loaded @image@0x048B8</c> — lazily allocates the 50-byte program buffer,
    /// resets the PC and the write cursor, and seeds the VM flags byte.
    /// </summary>
    /// <param name="registers">The combat register file.</param>
    /// <param name="heap">The script heap.</param>
    /// <returns>The original's <c>AL</c>: false only when the allocation failed.</returns>
    /// <remarks>
    /// The <c>[0xED74]!= 0</c> path does NOT skip the reset: <c>jne 0x3ff2</c>
    /// (<c>image@0x048BD</c>) lands after the allocation but before <c>[0xED78] = 0x38</c> /
    /// <c>[0xED76] = 0</c> / <c>cursor_init(0, [0xED74])</c>.  So EVERY call rewinds the program
    /// to offset 0 for both reading and writing — which is what makes the interpreter's emit arms
    /// safe: they always compile from the start of the buffer.
    /// </remarks>
    public static bool EnsureScriptLoaded(CombatRegisters registers, IAiScriptHeap heap)
    {
        ArgumentNullException.ThrowIfNull(registers);
        ArgumentNullException.ThrowIfNull(heap);

        if (registers.Word(ScriptSegmentOffset) == 0)                // image@0x048B8
        {
            ushort segment = heap.Allocate(ProgramBufferBytes);      // image@0x048BF/0x048C2
            registers.SetWord(ScriptSegmentOffset, segment);         // image@0x048C7
            if (segment == 0)
            {
                return false;                                        // image@0x048CE sub al,al
            }
        }

        registers.SetByte(ScriptFlagsOffset, 0x38);                  // image@0x048D2
        registers.SetWord(ScriptPcOffset, 0);                        // image@0x048D7
        CursorInit(registers, 0, registers.Word(ScriptSegmentOffset));  // image@0x048DD..0x048E4
        return true;                                                 // image@0x048E7 mov al,1
    }
}
