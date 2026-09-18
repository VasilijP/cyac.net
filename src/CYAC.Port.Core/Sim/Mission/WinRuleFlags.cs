namespace CYAC.Port.Core.Sim.Mission;

/// <summary>
/// The four 8086 status flags an x86 <c>cmp</c> leaves, and the ten condition codes the win-rule IR
/// records.
/// </summary>
/// <remarks>
/// <para>
/// The IR keeps every test's LITERAL jump-away condition code because the corpus spells the same
/// semantic rule two ways — <c>cmp 2; jl</c> in BLOOD.S and <c>cmp 2; jb</c> in FISH.S ("BODY IR").
/// A port that normalised them to "&gt;= 2" would be right for every shipped threshold and wrong
/// the moment a counter or an argument goes past 0x7F, so the evaluator computes the real flags and
/// asks the real condition.
/// </para>
/// <para>
/// The encodings the tests come from are fixed (same §2, "INSTRUCTION SELECTION"):
/// <c>cmp word [bp+6],imm</c> = <c>83 7E 06 ib</c> (the imm8 is SIGN-EXTENDED to 16 bits),
/// <c>cmp byte [bp+6],imm</c> = <c>80 7E 06 ib</c>, <c>cmp byte cs:[c],imm</c> =
/// <c>2E 80 3E cw ib</c> and <c>cmp word [bp+0xA],imm</c> = <c>83 7E 0A ib</c>.
/// </para>
/// </remarks>
public readonly record struct WinRuleFlags(bool Carry, bool Zero, bool Sign, bool Overflow)
{
    /// <summary>
    /// The flags of <c>cmp</c> on a BYTE operand — the <c>80 /7</c> and <c>2E 80 3E</c> forms.
    /// </summary>
    /// <param name="left">The operand read from memory; only its low 8 bits are compared.</param>
    /// <param name="immediate">The instruction's <c>ib</c>.</param>
    public static WinRuleFlags CompareByte(int left, int immediate) =>
        Compare(left & 0xFF, immediate & 0xFF, 0xFF, 0x80);

    /// <summary>
    /// The flags of <c>cmp</c> on a WORD operand against a SIGN-EXTENDED imm8 — the <c>83 /7</c>
    /// form the authoring compiler always used.
    /// </summary>
    /// <param name="left">The operand word.</param>
    /// <param name="immediate">The instruction's <c>ib</c>, sign-extended to 16 bits.</param>
    public static WinRuleFlags CompareWord(int left, int immediate) =>
        Compare(left & 0xFFFF, (sbyte)immediate & 0xFFFF, 0xFFFF, 0x8000);

    private static WinRuleFlags Compare(int a, int b, int mask, int signBit)
    {
        int result = (a - b) & mask;
        return new WinRuleFlags(
            Carry: a < b,
            Zero: result == 0,
            Sign: (result & signBit) != 0,
            Overflow: ((a ^ b) & (a ^ result) & signBit) != 0);
    }

    /// <summary>Whether a condition code's branch would be TAKEN on these flags.</summary>
    /// <param name="conditionCode">One of the ten codes the IR uses, e.g. <c>"jl"</c>.</param>
    /// <exception cref="NotSupportedException">
    /// The IR named a condition code this evaluator does not know.  The IR is literal on purpose, so
    /// an unknown spelling is a hole in the port, never something to approximate.
    /// </exception>
    public bool Taken(string conditionCode) => conditionCode switch
    {
        "jb" => Carry,
        "jae" => !Carry,
        "je" => Zero,
        "jne" => !Zero,
        "jbe" => Carry || Zero,
        "ja" => !Carry && !Zero,
        "jl" => Sign != Overflow,
        "jge" => Sign == Overflow,
        "jle" => Zero || (Sign != Overflow),
        "jg" => !Zero && (Sign == Overflow),
        _ => throw new NotSupportedException(
            $"the win-rule IR names condition code '{conditionCode}', which this evaluator does not "
                + "model"),
    };

    /// <summary>The condition codes the evaluator models —'s table.</summary>
    public static IReadOnlyList<string> KnownConditionCodes { get; } =
        ["jb", "jae", "je", "jne", "jbe", "ja", "jl", "jge", "jle", "jg"];
}
