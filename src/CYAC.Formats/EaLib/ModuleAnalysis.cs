namespace CYAC.Formats.EaLib;

/// <summary>One swept function of a mission module: an export or a helper it calls.</summary>
public sealed class ModuleFunction
{
    private readonly Insn[] _code;
    private Dictionary<int, Insn>? _byAddress;

    internal ModuleFunction(int slot, int offset, int end, Insn[] code, IReadOnlyList<long> calls)
    {
        Slot = slot;
        Offset = offset;
        End = end;
        _code = code;
        Calls = calls;
        Instructions = Array.ConvertAll(code, i => i.I);
    }

    /// <summary>The export slot (0..4), or -1 for a helper.</summary>
    public int Slot { get; }

    /// <summary>Where the sweep started.</summary>
    public int Offset { get; }

    /// <summary>Where the sweep stopped (the end of the terminating return).</summary>
    public int End { get; }

    /// <summary>The swept instructions, in address order.</summary>
    public IReadOnlyList<RealModeInstruction> Instructions { get; }

    /// <summary>The targets of the direct near calls, in instruction order (repeats kept).</summary>
    public IReadOnlyList<long> Calls { get; }

    /// <summary><c>slotN</c> for an export, <c>helper+0xNN</c> for a helper.</summary>
    public string Label => Slot >= 0 ? $"slot{Slot}" : $"helper+{PyText.Hex(Offset)}";

    internal Insn[] Code => _code;

    internal Dictionary<int, Insn> ByAddress => _byAddress ??= Index(_code);

    internal static Dictionary<int, Insn> Index(IEnumerable<Insn> code)
    {
        Dictionary<int, Insn> map = new Dictionary<int, Insn>();
        foreach (Insn i in code)
        {
            map[i.Address] = i;
        }

        return map;
    }
}

/// <summary>
/// The disassembly of one mission module: the five exported functions and, transitively, the helpers
/// they call.
/// </summary>
/// <remarks>
/// A function is swept linearly from its entry and ends at the first <c>ret</c>/<c>retf</c> that lies
/// beyond every jump target seen so far (or at an indirect <c>jmp</c> under the same condition).  The
/// decisions are taken on the instruction text, as in the Python tool.  One addition: the whole
/// analysis may decode at most <see cref="InstructionBudget"/> instructions, so a hostile block
/// full of calls cannot make it quadratic.
/// </remarks>
public sealed class ModuleAnalysis
{
    /// <summary>The most instructions one analysis decodes before it gives up.</summary>
    public const int InstructionBudget = 1 << 17;

    private ModuleAnalysis(byte[] block, int[] exports, ModuleFunction[] functions, List<ModuleFunction> helpers)
    {
        Block = block;
        ExportOffsets = exports;
        Exports = functions;
        HelpersInDiscoveryOrder = helpers;
        Helpers = helpers.ToDictionary(h => (long)h.Offset);
    }

    /// <summary>The module bytes (export table first).</summary>
    public byte[] Block { get; }

    /// <summary>The five export offsets.</summary>
    public IReadOnlyList<int> ExportOffsets { get; }

    /// <summary>The five exported functions, in slot order.</summary>
    public IReadOnlyList<ModuleFunction> Exports { get; }

    /// <summary>The helpers by entry offset.</summary>
    public IReadOnlyDictionary<long, ModuleFunction> Helpers { get; }

    /// <summary>The helpers in the order the closure found them.</summary>
    public IReadOnlyList<ModuleFunction> HelpersInDiscoveryOrder { get; }

    /// <summary>
    /// Disassembles <paramref name="block"/>.  Returns false with the reason (the Python tool's
    /// message) when a function runs off the block or meets bytes the decoder does not cover.
    /// Never throws.
    /// </summary>
    /// <param name="block">The module bytes.</param>
    /// <param name="exports">The five export offsets (normally the block's first ten bytes).</param>
    /// <param name="analysis">The result.</param>
    /// <param name="error">The reason, when it fails.</param>
    public static bool TryAnalyze(ReadOnlySpan<byte> block, IReadOnlyList<int> exports,
                                  out ModuleAnalysis? analysis, out string? error)
    {
        try
        {
            analysis = Analyze(block.ToArray(), exports);
            error = null;
            return true;
        }
        catch (PyError e)
        {
            analysis = null;
            error = e.Message;
            return false;
        }
        catch (Exception e)
        {
            analysis = null;
            error = $"internal error: {e.GetType().Name}: {e.Message}";
            return false;
        }
    }

