using System.Text.Json.Serialization;

namespace CYAC.Formats.EaLib;

/// <summary>
/// The shapes of the win-rule PARAMETER catalog: per mission, every byte-stable editable scalar of the
/// <c>.S</c> win-condition module, with the helpers that read and patch one.
/// </summary>
/// <remarks>
/// <para>
/// A mission lists its win thresholds (the immediate of a predicate helper's
/// <c>cmp byte cs:[counter], N</c>), the argument bounds of its event rules, its debrief-only
/// thresholds and, for the bespoke missions, clock triggers, landing radii and other scalars.  Each
/// parameter carries its byte offset, its encoding (imm8/imm16), its range, the instruction bytes in
/// front of the immediate, and how it was verified: patched and re-extracted by the concrete-execution
/// rule extractor ("semantic"), or re-disassembled in place ("syntactic").
/// </para>
/// <para>
/// <see cref="WinRuleParamCollector"/> derives the catalog from the module bytes.  Against a body that
/// may already be modded, a caller checks <see cref="ContextMatches"/> and reads the current value with
/// <see cref="ReadValue"/> rather than trusting <see cref="Param.Value"/>.
/// </para>
/// <para>
/// An immediate reads by its instruction's opcode (<see cref="FormOf"/>): the byte operations
/// (the <c>0x80</c> group, <c>cmp</c>/<c>sub</c>/<c>add</c>/<c>and</c>/<c>or</c>/<c>xor al, imm8</c>,
/// <c>mov r8, imm8</c>) take a whole byte, 0..255; the <c>0x83</c> group sign-extends its byte to the
/// word it works on, −128..127; a word immediate reads signed.  Every read and write of a parameter
/// value goes through that one rule, so a byte threshold of <c>0xC8</c> is 200 wherever it is shown.
/// </para>
/// </remarks>
public sealed class WinRulesCatalog
{
    /// <summary>One mission's parameters and rule summary.</summary>
    public sealed class Mission
    {
        /// <summary>"template" (the parameter-only rule model covers it 100%) or "bespoke".</summary>
        [JsonPropertyName("class")] public string Class { get; set; } = "";
        [JsonPropertyName("bespoke_slots")] public List<string> BespokeSlots { get; set; } = new();
        [JsonPropertyName("table_off")] public int TableOff { get; set; }
        [JsonPropertyName("block_file_off")] public int BlockFileOff { get; set; }
        [JsonPropertyName("block_size")] public int BlockSize { get; set; }
        [JsonPropertyName("params")] public List<Param> Params { get; set; } = new();
        /// <summary>A one-line description of a bespoke module's idiom.</summary>
        [JsonPropertyName("idiom")] public string? Idiom { get; set; }
        /// <summary>The briefing copy export 0 makes, when it makes one.</summary>
        [JsonPropertyName("briefing")] public BriefingCopy? Briefing { get; set; }
        [JsonPropertyName("rule_lines")] public List<string> RuleLines { get; set; } = new();

        public bool IsTemplate => Class == "template";
    }

    /// <summary>The (source, length) copy of the briefing; read-only, since editing the text moves the module layout.</summary>
    public sealed class BriefingCopy
    {
        [JsonPropertyName("src")] public int Src { get; set; }
        [JsonPropertyName("len")] public int Len { get; set; }
        [JsonPropertyName("editable")] public bool Editable { get; set; }
        [JsonPropertyName("note")] public string Note { get; set; } = "";
    }

    /// <summary>How an immediate's bytes read as a number (<see cref="FormOf"/>).</summary>
    public enum ImmediateForm
    {
        /// <summary>A byte operation's immediate: 0..255.</summary>
        UnsignedByte,

        /// <summary>The <c>0x83</c> group's byte, sign-extended to the operand word: −128..127.</summary>
        SignExtendedByte,

        /// <summary>A word immediate, read signed: −32768..32767.</summary>
        Word,
    }

