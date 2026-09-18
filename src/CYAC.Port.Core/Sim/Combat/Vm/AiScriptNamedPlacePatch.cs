namespace CYAC.Port.Core.Sim.Combat.Vm;

/// <summary>
/// <c>ai_script_named_place_patch @image@0x094EF</c> — the LOAD-TIME rewrite that turns an authored
/// script's place-relative pseudo-ops into absolute-coordinate ops the VM can run.
/// </summary>
/// <remarks>
/// <para>
/// The authored alphabet has two pseudo-opcodes the interpreter has no handler for:
/// <c>0xE3 + place</c> (NAMED_PLACE_HEADING) and <c>0xF0 + place</c> (NAMED_PLACE_ORIENT), plus the
/// dormant <c>0xFD</c> (relative to the previous object's final position).  The parser walks the
/// program once as it stores it and REWRITES them in place:
/// </para>
/// <list type="bullet">
///   <item><description><c>0xF0+k</c> → <c>0x02</c> <c>SET_ORIENTATION_TARGETS</c>, its three
///     <c>i24</c> offsets displaced by the place's XYZ (<c>image@0x09588..0x095E0</c> →
///     <c>image@0x094AF</c>).</description></item>
///   <item><description><c>0xE3+k</c> → <c>0x09</c> <c>SET_HEADING_TARGETS</c>, its two <c>i24</c>
///     offsets displaced by the place's X and Z — the Y is not touched
///     (<c>image@0x095F0..0x09626</c>).</description></item>
///   <item><description><c>0xFD</c> → <c>0x02</c> with the coordinates taken from
///     <c>g_wld_fixed_coord_entry [0xB556]</c> instead (<c>image@0x09651</c>).</description></item>
/// </list>
/// <para>
/// The displacement arithmetic is <c>image@0x09478</c> and it is not a plain add: the operand's
/// 3-byte <c>i24</c> is read SHIFTED LEFT 8 (<c>ax = word[dst-1]; sub al,al</c> — <c>(mem[dst]
/// &lt;&lt; 8)</c> with <c>dx = word[dst+1]</c> supplying the upper two bytes), the place's
/// <c>i32</c> is added to that 32-bit value, and the top three bytes are written back.  In other
/// words the operand is in WORLD units and the table is in world-object units (<c>world &lt;&lt;
/// 8</c>), and the shift is how the two meet.
/// </para>
/// <para>
/// It also patches <c>0x0A track_actor</c>'s first operand from an ACTOR SLOT to that actor's
/// record near pointer plus <c>0x18</c> (<c>image@0x09630</c>) — the only non-coordinate rewrite.
/// </para>
/// </remarks>
public static class AiScriptNamedPlacePatch
{
    /// <summary><c>g_named_place_coord_table [0xEE74]</c> — 13 × three <c>i32</c>.</summary>
    public const int NamedPlaceCoordTable = 0xEE74;

    /// <summary><c>g_named_place_nearptr_table [0xEE5A]</c> — u16[13].</summary>
    public const int NamedPlaceNearPtrTable = 0xEE5A;

    /// <summary><c>g_wld_fixed_coord_entry [0xB556]</c> — the previous object's final position.</summary>
    public const int LastPlacedPosition = 0xB556;

    /// <summary>Bytes one named place occupies in the coordinate table: 12.</summary>
    public const int CoordEntryBytes = 12;

    /// <summary>What one patch pass did.</summary>
    /// <param name="Instructions">Instructions walked.</param>
    /// <param name="OrientPatches"><c>0xF0+k</c> rewrites.</param>
    /// <param name="HeadingPatches"><c>0xE3+k</c> rewrites.</param>
    /// <param name="ActorPatches"><c>0x0A</c> actor-slot rewrites.</param>
    /// <param name="Derailed">True when the walk ran off the end of an instruction.</param>
    public readonly record struct Result(
        int Instructions, int OrientPatches, int HeadingPatches, int ActorPatches, bool Derailed);