    /// <summary>The five export offsets a block starts with, or null when it is shorter than ten bytes.</summary>
    /// <param name="block">The module bytes.</param>
    public static int[]? ReadExports(ReadOnlySpan<byte> block)
    {
        if (block.Length < 10)
        {
            return null;
        }

        int[] offs = new int[5];
        for (int k = 0; k < 5; k++)
        {
            offs[k] = block[2 * k] | (block[(2 * k) + 1] << 8);
        }

        return offs;
    }

    internal static ModuleAnalysis Analyze(byte[] block, IReadOnlyList<int> exports)
    {
        int budget = InstructionBudget;
        ModuleFunction[] functions = new ModuleFunction[exports.Count];
        SortedSet<long> helperOffsets = new SortedSet<long>();
        for (int k = 0; k < exports.Count; k++)
        {
            functions[k] = Sweep(block, exports[k], k, ref budget);
            helperOffsets.UnionWith(functions[k].Calls);
        }

        // The closure pops the highest pending offset first, as the Python list does.
        List<ModuleFunction> helpers = new List<ModuleFunction>();
        HashSet<long> seen = new HashSet<long>();
        List<long> todo = new List<long>(helperOffsets);
        while (todo.Count > 0)
        {
            long h = todo[^1];
            todo.RemoveAt(todo.Count - 1);
            if (!seen.Add(h))
            {
                continue;
            }

            ModuleFunction helper = Sweep(block, h, -1, ref budget);
            helpers.Add(helper);
            foreach (long c in helper.Calls)
            {
                if (!seen.Contains(c))
                {
                    todo.Add(c);
                }
            }
        }

        return new ModuleAnalysis(block, exports.ToArray(), functions, helpers);
    }

    /// <summary><c>linear_sweep</c>.</summary>
    private static ModuleFunction Sweep(byte[] block, long start, int slot, ref int budget)
    {
        List<Insn> code = new List<Insn>();
        List<long> calls = new List<long>();
        long maxTarget = start;
        long addr = start;
        while (addr < block.Length)
        {
            if (--budget < 0)
            {
                throw PyError.Value($"analysis budget of {InstructionBudget} instructions exceeded at +{PyText.Hex(addr)}");
            }

            int at = (int)addr;
            RealModeInstruction? decoded = RealModeDisassembler.Decode(block.AsSpan(at, Math.Min(16, block.Length - at)), at);
            if (decoded is null)
            {
                throw PyError.Value($"undecodable byte at +{PyText.Hex(addr)}");
            }

            Insn ins = new Insn(decoded);
            code.Add(ins);
            if (decoded.IsJump && PyText.TryInt(ins.O, 16, out long target))
            {
                maxTarget = Math.Max(maxTarget, target);
            }

            if (ins.M == "call" && PyText.TryInt(ins.O, 16, out long callee))
            {
                calls.Add(callee);    // an indirect call's text is kept by the Python tool but never used
            }

            long end = addr + decoded.Size;
            if (ins.M is "ret" or "retf" && end > maxTarget)
            {
                return Done(end);
            }

            if (ins.M == "jmp" && end > maxTarget && !ins.O.StartsWith("0x", StringComparison.Ordinal))
            {
                return Done(end);     // an indirect jmp ends the function the same way
            }

            addr = end;
        }

        throw PyError.Value($"ran off block end from +{PyText.Hex(start)}");

        ModuleFunction Done(long end) => new(slot, (int)start, (int)end, code.ToArray(), calls);
    }

