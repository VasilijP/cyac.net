using System.Globalization;
using System.Text;

namespace CYAC.Formats.EaLib;

/// <summary>A register as a real-mode (16-bit) instruction names it.</summary>
public enum RealModeRegister : byte
{
    /// <summary>No register.</summary>
    None,
    /// <summary>8-bit registers, in encoding order.</summary>
    AL, CL, DL, BL, AH, CH, DH, BH,
    /// <summary>16-bit registers, in encoding order.</summary>
    AX, CX, DX, BX, SP, BP, SI, DI,
    /// <summary>Segment registers, in encoding order.</summary>
    ES, CS, SS, DS,
}

/// <summary>What an operand is.</summary>
public enum RealModeOperandKind : byte
{
    /// <summary>A register.</summary>
    Register,
    /// <summary>An immediate, a branch target or a constant.</summary>
    Immediate,
    /// <summary>A memory reference.</summary>
    Memory,
}

/// <summary>One decoded operand, with the values a Capstone-style disassembler reports for it.</summary>
/// <param name="Kind">What the operand is.</param>
/// <param name="Size">The operand's width in bytes.</param>
/// <param name="Register">The register, for <see cref="RealModeOperandKind.Register"/>.</param>
/// <param name="Immediate">The value, for <see cref="RealModeOperandKind.Immediate"/>.</param>
/// <param name="Segment">The explicit segment of a memory operand (<see cref="RealModeRegister.None"/> when the default applies).</param>
/// <param name="Base">The base register of a memory operand.</param>
/// <param name="Index">The index register of a memory operand.</param>
/// <param name="Displacement">The displacement of a memory operand.</param>
public readonly record struct RealModeOperand(
    RealModeOperandKind Kind,
    int Size,
    RealModeRegister Register = RealModeRegister.None,
    long Immediate = 0,
    RealModeRegister Segment = RealModeRegister.None,
    RealModeRegister Base = RealModeRegister.None,
    RealModeRegister Index = RealModeRegister.None,
    int Displacement = 0);

/// <summary>One decoded real-mode instruction.</summary>
public sealed class RealModeInstruction
{
    internal RealModeInstruction(int address, byte[] bytes, string mnemonic, string operandText,
                                 RealModeOperand[] operands, bool isJump)
    {
        Address = address;
        Bytes = bytes;
        Mnemonic = mnemonic;
        OperandText = operandText;
        Operands = operands;
        IsJump = isJump;
        Shape = MakeShape(mnemonic, operands);
    }

    /// <summary>Where the instruction starts (the address the decoder was given).</summary>
    public int Address { get; }

    /// <summary>The instruction's length in bytes, prefixes included.</summary>
    public int Size => Bytes.Length;

    /// <summary>The address right after the instruction.</summary>
    public int End => Address + Bytes.Length;

    /// <summary>The instruction's bytes.</summary>
    public byte[] Bytes { get; }

    /// <summary>The mnemonic, as Capstone 5 prints it in 16-bit mode (a string operation carries its <c>rep</c> prefix).</summary>
    public string Mnemonic { get; }

    /// <summary>The operand text, as Capstone 5 prints it (its <c>op_str</c>); empty when there are no operands.</summary>
    public string OperandText { get; }

    /// <summary>The operands, in printed order (a string operation lists its implicit ones).</summary>
    public IReadOnlyList<RealModeOperand> Operands { get; }

    /// <summary>True for the instructions Capstone puts in its jump group: every <c>jmp</c>, conditional jump and <c>jcxz</c>.</summary>
    public bool IsJump { get; }

    /// <summary>
    /// The shape token: mnemonic, registers, segment prefixes, operand kinds and widths, with every
    /// immediate and displacement masked (e.g. <c>cmp m1[cs:D] imm1</c>).
    /// </summary>
    public string Shape { get; }

    /// <summary><c>mnemonic + " " + operands</c>, with the trailing space an operand-less instruction keeps.</summary>
    public string Text => Mnemonic + " " + OperandText;

    /// <summary>The same text with that trailing space removed.</summary>
    public string TrimmedText => OperandText.Length == 0 ? Mnemonic : Text;

    /// <inheritdoc/>
    public override string ToString() => $"+0x{Address:X4}  {Convert.ToHexString(Bytes)}  {TrimmedText}";