    /// <summary>Patches a freshly assembled program in place.</summary>
    /// <param name="program">The program bytes.</param>
    /// <param name="registers">The register file the two tables live in.</param>
    /// <returns>The census.</returns>
    public static Result Apply(Span<byte> program, CombatRegisters registers)
    {
        ArgumentNullException.ThrowIfNull(registers);

        int instructions = 0, orient = 0, heading = 0, actor = 0;
        int pc = 0;
        while (pc + 3 <= program.Length)
        {
            int op = pc + 2;                                  // image@0x9502: skip the delay word
            byte code = program[op];
            instructions++;

            switch (code)
            {
                case >= 0xF0 and <= 0xFC:
                {
                    if (op + 10 > program.Length)
                    {
                        return new Result(instructions, orient, heading, actor, true);
                    }

                    int place = code - 0xF0;
                    PatchOrient(program, op, PlaceCoordinate(registers, place));
                    orient++;
                    pc = op + 10;
                    break;
                }

                case 0xFD:
                {
                    if (op + 10 > program.Length)
                    {
                        return new Result(instructions, orient, heading, actor, true);
                    }

                    PatchOrient(program, op, ReadTriple(registers, LastPlacedPosition));
                    orient++;
                    pc = op + 10;
                    break;
                }

                case >= 0xE3 and <= 0xEF:
                {
                    if (op + 7 > program.Length)
                    {
                        return new Result(instructions, orient, heading, actor, true);
                    }

                    (int X, int Y, int Z) at = PlaceCoordinate(registers, code - 0xE3);
                    program[op] = 0x09;                        // image@0x095FA
                    Displace(program, op + 1, at.X);
                    Displace(program, op + 4, at.Z);
                    heading++;
                    pc = op + 7;
                    break;
                }

                case 0x0A:
                {
                    if (op + 9 > program.Length)
                    {
                        return new Result(instructions, orient, heading, actor, true);
                    }

                    // image@0x09630 — the actor SLOT becomes the actor's record near pointer + 0x18.
                    int slot = program[op + 1] | (program[op + 2] << 8);
                    ushort record = registers.Word(NamedPlaceNearPtrTable + (2 * (slot & 0x0F)));
                    ushort patched = unchecked((ushort)(record + 0x18));
                    program[op + 1] = (byte)patched;
                    program[op + 2] = (byte)(patched >> 8);
                    actor++;
                    pc = op + 9;
                    break;
                }

                case 0xDF:
                {
                    // image@0x09657 — walk to the NUL that ends the radio string.
                    int cursor = op + 1;
                    while (cursor < program.Length && program[cursor] != 0)
                    {
                        cursor++;
                    }

                    pc = cursor + 1;
                    break;
                }

                default:
                {
                    int operands = AiScriptDecoder.OperandBytes(code);
                    if (operands < 0)
                    {
                        return new Result(instructions, orient, heading, actor, true);
                    }

                    pc = op + 1 + operands;
                    break;
                }
            }
        }

        return new Result(instructions, orient, heading, actor, false);
    }

    /// <summary>One named place's three <c>i32</c> coordinates.</summary>
    /// <param name="registers">The register file.</param>
    /// <param name="place">The place index 0..12.</param>
    public static (int X, int Y, int Z) PlaceCoordinate(CombatRegisters registers, int place)
    {
        ArgumentNullException.ThrowIfNull(registers);
        return ReadTriple(registers, NamedPlaceCoordTable + (CoordEntryBytes * place));
    }

    /// <summary>
    /// <c>image@0x094AF</c> — rewrite one <c>0xF0+k</c> / <c>0xFD</c> to <c>0x02</c> and displace
    /// its three coordinates.
    /// </summary>
    /// <param name="program">The program.</param>
    /// <param name="op">The opcode byte's offset.</param>
    /// <param name="at">The place's coordinates.</param>
    private static void PatchOrient(Span<byte> program, int op, (int X, int Y, int Z) at)
    {
        program[op] = 0x02;                                    // image@0x094B6
        Displace(program, op + 1, at.X);
        Displace(program, op + 4, at.Y);
        Displace(program, op + 7, at.Z);
    }

    /// <summary>
    /// <c>image@0x09478</c> — <c>i24[dst] := (((i24[dst] &lt;&lt; 8) + delta) &gt;&gt; 8)</c>,
    /// spelled the way the original spells it.
    /// </summary>
    /// <param name="program">The program.</param>
    /// <param name="dst">The operand's first byte.</param>
    /// <param name="delta">The place coordinate, an <c>i32</c> in world-object units.</param>
    public static void Displace(Span<byte> program, int dst, int delta)
    {
        int shifted = (program[dst] << 8) | (program[dst + 1] << 16) | (program[dst + 2] << 24);
        int sum = unchecked(shifted + delta);
        program[dst] = (byte)(sum >> 8);
        program[dst + 1] = (byte)(sum >> 16);
        program[dst + 2] = (byte)(sum >> 24);
    }

    private static (int X, int Y, int Z) ReadTriple(CombatRegisters registers, int at) =>
        (registers.Word(at) | (registers.Word(at + 2) << 16),
         registers.Word(at + 4) | (registers.Word(at + 6) << 16),
         registers.Word(at + 8) | (registers.Word(at + 10) << 16));
}