    public sealed class Param
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("kind")] public string Kind { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("meaning")] public string Meaning { get; set; } = "";
        /// <summary>The value the module carried when the catalog was made.</summary>
        [JsonPropertyName("value")] public int Value { get; set; }
        /// <summary>"imm8" or "imm16".  imm8 may be sign-extended (opcode 0x83) — Max stays ≤ 0x7F.</summary>
        [JsonPropertyName("encoding")] public string Encoding { get; set; } = "";
        [JsonPropertyName("block_offset")] public int BlockOffset { get; set; }
        [JsonPropertyName("insn")] public string Insn { get; set; } = "";
        [JsonPropertyName("insn_block_offset")] public int InsnBlockOffset { get; set; }
        /// <summary>Hex of the instruction bytes MINUS the trailing immediate — the live-validation anchor.</summary>
        [JsonPropertyName("context_bytes")] public string ContextBytes { get; set; } = "";
        [JsonPropertyName("min")] public int Min { get; set; }
        [JsonPropertyName("max")] public int Max { get; set; }
        [JsonPropertyName("editable")] public bool Editable { get; set; }
        [JsonPropertyName("counter")] public int? Counter { get; set; }
        /// <summary>True when the win-predicate helper is ALSO called by get_debrief_text —
        /// one patch drives both the in-flight advisor and the debrief scoring.</summary>
        [JsonPropertyName("shared_with_debrief")] public bool SharedWithDebrief { get; set; }
        /// <summary>Byte offset of the immediate in the DECOMPRESSED .S body.</summary>
        [JsonPropertyName("file_offset")] public int FileOffset { get; set; }
        /// <summary>"semantic" | "syntactic" | "inert" | "skipped" | "FAILED: …".</summary>
        [JsonPropertyName("verified")] public string Verified { get; set; } = "";

        public int EncodingBytes => Encoding == "imm16" ? 2 : 1;

        /// <summary>How the immediate reads, from the opcode in <see cref="ContextBytes"/>.</summary>
        [JsonIgnore]
        public ImmediateForm Form => FormOf(Convert.FromHexString(ContextBytes), EncodingBytes);
    }

    /// <summary>
    /// The form of the immediate that ends an instruction, from the instruction's bytes in front of it
    /// (its prefixes and opcode first) and the immediate's width.
    /// </summary>
    /// <param name="instructionHead">The instruction without its immediate (a parameter's anchor bytes).</param>
    /// <param name="width">The immediate's width in bytes, 1 or 2.</param>
    public static ImmediateForm FormOf(ReadOnlySpan<byte> instructionHead, int width)
    {
        if (width != 1)
        {
            return ImmediateForm.Word;
        }

        int i = SkipPrefixes(instructionHead);
        return i < instructionHead.Length && instructionHead[i] == 0x83
            ? ImmediateForm.SignExtendedByte
            : ImmediateForm.UnsignedByte;
    }

    /// <summary>The number an immediate's bytes stand for (little-endian).</summary>
    /// <param name="immediate">The immediate's bytes: one for a byte form, two for a word.</param>
    /// <param name="form">Its form.</param>
    public static int ReadImmediate(ReadOnlySpan<byte> immediate, ImmediateForm form) => form switch
    {
        ImmediateForm.UnsignedByte => immediate[0],
        ImmediateForm.SignExtendedByte => unchecked((sbyte)immediate[0]),
        _ => unchecked((short)(immediate[0] | (immediate[1] << 8))),
    };

    /// <summary>The values an immediate of <paramref name="form"/> can hold.</summary>
    /// <param name="form">The form.</param>
    public static (int Min, int Max) RangeOf(ImmediateForm form) => form switch
    {
        ImmediateForm.UnsignedByte => (0, byte.MaxValue),
        ImmediateForm.SignExtendedByte => (sbyte.MinValue, sbyte.MaxValue),
        _ => (short.MinValue, short.MaxValue),
    };

    /// <summary>The index of an instruction's first byte after its segment, lock and repeat prefixes.</summary>
    /// <param name="instruction">The instruction's bytes.</param>
    public static int SkipPrefixes(ReadOnlySpan<byte> instruction)
    {
        int i = 0;
        while (i < instruction.Length && instruction[i] is 0x26 or 0x2E or 0x36 or 0x3E or 0xF0 or 0xF2 or 0xF3)
        {
            i++;
        }

        return i;
    }

    /// <summary>
    /// Do the instruction bytes around the immediate still match the catalog?  False means the opened
    /// module differs structurally (modded or foreign) — the parameter must not be offered.
    /// </summary>
    public static bool ContextMatches(byte[] decompressedBody, Mission m, Param p)
    {
        byte[] ctx = Convert.FromHexString(p.ContextBytes);
        int insnFile = m.BlockFileOff + p.InsnBlockOffset;
        if (insnFile < 0 || insnFile + ctx.Length + p.EncodingBytes > decompressedBody.Length)
            return false;
        return decompressedBody.AsSpan(insnFile, ctx.Length).SequenceEqual(ctx)
            && p.FileOffset == insnFile + ctx.Length;
    }

    /// <summary>Read the CURRENT immediate value from a decompressed .S body (LE, by <see cref="Param.Form"/>).</summary>
    public static int ReadValue(byte[] decompressedBody, Param p) =>
        ReadImmediate(decompressedBody.AsSpan(p.FileOffset, p.EncodingBytes), p.Form);

    /// <summary>The immediate's byte image for <paramref name="value"/> (LE).</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is outside the parameter's range, or does not fit its immediate's form.
    /// </exception>
    public static byte[] ValueBytes(Param p, int value)
    {
        if (value < p.Min || value > p.Max)
            throw new ArgumentOutOfRangeException(nameof(value),
                $"{p.Id}: {value} outside [{p.Min}..{p.Max}]");
        (int lo, int hi) = RangeOf(p.Form);
        if (value < lo || value > hi)
            throw new ArgumentOutOfRangeException(nameof(value),
                $"{p.Id}: {value} does not fit its {p.Form} immediate [{lo}..{hi}]");
        return p.EncodingBytes == 1
            ? new[] { (byte)value }
            : new[] { (byte)value, (byte)(value >> 8) };
    }
}