    /// <summary>
    /// <c>split_template</c>: checks the far-call frame (<c>push bp; mov bp,sp; push si; push di;
    /// mov cs:[SAVE],ds; mov ax,cs; mov ds,ax</c>) and returns the instructions between it and the first
    /// <c>mov ds,cs:[SAVE]</c>.
    /// </summary>
    internal static ArraySegment<Insn> SplitTemplate(Insn[] code)
    {
        const string SaveHead = "mov word ptr cs:[";
        const string SaveTail = "], ds";
        int n = Math.Min(7, code.Length);
        string[] got = new string[n];
        for (int i = 0; i < n; i++)
        {
            got[i] = code[i].I.TrimmedText;
        }

        if (n < 5)
        {
            throw PyError.Index();
        }

        if (!(got[4].StartsWith(SaveHead, StringComparison.Ordinal) && got[4].EndsWith(SaveTail, StringComparison.Ordinal)))
        {
            throw PyError.Value($"nonstandard prologue: {PyText.Repr(got)}");
        }

        string save = got[4][SaveHead.Length..^SaveTail.Length];
        got[4] = "mov word ptr cs:[SAVE], ds";
        if (!got.AsSpan().SequenceEqual(Prologue))
        {
            throw PyError.Value($"nonstandard prologue: {PyText.Repr(got)}");
        }

        string epilogue = $"mov ds, word ptr cs:[{save}]";
        for (int k = 7; k < code.Length; k++)
        {
            if (code[k].I.Text == epilogue)
            {
                return new ArraySegment<Insn>(code, 7, k - 7);
            }
        }

        throw PyError.Value("no epilogue");
    }

    private static readonly string[] Prologue =
    [
        "push bp", "mov bp, sp", "push si", "push di",
        "mov word ptr cs:[SAVE], ds", "mov ax, cs", "mov ds, ax",
    ];
}

/// <summary>
/// One instruction as the rule analysis sees it: its text, plus the numbers the analysis parses out
/// of that text, each parsed once and failing the same way every time it is asked for.
/// </summary>
internal sealed class Insn(RealModeInstruction i)
{
    private Parsed _lastImmediate;
    private Parsed _csOffset;
    private Parsed _target;
    private Parsed _incOffset;

    private struct Parsed
    {
        public bool Done;
        public long Value;
        public PyError? Error;
    }

    public RealModeInstruction I { get; } = i;

    /// <summary>The mnemonic.</summary>
    public string M { get; } = i.Mnemonic;

    /// <summary>The operand text.</summary>
    public string O { get; } = i.OperandText;

    public int Address => I.Address;

    public int Size => I.Size;

    /// <summary><c>int(op_str.rsplit(", ", 1)[1], 0)</c>.</summary>
    public long LastImmediate => Get(ref _lastImmediate, static o => PyText.Int(PyText.AfterLastComma(o), 0));

    /// <summary><c>cs_off(op_str)</c>: the number between <c>cs:[</c> and the first <c>]</c>.</summary>
    public long CsOffset => Get(ref _csOffset, static o =>
    {
        int from = PyText.IndexOf(o, "cs:[") + 4;
        int to = PyText.IndexOf(o, "]");
        return PyText.Int(PyText.Slice(o, from, to), 0);
    });

    /// <summary><c>int(op_str, 16)</c>: a branch or call target.</summary>
    public long Target => Get(ref _target, static o => PyText.Int(o, 16));

    /// <summary><c>int(op_str[op_str.index("cs:[") + 4:-1], 0)</c>: the variable an <c>inc</c> bumps.</summary>
    public long IncOffset => Get(ref _incOffset, static o =>
        PyText.Int(PyText.Slice(o, PyText.IndexOf(o, "cs:[") + 4, -1), 0));

    private long Get(ref Parsed slot, Func<string, long> parse)
    {
        if (!slot.Done)
        {
            try
            {
                slot.Value = parse(O);
            }
            catch (PyError e)
            {
                slot.Error = e;
            }

            slot.Done = true;
        }

        return slot.Error is { } error ? throw error : slot.Value;
    }
}