    private static string MakeShape(string mnemonic, RealModeOperand[] operands)
    {
        StringBuilder sb = new StringBuilder(mnemonic);
        foreach (RealModeOperand op in operands)
        {
            sb.Append(' ');
            switch (op.Kind)
            {
                case RealModeOperandKind.Register:
                    sb.Append(RealModeDisassembler.Name(op.Register));
                    break;
                case RealModeOperandKind.Immediate:
                    sb.Append("imm").Append(op.Size.ToString(CultureInfo.InvariantCulture));
                    break;
                default:
                    string seg = RealModeDisassembler.Name(op.Segment);
                    string size = op.Size.ToString(CultureInfo.InvariantCulture);
                    if (op.Base != RealModeRegister.None || op.Index != RealModeRegister.None)
                    {
                        sb.Append('m').Append(size).Append('[').Append(seg).Append(':')
                          .Append(RealModeDisassembler.Name(op.Base)).Append('+')
                          .Append(RealModeDisassembler.Name(op.Index))
                          .Append(op.Displacement != 0 ? "+d]" : "]");
                    }
                    else
                    {
                        sb.Append('m').Append(size).Append('[').Append(seg).Append(":D]");
                    }

                    break;
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// A table-driven decoder for the 8086 integer instruction set, printing each instruction the way
/// Capstone 5 does in 16-bit mode.
/// </summary>
/// <remarks>
/// <para>
/// The text is the product: the win-rule analysis matches on it the way matches on Capstone's, so the
/// rules are Capstone's, quirks included: numbers up to 9 print in decimal and larger ones in hex; a
/// sign-extended byte immediate prints signed except for <c>and</c>/<c>or</c>/<c>xor</c>, which print
/// it masked to the operand width; <c>les</c>/<c>lds</c> print a bare <c>ptr</c>, <c>lea</c>,
/// <c>lcall</c> and <c>ljmp</c> none; a segment prefix is printed whenever it is present, the default
/// one included; a branch target is address + size + displacement, not wrapped to 16 bits.  Three more
/// are Capstone's own: a <c>ds</c> prefix turns a near call or jump into <c>notrack call</c>/<c>notrack
/// jmp</c>; the /1 alias of the byte <c>test</c> prints its immediate signed; and <c>rcl</c> on memory
/// by the constant 1 prints no second operand.
/// </para>
/// <para>
/// Covered: the eight ALU operations in all six forms and the 0x80/0x81/0x82/0x83 group, <c>inc</c>,
/// <c>dec</c>, <c>push</c>, <c>pop</c> (registers, segment registers, memory), the short and near
/// jumps and calls, the <c>loop</c> family, <c>test</c>, <c>xchg</c>, the <c>mov</c> family
/// (register/memory, segment register, direct address, immediate), <c>lea</c>, <c>les</c>,
/// <c>lds</c>, the string operations with <c>rep</c>/<c>repe</c>/<c>repne</c>, the shift group,
/// the 0xF6/0xF7 and 0xFE/0xFF groups, <c>ret</c>/<c>retf</c>, <c>int</c>, <c>int3</c>, the flag
/// instructions, the BCD adjustments, <c>pushf</c>/<c>popf</c>/<c>sahf</c>/<c>lahf</c>, <c>nop</c>
/// and <c>hlt</c>.  Segment and repeat prefixes may repeat (the last of each kind counts, as in
/// Capstone); a repeat prefix is accepted only on a string operation.  Everything else (the 0x0F page,
/// operand- and address-size prefixes, <c>lock</c>, the 186+ opcodes, far direct calls, port I/O, the
/// FPU) is not decoded: <see cref="Decode"/> returns null, which callers treat as undecodable bytes.
/// </para>
/// </remarks>
public static class RealModeDisassembler
{
    /// <summary>The longest instruction the decoder accepts, prefixes included.</summary>
    public const int MaxInstructionLength = 15;

    private const int HexThreshold = 9;

    private enum Arg : byte
    {
        None,
        Eb, Ew,             // ModRM r/m
        Gb, Gw, Sw,         // ModRM reg field (general, segment)
        Mptr, Mlea, Mfar,   // memory-only r/m (les/lds, lea, lcall/ljmp)
        Ib, Iw, Ibs, Ibx,   // immediates (Ibs: byte sign-extended to the word operand; Ibx: byte printed signed)
        Jb, Jw,             // relative targets
        AL, AX, CL,         // fixed registers
        One, OneHidden,     // the constant 1 of the shift group (OneHidden: reported but not printed)
        Zb, Zw,             // register in the opcode's low three bits
        Ob, Ow,             // direct address
        Xb, Xw, Yb, Yw,     // string source ds:si, destination es:di
        ES, CS, SS, DS,     // fixed segment registers
    }

    private enum Kind : byte
    {
        Invalid,
        Plain,      // one mnemonic
        Group,      // mnemonic from the ModRM reg field
        Prefix,     // segment or repeat prefix
        String,     // string operation (takes a repeat prefix)
    }

    private sealed record Entry(Kind Kind, string Mnemonic, Arg A = Arg.None, Arg B = Arg.None, bool Jump = false,
                                string[]? Group = null);

    private static readonly Entry Invalid = new(Kind.Invalid, "");

    private static readonly string[] Alu = ["add", "or", "adc", "sbb", "and", "sub", "xor", "cmp"];
    private static readonly string[] Shifts = ["rol", "ror", "rcl", "rcr", "shl", "shr", "sal", "sar"];
    private static readonly string[] Unary = ["test", "test", "not", "neg", "mul", "imul", "div", "idiv"];
    private static readonly string[] IncDec = ["inc", "dec", "", "", "", "", "", ""];
    private static readonly string[] Indirect = ["inc", "dec", "call", "lcall", "jmp", "ljmp", "push", ""];
    private static readonly string[] PopGroup = ["pop", "", "", "", "", "", "", ""];
    private static readonly string[] MovGroup = ["mov", "", "", "", "", "", "", ""];

    private static readonly string[] JccLow = ["jo", "jno", "jb", "jae", "je", "jne", "jbe", "ja"];
    private static readonly string[] JccHigh = ["js", "jns", "jp", "jnp", "jl", "jge", "jle", "jg"];

    private static readonly RealModeRegister[] Rm16Base =
        [RealModeRegister.BX, RealModeRegister.BX, RealModeRegister.BP, RealModeRegister.BP,
         RealModeRegister.SI, RealModeRegister.DI, RealModeRegister.BP, RealModeRegister.BX];

    private static readonly RealModeRegister[] Rm16Index =
        [RealModeRegister.SI, RealModeRegister.DI, RealModeRegister.SI, RealModeRegister.DI,
         RealModeRegister.None, RealModeRegister.None, RealModeRegister.None, RealModeRegister.None];

    // Declared after the name lists it reads: static initializers run in textual order.
    private static readonly Entry[] Table = BuildTable();

    private static readonly string[] RegisterNames =
        Enum.GetValues<RealModeRegister>().Select(r => r == RealModeRegister.None ? "" : r.ToString().ToLowerInvariant()).ToArray();

    /// <summary>The lower-case name of a register ("" for none).</summary>
    public static string Name(RealModeRegister register) => RegisterNames[(int)register];

    /// <summary>
    /// Decodes the instruction at the start of <paramref name="code"/>, reporting it at
    /// <paramref name="address"/>.  Returns null when the bytes are not an instruction this decoder
    /// covers or are cut short.  Never throws.
    /// </summary>
    /// <param name="code">The bytes, starting at the instruction.  At most <see cref="MaxInstructionLength"/> are read.</param>
    /// <param name="address">The address to report (branch targets are relative to it).</param>
    public static RealModeInstruction? Decode(ReadOnlySpan<byte> code, int address)
    {
        if (code.Length > MaxInstructionLength)
        {
            code = code[..MaxInstructionLength];
        }

        RealModeRegister segment = RealModeRegister.None;
        byte repeat = 0;
        int p = 0;
        Entry entry;
        while (true)
        {
            if (p >= code.Length)
            {
                return null;
            }

            entry = Table[code[p]];
            if (entry.Kind != Kind.Prefix)
            {
                break;
            }

            if (entry.A == Arg.None)
            {
                repeat = code[p];
            }
            else
            {
                segment = SegmentOf(entry.A);
            }

            p++;
        }

        byte opcode = code[p++];
        Cursor cursor = new Cursor(code, p);
        switch (entry.Kind)
        {
            case Kind.Invalid:
                return null;
            case Kind.String:
                return DecodeString(code, address, opcode, entry, segment, repeat, ref cursor);
            default:
                if (repeat != 0)
                {
                    return null;
                }

                return DecodeGeneral(code, address, opcode, entry, segment, ref cursor);
        }
    }

    // ---- the table ---------------------------------------------------------------------------

    private static Entry[] BuildTable()
    {
        Entry[] t = new Entry[256];
        Array.Fill(t, Invalid);

        // The eight ALU operations share six forms at 8k+0 .. 8k+5.
        for (int k = 0; k < Alu.Length; k++)
        {
            int b = k << 3;
            t[b] = new(Kind.Plain, Alu[k], Arg.Eb, Arg.Gb);
            t[b + 1] = new(Kind.Plain, Alu[k], Arg.Ew, Arg.Gw);
            t[b + 2] = new(Kind.Plain, Alu[k], Arg.Gb, Arg.Eb);
            t[b + 3] = new(Kind.Plain, Alu[k], Arg.Gw, Arg.Ew);
            t[b + 4] = new(Kind.Plain, Alu[k], Arg.AL, Arg.Ib);
            t[b + 5] = new(Kind.Plain, Alu[k], Arg.AX, Arg.Iw);
        }

        // Segment register push/pop in the ALU block's sixth and seventh columns (no pop cs).
        Arg[] segs = [Arg.ES, Arg.CS, Arg.SS, Arg.DS];
        for (int k = 0; k < segs.Length; k++)
        {
            int b = (k << 3) + 6;
            t[b] = new(Kind.Plain, "push", segs[k]);
            if (segs[k] != Arg.CS)
            {
                t[b + 1] = new(Kind.Plain, "pop", segs[k]);
            }
        }

        // Segment override prefixes sit in the same column, one row down; BCD adjustments beside them.
        string[] bcd = ["daa", "das", "aaa", "aas"];
        for (int k = 0; k < segs.Length; k++)
        {
            int b = (k << 3) + 0x26;
            t[b] = new(Kind.Prefix, "", segs[k]);
            t[b + 1] = new(Kind.Plain, bcd[k]);
        }

        string[] rowFour = ["inc", "dec", "push", "pop"];
        for (int k = 0; k < rowFour.Length; k++)
        {
            for (int r = 0; r < 8; r++)
            {
                t[0x40 + (k << 3) + r] = new(Kind.Plain, rowFour[k], Arg.Zw);
            }
        }

        for (int c = 0; c < 8; c++)
        {
            t[0x70 + c] = new(Kind.Plain, JccLow[c], Arg.Jb, Jump: true);
            t[0x78 + c] = new(Kind.Plain, JccHigh[c], Arg.Jb, Jump: true);
        }

        t[0x80] = new(Kind.Group, "", Arg.Eb, Arg.Ib, Group: Alu);
        t[0x81] = new(Kind.Group, "", Arg.Ew, Arg.Iw, Group: Alu);
        t[0x82] = new(Kind.Group, "", Arg.Eb, Arg.Ib, Group: Alu);
        t[0x83] = new(Kind.Group, "", Arg.Ew, Arg.Ibs, Group: Alu);
        t[0x84] = new(Kind.Plain, "test", Arg.Eb, Arg.Gb);
        t[0x85] = new(Kind.Plain, "test", Arg.Ew, Arg.Gw);
        t[0x86] = new(Kind.Plain, "xchg", Arg.Eb, Arg.Gb);
        t[0x87] = new(Kind.Plain, "xchg", Arg.Ew, Arg.Gw);
        t[0x88] = new(Kind.Plain, "mov", Arg.Eb, Arg.Gb);
        t[0x89] = new(Kind.Plain, "mov", Arg.Ew, Arg.Gw);
        t[0x8A] = new(Kind.Plain, "mov", Arg.Gb, Arg.Eb);
        t[0x8B] = new(Kind.Plain, "mov", Arg.Gw, Arg.Ew);
        t[0x8C] = new(Kind.Plain, "mov", Arg.Ew, Arg.Sw);
        t[0x8D] = new(Kind.Plain, "lea", Arg.Gw, Arg.Mlea);
        t[0x8E] = new(Kind.Plain, "mov", Arg.Sw, Arg.Ew);
        t[0x8F] = new(Kind.Group, "", Arg.Ew, Group: PopGroup);

        t[0x90] = new(Kind.Plain, "nop");
        for (int r = 1; r < 8; r++)
        {
            t[0x90 + r] = new(Kind.Plain, "xchg", Arg.Zw, Arg.AX);
        }

        t[0x9C] = new(Kind.Plain, "pushf");
        t[0x9D] = new(Kind.Plain, "popf");
        t[0x9E] = new(Kind.Plain, "sahf");
        t[0x9F] = new(Kind.Plain, "lahf");

        t[0xA0] = new(Kind.Plain, "mov", Arg.AL, Arg.Ob);
        t[0xA1] = new(Kind.Plain, "mov", Arg.AX, Arg.Ow);
        t[0xA2] = new(Kind.Plain, "mov", Arg.Ob, Arg.AL);
        t[0xA3] = new(Kind.Plain, "mov", Arg.Ow, Arg.AX);
        t[0xA4] = new(Kind.String, "movsb", Arg.Yb, Arg.Xb);
        t[0xA5] = new(Kind.String, "movsw", Arg.Yw, Arg.Xw);
        t[0xA6] = new(Kind.String, "cmpsb", Arg.Xb, Arg.Yb);
        t[0xA7] = new(Kind.String, "cmpsw", Arg.Xw, Arg.Yw);
        t[0xA8] = new(Kind.Plain, "test", Arg.AL, Arg.Ib);
        t[0xA9] = new(Kind.Plain, "test", Arg.AX, Arg.Iw);
        t[0xAA] = new(Kind.String, "stosb", Arg.Yb, Arg.AL);
        t[0xAB] = new(Kind.String, "stosw", Arg.Yw, Arg.AX);
        t[0xAC] = new(Kind.String, "lodsb", Arg.AL, Arg.Xb);
        t[0xAD] = new(Kind.String, "lodsw", Arg.AX, Arg.Xw);
        t[0xAE] = new(Kind.String, "scasb", Arg.AL, Arg.Yb);
        t[0xAF] = new(Kind.String, "scasw", Arg.AX, Arg.Yw);

        for (int r = 0; r < 8; r++)
        {
            t[0xB0 + r] = new(Kind.Plain, "mov", Arg.Zb, Arg.Ib);
            t[0xB8 + r] = new(Kind.Plain, "mov", Arg.Zw, Arg.Iw);
        }

        t[0xC2] = new(Kind.Plain, "ret", Arg.Iw);
        t[0xC3] = new(Kind.Plain, "ret");
        t[0xC4] = new(Kind.Plain, "les", Arg.Gw, Arg.Mptr);
        t[0xC5] = new(Kind.Plain, "lds", Arg.Gw, Arg.Mptr);
        t[0xC6] = new(Kind.Group, "", Arg.Eb, Arg.Ib, Group: MovGroup);
        t[0xC7] = new(Kind.Group, "", Arg.Ew, Arg.Iw, Group: MovGroup);
        t[0xCA] = new(Kind.Plain, "retf", Arg.Iw);
        t[0xCB] = new(Kind.Plain, "retf");
        t[0xCC] = new(Kind.Plain, "int3");
        t[0xCD] = new(Kind.Plain, "int", Arg.Ib);

        t[0xD0] = new(Kind.Group, "", Arg.Eb, Arg.One, Group: Shifts);
        t[0xD1] = new(Kind.Group, "", Arg.Ew, Arg.One, Group: Shifts);
        t[0xD2] = new(Kind.Group, "", Arg.Eb, Arg.CL, Group: Shifts);
        t[0xD3] = new(Kind.Group, "", Arg.Ew, Arg.CL, Group: Shifts);

        t[0xE0] = new(Kind.Plain, "loopne", Arg.Jb);
        t[0xE1] = new(Kind.Plain, "loope", Arg.Jb);
        t[0xE2] = new(Kind.Plain, "loop", Arg.Jb);
        t[0xE3] = new(Kind.Plain, "jcxz", Arg.Jb, Jump: true);
        t[0xE8] = new(Kind.Plain, "call", Arg.Jw);
        t[0xE9] = new(Kind.Plain, "jmp", Arg.Jw, Jump: true);
        t[0xEB] = new(Kind.Plain, "jmp", Arg.Jb, Jump: true);

        t[0xF2] = new(Kind.Prefix, "");
        t[0xF3] = new(Kind.Prefix, "");
        t[0xF4] = new(Kind.Plain, "hlt");
        t[0xF5] = new(Kind.Plain, "cmc");
        t[0xF6] = new(Kind.Group, "", Arg.Eb, Group: Unary);
        t[0xF7] = new(Kind.Group, "", Arg.Ew, Group: Unary);
        t[0xF8] = new(Kind.Plain, "clc");
        t[0xF9] = new(Kind.Plain, "stc");
        t[0xFA] = new(Kind.Plain, "cli");
        t[0xFB] = new(Kind.Plain, "sti");
        t[0xFC] = new(Kind.Plain, "cld");
        t[0xFD] = new(Kind.Plain, "std");
        t[0xFE] = new(Kind.Group, "", Arg.Eb, Group: IncDec);
        t[0xFF] = new(Kind.Group, "", Arg.Ew, Group: Indirect);
        return t;
    }

    // ---- decoding ----------------------------------------------------------------------------

    private ref struct Cursor(ReadOnlySpan<byte> code, int position)
    {
        private readonly ReadOnlySpan<byte> _code = code;

        public int Position = position;

        public bool Ok = true;

        public byte U8()
        {
            if (Position >= _code.Length)
            {
                Ok = false;
                return 0;
            }

            return _code[Position++];
        }

        public int U16()
        {
            int lo = U8();
            int hi = U8();
            return lo | (hi << 8);
        }
    }

    private readonly record struct ModRm(int Mod, int Reg, int Rm);

    private static RealModeInstruction? DecodeGeneral(ReadOnlySpan<byte> code, int address, byte opcode, Entry entry,
                                                      RealModeRegister segment, ref Cursor cursor)
    {
        bool needsModRm = entry.Kind == Kind.Group || UsesModRm(entry.A) || UsesModRm(entry.B);
        ModRm modrm = default;
        if (needsModRm)
        {
            byte m = cursor.U8();
            if (!cursor.Ok)
            {
                return null;
            }

            modrm = new ModRm(m >> 6, (m >> 3) & 7, m & 7);
        }

        string mnemonic = entry.Mnemonic;
        bool jump = entry.Jump;
        Arg a = entry.A;
        Arg b = entry.B;
        if (entry.Kind == Kind.Group)
        {
            mnemonic = entry.Group![modrm.Reg];
            if (mnemonic.Length == 0)
            {
                return null;
            }

            if (opcode is 0xF6 or 0xF7 && modrm.Reg <= 1)
            {
                // test r/m, imm; Capstone prints the byte form of the /1 alias signed.
                b = opcode == 0xF7 ? Arg.Iw : modrm.Reg == 1 ? Arg.Ibx : Arg.Ib;
            }
            else if (opcode is 0xD0 or 0xD1 && modrm.Reg == 2 && modrm.Mod != 3)
            {
                b = Arg.OneHidden;                          // Capstone drops the ", 1" of rcl on memory
            }
            else if (opcode == 0xFF && modrm.Reg is 3 or 5)
            {
                a = Arg.Mfar;                               // lcall / ljmp through memory
            }

            jump = opcode == 0xFF && modrm.Reg is 4 or 5;
        }

        // A ds prefix in front of a near call or jump is read as the CET "notrack" prefix.
        if (segment == RealModeRegister.DS
            && (opcode is 0xE8 or 0xE9 or 0xEB || (opcode == 0xFF && modrm.Reg is 2 or 4)))
        {
            mnemonic = "notrack " + mnemonic;
        }

        // A memory-only operand refuses a register ModRM; segment registers stop at DS.
        if ((IsMemoryOnly(a) || IsMemoryOnly(b)) && modrm.Mod == 3)
        {
            return null;
        }

        if ((a == Arg.Sw || b == Arg.Sw) && modrm.Reg > 3)
        {
            return null;
        }

        // The ModRM displacement comes before any immediate in the encoding.
        RealModeOperand memory = default(RealModeOperand);
        if (needsModRm && modrm.Mod != 3)
        {
            memory = Memory(ref cursor, modrm, segment);
        }

        Arg[] args = a == Arg.None ? [] : b == Arg.None ? [a] : [a, b];
        RealModeOperand[] ops = new RealModeOperand[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            ops[i] = Operand(args[i], ref cursor, opcode, modrm, memory, segment);
        }

        if (!cursor.Ok)
        {
            return null;
        }

        byte[] bytes = code[..cursor.Position].ToArray();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is Arg.Jb or Arg.Jw)
            {
                // Capstone reports address + size + displacement, masked to 32 bits only.
                ops[i] = ops[i] with { Immediate = ((long)address + bytes.Length + ops[i].Immediate) & 0xFFFFFFFFL };
            }
        }

        return new RealModeInstruction(address, bytes, mnemonic, Render(ops, args), ops, jump);
    }

    private static RealModeInstruction DecodeString(ReadOnlySpan<byte> code, int address, byte opcode, Entry entry,
                                                    RealModeRegister segment, byte repeat, ref Cursor cursor)
    {
        string mnemonic = entry.Mnemonic;
        if (repeat != 0)
        {
            bool compares = opcode is 0xA6 or 0xA7 or 0xAE or 0xAF;
            string prefix = repeat == 0xF2 ? "repne" : compares ? "repe" : "rep";
            mnemonic = prefix + " " + mnemonic;
        }

        Arg[] args = [entry.A, entry.B];
        RealModeOperand[] ops = [StringOperand(entry.A, segment), StringOperand(entry.B, segment)];
        byte[] bytes = code[..cursor.Position].ToArray();
        return new RealModeInstruction(address, bytes, mnemonic, Render(ops, args), ops, isJump: false);
    }

    private static RealModeOperand StringOperand(Arg arg, RealModeRegister segment) => arg switch
    {
        // The source takes the segment prefix; the destination is always es.
        Arg.Xb => new(RealModeOperandKind.Memory, 1, Segment: segment, Base: RealModeRegister.SI),
        Arg.Xw => new(RealModeOperandKind.Memory, 2, Segment: segment, Base: RealModeRegister.SI),
        Arg.Yb => new(RealModeOperandKind.Memory, 1, Segment: RealModeRegister.ES, Base: RealModeRegister.DI),
        Arg.Yw => new(RealModeOperandKind.Memory, 2, Segment: RealModeRegister.ES, Base: RealModeRegister.DI),
        Arg.AL => new(RealModeOperandKind.Register, 1, RealModeRegister.AL),
        _ => new(RealModeOperandKind.Register, 2, RealModeRegister.AX),
    };

    private static bool UsesModRm(Arg arg) =>
        arg is Arg.Eb or Arg.Ew or Arg.Gb or Arg.Gw or Arg.Sw or Arg.Mptr or Arg.Mlea or Arg.Mfar;

    private static bool IsMemoryOnly(Arg arg) => arg is Arg.Mptr or Arg.Mlea or Arg.Mfar;

    private static RealModeRegister SegmentOf(Arg arg) => arg switch
    {
        Arg.ES => RealModeRegister.ES,
        Arg.CS => RealModeRegister.CS,
        Arg.SS => RealModeRegister.SS,
        _ => RealModeRegister.DS,
    };

    private static RealModeRegister Reg8(int n) => (RealModeRegister)((int)RealModeRegister.AL + n);

    private static RealModeRegister Reg16(int n) => (RealModeRegister)((int)RealModeRegister.AX + n);

    private static RealModeRegister Seg(int n) => (RealModeRegister)((int)RealModeRegister.ES + n);

    private static RealModeOperand Memory(ref Cursor cursor, ModRm modrm, RealModeRegister segment)
    {
        if (modrm.Mod == 0 && modrm.Rm == 6)
        {
            // A direct address: reported sign-extended, printed masked to 16 bits.
            return new(RealModeOperandKind.Memory, 0, Segment: segment, Displacement: (short)cursor.U16());
        }

        int disp = modrm.Mod switch
        {
            1 => (sbyte)cursor.U8(),
            2 => (short)cursor.U16(),
            _ => 0,
        };
        return new(RealModeOperandKind.Memory, 0, Segment: segment,
                   Base: Rm16Base[modrm.Rm], Index: Rm16Index[modrm.Rm], Displacement: disp);
    }

    private static RealModeOperand Operand(Arg arg, ref Cursor cursor, byte opcode, ModRm modrm,
                                           RealModeOperand memory, RealModeRegister segment)
    {
        switch (arg)
        {
            case Arg.Eb when modrm.Mod == 3:
                return new(RealModeOperandKind.Register, 1, Reg8(modrm.Rm));
            case Arg.Ew when modrm.Mod == 3:
                return new(RealModeOperandKind.Register, 2, Reg16(modrm.Rm));
            case Arg.Eb:
                return memory with { Size = 1 };
            case Arg.Ew:
            case Arg.Mptr:
            case Arg.Mlea:
                return memory with { Size = 2 };
            case Arg.Mfar:
                return memory with { Size = 4 };
            case Arg.Gb:
                return new(RealModeOperandKind.Register, 1, Reg8(modrm.Reg));
            case Arg.Gw:
                return new(RealModeOperandKind.Register, 2, Reg16(modrm.Reg));
            case Arg.Sw:
                return new(RealModeOperandKind.Register, 2, Seg(modrm.Reg));
            case Arg.Ib:
                return new(RealModeOperandKind.Immediate, 1, Immediate: cursor.U8());
            case Arg.Iw:
                return new(RealModeOperandKind.Immediate, 2, Immediate: cursor.U16());
            case Arg.Ibx:
                return new(RealModeOperandKind.Immediate, 1, Immediate: (sbyte)cursor.U8());
            case Arg.Ibs:
                long v = (sbyte)cursor.U8();
                if (modrm.Reg is 1 or 4 or 6)
                {
                    v &= 0xFFFF;                                // or / and / xor report the masked word
                }

                return new(RealModeOperandKind.Immediate, 2, Immediate: v);
            case Arg.Jb:
                return new(RealModeOperandKind.Immediate, 2, Immediate: (sbyte)cursor.U8());
            case Arg.Jw:
                return new(RealModeOperandKind.Immediate, 2, Immediate: (short)cursor.U16());
            case Arg.AL:
                return new(RealModeOperandKind.Register, 1, RealModeRegister.AL);
            case Arg.AX:
                return new(RealModeOperandKind.Register, 2, RealModeRegister.AX);
            case Arg.CL:
                return new(RealModeOperandKind.Register, 1, RealModeRegister.CL);
            case Arg.One:
                return new(RealModeOperandKind.Immediate, opcode == 0xD0 ? 1 : 2, Immediate: 1);
            case Arg.OneHidden:
                return new(RealModeOperandKind.Immediate, 0, Immediate: 1);
            case Arg.Zb:
                return new(RealModeOperandKind.Register, 1, Reg8(opcode & 7));
            case Arg.Zw:
                return new(RealModeOperandKind.Register, 2, Reg16(opcode & 7));
            case Arg.Ob:
                return new(RealModeOperandKind.Memory, 1, Segment: segment, Displacement: cursor.U16());
            case Arg.Ow:
                return new(RealModeOperandKind.Memory, 2, Segment: segment, Displacement: cursor.U16());
            default:
                return new(RealModeOperandKind.Register, 2, SegmentOf(arg));
        }
    }

    // ---- text --------------------------------------------------------------------------------

    private static string Render(RealModeOperand[] ops, Arg[] args)
    {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < ops.Length; i++)
        {
            if (args[i] == Arg.OneHidden)
            {
                continue;
            }

            if (i > 0)
            {
                sb.Append(", ");
            }

            RealModeOperand op = ops[i];
            switch (op.Kind)
            {
                case RealModeOperandKind.Register:
                    sb.Append(Name(op.Register));
                    break;
                case RealModeOperandKind.Immediate:
                    sb.Append(Number(op.Immediate));
                    break;
                default:
                    sb.Append(args[i] switch
                    {
                        Arg.Mptr => "ptr ",
                        Arg.Mlea or Arg.Mfar => string.Empty,
                        _ => op.Size == 1 ? "byte ptr " : "word ptr ",
                    });
                    AppendMemory(sb, op, directAddress: args[i] is Arg.Ob or Arg.Ow);
                    break;
            }
        }

        return sb.ToString();
    }

    private static void AppendMemory(StringBuilder sb, RealModeOperand op, bool directAddress)
    {
        if (op.Segment != RealModeRegister.None)
        {
            sb.Append(Name(op.Segment)).Append(':');
        }

        sb.Append('[');
        if (op.Base != RealModeRegister.None)
        {
            sb.Append(Name(op.Base));
            if (op.Index != RealModeRegister.None)
            {
                sb.Append(" + ").Append(Name(op.Index));
            }

            if (op.Displacement > 0)
            {
                sb.Append(" + ").Append(Number(op.Displacement));
            }
            else if (op.Displacement < 0)
            {
                sb.Append(" - ").Append(Number(-(long)op.Displacement));
            }
        }
        else
        {
            sb.Append(Number(directAddress ? op.Displacement : op.Displacement & 0xFFFF));
        }

        sb.Append(']');
    }

    /// <summary>A number the way Capstone prints it: up to 9 in decimal, beyond that in hex, negatives with a minus sign.</summary>
    /// <param name="value">The number.</param>
    public static string Number(long value)
    {
        if (value < 0)
        {
            long magnitude = -value;
            return magnitude > HexThreshold
                ? "-0x" + magnitude.ToString("x", CultureInfo.InvariantCulture)
                : "-" + magnitude.ToString(CultureInfo.InvariantCulture);
        }

        return value > HexThreshold
            ? "0x" + value.ToString("x", CultureInfo.InvariantCulture)
            : value.ToString(CultureInfo.InvariantCulture);
    }
}
